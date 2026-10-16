using Panely.Domain.Model;

namespace Panely.Core.Abstractions;

/// <summary>로딩 단계(Opening/Extracting/Loading/Building).</summary>
public enum LoadStage { Opening, Analyzing, Extracting, Loading, Building }

public enum OpenErrorKind
{
    NotFound,
    TemporarilyUnavailable,
    AccessDenied,
    Unsupported,
    Empty,
    Encrypted,
    Corrupt,
    ExtractionLimit,
    InnerPathMissing,
    Unknown,
}

/// <summary>열기 실패. Kind로 원인을 구분해서 설명한다. Message는 경로를 축약한 사용자 문구.</summary>
public sealed class BookOpenException(OpenErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public OpenErrorKind Kind { get; } = kind;
}

/// <summary>열기 의도. 복원 규칙과 오류 fallback이 달라진다(04-files.md 로딩 파이프라인).</summary>
public enum OpenIntent
{
    /// <summary>파일 열기/탐색기/드롭. 저장 위치 복원.</summary>
    Normal,
    /// <summary>라이브러리 트리 선택. 저장 위치 복원, 루트 보존.</summary>
    TreeSelection,
    /// <summary>즐겨찾기. best-effort fallback 허용.</summary>
    Favorite,
    /// <summary>이어보기. innerPath가 사라지면 오류.</summary>
    Continue,
    /// <summary>다른 책 북마크. PageIndex가 모든 복원보다 우선, innerPath 사라지면 오류.</summary>
    Bookmark,
    /// <summary>최근 항목.</summary>
    Recent,
    /// <summary>다음 권: 첫 쪽부터.</summary>
    NextVolume,
    /// <summary>이전 권: 저장 위치 복원, 없으면 첫 펼침.</summary>
    PreviousVolume,
    /// <summary>현재 책 다시 불러오기: 현재 쪽 유지.</summary>
    Reload,
    /// <summary>cold start 마지막 폴더 복원.</summary>
    RestoreLibrary,
}

public sealed record OpenRequest(string Path, string? InnerPath = null, int? PageIndex = null, OpenIntent Intent = OpenIntent.Normal);

/// <summary>한 쪽. RelativePath는 책 기준 정규화 상대경로('/'), 아카이브면 entry 전체 경로.</summary>
public sealed record PageEntry
{
    public required int Index { get; init; }
    public required string RelativePath { get; init; }
    public required string FileName { get; init; }
    /// <summary>소문자, 점 없음(jpg, png …).</summary>
    public required string Extension { get; init; }
    public required string PageId { get; init; }
    public long? Length { get; init; }
}

/// <summary>열린 책의 쪽 데이터 접근. 한 아카이브 접근은 직렬화한다. 스레드 안전.</summary>
public interface IPageSource : IDisposable
{
    Task<byte[]> ReadAsync(PageEntry page, CancellationToken cancellationToken);

    /// <summary>헤더 치수 읽기용 앞부분(아카이브는 우선 64KiB).</summary>
    Task<byte[]> ReadPrefixAsync(PageEntry page, int maxBytes, CancellationToken cancellationToken);

    /// <summary>폴더 책이면 실제 파일 경로, 아카이브 entry면 null.</summary>
    string? TryGetFilePath(PageEntry page);
}

/// <summary>권 목록 항목(시리즈 sibling 또는 중첩 내부 권).</summary>
public sealed record VolumeEntry
{
    public required string DisplayName { get; init; }
    /// <summary>실제 파일/폴더 경로. 중첩 내부 권이면 바깥 아카이브 경로.</summary>
    public required string Path { get; init; }
    /// <summary>중첩 내부 권의 innerPath(바깥 아카이브 기준). 일반 sibling이면 null.</summary>
    public string? InnerPath { get; init; }
    public required bool IsArchive { get; init; }
    /// <summary>중첩 캐시 안의 권이면 true → 사이드바 Volumes 섹션에 표시.</summary>
    public bool IsNested { get; init; }

    public OpenRequest ToRequest(OpenIntent intent, int? pageIndex = null) => new(Path, InnerPath, pageIndex, intent);
}

/// <summary>열린 책. Dispose 시 Data와 cache lease를 해제한다.</summary>
public sealed class OpenedBook : IDisposable
{
    public required SourceRef Source { get; init; }
    public required BookRef Book { get; init; }
    public string BookKey => Book.Key;
    /// <summary>"series:..." 키. 일반 책은 부모 디렉터리, 중첩 책은 바깥 아카이브 identity 기준.</summary>
    public required string SeriesKey { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<PageEntry> Pages { get; init; }
    public required IPageSource Data { get; init; }
    /// <summary>앞뒤 권 목록(자연 정렬). 없으면 비어 있음.</summary>
    public IReadOnlyList<VolumeEntry> Volumes { get; init; } = [];
    /// <summary>Volumes 안에서 이 책의 위치, 없으면 -1.</summary>
    public int VolumeIndex { get; init; } = -1;
    /// <summary>중첩 아카이브 캐시 안의 책(떠날 때 번역 일시정지 대상).</summary>
    public bool IsNestedCacheBook { get; init; }
    /// <summary>변경 감시 대상: 아카이브 파일 또는 폴더 경로.</summary>
    public required string WatchPath { get; init; }
    /// <summary>사용자에게 보여줄 위치 설명(파일 이름 중심).</summary>
    public required string DisplayPath { get; init; }
    /// <summary>파일 트리의 활성 표시용 실제 경로(폴더 또는 아카이브 파일).</summary>
    public required string LibraryPath { get; init; }
    /// <summary>cache lease 등 추가 해제 대상.</summary>
    public IDisposable? Lease { get; init; }

    public VolumeEntry? NextVolume => VolumeIndex >= 0 && VolumeIndex + 1 < Volumes.Count ? Volumes[VolumeIndex + 1] : null;
    public VolumeEntry? PreviousVolume => VolumeIndex > 0 ? Volumes[VolumeIndex - 1] : null;

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Data.Dispose();
        Lease?.Dispose();
    }
}

/// <summary>소스 해석·형식 판별·폴더/겉포장/시리즈·중첩 추출·쪽 목록(04-files.md).</summary>
public interface IBookOpener
{
    /// <summary>
    /// 경로/innerPath의 책을 연다. 취소되면 OperationCanceledException.
    /// 실패하면 BookOpenException. 부분 상태를 남기지 않는다.
    /// </summary>
    Task<OpenedBook> OpenAsync(OpenRequest request, IProgress<LoadStage>? progress, CancellationToken cancellationToken);
}

/// <summary>지원 확장자·숨김 규칙.</summary>
public static class SupportedFormats
{
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "webp", "gif", "heic", "heif", "bmp", "tiff", "tif" };

    public static readonly IReadOnlySet<string> ArchiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "cbz", "zip", "cbr", "rar" };

    public static string ExtensionOf(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Length > 1 ? ext[1..].ToLowerInvariant() : string.Empty;
    }

    public static bool IsImage(string path) => ImageExtensions.Contains(ExtensionOf(path));

    public static bool IsArchive(string path) => ArchiveExtensions.Contains(ExtensionOf(path));

    /// <summary>점으로 시작하는 구성요소, __MACOSX, ._* 제외.</summary>
    public static bool IsHiddenRelativePath(string relativePath)
    {
        foreach (var part in relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith('.') || part.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
