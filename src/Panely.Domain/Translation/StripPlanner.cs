using Panely.Domain.Model;

namespace Panely.Domain.Translation;

/// <summary>한 strip OCR 단위. OwnedStart..OwnedEnd에 중심 Y가 있는 풍선만 이 band에서 채택한다.</summary>
public sealed record StripBand(int Index, int Top, int Height, double OwnedStart, double OwnedEnd, IReadOnlyList<int> PageIndices)
{
    public bool Owns(double y) => y >= OwnedStart && y < OwnedEnd;
}

/// <summary>
/// vertical strip OCR 계획(06 vertical strip OCR). 짧은 이미지들을 읽기 화면과 같은 maxWidth·수평 중앙·연속 y 공간에 합친다.
/// band 크기는 OCR 타일 규칙과 같다. ownedRange는 strip을 빠짐·겹침 없이 분할한다.
/// </summary>
public sealed class StripPlan
{
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<RectD> PageRects { get; }
    public IReadOnlyList<StripBand> Bands { get; }

    private StripPlan(int width, int height, IReadOnlyList<RectD> pageRects, IReadOnlyList<StripBand> bands)
    {
        Width = width; Height = height; PageRects = pageRects; Bands = bands;
    }

    public static StripPlan Create(IReadOnlyList<SizeD> pageSizes)
    {
        if (pageSizes.Count == 0) return new StripPlan(0, 0, [], []);
        var width = (int)Math.Round(pageSizes.Max(s => s.Width));
        var rects = new List<RectD>(pageSizes.Count);
        double y = 0;
        foreach (var s in pageSizes)
        {
            rects.Add(new RectD((width - s.Width) / 2, y, s.Width, s.Height));
            y += s.Height;
        }
        var height = (int)Math.Round(y);
        var tiles = TilePlanner.OcrTiles(width, height);
        var overlap = tiles.Count > 1 ? TilePlanner.OcrOverlap(TilePlanner.OcrTileHeight(width)) : 0;
        var bands = new List<StripBand>(tiles.Count);
        for (var i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            double lower = i == 0 ? 0 : t.Top + overlap / 2.0;
            double upper = i == tiles.Count - 1 ? height : t.Top + t.Height - overlap / 2.0;
            var pages = new List<int>();
            for (var p = 0; p < rects.Count; p++)
            {
                var r = rects[p];
                if (r.Top < upper && r.Bottom > lower) pages.Add(p);
            }
            bands.Add(new StripBand(i, t.Top, t.Height, lower, upper, pages));
        }
        return new StripPlan(width, height, rects, bands);
    }

    /// <summary>band가 실제로 덮는(이미지를 그려야 하는) 페이지.</summary>
    public IReadOnlyList<int> PagesIntersecting(StripBand band)
    {
        var list = new List<int>();
        for (var p = 0; p < PageRects.Count; p++)
            if (PageRects[p].Top < band.Top + band.Height && PageRects[p].Bottom > band.Top) list.Add(p);
        return list;
    }

    /// <summary>strip y를 포함하는 페이지(마지막 페이지로 clamp).</summary>
    public int PageAtY(double y)
    {
        for (var p = 0; p < PageRects.Count; p++)
            if (y < PageRects[p].Bottom) return p;
        return Math.Max(0, PageRects.Count - 1);
    }

    /// <summary>완료된 band 집합에서 모든 담당 unit이 끝난 페이지.</summary>
    public IReadOnlyList<int> CompletedPages(IReadOnlySet<int> completedBands)
    {
        var done = new List<int>();
        for (var p = 0; p < PageRects.Count; p++)
        {
            var units = Bands.Where(b => b.PageIndices.Contains(p)).ToList();
            if (units.Count > 0 && units.All(b => completedBands.Contains(b.Index))) done.Add(p);
        }
        return done;
    }
}
