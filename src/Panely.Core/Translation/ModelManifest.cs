using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Panely.Core.Translation;

public sealed record ModelFile
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    /// <summary>pinned 다운로드 URL. 없으면 source + "/" + name.</summary>
    [JsonPropertyName("url")] public string? Url { get; init; }
}

/// <summary>
/// 모델 패키지 manifest(07-windows.md). revision/hash/license/source가 검증된 값이 아니면 설치 불가.
/// URL을 floating latest로 두지 않는다.
/// </summary>
public sealed partial record ModelManifest
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("revision")] public required string Revision { get; init; }
    [JsonPropertyName("files")] public required IReadOnlyList<ModelFile> Files { get; init; }
    [JsonPropertyName("license")] public required string License { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("languages")] public IReadOnlyList<string> Languages { get; init; } = [];
    /// <summary>detector / translator / ocr-manga / ocr-ppocr(정보용, 코드는 id로 고른다).</summary>
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("input")] public JsonElement? Input { get; init; }
    [JsonPropertyName("output")] public JsonElement? Output { get; init; }
    [JsonPropertyName("runtime")] public JsonElement? Runtime { get; init; }
    /// <summary>무엇을 어떻게 검증했는지(해시 출처, 실제 실행 여부, 품질 검수 여부). UI·About에 그대로 보인다.</summary>
    [JsonPropertyName("validation")] public string? Validation { get; init; }

    public long TotalBytes => Files.Sum(f => Math.Max(0, f.Bytes));

    /// <summary>모든 필수 값이 placeholder가 아닌지(실제 검증은 설치 시 sha256 비교).</summary>
    public bool IsVerified => VerificationProblem() is null;

    public string? VerificationProblem()
    {
        if (IsPlaceholder(Revision)) return "revision not pinned";
        if (IsPlaceholder(License)) return "license not verified";
        if (IsPlaceholder(Source) || Source.Contains("/main/", StringComparison.OrdinalIgnoreCase) || Source.EndsWith("/main", StringComparison.OrdinalIgnoreCase))
            return "source not pinned";
        if (Files.Count == 0) return "no files";
        foreach (var f in Files)
        {
            if (f.Bytes <= 0) return $"{f.Name}: size missing";
            if (!Sha256Pattern().IsMatch(f.Sha256)) return $"{f.Name}: sha256 missing";
            if (f.Url is { } url && (IsPlaceholder(url) || url.Contains("/main/", StringComparison.OrdinalIgnoreCase))) return $"{f.Name}: url not pinned";
        }
        return null;
    }

    public string UrlFor(ModelFile file) => file.Url ?? Source.TrimEnd('/') + "/" + file.Name;

    /// <summary>output 이름 같은 문자열 설정을 읽는다.</summary>
    public string? OutputString(string name) =>
        Output is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public string? InputString(string name) =>
        Input is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>runtime의 정수 설정(contextSize 등). 없거나 정수가 아니면 null.</summary>
    public int? RuntimeInt(string name) =>
        Runtime is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    private static bool IsPlaceholder(string? s) =>
        string.IsNullOrWhiteSpace(s) || s.Contains("REQUIRED", StringComparison.OrdinalIgnoreCase) || s.Contains("VALIDATE", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    public static ModelManifest Parse(string json) =>
        JsonSerializer.Deserialize<ModelManifest>(json) ?? throw new InvalidDataException("empty manifest");
}

