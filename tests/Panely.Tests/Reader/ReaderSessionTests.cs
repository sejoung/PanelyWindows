using Panely.Core.Abstractions;
using Panely.Core.Reader;
using Panely.Domain.Model;

namespace Panely.Tests.Reader;

public class ReaderSessionTests
{
    private readonly FakeStateStore _state = new();
    private readonly FakeOpener _opener;
    private readonly ReaderSession _session;

    public ReaderSessionTests()
    {
        _opener = new FakeOpener(_state);
        _session = new ReaderSession(_opener, _state, null, _ => true);
    }

    private Task<bool> Open(string path, OpenIntent intent = OpenIntent.Normal, int? page = null, string? inner = null)
        => _session.OpenAsync(new OpenRequest(path, inner, page, intent));

    [Fact]
    public async Task Open_UsesGlobalDefaults_AndStartsAtFirstPage()
    {
        _opener.AddBook("a.cbz", 10);
        Assert.True(await Open("a.cbz"));
        Assert.Equal(0, _session.PageIndex);
        Assert.Equal(ReadingLayout.Single, _session.Layout);
        Assert.False(_session.IsLoading);
        Assert.Single(_state.Recents);
    }

    [Fact]
    public async Task Open_RestoresSavedPosition_SnappedToSpread()
    {
        _opener.AddBook("a.cbz", 10);
        await Open("a.cbz");
        var key = _session.Book!.BookKey;
        _session.SetLayout(ReadingLayout.Double);
        _session.Close();
        _state.SetPosition(key, 3);
        await Open("a.cbz");
        Assert.Equal(2, _session.PageIndex);
    }

    [Fact]
    public async Task Bookmark_PageIndex_WinsOverSavedPosition()
    {
        _opener.AddBook("a.cbz", 10);
        await Open("a.cbz");
        _state.SetPosition(_session.Book!.BookKey, 7);
        await Open("a.cbz", OpenIntent.Bookmark, page: 4);
        Assert.Equal(4, _session.PageIndex);
    }

    [Fact]
    public async Task Reload_KeepsCurrentPage()
    {
        _opener.AddBook("a.cbz", 10);
        await Open("a.cbz");
        _session.Jump(6);
        await _session.ReloadAsync();
        Assert.Equal(6, _session.PageIndex);
        Assert.Equal(OpenIntent.Reload, _opener.Requests[^1].Intent);
    }

    [Fact]
    public async Task SeriesOverride_AppliesAndChangesWriteSeriesAndGlobal()
    {
        _opener.AddSeries(("v1.cbz", 4), ("v2.cbz", 4));
        await Open("v1.cbz");
        _session.SetDirection(ReadingDirection.RightToLeft);
        _session.SetFitMode(FitMode.FitWidth);
        Assert.Equal(ReadingDirection.RightToLeft, _state.Settings.Direction);
        Assert.Equal(ReadingDirection.RightToLeft, _state.GetSeries("series:S")!.Direction);
        Assert.Equal(FitMode.FitWidth, _state.GetSeries("series:S")!.FitMode);

        // 다른 시리즈 책: 시리즈 override 없음 → 전역(방금 바뀐 값)
        _opener.AddBook("other.cbz", 3);
        _state.UpdateSettings(s => s.Direction = ReadingDirection.LeftToRight);
        await Open("other.cbz");
        Assert.Equal(ReadingDirection.LeftToRight, _session.Direction);

        // 원래 시리즈로 돌아오면 override 적용
        await Open("v2.cbz");
        Assert.Equal(ReadingDirection.RightToLeft, _session.Direction);
        Assert.Equal(FitMode.FitWidth, _session.FitMode);
    }

