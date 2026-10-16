using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Panely.Core.Translation;
using Panely.Domain.Translation;
using Xunit.Abstractions;

namespace Panely.Tests.Translation;

/// <summary>
/// 실제 Windows OCR 어댑터 검증. 해당 OCR 언어가 이 PC에 없으면 결과만 기록하고 통과한다(환경 의존).
/// </summary>
public class WindowsOcrIntegrationTests(ITestOutputHelper output)
{
    private static PagePixels Render(string text, int width, int height, double fontSize, string font = "Segoe UI")
    {
        PagePixels? result = null;
        var thread = new Thread(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(font), fontSize, Brushes.Black, 1.0);
                dc.DrawText(ft, new Point(40, 60));
            }
            var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            var data = new byte[width * height * 4];
            converted.CopyPixels(data, width * 4, 0);
            result = new PagePixels(width, height, data, width * 4);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result!;
    }

    [Fact]
    public async Task RecognizesEnglishTextWhenOcrLanguageInstalled()
    {
        var ocr = new WindowsOcrRecognizer();
        foreach (var lang in Panely.Domain.Model.TranslationLanguages.All)
            output.WriteLine($"OCR {lang}: {ocr.GetCapability(lang).Status} {ocr.GetCapability(lang).Reason}");
        if (!ocr.GetCapability("en").IsReady) return;

        var image = Render("Hello there, General Kenobi", 900, 220, 40);
        var regions = await ocr.RecognizeAsync(image, "en", null, CancellationToken.None);
        foreach (var r in regions) output.WriteLine($"{r.Text} @ {r.Rect} glyph={r.GlyphSize:0.0} vertical={r.IsVertical}");
        var all = string.Join(" ", regions.Select(r => r.Text));
        Assert.Contains("Kenobi", all);
        var line = regions.First(r => r.Text.Contains("Kenobi"));
        Assert.InRange(line.Rect.Y, 40, 140);
        Assert.InRange(line.GlyphSize, 20, 80);

        // 잘라낸 영역 재인식 경로(확대 crop)
        var cropped = await ocr.RecognizeAsync(image, "en", [line.Rect], CancellationToken.None);
        Assert.Contains(cropped, r => r.Text.Contains("Kenobi"));

        // 그룹 → 효과음 제외 → 배경까지 파이프라인
        var analyzer = new PageAnalyzer(ocr, new FakeDetector());
        var bubbles = await analyzer.AnalyzeAsync(image, "en", CancellationToken.None);
        var b = Assert.Single(bubbles);
        Assert.True(b.Background.IsUniform);
        Assert.False(b.Background.UsesWhiteText);
    }

    [Fact]
    public async Task RecognizesTallImageAcrossTiles()
    {
        var ocr = new WindowsOcrRecognizer();
        if (!ocr.GetCapability("en").IsReady) return;
        // 720×5000: OCR 타일 3개. 텍스트는 첫 타일 안
        var image = Render("Tiled page reading works", 720, 5000, 36);
        var regions = await ocr.RecognizeAsync(image, "en", null, CancellationToken.None);
        Assert.Contains(regions, r => r.Text.Contains("reading"));
        Assert.Equal(3, TilePlanner.OcrTiles(720, 5000).Count);
    }

    [Fact]
    public async Task RecognizesKoreanAndGroupsWhenInstalled()
    {
        var ocr = new WindowsOcrRecognizer();
        if (!ocr.GetCapability("ko").IsReady) return;
        var image = Render("안녕하세요 반가워요", 900, 220, 44, "Malgun Gothic");
        var analyzer = new PageAnalyzer(ocr, new FakeDetector());
        var bubbles = await analyzer.AnalyzeAsync(image, "ko", CancellationToken.None);
        foreach (var b in bubbles) output.WriteLine($"{b.Source} @ {b.Rect}");
        Assert.Contains(bubbles, b => b.Source.Replace(" ", "").Contains("안녕하세요"));
        Assert.Equal("ko", LanguageDetector.Detect(bubbles.Select(b => b.Source)));
    }
}
