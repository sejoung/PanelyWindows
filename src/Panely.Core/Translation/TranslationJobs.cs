using Panely.Core.Abstractions;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Core.Translation;

public enum TranslationPhase { Idle, DetectingLanguage, Preparing, Translating, Paused, Queued, Completed, Failed }

public enum TranslationStopReason { None, NeedsLanguageDownload, UnsupportedPair, CouldNotDetectLanguage, Failed }

/// <summary>번역/내보내기 대상 책(열려 있지 않아도 다시 열 수 있는 정보).</summary>
public sealed record BookTarget(string BookKey, string Path, string? InnerPath, string Title, string SeriesKey, bool IsNestedCacheBook)
{
    public static BookTarget From(OpenedBook book) =>
        new(book.BookKey, book.Source.Path, book.Book.InnerPath, book.Title, book.SeriesKey, book.IsNestedCacheBook);

    public OpenRequest ToRequest() => new(Path, InnerPath, null, OpenIntent.Normal);
}

/// <summary>번역 작업 상태 snapshot(UI 표시용, 불변).</summary>
public sealed record TranslationJobInfo
{
    public required BookTarget Book { get; init; }
    public required string Target { get; init; }
    public string? Source { get; init; }
    public required TranslationMode Mode { get; init; }
    public required TranslationScope Scope { get; init; }
    public required TranslationPhase Phase { get; init; }
    public TranslationStopReason Reason { get; init; }
    /// <summary>사용자에게 보여줄 사유 문장(현지화 완료).</summary>
    public string? ReasonMessage { get; init; }
    public int CompletedPages { get; init; }
    public int TotalPages { get; init; }
    public bool UserPaused { get; init; }
    /// <summary>중첩 캐시 책이라 떠나서 일시정지됨(돌아오면 재개).</summary>
    public bool AutoPaused { get; init; }
    public bool IsCurrentBook { get; init; }
    /// <summary>pace가 생긴 뒤에만 값이 있다(가짜 ETA 금지).</summary>
    public TimeSpan? Remaining { get; init; }
    public DateTimeOffset EnqueuedAt { get; init; }
    public bool IsActive { get; init; }

    public string Key => TranslationScheduler.JobKey(Book.BookKey, Target);
    public bool IsRunningOrQueued => Phase is TranslationPhase.DetectingLanguage or TranslationPhase.Preparing or TranslationPhase.Translating or TranslationPhase.Queued;
    public bool CanResume => Phase is TranslationPhase.Paused or TranslationPhase.Failed && Reason != TranslationStopReason.UnsupportedPair;
}

/// <summary>쪽 갱신(부분 결과 포함). Page.IsComplete=false면 아직 번역 중.</summary>
public sealed record TranslationPageUpdate(string BookKey, string Target, int PageIndex, TranslatedPage Page);

/// <summary>페이지 픽셀/치수 공급(앱의 decoder). worker 스레드에서 호출된다.</summary>
public interface IPagePixelSource
{
    Task<PagePixels> GetPixelsAsync(OpenedBook book, PageEntry page, CancellationToken cancellationToken);
    Task<SizeD> GetSizeAsync(OpenedBook book, PageEntry page, CancellationToken cancellationToken);
}

/// <summary>번역/내보내기 작업이 자기 책 핸들을 연다(리더의 책 수명과 독립).</summary>
public interface IBookAccess
{
    Task<OpenedBook> OpenAsync(BookTarget target, CancellationToken cancellationToken);
}

public sealed class OpenerBookAccess(IBookOpener opener) : IBookAccess
{
    public Task<OpenedBook> OpenAsync(BookTarget target, CancellationToken cancellationToken)
        => opener.OpenAsync(target.ToRequest(), null, cancellationToken);
}

/// <summary>번역 설정 조회(앱 설정과 연결).</summary>
public sealed class TranslationOptions
{
    public Func<TranslationMode> Mode { get; init; } = () => TranslationMode.Fast;
    public Func<bool> MatchSpeechLevel { get; init; } = () => false;
    /// <summary>한 번에 번역기에 보내는 풍선 수(점진 표시 단위).</summary>
    public int TranslateChunk { get; init; } = 4;
    public int PixelCacheSize { get; init; } = 4;
}