    [Fact]
    public async Task Vertical_IgnoresDirection_ButKeepsStoredChoice()
    {
        _opener.AddBook("a.cbz", 5);
        await Open("a.cbz");
        _session.SetDirection(ReadingDirection.RightToLeft);
        _session.SetLayout(ReadingLayout.Vertical);
        Assert.Equal(ReadingDirection.LeftToRight, _session.EffectiveDirection);
        _session.ToggleDirection();
        Assert.Equal(ReadingDirection.RightToLeft, _session.Direction);
        _session.SetLayout(ReadingLayout.Double);
        Assert.Equal(ReadingDirection.RightToLeft, _session.EffectiveDirection);
    }

    [Fact]
    public async Task StaleLoad_DoesNotOverrideNewerRequest()
    {
        _opener.AddBook("slow.cbz", 5);
        _opener.AddBook("fast.cbz", 3);
        var gate = _opener.Gate("slow.cbz");
        var slow = Open("slow.cbz");
        var fast = await Open("fast.cbz");
        Assert.True(fast);
        Assert.Equal("fast", _session.Book!.Title);
        gate.SetResult();
        Assert.False(await slow);
        Assert.Equal("fast", _session.Book!.Title);
        Assert.False(_session.IsLoading);
        var slowBook = _opener.Opened.Single(b => b.Title == "slow");
        Assert.True(((FakePageSource)slowBook.Data).Disposed);
    }

    [Fact]
    public async Task StaleFailure_DoesNotSetError()
    {
        _opener.AddBook("fast.cbz", 3);
        _opener.Fail("bad.cbz", OpenErrorKind.Corrupt);
        var gate = _opener.Gate("bad.cbz");
        var bad = Open("bad.cbz");
        await Open("fast.cbz");
        gate.SetResult();
        await bad;
        Assert.Null(_session.LastError);
        Assert.Equal("fast", _session.Book!.Title);
    }

    [Fact]
    public async Task Failure_KeepsPreviousBook_AndRecordsError()
    {
        _opener.AddBook("a.cbz", 3);
        _opener.Fail("bad.cbz", OpenErrorKind.Encrypted);
        await Open("a.cbz");
        Assert.False(await Open("bad.cbz"));
        Assert.Equal("a", _session.Book!.Title);
        Assert.Equal(OpenErrorKind.Encrypted, _session.LastError!.Kind);
        Assert.False(_session.IsLoading);
    }

    [Fact]
    public async Task OpeningNewBook_FlushesPreviousPosition()
    {
        _opener.AddBook("a.cbz", 9);
        _opener.AddBook("b.cbz", 3);
        await Open("a.cbz");
        var keyA = _session.Book!.BookKey;
        _session.Jump(5);
        var flushes = _state.FlushCount;
        await Open("b.cbz");
        Assert.Equal(5, _state.GetPosition(keyA));
        Assert.True(_state.FlushCount > flushes);
    }

    [Fact]
    public async Task Forward_AtLastSpread_OpensNextVolumeAtFirstPage()
    {
        _opener.AddSeries(("v1.cbz", 4), ("v2.cbz", 4));
        await Open("v2.cbz");
        _session.Jump(3);
        await Open("v1.cbz");
        _session.Jump(3);
        Assert.True(_session.ShowNextVolumeCard);
        await _session.ForwardAsync();
        Assert.Equal("v2", _session.Book!.Title);
        Assert.Equal(0, _session.PageIndex);
        Assert.Equal(OpenIntent.NextVolume, _opener.Requests[^1].Intent);
    }

    [Fact]
    public async Task LastVolume_HasNoNextCard_AndForwardStays()
    {
        _opener.AddSeries(("v1.cbz", 2), ("v2.cbz", 2));
        await Open("v2.cbz");
        _session.Jump(1);
        Assert.False(_session.ShowNextVolumeCard);
        var count = _opener.Requests.Count;
        await _session.ForwardAsync();
        Assert.Equal(count, _opener.Requests.Count);
    }

