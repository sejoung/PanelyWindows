using Panely.Core.Abstractions;
using Panely.Core.Settings;
using Panely.Domain.Model;

namespace Panely.Tests.Reader;

/// <summary>메모리 IStateStore(ReaderSession 테스트용, 상한·debounce 없음).</summary>
internal sealed class FakeStateStore : IStateStore
{
    private readonly Dictionary<string, int> _positions = new();
    private readonly Dictionary<string, ProgressRecord> _progress = new();
    private readonly Dictionary<string, SeriesPreferences> _series = new();
    private readonly Dictionary<string, (BookRef Ref, string Title, List<BookmarkRecord> List)> _bookmarks = new();
    private readonly List<FavoriteRecord> _favorites = new();
    private readonly List<RecentRecord> _recents = new();
    private readonly List<SourceRef> _sources = new();

    public event EventHandler<StateChange>? Changed;
    public int FlushCount { get; private set; }
    public AppSettings Settings { get; } = new();

    public void UpdateSettings(Action<AppSettings> update) { update(Settings); Changed?.Invoke(this, StateChange.Settings); }

    public SourceRef GetOrCreateSource(string path, SourceKind kind, string title)
    {
        var s = _sources.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (s is not null) return s;
        s = new SourceRef { Id = Guid.NewGuid(), Path = path, Kind = kind, Title = title };
        _sources.Add(s);
        return s;
    }

    public SourceRef? FindSource(Guid id) => _sources.FirstOrDefault(s => s.Id == id);
    public SourceRef? FindSourceByPath(string path) => _sources.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
    public int? GetPosition(string bookKey) => _positions.TryGetValue(bookKey, out var p) ? p : null;
    public void SetPosition(string bookKey, int pageIndex) => _positions[bookKey] = pageIndex;
    public ProgressRecord? GetProgress(string bookKey) => _progress.TryGetValue(bookKey, out var p) ? p : null;
    public void SetProgress(string bookKey, int page, int total, bool finished) => _progress[bookKey] = new ProgressRecord(page, total, finished, DateTimeOffset.UtcNow);
    public IReadOnlyDictionary<string, ProgressRecord> AllProgress => _progress;
    public void RemoveProgress(string bookKey) => _progress.Remove(bookKey);
    public void RestartBook(string bookKey, int total) { _positions[bookKey] = 0; _progress[bookKey] = new ProgressRecord(0, total, false, DateTimeOffset.UtcNow); }
    public IReadOnlyList<RecentRecord> Recents => _recents;

    public void TouchRecent(SourceRef source)
    {
        _recents.RemoveAll(r => r.SourceId == source.Id);
        _recents.Insert(0, new RecentRecord(Guid.NewGuid(), source.Id, source.Title, DateTimeOffset.UtcNow));
    }

    public void RemoveRecent(Guid sourceId) => _recents.RemoveAll(r => r.SourceId == sourceId);
    public void ClearRecents() => _recents.Clear();
    public IReadOnlyList<FavoriteRecord> Favorites => _favorites;
    public bool IsFavorite(string bookKey) => _favorites.Any(f => f.BookRef.Key == bookKey);

    public bool ToggleFavorite(BookRef book, string title)
    {
        if (_favorites.RemoveAll(f => f.BookRef.Key == book.Key) > 0) return false;
        _favorites.Add(new FavoriteRecord(Guid.NewGuid(), book, title, DateTimeOffset.UtcNow));
        return true;
    }

    public void RemoveFavorite(Guid favoriteId) => _favorites.RemoveAll(f => f.Id == favoriteId);
    public IReadOnlyList<BookmarkRecord> GetBookmarks(string bookKey) => _bookmarks.TryGetValue(bookKey, out var b) ? b.List : [];
    public IReadOnlyList<BookBookmarks> AllBookmarks => _bookmarks.Values.Select(v => new BookBookmarks(v.Ref, v.Title, v.List)).ToList();

    public bool ToggleBookmark(BookRef book, string title, int pageIndex)
    {
        if (!_bookmarks.TryGetValue(book.Key, out var entry)) _bookmarks[book.Key] = entry = (book, title, new List<BookmarkRecord>());
        if (entry.List.RemoveAll(b => b.PageIndex == pageIndex) > 0) return false;
        entry.List.Add(new BookmarkRecord(Guid.NewGuid(), pageIndex, DateTimeOffset.UtcNow));
        return true;
    }

