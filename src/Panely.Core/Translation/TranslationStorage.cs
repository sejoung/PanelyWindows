using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Core.Translation;

/// <summary>domain.schema.json의 TranslatedPage/PageEdits 형식 JSON 변환.</summary>
public static class TranslationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions IndentedOptions = new(Options) { WriteIndented = true };

    private sealed record RectDto(double X, double Y, double Width, double Height)
    {
        public static RectDto From(RectD r) => new(r.X, r.Y, r.Width, r.Height);
        public RectD To() => new(X, Y, Width, Height);
    }

    private sealed record BackgroundDto(double Red, double Green, double Blue, bool IsUniform)
    {
        public static BackgroundDto From(BubbleBackground b) => new(b.Red, b.Green, b.Blue, b.IsUniform);
        public BubbleBackground To() => new(Red, Green, Blue, IsUniform);
    }

    private sealed record BubbleDto(int Id, RectDto Rect, double GlyphSize, bool IsVertical, string Source, string? Translation, BackgroundDto Background, RectDto? BalloonRect);

    private sealed record PageDto(int SchemaVersion, string PageId, string Source, string Target, string Scope, string ProviderRevision,
        int AnalysisVersion, double ImageWidth, double ImageHeight, bool IsComplete, List<BubbleDto> Bubbles);

    private sealed record AddedDto(double GlyphSize, bool IsVertical, BackgroundDto Background);

    private sealed record EditDto(string RecognizedSource, RectDto Rect, bool IsHidden, string? CorrectedSource,
        Dictionary<string, string> Retranslations, Dictionary<string, string> Translations, AddedDto? Added);

    private sealed record EditsDto(int SchemaVersion, string PageId, string Scope, List<EditDto> Bubbles);

    public static string ScopeName(TranslationScope s) => s == TranslationScope.Strip ? "strip" : "page";
    public static TranslationScope ParseScope(string s) => s == "strip" ? TranslationScope.Strip : TranslationScope.Page;

    public static string Serialize(TranslatedPage page) => JsonSerializer.Serialize(new PageDto(
        TranslatedPage.SchemaVersion, page.PageId, page.SourceLanguage, page.TargetLanguage, ScopeName(page.Scope),
        page.ProviderRevision, page.AnalysisVersion, page.ImageWidth, page.ImageHeight, page.IsComplete,
        page.Bubbles.Select(b => new BubbleDto(b.Id, RectDto.From(b.Rect), b.GlyphSize, b.IsVertical, b.Source, b.Translation,
            BackgroundDto.From(b.Background), b.BalloonRect is { } br ? RectDto.From(br) : null)).ToList()), Options);

    public static TranslatedPage DeserializePage(string json)
    {
        var dto = JsonSerializer.Deserialize<PageDto>(json, Options) ?? throw new InvalidDataException("empty page");
        if (dto.SchemaVersion != TranslatedPage.SchemaVersion) throw new InvalidDataException("unsupported schema");
        if (!TranslationLanguages.IsSupported(dto.Source) || !TranslationLanguages.IsSupported(dto.Target)) throw new InvalidDataException("language");
        if (dto.ImageWidth <= 0 || dto.ImageHeight <= 0 || dto.Bubbles is null) throw new InvalidDataException("size");
        return new TranslatedPage
        {
            PageId = dto.PageId,
            SourceLanguage = dto.Source,
            TargetLanguage = dto.Target,
            Scope = ParseScope(dto.Scope),
            ProviderRevision = dto.ProviderRevision,
            AnalysisVersion = dto.AnalysisVersion,
            ImageWidth = dto.ImageWidth,
            ImageHeight = dto.ImageHeight,
            IsComplete = dto.IsComplete,
            Bubbles = dto.Bubbles.Select(b => new TranslatedBubble
            {
                Id = b.Id,
                Rect = b.Rect.To(),
                GlyphSize = b.GlyphSize,
                IsVertical = b.IsVertical,
                Source = b.Source,
                Translation = b.Translation,
                Background = b.Background.To(),
                BalloonRect = b.BalloonRect?.To(),
            }).ToList(),
        };
    }

    public static string Serialize(PageEdits edits) => JsonSerializer.Serialize(new EditsDto(
        PageEdits.SchemaVersion, edits.PageId, ScopeName(edits.Scope),
        edits.Bubbles.Select(e => new EditDto(e.RecognizedSource, RectDto.From(e.Rect), e.IsHidden, e.CorrectedSource,
            new Dictionary<string, string>(e.Retranslations), new Dictionary<string, string>(e.Translations),
            e.Added is { } a ? new AddedDto(a.GlyphSize, a.IsVertical, BackgroundDto.From(a.Background)) : null)).ToList()),
        IndentedOptions);

    public static PageEdits DeserializeEdits(string json)
    {
        var dto = JsonSerializer.Deserialize<EditsDto>(json, Options) ?? throw new InvalidDataException("empty edits");
        if (dto.SchemaVersion != PageEdits.SchemaVersion) throw new InvalidDataException("unsupported schema");
        return new PageEdits
        {
            PageId = dto.PageId,
            Scope = ParseScope(dto.Scope),
            Bubbles = (dto.Bubbles ?? []).Select(e => new BubbleEdit
            {
                RecognizedSource = e.RecognizedSource,
                Rect = e.Rect.To(),
                IsHidden = e.IsHidden,
                CorrectedSource = e.CorrectedSource,
                Retranslations = e.Retranslations ?? new Dictionary<string, string>(),
                Translations = e.Translations ?? new Dictionary<string, string>(),
                Added = e.Added is { } a ? new AddedBalloon(a.GlyphSize, a.IsVertical, a.Background.To()) : null,
            }).ToList(),
        };
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>같은 디렉터리 temp에 쓰고 디스크까지 내린 뒤 replace(전원이 나가도 잘린 파일이 제자리에 남지 않게).</summary>
    public static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(content));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}

