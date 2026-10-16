using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Panely.Core.Commands;
using Panely.Core.Localization;
using Panely.Core.Translation;
using Panely.Domain.Model;

namespace Panely.App.Translation;

/// <summary>
/// 번역 패널(폭 340, padding 12). 순서: 제목·실험 배지·설정 → 주요 버튼 → 팩 상태·진행 → View → 다른 언어 번역
/// → This Book &amp; Series → 풍선 클릭 팁 → Edit Balloons.
/// </summary>
internal sealed class TranslationPanel : Border
{
    private readonly TranslationFeature _f;
    private Control? _initialFocus;
    private bool _bookSectionExpanded;

    public TranslationPanel(TranslationFeature feature)
    {
        _f = feature;
        Width = 340;
        Padding = new Thickness(12);
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(1);
        Margin = new Thickness(8);
        SetResourceReference(BackgroundProperty, "Brush.BgSecondary");
        SetResourceReference(BorderBrushProperty, "Brush.BorderSubtle");
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Opacity = 0.35 };
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        Rebuild();
    }

    public void FocusInitial() => Dispatcher.BeginInvoke(() => _initialFocus?.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    public void Rebuild()
    {
        var focused = Keyboard.FocusedElement as FrameworkElement;
        var focusName = focused is not null && IsAncestorOf(focused) ? System.Windows.Automation.AutomationProperties.GetName(focused) : null;
        var root = new StackPanel();
        var book = _f.Book;
        var services = _f.Service;
        var target = _f.VisibleLanguage ?? _f.Preferred;
        var source = _f.SourceLanguage;
        var job = _f.PrimaryJob();

        // 헤더
        var header = new DockPanel();
        var gear = Ui.IconButton(Ui.GearIcon, Loc.T("Translation Settings"), () => ShowSettingsMenu());
        DockPanel.SetDock(gear, Dock.Right);
        header.Children.Add(gear);
        var title = Ui.Row(Ui.Text(Loc.T("Translation"), "Text.Title", wrap: false), Ui.Badge(Loc.T("Experimental")));
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        root.Children.Add(header);
        if (services.Providers.IsDeveloperTestProvider)
            root.Children.Add(Ui.Caption(DeveloperProviders.Label + " — PANELY_DEV_PSEUDO_TRANSLATION=1"));

        if (book is null)
        {
            root.Children.Add(Spaced(Ui.Caption(Loc.T("No image loaded"))));
            Child = root;
            return;
        }

        // 주요 버튼
        var capability = source is null ? null : services.Providers.Packs.GetCapability(source, target, services.State.Settings.TranslationMode);
        Button primary;
        if (job is { IsRunningOrQueued: true })
            primary = Ui.Button("❚❚  " + Loc.T("Pause Translating"), () => _f.PauseLanguage(target));
        else if (capability is { Status: CapabilityStatus.Supported })
            primary = Ui.Button(Loc.T("Download Language Pack…"), () => _ = DownloadAsync(source!, target));
        else if (capability is { Status: CapabilityStatus.Downloading })
        {
            primary = Ui.Button(Loc.T("Downloading Language Pack…"), () => { });
            primary.IsEnabled = false;
        }
        else if (job is { CanResume: true })
            primary = Ui.Button("▶  " + Loc.T("Resume Translation"), () => _f.StartLanguage(target, switchVisible: true));
        else if (job is { Phase: TranslationPhase.Completed } || _f.CompletedPages(target) >= book.Pages.Count)
        {
            primary = Ui.Button(Loc.T("All %lld pages translated", book.Pages.Count), () => { });
            primary.IsEnabled = false;
        }
        else
            primary = Ui.Button("▶  " + Loc.T("Translate This Book into %@", Loc.LanguageName(target)), () => _f.StartLanguage(target, switchVisible: true));
        // 지원하지 않는 쌍·모델 없음·OCR 언어 없음: 버튼을 끄고 아래에 사유를 보여준다(disabled도 사유 tooltip)
        if (capability is { Status: CapabilityStatus.Unavailable } && job is not { IsRunningOrQueued: true })
        {
            primary.IsEnabled = false;
            ToolTipService.SetShowOnDisabled(primary, true);
            primary.ToolTip = capability.Reason;
        }
        primary.HorizontalAlignment = HorizontalAlignment.Stretch;
        primary.Padding = new Thickness(12, 9, 12, 9);
        primary.ToolTip ??= CommandRegistry.Tooltip(Loc.T("Translate This Book"), CommandIds.TranslationRun);
        root.Children.Add(Spaced(primary, 12));
        _initialFocus = primary;

        // 팩 상태·진행
        if (capability is { } cap)
        {
            var status = cap.Status switch
            {
                CapabilityStatus.Installed => Loc.T("Language pack installed"),
                CapabilityStatus.Downloading => Loc.T("Downloading…"),
                _ => cap.Reason ?? Loc.T("Language pack not installed"),
            };
            root.Children.Add(Spaced(Ui.Caption(status), 6));
            if (cap.Status == CapabilityStatus.Downloading) root.Children.Add(Ui.Progress(cap.Progress ?? 0));
        }
        else root.Children.Add(Spaced(Ui.Caption(Loc.T("The book's language is detected when translation starts.")), 6));
        if (job is not null && job.Phase != TranslationPhase.Completed)
        {
            root.Children.Add(Spaced(Ui.Caption(TranslationFeature.DescribeJob(job)), 4));
            if (job.TotalPages > 0 && job.IsRunningOrQueued)
                root.Children.Add(Ui.Progress(job.CompletedPages / (double)job.TotalPages, job.Phase is TranslationPhase.Preparing or TranslationPhase.DetectingLanguage && job.CompletedPages == 0));
        }

        // View
        root.Children.Add(Spaced(Ui.Section(Loc.T("View")), 12));
        var showing = services.State.Settings.TranslationOverlayVisible && _f.VisibleLanguage is not null;
        root.Children.Add(ViewRow(Ui.Radio(Loc.T("Original (Translation Off)"), "tr-view", !showing, () => _f.ChooseView(null)), TranslationFeature.GestureHint(CommandIds.TranslationToggle)));
        var shown = TranslationLanguages.All.Where(l => l != source && (_f.CompletedPages(l) > 0 || services.Scheduler.Get(book.BookKey, l) is not null || l == _f.VisibleLanguage)).ToList();
        foreach (var lang in shown)
        {
            var radio = Ui.Radio(Loc.LanguageName(lang), "tr-view", showing && _f.VisibleLanguage == lang, () => _f.ChooseView(lang));
            var more = Ui.IconButton(Ui.MoreIcon, Loc.T("Translate, pause or stop this language"), () => { });
            more.Click += (_, _) => ShowLanguageMenu(more, lang);
            more.Width = more.Height = 24;
            root.Children.Add(ViewRow(radio, $"{_f.CompletedPages(lang)}/{book.Pages.Count}", more));
        }

        // 다른 언어 번역
        var others = TranslationLanguages.All.Where(l => l != source && !shown.Contains(l)).ToList();
        if (others.Count > 0)
        {
            Button? another = null;
            another = Ui.LinkButton(Loc.T("Translate into Another Language") + "  ▾", () =>
            {
                var menu = new ContextMenu { PlacementTarget = another, Placement = PlacementMode.Bottom };
                foreach (var l in others) menu.Items.Add(Ui.MenuItem(Loc.LanguageName(l), () => _f.StartLanguage(l, switchVisible: false)));
                menu.IsOpen = true;
            });
            root.Children.Add(Spaced(another, 4));
        }

        // This Book & Series
        var expander = new Expander
        {
            Header = Loc.T("This Book & Series") + (source is not null ? " · " + Loc.LanguageName(source) : string.Empty),
            IsExpanded = _bookSectionExpanded,
            Margin = new Thickness(0, 12, 0, 0),
        };
        expander.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        expander.Expanded += (_, _) => _bookSectionExpanded = true;
        expander.Collapsed += (_, _) => _bookSectionExpanded = false;
        expander.Content = BuildBookSection(book.Pages.Count, source);
        root.Children.Add(expander);

        // 팁
        if (!services.State.Settings.TranslationBalloonTipDismissed)
        {
            var tip = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var close = Ui.IconButton(Ui.CloseIcon, Loc.T("Dismiss"), () => { _f.DismissTip(); Rebuild(); });
            close.Width = close.Height = 24;
            DockPanel.SetDock(close, Dock.Right);
            tip.Children.Add(close);
            tip.Children.Add(Ui.Caption(Loc.T("Click a balloon to see the original next to its translations.")));
            root.Children.Add(tip);
        }

        root.Children.Add(Ui.Separator());
        var edit = Ui.Button((_f.IsEditing ? Loc.T("Done Editing Balloons") : Loc.T("Edit Balloons")), () => { _f.ClosePanel(); _f.ToggleEditMode(); },
            tooltip: Loc.T("Correct translations, fix misread originals and hide balloons, right on the page (%@)", TranslationFeature.GestureHint(CommandIds.TranslationEdit)));
        edit.HorizontalAlignment = HorizontalAlignment.Stretch;
        edit.IsEnabled = _f.IsEditing || _f.CanEdit;
        root.Children.Add(edit);

        Child = root;
        if (focusName is not null)
        {
            var match = FindByName(root, focusName);
            if (match is not null) Dispatcher.BeginInvoke(() => match.Focus());
        }
    }

    private StackPanel BuildBookSection(int pageCount, string? source)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var services = _f.Service;
        panel.Children.Add(Ui.Caption(Loc.T("Book's Language")));
        var options = new List<(string, string?)> { (Loc.T("Detect Automatically"), null) };
        options.AddRange(TranslationLanguages.All.Select(l => (Loc.LanguageName(l), (string?)l)));
        var combo = Ui.Combo(options, source, v => _f.SetBookLanguage(v));
        combo.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(combo);

        var series = _f.Book is null ? null : services.State.GetSeries(_f.Book.SeriesKey);
        var auto = Ui.Toggle(Loc.T("Translate This Series Automatically"), series?.AutoTranslate == true, on => _f.SetAutoTranslate(on));
        auto.ToolTip = Loc.T("Translates this book first, then the other volumes of the series in the background.");
        auto.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(auto);

        var (queue, _) = _f.SeriesVolumes(filterByStem: true);
        if (queue.Count > 1)
            panel.Children.Add(Spaced(Ui.Button(Loc.T("Translate Entire Series (%lld Volumes)", queue.Count), () => _f.TranslateSeries()), 8));
        panel.Children.Add(Spaced(Ui.Button(Loc.T("Translate This Book Again…"), () =>
        {
            if (SheetWindow.Confirm(_f.Host?.OwnerWindow, Loc.T("Translate this book again?"),
                    Loc.T("Every page is read and translated again, the way Panely does it now — balloons a page split in two are joined, speech levels matched. It takes as long as the first time. Your edits stay, but one on a balloon that's now found differently may no longer apply."),
                    Loc.T("Translate Again")))
                _f.RetranslateBook();
        }, tooltip: Loc.T("Read and translate every page again, the current way. Your edits stay.")), 8));
        var ai = Ui.Button(Loc.T("Improve with an AI Chat…"), () => { _f.ClosePanel(); _f.ShowAiChat(); },
            tooltip: Loc.T("Save the book's balloons as a text file for ChatGPT, Claude or another chat to improve in context, then open its reply."));
        ai.IsEnabled = _f.VisibleLanguage is not null && _f.CompletedPages(_f.VisibleLanguage) > 0 && source is not null;
        panel.Children.Add(Spaced(ai, 8));
        panel.Children.Add(Spaced(Ui.Button(Loc.T("Export Translated Book…"), () => { _f.ClosePanel(); _f.ShowExport(); }), 8));
        var (seriesQueue, _) = _f.SeriesVolumes(filterByStem: false);
        if (seriesQueue.Count > 1)
            panel.Children.Add(Spaced(Ui.Button(Loc.T("Export Translated Series (%lld Volumes)…", seriesQueue.Count), () => { _f.ClosePanel(); _f.ExportSeries(); },
                tooltip: Loc.T("Saves a copy of every volume with its translation painted in, as CBZ or ZIP files or folders of images. Volumes not translated yet are translated first.")), 8));
        if (_f.Book?.IsNestedCacheBook == true)
            panel.Children.Add(Spaced(Ui.Caption(Loc.T("This book is an archive inside an archive. Its translation pauses when you open another book and continues when you come back.")), 8));
        return panel;
    }

    private static FrameworkElement Spaced(FrameworkElement e, double top = 8)
    {
        e.Margin = new Thickness(e.Margin.Left, top, e.Margin.Right, e.Margin.Bottom);
        return e;
    }

    private static DockPanel ViewRow(RadioButton radio, string trailing, Button? more = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (more is not null) { DockPanel.SetDock(more, Dock.Right); row.Children.Add(more); }
        var right = Ui.Caption(trailing);
        right.VerticalAlignment = VerticalAlignment.Center;
        right.Margin = new Thickness(8, 0, 4, 0);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(radio);
        return row;
    }

    private void ShowLanguageMenu(FrameworkElement anchor, string lang)
    {
        var book = _f.Book;
        if (book is null) return;
        var job = _f.Service.Scheduler.Get(book.BookKey, lang);
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        if (job is { IsRunningOrQueued: true })
            menu.Items.Add(Ui.MenuItem(Loc.T("Pause"), () => _f.PauseLanguage(lang)));
        else
            menu.Items.Add(Ui.MenuItem(job is null ? Loc.T("Translate") : Loc.T("Resume"), () => _f.StartLanguage(lang, switchVisible: false)));
        menu.Items.Add(Ui.MenuItem(Loc.T("Show This Language"), () => _f.ChooseView(lang)));
        menu.Items.Add(Ui.MenuItem(Loc.T("Stop"), () => _f.StopLanguage(lang), enabled: job is not null && job.Phase != TranslationPhase.Completed));
        menu.IsOpen = true;
    }

    private void ShowSettingsMenu()
    {
        var state = _f.Service.State;
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.Relative, HorizontalOffset = Width - 200, VerticalOffset = 44 };
        var modeHeader = new MenuItem { Header = Loc.T("Translation Mode") };
        modeHeader.Items.Add(Ui.MenuItem(Loc.T("Fast"), () => state.UpdateSettings(s => s.TranslationMode = TranslationMode.Fast), isChecked: state.Settings.TranslationMode == TranslationMode.Fast));
        modeHeader.Items.Add(Ui.MenuItem(Loc.T("Accurate"), () => state.UpdateSettings(s => s.TranslationMode = TranslationMode.Accurate), isChecked: state.Settings.TranslationMode == TranslationMode.Accurate));
        menu.Items.Add(modeHeader);
        var speech = _f.Service.Providers.SpeechLevel.GetCapability(TranslationLanguages.Japanese, TranslationLanguages.Korean);
        menu.Items.Add(Ui.MenuItem(Loc.T("Match Speech Level"), () => state.UpdateSettings(s => s.TranslationMatchesSpeechLevel = !s.TranslationMatchesSpeechLevel),
            enabled: speech.IsReady, isChecked: speech.IsReady && state.Settings.TranslationMatchesSpeechLevel, tooltip: speech.Reason));
        var lang = new MenuItem { Header = Loc.T("Default Language") };
        foreach (var l in TranslationLanguages.All)
            lang.Items.Add(Ui.MenuItem(Loc.LanguageName(l), () => state.UpdateSettings(s => s.TranslationTargetLanguage = l), isChecked: state.Settings.TranslationTargetLanguage == l));
        menu.Items.Add(lang);
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.MenuItem(Loc.T("Jobs"), _f.ShowJobs, gesture: TranslationFeature.GestureHint(CommandIds.JobsShow)));
        menu.IsOpen = true;
    }

    private async Task DownloadAsync(string source, string target)
    {
        var packs = _f.Service.Providers.Packs;
        var missing = packs.MissingPacks(source, target, _f.Service.State.Settings.TranslationMode);
        try
        {
            foreach (var pack in missing) await packs.InstallAsync(pack, null, CancellationToken.None);
            _f.StartLanguage(target, switchVisible: true);
        }
        catch (Exception ex)
        {
            SheetWindow.Inform(_f.Host?.OwnerWindow, Loc.T("Language Pack"), ex.Message);
        }
        _f.RequestStateRefresh();
    }

    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement fe && System.Windows.Automation.AutomationProperties.GetName(fe) == name && fe.Focusable) return fe;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            var r = FindByName(child, name);
            if (r is not null) return r;
        }
        return null;
    }
}
