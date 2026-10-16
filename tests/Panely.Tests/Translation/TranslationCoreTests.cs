using System.IO.Compression;
using System.Text.Json;
using Panely.Core.Abstractions;
using Panely.Core.Localization;
using Panely.Core.Translation;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Tests.Translation;

public class TranslationStorageTests
{
    [Fact]
    public void ExampleTranslationPageRoundTrips()
    {
        var page = TranslationJson.DeserializePage(TrVectors.ExampleText("translation-page.json"));
        Assert.Equal("ja", page.SourceLanguage);
        Assert.Equal(TranslationScope.Page, page.Scope);
        var b = Assert.Single(page.Bubbles);
        Assert.Equal("안녕하세요", b.Translation);
        Assert.Equal(new RectD(70, 90, 220, 320), b.BalloonRect);
        var again = TranslationJson.DeserializePage(TranslationJson.Serialize(page));
        Assert.Equal(page.Bubbles[0], again.Bubbles[0]);
        // 스키마 필드 이름 유지
        using var doc = JsonDocument.Parse(TranslationJson.Serialize(page));
        foreach (var name in new[] { "schemaVersion", "pageId", "source", "target", "scope", "mode", "providerRevision", "analysisVersion", "imageWidth", "imageHeight", "isComplete", "bubbles" })
            Assert.True(doc.RootElement.TryGetProperty(name, out _), name);
        Assert.True(doc.RootElement.GetProperty("bubbles")[0].GetProperty("background").TryGetProperty("isUniform", out _));
    }

    [Fact]
    public void ExamplePageEditsRoundTrip()
    {
        var edits = TranslationJson.DeserializeEdits(TrVectors.ExampleText("page-edits.json"));
        var e = Assert.Single(edits.Bubbles);
        Assert.Equal("안녕", e.Translations["ko"]);
        Assert.Null(e.Added);
        var again = TranslationJson.DeserializeEdits(TranslationJson.Serialize(edits));
        Assert.Equal("こんにちは", again.Bubbles[0].RecognizedSource);
        Assert.Equal(e.Rect, again.Bubbles[0].Rect);
    }

    [Fact]
    public void ExampleEditOverridesExampleMachineTranslation()
    {
        var page = TranslationJson.DeserializePage(TrVectors.ExampleText("translation-page.json"));
        var edits = TranslationJson.DeserializeEdits(TrVectors.ExampleText("page-edits.json"));
        var d = Assert.Single(EditResolver.Resolve(page.Bubbles, edits, "ko"));
        Assert.Equal("안녕", d.Text);
        Assert.Equal(BubbleTextState.UserTranslation, d.State);
    }

    [Fact]
    public void CacheStoresOnlyCompletePagesAndRespectsGeneration()
    {
        using var dir = new TempDir();
        var cache = new TranslationCache(dir.Path);
        var page = TranslationJson.DeserializePage(TrVectors.ExampleText("translation-page.json"));
        var key = new TranslationCacheKey(page.PageId, "ja", "ko", TranslationScope.Page, TranslationMode.Fast, 1, "rev");
        Assert.False(cache.Store(key, page with { IsComplete = false }, cache.Generation));
        var stale = cache.Generation;
        cache.Clear();
        Assert.False(cache.Store(key, page, stale));
        Assert.True(cache.Store(key, page, cache.Generation));
        Assert.NotNull(cache.TryLoad(key));
        // identity: 다른 모드/scope/revision은 miss
        Assert.Null(cache.TryLoad(key with { Mode = TranslationMode.Accurate }));
        Assert.Null(cache.TryLoad(key with { Scope = TranslationScope.Strip }));
        Assert.Null(cache.TryLoad(key with { ProviderRevision = "rev2" }));
        Assert.True(cache.SizeBytes() > 0);
    }

