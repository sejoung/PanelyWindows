using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Panely.Core.Commands;
using Panely.Core.Localization;
using Panely.Core.Translation;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.App.Translation;

public sealed partial class TranslationFeature
{
    private Popup? _popover;
    private ContextMenu? _contextMenu;
    private EditBar? _editBar;
    private (int Page, Point Start)? _dragStart;
    private (int Page, RectD Rect)? _pressedBubble;
    private (int Page, Rect Rect)? _dragRect;
    private WpfPagePixelSource? _localPixels;

    private WpfPagePixelSource LocalPixels => _localPixels ??= new WpfPagePixelSource(_services.Decoder);

    // ───────────────────────── 표시 데이터 ─────────────────────────

    /// <summary>페이지의 표시 풍선(캐시 원본 + 사용자 수정). 데이터가 없으면 null.</summary>
    private (TranslatedPage? Page, List<DisplayBubble> Bubbles, double ImageWidth, double ImageHeight)? Display(int pageIndex, string? target = null)
    {
        if (_book is null || pageIndex < 0 || pageIndex >= _book.Pages.Count) return null;
        target ??= _visible;
        if (target is null) return null;
        var entry = _book.Pages[pageIndex];
        var scope = CurrentScope;
        var edits = Service.Edits.Get(entry.PageId, scope);
        _pages.TryGetValue(pageIndex, out var page);
        if (target != _visible) page = null;
        if (page is null && edits is null) return null;
        double w = page?.ImageWidth ?? 0, h = page?.ImageHeight ?? 0;
        if ((w <= 0 || h <= 0) && _host?.GetPageSize(pageIndex) is { } size) { w = size.Width; h = size.Height; }
        if (w <= 0 || h <= 0) return null;
        var bubbles = EditResolver.Resolve(page?.Bubbles ?? [], edits, target,
            index => _pendingRetranslations.Contains((entry.PageId, index, target)));
        return (page, bubbles, w, h);
    }

    public void RenderPageOverlay(DrawingContext dc, int pageIndex, SizeD pagePixelSize)
    {
        if (!IsOverlayActive) return;
        var display = Display(pageIndex);
        if (display is not { } d) return;
        var scale = d.ImageWidth > 0 && pagePixelSize.Width > 0 ? pagePixelSize.Width / d.ImageWidth : 1;
        var scaled = Math.Abs(scale - 1) > 0.0001;
        if (scaled) dc.PushTransform(new ScaleTransform(scale, scale));
        try
        {
            if (ShowingTranslation || _editing)
            {
                var sources = EditResolver.ToPatchSources(d.Bubbles, includePending: true);
                OverlayPainter.DrawPatches(dc, PatchGeometry.Layout(sources, d.ImageWidth));
            }
            if (_editing)
            {
                OverlayPainter.DrawEditDecorations(dc, d.Bubbles, d.ImageWidth);
                if (_dragRect is { } drag && drag.Page == pageIndex) OverlayPainter.DrawSelection(dc, drag.Rect, d.ImageWidth);
            }
        }
        finally
        {
            if (scaled) dc.Pop();
        }
    }

    /// <summary>view 점 → (페이지, 캐시 이미지 픽셀 좌표). strip에서는 이전 페이지 풍선이 넘친 영역도 찾는다.</summary>
    private (int Page, Point Px)? ToImagePoint(Point view, int? forcePage = null)
    {
        if (_host?.HitTestPage(view) is not { } hit) return null;
        var page = forcePage ?? hit.PageIndex;
        var px = hit.PagePx;
        if (page != hit.PageIndex)
        {
            if (_host.GetPageSize(page) is not { } prev || _host.GetPageSize(hit.PageIndex) is not { } cur) return null;
            px = new Point(px.X + (prev.Width - cur.Width) / 2, px.Y + prev.Height);
        }
        var size = _host.GetPageSize(page);
        if (Display(page) is { } d && size is { Width: > 0 } s && d.ImageWidth > 0)
        {
            var scale = d.ImageWidth / s.Width;
            px = new Point(px.X * scale, px.Y * scale);
        }
        return (page, px);
    }

