using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Panely.App.Controls;

/// <summary>회전 호 스피너. 애니메이션 감소 설정이면 정지 표시.</summary>
public sealed class Spinner : FrameworkElement
{
    private readonly RotateTransform _rotate = new();

    public Spinner()
    {
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _rotate;
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        if (IsVisible && SystemParameters.ClientAreaAnimation)
        {
            var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
            _rotate.BeginAnimation(RotateTransform.AngleProperty, anim);
        }
        else
        {
            _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var r = size / 2 - 1.5;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var track = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xAA, 0xB2, 0xC0)), 2);
        dc.DrawEllipse(null, track, c, r, r);
        var accent = (Brush)(TryFindResource("Brush.Accent") ?? Brushes.DodgerBlue);
        var fig = new PathFigure { StartPoint = new Point(c.X, c.Y - r) };
        fig.Segments.Add(new ArcSegment(new Point(c.X + r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        dc.DrawGeometry(null, new Pen(accent, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new PathGeometry([fig]));
    }
}