/// <summary>
/// 기본 manifest 목록. revision·크기·sha256을 고정했다(검증 범위는 각 manifest의 validation).
/// 앱은 모델을 포함하지 않으며, 사용자가 번역 패널/실험 설정에서 다운로드를 시작할 때만 받는다.
/// 검증한 manifest JSON을 %LOCALAPPDATA%/Panely/Models/manifests/&lt;id&gt;.json에 두면 같은 id를 대체한다.
/// </summary>
public static class ModelCatalog
{
    public const string DetectorId = "comic-text-and-bubble-detector";
    /// <summary>번역 모델(Gemma 4 E2B, GGUF). 말투·문맥까지 한 번에 처리한다(ADR 0008).</summary>
    public const string TranslatorId = "gemma-4-e2b-it-q4_0";
    /// <summary>이전 버전의 M2M100 언어팩. 시작할 때 설치 폴더를 지운다.</summary>
    public static IReadOnlyList<string> ObsoleteIds { get; } = ["m2m100-418m-int8", "m2m100-418m-fp32"];
    /// <summary>일본어 만화 OCR(manga-ocr).</summary>
    public const string MangaOcrId = "manga-ocr-int8";
    /// <summary>중국어·영어·한국어 OCR과 글자 줄 탐지, 언어 감지(PP-OCRv5).</summary>
    public const string PpOcrId = "ppocr-v5-mobile";

