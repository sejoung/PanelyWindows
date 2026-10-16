using Panely.Core.Localization;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Core.Translation;

/// <summary>말풍선/글자 상자 단위 입력을 선호하는 인식기(PageAnalyzer가 탐지 상자를 넘겨 준다).</summary>
public interface IRegionTextRecognizer
{
    bool PrefersRegions(string language);
}

/// <summary>원문 언어 감지 근거를 직접 계산하는 인식기(언어마다 OCR을 따로 돌리는 방식보다 빠르고 정확하다).</summary>
public interface ILanguageIdentifier
{
    bool CanIdentify { get; }
    /// <summary>한 쪽의 언어별 근거. textBoxes가 있으면 그 상자만 읽는다.</summary>
    Task<IReadOnlyDictionary<string, DetectionEvidence>> IdentifyAsync(PagePixels page, IReadOnlyList<RectD>? textBoxes, CancellationToken cancellationToken);
}

/// <summary>
/// comic-translate와 같은 구성의 만화 OCR(07 문서의 OCR 공급자 결정):
/// 일본어 = manga-ocr(상자 crop 통째로, 세로쓰기 포함), 중국어·영어·한국어 = PP-OCRv5(줄 탐지 + CTC).
/// 모델 팩이 설치되지 않았으면 다운로드를 안내하고(Supported), 모델 manifest 자체가 없을 때만 Windows OCR로 돌아간다.
/// </summary>
public sealed class ModelTextRecognizer : ITextRecognizer, IRegionTextRecognizer, ILanguageIdentifier, IDisposable
{
    private readonly ModelStore _store;
    private readonly ITextRecognizer? _fallback;

    public ModelTextRecognizer(ModelStore store, ITextRecognizer? fallback = null)
    {
        _store = store;
        _fallback = fallback;
        Manga = new MangaOcrEngine(store);
        PpOcr = new PpOcrEngine(store);
    }

    public MangaOcrEngine Manga { get; }
    public PpOcrEngine PpOcr { get; }

    public string Name => "manga-ocr / PP-OCRv5 (ONNX Runtime)";

    /// <summary>이 언어를 읽는 데 필요한 모델 팩. 일본어는 줄 탐지·감지용 PP-OCR도 함께 쓴다.</summary>
    public static IReadOnlyList<string> PacksFor(string language) => language == TranslationLanguages.Japanese
        ? [ModelCatalog.MangaOcrId, ModelCatalog.PpOcrId]
        : [ModelCatalog.PpOcrId];

    /// <summary>언어를 만화 OCR 모델로 읽을 수 있는지(설치 완료).</summary>
    public bool IsModelReady(string language) => PacksFor(language).All(_store.IsInstalled);

    public Capability GetCapability(string language)
    {
        if (!TranslationLanguages.IsSupported(language)) return Capability.Unavailable(Loc.T("This language pair isn't supported"));
        if (IsModelReady(language)) return Capability.Ready;
        var packs = PacksFor(language).Select(_store.Find).ToList();
        if (packs.All(m => m is { IsVerified: true }))
        {
            var downloading = PacksFor(language).Select(_store.DownloadProgress).FirstOrDefault(p => p is not null);
            if (downloading is { } p) return new Capability(CapabilityStatus.Downloading, null, p);
            return new Capability(CapabilityStatus.Supported, Loc.T("Download the text recognition pack for %@ to read this book.", Loc.LanguageName(language)));
        }
        return _fallback?.GetCapability(language) ?? Capability.Unavailable(Loc.T("Text recognition isn't available."));
    }

    public bool PrefersRegions(string language) => IsModelReady(language);

