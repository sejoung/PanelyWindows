using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Panely.Core.Commands;
using Panely.Core.Localization;
using Panely.Domain.Model;

namespace Panely.App.Controls;

/// <summary>
/// 플로팅 툴바(02): inset 12, 높이 40(버튼 32 + 위아래 4), radius 10, border 1, 배경 BgSecondary α.94(blur 대체).
/// 항목 순서 고정. 폭을 실측해 전체 → 맞춤/줌 메뉴 → 레이아웃 메뉴 → 추가 overflow 순서로 축약한다.
/// </summary>
public sealed class FloatingToolbar : Border
{
    public const string BookmarkListId = "toolbar.bookmarks";
    public const string TranslationId = "toolbar.translation";

    private const double ButtonSize = 32;
    private const double Gap = 4;
    private const double DividerWidth = 1 + 4;
    private const double HorizontalPadding = 8;
    private const double MinSpacer = 8;

    private readonly Action<string> _invoke;
    private readonly Func<IReadOnlyList<Control>> _translationMenu;
    private readonly Func<IReadOnlyList<Control>> _bookmarkMenu;
    private readonly StackPanel _left = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _right = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    private readonly Button _open, _bookmarkList, _translateMenu;
    private readonly ToggleButton _libPin, _toolPin, _single, _double, _vertical, _cover, _lock, _thumbs, _translate;
    private readonly Button _direction, _fitScreen, _fitWidth, _fitHeight, _zoomOut, _zoomIn, _favorite, _bookmark;
    private readonly Button _prevVol, _prevPage, _nextPage, _nextVol;
    private readonly Button _layoutMenu, _fitMenu, _moreMenu;
    private readonly TextBlock _translateLabel = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
    private ToolbarState _state = new();
    private int _mode = -1;

