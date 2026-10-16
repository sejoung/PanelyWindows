using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Panely.Core.Localization;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Core.Translation;

/// <summary>
/// M2M100-418M ONNX 번역기(Apple Translation 대체 후보, 미검증).
/// Fast = int8 quantized encoder/decoder, Accurate = fp32 encoder/decoder + beam search. 서로 다른 파일이며 같은 실행을 두 이름으로 보이지 않는다.
/// 토크나이저: sentencepiece.bpe.model(조각) + vocab.json(조각→fairseq id), 언어 토큰 __xx__.
/// 한계: 모델은 "zh" 하나만 알며 zh-Hans/zh-Hant 구분을 하지 않는다(번체 출력 품질 미검증).
/// </summary>
public sealed class M2M100Translator : ITranslator, IDisposable
{
    private static readonly string[] FairseqLanguages =
        "af,am,ar,ast,az,ba,be,bg,bn,br,bs,ca,ceb,cs,cy,da,de,el,en,es,et,fa,ff,fi,fr,fy,ga,gd,gl,gu,ha,he,hi,hr,ht,hu,hy,id,ig,ilo,is,it,ja,jv,ka,kk,km,kn,ko,lb,lg,ln,lo,lt,lv,mg,mk,ml,mn,mr,ms,my,ne,nl,no,ns,oc,or,pa,pl,ps,pt,ro,ru,sd,si,sk,sl,so,sq,sr,ss,su,sv,sw,ta,th,tl,tn,tr,uk,ur,uz,vi,wo,xh,yi,yo,zh,zu".Split(',');

    private const int Bos = 0, Pad = 1, Eos = 2, Unk = 3;
    private const int MaxSourceTokens = 200;

    private readonly ModelStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<TranslationMode, Runtime> _runtimes = new();

    public M2M100Translator(ModelStore store) => _store = store;

    public string Name => "M2M100-418M (ONNX Runtime CPU)";

    public string Revision(TranslationMode mode)
    {
        var m = _store.Find(LanguagePackManager.TranslatorPackFor(mode));
        return m is null ? "none" : $"{m.Id}@{m.Revision}";
    }

    public static string ModelLanguage(string code) => code switch
    {
        TranslationLanguages.ChineseSimplified or TranslationLanguages.ChineseTraditional => "zh",
        _ => code,
    };

    public Capability GetCapability(string source, string target, TranslationMode mode)
    {
        if (source == target || !TranslationLanguages.IsSupported(source) || !TranslationLanguages.IsSupported(target))
            return Capability.Unavailable(Loc.T("This language pair isn't supported"));
        if (ModelLanguage(source) == ModelLanguage(target))
            return Capability.Unavailable(Loc.T("%@ can't be translated into %@.", Loc.LanguageName(source), Loc.LanguageName(target)));
        var m = _store.Find(LanguagePackManager.TranslatorPackFor(mode));
        if (m is null) return Capability.Unavailable(Loc.T("The translation model isn't available."));
        if (_store.IsInstalled(m)) return Capability.Ready;
        if (!m.IsVerified)
            return Capability.Unavailable(Loc.T("The translation model isn't bundled or verified for this PC yet, so this build can't translate. (%@)", m.VerificationProblem() ?? ""));
        return new Capability(CapabilityStatus.Supported, Loc.T("Download the %@ → %@ language pack to translate.", Loc.LanguageName(source), Loc.LanguageName(target)));
    }

    public async Task<IReadOnlyList<TranslationResultItem>> TranslateAsync(IReadOnlyList<TranslationRequestItem> items, string source, string target, TranslationMode mode, CancellationToken cancellationToken)
    {
        var capability = GetCapability(source, target, mode);
        if (!capability.IsReady) throw new InvalidOperationException(capability.Reason);
        var results = new List<TranslationResultItem>(items.Count);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runtime = EnsureRuntime(mode);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var text = await Task.Run(() => runtime.Translate(item.Text, ModelLanguage(source), ModelLanguage(target), cancellationToken), cancellationToken).ConfigureAwait(false);
                    results.Add(new TranslationResultItem(item.Id, text));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    results.Add(new TranslationResultItem(item.Id, null, ex.Message));
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        return results;
    }

