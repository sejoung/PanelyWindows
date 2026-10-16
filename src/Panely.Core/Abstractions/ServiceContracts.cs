using Panely.Core.Settings;
using Panely.Domain.Library;
using Panely.Domain.Model;

namespace Panely.Core.Abstractions;

// ───────────────────────── 상태 저장 ─────────────────────────

public sealed record RecentRecord(Guid Id, Guid SourceId, string Title, DateTimeOffset OpenedAt);

public sealed record FavoriteRecord(Guid Id, BookRef BookRef, string Title, DateTimeOffset AddedAt);

public sealed record BookmarkRecord(Guid Id, int PageIndex, DateTimeOffset CreatedAt);

public sealed record BookBookmarks(BookRef BookRef, string Title, IReadOnlyList<BookmarkRecord> Bookmarks);

public sealed record ProgressRecord(int Page, int Total, bool Finished, DateTimeOffset UpdatedAt)
{
    public double Fraction => ReadingProgressMath.Fraction(Page, Total);
}

public sealed record ExportRecord(Guid Id, string Title, string Target, ExportFormat Format, string Path, int PageCount, long Bytes, DateTimeOffset Date);

public enum SourceAvailability { Available, TemporarilyUnavailable, InvalidReference, Unknown }

[Flags]
public enum StateChange
{
    None = 0,
    Settings = 1,
    Positions = 2,
    Progress = 4,
    Recents = 8,
    Favorites = 16,
    Bookmarks = 32,
    Series = 64,
    Exports = 128,
    LibraryRoot = 256,
    Sources = 512,
}

/// <summary>
/// 영속 상태(05-data.md). 단일 UI dispatcher에서 호출하는 것을 기본으로 하되 내부는 thread-safe.
/// 위치/진행은 2초 debounce, pending 값은 즉시 조회 가능. Flush는 책 전환/종료 시 호출.
/// 쓰기는 temp→flush→replace, 직전 사본(.bak) 유지, 손상 파일은 격리 후 기본값.
/// </summary>
public interface IStateStore
{
    event EventHandler<StateChange>? Changed;

    AppSettings Settings { get; }
    void UpdateSettings(Action<AppSettings> update);

    /// <summary>경로(정규화)로 기존 SourceRef를 찾거나, file id/volume id로 이동을 감지해 갱신, 없으면 새 UUID.</summary>
    SourceRef GetOrCreateSource(string path, SourceKind kind, string title);
    SourceRef? FindSource(Guid id);
    SourceRef? FindSourceByPath(string path);

    int? GetPosition(string bookKey);
    void SetPosition(string bookKey, int pageIndex);

    ProgressRecord? GetProgress(string bookKey);
    void SetProgress(string bookKey, int page, int total, bool finished);
    IReadOnlyDictionary<string, ProgressRecord> AllProgress { get; }
    /// <summary>이어보기 제거: 진행 제안 기록만 지운다.</summary>
    void RemoveProgress(string bookKey);
    /// <summary>처음부터 다시 읽기: position 0, finished=false.</summary>
    void RestartBook(string bookKey, int total);

    IReadOnlyList<RecentRecord> Recents { get; }
    void TouchRecent(SourceRef source);
    /// <summary>Remove from List: 해당 recent와 그 source 진행 기록 제거.</summary>
    void RemoveRecent(Guid sourceId);
    void ClearRecents();

    IReadOnlyList<FavoriteRecord> Favorites { get; }
    bool IsFavorite(string bookKey);
    /// <returns>추가되었으면 true, 제거되었으면 false.</returns>
    bool ToggleFavorite(BookRef book, string title);
    void RemoveFavorite(Guid favoriteId);

    IReadOnlyList<BookmarkRecord> GetBookmarks(string bookKey);
    IReadOnlyList<BookBookmarks> AllBookmarks { get; }
    /// <returns>추가되었으면 true, 제거되었으면 false. 책당 500·전체 200책 상한.</returns>
    bool ToggleBookmark(BookRef book, string title, int pageIndex);
    void RemoveBookmark(string bookKey, Guid bookmarkId);
    void RemoveAllBookmarks(string bookKey);
    void RemoveAllBookmarks();

    SeriesPreferences? GetSeries(string seriesKey);
    void UpdateSeries(string seriesKey, Action<SeriesPreferences> update);

    IReadOnlyList<ExportRecord> ExportHistory { get; }
    void AddExport(ExportRecord record);
    void ClearExportHistory();
    ExportOptions? ExportOptions { get; set; }
    string? ExportDestination { get; set; }

    Guid? LastLibraryRoot { get; }
    void SetLastLibraryRoot(Guid? sourceId);

    /// <summary>pending 위치·진행 등을 즉시 디스크에 쓴다.</summary>
    void Flush();
}

// ───────────────────────── 라이브러리 ─────────────────────────

public enum LibraryNodeKind { Folder, Archive }

/// <summary>파일 트리 노드(폴더/지원 아카이브만). 이미지 파일은 트리에 표시하지 않는다.</summary>
public sealed record LibraryNode(string Name, string Path, LibraryNodeKind Kind, string Extension, bool MayHaveChildren);

