using Panely.Core.Localization;
using Panely.Core.Translation;
using Panely.Domain.Model;

namespace Panely.Tests.Translation;

/// <summary>원문 언어 감지 실패 원인 안내와, 앞쪽에 글자가 없는 웹툰에서의 감지.</summary>
public class LanguageDetectionTests
{
    [Fact]
    public void Failure_message_names_the_readable_languages_when_some_are_missing()
    {
        var message = TranslationScheduler.DetectionFailureMessage(["en", "ko"]);
        Assert.Contains(Loc.LanguageName("en"), message, StringComparison.Ordinal);
        Assert.Contains(Loc.LanguageName("ko"), message, StringComparison.Ordinal);
        Assert.NotEqual(TranslationScheduler.DetectionFailureMessage(TranslationLanguages.All.ToList()), message);
    }

    [Fact]
    public void Failure_message_without_any_ocr_explains_installation()
    {
        var ko = Loc.TIn("ko", "No text recognition language is installed on this PC, so the book can't be read. Add a language with its optical character recognition feature in Windows Settings › Time & language › Language & region.");
        Assert.Contains("광학 문자 인식", ko, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(TranslationScheduler.DetectionFailureMessage([])));
    }

    [Fact]
    public async Task Detection_reads_past_leading_pages_without_text()
    {
        // 웹툰처럼 앞 6쪽에는 글자가 없고 7쪽부터 일본어가 나오는 책. 예전에는 3쪽만 읽어 감지에 실패했다.
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer
        {
            Produce = (image, language) =>
            {
                var pageMarker = image.Bgra[0];
                if (language != "ja" || pageMarker < 7) return [];
                return [new Panely.Domain.Translation.TextRegion("こんにちはありがとう", new RectD(50, 50, 200, 24), false, 24)];
            },
        };
        var book = FakeBooks.Create(10);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(10)));
        scheduler.Start(BookTarget.From(book), "ko", null, TranslationScope.Page, focusPage: 0);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase is TranslationPhase.Completed or TranslationPhase.Failed);
        var info = scheduler.Get(book.BookKey, "ko")!;
        Assert.Equal("ja", info.Source);
    }

    [Fact]
    public async Task Undetectable_book_reports_which_languages_this_pc_can_read()
    {
        using var dir = new TempDir();
        var recognizer = new FakeRecognizer { Produce = (_, _) => [] };
        recognizer.Languages.Clear();
        recognizer.Languages.UnionWith(["en", "ko"]); // 이 PC처럼 일본어·중국어 글자 인식이 없음
        var book = FakeBooks.Create(2);
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(recognizer), new TranslationCache(dir.Path), new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(2)));
        scheduler.Start(BookTarget.From(book), "ko", null, TranslationScope.Page);
        await Wait.UntilAsync(() => scheduler.Get(book.BookKey, "ko")?.Phase == TranslationPhase.Failed);
        var info = scheduler.Get(book.BookKey, "ko")!;
        Assert.Equal(TranslationStopReason.CouldNotDetectLanguage, info.Reason);
        Assert.Equal(TranslationScheduler.DetectionFailureMessage(["ko", "en"]), info.ReasonMessage);
    }
}
