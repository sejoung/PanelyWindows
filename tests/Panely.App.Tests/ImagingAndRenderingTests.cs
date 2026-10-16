using System.Windows;
using System.Windows.Media;
using Panely.App.Imaging;
using Panely.App.Translation;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.App.Tests;

public class ImagingAndRenderingTests
{
    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("tif")]
    public void Decodes_common_formats_to_frozen_bitmaps(string format)
    {
        var bytes = TestImages.Make(120, 80, format, Colors.White);
        var bitmap = new PageDecoder().Decode(bytes, format);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(120, bitmap.PixelWidth);
        Assert.Equal(80, bitmap.PixelHeight);
    }

    [Theory]
    [InlineData((ushort)1, 120, 80)]
    [InlineData((ushort)3, 120, 80)]
    [InlineData((ushort)6, 80, 120)]
    [InlineData((ushort)8, 80, 120)]
    public void Applies_exif_orientation(ushort orientation, int width, int height)
    {
        var bytes = TestImages.Make(120, 80, "jpg", Colors.White, exifOrientation: orientation);
        var bitmap = new PageDecoder().Decode(bytes, "jpg");
        Assert.Equal(width, bitmap.PixelWidth);
        Assert.Equal(height, bitmap.PixelHeight);
    }

    [Fact]
    public void Downscaled_decode_respects_max_width()
    {
        var bytes = TestImages.Make(1600, 1000, "png", Colors.White);
        var bitmap = new PageDecoder().Decode(bytes, "png", maxPixelWidth: 400);
        Assert.Equal(400, bitmap.PixelWidth);
    }

    [Fact]
    public void Corrupt_bytes_raise_a_decode_error()
    {
        var ex = Record.Exception(() => new PageDecoder().Decode([1, 2, 3, 4, 5], "png"));
        Assert.NotNull(ex);
        Assert.IsType<ImageDecodeException>(ex);
    }

    private static PatchLayout Patch(RectD textRect, BubbleBackground background, string text)
        => PatchGeometry.Layout([new PatchSource(0, textRect, 24, false, background, null, text)], 400).Single();

    [Fact]
    public async Task Export_renderer_covers_original_text_and_draws_translation()
    {
        // 흰 바탕의 검은 "원문" 덩어리 위에 번역 patch를 그린다(뷰어와 같은 painter)
        var original = TestImages.Make(400, 300, "png", Colors.White, new Int32Rect(150, 130, 100, 30));
        using var renderer = new WpfTranslationRenderer(new PageDecoder());
        var patch = Patch(new RectD(150, 130, 100, 30), BubbleBackground.White, "안녕");
        var (bytes, extension) = await renderer.RenderAsync(original, "png", [patch], CancellationToken.None);

        Assert.Equal("png", extension); // PNG 원본은 PNG로
        var result = Sta.Run(() => TestImages.Decode(bytes));
        Assert.Equal(400, result.PixelWidth); // 원래 해상도
        Assert.Equal(300, result.PixelHeight);

        // 원문 덩어리의 가장자리(글자가 그려지지 않을 위치)는 배경색으로 덮여야 한다
        var corner = Sta.Run(() => TestImages.PixelAt(result, 152, 132));
        Assert.True(corner.R > 200 && corner.G > 200 && corner.B > 200, $"원문이 덮이지 않음: {corner}");

        // patch 안 어딘가에는 번역 글자(어두운 픽셀)가 있어야 한다
        var dark = Sta.Run(() =>
        {
            var count = 0;
            for (var y = 130; y < 160; y += 2)
                for (var x = 150; x < 250; x += 2)
                {
                    var p = TestImages.PixelAt(result, x, y);
                    if (p.R < 100 && p.G < 100 && p.B < 100) count++;
                }
            return count;
        });
        Assert.True(dark > 5, "번역 글자가 그려지지 않았습니다.");
        // patch 밖은 그대로
        Assert.Equal((byte)255, Sta.Run(() => TestImages.PixelAt(result, 10, 10)).R);
    }

    [Fact]
    public async Task Dark_balloon_gets_light_text_and_jpeg_stays_jpeg()
    {
        var original = TestImages.Make(400, 300, "jpg", Colors.Black, null);
        using var renderer = new WpfTranslationRenderer(new PageDecoder());
        var patch = Patch(new RectD(150, 130, 100, 30), new BubbleBackground(0, 0, 0, true), "고마워");
        var (bytes, extension) = await renderer.RenderAsync(original, "jpg", [patch], CancellationToken.None);
        Assert.Equal("jpg", extension);
        var result = Sta.Run(() => TestImages.Decode(bytes));
        var light = Sta.Run(() =>
        {
            var count = 0;
            for (var y = 130; y < 160; y += 2)
                for (var x = 150; x < 250; x += 2)
                {
                    var p = TestImages.PixelAt(result, x, y);
                    if (p.R > 180 && p.G > 180 && p.B > 180) count++;
                }
            return count;
        });
        Assert.True(light > 5, "어두운 풍선 위 번역 글자가 밝은색이 아닙니다.");
    }
}
