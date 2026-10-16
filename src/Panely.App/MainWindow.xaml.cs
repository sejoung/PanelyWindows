using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Panely.App.Controls;
using Panely.App.Imaging;
using Panely.App.Integration;
using Panely.App.Reader;
using Panely.App.Shell;
using Panely.Core.Abstractions;
using Panely.Core.Commands;
using Panely.Core.Localization;
using Panely.Core.Reader;
using Panely.Domain.Model;

namespace Panely.App;

/// <summary>
/// 단일 리더 창(01/02). 뷰어·양쪽 패널·떠 있는 툴바/슬라이더·카드·배너·로딩 차폐를 배치하고
/// ReaderSession(Core/Reader)과 서비스들을 연결한다. 명령은 MainWindow.Commands, 메뉴는 MainWindow.Menu,
/// 번역 호스트는 MainWindow.Host, 라이브러리는 MainWindow.Library에 있다.
/// </summary>
public partial class MainWindow : Window
{
    private const double ChromeHoverZone = 80;

    private readonly AppServices _services;
    private readonly IStateStore _state;
    private readonly ILibraryService _library;
    private readonly ISourceMonitor _monitor;
    private readonly IAvailabilityChecker _availability;
    private readonly ReaderSession _session;
    private readonly PageImageCache _cache;
    private readonly FloatingToolbar _toolbar;
    private readonly ThumbnailPanel _thumbnails = new();
    private readonly LibrarySidebar _sidebar = new();
    private readonly DispatcherTimer _revealTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _dismissTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _noticeTimer = new();
    private ITranslationFeature _feature;
    private bool _chromeVisible = true;
    private bool _libraryOverlayOpen;
    private bool _explicitOpenSeen;
    private bool _closing;
    private bool _fullScreen;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResize;
    private long _shownNoticeId;

    public MainWindow(AppServices services, ILibraryService library, ISourceMonitor monitor, IAvailabilityChecker availability, ITranslationFeature feature)
    {
        _services = services;
        _state = services.State;
        _library = library;
        _monitor = monitor;
        _availability = availability;
        _feature = feature;
        InitializeComponent();

        _session = new ReaderSession(services.Opener, _state, services.Diagnostics);
        _cache = new PageImageCache(Dispatcher, services.Decoder, services.Diagnostics);
        Viewer.Attach(_session, _cache, _feature, () => _state.Settings.WheelPageTurn, () => !_state.Settings.AutoFitOnResize);

        _toolbar = new FloatingToolbar(Execute, () => _feature.BuildLanguageMenuItems(), BuildBookmarkMenuItems);
        ToolbarHost.Child = _toolbar;
        _toolbar.MenuOpenChanged += (_, _) => UpdateChromeVisibility(null);

        _thumbnails.SetDecoder(services.Decoder);
        ThumbnailHost.Child = _thumbnails;
        _thumbnails.PageSelected += i => { _session.Jump(i); FocusReader(); };

        WireSession();
        WireViewer();
        WireSlider();
        WireLibrary();
        WireMisc();

        _feature.Attach(this);
        _feature.StateChanged += (_, _) => Dispatcher.BeginInvoke(OnFeatureStateChanged);

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => { ApplyMinimumContentSize(); FocusReader(); };
        Activated += (_, _) => RefreshAvailability();
        Loc.LanguageChanged += (_, _) => { BuildMenu(); UpdateStaticTexts(); UpdateUi(ReaderChange.None); RefreshSidebar(); };

        ApplyPinnedState();
        BuildMenu();
        UpdateStaticTexts();
        UpdateUi(ReaderChange.None);
    }

