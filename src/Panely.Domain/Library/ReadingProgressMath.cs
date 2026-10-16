namespace Panely.Domain.Library;

public readonly record struct ProgressValue(int Page, int Total, bool Finished);

public static class ReadingProgressMath
{
    public static double Fraction(int page, int total)
        => total > 0 ? Math.Clamp((page + 1) / (double)total, 0, 1) : 0;

    /// <summary>
    /// 새 진행 값을 계산한다. 끝 펼침이면 finished. 같은 total이면 완독 상태를 자동 해제하지 않는다.
    /// total이 바뀌면 새 값으로 재평가한다.
    /// </summary>
    public static ProgressValue Update(ProgressValue? previous, int page, int total, bool isLastSpread)
    {
        var finished = isLastSpread;
        if (!finished && previous is { Finished: true } p && p.Total == total) finished = true;
        return new ProgressValue(Math.Max(0, page), Math.Max(0, total), finished);
    }
}

/// <summary>북마크 이동 규칙(05-data.md). 자동 wrap 없음.</summary>
public static class BookmarkNavigator
{
    /// <summary>
    /// 다음 북마크: currentPageIndex보다 엄격히 큰 쪽. Windows 보완: visible range 밖을 찾아 반복 정지 방지.
    /// </summary>
    public static int? Next(IEnumerable<int> bookmarkedPages, int currentPageIndex, int visibleEndExclusive)
    {
        var threshold = Math.Max(currentPageIndex, visibleEndExclusive - 1);
        int? best = null;
        foreach (var p in bookmarkedPages)
            if (p > threshold && (best is null || p < best)) best = p;
        return best;
    }

    public static int? Previous(IEnumerable<int> bookmarkedPages, int currentPageIndex)
    {
        int? best = null;
        foreach (var p in bookmarkedPages)
            if (p < currentPageIndex && (best is null || p > best)) best = p;
        return best;
    }
}
