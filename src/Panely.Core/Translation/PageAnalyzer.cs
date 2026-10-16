using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Core.Translation;

/// <summary>
/// 한 이미지(페이지 또는 strip band)의 분석: 탐지 → OCR(+빈 text box crop 재인식) → 그룹 → 효과음 제외 → 배경 sampling.
/// 결과 풍선은 번역 전(translation=null).
/// </summary>
public sealed class PageAnalyzer
{
    /// <summary>Windows 분석 규칙 버전(캐시 identity). 그룹/필터/OCR 규칙을 바꾸면 올린다.</summary>
    /// <summary>2: 배경 flat 판정을 다중 고리·표본 비율 방식으로 변경(이전 캐시는 다시 분석).</summary>
    public const int AnalysisVersion = 2;
    private const int MaxConcurrentRereads = 2;

    private readonly ITextRecognizer _recognizer;
    private readonly IBalloonDetector _detector;

    public PageAnalyzer(ITextRecognizer recognizer, IBalloonDetector detector)
    {
        _recognizer = recognizer;
        _detector = detector;
    }

    public async Task<List<TranslatedBubble>> AnalyzeAsync(PagePixels image, string language, CancellationToken cancellationToken)
    {
        IReadOnlyList<DetectedRegion>? detections = null;
        if (_detector.Capability.IsReady)
            detections = await _detector.DetectAsync(image, cancellationToken).ConfigureAwait(false);

        var regions = (await _recognizer.RecognizeAsync(image, language, null, cancellationToken).ConfigureAwait(false)).ToList();

        // 탐지 text box는 있는데 OCR이 비어 있으면 crop을 확대해 재인식
        if (detections is { Count: > 0 })
        {
            var empty = detections.Where(d => d.IsTextBox && !regions.Any(r => r.Rect.CoveredBy(d.Rect) >= 0.5 || d.Rect.CoveredBy(r.Rect) >= 0.5))
                .Select(d => d.Rect).ToList();
            foreach (var batch in empty.Chunk(MaxConcurrentRereads))
            {
                var found = await _recognizer.RecognizeAsync(image, language, batch, cancellationToken).ConfigureAwait(false);
                regions.AddRange(found);
            }
            regions = TilePlanner.DedupeOcr(regions);
        }

        var groups = BalloonGrouper.Group(regions, detections, language)
            .Where(g => !SoundEffectFilter.IsSoundEffect(g.Text, language))
            .ToList();
        groups = BalloonGrouper.AssignIds(groups);

        var bubbles = new List<TranslatedBubble>(groups.Count);
        foreach (var g in groups)
        {
            var (background, balloonSafe) = BackgroundSampler.Sample(image, g.Rect, g.GlyphSize, g.BalloonRect);
            bubbles.Add(new TranslatedBubble
            {
                Id = g.Id,
                Rect = g.Rect,
                GlyphSize = g.GlyphSize,
                IsVertical = g.IsVertical,
                Source = g.Text,
                Translation = null,
                Background = background,
                BalloonRect = balloonSafe ? g.BalloonRect : null,
            });
        }
        return bubbles;
    }

    /// <summary>사용자가 드래그로 추가한 영역의 확대 crop OCR.</summary>
    public async Task<(string Text, double Glyph, bool Vertical, BubbleBackground Background)> RecognizeRegionAsync(PagePixels image, RectD rect, string language, CancellationToken cancellationToken)
    {
        var regions = _recognizer.GetCapability(language).IsReady
            ? await _recognizer.RecognizeAsync(image, language, [rect], cancellationToken).ConfigureAwait(false)
            : [];
        var groups = BalloonGrouper.Group(regions.Where(r => r.Rect.CoveredBy(rect) >= 0.3).ToList(), null, language);
        var text = string.Join(Scripts.LineJoiner(language), groups.Select(g => g.Text)).Trim();
        var vertical = groups.Count > 0 ? groups[0].IsVertical : rect.Height > rect.Width * 1.6 && TranslationLanguages.IsCjkVertical(language);
        var glyph = groups.Count > 0 ? groups.Min(g => g.GlyphSize) : Math.Max(10, Math.Min(rect.Width, rect.Height) / 4);
        var (bg, _) = BackgroundSampler.Sample(image, rect, glyph, null);
        return (text, glyph, vertical, bg);
    }
}