    [Fact]
    public void CorruptCacheIsMiss()
    {
        using var dir = new TempDir();
        var cache = new TranslationCache(dir.Path);
        var page = TranslationJson.DeserializePage(TrVectors.ExampleText("translation-page.json"));
        var key = new TranslationCacheKey(page.PageId, "ja", "ko", TranslationScope.Page, TranslationMode.Fast, 1, "rev");
        Assert.True(cache.Store(key, page, cache.Generation));
        foreach (var f in Directory.EnumerateFiles(dir.Path, "*.json", SearchOption.AllDirectories)) File.WriteAllText(f, "{ broken");
        Assert.Null(cache.TryLoad(key));
        Assert.False(cache.Contains(key));
    }

    [Fact]
    public void EditsStoreIsSeparateFromCacheAndPersists()
    {
        using var dir = new TempDir();
        var store = new PageEditsStore(Path.Combine(dir.Path, "edits"));
        var edits = PageEditsEditor.Empty("source:x#A|001.png|s", TranslationScope.Page);
        (edits, var i) = PageEditsEditor.Ensure(edits, "src", new RectD(0, 0, 10, 10));
        edits = PageEditsEditor.SetTranslation(edits, i, "ko", "mine");
        store.Save(edits);
        var other = PageEditsEditor.Empty("source:y|001.png|s", TranslationScope.Page);
        (other, var j) = PageEditsEditor.Ensure(other, "a", new RectD(0, 0, 10, 10));
        store.Save(PageEditsEditor.SetHidden(other, j, true));

        var reloaded = new PageEditsStore(Path.Combine(dir.Path, "edits"));
        Assert.Equal("mine", reloaded.Get("source:x#A|001.png|s", TranslationScope.Page)!.Bubbles[0].Translations["ko"]);
        Assert.Null(reloaded.Get("source:x#A|001.png|s", TranslationScope.Strip));
        Assert.Equal(2, reloaded.EditedPageCount);
        Assert.Single(reloaded.ForBook("source:x#A"));
        Assert.Equal(1, reloaded.EditedBubbleCount("source:y"));

        var snapshot = reloaded.Snapshot([("source:x#A|001.png|s", TranslationScope.Page), ("new|p|s", TranslationScope.Page)]);
        reloaded.DeleteBook("source:x#A");
        Assert.Null(reloaded.Get("source:x#A|001.png|s", TranslationScope.Page));
        reloaded.Restore(snapshot);
        Assert.NotNull(reloaded.Get("source:x#A|001.png|s", TranslationScope.Page));
        reloaded.DeleteAll();
        Assert.Equal(0, new PageEditsStore(Path.Combine(dir.Path, "edits")).EditedPageCount);
    }
}

public class ModelManifestTests
{
    [Fact]
    public void BuiltInManifestsArePinnedButNotInstalledByDefault()
    {
        Assert.Equal(3, ModelCatalog.BuiltIn.Count);
        Assert.All(ModelCatalog.BuiltIn, m =>
        {
            Assert.Null(m.VerificationProblem());
            Assert.Matches("^[0-9a-f]{40}$", m.Revision);
            Assert.All(m.Files, f => Assert.Contains(m.Revision, m.UrlFor(f)));
            Assert.False(string.IsNullOrWhiteSpace(m.Validation));
        });
        using var dir = new TempDir();
        var store = new ModelStore(dir.Path, manifests: ModelCatalog.BuiltIn);
        Assert.False(store.IsInstalled(ModelCatalog.DetectorId));
        Assert.False(store.IsInstalled(ModelCatalog.TranslatorFastId));
    }

    [Fact]
    public async Task PlaceholderManifestIsNotInstallable()
    {
        using var dir = new TempDir();
        var placeholder = new ModelManifest
        {
            Id = "p",
            Revision = "PINNED_REVISION_REQUIRED",
            License = "MIT",
            Source = "https://example.invalid/x",
            Mode = "detector",
            Files = [new ModelFile { Name = "m.onnx", Bytes = 0, Sha256 = "REQUIRED" }],
        };
        var store = new ModelStore(dir.Path, manifests: [placeholder]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync("p", null, CancellationToken.None));
    }