    [Fact]
    public async Task PreviousVolume_IsAsymmetric()
    {
        _opener.AddSeries(("v1.cbz", 6), ("v2.cbz", 6));
        await Open("v1.cbz");
        _session.Jump(4);
        await Open("v2.cbz");
        // 새 책 첫 쪽: 카드 없음
        Assert.False(_session.ShowPreviousVolumeCard);
        await _session.BackwardAsync();
        Assert.True(_session.ShowPreviousVolumeCard);
        Assert.Equal("v2", _session.Book!.Title);
        await _session.BackwardAsync();
        Assert.Equal("v1", _session.Book!.Title);
        // 이전 권은 저장 위치 복원
        Assert.Equal(4, _session.PageIndex);
        Assert.Equal(OpenIntent.PreviousVolume, _opener.Requests[^1].Intent);
    }

    [Fact]
    public async Task ArrivingAtFirstPageByBackward_PreparesImmediately()
    {
        _opener.AddSeries(("v1.cbz", 6), ("v2.cbz", 6));
        await Open("v2.cbz");
        _session.Jump(1);
        await _session.BackwardAsync();
        Assert.Equal(0, _session.PageIndex);
        Assert.True(_session.IsPreviousVolumePrepared);
        await _session.BackwardAsync();
        Assert.Equal("v1", _session.Book!.Title);
    }

    [Fact]
    public async Task LeavingFirstPage_ClearsPreparedState()
    {
        _opener.AddSeries(("v1.cbz", 6), ("v2.cbz", 6));
        await Open("v2.cbz");
        await _session.BackwardAsync();
        Assert.True(_session.IsPreviousVolumePrepared);
        await _session.ForwardAsync();
        Assert.False(_session.IsPreviousVolumePrepared);
        _session.Jump(0);
        Assert.False(_session.ShowPreviousVolumeCard);
    }

    [Fact]
    public async Task ExplicitVolumeNavigation_ReportsBoundaries()
    {
        _opener.AddSeries(("v1.cbz", 2), ("v2.cbz", 2));
        await Open("v2.cbz");
        Assert.Equal(VolumeNavigationResult.LastVolume, await _session.OpenNextVolumeAsync());
        var notice = _session.Notices.Current!;
        Assert.Equal(NoticeCategory.Volume, notice.Category);
        Assert.Equal(TimeSpan.FromSeconds(3), notice.Duration);
        Assert.Equal(VolumeNavigationResult.Opened, await _session.OpenPreviousVolumeAsync());
        Assert.Equal(VolumeNavigationResult.FirstVolume, await _session.OpenPreviousVolumeAsync());
    }

    [Fact]
    public async Task ExplicitVolumeNavigation_UnreadableFolder_NeedsAccess()
    {
        var session = new ReaderSession(_opener, _state, null, _ => false);
        _opener.AddBook(@"C:\x\lonely.cbz", 2);
        await session.OpenAsync(new OpenRequest(@"C:\x\lonely.cbz"));
        Assert.Equal(VolumeNavigationResult.NeedsAccess, await session.OpenNextVolumeAsync());
        Assert.Null(session.Notices.Current!.Duration);
    }

    [Fact]
    public async Task Bookmark_InDouble_TargetsSpreadStart_AndNextSkipsVisibleRange()
    {
        _opener.AddBook("a.cbz", 10);
        await Open("a.cbz");
        _session.SetLayout(ReadingLayout.Double);
        _session.Jump(3); // 펼침 (2,3)
        Assert.Equal(2, _session.PageIndex);
        Assert.True(_session.ToggleBookmark());
        Assert.Contains(2, _session.BookmarkedPages);
        Assert.True(_session.IsCurrentPageBookmarked);
        Assert.False(_session.ToggleBookmark()); // 같은 쪽 toggle은 제거
        Assert.Empty(_session.BookmarkedPages);

        // 펼침 안 두 번째 쪽(3)에 북마크가 있어도 다음 탐색은 visible range 밖을 찾는다
        _state.ToggleBookmark(_session.Book!.Book, "a", 3);
        _state.ToggleBookmark(_session.Book!.Book, "a", 7);
        Assert.Equal(7, _session.NextBookmarkTarget);
        Assert.True(_session.GoToNextBookmark());
        Assert.Equal(6, _session.PageIndex);
        Assert.Equal(3, _session.PreviousBookmarkTarget);
        _session.GoToPreviousBookmark();
        Assert.Equal(2, _session.PageIndex);
        Assert.Null(_session.PreviousBookmarkTarget);
    }

