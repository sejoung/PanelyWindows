using Panely.Core.Abstractions;
using Panely.Core.Localization;
using Panely.Domain.Library;
using Panely.Domain.Model;
using Panely.Domain.Reading;

namespace Panely.Core.Reader;

[Flags]
public enum ReaderChange
{
    None = 0,
    Book = 1,
    Page = 2,
    Layout = 4,
    Direction = 8,
    Fit = 16,
    Cover = 32,
    Loading = 64,
    VolumeCards = 128,
    Bookmarks = 256,
    Favorite = 512,
    Error = 1024,
}

public sealed record ReaderLoadError(OpenRequest Request, OpenErrorKind Kind, string Message);

public enum VolumeNavigationResult { Opened, NoVolume, FirstVolume, LastVolume, NeedsAccess, Failed, Superseded }

/// <summary>
/// 리더 세션(07 ReaderSession + LoadCoordinator): 소스·논리 쪽·레이아웃·방향·맞춤·시리즈·loadEpoch.
/// UI 스레드에서만 호출한다. 모든 계산은 Domain(SpreadCalculator 등)을 공유한다.
/// </summary>
public sealed class ReaderSession : IDisposable
{
    private readonly IBookOpener _opener;
    private readonly IStateStore _state;
    private readonly IDiagnostics? _diagnostics;
    private readonly Func<string, bool> _directoryReadable;
    private CancellationTokenSource? _loadCts;
    private long _epoch;
    private bool _previousPrepared;

    public ReaderSession(IBookOpener opener, IStateStore state, IDiagnostics? diagnostics = null, Func<string, bool>? directoryReadable = null)
    {
        _opener = opener;
        _state = state;
        _diagnostics = diagnostics;
        _directoryReadable = directoryReadable ?? DefaultDirectoryReadable;
        var s = state.Settings;
        Layout = s.Layout;
        Direction = s.Direction;
        FitMode = s.FitMode;
        CoverAlone = s.DoublePageCoverAlone;
    }

    public event EventHandler<ReaderChange>? Changed;

    /// <summary>책이 화면에 반영되기 직전(이전 책 정리용): (이전 책, 새 책).</summary>
    public event EventHandler<OpenedBook?>? BookClosing;

    public NotificationCenter Notices { get; } = new();
    public SessionZoomMemory ZoomMemory { get; } = new();

    public OpenedBook? Book { get; private set; }
    public int PageIndex { get; private set; }
    public ReadingLayout Layout { get; private set; }
    /// <summary>저장된 방향 선택(vertical에서도 지우지 않음).</summary>
    public ReadingDirection Direction { get; private set; }
    public FitMode FitMode { get; private set; }
    public bool CoverAlone { get; private set; }

    public bool IsLoading { get; private set; }
    public LoadStage? Stage { get; private set; }
    public OpenRequest? PendingRequest { get; private set; }
    public ReaderLoadError? LastError { get; private set; }
    public long Epoch => Interlocked.Read(ref _epoch);

    public int PageCount => Book?.Pages.Count ?? 0;
    public int Step => Layout.Step();
    public bool EffectiveCoverAlone => CoverAlone && Layout == ReadingLayout.Double;
    public ReadingDirection EffectiveDirection => Layout.EffectiveDirection(Direction);
    public PageRange Spread => SpreadCalculator.Spread(PageIndex, PageCount, Step, EffectiveCoverAlone);
    public bool IsAtLastSpread => SpreadCalculator.IsLastSpread(PageIndex, PageCount, Step, EffectiveCoverAlone);
    public bool IsAtFirstSpread => PageCount > 0 && Spread.Start == 0;
    public bool CanGoNext => SpreadCalculator.NextStart(PageIndex, PageCount, Step, EffectiveCoverAlone) is not null;
    public bool CanGoPrevious => SpreadCalculator.PreviousStart(PageIndex, PageCount, Step, EffectiveCoverAlone) is not null;

    /// <summary>끝 펼침이고 다음 sibling이 있으면 Up next 카드.</summary>
    public bool ShowNextVolumeCard => Book?.NextVolume is not null && IsAtLastSpread && !IsLoading;

    /// <summary>첫 쪽에서 backward로 준비된 경우에만 Previous 카드.</summary>
    public bool ShowPreviousVolumeCard => _previousPrepared && Book?.PreviousVolume is not null && IsAtFirstSpread && !IsLoading;

    public bool IsPreviousVolumePrepared => _previousPrepared;

    public IReadOnlyList<int> BookmarkedPages
        => Book is null ? [] : _state.GetBookmarks(Book.BookKey).Select(b => b.PageIndex).Order().ToList();