    public void RemoveBookmark(string bookKey, Guid bookmarkId) { if (_bookmarks.TryGetValue(bookKey, out var e)) e.List.RemoveAll(b => b.Id == bookmarkId); }
    public void RemoveAllBookmarks(string bookKey) => _bookmarks.Remove(bookKey);
    public void RemoveAllBookmarks() => _bookmarks.Clear();
    public SeriesPreferences? GetSeries(string seriesKey) => _series.TryGetValue(seriesKey, out var s) ? s : null;

    public void UpdateSeries(string seriesKey, Action<SeriesPreferences> update)
    {
        if (!_series.TryGetValue(seriesKey, out var s)) _series[seriesKey] = s = new SeriesPreferences();
        update(s);
    }

    public IReadOnlyList<ExportRecord> ExportHistory => [];
    public void AddExport(ExportRecord record) { }
    public void ClearExportHistory() { }
    public ExportOptions? ExportOptions { get; set; }
    public string? ExportDestination { get; set; }
    public Guid? LastLibraryRoot { get; private set; }
    public void SetLastLibraryRoot(Guid? sourceId) => LastLibraryRoot = sourceId;
    public void Flush() => FlushCount++;
}

internal sealed class FakePageSource : IPageSource
{
    public bool Disposed { get; private set; }
    public Task<byte[]> ReadAsync(PageEntry page, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> ReadPrefixAsync(PageEntry page, int maxBytes, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<byte>());
    public string? TryGetFilePath(PageEntry page) => null;
    public void Dispose() => Disposed = true;
}

/// <summary>경로별 쪽 수·시리즈를 미리 정의하는 IBookOpener. Gate를 걸면 완료를 수동으로 제어한다.</summary>
internal sealed class FakeOpener : IBookOpener
{
    private readonly FakeStateStore _state;
    private readonly Dictionary<string, int> _pageCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OpenErrorKind> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _series = new();

    public FakeOpener(FakeStateStore state) => _state = state;

    public List<OpenRequest> Requests { get; } = new();
    public List<OpenedBook> Opened { get; } = new();

    public void AddBook(string path, int pages) => _pageCounts[path] = pages;

    /// <summary>같은 폴더 시리즈의 권들(자연 정렬 순서로 전달).</summary>
    public void AddSeries(params (string Path, int Pages)[] volumes)
    {
        foreach (var (p, n) in volumes) { _pageCounts[p] = n; _series.Add(p); }
    }

    public void Fail(string path, OpenErrorKind kind) => _failures[path] = kind;

    public TaskCompletionSource Gate(string path)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates[path] = tcs;
        return tcs;
    }

    public async Task<OpenedBook> OpenAsync(OpenRequest request, IProgress<LoadStage>? progress, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        progress?.Report(LoadStage.Opening);
        if (_gates.TryGetValue(request.Path, out var gate))
        {
            _gates.Remove(request.Path);
            await gate.Task;
        }
        if (_failures.TryGetValue(request.Path, out var kind)) throw new BookOpenException(kind, kind.ToString());
        if (!_pageCounts.TryGetValue(request.Path, out var count)) throw new BookOpenException(OpenErrorKind.NotFound, "missing");

        var source = _state.GetOrCreateSource(request.Path, SourceKind.Archive, Path.GetFileNameWithoutExtension(request.Path));
        var pages = Enumerable.Range(0, count).Select(i => new PageEntry
        {
            Index = i,
            RelativePath = $"{i + 1:000}.jpg",
            FileName = $"{i + 1:000}.jpg",
            Extension = "jpg",
            PageId = $"{source.Id}|{i}",
        }).ToList();
        var volumes = _series.Contains(request.Path, StringComparer.OrdinalIgnoreCase)
            ? _series.Select(p => new VolumeEntry { DisplayName = Path.GetFileNameWithoutExtension(p), Path = p, IsArchive = true }).ToList()
            : [];
        var book = new OpenedBook
        {
            Source = source,
            Book = new BookRef(source.Id, null),
            SeriesKey = _series.Contains(request.Path, StringComparer.OrdinalIgnoreCase) ? "series:S" : $"series:{source.Id}",
            Title = source.Title,
            Pages = pages,
            Data = new FakePageSource(),
            Volumes = volumes,
            VolumeIndex = volumes.FindIndex(v => string.Equals(v.Path, request.Path, StringComparison.OrdinalIgnoreCase)),
            WatchPath = request.Path,
            DisplayPath = Path.GetFileName(request.Path),
            LibraryPath = request.Path,
        };
        Opened.Add(book);
        return book;
    }
}