/// <summary>캐시 identity. 번역 모델·프롬프트가 바뀌면 ProviderRevision이 달라져 이전 결과는 쓰지 않는다.</summary>
public readonly record struct TranslationCacheKey(string PageId, string Source, string Target, TranslationScope Scope, int AnalysisVersion, string ProviderRevision)
{
    public string Identity => $"{PageId}\n{Source}>{Target}\n{TranslationJson.ScopeName(Scope)}\n{AnalysisVersion}\n{ProviderRevision}";
}

/// <summary>
/// 완료 쪽 번역 캐시(Cache/translation/v1). 손상 파일은 miss(지운다). 일시적으로 읽지 못한 파일은 miss지만 남겨 둔다. 완료 쪽만 저장.
/// Clear는 generation을 올려 in-flight 결과가 다시 기록되지 않게 한다.
/// </summary>
public sealed class TranslationCache
{
    public const int FormatVersion = 1;
    private readonly string _root;
    private int _generation;

    public TranslationCache(string translationCacheRoot)
    {
        _root = Path.Combine(translationCacheRoot, $"v{FormatVersion}");
    }

    public int Generation => Volatile.Read(ref _generation);

    public event EventHandler? Cleared;

    private string PathFor(TranslationCacheKey key)
    {
        var hash = TranslationJson.Hash(key.Identity);
        return Path.Combine(_root, hash[..2], hash + ".json");
    }

    /// <summary>
    /// 파일이 있고 끝까지 쓰인 것으로 보이는가. CachedPages가 책 전체에 부르므로 파싱하지 않고 끝 두 바이트만 본다
    /// (PageDto는 들여쓰기 없이 bubbles 배열로 끝나므로 온전한 파일은 언제나 "]}"로 끝난다).
    /// 읽어 봐야 아는 손상은 TryLoad가 잡는다. 지금 열 수 없으면 있다고 답하고 판단을 TryLoad에 맡긴다.
    /// </summary>
    public bool Contains(TranslationCacheKey key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return false;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = RandomAccess.GetLength(handle);
            if (length < 2) return false;
            Span<byte> tail = stackalloc byte[2];
            return RandomAccess.Read(handle, tail, length - 2) == 2 && tail[0] == (byte)']' && tail[1] == (byte)'}';
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public TranslatedPage? TryLoad(TranslationCacheKey key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        string json;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            json = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // 일시적 오류(백신·공유 위반 등): 멀쩡한 파일이므로 지우지 않는다
        }
        try
        {
            var page = TranslationJson.DeserializePage(json);
            if (page.PageId == key.PageId && page.TargetLanguage == key.Target && page.IsComplete) return page;
        }
        catch (Exception)
        {
            // 아래에서 지운다
        }
        TryDelete(path); // 손상 캐시는 miss로 보고 다시 생성한다
        return null;
    }

