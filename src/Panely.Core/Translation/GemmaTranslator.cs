using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Panely.Core.Localization;
using Panely.Domain.Model;

namespace Panely.Core.Translation;

/// <summary>
/// Gemma 4 E2B(GGUF) 번역기. LLamaSharp(llama.cpp)로 앱 프로세스 안에서 돌린다(ADR 0008).
/// GPU는 Vulkan, 드라이버가 없거나 PANELY_ORT_DEVICE=cpu면 CPU. 말풍선 묶음을 앞 문맥과 함께 한 번에 번역하고,
/// 원문의 말투(존댓말·반말)와 이름을 이어 가도록 지시한다. 모델은 첫 번역 때 올리고 컨텍스트 하나를 계속 쓴다.
/// </summary>
public sealed partial class GemmaTranslator : ITranslator, IDisposable
{
    /// <summary>프롬프트·후처리를 바꾸면 올린다(캐시 identity에 들어간다).</summary>
    private const int PromptVersion = 1;

    private static readonly object NativeGate = new();
    private static bool _nativeConfigured;
    private static string? _device;

    private readonly ModelStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Runtime? _runtime;

    public GemmaTranslator(ModelStore store) => _store = store;

    /// <summary>모델을 올린 뒤에는 실제 장치(예: "Vulkan · NVIDIA GeForce RTX 4070 Ti SUPER", "CPU")를 함께 보인다.</summary>
    public string Name => _device is { } device ? $"Gemma 4 E2B (llama.cpp · {device})" : "Gemma 4 E2B (llama.cpp)";

    public string Revision => _store.Find(ModelCatalog.TranslatorId) is { } m ? $"{m.Id}@{m.Revision}+p{PromptVersion}" : "none";

    public Capability GetCapability(string source, string target)
    {
        if (source == target || !TranslationLanguages.IsSupported(source) || !TranslationLanguages.IsSupported(target))
            return Capability.Unavailable(Loc.T("This language pair isn't supported"));
        var m = _store.Find(ModelCatalog.TranslatorId);
        if (m is null) return Capability.Unavailable(Loc.T("The translation model isn't available."));
        if (_store.IsInstalled(m)) return Capability.Ready;
        if (!m.IsVerified)
            return Capability.Unavailable(Loc.T("The translation model isn't bundled or verified for this PC yet, so this build can't translate. (%@)", m.VerificationProblem() ?? ""));
        return new Capability(CapabilityStatus.Supported, Loc.T("Download the %@ → %@ language pack to translate.", Loc.LanguageName(source), Loc.LanguageName(target)));
    }

