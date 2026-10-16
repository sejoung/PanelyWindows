using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Panely.Core.Abstractions;
using Panely.Core.Commands;
using Panely.Core.Localization;
using Panely.Core.Translation;
using Panely.Domain.Library;
using Panely.Domain.Model;

namespace Panely.App.Translation;

public sealed partial class TranslationFeature
{
    private Popup? _panelPopup;
    private Border? _pillHost;

    // ───────────────────────── 번역 패널 ─────────────────────────

    public void ShowPanel()
    {
        if (!IsEnabled || _host is null) return;
        if (_panelPopup?.IsOpen == true) { _panelPopup.IsOpen = false; return; }
        (Service.Providers.Recognizer as WindowsOcrRecognizer)?.Refresh();
        var anchor = _host.TranslationButtonAnchor;
        _panelPopup = new Popup
        {
            StaysOpen = false,
            AllowsTransparency = true,
            Placement = anchor is not null ? PlacementMode.Bottom : PlacementMode.Relative,
            PlacementTarget = (UIElement?)anchor ?? _host.OverlayLayer,
            HorizontalOffset = anchor is null ? Math.Max(12, _host.OverlayLayer.ActualWidth - 352) : 0,
            VerticalOffset = anchor is null ? 60 : 6,
            Child = new TranslationPanel(this),
        };
        _panelPopup.Closed += (_, _) => _host?.FocusReader();
        _panelPopup.IsOpen = true;
        ((TranslationPanel)_panelPopup.Child).FocusInitial();
    }

    private void RefreshOpenPanel(bool force = false)
    {
        if (_panelPopup is { IsOpen: true, Child: TranslationPanel panel }) panel.Rebuild();
    }

    internal void ClosePanel()
    {
        if (_panelPopup is not null) _panelPopup.IsOpen = false;
    }

    /// <summary>현재 책의 주요 작업(보이는/선호 언어).</summary>
    internal TranslationJobInfo? PrimaryJob()
    {
        if (_book is null) return null;
        var target = _visible ?? PreferredTarget();
        return Service.Scheduler.Get(_book.BookKey, target);
    }

    // ───────────────────────── 툴바 언어 메뉴 ─────────────────────────

    public IReadOnlyList<Control> BuildLanguageMenuItems()
    {
        if (!IsEnabled || _book is null) return [];
        var items = new List<Control>
        {
            Ui.MenuItem(Loc.T("Original (Translation Off)"), () => ChooseView(null), isChecked: !ShowingTranslation,
                gesture: GestureHint(CommandIds.TranslationToggle)),
        };
        foreach (var lang in TranslationLanguages.All.Where(l => l != _source))
        {
            var has = HasResults(lang);
            var count = CompletedPages(lang);
            var header = has ? $"{Loc.LanguageName(lang)}  {count}/{_book.Pages.Count}" : Loc.LanguageName(lang);
            items.Add(Ui.MenuItem(header, () => { if (has) ChooseView(lang); else StartLanguage(lang, switchVisible: true); },
                isChecked: ShowingTranslation && _visible == lang,
                tooltip: has ? null : Loc.T("Not translated yet — click to translate this book into it")));
        }
        items.Add(new Separator());
        items.Add(Ui.MenuItem(Loc.T("Translation Panel…"), ShowPanel));
        items.Add(Ui.MenuItem(Loc.T("Export Translated Book…"), ShowExport, enabled: CanExport));
        items.Add(Ui.MenuItem(Loc.T("Jobs"), ShowJobs, gesture: GestureHint(CommandIds.JobsShow)));
        return items;
    }

    // ───────────────────────── 라이브러리 우클릭 ─────────────────────────

    public IReadOnlyList<Control> BuildLibraryContextMenuItems(string path, bool isFolder)
    {
        if (!IsEnabled) return [];
        var items = new List<Control>
        {
            Ui.MenuItem(Loc.T("Export Translated Book…"), () => ExportPaths([path], isSeries: false)),
        };
        if (isFolder)
        {
            var volumes = ChildVolumes(path);
            if (volumes.Count > 1)
                items.Add(Ui.MenuItem(Loc.T("Export Translated Series (%lld Volumes)…", volumes.Count), () => ExportPaths(volumes, isSeries: true)));
        }
        return items;
    }

