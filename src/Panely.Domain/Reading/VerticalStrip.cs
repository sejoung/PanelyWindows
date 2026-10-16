using Panely.Domain.Model;

namespace Panely.Domain.Reading;

public static class VerticalStrip
{
    public const double EdgeEpsilon = 0.5;

    public static double[] Tops(IReadOnlyList<double> heights)
    {
        var tops = new double[heights.Count];
        double y = 0;
        for (var i = 0; i < heights.Count; i++) { tops[i] = y; y += heights[i]; }
        return tops;
    }

    /// <summary>뷰포트 중심 Y를 포함하는 쪽. 맨 위에만 닿으면 0, 맨 아래에만 닿으면 N-1.</summary>
    public static int CurrentPage(IReadOnlyList<double> heights, double viewportTop, double viewportBottom)
    {
        var n = heights.Count;
        if (n == 0) return 0;
        var total = heights.Sum();
        var atTop = viewportTop <= EdgeEpsilon;
        var atBottom = viewportBottom >= total - EdgeEpsilon;
        if (atTop && !atBottom) return 0;
        if (atBottom && !atTop) return n - 1;
        return PageAt(Tops(heights), (viewportTop + viewportBottom) / 2);
    }

    /// <summary>pageTops 이진 탐색: tops[i] &lt;= y 인 마지막 i.</summary>
    public static int PageAt(IReadOnlyList<double> tops, double y)
    {
        var n = tops.Count;
        if (n == 0) return 0;
        int lo = 0, hi = n - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (tops[mid] <= y) lo = mid; else hi = mid - 1;
        }
        return Math.Clamp(lo, 0, n - 1);
    }

    /// <summary>뷰포트 상단/하단이 걸치는 쪽까지 포함한 가시 범위.</summary>
    public static PageRange VisibleRange(IReadOnlyList<double> tops, double viewportTop, double viewportBottom)
    {
        var n = tops.Count;
        if (n == 0) return new PageRange(0, 0);
        var first = PageAt(tops, Math.Max(0, viewportTop));
        var last = PageAt(tops, Math.Max(0, viewportBottom - 0.0001));
        return new PageRange(first, Math.Min(n, Math.Max(first, last) + 1));
    }
}
