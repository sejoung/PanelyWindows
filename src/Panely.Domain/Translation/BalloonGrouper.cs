using Panely.Domain.Model;

namespace Panely.Domain.Translation;

/// <summary>한 풍선(번역 단위). Lines는 읽기 순서.</summary>
public sealed record BalloonGroup(
    int Id,
    RectD Rect,
    string Text,
    bool IsVertical,
    double GlyphSize,
    RectD? BalloonRect,
    IReadOnlyList<TextRegion> Lines);

/// <summary>
/// OCR 영역을 풍선으로 묶는다(06 풍선 그룹과 필터).
/// 탐지 결과가 있으면 container(텍스트 박스/풍선)로, 없으면 보수적인 연결 요소로 묶는다. 다른 container는 섞지 않는다.
/// </summary>
public static class BalloonGrouper
{
    public const double CoverageThreshold = 0.5;

    public static List<BalloonGroup> Group(IReadOnlyList<TextRegion> rawRegions, IReadOnlyList<DetectedRegion>? detections, string language)
    {
        // 정규화와 잡음 제거
        var regions = new List<TextRegion>();
        foreach (var r in rawRegions)
        {
            var text = Scripts.Normalize(r.Text);
            if (TranslationLanguages.IsCjkVertical(language)) text = Scripts.RemoveCjkSpaces(text);
            if (text.Length == 0 || !Scripts.HasOwnScript(text, language) || Scripts.LooksLikeMixedGarbage(text, language) || Scripts.IsLoneParticle(text, language)) continue;
            if (r.Rect.IsEmpty) continue;
            regions.Add(r with { Text = text });
        }

        var groups = new List<(List<TextRegion> Lines, RectD? Balloon)>();
        var unassigned = new List<TextRegion>();

        if (detections is { Count: > 0 })
        {
            var textBoxes = detections.Where(d => d.IsTextBox).ToList();
            var balloons = detections.Where(d => d.Kind == DetectionKind.Balloon).ToList();
            var byContainer = new Dictionary<DetectedRegion, List<TextRegion>>(ReferenceEqualityComparer.Instance);
            foreach (var region in regions)
            {
                var container = FindContainer(region, textBoxes, balloons);
                if (container is null) { unassigned.Add(region); continue; }
                if (!byContainer.TryGetValue(container, out var list)) byContainer[container] = list = [];
                list.Add(region);
            }
            foreach (var (container, lines) in byContainer)
            {
                RectD? balloonRect = container.Kind == DetectionKind.Balloon ? container.Rect : null;
                groups.Add((lines, balloonRect));
            }
        }
        else
        {
            unassigned.AddRange(regions);
        }

        foreach (var component in ConnectedComponents(unassigned))
            groups.Add((component, null));

        var built = new List<BalloonGroup>();
        var joiner = Scripts.LineJoiner(language);
        var cjk = TranslationLanguages.IsCjkVertical(language);
        foreach (var (lines, balloonRect) in groups)
        {
            var ordered = OrderLines(lines, cjk);
            var rect = ordered.Aggregate(new RectD(0, 0, 0, 0), (acc, l) => acc.Union(l.Rect));
            var verticalCount = ordered.Count(l => l.IsVertical);
            var glyphs = ordered.Select(l => l.GlyphSize).Where(g => g > 0).OrderBy(g => g).ToList();
            var glyph = glyphs.Count > 0 ? glyphs[glyphs.Count / 2] : Math.Max(8, Math.Min(rect.Width, rect.Height));
            var text = string.Join(joiner, ordered.Select(l => l.Text)).Trim();
            if (text.Length == 0) continue;
            built.Add(new BalloonGroup(0, rect, text, verticalCount * 2 > ordered.Count, glyph, balloonRect, ordered));
        }

        return AssignIds(built);
    }

    private static DetectedRegion? FindContainer(TextRegion region, List<DetectedRegion> textBoxes, List<DetectedRegion> balloons)
    {
        DetectedRegion? best = null;
        double bestCoverage = 0;
        foreach (var tb in textBoxes)
        {
            var coverage = region.Rect.CoveredBy(tb.Rect);
            if (coverage >= CoverageThreshold && coverage > bestCoverage) { best = tb; bestCoverage = coverage; }
        }
        if (best is not null)
        {
            if (best.Kind == DetectionKind.FreeText) return best;
            return SmallestCovering(best.Rect, balloons) ?? best;
        }
        return SmallestCovering(region.Rect, balloons);
    }

