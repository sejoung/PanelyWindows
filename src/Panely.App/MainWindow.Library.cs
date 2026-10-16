using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Panely.App.Controls;
using Panely.App.Shell;
using Panely.Core.Abstractions;
using Panely.Core.Library;
using Panely.Core.Localization;
using Panely.Domain.Model;

namespace Panely.App;

public partial class MainWindow
{
    private readonly Dictionary<Guid, SourceAvailability> _availabilityMap = new();
    private bool _sidebarRefreshPending;

    private void WireLibrary()
    {
        _sidebar.AttachLibrary(_library);
        _sidebar.OpenRequested += r => _ = OpenRequestAsync(r);
        _sidebar.PageRequested += i => _session.Jump(i);
        _sidebar.ItemActivated += () =>
        {
            if (!_state.Settings.SidebarPinned) CloseLibraryOverlay();
            FocusReader();
        };
        _sidebar.RemoveContinueRequested += key => _state.RemoveProgress(key);
        _sidebar.RemoveFavoriteRequested += id => _state.RemoveFavorite(id);
        _sidebar.RemoveBookmarkRequested += (key, id) => _state.RemoveBookmark(key, id);
        _sidebar.RemoveAllBookmarksRequested += (key, title) =>
        {
            if (ConfirmDialog.Show(this, Loc.T("Remove all bookmarks in “%@”?", title),
                    Loc.T("Bookmarks to remove: %lld. This can't be undone.", (long)_state.GetBookmarks(key).Count), Loc.T("Remove All"), destructive: true))
                _state.RemoveAllBookmarks(key);
        };
        _sidebar.PickFolderRequested += () => PickLibraryFolder();
        _sidebar.RefreshRequested += async () =>
        {
            _library.NotifyManualRefresh();
            await _sidebar.RefreshAll();
        };
        _sidebar.PinToggled += () => Execute(Panely.Core.Commands.CommandIds.LibraryPin);
        _sidebar.ExtraContextItems = (path, isFolder) => _feature.IsEnabled ? _feature.BuildLibraryContextMenuItems(path, isFolder) : [];
        _sidebar.ProgressFor = ProgressForPath;
        _sidebar.BookmarkThumbnail = LoadBookmarkThumbnail;

        _library.DirectoriesChanged += (_, dirs) => Dispatcher.BeginInvoke(async () =>
        {
            await _sidebar.ApplyDirectoryChanges(dirs);
        });
        _library.WatchOverflowed += (_, _) => Dispatcher.BeginInvoke(_sidebar.ShowOverflowNotice);
        WireLibraryHover();
        RefreshSidebar();
    }

    // ───────────────────────── 열기 ─────────────────────────

    /// <summary>탐색기·두 번째 실행·IPC에서 온 열기. 마지막 명시 요청이 화면을 소유한다.</summary>
    public void OpenFromExternal(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _explicitOpenSeen = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _ = OpenPathAsync(paths[^1], OpenIntent.Normal);
    }

    /// <summary>파일 열기/드롭/외부. 폴더면 라이브러리 루트로 기억한다(파일 직접 열기와 구분).</summary>
    private async Task OpenPathAsync(string path, OpenIntent intent)
    {
        if (Directory.Exists(path) && intent == OpenIntent.Normal)
        {
            SetLibraryRoot(path, remember: true);
        }
        else if (File.Exists(path) && SupportedFormats.IsImage(path) && Path.GetDirectoryName(path) is { } folder)
        {
            await OpenImageAsync(folder, Path.GetFileName(path));
            return;
        }
        await OpenRequestAsync(new OpenRequest(path, null, null, intent));
    }

    private async Task OpenRequestAsync(OpenRequest request)
    {
        if (request.Intent != OpenIntent.RestoreLibrary) _explicitOpenSeen = true;
        await _session.OpenAsync(request);
        FocusReader();
    }

