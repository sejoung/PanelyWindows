using Panely.Core.Abstractions;
using Panely.Core.Translation;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Tests.Translation;

internal sealed class MemoryPageSource(IReadOnlyDictionary<string, byte[]> data) : IPageSource
{
    public bool Disposed { get; private set; }
    public Task<byte[]> ReadAsync(PageEntry page, CancellationToken cancellationToken) => Task.FromResult(data[page.RelativePath]);
    public Task<byte[]> ReadPrefixAsync(PageEntry page, int maxBytes, CancellationToken cancellationToken) => Task.FromResult(data[page.RelativePath].Take(maxBytes).ToArray());
    public string? TryGetFilePath(PageEntry page) => null;
    public void Dispose() => Disposed = true;
}

internal static class FakeBooks
{
    public static readonly Guid SourceId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    public static OpenedBook Create(int pages, string title = "Sample", string ext = "png", bool nested = false, string? innerPath = null)
    {
        var bookRef = new BookRef(SourceId, innerPath);
        var entries = new List<PageEntry>();
        var data = new Dictionary<string, byte[]>();
        for (var i = 0; i < pages; i++)
        {
            var rel = $"{i + 1:000}.{ext}";
            entries.Add(new PageEntry { Index = i, RelativePath = rel, FileName = rel, Extension = ext, PageId = PageIds.Make(bookRef.Key, rel, "sig") });
            data[rel] = [(byte)i, 1, 2, 3, 4];
        }
        return new OpenedBook
        {
            Source = new SourceRef { Id = SourceId, Path = @"C:\Comics\Sample.cbz", Kind = SourceKind.Archive, Title = title },
            Book = bookRef,
            SeriesKey = "series:test",
            Title = title,
            Pages = entries,
            Data = new MemoryPageSource(data),
            WatchPath = @"C:\Comics\Sample.cbz",
            DisplayPath = "Sample.cbz",
            LibraryPath = @"C:\Comics\Sample.cbz",
            IsNestedCacheBook = nested,
        };
    }
}

internal sealed class FakeBookAccess(Func<OpenedBook> factory) : IBookAccess
{
    public int Opens;
    public Task<OpenedBook> OpenAsync(BookTarget target, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Opens);
        return Task.FromResult(factory());
    }
}

internal sealed class FakePixels(Func<int, SizeD>? size = null) : IPagePixelSource
{
    public Task<PagePixels> GetPixelsAsync(OpenedBook book, PageEntry page, CancellationToken cancellationToken)
    {
        var s = (size ?? (_ => new SizeD(400, 600)))(page.Index);
        var px = PagePixels.Blank((int)s.Width, (int)s.Height);
        // 페이지 index를 첫 픽셀에 기록해 recognizer가 구별할 수 있게 한다
        px.Bgra[0] = (byte)page.Index;
        return Task.FromResult(px);
    }

    public Task<SizeD> GetSizeAsync(OpenedBook book, PageEntry page, CancellationToken cancellationToken)
        => Task.FromResult((size ?? (_ => new SizeD(400, 600)))(page.Index));
}

/// <summary>페이지마다 가로 한국어/일본어 줄 하나를 돌려준다.</summary>
internal sealed class FakeRecognizer : ITextRecognizer
{
    public string Name => "fake-ocr";
    public HashSet<string> Languages { get; } = ["ja", "ko", "en"];
    public int Calls;
    public Func<PagePixels, string, IReadOnlyList<TextRegion>>? Produce { get; set; }
    public TimeSpan Delay { get; set; }

    public Capability GetCapability(string language) => Languages.Contains(language) ? Capability.Ready : Capability.Unavailable("no ocr");

    public async Task<IReadOnlyList<TextRegion>> RecognizeAsync(PagePixels image, string language, IReadOnlyList<RectD>? regions, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        if (Produce is not null) return Produce(image, language);
        if (language != "ja") return [];
        return [new TextRegion($"こんにちは{image.Bgra[0]}", new RectD(50, 50, 120, 24), false, 24)];
    }
}

internal sealed class FakeDetector : IBalloonDetector
{
    public string Name => "none";
    public string Revision => "none";
    public Capability Capability => Capability.Unavailable("none");
    public Task<IReadOnlyList<DetectedRegion>?> DetectAsync(PagePixels image, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DetectedRegion>?>(null);
}

internal sealed class FakePacks(Func<Capability>? capability = null) : ILanguagePackProvider
{
    public Capability GetCapability(string source, string target) => capability?.Invoke() ?? Capability.Ready;
    public IReadOnlyList<string> MissingPacks(string source, string target) => [];
    public Task InstallAsync(string packId, IProgress<InstallProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
    public event EventHandler? PacksChanged { add { } remove { } }
}

internal static class FakeProviders
{
    public static TranslationProviders Create(FakeRecognizer? recognizer = null, ITranslator? translator = null, Func<Capability>? packs = null) => new()
    {
        Recognizer = recognizer ?? new FakeRecognizer(),
        Detector = new FakeDetector(),
        Translator = translator ?? new PseudoTranslator { Delay = TimeSpan.Zero },
        Packs = new FakePacks(packs),
    };
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "panely-tr-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

internal sealed class FakeRenderer : ITranslatedPageRenderer
{
    public int Calls;
    public TimeSpan Delay { get; set; }
    public async Task<(byte[] Bytes, string Extension)> RenderAsync(byte[] original, string extension, IReadOnlyList<PatchLayout> patches, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        return (System.Text.Encoding.UTF8.GetBytes($"rendered:{patches.Count}:{string.Join("|", patches.Select(p => p.Text))}"), extension == "png" ? "png" : "jpg");
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }
}
