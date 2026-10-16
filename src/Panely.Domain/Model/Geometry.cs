namespace Panely.Domain.Model;

/// <summary>좌상단 원점 사각형. 이미지 픽셀 또는 문서 좌표.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public double Area => Width > 0 && Height > 0 ? Width * Height : 0;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectD FromEdges(double left, double top, double right, double bottom)
        => new(left, top, right - left, bottom - top);

    public RectD Intersect(RectD o)
    {
        var l = Math.Max(Left, o.Left);
        var t = Math.Max(Top, o.Top);
        var r = Math.Min(Right, o.Right);
        var b = Math.Min(Bottom, o.Bottom);
        return r > l && b > t ? FromEdges(l, t, r, b) : new RectD(l, t, 0, 0);
    }

    public bool Intersects(RectD o) => !Intersect(o).IsEmpty;

    public RectD Union(RectD o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        return FromEdges(Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));
    }

    public RectD Inflate(double dx, double dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);

    public RectD Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    /// <summary>작은 면적 대비 교집합 비율.</summary>
    public double OverlapOfSmaller(RectD o)
    {
        var smaller = Math.Min(Area, o.Area);
        return smaller <= 0 ? 0 : Intersect(o).Area / smaller;
    }

    /// <summary>이 사각형 면적 중 o가 덮는 비율.</summary>
    public double CoveredBy(RectD o) => Area <= 0 ? 0 : Intersect(o).Area / Area;
}

public readonly record struct SizeD(double Width, double Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct PointD(double X, double Y);

/// <summary>반열린 구간 [Start, End).</summary>
public readonly record struct PageRange(int Start, int End)
{
    public int Length => Math.Max(0, End - Start);
    public bool IsEmpty => End <= Start;
    public bool Contains(int index) => index >= Start && index < End;
    public IEnumerable<int> Indices() { for (var i = Start; i < End; i++) yield return i; }
}