    private (int Page, DisplayBubble Bubble)? HitBubble(Point view)
    {
        if (_host?.HitTestPage(view) is not { } hit) return null;
        var candidates = new List<int> { hit.PageIndex };
        if (CurrentScope == TranslationScope.Strip && hit.PageIndex > 0) candidates.Add(hit.PageIndex - 1);
        foreach (var page in candidates)
        {
            if (ToImagePoint(view, page) is not { } p || Display(page) is not { } d) continue;
            for (var i = d.Bubbles.Count - 1; i >= 0; i--)
            {
                var b = d.Bubbles[i];
                if (b.IsHidden && !_editing) continue;
                var pad = PatchGeometry.PaddingFraction * b.GlyphSize;
                if (b.Rect.Inflate(pad, pad).Contains(p.Px.X, p.Px.Y)) return (page, b);
            }
        }
        return null;
    }

    // ───────────────────────── 포인터 ─────────────────────────

    public bool OnPointerPressed(Point viewPoint, MouseButton button, int clickCount)
    {
        if (!IsEnabled || _book is null || _host is null || _visible is null) return false;
        if (button != MouseButton.Left) return false;
        var hit = HitBubble(viewPoint);
        if (hit is { } h)
        {
            // StaysOpen=false 팝업을 mouse-down에서 열면 뒤이은 mouse-up이 팝업 밖으로 판정되어 바로 닫힌다.
            // 누른 풍선을 기억했다가 같은 풍선 위에서 놓을 때 연다.
            _pressedBubble = clickCount == 1 ? (h.Page, h.Bubble.Rect) : null;
            return true;
        }
        _pressedBubble = null;
        if (_editing && ToImagePoint(viewPoint) is { } p)
        {
            ClosePopover();
            _dragStart = (p.Page, p.Px);
            _dragRect = (p.Page, new Rect(p.Px, p.Px));
            return true;
        }
        return false;
    }

    public bool OnPointerMoved(Point viewPoint, bool leftButtonDown)
    {
        if (_dragStart is not { } start) return false;
        if (!leftButtonDown) { _dragStart = null; _dragRect = null; _host?.InvalidateOverlay(); return false; }
        if (ToImagePoint(viewPoint, start.Page) is { } p)
        {
            _dragRect = (start.Page, new Rect(start.Start, p.Px));
            _host?.InvalidateOverlay();
        }
        return true;
    }

    public bool OnPointerReleased(Point viewPoint, MouseButton button)
    {
        if (button == MouseButton.Right)
        {
            // 풍선 위 우클릭: Edit Translation / Fix Original / Hide / Revert. 그 외는 리더의 페이지 메뉴.
            if (!IsEnabled || _book is null || _visible is null || HitBubble(viewPoint) is not { } rh) return false;
            ShowBubbleContextMenu(rh.Page, rh.Bubble);
            return true;
        }
        if (button == MouseButton.Left && _pressedBubble is { } pressed)
        {
            _pressedBubble = null;
            if (HitBubble(viewPoint) is { } up && up.Page == pressed.Page && up.Bubble.Rect == pressed.Rect)
                ShowPopover(up.Page, up.Bubble);
            return true;
        }
        if (_dragStart is not { } start || button != MouseButton.Left) return false;
        var rect = _dragRect?.Rect ?? Rect.Empty;
        _dragStart = null;
        _dragRect = null;
        _host?.InvalidateOverlay();
        if (rect.Width >= 8 && rect.Height >= 8) _ = AddBalloonAsync(start.Page, new RectD(rect.X, rect.Y, rect.Width, rect.Height));
        return true;
    }

    public bool OnKeyDown(KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return false;
        if (_popover?.IsOpen == true) { ClosePopover(); _host?.FocusReader(); return true; }
        if (_editing) { SetEditing(false); return true; }
        if (_panelPopup?.IsOpen == true) { _panelPopup.IsOpen = false; return true; }
        return false;
    }

    // ───────────────────────── 수정 작업 ─────────────────────────

