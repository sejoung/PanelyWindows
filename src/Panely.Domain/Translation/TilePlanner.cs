using Panely.Domain.Model;

namespace Panely.Domain.Translation;

/// <summary>
/// 긴 이미지의 OCR 타일과 탐지 청크 계획(06-translation.md 탐지·OCR).
/// 두 계획은 서로 다르다: OCR은 tileHeight=max(2400, floor(W*3/2)), 탐지는 chunkHeight=max(W*3/2, 640).
/// </summary>
public static class TilePlanner
{
    public const int OcrMinTileHeight = 2400;
    public const int OcrSeamMargin = 6;
    public const int DetectorMinChunk = 640;
    public const double DuplicateOverlap = 0.5;

    public static int OcrTileHeight(int width) => Math.Max(OcrMinTileHeight, (int)Math.Floor(width * 3 / 2.0));

    public static int OcrOverlap(int tileHeight) => (int)Math.Floor(tileHeight * 5 / 12.0);

    /// <summary>OCR 타일. H &gt; max(tileHeight, 2W)일 때만 분할하고 마지막 band는 남은 높이로 끝까지 덮는다.</summary>
    public static IReadOnlyList<Band> OcrTiles(int width, int height)
    {
        if (height <= 0) return [];
        var tile = OcrTileHeight(width);
        if (height <= Math.Max(tile, 2 * width)) return [new Band(0, height)];
        var overlap = OcrOverlap(tile);
        return Split(height, tile, tile - overlap);
    }

    /// <summary>탐지 청크. H &gt; chunk×4/3일 때 overlap=chunk/3으로 자른다.</summary>
    public static IReadOnlyList<Band> DetectorChunks(int width, int height)
    {
        if (height <= 0) return [];
        var chunk = Math.Max(width * 3 / 2, DetectorMinChunk);
        if (height <= chunk * 4 / 3) return [new Band(0, height)];
        var overlap = chunk / 3;
        return Split(height, chunk, chunk - overlap);
    }

    private static List<Band> Split(int height, int tile, int step)
    {
        var bands = new List<Band>();
        if (step <= 0) step = tile;
        for (var top = 0; ; top += step)
        {
            if (top + tile >= height)
            {
                bands.Add(new Band(top, height - top));
                break;
            }
            bands.Add(new Band(top, tile));
        }
        return bands;
    }

    /// <summary>처음/마지막이 아닌 seam에서 margin 이내로 닿는 rect인가(band 로컬 좌표).</summary>
    public static bool TouchesInteriorSeam(RectD rectInBand, Band band, int imageHeight, double margin)
    {
        var touchesTop = band.Top > 0 && rectInBand.Top <= margin;
        var touchesBottom = band.Bottom < imageHeight && rectInBand.Bottom >= band.Height - margin;
        return touchesTop || touchesBottom;
    }

    /// <summary>타일 간 OCR 중복 제거: 작은 면적 대비 교집합&gt;.5면 텍스트가 긴 것을 남긴다.</summary>
    public static List<TextRegion> DedupeOcr(IEnumerable<TextRegion> regions)
    {
        var result = new List<TextRegion>();
        foreach (var r in regions)
        {
            var dup = result.FindIndex(e => e.Rect.OverlapOfSmaller(r.Rect) > DuplicateOverlap);
            if (dup < 0) { result.Add(r); continue; }
            if (r.Text.Length > result[dup].Text.Length) result[dup] = r;
        }
        return result;
    }

    /// <summary>
    /// 같은 종류의 두 rect가 작은 면적 대비 교집합&gt;.5면 score 높은 것을 남긴다.
    /// 말풍선 안 글자와 밖 글자는 같은 종류(글자 상자)로 본다. 탐지기가 같은 상자를 두 class로 함께 내면
    /// 둘 다 읽혀 원문이 두 번 이어 붙는다(예: "男の悦びを男の悦びを").
    /// </summary>
    public static List<DetectedRegion> DedupeDetections(IEnumerable<DetectedRegion> regions)
    {
        var result = new List<DetectedRegion>();
        foreach (var r in regions.OrderByDescending(d => d.Score))
        {
            if (result.Any(e => e.IsTextBox == r.IsTextBox && e.Rect.OverlapOfSmaller(r.Rect) > DuplicateOverlap)) continue;
            result.Add(r);
        }
        return result;
    }
}
