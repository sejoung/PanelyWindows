using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Panely.App.Integration;
using Panely.App.Shell;
using Panely.Core.Abstractions;
using Panely.Core.Localization;
using Panely.Domain.Model;

namespace Panely.App;

/// <summary>
/// 설정 창(05): 일반·라이브러리·저장소·진단·실험 5개 탭. 최소 560×280, 세로로 넘치면 스크롤.
/// 언어를 바꾸면 창·메뉴·팝오버가 즉시 갱신된다(재시작 불필요).
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly AppServices _services;
    private readonly ITranslationFeature _feature;
    private readonly TabControl _tabs = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _selectedTab;

    public SettingsWindow(MainWindow main, AppServices services, ITranslationFeature feature)
    {
        _main = main;
        _services = services;
        _feature = feature;
        Style = (Style)Application.Current.FindResource("Panely.Window");
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Panely.ico"));
        MinWidth = 560 + 16;
        MinHeight = 280 + 40;
        Width = 640;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Content = _tabs;
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkCaption(this);
        Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => { Loc.LanguageChanged -= OnLanguageChanged; _refresh.Stop(); };
        _refresh.Tick += (_, _) => RefreshDynamic();
        _refresh.Start();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        Build();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Build);

    private IStateStore State => _services.State;

    private Action? _refreshStorage;
    private Action? _refreshDiagnostics;

    private void RefreshDynamic()
    {
        _refreshStorage?.Invoke();
        _refreshDiagnostics?.Invoke();
    }

    private void Build()
    {
        _selectedTab = Math.Max(0, _tabs.SelectedIndex);
        Title = Loc.T("Settings…").TrimEnd('…', '.');
        _tabs.Items.Clear();
        _tabs.Items.Add(Tab(Loc.T("General"), BuildGeneral()));
        _tabs.Items.Add(Tab(Loc.T("Library"), BuildLibrary()));
        _tabs.Items.Add(Tab(Loc.T("Storage"), BuildStorage()));
        _tabs.Items.Add(Tab(Loc.T("Diagnostics"), BuildDiagnostics(), 560));
        FrameworkElement experimental;
        try { experimental = _feature.CreateExperimentalSettingsView(); }
        catch (Exception) { experimental = Caption(Loc.T("Translation isn't available in this build.")); }
        _tabs.Items.Add(Tab(Loc.T("Experimental"), experimental, 560));
        _tabs.SelectedIndex = Math.Min(_selectedTab, _tabs.Items.Count - 1);
    }

    private static TabItem Tab(string header, FrameworkElement content, double width = 420)
    {
        content.HorizontalAlignment = HorizontalAlignment.Center;
        if (content is not ScrollViewer && double.IsNaN(content.Width)) content.MaxWidth = width;
        content.Margin = new Thickness(20, 16, 20, 20);
        return new TabItem
        {
            Header = header,
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("Text.Section"),
        Margin = new Thickness(0, 14, 0, 6),
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("Text.Caption"),
        Margin = new Thickness(0, 4, 0, 0),
    };

    private static Button MakeButton(string text, bool accent = false) => new()
    {
        Content = text,
        Style = (Style)Application.Current.FindResource(accent ? "Panely.AccentButton" : "Panely.Button"),
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 8, 8, 0),
    };

    private static Grid InfoRow(string label, string value, out TextBlock valueBlock, string? tooltip = null)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("Text.Base"), Foreground = (Brush)Application.Current.FindResource("Brush.TextSecondary") });
        valueBlock = new TextBlock { Text = value, Style = (Style)Application.Current.FindResource("Text.Base"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = tooltip ?? value };
        Grid.SetColumn(valueBlock, 1);
        g.Children.Add(valueBlock);
        return g;
    }

    // ───────────────────────── 일반 ─────────────────────────

    private FrameworkElement BuildGeneral()
    {
        var p = new StackPanel();
        p.Children.Add(Section(Loc.T("Language")));
        var current = State.Settings.AppLanguage;
        var systemName = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ko" ? "한국어" : "English";
        foreach (var (value, label) in new[]
                 {
                     (AppLanguage.System, Loc.T("System Default (%@)", systemName)),
                     (AppLanguage.En, "English"),
                     (AppLanguage.Ko, "한국어"),
                 })
        {
            var rb = new RadioButton { Content = label, GroupName = "lang", IsChecked = current == value, Margin = new Thickness(0, 4, 0, 4) };
            var v = value;
            rb.Checked += (_, _) =>
            {
                State.UpdateSettings(s => s.AppLanguage = v);
                Loc.SetLanguage(Loc.Resolve(v));
            };
            p.Children.Add(rb);
        }
        p.Children.Add(Caption(Loc.T("Menus and windows switch right away.")));

        p.Children.Add(Section(Loc.T("Reading")));
        var wheel = new CheckBox { Content = Loc.T("Turn pages with the scroll wheel"), IsChecked = State.Settings.WheelPageTurn, Style = (Style)FindResource("Panely.ToggleSwitch"), Margin = new Thickness(0, 4, 0, 4) };
        wheel.Click += (_, _) => State.UpdateSettings(s => s.WheelPageTurn = wheel.IsChecked == true);
        p.Children.Add(wheel);
        var autoFit = new CheckBox { Content = Loc.T("Fit pages again when the window is resized"), IsChecked = State.Settings.AutoFitOnResize, Style = (Style)FindResource("Panely.ToggleSwitch"), Margin = new Thickness(0, 4, 0, 4) };
        autoFit.Click += (_, _) => State.UpdateSettings(s => s.AutoFitOnResize = autoFit.IsChecked == true);
        p.Children.Add(autoFit);

        p.Children.Add(Section(Loc.T("File Explorer")));
        var status = Caption(string.Empty);
        var button = MakeButton(string.Empty);
        void Update()
        {
            var registered = SafeIsRegistered();
            status.Text = registered
                ? Loc.T("CBZ, CBR, ZIP and RAR files offer “Open with Panely”. Default apps for ZIP and RAR aren't changed.")
                : Loc.T("Add “Open with Panely” for CBZ, CBR, ZIP and RAR files and folders. Default apps for ZIP and RAR aren't changed.");
            button.Content = registered ? Loc.T("Remove from File Explorer") : Loc.T("Add to File Explorer");
        }
        button.Click += (_, _) =>
        {
            try
            {
                if (SafeIsRegistered()) FileAssociation.Unregister();
                else FileAssociation.Register();
            }
            catch (Exception ex)
            {
                _services.Diagnostics.Warning(LogCategory.App, $"File association failed: {ex.GetType().Name}");
            }
            Update();
        };
        Update();
        p.Children.Add(status);
        p.Children.Add(button);
        return p;
    }

    private static bool SafeIsRegistered()
    {
        try { return FileAssociation.IsRegistered(); }
        catch (Exception) { return false; }
    }

    // ───────────────────────── 라이브러리 ─────────────────────────

    private FrameworkElement BuildLibrary()
    {
        var p = new StackPanel();
        var reopen = new CheckBox { Content = Loc.T("Reopen last folder on launch"), IsChecked = State.Settings.ReopenLastFolderOnLaunch, Style = (Style)FindResource("Panely.ToggleSwitch"), Margin = new Thickness(0, 8, 0, 8) };
        reopen.Click += (_, _) => State.UpdateSettings(s => s.ReopenLastFolderOnLaunch = reopen.IsChecked == true);
        p.Children.Add(reopen);

        var root = State.LastLibraryRoot is { } id ? State.FindSource(id) : null;
        p.Children.Add(Section(Loc.T("Last opened")));
        var path = new TextBlock
        {
            Text = root?.Path ?? Loc.T("No folder remembered yet — it's saved the first time you browse one."),
            ToolTip = root?.Path,
            Style = (Style)FindResource("Text.Base"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource(root is null ? "Brush.TextSecondary" : "Brush.TextPrimary"),
        };
        p.Children.Add(path);
        var forget = MakeButton(Loc.T("Forget Now"));
        forget.IsEnabled = root is not null;
        forget.Click += (_, _) =>
        {
            _main.ForgetLibraryRoot();
            path.Text = Loc.T("No folder remembered yet — it's saved the first time you browse one.");
            path.ToolTip = null;
            forget.IsEnabled = false;
        };
        p.Children.Add(forget);

        p.Children.Add(Section(Loc.T("File Tree")));
        var auto = new CheckBox { Content = Loc.T("Refresh the file tree automatically"), IsChecked = State.Settings.LibraryAutoRefreshEnabled, Style = (Style)FindResource("Panely.ToggleSwitch"), Margin = new Thickness(0, 4, 0, 4) };
        auto.Click += (_, _) => State.UpdateSettings(s => s.LibraryAutoRefreshEnabled = auto.IsChecked == true);
        p.Children.Add(auto);
        return p;
    }

    // ───────────────────────── 저장소 ─────────────────────────

    private FrameworkElement BuildStorage()
    {
        var p = new StackPanel();
        p.Children.Add(Section(Loc.T("Extraction cache")));
        p.Children.Add(InfoRow(Loc.T("Extraction cache"), "…", out var total));
        p.Children.Add(InfoRow(Loc.T("Clearable cache"), "…", out var clearable));
        p.Children.Add(InfoRow(Loc.T("Cache limit"), "…", out var limit));
        var clear = MakeButton(Loc.T("Clear Cache"));
        var note = Caption(Loc.T("Current book cache is kept while open."));
        clear.Click += async (_, _) =>
        {
            clear.IsEnabled = false;
            await _main.ClearExtractionCacheAsync();
            _refreshStorage?.Invoke();
        };
        p.Children.Add(clear);
        p.Children.Add(note);
        _refreshStorage = () =>
        {
            var u = _services.ExtractionCache.GetUsage();
            total.Text = MainWindow.FormatBytes(u.TotalBytes);
            clearable.Text = MainWindow.FormatBytes(u.ClearableBytes);
            limit.Text = MainWindow.FormatBytes(u.LimitBytes);
            var busy = _main.IsLoadingBook || (_services.ExtractionCache is Panely.Core.Cache.ExtractionCache ec && ec.IsBusy);
            clear.IsEnabled = !busy && u.ClearableBytes > 0;
            clear.ToolTip = busy ? Loc.T("Available after loading finishes.") : u.ClearableBytes == 0 ? Loc.T("There is no inactive extraction cache to remove.") : null;
            ToolTipService.SetShowOnDisabled(clear, true);
        };
        _refreshStorage();
        return p;
    }

    // ───────────────────────── 진단 ─────────────────────────

    private FrameworkElement BuildDiagnostics()
    {
        var d = _services.Diagnostics;
        var p = new StackPanel();
        p.Children.Add(InfoRow(Loc.T("App version"), AppInfo.Version, out _));
        p.Children.Add(InfoRow(Loc.T("Build"), AppInfo.Build, out _));
        p.Children.Add(InfoRow(Loc.T("Windows version"), AppInfo.OsVersion, out _));
        p.Children.Add(InfoRow(Loc.T("Session ID"), d.SessionId.ToString(), out _));
        p.Children.Add(InfoRow(Loc.T("File log"), "…", out var logSize));
        p.Children.Add(InfoRow(Loc.T("Retention"), Loc.T("%@ max, reports include %@", "768 KB", "256 KB"), out _));

        p.Children.Add(Section(Loc.T("Log level")));
        var level = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var l in Enum.GetValues<LogLevel>()) level.Items.Add(new ComboBoxItem { Content = l.ToString().ToLowerInvariant(), Tag = l });
        level.SelectedIndex = (int)State.Settings.LogLevel;
        var levelNote = Caption(Loc.T("Applies to new log entries only."));
        level.SelectionChanged += (_, _) =>
        {
            if (level.SelectedItem is ComboBoxItem { Tag: LogLevel l })
            {
                State.UpdateSettings(s => s.LogLevel = l);
                d.MinimumLevel = l;
                levelNote.Text = Loc.T("Diagnostic log level set to %@.", l.ToString().ToLowerInvariant());
            }
        };
        p.Children.Add(level);
        p.Children.Add(levelNote);

        p.Children.Add(Section(Loc.T("Diagnostics")));
        p.Children.Add(Caption(Loc.T("Reports leave out full paths, your user name, recognized text and translations. Panely never sends them anywhere — you choose where to save.")));
        var buttons = new WrapPanel();
        var export = MakeButton(Loc.T("Export Diagnostic Report…"), accent: true);
        export.Click += async (_, _) => await _main.ExportDiagnosticsAsync();
        var clearLogs = MakeButton(Loc.T("Clear Logs"));
        clearLogs.Click += (_, _) => { _main.ClearDiagnosticLogs(); _refreshDiagnostics?.Invoke(); };
        var folder = MakeButton(Loc.T("Open Logs Folder"));
        folder.Click += (_, _) => MainWindow.OpenFolder(d.LogFolder);
        buttons.Children.Add(export);
        buttons.Children.Add(clearLogs);
        buttons.Children.Add(folder);
        p.Children.Add(buttons);
        _refreshDiagnostics = () => logSize.Text = MainWindow.FormatBytes(d.LogFileBytes);
        _refreshDiagnostics();
        return p;
    }
}
