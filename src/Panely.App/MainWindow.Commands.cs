using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Panely.App.Controls;
using Panely.App.Shell;
using Panely.Core.Abstractions;
using Panely.Core.Commands;
using Panely.Core.Localization;
using Panely.Core.Reader;
using Panely.Domain.Model;

namespace Panely.App;

public partial class MainWindow
{
    public const string OpenFolderId = "file.openFolder";
    public const string PreviousPageButtonId = "page.previousButton";
    public const string NextPageButtonId = "page.nextButton";
    public const string RemoveAllBookBookmarksId = "bookmarks.removeAllBook";
    public const string RemoveAllBookmarksId = "bookmarks.removeAll";
    public const string AboutId = "help.about";

    private SettingsWindow? _settingsWindow;

    /// <summary>명령 실행(메뉴·툴바·단축키 공통).</summary>
    public async void Execute(string id)
    {
        var s = _state.Settings;
        switch (id)
        {
            case CommandIds.FileOpen: ShowOpenFileDialog(); break;
            case OpenFolderId: ShowOpenFolderDialog(); break;
            case CommandIds.FileReload: if (_session.Book is not null && !_session.IsLoading) await _session.ReloadAsync(); break;
            case CommandIds.AppSettings: ShowSettings(); break;
            case CommandIds.FileExport: if (_feature.IsEnabled && _feature.CanExport) _feature.ShowExport(); break;
            case CommandIds.DiagnosticsExport: await ExportDiagnosticsAsync(); break;
            case CommandIds.DiagnosticsClear: ClearDiagnosticLogs(); break;
            case CommandIds.DiagnosticsFolder: OpenFolder(_services.Diagnostics.LogFolder); break;
            case CommandIds.CacheClear: await ClearExtractionCacheAsync(); break;
            case CommandIds.LibraryPin:
                _state.UpdateSettings(x => x.SidebarPinned = !x.SidebarPinned);
                if (!_state.Settings.SidebarPinned) CloseLibraryOverlay();
                break;
            case CommandIds.ToolbarPin: _state.UpdateSettings(x => x.ToolbarPinned = !x.ToolbarPinned); break;
            case CommandIds.ThumbnailsToggle:
                if (_session.Book is null) break;
                _state.UpdateSettings(x => x.ThumbnailSidebarVisible = !x.ThumbnailSidebarVisible);
                break;
            case CommandIds.LayoutSingle: _session.SetLayout(ReadingLayout.Single); break;
            case CommandIds.LayoutDouble: _session.SetLayout(ReadingLayout.Double); break;
            case CommandIds.LayoutVertical: _session.SetLayout(ReadingLayout.Vertical); break;
            case CommandIds.ReadingDirection: _session.ToggleDirection(); break;
            case CommandIds.SpreadCover: if (_session.Layout == ReadingLayout.Double) _session.SetCoverAlone(!_session.CoverAlone); break;
            case CommandIds.FitScreen: if (_session.Book is not null) _session.SetFitMode(FitMode.FitScreen); break;
            case CommandIds.FitWidth: if (_session.Book is not null) _session.SetFitMode(FitMode.FitWidth); break;
            case CommandIds.FitHeight: if (_session.Book is not null) _session.SetFitMode(FitMode.FitHeight); break;
            case CommandIds.ZoomIn: Viewer.ZoomIn(); break;
            case CommandIds.ZoomOut: Viewer.ZoomOut(); break;
            case CommandIds.ZoomReset: Viewer.ResetZoom(); break;
            case CommandIds.ViewLock: _state.UpdateSettings(x => x.AutoFitOnResize = !x.AutoFitOnResize); break;
            case CommandIds.WheelToggle:
                _state.UpdateSettings(x => x.WheelPageTurn = !x.WheelPageTurn);
                Viewer.ResetWheelState();
                break;
            case CommandIds.PageForward: await _session.ForwardAsync(); break;
            case CommandIds.PageBackward: await _session.BackwardAsync(); break;
            case PreviousPageButtonId: _session.Previous(); break;
            case NextPageButtonId: _session.Next(); break;
            case CommandIds.PageJump: ShowGoToPage(); break;
            case CommandIds.BookmarkToggle: _session.ToggleBookmark(); break;
            case CommandIds.BookmarkPrevious: _session.GoToPreviousBookmark(); break;
            case CommandIds.BookmarkNext: _session.GoToNextBookmark(); break;
            case CommandIds.FavoriteToggle: _session.ToggleFavorite(); break;
            case CommandIds.VolumePrevious: await _session.OpenPreviousVolumeAsync(); break;
            case CommandIds.VolumeNext: await _session.OpenNextVolumeAsync(); break;
            case CommandIds.TranslationToggle: if (_feature.IsEnabled && _feature.CanToggleOriginal) _feature.ToggleOriginal(); break;
            case CommandIds.TranslationRun: if (_feature.IsEnabled && _feature.CanRunOrPause) _feature.RunOrPause(); break;
            case CommandIds.TranslationEdit: if (_feature.IsEnabled && (_feature.CanEdit || _feature.IsEditing)) _feature.ToggleEditMode(); break;
            case CommandIds.JobsShow: if (_feature.IsEnabled) _feature.ShowJobs(); break;
            case FloatingToolbar.TranslationId: if (_feature.IsEnabled) _feature.OnToolbarButtonClicked(); break;
            case CommandIds.WindowFullscreen: ToggleFullScreen(); break;
            case CommandIds.AppExit: Close(); break;
            case RemoveAllBookBookmarksId:
                if (_session.Book is { } b && ConfirmDialog.Show(this, Loc.T("Remove all bookmarks in this book?"),
                        Loc.T("Bookmarks to remove: %lld. This can't be undone.", (long)_session.BookmarkedPages.Count), Loc.T("Remove All"), true))
                    _session.RemoveAllBookmarksInBook();
                break;
            case RemoveAllBookmarksId:
                var total = _state.AllBookmarks.Sum(x => x.Bookmarks.Count);
                if (total > 0 && ConfirmDialog.Show(this, Loc.T("Remove all bookmarks in every book?"),
                        Loc.T("Bookmarks to remove: %lld. This can't be undone.", (long)total), Loc.T("Remove All"), true))
                    _state.RemoveAllBookmarks();
                break;
            case AboutId: new AboutWindow { Owner = this }.ShowDialog(); break;
        }
        UpdateToolbar();
    }