/// <summary>
/// 파일 트리 지연 탐색과 변경 감시. 재귀 변경 알림을 1.5초 debounce하고 cache·로그·무관 파일을 걸러
/// 바뀐 디렉터리 목록을 알린다. UI가 해당 폴더의 children을 다시 받아 diff 한다(목록을 비우지 않음).
/// </summary>
public interface ILibraryService : IDisposable
{
    string? RootPath { get; }
    /// <summary>바뀐 디렉터리 경로 목록(UI 스레드 아님).</summary>
    event EventHandler<IReadOnlyList<string>>? DirectoriesChanged;
    /// <summary>감시 버퍼 overflow: 수동 새로고침 안내. 무한 재스캔하지 않는다.</summary>
    event EventHandler? WatchOverflowed;

    void SetRoot(string? path, bool autoRefresh);
    void SetAutoRefresh(bool enabled);
    /// <summary>헤더 수동 새로고침 후 호출: overflow 안내 상태를 초기화한다.</summary>
    void NotifyManualRefresh();
    /// <summary>자연 정렬된 하위 폴더+아카이브. 숨김/시스템 제외.</summary>
    Task<IReadOnlyList<LibraryNode>> GetChildrenAsync(string folderPath, CancellationToken cancellationToken);
}

/// <summary>현재 열린 책 소스 변경 감시(배너용). 자동 페이지 이동은 하지 않는다.</summary>
public interface ISourceMonitor : IDisposable
{
    event EventHandler? SourceChanged;
    void Watch(OpenedBook? book);
}

public interface IAvailabilityChecker
{
    /// <summary>백그라운드에서 존재 확인. 이동식 드라이브 분리는 TemporarilyUnavailable.</summary>
    Task<SourceAvailability> CheckAsync(SourceRef source, string? innerPath, CancellationToken cancellationToken);
}

// ───────────────────────── 캐시 ─────────────────────────

public sealed record CacheUsage(long TotalBytes, long ClearableBytes, long LimitBytes);

/// <summary>영속 해제 캐시(10GiB LRU, lease 보호).</summary>
public interface IExtractionCache
{
    CacheUsage GetUsage();
    /// <summary>lease 없는 entry만 삭제. 삭제한 바이트 수.</summary>
    Task<long> ClearAsync(CancellationToken cancellationToken);
    /// <summary>앱 시작 시 orphan staging 정리.</summary>
    void CleanupOrphans();
}

// ───────────────────────── 진단 ─────────────────────────

public enum LogCategory { App, Reader, Load, Cache, Library, Persistence, Image, Diagnostics, Translation, Export }

public sealed record LoadEvent(DateTimeOffset Time, string Kind, string FileName, string Extension, string Outcome, TimeSpan? Duration);

/// <summary>
/// 파일 로그(recent-log.txt, 768KiB 초과 시 최신 ~384KiB로 trim)와 진단 리포트.
/// 절대 경로·사용자명·OCR 원문·번역 본문을 metadata로 기록하지 않는다. 로그 I/O 실패로 앱을 멈추지 않는다.
/// </summary>
public interface IDiagnostics
{
    Guid SessionId { get; }
    DateTimeOffset LaunchedAt { get; }
    LogLevel MinimumLevel { get; set; }
    string LogFolder { get; }
    long LogFileBytes { get; }

    void Log(LogLevel level, LogCategory category, string message);
    void RecordLoad(LoadEvent loadEvent);
    IReadOnlyList<string> RecentErrors { get; }

    /// <summary>Panely-Diagnostic-Report-yyyyMMdd-HHmmss.zip 기본 이름.</summary>
    string DefaultReportFileName(DateTimeOffset now);
    /// <summary>diagnostic-report.md / recent-log.txt / recent-load-events.txt 3파일 ZIP.</summary>
    Task ExportReportAsync(string zipPath, DiagnosticSnapshot snapshot, CancellationToken cancellationToken);
    /// <summary>Diagnostics 파일 로그만 삭제.</summary>
    void ClearLogs();
}

/// <summary>리포트에 넣을 앱 상태(경로는 축약해서 넘긴다).</summary>
public sealed record DiagnosticSnapshot(
    string AppVersion,
    string Build,
    string OsVersion,
    CacheUsage Cache,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyDictionary<string, string> ReaderState);

public static class DiagnosticsExtensions
{
    public static void Info(this IDiagnostics d, LogCategory c, string m) => d.Log(LogLevel.Info, c, m);
    public static void Warning(this IDiagnostics d, LogCategory c, string m) => d.Log(LogLevel.Warning, c, m);
    public static void Error(this IDiagnostics d, LogCategory c, string m) => d.Log(LogLevel.Error, c, m);
    public static void Debug(this IDiagnostics d, LogCategory c, string m) => d.Log(LogLevel.Debug, c, m);
}

// ───────────────────────── 이미지 헤더 ─────────────────────────

/// <summary>헤더만 읽은 이미지 정보. Width/Height는 EXIF 회전 적용 후 픽셀 크기.</summary>
public readonly record struct ImageHeaderInfo(int Width, int Height, int ExifOrientation, string Format);
