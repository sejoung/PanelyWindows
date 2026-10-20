using System.Security.Cryptography;
using System.Text.Json;
using Panely.Core.Localization;
using Panely.Domain.Model;

namespace Panely.Core.Translation;

/// <summary>
/// Models/ 아래 설치 관리. 설치: 임시 파일 → sha256·크기 검증 → staging 디렉터리 원자적 publish.
/// 설치 후 로컬 OCR/번역은 네트워크를 쓰지 않는다. cache Clear 대상이 아니다.
/// </summary>
public sealed class ModelStore
{
    private readonly string _root;
    private readonly Func<HttpClient> _httpFactory;
    private readonly object _gate = new();
    private IReadOnlyList<ModelManifest> _manifests;
    private readonly Dictionary<string, double> _downloading = new();

    public ModelStore(string modelsRoot, Func<HttpClient>? httpFactory = null, IReadOnlyList<ModelManifest>? manifests = null)
    {
        _root = modelsRoot;
        _httpFactory = httpFactory ?? (() => new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
        _manifests = manifests ?? ModelCatalog.Load(modelsRoot);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ModelManifest> Manifests => _manifests;

    public ModelManifest? Find(string id) => _manifests.FirstOrDefault(m => m.Id == id);

    public void Reload()
    {
        _manifests = ModelCatalog.Load(_root);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string InstallDirectory(ModelManifest m) => Path.Combine(_root, m.Id, Sanitize(m.Revision));

    public bool IsInstalled(string id) => Find(id) is { } m && IsInstalled(m);

    public bool IsInstalled(ModelManifest m)
    {
        if (!m.IsVerified) return false;
        var dir = InstallDirectory(m);
        var marker = Path.Combine(dir, "installed.json");
        if (!File.Exists(marker)) return false;
        foreach (var f in m.Files)
        {
            var path = Path.Combine(dir, f.Name);
            if (!File.Exists(path) || new FileInfo(path).Length != f.Bytes) return false;
        }
        return true;
    }

    public double? DownloadProgress(string id)
    {
        lock (_gate) return _downloading.TryGetValue(id, out var p) ? p : null;
    }

    /// <summary>설치된 모델 파일 경로(설치 안 됐으면 null).</summary>
    public string? FilePath(string id, string fileName)
    {
        var m = Find(id);
        if (m is null || !IsInstalled(m)) return null;
        return Path.Combine(InstallDirectory(m), fileName);
    }

    public async Task InstallAsync(string id, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var m = Find(id) ?? throw new InvalidOperationException($"Unknown model package {id}.");
        if (m.VerificationProblem() is { } problem)
            throw new InvalidOperationException(Loc.T("This model package can't be installed because it isn't verified yet (%@).", problem));
        if (IsInstalled(m)) return;

        lock (_gate)
        {
            if (_downloading.ContainsKey(id)) throw new InvalidOperationException(Loc.T("Downloading…"));
            _downloading[id] = 0;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        var staging = Path.Combine(_root, ".staging", $"{m.Id}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            using var http = _httpFactory();
            long done = 0;
            var total = m.TotalBytes;
            var reportedPercent = 0;
            foreach (var f in m.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(staging, f.Name);
                var part = target + ".part";
                using (var response = await http.GetAsync(m.UrlFor(f), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[1 << 16];
                    long fileBytes = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        sha.AppendData(buffer, 0, read);
                        fileBytes += read;
                        done += read;
                        if (fileBytes > f.Bytes) throw new InvalidDataException(Loc.T("%@ is larger than its manifest says.", f.Name));
                        progress?.Report(new InstallProgress(done, total, f.Name));
                        lock (_gate) _downloading[id] = total > 0 ? done / (double)total : 0;
                        // 설정·번역 패널이 진행률을 따라 다시 그리도록 1%가 바뀔 때마다 알린다(팩 하나에 최대 100번)
                        var percent = total > 0 ? (int)(done * 100 / total) : 0;
                        if (percent != reportedPercent)
                        {
                            reportedPercent = percent;
                            Changed?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    var hash = Convert.ToHexString(sha.GetHashAndReset());
                    if (fileBytes != f.Bytes || !hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(Loc.T("%@ failed its checksum check.", f.Name));
                }
                File.Move(part, target, true);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "installed.json"),
                JsonSerializer.Serialize(new { m.Id, m.Revision, installedAt = DateTimeOffset.UtcNow }), cancellationToken).ConfigureAwait(false);
            var final = InstallDirectory(m);
            if (Directory.Exists(final)) Directory.Delete(final, true);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            Directory.Move(staging, final);
        }
        finally
        {
            lock (_gate) _downloading.Remove(id);
            TryDelete(staging);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>강제 종료 후 남은 staging 정리.</summary>
    public void CleanupStaging() => TryDelete(Path.Combine(_root, ".staging"));

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Sanitize(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        return new string(chars);
    }
}

/// <summary>
/// 언어팩 공급자. Windows에서 "팩"은 (1) 번역 모델 variant(모드별, 다국어 하나), (2) 선택적 탐지 모델,
/// (3) Windows OCR 언어(앱이 설치하지 않고 Windows 설정에서 추가)로 구성된다.
/// </summary>
public sealed class LanguagePackManager : ILanguagePackProvider
{
    private readonly ModelStore _store;
    private readonly ITextRecognizer _recognizer;
    private readonly ITranslator _translator;

    public LanguagePackManager(ModelStore store, ITextRecognizer recognizer, ITranslator translator)
    {
        _store = store; _recognizer = recognizer; _translator = translator;
        _store.Changed += (_, _) => PacksChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? PacksChanged;

    public static string TranslatorPackFor(TranslationMode mode) =>
        mode == TranslationMode.Fast ? ModelCatalog.TranslatorFastId : ModelCatalog.TranslatorAccurateId;

    public Capability GetCapability(string source, string target, TranslationMode mode)
    {
        if (source == target || !TranslationLanguages.IsSupported(source) || !TranslationLanguages.IsSupported(target))
            return Capability.Unavailable(Loc.T("This language pair isn't supported"));
        // 필요한 팩 중 받는 중인 것이 있으면 먼저 알린다. 번역·탐지 팩은 글자 인식 팩보다 먼저 받으므로
        // 글자 인식 상태부터 보면 그동안 패널이 다운로드 중으로 보이지 않는다.
        foreach (var id in RequiredPacks(source, mode))
            if (_store.DownloadProgress(id) is { } p) return new Capability(CapabilityStatus.Downloading, null, p);
        var ocr = _recognizer.GetCapability(source);
        // 글자 인식 팩이 없으면(다운로드 필요·불가) 번역 모델이 있어도 바로 실행할 수 없다
        if (!ocr.IsReady) return ocr;
        return _translator.GetCapability(source, target, mode);
    }

    public IReadOnlyList<string> MissingPacks(string source, string target, TranslationMode mode)
        => RequiredPacks(source, mode).Where(id => _store.Find(id) is { IsVerified: true } m && !_store.IsInstalled(m)).ToList();

    /// <summary>번역 모델, 말풍선 탐지, 만화 OCR 팩(일본어 manga-ocr + PP-OCR, 그 외 PP-OCR) 순. PP-OCR은 언어 감지에도 쓴다.</summary>
    private static IEnumerable<string> RequiredPacks(string source, TranslationMode mode)
        => new[] { TranslatorPackFor(mode), ModelCatalog.DetectorId }.Concat(ModelTextRecognizer.PacksFor(source)).Distinct();

    public Task InstallAsync(string packId, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
        => _store.InstallAsync(packId, progress, cancellationToken);
}
