using Panely.Core.Translation;
using Panely.Domain.Model;

namespace Panely.Tests.Translation;

public class BackgroundSamplerTests
{
    private static void Fill(PagePixels p, RectD r, byte value)
    {
        for (var y = (int)r.Top; y < Math.Min((int)r.Bottom, p.Height); y++)
            for (var x = (int)r.Left; x < Math.Min((int)r.Right, p.Width); x++)
            {
                var i = y * p.Stride + x * 4;
                p.Bgra[i] = p.Bgra[i + 1] = p.Bgra[i + 2] = value;
            }
    }

    [Fact]
    public void White_balloon_with_descenders_touching_the_ring_is_flat()
    {
        var page = PagePixels.Blank(400, 300);
        var text = new RectD(100, 100, 150, 40);
        Fill(page, text, 0); // 글자 덩어리
        // OCR rect 아래로 내려온 g/y 꼬리 몇 개가 고리에 걸린다
        Fill(page, new RectD(130, 140, 4, 12), 0);
        Fill(page, new RectD(200, 140, 4, 12), 0);
        var (bg, _) = BackgroundSampler.Sample(page, text, 36, null);
        Assert.True(bg.IsUniform);
        Assert.True(bg.Luminance > 0.95);
    }

    [Fact]
    public void Busy_artwork_is_not_flat()
    {
        var page = PagePixels.Blank(400, 300);
        // 체크무늬 그림
        for (var y = 0; y < 300; y += 8)
            for (var x = 0; x < 400; x += 8)
                if (((x + y) / 8) % 2 == 0) Fill(page, new RectD(x, y, 8, 8), 60);
        var text = new RectD(100, 100, 150, 40);
        var (bg, _) = BackgroundSampler.Sample(page, text, 36, null);
        Assert.False(bg.IsUniform);
    }

    [Fact]
    public void Black_balloon_uses_white_text()
    {
        var page = PagePixels.Blank(400, 300, 0);
        var text = new RectD(100, 100, 150, 40);
        Fill(page, text, 255);
        var (bg, _) = BackgroundSampler.Sample(page, text, 36, null);
        Assert.True(bg.IsUniform);
        Assert.True(bg.UsesWhiteText);
    }
}
