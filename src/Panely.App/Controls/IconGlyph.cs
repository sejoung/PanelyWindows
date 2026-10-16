using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Panely.App.Controls;

/// <summary>
/// 16×16 좌표계 벡터 아이콘. 윤곽선 14 DIP 계열(기본), Filled면 채움(활성 변형).
/// 색은 상속된 Foreground(TextElement.Foreground)를 따른다. SF Symbols 대신 자체 path를 사용한다.
/// </summary>
public sealed class IconGlyph : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(Geometry), typeof(IconGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(IconGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(IconGlyph), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(IconGlyph),
        new FrameworkPropertyMetadata(1.4, FrameworkPropertyMetadataOptions.AffectsRender));

    public IconGlyph()
    {
        Width = 14;
        Height = 14;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    public Geometry? Glyph { get => (Geometry?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var g = Glyph;
        if (g is null) return;
        var scale = Math.Min(ActualWidth, ActualHeight) / 16.0;
        if (scale <= 0) return;
        var ox = (ActualWidth - 16 * scale) / 2;
        var oy = (ActualHeight - 16 * scale) / 2;
        var pen = new Pen(Foreground, StrokeThickness / scale) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, ox, oy));
        dc.DrawGeometry(Filled ? Foreground : null, pen, g);
        dc.Pop();
    }
}

