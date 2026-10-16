using Panely.Domain.Model;

namespace Panely.Domain.Translation;

/// <summary>
/// OCR 결과 한 영역. rect는 회전 보정된 페이지(또는 band) 픽셀 좌상단 원점.
/// glyphSize: 세로 글은 글자 열 너비, 가로 글은 줄 높이.
/// </summary>
public sealed record TextRegion(string Text, RectD Rect, bool IsVertical, double GlyphSize)
{
    public TextRegion Offset(double dx, double dy) => this with { Rect = Rect.Offset(dx, dy) };
}

/// <summary>탐지 모델 class: 0 balloon, 1 textInBalloon, 2 freeText.</summary>
public enum DetectionKind { Balloon = 0, TextInBalloon = 1, FreeText = 2 }

public sealed record DetectedRegion(RectD Rect, DetectionKind Kind, double Score)
{
    public DetectedRegion Offset(double dx, double dy) => this with { Rect = Rect.Offset(dx, dy) };

    public bool IsTextBox => Kind is DetectionKind.TextInBalloon or DetectionKind.FreeText;
}

/// <summary>세로 band(타일/청크). [Top, Top+Height).</summary>
public readonly record struct Band(int Top, int Height)
{
    public int Bottom => Top + Height;
}
