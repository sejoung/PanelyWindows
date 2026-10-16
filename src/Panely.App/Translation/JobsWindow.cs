using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Panely.Core.Abstractions;
using Panely.Core.Localization;
using Panely.Core.Translation;

namespace Panely.App.Translation;

/// <summary>
/// 작업 창(초기 500×560, 최소 420×320, 단일 인스턴스): 현재/다른 책 번역, 내보내기, 실패 작업, 완료 내보내기 이력(50).
/// </summary>
internal sealed class JobsWindow : Window
{
    private static JobsWindow? _instance;
    private readonly TranslationFeature _f;
    private readonly StackPanel _list = new() { Margin = new Thickness(16) };
    private bool _queued;

    private JobsWindow(Window? owner, TranslationFeature feature)
    {
        _f = feature;
        Owner = owner;
        Title = Loc.T("Jobs");
        Width = 500;
        Height = 560;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        SetResourceReference(StyleProperty, "Panely.Window");
        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.SetResourceReference(StyleProperty, "Panely.ScrollViewer");
        Content = scroll;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

        EventHandler<TranslationJobInfo> onJob = (_, _) => Queue();
        EventHandler<string> onRemoved = (_, _) => Queue();
        EventHandler<ExportJobInfo> onExport = (_, _) => Queue();
        EventHandler<Guid> onExportRemoved = (_, _) => Queue();
        EventHandler<StateChange> onState = (_, c) => { if (c.HasFlag(StateChange.Exports)) Queue(); };
        EventHandler onLanguage = (_, _) => Queue();
        feature.Service.Scheduler.JobChanged += onJob;
        feature.Service.Scheduler.JobRemoved += onRemoved;
        feature.Service.Exports.JobChanged += onExport;
        feature.Service.Exports.JobRemoved += onExportRemoved;
        feature.StateStore.Changed += onState;
        Loc.LanguageChanged += onLanguage;
        Closed += (_, _) =>
        {
            feature.Service.Scheduler.JobChanged -= onJob;
            feature.Service.Scheduler.JobRemoved -= onRemoved;
            feature.Service.Exports.JobChanged -= onExport;
            feature.Service.Exports.JobRemoved -= onExportRemoved;
            feature.StateStore.Changed -= onState;
            Loc.LanguageChanged -= onLanguage;
            _instance = null;
        };
        Rebuild();
    }