    public FloatingToolbar(Action<string> invoke, Func<IReadOnlyList<Control>> translationMenu, Func<IReadOnlyList<Control>> bookmarkMenu)
    {
        _invoke = invoke;
        _translationMenu = translationMenu;
        _bookmarkMenu = bookmarkMenu;
        Height = 40;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(HorizontalPadding, 4, HorizontalPadding, 4);
        SetResourceReference(BackgroundProperty, "Brush.ToolbarFallback");
        SetResourceReference(BorderBrushProperty, "Brush.BorderSubtle");
        Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black };
        AutomationProperties.SetName(this, "Toolbar");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Continue);

        _open = Btn(CommandIds.FileOpen, Icons.Folder);
        _libPin = Toggle(CommandIds.LibraryPin, Icons.Pin);
        _toolPin = Toggle(CommandIds.ToolbarPin, Icons.PinToolbar);
        _single = Toggle(CommandIds.LayoutSingle, Icons.Single);
        _double = Toggle(CommandIds.LayoutDouble, Icons.Double);
        _vertical = Toggle(CommandIds.LayoutVertical, Icons.Vertical);
        _direction = Btn(CommandIds.ReadingDirection, Icons.ArrowRight);
        _cover = Toggle(CommandIds.SpreadCover, Icons.Cover);
        _fitScreen = Btn(CommandIds.FitScreen, Icons.FitScreen);
        _fitWidth = Btn(CommandIds.FitWidth, Icons.FitWidth);
        _fitHeight = Btn(CommandIds.FitHeight, Icons.FitHeight);
        _zoomOut = Btn(CommandIds.ZoomOut, Icons.ZoomOut);
        _zoomIn = Btn(CommandIds.ZoomIn, Icons.ZoomIn);
        _lock = Toggle(CommandIds.ViewLock, Icons.Unlock);
        _favorite = Btn(CommandIds.FavoriteToggle, Icons.Star);
        _bookmark = Btn(CommandIds.BookmarkToggle, Icons.Bookmark);
        _bookmarkList = Btn(BookmarkListId, Icons.BookmarkList);
        _thumbs = Toggle(CommandIds.ThumbnailsToggle, Icons.Thumbnails);
        _translate = Toggle(TranslationId, Icons.Translate);
        _translateMenu = new Button { Style = IconStyle, Width = double.NaN, Padding = new Thickness(4, 0, 4, 0), Focusable = true };
        var tmContent = new StackPanel { Orientation = Orientation.Horizontal };
        tmContent.Children.Add(_translateLabel);
        tmContent.Children.Add(new IconGlyph { Glyph = Icons.ChevronDown, Width = 10, Height = 10 });
        _translateMenu.Content = tmContent;
        _translateMenu.Click += (_, _) => OpenMenu(_translateMenu, _translationMenu());
        _prevVol = Btn(CommandIds.VolumePrevious, Icons.DoubleChevronLeft);
        _prevPage = Btn("page.previousButton", Icons.ChevronLeft);
        _nextPage = Btn("page.nextButton", Icons.ChevronRight);
        _nextVol = Btn(CommandIds.VolumeNext, Icons.DoubleChevronRight);
        _layoutMenu = MenuBtn(Icons.Single, BuildLayoutMenu);
        _fitMenu = MenuBtn(Icons.FitScreen, BuildFitMenu);
        _moreMenu = MenuBtn(Icons.More, BuildMoreMenu);
        _bookmarkList.Click -= OnClick;
        _bookmarkList.Click += (_, _) => OpenMenu(_bookmarkList, _bookmarkMenu());

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_right, 1);
        grid.Children.Add(_left);
        grid.Children.Add(_right);
        Child = grid;

        SizeChanged += (_, _) => Arrange(force: false);
        Loc.LanguageChanged += (_, _) => { UpdateTooltips(); Arrange(force: true); };
        UpdateTooltips();
    }

    private static Style IconStyle => (Style)Application.Current.FindResource("Panely.IconButton");

    /// <summary>열린 메뉴가 있는 동안 툴바를 숨기지 않는다.</summary>
    public bool IsMenuOpen { get; private set; }

    public event EventHandler? MenuOpenChanged;

    private Button Btn(string id, Geometry glyph)
    {
        var b = new Button { Style = IconStyle, Tag = null, Content = new IconGlyph { Glyph = glyph }, CommandParameter = id };
        b.Click += OnClick;
        return b;
    }

    private ToggleButton Toggle(string id, Geometry glyph)
    {
        var b = new ToggleButton { Style = IconStyle, Content = new IconGlyph { Glyph = glyph }, CommandParameter = id };
        b.Click += OnClick;
        return b;
    }

    private Button MenuBtn(Geometry glyph, Func<IReadOnlyList<Control>> items)
    {
        var b = new Button { Style = IconStyle, Content = new IconGlyph { Glyph = glyph } };
        b.Click += (_, _) => OpenMenu(b, items());
        return b;
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is ButtonBase { CommandParameter: string id } b)
        {
            if (b is ToggleButton t) t.IsChecked = !t.IsChecked; // 상태는 Update가 결정
            _invoke(id);
        }
    }

    private void OpenMenu(FrameworkElement anchor, IReadOnlyList<Control> items)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
        foreach (var item in items)
        {
            if (item.Parent is ItemsControl old) old.Items.Remove(item);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;
        menu.Opened += (_, _) => { IsMenuOpen = true; MenuOpenChanged?.Invoke(this, EventArgs.Empty); };
        menu.Closed += (_, _) => { IsMenuOpen = false; MenuOpenChanged?.Invoke(this, EventArgs.Empty); };
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, string? gestureId, bool isChecked, bool enabled, Action click)
    {
        var mi = new MenuItem { Header = header, IsCheckable = false, IsChecked = isChecked, IsEnabled = enabled };
        if (gestureId is not null) mi.InputGestureText = CommandRegistry.GestureText(gestureId) ?? string.Empty;
        mi.Click += (_, _) => click();
        return mi;
    }

    private IReadOnlyList<Control> BuildLayoutMenu()
    {
        var s = _state;
        var list = new List<Control>
        {
            Item(Loc.T("Single Page"), CommandIds.LayoutSingle, s.Layout == ReadingLayout.Single, s.HasBook || true, () => _invoke(CommandIds.LayoutSingle)),
            Item(Loc.T("Double Page"), CommandIds.LayoutDouble, s.Layout == ReadingLayout.Double, true, () => _invoke(CommandIds.LayoutDouble)),
            Item(Loc.T("Vertical Scroll"), CommandIds.LayoutVertical, s.Layout == ReadingLayout.Vertical, true, () => _invoke(CommandIds.LayoutVertical)),
            new Separator(),
            Item(s.Direction == ReadingDirection.RightToLeft ? Loc.T("Read Right to Left") : Loc.T("Read Left to Right"), null, false,
                s.Layout != ReadingLayout.Vertical, () => _invoke(CommandIds.ReadingDirection)),
        };
        if (s.Layout == ReadingLayout.Double)
            list.Add(Item(Loc.T("Offset spread (standalone cover)"), null, s.CoverAlone, true, () => _invoke(CommandIds.SpreadCover)));
        return list;
    }

    private IReadOnlyList<Control> BuildFitMenu()
    {
        var s = _state;
        return
        [
            Item(Loc.T("Fit to Screen"), CommandIds.FitScreen, s.AtFitScreen, s.HasBook, () => _invoke(CommandIds.FitScreen)),
            Item(Loc.T("Fit to Width"), CommandIds.FitWidth, s.AtFitWidth, s.HasBook, () => _invoke(CommandIds.FitWidth)),
            Item(Loc.T("Fit to Height"), CommandIds.FitHeight, s.AtFitHeight, s.HasBook, () => _invoke(CommandIds.FitHeight)),
            new Separator(),
            Item(Loc.T("Zoom Out"), CommandIds.ZoomOut, false, s.HasBook && s.CanZoomOut, () => _invoke(CommandIds.ZoomOut)),
            Item(Loc.T("Zoom In"), CommandIds.ZoomIn, false, s.HasBook && s.CanZoomIn, () => _invoke(CommandIds.ZoomIn)),
            Item(Loc.T("Reset Zoom"), CommandIds.ZoomReset, false, s.HasBook, () => _invoke(CommandIds.ZoomReset)),
            new Separator(),
            Item(Loc.T("Lock View Size"), CommandIds.ViewLock, s.SizeLocked, true, () => _invoke(CommandIds.ViewLock)),
        ];
    }

    private IReadOnlyList<Control> BuildMoreMenu()
    {
        var s = _state;
        var list = new List<Control>
        {
            Item(s.IsFavorite ? Loc.T("Remove from Favorites") : Loc.T("Add to Favorites"), CommandIds.FavoriteToggle, false, s.HasBook, () => _invoke(CommandIds.FavoriteToggle)),
            Item(s.IsBookmarked ? Loc.T("Remove Page Bookmark") : Loc.T("Add Page Bookmark"), CommandIds.BookmarkToggle, false, s.HasBook, () => _invoke(CommandIds.BookmarkToggle)),
        };
        var bookmarks = new MenuItem { Header = Loc.T("Bookmark List") };
        foreach (var c in _bookmarkMenu()) bookmarks.Items.Add(c);
        bookmarks.IsEnabled = bookmarks.Items.Count > 0;
        list.Add(bookmarks);
        list.Add(Item(s.ThumbnailsVisible ? Loc.T("Hide Thumbnails") : Loc.T("Show Thumbnails"), CommandIds.ThumbnailsToggle, s.ThumbnailsVisible, s.HasBook, () => _invoke(CommandIds.ThumbnailsToggle)));
        if (s.TranslationEnabled)
        {
            var tr = new MenuItem { Header = Loc.T("Translation") + " · " + s.TranslationLabel };
            foreach (var c in _translationMenu()) tr.Items.Add(c);
            list.Add(tr);
        }
        return list;
    }

    private void UpdateTooltips()
    {
        void Tip(FrameworkElement e, string label, string? id)
        {
            var text = id is null ? label : CommandRegistry.Tooltip(label, id);
            e.ToolTip = text;
            AutomationProperties.SetName(e, label);
        }
        var s = _state;
        Tip(_open, Loc.T("Open…"), CommandIds.FileOpen);
        Tip(_libPin, s.LibraryPinned ? Loc.T("Unpin Library") : Loc.T("Pin Library"), CommandIds.LibraryPin);
        Tip(_toolPin, s.ToolbarPinned ? Loc.T("Unpin Toolbar") : Loc.T("Pin Toolbar"), CommandIds.ToolbarPin);
        Tip(_single, Loc.T("Single Page"), CommandIds.LayoutSingle);
        Tip(_double, Loc.T("Double Page"), CommandIds.LayoutDouble);
        Tip(_vertical, Loc.T("Vertical Scroll"), CommandIds.LayoutVertical);
        Tip(_direction, s.Layout == ReadingLayout.Vertical ? Loc.T("Reading direction is fixed in vertical mode")
            : s.Direction == ReadingDirection.RightToLeft ? Loc.T("Read Right to Left") : Loc.T("Read Left to Right"), null);
        Tip(_cover, Loc.T("Offset spread (standalone cover)"), null);
        Tip(_fitScreen, Loc.T("Fit to Screen"), CommandIds.FitScreen);
        Tip(_fitWidth, Loc.T("Fit to Width"), CommandIds.FitWidth);
        Tip(_fitHeight, Loc.T("Fit to Height"), CommandIds.FitHeight);
        Tip(_zoomOut, Loc.T("Zoom Out"), CommandIds.ZoomOut);
        Tip(_zoomIn, Loc.T("Zoom In"), CommandIds.ZoomIn);
        Tip(_lock, s.SizeLocked ? Loc.T("Unlock view size (auto-fit on resize)") : Loc.T("Lock view size (don't auto-fit on resize)"), CommandIds.ViewLock);
        Tip(_favorite, s.IsFavorite ? Loc.T("Remove from Favorites") : Loc.T("Add to Favorites"), CommandIds.FavoriteToggle);
        Tip(_bookmark, s.IsBookmarked ? Loc.T("Remove Page Bookmark") : Loc.T("Bookmark Current Page"), CommandIds.BookmarkToggle);
        Tip(_bookmarkList, Loc.T("Bookmark List"), null);
        Tip(_thumbs, s.ThumbnailsVisible ? Loc.T("Hide Thumbnails") : Loc.T("Show Thumbnails"), CommandIds.ThumbnailsToggle);
        Tip(_translate, s.TranslationActive ? Loc.T("Show Original") : Loc.T("Show Translation"), CommandIds.TranslationToggle);
        Tip(_translateMenu, Loc.T("Choose Language"), null);
        Tip(_prevVol, s.HasPreviousVolume ? Loc.T("Previous Volume") : Loc.T("This is the first book in the folder."), CommandIds.VolumePrevious);
        Tip(_prevPage, Loc.T("Previous Page"), null);
        Tip(_nextPage, Loc.T("Next Page"), null);
        Tip(_nextVol, s.HasNextVolume ? Loc.T("Next Volume") : Loc.T("This is the last book in the folder."), CommandIds.VolumeNext);
        Tip(_layoutMenu, Loc.T("Page Layout"), null);
        Tip(_fitMenu, Loc.T("Fit & Zoom"), null);
        Tip(_moreMenu, Loc.T("More"), null);
        if (!s.HasBook)
        {
            foreach (var b in new FrameworkElement[] { _fitScreen, _fitWidth, _fitHeight, _zoomIn, _zoomOut, _favorite, _bookmark, _thumbs })
                b.ToolTip = $"{b.ToolTip} — {Loc.T("No image loaded")}";
        }
    }

    public void Update(ToolbarState state)
    {
        var layoutChanged = _state.Layout != state.Layout || _state.TranslationEnabled != state.TranslationEnabled
                            || _state.TranslationLabel != state.TranslationLabel;
        _state = state;
        _libPin.IsChecked = state.LibraryPinned;
        ((IconGlyph)_libPin.Content).Filled = state.LibraryPinned;
        _toolPin.IsChecked = state.ToolbarPinned;
        _single.IsChecked = state.Layout == ReadingLayout.Single;
        _double.IsChecked = state.Layout == ReadingLayout.Double;
        _vertical.IsChecked = state.Layout == ReadingLayout.Vertical;
        ((IconGlyph)_direction.Content).Glyph = state.Direction == ReadingDirection.RightToLeft ? Icons.ArrowLeft : Icons.ArrowRight;
        _direction.IsEnabled = state.Layout != ReadingLayout.Vertical;
        _cover.IsChecked = state.CoverAlone;
        _fitScreen.Tag = state.AtFitScreen ? "selected" : null;
        _fitWidth.Tag = state.AtFitWidth ? "selected" : null;
        _fitHeight.Tag = state.AtFitHeight ? "selected" : null;
        foreach (var b in new UIElement[] { _fitScreen, _fitWidth, _fitHeight }) b.IsEnabled = state.HasBook;
        _zoomIn.IsEnabled = state.HasBook && state.CanZoomIn;
        _zoomOut.IsEnabled = state.HasBook && state.CanZoomOut;
        _lock.IsChecked = state.SizeLocked;
        ((IconGlyph)_lock.Content).Glyph = state.SizeLocked ? Icons.Lock : Icons.Unlock;
        _favorite.IsEnabled = state.HasBook;
        ((IconGlyph)_favorite.Content).Filled = state.IsFavorite;
        _favorite.Tag = null;
        _bookmark.IsEnabled = state.HasBook;
        ((IconGlyph)_bookmark.Content).Filled = state.IsBookmarked;
        _bookmarkList.IsEnabled = state.HasBookmarks;
        _thumbs.IsChecked = state.ThumbnailsVisible;
        _thumbs.IsEnabled = state.HasBook;
        _translate.IsChecked = state.TranslationActive;
        _translate.IsEnabled = state.HasBook;
        _translateLabel.Text = state.TranslationLabel;
        _translateMenu.IsEnabled = state.HasBook;
        _prevPage.IsEnabled = state.CanPreviousPage;
        _nextPage.IsEnabled = state.CanNextPage;
        _prevVol.IsEnabled = state.HasBook && state.HasPreviousVolume;
        _nextVol.IsEnabled = state.HasBook && state.HasNextVolume;
        UpdateTooltips();
        Arrange(force: layoutChanged);
    }

    /// <summary>이름이 가장 긴 번역 언어명까지 고려해 실측.</summary>
    private double TranslationWidth()
    {
        if (!_state.TranslationEnabled) return 0;
        var ft = new FormattedText(_state.TranslationLabel, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ButtonSize + Gap + ft.WidthIncludingTrailingWhitespace + 8 + 10 + 4 + Gap;
    }

    private static double W(int buttons) => buttons * (ButtonSize + Gap);

    private double RequiredWidth(int mode)
    {
        var s = _state;
        var a = W(3) + DividerWidth;
        var layout = mode >= 2 ? W(1) : W(4 + (s.Layout == ReadingLayout.Double ? 1 : 0));
        var fit = mode >= 1 ? W(1) : W(6);
        var d = mode >= 3 ? W(1) : DividerWidth + W(4) + TranslationWidth();
        var right = W(4);
        return a + layout + fit + d + MinSpacer + right + HorizontalPadding * 2 + 2;
    }

    private void Arrange(bool force)
    {
        var available = ActualWidth > 0 ? ActualWidth : (Parent as FrameworkElement)?.ActualWidth ?? 2000;
        var mode = 3;
        for (var m = 0; m <= 3; m++)
        {
            if (RequiredWidth(m) <= available) { mode = m; break; }
        }
        if (mode == _mode && !force) return;
        _mode = mode;

        Detach(_left);
        Detach(_right);
        void Add(Panel p, UIElement e)
        {
            if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 0, Gap, 0);
            p.Children.Add(e);
        }
        Add(_left, _open);
        Add(_left, _libPin);
        Add(_left, _toolPin);
        _left.Children.Add(Divider());
        if (mode >= 2)
        {
            ((IconGlyph)_layoutMenu.Content).Glyph = _state.Layout switch
            {
                ReadingLayout.Double => Icons.Double,
                ReadingLayout.Vertical => Icons.Vertical,
                _ => Icons.Single,
            };
            Add(_left, _layoutMenu);
        }
        else
        {
            Add(_left, _single);
            Add(_left, _double);
            Add(_left, _vertical);
            Add(_left, _direction);
            if (_state.Layout == ReadingLayout.Double) Add(_left, _cover);
        }
        if (mode >= 1) Add(_left, _fitMenu);
        else
        {
            Add(_left, _fitScreen);
            Add(_left, _fitWidth);
            Add(_left, _fitHeight);
            Add(_left, _zoomOut);
            Add(_left, _zoomIn);
            Add(_left, _lock);
        }
        if (mode >= 3) Add(_left, _moreMenu);
        else
        {
            _left.Children.Add(Divider());
            Add(_left, _favorite);
            Add(_left, _bookmark);
            Add(_left, _bookmarkList);
            Add(_left, _thumbs);
            if (_state.TranslationEnabled)
            {
                Add(_left, _translate);
                Add(_left, _translateMenu);
            }
        }
        Add(_right, _prevVol);
        Add(_right, _prevPage);
        Add(_right, _nextPage);
        _right.Children.Add(_nextVol);
        _nextVol.Margin = new Thickness(0);
    }

    private static void Detach(Panel p) => p.Children.Clear();

    private Border Divider()
    {
        // 높이 18, 양쪽 4(왼쪽 4는 앞 버튼의 오른쪽 간격)
        var b = new Border { Width = 1, Height = 18, Margin = new Thickness(0, 0, Gap, 0), VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(BackgroundProperty, "Brush.BorderSubtle");
        return b;
    }

    /// <summary>번역 패널 앵커.</summary>
    public FrameworkElement TranslationAnchor => _translate;
}
