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
            if (runtime.SupportsCache && items.Count > 0)
            {
                // KV 캐시 + 배치: 한 묶음의 말풍선을 한 번에 디코딩한다(문장마다 따로 돌리던 방식보다 훨씬 빠르다)
                try
                {
                    var texts = await Task.Run(() => runtime.TranslateBatch(items.Select(i => i.Text).ToList(), ModelLanguage(source), ModelLanguage(target), cancellationToken), cancellationToken).ConfigureAwait(false);
                    for (var i = 0; i < items.Count; i++) results.Add(new TranslationResultItem(items[i].Id, texts[i]));
                    return results;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    results.Clear(); // 배치가 실패하면 아래에서 한 문장씩 다시 시도한다
                }
            }
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

    /// <summary>history 끝 두 토큰 + token 으로 이루어진 3-gram이 이미 나왔는가(시작 토큰 두 개는 제외).</summary>
    internal static bool RepeatsTrigram(List<long> history, long token)
    {
        if (history.Count < 4) return false;
        long a = history[^2], b = history[^1];
        for (var i = 2; i + 2 < history.Count; i++)
            if (history[i] == a && history[i + 1] == b && history[i + 2] == token) return true;
        return false;
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

        /// <summary>실제로 쓰는 장치(GPU/CPU).</summary>
        public InferenceDevice Device { get; }

        /// <summary>
        /// 번역 모델 GPU 사용 여부. 실측(ONNX Runtime DirectML 1.24.4): M2M100 int8 decoder 세션 생성 중 네이티브 접근 위반(0xC0000005)으로
        /// 프로세스가 종료되어 기본값은 CPU다. 잡을 수 없는 오류라 시도도 하지 않는다.
        /// </summary>
        public static bool TranslatorPrefersGpu { get; set; }

        public Runtime(string revision, string encoderPath, string decoderPath, string spmPath, string vocabPath, string hiddenName, string logitsName, int beams, int maxNew)
        {
            Revision = revision;
            (_encoder, Device) = OnnxSessions.Create(encoderPath, TranslatorPrefersGpu);
            (_decoder, _) = OnnxSessions.Create(decoderPath, TranslatorPrefersGpu);
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

        /// <summary>optimum merged decoder(use_cache_branch + past_key_values)면 KV 캐시 경로를 쓴다.</summary>
        public bool SupportsCache => _decoder.InputMetadata.ContainsKey("use_cache_branch");

        /// <summary>
        /// 원문 → fairseq id. NFKC로 전각 숫자·영문(예: manga-ocr의 "４時間")을 반각으로 바꿔 모델이 숫자를 잃지 않게 한다.
        /// </summary>
        private List<long> Encode(string text, string source)
        {
            var normalized = text.Normalize(NormalizationForm.FormKC);
            var pieces = _spm.EncodeToTokens(normalized, out _, false, false, true, true);
            var ids = new List<long> { _langIds[source] };
            foreach (var p in pieces.Take(MaxSourceTokens)) ids.Add(_vocab.TryGetValue(p.Value, out var id) ? id : Unk);
            ids.Add(Eos);
            return ids;
        }

        /// <summary>
        /// 여러 문장을 한 번에: 인코더 1회, 디코더는 KV 캐시로 토큰마다 마지막 위치만 계산한다.
        /// beams=1이면 greedy(Fast), 그 이상이면 문장별 beam search(Accurate). 길이로 나눈 점수로 고른다.
        /// </summary>
        public string[] TranslateBatch(IReadOnlyList<string> texts, string source, string target, CancellationToken ct)
        {
            var n = texts.Count;
            var k = _beams;
            var seqs = texts.Select(t => Encode(t, source)).ToList();
            var srcLen = seqs.Max(s => s.Count);
            var ids = new long[n * srcLen];
            var mask = new long[n * srcLen];
            Array.Fill(ids, Pad);
            for (var i = 0; i < n; i++)
                for (var t = 0; t < seqs[i].Count; t++) { ids[i * srcLen + t] = seqs[i][t]; mask[i * srcLen + t] = 1; }

            float[] hiddenData;
            int dim;
            using (var encoded = _encoder.Run(
            [
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [n, srcLen])),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [n, srcLen])),
            ]))
            {
                var hidden = encoded.First(o => o.Name == _hiddenName).AsTensor<float>();
                dim = hidden.Dimensions[2];
                hiddenData = hidden.ToArray();
            }

            var rows = n * k;
            var rowHidden = new float[rows * srcLen * dim];
            var rowMask = new long[rows * srcLen];
            for (var r = 0; r < rows; r++)
            {
                Array.Copy(hiddenData, r / k * srcLen * dim, rowHidden, r * srcLen * dim, srcLen * dim);
                Array.Copy(mask, r / k * srcLen, rowMask, r * srcLen, srcLen);
            }
            var encoderHidden = new DenseTensor<float>(rowHidden, [rows, srcLen, dim]);
            var encoderMask = new DenseTensor<long>(rowMask, [rows, srcLen]);

            var layers = _decoder.InputMetadata.Keys.Count(name => name.StartsWith("past_key_values.", StringComparison.Ordinal) && name.EndsWith(".decoder.key", StringComparison.Ordinal));
            var heads = _decoder.InputMetadata["past_key_values.0.decoder.key"].Dimensions[1];
            var headDim = _decoder.InputMetadata["past_key_values.0.decoder.key"].Dimensions[3];
            var decoderPast = new DenseTensor<float>[layers * 2];
            var encoderPast = new DenseTensor<float>[layers * 2];

            // 시작: [</s>, __tgt__]
            var tokens = Enumerable.Range(0, rows).Select(_ => new List<long> { Eos, _langIds[target] }).ToArray();
            var scores = new double[rows];
            for (var r = 0; r < rows; r++) scores[r] = r % k == 0 ? 0 : double.NegativeInfinity;
            var finished = Enumerable.Range(0, n).Select(_ => new List<(List<long> Tokens, double Score)>()).ToArray();
            var done = new bool[n];

            var stepIds = new long[rows * 2];
            for (var r = 0; r < rows; r++) { stepIds[r * 2] = Eos; stepIds[r * 2 + 1] = _langIds[target]; }
            var logits = RunDecoder(new DenseTensor<long>(stepIds, [rows, 2]), encoderMask, encoderHidden, decoderPast, encoderPast, layers, heads, headDim, rows, firstStep: true);

            for (var step = 0; step < _maxNew; step++)
            {
                ct.ThrowIfCancellationRequested();
                var parents = new int[rows];
                var next = new long[rows];
                var newScores = new double[rows];
                var newTokens = new List<long>[rows];
                for (var item = 0; item < n; item++)
                {
                    var baseRow = item * k;
                    if (done[item])
                    {
                        for (var b = 0; b < k; b++) { parents[baseRow + b] = baseRow; next[baseRow + b] = Pad; newScores[baseRow + b] = double.NegativeInfinity; newTokens[baseRow + b] = tokens[baseRow]; }
                        continue;
                    }
                    var candidates = new List<(int Row, int Token, double Score)>();
                    for (var b = 0; b < k; b++)
                    {
                        var row = baseRow + b;
                        if (double.IsNegativeInfinity(scores[row])) continue;
                        var history = tokens[row];
                        foreach (var (token, lp) in TopK(LogSoftmax(logits[row]), Math.Max(3, k * 2 + 1)))
                        {
                            if (token == Pad || token == Bos) continue;
                            // 같은 3-gram 반복 금지(HF no_repeat_ngram_size=3과 같은 뜻): 잡음 원문에서 "는 는 는…"처럼 도는 것을 막는다
                            if (RepeatsTrigram(history, token)) continue;
                            candidates.Add((row, token, scores[row] + lp));
                        }
                    }
                    candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
                    var filled = 0;
                    foreach (var c in candidates)
                    {
                        if (filled == k) break;
                        if (c.Token == Eos)
                        {
                            if (finished[item].Count < k) finished[item].Add((new List<long>(tokens[c.Row]), c.Score));
                            continue;
                        }
                        var target2 = baseRow + filled++;
                        parents[target2] = c.Row;
                        next[target2] = c.Token;
                        newScores[target2] = c.Score;
                        newTokens[target2] = new List<long>(tokens[c.Row]) { c.Token };
                    }
                    for (; filled < k; filled++)
                    {
                        parents[baseRow + filled] = baseRow; next[baseRow + filled] = Pad;
                        newScores[baseRow + filled] = double.NegativeInfinity; newTokens[baseRow + filled] = tokens[baseRow];
                    }
                    // beam이 다 끝났거나 살아 있는 beam이 없으면 이 문장은 끝
                    if (finished[item].Count >= k || Enumerable.Range(baseRow, k).All(r => double.IsNegativeInfinity(newScores[r])))
                        done[item] = true;
                }
                tokens = newTokens;
                scores = newScores;
                if (done.All(d => d)) break;

                for (var i = 0; i < decoderPast.Length; i++) decoderPast[i] = Gather(decoderPast[i], parents);
                logits = RunDecoder(new DenseTensor<long>(next, [rows, 1]), encoderMask, encoderHidden, decoderPast, encoderPast, layers, heads, headDim, rows, firstStep: false);
            }

            var results = new string[n];
            for (var item = 0; item < n; item++)
            {
                var pool = finished[item].Select(f => (f.Tokens, f.Score)).ToList();
                if (pool.Count == 0)
                    pool = Enumerable.Range(item * k, k).Where(r => !double.IsNegativeInfinity(scores[r])).Select(r => (tokens[r], scores[r])).ToList();
                var best = pool.Count == 0 ? tokens[item * k] : pool.OrderByDescending(p => p.Item2 / Math.Max(1, p.Item1.Count)).First().Item1;
                results[item] = DecodeTokens(best.Skip(2));
            }
            return results;
        }

        /// <summary>디코더 1단계. 마지막 위치의 logits만 행별로 돌려주고 present.* 로 캐시를 갱신한다.</summary>
        private float[][] RunDecoder(DenseTensor<long> inputIds, DenseTensor<long> encoderMask, DenseTensor<float> encoderHidden,
            DenseTensor<float>[] decoderPast, DenseTensor<float>[] encoderPast, int layers, int heads, int headDim, int rows, bool firstStep)
        {
            var empty = new DenseTensor<float>(Array.Empty<float>(), [rows, heads, 0, headDim]);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", encoderMask),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHidden),
                NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<bool>(new[] { !firstStep }, [1])),
            };
            for (var l = 0; l < layers; l++)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.decoder.key", firstStep ? empty : decoderPast[l * 2]));
                inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.decoder.value", firstStep ? empty : decoderPast[l * 2 + 1]));
                inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.encoder.key", firstStep ? empty : encoderPast[l * 2]));
                inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.encoder.value", firstStep ? empty : encoderPast[l * 2 + 1]));
            }
            using var outputs = _decoder.Run(inputs);
            var map = outputs.ToDictionary(o => o.Name);
            for (var l = 0; l < layers; l++)
            {
                decoderPast[l * 2] = map[$"present.{l}.decoder.key"].AsTensor<float>().ToDenseTensor();
                decoderPast[l * 2 + 1] = map[$"present.{l}.decoder.value"].AsTensor<float>().ToDenseTensor();
                // 인코더 cross-attention 캐시는 첫 단계에서만 만들어지고 이후 그대로다
                if (firstStep)
                {
                    encoderPast[l * 2] = map[$"present.{l}.encoder.key"].AsTensor<float>().ToDenseTensor();
                    encoderPast[l * 2 + 1] = map[$"present.{l}.encoder.value"].AsTensor<float>().ToDenseTensor();
                }
            }
            var logits = map[_logitsName].AsTensor<float>();
            var length = logits.Dimensions[1];
            var vocab = logits.Dimensions[2];
            var result = new float[rows][];
            for (var r = 0; r < rows; r++)
            {
                var row = new float[vocab];
                for (var v = 0; v < vocab; v++) row[v] = logits[r, length - 1, v];
                result[r] = row;
            }
            return result;
        }


        /// <summary>beam 재배열: 새 행 r의 캐시 = 부모 행 parents[r]의 캐시.</summary>
        private static DenseTensor<float> Gather(DenseTensor<float> source, int[] parents)
        {
            var dims = source.Dimensions.ToArray();
            var block = dims[1] * dims[2] * dims[3];
            var data = new float[parents.Length * block];
            var span = source.Buffer.Span;
            for (var r = 0; r < parents.Length; r++) span.Slice(parents[r] * block, block).CopyTo(data.AsSpan(r * block, block));
            return new DenseTensor<float>(data, dims);
        }

        public string Translate(string text, string source, string target, CancellationToken cancellationToken)
        {
            var ids = Encode(text, source);

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
