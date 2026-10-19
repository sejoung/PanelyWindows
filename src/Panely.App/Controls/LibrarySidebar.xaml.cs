using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Panely.Core.Abstractions;
using Panely.Core.Commands;
using Panely.Core.Localization;
using Path = System.IO.Path;

namespace Panely.App.Controls;

public sealed record ContinueEntry(string BookKey, string Title, double Fraction, bool Finished, OpenRequest Request);
public sealed record VolumeRow(string Title, OpenRequest Request, bool IsActive);
public sealed record FavoriteRow(Guid Id, string Title, OpenRequest? Request, bool IsActive);
public sealed record BookmarkRow(Guid Id, string BookKey, int PageIndex, string BookTitle, OpenRequest? Request);
public sealed record ProgressMark(double Fraction, bool Finished, bool Known);

public sealed class SidebarData
{
    public ContinueEntry? Continue { get; init; }
    public IReadOnlyList<VolumeRow> Volumes { get; init; } = [];
    public IReadOnlyList<FavoriteRow> Favorites { get; init; } = [];
    public string? CurrentBookKey { get; init; }
    public IReadOnlyList<BookmarkRow> CurrentBookmarks { get; init; } = [];
    public IReadOnlyList<BookmarkRow> OtherBookmarks { get; init; } = [];
}