    private static DetectedRegion? SmallestCovering(RectD rect, List<DetectedRegion> balloons)
    {
        DetectedRegion? best = null;
        foreach (var b in balloons)
        {
            if (rect.CoveredBy(b.Rect) < CoverageThreshold) continue;
            if (best is null || b.Rect.Area < best.Rect.Area) best = b;
        }
        return best;
    }

    /// <summary>탐지 없는 영역의 보수적 연결 요소(같은 방향만).</summary>
    public static List<List<TextRegion>> ConnectedComponents(IReadOnlyList<TextRegion> regions)
    {
        var n = regions.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                if (ShouldJoin(regions[i], regions[j])) parent[Find(i)] = Find(j);

        return Enumerable.Range(0, n).GroupBy(Find).Select(g => g.Select(i => regions[i]).ToList()).ToList();
    }

    public static bool ShouldJoin(TextRegion a, TextRegion b)
    {
        if (a.IsVertical != b.IsVertical) return false;
        var glyph = Math.Max(1, Math.Min(a.GlyphSize, b.GlyphSize));
        var ra = a.Rect; var rb = b.Rect;
        var gapX = Math.Max(ra.Left, rb.Left) - Math.Min(ra.Right, rb.Right);
        var gapY = Math.Max(ra.Top, rb.Top) - Math.Min(ra.Bottom, rb.Bottom);
        var overlapY = Math.Min(ra.Bottom, rb.Bottom) - Math.Max(ra.Top, rb.Top);
        var overlapX = Math.Min(ra.Right, rb.Right) - Math.Max(ra.Left, rb.Left);
        if (a.IsVertical)
        {
            if (gapX < 1.3 * glyph && overlapY > 0.3 * Math.Min(ra.Height, rb.Height) && Math.Abs(ra.Top - rb.Top) < 2.5 * glyph)
                return true;
            // 한 열이 두 조각으로 잘린 경우: 같은 열(가로 겹침 큼) + 세로로 거의 붙음
            return overlapX > 0.5 * Math.Min(ra.Width, rb.Width) && gapY < 1.0 * glyph;
        }
        return gapY < 0.8 * glyph && overlapX > 0.5 * Math.Min(ra.Width, rb.Width);
    }

    /// <summary>
    /// 풍선 내부 순서: 높이 겹침&gt;.3이면 일본어·중국어/세로 글자는 오른쪽 열부터, 그 외 왼쪽부터. 겹치지 않으면 위부터.
    /// 비이행적 비교라 안정 삽입 정렬을 쓴다.
    /// </summary>
    public static List<TextRegion> OrderLines(IReadOnlyList<TextRegion> lines, bool cjkLanguage)
    {
        var result = new List<TextRegion>(lines.Count);
        foreach (var line in lines.OrderBy(l => l.Rect.Top))
        {
            var index = result.Count;
            while (index > 0 && CompareLines(line, result[index - 1], cjkLanguage) < 0) index--;
            result.Insert(index, line);
        }
        return result;
    }

    private static int CompareLines(TextRegion a, TextRegion b, bool cjkLanguage)
    {
        var overlap = Math.Min(a.Rect.Bottom, b.Rect.Bottom) - Math.Max(a.Rect.Top, b.Rect.Top);
        var smaller = Math.Min(a.Rect.Height, b.Rect.Height);
        if (smaller > 0 && overlap > 0.3 * smaller)
        {
            var rightFirst = cjkLanguage || a.IsVertical || b.IsVertical;
            return rightFirst ? b.Rect.CenterX.CompareTo(a.Rect.CenterX) : a.Rect.CenterX.CompareTo(b.Rect.CenterX);
        }
        return a.Rect.Top.CompareTo(b.Rect.Top);
    }

    /// <summary>풍선 전체 ID: top 오름차순, top 차≤1이면 centerX 내림차순.</summary>
    public static List<BalloonGroup> AssignIds(IReadOnlyList<BalloonGroup> groups)
    {
        var ordered = new List<BalloonGroup>(groups.Count);
        foreach (var g in groups.OrderBy(g => g.Rect.Top).ThenByDescending(g => g.Rect.CenterX))
        {
            var index = ordered.Count;
            while (index > 0 && Precedes(g, ordered[index - 1])) index--;
            ordered.Insert(index, g);
        }
        return ordered.Select((g, i) => g with { Id = i }).ToList();
    }

    private static bool Precedes(BalloonGroup a, BalloonGroup b)
    {
        if (Math.Abs(a.Rect.Top - b.Rect.Top) <= 1) return a.Rect.CenterX > b.Rect.CenterX;
        return a.Rect.Top < b.Rect.Top;
    }
}