    private static List<string> ChildVolumes(string folder)
    {
        try
        {
            var dir = new DirectoryInfo(folder);
            return dir.EnumerateFileSystemInfos()
                .Where(i => (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !i.Name.StartsWith('.'))
                .Where(i => i is DirectoryInfo || SupportedFormats.IsArchive(i.FullName))
                .Select(i => i.FullName)
                .Order(new NaturalComparer(Loc.Culture))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void ExportPaths(IReadOnlyList<string> paths, bool isSeries)
    {
        var targets = new List<BookTarget>();
        foreach (var p in paths)
        {
            // 열려 있는 책이면 현재 책 정보를 그대로 쓴다(중첩 책 포함)
            if (_book is not null && string.Equals(_book.LibraryPath, p, StringComparison.OrdinalIgnoreCase) && _bookTarget is not null)
            {
                targets.Add(_bookTarget);
                continue;
            }
            var kind = Directory.Exists(p) ? SourceKind.Folder : SourceKind.Archive;
            var title = kind == SourceKind.Folder ? System.IO.Path.GetFileName(p.TrimEnd('\\', '/')) : System.IO.Path.GetFileNameWithoutExtension(p);
            var source = State.GetOrCreateSource(p, kind, title);
            targets.Add(new BookTarget(BookKeys.Make(source.Id, null), p, null, title, string.Empty, false));
        }
        if (targets.Count == 0) return;
        var seriesSource = _book is not null && targets.Any(t => t.BookKey == _book.BookKey) ? _source : null;
        ExportSheet.Show(_host?.OwnerWindow, this, targets, _visible ?? PreferredTarget(), seriesSource, isSeries ? TranslationScope.Page : CurrentScope);
    }

    internal void ExportSeries()
    {
        var (queue, skipped) = SeriesVolumes(filterByStem: false);
        if (skipped.Count > 0)
            _host?.ShowNotice(Loc.T("These volumes are inside another archive and only exist while it's open. Keep it open until the export finishes."), TimeSpan.FromSeconds(8));
        ExportSheet.Show(_host?.OwnerWindow, this, queue, _visible ?? PreferredTarget(), _source, CurrentScope);
    }

    // ───────────────────────── 진행 pill ─────────────────────────

    /// <summary>뷰어 하단 +76: 현재 책 진행, 다른 책/내보내기 진행. 클릭하면 Jobs.</summary>
    private void RefreshProgressPills()
    {
        if (_host is null) return;
        var lines = new List<(string Text, double? Fraction)>();
        if (IsEnabled)
        {
            var jobs = Service.Scheduler.Jobs;
            var current = _book is null ? [] : jobs.Where(j => j.Book.BookKey == _book.BookKey).ToList();
            var primary = current.FirstOrDefault(j => j.Target == (_visible ?? PreferredTarget())) ?? current.FirstOrDefault(j => j.IsRunningOrQueued);
            if (primary is not null && primary.Phase != TranslationPhase.Completed && !(primary.Phase == TranslationPhase.Failed && primary.Reason == TranslationStopReason.UnsupportedPair))
                lines.Add((DescribeJob(primary), primary.TotalPages > 0 ? primary.CompletedPages / (double)primary.TotalPages : null));

            var others = jobs.Where(j => _book is null || j.Book.BookKey != _book.BookKey).ToList();
            var running = others.Where(j => j.IsRunningOrQueued).ToList();
            if (running.Count == 1)
                lines.Add((Loc.T("Translating another book · %@ %lld/%lld", running[0].Book.Title, running[0].CompletedPages, running[0].TotalPages), null));
            else if (running.Count > 1)
            {
                var active = running.FirstOrDefault(j => j.IsActive) ?? running[0];
                lines.Add((Loc.T("Translating %lld other books · %@ %lld/%lld", running.Count, active.Book.Title, active.CompletedPages, active.TotalPages), null));
            }
            if (others.Any(j => j.Phase == TranslationPhase.Failed))
                lines.Add((Loc.T("Translation stopped in another book — click for details"), null));

            var exports = Service.Exports.Jobs;
            var activeExports = exports.Where(e => !e.IsTerminal).ToList();
            if (activeExports.Count == 1)
            {
                var e = activeExports[0];
                lines.Add((e.Phase == ExportPhase.Exporting ? Loc.T("Exporting · %lld/%lld pages", e.Done, e.Total) : Loc.T("Exporting · %@", e.Request.Book.Title), e.Total > 0 && e.Phase == ExportPhase.Exporting ? e.Done / (double)e.Total : null));
            }
            else if (activeExports.Count > 1)
                lines.Add((Loc.T("Exporting %lld books · %@", activeExports.Count, activeExports[0].Request.Book.Title), null));
            if (exports.Any(e => e.Phase == ExportPhase.Failed)) lines.Add((Loc.T("An export stopped — click for details"), null));
            else if (exports.Any(e => e.Phase == ExportPhase.Finished)) lines.Add((Loc.T("Export finished — click to see"), null));
        }

        if (_pillHost is not null) _host.ProgressLayer.Children.Remove(_pillHost);
        _pillHost = null;
        if (lines.Count == 0) return;
        var stack = new StackPanel();
        foreach (var (text, fraction) in lines)
        {
            var row = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(Ui.Text(text, "Text.Caption", wrap: false));
            if (fraction is { } f) { var bar = Ui.Progress(f); bar.Width = 180; row.Children.Add(bar); }
            stack.Children.Add(row);
        }
        var button = new Button { Content = stack, Cursor = Cursors.Hand, Padding = new Thickness(14, 6, 14, 6), ToolTip = CommandRegistry.Tooltip(Loc.T("Jobs"), CommandIds.JobsShow) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Panely.Button");
        button.Click += (_, _) => ShowJobs();
        _pillHost = new Border { Child = button, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        _host.ProgressLayer.Children.Add(_pillHost);
    }

    /// <summary>작업 상태 문구(가짜 ETA 없음).</summary>
    internal static string DescribeJob(TranslationJobInfo j)
    {
        switch (j.Phase)
        {
            case TranslationPhase.DetectingLanguage:
                return Loc.T("Detecting the book's language…");
            case TranslationPhase.Preparing when j.CompletedPages == 0:
                return Loc.T("Preparing models…");
            case TranslationPhase.Preparing:
            case TranslationPhase.Translating:
                if (j.Remaining is { } r)
                {
                    return r.TotalMinutes < 1
                        ? Loc.T("Translating · %lld/%lld pages · less than a minute left", j.CompletedPages, j.TotalPages)
                        : Loc.T("Translating · %lld/%lld pages · about %lld min left", j.CompletedPages, j.TotalPages, (long)Math.Ceiling(r.TotalMinutes));
                }
                return Loc.T("Translating · %lld/%lld pages", j.CompletedPages, j.TotalPages);
            case TranslationPhase.Queued:
                return Loc.T("Waiting for another book · %lld/%lld pages", j.CompletedPages, j.TotalPages);
            case TranslationPhase.Paused when j.Reason == TranslationStopReason.NeedsLanguageDownload:
                return j.ReasonMessage ?? Loc.T("Language pack not installed");
            case TranslationPhase.Paused when j.AutoPaused:
                return Loc.T("Paused · open this book to continue · %lld/%lld pages", j.CompletedPages, j.TotalPages);
            case TranslationPhase.Paused:
                return Loc.T("Paused · %lld/%lld pages", j.CompletedPages, j.TotalPages);
            case TranslationPhase.Completed:
                return Loc.T("All %lld pages translated", j.TotalPages);
            case TranslationPhase.Failed:
                return j.ReasonMessage is { } m ? Loc.T("Translation stopped: %@", m) : Loc.T("Translation was stopped before the book was finished.");
            default:
                return Loc.T("%lld of %lld pages translated", j.CompletedPages, j.TotalPages);
        }
    }

    internal static Geometry GlobeIcon { get; } = Geometry.Parse("M7,0.5 A6.5,6.5 0 1 1 6.99,0.5 Z M0.5,7 L13.5,7 M7,0.5 C4,3 4,11 7,13.5 M7,0.5 C10,3 10,11 7,13.5");
}
