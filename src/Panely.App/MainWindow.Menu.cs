using System.Windows;
using System.Windows.Controls;
using Panely.Core.Abstractions;
using Panely.Core.Commands;
using Panely.Core.Localization;
using Panely.Domain.Model;

namespace Panely.App;

/// <summary>
/// 메뉴(08): File / Edit / View / Go / Window / Help. 단축키 문구는 CommandRegistry에서 만든다.
/// 하위 메뉴는 열릴 때마다 현재 상태로 다시 채운다(활성/선택 상태 정확성).
/// </summary>
public partial class MainWindow
{
    private void BuildMenu()
    {
        MainMenu.Items.Clear();
        MainMenu.Items.Add(TopMenu("File", FillFileMenu));
        MainMenu.Items.Add(TopMenu("Edit", FillEditMenu));
        MainMenu.Items.Add(TopMenu("View", FillViewMenu));
        MainMenu.Items.Add(TopMenu("Go", FillGoMenu));
        MainMenu.Items.Add(TopMenu("Window", FillWindowMenu));
        MainMenu.Items.Add(TopMenu("Help", FillHelpMenu));
    }

    private MenuItem TopMenu(string key, Action<MenuItem> fill)
    {
        var top = new MenuItem { Header = "_" + Loc.T(key) };
        top.Items.Add(new MenuItem()); // 하위 메뉴 표시용 자리
        top.SubmenuOpened += (s, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, top)) return;
            top.Items.Clear();
            fill(top);
        };
        return top;
    }

    internal MenuItem MenuCommand(string labelKey, string commandId, bool enabled = true, bool isChecked = false)
    {
        var item = new MenuItem
        {
            Header = Loc.T(labelKey),
            IsEnabled = enabled,
            IsChecked = isChecked,
            InputGestureText = CommandRegistry.All.Any(c => c.Id == commandId) ? CommandRegistry.GestureText(commandId) ?? string.Empty : string.Empty,
        };
        item.Click += (_, _) => Execute(commandId);
        return item;
    }

    private static MenuItem Action(string header, Action click, bool enabled = true, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = isChecked };
        item.Click += (_, _) => click();
        return item;
    }

    private void FillFileMenu(MenuItem m)
    {
        var hasBook = _session.Book is not null;
        m.Items.Add(MenuCommand("Open…", CommandIds.FileOpen));
        var folder = MenuCommand("Open Folder…", OpenFolderId);
        m.Items.Add(folder);
        m.Items.Add(MenuCommand("Reload Book", CommandIds.FileReload, hasBook && !_session.IsLoading));

        var recent = new MenuItem { Header = Loc.T("Open Recent") };
        var recents = _state.Recents.Take(10).ToList();
        if (recents.Count == 0) recent.Items.Add(new MenuItem { Header = Loc.T("No Recent Items"), IsEnabled = false });
        foreach (var r in recents)
        {
            var src = _state.FindSource(r.SourceId);
            if (src is null) continue;
            var availability = AvailabilityOf(src.Id);
            var header = availability is SourceAvailability.TemporarilyUnavailable or SourceAvailability.InvalidReference
                ? $"{r.Title}  ({Loc.T("Unavailable")})" : r.Title;
            var item = Action(header, () => _ = OpenRequestAsync(new OpenRequest(src.Path, null, null, OpenIntent.Recent)));
            item.ToolTip = src.Path;
            recent.Items.Add(item);
        }
        recent.Items.Add(new Separator());
        recent.Items.Add(Action(Loc.T("Clear Menu"), () => _state.ClearRecents(), recents.Count > 0));
        m.Items.Add(recent);
        m.Items.Add(new Separator());
        var canExport = _feature.IsEnabled && _feature.CanExport;
        var export = MenuCommand("Export Translated Book…", CommandIds.FileExport, canExport);
        if (_feature.IsEnabled) m.Items.Add(export);
        m.Items.Add(MenuCommand("Settings…", CommandIds.AppSettings));
        m.Items.Add(new Separator());
        m.Items.Add(MenuCommand("Export Diagnostic Report…", CommandIds.DiagnosticsExport));
        m.Items.Add(MenuCommand("Clear Diagnostic Logs", CommandIds.DiagnosticsClear));
        m.Items.Add(MenuCommand("Open Diagnostic Logs Folder", CommandIds.DiagnosticsFolder));
        var clear = MenuCommand("Clear Extraction Cache", CommandIds.CacheClear, !_session.IsLoading);
        if (_session.IsLoading) clear.ToolTip = Loc.T("Available after loading finishes.");
        m.Items.Add(clear);
        m.Items.Add(new Separator());
        var exit = MenuCommand("Exit", CommandIds.AppExit);
        m.Items.Add(exit);
    }

    private static void FillEditMenu(MenuItem m)
    {
        // 현재 입력 컨트롤 대상(WPF ApplicationCommands는 포커스된 TextBox로 라우팅된다)
        foreach (var (key, cmd) in new (string, System.Windows.Input.RoutedUICommand)[]
                 {
                     ("Undo", System.Windows.Input.ApplicationCommands.Undo),
                     ("Redo", System.Windows.Input.ApplicationCommands.Redo),
                 })
            m.Items.Add(new MenuItem { Header = Loc.T(key), Command = cmd, InputGestureText = key == "Undo" ? "Ctrl+Z" : "Ctrl+Y" });
        m.Items.Add(new Separator());
        foreach (var (key, cmd, g) in new (string, System.Windows.Input.RoutedUICommand, string)[]
                 {
                     ("Cut", System.Windows.Input.ApplicationCommands.Cut, "Ctrl+X"),
                     ("Copy", System.Windows.Input.ApplicationCommands.Copy, "Ctrl+C"),
                     ("Paste", System.Windows.Input.ApplicationCommands.Paste, "Ctrl+V"),
                     ("Select All", System.Windows.Input.ApplicationCommands.SelectAll, "Ctrl+A"),
                 })
            m.Items.Add(new MenuItem { Header = Loc.T(key), Command = cmd, InputGestureText = g });
    }

    private void FillViewMenu(MenuItem m)
    {
        var s = _state.Settings;
        var hasBook = _session.Book is not null;
        m.Items.Add(MenuCommand(s.SidebarPinned ? "Unpin Library" : "Pin Library", CommandIds.LibraryPin));
        m.Items.Add(MenuCommand(s.ToolbarPinned ? "Unpin Toolbar" : "Pin Toolbar", CommandIds.ToolbarPin));
        m.Items.Add(MenuCommand(s.ThumbnailSidebarVisible ? "Hide Thumbnails" : "Show Thumbnails", CommandIds.ThumbnailsToggle, hasBook));
        m.Items.Add(new Separator());
        m.Items.Add(MenuCommand("Single Page", CommandIds.LayoutSingle, true, _session.Layout == ReadingLayout.Single));
        m.Items.Add(MenuCommand("Double Page", CommandIds.LayoutDouble, true, _session.Layout == ReadingLayout.Double));
        m.Items.Add(MenuCommand("Vertical Scroll", CommandIds.LayoutVertical, true, _session.Layout == ReadingLayout.Vertical));
        var dir = MenuCommand(_session.Direction == ReadingDirection.RightToLeft ? "Read Right to Left" : "Read Left to Right",
            CommandIds.ReadingDirection, _session.Layout != ReadingLayout.Vertical, _session.Direction == ReadingDirection.RightToLeft);
        if (_session.Layout == ReadingLayout.Vertical) dir.ToolTip = Loc.T("Reading direction is fixed in vertical mode");
        m.Items.Add(dir);
        if (_session.Layout == ReadingLayout.Double)
            m.Items.Add(MenuCommand("Offset spread (standalone cover)", CommandIds.SpreadCover, true, _session.CoverAlone));
        m.Items.Add(new Separator());
        // Windows 결정: 같은 맞춤 재선택도 baseline 복귀(비활성화하지 않음)
        m.Items.Add(MenuCommand("Fit to Screen", CommandIds.FitScreen, hasBook, hasBook && Viewer.Zoom.IsAtFit(FitMode.FitScreen)));
        m.Items.Add(MenuCommand("Fit to Width", CommandIds.FitWidth, hasBook, hasBook && Viewer.Zoom.IsAtFit(FitMode.FitWidth)));
        m.Items.Add(MenuCommand("Fit to Height", CommandIds.FitHeight, hasBook, hasBook && Viewer.Zoom.IsAtFit(FitMode.FitHeight)));
        m.Items.Add(MenuCommand("Zoom In", CommandIds.ZoomIn, hasBook));
        m.Items.Add(MenuCommand("Zoom Out", CommandIds.ZoomOut, hasBook));
        m.Items.Add(MenuCommand("Reset Zoom", CommandIds.ZoomReset, hasBook));
        m.Items.Add(MenuCommand(s.AutoFitOnResize ? "Lock View Size" : "Unlock View Size", CommandIds.ViewLock, true, !s.AutoFitOnResize));
        m.Items.Add(MenuCommand(s.WheelPageTurn ? "Disable Scroll Page Turning" : "Enable Scroll Page Turning", CommandIds.WheelToggle));
        if (_feature.IsEnabled)
        {
            m.Items.Add(new Separator());
            m.Items.Add(MenuCommand(_feature.IsOverlayActive ? "Hide Translation" : "Show Translation", CommandIds.TranslationToggle, hasBook && _feature.CanToggleOriginal));
            m.Items.Add(MenuCommand(_feature.IsRunningCurrentBook ? "Pause Translating" : "Translate This Book", CommandIds.TranslationRun, hasBook && _feature.CanRunOrPause));
            m.Items.Add(MenuCommand(_feature.IsEditing ? "Done Editing Balloons" : "Edit Balloons", CommandIds.TranslationEdit, _feature.CanEdit || _feature.IsEditing));
            m.Items.Add(Action(Loc.T("Show Hidden Balloons"), _feature.ShowHiddenBalloons, _feature.HasHiddenBalloons));
        }
        m.Items.Add(new Separator());
        m.Items.Add(MenuCommand(_fullScreen ? "Exit Full Screen" : "Enter Full Screen", CommandIds.WindowFullscreen));
    }

    private void FillGoMenu(MenuItem m)
    {
        var hasBook = _session.Book is not null;
        m.Items.Add(MenuCommand("Go to Page…", CommandIds.PageJump, hasBook && _session.PageCount >= 2));
        m.Items.Add(new Separator());
        m.Items.Add(MenuCommand(_session.IsCurrentPageBookmarked ? "Remove Page Bookmark" : "Add Page Bookmark", CommandIds.BookmarkToggle, hasBook));
        m.Items.Add(MenuCommand("Previous Bookmark", CommandIds.BookmarkPrevious, _session.PreviousBookmarkTarget is not null));
        m.Items.Add(MenuCommand("Next Bookmark", CommandIds.BookmarkNext, _session.NextBookmarkTarget is not null));
        var list = new MenuItem { Header = Loc.T("Bookmarks") };
        foreach (var c in BuildBookmarkMenuItems()) list.Items.Add(c);
        if (list.Items.Count == 0) list.Items.Add(new MenuItem { Header = Loc.T("No Bookmarks"), IsEnabled = false });
        m.Items.Add(list);
        m.Items.Add(MenuCommand("Remove All Bookmarks in This Book…", RemoveAllBookBookmarksId, hasBook && _session.BookmarkedPages.Count > 0));
        m.Items.Add(MenuCommand("Remove All Bookmarks…", RemoveAllBookmarksId, _state.AllBookmarks.Count > 0));
        m.Items.Add(new Separator());
        m.Items.Add(MenuCommand(_session.IsFavorite ? "Remove from Favorites" : "Add to Favorites", CommandIds.FavoriteToggle, hasBook));
        m.Items.Add(new Separator());
        m.Items.Add(MenuCommand("Previous Volume", CommandIds.VolumePrevious, hasBook));
        m.Items.Add(MenuCommand("Next Volume", CommandIds.VolumeNext, hasBook));
    }

    /// <summary>현재 책 쪽 북마크 + 다른 책 하위 메뉴.</summary>
    private IReadOnlyList<Control> BuildBookmarkMenuItems()
    {
        var items = new List<Control>();
        var book = _session.Book;
        if (book is not null)
        {
            var visible = Viewer.CommittedRange;
            foreach (var p in _session.BookmarkedPages)
            {
                var page = p;
                items.Add(Action(Loc.T("Page %lld", page + 1), () => { _session.Jump(page); FocusReader(); }, true, visible.Contains(page)));
            }
        }
        var others = _state.AllBookmarks.Where(b => book is null || b.BookRef.Key != book.BookKey).ToList();
        if (others.Count > 0)
        {
            if (items.Count > 0) items.Add(new Separator());
            var sub = new MenuItem { Header = Loc.T("Other Books") };
            foreach (var bb in others)
            {
                var src = _state.FindSource(bb.BookRef.SourceId);
                var bookItem = new MenuItem { Header = bb.Title };
                foreach (var b in bb.Bookmarks.OrderBy(x => x.PageIndex))
                {
                    var req = src is null ? null : new OpenRequest(src.Path, bb.BookRef.InnerPath, b.PageIndex, OpenIntent.Bookmark);
                    bookItem.Items.Add(Action(Loc.T("Page %lld", b.PageIndex + 1), () => { if (req is not null) _ = OpenRequestAsync(req); }, req is not null));
                }
                sub.Items.Add(bookItem);
            }
            items.Add(sub);
        }
        return items;
    }

    private void FillWindowMenu(MenuItem m)
    {
        m.Items.Add(Action(Loc.T("Reader"), () => { Activate(); FocusReader(); }));
        var jobs = MenuCommand("Jobs", CommandIds.JobsShow, _feature.IsEnabled);
        m.Items.Add(jobs);
        m.Items.Add(new Separator());
        m.Items.Add(Action(Loc.T("Minimize"), () => WindowState = WindowState.Minimized));
    }

    private void FillHelpMenu(MenuItem m)
    {
        m.Items.Add(MenuCommand("About Panely", AboutId));
    }
}