    /// <summary>창이 보인 뒤: 실행 인자 열기 또는 마지막 폴더 복원.</summary>
    public void StartUp(IReadOnlyList<string> paths)
    {
        // 저장 파일 손상/미래 schema: 기본값으로 시작했음을 알린다(원본은 격리 보존)
        if (_state is Panely.Core.State.StateStore { LoadProblem: not null } store)
        {
            var message = store.IsReadOnly
                ? Loc.T("Your saved data was created by a newer version of Panely. Changes won't be saved in this session.")
                : Loc.T("Your saved data couldn't be read, so Panely started with default settings. A copy of the old file was kept.");
            _session.Notices.Post(new Notice(NoticeCategory.ReaderError, message, [NoticeAction.Dismiss]));
        }

        if (paths.Count > 0)
        {
            OpenFromExternal(paths);
            return;
        }
        // 늦게 도착하는 탐색기 열기 요청이 복원을 덮을 수 있도록 지연 후 확인
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, RestoreLastLibrary);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        NativeMethods.ApplyDarkCaption(this);
        if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEHWHEEL && Viewer.IsMouseOver)
        {
            Viewer.OnHorizontalWheel(NativeMethods.HiWord(wParam));
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>최소 콘텐츠 800×600 DIP(캡션·메뉴 제외).</summary>
    private void ApplyMinimumContentSize()
    {
        if (Content is not FrameworkElement content || content.ActualWidth <= 0) return;
        var ncW = ActualWidth - content.ActualWidth;
        var ncH = ActualHeight - content.ActualHeight;
        MinWidth = 800 + Math.Max(0, ncW);
        MinHeight = 600 + Math.Max(0, ncH) + MainMenu.ActualHeight;
    }

    // ───────────────────────── 세션 ─────────────────────────

    private void WireSession()
    {
        _session.BookClosing += (_, old) =>
        {
            if (old is not null && Viewer.Zoom.InitialFitApplied)
                _session.ZoomMemory.Remember(old.SeriesKey, Viewer.Zoom.ManualFactor);
        };
        _session.Changed += (_, change) => OnSessionChanged(change);
        _session.Notices.Changed += (_, _) => UpdateBanner();
        _monitor.SourceChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_session.Book is null) return;
            _session.Notices.Post(new Notice(NoticeCategory.SourceChanged, Loc.T("This book changed on disk."), [NoticeAction.Reload, NoticeAction.Dismiss]));
        });
    }

    private void OnSessionChanged(ReaderChange change)
    {
        Viewer.OnSessionChanged(change);
        if (change.HasFlag(ReaderChange.Book))
        {
            var book = _session.Book;
            _thumbnails.SetBook(book, i => _cache.GetSize(i));
            _monitor.Watch(book);
            _session.Notices.Clear(NoticeCategory.SourceChanged);
            _session.Notices.Clear(NoticeCategory.ReaderError);
            _session.Notices.Clear(NoticeCategory.Volume);
            if (book is not null)
            {
                EnsureLibraryRootFor(book);
                _sidebar.SetActivePath(book.LibraryPath);
            }
            BookChanged?.Invoke(this, EventArgs.Empty);
        }
        if (change.HasFlag(ReaderChange.Layout)) Viewer.ResetWheelState();
        if (change.HasFlag(ReaderChange.Error) && _session.LastError is { } err) PostLoadError(err);
        UpdateUi(change);
        if (change.HasFlag(ReaderChange.Book) || change.HasFlag(ReaderChange.Bookmarks) || change.HasFlag(ReaderChange.Favorite))
            RefreshSidebarSoon();
        if (change.HasFlag(ReaderChange.Page)) _sidebar.RefreshRowsSoon(Dispatcher);
    }

    private void PostLoadError(ReaderLoadError err)
    {
        var actions = new List<NoticeAction> { NoticeAction.Retry, NoticeAction.Open };
        if (err.Request.Intent is OpenIntent.Recent or OpenIntent.Continue or OpenIntent.Favorite or OpenIntent.RestoreLibrary
            && err.Kind is OpenErrorKind.NotFound or OpenErrorKind.InnerPathMissing or OpenErrorKind.TemporarilyUnavailable)
            actions.Add(NoticeAction.RemoveFromList);
        actions.Add(NoticeAction.Dismiss);
        var text = ReaderText.DescribeError(err.Kind, err.Message, err.Request.Intent);
        var name = Path.GetFileName(err.Request.Path.TrimEnd('\\', '/'));
        _session.Notices.Post(new Notice(NoticeCategory.ReaderError, string.IsNullOrEmpty(name) ? text : $"{text} — {name}", actions));
    }

    private void WireViewer()
    {
        Viewer.ViewChanged += (_, _) => UpdateToolbar();
        Viewer.VisiblePagesCommitted += (_, _) =>
        {
            VisiblePagesChanged?.Invoke(this, EventArgs.Empty);
            _thumbnails.SetActive(Viewer.CommittedRange, scroll: true);
        };
        Viewer.WheelTurnRequested += forward => _ = forward ? _session.ForwardAsync() : _session.BackwardAsync();
        Viewer.ContextMenuRequested += ShowPageContextMenu;
        _cache.SizesChanged += indices => _thumbnails.UpdateSizes(indices, i => _cache.GetSize(i));
        ViewerArea.MouseMove += (_, e) => UpdateChromeVisibility(e.GetPosition(ViewerArea));
        ViewerArea.MouseLeave += (_, _) => UpdateChromeVisibility(null);
    }

    private void WireSlider()
    {
        Slider.PageRequested += i => _session.Jump(i);
        Slider.PageNumberEntered += n => _session.JumpPage(n);
        Slider.FocusReaderRequested += FocusReader;
    }

    private void WireMisc()
    {
        EmptyOpenButton.Click += (_, _) => Execute(CommandIds.FileOpen);
        LoadingCancel.Click += (_, _) => _session.CancelLoading();
        PreviousOpen.Click += async (_, _) => { await _session.OpenPreviousVolumeAsync(); FocusReader(); };
        NextOpen.Click += async (_, _) => { await _session.OpenNextVolumeAsync(); FocusReader(); };
        RestartButton.Click += (_, _) => { _session.RestartFromBeginning(); FocusReader(); };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _session.Notices.Dismiss(_shownNoticeId);
        };
        _state.Changed += (_, change) => Dispatcher.BeginInvoke(() => OnStateChanged(change));
        PreviewKeyDown += OnPreviewKeyDown;
        KeyDown += OnKeyDownFallback;
        Drop += OnDrop;
        DragOver += OnDragOver;
        Closing += OnClosing;
    }

    private void OnStateChanged(StateChange change)
    {
        if (change.HasFlag(StateChange.Settings))
        {
            ApplyPinnedState();
            _services.Diagnostics.MinimumLevel = _state.Settings.LogLevel;
            _library.SetAutoRefresh(_state.Settings.LibraryAutoRefreshEnabled);
            var lang = Loc.Resolve(_state.Settings.AppLanguage);
            if (lang != Loc.Language) Loc.SetLanguage(lang);
            UpdateUi(ReaderChange.None);
        }
        if (change.HasFlag(StateChange.Recents) || change.HasFlag(StateChange.Favorites) || change.HasFlag(StateChange.Bookmarks)
            || change.HasFlag(StateChange.Progress) || change.HasFlag(StateChange.LibraryRoot))
            RefreshSidebarSoon();
    }

    private void OnFeatureStateChanged()
    {
        Viewer.InvalidateOverlay();
        var translated = _feature.IsEnabled ? _feature.TranslatedPageIndices : new HashSet<int>();
        _thumbnails.SetTranslated(translated);
        UpdateUi(ReaderChange.None);
    }

    // ───────────────────────── 화면 갱신 ─────────────────────────

    private void UpdateStaticTexts()
    {
        EmptyText.Text = Loc.T("Open a folder or comic archive to start reading.");
        EmptyOpenButton.Content = Loc.T("Open…");
        EmptyOpenButton.ToolTip = CommandRegistry.Tooltip(Loc.T("Open Folder, CBZ/CBR, or ZIP/RAR…"), CommandIds.FileOpen);
        LoadingCancel.Content = Loc.T("Cancel");
        PreviousEyebrow.Text = Loc.T("Previous Volume");
        PreviousOpen.Content = Loc.T("Open");
        NextEyebrow.Text = Loc.T("Up next");
        NextOpen.Content = Loc.T("Open");
        RestartButton.Content = Loc.T("Read Again from the Start");
        UpdateBanner();
    }

    private void UpdateUi(ReaderChange change)
    {
        var book = _session.Book;
        var hasBook = book is not null;
        Title = hasBook ? $"{book!.Title} — Panely" : "Panely";
        EmptyState.Visibility = hasBook || _session.IsLoading ? Visibility.Collapsed : Visibility.Visible;

        // 로딩 차폐
        LoadingScrim.Visibility = _session.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Text = ReaderText.StageText(_session.Stage);

        // 슬라이더: 책이 열려 있고 두 쪽 이상일 때만
        var showSlider = hasBook && _session.PageCount >= 2;
        Slider.Visibility = showSlider ? (_chromeVisible ? Visibility.Visible : Visibility.Hidden) : Visibility.Collapsed;
        if (hasBook)
        {
            var translated = _feature.IsEnabled ? _feature.TranslatedPageIndices : new HashSet<int>();
            Slider.Update(book!.Volumes.Count > 1 ? book.VolumeIndex : -1, book.Volumes.Count, _session.Spread, _session.PageCount, translated);
        }

        // 썸네일
        var thumbs = _state.Settings.ThumbnailSidebarVisible && hasBook;
        ThumbnailHost.Visibility = thumbs ? Visibility.Visible : Visibility.Collapsed;
        if (thumbs && (change.HasFlag(ReaderChange.Page) || change.HasFlag(ReaderChange.Book)))
            _thumbnails.SetActive(_session.Spread, scroll: true);

        // 권 카드
        if (_session.ShowNextVolumeCard && book!.NextVolume is { } next)
        {
            NextTitle.Text = next.DisplayName;
            NextHint.Text = Loc.T("Turn the page once more to open it.");
            NextCard.Visibility = Visibility.Visible;
        }
        else NextCard.Visibility = Visibility.Collapsed;

        if (_session.ShowPreviousVolumeCard && book!.PreviousVolume is { } prev)
        {
            PreviousTitle.Text = prev.DisplayName;
            PreviousHint.Text = Loc.T("Go back once more to open it.");
            PreviousCard.Visibility = Visibility.Visible;
        }
        else PreviousCard.Visibility = Visibility.Collapsed;

        UpdateToolbar();
        PositionCards();
        UpdateChromeVisibility(null);
    }

    private void PositionCards()
    {
        var top = 24.0;
        if (ToolbarHost.Visibility == Visibility.Visible && _chromeVisible) top = Math.Max(top, 12 + 40 + 12);
        if (Banner.Visibility == Visibility.Visible) top = Math.Max(top, 60 + Math.Max(Banner.ActualHeight, 40) + 12);
        PreviousCard.Margin = new Thickness(0, top, 0, 0);
        var bottom = 24.0;
        if (Slider.Visibility == Visibility.Visible) bottom = Math.Max(bottom, 64);
        if (ProgressLayer.Children.Count > 0) bottom = Math.Max(bottom, 76 + ProgressLayer.ActualHeight + 8);
        NextCard.Margin = new Thickness(0, 0, 0, bottom);
    }

    private ToolbarState CurrentToolbarState()
    {
        var s = _state.Settings;
        var hasBook = _session.Book is not null;
        var zoom = Viewer.Zoom;
        return new ToolbarState
        {
            HasBook = hasBook,
            IsLoading = _session.IsLoading,
            LibraryPinned = s.SidebarPinned,
            ToolbarPinned = s.ToolbarPinned,
            ThumbnailsVisible = s.ThumbnailSidebarVisible,
            Layout = _session.Layout,
            Direction = _session.Direction,
            CoverAlone = _session.CoverAlone,
            FitMode = _session.FitMode,
            AtFitScreen = hasBook && zoom.IsAtFit(FitMode.FitScreen),
            AtFitWidth = hasBook && zoom.IsAtFit(FitMode.FitWidth),
            AtFitHeight = hasBook && zoom.IsAtFit(FitMode.FitHeight),
            CanZoomIn = zoom.Zoom < Panely.Domain.Reading.ZoomPolicy.Max - 1e-6,
            CanZoomOut = zoom.Zoom > Panely.Domain.Reading.ZoomPolicy.Min + 1e-6,
            SizeLocked = !s.AutoFitOnResize,
            IsFavorite = _session.IsFavorite,
            IsBookmarked = _session.IsCurrentPageBookmarked,
            HasBookmarks = hasBook && _session.BookmarkedPages.Count > 0,
            TranslationEnabled = _feature.IsEnabled,
            TranslationActive = _feature.IsEnabled && _feature.IsOverlayActive,
            TranslationLabel = _feature.IsEnabled ? _feature.VisibleLanguageLabel : string.Empty,
            CanPreviousPage = hasBook && _session.CanGoPrevious,
            CanNextPage = hasBook && _session.CanGoNext,
            HasPreviousVolume = _session.Book?.PreviousVolume is not null,
            HasNextVolume = _session.Book?.NextVolume is not null,
            HasVolumes = (_session.Book?.Volumes.Count ?? 0) > 1,
            WheelPageTurn = s.WheelPageTurn,
        };
    }

    private void UpdateToolbar() => _toolbar.Update(CurrentToolbarState());

    /// <summary>상/하단 80 DIP에 포인터가 있으면 툴바와 슬라이더를 함께 보인다. 고정·메뉴 열림·입력 중이면 유지.</summary>
    private void UpdateChromeVisibility(Point? pointer)
    {
        bool show;
        if (_state.Settings.ToolbarPinned || _session.Book is null || _toolbar.IsMenuOpen || Slider.IsEditing
            || _toolbar.IsKeyboardFocusWithin || Slider.IsKeyboardFocusWithin || Slider.IsMouseCaptureWithin)
            show = true;
        else if (pointer is { } p)
            show = p.Y <= ChromeHoverZone || p.Y >= ViewerArea.ActualHeight - ChromeHoverZone;
        else
            show = ToolbarHost.IsMouseOver || Slider.IsMouseOver;

        if (show == _chromeVisible && ToolbarHost.Visibility != Visibility.Collapsed) return;
        _chromeVisible = show;
        Fade(ToolbarHost, show);
        if (Slider.Visibility != Visibility.Collapsed) Fade(Slider, show);
        PositionCards();
    }

    private static void Fade(UIElement element, bool show)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
            element.Visibility = show ? Visibility.Visible : Visibility.Hidden;
            return;
        }
        if (show)
        {
            element.Visibility = Visibility.Visible;
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            anim.Completed += (_, _) => { if (element.Opacity < 0.01) element.Visibility = Visibility.Hidden; };
            element.BeginAnimation(OpacityProperty, anim);
        }
    }

    // ───────────────────────── 배너 ─────────────────────────

    private void UpdateBanner()
    {
        var notice = _session.Notices.Current;
        _noticeTimer.Stop();
        if (notice is null)
        {
            Banner.Visibility = Visibility.Collapsed;
            PositionCards();
            return;
        }
        _shownNoticeId = notice.Id;
        BannerText.Text = notice.Message;
        BannerIcon.Glyph = notice.Category == NoticeCategory.ReaderError ? Icons.Warning : Icons.Info;
        BannerActions.Children.Clear();
        foreach (var action in notice.Actions)
        {
            var label = action switch
            {
                NoticeAction.Reload => Loc.T("Reload"),
                NoticeAction.Retry => Loc.T("Retry"),
                NoticeAction.Open => Loc.T("Open…"),
                NoticeAction.RemoveFromList => Loc.T("Remove from List"),
                NoticeAction.Dismiss => Loc.T("Dismiss"),
                _ => action.ToString(),
            };
            var button = new Button { Content = label, Style = (Style)FindResource(action is NoticeAction.Reload or NoticeAction.Retry ? "Panely.AccentButton" : "Panely.Button"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
            var captured = notice;
            button.Click += (_, _) => OnNoticeAction(captured, action);
            BannerActions.Children.Add(button);
        }
        Banner.Visibility = Visibility.Visible;
        if (notice.Duration is { } d)
        {
            _noticeTimer.Interval = d;
            _noticeTimer.Start();
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PositionCards);
    }

    private async void OnNoticeAction(Notice notice, NoticeAction action)
    {
        _session.Notices.Dismiss(notice.Id);
        switch (action)
        {
            case NoticeAction.Reload:
                await _session.ReloadAsync();
                break;
            case NoticeAction.Retry:
                await _session.RetryAsync();
                break;
            case NoticeAction.Open:
                Execute(CommandIds.FileOpen);
                break;
            case NoticeAction.RemoveFromList:
                RemoveFailedFromList();
                break;
        }
        FocusReader();
    }

    private void RemoveFailedFromList()
    {
        if (_session.LastError is not { } err) return;
        var source = _state.FindSourceByPath(err.Request.Path);
        if (source is not null)
        {
            _state.RemoveRecent(source.Id);
            if (err.Request.Intent == OpenIntent.Continue)
                _state.RemoveProgress(BookKeys.Make(source.Id, err.Request.InnerPath));
        }
        _session.ClearError();
    }

    // ───────────────────────── 라이브러리 고정/자동 숨김 ─────────────────────────

    private void ApplyPinnedState()
    {
        var pinned = _state.Settings.SidebarPinned;
        _sidebar.SetPinned(pinned);
        if (pinned)
        {
            LibraryOverlay.Child = null;
            LibraryOverlay.Visibility = Visibility.Collapsed;
            _libraryOverlayOpen = false;
            if (PinnedLibraryHost.Child != _sidebar) PinnedLibraryHost.Child = _sidebar;
            PinnedLibraryHost.Visibility = Visibility.Visible;
            HotEdge.Visibility = Visibility.Collapsed;
        }
        else
        {
            PinnedLibraryHost.Child = null;
            PinnedLibraryHost.Visibility = Visibility.Collapsed;
            if (LibraryOverlay.Child != _sidebar) LibraryOverlay.Child = _sidebar;
            LibraryOverlay.Visibility = _libraryOverlayOpen ? Visibility.Visible : Visibility.Collapsed;
            HotEdge.Visibility = Visibility.Visible;
        }
        ThumbnailHost.Visibility = _state.Settings.ThumbnailSidebarVisible && _session.Book is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void WireLibraryHover()
    {
        HotEdge.MouseEnter += (_, _) =>
        {
            HotEdgeIndicator.Opacity = 1;
            _dismissTimer.Stop();
            _revealTimer.Start();
        };
        HotEdge.MouseLeave += (_, _) =>
        {
            HotEdgeIndicator.Opacity = 0;
            _revealTimer.Stop();
        };
        _revealTimer.Tick += (_, _) => { _revealTimer.Stop(); OpenLibraryOverlay(); };
        _dismissTimer.Tick += (_, _) => { _dismissTimer.Stop(); CloseLibraryOverlay(); };
        LibraryOverlay.MouseEnter += (_, _) => _dismissTimer.Stop();
        LibraryOverlay.MouseLeave += (_, _) =>
        {
            if (_libraryOverlayOpen && !_sidebar.IsKeyboardFocusWithin && !HasOpenContextMenu()) _dismissTimer.Start();
        };
    }

    private bool HasOpenContextMenu() => _sidebar.ContextMenu?.IsOpen == true;

    private void OpenLibraryOverlay()
    {
        if (_state.Settings.SidebarPinned) return;
        _libraryOverlayOpen = true;
        LibraryOverlay.Visibility = Visibility.Visible;
        HotEdgeIndicator.Opacity = 0;
    }

    private void CloseLibraryOverlay()
    {
        if (!_libraryOverlayOpen) return;
        _libraryOverlayOpen = false;
        LibraryOverlay.Visibility = Visibility.Collapsed;
        _dismissTimer.Stop();
    }

    // ───────────────────────── 드롭·종료 ─────────────────────────

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            _explicitOpenSeen = true;
            _ = OpenPathAsync(files[0], OpenIntent.Normal);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _closing = true;
        try
        {
            _session.FlushPosition();
            var shutdown = _feature.ShutdownAsync();
            await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        catch (Exception ex)
        {
            _services.Diagnostics.Warning(LogCategory.App, $"Shutdown: {ex.GetType().Name}");
        }
        foreach (var w in OwnedWindows.Cast<Window>().ToList()) w.Close();
        _settingsWindow?.Close();
        _session.Dispose();
        _cache.Dispose();
        Application.Current.Shutdown();
    }

    public void FocusReader()
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase { IsVisible: true } tb && tb.IsKeyboardFocused && Slider.IsEditing) return;
            Viewer.Focus();
            Keyboard.Focus(Viewer);
        });
    }
}