/// <summary>
/// 라이브러리(02): 폭 240. 섹션 순서 이어보기 → (중첩 권) 권 목록 → 즐겨찾기 → 현재 책 북마크 → 다른 책 북마크 → 파일.
/// 파일 트리는 지연 탐색, 현재 책 조상 자동 펼침, 자동/수동 diff 새로고침에서 펼침·선택·스크롤을 보존한다.
/// </summary>
public partial class LibrarySidebar : UserControl
{
    private readonly TreeView _tree = new() { Margin = new Thickness(-4, 0, 0, 0) };
    private readonly TextBlock _treeEmpty = new() { Margin = new Thickness(8, 2, 8, 8), Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, TreeViewItem> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private ILibraryService? _library;
    private string? _rootPath;
    private string? _activePath;
    private SidebarData _data = new();

    public LibrarySidebar()
    {
        InitializeComponent();
        RefreshButton.Click += (_, _) => RefreshRequested?.Invoke();
        PinButton.Click += (_, _) => { PinButton.IsChecked = !PinButton.IsChecked; PinToggled?.Invoke(); };
        _tree.SelectedItemChanged += (_, _) => { };
        KeyboardNavigation.SetTabNavigation(_tree, KeyboardNavigationMode.Once);
        Loc.LanguageChanged += (_, _) => { UpdateHeader(); Rebuild(); };
        UpdateHeader();
        Rebuild();
    }

    public event Action<OpenRequest>? OpenRequested;
    public event Action<int>? PageRequested;
    public event Action<string>? RemoveContinueRequested;
    public event Action<Guid>? RemoveFavoriteRequested;
    public event Action<string, Guid>? RemoveBookmarkRequested;
    public event Action<string, string>? RemoveAllBookmarksRequested;
    public event Action? PickFolderRequested;
    public event Action? RefreshRequested;
    public event Action? PinToggled;
    /// <summary>항목을 고른 뒤 리더로 포커스를 돌린다.</summary>
    public event Action? ItemActivated;

    /// <summary>파일 트리 항목 우클릭 추가 메뉴(번역 내보내기 등).</summary>
    public Func<string, bool, IEnumerable<Control>>? ExtraContextItems { get; set; }

    /// <summary>파일 경로의 읽기 진행.</summary>
    public Func<string, ProgressMark?>? ProgressFor { get; set; }

    /// <summary>현재 책 북마크 썸네일.</summary>
    public Func<int, Task<BitmapSource?>>? BookmarkThumbnail { get; set; }

    public void SetPinned(bool pinned)
    {
        PinButton.IsChecked = pinned;
        PinGlyph.Filled = pinned;
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        var pinned = PinButton.IsChecked == true;
        PinButton.ToolTip = CommandRegistry.Tooltip(pinned ? Loc.T("Unpin Library") : Loc.T("Pin Library Open"), CommandIds.LibraryPin);
        AutomationProperties.SetName(PinButton, pinned ? Loc.T("Unpin Library") : Loc.T("Pin Library"));
        RefreshButton.ToolTip = Loc.T("Refresh File Tree");
        AutomationProperties.SetName(RefreshButton, Loc.T("Refresh File Tree"));
        RootName.Text = _rootPath is null ? Loc.T("Library") : DisplayName(_rootPath);
        RootName.ToolTip = _rootPath;
        // 헤더 우클릭: 라이브러리 루트 바꾸기(파일 › 폴더 열기와 달리 책을 열지 않는다)
        HeaderBar.ContextMenu = Menu(MenuItem(Loc.T("Change Library Root…"), () => PickFolderRequested?.Invoke()));
        RefreshButton.IsEnabled = _rootPath is not null;
        OverflowText.Text = Loc.T("Too many changes to follow automatically. Click Refresh File Tree to update the list.");
    }

    private static string DisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    // ───────────────────────── 섹션 ─────────────────────────

    public void SetData(SidebarData data)
    {
        _data = data;
        Rebuild();
    }

    private void Rebuild()
    {
        var offset = Scroller.VerticalOffset;
        Sections.Children.Clear();

        if (_data.Continue is { } cont)
        {
            Sections.Children.Add(SectionHeader(Loc.T("Continue Reading"), Icons.Bookmark));
            var row = Row(Icons.Archive, cont.Title, null, false, new ProgressMark(cont.Fraction, cont.Finished, true));
            row.ToolTip = $"{cont.Title} · {Loc.T("%lld%% read", (long)Math.Round(cont.Fraction * 100))}";
            row.MouseLeftButtonUp += (_, _) => Activate(cont.Request);
            row.ContextMenu = Menu(MenuItem(Loc.T("Remove from Continue Reading"), () => RemoveContinueRequested?.Invoke(cont.BookKey)));
            Sections.Children.Add(row);
        }

        if (_data.Volumes.Count > 0)
        {
            Sections.Children.Add(SectionHeader(Loc.T("Volumes"), Icons.Archive));
            foreach (var v in _data.Volumes)
            {
                var row = Row(Icons.Archive, v.Title, null, v.IsActive, null);
                row.MouseLeftButtonUp += (_, _) => Activate(v.Request);
                Sections.Children.Add(row);
            }
        }

        if (_data.Favorites.Count > 0)
        {
            Sections.Children.Add(SectionHeader(Loc.T("Favorites"), Icons.Star));
            foreach (var f in _data.Favorites)
            {
                var row = Row(Icons.Archive, f.Title, null, f.IsActive, null);
                row.MouseLeftButtonUp += (_, _) => { if (f.Request is { } r) Activate(r); };
                row.ContextMenu = Menu(MenuItem(Loc.T("Remove from Favorites"), () => RemoveFavoriteRequested?.Invoke(f.Id)));
                Sections.Children.Add(row);
            }
        }

        if (_data.CurrentBookKey is { } key)
        {
            var header = SectionHeader(_data.CurrentBookmarks.Count > 0 ? $"{Loc.T("Bookmarks")} {_data.CurrentBookmarks.Count}" : Loc.T("Bookmarks"), Icons.Bookmark);
            if (_data.CurrentBookmarks.Count > 0)
            {
                var trash = new Button { Style = (Style)FindResource("Panely.IconButton"), Width = 24, Height = 24, Content = new IconGlyph { Glyph = Icons.Trash, Width = 12, Height = 12 }, ToolTip = Loc.T("Remove All Bookmarks in This Book…") };
                trash.Click += (_, _) => RemoveAllBookmarksRequested?.Invoke(key, _data.CurrentBookmarks[0].BookTitle);
                DockPanel.SetDock(trash, Dock.Right);
                ((DockPanel)header).Children.Insert(0, trash);
            }
            Sections.Children.Add(header);
            if (_data.CurrentBookmarks.Count == 0)
            {
                Sections.Children.Add(new TextBlock { Text = Loc.T("No Bookmarks in This Book"), Style = (Style)FindResource("Text.Caption"), Margin = new Thickness(8, 2, 8, 6) });
            }
            foreach (var b in _data.CurrentBookmarks) Sections.Children.Add(BookmarkRowElement(b, current: true));
        }

        if (_data.OtherBookmarks.Count > 0)
        {
            Sections.Children.Add(SectionHeader(Loc.T("Bookmarks in Other Books"), Icons.Bookmark));
            foreach (var b in _data.OtherBookmarks) Sections.Children.Add(BookmarkRowElement(b, current: false));
        }

        Sections.Children.Add(SectionHeader(Loc.T("Files"), Icons.Folder));
        if (_rootPath is null)
        {
            Sections.Children.Add(new TextBlock { Text = Loc.T("Select a folder to browse books from."), Style = (Style)FindResource("Text.Caption"), Margin = new Thickness(8, 2, 8, 8) });
            var pick = new Button { Content = Loc.T("Pick Folder…"), Style = (Style)FindResource("Panely.Button"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 0, 0, 0) };
            pick.Click += (_, _) => PickFolderRequested?.Invoke();
            Sections.Children.Add(pick);
        }
        else
        {
            if (_tree.Parent is Panel p) p.Children.Remove(_tree);
            if (_treeEmpty.Parent is Panel p2) p2.Children.Remove(_treeEmpty);
            _treeEmpty.Style = (Style)FindResource("Text.Caption");
            _treeEmpty.Text = Loc.T("No books to show");
            Sections.Children.Add(_tree);
            Sections.Children.Add(_treeEmpty);
        }
        Scroller.ScrollToVerticalOffset(offset);
    }

    private FrameworkElement BookmarkRowElement(BookmarkRow b, bool current)
    {
        var panel = new DockPanel { Margin = new Thickness(4, 2, 0, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var remove = new Button { Style = (Style)FindResource("Panely.IconButton"), Width = 18, Height = 18, Content = new IconGlyph { Glyph = Icons.Close, Width = 10, Height = 10 }, ToolTip = Loc.T("Remove Bookmark"), Visibility = Visibility.Hidden };
        AutomationProperties.SetName(remove, Loc.T("Remove Bookmark"));
        remove.Click += (_, _) => RemoveBookmarkRequested?.Invoke(b.BookKey, b.Id);
        DockPanel.SetDock(remove, Dock.Right);
        panel.Children.Add(remove);
        panel.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
        panel.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;
        panel.GotKeyboardFocus += (_, _) => remove.Visibility = Visibility.Visible;

        var thumb = new Border { Width = 24, Height = 34, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 8, 0), ClipToBounds = true, BorderThickness = new Thickness(1) };
        thumb.SetResourceReference(Border.BackgroundProperty, "Brush.BgTertiary");
        thumb.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderSubtle");
        if (current && BookmarkThumbnail is { } provider)
        {
            _ = LoadThumb(thumb, provider, b.PageIndex);
        }
        panel.Children.Add(thumb);
        var text = new TextBlock
        {
            Text = current ? Loc.T("Page %lld", b.PageIndex + 1) : $"{b.BookTitle} · {Loc.T("p. %lld", b.PageIndex + 1)}",
            Style = (Style)FindResource("Text.Base"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        panel.Children.Add(text);
        panel.ToolTip = text.Text;
        panel.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, remove)) return;
            if (current) { PageRequested?.Invoke(b.PageIndex); ItemActivated?.Invoke(); }
            else if (b.Request is { } r) Activate(r);
        };
        panel.ContextMenu = Menu(MenuItem(Loc.T("Remove Bookmark"), () => RemoveBookmarkRequested?.Invoke(b.BookKey, b.Id)),
            MenuItem(Loc.T("Remove This Book's Bookmarks…"), () => RemoveAllBookmarksRequested?.Invoke(b.BookKey, b.BookTitle)));
        return panel;
    }

    private static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (var cur = d; cur is not null; cur = VisualTreeHelper.GetParent(cur))
            if (ReferenceEquals(cur, container)) return true;
        return false;
    }