    private void MutateEdits(int pageIndex, DisplayBubble bubble, Func<PageEdits, int, PageEdits> op)
    {
        if (_book is null) return;
        var entry = _book.Pages[pageIndex];
        var scope = CurrentScope;
        var edits = Service.Edits.Get(entry.PageId, scope) ?? PageEditsEditor.Empty(entry.PageId, scope);
        int index;
        if (bubble.IsAdded && bubble.EditIndex is { } ai) index = ai;
        else (edits, index) = PageEditsEditor.Ensure(edits, bubble.RecognizedSource, bubble.Rect);
        Service.Edits.Save(op(edits, index));
        _host?.InvalidateOverlay();
    }

    internal void SaveTranslation(int page, DisplayBubble bubble, string text)
    {
        if (_visible is null) return;
        var target = _visible;
        MutateEdits(page, bubble, (e, i) => PageEditsEditor.SetTranslation(e, i, target, text.Trim()));
    }

    internal void FixOriginal(int page, DisplayBubble bubble, string corrected)
    {
        MutateEdits(page, bubble, (e, i) => PageEditsEditor.SetCorrectedSource(e, i, corrected));
        _ = RetranslateCorrectedAsync(page, bubble, corrected.Trim());
    }

    /// <summary>원문 수정 시 보유한 모든 언어를 다시 번역한다(사용자 번역은 덮지 않음: 우선순위가 더 높다).</summary>
    private async Task RetranslateCorrectedAsync(int page, DisplayBubble bubble, string corrected)
    {
        if (_book is null || string.IsNullOrWhiteSpace(corrected)) return;
        var source = _source ?? LanguageDetector.Detect([corrected]);
        if (source is null) return;
        var entry = _book.Pages[page];
        var scope = CurrentScope;
        var languages = TranslationLanguages.All.Where(l => l != source && (l == _visible || CompletedPages(l) > 0)).ToList();
        var bookKey = _book.BookKey;
        foreach (var lang in languages)
        {
            var edits = Service.Edits.Get(entry.PageId, scope);
            var index = bubble.IsAdded ? bubble.EditIndex ?? -1 : EditResolver.FindEditIndex(edits, bubble.RecognizedSource, bubble.Rect);
            if (index < 0) return;
            var key = (entry.PageId, index, lang);
            _pendingRetranslations.Add(key);
            _host?.InvalidateOverlay();
            try
            {
                var result = await Service.Scheduler.TranslateTextsAsync([new TranslationRequestItem("0", corrected)], source, lang, CancellationToken.None);
                if (_book?.BookKey != bookKey || !result.TryGetValue("0", out var text)) continue;
                var current = Service.Edits.Get(entry.PageId, scope);
                if (current is null) continue;
                var i = bubble.IsAdded ? index : EditResolver.FindEditIndex(current, bubble.RecognizedSource, bubble.Rect);
                if (i < 0 || i >= current.Bubbles.Count || current.Bubbles[i].CorrectedSource is null && !current.Bubbles[i].IsAddedWithText()) continue;
                Service.Edits.Save(PageEditsEditor.SetRetranslation(current, i, lang, text));
            }
            catch (Exception ex)
            {
                _host?.ShowNotice(Loc.T("Translation stopped: %@", ex.Message), TimeSpan.FromSeconds(6));
                break;
            }
            finally
            {
                _pendingRetranslations.Remove(key);
                _host?.InvalidateOverlay();
            }
        }
    }

    internal void SetHidden(int page, DisplayBubble bubble, bool hidden)
    {
        MutateEdits(page, bubble, (e, i) => PageEditsEditor.SetHidden(e, i, hidden));
        if (hidden)
            ShowUndoToast(Loc.T("Balloon hidden. While editing, hidden balloons show as dashed outlines — click one to bring it back."),
                () => MutateEdits(page, bubble with { IsHidden = true }, (e, i) => PageEditsEditor.SetHidden(e, i, false)));
    }

    internal void Revert(int page, DisplayBubble bubble, RevertKind kind)
    {
        if (_visible is null) return;
        var target = _visible;
        MutateEdits(page, bubble, (e, i) => PageEditsEditor.Revert(e, i, kind, target));
    }

    internal void RemoveAdded(int page, DisplayBubble bubble)
    {
        if (!bubble.IsAdded || bubble.EditIndex is not { } index || _book is null) return;
        var entry = _book.Pages[page];
        var edits = Service.Edits.Get(entry.PageId, CurrentScope);
        if (edits is null || index >= edits.Bubbles.Count) return;
        Service.Edits.Save(PageEditsEditor.RemoveAt(edits, index));
        _host?.InvalidateOverlay();
    }