    [Fact]
    public async Task Progress_FinishedAtLastDoubleSpread_IsSticky()
    {
        _opener.AddBook("a.cbz", 6);
        await Open("a.cbz");
        _session.SetLayout(ReadingLayout.Double);
        _session.Jump(4);
        var key = _session.Book!.BookKey;
        Assert.True(_state.GetProgress(key)!.Finished);
        Assert.True(_state.GetProgress(key)!.Fraction < 1);
        _session.Jump(0);
        Assert.True(_state.GetProgress(key)!.Finished);
        _session.RestartFromBeginning();
        Assert.False(_state.GetProgress(key)!.Finished);
        Assert.Equal(0, _state.GetPosition(key));
    }

    [Fact]
    public async Task CoverToggle_ResnapsToSpreadContainingPage()
    {
        _opener.AddBook("a.cbz", 6);
        await Open("a.cbz");
        _session.SetLayout(ReadingLayout.Double);
        _session.Jump(2); // (2,3)
        var changes = new List<ReaderChange>();
        _session.Changed += (_, c) => changes.Add(c);
        _session.SetCoverAlone(true);
        Assert.Equal(1, _session.PageIndex); // (1,2)
        Assert.Contains(changes, c => c.HasFlag(ReaderChange.Cover));
        Assert.True(_state.Settings.DoublePageCoverAlone);
    }

    [Fact]
    public async Task VerticalScroll_UpdatesPageWithoutSnap()
    {
        _opener.AddBook("a.cbz", 6);
        await Open("a.cbz");
        _session.SetLayout(ReadingLayout.Vertical);
        _session.SetCurrentPageFromScroll(5);
        Assert.Equal(5, _session.PageIndex);
        Assert.True(_session.IsAtLastSpread);
        Assert.True(_state.GetProgress(_session.Book!.BookKey)!.Finished);
    }

    [Fact]
    public async Task NextPrevious_StayInsideBook()
    {
        _opener.AddSeries(("v1.cbz", 2), ("v2.cbz", 2));
        await Open("v1.cbz");
        Assert.True(_session.Next());
        Assert.False(_session.Next());
        Assert.Equal("v1", _session.Book!.Title);
        Assert.True(_session.Previous());
        Assert.False(_session.Previous());
    }

    [Fact]
    public async Task JumpPage_ClampsToRange()
    {
        _opener.AddBook("a.cbz", 6);
        await Open("a.cbz");
        _session.JumpPage(999);
        Assert.Equal(5, _session.PageIndex);
        _session.JumpPage(-10);
        Assert.Equal(0, _session.PageIndex);
    }

    [Fact]
    public async Task Favorite_Toggle()
    {
        _opener.AddBook("a.cbz", 2);
        await Open("a.cbz");
        Assert.True(_session.ToggleFavorite());
        Assert.True(_session.IsFavorite);
        Assert.False(_session.ToggleFavorite());
        Assert.False(_session.IsFavorite);
    }

    [Fact]
    public async Task CancelLoading_KeepsPreviousBook()
    {
        _opener.AddBook("a.cbz", 2);
        _opener.AddBook("slow.cbz", 2);
        await Open("a.cbz");
        var gate = _opener.Gate("slow.cbz");
        var pending = Open("slow.cbz");
        Assert.True(_session.IsLoading);
        _session.CancelLoading();
        Assert.False(_session.IsLoading);
        gate.SetResult();
        Assert.False(await pending);
        Assert.Equal("a", _session.Book!.Title);
    }
}