    // ───────────────────────── 대화상자 ─────────────────────────

    private void ShowOpenFileDialog()
    {
        var images = string.Join(';', SupportedFormats.ImageExtensions.Select(e => "*." + e));
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Open Folder, CBZ/CBR, or ZIP/RAR…"),
            Filter = $"{Loc.T("Comic archives")} (*.cbz;*.cbr;*.zip;*.rar)|*.cbz;*.cbr;*.zip;*.rar|{Loc.T("Images")}|{images}|{Loc.T("All files")} (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        _explicitOpenSeen = true;
        var path = dialog.FileName;
        // 이미지 파일을 고르면 그 폴더를 책으로 열고 그 쪽으로 이동한다
        if (SupportedFormats.IsImage(path) && Path.GetDirectoryName(path) is { } folder)
        {
            _ = OpenImageAsync(folder, Path.GetFileName(path));
            return;
        }
        _ = OpenPathAsync(path, OpenIntent.Normal);
    }

    private async Task OpenImageAsync(string folder, string fileName)
    {
        await OpenRequestAsync(new OpenRequest(folder, null, null, OpenIntent.Normal));
        if (_session.Book is { } book)
        {
            var index = book.Pages.ToList().FindIndex(p => string.Equals(p.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _session.Jump(index);
        }
    }

    private void ShowOpenFolderDialog()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Open Folder…") };
        if (dialog.ShowDialog(this) != true) return;
        _explicitOpenSeen = true;
        _ = OpenPathAsync(dialog.FolderName, OpenIntent.Normal);
    }

    private void ShowGoToPage()
    {
        if (_session.Book is null || _session.PageCount < 2) return;
        var number = GoToPageDialog.Ask(this, _session.PageCount, _session.Spread.Start + 1);
        if (number is { } n) _session.JumpPage(n);
        FocusReader();
    }

    public void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this, _services, _feature) { Owner = this };
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; FocusReader(); };
        _settingsWindow.Show();
    }

    internal bool IsLoadingBook => _session.IsLoading;

    internal async Task<long?> ClearExtractionCacheAsync()
    {
        if (_session.IsLoading) return null;
        long removed;
        try
        {
            removed = await _services.ExtractionCache.ClearAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _services.Diagnostics.Warning(LogCategory.Cache, $"Clear failed: {ex.GetType().Name}");
            return null;
        }
        ConfirmDialog.Info(this, removed > 0 ? Loc.T("Extraction Cache Cleared") : Loc.T("No Extraction Cache Cleared"),
            removed > 0 ? FormatBytes(removed) : Loc.T("There is no inactive extraction cache to remove."));
        return removed;
    }

    internal void ClearDiagnosticLogs()
    {
        if (!ConfirmDialog.Show(this, Loc.T("Clear Diagnostic Logs?"),
                Loc.T("This removes Panely's recent file log. It does not clear OSLog, cache data, recent files, bookmarks, favorites, or reading position."),
                Loc.T("Clear Logs"), destructive: true)) return;
        try
        {
            _services.Diagnostics.ClearLogs();
            ConfirmDialog.Info(this, Loc.T("Diagnostic Logs Cleared"), Loc.T("Recent file logs were removed."));
        }
        catch (Exception)
        {
            ConfirmDialog.Info(this, Loc.T("Could Not Clear Diagnostic Logs"), Loc.T("The recent file log could not be removed. Please try again."));
        }
    }

    internal async Task ExportDiagnosticsAsync()
    {
        var d = _services.Diagnostics;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = d.DefaultReportFileName(DateTimeOffset.Now),
            Filter = "ZIP (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await d.ExportReportAsync(dialog.FileName, BuildSnapshot(), CancellationToken.None);
            ConfirmDialog.Info(this, Loc.T("Diagnostic Report Exported"), Loc.T("Diagnostic report exported."));
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, Loc.T("Diagnostic Report Export Failed"), Loc.T("Couldn't save the file: %@", ex.Message));
        }
    }

    private DiagnosticSnapshot BuildSnapshot()
    {
        var s = _state.Settings;
        var settings = new Dictionary<string, string>
        {
            ["layout"] = s.Layout.ToString(),
            ["direction"] = s.Direction.ToString(),
            ["fitMode"] = s.FitMode.ToString(),
            ["appLanguage"] = s.AppLanguage.ToString(),
            ["sidebarPinned"] = s.SidebarPinned.ToString(),
            ["toolbarPinned"] = s.ToolbarPinned.ToString(),
            ["thumbnailSidebarVisible"] = s.ThumbnailSidebarVisible.ToString(),
            ["autoFitOnResize"] = s.AutoFitOnResize.ToString(),
            ["doublePageCoverAlone"] = s.DoublePageCoverAlone.ToString(),
            ["wheelPageTurn"] = s.WheelPageTurn.ToString(),
            ["reopenLastFolderOnLaunch"] = s.ReopenLastFolderOnLaunch.ToString(),
            ["translationEnabled"] = s.TranslationEnabled.ToString(),
            ["translationMode"] = s.TranslationMode.ToString(),
            ["logLevel"] = s.LogLevel.ToString(),
            ["libraryAutoRefreshEnabled"] = s.LibraryAutoRefreshEnabled.ToString(),
        };
        var book = _session.Book;
        var reader = new Dictionary<string, string>
        {
            ["hasBook"] = (book is not null).ToString(),
            ["sourceKind"] = book?.Source.Kind.ToString() ?? "-",
            ["extension"] = book is null ? "-" : SupportedFormats.ExtensionOf(book.Source.Path),
            ["nested"] = (book?.IsNestedCacheBook ?? false).ToString(),
            ["pageCount"] = _session.PageCount.ToString(),
            ["pageIndex"] = _session.PageIndex.ToString(),
            ["volumes"] = (book?.Volumes.Count ?? 0).ToString(),
            ["layout"] = _session.Layout.ToString(),
            ["zoom"] = Viewer.Zoom.Zoom.ToString("0.###"),
            ["baseline"] = Viewer.Zoom.Baseline.ToString("0.###"),
            ["loading"] = _session.IsLoading.ToString(),
            ["lastError"] = _session.LastError?.Kind.ToString() ?? "-",
            ["imageBudgetMiB"] = (_cache.BudgetBytes / 1024 / 1024).ToString(),
            ["imageCostMiB"] = (_cache.TotalCost / 1024 / 1024).ToString(),
            ["decodeConcurrency"] = _cache.Concurrency.ToString(),
        };
        return new DiagnosticSnapshot(AppInfo.Version, AppInfo.Build, AppInfo.OsVersion, _services.ExtractionCache.GetUsage(), settings, reader);
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }

    internal static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            _restoreResize = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            MainMenu.Visibility = Visibility.Collapsed;
            _fullScreen = true;
        }
        else
        {
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResize;
            WindowState = _restoreState;
            MainMenu.Visibility = Visibility.Visible;
            _fullScreen = false;
        }
        BuildMenu();
    }

    // ───────────────────────── 페이지 우클릭 ─────────────────────────

    private void ShowPageContextMenu(Point viewPoint)
    {
        if (_session.Book is null) return;
        var menu = new ContextMenu { PlacementTarget = Viewer, Placement = PlacementMode.MousePoint };
        if (_feature.IsEnabled)
        {
            foreach (var item in _feature.BuildPageContextMenuItems(viewPoint))
            {
                if (item.Parent is ItemsControl old) old.Items.Remove(item);
                menu.Items.Add(item);
            }
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        }
        menu.Items.Add(MenuCommand(_session.IsCurrentPageBookmarked ? "Remove Page Bookmark" : "Add Page Bookmark", CommandIds.BookmarkToggle));
        menu.Items.Add(MenuCommand(_session.IsFavorite ? "Remove from Favorites" : "Add to Favorites", CommandIds.FavoriteToggle));
        menu.Items.Add(MenuCommand("Go to Page…", CommandIds.PageJump, _session.PageCount >= 2));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuCommand("Fit to Screen", CommandIds.FitScreen));
        menu.Items.Add(MenuCommand("Fit to Width", CommandIds.FitWidth));
        menu.Items.Add(MenuCommand("Fit to Height", CommandIds.FitHeight));
        menu.Closed += (_, _) => FocusReader();
        menu.IsOpen = true;
    }

    // ───────────────────────── 키보드 ─────────────────────────

    private static readonly Lazy<IReadOnlyList<(CommandSpec Spec, ModifierKeys Mods, Key Key)>> Gestures = new(() =>
    {
        var list = new List<(CommandSpec, ModifierKeys, Key)>();
        foreach (var spec in CommandRegistry.All)
            foreach (var g in spec.Gestures)
                if (TryParseGesture(g, out var mods, out var key)) list.Add((spec, mods, key));
        return list;
    });

    internal static bool TryParseGesture(string gesture, out ModifierKeys mods, out Key key)
    {
        mods = ModifierKeys.None;
        key = Key.None;
        var parts = gesture.Split('+');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i])
            {
                case "Ctrl": mods |= ModifierKeys.Control; break;
                case "Shift": mods |= ModifierKeys.Shift; break;
                case "Alt": mods |= ModifierKeys.Alt; break;
                default: return false;
            }
        }
        return Enum.TryParse(parts[^1], out key);
    }

    private static bool IsTypingTarget(object? focused)
        => focused is TextBoxBase or PasswordBox || focused is ComboBox { IsEditable: true };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var typing = IsTypingTarget(Keyboard.FocusedElement);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        // IME 조합 중에는 앱 명령으로 처리하지 않는다
        if (e.Key == Key.ImeProcessed) return;

        // 번역 편집 draft/편집 바(포커스 우선순위 2·3)
        if (!typing && _feature.IsEnabled && _feature.OnKeyDown(e))
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape && !typing)
        {
            if (_libraryOverlayOpen) { CloseLibraryOverlay(); FocusReader(); e.Handled = true; return; }
            if (_fullScreen) { ToggleFullScreen(); e.Handled = true; return; }
            return;
        }

        foreach (var (spec, gm, gk) in Gestures.Value)
        {
            if (gk != key || gm != mods) continue;
            if (spec.Id == CommandIds.AppExit) return; // Alt+F4는 시스템 창 닫기
            if (typing && (spec.ReaderOnly || gm == ModifierKeys.None || gm == ModifierKeys.Shift)) return;
            if (spec.ReaderOnly && !IsReaderFocusContext()) return;
            if (spec.Id is CommandIds.VolumePrevious or CommandIds.VolumeNext && e.IsRepeat) { e.Handled = true; return; }
            e.Handled = true;
            Execute(spec.Id);
            return;
        }

        if (typing || !IsReaderFocusContext() || mods != ModifierKeys.None) return;
        HandleReaderKey(key, e);
    }

    /// <summary>리더 키(화살표·Space·bare bracket)는 리더 문맥에서만: 텍스트/모달/팝오버 입력이 아닐 때.</summary>
    private bool IsReaderFocusContext()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is null) return true;
        if (focused is TreeViewItem or ListBoxItem or MenuItem or ComboBox) return false;
        if (focused is DependencyObject d && (IsInside(d, Slider) && Slider.IsEditing)) return false;
        return true;
    }

    private static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (var cur = d; cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            if (ReferenceEquals(cur, container)) return true;
        return false;
    }

    private void HandleReaderKey(Key key, KeyEventArgs e)
    {
        if (_session.Book is null) return;
        var rtl = _session.EffectiveDirection == ReadingDirection.RightToLeft;
        var vertical = _session.Layout == ReadingLayout.Vertical;
        switch (key)
        {
            case Key.Right:
                _ = rtl ? _session.BackwardAsync() : _session.ForwardAsync();
                break;
            case Key.Left:
                _ = rtl ? _session.ForwardAsync() : _session.BackwardAsync();
                break;
            case Key.Down:
                if (vertical || Viewer.CanPanDown) Viewer.PanBy(0, Viewer.ActualHeight * 0.1);
                else return;
                break;
            case Key.Up:
                if (vertical || Viewer.CanPanUp) Viewer.PanBy(0, -Viewer.ActualHeight * 0.1);
                else return;
                break;
            case Key.PageDown:
                if (vertical) Viewer.PanBy(0, Viewer.ActualHeight * 0.9);
                else _ = _session.ForwardAsync();
                break;
            case Key.PageUp:
                if (vertical) Viewer.PanBy(0, -Viewer.ActualHeight * 0.9);
                else _ = _session.BackwardAsync();
                break;
            case Key.Home:
                _session.Jump(0);
                break;
            case Key.End:
                _session.Jump(_session.PageCount - 1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnKeyDownFallback(object sender, KeyEventArgs e)
    {
    }
}