    private static async Task LoadThumb(Border target, Func<int, Task<BitmapSource?>> provider, int index)
    {
        try
        {
            var image = await provider(index);
            if (image is not null) target.Child = new Image { Source = image, Stretch = Stretch.UniformToFill };
        }
        catch (Exception)
        {
        }
    }

    private void Activate(OpenRequest request)
    {
        OpenRequested?.Invoke(request);
        ItemActivated?.Invoke();
    }

    private FrameworkElement SectionHeader(string title, Geometry glyph)
    {
        var dp = new DockPanel { Margin = new Thickness(4, 12, 0, 4), LastChildFill = true };
        var icon = new IconGlyph { Glyph = glyph, Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0), Filled = glyph == Icons.Star || glyph == Icons.Bookmark };
        icon.SetResourceReference(IconGlyph.ForegroundProperty, "Brush.TextSecondary");
        var text = new TextBlock { Text = title, Style = (Style)FindResource("Text.Section"), VerticalAlignment = VerticalAlignment.Center };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(icon);
        left.Children.Add(text);
        dp.Children.Add(left);
        return dp;
    }

    private FrameworkElement Row(Geometry glyph, string name, string? extension, bool active, ProgressMark? progress)
    {
        var grid = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16 + 6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var border = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(4, 2, 6, 2), Child = grid, Background = Brushes.Transparent };
        border.MouseEnter += (_, _) => border.SetResourceReference(Border.BackgroundProperty, "Brush.BgTertiary");
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;

        var icon = new IconGlyph { Glyph = glyph, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left };
        icon.SetResourceReference(IconGlyph.ForegroundProperty, active ? "Brush.Accent" : "Brush.TextPrimary");
        grid.Children.Add(icon);

        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 14 };
        text.Inlines.Add(new System.Windows.Documents.Run(name));
        if (!string.IsNullOrEmpty(extension))
        {
            var ext = new System.Windows.Documents.Run("  ." + extension) { FontSize = 12 };
            ext.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.TextTertiary");
            text.Inlines.Add(ext);
        }
        text.SetResourceReference(TextBlock.ForegroundProperty, active ? "Brush.Accent" : "Brush.TextPrimary");
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (progress is { } p)
        {
            var ring = ProgressRing(p);
            Grid.SetColumn(ring, 2);
            grid.Children.Add(ring);
        }
        AutomationProperties.SetName(border, name);
        border.Focusable = true;
        border.FocusVisualStyle = (Style)FindResource("Panely.FocusVisual");
        border.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                border.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = MouseLeftButtonUpEvent });
                e.Handled = true;
            }
        };
        return border;
    }

    /// <summary>읽기 링 11×11, 선 1.5. 완독은 체크. total 미상은 일반 읽는 중 표식(점).</summary>
    internal static FrameworkElement ProgressRing(ProgressMark p)
    {
        var size = 11.0;
        var host = new Grid { Width = size, Height = size, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = p.Finished ? Loc.T("Finished") : p.Known ? Loc.T("%lld%% read", (long)Math.Round(p.Fraction * 100)) : Loc.T("In Progress");
        host.ToolTip = label;
        AutomationProperties.SetName(host, label);
        if (p.Finished)
        {
            var check = new IconGlyph { Glyph = Icons.Check, Width = size, Height = size, StrokeThickness = 1.6 };
            check.SetResourceReference(IconGlyph.ForegroundProperty, "Brush.Accent");
            host.Children.Add(check);
            return host;
        }
        var bg = new Ellipse { StrokeThickness = 1.5 };
        bg.SetResourceReference(Shape.StrokeProperty, "Brush.BgTertiary");
        host.Children.Add(bg);
        if (!p.Known)
        {
            var dot = new Ellipse { Width = 4, Height = 4 };
            dot.SetResourceReference(Shape.FillProperty, "Brush.Accent");
            host.Children.Add(dot);
            return host;
        }
        var fraction = Math.Clamp(p.Fraction, 0, 0.9999);
        var r = (size - 1.5) / 2;
        var c = size / 2;
        var angle = fraction * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var arc = new System.Windows.Shapes.Path { Data = new PathGeometry([fig]), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        arc.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        host.Children.Add(arc);
        return host;
    }

    private static ContextMenu Menu(params Control[] items)
    {
        var m = new ContextMenu();
        foreach (var i in items) m.Items.Add(i);
        return m;
    }

    private static MenuItem MenuItem(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => click();
        return mi;
    }

    // ───────────────────────── 파일 트리 ─────────────────────────

    public void AttachLibrary(ILibraryService library)
    {
        _library = library;
    }

    public string? RootPath => _rootPath;

    public async void SetRoot(string? path)
    {
        if (string.Equals(path, _rootPath, StringComparison.OrdinalIgnoreCase)) return;
        _rootPath = path;
        _nodes.Clear();
        _tree.Items.Clear();
        OverflowNotice.Visibility = Visibility.Collapsed;
        UpdateHeader();
        Rebuild();
        if (path is null || _library is null) return;
        await LoadChildren(null, path);
        if (_activePath is not null) await Reveal(_activePath);
    }

    /// <summary>활성 책 표시와 조상 자동 펼침.</summary>
    public async void SetActivePath(string? path)
    {
        var previous = _activePath;
        _activePath = path;
        if (previous is not null && _nodes.TryGetValue(previous, out var old)) RefreshHeader(old);
        if (path is null) return;
        await Reveal(path);
    }

    private async Task Reveal(string path)
    {
        if (_rootPath is null || !IsUnder(path, _rootPath)) return;
        var rel = Path.GetRelativePath(_rootPath, path);
        var parts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = _rootPath;
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (!_nodes.TryGetValue(current, out var item)) return;
            if (i < parts.Length - 1)
            {
                if (!item.IsExpanded) item.IsExpanded = true;
                await EnsureLoaded(item);
            }
            else
            {
                RefreshHeader(item);
                item.BringIntoView();
            }
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureLoaded(TreeViewItem item)
    {
        if (item.Tag is not NodeTag tag || tag.Loaded) return;
        await LoadChildren(item, tag.Node.Path);
    }

    private sealed class NodeTag(LibraryNode node)
    {
        public LibraryNode Node { get; set; } = node;
        public bool Loaded { get; set; }
    }

    private async Task LoadChildren(TreeViewItem? parent, string folder)
    {
        if (_library is null) return;
        IReadOnlyList<LibraryNode> children;
        try
        {
            children = await _library.GetChildrenAsync(folder, CancellationToken.None);
        }
        catch (Exception)
        {
            children = [];
        }
        ApplyChildren(parent, children);
        if (parent?.Tag is NodeTag t) t.Loaded = true;
        if (parent is null) _treeEmpty.Visibility = _tree.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>자식 diff: 사라진 것 제거, 새 것은 정렬 위치에 삽입, 기존 항목은 유지(펼침·선택 보존).</summary>
    private void ApplyChildren(TreeViewItem? parent, IReadOnlyList<LibraryNode> children)
    {
        var items = parent?.Items ?? _tree.Items;
        // placeholder 제거
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i] is not TreeViewItem { Tag: NodeTag }) items.RemoveAt(i);

        var wanted = new HashSet<string>(children.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is TreeViewItem { Tag: NodeTag tag } && !wanted.Contains(tag.Node.Path))
            {
                RemoveNodeRecursive((TreeViewItem)items[i]);
                items.RemoveAt(i);
            }
        }
        for (var i = 0; i < children.Count; i++)
        {
            var node = children[i];
            if (_nodes.TryGetValue(node.Path, out var existing) && ReferenceEquals(existing.Parent, parent ?? (object)_tree))
            {
                var idx = items.IndexOf(existing);
                if (idx != i && idx >= 0) { items.RemoveAt(idx); items.Insert(Math.Min(i, items.Count), existing); }
                ((NodeTag)existing.Tag).Node = node;
                RefreshHeader(existing);
                continue;
            }
            var item = CreateNode(node);
            items.Insert(Math.Min(i, items.Count), item);
        }
    }

    private void RemoveNodeRecursive(TreeViewItem item)
    {
        if (item.Tag is NodeTag tag) _nodes.Remove(tag.Node.Path);
        foreach (var child in item.Items.OfType<TreeViewItem>()) RemoveNodeRecursive(child);
    }

    private TreeViewItem CreateNode(LibraryNode node)
    {
        var item = new TreeViewItem { Tag = new NodeTag(node), Padding = new Thickness(0, 2, 4, 2) };
        _nodes[node.Path] = item;
        RefreshHeader(item);
        if (node.MayHaveChildren && node.Kind == LibraryNodeKind.Folder)
            item.Items.Add(new TreeViewItem { Header = Loc.T("Loading…"), IsEnabled = false });
        item.Expanded += async (s, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, item)) return;
            await EnsureLoaded(item);
        };
        item.MouseLeftButtonUp += (s, e) =>
        {
            if (!ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), null) && e.OriginalSource is DependencyObject d
                && FindAncestorItem(d) == item && e.ChangedButton == MouseButton.Left)
            {
                if (e.OriginalSource is FrameworkElement { TemplatedParent: System.Windows.Controls.Primitives.ToggleButton }) return;
                OpenNode(node);
                e.Handled = true;
            }
        };
        item.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && ReferenceEquals(e.OriginalSource, item)) { OpenNode(((NodeTag)item.Tag).Node); e.Handled = true; }
        };
        item.ContextMenuOpening += (s, e) =>
        {
            if (!ReferenceEquals(FindAncestorItem(e.OriginalSource as DependencyObject), item)) return;
            var n = ((NodeTag)item.Tag).Node;
            var menu = new ContextMenu();
            menu.Items.Add(MenuItem(Loc.T("Open This Book"), () => OpenNode(n)));
            menu.Items.Add(MenuItem(Loc.T("Show in Explorer"), () => ShowInExplorer(n.Path)));
            var extra = ExtraContextItems?.Invoke(n.Path, n.Kind == LibraryNodeKind.Folder)?.ToList();
            if (extra is { Count: > 0 })
            {
                menu.Items.Add(new Separator());
                foreach (var x in extra)
                {
                    if (x.Parent is ItemsControl ic) ic.Items.Remove(x);
                    menu.Items.Add(x);
                }
            }
            item.ContextMenu = menu;
        };
        item.ContextMenu = new ContextMenu();
        return item;
    }

    private static TreeViewItem? FindAncestorItem(DependencyObject? d)
    {
        for (var cur = d; cur is not null; cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is TreeViewItem t) return t;
        return null;
    }

    private void OpenNode(LibraryNode node)
    {
        Activate(new OpenRequest(node.Path, null, null, OpenIntent.TreeSelection));
    }

    private static void ShowInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    private void RefreshHeader(TreeViewItem item)
    {
        if (item.Tag is not NodeTag tag) return;
        var node = tag.Node;
        var active = _activePath is not null && string.Equals(_activePath, node.Path, StringComparison.OrdinalIgnoreCase);
        var name = node.Kind == LibraryNodeKind.Archive ? Path.GetFileNameWithoutExtension(node.Name) : node.Name;
        var progress = node.Kind == LibraryNodeKind.Archive || !node.MayHaveChildren ? ProgressFor?.Invoke(node.Path) : null;
        var row = Row(node.Kind == LibraryNodeKind.Folder ? Icons.Folder : Icons.Archive, name,
            node.Kind == LibraryNodeKind.Archive ? node.Extension : null, active, progress);
        ((Border)row).Padding = new Thickness(0);
        ((Border)row).Focusable = false;
        row.ToolTip = node.Path;
        item.Header = row;
        AutomationProperties.SetName(item, name);
    }

    /// <summary>자동/수동 새로고침: 바뀐 디렉터리만 다시 받아 diff.</summary>
    public async Task ApplyDirectoryChanges(IReadOnlyList<string> directories)
    {
        if (_rootPath is null) return;
        foreach (var dir in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(_rootPath), StringComparison.OrdinalIgnoreCase))
                await LoadChildren(null, _rootPath);
            else if (_nodes.TryGetValue(dir, out var item) && item.Tag is NodeTag { Loaded: true })
                await LoadChildren(item, dir);
            else if (_nodes.TryGetValue(dir, out var unloaded))
                RefreshHeader(unloaded);
        }
    }

    /// <summary>수동 새로고침: 루트와 펼쳐진(로드된) 모든 폴더.</summary>
    public async Task RefreshAll()
    {
        if (_rootPath is null) return;
        OverflowNotice.Visibility = Visibility.Collapsed;
        var loaded = _nodes.Where(kv => kv.Value.Tag is NodeTag { Loaded: true }).Select(kv => kv.Key).ToList();
        await LoadChildren(null, _rootPath);
        foreach (var dir in loaded)
            if (_nodes.TryGetValue(dir, out var item)) await LoadChildren(item, dir);
        foreach (var item in _nodes.Values.ToList()) RefreshHeader(item);
    }

    public void ShowOverflowNotice() => OverflowNotice.Visibility = Visibility.Visible;

    /// <summary>진행 링 등 행 표시만 갱신.</summary>
    public void RefreshRows()
    {
        foreach (var item in _nodes.Values.ToList()) RefreshHeader(item);
    }

    private bool _rowRefreshPending;

    /// <summary>잦은 쪽 이동에서 행 갱신을 묶는다(약 1초).</summary>
    public void RefreshRowsSoon(System.Windows.Threading.Dispatcher dispatcher)
    {
        if (_rowRefreshPending) return;
        _rowRefreshPending = true;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _rowRefreshPending = false;
            if (_activePath is not null && _nodes.TryGetValue(_activePath, out var item)) RefreshHeader(item);
        };
        timer.Start();
    }
}