    public async Task<IReadOnlyList<TextRegion>> RecognizeAsync(PagePixels image, string language, IReadOnlyList<RectD>? regions, CancellationToken cancellationToken)
    {
        if (!IsModelReady(language))
        {
            if (_fallback is not null && _fallback.GetCapability(language).IsReady)
                return await _fallback.RecognizeAsync(image, language, regions, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(GetCapability(language).Reason);
        }

        if (language == TranslationLanguages.Japanese)
        {
            // 상자가 없으면 PP-OCR로 줄을 찾아 줄마다 manga-ocr로 읽는다
            var boxes = regions is { Count: > 0 }
                ? regions
                : (await PpOcr.RecognizeAsync(image, null, TranslationLanguages.Japanese, cancellationToken).ConfigureAwait(false)).Select(r => r.Rect).ToList();
            var expanded = boxes.Select(b => Expand(b, 0.05, image)).ToList();
            var crops = expanded.Select(b => image.Crop((int)b.X, (int)b.Y, (int)Math.Ceiling(b.Width), (int)Math.Ceiling(b.Height))).ToList();
            var texts = await Manga.RecognizeAsync(crops, cancellationToken).ConfigureAwait(false);
            var result = new List<TextRegion>(boxes.Count);
            for (var i = 0; i < boxes.Count; i++)
            {
                var text = texts[i];
                if (string.IsNullOrWhiteSpace(text)) continue;
                var box = boxes[i];
                // 탐지 상자는 여백이 넉넉하다. 상자 안의 실제 글자 줄로 글자 범위와 글자 크기(줄 두께)를 잰다.
                var lines = await PpOcr.DetectLinesAsync(image, expanded[i], cancellationToken).ConfigureAwait(false);
                result.Add(Measure(text, box, lines));
            }
            return result;
        }

        if (regions is { Count: > 0 })
        {
            var all = new List<TextRegion>();
            foreach (var region in regions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                all.AddRange(await PpOcr.RecognizeAsync(image, Expand(region, 0.05, image), language, cancellationToken).ConfigureAwait(false));
            }
            return all;
        }
        return await PpOcr.RecognizeAsync(image, null, language, cancellationToken).ConfigureAwait(false);
    }

    public bool CanIdentify => _store.IsInstalled(ModelCatalog.PpOcrId);

    /// <summary>
    /// 줄마다 PP-OCR 다국어(ch: 간체·번체·일본어 가나·영어) 모델과 한국어 모델로 읽고, 더 확신하는 쪽 결과를 근거로 센다.
    /// 다른 언어 모델이 억지로 읽은 결과는 확신도(CTC 평균 확률)가 낮아 대부분 걸러진다.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DetectionEvidence>> IdentifyAsync(PagePixels page, IReadOnlyList<RectD>? textBoxes, CancellationToken cancellationToken)
    {
        var evidence = new Dictionary<string, DetectionEvidence>();
        var areas = textBoxes is { Count: > 0 } ? textBoxes.Select(b => (RectD?)Expand(b, 0.05, page)).ToList() : [null];
        foreach (var area in areas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await PpOcr.ReadWithAsync(page, area, ["ch", "korean"], cancellationToken).ConfigureAwait(false);
            foreach (var line in read.GroupBy(r => r.Line))
            {
                var ch = line.FirstOrDefault(r => r.Recognizer == "ch");
                var ko = line.FirstOrDefault(r => r.Recognizer == "korean");
                if (ko.Text is { Length: > 0 } && ko.Confidence > ch.Confidence)
                {
                    Add(TranslationLanguages.Korean, DetectionEvidence.Of(ko.Text, TranslationLanguages.Korean));
                }
                else if (ch.Text is { Length: > 0 })
                {
                    Add(TranslationLanguages.Japanese, DetectionEvidence.Of(ch.Text, TranslationLanguages.Japanese));
                    Add(TranslationLanguages.ChineseSimplified, DetectionEvidence.Of(ch.Text, TranslationLanguages.ChineseSimplified));
                    Add(TranslationLanguages.English, DetectionEvidence.Of(ch.Text, TranslationLanguages.English));
                }
            }
        }
        return evidence;

        void Add(string language, DetectionEvidence e) => evidence[language] = evidence.GetValueOrDefault(language).Add(e);
    }

    /// <summary>
    /// manga-ocr 결과의 영역·방향·글자 크기. 줄이 잡히면 줄들의 합집합(실제 글자 범위)과 줄 두께 중앙값을 쓰고,
    /// 세로 여부는 줄 모양으로 정한다. 줄을 못 찾으면 상자와 면적 기반 어림값을 쓴다.
    /// </summary>
    internal static TextRegion Measure(string text, RectD box, IReadOnlyList<RectD> lines)
    {
        var estimate = EstimateGlyph(box, text);
        var fallback = new TextRegion(text, box, box.Height > box.Width * 1.15, estimate);
        var inside = lines.Where(l => l.CoveredBy(box) >= 0.5).ToList();
        if (inside.Count == 0) return fallback;
        var tight = inside.Aggregate(inside[0], (acc, l) => acc.Union(l)).Intersect(box);
        if (tight.IsEmpty) return fallback;
        var verticalLines = inside.Count(l => l.Height > l.Width * 1.2);
        var vertical = verticalLines * 2 >= inside.Count && inside.Any(l => l.Height > l.Width * 1.2);
        var thickness = inside.Select(l => vertical ? l.Width : l.Height).Order().ToList();
        var measured = thickness[thickness.Count / 2] * 0.8; // DB 줄 상자는 unclip으로 글자보다 조금 넓다
        // 줄 범위 안에 읽은 글자가 다 들어갈 만한 크기와 비교한다. 절반도 안 되면 그림 위 조각 줄을 잰 것이라 믿지 않는다.
        var tightEstimate = EstimateGlyph(tight, text);
        if (measured < tightEstimate * 0.5) return fallback;
        var glyph = Math.Clamp(measured, 6, Math.Max(6, tightEstimate * 1.2));
        return new TextRegion(text, tight, vertical, glyph);
    }

    /// <summary>상자 면적과 글자 수로 글자 크기를 어림한다(번역 글자 크기 맞춤의 상한에 쓰인다).</summary>
    internal static double EstimateGlyph(RectD box, string text)
    {
        var chars = Math.Max(1, text.Count(c => !char.IsWhiteSpace(c)));
        var g = Math.Sqrt(box.Width * box.Height / chars) * 0.8;
        return Math.Clamp(g, 8, Math.Max(8, Math.Min(box.Width, box.Height)));
    }

    private static RectD Expand(RectD r, double fraction, PagePixels image)
    {
        var dx = r.Width * fraction;
        var dy = r.Height * fraction;
        return RectD.FromEdges(Math.Max(0, r.Left - dx), Math.Max(0, r.Top - dy), Math.Min(image.Width, r.Right + dx), Math.Min(image.Height, r.Bottom + dy));
    }

    public void Dispose()
    {
        Manga.Dispose();
        PpOcr.Dispose();
    }
}