/// <summary>앱 아이콘 path(16×16). 의미: 02-design.md 아이콘 표.</summary>
public static class Icons
{
    private static Geometry G(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    public static readonly Geometry Folder = G("M1.5,4 L1.5,13 L14.5,13 L14.5,5 L7.5,5 L6,3 L1.5,3 Z");
    public static readonly Geometry Archive = G("M3.5,1.5 L12.5,1.5 L12.5,14.5 L3.5,14.5 Z M7,3 L9,3 M7,5 L9,5 M7,7 L9,7 M6.8,9 L9.2,9 L9.2,11.5 L6.8,11.5 Z");
    public static readonly Geometry Pin = G("M6,1.5 L10,1.5 L9.5,6 L12,9 L4,9 L6.5,6 Z M8,9 L8,14.5");
    public static readonly Geometry PinToolbar = G("M2,2 L14,2 L14,14 L2,14 Z M6.5,4 L9.5,4 L9,7 L10.5,9 L5.5,9 L7,7 Z M8,9 L8,12");
    public static readonly Geometry Single = G("M4,1.5 L12,1.5 L12,14.5 L4,14.5 Z");
    public static readonly Geometry Double = G("M1.5,2.5 L14.5,2.5 L14.5,13.5 L1.5,13.5 Z M8,2.5 L8,13.5");
    public static readonly Geometry Vertical = G("M3,6 L13,6 L13,14 L3,14 Z M4,3.8 L12,3.8 M5,1.8 L11,1.8");
    public static readonly Geometry ArrowRight = G("M2,8 L14,8 M10,4 L14,8 L10,12");
    public static readonly Geometry ArrowLeft = G("M14,8 L2,8 M6,4 L2,8 L6,12");
    public static readonly Geometry Cover = G("M3,1.5 L11,1.5 L13,3.5 L13,14.5 L3,14.5 Z M5.5,5 L10.5,5 M5.5,7.5 L10.5,7.5 M5.5,10 L10.5,10 M5.5,12.5 L8.5,12.5");
    public static readonly Geometry FitScreen = G("M2,2 L6.5,6.5 M2,2 L2,6 M2,2 L6,2 M14,14 L9.5,9.5 M14,14 L14,10 M14,14 L10,14");
    public static readonly Geometry FitWidth = G("M1.5,8 L14.5,8 M4.5,5 L1.5,8 L4.5,11 M11.5,5 L14.5,8 L11.5,11");
    public static readonly Geometry FitHeight = G("M8,1.5 L8,14.5 M5,4.5 L8,1.5 L11,4.5 M5,11.5 L8,14.5 L11,11.5");
    public static readonly Geometry ZoomIn = G("M6.8,1.8 A5,5 0 1 1 6.79,1.8 Z M10.4,10.4 L14.5,14.5 M4.5,6.8 L9.1,6.8 M6.8,4.5 L6.8,9.1");
    public static readonly Geometry ZoomOut = G("M6.8,1.8 A5,5 0 1 1 6.79,1.8 Z M10.4,10.4 L14.5,14.5 M4.5,6.8 L9.1,6.8");
    public static readonly Geometry Unlock = G("M2,8 L10,8 L10,14.5 L2,14.5 Z M8,8 L8,4.5 A3,3 0 0 1 14,4.5 L14,6");
    public static readonly Geometry Lock = G("M3.5,7.5 L12.5,7.5 L12.5,14.5 L3.5,14.5 Z M5.5,7.5 L5.5,4.5 A2.5,2.5 0 0 1 10.5,4.5 L10.5,7.5");
    public static readonly Geometry Star = G("M8,1.5 L9.9,5.6 L14.3,6.1 L11,9.1 L11.9,13.5 L8,11.3 L4.1,13.5 L5,9.1 L1.7,6.1 L6.1,5.6 Z");
    public static readonly Geometry Bookmark = G("M4,1.5 L12,1.5 L12,14.5 L8,11 L4,14.5 Z");
    public static readonly Geometry BookmarkList = G("M1.5,2.5 L14.5,2.5 L14.5,13.5 L1.5,13.5 Z M4,5.5 L4.6,5.5 M6.5,5.5 L12,5.5 M4,8 L4.6,8 M6.5,8 L12,8 M4,10.5 L4.6,10.5 M6.5,10.5 L12,10.5");
    public static readonly Geometry Thumbnails = G("M4,4 L12,4 L12,14.5 L4,14.5 Z M5,2.2 L11,2.2 M6.5,0.8 L9.5,0.8");
    public static readonly Geometry Translate = G("M1.5,3 L8.5,3 M5,1.5 L5,3 M7,3 C6.5,6 4.5,8.5 2,10 M3.5,5.5 C4.5,7.5 6,8.8 8,9.5 M8.5,14.5 L11.25,7 L14,14.5 M9.4,12 L13.1,12");
    public static readonly Geometry ChevronLeft = G("M10,3 L5,8 L10,13");
    public static readonly Geometry ChevronRight = G("M6,3 L11,8 L6,13");
    public static readonly Geometry ChevronDown = G("M3,6 L8,11 L13,6");
    public static readonly Geometry DoubleChevronLeft = G("M8,3 L3,8 L8,13 M13,3 L8,8 L13,13");
    public static readonly Geometry DoubleChevronRight = G("M3,3 L8,8 L3,13 M8,3 L13,8 L8,13");
    public static readonly Geometry Refresh = G("M13.5,8 A5.5,5.5 0 1 1 11.9,4.1 M12.5,1.5 L12.5,4.6 L9.4,4.6");
    public static readonly Geometry Library = G("M1.5,2 L4,2 L4,14 L1.5,14 Z M5.5,2 L8,2 L8,14 L5.5,14 Z M9.5,3 L12,2.3 L14.6,13.3 L12.1,14 Z");
    public static readonly Geometry Trash = G("M2,4 L14,4 M6,4 L6,2 L10,2 L10,4 M3.5,4 L4.5,14.5 L11.5,14.5 L12.5,4 M6.5,7 L6.5,12 M9.5,7 L9.5,12");
    public static readonly Geometry Close = G("M3.5,3.5 L12.5,12.5 M12.5,3.5 L3.5,12.5");
    public static readonly Geometry Check = G("M2.5,8.5 L6.5,12.5 L13.5,4");
    public static readonly Geometry More = G("M3,8 L3.1,8 M8,8 L8.1,8 M13,8 L13.1,8");
    public static readonly Geometry Settings = G("M8,5.5 A2.5,2.5 0 1 1 7.99,5.5 Z M8,1 L9,3 L11,2.3 L11.5,4.5 L13.7,5 L13,7 L15,8 L13,9 L13.7,11 L11.5,11.5 L11,13.7 L9,13 L8,15 L7,13 L5,13.7 L4.5,11.5 L2.3,11 L3,9 L1,8 L3,7 L2.3,5 L4.5,4.5 L5,2.3 L7,3 Z");
    public static readonly Geometry Jobs = G("M5,3.5 L14.5,3.5 M5,8 L14.5,8 M5,12.5 L14.5,12.5 M1.5,3.5 L2,3.5 M1.5,8 L2,8 M1.5,12.5 L2,12.5");
    public static readonly Geometry Warning = G("M8,1.5 L15,14 L1,14 Z M8,6 L8,9.5 M8,11.8 L8,12");
    public static readonly Geometry Info = G("M8,1.5 A6.5,6.5 0 1 1 7.99,1.5 Z M8,7 L8,11.5 M8,4.6 L8,4.8");
    public static readonly Geometry FileImage = G("M3,1.5 L10,1.5 L13,4.5 L13,14.5 L3,14.5 Z M5,12 L7.5,8.5 L9,10.5 L10,9.3 L11.5,12 Z");
}