    public async Task<IReadOnlyList<TranslationResultItem>> TranslateAsync(IReadOnlyList<TranslationRequestItem> items, IReadOnlyList<TranslationContextLine> context,
        string source, string target, CancellationToken cancellationToken)
    {
        var capability = GetCapability(source, target);
        if (!capability.IsReady) throw new InvalidOperationException(capability.Reason);
        if (items.Count == 0) return [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runtime = _runtime ??= await Task.Run(LoadRuntime, cancellationToken).ConfigureAwait(false);
            var texts = new Dictionary<int, string>();
            try
            {
                var output = await Task.Run(() => runtime.Generate(BuildPrompt(items, context, source, target), MaxTokens(items), cancellationToken), cancellationToken).ConfigureAwait(false);
                foreach (var (n, text) in ParseLines(output)) if (n >= 1 && n <= items.Count) texts[n] = text;
                // 형식이 어긋나 빠진 줄은 한 줄씩 다시 묻는다
                for (var n = 1; n <= items.Count; n++)
                {
                    if (texts.ContainsKey(n)) continue;
                    var single = await Task.Run(() => runtime.Generate(BuildPrompt([items[n - 1]], context, source, target), MaxTokens([items[n - 1]]), cancellationToken), cancellationToken).ConfigureAwait(false);
                    if (ParseLines(single).FirstOrDefault(p => p.Number == 1) is { Text: { } text }) texts[n] = text;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return items.Select(i => new TranslationResultItem(i.Id, null, ex.Message)).ToList();
            }
            // 끝까지 돌아오지 않은 줄은 빈 결과: 스케줄러가 "번역하지 못한 말풍선"으로 원문을 보인다(ADR 0001)
            return items.Select((item, i) => new TranslationResultItem(item.Id, texts.TryGetValue(i + 1, out var t) ? Clean(t, target) : null)).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 모델을 올리고 번역과 같은 모양의 짧은 프롬프트로 몇 토큰을 생성한다. GPU(Vulkan)는 첫 생성 때 셰이더를 준비하느라 20초가량 걸리므로,
    /// 번역 작업이 시작될 때 분석(탐지·OCR)과 함께 돌려 첫 결과가 늦어지지 않게 한다.
    /// </summary>
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (_store.Find(ModelCatalog.TranslatorId) is not { } m || !_store.IsInstalled(m)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtime is not null) return;
            var runtime = _runtime = await Task.Run(LoadRuntime, cancellationToken).ConfigureAwait(false);
            await Task.Run(() => runtime.Generate(BuildPrompt([new TranslationRequestItem("0", "こんにちは")], [], TranslationLanguages.Japanese, TranslationLanguages.Korean), 4, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Runtime LoadRuntime()
    {
        var m = _store.Find(ModelCatalog.TranslatorId) ?? throw new InvalidOperationException(Loc.T("The translation model isn't available."));
        var path = _store.FilePath(m.Id, m.InputString("model") ?? m.Files[0].Name) ?? throw new InvalidOperationException(Loc.T("The translation model isn't available."));
        var gpu = !OnnxSessions.GpuDisabledByUser;
        ConfigureNative(gpu);
        var parameters = new ModelParams(path)
        {
            ContextSize = (uint)(m.RuntimeInt("contextSize") ?? 8192),
            GpuLayerCount = gpu ? m.RuntimeInt("gpuLayers") ?? 99 : 0,
        };
        LLamaWeights weights;
        try
        {
            weights = LLamaWeights.LoadFromFile(parameters);
        }
        catch (Exception) when (parameters.GpuLayerCount > 0)
        {
            // GPU 메모리 부족 등으로 올리지 못하면 CPU로 다시 올린다
            parameters.GpuLayerCount = 0;
            _device = "CPU";
            weights = LLamaWeights.LoadFromFile(parameters);
        }
        try
        {
            var context = weights.CreateContext(parameters);
            _device ??= "CPU";
            return new Runtime(weights, context);
        }
        catch
        {
            weights.Dispose();
            throw;
        }
    }

    /// <summary>네이티브 라이브러리는 프로세스에서 한 번만 고른다(첫 네이티브 호출 전).</summary>
    private static void ConfigureNative(bool gpu)
    {
        lock (NativeGate)
        {
            if (_nativeConfigured) return;
            _nativeConfigured = true;
            try
            {
                NativeLibraryConfig.All.WithVulkan(gpu).WithCuda(false).WithAutoFallback(true).WithLogCallback(OnNativeLog);
            }
            catch (InvalidOperationException)
            {
                // 이미 다른 경로로 로드됐으면 그 설정을 쓴다
            }
        }
    }

    private static void OnNativeLog(LLamaLogLevel level, string message)
    {
        if (DeviceLine().Match(message) is { Success: true } match) _device = $"Vulkan · {match.Groups[1].Value.Trim()}";
    }

    [GeneratedRegex(@"using device Vulkan\d+ \(([^)]+)\)")]
    private static partial Regex DeviceLine();

    private static int MaxTokens(IReadOnlyList<TranslationRequestItem> items) => 64 + items.Sum(i => 16 + 3 * i.Text.Length);

    /// <summary>Gemma 4 대화 형식(llama-server /apply-template, 추론 끔과 같은 형식). LLamaSharp의 템플릿 기능은 Gemma 4 Jinja 템플릿을 모른다.</summary>
    internal static string BuildPrompt(IReadOnlyList<TranslationRequestItem> items, IReadOnlyList<TranslationContextLine> context, string source, string target)
    {
        var from = Loc.LanguageNameIn("en", source);
        var to = Loc.LanguageNameIn("en", target);
        var sb = new StringBuilder();
        sb.Append("<|turn>user\n");
        sb.Append(CultureInfo.InvariantCulture, $"You translate {from} comic dialogue into natural {to}.\n");
        sb.Append("Rules:\n");
        sb.Append("- Translate every numbered line. Output exactly one line per input line, as \"[n] translation\", nothing else.\n");
        sb.Append("- Keep each speaker's speech level: polite speech stays polite, plain or casual speech stays casual.");
        sb.Append(target switch
        {
            TranslationLanguages.Korean => " In Korean, polite speech is 존댓말 (-요/-습니다) and casual speech is 반말.",
            TranslationLanguages.Japanese => " In Japanese, polite speech uses です/ます.",
            _ => "",
        });
        sb.Append('\n');
        sb.Append("- Keep names, honorifics and terms consistent with the earlier lines.\n");
        sb.Append("- \" / \" inside a line marks a line break in the balloon; keep it.\n");
        sb.Append("- Do not add explanations.\n");
        if (context.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\nEarlier lines ({from} → {to}), for context only:\n");
            foreach (var line in context) sb.Append(OneLine(line.Source)).Append(" → ").Append(OneLine(line.Translation)).Append('\n');
        }
        sb.Append("\nTranslate:\n");
        for (var i = 0; i < items.Count; i++) sb.Append(CultureInfo.InvariantCulture, $"[{i + 1}] {OneLine(items[i].Text)}\n");
        sb.Append("<turn|>\n<|turn>model\n");
        return sb.ToString();
    }

    private static string OneLine(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Replace("\n", " / ", StringComparison.Ordinal);

    /// <summary>"[n] 번역" 줄을 읽는다(앞뒤 공백·빈 줄·다른 줄은 무시).</summary>
    internal static IReadOnlyList<(int Number, string Text)> ParseLines(string output)
    {
        var list = new List<(int, string)>();
        foreach (Match m in OutputLine().Matches(output))
            if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && m.Groups[2].Value.Trim() is { Length: > 0 } text)
                list.Add((n, text));
        return list;
    }

    // 줄 안의 공백만 본다(\s는 줄바꿈을 넘어 다음 줄을 번역으로 가져간다)
    [GeneratedRegex(@"^[ \t]*\[(\d+)\][ \t]*(.*?)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex OutputLine();

    /// <summary>" / "를 줄바꿈으로 되돌리고, 일본어가 아닌 번역에 남은 장음 기호 줄표(ーー)를 줄표로 바꾼다.</summary>
    internal static string Clean(string text, string target)
    {
        var result = text.Replace(" / ", "\n", StringComparison.Ordinal).Trim();
        if (target != TranslationLanguages.Japanese) result = LongDash().Replace(result, m => new string('—', m.Length));
        return result;
    }

    [GeneratedRegex("ー{2,}")]
    private static partial Regex LongDash();

    public void Dispose()
    {
        _runtime?.Dispose();
        _runtime = null;
        _gate.Dispose();
    }

    /// <summary>모델과 컨텍스트 하나. 요청마다 KV 캐시를 비우고 greedy로 생성한다.</summary>
    private sealed class Runtime(LLamaWeights weights, LLamaContext context) : IDisposable
    {
        public string Generate(string prompt, int maxTokens, CancellationToken cancellationToken)
        {
            context.NativeHandle.MemoryClear(true);
            var tokens = context.Tokenize(prompt, true, true);
            if (tokens.Length + maxTokens > context.ContextSize)
                throw new InvalidOperationException($"Prompt too long for the model context ({tokens.Length} tokens).");

            var batch = new LLamaBatch();
            var position = 0;
            var batchSize = Math.Max(1, (int)context.BatchSize);
            for (var start = 0; start < tokens.Length; start += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch.Clear();
                var end = Math.Min(tokens.Length, start + batchSize);
                for (var i = start; i < end; i++) batch.Add(tokens[i], position++, LLamaSeqId.Zero, i == tokens.Length - 1);
                Decode(batch);
            }

            using var sampler = new GreedySamplingPipeline();
            var decoder = new StreamingTokenDecoder(context);
            var text = new StringBuilder();
            var logitIndex = batch.TokenCount - 1;
            for (var n = 0; n < maxTokens; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = sampler.Sample(context.NativeHandle, logitIndex);
                if (token.IsEndOfGeneration(context.Vocab)) break;
                decoder.Add(token);
                text.Append(decoder.Read());
                if (text.ToString().EndsWith("<turn|>", StringComparison.Ordinal)) { text.Length -= "<turn|>".Length; break; }
                batch.Clear();
                batch.Add(token, position++, LLamaSeqId.Zero, true);
                Decode(batch);
                logitIndex = 0;
            }
            return text.ToString();
        }

        private void Decode(LLamaBatch batch)
        {
            var result = context.Decode(batch);
            if (result != DecodeResult.Ok) throw new InvalidOperationException($"llama.cpp decode failed: {result}");
        }

        public void Dispose()
        {
            context.Dispose();
            weights.Dispose();
        }
    }
}