    /// <summary>현재 루트 밖의 책을 열면 그 부모를 탐색 루트로 보여준다(기억하지 않음).</summary>
    private void EnsureLibraryRootFor(OpenedBook book)
    {
        var root = _sidebar.RootPath;
        var path = book.LibraryPath;
        if (root is not null && IsUnder(path, root)) return;
        var parent = Path.GetDirectoryName(path);
        if (parent is null) return;
        SetLibraryRoot(parent, remember: false);
    }

    private static bool IsUnder(string path, string root)
    {
        try
        {
            var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void SetLibraryRoot(string folder, bool remember)
    {
        _sidebar.SetRoot(folder);
        _library.SetRoot(folder, _state.Settings.LibraryAutoRefreshEnabled);
        if (remember)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            var source = _state.GetOrCreateSource(folder, SourceKind.Folder, string.IsNullOrEmpty(name) ? folder : name);
            _state.SetLastLibraryRoot(source.Id);
        }
    }

    private void PickLibraryFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Select a folder to browse books from.") };
        if (dialog.ShowDialog(this) == true) SetLibraryRoot(dialog.FolderName, remember: true);
    }

    /// <summary>cold start: reopen=true이고 명시 열기/로딩이 없으면 마지막 폴더를 라이브러리로 복원한다.</summary>
    private void RestoreLastLibrary()
    {
        if (_explicitOpenSeen || _session.IsLoading || _session.Book is not null) return;
        if (!_state.Settings.ReopenLastFolderOnLaunch) return;
        if (_state.LastLibraryRoot is not { } id || _state.FindSource(id) is not { } source) return;
        if (!Directory.Exists(source.Path))
        {
            _services.Diagnostics.Info(LogCategory.Library, "Last library root unavailable");
            return;
        }
        _sidebar.SetRoot(source.Path);
        _library.SetRoot(source.Path, _state.Settings.LibraryAutoRefreshEnabled);
    }

    public void ForgetLibraryRoot() => _state.SetLastLibraryRoot(null);

    // ───────────────────────── 사이드바 데이터 ─────────────────────────

