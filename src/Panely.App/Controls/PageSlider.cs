using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Panely.Core.Commands;
using Panely.Core.Localization;
using Panely.Core.Reader;
using PageRange = Panely.Domain.Model.PageRange;

namespace Panely.App.Controls;

/// <summary>
/// 페이지 트랙(02): 높이 2, thumb 지름 10, hit 높이 20. bgTertiary 트랙·accent 채움·textPrimary thumb.
/// 0..N-1 논리 인덱스, 드래그 중 반올림, 끝에서 정수 스냅. RTL에서도 왼쪽→오른쪽. 번역 완료 쪽은 아래 높이 2 마크.
/// RangeBase라서 UI Automation RangeValue를 제공한다. 키보드 Home/End/화살표.
/// </summary>
public sealed class PageTrack : RangeBase
{
    private bool _dragging;
    private IReadOnlySet<int> _marks = new HashSet<int>();

    public PageTrack()
    {
        Height = 20;
        Focusable = true;
        Cursor = Cursors.Hand;
        Minimum = 0;
        SmallChange = 1;
        LargeChange = 10;
        FocusVisualStyle = (Style)Application.Current.FindResource("Panely.FocusVisual");
        AutomationProperties.SetName(this, "Page");
    }

    /// <summary>사용자 조작으로 정수 인덱스가 바뀜(드래그 중 포함).</summary>
    public event Action<int>? PageRequested;

    /// <summary>드래그 종료.</summary>
    public event Action? DragCompleted;

    public bool IsDragging => _dragging;

    public void SetMarks(IReadOnlySet<int> marks)
    {
        _marks = marks;
        InvalidateVisual();
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        InvalidateVisual();
    }

    protected override void OnMaximumChanged(double oldMaximum, double newMaximum)
    {
        base.OnMaximumChanged(oldMaximum, newMaximum);
        InvalidateVisual();
    }

    private const double ThumbRadius = 5;

    private double XFor(double value)
    {
        var w = ActualWidth - ThumbRadius * 2;
        return Maximum <= 0 ? ThumbRadius : ThumbRadius + w * (value - Minimum) / (Maximum - Minimum);
    }

    private double ValueFor(double x)
    {
        var w = Math.Max(1, ActualWidth - ThumbRadius * 2);
        var v = (x - ThumbRadius) / w * (Maximum - Minimum) + Minimum;
        return Math.Clamp(v, Minimum, Maximum);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var track = (Brush)FindResource("Brush.BgTertiary");
        var accent = (Brush)FindResource("Brush.Accent");
        var thumb = (Brush)FindResource("Brush.TextPrimary");
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var cy = ActualHeight / 2;
        dc.DrawRoundedRectangle(track, null, new Rect(ThumbRadius, cy - 1, Math.Max(0, ActualWidth - ThumbRadius * 2), 2), 1, 1);
        var x = XFor(Value);
        dc.DrawRoundedRectangle(accent, null, new Rect(ThumbRadius, cy - 1, Math.Max(0, x - ThumbRadius), 2), 1, 1);
        if (_marks.Count > 0 && Maximum >= 0)
        {
            var n = (int)Maximum + 1;
            var segment = Math.Max(1, (ActualWidth - ThumbRadius * 2) / Math.Max(1, n));
            foreach (var i in _marks)
            {
                if (i < 0 || i >= n) continue;
                var mx = n == 1 ? ThumbRadius : XFor(i);
                dc.DrawRectangle(accent, null, new Rect(mx - segment / 2, cy + 4, Math.Max(1, segment), 2));
            }
        }
        dc.DrawEllipse(thumb, null, new Point(x, cy), ThumbRadius, ThumbRadius);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Maximum <= 0) return;
        _dragging = true;
        CaptureMouse();
        Scrub(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Scrub(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Value = Math.Round(Value);
        PageRequested?.Invoke((int)Value);
        DragCompleted?.Invoke();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
        {
            _dragging = false;
            Value = Math.Round(Value);
            DragCompleted?.Invoke();
        }
    }

    private int _lastRequested = -1;

    private void Scrub(double x)
    {
        Value = ValueFor(x);
        var rounded = (int)Math.Round(Value);
        if (rounded == _lastRequested) return;
        _lastRequested = rounded;
        PageRequested?.Invoke(rounded);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int? target = e.Key switch
        {
            Key.Home => 0,
            Key.End => (int)Maximum,
            Key.Left or Key.Down => (int)Math.Max(0, Value - 1),
            Key.Right or Key.Up => (int)Math.Min(Maximum, Value + 1),
            Key.PageDown => (int)Math.Min(Maximum, Value + LargeChange),
            Key.PageUp => (int)Math.Max(0, Value - LargeChange),
            _ => null,
        };
        if (target is null) return;
        Value = target.Value;
        PageRequested?.Invoke(target.Value);
        e.Handled = true;
    }
}

/// <summary>
/// 하단 슬라이더+카운터(02). 뷰어 좌우 padding 24, 바닥 16. `Vol 1 / 3 · 5-6 / 24`.
/// 앞 번호를 클릭하면 입력창(폭 max(32, digits×9+10)), Enter 확정·Escape/포커스 상실 취소.
/// </summary>
public sealed class PageSlider : Grid
{
    private readonly PageTrack _track = new();
    private readonly TextBlock _volumeText = new();
    private readonly Button _rangeButton;
    private readonly TextBlock _rangeText = new();
    private readonly TextBlock _totalText = new();
    private readonly TextBox _input;
    private int _total;
    private bool _committing;

