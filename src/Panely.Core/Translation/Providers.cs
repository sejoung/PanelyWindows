using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Core.Translation;

/// <summary>공급자 능력 상태.</summary>
public enum CapabilityStatus { Supported, Installed, Downloading, Unavailable }

/// <summary>
/// capability(source,target) 결과. Supported = 지원하지만 팩 설치 필요, Installed = 바로 실행 가능.
/// Reason은 Unavailable/Supported일 때 사용자에게 보여줄 문구(이미 현지화된 문장).
/// </summary>
public sealed record Capability(CapabilityStatus Status, string? Reason = null, double? Progress = null)
{
    public static readonly Capability Ready = new(CapabilityStatus.Installed);
    public bool IsReady => Status == CapabilityStatus.Installed;
    public static Capability Unavailable(string reason) => new(CapabilityStatus.Unavailable, reason);
}

/// <summary>팩 설치 진행.</summary>
public readonly record struct InstallProgress(long BytesDone, long BytesTotal, string? CurrentFile)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp(BytesDone / (double)BytesTotal, 0, 1) : 0;
}

/// <summary>언어팩(모델 패키지) 공급자.</summary>
public interface ILanguagePackProvider
{
    Capability GetCapability(string source, string target);

    /// <summary>필요한 팩 ID 목록(설치 안 된 것만).</summary>
    IReadOnlyList<string> MissingPacks(string source, string target);

    /// <summary>임시 파일 → checksum 검증 → publish. 검증되지 않은 manifest는 설치를 거부한다.</summary>
    Task InstallAsync(string packId, IProgress<InstallProgress>? progress, CancellationToken cancellationToken);

    /// <summary>이 언어쌍에 필요한 팩을 모두 차례로 받는다. 받는 동안은 팩 사이에도 다운로드 중으로 보인다.</summary>
    async Task InstallMissingAsync(string source, string target, CancellationToken cancellationToken)
    {
        foreach (var pack in MissingPacks(source, target)) await InstallAsync(pack, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>지금 받는 중인 팩과 전체 진행(받는 중이 아니면 null).</summary>
    PackDownloadStatus? DownloadStatus => null;

    event EventHandler? PacksChanged;
}

/// <summary>언어팩 다운로드 진행. 여러 팩을 받을 때 바이트는 전체 기준이다.</summary>
public sealed record PackDownloadStatus(string CurrentPackId, long BytesDone, long BytesTotal)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp(BytesDone / (double)BytesTotal, 0, 1) : 0;
}

/// <summary>회전 보정된 BGRA32 픽셀(좌상단 원점). 큰 bitmap을 UI 상태에 보관하지 않는다.</summary>
public sealed class PagePixels
{
    public PagePixels(int width, int height, byte[] bgra, int stride)
    {
        Width = width; Height = height; Bgra = bgra; Stride = stride;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public int Stride { get; }

    public static PagePixels Blank(int width, int height, byte value = 255)
    {
        var data = new byte[width * height * 4];
        Array.Fill(data, value);
        return new PagePixels(width, height, data, width * 4);
    }

    /// <summary>영역을 잘라낸다(이미지 밖은 흰색).</summary>
    public PagePixels Crop(int x, int y, int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        var result = Blank(width, height);
        for (var row = 0; row < height; row++)
        {
            var sy = y + row;
            if (sy < 0 || sy >= Height) continue;
            var sx0 = Math.Max(0, x);
            var sx1 = Math.Min(Width, x + width);
            if (sx1 <= sx0) continue;
            Buffer.BlockCopy(Bgra, sy * Stride + sx0 * 4, result.Bgra, row * result.Stride + (sx0 - x) * 4, (sx1 - sx0) * 4);
        }
        return result;
    }

    /// <summary>nearest/bilinear 축소·확대(OCR crop 재인식, 탐지 입력용).</summary>
    public PagePixels Resize(int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        var result = new byte[width * height * 4];
        var sx = Width / (double)width;
        var sy = Height / (double)height;
        for (var y = 0; y < height; y++)
        {
            var fy = Math.Min(Height - 1, (y + 0.5) * sy - 0.5);
            var y0 = Math.Max(0, (int)Math.Floor(fy)); var y1 = Math.Min(Height - 1, y0 + 1); var wy = Math.Clamp(fy - y0, 0, 1);
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Min(Width - 1, (x + 0.5) * sx - 0.5);
                var x0 = Math.Max(0, (int)Math.Floor(fx)); var x1 = Math.Min(Width - 1, x0 + 1); var wx = Math.Clamp(fx - x0, 0, 1);
                for (var c = 0; c < 4; c++)
                {
                    double p00 = Bgra[y0 * Stride + x0 * 4 + c], p01 = Bgra[y0 * Stride + x1 * 4 + c];
                    double p10 = Bgra[y1 * Stride + x0 * 4 + c], p11 = Bgra[y1 * Stride + x1 * 4 + c];
                    var v = (p00 * (1 - wx) + p01 * wx) * (1 - wy) + (p10 * (1 - wx) + p11 * wx) * wy;
                    result[(y * width + x) * 4 + c] = (byte)Math.Clamp(Math.Round(v), 0, 255);
                }
            }
        }
        return new PagePixels(width, height, result, width * 4);
    }