    public static IReadOnlyList<ModelManifest> BuiltIn { get; } =
    [
        ModelManifest.Parse("""
        {
          "id": "comic-text-and-bubble-detector",
          "revision": "16e8a622f91fabc6b5b65c96d32d1183f8843546",
          "files": [{"name": "detector.onnx", "bytes": 168481531, "sha256": "065744e91c0594ad8663aa8b870ce3fb27222942eded5a3cc388ce23421bd195"}],
          "license": "Apache-2.0 (model card license at this revision)",
          "source": "https://huggingface.co/ogkalu/comic-text-and-bubble-detector/resolve/16e8a622f91fabc6b5b65c96d32d1183f8843546",
          "languages": [],
          "mode": "detector",
          "input": {"name": "images", "targetSizes": "orig_target_sizes", "layout": "NCHW", "size": [1,3,640,640], "rgbRange": [0,1], "resize": "stretch"},
          "output": {"labels": "labels", "scores": "scores", "boxes": "boxes", "scoresAreSigmoid": true, "boxFormat": "postprocessed-xyxy"},
          "runtime": {"cpu": true, "gpu": false},
          "validation": "sha256 matches the Hub LFS object; downloaded and run on CPU with ONNX Runtime 1.31 (outputs labels/scores/boxes, boxes absolute xyxy in orig_target_sizes space). Not compared box-by-box with the original PyTorch model."
        }
        """),
        ModelManifest.Parse("""
        {
          "id": "gemma-4-e2b-it-q4_0",
          "revision": "675cff42a74c774d6cb76f76d8eacb49b48c9b93",
          "files": [{"name": "gemma-4-E2B_q4_0-it.gguf", "bytes": 3349516256, "sha256": "fa401b55b07ee70a54c6dae3903c783a6e65064312529ea57175cb5f8dec6634"}],
          "license": "Apache-2.0 (Gemma 4, model card license at this revision)",
          "source": "https://huggingface.co/google/gemma-4-E2B-it-qat-q4_0-gguf/resolve/675cff42a74c774d6cb76f76d8eacb49b48c9b93",
          "validation": "sha256 matches the Hub LFS object published by Google. Ran in-process with LLamaSharp 0.30.0 (Vulkan and CPU) next to the ONNX Runtime DirectML detector; translated a 298-page Japanese webtoon (146 balloons) ja→ko with no missing lines. In the developer's side-by-side review it read much better than M2M100 (one reviewer, not a golden set).",
          "languages": ["ja", "zh-Hans", "zh-Hant", "ko", "en"],
          "mode": "translator",
          "input": {"model": "gemma-4-E2B_q4_0-it.gguf"},
          "runtime": {"contextSize": 8192, "gpuLayers": 99}
        }
        """),
        ModelManifest.Parse("""
        {
          "id": "manga-ocr-int8",
          "revision": "c68e56437a2e0b13a7b1f4efa0ffce76efd0478d",
          "files": [
            {"name": "encoder_model_int8.onnx", "bytes": 87074595, "sha256": "0eaf2b867292a44700ce38ef028b90639a2e36fc4c18c2bcdd1de7409488adb3"},
            {"name": "decoder_model_int8.onnx", "bytes": 29675736, "sha256": "3ff0d4c34c4a66613d98ff93e0b17f22a7b09dfbeec195c3e4d6f595af3a6b6c"},
            {"name": "vocab.txt", "bytes": 30216, "sha256": "5cb5c5586d98a2f331d9f8828e4586479b0611bfba5d8c3b6dadffc84d6a36a3"}
          ],
          "license": "Apache-2.0 (kha-white/manga-ocr-base; ONNX conversion ogkalu/manga-ocr-onnx, model card license at this revision)",
          "source": "https://huggingface.co/ogkalu/manga-ocr-onnx/resolve/c68e56437a2e0b13a7b1f4efa0ffce76efd0478d",
          "validation": "sha256 matches the Hub LFS objects and the hashes registered by ogkalu2/comic-translate. Run on Japanese webtoon crops with ONNX Runtime 1.24 (DirectML/CPU).",
          "languages": ["ja"],
          "mode": "ocr-manga",
          "input": {"size": [1, 3, 224, 224], "grayscale": true, "normalize": "(x/255-0.5)/0.5"},
          "output": {"start": 2, "end": 3},
          "runtime": {"cpu": true, "gpu": true}
        }
        """),
        ModelManifest.Parse("""
        {
          "id": "ppocr-v5-mobile",
          "revision": "3b76726c3606c4af92f5133b359ed14e0d30a455",
          "files": [
            {"name": "ch_PP-OCRv5_mobile_det.onnx", "bytes": 4819576, "sha256": "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae"},
            {"name": "ch_PP-OCRv5_rec_mobile_infer.onnx", "bytes": 16631306, "sha256": "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5"},
            {"name": "en_PP-OCRv5_rec_mobile_infer.onnx", "bytes": 7872351, "sha256": "c3461add59bb4323ecba96a492ab75e06dda42467c9e3d0c18db5d1d21924be8"},
            {"name": "korean_PP-OCRv5_rec_mobile_infer.onnx", "bytes": 13488748, "sha256": "cd6e2ea50f6943ca7271eb8c56a877a5a90720b7047fe9c41a2e541a25773c9b"},
            {"name": "ppocrv5_dict.txt", "bytes": 74012, "sha256": "d1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b"},
            {"name": "ppocrv5_en_dict.txt", "bytes": 1416, "sha256": "e025a66d31f327ba0c232e03f407ae8d105e1e709e7ccb3f408aa778c24e70d6"},
            {"name": "ppocrv5_korean_dict.txt", "bytes": 47451, "sha256": "a88071c68c01707489baa79ebe0405b7beb5cca229f4fc94cc3ef992328802d7"}
          ],
          "license": "Apache-2.0 (PaddleOCR PP-OCRv5; ONNX conversion ogkalu/ppocr-v5-onnx, model card license at this revision)",
          "source": "https://huggingface.co/ogkalu/ppocr-v5-onnx/resolve/3b76726c3606c4af92f5133b359ed14e0d30a455",
          "validation": "sha256 matches the Hub LFS objects and ogkalu2/comic-translate's registry (dictionary hashes computed from the downloaded files).",
          "languages": ["zh-Hans", "zh-Hant", "ko", "en", "ja"],
          "mode": "ocr-ppocr",
          "input": {"detector": "ch_PP-OCRv5_mobile_det.onnx", "recHeight": 48},
          "output": {"decoder": "ctc"},
          "runtime": {"cpu": true, "gpu": true}
        }
        """),
    ];

    /// <summary>기본 목록 + Models/manifests 의 덮어쓰기(같은 id).</summary>
    public static IReadOnlyList<ModelManifest> Load(string modelsRoot)
    {
        var map = BuiltIn.ToDictionary(m => m.Id);
        var dir = Path.Combine(modelsRoot, "manifests");
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try { var m = ModelManifest.Parse(File.ReadAllText(file)); map[m.Id] = m; }
                catch (Exception) { /* 손상 manifest는 무시: 설치 불가로 남는다 */ }
            }
        }
        return map.Values.ToList();
    }
}