    public PageSlider()
    {
        Margin = new Thickness(24, 0, 24, 16);
        VerticalAlignment = VerticalAlignment.Bottom;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var counter = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) };
        foreach (var t in new[] { _volumeText, _rangeText, _totalText })
        {
            t.FontSize = 12;
            t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            t.VerticalAlignment = VerticalAlignment.Center;
        }
        _rangeButton = new Button
        {
            Style = (Style)Application.Current.FindResource("Panely.LinkButton"),
            Content = _rangeText,
            Padding = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.IBeam,
        };
        _rangeButton.Click += (_, _) => BeginInput();
        _input = new TextBox
        {
            FontSize = 12,
            Padding = new Thickness(2, 0, 2, 0),
            Visibility = Visibility.Collapsed,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _input.KeyDown += OnInputKeyDown;
        _input.LostKeyboardFocus += (_, _) => { if (!_committing) EndInput(); };
        counter.Children.Add(_volumeText);
        counter.Children.Add(_rangeButton);
        counter.Children.Add(_input);
        counter.Children.Add(_totalText);
        Children.Add(counter);
        SetRow(_track, 1);
        Children.Add(_track);

        _track.PageRequested += i => PageRequested?.Invoke(i);
        _track.DragCompleted += () => FocusReaderRequested?.Invoke();
        Loc.LanguageChanged += (_, _) => UpdateTooltip();
        UpdateTooltip();
    }

    public event Action<int>? PageRequested;
    public event Action<int>? PageNumberEntered;
    public event Action? FocusReaderRequested;

    public bool IsEditing => _input.Visibility == Visibility.Visible;

    private void UpdateTooltip()
    {
        _rangeButton.ToolTip = Loc.T("Click to jump to a page") + (CommandRegistry.GestureText(CommandIds.PageJump) is { } g ? $" ({g})" : string.Empty);
    }

    public void Update(int volumeIndex, int volumeCount, PageRange spread, int total, IReadOnlySet<int> translated)
    {
        _total = total;
        _volumeText.Text = ReaderText.VolumePrefix(volumeIndex, volumeCount);
        _rangeText.Text = ReaderText.PageRangeText(spread);
        _totalText.Text = " / " + total;
        if (!_track.IsDragging)
        {
            _track.Maximum = Math.Max(0, total - 1);
            _track.Value = Math.Clamp(spread.Start, 0, Math.Max(0, total - 1));
        }
        _track.SetMarks(translated);
        AutomationProperties.SetName(_track, Loc.T("Page %lld", spread.Start + 1));
    }

    public void BeginInput()
    {
        if (_total <= 0) return;
        _input.Width = ReaderText.JumpInputWidth(_total);
        _input.Text = (_track.Value + 1).ToString("0");
        _rangeButton.Visibility = Visibility.Collapsed;
        _input.Visibility = Visibility.Visible;
        _input.Focus();
        _input.SelectAll();
    }

    private void EndInput()
    {
        _input.Visibility = Visibility.Collapsed;
        _rangeButton.Visibility = Visibility.Visible;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var n = ReaderText.ParsePageNumber(_input.Text, _total);
            _committing = true;
            EndInput();
            _committing = false;
            if (n is { } page) PageNumberEntered?.Invoke(page);
            FocusReaderRequested?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndInput();
            FocusReaderRequested?.Invoke();
            e.Handled = true;
        }
    }
}
