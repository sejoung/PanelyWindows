using Panely.Domain.Model;

namespace Panely.Domain.Translation;

/// <summary>patch 계산 입력(표시할 한 풍선).</summary>
public sealed record PatchSource(
    int Id,
    RectD Rect,
    double GlyphSize,
    bool IsVertical,
    BubbleBackground Background,
    RectD? BalloonRect,
    string Text);

/// <summary>그릴 patch 한 개. 뷰어와 내보내기가 같은 결과를 그린다.</summary>
public sealed record PatchLayout(
    RectD Rect,
    IReadOnlyList<int> BubbleIds,
    string Text,
    BubbleBackground Background,
    double GlyphSize,
    bool IsVertical,
    IReadOnlyList<RectD> HitAreas,
    double CornerRadius,
    /// <summary>artwork 배경일 때 feather 반경, flat이면 0.</summary>
    double Feather,
    double InnerPadding,
    double MinFontSize,
    double MaxFontSize)
{
    public bool IsFlat => Background.IsUniform;
    /// <summary>artwork 위 patch 불투명도.</summary>
    public double Opacity => IsFlat ? 1.0 : PatchGeometry.ArtworkAlpha;
}

/// <summary>번역 patch geometry(06 번역 패치 렌더링).</summary>
public static class PatchGeometry
{
    public const double PaddingFraction = 0.3;
    public const double BalloonInteriorShare = 0.66;
    public const double ArtworkAlpha = 0.92;
    public const double MinFont = 6;
    public const double FontPrecision = 0.5;
    public const double BalloonInkSafeRatio = 0.03;

    /// <summary>
    /// 기본 patch = 원문 rect + .3glyph padding. flat+balloonRect면 balloon 중앙 66%와 union,
    /// 아니고 세로 글이면 최소 폭 min(patchHeight*.9, glyph*9)까지 중앙 확장. 폭은 페이지로 clip, 높이는 clip하지 않는다.
    /// </summary>
    public static RectD PatchRect(RectD rect, double glyph, bool isVertical, bool isUniform, RectD? balloonRect, double pageWidth)
    {
        var pad = PaddingFraction * glyph;
        var r = rect.Inflate(pad, pad);
        if (isUniform && balloonRect is { } b && !b.IsEmpty)
        {
            var w = b.Width * BalloonInteriorShare;
            var h = b.Height * BalloonInteriorShare;
            r = r.Union(new RectD(b.CenterX - w / 2, b.CenterY - h / 2, w, h));
        }
        else if (isVertical)
        {
            var minWidth = Math.Min(r.Height * 0.9, glyph * 9);
            if (r.Width < minWidth) r = new RectD(r.CenterX - minWidth / 2, r.Y, minWidth, r.Height);
        }
        if (pageWidth > 0)
        {
            var left = Math.Max(0, r.Left);
            var right = Math.Min(pageWidth, r.Right);
            if (right > left) r = RectD.FromEdges(left, r.Top, right, r.Bottom);
        }
        return r;
    }

    public static double CornerRadius(double glyph) => Math.Max(4, 0.5 * glyph);

    public static double FeatherRadius(double glyph) => Math.Max(6, 0.8 * glyph);

    public static double MaxFontSize(double glyph) => Math.Max(0.95 * glyph, 8);

