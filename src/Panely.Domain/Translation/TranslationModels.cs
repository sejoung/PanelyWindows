using Panely.Domain.Model;

namespace Panely.Domain.Translation;

/// <summary>풍선 주변 배경 톤(0..1)과 flat 여부.</summary>
public sealed record BubbleBackground(double Red, double Green, double Blue, bool IsUniform)
{
    public static readonly BubbleBackground White = new(1, 1, 1, true);

    public double Luminance => 0.299 * Red + 0.587 * Green + 0.114 * Blue;

    /// <summary>luminance &lt; .5 이면 흰 글씨.</summary>
    public bool UsesWhiteText => Luminance < 0.5;
}

/// <summary>캐시 원본의 풍선. rect는 회전 보정된 페이지 픽셀 좌상단 원점.</summary>
public sealed record TranslatedBubble
{
    public required int Id { get; init; }
    public required RectD Rect { get; init; }
    public required double GlyphSize { get; init; }
    public required bool IsVertical { get; init; }
    public required string Source { get; init; }
    public string? Translation { get; init; }
    public required BubbleBackground Background { get; init; }
    public RectD? BalloonRect { get; init; }
}

public sealed record TranslatedPage
{
    public const int SchemaVersion = 1;
    public required string PageId { get; init; }
    public required string SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public required TranslationScope Scope { get; init; }
    public required string ProviderRevision { get; init; }
    public required int AnalysisVersion { get; init; }
    public required double ImageWidth { get; init; }
    public required double ImageHeight { get; init; }
    public required bool IsComplete { get; init; }
    public required IReadOnlyList<TranslatedBubble> Bubbles { get; init; }
}

public sealed record AddedBalloon(double GlyphSize, bool IsVertical, BubbleBackground Background);

/// <summary>
/// 한 풍선의 사용자 수정. recognizedSource+원래 rect가 매칭 근거(숫자 ID로 영속 매칭하지 않음).
/// 원문 수정·숨김은 모든 언어, translations는 해당 언어만.
/// </summary>
public sealed record BubbleEdit
{
    public required string RecognizedSource { get; init; }
    public required RectD Rect { get; init; }
    public bool IsHidden { get; init; }
    public string? CorrectedSource { get; init; }
    /// <summary>correctedSource의 언어별 기계 재번역 결과.</summary>
    public IReadOnlyDictionary<string, string> Retranslations { get; init; } = new Dictionary<string, string>();
    /// <summary>언어별 사용자 직접 번역.</summary>
    public IReadOnlyDictionary<string, string> Translations { get; init; } = new Dictionary<string, string>();
    /// <summary>사용자가 드래그로 추가한 풍선이면 non-null. Hide 대신 Remove.</summary>
    public AddedBalloon? Added { get; init; }
}

public sealed record PageEdits
{
    public const int SchemaVersion = 1;
    public required string PageId { get; init; }
    public required TranslationScope Scope { get; init; }
    public required IReadOnlyList<BubbleEdit> Bubbles { get; init; }
}
