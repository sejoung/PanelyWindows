using Panely.Core.Translation;

namespace Panely.Tests.Translation;

/// <summary>원문 언어 감지의 잡음 거르기(실제 사례: 한국어 OCR이 일본어 웹툰의 건물 무늬를 한글로 읽음).</summary>
public class DetectionEvidenceTests
{
    private static Dictionary<string, DetectionEvidence> Evidence(params (string Lang, string Text)[] lines)
    {
        var map = new Dictionary<string, DetectionEvidence>();
        foreach (var (lang, text) in lines) map[lang] = map.GetValueOrDefault(lang).Add(DetectionEvidence.Of(text, lang));
        return map;
    }

    [Fact]
    public void Ocr_noise_from_artwork_is_not_a_language()
    {
        // 세평유 3쪽(그림만 있는 쪽)에서 한국어 OCR이 실제로 돌려준 문자열
        var evidence = Evidence(("ko", "1닿11*들흁원『『"), ("en", ""));
        Assert.Null(TranslationScheduler.PickDetectedLanguage(evidence));
    }

    [Fact]
    public void Real_korean_dialogue_is_detected()
    {
        var evidence = Evidence(("ko", "비켜, 비켜!"), ("ko", "어디 가는 거야?"), ("en", "H"));
        Assert.Equal("ko", TranslationScheduler.PickDetectedLanguage(evidence));
    }

    [Fact]
    public void Japanese_counts_kanji_only_in_lines_with_kana()
    {
        Assert.Equal(5, DetectionEvidence.Of("聖下お待ち", "ja").Own);     // 가나 2 + 한자 3
        Assert.Equal(0, DetectionEvidence.Of("聖下陛下", "ja").Own);       // 가나 없는 한자 줄은 일본어 근거가 아님
        var evidence = Evidence(("ja", "アッシャー卿がお待ちでございます"), ("ko", "1들"));
        Assert.Equal("ja", TranslationScheduler.PickDetectedLanguage(evidence));
    }

    [Fact]
    public void English_ocr_reading_everything_as_latin_loses_to_cjk()
    {
        var evidence = Evidence(("en", "lIlI ll1 Il lI"), ("ja", "こんにちはありがとう"));
        Assert.Equal("ja", TranslationScheduler.PickDetectedLanguage(evidence));
    }

    [Theory]
    [InlineData(",가들十수덯t•i/", "ko", true)]   // 세평유: 한국어 OCR이 세로 일본어를 읽은 실제 결과
    [InlineData("비켜, 비켜!", "ko", false)]
    [InlineData("뭐?!", "ko", false)]
    [InlineData("OK 알았어", "ko", false)]
    [InlineData("アッシャー卿がお待ちでございます", "ja", false)]
    [InlineData("abc 가 def", "ja", true)]
    [InlineData("Where am I?", "en", false)]
    public void Mixed_garbage_lines_are_recognized(string text, string language, bool garbage)
        => Assert.Equal(garbage, Panely.Domain.Translation.Scripts.LooksLikeMixedGarbage(text, language));

    [Fact]
    public void Too_little_evidence_is_rejected()
    {
        Assert.Null(TranslationScheduler.PickDetectedLanguage(Evidence(("ko", "네"), ("en", "OK"))));
    }
}