    [Fact]
    public async Task VerifiedManifestInstallsWithChecksumAndRejectsMismatch()
    {
        using var dir = new TempDir();
        var payload = "hello model"u8.ToArray();
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        ModelManifest Manifest(string hash) => new()
        {
            Id = "test-model",
            Revision = "abc123",
            License = "MIT",
            Source = "https://example.invalid/pinned/abc123",
            Mode = "detector",
            Files = [new ModelFile { Name = "model.onnx", Bytes = payload.Length, Sha256 = hash }],
        };
        var handler = new StubHandler(payload);
        var bad = new ModelStore(dir.Path, () => new HttpClient(handler), [Manifest(new string('0', 64))]);
        await Assert.ThrowsAsync<InvalidDataException>(() => bad.InstallAsync("test-model", null, CancellationToken.None));
        Assert.False(bad.IsInstalled("test-model"));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, ".staging")) && Directory.EnumerateFileSystemEntries(Path.Combine(dir.Path, ".staging")).Any());

        var good = new ModelStore(dir.Path, () => new HttpClient(handler), [Manifest(sha)]);
        var reports = new List<InstallProgress>();
        await good.InstallAsync("test-model", new Progress<InstallProgress>(reports.Add), CancellationToken.None);
        Assert.True(good.IsInstalled("test-model"));
        Assert.NotNull(good.FilePath("test-model", "model.onnx"));
    }

    [Fact]
    public void FloatingMainSourceIsRejected()
    {
        var m = new ModelManifest
        {
            Id = "x",
            Revision = "abc",
            License = "MIT",
            Source = "https://huggingface.co/a/b/resolve/main",
            Mode = "detector",
            Files = [new ModelFile { Name = "m", Bytes = 1, Sha256 = new string('a', 64) }],
        };
        Assert.Equal("source not pinned", m.VerificationProblem());
    }

    private sealed class StubHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    }
}

public class TranslationProviderTests
{
    [Fact]
    public void SpeechLevelReportsUnavailable()
    {
        var c = new UnavailableSpeechLevelCorrector();
        Assert.Equal(CapabilityStatus.Unavailable, c.GetCapability("ja", "ko").Status);
        Assert.False(string.IsNullOrWhiteSpace(c.GetCapability("ja", "ko").Reason));
    }

    [Fact]
    public void M2M100WithoutInstalledModelNeedsDownload()
    {
        using var dir = new TempDir();
        var t = new M2M100Translator(new ModelStore(dir.Path, manifests: ModelCatalog.BuiltIn));
        var capability = t.GetCapability("ja", "ko", TranslationMode.Fast);
        Assert.Equal(CapabilityStatus.Supported, capability.Status);
        Assert.False(capability.IsReady);
        Assert.Equal(CapabilityStatus.Unavailable, t.GetCapability("zh-Hans", "zh-Hant", TranslationMode.Fast).Status);
        Assert.NotEqual(t.Revision(TranslationMode.Fast), t.Revision(TranslationMode.Accurate));
    }

    [Fact]
    public void DeveloperProviderIsOffByDefault()
    {
        if (Environment.GetEnvironmentVariable(DeveloperProviders.EnvironmentVariable) is null)
            Assert.False(DeveloperProviders.Enabled);
    }

    [Fact]
    public void PromptUsesCatalogKey()
    {
        Assert.True(Loc.Has(AiChatPrompt.PromptKey));
        var snapshot = new AiChatSnapshot("b", "Sample", "ja", "ko", [new AiChatEntry("p1-1", 0, 0, "a", "b")]);
        var prompt = AiChatPrompt.Build(snapshot, "Sample.txt");
        Assert.Contains("Sample.txt", prompt);
        Assert.Contains("[id]", prompt);
    }

    [Fact]
    public void WindowsTranslationStringsHaveKorean()
    {
        foreach (var key in new[] { "Developer test provider", "Shift+Enter for a new line", "The disk is full.", "Preparing translation models — the first time takes 1–2 minutes…" })
        {
            Assert.True(Loc.Has(key), key);
            Assert.NotEqual(Loc.TIn("en", key), Loc.TIn("ko", key));
        }
        Assert.DoesNotContain("⌘", Loc.TIn("en", "Show Jobs (⌥⌘J)"));
        Assert.Contains("Ctrl+Alt+J", Loc.TIn("ko", "Show Jobs (⌥⌘J)"));
    }

