using Panely.Core.Translation;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Tests.Translation;

/// <summary>원문마다 정해 둔 결과를 돌려주는 번역기. Fail이 원문을 받으면 그 항목에 오류를 낸다.</summary>
internal sealed class ScriptedTranslator(Func<string, string?> translate) : ITranslator
{
    public Func<string, bool> Fail { get; set; } = _ => false;
    public string Name => "scripted";
    public string Revision(TranslationMode mode) => "scripted@1";
    public Capability GetCapability(string source, string target, TranslationMode mode) => Capability.Ready;

    public Task<IReadOnlyList<TranslationResultItem>> TranslateAsync(IReadOnlyList<TranslationRequestItem> items, string source, string target, TranslationMode mode, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TranslationResultItem>>(items
            .Select(i => Fail(i.Text) ? new TranslationResultItem(i.Id, null, "model crashed") : new TranslationResultItem(i.Id, translate(i.Text)))
            .ToList());
}

public class UntranslatableBubbleTests
{
    private static FakeRecognizer TwoLines() => new()
    {
        Produce = (_, lang) => lang != "ja" ? [] :
        [
            new TextRegion("かしこまりました", new RectD(50, 50, 120, 24), false, 24),
            new TextRegion("こんにちは", new RectD(50, 300, 120, 24), false, 24),
        ],
    };

    [Fact]
    public async Task EmptyTranslationLeavesBalloonUntranslatedAndBookCompletes()
    {
        // M2M100은 "かしこまりました" 같은 짧은 대사에서 바로 끝내 빈 결과를 낸다. 그 때문에 책 전체가 멈추면 안 된다.
        using var dir = new TempDir();
        var translator = new ScriptedTranslator(text => text == "こんにちは" ? "안녕하세요" : "");
        var book = FakeBooks.Create(3);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(TwoLines(), translator), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(3)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed);

        Assert.Equal(TranslationPhase.Completed, scheduler.Get(book.BookKey, "ko")!.Phase);
        var page = scheduler.LoadCached(book.Pages[0].PageId, "ja", "ko", TranslationScope.Page, TranslationMode.Fast)!;
        Assert.True(page.IsComplete);
        Assert.Null(page.Bubbles.Single(b => b.Source == "かしこまりました").Translation);
        Assert.Equal("안녕하세요", page.Bubbles.Single(b => b.Source == "こんにちは").Translation);
    }

    [Fact]
    public async Task TranslatorErrorKeepsPageUnfinishedAndReportsTheError()
    {
        using var dir = new TempDir();
        var translator = new ScriptedTranslator(_ => "번역") { Fail = text => text == "かしこまりました" };
        var book = FakeBooks.Create(2);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(TwoLines(), translator), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(2)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed);

        var info = scheduler.Get(book.BookKey, "ko")!;
        Assert.Equal(TranslationPhase.Failed, info.Phase);
        Assert.Equal("model crashed", info.ReasonMessage);
        Assert.Null(scheduler.LoadCached(book.Pages[0].PageId, "ja", "ko", TranslationScope.Page, TranslationMode.Fast));
    }

    [Fact]
    public async Task RestartingFailedStripJobReadsUnfinishedPagesAgain()
    {
        // strip에서 실패한 쪽의 band가 "완료"로 남아 있으면 다시 시작해도 곧바로 같은 실패로 끝났다
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer
        {
            Produce = (img, lang) => img.Height == 2400
                ? [new TextRegion("こんにちは", new RectD(100, 1800, 200, 30), false, 30)]
                : [new TextRegion("こんにちは", new RectD(100, 400, 200, 30), false, 30), new TextRegion("さようなら", new RectD(100, 1200, 200, 30), false, 30)],
        };
        var translator = new ScriptedTranslator(text => text == "さようなら" ? "안녕히" : "안녕") { Fail = text => text == "さようなら" };
        var book = FakeBooks.Create(5);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer, translator), new TranslationCache(dir.Path), new FakePixels(_ => new SizeD(720, 576)),
            new FakeBookAccess(() => FakeBooks.Create(5)));
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Strip);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed);
        Assert.Equal(TranslationPhase.Failed, scheduler.Get(book.BookKey, "ko")!.Phase);
        Assert.Null(scheduler.LoadCached(book.Pages[4].PageId, "ja", "ko", TranslationScope.Strip, TranslationMode.Fast));

        translator.Fail = _ => false;
        scheduler.Start(BookTarget.From(book), "ko", "ja", TranslationScope.Strip);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Completed);

        var page4 = scheduler.LoadCached(book.Pages[4].PageId, "ja", "ko", TranslationScope.Strip, TranslationMode.Fast)!;
        Assert.Equal("안녕히", Assert.Single(page4.Bubbles).Translation);
        // 이미 끝났던 쪽의 풍선이 다시 들어가 겹치지 않는다
        var page3 = scheduler.LoadCached(book.Pages[3].PageId, "ja", "ko", TranslationScope.Strip, TranslationMode.Fast)!;
        Assert.Single(page3.Bubbles);
    }

    [Fact]
    public void FinishedPageShowsOriginalForBalloonWithoutTranslation()
    {
        TranslatedBubble Bubble(int id, string? translation) => new()
        {
            Id = id,
            Rect = new RectD(10, 10 + id * 100, 80, 30),
            GlyphSize = 20,
            IsVertical = false,
            Source = "くっく",
            Translation = translation,
            Background = new BubbleBackground(1, 1, 1, true),
        };
        IReadOnlyList<TranslatedBubble> machine = [Bubble(0, null), Bubble(1, "하하")];

        var finished = EditResolver.Resolve(machine, null, "ko", pageComplete: true);
        Assert.Equal(BubbleTextState.Untranslated, finished[0].State);
        // 패치를 그리지 않아 원문이 그대로 보인다("…" 대기 표시도 없음)
        Assert.Equal([1], EditResolver.ToPatchSources(finished, includePending: true).Select(p => p.Id));

        var inProgress = EditResolver.Resolve(machine, null, "ko");
        Assert.Equal(BubbleTextState.Pending, inProgress[0].State);
        Assert.Equal("…", EditResolver.ToPatchSources(inProgress, includePending: true).Single(p => p.Id == 0).Text);
    }
}