    internal void RevertBook()
    {
        if (_book is null) return;
        var count = Service.Edits.EditedBubbleCount(_book.BookKey);
        if (count == 0) return;
        if (!SheetWindow.Confirm(_host?.OwnerWindow, Loc.T("Revert all %lld edited balloons in this book?", count),
                Loc.T("Every balloon edited or hidden in this book goes back to the machine translation. Other books keep their edits."), Loc.T("Revert All")))
            return;
        Service.Edits.DeleteBook(_book.BookKey);
        _host?.InvalidateOverlay();
    }

    /// <summary>드래그 영역 확대 crop OCR → 추가 풍선. 글자가 없으면 빈 풍선(Fix Original로 채움).</summary>
    private async Task AddBalloonAsync(int pageIndex, RectD rect)
    {
        if (_book is null || _host is null) return;
        var book = _book;
        var entry = book.Pages[pageIndex];
        var scope = CurrentScope;
        var source = _source;
        string text = string.Empty;
        double glyph = Math.Max(10, Math.Min(rect.Width, rect.Height) / 4);
        var vertical = false;
        var background = BubbleBackground.White;
        try
        {
            var pixels = await LocalPixels.GetPixelsAsync(book, entry, CancellationToken.None);
            if (Display(pageIndex) is { } d && d.ImageWidth > 0 && Math.Abs(d.ImageWidth - pixels.Width) > 0.5)
            {
                var s = pixels.Width / d.ImageWidth;
                rect = new RectD(rect.X * s, rect.Y * s, rect.Width * s, rect.Height * s);
            }
            if (source is not null)
                (text, glyph, vertical, background) = await Task.Run(() => Service.Scheduler.Analyzer.RecognizeRegionAsync(pixels, rect, source, CancellationToken.None));
            else
                (_, glyph, vertical, background) = await Task.Run(() => Service.Scheduler.Analyzer.RecognizeRegionAsync(pixels, rect, TranslationLanguages.English, CancellationToken.None));
        }
        catch (Exception)
        {
            // OCR 실패: 빈 풍선으로 추가
        }
        if (_book?.BookKey != book.BookKey) return;
        var edits = Service.Edits.Get(entry.PageId, scope) ?? PageEditsEditor.Empty(entry.PageId, scope);
        (edits, var index) = PageEditsEditor.AddBalloon(edits, rect, text, new AddedBalloon(glyph, vertical, background));
        Service.Edits.Save(edits);
        _host.InvalidateOverlay();
        if (text.Length == 0 || source is null || _visible is null)
        {
            _host.ShowNotice(Loc.T("Balloon added, but no text could be read there. Click its dashed outline and choose Fix Original to type the text."), TimeSpan.FromSeconds(8));
            return;
        }
        var target = _visible;
        var key = (entry.PageId, index, target);
        _pendingRetranslations.Add(key);
        try
        {
            var result = await Service.Scheduler.TranslateTextsAsync([new TranslationRequestItem("0", text)], source, target, CancellationToken.None);
            if (result.TryGetValue("0", out var translated) && Service.Edits.Get(entry.PageId, scope) is { } current && index < current.Bubbles.Count)
                Service.Edits.Save(PageEditsEditor.SetRetranslation(current, index, target, translated));
            _host.ShowNotice(Loc.T("Balloon added. It's translated now; click it to correct the text or remove it."), TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            _host.ShowNotice(Loc.T("Translation stopped: %@", ex.Message), TimeSpan.FromSeconds(6));
        }
        finally
        {
            _pendingRetranslations.Remove(key);
            _host.InvalidateOverlay();
        }
    }

    // ───────────────────────── 팝오버·메뉴 ─────────────────────────

    private void ShowPopover(int page, DisplayBubble bubble)
    {
        if (_host is null || _book is null) return;
        ClosePopover();
        var content = new BalloonPopover(this, page, bubble, OtherLanguageTexts(page, bubble));
        var anchor = BubbleViewRect(page, bubble) ?? new Rect(Mouse.GetPosition(_host.OverlayLayer), new Size(1, 1));
        _popover = new Popup
        {
            Child = content,
            PlacementTarget = _host.OverlayLayer,
            Placement = PlacementMode.Bottom,
            PlacementRectangle = anchor,
            StaysOpen = false,
            AllowsTransparency = true,
            Focusable = true,
        };
        _popover.Closed += (_, _) => { if (_popover?.Child == content) _host?.FocusReader(); };
        _popover.IsOpen = true;
        content.FocusInitial();
    }

    internal void ClosePopover()
    {
        if (_popover is null) return;
        var p = _popover;
        _popover = null;
        p.IsOpen = false;
    }

    internal void ReopenPopover(int page, DisplayBubble bubble)
    {
        // 수정 후 최신 상태로 다시 연다
        if (Display(page) is { } d && d.Bubbles.FirstOrDefault(b => b.Id == bubble.Id || (b.RecognizedSource == bubble.RecognizedSource && b.Rect == bubble.Rect)) is { } fresh)
            ShowPopover(page, fresh);
        else ClosePopover();
    }

    private Rect? BubbleViewRect(int page, DisplayBubble bubble)
    {
        if (_host is null) return null;
        var rect = bubble.Rect;
        if (Display(page) is { } d && _host.GetPageSize(page) is { Width: > 0 } s && d.ImageWidth > 0)
        {
            var scale = s.Width / d.ImageWidth;
            rect = new RectD(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale);
        }
        return _host.PageRectToView(page, rect);
    }

    /// <summary>다른 언어 번역(완료 캐시에서 같은 원문+겹치는 rect).</summary>
    private List<(string Language, string Text)> OtherLanguageTexts(int page, DisplayBubble bubble)
    {
        var list = new List<(string, string)>();
        if (_book is null || _source is null) return list;
        foreach (var lang in TranslationLanguages.All.Where(l => l != _source && l != _visible && CompletedPages(l) > 0))
        {
            var cached = Service.Scheduler.LoadCached(_book.Pages[page].PageId, _source, lang, CurrentScope, Mode);
            if (cached is null) continue;
            var edits = Service.Edits.Get(_book.Pages[page].PageId, CurrentScope);
            var match = EditResolver.Resolve(cached.Bubbles, edits, lang)
                .FirstOrDefault(b => b.RecognizedSource == bubble.RecognizedSource && b.Rect.Intersects(bubble.Rect));
            if (match?.Text is { Length: > 0 } t) list.Add((lang, t));
        }
        return list;
    }

    private void ShowBubbleContextMenu(int page, DisplayBubble bubble)
    {
        if (_host is null) return;
        _contextMenu = new ContextMenu { PlacementTarget = _host.OverlayLayer, Placement = PlacementMode.MousePoint };
        foreach (var item in BubbleActionItems(page, bubble)) _contextMenu.Items.Add(item);
        _contextMenu.IsOpen = true;
    }

    internal IEnumerable<Control> BubbleActionItems(int page, DisplayBubble bubble)
    {
        yield return Ui.MenuItem(Loc.T("Edit Translation…"), () => ShowPopover(page, bubble, BalloonPopover.Mode.EditTranslation));
        yield return Ui.MenuItem(Loc.T("Fix Original…"), () => ShowPopover(page, bubble, BalloonPopover.Mode.FixOriginal));
        if (bubble.IsAdded) yield return Ui.MenuItem(Loc.T("Remove This Balloon"), () => RemoveAdded(page, bubble));
        else if (bubble.IsHidden) yield return Ui.MenuItem(Loc.T("Show This Balloon"), () => SetHidden(page, bubble, false));
        else yield return Ui.MenuItem(Loc.T("Hide This Balloon"), () => SetHidden(page, bubble, true));
        if (bubble.IsEdited && !bubble.IsAdded)
        {
            var revert = new MenuItem { Header = Loc.T("Revert") };
            revert.Items.Add(Ui.MenuItem(Loc.T("Use Machine Translation"), () => Revert(page, bubble, RevertKind.Translation)));
            revert.Items.Add(Ui.MenuItem(Loc.T("Restore Original"), () => Revert(page, bubble, RevertKind.Original)));
            revert.Items.Add(Ui.MenuItem(Loc.T("Revert Both"), () => Revert(page, bubble, RevertKind.Both)));
            yield return revert;
        }
    }

    private void ShowPopover(int page, DisplayBubble bubble, BalloonPopover.Mode mode)
    {
        ShowPopover(page, bubble);
        if (_popover?.Child is BalloonPopover p) p.Enter(mode);
    }

    public IReadOnlyList<Control> BuildPageContextMenuItems(Point viewPoint)
    {
        if (!IsEnabled || _book is null || _host is null) return [];
        var items = new List<Control>();
        var page = _host.HitTestPage(viewPoint)?.PageIndex ?? _host.CurrentPageIndex;
        items.Add(Ui.MenuItem(Loc.T("Translate This Page Again"), () => RetranslatePage(page),
            enabled: _source is not null || HasResultsOrJob,
            tooltip: Loc.T("Read and translate the page on screen again, the current way — balloons split in two get joined. Your edits stay; one on a balloon found differently may no longer apply.")));
        items.Add(Ui.MenuItem(Loc.T("Add a Balloon Here…"), () => AddBalloonAt(viewPoint), enabled: _visible is not null));
        items.Add(new Separator());
        items.Add(Ui.MenuItem(Loc.T("Improve with an AI Chat…"), ShowAiChat, enabled: _visible is not null && _translated.Count > 0));
        items.Add(Ui.MenuItem(_editing ? Loc.T("Done Editing Balloons") : Loc.T("Edit Balloons"), ToggleEditMode,
            enabled: _editing || CanEdit, gesture: GestureHint(CommandIds.TranslationEdit)));
        return items;
    }

    private void AddBalloonAt(Point viewPoint)
    {
        if (ToImagePoint(viewPoint) is not { } p || Display(p.Page) is not { } d) return;
        if (!_editing) SetEditing(true);
        var w = Math.Max(40, d.ImageWidth * 0.18);
        var h = Math.Max(30, d.ImageWidth * 0.12);
        var rect = new RectD(Math.Clamp(p.Px.X - w / 2, 0, Math.Max(0, d.ImageWidth - w)), Math.Max(0, p.Px.Y - h / 2), w, h);
        _ = AddBalloonAsync(p.Page, rect);
    }

    internal void ShowAiChat()
    {
        if (_book is null || _visible is null || _source is null) return;
        AiChatSheet.Show(_host?.OwnerWindow, this);
    }

    // ───────────────────────── 편집 바 / 토스트 ─────────────────────────

    private void ShowEditBar(bool on)
    {
        if (_host is null) return;
        if (on)
        {
            _editBar ??= new EditBar(this);
            _editBar.Refresh();
            if (!_host.ProgressLayer.Children.Contains(_editBar)) _host.ProgressLayer.Children.Insert(0, _editBar);
        }
        else if (_editBar is not null)
        {
            _host.ProgressLayer.Children.Remove(_editBar);
        }
    }

    private Border? _toast;

    private void ShowUndoToast(string message, Action undo)
    {
        if (_host is null) return;
        if (_toast is not null) _host.ProgressLayer.Children.Remove(_toast);
        var undoButton = Ui.LinkButton(Loc.T("Undo"), () => { undo(); RemoveToast(); });
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(undoButton, Dock.Right);
        undoButton.Margin = new Thickness(12, 0, 0, 0);
        row.Children.Add(undoButton);
        row.Children.Add(Ui.Text(message, "Text.Caption"));
        _toast = new Border { Child = row, MaxWidth = 460, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1) };
        _toast.SetResourceReference(Border.BackgroundProperty, "Brush.CardBackground");
        _toast.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderSubtle");
        _host.ProgressLayer.Children.Insert(0, _toast);
        var toast = _toast;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) => { timer.Stop(); if (_toast == toast) RemoveToast(); };
        timer.Start();
    }

    private void RemoveToast()
    {
        if (_toast is null || _host is null) return;
        _host.ProgressLayer.Children.Remove(_toast);
        _toast = null;
    }
}

internal static class BubbleEditExtensions
{
    public static bool IsAddedWithText(this BubbleEdit e) => e.Added is not null && !string.IsNullOrWhiteSpace(e.CorrectedSource ?? e.RecognizedSource);
}
