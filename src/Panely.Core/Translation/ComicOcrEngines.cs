using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Core.Translation;

/// <summary>
/// 일본어 만화 전용 OCR: manga-ocr(kha-white/manga-ocr-base, Apache-2.0)의 ONNX int8 변환(ogkalu/manga-ocr-onnx).
/// 말풍선/글자 상자 crop 하나를 통째로 읽는다(세로쓰기·여러 줄 포함). comic-translate의 MangaOCRONNX를 C#으로 옮겼다.
/// 입력: 회색조→RGB, 224×224 bilinear, (x/255−.5)/.5, NCHW. 출력: [CLS]부터 greedy, [SEP]에서 끝.
/// </summary>
public sealed class MangaOcrEngine : IDisposable
{
    private const int InputSize = 224;
    private readonly ModelStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private string[] _vocab = [];
    private string? _revision;

    public MangaOcrEngine(ModelStore store) => _store = store;

    public bool IsInstalled => _store.IsInstalled(ModelCatalog.MangaOcrId);
    public InferenceDevice? Device { get; private set; }

    public async Task<IReadOnlyList<string>> RecognizeAsync(IReadOnlyList<PagePixels> crops, CancellationToken cancellationToken)
    {
        if (crops.Count == 0) return [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSessions();
            var results = new List<string>(crops.Count);
            // 배치가 크면 decoder 단계마다 패딩이 늘어나므로 8개씩
            foreach (var batch in crops.Chunk(8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.AddRange(RecognizeBatch(batch, cancellationToken));
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<string> RecognizeBatch(PagePixels[] crops, CancellationToken ct)
    {
        var n = crops.Length;
        var pixels = new DenseTensor<float>([n, 3, InputSize, InputSize]);
        for (var b = 0; b < n; b++)
        {
            var resized = crops[b].Resize(InputSize, InputSize);
            for (var y = 0; y < InputSize; y++)
                for (var x = 0; x < InputSize; x++)
                {
                    var i = y * resized.Stride + x * 4;
                    // 회색조(학습 전처리와 같게) 후 세 채널에 복사
                    var gray = (0.299f * resized.Bgra[i + 2] + 0.587f * resized.Bgra[i + 1] + 0.114f * resized.Bgra[i]) / 255f;
                    var v = (gray - 0.5f) / 0.5f;
                    pixels[b, 0, y, x] = v;
                    pixels[b, 1, y, x] = v;
                    pixels[b, 2, y, x] = v;
                }
        }

        var encoderInput = FindInput(_encoder!, "pixel_values", "image", "input");
        using var encoded = _encoder!.Run([NamedOnnxValue.CreateFromTensor(encoderInput, pixels)]);
        var hidden = encoded.First().AsTensor<float>().ToDenseTensor();

        var tokenInput = FindInput(_decoder!, "input_ids", "token", "input");
        var hiddenInput = FindInput(_decoder!, "encoder_hidden_states", "encoder_outputs", "encoder");
        var tokens = Enumerable.Range(0, n).Select(_ => new List<long> { MangaOcrText.StartToken }).ToArray();
        var finished = new bool[n];
        for (var step = 0; step < MangaOcrText.MaxTokens && !finished.All(f => f); step++)
        {
            ct.ThrowIfCancellationRequested();
            var length = tokens[0].Count;
            var ids = new DenseTensor<long>([n, length]);
            for (var b = 0; b < n; b++)
                for (var t = 0; t < length; t++) ids[b, t] = tokens[b][t];
            using var outputs = _decoder!.Run(
            [
                NamedOnnxValue.CreateFromTensor(tokenInput, ids),
                NamedOnnxValue.CreateFromTensor(hiddenInput, hidden),
            ]);
            var logits = outputs.First().AsTensor<float>();
            var vocab = logits.Dimensions[2];
            for (var b = 0; b < n; b++)
            {
                if (finished[b]) { tokens[b].Add(MangaOcrText.EndToken); continue; }
                var best = 0;
                var bestValue = float.MinValue;
                for (var v = 0; v < vocab; v++)
                {
                    var value = logits[b, length - 1, v];
                    if (value > bestValue) { bestValue = value; best = v; }
                }
                tokens[b].Add(best);
                if (best == MangaOcrText.EndToken) finished[b] = true;
            }
        }
        return tokens.Select(t => MangaOcrText.Decode(t, _vocab)).ToList();
    }

    private void EnsureSessions()
    {
        var m = _store.Find(ModelCatalog.MangaOcrId) ?? throw new InvalidOperationException("manga-ocr manifest missing");
        if (_encoder is not null && _revision == m.Revision) return;
        var encoderPath = _store.FilePath(m.Id, "encoder_model_int8.onnx") ?? throw new InvalidOperationException("manga-ocr not installed");
        var decoderPath = _store.FilePath(m.Id, "decoder_model_int8.onnx")!;
        var vocabPath = _store.FilePath(m.Id, "vocab.txt")!;
        DisposeSessions();
        // int8 동적 양자화 모델: GPU 실행 공급자에서 연산자가 CPU로 쪼개질 수 있어 장치는 실측 설정을 따른다
        var gpu = OcrRuntimePolicy.MangaOcrPrefersGpu;
        (_encoder, var device) = OnnxSessions.Create(encoderPath, gpu);
        (_decoder, _) = OnnxSessions.Create(decoderPath, gpu);
        Device = device;
        _vocab = File.ReadAllLines(vocabPath);
        _revision = m.Revision;
    }

    internal static string FindInput(InferenceSession session, params string[] candidates)
    {
        foreach (var c in candidates)
            foreach (var name in session.InputMetadata.Keys)
                if (name.Contains(c, StringComparison.OrdinalIgnoreCase)) return name;
        return session.InputMetadata.Keys.First();
    }

    private void DisposeSessions()
    {
        _encoder?.Dispose(); _encoder = null;
        _decoder?.Dispose(); _decoder = null;
    }

    public void Dispose() => DisposeSessions();
}

/// <summary>
/// 중국어·영어·한국어(및 언어 감지)용 PP-OCRv5 mobile(PaddleOCR, Apache-2.0)의 ONNX 변환(ogkalu/ppocr-v5-onnx).
/// DB 탐지로 글자 줄을 찾고 줄마다 CTC 인식을 한다. 중국어/일본어 세로 줄은 90° 돌려서 읽는다.
/// </summary>
public sealed class PpOcrEngine : IDisposable
{
    private const int RecHeight = 48;
    private const int RecMinWidth = 320;
    private readonly ModelStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (InferenceSession Session, string[] Dict)> _recognizers = new();
    private InferenceSession? _detector;
    private string? _revision;

    public PpOcrEngine(ModelStore store) => _store = store;

    public bool IsInstalled => _store.IsInstalled(ModelCatalog.PpOcrId);
    public InferenceDevice? Device { get; private set; }

    /// <summary>언어별 인식 모델 이름(ch 모델은 간체·번체·일본어 가나·영어를 함께 읽는다).</summary>
    public static string RecognizerFor(string language) => language switch
    {
        TranslationLanguages.Korean => "korean",
        TranslationLanguages.English => "en",
        _ => "ch",
    };

    /// <summary>area(페이지 좌표, null이면 전체)의 글자 줄을 찾아 읽는다.</summary>
    public async Task<IReadOnlyList<TextRegion>> RecognizeAsync(PagePixels image, RectD? area, string language, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSessions();
            var lines = DetectLines(image, area);
            var results = new List<TextRegion>(lines.Count);
            var recognized = RecognizeLines(image, lines, RecognizerFor(language), cancellationToken);
            for (var i = 0; i < lines.Count; i++)
            {
                var (text, _) = recognized[i];
                if (string.IsNullOrWhiteSpace(text)) continue;
                var r = lines[i];
                var vertical = IsVerticalLine(r, language);
                results.Add(new TextRegion(text, r, vertical, Math.Max(1, vertical ? r.Width : r.Height)));
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>이미 찾은 줄들을 지정 인식 모델로 읽는다(언어 감지에서 ch/korean 모델 비교용).</summary>
    public async Task<IReadOnlyList<(RectD Line, string Recognizer, string Text, double Confidence)>> ReadWithAsync(PagePixels image, RectD? area, IReadOnlyList<string> recognizers, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSessions();
            var lines = DetectLines(image, area);
            var all = new List<(RectD, string, string, double)>();
            foreach (var rec in recognizers)
            {
                var read = RecognizeLines(image, lines, rec, cancellationToken);
                for (var i = 0; i < lines.Count; i++) all.Add((lines[i], rec, read[i].Text, read[i].Confidence));
            }
            return all;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>area 안의 글자 줄 사각형만 찾는다(manga-ocr 상자의 실제 글자 범위·글자 크기 측정용).</summary>
    public async Task<IReadOnlyList<RectD>> DetectLinesAsync(PagePixels image, RectD area, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSessions();
            return DetectLines(image, area);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsVerticalLine(RectD r, string language)
        => r.Height > r.Width * 1.5 && language is TranslationLanguages.Japanese or TranslationLanguages.ChineseSimplified or TranslationLanguages.ChineseTraditional;

    private List<RectD> DetectLines(PagePixels image, RectD? area)
    {
        var a = area ?? new RectD(0, 0, image.Width, image.Height);
        var x0 = (int)Math.Floor(Math.Max(0, a.X));
        var y0 = (int)Math.Floor(Math.Max(0, a.Y));
        var w = (int)Math.Ceiling(Math.Min(image.Width, a.Right)) - x0;
        var h = (int)Math.Ceiling(Math.Min(image.Height, a.Bottom)) - y0;
        if (w < 4 || h < 4) return [];
        var crop = area is null ? image : image.Crop(x0, y0, w, h);
        var (iw, ih) = DbPostProcess.InputSize(crop.Width, crop.Height);
        var resized = crop.Resize(iw, ih);
        var input = new DenseTensor<float>([1, 3, ih, iw]);
        for (var y = 0; y < ih; y++)
            for (var x = 0; x < iw; x++)
            {
                var i = y * resized.Stride + x * 4;
                // PaddleOCR은 BGR 순서, mean .5 std .5
                input[0, 0, y, x] = (resized.Bgra[i] / 255f - 0.5f) / 0.5f;
                input[0, 1, y, x] = (resized.Bgra[i + 1] / 255f - 0.5f) / 0.5f;
                input[0, 2, y, x] = (resized.Bgra[i + 2] / 255f - 0.5f) / 0.5f;
            }
        using var outputs = _detector!.Run([NamedOnnxValue.CreateFromTensor(_detector.InputMetadata.Keys.First(), input)]);
        var map = outputs.First().AsTensor<float>();
        var probability = map.ToArray();
        var boxes = DbPostProcess.Boxes(probability, iw, ih);
        var sx = crop.Width / (double)iw;
        var sy = crop.Height / (double)ih;
        return boxes
            .Select(b => new RectD(x0 + b.X * sx, y0 + b.Y * sy, b.Width * sx, b.Height * sy))
            .OrderBy(b => b.Y).ThenBy(b => b.X)
            .ToList();
    }

    private List<(string Text, double Confidence)> RecognizeLines(PagePixels image, IReadOnlyList<RectD> lines, string recognizer, CancellationToken ct)
    {
        var (session, dict) = EnsureRecognizer(recognizer);
        var results = new (string, double)[lines.Count];
        var crops = new List<(int Index, PagePixels Line)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var r = lines[i];
            var crop = image.Crop((int)r.X, (int)r.Y, (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height));
            // 세로 줄(높이가 폭의 1.5배 초과)은 반시계로 90° 돌려 가로 줄처럼 읽는다(comic-translate와 같은 방식)
            if (r.Height > r.Width * 1.5 && recognizer == "ch") crop = RotateCounterClockwise(crop);
            crops.Add((i, crop));
        }
        // 같은 입력 폭끼리 묶어 패딩 낭비를 줄인다
        foreach (var bucket in crops.GroupBy(c => TargetWidth(c.Line)))
        {
            ct.ThrowIfCancellationRequested();
            var width = bucket.Key;
            var items = bucket.ToList();
            var input = new DenseTensor<float>([items.Count, 3, RecHeight, width]);
            for (var b = 0; b < items.Count; b++)
            {
                var line = items[b].Line;
                var resizedWidth = Math.Min(width, (int)Math.Ceiling(RecHeight * line.Width / (double)Math.Max(1, line.Height)));
                var resized = line.Resize(Math.Max(1, resizedWidth), RecHeight);
                for (var y = 0; y < RecHeight; y++)
                    for (var x = 0; x < resized.Width; x++)
                    {
                        var i = y * resized.Stride + x * 4;
                        input[b, 0, y, x] = (resized.Bgra[i] / 255f - 0.5f) / 0.5f;
                        input[b, 1, y, x] = (resized.Bgra[i + 1] / 255f - 0.5f) / 0.5f;
                        input[b, 2, y, x] = (resized.Bgra[i + 2] / 255f - 0.5f) / 0.5f;
                    }
            }
            using var outputs = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input)]);
            var logits = outputs.First().AsTensor<float>();
            var d1 = logits.Dimensions[1];
            var d2 = logits.Dimensions[2];
            var transposed = d1 > d2; // (N, C, T)이면 (N, T, C)로
            var steps = transposed ? d2 : d1;
            var classes = transposed ? d1 : d2;
            var row = new float[steps * classes];
            for (var b = 0; b < items.Count; b++)
            {
                for (var t = 0; t < steps; t++)
                    for (var c = 0; c < classes; c++)
                        row[t * classes + c] = transposed ? logits[b, c, t] : logits[b, t, c];
                results[items[b].Index] = CtcDecoder.Decode(row, steps, classes, dict);
            }
        }
        return results.ToList();
    }

    private static int TargetWidth(PagePixels line)
        => Math.Max(RecMinWidth, (int)Math.Ceiling(RecHeight * line.Width / (double)Math.Max(1, line.Height)));

    internal static PagePixels RotateCounterClockwise(PagePixels source)
    {
        var w = source.Height;
        var h = source.Width;
        var data = new byte[w * h * 4];
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                // (x, y) → (y, W-1-x)
                var nx = y;
                var ny = source.Width - 1 - x;
                Buffer.BlockCopy(source.Bgra, y * source.Stride + x * 4, data, (ny * w + nx) * 4, 4);
            }
        return new PagePixels(w, h, data, w * 4);
    }

    private void EnsureSessions()
    {
        var m = _store.Find(ModelCatalog.PpOcrId) ?? throw new InvalidOperationException("PP-OCR manifest missing");
        if (_detector is not null && _revision == m.Revision) return;
        var detPath = _store.FilePath(m.Id, "ch_PP-OCRv5_mobile_det.onnx") ?? throw new InvalidOperationException("PP-OCR not installed");
        DisposeSessions();
        (_detector, var device) = OnnxSessions.Create(detPath, OcrRuntimePolicy.PpOcrPrefersGpu);
        Device = device;
        _revision = m.Revision;
    }

    private (InferenceSession, string[]) EnsureRecognizer(string name)
    {
        if (_recognizers.TryGetValue(name, out var existing)) return existing;
        var (model, dict) = name switch
        {
            "korean" => ("korean_PP-OCRv5_rec_mobile_infer.onnx", "ppocrv5_korean_dict.txt"),
            "en" => ("en_PP-OCRv5_rec_mobile_infer.onnx", "ppocrv5_en_dict.txt"),
            _ => ("ch_PP-OCRv5_rec_mobile_infer.onnx", "ppocrv5_dict.txt"),
        };
        var modelPath = _store.FilePath(ModelCatalog.PpOcrId, model) ?? throw new InvalidOperationException("PP-OCR not installed");
        var dictPath = _store.FilePath(ModelCatalog.PpOcrId, dict)!;
        var (session, _) = OnnxSessions.Create(modelPath, OcrRuntimePolicy.PpOcrPrefersGpu);
        var lines = File.ReadAllText(dictPath).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        var entry = (session, lines);
        _recognizers[name] = entry;
        return entry;
    }

    private void DisposeSessions()
    {
        _detector?.Dispose(); _detector = null;
        foreach (var (s, _) in _recognizers.Values) s.Dispose();
        _recognizers.Clear();
    }

    public void Dispose() => DisposeSessions();
}

/// <summary>
/// 모델별 GPU 사용 여부. 실측(이 PC, DirectML, 웹툰 12쪽/말풍선 12개, 세션 생성 포함):
/// 말풍선 탐지 RT-DETR fp32 GPU 2.1초 vs CPU 5.4초 → GPU. manga-ocr int8 GPU 3.8초 vs CPU 0.9초 → CPU.
/// PP-OCR mobile(작은 모델, 줄마다 입력 폭이 달라 GPU 그래프 재구성 비용) GPU 1.0~1.6초 vs CPU 0.6초 → CPU.
/// </summary>
public static class OcrRuntimePolicy
{
    public static bool MangaOcrPrefersGpu { get; set; }
    public static bool PpOcrPrefersGpu { get; set; }
}