    /// <summary>
    /// 페이지의 표시 풍선들로 patch를 만든다. 원문 rect가 겹치면 한 patch(ID순 줄바꿈),
    /// padding만 겹치면 원문 사이 중간선까지 확장을 줄인다(원문 자체는 자르지 않음).
    /// </summary>
    public static List<PatchLayout> Layout(IReadOnlyList<PatchSource> sources, double pageWidth)
    {
        var items = sources.Where(s => !s.Rect.IsEmpty).OrderBy(s => s.Id).ToList();
        var n = items.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                if (items[i].Rect.Intersects(items[j].Rect)) parent[Find(i)] = Find(j);

        var groups = Enumerable.Range(0, n).GroupBy(Find).Select(g => g.Select(i => items[i]).OrderBy(s => s.Id).ToList()).ToList();

        var originals = new List<RectD>();
        var patches = new List<RectD>();
        foreach (var g in groups)
        {
            var original = g.Aggregate(new RectD(0, 0, 0, 0), (acc, s) => acc.Union(s.Rect));
            var patch = g.Aggregate(new RectD(0, 0, 0, 0), (acc, s) =>
                acc.Union(PatchRect(s.Rect, s.GlyphSize, s.IsVertical, s.Background.IsUniform, s.BalloonRect, pageWidth)));
            originals.Add(original);
            patches.Add(patch);
        }

        // padding만 겹치는 이웃 patch를 중간선으로 줄인다
        for (var i = 0; i < patches.Count; i++)
        {
            for (var j = i + 1; j < patches.Count; j++)
            {
                if (!patches[i].Intersects(patches[j])) continue;
                (patches[i], patches[j]) = ShrinkToMidline(patches[i], originals[i], patches[j], originals[j]);
            }
        }

        var result = new List<PatchLayout>(groups.Count);
        for (var k = 0; k < groups.Count; k++)
        {
            var g = groups[k];
            var first = g[0];
            var glyph = g.Min(s => s.GlyphSize);
            var uniform = g.All(s => s.Background.IsUniform);
            var bg = uniform ? first.Background : first.Background with { IsUniform = false };
            var hit = g.Select(s => s.Rect.Inflate(PaddingFraction * s.GlyphSize, PaddingFraction * s.GlyphSize)).ToList();
            var text = string.Join("\n", g.Select(s => s.Text).Where(t => !string.IsNullOrEmpty(t)));
            result.Add(new PatchLayout(
                patches[k], g.Select(s => s.Id).ToList(), text, bg, glyph, first.IsVertical, hit,
                CornerRadius(glyph), uniform ? 0 : FeatherRadius(glyph), PaddingFraction * glyph,
                MinFont, MaxFontSize(glyph)));
        }
        return result;
    }

    private static (RectD, RectD) ShrinkToMidline(RectD pa, RectD oa, RectD pb, RectD ob)
    {
        var gapX = Math.Max(oa.Left, ob.Left) - Math.Min(oa.Right, ob.Right);
        var gapY = Math.Max(oa.Top, ob.Top) - Math.Min(oa.Bottom, ob.Bottom);
        if (gapX >= gapY)
        {
            var aLeft = oa.CenterX <= ob.CenterX;
            var (lp, lo, rp, ro) = aLeft ? (pa, oa, pb, ob) : (pb, ob, pa, oa);
            var mid = (lo.Right + ro.Left) / 2;
            var newL = RectD.FromEdges(lp.Left, lp.Top, Math.Max(lo.Right, Math.Min(lp.Right, mid)), lp.Bottom);
            var newR = RectD.FromEdges(Math.Min(ro.Left, Math.Max(rp.Left, mid)), rp.Top, rp.Right, rp.Bottom);
            return aLeft ? (newL, newR) : (newR, newL);
        }
        else
        {
            var aTop = oa.CenterY <= ob.CenterY;
            var (tp, to, bp, bo) = aTop ? (pa, oa, pb, ob) : (pb, ob, pa, oa);
            var mid = (to.Bottom + bo.Top) / 2;
            var newT = RectD.FromEdges(tp.Left, tp.Top, tp.Right, Math.Max(to.Bottom, Math.Min(tp.Bottom, mid)));
            var newB = RectD.FromEdges(bp.Left, Math.Min(bo.Top, Math.Max(bp.Top, mid)), bp.Right, bp.Bottom);
            return aTop ? (newT, newB) : (newB, newT);
        }
    }

    /// <summary>
    /// 들어가는 최대 글자 크기를 이진 탐색한다(정밀도 .5px). fits(size)는 렌더러가 실제 측정한다.
    /// </summary>
    public static double FitFontSize(double min, double max, Func<double, bool> fits)
    {
        if (max < min) max = min;
        if (fits(max)) return max;
        if (!fits(min)) return min;
        double lo = min, hi = max;
        while (hi - lo > FontPrecision)
        {
            var mid = (lo + hi) / 2;
            if (fits(mid)) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary>strip 풍선이 다음 slice로 넘친 부분: dx=(현재W-이전W)/2, dy=-이전H.</summary>
    public static PatchLayout ShiftToNextSlice(PatchLayout patch, double previousWidth, double previousHeight, double currentWidth)
    {
        var dx = (currentWidth - previousWidth) / 2;
        var dy = -previousHeight;
        return patch with
        {
            Rect = patch.Rect.Offset(dx, dy),
            HitAreas = patch.HitAreas.Select(h => h.Offset(dx, dy)).ToList(),
        };
    }
}