    /// <returns>generation이 바뀌었거나 미완료 쪽이면 false(기록하지 않음).</returns>
    public bool Store(TranslationCacheKey key, TranslatedPage page, int generation)
    {
        // 완료된 쪽의 translation=null 풍선은 "번역기가 글을 내지 못함"이다(06: translation nullable)
        if (!page.IsComplete || generation != Generation) return false;
        TranslationJson.AtomicWrite(PathFor(key), TranslationJson.Serialize(page));
        if (generation != Generation) { TryDelete(PathFor(key)); return false; }
        return true;
    }

    public long SizeBytes()
    {
        if (!Directory.Exists(_root)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; } catch (IOException) { }
        }
        return total;
    }

    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// 사용자 수정 저장(Data/edits). 캐시 정리·모드 변경·재번역과 분리되어 사용자가 명시적으로 지울 때까지 남는다.
/// </summary>
public sealed class PageEditsStore
{
    private readonly string _root;
    private readonly object _gate = new();
    private Dictionary<string, PageEdits>? _index;

    public PageEditsStore(string editsRoot) => _root = editsRoot;

    public event EventHandler<string>? Changed;

    private static string IndexKey(string pageId, TranslationScope scope) => $"{TranslationJson.ScopeName(scope)}|{pageId}";

    private string PathFor(string pageId, TranslationScope scope) => Path.Combine(_root, TranslationJson.Hash(IndexKey(pageId, scope)) + ".json");

    private Dictionary<string, PageEdits> Index
    {
        get
        {
            lock (_gate)
            {
                if (_index is not null) return _index;
                var map = new Dictionary<string, PageEdits>(StringComparer.Ordinal);
                if (Directory.Exists(_root))
                {
                    foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
                    {
                        try
                        {
                            var edits = TranslationJson.DeserializeEdits(File.ReadAllText(file));
                            map[IndexKey(edits.PageId, edits.Scope)] = edits;
                        }
                        catch (Exception)
                        {
                            // 손상 파일은 덮어쓰지 않고 남겨 둔다(사용자 데이터 보존)
                        }
                    }
                }
                return _index = map;
            }
        }
    }

    public PageEdits? Get(string pageId, TranslationScope scope)
    {
        lock (_gate) return Index.TryGetValue(IndexKey(pageId, scope), out var e) ? e : null;
    }

    public void Save(PageEdits edits)
    {
        lock (_gate)
        {
            var key = IndexKey(edits.PageId, edits.Scope);
            var path = PathFor(edits.PageId, edits.Scope);
            if (edits.Bubbles.Count == 0)
            {
                Index.Remove(key);
                try { File.Delete(path); } catch (IOException) { }
            }
            else
            {
                Index[key] = edits;
                TranslationJson.AtomicWrite(path, TranslationJson.Serialize(edits));
            }
        }
        Changed?.Invoke(this, edits.PageId);
    }

    public int EditedPageCount
    {
        get { lock (_gate) return Index.Count; }
    }

    public IReadOnlyList<PageEdits> ForBook(string bookKey)
    {
        var prefix = bookKey + "|";
        lock (_gate) return Index.Values.Where(e => e.PageId.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    public int EditedBubbleCount(string bookKey) => ForBook(bookKey).Sum(e => e.Bubbles.Count);

    /// <summary>책 전체 수정 되돌리기.</summary>
    public void DeleteBook(string bookKey)
    {
        foreach (var e in ForBook(bookKey)) Save(e with { Bubbles = [] });
    }

    /// <summary>전체 수정 삭제(확인 후 호출).</summary>
    public void DeleteAll()
    {
        lock (_gate)
        {
            _index = new Dictionary<string, PageEdits>(StringComparer.Ordinal);
            try { if (Directory.Exists(_root)) foreach (var f in Directory.EnumerateFiles(_root, "*.json")) File.Delete(f); } catch (IOException) { }
        }
        Changed?.Invoke(this, string.Empty);
    }

    /// <summary>AI 일괄 적용 Undo용 snapshot.</summary>
    public IReadOnlyDictionary<(string PageId, TranslationScope Scope), PageEdits?> Snapshot(IEnumerable<(string PageId, TranslationScope Scope)> pages)
        => pages.Distinct().ToDictionary(p => p, p => Get(p.PageId, p.Scope));

    public void Restore(IReadOnlyDictionary<(string PageId, TranslationScope Scope), PageEdits?> snapshot)
    {
        foreach (var ((pageId, scope), edits) in snapshot)
            Save(edits ?? PageEditsEditor.Empty(pageId, scope));
    }
}
