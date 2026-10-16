using System.Text.Json;
using Panely.Core.Abstractions;
using Panely.Core.Settings;
using Panely.Domain.Model;

namespace Panely.Core.State;

/// <summary>
/// 영속 상태 저장소(05-data.md). %LOCALAPPDATA%/Panely/Data/state.json.
/// - 위치/진행은 2초 throttle로 디스크에 쓰고 pending 값은 즉시 조회된다. 다른 변경은 짧게(300ms) 모아서 쓴다.
/// - 쓰기: 같은 디렉터리 temp → flush → File.Replace(.bak 유지).
/// - 손상 파일은 격리 사본을 남기고 .bak 또는 기본값으로 시작한다. 원본은 덮지 않는다.
/// - 미래 schemaVersion이면 읽기 전용(IsReadOnly)으로 동작하고 쓰지 않는다.
/// </summary>
public sealed class StateStore : IStateStore, IDisposable
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxPositions = 4000;
    public const int MaxProgress = 4000;
    public const int MaxRecents = 50;
    public const int MaxBookmarksPerBook = 500;
    public const int MaxBookmarkedBooks = 200;
    public const int MaxSeries = 2000;
    public const int MaxExportHistory = 50;

    private readonly IDiagnostics _diagnostics;
    private readonly string _statePath;
    private readonly string _backupPath;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private StateDocument _doc;
    private bool _dirty;
    private bool _timerArmed;
    private bool _disposed;

    public StateStore(AppPaths paths, IDiagnostics diagnostics) : this(paths, diagnostics, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(300)) { }

    internal StateStore(AppPaths paths, IDiagnostics diagnostics, TimeSpan positionThrottle, TimeSpan otherDelay)
    {
        _diagnostics = diagnostics;
        PositionThrottle = positionThrottle;
        OtherDelay = otherDelay;
        Directory.CreateDirectory(paths.Data);
        _statePath = paths.StateFile;
        _backupPath = paths.StateFile + ".bak";
        _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
        _doc = Load();
        Cleanup();
    }

    internal TimeSpan PositionThrottle { get; }
    internal TimeSpan OtherDelay { get; }

    /// <summary>호출한 스레드에서 동기적으로 발생한다(대부분 UI 스레드의 변경). Sources 변경은 알리지 않는다.</summary>
    public event EventHandler<StateChange>? Changed;

    /// <summary>미래 schema 등으로 쓰기를 하지 않는 상태.</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>로드 중 발생한 문제(손상 격리/미래 schema). UI 복구 안내용, 없으면 null.</summary>
    public string? LoadProblem { get; private set; }

    /// <summary>손상 파일 격리 사본 경로(있으면).</summary>
    public string? QuarantinedPath { get; private set; }

    // ───────────── 로드/저장 ─────────────

    private StateDocument Load()
    {
        if (!File.Exists(_statePath))
        {
            if (File.Exists(_backupPath) && TryParse(_backupPath, out var fromBackup, out _)) return fromBackup!;
            return new StateDocument();
        }

        if (TryParse(_statePath, out var doc, out var error))
        {
            if (doc!.SchemaVersion > CurrentSchemaVersion)
            {
                IsReadOnly = true;
                LoadProblem = $"state.json schemaVersion {doc.SchemaVersion} is newer than this app supports ({CurrentSchemaVersion}). Running read-only.";
                _diagnostics.Log(LogLevel.Error, LogCategory.Persistence, LoadProblem);
            }
            return doc;
        }

        // 손상: 격리 사본을 남기고(원본은 그대로 둠) 백업 또는 기본값으로 시작
        try
        {
            var quarantine = Path.Combine(Path.GetDirectoryName(_statePath)!, $"state.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
            File.Copy(_statePath, quarantine, overwrite: false);
            QuarantinedPath = quarantine;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Log(LogLevel.Warning, LogCategory.Persistence, $"Could not quarantine corrupt state: {ex.GetType().Name}");
        }
        LoadProblem = $"state.json could not be read ({error}). A copy was kept and defaults were used.";
        _diagnostics.Log(LogLevel.Error, LogCategory.Persistence, LoadProblem);
        if (File.Exists(_backupPath) && TryParse(_backupPath, out var backup, out _) && backup!.SchemaVersion <= CurrentSchemaVersion)
        {
            _diagnostics.Log(LogLevel.Notice, LogCategory.Persistence, "Restored state from backup copy");
            return backup;
        }
        return new StateDocument();
    }

    private static bool TryParse(string path, out StateDocument? doc, out string? error)
    {
        doc = null;
        error = null;
        try
        {
            var text = File.ReadAllText(path);
            using (var probe = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                if (probe.RootElement.ValueKind != JsonValueKind.Object) { error = "root is not an object"; return false; }
                if (probe.RootElement.TryGetProperty("schemaVersion", out var v) && v.TryGetInt32(out var version) && version > CurrentSchemaVersion)
                {
                    // 미래 형식: 알 수 있는 부분만 읽는다
                    doc = TryLenient(text) ?? new StateDocument();
                    doc.SchemaVersion = version;
                    return true;
                }
            }
            doc = JsonSerializer.Deserialize<StateDocument>(text, StateJson.Options);
            if (doc is null) { error = "empty"; return false; }
            Normalize(doc);
            return true;
        }
        catch (JsonException ex) { error = ex.GetType().Name; return false; }
        catch (NotSupportedException ex) { error = ex.GetType().Name; return false; }
        catch (FormatException ex) { error = ex.GetType().Name; return false; }
        catch (IOException ex) { error = ex.GetType().Name; return false; }
        catch (UnauthorizedAccessException ex) { error = ex.GetType().Name; return false; }
    }

    private static StateDocument? TryLenient(string text)
    {
        try { var d = JsonSerializer.Deserialize<StateDocument>(text, StateJson.Options); if (d is not null) Normalize(d); return d; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException) { return null; }
    }

    private static void Normalize(StateDocument doc)
    {
        doc.Settings ??= new AppSettings();
        doc.Sources ??= [];
        doc.Positions = new Dictionary<string, PositionDto>(doc.Positions ?? [], StringComparer.Ordinal);
        doc.Progress = new Dictionary<string, ProgressDto>(doc.Progress ?? [], StringComparer.Ordinal);
        doc.Recents ??= [];
        doc.Favorites ??= [];
        doc.Bookmarks = new Dictionary<string, BookBookmarksDto>(doc.Bookmarks ?? [], StringComparer.Ordinal);
        doc.SeriesPreferences = new Dictionary<string, SeriesPreferences>(doc.SeriesPreferences ?? [], StringComparer.Ordinal);
        doc.ExportHistory ??= [];
        foreach (var b in doc.Bookmarks.Values) b.Bookmarks ??= [];
    }

    /// <summary>시작 시 참조 없는 metadata 정리와 상한 적용. 세션 중에는 source를 지우지 않는다.</summary>
    private void Cleanup()
    {
        lock (_gate)
        {
            var changed = TrimPositions() | TrimProgress() | TrimSeries() | TrimBookmarks();
            if (_doc.Recents.Count > MaxRecents) { _doc.Recents.RemoveRange(MaxRecents, _doc.Recents.Count - MaxRecents); changed = true; }
            if (_doc.ExportHistory.Count > MaxExportHistory) { _doc.ExportHistory.RemoveRange(MaxExportHistory, _doc.ExportHistory.Count - MaxExportHistory); changed = true; }
            foreach (var key in _doc.Bookmarks.Where(p => p.Value.Bookmarks.Count == 0).Select(p => p.Key).ToList())
            {
                _doc.Bookmarks.Remove(key);
                changed = true;
            }

            var referenced = new HashSet<Guid>();
            void AddKey(string bookKey)
            {
                if (BookKeys.TryParse(bookKey, out var id, out _)) referenced.Add(id);
            }
            foreach (var k in _doc.Positions.Keys) AddKey(k);
            foreach (var k in _doc.Progress.Keys) AddKey(k);
            foreach (var k in _doc.Bookmarks.Keys) AddKey(k);
            foreach (var r in _doc.Recents) referenced.Add(r.SourceId);
            foreach (var f in _doc.Favorites) referenced.Add(f.BookRef.SourceId);
            foreach (var b in _doc.Bookmarks.Values) referenced.Add(b.BookRef.SourceId);
            foreach (var k in _doc.SeriesPreferences.Keys)
            {
                if (k.StartsWith("series:", StringComparison.Ordinal) && Guid.TryParse(k.AsSpan(7), out var sid)) referenced.Add(sid);
            }
            if (_doc.LastLibraryRoot is { } root) referenced.Add(root);
            var before = _doc.Sources.Count;
            _doc.Sources.RemoveAll(s => !referenced.Contains(s.Id));
            if (_doc.Sources.Count != before) changed = true;
            if (changed) ScheduleSave(immediate: false);
        }
    }

    private void ScheduleSave(bool immediate, bool throttled = false)
    {
        // _gate 안에서 호출
        if (IsReadOnly || _disposed) return;
        _dirty = true;
        if (_timerArmed && throttled) return; // throttle: 이미 예약됨
        var delay = throttled ? PositionThrottle : OtherDelay;
        if (_timerArmed && !throttled)
        {
            // 위치 throttle보다 빠른 저장이 필요하면 앞당긴다
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
            return;
        }
        _timerArmed = true;
        _timer.Change(immediate ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        try { Flush(); }
        catch (Exception ex) { _diagnostics.Log(LogLevel.Error, LogCategory.Persistence, $"State save failed: {ex.GetType().Name}"); }
    }

    public void Flush()
    {
        string json;
        lock (_gate)
        {
            _timerArmed = false;
            if (!_dirty || IsReadOnly) return;
            json = JsonSerializer.Serialize(_doc, StateJson.Options);
            _dirty = false;
            try
            {
                WriteAtomic(json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dirty = true;
                _diagnostics.Log(LogLevel.Error, LogCategory.Persistence, $"State write failed: {ex.GetType().Name}");
            }
        }
    }

    private void WriteAtomic(string json)
    {
        var dir = Path.GetDirectoryName(_statePath)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $"state.json.{Guid.NewGuid():N}.tmp");
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
        {
            writer.Write(json);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }
        try
        {
            if (File.Exists(_statePath) && QuarantinedPath is null)
                File.Replace(tmp, _statePath, _backupPath, ignoreMetadataErrors: true);
            else if (File.Exists(_statePath))
            {
                // 손상 원본은 격리 사본이 있으므로 백업으로 덮지 않고 교체만 한다
                File.Move(tmp, _statePath, overwrite: true);
                QuarantinedPath = null;
            }
            else
                File.Move(tmp, _statePath);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }

    private void Raise(StateChange change)
    {
        if (change != StateChange.None) Changed?.Invoke(this, change);
    }

    // ───────────── 설정 ─────────────

    public AppSettings Settings
    {
        get { lock (_gate) return _doc.Settings; }
    }

    public void UpdateSettings(Action<AppSettings> update)
    {
        lock (_gate)
        {
            update(_doc.Settings);
            ScheduleSave(false);
        }
        Raise(StateChange.Settings);
    }

    // ───────────── Source ─────────────

    public static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (root is not null && full.Length > root.Length) full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full;
    }

    public SourceRef GetOrCreateSource(string path, SourceKind kind, string title)
    {
        var normalized = NormalizePath(path);
        FileIdentity.TryGet(normalized, out var volumeId, out var fileId);
        var change = StateChange.None;
        SourceRef result;
        lock (_gate)
        {
            var existing = _doc.Sources.FirstOrDefault(s => string.Equals(s.Path, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (fileId.Length > 0 && (existing.FileId != fileId || existing.VolumeId != volumeId))
                {
                    existing.FileId = fileId;
                    existing.VolumeId = volumeId;
                    change = StateChange.Sources;
                }
                if (existing.Kind != kind) { existing.Kind = kind; change = StateChange.Sources; }
                result = existing.ToRef();
            }
            else
            {
                SourceDto? moved = null;
                if (fileId.Length > 0)
                {
                    moved = _doc.Sources.FirstOrDefault(s => s.FileId == fileId && s.VolumeId == volumeId && s.Kind == kind
                                                             && !PathExists(s.Path, s.Kind));
                }
                if (moved is not null)
                {
                    _diagnostics.Log(LogLevel.Info, LogCategory.Persistence, "Source moved; reference path updated");
                    moved.Path = normalized;
                    moved.Title = title;
                    result = moved.ToRef();
                }
                else
                {
                    var dto = new SourceDto
                    {
                        Id = Guid.NewGuid(),
                        Path = normalized,
                        Kind = kind,
                        Title = title,
                        VolumeId = volumeId.Length > 0 ? volumeId : null,
                        FileId = fileId.Length > 0 ? fileId : null,
                    };
                    _doc.Sources.Add(dto);
                    result = dto.ToRef();
                }
                change = StateChange.Sources;
            }
            if (change != StateChange.None) ScheduleSave(false);
        }
        // Sources 변경은 내부 bookkeeping이며 BookOpener의 worker 스레드에서 호출되므로 Changed를 발생시키지 않는다.
        return result;
    }

    private static bool PathExists(string path, SourceKind kind)
    {
        try { return kind == SourceKind.Folder ? Directory.Exists(path) : File.Exists(path); }
        catch { return false; }
    }

    public SourceRef? FindSource(Guid id)
    {
        lock (_gate) return _doc.Sources.FirstOrDefault(s => s.Id == id)?.ToRef();
    }

    public SourceRef? FindSourceByPath(string path)
    {
        var normalized = NormalizePath(path);
        lock (_gate) return _doc.Sources.FirstOrDefault(s => string.Equals(s.Path, normalized, StringComparison.OrdinalIgnoreCase))?.ToRef();
    }

    public IReadOnlyList<SourceRef> Sources
    {
        get { lock (_gate) return _doc.Sources.Select(s => s.ToRef()).ToList(); }
    }

    // ───────────── 위치/진행 ─────────────

    public int? GetPosition(string bookKey)
    {
        lock (_gate) return _doc.Positions.TryGetValue(bookKey, out var p) ? p.PageIndex : null;
    }

    public void SetPosition(string bookKey, int pageIndex)
    {
        lock (_gate)
        {
            if (_doc.Positions.TryGetValue(bookKey, out var p) && p.PageIndex == pageIndex) return;
            _doc.Positions[bookKey] = new PositionDto { PageIndex = Math.Max(0, pageIndex), UpdatedAt = DateTimeOffset.UtcNow };
            TrimPositions();
            ScheduleSave(false, throttled: true);
        }
        Raise(StateChange.Positions);
    }

    public ProgressRecord? GetProgress(string bookKey)
    {
        lock (_gate) return _doc.Progress.TryGetValue(bookKey, out var p) ? ToRecord(p) : null;
    }

    private static ProgressRecord ToRecord(ProgressDto p) => new(p.Page, p.Total, p.Finished, p.UpdatedAt);

    public void SetProgress(string bookKey, int page, int total, bool finished)
    {
        lock (_gate)
        {
            if (_doc.Progress.TryGetValue(bookKey, out var p) && p.Page == page && p.Total == total && p.Finished == finished) return;
            _doc.Progress[bookKey] = new ProgressDto { Page = Math.Max(0, page), Total = Math.Max(0, total), Finished = finished, UpdatedAt = DateTimeOffset.UtcNow };
            TrimProgress();
            ScheduleSave(false, throttled: true);
        }
        Raise(StateChange.Progress);
    }

    public IReadOnlyDictionary<string, ProgressRecord> AllProgress
    {
        get { lock (_gate) return _doc.Progress.ToDictionary(p => p.Key, p => ToRecord(p.Value), StringComparer.Ordinal); }
    }

    public void RemoveProgress(string bookKey)
    {
        lock (_gate)
        {
            if (!_doc.Progress.Remove(bookKey)) return;
            ScheduleSave(false);
        }
        Raise(StateChange.Progress);
    }

    public void RestartBook(string bookKey, int total)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _doc.Positions[bookKey] = new PositionDto { PageIndex = 0, UpdatedAt = now };
            _doc.Progress[bookKey] = new ProgressDto { Page = 0, Total = Math.Max(0, total), Finished = false, UpdatedAt = now };
            ScheduleSave(false);
        }
        Raise(StateChange.Positions | StateChange.Progress);
    }

    private bool TrimPositions()
    {
        if (_doc.Positions.Count <= MaxPositions) return false;
        foreach (var key in _doc.Positions.OrderBy(p => p.Value.UpdatedAt).Take(_doc.Positions.Count - MaxPositions).Select(p => p.Key).ToList())
            _doc.Positions.Remove(key);
        return true;
    }

    private bool TrimProgress()
    {
        if (_doc.Progress.Count <= MaxProgress) return false;
        foreach (var key in _doc.Progress.OrderBy(p => p.Value.UpdatedAt).Take(_doc.Progress.Count - MaxProgress).Select(p => p.Key).ToList())
            _doc.Progress.Remove(key);
        return true;
    }

    // ───────────── 최근 ─────────────

    public IReadOnlyList<RecentRecord> Recents
    {
        get { lock (_gate) return _doc.Recents.Select(r => new RecentRecord(r.Id, r.SourceId, r.Title, r.OpenedAt)).ToList(); }
    }

    public void TouchRecent(SourceRef source)
    {
        lock (_gate)
        {
            var existing = _doc.Recents.FirstOrDefault(r => r.SourceId == source.Id);
            if (existing is not null) _doc.Recents.Remove(existing);
            var item = existing ?? new RecentDto { Id = Guid.NewGuid(), SourceId = source.Id };
            item.Title = source.Title;
            item.OpenedAt = DateTimeOffset.UtcNow;
            _doc.Recents.Insert(0, item);
            if (_doc.Recents.Count > MaxRecents) _doc.Recents.RemoveRange(MaxRecents, _doc.Recents.Count - MaxRecents);
            ScheduleSave(false);
        }
        Raise(StateChange.Recents);
    }

    public void RemoveRecent(Guid sourceId)
    {
        lock (_gate)
        {
            _doc.Recents.RemoveAll(r => r.SourceId == sourceId);
            var prefix = $"source:{sourceId:D}";
            foreach (var key in _doc.Progress.Keys.Where(k => k == prefix || k.StartsWith(prefix + "#", StringComparison.Ordinal)).ToList())
                _doc.Progress.Remove(key);
            ScheduleSave(false);
        }
        Raise(StateChange.Recents | StateChange.Progress);
    }

    public void ClearRecents()
    {
        lock (_gate)
        {
            if (_doc.Recents.Count == 0) return;
            _doc.Recents.Clear();
            ScheduleSave(false);
        }
        Raise(StateChange.Recents);
    }

    // ───────────── 즐겨찾기 ─────────────

    public IReadOnlyList<FavoriteRecord> Favorites
    {
        get { lock (_gate) return _doc.Favorites.Select(f => new FavoriteRecord(f.Id, f.BookRef.ToRef(), f.Title, f.AddedAt)).ToList(); }
    }

    public bool IsFavorite(string bookKey)
    {
        lock (_gate) return _doc.Favorites.Any(f => f.BookRef.ToRef().Key == bookKey);
    }

    public bool ToggleFavorite(BookRef book, string title)
    {
        bool added;
        lock (_gate)
        {
            var key = book.Key;
            var existing = _doc.Favorites.FirstOrDefault(f => f.BookRef.ToRef().Key == key);
            if (existing is not null)
            {
                _doc.Favorites.Remove(existing);
                added = false;
            }
            else
            {
                _doc.Favorites.Insert(0, new FavoriteDto { Id = Guid.NewGuid(), BookRef = BookRefDto.From(book), Title = title, AddedAt = DateTimeOffset.UtcNow });
                added = true;
            }
            ScheduleSave(false);
        }
        Raise(StateChange.Favorites);
        return added;
    }

    public void RemoveFavorite(Guid favoriteId)
    {
        lock (_gate)
        {
            if (_doc.Favorites.RemoveAll(f => f.Id == favoriteId) == 0) return;
            ScheduleSave(false);
        }
        Raise(StateChange.Favorites);
    }

    // ───────────── 북마크 ─────────────

    public IReadOnlyList<BookmarkRecord> GetBookmarks(string bookKey)
    {
        lock (_gate)
        {
            return _doc.Bookmarks.TryGetValue(bookKey, out var b)
                ? b.Bookmarks.OrderBy(x => x.PageIndex).Select(x => new BookmarkRecord(x.Id, x.PageIndex, x.CreatedAt)).ToList()
                : [];
        }
    }

    public IReadOnlyList<BookBookmarks> AllBookmarks
    {
        get
        {
            lock (_gate)
            {
                return _doc.Bookmarks.Values
                    .Where(b => b.Bookmarks.Count > 0)
                    .OrderByDescending(b => b.Bookmarks.Max(x => x.CreatedAt))
                    .Select(b => new BookBookmarks(b.BookRef.ToRef(), b.Title,
                        b.Bookmarks.OrderBy(x => x.PageIndex).Select(x => new BookmarkRecord(x.Id, x.PageIndex, x.CreatedAt)).ToList()))
                    .ToList();
            }
        }
    }

    public bool ToggleBookmark(BookRef book, string title, int pageIndex)
    {
        bool added;
        lock (_gate)
        {
            var key = book.Key;
            if (!_doc.Bookmarks.TryGetValue(key, out var entry))
            {
                entry = new BookBookmarksDto { BookRef = BookRefDto.From(book), Title = title };
                _doc.Bookmarks[key] = entry;
            }
            entry.Title = title;
            var existing = entry.Bookmarks.FirstOrDefault(b => b.PageIndex == pageIndex);
            if (existing is not null)
            {
                entry.Bookmarks.Remove(existing);
                if (entry.Bookmarks.Count == 0) _doc.Bookmarks.Remove(key);
                added = false;
            }
            else
            {
                entry.Bookmarks.Add(new BookmarkDto { Id = Guid.NewGuid(), PageIndex = pageIndex, CreatedAt = DateTimeOffset.UtcNow });
                added = true;
                TrimBookmarks(protectKey: key);
            }
            ScheduleSave(false);
        }
        Raise(StateChange.Bookmarks);
        return added;
    }

    /// <summary>책당 500 초과면 그 책의 가장 오래된 북마크, 200책 초과면 최근 북마크 시각이 가장 오래된 책부터 제거.</summary>
    private bool TrimBookmarks(string? protectKey = null)
    {
        var changed = false;
        foreach (var b in _doc.Bookmarks.Values)
        {
            if (b.Bookmarks.Count <= MaxBookmarksPerBook) continue;
            foreach (var old in b.Bookmarks.OrderBy(x => x.CreatedAt).Take(b.Bookmarks.Count - MaxBookmarksPerBook).ToList())
                b.Bookmarks.Remove(old);
            changed = true;
        }
        while (_doc.Bookmarks.Count > MaxBookmarkedBooks)
        {
            var victim = _doc.Bookmarks
                .Where(p => p.Key != protectKey)
                .OrderBy(p => p.Value.Bookmarks.Count == 0 ? DateTimeOffset.MinValue : p.Value.Bookmarks.Max(x => x.CreatedAt))
                .Select(p => p.Key)
                .FirstOrDefault();
            if (victim is null) break;
            _doc.Bookmarks.Remove(victim);
            changed = true;
        }
        return changed;
    }

    public void RemoveBookmark(string bookKey, Guid bookmarkId)
    {
        lock (_gate)
        {
            if (!_doc.Bookmarks.TryGetValue(bookKey, out var b) || b.Bookmarks.RemoveAll(x => x.Id == bookmarkId) == 0) return;
            if (b.Bookmarks.Count == 0) _doc.Bookmarks.Remove(bookKey);
            ScheduleSave(false);
        }
        Raise(StateChange.Bookmarks);
    }

    public void RemoveAllBookmarks(string bookKey)
    {
        lock (_gate)
        {
            if (!_doc.Bookmarks.Remove(bookKey)) return;
            ScheduleSave(false);
        }
        Raise(StateChange.Bookmarks);
    }

    public void RemoveAllBookmarks()
    {
        lock (_gate)
        {
            if (_doc.Bookmarks.Count == 0) return;
            _doc.Bookmarks.Clear();
            ScheduleSave(false);
        }
        Raise(StateChange.Bookmarks);
    }

    // ───────────── 시리즈 ─────────────

    public SeriesPreferences? GetSeries(string seriesKey)
    {
        lock (_gate) return _doc.SeriesPreferences.TryGetValue(seriesKey, out var s) ? s.Clone() : null;
    }

    public void UpdateSeries(string seriesKey, Action<SeriesPreferences> update)
    {
        lock (_gate)
        {
            if (!_doc.SeriesPreferences.TryGetValue(seriesKey, out var s))
            {
                s = new SeriesPreferences();
                _doc.SeriesPreferences[seriesKey] = s;
            }
            update(s);
            s.UpdatedAt = DateTimeOffset.UtcNow;
            TrimSeries();
            ScheduleSave(false);
        }
        Raise(StateChange.Series);
    }

    private bool TrimSeries()
    {
        if (_doc.SeriesPreferences.Count <= MaxSeries) return false;
        foreach (var key in _doc.SeriesPreferences.OrderBy(p => p.Value.UpdatedAt).Take(_doc.SeriesPreferences.Count - MaxSeries).Select(p => p.Key).ToList())
            _doc.SeriesPreferences.Remove(key);
        return true;
    }

    // ───────────── 내보내기 ─────────────

    public IReadOnlyList<ExportRecord> ExportHistory
    {
        get
        {
            lock (_gate)
                return _doc.ExportHistory.Select(e => new ExportRecord(e.Id, e.Title, e.Target, e.Format, e.Path, e.PageCount, e.Bytes, e.Date)).ToList();
        }
    }

    public void AddExport(ExportRecord record)
    {
        lock (_gate)
        {
            _doc.ExportHistory.RemoveAll(e => e.Id == record.Id);
            _doc.ExportHistory.Insert(0, new ExportRecordDto
            {
                Id = record.Id, Title = record.Title, Target = record.Target, Format = record.Format,
                Path = record.Path, PageCount = record.PageCount, Bytes = record.Bytes, Date = record.Date,
            });
            if (_doc.ExportHistory.Count > MaxExportHistory) _doc.ExportHistory.RemoveRange(MaxExportHistory, _doc.ExportHistory.Count - MaxExportHistory);
            ScheduleSave(false);
        }
        Raise(StateChange.Exports);
    }

    public void ClearExportHistory()
    {
        lock (_gate)
        {
            _doc.ExportHistory.Clear();
            ScheduleSave(false);
        }
        Raise(StateChange.Exports);
    }

    public ExportOptions? ExportOptions
    {
        get { lock (_gate) return _doc.ExportOptions is { } o ? new ExportOptions { Target = o.Target, Format = o.Format, Untranslated = o.Untranslated } : null; }
        set
        {
            lock (_gate)
            {
                _doc.ExportOptions = value is null ? null : new ExportOptions { Target = value.Target, Format = value.Format, Untranslated = value.Untranslated };
                ScheduleSave(false);
            }
            Raise(StateChange.Exports);
        }
    }

    public string? ExportDestination
    {
        get { lock (_gate) return _doc.ExportDestination; }
        set
        {
            lock (_gate)
            {
                _doc.ExportDestination = value;
                ScheduleSave(false);
            }
            Raise(StateChange.Exports);
        }
    }

    // ───────────── 라이브러리 root ─────────────

    public Guid? LastLibraryRoot
    {
        get { lock (_gate) return _doc.LastLibraryRoot; }
    }

    public void SetLastLibraryRoot(Guid? sourceId)
    {
        lock (_gate)
        {
            if (_doc.LastLibraryRoot == sourceId) return;
            _doc.LastLibraryRoot = sourceId;
            ScheduleSave(false);
        }
        Raise(StateChange.LibraryRoot);
    }

    public void Dispose()
    {
        Flush();
        lock (_gate) _disposed = true;
        _timer.Dispose();
    }
}
