using System.Windows;
using System.Windows.Threading;
using Panely.App.Imaging;
using Panely.App.Integration;
using Panely.App.Shell;
using Panely.Core;
using Panely.Core.Abstractions;
using Panely.Core.Cache;
using Panely.Core.Diagnostics;
using Panely.Core.Library;
using Panely.Core.Localization;
using Panely.Core.Sources;
using Panely.Core.State;

namespace Panely.App;

/// <summary>
/// Composition root(07): 경로 준비 → 진단 → 상태 → 캐시 → 열기 → 라이브러리 → 리더 창.
/// single instance: 이미 실행 중이면 경로를 전달하고 종료한다.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF Application 수명과 같다. OnExit에서 모든 서비스를 Dispose한다.")]
public partial class App : Application
{
    private SingleInstance? _instance;
    private DiagnosticsService? _diagnostics;
    private StateStore? _state;
    private LibraryService? _library;
    private SourceMonitor? _monitor;

    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = ExtractPaths(e.Args);

        _instance = new SingleInstance();
        if (!_instance.TryAcquire())
        {
            if (_instance.SendToPrimary(paths))
            {
                Shutdown(0);
                return;
            }
            // Mutex를 쥔 인스턴스가 있는 동안 두 번째 인스턴스로 계속하면 staging과 state.json을 서로 망가뜨린다.
            // 기다리는 사이 첫 인스턴스가 끝나 Mutex가 풀렸을 때만 이어서 시작한다.
            if (!_instance.TryAcquire())
            {
                Loc.SetLanguage(Loc.Resolve(Panely.Domain.Model.AppLanguage.System)); // 설정은 이 인스턴스가 읽지 않는다
                MessageBox.Show(Loc.T("Panely is already running but isn't responding. Close it and try again."), "Panely", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
                return;
            }
        }
        // 창을 만드는 동안(수 초)에도 후속 실행이 연결할 수 있게 먼저 연다. 요청은 MarkReady까지 쌓인다.
        _instance.StartServer();

        // 모든 앱 창(설정·작업·시트 포함)에 다크 캡션을 적용한다. 개별 창이 빠뜨려도 밝은 제목 표시줄이 나오지 않게.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window w) NativeMethods.ApplyDarkCaption(w); }));

        var appPaths = AppPaths.Default;
        appPaths.EnsureCreated();
        _diagnostics = new DiagnosticsService(appPaths);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            _diagnostics?.Log(Panely.Domain.Model.LogLevel.Critical, LogCategory.App, $"Unhandled: {PathRedactor.Redact(ex.ExceptionObject?.ToString())}");
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            _diagnostics?.Log(Panely.Domain.Model.LogLevel.Error, LogCategory.App, $"Unobserved task: {PathRedactor.Redact(ex.Exception.GetBaseException().Message)}");
            ex.SetObserved();
        };

        _state = new StateStore(appPaths, _diagnostics);
        _diagnostics.MinimumLevel = _state.Settings.LogLevel;
        _diagnostics.Info(LogCategory.App, $"Launch {AppInfo.Version} ({AppInfo.Build}) on {AppInfo.OsVersion}");
        Loc.SetLanguage(Loc.Resolve(_state.Settings.AppLanguage));

        // 책을 닫는 UI 스레드에서 캐시 정리가 돌지 않게 하고, 시작 정리는 창을 막지 않게 백그라운드로 보낸다.
        // 남은 staging은 먼저 치워 두므로 곧바로 시작되는 새 해제와 섞이지 않는다.
        var cache = new ExtractionCache(appPaths, _diagnostics) { EnforceInBackground = true };
        var diagnostics = _diagnostics;
        try
        {
            cache.DetachStaleStaging();
            _ = Task.Run(() =>
            {
                try { cache.FinishOrphanCleanup(); }
                catch (Exception ex) { diagnostics.Warning(LogCategory.Cache, $"Orphan cleanup failed: {ex.GetType().Name}"); }
            });
        }
        catch (Exception ex) { _diagnostics.Warning(LogCategory.Cache, $"Orphan cleanup failed: {ex.GetType().Name}"); }

        var opener = new BookOpener(_state, cache, _diagnostics);
        Services = new AppServices
        {
            Paths = appPaths,
            State = _state,
            Diagnostics = _diagnostics,
            Opener = opener,
            ExtractionCache = cache,
            Decoder = new PageDecoder(),
        };
        _library = new LibraryService(_diagnostics);
        _monitor = new SourceMonitor();

        var feature = CreateTranslationFeature(Services);
        var window = new MainWindow(Services, _library, _monitor, new AvailabilityChecker(), feature);
        MainWindow = window;
        _instance.OpenRequested += list => Dispatcher.BeginInvoke(() => window.OpenFromExternal(list));
        window.Show();
        window.StartUp(paths);
        _instance.MarkReady();
        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase)) window.ShowSettings();
    }

    /// <summary>번역 모듈(06). 실패하면 기능을 끈 구현으로 대체해 리더는 계속 동작한다.</summary>
    private ITranslationFeature CreateTranslationFeature(AppServices services)
    {
        try
        {
            // 번역 모듈은 같은 어셈블리의 Panely.App.Translation.TranslationFeature(AppServices)
            var type = typeof(App).Assembly.GetType("Panely.App.Translation.TranslationFeature");
            if (type is not null && Activator.CreateInstance(type, services) is ITranslationFeature feature) return feature;
            _diagnostics?.Warning(LogCategory.Translation, "Translation module not present");
        }
        catch (Exception ex)
        {
            _diagnostics?.Error(LogCategory.Translation, $"Translation feature unavailable: {(ex.InnerException ?? ex).GetType().Name}");
        }
        return new NullTranslationFeature();
    }

    private static IReadOnlyList<string> ExtractPaths(string[] args)
    {
        var list = new List<string>();
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a) || a.StartsWith("--", StringComparison.Ordinal)) continue;
            try { list.Add(Path.GetFullPath(a.Trim('"'))); }
            catch (Exception) { }
        }
        return list;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _diagnostics?.Log(Panely.Domain.Model.LogLevel.Critical, LogCategory.App, $"UI exception: {PathRedactor.Redact(e.Exception.ToString())}");
        // 읽기를 중단시키지 않는다. 치명적 예외만 기록 후 계속.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _state?.Flush(); } catch (Exception) { }
        _monitor?.Dispose();
        _library?.Dispose();
        _state?.Dispose();
        _diagnostics?.Info(LogCategory.App, "Exit");
        _instance?.Dispose();
        base.OnExit(e);
    }
}
