using Panely.Domain.Model;

namespace Panely.Domain.Reading;

public sealed record PageFrame(int LogicalIndex, RectD Rect);

public sealed record DocumentLayout(SizeD Size, IReadOnlyList<PageFrame> FramesInDisplayOrder, SizeD FitReference)
{
    public static readonly DocumentLayout Empty = new(new SizeD(0, 0), [], new SizeD(0, 0));

    public PageFrame? FrameFor(int logicalIndex)
    {
        foreach (var f in FramesInDisplayOrder)
            if (f.LogicalIndex == logicalIndex) return f;
        return null;
    }
}

/// <summary>이미지 픽셀 기준 문서 배치(EXIF 회전 적용 크기).</summary>
public static class PageLayoutCalculator
{
    /// <summary>single/double: 공통 높이 H=max(h), 폭은 비율대로, 간격 0. RTL은 화면 순서만 뒤집는다.</summary>
    public static DocumentLayout Horizontal(IReadOnlyList<(int Index, SizeD Size)> pages, ReadingDirection direction)
    {
        if (pages.Count == 0) return DocumentLayout.Empty;
        var h = pages.Max(p => p.Size.Height > 0 ? p.Size.Height : 0);
        if (h <= 0) h = 1;
        var ordered = direction == ReadingDirection.RightToLeft ? pages.Reverse().ToList() : pages.ToList();
        var frames = new List<PageFrame>(ordered.Count);
        double x = 0;
        foreach (var (index, size) in ordered)
        {
            var w = size.Height > 0 ? size.Width * h / size.Height : size.Width;
            if (w <= 0) w = h / 1.5;
            frames.Add(new PageFrame(index, new RectD(x, 0, w, h)));
            x += w;
        }
        var docSize = new SizeD(x, h);
        return new DocumentLayout(docSize, frames, docSize);
    }

    /// <summary>vertical: W=max(wi), 각 페이지 원본 크기로 수평 중앙, 간격 0. fit 기준은 첫 페이지 rect.</summary>
    public static DocumentLayout Vertical(IReadOnlyList<SizeD> sizes)
    {
        if (sizes.Count == 0) return DocumentLayout.Empty;
        var w = sizes.Max(s => s.Width);
        var frames = new List<PageFrame>(sizes.Count);
        double y = 0;
        for (var i = 0; i < sizes.Count; i++)
        {
            var s = sizes[i];
            frames.Add(new PageFrame(i, new RectD((w - s.Width) / 2, y, s.Width, s.Height)));
            y += s.Height;
        }
        return new DocumentLayout(new SizeD(w, y), frames, sizes[0]);
    }
}