    private Runtime EnsureRuntime(TranslationMode mode)
    {
        var m = _store.Find(LanguagePackManager.TranslatorPackFor(mode))!;
        if (_runtimes.TryGetValue(mode, out var existing) && existing.Revision == m.Revision) return existing;
        existing?.Dispose();
        string FilePath(string key) => _store.FilePath(m.Id, m.InputString(key) ?? throw new InvalidDataException($"manifest input.{key} missing"))
            ?? throw new FileNotFoundException(key);
        var beams = 1;
        var maxNew = 128;
        if (m.Runtime is { ValueKind: JsonValueKind.Object } rt)
        {
            if (rt.TryGetProperty("beams", out var b) && b.TryGetInt32(out var bv)) beams = Math.Clamp(bv, 1, 8);
            if (rt.TryGetProperty("maxNewTokens", out var n) && n.TryGetInt32(out var nv)) maxNew = Math.Clamp(nv, 8, 512);
        }
        var runtime = new Runtime(m.Revision, FilePath("encoder"), FilePath("decoder"), FilePath("tokenizer"), FilePath("vocab"),
            m.OutputString("encoderHidden") ?? "last_hidden_state", m.OutputString("logits") ?? "logits", beams, maxNew);
        _runtimes[mode] = runtime;
        return runtime;
    }

    public void Dispose()
    {
        foreach (var r in _runtimes.Values) r.Dispose();
        _runtimes.Clear();
    }

    private sealed class Runtime : IDisposable
    {
        private readonly InferenceSession _encoder;
        private readonly InferenceSession _decoder;
        private readonly SentencePieceTokenizer _spm;
        private readonly Dictionary<string, int> _vocab;
        private readonly Dictionary<int, string> _inverse;
        private readonly Dictionary<string, int> _langIds = new();
        private readonly string _hiddenName;
        private readonly string _logitsName;
        private readonly int _beams;
        private readonly int _maxNew;

        public string Revision { get; }

