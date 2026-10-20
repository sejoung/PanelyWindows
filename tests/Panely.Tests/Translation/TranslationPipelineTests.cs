using Panely.Core.Translation;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Tests.Translation;

/// <summary>원문 앞에 "T:"를 붙여 돌려주고 호출마다 받은 문장 수·문맥을 남기는 번역기. Hold가 있으면 첫 호출이 그 조건을 기다린다.</summary>
internal sealed class RecordingTranslator : ITranslator
{
    private int _calls;
    public List<int> Batches { get; } = [];
    public List<IReadOnlyList<TranslationContextLine>> Contexts { get; } = [];
    public Func<bool>? Hold { get; set; }
    public Exception? Throw { get; set; }
    public string Name => "recording";
    public string Revision => "recording@1";
    public Capability GetCapability(string source, string target) => Capability.Ready;

    public async Task<IReadOnlyList<TranslationResultItem>> TranslateAsync(IReadOnlyList<TranslationRequestItem> items, IReadOnlyList<TranslationContextLine> context,
        string source, string target, CancellationToken cancellationToken)
    {
        if (Throw is not null) throw Throw;
        lock (Batches) { Batches.Add(items.Count); Contexts.Add(context); }
        if (Interlocked.Increment(ref _calls) == 1 && Hold is { } hold)
        {
            await Wait.UntilAsync(hold);
            await Task.Delay(100, cancellationToken); // 마지막 쪽이 대기열에 들어갈 시간
        }
        return items.Select(i => new TranslationResultItem(i.Id, "T:" + i.Text)).ToList();
    }
}

public class TranslationPipelineTests
{
    [Fact]
    public async Task BalloonsFromSeveralPagesShareOneTranslatorCall()
    {
        // 번역이 밀리는 동안 분석은 다음 쪽을 읽어 둔다. 쌓인 쪽의 풍선은 한 번에 번역기에 들어간다.
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer();
        var translator = new RecordingTranslator { Hold = () => recognizer.Calls >= 6 };
        var book = FakeBooks.Create(6);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer, translator), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(6)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed);

        Assert.Equal(TranslationPhase.Completed, scheduler.Get(book.BookKey, "ko")!.Phase);
        lock (translator.Batches)
        {
            Assert.Equal(6, translator.Batches.Sum());
            Assert.True(translator.Batches.Count < 6, $"batches: {string.Join(",", translator.Batches)}");
        }
        for (var p = 0; p < 6; p++)
        {
            var page = scheduler.LoadCached(book.Pages[p].PageId, "ja", "ko", TranslationScope.Page)!;
            Assert.Equal($"T:こんにちは{p}", Assert.Single(page.Bubbles).Translation);
        }
    }

    [Fact]
    public async Task EarlierTranslationsGoToTheTranslatorAsContext()
    {
        // 말투·이름을 이어 가도록 이 작업에서 직전에 번역한 풍선을 ContextLines개까지 함께 넘긴다
        using var dir = new TempDir();
        var translator = new RecordingTranslator();
        var book = FakeBooks.Create(4);
        var options = new TranslationOptions { TranslateChunk = 1, ContextLines = 2 };
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(new FakeRecognizer(), translator), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(4)), options);
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed);

        lock (translator.Batches)
        {
            Assert.Equal(4, translator.Contexts.Count);
            Assert.Empty(translator.Contexts[0]);
            Assert.Equal(new TranslationContextLine("こんにちは0", "T:こんにちは0"), Assert.Single(translator.Contexts[1]));
            Assert.Equal(["こんにちは1", "こんにちは2"], translator.Contexts[3].Select(c => c.Source));
        }
    }

    [Fact]
    public async Task TranslatorExceptionFailsJobWithItsMessage()
    {
        // 번역 단계의 오류가 분석 단계의 취소에 가려지지 않는다
        using var dir = new TempDir();
        var translator = new RecordingTranslator { Throw = new InvalidOperationException("translator broke") };
        var book = FakeBooks.Create(4);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(new FakeRecognizer(), translator), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(4)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed);

        var info = scheduler.Get(book.BookKey, "ko")!;
        Assert.Equal(TranslationPhase.Failed, info.Phase);
        Assert.Equal("translator broke", info.ReasonMessage);
    }

    [Fact]
    public async Task StripPageWithBalloonsFromTwoBandsKeepsEachTranslation()
    {
        // 5 × 720×576 strip, band0 [0,2400) owned [0,1900), band1 [1400,2880). page 3(1728..2304)에 band마다 풍선 하나.
        // band마다 풍선 ID를 0부터 매기므로, ID로 결과를 맞추면 band1의 번역이 band0 풍선에 들어갔다.
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer
        {
            Produce = (img, lang) => img.Height == 2400
                ? [new TextRegion("あさ", new RectD(100, 1750, 200, 30), false, 30)]
                : [new TextRegion("よる", new RectD(100, 600, 200, 30), false, 30)],
        };
        var book = FakeBooks.Create(5);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer, new RecordingTranslator()), new TranslationCache(dir.Path), new FakePixels(_ => new SizeD(720, 576)),
            new FakeBookAccess(() => FakeBooks.Create(5)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Strip);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed);

        Assert.Equal(TranslationPhase.Completed, scheduler.Get(book.BookKey, "ko")!.Phase);
        var page3 = scheduler.LoadCached(book.Pages[3].PageId, "ja", "ko", TranslationScope.Strip)!;
        Assert.Equal(2, page3.Bubbles.Count);
        Assert.All(page3.Bubbles, b => Assert.Equal("T:" + b.Source, b.Translation));
        Assert.Equal([0, 1], page3.Bubbles.Select(b => b.Id));
    }
}
