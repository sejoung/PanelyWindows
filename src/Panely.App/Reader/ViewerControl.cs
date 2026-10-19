using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Panely.App.Imaging;
using Panely.App.Integration;
using Panely.Core.Localization;
using Panely.Core.Reader;
using Panely.Domain.Model;
using Panely.Domain.Reading;
using PageRange = Panely.Domain.Model.PageRange;

namespace Panely.App.Reader;

/// <summary>
/// 리더 렌더 표면(03). 이미지 픽셀(문서 좌표)과 화면 DIP를 ViewTransform 하나로 변환한다.
/// 보이는 쪽만 그리고(가상화), 번역 오버레이는 이미지 위 별도 레이어에 페이지 경계 clip 없이 그린다.
/// </summary>
public sealed class ViewerControl : FrameworkElement
{
    private static readonly SizeD DefaultPageSize = new(1000, 1500);
    private static readonly TimeSpan CommitFallback = TimeSpan.FromMilliseconds(400);

    private ReaderSession? _session;
    private PageImageCache? _cache;
    private ITranslationFeature? _feature;
    private Func<bool> _wheelPageTurnEnabled = () => true;
    private Func<bool> _sizeLocked = () => false;

    private readonly WheelPageTurnEngine _wheelEngine = new();
    private readonly WheelInputAdapter _wheelAdapter = new();
    private readonly Dictionary<int, DrawingGroup> _overlayCache = new();
    private readonly DispatcherTimer _commitTimer;
    private readonly Brush _bg;
    private readonly Brush _placeholder;
    private readonly Brush _errorFill;
    private readonly Brush _textSecondary;
    private readonly Brush _textPrimary;

    private DocumentLayout _layout = DocumentLayout.Empty;
    private PageRange _committed;
    private PageRange _verticalVisible;
    private double[] _verticalTops = [];
    private double[] _verticalHeights = [];
    private double _scrollX;
    private double _scrollY;
    private bool _syncingFromScroll;
    private bool _dragging;
    private Point _dragStart;
    private double _dragScrollX;
    private double _dragScrollY;
    private bool _pendingBookFit;