    private void RefreshSidebarSoon()
    {
        if (_sidebarRefreshPending) return;
        _sidebarRefreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _sidebarRefreshPending = false;
            RefreshSidebar();
        });
    }

    private bool IsKeyAvailable(string bookKey)
    {
        if (!BookKeys.TryParse(bookKey, out var id, out _)) return false;
        return !_availabilityMap.TryGetValue(id, out var a) || a is SourceAvailability.Available or SourceAvailability.Unknown;
    }

    private void RefreshSidebar()
    {
        var book = _session.Book;
        ContinueEntry? cont = null;
        try
        {
            var key = ContinueReading.SuggestBookKeys(_state, IsKeyAvailable).FirstOrDefault();
            if (key is not null && _state.GetProgress(key) is { } p && ContinueReading.ToRequest(_state, key, OpenIntent.Continue) is { } req)
                cont = new ContinueEntry(key, TitleForKey(key), p.Fraction, p.Finished, req);
        }
        catch (Exception ex)
        {
            _services.Diagnostics.Warning(LogCategory.Library, $"Continue suggestion failed: {ex.GetType().Name}");
        }

        var volumes = new List<VolumeRow>();
        if (book is not null && book.Volumes.Any(v => v.IsNested))
        {
            for (var i = 0; i < book.Volumes.Count; i++)
            {
                var v = book.Volumes[i];
                volumes.Add(new VolumeRow(v.DisplayName, v.ToRequest(OpenIntent.TreeSelection), i == book.VolumeIndex));
            }
        }

        var favorites = _state.Favorites.Select(f =>
        {
            var src = _state.FindSource(f.BookRef.SourceId);
            var req = src is null ? null : new OpenRequest(src.Path, f.BookRef.InnerPath, null, OpenIntent.Favorite);
            return new FavoriteRow(f.Id, f.Title, req, book is not null && book.BookKey == f.BookRef.Key);
        }).ToList();

        var current = new List<BookmarkRow>();
        var others = new List<BookmarkRow>();
        foreach (var bb in _state.AllBookmarks)
        {
            var key = bb.BookRef.Key;
            var src = _state.FindSource(bb.BookRef.SourceId);
            foreach (var b in bb.Bookmarks.OrderBy(b => b.PageIndex))
            {
                var req = src is null ? null : new OpenRequest(src.Path, bb.BookRef.InnerPath, b.PageIndex, OpenIntent.Bookmark);
                var row = new BookmarkRow(b.Id, key, b.PageIndex, bb.Title, req);
                if (book is not null && key == book.BookKey) current.Add(row);
                else others.Add(row);
            }
        }
        if (book is not null && current.Count == 0)
        {
            // 상태에 책 묶음이 없으면 현재 책 제목만 넣는다
        }

        _sidebar.SetData(new SidebarData
        {
            Continue = cont,
            Volumes = volumes,
            Favorites = favorites,
            CurrentBookKey = book?.BookKey,
            CurrentBookmarks = current.Select(r => r with { BookTitle = book!.Title }).ToList(),
            OtherBookmarks = others,
        });
        UpdateToolbar();
    }

    private string TitleForKey(string bookKey)
    {
        if (_session.Book is { } b && b.BookKey == bookKey) return b.Title;
        if (!BookKeys.TryParse(bookKey, out var id, out var inner)) return bookKey;
        var src = _state.FindSource(id);
        if (inner is not null)
        {
            var leaf = inner.Split('/').Last();
            var stem = Path.GetFileNameWithoutExtension(leaf);
            return string.IsNullOrEmpty(stem) ? leaf : stem;
        }
        return src?.Title ?? bookKey;
    }

    private ProgressMark? ProgressForPath(string path)
    {
        var src = _state.FindSourceByPath(path);
        if (src is null) return null;
        var key = BookKeys.Make(src.Id, null);
        if (_state.GetProgress(key) is { } p) return new ProgressMark(p.Fraction, p.Finished, p.Total > 0);
        if (_state.GetPosition(key) is not null) return new ProgressMark(0, false, false);
        return null;
    }

    private async Task<BitmapSource?> LoadBookmarkThumbnail(int index)
    {
        var book = _session.Book;
        if (book is null || index < 0 || index >= book.Pages.Count) return null;
        var page = book.Pages[index];
        var decoder = _services.Decoder;
        return await Task.Run(async () =>
        {
            try
            {
                var bytes = await book.Data.ReadAsync(page, CancellationToken.None).ConfigureAwait(false);
                return decoder.Decode(bytes, page.Extension, 48);
            }
            catch (Exception)
            {
                return null;
            }
        });
    }

    /// <summary>앱 활성화 시 최근/이어보기 가용성을 백그라운드로 갱신(이동식 드라이브 분리 시 자동 삭제하지 않음).</summary>
    private void RefreshAvailability()
    {
        var sources = _state.Recents.Select(r => _state.FindSource(r.SourceId)).Where(s => s is not null).Cast<SourceRef>()
            .Concat(_state.AllProgress.Keys.Select(k => BookKeys.TryParse(k, out var id, out _) ? _state.FindSource(id) : null).Where(s => s is not null).Cast<SourceRef>())
            .DistinctBy(s => s.Id).Take(200).ToList();
        _ = Task.Run(async () =>
        {
            var results = new Dictionary<Guid, SourceAvailability>();
            foreach (var s in sources)
            {
                try { results[s.Id] = await _availability.CheckAsync(s, null, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { results[s.Id] = SourceAvailability.Unknown; }
            }
            await Dispatcher.InvokeAsync(() =>
            {
                var changed = false;
                foreach (var (id, a) in results)
                {
                    if (!_availabilityMap.TryGetValue(id, out var old) || old != a) changed = true;
                    _availabilityMap[id] = a;
                }
                if (changed) RefreshSidebar();
            });
        });
    }

    internal SourceAvailability AvailabilityOf(Guid sourceId)
        => _availabilityMap.TryGetValue(sourceId, out var a) ? a : SourceAvailability.Unknown;
}
