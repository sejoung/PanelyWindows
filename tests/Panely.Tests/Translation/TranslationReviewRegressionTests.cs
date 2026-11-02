using System.IO.Compression;
using Panely.Core.Abstractions;
using Panely.Core.Translation;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Tests.Translation;

/// <summary>번역 파이프라인 코드 리뷰에서 나온 결함의 회귀 테스트.</summary>
public class TranslationReviewRegressionTests
{
    private static string CacheFile(string cacheRoot, TranslationCacheKey key)
    {
        var hash = TranslationJson.Hash(key.Identity);
        return Path.Combine(cacheRoot, $"v{TranslationCache.FormatVersion}", hash[..2], hash + ".json");
    }

    private static List<string> ReadEntries(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal).Select(e =>
        {
            using var s = e.Open();
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }).ToList();
    }

    private static TranslationProviders Providers(FakeRecognizer recognizer, FakePacks packs) => new()
    {
        Recognizer = recognizer,
        Detector = new FakeDetector(),
        Translator = new PseudoTranslator { Delay = TimeSpan.Zero },
        Packs = packs,
    };

    /// <summary>감지가 돌면 실패하는 인식기: 어떤 언어도 준비되지 않았다고 답하지만 원어를 알면 읽는다.</summary>
    private static FakeRecognizer CannotDetect()
    {
        var recognizer = new FakeRecognizer();
        recognizer.Languages.Clear();
        return recognizer;
    }

    private sealed class GatedBookAccess(Func<OpenedBook> factory, Task gate) : IBookAccess
    {
        public volatile bool Entered;

        public async Task<OpenedBook> OpenAsync(BookTarget target, CancellationToken cancellationToken)
        {
            Entered = true;
            // 취소를 보지 않는다: Pause가 와도 끝나야 돌아온다
            await gate.ConfigureAwait(false);
            return factory();
        }
    }

    // ───────── 1. 내보내기와 다른 범위·쪽 지정 작업 ─────────

    [Fact]
    public async Task TranslateFirstExportWaitsForOtherScopeJobThenTranslatesItsOwnScope()
    {
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer { Delay = TimeSpan.FromMilliseconds(100) };
        var book = FakeBooks.Create(3);
        var edits = new PageEditsStore(Path.Combine(dir.Path, "edits"));
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer), new TranslationCache(Path.Combine(dir.Path, "cache")), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(3)));
        var stripCompleted = false;
        scheduler.JobChanged += (_, i) => { if (i.Scope == TranslationScope.Strip && i.Phase == TranslationPhase.Completed) stripCompleted = true; };
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Strip);

        var exports = new ExportScheduler(scheduler, edits, new FakeBookAccess(() => FakeBooks.Create(3)), new FakeRenderer(), null, _ => "Korean");
        exports.Enqueue(new ExportRequest(BookTarget.From(book), "ko", null, ExportFormat.Cbz, UntranslatedPolicy.TranslateFirst, Path.Combine(dir.Path, "out"), TranslationScope.Page));
        await Wait.UntilAsync(() => exports.Jobs.Single().IsTerminal, 30000);

        var done = exports.Jobs.Single();
        Assert.Equal(ExportPhase.Finished, done.Phase);
        Assert.True(stripCompleted, "사용자가 시작한 strip 작업을 끊지 않아야 한다");
        Assert.All(ReadEntries(done.OutputPath!), text => Assert.StartsWith("rendered:1:", text));
    }

    [Fact]
    public async Task TranslateFirstExportFinishesTheBookAfterARunningSinglePageJob()
    {
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer { Delay = TimeSpan.FromMilliseconds(100) };
        var book = FakeBooks.Create(3);
        var edits = new PageEditsStore(Path.Combine(dir.Path, "edits"));
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer), new TranslationCache(Path.Combine(dir.Path, "cache")), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(3)));
        Assert.Equal(1, scheduler.Retranslate(BookTarget.From(book), "ko", "ja", TranslationScope.Page, [1], 1).TotalPages);
        await Wait.UntilAsync(() => recognizer.Calls >= 1);

        var exports = new ExportScheduler(scheduler, edits, new FakeBookAccess(() => FakeBooks.Create(3)), new FakeRenderer(), null, _ => "Korean");
        exports.Enqueue(new ExportRequest(BookTarget.From(book), "ko", "ja", ExportFormat.Cbz, UntranslatedPolicy.TranslateFirst, Path.Combine(dir.Path, "out"), TranslationScope.Page));
        await Wait.UntilAsync(() => exports.Jobs.Single().IsTerminal, 30000);

        var done = exports.Jobs.Single();
        Assert.Equal(ExportPhase.Finished, done.Phase);
        Assert.All(ReadEntries(done.OutputPath!), text => Assert.StartsWith("rendered:1:", text));
    }

    [Fact]
    public async Task TranslateFirstExportFailsWhenAPageStaysUnreadable()
    {
        using var dir = new TempDir();
        var cacheRoot = Path.Combine(dir.Path, "cache");
        var book = FakeBooks.Create(2);
        var edits = new PageEditsStore(Path.Combine(dir.Path, "edits"));
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(cacheRoot), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(2)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);

        // 파일은 멀쩡한데 계속 읽을 수 없다: 원본 쪽을 쓰고 완료라고 하면 안 된다
        var file = CacheFile(cacheRoot, scheduler.CacheKey(book.Pages[1].PageId, "ja", "ko", TranslationScope.Page));
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exports = new ExportScheduler(scheduler, edits, new FakeBookAccess(() => FakeBooks.Create(2)), new FakeRenderer(), null, _ => "Korean");
            exports.Enqueue(new ExportRequest(BookTarget.From(book), "ko", "ja", ExportFormat.Cbz, UntranslatedPolicy.TranslateFirst, Path.Combine(dir.Path, "out"), TranslationScope.Page));
            await Wait.UntilAsync(() => exports.Jobs.Single().IsTerminal);
            Assert.Equal(ExportPhase.Failed, exports.Jobs.Single().Phase);
            Assert.False(string.IsNullOrEmpty(exports.Jobs.Single().Error));
        }
    }

    // ───────── 2. 도는 중의 재번역 ─────────

    [Fact]
    public async Task RetranslateDuringARunningStripJobFinishes()
    {
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer
        {
            Delay = TimeSpan.FromMilliseconds(20),
            Produce = (_, _) => [new TextRegion("こんにちは", new RectD(100, 100, 200, 30), false, 30)],
        };
        var book = FakeBooks.Create(12);
        var published = 0;
        var providers = FakeProviders.Create(recognizer, new PseudoTranslator { Delay = TimeSpan.FromMilliseconds(20) });
        await using var scheduler = new TranslationScheduler(providers, new TranslationCache(dir.Path), new FakePixels(_ => new SizeD(720, 576)), new FakeBookAccess(() => FakeBooks.Create(12)));
        scheduler.PageUpdated += (_, _) => Interlocked.Increment(ref published);
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Strip);
        await Wait.UntilAsync(() => Volatile.Read(ref published) >= 1);

        scheduler.Retranslate(BookTarget.From(book), "ko", "ja", TranslationScope.Strip, [0], 0);

        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed, 30000);
        var info = scheduler.Get(book.BookKey, "ko")!;
        Assert.Equal(TranslationPhase.Completed, info.Phase);
        Assert.All(book.Pages, p => Assert.NotNull(scheduler.LoadCached(p.PageId, "ja", "ko", TranslationScope.Strip)));
    }

    private sealed class GatedTranslator(Task gate) : ITranslator
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "gated";
        public string Revision => "gated";
        public Capability GetCapability(string source, string target) => Capability.Ready;

        public async Task<IReadOnlyList<TranslationResultItem>> TranslateAsync(IReadOnlyList<TranslationRequestItem> items, IReadOnlyList<TranslationContextLine> context, string source, string target, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return items.Select(i => new TranslationResultItem(i.Id, "t-" + i.Text)).ToList();
        }
    }

    [Fact]
    public async Task RetranslateAfterAnalysisEndedRunsAgainInsteadOfFailing()
    {
        using var dir = new TempDir();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var translator = new GatedTranslator(gate.Task);
        var book = FakeBooks.Create(3);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(translator: translator), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(3)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Strip);
        await translator.Entered.Task;
        await Task.Delay(100); // band가 하나뿐이라 분석은 끝났고 번역만 남았다

        scheduler.Retranslate(BookTarget.From(book), "ko", "ja", TranslationScope.Strip, [0], 0);
        gate.SetResult();

        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed);
        Assert.Equal(TranslationPhase.Completed, scheduler.Get(book.BookKey, "ko")!.Phase);
    }

    [Theory]
    [InlineData(TranslationScope.Strip)]
    [InlineData(TranslationScope.Page)]
    public async Task ChangingSourceDuringARunningJobRestartsWithTheNewSource(TranslationScope scope)
    {
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer
        {
            Delay = TimeSpan.FromMilliseconds(20),
            Produce = (_, _) => [new TextRegion("Hello there", new RectD(100, 100, 200, 30), false, 30)],
        };
        var book = FakeBooks.Create(12);
        var published = 0;
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer), new TranslationCache(dir.Path), new FakePixels(_ => new SizeD(720, 576)), new FakeBookAccess(() => FakeBooks.Create(12)));
        scheduler.PageUpdated += (_, _) => Interlocked.Increment(ref published);
        scheduler.Start(BookTarget.From(book), "ko", "ja", scope);
        await Wait.UntilAsync(() => Volatile.Read(ref published) >= 1);

        scheduler.Start(BookTarget.From(book), "ko", "en", scope);

        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed, 30000);
        var info = scheduler.Get(book.BookKey, "ko")!;
        Assert.Equal(TranslationPhase.Completed, info.Phase);
        Assert.Equal("en", info.Source);
        Assert.All(book.Pages, p => Assert.Equal("en", scheduler.LoadCached(p.PageId, "en", "ko", scope)?.SourceLanguage));
    }

    // ───────── 3. 원어 찾기 ─────────

    [Fact]
    public async Task ExportAsIsFindsTheSourceInTheCacheAfterARestart()
    {
        using var dir = new TempDir();
        var cacheRoot = Path.Combine(dir.Path, "cache");
        var book = FakeBooks.Create(2);
        await using (var first = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(cacheRoot), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(2))))
        {
            first.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
            await Wait.UntilAsync(() => first.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
        }

        await using var scheduler = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(cacheRoot), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(2)));
        var exports = new ExportScheduler(scheduler, new PageEditsStore(Path.Combine(dir.Path, "edits")), new FakeBookAccess(() => FakeBooks.Create(2)), new FakeRenderer(), null, _ => "Korean");
        exports.Enqueue(new ExportRequest(BookTarget.From(book), "ko", null, ExportFormat.Cbz, UntranslatedPolicy.ExportAsIs, Path.Combine(dir.Path, "out"), TranslationScope.Page));
        await Wait.UntilAsync(() => exports.Jobs.Single().IsTerminal);

        var done = exports.Jobs.Single();
        Assert.Equal(ExportPhase.Finished, done.Phase);
        Assert.All(ReadEntries(done.OutputPath!), text => Assert.StartsWith("rendered:1:", text));
    }

    [Fact]
    public async Task TranslateFirstUsesTheSourceFoundInTheCacheInsteadOfDetecting()
    {
        using var dir = new TempDir();
        var cacheRoot = Path.Combine(dir.Path, "cache");
        var book = FakeBooks.Create(3);
        await using (var first = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(cacheRoot), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(3))))
        {
            // 책의 끝쪽 한 쪽만 번역된 상태(앞 3쪽만 보는 탐색으로는 놓치지 않는지도 본다)
            first.Retranslate(BookTarget.From(book), "ko", "ja", TranslationScope.Page, [2], 2);
            await Wait.UntilAsync(() => first.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
        }

        await using var scheduler = new TranslationScheduler(FakeProviders.Create(CannotDetect()), new TranslationCache(cacheRoot), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(3)));
        var exports = new ExportScheduler(scheduler, new PageEditsStore(Path.Combine(dir.Path, "edits")), new FakeBookAccess(() => FakeBooks.Create(3)), new FakeRenderer(), null, _ => "Korean");
        exports.Enqueue(new ExportRequest(BookTarget.From(book), "ko", null, ExportFormat.Cbz, UntranslatedPolicy.TranslateFirst, Path.Combine(dir.Path, "out"), TranslationScope.Page));
        await Wait.UntilAsync(() => exports.Jobs.Single().IsTerminal);
        Assert.Equal(ExportPhase.Finished, exports.Jobs.Single().Phase);
    }

    [Fact]
    public async Task TranslateFirstUsesTheSeriesSourceInsteadOfDetecting()
    {
        using var dir = new TempDir();
        using var env = new Panely.Tests.Core.CoreEnv();
        env.State.UpdateSeries("series:test", s => s.TranslationSource = "ja");
        var book = FakeBooks.Create(2);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(CannotDetect()), new TranslationCache(Path.Combine(dir.Path, "cache")), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(2)));
        var exports = new ExportScheduler(scheduler, new PageEditsStore(Path.Combine(dir.Path, "edits")), new FakeBookAccess(() => FakeBooks.Create(2)), new FakeRenderer(), env.State, _ => "Korean");
        exports.Enqueue(new ExportRequest(BookTarget.From(book), "ko", null, ExportFormat.Cbz, UntranslatedPolicy.TranslateFirst, Path.Combine(dir.Path, "out"), TranslationScope.Page));
        await Wait.UntilAsync(() => exports.Jobs.Single().IsTerminal);
        Assert.Equal(ExportPhase.Finished, exports.Jobs.Single().Phase);
    }

    // ───────── 7. 일시정지를 덮어쓰지 않음 ─────────

    [Fact]
    public async Task PauseWhileTheBookOpensStaysPausedAndCanResume()
    {
        using var dir = new TempDir();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var book = FakeBooks.Create(2);
        var access = new GatedBookAccess(() => FakeBooks.Create(2), gate.Task);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(dir.Path), new FakePixels(), access);
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => access.Entered);

        scheduler.Pause(book.BookKey, "ko");
        gate.SetResult();
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.IsActive == false);

        var paused = scheduler.Get(book.BookKey, "ko")!;
        Assert.Equal(TranslationPhase.Paused, paused.Phase);
        Assert.True(paused.UserPaused);
        Assert.False(paused.IsRunningOrQueued);
        Assert.True(paused.CanResume);

        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
    }

    [Fact]
    public async Task PauseArrivingBeforeTheWorkerWritesItsPhaseIsKept()
    {
        using var dir = new TempDir();
        TranslationScheduler? scheduler = null;
        var book = FakeBooks.Create(2);
        var calls = 0;
        // 언어팩 확인은 worker가 단계를 "준비 중"으로 적기 직전이다. 취소를 볼 수 없는 구간에서 Pause가 온 것과 같다.
        var packs = new FakePacks(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) scheduler!.Pause(book.BookKey, "ko");
            return Capability.Ready;
        });
        scheduler = new TranslationScheduler(Providers(new FakeRecognizer(), packs), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(2)));
        await using var scope = scheduler;
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => Volatile.Read(ref calls) >= 1 && scheduler.Get(book.BookKey, "ko")?.IsActive == false);

        var paused = scheduler.Get(book.BookKey, "ko")!;
        Assert.Equal(TranslationPhase.Paused, paused.Phase);
        Assert.False(paused.IsRunningOrQueued);

        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
    }

    // ───────── 8. 캐시 손상 ─────────

    [Fact]
    public void ContainsRejectsATruncatedFileWithoutParsing()
    {
        using var dir = new TempDir();
        var cache = new TranslationCache(dir.Path);
        var page = TranslationJson.DeserializePage(TrVectors.ExampleText("translation-page.json"));
        var key = new TranslationCacheKey(page.PageId, "ja", "ko", TranslationScope.Page, 1, "rev");
        Assert.True(cache.Store(key, page, cache.Generation));
        Assert.True(cache.Contains(key));

        var file = CacheFile(dir.Path, key);
        var json = File.ReadAllText(file);
        File.WriteAllText(file, json[..(json.Length / 2)]);
        Assert.False(cache.Contains(key));
        File.WriteAllBytes(file, []);
        Assert.False(cache.Contains(key));
    }

    [Fact]
    public void TransientReadFailureKeepsTheCacheFile()
    {
        using var dir = new TempDir();
        var cache = new TranslationCache(dir.Path);
        var page = TranslationJson.DeserializePage(TrVectors.ExampleText("translation-page.json"));
        var key = new TranslationCacheKey(page.PageId, "ja", "ko", TranslationScope.Page, 1, "rev");
        Assert.True(cache.Store(key, page, cache.Generation));

        var file = CacheFile(dir.Path, key);
        // 읽기는 막지만 삭제는 허용한다: 잘못 지우면 실제로 지워진다
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            Assert.Null(cache.TryLoad(key));
            Assert.True(cache.Contains(key));
        }
        Assert.True(File.Exists(file));
        Assert.NotNull(cache.TryLoad(key));
    }

    [Fact]
    public void WellFormedTailButBrokenContentIsDeletedOnLoad()
    {
        using var dir = new TempDir();
        var cache = new TranslationCache(dir.Path);
        var page = TranslationJson.DeserializePage(TrVectors.ExampleText("translation-page.json"));
        var key = new TranslationCacheKey(page.PageId, "ja", "ko", TranslationScope.Page, 1, "rev");
        Assert.True(cache.Store(key, page, cache.Generation));

        var file = CacheFile(dir.Path, key);
        File.WriteAllText(file, "broken]}");
        Assert.True(cache.Contains(key));
        Assert.Null(cache.TryLoad(key));
        Assert.False(File.Exists(file));
        Assert.False(cache.Contains(key));
    }

    [Theory]
    [InlineData(TranslationScope.Page)]
    [InlineData(TranslationScope.Strip)]
    public async Task JobTranslatesATruncatedPageAgain(TranslationScope scope)
    {
        using var dir = new TempDir();
        var book = FakeBooks.Create(3);
        var first = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(3)));
        await using (first.ConfigureAwait(false))
        {
            first.Start(BookTarget.From(book), "ko", "ja", scope);
            await Wait.UntilAsync(() => first.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
            var file = CacheFile(dir.Path, first.CacheKey(book.Pages[1].PageId, "ja", "ko", scope));
            var json = File.ReadAllText(file);
            File.WriteAllText(file, json[..(json.Length / 2)]);
        }

        await using var scheduler = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(3)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", scope);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed);
        Assert.Equal(TranslationPhase.Completed, scheduler.Get(book.BookKey, "ko")!.Phase);
        Assert.NotNull(scheduler.LoadCached(book.Pages[1].PageId, "ja", "ko", scope));
    }

    [Fact]
    public async Task StripJobTranslatesAPageThatFailsToLoadAgain()
    {
        using var dir = new TempDir();
        var book = FakeBooks.Create(3);
        var first = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(3)));
        await using (first.ConfigureAwait(false))
        {
            first.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Strip);
            await Wait.UntilAsync(() => first.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
            // 끝 두 바이트는 멀쩡해 CachedPages는 완료로 본다
            File.WriteAllText(CacheFile(dir.Path, first.CacheKey(book.Pages[1].PageId, "ja", "ko", TranslationScope.Strip)), "broken]}");
        }

        await using var scheduler = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(3)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Strip);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed);
        Assert.Equal(TranslationPhase.Completed, scheduler.Get(book.BookKey, "ko")!.Phase);
        Assert.NotNull(scheduler.LoadCached(book.Pages[1].PageId, "ja", "ko", TranslationScope.Strip));
    }

    [Fact]
    public async Task TranslateFirstExportTranslatesAPageThatFailsToLoadAgain()
    {
        using var dir = new TempDir();
        var cacheRoot = Path.Combine(dir.Path, "cache");
        var book = FakeBooks.Create(3);
        var edits = new PageEditsStore(Path.Combine(dir.Path, "edits"));
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(cacheRoot), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(3)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
        File.WriteAllText(CacheFile(cacheRoot, scheduler.CacheKey(book.Pages[1].PageId, "ja", "ko", TranslationScope.Page)), "broken]}");

        var exports = new ExportScheduler(scheduler, edits, new FakeBookAccess(() => FakeBooks.Create(3)), new FakeRenderer(), null, _ => "Korean");
        exports.Enqueue(new ExportRequest(BookTarget.From(book), "ko", "ja", ExportFormat.Cbz, UntranslatedPolicy.TranslateFirst, Path.Combine(dir.Path, "out"), TranslationScope.Page));
        await Wait.UntilAsync(() => exports.Jobs.Single().IsTerminal);

        var done = exports.Jobs.Single();
        Assert.Equal(ExportPhase.Finished, done.Phase);
        Assert.All(ReadEntries(done.OutputPath!), text => Assert.StartsWith("rendered:1:", text));
    }

    // ───────── 9. 언어팩 잠금과 놓친 알림 ─────────

    [Fact]
    public async Task ResumingWaitingJobsAsksForCapabilityOutsideTheSchedulerLock()
    {
        using var dir = new TempDir();
        var installed = false;
        TranslationScheduler? scheduler = null;
        var probeFinished = true;
        var packs = new FakePacks(() =>
        {
            if (!Volatile.Read(ref installed)) return new Capability(CapabilityStatus.Supported, "download");
            // 언어팩 관리자의 잠금을 쥔 다른 스레드가 스케줄러 잠금을 기다리는 상황: 스케줄러가 잠금을 쥐고 여기 왔다면 시간 초과다
            if (!Task.Run(() => _ = scheduler!.Jobs).Wait(TimeSpan.FromSeconds(3))) probeFinished = false;
            return Capability.Ready;
        });
        var book = FakeBooks.Create(1);
        scheduler = new TranslationScheduler(Providers(new FakeRecognizer(), packs), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(1)));
        await using var scope = scheduler;
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Paused);

        Volatile.Write(ref installed, true);
        packs.RaiseChanged();

        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
        Assert.True(probeFinished);
    }

    [Fact]
    public async Task InstallFinishingBetweenTheCheckAndThePauseStillResumes()
    {
        using var dir = new TempDir();
        var calls = 0;
        // 첫 확인 때는 받는 중이고, 설치 완료 알림은 작업이 "언어팩 필요"로 멈추기 전에 지나갔다
        var packs = new FakePacks(() => Interlocked.Increment(ref calls) == 1 ? new Capability(CapabilityStatus.Downloading, null, 0.5) : Capability.Ready);
        var book = FakeBooks.Create(1);
        await using var scheduler = new TranslationScheduler(Providers(new FakeRecognizer(), packs), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(1)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);
    }

    // ───────── 12. 내보내기의 추가 풍선, 총 쪽 수 ─────────

    [Fact]
    public async Task ExportKeepsAddedBalloonsOnPagesWithoutATranslation()
    {
        using var dir = new TempDir();
        var book = FakeBooks.Create(2);
        var edits = new PageEditsStore(Path.Combine(dir.Path, "edits"));
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(), new TranslationCache(Path.Combine(dir.Path, "cache")), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(2)));
        var added = PageEditsEditor.Empty(book.Pages[1].PageId, TranslationScope.Page);
        (added, var index) = PageEditsEditor.AddBalloon(added, new RectD(10, 10, 100, 40), "Hello", new AddedBalloon(20, false, BubbleBackground.White));
        edits.Save(PageEditsEditor.SetTranslation(added, index, "ko", "내 풍선"));

        var exports = new ExportScheduler(scheduler, edits, new FakeBookAccess(() => FakeBooks.Create(2)), new FakeRenderer(), null, _ => "Korean");
        exports.Enqueue(new ExportRequest(BookTarget.From(book), "ko", "ja", ExportFormat.Cbz, UntranslatedPolicy.ExportAsIs, Path.Combine(dir.Path, "out"), TranslationScope.Page));
        await Wait.UntilAsync(() => exports.Jobs.Single().IsTerminal);

        var done = exports.Jobs.Single();
        Assert.Equal(ExportPhase.Finished, done.Phase);
        Assert.Equal("rendered:1:내 풍선", ReadEntries(done.OutputPath!)[1]);
    }

    [Fact]
    public async Task TotalPagesFollowsTheBookWhenTheOnlyListIsClearedWhileRunning()
    {
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer { Delay = TimeSpan.FromMilliseconds(200) };
        var book = FakeBooks.Create(5);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(5)));
        Assert.Equal(1, scheduler.Retranslate(BookTarget.From(book), "ko", "ja", TranslationScope.Page, [1], 1).TotalPages);
        await Wait.UntilAsync(() => recognizer.Calls >= 1); // 핸들이 열려 있다

        var info = scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);

        Assert.Equal(5, info.TotalPages);
    }

    // ───────── 4. AI 채팅 snapshot ─────────

    private static TranslatedBubble Machine(int id, string source) => new()
    {
        Id = id,
        Rect = new RectD(10, 10 + id * 50, 100, 30),
        GlyphSize = 20,
        IsVertical = false,
        Source = source,
        Translation = "t" + source,
        Background = BubbleBackground.White,
    };

    [Fact]
    public void SavedAiChatIdsKeepPointingAtTheirBalloonsAfterOneIsHidden()
    {
        var machine = new[] { Machine(0, "a"), Machine(1, "b"), Machine(2, "c"), Machine(3, "d") };
        var snapshot = AiChatExchange.BuildSnapshot("book", "T", "ja", "ko", [(2, EditResolver.Resolve(machine, null, "ko"))]);
        Assert.Equal("p3-3", snapshot.Entries[2].Id);

        var edits = PageEditsEditor.Empty("page", TranslationScope.Page);
        (edits, var i) = PageEditsEditor.Ensure(edits, "b", machine[1].Rect);
        edits = PageEditsEditor.SetHidden(edits, i, true);
        var now = EditResolver.Resolve(machine, edits, "ko");

        // 지금 상태로 다시 만들면 번호가 밀린다(p3-3이 d를 가리킨다). 저장한 snapshot의 p3-3은 그대로 c다.
        var rebuilt = AiChatExchange.BuildSnapshot("book", "T", "ja", "ko", [(2, now)]);
        Assert.Equal("d", rebuilt.Find("p3-3")!.Original);
        Assert.Equal("c", AiChatExchange.FindBubble(snapshot.Find("p3-3")!, now)!.EffectiveSource);
        // 숨긴 풍선도 그대로 찾는다
        Assert.True(AiChatExchange.FindBubble(snapshot.Find("p3-2")!, now)!.IsHidden);
    }

    [Fact]
    public void SavedAiChatEntryDoesNotMatchADifferentBalloon()
    {
        var machine = new[] { Machine(0, "a"), Machine(1, "b") };
        var snapshot = AiChatExchange.BuildSnapshot("book", "T", "ja", "ko", [(0, EditResolver.Resolve(machine, null, "ko"))]);

        // 재번역으로 같은 ID에 다른 풍선이 왔다
        var retranslated = new[] { Machine(0, "x"), Machine(1, "b") };
        Assert.Null(AiChatExchange.FindBubble(snapshot.Find("p1-1")!, EditResolver.Resolve(retranslated, null, "ko")));
        Assert.NotNull(AiChatExchange.FindBubble(snapshot.Find("p1-2")!, EditResolver.Resolve(retranslated, null, "ko")));

        // 저장한 뒤 원문을 고쳤다
        var edits = PageEditsEditor.Empty("page", TranslationScope.Page);
        (edits, var i) = PageEditsEditor.Ensure(edits, "b", machine[1].Rect);
        edits = PageEditsEditor.SetCorrectedSource(edits, i, "b2");
        Assert.Null(AiChatExchange.FindBubble(snapshot.Find("p1-2")!, EditResolver.Resolve(machine, edits, "ko")));
    }

    // ───────── 5. 추가 풍선 번역 결과 ─────────

    [Fact]
    public void AddedBalloonResultLandsOnItsOwnBalloonAfterAnotherIsRemoved()
    {
        var a = new RectD(0, 0, 100, 40);
        var b = new RectD(0, 100, 100, 40);
        var style = new AddedBalloon(20, false, BubbleBackground.White);
        var edits = PageEditsEditor.Empty("page", TranslationScope.Page);
        (edits, var ai) = PageEditsEditor.AddBalloon(edits, a, "A", style);
        (edits, _) = PageEditsEditor.AddBalloon(edits, b, "B", style);

        // 둘 다 번역을 기다리는 동안 A를 지웠다
        edits = PageEditsEditor.RemoveAt(edits, ai);

        Assert.Null(PageEditsEditor.SetAddedRetranslation(edits, "A", a, "ko", "에이"));
        var updated = PageEditsEditor.SetAddedRetranslation(edits, "B", b, "ko", "비");
        Assert.NotNull(updated);
        Assert.Equal("비", Assert.Single(updated.Bubbles).Retranslations["ko"]);
    }

    [Fact]
    public void AddedBalloonResultIsDroppedWhenTheOriginalWasFixedMeanwhile()
    {
        var rect = new RectD(0, 0, 100, 40);
        var edits = PageEditsEditor.Empty("page", TranslationScope.Page);
        (edits, var i) = PageEditsEditor.AddBalloon(edits, rect, "A", new AddedBalloon(20, false, BubbleBackground.White));
        edits = PageEditsEditor.SetCorrectedSource(edits, i, "A2");
        Assert.Null(PageEditsEditor.SetAddedRetranslation(edits, "A", rect, "ko", "옛 번역"));
        Assert.Equal(0, PageEditsEditor.FindAdded(edits, "A", rect));
    }

    // ───────── 6. Gemma 컨텍스트 초과 ─────────

    private static List<TranslationRequestItem> Lines(int count) =>
        Enumerable.Range(0, count).Select(i => new TranslationRequestItem(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "s" + i)).ToList();

    private static string Echo(IReadOnlyList<TranslationRequestItem> part) =>
        string.Join("\n", part.Select((p, i) => $"[{i + 1}] t-{p.Text}"));

    [Fact]
    public void OverflowingChunkIsSplitUntilItFits()
    {
        var calls = new List<int>();
        var (texts, errors) = GemmaTranslator.GenerateLines(Lines(16), part =>
        {
            calls.Add(part.Count);
            if (part.Count > 3) throw new GemmaTranslator.PromptTooLongException("too long");
            return Echo(part);
        });
        Assert.Empty(errors);
        Assert.Equal(16, texts.Count);
        for (var n = 1; n <= 16; n++) Assert.Equal($"t-s{n - 1}", texts[n]);
        Assert.Equal(16, calls[0]);
    }

    [Fact]
    public void ALineTooLongOnItsOwnFailsAlone()
    {
        var (texts, errors) = GemmaTranslator.GenerateLines(Lines(8), part =>
        {
            if (part.Any(p => p.Text == "s5")) throw new GemmaTranslator.PromptTooLongException("too long");
            return Echo(part);
        });
        Assert.Equal([6], errors.Keys);
        Assert.Equal(7, texts.Count);
        Assert.DoesNotContain(6, texts.Keys);
    }

    [Fact]
    public void AnExceptionInASingleLineRetryKeepsLinesThatSucceeded()
    {
        var (texts, errors) = GemmaTranslator.GenerateLines(Lines(3), part =>
        {
            if (part.Count > 1) return "[1] one\n[3] three"; // 2번 줄이 빠졌다
            throw new InvalidOperationException("decode failed");
        });
        Assert.Equal("one", texts[1]);
        Assert.Equal("three", texts[3]);
        Assert.Equal("decode failed", errors[2]);
    }

    [Fact]
    public void CancellationDuringGenerationIsNotTurnedIntoAnError()
    {
        Assert.Throws<OperationCanceledException>(() => GemmaTranslator.GenerateLines(Lines(2), _ => throw new OperationCanceledException()));
    }

    // ───────── 10. 종료와 쓰는 중인 세션 ─────────

    [Fact]
    public async Task ModelGateCloseWaitsForTheCurrentUserThenRefusesNewOnes()
    {
        var gate = new ModelGate(TimeSpan.FromSeconds(30));
        await gate.EnterAsync(CancellationToken.None);
        var released = false;
        var closing = Task.Run(() => gate.Close(() => released = true));
        await Task.Delay(100);
        Assert.False(released);

        gate.Exit();
        Assert.True(await closing);
        Assert.True(released);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => gate.EnterAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ModelGateCloseGivesUpWithoutReleasingWhenStillInUse()
    {
        var gate = new ModelGate(TimeSpan.FromMilliseconds(50));
        await gate.EnterAsync(CancellationToken.None);
        var released = false;
        Assert.False(gate.Close(() => released = true));
        Assert.False(released);
        gate.Exit();
    }

    [Fact]
    public async Task EnginesDoNotLoadAgainAfterDispose()
    {
        using var dir = new TempDir();
        var store = new ModelStore(dir.Path, manifests: ModelCatalog.BuiltIn);
        var manga = new MangaOcrEngine(store);
        manga.Dispose();
        manga.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manga.RecognizeAsync([PagePixels.Blank(4, 4)], CancellationToken.None));
        var ppocr = new PpOcrEngine(store);
        ppocr.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ppocr.RecognizeAsync(PagePixels.Blank(4, 4), null, "en", CancellationToken.None));
    }

    // ───────── 11. 시작할 때의 staging 정리 ─────────

    [Fact]
    public void DetachingStaleStagingSparesADownloadStartedAfterIt()
    {
        using var dir = new TempDir();
        var store = new ModelStore(dir.Path, manifests: ModelCatalog.BuiltIn);
        var stale = Path.Combine(dir.Path, ".staging", "old-1234");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "model.part"), "x");

        store.DetachStaleStaging();
        Assert.False(Directory.Exists(stale));

        // 시작 직후의 다운로드: 뒤늦은 백그라운드 정리가 지우면 안 된다
        var fresh = Path.Combine(dir.Path, ".staging", "new-5678");
        Directory.CreateDirectory(fresh);
        store.FinishStagingCleanup();

        Assert.True(Directory.Exists(fresh));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, ".trash")));
    }

    // ───────── 13. 한 줄이 둘로 나뉜 표지 글자 ─────────

    private static readonly List<DetectedRegion> CoverBox = [new(new RectD(431.9, 693.5, 337.9, 87.5), DetectionKind.FreeText, 0.9)];

    [Fact]
    public void NumberPieceOnTheSameRowInsideTheTextBoxStaysWithItsLine()
    {
        // 데모 표지를 실제 모델로 읽은 값: 줄 탐지가 "Volume 01"을 둘로 나눈다. 영어 조각 "O1"은 글자가 하나라 잡음으로 버려지고
        // patch(오른쪽 끝 677+패딩 22)가 O1(671..734)의 앞부분만 덮어 뒷부분이 보였다.
        var volume = new TextRegion("Volume", new RectD(488, 697.3, 188.8, 74.3), false, 74.3);
        var number = new TextRegion("O1", new RectD(670.6, 716.6, 63, 42.1), false, 42.1);
        var noise = new TextRegion(".", new RectD(500, 700, 4, 4), false, 4);

        var group = Assert.Single(BalloonGrouper.Group([volume, number, noise], CoverBox, "en"));

        Assert.Equal("Volume O1", group.Text);
        Assert.Equal(733.6, group.Rect.Right, 1);
    }

    [Fact]
    public void NumbersAwayFromTheLineOrOutsideTheBoxAreStillDropped()
    {
        var volume = new TextRegion("Volume", new RectD(488, 697.3, 188.8, 74.3), false, 74.3);
        var below = new TextRegion("12", new RectD(488, 900, 40, 30), false, 30);
        var sameRowElsewhere = new TextRegion("34", new RectD(1000, 716, 40, 30), false, 30);

        var group = Assert.Single(BalloonGrouper.Group([volume, below, sameRowElsewhere], CoverBox, "en"));

        Assert.Equal("Volume", group.Text);
        Assert.Equal(volume.Rect, group.Rect);
    }
}
