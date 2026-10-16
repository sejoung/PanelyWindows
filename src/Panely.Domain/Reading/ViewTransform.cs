using Panely.Domain.Model;

namespace Panely.Domain.Reading;

/// <summary>
/// 문서 좌표 ↔ 뷰포트 DIP 변환. 번역·클릭·드래그·확대 중심이 같은 변환을 쓴다.
/// view = origin + (doc - scroll) * zoom. 문서가 뷰포트보다 작으면 여분의 절반만큼 중앙 정렬.
/// </summary>
public readonly record struct ViewTransform(double Zoom, double ScrollX, double ScrollY, double OriginX, double OriginY)
{
    public PointD DocToView(PointD doc) => new(OriginX + (doc.X - ScrollX) * Zoom, OriginY + (doc.Y - ScrollY) * Zoom);

    public PointD ViewToDoc(PointD view) => new((view.X - OriginX) / Zoom + ScrollX, (view.Y - OriginY) / Zoom + ScrollY);

    public RectD DocToView(RectD r)
    {
        var p = DocToView(new PointD(r.X, r.Y));
        return new RectD(p.X, p.Y, r.Width * Zoom, r.Height * Zoom);
    }

    public RectD ViewToDoc(RectD r)
    {
        var p = ViewToDoc(new PointD(r.X, r.Y));
        return new RectD(p.X, p.Y, r.Width / Zoom, r.Height / Zoom);
    }

    /// <summary>스크롤 clamp와 중앙 정렬 origin 계산.</summary>
    public static ViewTransform Create(SizeD document, SizeD viewport, double zoom, double scrollX, double scrollY)
    {
        if (zoom <= 0 || !double.IsFinite(zoom)) zoom = 1;
        var contentW = document.Width * zoom;
        var contentH = document.Height * zoom;
        double originX = 0, originY = 0;
        if (contentW <= viewport.Width) { originX = (viewport.Width - contentW) / 2; scrollX = 0; }
        else scrollX = Math.Clamp(scrollX, 0, document.Width - viewport.Width / zoom);
        if (contentH <= viewport.Height) { originY = (viewport.Height - contentH) / 2; scrollY = 0; }
        else scrollY = Math.Clamp(scrollY, 0, document.Height - viewport.Height / zoom);
        return new ViewTransform(zoom, scrollX, scrollY, originX, originY);
    }

    /// <summary>뷰포트 상의 anchor 점(문서 위치)을 고정한 채 새 배율로 바꿀 때의 스크롤(clamp 전).</summary>
    public static (double ScrollX, double ScrollY) ScrollKeepingAnchor(ViewTransform old, PointD anchorView, double newZoom, SizeD document, SizeD viewport)
    {
        var doc = old.ViewToDoc(anchorView);
        var contentW = document.Width * newZoom;
        var contentH = document.Height * newZoom;
        var originX = contentW <= viewport.Width ? (viewport.Width - contentW) / 2 : 0;
        var originY = contentH <= viewport.Height ? (viewport.Height - contentH) / 2 : 0;
        return (doc.X - (anchorView.X - originX) / newZoom, doc.Y - (anchorView.Y - originY) / newZoom);
    }
}