    /// <summary>여러 번 불러도 기존 창을 활성화한다.</summary>
    public static void ShowSingle(Window? owner, TranslationFeature feature)
    {
        if (_instance is not null)
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
            return;
        }
        _instance = new JobsWindow(owner, feature);
        _instance.Show();
    }

    private void Queue()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _queued = false; Title = Loc.T("Jobs"); Rebuild(); });
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var translations = _f.Service.Scheduler.Jobs;
        var exports = _f.Service.Exports.Jobs;
        var history = _f.StateStore.ExportHistory;

        var active = translations.Where(j => j.Phase != TranslationPhase.Completed).ToList();
        var activeExports = exports.Where(e => e.Phase != ExportPhase.Finished).ToList();
        if (active.Count == 0 && activeExports.Count == 0)
        {
            _list.Children.Add(Ui.Text(Loc.T("Nothing in Progress"), "Text.Title"));
            _list.Children.Add(Gap(Ui.Caption(Loc.T("Translations and exports show here while they run, and exported books stay listed. Export a book from the translation menu in the toolbar, File › Export Translated Book…, or a book's or folder's context menu in the library."))));
        }
        if (active.Count > 0)
        {
            _list.Children.Add(Gap(Ui.Section(Loc.T("Translation")), 4));
            foreach (var j in active.OrderByDescending(j => j.IsCurrentBook).ThenByDescending(j => j.IsActive)) _list.Children.Add(TranslationCard(j));
        }
        if (activeExports.Count > 0)
        {
            _list.Children.Add(Gap(Ui.Section(Loc.T("Export")), 16));
            foreach (var e in activeExports) _list.Children.Add(ExportCard(e));
        }
        var completed = translations.Where(j => j.Phase == TranslationPhase.Completed).ToList();
        if (completed.Count > 0)
        {
            _list.Children.Add(Gap(Ui.Section(Loc.T("Finished Jobs")), 16));
            foreach (var j in completed) _list.Children.Add(TranslationCard(j));
        }
        if (history.Count > 0)
        {
            var header = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
            var clear = Ui.LinkButton(Loc.T("Clear List"), () => _f.StateStore.ClearExportHistory());
            clear.ToolTip = Loc.T("Forget these exports. The files stay where they are.");
            DockPanel.SetDock(clear, Dock.Right);
            header.Children.Add(clear);
            header.Children.Add(Ui.Section(Loc.T("Exported")));
            _list.Children.Add(header);
            foreach (var r in history.OrderByDescending(r => r.Date)) _list.Children.Add(HistoryCard(r));
        }
    }

    private static FrameworkElement Gap(FrameworkElement e, double top = 8) { e.Margin = new Thickness(0, top, 0, 0); return e; }

    private static Border Card(UIElement content)
    {
        var b = new Border { Child = content, Padding = new Thickness(12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0) };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.BgPrimary");
        b.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderSubtle");
        return b;
    }

    private static WrapPanel Actions(params Button[] buttons)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in buttons) { b.Margin = new Thickness(0, 0, 6, 0); w.Children.Add(b); }
        return w;
    }

    private Border TranslationCard(TranslationJobInfo j)
    {
        var s = new StackPanel();
        s.Children.Add(Ui.Text(j.Book.Title, "Text.Body", wrap: false));
        s.Children.Add(Ui.Caption((j.Source is { } src ? Loc.LanguageName(src) + " → " : string.Empty) + Loc.LanguageName(j.Target)
            + (j.IsCurrentBook ? " · " + Loc.T("Open This Book") : string.Empty)));
        s.Children.Add(Gap(Ui.Caption(TranslationFeature.DescribeJob(j)), 4));
        if (j.TotalPages > 0 && j.Phase != TranslationPhase.Completed) s.Children.Add(Ui.Progress(j.CompletedPages / (double)j.TotalPages));
        if (j.Book.IsNestedCacheBook && !j.IsCurrentBook && j.Phase != TranslationPhase.Completed)
            s.Children.Add(Gap(Ui.Caption(Loc.T("This book is an archive inside an archive. Its translation pauses when you open another book and continues when you come back.")), 4));
        var scheduler = _f.Service.Scheduler;
        var buttons = new List<Button>();
        if (j.IsRunningOrQueued) buttons.Add(Ui.Button(Loc.T("Pause"), () => scheduler.Pause(j.Book.BookKey, j.Target)));
        else if (j.CanResume && !j.AutoPaused) buttons.Add(Ui.Button(j.Phase == TranslationPhase.Failed ? Loc.T("Try Again") : Loc.T("Resume"), () => scheduler.Start(j.Book, j.Target, j.Source, j.Scope)));
        if (j.Phase is TranslationPhase.Completed or TranslationPhase.Failed) buttons.Add(Ui.Button(Loc.T("Dismiss"), () => scheduler.Dismiss(j.Book.BookKey, j.Target)));
        else buttons.Add(Ui.Button(Loc.T("Stop"), () => scheduler.Stop(j.Book.BookKey, j.Target)));
        if (!j.IsCurrentBook && _f.Host is { } host)
            buttons.Add(Ui.Button(Loc.T("Open This Book"), () => _ = host.OpenAsync(j.Book.ToRequest())));
        s.Children.Add(Actions([.. buttons]));
        return Card(s);
    }

    private Border ExportCard(ExportJobInfo e)
    {
        var s = new StackPanel();
        s.Children.Add(Ui.Text(e.Request.Book.Title, "Text.Body", wrap: false));
        s.Children.Add(Ui.Caption($"{Loc.LanguageName(e.Request.Target)} · {FormatName(e.Request.Format)}"));
        var status = e.Phase switch
        {
            ExportPhase.Preparing => Loc.T("Preparing…"),
            ExportPhase.Translating => Loc.T("Translating first"),
            ExportPhase.Waiting => Loc.T("Translated — waiting for the export ahead of it"),
            ExportPhase.Exporting => Loc.T("Exporting · %lld/%lld pages", e.Done, e.Total),
            ExportPhase.Failed => e.Error ?? Loc.T("Failed to load"),
            ExportPhase.Cancelled => Loc.T("Cancelled"),
            _ => Loc.T("Exported"),
        };
        var statusText = Gap(Ui.Caption(status), 4);
        if (e.Phase == ExportPhase.Failed) ((TextBlock)statusText).SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        s.Children.Add(statusText);
        if (e.Phase == ExportPhase.Exporting && e.Total > 0) s.Children.Add(Ui.Progress(e.Done / (double)e.Total));
        var exports = _f.Service.Exports;
        if (e.IsTerminal)
            s.Children.Add(Actions(Ui.Button(Loc.T("Try Again"), () => exports.Retry(e.Id)), Ui.Button(Loc.T("Dismiss"), () => exports.Dismiss(e.Id))));
        else
            s.Children.Add(Actions(Ui.Button(Loc.T("Cancel Export"), () => exports.Cancel(e.Id))));
        return Card(s);
    }

    private static Border HistoryCard(ExportRecord r)
    {
        var s = new StackPanel();
        s.Children.Add(Ui.Text($"{r.Title} [{Loc.LanguageName(r.Target)}]", "Text.Body", wrap: false));
        s.Children.Add(Ui.Caption($"{FormatName(r.Format)} · {Loc.T("%lld pages", r.PageCount)} · {FormatBytes(r.Bytes)} · {r.Date.ToLocalTime():g}"));
        var exists = File.Exists(r.Path) || Directory.Exists(r.Path);
        var open = Ui.Button(Loc.T("Open"), () => OpenPath(r.Path));
        var reveal = Ui.Button(Loc.T("Show in Explorer"), () => RevealPath(r.Path));
        open.IsEnabled = reveal.IsEnabled = exists;
        s.Children.Add(Actions(open, reveal));
        return Card(s);
    }

    internal static string FormatName(Domain.Model.ExportFormat f) => f switch
    {
        Domain.Model.ExportFormat.Zip => Loc.T("ZIP"),
        Domain.Model.ExportFormat.Folder => Loc.T("Folder of Images"),
        _ => Loc.T("CBZ"),
    };

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }

    internal static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception) { }
    }

    internal static void RevealPath(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch (Exception) { }
    }
}
