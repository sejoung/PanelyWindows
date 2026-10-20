using Panely.Core.Translation;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Tests.Translation;

/// <summary>만화 OCR(manga-ocr / PP-OCRv5) 해석 규칙과 번역 반복 방지.</summary>
public class ComicOcrDecodingTests
{
    [Fact]
    public void Manga_ocr_tokens_skip_specials_and_convert_to_full_width()
    {
        string[] vocab = ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "聖", "下", "4", "時", "間", "…", " "];
        // [CLS] 聖 下 [SEP]
        Assert.Equal("聖下", MangaOcrText.Decode([2, 5, 6, 3], vocab));
        // 반각 숫자는 전각으로(jaconv.h2z), 공백 제거, … → ...(전각 점)
        Assert.Equal("４時間．．．", MangaOcrText.Decode([2, 7, 11, 8, 9, 10, 3], vocab));
    }

    [Fact]
    public void Manga_ocr_collapses_dot_runs()
        => Assert.Equal("．．．やっぱり", MangaOcrText.Postprocess("・・・やっぱり"));

    [Fact]
    public void Ctc_greedy_collapses_repeats_and_blanks()
    {
        string[] dict = ["가", "나", "다"];
        // classes = blank + 3 + space = 5. 시퀀스: 가 가 blank 나 space 다
        var steps = new[] { 1, 1, 0, 2, 4, 3 };
        var logits = new float[steps.Length * 5];
        for (var t = 0; t < steps.Length; t++) logits[t * 5 + steps[t]] = 10f;
        var (text, confidence) = CtcDecoder.Decode(logits, steps.Length, 5, dict);
        Assert.Equal("가나 다", text);
        Assert.True(confidence > 0.9);
    }

    [Fact]
    public void Ctc_handles_probabilities_without_softmax()
    {
        string[] dict = ["a", "b"];
        float[] probs = [0.1f, 0.8f, 0.1f, 0.9f, 0.05f, 0.05f, 0.1f, 0.1f, 0.8f];
        var (text, confidence) = CtcDecoder.Decode(probs, 3, 3, dict);
        Assert.Equal("ab", text);
        Assert.InRange(confidence, 0.79, 0.81);
    }

    [Fact]
    public void Db_postprocess_finds_and_expands_text_lines()
    {
        const int w = 100, h = 60;
        var map = new float[w * h];
        void Fill(int x0, int y0, int x1, int y1) { for (var y = y0; y < y1; y++) for (var x = x0; x < x1; x++) map[y * w + x] = 0.9f; }
        Fill(10, 10, 60, 18);   // 가로 줄
        Fill(80, 5, 88, 55);    // 세로 줄
        Fill(30, 40, 31, 41);   // 1px 잡음(최소 크기 미만)
        var boxes = DbPostProcess.Boxes(map, w, h);
        Assert.Equal(2, boxes.Count);
        var line = boxes.Single(b => b.Width > b.Height);
        Assert.True(line.Left < 10 && line.Right > 60 && line.Top < 10 && line.Bottom > 18); // unclip으로 넓어짐
        Assert.Contains(boxes, b => b.Height > b.Width);
    }

    [Theory]
    [InlineData(720, 576, 1216, 960)]   // 짧은 변 960으로 키우고 32배수(1200 → 1216)
    [InlineData(2000, 1500, 2016, 1504)] // 이미 크면 그대로(32배수로만)
    [InlineData(720, 12000, 160, 2400)]  // 아주 긴 웹툰 조각: 긴 변 2400으로 제한
    public void Db_input_size_follows_limit_rules(int width, int height, int expectedW, int expectedH)
        => Assert.Equal((expectedW, expectedH), DbPostProcess.InputSize(width, height));

    [Fact]
    public void Rotating_a_vertical_line_counter_clockwise()
    {
        var src = PagePixels.Blank(2, 3, 0);
        src.Bgra[(0 * src.Stride) + 0] = 9; // (x=0,y=0) 위쪽 왼쪽
        var rotated = PpOcrEngine.RotateCounterClockwise(src);
        Assert.Equal(3, rotated.Width);
        Assert.Equal(2, rotated.Height);
        Assert.Equal(9, rotated.Bgra[(1 * rotated.Stride) + 0]); // 반시계 90°: 왼쪽 위 → 왼쪽 아래
    }

    [Fact]
    public void Measure_uses_tight_lines_inside_a_loose_detector_box()
    {
        // 세평유 12쪽 "聖下": 탐지 상자 98×119 안에 세로 줄 하나(35×70)
        var box = new RectD(293, 80, 98, 119);
        var region = ModelTextRecognizer.Measure("聖下", box, [new RectD(324, 102, 35, 70)]);
        Assert.Equal(new RectD(324, 102, 35, 70), region.Rect);
        Assert.True(region.IsVertical);
        Assert.InRange(region.GlyphSize, 20, 35);
    }

    [Fact]
    public void Measure_ignores_fragment_lines_over_artwork()
    {
        // 그림 위 세로 대사: 줄 탐지가 얇은 조각만 잡으면 상자와 면적 어림값을 쓴다
        var box = new RectD(475, 1, 152, 228);
        var region = ModelTextRecognizer.Measure("男のお導きになどあるはずがいません", box, [new RectD(500, 10, 7, 200), new RectD(520, 10, 8, 200)]);
        Assert.Equal(box, region.Rect);
        Assert.True(region.GlyphSize > 20);
    }

    [Fact]
    public void Measure_without_lines_falls_back_to_the_box()
    {
        var box = new RectD(0, 0, 60, 200);
        var region = ModelTextRecognizer.Measure("こんにちは", box, []);
        Assert.Equal(box, region.Rect);
        Assert.True(region.IsVertical);
    }

    [Fact]
    public void Glyph_estimate_stays_inside_the_box()
    {
        var g = ModelTextRecognizer.EstimateGlyph(new RectD(0, 0, 60, 300), "アッシャー卿がお待ちでございます");
        Assert.InRange(g, 8, 60);
        Assert.Equal(8, ModelTextRecognizer.EstimateGlyph(new RectD(0, 0, 5, 5), "あ"));
    }

    [Fact]
    public void Two_text_boxes_in_one_balloon_get_separate_patches()
    {
        // 세평유 12쪽: 두 갈래 풍선 하나에 "聖下"와 긴 세로 대사가 따로 있다
        var balloon = new RectD(60, 40, 360, 440);
        PatchSource Src(int id, RectD r) => new(id, r, 24, true, BubbleBackground.White, balloon, "x");
        var patches = PatchGeometry.Layout([Src(0, new RectD(320, 90, 40, 80)), Src(1, new RectD(210, 210, 90, 240))], 720);
        Assert.Equal(2, patches.Count);
        Assert.False(patches[0].Rect.Intersects(patches[1].Rect));
        // 풍선 하나에 상자 하나면 기존처럼 풍선 중앙 66%까지 넓힌다
        var single = PatchGeometry.Layout([Src(0, new RectD(210, 210, 90, 240))], 720).Single();
        Assert.True(single.Rect.Width >= balloon.Width * PatchGeometry.BalloonInteriorShare - 0.01);
    }


    [Theory]
    [InlineData("に", true)]
    [InlineData("は。", true)]
    [InlineData("あ", false)]
    [InlineData("聖下", false)]
    public void Lone_japanese_particles_are_noise(string text, bool noise)
        => Assert.Equal(noise, Scripts.IsLoneParticle(text, TranslationLanguages.Japanese));

    [Fact]
    public void Model_recognizer_asks_to_download_missing_packs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "panely-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var recognizer = new ModelTextRecognizer(new ModelStore(dir, manifests: ModelCatalog.BuiltIn));
            var ja = recognizer.GetCapability(TranslationLanguages.Japanese);
            Assert.Equal(CapabilityStatus.Supported, ja.Status);
            Assert.False(recognizer.IsModelReady(TranslationLanguages.Japanese));
            Assert.Equal([ModelCatalog.MangaOcrId, ModelCatalog.PpOcrId], ModelTextRecognizer.PacksFor(TranslationLanguages.Japanese));
            Assert.Equal([ModelCatalog.PpOcrId], ModelTextRecognizer.PacksFor(TranslationLanguages.Korean));
            Assert.False(recognizer.CanIdentify);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
