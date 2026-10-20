using Panely.Core.Localization;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Core.Translation;

/// <summary>공급자 이름 표시용 상수.</summary>
public static class DeveloperProviders
{
    public const string EnvironmentVariable = "PANELY_DEV_PSEUDO_TRANSLATION";

    /// <summary>PANELY_DEV_PSEUDO_TRANSLATION=1일 때만 true. 기본 배포에서는 항상 false.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";

    public static string Label => Loc.T("Developer test provider");
}

/// <summary>
/// 개발 전용 가짜 번역기. UI 흐름 확인용이며 실제 번역이 아니다. `⟦ko⟧ 원문` 형태로 결과를 만든다.
/// </summary>
public sealed class PseudoTranslator : ITranslator
{
    public string Name => DeveloperProviders.Label;
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(40);

    public string Revision => "pseudo-1";

    public Capability GetCapability(string source, string target) =>
        source == target ? Capability.Unavailable(Loc.T("This language pair isn't supported")) : Capability.Ready;

    public async Task<IReadOnlyList<TranslationResultItem>> TranslateAsync(IReadOnlyList<TranslationRequestItem> items, IReadOnlyList<TranslationContextLine> context,
        string source, string target, CancellationToken cancellationToken)
    {
        var list = new List<TranslationResultItem>();
        foreach (var item in items.Reverse())
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            list.Add(new TranslationResultItem(item.Id, PseudoText(item.Text, target)));
        }
        return list; // 의도적으로 역순: 호출자는 ID로 맞춰야 한다
    }

    public static string PseudoText(string text, string target) => target switch
    {
        TranslationLanguages.Korean => $"[번역] {text}",
        TranslationLanguages.Japanese => $"［翻訳］{text}",
        TranslationLanguages.ChineseSimplified => $"［翻译］{text}",
        TranslationLanguages.ChineseTraditional => $"［翻譯］{text}",
        _ => $"[translated] {text}",
    };
}

/// <summary>
/// 개발 전용 OCR: 실제 OCR이 가능하면 그것을 쓰고, 해당 언어 OCR이 없을 때만 페이지에 가짜 풍선 2개를 만든다.
/// </summary>
public sealed class PseudoRecognizer(ITextRecognizer inner) : ITextRecognizer
{
    public string Name => $"{inner.Name} + {DeveloperProviders.Label}";

    public Capability GetCapability(string language) => Capability.Ready;

    public async Task<IReadOnlyList<TextRegion>> RecognizeAsync(PagePixels image, string language, IReadOnlyList<RectD>? regions, CancellationToken cancellationToken)
    {
        if (inner.GetCapability(language).IsReady)
            return await inner.RecognizeAsync(image, language, regions, cancellationToken).ConfigureAwait(false);
        var sample = language switch
        {
            TranslationLanguages.Japanese => ("こんにちは", "ありがとう"),
            TranslationLanguages.Korean => ("안녕하세요", "고마워요"),
            TranslationLanguages.ChineseSimplified => ("你好", "谢谢"),
            TranslationLanguages.ChineseTraditional => ("你好", "謝謝"),
            _ => ("Hello there", "Thank you"),
        };
        if (regions is not null)
            return regions.Select(r => new TextRegion(sample.Item1, r, false, Math.Max(10, r.Height / 2))).ToList();
        var g = Math.Max(14, image.Width / 30.0);
        return
        [
            new TextRegion(sample.Item1, new RectD(image.Width * 0.12, image.Height * 0.08, g * 6, g), false, g),
            new TextRegion(sample.Item2, new RectD(image.Width * 0.55, image.Height * 0.55, g * 6, g), false, g),
        ];
    }
}
