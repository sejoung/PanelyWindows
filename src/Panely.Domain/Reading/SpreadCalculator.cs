using Panely.Domain.Model;

namespace Panely.Domain.Reading;

/// <summary>펼침 계산(03-reader.md). 모든 UI·복원·북마크·슬라이더가 이 계산을 공유한다.</summary>
public static class SpreadCalculator
{
    public static PageRange Spread(int index, int pageCount, int step, bool coverAlone)
    {
        if (pageCount <= 0 || step <= 0) return new PageRange(0, 0);
        var i = Math.Clamp(index, 0, pageCount - 1);
        var offset = coverAlone && step >= 2 ? 1 : 0;
        var start = i < offset ? 0 : offset + FloorDiv(i - offset, step) * step;
        var length = start < offset ? 1 : Math.Min(step, pageCount - start);
        return new PageRange(start, start + length);
    }

    public static PageRange Spread(int index, int pageCount, ReadingLayout layout, bool coverAlone)
        => Spread(index, pageCount, layout.Step(), coverAlone);

    public static int? NextStart(int index, int pageCount, int step, bool coverAlone)
    {
        var s = Spread(index, pageCount, step, coverAlone);
        return s.End < pageCount ? s.End : null;
    }

    public static int? PreviousStart(int index, int pageCount, int step, bool coverAlone)
    {
        var s = Spread(index, pageCount, step, coverAlone);
        return s.Start > 0 ? Spread(s.Start - 1, pageCount, step, coverAlone).Start : null;
    }

    /// <summary>jump(index): 펼침 시작으로 맞춘다.</summary>
    public static int Jump(int index, int pageCount, int step, bool coverAlone)
        => Spread(index, pageCount, step, coverAlone).Start;

    /// <summary>jumpPage(number): 1..N clamp 후 0-based로 jump.</summary>
    public static int JumpPage(int pageNumber, int pageCount, int step, bool coverAlone)
    {
        if (pageCount <= 0) return 0;
        var n = Math.Clamp(pageNumber, 1, pageCount);
        return Jump(n - 1, pageCount, step, coverAlone);
    }

    /// <summary>끝 펼침(nextStart 없음)이면 완독.</summary>
    public static bool IsLastSpread(int index, int pageCount, int step, bool coverAlone)
        => pageCount > 0 && NextStart(index, pageCount, step, coverAlone) is null;

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
}