    [Fact]
    public void ValidTranslationRules()
    {
        Assert.False(TranslationScheduler.IsValidTranslation("こんにちは", "", "ko"));
        Assert.False(TranslationScheduler.IsValidTranslation("こんにちは", "こんにちは", "ko"));
        Assert.True(TranslationScheduler.IsValidTranslation("こんにちは", "안녕", "ko"));
        Assert.True(TranslationScheduler.IsValidTranslation("OK", "OK", "ko"));
    }

    [Fact]
    public void PagePixelsCropResizeBlit()
    {
        var p = PagePixels.Blank(10, 10, 0);
        var c = p.Crop(-5, -5, 10, 10);
        Assert.Equal(255, c.Bgra[0]);           // 밖은 흰색
        Assert.Equal(0, c.Bgra[(9 * 10 + 9) * 4]); // 안쪽은 원본
        var big = PagePixels.Blank(20, 20);
        big.Blit(p, 5, 5);
        Assert.Equal(0, big.Bgra[(6 * 20 + 6) * 4]);
        Assert.Equal(255, big.Bgra[0]);
        Assert.Equal(40, p.Resize(40, 30).Width);
    }

    [Fact]
    public void BackgroundSamplerDetectsDarkUniform()
    {
        var dark = PagePixels.Blank(100, 100, 20);
        var (bg, _) = BackgroundSampler.Sample(dark, new RectD(30, 30, 40, 20), 20, null);
        Assert.True(bg.IsUniform);
        Assert.True(bg.UsesWhiteText);
    }
}

