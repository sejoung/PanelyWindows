using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.App.Translation;

/// <summary>
/// 번역 patch painter(06 번역 패치 렌더링). 뷰어 오버레이와 내보내기 렌더러가 같은 코드를 쓴다.
/// 좌표는 페이지 픽셀. flat 배경은 solid, artwork 배경은 alpha .92 + feather, 글자는 semibold 가운데 정렬,
/// 크기 6..max(.95glyph,8) 이진 탐색(.5px), 한글·영문은 단어 우선 줄바꿈(60% 규칙), 일본어·중국어는 글자 단위,
/// artwork 위에서는 반대색 외곽선 후 fill.
/// </summary>
public static class OverlayPainter
{
    private static readonly Typeface TextFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic, Yu Gothic UI, Microsoft YaHei UI, Microsoft JhengHei UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private const int FeatherSteps = 6;

    public static void DrawPatches(DrawingContext dc, IReadOnlyList<PatchLayout> patches, bool pendingDimmed = true)
    {
        foreach (var patch in patches) DrawPatch(dc, patch, pendingDimmed);
    }

    public static void DrawPatch(DrawingContext dc, PatchLayout patch, bool pendingDimmed = true)
    {
        var rect = ToRect(patch.Rect);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var bg = patch.Background;
        var color = Color.FromRgb(ToByte(bg.Red), ToByte(bg.Green), ToByte(bg.Blue));
        if (patch.IsFlat)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, rect, patch.CornerRadius, patch.CornerRadius);
        }
        else
        {
            DrawFeathered(dc, rect, color, patch.CornerRadius, patch.Feather, PatchGeometry.ArtworkAlpha);
        }

        if (string.IsNullOrEmpty(patch.Text)) return;
        var textColor = bg.UsesWhiteText ? Colors.White : Colors.Black;
        var isPending = patch.Text == "…";
        if (isPending && pendingDimmed) textColor = Color.FromArgb(150, textColor.R, textColor.G, textColor.B);
        var box = rect;
        box.Inflate(-patch.InnerPadding, -patch.InnerPadding);
        if (box.Width < 2 || box.Height < 2) box = rect;
        // 한글 어절·영문 단어는 통째로 맞춘 크기를 우선하고, 단어 안 줄바꿈 크기의 60% 미만일 때만 단어를 나눈다
        var (size, splitWords) = PatchGeometry.ChooseWrapFontSize(patch.MinFontSize, patch.MaxFontSize,
            s => Fits(patch.Text, s, box, splitWords: false), s => Fits(patch.Text, s, box, splitWords: true));
        var lines = WrapLines(patch.Text, size, box.Width, splitWords);
        var lineHeight = LineHeight(size);
        var total = lines.Count * lineHeight;
        var y = box.Top + (box.Height - total) / 2;
        var fill = new SolidColorBrush(textColor);
        fill.Freeze();
        Pen? outline = null;
        if (!patch.IsFlat)
        {
            var oc = bg.UsesWhiteText ? Colors.Black : Colors.White;
            outline = new Pen(new SolidColorBrush(Color.FromArgb(220, oc.R, oc.G, oc.B)), Math.Max(1, size * 0.18)) { LineJoin = PenLineJoin.Round };
            outline.Freeze();
        }
        foreach (var line in lines)
        {
            var ft = Format(line, size, fill);
            var origin = new Point(box.Left + (box.Width - ft.WidthIncludingTrailingWhitespace) / 2, y + (lineHeight - ft.Height) / 2);
            if (outline is not null)
            {
                var geometry = ft.BuildGeometry(origin);
                dc.DrawGeometry(null, outline, geometry);
                dc.DrawGeometry(fill, null, geometry);
            }
            else
            {
                dc.DrawText(ft, origin);
            }
            y += lineHeight;
        }
    }

    /// <summary>
    /// 사각형 feather: 안쪽에서 바깥으로 겹치는 반투명 rounded rect를 쌓아 가운데가 alpha .92가 되게 한다.
    /// </summary>
    private static void DrawFeathered(DrawingContext dc, Rect rect, Color color, double radius, double feather, double alpha)
    {
        // 바깥(rect+f/2)부터 안쪽(rect-f/2)까지 n겹: 가운데는 n겹이 쌓여 alpha가 되고 가장자리는 한 겹만 남는다
        var stepAlpha = 1 - Math.Pow(1 - alpha, 1.0 / FeatherSteps);
        var brush = new SolidColorBrush(Color.FromArgb(ToByte(stepAlpha), color.R, color.G, color.B));
        brush.Freeze();
        for (var i = 0; i < FeatherSteps; i++)
        {
            var d = feather / 2 - feather * i / (FeatherSteps - 1);
            var r = rect;
            r.Inflate(d, d);
            if (r.Width <= 0 || r.Height <= 0) r = new Rect(rect.X + rect.Width / 2 - 0.5, rect.Y + rect.Height / 2 - 0.5, 1, 1);
            var rr = Math.Max(0, radius + d);
            dc.DrawRoundedRectangle(brush, null, r, rr, rr);
        }
    }

    private static double LineHeight(double size) => size * 1.22;

    public static bool Fits(string text, double size, Rect box, bool splitWords = true)
    {
        var lines = WrapLines(text, size, box.Width, splitWords);
        if (lines.Count * LineHeight(size) > box.Height + 0.5) return false;
        foreach (var l in lines) if (Measure(l, size) > box.Width + 0.5) return false;
        return true;
    }

    /// <summary>
    /// 줄바꿈: 명시 줄바꿈 유지. 일본어·중국어는 공백이 있어도 문장 안에서 글자 단위로 이어 줄을 바꾼다.
    /// 한글 어절·영문 단어는 공백 단위로 넘기고, splitWords일 때만 폭을 넘는 단어를 글자 단위로 나눈다
    /// (아니면 Fits에서 폭 초과로 판정돼 더 작은 글자 크기를 고른다).
    /// </summary>
    public static List<string> WrapLines(string text, double size, double maxWidth, bool splitWords = true)
    {
        var flowing = IsChineseOrJapanese(text);
        var result = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var tokens = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) { result.Add(string.Empty); continue; }
            var line = string.Empty;
            foreach (var token in tokens)
            {
                var candidate = line.Length == 0 ? token : line + " " + token;
                if (Measure(candidate, size) <= maxWidth) { line = candidate; continue; }
                if (flowing && !IsWord(token))
                {
                    // 일본어·중국어: 공백 뒤에 이어 붙이고 넘치는 글자부터 다음 줄로
                    if (line.Length > 0) line += " ";
                }
                else
                {
                    if (line.Length > 0) { result.Add(line); line = string.Empty; }
                    if (Measure(token, size) <= maxWidth || (!splitWords && IsWord(token))) { line = token; continue; }
                }
                // 공백 없는 CJK 토큰이나 나누기로 한 단어: 글자 단위
                foreach (var ch in SplitGraphemes(token))
                {
                    var c2 = line + ch;
                    if (line.Length > 0 && Measure(c2, size) > maxWidth && !IsClosingPunctuation(ch)) { result.Add(line.TrimEnd()); line = ch; }
                    else line = c2;
                }
            }
            if (line.Length > 0) result.Add(line);
        }
        return result;
    }

    private static bool IsWord(string token) => IsLatinWord(token) || ContainsHangul(token);

    private static bool IsLatinWord(string token) => token.All(c => c < 0x2E80);

    private static bool ContainsHangul(string token) => token.Any(c => c is >= '가' and <= '힣');

    private static bool IsChineseOrJapanese(string text) =>
        !ContainsHangul(text) && text.Any(c => c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿');

    private static bool IsClosingPunctuation(string ch) => ch is "、" or "。" or "，" or "." or "," or "!" or "?" or "！" or "？" or "…" or "」" or "』" or ")" or "）" or "ー";

    private static IEnumerable<string> SplitGraphemes(string s)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) yield return (string)e.Current;
    }

    private static readonly Dictionary<(string, double), double> MeasureCache = new();

    private static double Measure(string text, double size)
    {
        if (text.Length == 0) return 0;
        lock (MeasureCache)
        {
            if (MeasureCache.TryGetValue((text, size), out var w)) return w;
            if (MeasureCache.Count > 20000) MeasureCache.Clear();
            w = Format(text, size, Brushes.Black).WidthIncludingTrailingWhitespace;
            MeasureCache[(text, size)] = w;
            return w;
        }
    }

    private static FormattedText Format(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, size, brush, 1.0);

    public static Rect ToRect(RectD r) => new(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    // ───────── 편집 모드 표시 ─────────

    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    private static readonly Brush AccentFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x84, 0xFF)));

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>숨긴 풍선 점선, 수정한 풍선 연필 표시, 추가 풍선 테두리.</summary>
    public static void DrawEditDecorations(DrawingContext dc, IReadOnlyList<DisplayBubble> bubbles, double pageWidth)
    {
        var thickness = Math.Max(1.5, pageWidth / 450);
        var dashed = new Pen(AccentBrush, thickness) { DashStyle = new DashStyle([3, 2], 0) };
        dashed.Freeze();
        var solid = new Pen(AccentBrush, thickness);
        solid.Freeze();
        foreach (var b in bubbles)
        {
            var pad = PatchGeometry.PaddingFraction * b.GlyphSize;
            var rect = ToRect(b.Rect);
            rect.Inflate(pad, pad);
            if (b.IsHidden)
            {
                dc.DrawRoundedRectangle(null, dashed, rect, 4, 4);
                continue;
            }
            if (b.IsAdded) dc.DrawRoundedRectangle(null, b.HasVisibleText ? solid : dashed, rect, 4, 4);
            if (b.IsEdited || b.IsAdded) DrawPencil(dc, new Point(rect.Right, rect.Top), Math.Max(10, b.GlyphSize * 0.7));
        }
    }

    private static void DrawPencil(DrawingContext dc, Point center, double size)
    {
        dc.DrawEllipse(AccentBrush, null, center, size / 2, size / 2);
        var s = size * 0.28;
        var pen = new Pen(Brushes.White, Math.Max(1, size * 0.1)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawLine(pen, new Point(center.X - s, center.Y + s), new Point(center.X + s, center.Y - s));
        dc.DrawLine(pen, new Point(center.X - s, center.Y + s), new Point(center.X - s * 0.4, center.Y + s));
    }

    public static void DrawSelection(DrawingContext dc, Rect rect, double pageWidth)
    {
        var pen = new Pen(AccentBrush, Math.Max(1.5, pageWidth / 450)) { DashStyle = new DashStyle([4, 2], 0) };
        pen.Freeze();
        dc.DrawRectangle(AccentFill, pen, rect);
    }
}
