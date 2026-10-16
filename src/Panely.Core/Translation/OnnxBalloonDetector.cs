using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Panely.Core.Localization;
using Panely.Domain.Translation;

namespace Panely.Core.Translation;

/// <summary>
/// RT-DETR-v2 말풍선 탐지(ogkalu/comic-text-and-bubble-detector 계약).
/// 입력 640×640 RGB stretch 0..1 NCHW [1,3,640,640], 출력 scores [1,300,3](sigmoid 적용), boxes [1,300,4](normalized cxcywh).
/// 입력/출력 이름은 manifest에서 읽는다. 검증된 모델이 설치되지 않았으면 사용 불가(탐지 없이 연결 요소로 묶는다).
/// 긴 이미지는 탐지 청크로 잘라 seam 결과를 제외하고 같은 kind 중복을 제거한다.
/// </summary>
public sealed class OnnxBalloonDetector : IBalloonDetector, IDisposable
{
    public const int InputSize = 640;
    private readonly ModelStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _session;
    private string? _sessionRevision;

    public OnnxBalloonDetector(ModelStore store) => _store = store;

    public string Name => "RT-DETR comic detector (ONNX Runtime)";

    /// <summary>실제로 쓰는 장치(GPU/CPU). 세션을 만들기 전에는 null.</summary>
    public InferenceDevice? Device { get; private set; }

    public string Revision => _store.Find(ModelCatalog.DetectorId) is { } m && _store.IsInstalled(m) ? m.Revision : "none";

    public Capability Capability
    {
        get
        {
            var m = _store.Find(ModelCatalog.DetectorId);
            if (m is null) return Capability.Unavailable(Loc.T("The balloon detector model isn't available."));
            if (_store.IsInstalled(m)) return Capability.Ready;
            if (!m.IsVerified) return Capability.Unavailable(Loc.T("The balloon detector model isn't bundled or verified yet. Balloons are grouped from the recognized text instead."));
            return new Capability(CapabilityStatus.Supported, Loc.T("Language pack not installed"));
        }
    }

    public async Task<IReadOnlyList<DetectedRegion>?> DetectAsync(PagePixels image, CancellationToken cancellationToken)
    {
        if (!Capability.IsReady) return null;
        var session = EnsureSession();
        if (session is null) return null;
        var manifest = _store.Find(ModelCatalog.DetectorId)!;
        var inputName = manifest.InputString("name") ?? session.InputMetadata.Keys.First();
        var sizesName = manifest.InputString("targetSizes");
        var scoresName = manifest.OutputString("scores") ?? "scores";
        var boxesName = manifest.OutputString("boxes") ?? "boxes";
        var labelsName = manifest.OutputString("labels") ?? "labels";
        // "postprocessed-xyxy": labels/scores/boxes(절대 xyxy). 그 외: scores [1,Q,3] + normalized cxcywh.
        var postprocessed = string.Equals(manifest.OutputString("boxFormat"), "postprocessed-xyxy", StringComparison.OrdinalIgnoreCase);

        var all = new List<DetectedRegion>();
        var chunks = TilePlanner.DetectorChunks(image.Width, image.Height);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var crop = chunks.Count == 1 ? image : image.Crop(0, chunk.Top, image.Width, chunk.Height);
            var tensor = ToTensor(crop);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, tensor) };
                // RT-DETR deploy export는 orig_target_sizes 입력을 요구한다. 640×640을 넣고 결과를 원래 크기로 환산한다.
                foreach (var extra in session.InputMetadata.Keys.Where(k => k != inputName && (sizesName is null || k == sizesName)))
                {
                    var sizes = new DenseTensor<long>(new long[] { InputSize, InputSize }, [1, 2]);
                    inputs.Add(NamedOnnxValue.CreateFromTensor(extra, sizes));
                }
                using var outputs = session.Run(inputs);
                var scores = outputs.First(o => o.Name == scoresName).AsEnumerable<float>().ToArray();
                var boxes = outputs.First(o => o.Name == boxesName).AsEnumerable<float>().ToArray();
                List<DetectedRegion> decoded;
                if (postprocessed)
                {
                    var labels = outputs.First(o => o.Name == labelsName).AsEnumerable<long>().ToArray();
                    decoded = DetectorDecoder.DecodePostprocessed(labels, scores, boxes, InputSize, InputSize, crop.Width, crop.Height);
                }
                else
                {
                    decoded = DetectorDecoder.Decode(scores, boxes, boxes.Length / 4, crop.Width, crop.Height);
                }
                foreach (var d in decoded)
                {
                    if (chunks.Count > 1 && TilePlanner.TouchesInteriorSeam(d.Rect, chunk, image.Height, 2)) continue;
                    all.Add(d.Offset(0, chunk.Top));
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        return TilePlanner.DedupeDetections(all);
    }

    /// <summary>stretch resize(letterbox 아님) → RGB 0..1 NCHW.</summary>
    public static DenseTensor<float> ToTensor(PagePixels image)
    {
        var resized = image.Resize(InputSize, InputSize);
        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        for (var y = 0; y < InputSize; y++)
        {
            for (var x = 0; x < InputSize; x++)
            {
                var i = y * resized.Stride + x * 4;
                tensor[0, 0, y, x] = resized.Bgra[i + 2] / 255f;
                tensor[0, 1, y, x] = resized.Bgra[i + 1] / 255f;
                tensor[0, 2, y, x] = resized.Bgra[i] / 255f;
            }
        }
        return tensor;
    }

    private InferenceSession? EnsureSession()
    {
        var m = _store.Find(ModelCatalog.DetectorId);
        if (m is null) return null;
        if (_session is not null && _sessionRevision == m.Revision) return _session;
        var path = _store.FilePath(m.Id, m.Files[0].Name);
        if (path is null) return null;
        _session?.Dispose();
        (_session, Device) = OnnxSessions.Create(path);
        _sessionRevision = m.Revision;
        return _session;
    }

    public void Dispose() => _session?.Dispose();
}