/// <summary>풍선 텍스트 주변 톤 sampling(06 번역 패치 렌더링).</summary>
public static class BackgroundSampler
{
    public const double UniformThreshold = 0.07;
    /// <summary>배경 톤에 가까운 표본이 이 비율 이상이면 flat으로 본다(글자 획·테두리 몇 점은 허용).</summary>
    public const double UniformShare = 0.85;
    /// <summary>표본이 배경 톤과 같다고 보는 채널 평균 차이.</summary>
    public const double SameToneDistance = 0.06;

    /// <returns>배경 톤과 balloonRect 중앙 66%를 덮어도 안전한지(추가 잉크 비율 ≤ .03).</returns>
    public static (BubbleBackground Background, bool BalloonSafe) Sample(PagePixels image, RectD rect, double glyph, RectD? balloonRect)
    {
        var pad = Math.Max(2, PatchGeometry.PaddingFraction * glyph);
        var samples = new List<(double R, double G, double B)>();
        void Add(int x, int y)
        {
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return;
            var (b, g, r) = image.PixelAt(x, y);
            samples.Add((r / 255.0, g / 255.0, b / 255.0));
        }
        // 텍스트 주변 세 겹의 고리. 바깥 고리 하나만 보면 풍선 외곽선/상자 테두리에 걸리기 쉽다.
        foreach (var inset in new[] { 0.35, 0.65, 1.0 })
        {
            var ring = rect.Inflate(pad * inset, pad * inset);
            var step = Math.Max(1, (int)(Math.Max(ring.Width, ring.Height) / 60));
            for (var x = (int)ring.Left; x <= (int)ring.Right; x += step) { Add(x, (int)ring.Top); Add(x, (int)ring.Bottom); }
            for (var y = (int)ring.Top; y <= (int)ring.Bottom; y += step) { Add((int)ring.Left, y); Add((int)ring.Right, y); }
        }
        if (samples.Count == 0) return (BubbleBackground.White, false);

        // 밝은 쪽/어두운 쪽 중 다수를 배경으로 본다(글자 획이 섞인 표본 제거)
        var lum = samples.Select(s => 0.299 * s.R + 0.587 * s.G + 0.114 * s.B).ToList();
        var median = lum.OrderBy(v => v).ElementAt(lum.Count / 2);
        var near = samples.Where((s, i) => Math.Abs(lum[i] - median) < 0.25).ToList();
        if (near.Count == 0) near = samples;
        var mr = near.Average(s => s.R); var mg = near.Average(s => s.G); var mb = near.Average(s => s.B);
        // 배경 톤에 가까운 표본 비율로 flat을 판단한다. 글자 획·외곽선 몇 점 때문에 흰 풍선을 그림 위로 오판하지 않게 한다.
        var sameTone = samples.Count(s => (Math.Abs(s.R - mr) + Math.Abs(s.G - mg) + Math.Abs(s.B - mb)) / 3 < SameToneDistance);
        var nearDeviation = Math.Sqrt(near.Average(s => ((s.R - mr) * (s.R - mr) + (s.G - mg) * (s.G - mg) + (s.B - mb) * (s.B - mb)) / 3));
        var uniform = sameTone >= samples.Count * UniformShare && nearDeviation < UniformThreshold;
        var background = new BubbleBackground(Math.Round(mr, 4), Math.Round(mg, 4), Math.Round(mb, 4), uniform);

        var safe = false;
        if (uniform && balloonRect is { } b && !b.IsEmpty)
        {
            var w = b.Width * PatchGeometry.BalloonInteriorShare;
            var h = b.Height * PatchGeometry.BalloonInteriorShare;
            var interior = new RectD(b.CenterX - w / 2, b.CenterY - h / 2, w, h);
            var text = rect.Inflate(pad, pad);
            int total = 0, ink = 0;
            var s = Math.Max(1, (int)(Math.Max(interior.Width, interior.Height) / 80));
            for (var y = (int)interior.Top; y < (int)interior.Bottom; y += s)
            {
                for (var x = (int)interior.Left; x < (int)interior.Right; x += s)
                {
                    if (text.Contains(x, y) || x < 0 || y < 0 || x >= image.Width || y >= image.Height) continue;
                    total++;
                    var (pb, pg, pr) = image.PixelAt(x, y);
                    var d = Math.Abs(pr / 255.0 - mr) + Math.Abs(pg / 255.0 - mg) + Math.Abs(pb / 255.0 - mb);
                    if (d > 0.45) ink++;
                }
            }
            safe = total == 0 || ink / (double)total <= PatchGeometry.BalloonInkSafeRatio;
        }
        return (background, safe);
    }
}
