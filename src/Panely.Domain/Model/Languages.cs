namespace Panely.Domain.Model;

public static class TranslationLanguages
{
    public const string Japanese = "ja";
    public const string ChineseSimplified = "zh-Hans";
    public const string ChineseTraditional = "zh-Hant";
    public const string Korean = "ko";
    public const string English = "en";

    public static readonly IReadOnlyList<string> All = [Japanese, ChineseSimplified, ChineseTraditional, Korean, English];

    public static bool IsSupported(string? code) => code is not null && All.Contains(code);

    public static bool IsCjkVertical(string code) => code is Japanese or ChineseSimplified or ChineseTraditional;

    /// <summary>기본 대상: UI 언어, 미지원이면 en.</summary>
    public static string DefaultTarget(string uiLanguage) => uiLanguage switch
    {
        "ko" => Korean,
        "ja" => Japanese,
        _ => English,
    };
}