    public ViewerControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        IsManipulationEnabled = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        _commitTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = CommitFallback };
        _commitTimer.Tick += (_, _) => { _commitTimer.Stop(); TryCommitSpread(force: true); };
        _bg = Frozen(Color.FromRgb(0x0F, 0x11, 0x15));
        _placeholder = Frozen(Color.FromRgb(0x17, 0x1A, 0x20));
        _errorFill = Frozen(Color.FromRgb(0x2A, 0x2E, 0x36));
        _textSecondary = Frozen(Color.FromRgb(0xAA, 0xB2, 0xC0));
        _textPrimary = Frozen(Color.FromRgb(0xE6, 0xEA, 0xF2));
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public ZoomController Zoom { get; } = new();

    /// <summary>배율·맞춤 상태가 바뀜(툴바 갱신).</summary>
    public event EventHandler? ViewChanged;

    /// <summary>화면에 반영된 쪽 범위가 바뀜(이미지 교체와 함께).</summary>
    public event EventHandler? VisiblePagesCommitted;

    /// <summary>우클릭(뷰어 좌표).</summary>
    public event Action<Point>? ContextMenuRequested;

    /// <summary>휠 forward/backward(권 경계 포함, 세션이 처리).</summary>
    public event Action<bool>? WheelTurnRequested;

    public ViewTransform Transform => ViewTransform.Create(_layout.Size, ViewportSize, Zoom.Zoom, _scrollX, _scrollY);

    public SizeD ViewportSize => new(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));

    public DocumentLayout Layout => _layout;

    private bool IsVertical => _session?.Layout == ReadingLayout.Vertical;

    /// <summary>화면에 반영된 논리 쪽 범위.</summary>
    public PageRange CommittedRange => IsVertical ? _verticalVisible : _committed;

    public void Attach(ReaderSession session, PageImageCache cache, ITranslationFeature feature, Func<bool> wheelPageTurnEnabled, Func<bool> sizeLocked)
    {
        _session = session;
        _cache = cache;
        _feature = feature;
        _wheelPageTurnEnabled = wheelPageTurnEnabled;
        _sizeLocked = sizeLocked;
        cache.ImagesChanged += OnImagesChanged;
        cache.SizesChanged += OnSizesChanged;
    }

    public void SetFeature(ITranslationFeature feature)
    {
        _feature = feature;
        InvalidateOverlay();
    }

    // ───────────────────────── 세션 변경 ─────────────────────────

    public void OnSessionChanged(ReaderChange change)
    {
        if (_session is null || _cache is null) return;

        if (change.HasFlag(ReaderChange.Book))
        {
            _cache.SetBook(_session.Book);
            _overlayCache.Clear();
            _committed = default;
            _verticalVisible = default;
            _layout = DocumentLayout.Empty;
            _scrollX = _scrollY = 0;
            _wheelEngine.Reset();
            _wheelAdapter.Reset();
            Zoom.ResetForNewDocument(_session.FitMode);
            _pendingBookFit = _session.Book is not null;
            if (_session.Book is null)
            {
                InvalidateVisual();
                VisiblePagesCommitted?.Invoke(this, EventArgs.Empty);
                ViewChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        if (_session.Book is null) return;

        if (change.HasFlag(ReaderChange.Layout) || change.HasFlag(ReaderChange.Cover) || change.HasFlag(ReaderChange.Book))
        {
            _wheelEngine.Reset();
            _committed = default;
            _overlayCache.Clear();
            if (IsVertical)
            {
                _cache.EnsureSizes(Enumerable.Range(0, _session.PageCount));
                RebuildVerticalLayout(preserveAnchor: false);
                ScrollToPage(_session.PageIndex);
                RequestVerticalImages(initial: true);
                UpdateBaseline(force: _pendingBookFit ? null : false);
                ScrollToPage(_session.PageIndex);
                VisiblePagesCommitted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                RequestSpreadImages();
                TryCommitSpread(force: false);
            }
        }
        else if (change.HasFlag(ReaderChange.Direction) && !IsVertical)
        {
            RebuildPageLayout();
            UpdateBaseline(force: false);
        }
        else if (change.HasFlag(ReaderChange.Page))
        {
            if (IsVertical)
            {
                if (!_syncingFromScroll)
                {
                    ScrollToPage(_session.PageIndex);
                    RequestVerticalImages(initial: false);
                }
            }
            else
            {
                RequestSpreadImages();
                TryCommitSpread(force: false);
            }
        }

        if (change.HasFlag(ReaderChange.Fit) && !change.HasFlag(ReaderChange.Book))
            SelectFit(_session.FitMode);

        InvalidateVisual();
    }

    private void RequestSpreadImages()
    {
        if (_session is null || _cache is null) return;
        var spread = _session.Spread;
        var n = _session.PageCount;
        var priority = new List<int>();
        priority.AddRange(spread.Indices());
        AddNeighbors(priority, spread, n);
        var keep = new PageRange(Math.Max(0, spread.Start - 10), Math.Min(n, spread.End + 10));
        _cache.Request(priority, keep);
    }

    /// <summary>인접 프리로드: 읽는 방향으로 4쪽(두 쪽 보기면 두 펼침), 뒤로 2쪽. 가까운 쪽·앞쪽 우선.</summary>
    private static void AddNeighbors(List<int> priority, PageRange range, int n)
    {
        const int Ahead = 4, Behind = 2;
        for (var d = 1; d <= Ahead; d++)
        {
            if (range.End - 1 + d < n) priority.Add(range.End - 1 + d);
            if (d <= Behind && range.Start - d >= 0) priority.Add(range.Start - d);
        }
    }

    private void RequestVerticalImages(bool initial)
    {
        if (_session is null || _cache is null) return;
        var n = _session.PageCount;
        var priority = new List<int>();
        PageRange keep;
        if (initial || _verticalVisible.IsEmpty)
        {
            var c = _session.PageIndex;
            priority.Add(c);
            for (var d = 1; d <= 3; d++)
            {
                if (c + d < n) priority.Add(c + d);
                if (c - d >= 0) priority.Add(c - d);
            }
            keep = new PageRange(Math.Max(0, c - 10), Math.Min(n, c + 11));
        }
        else
        {
            priority.AddRange(_verticalVisible.Indices());
            AddNeighbors(priority, _verticalVisible, n);
            keep = new PageRange(Math.Max(0, _verticalVisible.Start - 10), Math.Min(n, _verticalVisible.End + 10));
        }
        _cache.Request(priority, keep);
    }

    /// <summary>페이지 모드: 새 펼침의 decode가 끝나면 이미지와 committed 쪽을 함께 교체한다.</summary>
    private void TryCommitSpread(bool force)
    {
        if (_session is null || _cache is null || IsVertical || _session.Book is null) return;
        var target = _session.Spread;
        if (target == _committed && !force && !_layout.FramesInDisplayOrder.Count.Equals(0)) return;
        var ready = target.Indices().All(_cache.IsReady);
        if (!ready && !force)
        {
            if (!_commitTimer.IsEnabled) _commitTimer.Start();
            return;
        }
        _commitTimer.Stop();
        var changed = target != _committed;
        _committed = target;
        RebuildPageLayout();
        if (changed)
        {
            _overlayCache.Clear();
            _scrollY = 0;
            _scrollX = _session.EffectiveDirection == ReadingDirection.RightToLeft ? double.MaxValue : 0;
        }
        UpdateBaseline(force: _pendingBookFit ? null : false);
        InvalidateVisual();
        if (changed) VisiblePagesCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void OnImagesChanged(IReadOnlyCollection<int> indices)
    {
        if (_session is null) return;
        if (!IsVertical)
        {
            if (_session.Spread != _committed) TryCommitSpread(force: false);
            else if (indices.Any(_committed.Contains))
            {
                // 크기가 바뀌었을 수 있다(placeholder → 실제)
                var before = _layout.Size;
                RebuildPageLayout();
                if (before != _layout.Size) UpdateBaseline(force: _pendingBookFit ? null : false);
            }
        }
        InvalidateVisual();
    }

    private void OnSizesChanged(IReadOnlyCollection<int> indices)
    {
        if (_session is null) return;
        if (IsVertical)
        {
            RebuildVerticalLayout(preserveAnchor: true);
            UpdateBaseline(force: _pendingBookFit ? null : false);
            UpdateVerticalCurrentPage();
        }
        else if (indices.Any(_committed.Contains))
        {
            RebuildPageLayout();
            UpdateBaseline(force: _pendingBookFit ? null : false);
        }
        InvalidateVisual();
    }

    // ───────────────────────── 문서 배치 ─────────────────────────

    private SizeD SizeOf(int index, SizeD fallback)
    {
        var s = _cache?.GetSize(index);
        return s is { Width: > 0, Height: > 0 } ? s.Value : fallback;
    }

    private void RebuildPageLayout()
    {
        if (_session is null) return;
        var indices = _committed.Indices().ToList();
        if (indices.Count == 0) { _layout = DocumentLayout.Empty; return; }
        var known = indices.Select(i => _cache?.GetSize(i)).FirstOrDefault(s => s is { Width: > 0 }) ?? DefaultPageSize;
        var pages = indices.Select(i => (i, SizeOf(i, known))).ToList();
        _layout = PageLayoutCalculator.Horizontal(pages, _session.EffectiveDirection);
    }

    private void RebuildVerticalLayout(bool preserveAnchor)
    {
        if (_session is null) return;
        var n = _session.PageCount;
        if (n == 0) { _layout = DocumentLayout.Empty; return; }

        // 앵커: 현재 쪽 안의 상대 위치를 유지
        int anchorPage = _session.PageIndex;
        double anchorOffset = 0;
        if (preserveAnchor && _verticalTops.Length == n)
        {
            var t = Transform;
            anchorPage = VerticalStrip.PageAt(_verticalTops, t.ScrollY);
            anchorOffset = t.ScrollY - _verticalTops[anchorPage];
        }

        double knownWidth = 0;
        for (var i = 0; i < n; i++)
            if (_cache?.GetSize(i) is { Width: > 0 } s) knownWidth = Math.Max(knownWidth, s.Width);
        var fallback = knownWidth > 0 ? new SizeD(knownWidth, knownWidth * 1.5) : DefaultPageSize;
        var sizes = new SizeD[n];
        for (var i = 0; i < n; i++) sizes[i] = SizeOf(i, fallback);
        _layout = PageLayoutCalculator.Vertical(sizes);
        _verticalHeights = sizes.Select(s => s.Height).ToArray();
        _verticalTops = VerticalStrip.Tops(_verticalHeights);

        if (preserveAnchor && anchorPage < n)
            _scrollY = _verticalTops[anchorPage] + Math.Min(anchorOffset, _verticalHeights[anchorPage]);
    }

    /// <summary>
    /// 새 baseline 계산. force=null은 새 책 최초 맞춤: 시리즈 세션 배수가 있으면 적용, 없으면 force.
    /// </summary>
    private void UpdateBaseline(bool? force)
    {
        if (_session is null || _layout.Size.IsEmpty) return;
        var baseline = FitCalculator.Fit(_layout.FitReference, ViewportSize, Zoom.FitMode);
        if (force is null && !FitReferenceKnown)
        {
            // 실제 치수를 알기 전에는 임시 맞춤만 하고 최초 맞춤(시리즈 배수)은 미룬다
            Zoom.ApplyBaseline(baseline, force: true, _sizeLocked());
        }
        else if (force is null)
        {
            var factor = _session.Book is { } b ? _session.ZoomMemory.Recall(b.SeriesKey) : null;
            Zoom.ApplyBaseline(baseline, force: factor is null, _sizeLocked(), factor);
            _pendingBookFit = false;
        }
        else
        {
            Zoom.ApplyBaseline(baseline, force.Value, _sizeLocked());
        }
        ClampScroll();
        if (IsVertical) UpdateVerticalCurrentPage();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool FitReferenceKnown => _cache is not null && (IsVertical
        ? _cache.GetSize(0) is not null
        : !_committed.IsEmpty && _committed.Indices().All(i => _cache.GetSize(i) is not null || _cache.IsFailed(i)));

    private void ClampScroll()
    {
        var t = Transform;
        _scrollX = t.ScrollX;
        _scrollY = t.ScrollY;
    }

    /// <summary>맞춤 선택(같은 모드 재선택도 baseline 복귀).</summary>
    public void SelectFit(FitMode mode)
    {
        if (_layout.Size.IsEmpty) { Zoom.ResetForNewDocument(mode); return; }
        var anchorPage = IsVertical && _verticalTops.Length > 0 ? VerticalStrip.PageAt(_verticalTops, Transform.ScrollY) : -1;
        var baseline = FitCalculator.Fit(_layout.FitReference, ViewportSize, mode);
        Zoom.SelectFit(mode, baseline);
        if (IsVertical && anchorPage >= 0) _scrollY = _verticalTops[anchorPage];
        else _scrollY = 0;
        ClampScroll();
        if (IsVertical) UpdateVerticalCurrentPage();
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomIn() => SetZoomAnchored(ZoomPolicy.ZoomIn(Zoom.Zoom), Center);
    public void ZoomOut() => SetZoomAnchored(ZoomPolicy.ZoomOut(Zoom.Zoom), Center);
    public void ResetZoom() => SetZoomAnchored(Zoom.Baseline, Center);

    private Point Center => new(ActualWidth / 2, ActualHeight / 2);

    public void SetZoomAnchored(double newZoom, Point anchor)
    {
        if (_layout.Size.IsEmpty) return;
        newZoom = ZoomPolicy.Clamp(newZoom);
        var t = Transform;
        var (sx, sy) = ViewTransform.ScrollKeepingAnchor(t, new PointD(anchor.X, anchor.Y), newZoom, _layout.Size, ViewportSize);
        Zoom.SetZoom(newZoom);
        _scrollX = sx;
        _scrollY = sy;
        ClampScroll();
        if (IsVertical) AfterVerticalScroll();
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>뷰포트 DIP 단위로 pan.</summary>
    public void PanBy(double dxDip, double dyDip)
    {
        if (_layout.Size.IsEmpty) return;
        var z = Zoom.Zoom;
        var t = Transform;
        _scrollX = t.ScrollX + dxDip / z;
        _scrollY = t.ScrollY + dyDip / z;
        ClampScroll();
        if (IsVertical) AfterVerticalScroll();
        InvalidateVisual();
    }

    /// <summary>vertical: 대상 쪽 시작 y로 이동하되 수평 pan은 보존.</summary>
    public void ScrollToPage(int index)
    {
        if (!IsVertical || _verticalTops.Length == 0) return;
        index = Math.Clamp(index, 0, _verticalTops.Length - 1);
        _scrollY = _verticalTops[index];
        ClampScroll();
        UpdateVisibleRange();
        InvalidateVisual();
    }

    public bool CanPanDown
    {
        get
        {
            var t = Transform;
            return _layout.Size.Height * Zoom.Zoom > ViewportSize.Height + 0.5 && t.ScrollY < _layout.Size.Height - ViewportSize.Height / Zoom.Zoom - 1;
        }
    }

    public bool CanPanUp => Transform.ScrollY > 1;

    private void AfterVerticalScroll()
    {
        UpdateVerticalCurrentPage();
        RequestVerticalImages(initial: false);
    }

    private void UpdateVisibleRange()
    {
        if (_verticalTops.Length == 0) return;
        var t = Transform;
        var top = t.ScrollY;
        var bottom = top + ViewportSize.Height / Zoom.Zoom;
        var range = VerticalStrip.VisibleRange(_verticalTops, top, Math.Min(bottom, _layout.Size.Height));
        if (range != _verticalVisible)
        {
            _verticalVisible = range;
            VisiblePagesCommitted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateVerticalCurrentPage()
    {
        if (_session is null || _verticalHeights.Length == 0) return;
        UpdateVisibleRange();
        var t = Transform;
        var top = t.ScrollY;
        var viewH = ViewportSize.Height / Zoom.Zoom;
        double vTop, vBottom;
        if (_layout.Size.Height <= viewH) { vTop = 0; vBottom = _layout.Size.Height; }
        else { vTop = top; vBottom = top + viewH; }
        var current = VerticalStrip.CurrentPage(_verticalHeights, vTop, vBottom);
        if (current != _session.PageIndex)
        {
            _syncingFromScroll = true;
            try { _session.SetCurrentPageFromScroll(current); }
            finally { _syncingFromScroll = false; }
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_layout.Size.IsEmpty) return;
        if (IsVertical && _verticalTops.Length > 0)
        {
            var t = Transform;
            var anchor = VerticalStrip.PageAt(_verticalTops, t.ScrollY);
            var offset = t.ScrollY - _verticalTops[anchor];
            UpdateBaseline(force: false);
            _scrollY = _verticalTops[anchor] + offset;
            ClampScroll();
            UpdateVerticalCurrentPage();
        }
        else
        {
            UpdateBaseline(force: false);
        }
        InvalidateVisual();
    }

    // ───────────────────────── 그리기 ─────────────────────────

    public void InvalidateOverlay()
    {
        _overlayCache.Clear();
        InvalidateVisual();
    }

    private IEnumerable<PageFrame> FramesToDraw()
    {
        if (!IsVertical)
        {
            foreach (var f in _layout.FramesInDisplayOrder) yield return f;
            yield break;
        }
        if (_verticalVisible.IsEmpty) UpdateVisibleRange();
        var start = Math.Max(0, _verticalVisible.Start - 1);
        var end = Math.Min(_layout.FramesInDisplayOrder.Count, _verticalVisible.End + 1);
        for (var i = start; i < end; i++) yield return _layout.FramesInDisplayOrder[i];
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(_bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_session?.Book is null || _cache is null || _layout.Size.IsEmpty) return;

        var t = Transform;
        var frames = FramesToDraw().ToList();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var frame in frames)
        {
            var r = t.DocToView(frame.Rect);
            var rect = new Rect(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));
            var image = _cache.GetImage(frame.LogicalIndex);
            if (image is not null)
            {
                dc.DrawImage(image, rect);
            }
            else if (_cache.IsFailed(frame.LogicalIndex))
            {
                DrawErrorTile(dc, rect, frame.LogicalIndex, dpi);
            }
            else
            {
                dc.DrawRectangle(_placeholder, null, rect);
            }
        }

        if (_feature is { IsEnabled: true })
        {
            foreach (var frame in frames)
            {
                var size = _cache.GetSize(frame.LogicalIndex);
                if (size is not { Width: > 0, Height: > 0 } px) continue;
                var group = OverlayFor(frame.LogicalIndex, px);
                if (group is null) continue;
                var r = t.DocToView(frame.Rect);
                var scale = r.Width / px.Width;
                dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, r.X, r.Y));
                dc.DrawDrawing(group);
                dc.Pop();
            }
        }
    }

    private DrawingGroup? OverlayFor(int index, SizeD px)
    {
        if (_overlayCache.TryGetValue(index, out var g)) return g.Children.Count == 0 ? null : g;
        g = new DrawingGroup();
        using (var ctx = g.Open())
        {
            try { _feature!.RenderPageOverlay(ctx, index, px); }
            catch (Exception) { /* 오버레이 오류가 읽기를 막지 않는다 */ }
        }
        g.Freeze();
        _overlayCache[index] = g;
        return g.Children.Count == 0 ? null : g;
    }

    private void DrawErrorTile(DrawingContext dc, Rect rect, int index, double dpi)
    {
        dc.DrawRectangle(_errorFill, null, rect);
        var name = _session?.Book is { } b && index < b.Pages.Count ? b.Pages[index].FileName : string.Empty;
        var title = new FormattedText(Loc.T("Couldn't read this page."), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 14, _textPrimary, dpi)
        { MaxTextWidth = Math.Max(10, rect.Width - 24), TextAlignment = TextAlignment.Center };
        var sub = new FormattedText($"{Loc.T("Page %lld", index + 1)} · {name}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, _textSecondary, dpi)
        { MaxTextWidth = Math.Max(10, rect.Width - 24), TextAlignment = TextAlignment.Center, Trimming = TextTrimming.CharacterEllipsis };
        var total = title.Height + 6 + sub.Height;
        var y = rect.Y + (rect.Height - total) / 2;
        dc.DrawText(title, new Point(rect.X + 12, y));
        dc.DrawText(sub, new Point(rect.X + 12, y + title.Height + 6));
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        => new PointHitTestResult(this, hitTestParameters.HitPoint);

    // ───────────────────────── 좌표(IReaderHost) ─────────────────────────

    public (int PageIndex, Point PagePx)? HitTestPage(Point viewPoint)
    {
        if (_cache is null || _layout.Size.IsEmpty) return null;
        var doc = Transform.ViewToDoc(new PointD(viewPoint.X, viewPoint.Y));
        foreach (var frame in FramesToDraw())
        {
            if (!frame.Rect.Contains(doc.X, doc.Y)) continue;
            var px = _cache.GetSize(frame.LogicalIndex) ?? new SizeD(frame.Rect.Width, frame.Rect.Height);
            var s = px.Width / frame.Rect.Width;
            return (frame.LogicalIndex, new Point((doc.X - frame.Rect.X) * s, (doc.Y - frame.Rect.Y) * s));
        }
        return null;
    }

    public Rect? PageRectToView(int pageIndex, RectD pagePx)
    {
        if (_cache is null) return null;
        var frame = _layout.FrameFor(pageIndex);
        if (frame is null) return null;
        if (!IsVertical && !_committed.Contains(pageIndex)) return null;
        var px = _cache.GetSize(pageIndex) ?? new SizeD(frame.Rect.Width, frame.Rect.Height);
        var s = frame.Rect.Width / px.Width;
        var doc = new RectD(frame.Rect.X + pagePx.X * s, frame.Rect.Y + pagePx.Y * s, pagePx.Width * s, pagePx.Height * s);
        var v = Transform.DocToView(doc);
        return new Rect(v.X, v.Y, Math.Max(0, v.Width), Math.Max(0, v.Height));
    }

    // ───────────────────────── 입력 ─────────────────────────

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        if (_session?.Book is null || _layout.Size.IsEmpty) return;
        var raw = e.Delta;
        var mods = Keyboard.Modifiers;

        // Ctrl-zoom(정밀 터치패드 핀치도 Ctrl+wheel로 들어온다)가 우선
        if (mods.HasFlag(ModifierKeys.Control))
        {
            var target = ZoomPolicy.ApplyContinuousDelta(Zoom.Zoom, WheelInputAdapter.ZoomDelta(raw));
            SetZoomAnchored(target, e.GetPosition(this));
            return;
        }

        if (mods.HasFlag(ModifierKeys.Shift))
        {
            PanBy(WheelInputAdapter.ScrollDip(raw), 0);
            return;
        }

        if (IsVertical || !_wheelPageTurnEnabled())
        {
            PanBy(0, WheelInputAdapter.ScrollDip(raw));
            return;
        }

        var input = _wheelAdapter.ToEngineInput(raw, TimeSpan.FromMilliseconds(e.Timestamp), atForwardEdge: !CanPanDown, atBackwardEdge: !CanPanUp);
        switch (_wheelEngine.Process(input))
        {
            case WheelOutcome.Pan:
                PanBy(0, WheelInputAdapter.ScrollDip(raw));
                break;
            case WheelOutcome.TurnForward:
                WheelTurnRequested?.Invoke(true);
                break;
            case WheelOutcome.TurnBackward:
                WheelTurnRequested?.Invoke(false);
                break;
        }
    }

    /// <summary>WM_MOUSEHWHEEL(가로 휠/터치패드 가로 스크롤).</summary>
    public void OnHorizontalWheel(int raw)
    {
        if (_session?.Book is null) return;
        PanBy(raw / (double)WheelInputAdapter.WheelDelta * WheelInputAdapter.DipPerNotch, 0);
    }

    /// <summary>레이아웃·휠 옵션이 바뀌면 상태 머신 초기화.</summary>
    public void ResetWheelState()
    {
        _wheelEngine.Reset();
        _wheelAdapter.Reset();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        if (_feature is { IsEnabled: true } f && f.OnPointerPressed(p, MouseButton.Left, e.ClickCount))
        {
            e.Handled = true;
            InvalidateVisual();
            return;
        }
        if (_session?.Book is null) return;
        if (e.ClickCount == 2)
        {
            SetZoomAnchored(ZoomPolicy.DoubleClick(Zoom.Baseline, Zoom.Zoom), p);
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragStart = p;
        var t = Transform;
        _dragScrollX = t.ScrollX;
        _dragScrollY = t.ScrollY;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_dragging)
        {
            var z = Zoom.Zoom;
            _scrollX = _dragScrollX - (p.X - _dragStart.X) / z;
            _scrollY = _dragScrollY - (p.Y - _dragStart.Y) / z;
            ClampScroll();
            if (IsVertical) AfterVerticalScroll();
            Cursor = Cursors.SizeAll;
            InvalidateVisual();
            return;
        }
        if (_feature is { IsEnabled: true } f && f.OnPointerMoved(p, e.LeftButton == MouseButtonState.Pressed))
            InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            _dragging = false;
            Cursor = null;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (_feature is { IsEnabled: true } f && f.OnPointerReleased(e.GetPosition(this), MouseButton.Left))
        {
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var p = e.GetPosition(this);
        if (_feature is { IsEnabled: true } f && f.OnPointerReleased(p, MouseButton.Right))
        {
            e.Handled = true;
            return;
        }
        ContextMenuRequested?.Invoke(p);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
        Cursor = null;
    }

    // 터치스크린 핀치/드래그
    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = this;
        e.Mode = ManipulationModes.Scale | ManipulationModes.Translate;
        e.Handled = true;
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        var d = e.DeltaManipulation;
        var scale = (d.Scale.X + d.Scale.Y) / 2;
        if (Math.Abs(scale - 1) > 0.001) SetZoomAnchored(Zoom.Zoom * scale, e.ManipulationOrigin);
        if (d.Translation.Length > 0) PanBy(-d.Translation.X, -d.Translation.Y);
        e.Handled = true;
    }
}