public class TranslationSchedulerTests
{
    [Fact]
    public async Task TranslatesFromCurrentPageAndCachesCompletePages()
    {
        using var dir = new TempDir();
        var cache = new TranslationCache(dir.Path);
        var book = FakeBooks.Create(4);
        var order = new List<int>();
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(), cache, new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(4)));
        scheduler.PageUpdated += (_, u) => { if (u.Page.IsComplete) lock (order) order.Add(u.PageIndex); };
        scheduler.SetCurrent(book.BookKey, "ko", 2);
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page, focusPage: 2);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
        lock (order) Assert.Equal([2, 3, 0, 1], order.Distinct());
        var info = scheduler.Get(book.BookKey, "ko")!;
        Assert.Equal(4, info.CompletedPages);
        Assert.Equal(4, scheduler.CachedPages(book.Pages, "ja", "ko", TranslationScope.Page, TranslationMode.Fast).Count);
        var cached = scheduler.LoadCached(book.Pages[1].PageId, "ja", "ko", TranslationScope.Page, TranslationMode.Fast)!;
        Assert.Equal("[번역] こんにちは1", cached.Bubbles[0].Translation);
    }

    [Fact]
    public async Task PageWithMoreBubblesThanOneChunkCompletes()
    {
        // 회귀: 번역 묶음을 지연 열거하면서 목록을 갱신해 "Collection was modified"로 중단되던 문제
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer
        {
            Produce = (_, language) => language != "ja" ? [] :
                Enumerable.Range(0, 30).Select(i => new TextRegion($"こんにちは{i}", new RectD(20, 20 + i * 120, 160, 24), false, 24)).ToList(),
        };
        var book = FakeBooks.Create(1);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer), new TranslationCache(dir.Path), new FakePixels(_ => new SizeD(400, 4000)),
            new FakeBookAccess(() => FakeBooks.Create(1)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed);
        var info = scheduler.Get(book.BookKey, "ko")!;
        Assert.Equal(TranslationPhase.Completed, info.Phase);
        var page = scheduler.LoadCached(book.Pages[0].PageId, "ja", "ko", TranslationScope.Page, TranslationMode.Fast)!;
        Assert.True(page.Bubbles.Count > 8);
        Assert.All(page.Bubbles, b => Assert.NotNull(b.Translation));
    }

    [Fact]
    public async Task PartialResultsArriveBeforeTranslation()
    {
        using var dir = new TempDir();
        var book = FakeBooks.Create(1);
        var sawPending = false;
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(1)));
        scheduler.PageUpdated += (_, u) => { if (!u.Page.IsComplete && u.Page.Bubbles.Any(b => b.Translation is null)) sawPending = true; };
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
        Assert.True(sawPending);
    }

    [Fact]
    public async Task DetectsSourceLanguage()
    {
        using var dir = new TempDir();
        var book = FakeBooks.Create(2);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(2)));
        scheduler.Start(BookTarget.From(book), "ko", null, TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
        Assert.Equal("ja", scheduler.Get(book.BookKey, "ko")!.Source);
    }

    [Fact]
    public async Task UndetectableLanguageFailsWithReason()
    {
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer { Produce = (_, _) => [] };
        var book = FakeBooks.Create(2);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(2)));
        scheduler.Start(BookTarget.From(book), "ko", null, TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Failed);
        Assert.Equal(TranslationStopReason.CouldNotDetectLanguage, scheduler.Get(book.BookKey, "ko")!.Reason);
    }

    [Fact]
    public async Task MissingPackPausesWithNeedsDownload()
    {
        using var dir = new TempDir();
        var book = FakeBooks.Create(2);
        var providers = FakeProviders.Create(packs: () => new Capability(CapabilityStatus.Supported, "download"));
        await using var scheduler = new TranslationScheduler(providers, new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(2)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Paused);
        var info = scheduler.Get(book.BookKey, "ko")!;
        Assert.Equal(TranslationStopReason.NeedsLanguageDownload, info.Reason);
        Assert.False(info.UserPaused);
    }

    [Fact]
    public async Task UnsupportedPairFails()
    {
        using var dir = new TempDir();
        var book = FakeBooks.Create(1);
        var providers = FakeProviders.Create(packs: () => Capability.Unavailable("pair"));
        await using var scheduler = new TranslationScheduler(providers, new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(1)));
        scheduler.Start(BookTarget.From(book), "zh-Hant", "zh-Hans", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "zh-Hant")?.Phase == TranslationPhase.Failed);
        Assert.Equal(TranslationStopReason.UnsupportedPair, scheduler.Get(book.BookKey, "zh-Hant")!.Reason);
    }

    [Fact]
    public async Task UserPauseIsNotAutoResumedAndCurrentBookHasPriority()
    {
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer { Delay = TimeSpan.FromMilliseconds(30) };
        var bookA = FakeBooks.Create(6, "A", innerPath: "A");
        var bookB = FakeBooks.Create(3, "B", innerPath: "B");
        var completedOrder = new List<string>();
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer), new TranslationCache(dir.Path), new FakePixels(),
            new FakeBookAccess(() => FakeBooks.Create(6, "A", innerPath: "A")));
        scheduler.JobChanged += (_, i) => { if (i.Phase == TranslationPhase.Completed) lock (completedOrder) completedOrder.Add(i.Book.Title); };
        scheduler.Start(BookTarget.From(bookA), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(bookA.BookKey, "ko")?.CompletedPages >= 1);
        scheduler.Pause(bookA.BookKey, "ko");
        await Wait.UntilAsync(() => scheduler.Get(bookA.BookKey, "ko")?.IsActive == false);
        var paused = scheduler.Get(bookA.BookKey, "ko")!;
        Assert.Equal(TranslationPhase.Paused, paused.Phase);
        Assert.True(paused.UserPaused);
        scheduler.SetCurrent(bookA.BookKey, "ko", 0);
        await Task.Delay(150);
        Assert.Equal(TranslationPhase.Paused, scheduler.Get(bookA.BookKey, "ko")!.Phase);
        scheduler.Start(BookTarget.From(bookA), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(bookA.BookKey, "ko")?.Phase == TranslationPhase.Completed);
        _ = bookB;
    }

    [Fact]
    public async Task NestedBookPausesWhenLeftAndResumesWhenBack()
    {
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer { Delay = TimeSpan.FromMilliseconds(40) };
        var nested = FakeBooks.Create(8, "N", nested: true, innerPath: "Vol1");
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer), new TranslationCache(dir.Path), new FakePixels(),
            new FakeBookAccess(() => FakeBooks.Create(8, "N", nested: true, innerPath: "Vol1")));
        scheduler.SetCurrent(nested.BookKey, "ko", 0);
        scheduler.Start(BookTarget.From(nested), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(nested.BookKey, "ko")?.CompletedPages >= 1);
        scheduler.SetCurrent("source:other", "ko", 0);
        var info = scheduler.Get(nested.BookKey, "ko")!;
        Assert.True(info.AutoPaused);
        Assert.Equal(TranslationPhase.Paused, info.Phase);
        scheduler.SetCurrent(nested.BookKey, "ko", 0);
        await Wait.UntilAsync(() => scheduler.Get(nested.BookKey, "ko")?.Phase == TranslationPhase.Completed);
    }

    [Fact]
    public async Task CacheClearStopsJobs()
    {
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer { Delay = TimeSpan.FromMilliseconds(30) };
        var cache = new TranslationCache(dir.Path);
        var book = FakeBooks.Create(10);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer), cache, new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(10)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.CompletedPages >= 1);
        cache.Clear();
        Assert.Null(scheduler.Get(book.BookKey, "ko"));
        await Task.Delay(100);
        Assert.Equal(0, cache.SizeBytes());
    }

    [Fact]
    public async Task RetranslatePageBypassesCache()
    {
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer();
        var book = FakeBooks.Create(2);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(2)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
        var before = recognizer.Calls;
        scheduler.Retranslate(BookTarget.From(book), "ko", "ja", TranslationScope.Page, [1], 1);
        await Wait.UntilAsync(() => recognizer.Calls > before && scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
        Assert.Equal(before + 1, recognizer.Calls);
    }

    [Fact]
    public async Task StripScopeAssignsSeamBalloonOnceAndCompletesAllPages()
    {
        using var dir = new TempDir();
        // 5 × 720×576 strip, band0 [0,2400) band1 [1400,2880). 풍선 하나가 page 3(1728..2304)에 걸침
        var recognizer = new FakeRecognizer
        {
            Produce = (img, lang) => img.Height == 2400
                ? [new TextRegion("こんにちは", new RectD(100, 1800, 200, 30), false, 30)]
                : [new TextRegion("こんにちは", new RectD(100, 400, 200, 30), false, 30), new TextRegion("さようなら", new RectD(100, 1200, 200, 30), false, 30)],
        };
        var book = FakeBooks.Create(5);
        var updates = new List<TranslationPageUpdate>();
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer), new TranslationCache(dir.Path), new FakePixels(_ => new SizeD(720, 576)),
            new FakeBookAccess(() => FakeBooks.Create(5)));
        scheduler.PageUpdated += (_, u) => { lock (updates) updates.Add(u); };
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Strip);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
        var cached = Enumerable.Range(0, 5).Select(p => scheduler.LoadCached(book.Pages[p].PageId, "ja", "ko", TranslationScope.Strip, TranslationMode.Fast)).ToList();
        Assert.All(cached, Assert.NotNull);
        // band0 y=1800(center 1815, owned [0,1900)) → page 3 local y 72; band1 local 400 → strip 1800 → owned by band0 → 버림
        var page3 = cached[3]!;
        Assert.Single(page3.Bubbles, b => b.Source == "こんにちは");
        Assert.Equal(1800 - 1728, page3.Bubbles.Single(b => b.Source == "こんにちは").Rect.Y, 3);
        // band1 local 1200 → strip 2600 → page 4 local 296
        Assert.Equal(2600 - 2304, cached[4]!.Bubbles.Single().Rect.Y, 3);
        Assert.Equal(TranslationScope.Strip, cached[4]!.Scope);
    }
}

