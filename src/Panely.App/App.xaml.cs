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
            if (_instance.SendToPrimary(paths) || paths.Count == 0)
            {
                Shutdown(0);
                return;
            }
        }

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

        var cache = new ExtractionCache(appPaths, _diagnostics);
        try { cache.CleanupOrphans(); }
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
        _instance.StartServer();
        window.Show();
        window.StartUp(paths);
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