        public Runtime(string revision, string encoderPath, string decoderPath, string spmPath, string vocabPath, string hiddenName, string logitsName, int beams, int maxNew)
        {
            Revision = revision;
            var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            _encoder = new InferenceSession(encoderPath, options);
            _decoder = new InferenceSession(decoderPath, options);
            using (var stream = File.OpenRead(spmPath)) _spm = SentencePieceTokenizer.Create(stream, false, false, null);
            _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabPath)) ?? [];
            _inverse = _vocab.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
            var size = _vocab.Count;
            for (var i = 0; i < FairseqLanguages.Length; i++)
            {
                var token = $"__{FairseqLanguages[i]}__";
                var id = _vocab.TryGetValue(token, out var existing) ? existing : size + i;
                _langIds[FairseqLanguages[i]] = id;
                _inverse.TryAdd(id, token);
            }
            _hiddenName = hiddenName;
            _logitsName = logitsName;
            _beams = beams;
            _maxNew = maxNew;
        }

        public string Translate(string text, string source, string target, CancellationToken cancellationToken)
        {
            var pieces = _spm.EncodeToTokens(text, out _, false, false, true, true);
            var ids = new List<long> { _langIds[source] };
            foreach (var p in pieces.Take(MaxSourceTokens)) ids.Add(_vocab.TryGetValue(p.Value, out var id) ? id : Unk);
            ids.Add(Eos);

            var srcLen = ids.Count;
            var inputIds = new DenseTensor<long>(ids.ToArray(), [1, srcLen]);
            var mask = new DenseTensor<long>(Enumerable.Repeat(1L, srcLen).ToArray(), [1, srcLen]);
            using var encoded = _encoder.Run(
            [
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            ]);
            var hidden = encoded.First(o => o.Name == _hiddenName).AsTensor<float>();
            var dim = hidden.Dimensions[2];
            var hiddenData = hidden.ToArray();

            var beams = new List<(List<long> Tokens, double Score, bool Done)> { (new List<long> { Eos, _langIds[target] }, 0, false) };
            for (var step = 0; step < _maxNew; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var active = beams.Where(b => !b.Done).ToList();
                if (active.Count == 0) break;
                var len = active[0].Tokens.Count;
                var batch = active.Count;
                var decIds = new long[batch * len];
                for (var b = 0; b < batch; b++) active[b].Tokens.CopyTo(0, decIds, b * len, len);
                var encHidden = new float[batch * srcLen * dim];
                for (var b = 0; b < batch; b++) Array.Copy(hiddenData, 0, encHidden, b * srcLen * dim, srcLen * dim);
                var encMask = Enumerable.Repeat(1L, batch * srcLen).ToArray();
                using var outputs = _decoder.Run(
                [
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(decIds, [batch, len])),
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", new DenseTensor<long>(encMask, [batch, srcLen])),
                    NamedOnnxValue.CreateFromTensor("encoder_hidden_states", new DenseTensor<float>(encHidden, [batch, srcLen, dim])),
                ]);
                var logits = outputs.First(o => o.Name == _logitsName).AsTensor<float>();
                var vocab = logits.Dimensions[2];
                var candidates = new List<(List<long> Tokens, double Score, bool Done)>();
                for (var b = 0; b < batch; b++)
                {
                    var row = new float[vocab];
                    for (var v = 0; v < vocab; v++) row[v] = logits[b, len - 1, v];
                    var logSoftmax = LogSoftmax(row);
                    foreach (var (token, lp) in TopK(logSoftmax, _beams))
                    {
                        if (token == Pad || token == Bos) continue;
                        var tokens = new List<long>(active[b].Tokens) { token };
                        candidates.Add((tokens, active[b].Score + lp, token == Eos));
                    }
                }
                var finished = beams.Where(b => b.Done);
                beams = candidates.Concat(finished)
                    .OrderByDescending(c => c.Score / Math.Pow(c.Tokens.Count, 1.0))
                    .Take(_beams).ToList();
                if (beams.All(b => b.Done)) break;
            }
            var best = beams.OrderByDescending(b => b.Score / Math.Pow(b.Tokens.Count, 1.0)).First().Tokens;
            return DecodeTokens(best.Skip(2));
        }

        private string DecodeTokens(IEnumerable<long> ids)
        {
            var sb = new StringBuilder();
            foreach (var id in ids)
            {
                if (id == Eos) break;
                if (!_inverse.TryGetValue((int)id, out var piece) || piece.StartsWith("__", StringComparison.Ordinal) || piece is "<s>" or "<pad>" or "</s>" or "<unk>") continue;
                sb.Append(piece);
            }
            return sb.ToString().Replace('▁', ' ').Trim();
        }

        private static double[] LogSoftmax(float[] row)
        {
            var max = row.Max();
            double sum = 0;
            foreach (var v in row) sum += Math.Exp(v - max);
            var log = Math.Log(sum) + max;
            return row.Select(v => v - log).ToArray();
        }

        private static IEnumerable<(int Token, double LogProb)> TopK(double[] values, int k)
        {
            var best = new List<(int, double)>(k + 1);
            for (var i = 0; i < values.Length; i++)
            {
                if (best.Count < k || values[i] > best[^1].Item2)
                {
                    best.Add((i, values[i]));
                    best.Sort((a, b) => b.Item2.CompareTo(a.Item2));
                    if (best.Count > k) best.RemoveAt(best.Count - 1);
                }
            }
            return best;
        }

        public void Dispose()
        {
            _encoder.Dispose();
            _decoder.Dispose();
        }
    }
}