public class ExportSchedulerTests
{
    private static async Task<(TranslationScheduler Scheduler, PageEditsStore Edits, OpenedBook Book)> PrepareAsync(TempDir dir, int pages, bool translateAll)
    {
        var book = FakeBooks.Create(pages);
        var scheduler = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(Path.Combine(dir.Path, "cache")), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(pages)));
        var edits = new PageEditsStore(Path.Combine(dir.Path, "edits"));
        if (translateAll)
        {
            scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
            await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
        }
        return (scheduler, edits, book);
    }

    [Fact]
    public async Task ExportsCbzWithNamesRenderedAndHiddenRespected()
    {
        using var dir = new TempDir();
        var (scheduler, edits, book) = await PrepareAsync(dir, 3, true);
        await using var schedulerScope = scheduler;
        // page 2(index 1)의 풍선 숨김 → 원본 bytes 그대로
        var page1 = scheduler.LoadCached(book.Pages[1].PageId, "ja", "ko", TranslationScope.Page, TranslationMode.Fast)!;
        var e = PageEditsEditor.Empty(book.Pages[1].PageId, TranslationScope.Page);
        (e, var i) = PageEditsEditor.Ensure(e, page1.Bubbles[0].Source, page1.Bubbles[0].Rect);
        edits.Save(PageEditsEditor.SetHidden(e, i, true));
        // page 1(index 0) 사용자 번역
        var page0 = scheduler.LoadCached(book.Pages[0].PageId, "ja", "ko", TranslationScope.Page, TranslationMode.Fast)!;
        var e0 = PageEditsEditor.Empty(book.Pages[0].PageId, TranslationScope.Page);
        (e0, var k) = PageEditsEditor.Ensure(e0, page0.Bubbles[0].Source, page0.Bubbles[0].Rect);
        edits.Save(PageEditsEditor.SetTranslation(e0, k, "ko", "내 번역"));

        var renderer = new FakeRenderer();
        var exports = new ExportScheduler(scheduler, edits, new FakeBookAccess(() => FakeBooks.Create(3)), renderer, null, () => TranslationMode.Fast, _ => "Korean");
        var outDir = Path.Combine(dir.Path, "out");
        var job = exports.Enqueue(new ExportRequest(BookTarget.From(book), "ko", "ja", ExportFormat.Cbz, UntranslatedPolicy.TranslateFirst, outDir, TranslationScope.Page));
        await Wait.UntilAsync(() => exports.Jobs.Single().IsTerminal);
        var done = exports.Jobs.Single();
        Assert.Equal(ExportPhase.Finished, done.Phase);
        Assert.Equal(Path.Combine(outDir, "Sample [Korean].cbz"), done.OutputPath);
        using var zip = ZipFile.OpenRead(done.OutputPath!);
        Assert.Equal(["001.png", "002.png", "003.png"], zip.Entries.Select(x => x.FullName));
        string Read(string n) { using var s = zip.GetEntry(n)!.Open(); using var r = new StreamReader(s); return r.ReadToEnd(); }
        Assert.Equal("rendered:1:내 번역", Read("001.png"));
        using (var s = zip.GetEntry("002.png")!.Open())
        using (var ms = new MemoryStream()) { s.CopyTo(ms); Assert.Equal(new byte[] { 1, 1, 2, 3, 4 }, ms.ToArray()); }
        Assert.Empty(Directory.EnumerateFileSystemEntries(outDir, "*.partial"));
        _ = job;
    }

    [Fact]
    public async Task CollisionAddsNumberAndFolderFormatWorks()
    {
        using var dir = new TempDir();
        var (scheduler, edits, book) = await PrepareAsync(dir, 2, true);
        await using var schedulerScope = scheduler;
        var exports = new ExportScheduler(scheduler, edits, new FakeBookAccess(() => FakeBooks.Create(2)), new FakeRenderer(), null, () => TranslationMode.Fast, _ => "Korean");
        var outDir = Path.Combine(dir.Path, "out");
        Directory.CreateDirectory(Path.Combine(outDir, "Sample [Korean]"));
        exports.Enqueue(new ExportRequest(BookTarget.From(book), "ko", "ja", ExportFormat.Folder, UntranslatedPolicy.ExportAsIs, outDir, TranslationScope.Page));
        await Wait.UntilAsync(() => exports.Jobs.Single().IsTerminal);
        var done = exports.Jobs.Single();
        Assert.Equal(Path.Combine(outDir, "Sample [Korean] 2"), done.OutputPath);
        Assert.True(File.Exists(Path.Combine(done.OutputPath!, "001.png")));
        Assert.True(Directory.Exists(Path.Combine(outDir, "Sample [Korean]")));
    }

    [Fact]
    public async Task ExportAsIsWithNothingTranslatedFails()
    {
        using var dir = new TempDir();
        var (scheduler, edits, book) = await PrepareAsync(dir, 2, false);
        await using var schedulerScope = scheduler;
        var exports = new ExportScheduler(scheduler, edits, new FakeBookAccess(() => FakeBooks.Create(2)), new FakeRenderer(), null, () => TranslationMode.Fast, _ => "Korean");
        exports.Enqueue(new ExportRequest(BookTarget.From(book), "ko", "ja", ExportFormat.Zip, UntranslatedPolicy.ExportAsIs, Path.Combine(dir.Path, "out"), TranslationScope.Page));
        await Wait.UntilAsync(() => exports.Jobs.Single().IsTerminal);
        Assert.Equal(ExportPhase.Failed, exports.Jobs.Single().Phase);
        Assert.False(string.IsNullOrEmpty(exports.Jobs.Single().Error));
    }

    [Fact]
    public async Task TranslateFirstWaitsForSharedQueue()
    {
        using var dir = new TempDir();
        var (scheduler, edits, book) = await PrepareAsync(dir, 3, false);
        await using var schedulerScope = scheduler;
        var phases = new List<ExportPhase>();
        var exports = new ExportScheduler(scheduler, edits, new FakeBookAccess(() => FakeBooks.Create(3)), new FakeRenderer(), null, () => TranslationMode.Fast, _ => "Korean");
        exports.JobChanged += (_, i) => { lock (phases) phases.Add(i.Phase); };
        exports.Enqueue(new ExportRequest(BookTarget.From(book), "ko", "ja", ExportFormat.Zip, UntranslatedPolicy.TranslateFirst, Path.Combine(dir.Path, "out"), TranslationScope.Page));
        await Wait.UntilAsync(() => exports.Jobs.Single().IsTerminal);
        Assert.Equal(ExportPhase.Finished, exports.Jobs.Single().Phase);
        lock (phases) Assert.Contains(ExportPhase.Translating, phases);
        Assert.EndsWith(".zip", exports.Jobs.Single().OutputPath);
    }

    [Fact]
    public async Task CancelCleansPartialAndStaysCancelled()
    {
        using var dir = new TempDir();
        var (scheduler, edits, book) = await PrepareAsync(dir, 3, true);
        await using var schedulerScope = scheduler;
        var renderer = new FakeRenderer { Delay = TimeSpan.FromMilliseconds(300) };
        var exports = new ExportScheduler(scheduler, edits, new FakeBookAccess(() => FakeBooks.Create(3)), renderer, null, () => TranslationMode.Fast, _ => "Korean");
        var outDir = Path.Combine(dir.Path, "out");
        var job = exports.Enqueue(new ExportRequest(BookTarget.From(book), "ko", "ja", ExportFormat.Cbz, UntranslatedPolicy.ExportAsIs, outDir, TranslationScope.Page));
        await Wait.UntilAsync(() => renderer.Calls >= 1);
        exports.Cancel(job.Id);
        await Task.Delay(600);
        Assert.Equal(ExportPhase.Cancelled, exports.Jobs.Single().Phase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outDir));
        var retried = exports.Retry(job.Id);
        Assert.NotNull(retried);
        await Wait.UntilAsync(() => exports.Jobs.Single().IsTerminal);
        Assert.Equal(ExportPhase.Finished, exports.Jobs.Single().Phase);
    }

    [Fact]
    public void PublishStagingNeverOverwrites()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "Book [Korean].cbz"), "existing");
        var staging = Path.Combine(dir.Path, ".x.partial");
        File.WriteAllText(staging, "new");
        var final = ExportScheduler.PublishStaging(staging, dir.Path, "Book [Korean]", ".cbz", false);
        Assert.Equal(Path.Combine(dir.Path, "Book [Korean] 2.cbz"), final);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(dir.Path, "Book [Korean].cbz")));
    }
}
