using Panely.Core.Localization;
using Panely.Domain.Model;
using Panely.Domain.Translation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace Panely.Core.Translation;

/// <summary>
/// Windows.Media.Ocr 기반 OCR(Vision 대체). Windows 설정에서 해당 언어의 OCR 기능이 설치되어 있어야 한다.
/// 알려진 한계(미검증): 세로쓰기 일본어/중국어 인식 품질은 보장되지 않는다. Windows OCR은 가로 줄 중심이다.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "SemaphoreSlim은 WaitAsync만 사용해 대기 핸들을 만들지 않는다. 앱 수명 동안 하나만 존재한다.")]
public sealed class WindowsOcrRecognizer : ITextRecognizer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, OcrEngine?> _engines = new();

    public string Name => "Windows OCR";

    /// <summary>Windows OCR 언어 태그 후보(앞에서부터 우선).</summary>
    public static IReadOnlyList<string> TagCandidates(string language) => language switch
    {
        TranslationLanguages.Japanese => ["ja"],
        TranslationLanguages.Korean => ["ko"],
        TranslationLanguages.English => ["en-US", "en"],
        TranslationLanguages.ChineseSimplified => ["zh-Hans", "zh-CN", "zh-SG"],
        TranslationLanguages.ChineseTraditional => ["zh-Hant", "zh-TW", "zh-HK"],
        _ => [language],
    };

    private OcrEngine? EngineFor(string language)
    {
        lock (_engines)
        {
            if (_engines.TryGetValue(language, out var cached)) return cached;
            OcrEngine? engine = null;
            try
            {
                var available = OcrEngine.AvailableRecognizerLanguages.ToList();
                foreach (var tag in TagCandidates(language))
                {
                    var match = available.FirstOrDefault(l => l.LanguageTag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                        ?? available.FirstOrDefault(l => l.LanguageTag.StartsWith(tag, StringComparison.OrdinalIgnoreCase));
                    if (match is null) continue;
                    engine = OcrEngine.TryCreateFromLanguage(match);
                    if (engine is not null) break;
                }
            }
            catch (Exception)
            {
                engine = null;
            }
            _engines[language] = engine;
            return engine;
        }
    }

    public Capability GetCapability(string language)
    {
        try
        {
            if (EngineFor(language) is not null) return Capability.Ready;
        }
        catch (Exception ex)
        {
            return Capability.Unavailable(Loc.T("Windows text recognition isn't available on this PC: %@", ex.Message));
        }
        return Capability.Unavailable(Loc.T("Text recognition for %@ isn't installed. Add the language (with its optical character recognition feature) in Windows Settings › Time & language › Language & region, then try again.", Loc.LanguageName(language)));
    }

    /// <summary>설치 상태가 바뀌었을 수 있으니 다음 조회 때 다시 확인한다.</summary>
    public void Refresh() { lock (_engines) _engines.Clear(); }

    public async Task<IReadOnlyList<TextRegion>> RecognizeAsync(PagePixels image, string language, IReadOnlyList<RectD>? regions, CancellationToken cancellationToken)
    {
        var engine = EngineFor(language) ?? throw new InvalidOperationException(GetCapability(language).Reason);
        var cjk = TranslationLanguages.IsCjkVertical(language);
        var results = new List<TextRegion>();
        if (regions is null)
        {
            var bands = TilePlanner.OcrTiles(image.Width, image.Height);
            foreach (var band in bands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var crop = bands.Count == 1 ? image : image.Crop(0, band.Top, image.Width, band.Height);
                var found = await RecognizeImageAsync(engine, crop, cjk, cancellationToken).ConfigureAwait(false);
                foreach (var r in found)
                {
                    if (bands.Count > 1 && TilePlanner.TouchesInteriorSeam(r.Rect, band, image.Height, TilePlanner.OcrSeamMargin)) continue;
                    results.Add(r.Offset(0, band.Top));
                }
            }
            return TilePlanner.DedupeOcr(results);
        }

        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var margin = Math.Max(4, Math.Min(region.Width, region.Height) * 0.1);
            var x = (int)Math.Floor(region.X - margin);
            var y = (int)Math.Floor(region.Y - margin);
            var w = (int)Math.Ceiling(region.Width + 2 * margin);
            var h = (int)Math.Ceiling(region.Height + 2 * margin);
            var crop = image.Crop(x, y, w, h);
            // 작은 crop은 확대해서 재인식(06: 탐지 text box에 OCR이 비면 crop 확대 재인식)
            var scale = Math.Clamp(160.0 / Math.Max(1, Math.Min(w, h)), 1, 3);
            if (scale > 1.01) crop = crop.Resize((int)(w * scale), (int)(h * scale));
            var found = await RecognizeImageAsync(engine, crop, cjk, cancellationToken).ConfigureAwait(false);
            foreach (var r in found)
            {
                var rect = new RectD(x + r.Rect.X / scale, y + r.Rect.Y / scale, r.Rect.Width / scale, r.Rect.Height / scale);
                results.Add(r with { Rect = rect, GlyphSize = r.GlyphSize / scale });
            }
        }
        return TilePlanner.DedupeOcr(results);
    }

    private async Task<List<TextRegion>> RecognizeImageAsync(OcrEngine engine, PagePixels image, bool cjk, CancellationToken cancellationToken)
    {
        var max = (int)OcrEngine.MaxImageDimension;
        var scale = 1.0;
        var source = image;
        if (image.Width > max || image.Height > max)
        {
            scale = Math.Min(max / (double)image.Width, max / (double)image.Height);
            source = image.Resize(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        }
        var tight = source.Stride == source.Width * 4 ? source.Bgra : Tighten(source);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        OcrResult result;
        try
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(tight),
                BitmapPixelFormat.Bgra8, source.Width, source.Height, BitmapAlphaMode.Premultiplied);
            result = await engine.RecognizeAsync(bitmap);
        }
        finally
        {
            _gate.Release();
        }

        var list = new List<TextRegion>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.ToList();
            if (words.Count == 0) continue;
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
            foreach (var w in words)
            {
                l = Math.Min(l, w.BoundingRect.X); t = Math.Min(t, w.BoundingRect.Y);
                r = Math.Max(r, w.BoundingRect.X + w.BoundingRect.Width); b = Math.Max(b, w.BoundingRect.Y + w.BoundingRect.Height);
            }
            var rect = new RectD(l / scale, t / scale, (r - l) / scale, (b - t) / scale);
            var vertical = cjk && words.Count > 1 && rect.Height > rect.Width * 1.5
                && words.Zip(words.Skip(1)).All(p => p.Second.BoundingRect.Y >= p.First.BoundingRect.Y - 1);
            var glyph = vertical ? rect.Width : rect.Height;
            var text = line.Text;
            if (cjk) text = Scripts.RemoveCjkSpaces(text);
            list.Add(new TextRegion(text, rect, vertical, Math.Max(1, glyph)));
        }
        return list;
    }

    private static byte[] Tighten(PagePixels p)
    {
        var data = new byte[p.Width * p.Height * 4];
        for (var y = 0; y < p.Height; y++) Buffer.BlockCopy(p.Bgra, y * p.Stride, data, y * p.Width * 4, p.Width * 4);
        return data;
    }
}