    /// <summary>다른 픽셀을 (x,y)에 복사(strip 합성용).</summary>
    public void Blit(PagePixels source, int x, int y)
    {
        for (var row = 0; row < source.Height; row++)
        {
            var dy = y + row;
            if (dy < 0 || dy >= Height) continue;
            var dx0 = Math.Max(0, x);
            var dx1 = Math.Min(Width, x + source.Width);
            if (dx1 <= dx0) continue;
            Buffer.BlockCopy(source.Bgra, row * source.Stride + (dx0 - x) * 4, Bgra, dy * Stride + dx0 * 4, (dx1 - dx0) * 4);
        }
    }

    public (byte B, byte G, byte R) PixelAt(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1); y = Math.Clamp(y, 0, Height - 1);
        var i = y * Stride + x * 4;
        return (Bgra[i], Bgra[i + 1], Bgra[i + 2]);
    }
}

/// <summary>OCR 공급자.</summary>
public interface ITextRecognizer
{
    string Name { get; }
    Capability GetCapability(string language);
    /// <summary>regions가 있으면 그 영역만(페이지 좌표) 인식. 결과는 페이지 픽셀 좌상단 원점.</summary>
    Task<IReadOnlyList<TextRegion>> RecognizeAsync(PagePixels image, string language, IReadOnlyList<RectD>? regions, CancellationToken cancellationToken);
}

/// <summary>말풍선 탐지 공급자. 사용 불가면 null을 돌려준다.</summary>
public interface IBalloonDetector
{
    string Name { get; }
    /// <summary>캐시 identity에 들어가는 모델 revision. 사용 불가면 "none".</summary>
    string Revision { get; }
    Capability Capability { get; }
    Task<IReadOnlyList<DetectedRegion>?> DetectAsync(PagePixels image, CancellationToken cancellationToken);
}

public sealed record TranslationRequestItem(string Id, string Text);

/// <summary>앞서 번역한 말풍선(읽는 순서). 번역기가 말투·이름을 이어 가는 데만 쓴다.</summary>
public sealed record TranslationContextLine(string Source, string Translation);

/// <summary>Text 또는 Error 중 하나.</summary>
public sealed record TranslationResultItem(string Id, string? Text, string? Error = null);

/// <summary>번역 공급자. 결과는 입력 순서를 가정하지 말고 ID로 맞춘다.</summary>
public interface ITranslator
{
    string Name { get; }
    /// <summary>캐시 identity용 provider/model revision.</summary>
    string Revision { get; }
    Capability GetCapability(string source, string target);
    /// <summary>items는 읽는 순서. context는 그 앞에서 이미 번역한 말풍선(없으면 빈 목록).</summary>
    Task<IReadOnlyList<TranslationResultItem>> TranslateAsync(IReadOnlyList<TranslationRequestItem> items, IReadOnlyList<TranslationContextLine> context,
        string source, string target, CancellationToken cancellationToken);

    /// <summary>
    /// 모델을 미리 올린다(번역 작업이 시작될 때 분석과 함께 부른다). 첫 결과가 늦어지지 않게 하려는 것이며 실패해도 무시해도 된다.
    /// </summary>
    Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>앱에 연결된 공급자 묶음.</summary>
public sealed class TranslationProviders
{
    public required ILanguagePackProvider Packs { get; init; }
    public required ITextRecognizer Recognizer { get; init; }
    public required IBalloonDetector Detector { get; init; }
    public required ITranslator Translator { get; init; }
    /// <summary>개발용 가짜 공급자 사용 중(PANELY_DEV_PSEUDO_TRANSLATION=1).</summary>
    public bool IsDeveloperTestProvider { get; init; }
}