    /// <summary>toggle 대상은 현재 펼침 시작 쪽.</summary>
    public bool IsCurrentPageBookmarked
        => Book is not null && _state.GetBookmarks(Book.BookKey).Any(b => b.PageIndex == Spread.Start);

    public bool IsFavorite => Book is not null && _state.IsFavorite(Book.BookKey);

    public int? NextBookmarkTarget => Book is null ? null : BookmarkNavigator.Next(BookmarkedPages, PageIndex, Spread.End);
    public int? PreviousBookmarkTarget => Book is null ? null : BookmarkNavigator.Previous(BookmarkedPages, Spread.Start);

    // ───────────────────────── 열기 ─────────────────────────

    /// <summary>
    /// 열기 파이프라인. epoch를 올리고 이전 로드를 취소한다. await 뒤마다 epoch를 검사해
    /// 오래된 결과(성공·오류·spinner 종료)가 새 요청의 화면 상태를 덮지 않게 한다.
    /// 반환: 이 요청이 화면에 반영되었으면 true.
    /// </summary>
    public async Task<bool> OpenAsync(OpenRequest request)
    {
        FlushPosition();
        var epoch = Interlocked.Increment(ref _epoch);
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        IsLoading = true;
        Stage = LoadStage.Opening;
        PendingRequest = request;
        Raise(ReaderChange.Loading | ReaderChange.VolumeCards);

        var progress = new SyncProgress<LoadStage>(stage =>
        {
            if (Epoch != epoch) return;
            Stage = stage;
            Raise(ReaderChange.Loading);
        });

        var started = DateTimeOffset.UtcNow;
        OpenedBook book;
        try
        {
            book = await _opener.OpenAsync(request, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (Epoch == epoch) EndLoading();
            return false;
        }
        catch (BookOpenException ex)
        {
            if (Epoch != epoch) return false;
            Fail(request, ex.Kind, ex.Message, started);
            return false;
        }
        catch (Exception ex)
        {
            if (Epoch != epoch) return false;
            Fail(request, OpenErrorKind.Unknown, ex.Message, started);
            return false;
        }

        if (Epoch != epoch || cts.IsCancellationRequested)
        {
            book.Dispose();
            return false;
        }

        // 열기 이벤트 자체는 BookOpener가 recent-load-events에 기록한다
        Commit(book, request);
        return true;
    }

    /// <summary>로딩 취소(사용자). 이전 책은 그대로.</summary>
    public void CancelLoading()
    {
        if (!IsLoading) return;
        Interlocked.Increment(ref _epoch);
        _loadCts?.Cancel();
        EndLoading();
    }

    private void EndLoading()
    {
        IsLoading = false;
        Stage = null;
        PendingRequest = null;
        Raise(ReaderChange.Loading | ReaderChange.VolumeCards);
    }

    private void Fail(OpenRequest request, OpenErrorKind kind, string message, DateTimeOffset started)
    {
        LastError = new ReaderLoadError(request, kind, message);
        IsLoading = false;
        Stage = null;
        PendingRequest = null;
        _diagnostics?.Log(LogLevel.Warning, LogCategory.Load,
            $"Open failed ({kind}) intent={request.Intent} ext={SupportedFormats.ExtensionOf(request.Path)} after {(DateTimeOffset.UtcNow - started).TotalMilliseconds:0}ms");
        Raise(ReaderChange.Loading | ReaderChange.Error | ReaderChange.VolumeCards);
    }

    private void Commit(OpenedBook book, OpenRequest request)
    {
        var previous = Book;
        BookClosing?.Invoke(this, previous);
        if (previous is not null) previous.Dispose();

        Book = book;
        LastError = null;
        _previousPrepared = false;

        // 시리즈 설정: override ?? global
        var settings = _state.Settings;
        var series = _state.GetSeries(book.SeriesKey);
        Layout = series?.Layout ?? settings.Layout;
        Direction = series?.Direction ?? settings.Direction;
        FitMode = series?.FitMode ?? settings.FitMode;
        CoverAlone = settings.DoublePageCoverAlone;

        var target = ResolveStartPage(book, request);
        PageIndex = SpreadCalculator.Jump(target, book.Pages.Count, Step, EffectiveCoverAlone);

        _state.TouchRecent(book.Source);
        IsLoading = false;
        Stage = null;
        PendingRequest = null;
        RecordProgress();
        Raise(ReaderChange.Book | ReaderChange.Page | ReaderChange.Layout | ReaderChange.Direction | ReaderChange.Fit
              | ReaderChange.Cover | ReaderChange.Loading | ReaderChange.VolumeCards | ReaderChange.Bookmarks
              | ReaderChange.Favorite | ReaderChange.Error);
    }

    /// <summary>
    /// 복원 규칙(04): 다른 책 북마크·명시 쪽이 모든 복원보다 우선, 다음 권은 첫 쪽, Reload는 요청 쪽,
    /// 나머지(일반·트리·즐겨찾기·이어보기·이전 권·최근)는 저장 위치, 없으면 첫 펼침.
    /// </summary>
    private int ResolveStartPage(OpenedBook book, OpenRequest request)
    {
        if (request.PageIndex is { } explicitPage) return explicitPage;
        if (request.Intent == OpenIntent.NextVolume) return 0;
        return _state.GetPosition(book.BookKey) ?? 0;
    }

    /// <summary>책 닫기(위치 flush).</summary>
    public void Close()
    {
        FlushPosition();
        CancelLoading();
        var previous = Book;
        if (previous is null) return;
        BookClosing?.Invoke(this, previous);
        previous.Dispose();
        Book = null;
        PageIndex = 0;
        _previousPrepared = false;
        Raise(ReaderChange.Book | ReaderChange.Page | ReaderChange.VolumeCards | ReaderChange.Bookmarks | ReaderChange.Favorite);
    }

    /// <summary>현재 책 다시 불러오기: 현재 쪽 유지.</summary>
    public Task<bool> ReloadAsync()
    {
        if (Book is null) return Task.FromResult(false);
        return OpenAsync(new OpenRequest(Book.Source.Path, Book.Book.InnerPath, PageIndex, OpenIntent.Reload));
    }

    /// <summary>실패한 요청 재시도.</summary>
    public Task<bool> RetryAsync() => LastError is { } e ? OpenAsync(e.Request) : Task.FromResult(false);

    public void ClearError()
    {
        if (LastError is null) return;
        LastError = null;
        Raise(ReaderChange.Error);
    }

    // ───────────────────────── 쪽 이동 ─────────────────────────

    /// <summary>임의 인덱스로 이동(펼침 시작으로 snap).</summary>
    public void Jump(int index)
    {
        if (Book is null || PageCount == 0) return;
        SetPage(SpreadCalculator.Jump(index, PageCount, Step, EffectiveCoverAlone));
    }

    /// <summary>1-based 쪽 번호로 이동(1..N clamp).</summary>
    public void JumpPage(int pageNumber)
    {
        if (Book is null || PageCount == 0) return;
        SetPage(SpreadCalculator.JumpPage(pageNumber, PageCount, Step, EffectiveCoverAlone));
    }

    /// <summary>vertical 스크롤로 현재 쪽이 바뀜(snap 없음, 이미지 재로딩 없음).</summary>
    public void SetCurrentPageFromScroll(int index)
    {
        if (Book is null || PageCount == 0) return;
        index = Math.Clamp(index, 0, PageCount - 1);
        if (index == PageIndex) return;
        SetPage(index);
    }

    /// <summary>툴바 다음 쪽: 책 안에서만.</summary>
    public bool Next()
    {
        var next = SpreadCalculator.NextStart(PageIndex, PageCount, Step, EffectiveCoverAlone);
        if (next is null) return false;
        SetPage(next.Value);
        return true;
    }

    /// <summary>툴바 이전 쪽: 책 안에서만.</summary>
    public bool Previous()
    {
        var prev = SpreadCalculator.PreviousStart(PageIndex, PageCount, Step, EffectiveCoverAlone);
        if (prev is null) return false;
        SetPage(prev.Value);
        return true;
    }

    /// <summary>키보드·휠 forward: 끝 펼침에서 다음 권이 있으면 다음 권(첫 쪽).</summary>
    public async Task ForwardAsync()
    {
        if (Book is null || IsLoading) return;
        if (Next()) return;
        if (Book.NextVolume is { } next) await OpenAsync(next.ToRequest(OpenIntent.NextVolume));
    }

    /// <summary>
    /// 키보드·휠 backward: 이전 권은 비대칭. 첫 쪽에서 backward → 카드 준비, 한 번 더 → 이전 권.
    /// 이전 펼침에서 backward로 첫 쪽에 도착하면 바로 준비.
    /// </summary>
    public async Task BackwardAsync()
    {
        if (Book is null || IsLoading) return;
        if (Previous())
        {
            if (IsAtFirstSpread && Book.PreviousVolume is not null)
            {
                _previousPrepared = true;
                Raise(ReaderChange.VolumeCards);
            }
            return;
        }
        if (Book.PreviousVolume is not { } prev) return;
        if (!_previousPrepared)
        {
            _previousPrepared = true;
            Raise(ReaderChange.VolumeCards);
            return;
        }
        await OpenAsync(prev.ToRequest(OpenIntent.PreviousVolume));
    }

    /// <summary>명시적 다음 권([, 권 버튼, 카드). 실패 이유를 구분한다.</summary>
    public async Task<VolumeNavigationResult> OpenNextVolumeAsync()
    {
        if (Book is null) return VolumeNavigationResult.NoVolume;
        if (Book.NextVolume is { } next)
            return await OpenAsync(next.ToRequest(OpenIntent.NextVolume)) ? VolumeNavigationResult.Opened : ResultOfFailure();
        return ReportBoundary(isNext: true);
    }

    public async Task<VolumeNavigationResult> OpenPreviousVolumeAsync()
    {
        if (Book is null) return VolumeNavigationResult.NoVolume;
        if (Book.PreviousVolume is { } prev)
            return await OpenAsync(prev.ToRequest(OpenIntent.PreviousVolume)) ? VolumeNavigationResult.Opened : ResultOfFailure();
        return ReportBoundary(isNext: false);
    }

    private VolumeNavigationResult ResultOfFailure() => LastError is null ? VolumeNavigationResult.Superseded : VolumeNavigationResult.Failed;

    private VolumeNavigationResult ReportBoundary(bool isNext)
    {
        var parent = Book is null ? null : Path.GetDirectoryName(Book.LibraryPath);
        if (Book is { Volumes.Count: 0 } && parent is not null && !_directoryReadable(parent))
        {
            Notices.Post(new Notice(NoticeCategory.Volume,
                Loc.T("Panely can't read the folder this book is in, so it can't open the next and previous books."),
                [NoticeAction.Dismiss]));
            return VolumeNavigationResult.NeedsAccess;
        }
        Notices.Post(new Notice(NoticeCategory.Volume,
            Loc.T(isNext ? "This is the last book in the folder." : "This is the first book in the folder."),
            [], TimeSpan.FromSeconds(3)));
        return isNext ? VolumeNavigationResult.LastVolume : VolumeNavigationResult.FirstVolume;
    }

    private static bool DefaultDirectoryReadable(string path)
    {
        try
        {
            using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            e.MoveNext();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void SetPage(int index)
    {
        if (Book is null) return;
        index = Math.Clamp(index, 0, Math.Max(0, PageCount - 1));
        var wasPrepared = _previousPrepared;
        if (!IsAtFirstSpreadFor(index)) _previousPrepared = false;
        var changed = index != PageIndex;
        PageIndex = index;
        if (changed) RecordProgress();
        var flags = ReaderChange.Page | ReaderChange.Bookmarks;
        if (wasPrepared != _previousPrepared || changed) flags |= ReaderChange.VolumeCards;
        Raise(flags);
    }

    private bool IsAtFirstSpreadFor(int index)
        => SpreadCalculator.Spread(index, PageCount, Step, EffectiveCoverAlone).Start == 0;

    // ───────────────────────── 보기 설정 ─────────────────────────

    /// <summary>레이아웃 변경: 같은 쪽을 포함하는 새 펼침으로, 시리즈+전역 기록.</summary>
    public void SetLayout(ReadingLayout layout)
    {
        if (Layout == layout) return;
        var anchor = PageIndex;
        Layout = layout;
        _state.UpdateSettings(s => s.Layout = layout);
        if (Book is not null) _state.UpdateSeries(Book.SeriesKey, p => { p.Layout = layout; p.UpdatedAt = DateTimeOffset.UtcNow; });
        if (Book is not null) PageIndex = SpreadCalculator.Jump(anchor, PageCount, Step, EffectiveCoverAlone);
        _previousPrepared = false;
        RecordProgress();
        Raise(ReaderChange.Layout | ReaderChange.Page | ReaderChange.Direction | ReaderChange.Cover | ReaderChange.VolumeCards | ReaderChange.Bookmarks);
    }

    /// <summary>방향 변경(vertical에서는 비활성이지만 저장값은 유지).</summary>
    public void SetDirection(ReadingDirection direction)
    {
        if (Direction == direction) return;
        Direction = direction;
        _state.UpdateSettings(s => s.Direction = direction);
        if (Book is not null) _state.UpdateSeries(Book.SeriesKey, p => { p.Direction = direction; p.UpdatedAt = DateTimeOffset.UtcNow; });
        Raise(ReaderChange.Direction);
    }

    public void ToggleDirection()
    {
        if (Layout == ReadingLayout.Vertical) return;
        SetDirection(Direction == ReadingDirection.LeftToRight ? ReadingDirection.RightToLeft : ReadingDirection.LeftToRight);
    }

    /// <summary>표지 단독: 같은 쪽을 포함한 새 펼침으로 이동하고, 인덱스가 같아도 다시 구성한다(Cover 변경 통지).</summary>
    public void SetCoverAlone(bool coverAlone)
    {
        if (CoverAlone == coverAlone) return;
        var anchor = PageIndex;
        CoverAlone = coverAlone;
        _state.UpdateSettings(s => s.DoublePageCoverAlone = coverAlone);
        if (Book is not null) PageIndex = SpreadCalculator.Jump(anchor, PageCount, Step, EffectiveCoverAlone);
        RecordProgress();
        Raise(ReaderChange.Cover | ReaderChange.Page | ReaderChange.VolumeCards | ReaderChange.Bookmarks);
    }

    /// <summary>맞춤 모드: 시리즈+전역 기록. 같은 모드 재선택도 Fit 변경 통지(뷰어가 baseline 복귀).</summary>
    public void SetFitMode(FitMode mode)
    {
        FitMode = mode;
        _state.UpdateSettings(s => s.FitMode = mode);
        if (Book is not null) _state.UpdateSeries(Book.SeriesKey, p => { p.FitMode = mode; p.UpdatedAt = DateTimeOffset.UtcNow; });
        Raise(ReaderChange.Fit);
    }

    // ───────────────────────── 북마크·즐겨찾기·진행 ─────────────────────────

    /// <returns>추가되었으면 true.</returns>
    public bool ToggleBookmark()
    {
        if (Book is null) return false;
        var added = _state.ToggleBookmark(Book.Book, Book.Title, Spread.Start);
        Raise(ReaderChange.Bookmarks);
        return added;
    }

    public bool GoToNextBookmark()
    {
        if (NextBookmarkTarget is not { } t) return false;
        Jump(t);
        return true;
    }

    public bool GoToPreviousBookmark()
    {
        if (PreviousBookmarkTarget is not { } t) return false;
        Jump(t);
        return true;
    }

    public void RemoveAllBookmarksInBook()
    {
        if (Book is null) return;
        _state.RemoveAllBookmarks(Book.BookKey);
        Raise(ReaderChange.Bookmarks);
    }

    public bool ToggleFavorite()
    {
        if (Book is null) return false;
        var added = _state.ToggleFavorite(Book.Book, FavoriteTitle(Book));
        Raise(ReaderChange.Favorite);
        return added;
    }

    /// <summary>즐겨찾기 제목: 시리즈 안의 권이면 "시리즈 · 권".</summary>
    public static string FavoriteTitle(OpenedBook book)
    {
        if (book.Volumes.Count > 1)
        {
            var parent = book.Book.InnerPath is null
                ? Path.GetFileName(Path.GetDirectoryName(book.LibraryPath) ?? string.Empty)
                : book.Source.Title;
            if (!string.IsNullOrEmpty(parent) && parent != book.Title) return $"{parent} · {book.Title}";
        }
        return book.Title;
    }

    /// <summary>권 끝 카드의 처음부터 다시 읽기: position 0, finished=false, 첫 쪽으로.</summary>
    public void RestartFromBeginning()
    {
        if (Book is null) return;
        _state.RestartBook(Book.BookKey, PageCount);
        PageIndex = 0;
        _previousPrepared = false;
        Raise(ReaderChange.Page | ReaderChange.VolumeCards | ReaderChange.Bookmarks);
    }

    private void RecordProgress()
    {
        if (Book is null || PageCount == 0) return;
        _state.SetPosition(Book.BookKey, PageIndex);
        var prev = _state.GetProgress(Book.BookKey);
        var value = ReadingProgressMath.Update(
            prev is null ? null : new ProgressValue(prev.Page, prev.Total, prev.Finished),
            PageIndex, PageCount, IsAtLastSpread);
        _state.SetProgress(Book.BookKey, value.Page, value.Total, value.Finished);
    }

    /// <summary>pending 위치를 디스크에 쓴다(책 전환/종료).</summary>
    public void FlushPosition()
    {
        if (Book is not null && PageCount > 0) _state.SetPosition(Book.BookKey, PageIndex);
        _state.Flush();
    }

    private void Raise(ReaderChange change) => Changed?.Invoke(this, change);

    public void Dispose()
    {
        FlushPosition();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        Book?.Dispose();
        Book = null;
    }

    /// <summary>동기 컨텍스트 없이 즉시 호출하는 IProgress(UI 스레드 epoch 검사는 콜백 안에서).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;

        public void Report(T value)
        {
            if (_context is null || SynchronizationContext.Current == _context) handler(value);
            else _context.Post(_ => handler(value), null);
        }
    }
}
