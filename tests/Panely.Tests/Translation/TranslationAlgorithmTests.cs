using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Tests.Translation;

public class SoundEffectFilterTests
{
    [Theory]
    [InlineData("スッ", "ja")]
    [InlineData("パチパチ", "ja")]
    [InlineData("ドーン", "ja")]
    [InlineData("ドキドキ", "ja")]
    [InlineData("짝짝", "ko")]
    [InlineData("쾅", "ko")]
    [InlineData("두근두근", "ko")]
    [InlineData("으아아아", "ko")]
    public void ExcludesSoundEffects(string text, string lang) => Assert.True(SoundEffectFilter.IsSoundEffect(text, lang));

    [Theory]
    [InlineData("はい", "ja")]
    [InlineData("メラス", "ja")]
    [InlineData("비켜, 비켜!", "ko")]
    [InlineData("네", "ko")]
    [InlineData("本当", "ja")]
    [InlineData("ドンッ", "en")]
    [InlineData("哈哈", "zh-Hans")]
    [InlineData("ありがとうございました", "ja")]
    public void KeepsDialogue(string text, string lang) => Assert.False(SoundEffectFilter.IsSoundEffect(text, lang));
}

public class ScriptTests
{
    [Theory]
    [InlineData("こんにちは", "ja")]
    [InlineData("안녕하세요 반가워요", "ko")]
    [InlineData("这是什么东西", "zh-Hans")]
    [InlineData("這是什麼東西", "zh-Hant")]
    [InlineData("Hello there friend", "en")]
    public void DetectsLanguage(string text, string expected) => Assert.Equal(expected, LanguageDetector.Detect([text]));

    [Fact]
    public void DetectReturnsNullWithoutEvidence() => Assert.Null(LanguageDetector.Detect(["123", "!?"]));

    [Theory]
    [InlineData("ね", "ja", true)]
    [InlineData("漢", "zh-Hans", true)]
    [InlineData("ね", "zh-Hans", false)]
    [InlineData("ab", "en", true)]
    [InlineData("a", "en", false)]
    [InlineData("!!", "ko", false)]
    public void OwnScriptNoiseFilter(string text, string lang, bool expected)
        => Assert.Equal(expected, Scripts.HasOwnScript(text, lang));

    [Fact]
    public void LineJoinerPerLanguage()
    {
        Assert.Equal(" ", Scripts.LineJoiner("ko"));
        Assert.Equal(" ", Scripts.LineJoiner("en"));
        Assert.Equal("", Scripts.LineJoiner("ja"));
        Assert.Equal("", Scripts.LineJoiner("zh-Hant"));
    }

    [Fact]
    public void NormalizeCollapsesWhitespaceAndWidth()
    {
        Assert.Equal("ABC 1", Scripts.Normalize("  ＡＢＣ\n\n１ "));
        Assert.Equal("こんにちは", Scripts.RemoveCjkSpaces("こ ん に ち は"));
    }

    [Fact]
    public void LooksLikeTargetLanguage()
    {
        Assert.True(LanguageDetector.LooksLikeLanguage("안녕", "ko"));
        Assert.False(LanguageDetector.LooksLikeLanguage("こんにちは", "ko"));
        Assert.False(LanguageDetector.LooksLikeLanguage("", "en"));
        Assert.True(LanguageDetector.LooksLikeLanguage("Hi!", "en"));
    }
}

public class TilePlannerTests
{
    [Fact]
    public void DetectorChunksSplitLongImages()
    {
        var chunks = TilePlanner.DetectorChunks(720, 12000);
        Assert.True(chunks.Count > 1);
        Assert.Equal(0, chunks[0].Top);
        Assert.Equal(1080, chunks[0].Height);
        Assert.Equal(12000, chunks[^1].Bottom);
        Assert.Equal(1080 - 360, chunks[1].Top);
        Assert.Single(TilePlanner.DetectorChunks(1000, 1000));
    }

    [Fact]
    public void SeamTouchingRectIsExcludedOnlyOnInteriorSeams()
    {
        var first = new Band(0, 2400);
        var middle = new Band(1400, 2400);
        Assert.False(TilePlanner.TouchesInteriorSeam(new RectD(10, 2, 50, 50), first, 5000, 6));
        Assert.True(TilePlanner.TouchesInteriorSeam(new RectD(10, 2380, 50, 18), first, 5000, 6));
        Assert.True(TilePlanner.TouchesInteriorSeam(new RectD(10, 3, 50, 50), middle, 5000, 6));
        var last = new Band(2800, 2200);
        Assert.False(TilePlanner.TouchesInteriorSeam(new RectD(10, 2150, 50, 50), last, 5000, 6));
    }

    [Fact]
    public void OcrDedupKeepsLongerText()
    {
        var a = new TextRegion("短い", new RectD(0, 0, 100, 100), false, 20);
        var b = new TextRegion("もっと長い", new RectD(10, 10, 80, 80), false, 20);
        var c = new TextRegion("別", new RectD(500, 500, 10, 10), false, 20);
        var result = TilePlanner.DedupeOcr([a, b, c]);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Text == "もっと長い");
    }

    [Fact]
    public void DetectionDedupKeepsHigherScoreSameKindOnly()
    {
        var a = new DetectedRegion(new RectD(0, 0, 100, 100), DetectionKind.Balloon, 0.6);
        var b = new DetectedRegion(new RectD(5, 5, 90, 90), DetectionKind.Balloon, 0.9);
        var c = new DetectedRegion(new RectD(5, 5, 90, 90), DetectionKind.TextInBalloon, 0.5);
        var result = TilePlanner.DedupeDetections([a, b, c]);
        Assert.Equal(2, result.Count);
        Assert.Contains(b, result);
        Assert.Contains(c, result);
    }

    [Fact]
    public void DetectorDecodeMultiClassAndThreshold()
    {
        float[] scores = [0.5f, 0.45f, 0.1f, 0.39f, 0.2f, 0.9f];
        float[] boxes = [0.5f, 0.5f, 0.2f, 0.2f, 0.1f, 0.1f, 0.1f, 0.1f];
        var result = DetectorDecoder.Decode(scores, boxes, 2, 100, 100);
        Assert.Equal(3, result.Count);
        Assert.Equal(DetectionKind.FreeText, result[0].Kind);
        Assert.True(result[0].Score >= result[1].Score && result[1].Score >= result[2].Score);
    }
}

public class BalloonGrouperTests
{
    private static TextRegion V(string t, double x, double y, double w, double h) => new(t, new RectD(x, y, w, h), true, w);
    private static TextRegion H(string t, double x, double y, double w, double h) => new(t, new RectD(x, y, w, h), false, h);

    [Fact]
    public void VerticalJapaneseColumnsReadRightToLeft()
    {
        var right = V("こんにちは", 200, 100, 30, 200);
        var left = V("げんき", 160, 100, 30, 150);
        var groups = BalloonGrouper.Group([left, right], null, "ja");
        var g = Assert.Single(groups);
        Assert.Equal("こんにちはげんき", g.Text);
        Assert.True(g.IsVertical);
    }

    [Fact]
    public void SplitJapaneseColumnIsJoined()
    {
        var top = V("ありが", 200, 100, 30, 90);
        var bottom = V("とう", 200, 195, 30, 60);
        var g = Assert.Single(BalloonGrouper.Group([bottom, top], null, "ja"));
        Assert.Equal("ありがとう", g.Text);
    }

    [Fact]
    public void HorizontalKoreanLinesJoinWithSpace()
    {
        var l1 = H("안녕하세요", 100, 100, 200, 20);
        var l2 = H("반가워요", 110, 125, 150, 20);
        var far = H("다른 풍선", 600, 700, 150, 20);
        var groups = BalloonGrouper.Group([far, l2, l1], null, "ko");
        Assert.Equal(2, groups.Count);
        Assert.Equal("안녕하세요 반가워요", groups[0].Text);
        Assert.Equal(0, groups[0].Id);
        Assert.Equal("다른 풍선", groups[1].Text);
    }

    [Fact]
    public void DifferentContainersAreNotMixed()
    {
        var a = H("Hello there", 100, 100, 100, 20);
        var b = H("General Kenobi", 100, 122, 100, 20); // 연결 요소로는 붙을 거리
        var detections = new List<DetectedRegion>
        {
            new(new RectD(90, 95, 120, 26), DetectionKind.TextInBalloon, 0.9),
            new(new RectD(90, 121, 120, 24), DetectionKind.TextInBalloon, 0.9),
        };
        var groups = BalloonGrouper.Group([a, b], detections, "en");
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void TextInBalloonUsesSmallestCoveringBalloonAsContainer()
    {
        var a = H("Line one", 100, 100, 100, 20);
        var b = H("Line two", 100, 160, 100, 20);
        var balloon = new DetectedRegion(new RectD(80, 80, 140, 120), DetectionKind.Balloon, 0.9);
        var bigBalloon = new DetectedRegion(new RectD(0, 0, 1000, 1000), DetectionKind.Balloon, 0.9);
        var detections = new List<DetectedRegion>
        {
            new(new RectD(95, 95, 110, 30), DetectionKind.TextInBalloon, 0.9),
            new(new RectD(95, 155, 110, 30), DetectionKind.TextInBalloon, 0.9),
            balloon, bigBalloon,
        };
        var g = Assert.Single(BalloonGrouper.Group([a, b], detections, "en"));
        Assert.Equal("Line one Line two", g.Text);
        Assert.Equal(balloon.Rect, g.BalloonRect);
    }

    [Fact]
    public void NoiseWithoutOwnScriptIsDropped()
    {
        var groups = BalloonGrouper.Group([H("!!", 0, 0, 20, 20), H("...", 50, 50, 20, 20)], null, "ja");
        Assert.Empty(groups);
    }

    [Fact]
    public void IdsOrderTopThenRightToLeftWhenSameTop()
    {
        var left = H("left one", 100, 100, 50, 20);
        var right = H("right one", 400, 100.5, 50, 20);
        var lower = H("lower one", 100, 400, 50, 20);
        var groups = BalloonGrouper.Group([left, lower, right], null, "en");
        Assert.Equal(["right one", "left one", "lower one"], groups.Select(g => g.Text));
    }
}

public class PatchGeometryTests
{
    private static PatchSource S(int id, double x, double y, double w, double h, bool uniform = true, string text = "t")
        => new(id, new RectD(x, y, w, h), 20, false, new BubbleBackground(1, 1, 1, uniform), null, text);

    [Fact]
    public void OverlappingOriginalsMergeIntoOnePatchInIdOrder()
    {
        var patches = PatchGeometry.Layout([S(2, 120, 100, 50, 20, text: "B"), S(1, 100, 100, 50, 20, text: "A")], 1000);
        var p = Assert.Single(patches);
        Assert.Equal("A\nB", p.Text);
        Assert.Equal([1, 2], p.BubbleIds);
        Assert.Equal(2, p.HitAreas.Count);
    }

    [Fact]
    public void PaddingOnlyOverlapShrinksToMidlineWithoutCuttingOriginal()
    {
        // 원문 간격 4px, padding 6px → 겹침
        var patches = PatchGeometry.Layout([S(1, 100, 100, 50, 20), S(2, 154, 100, 50, 20)], 1000);
        Assert.Equal(2, patches.Count);
        var a = patches.Single(p => p.BubbleIds[0] == 1);
        var b = patches.Single(p => p.BubbleIds[0] == 2);
        Assert.Equal(152, a.Rect.Right, 3);
        Assert.Equal(152, b.Rect.Left, 3);
        Assert.True(a.Rect.Right >= 150 && b.Rect.Left <= 154);
    }

    [Fact]
    public void VerticalArtworkExpandsToMinimumWidthAndClipsToPage()
    {
        var r = PatchGeometry.PatchRect(new RectD(5, 100, 20, 200), 20, true, false, null, 40);
        Assert.Equal(0, r.Left);
        Assert.Equal(40, r.Right);
        Assert.True(r.Height > 200);
    }

    [Fact]
    public void ArtworkStyleValues()
    {
        var p = Assert.Single(PatchGeometry.Layout([S(1, 0, 0, 100, 20, uniform: false)], 1000));
        Assert.Equal(PatchGeometry.FeatherRadius(20), p.Feather);
        Assert.Equal(0.92, p.Opacity);
        Assert.Equal(10, p.CornerRadius);
        Assert.Equal(19, p.MaxFontSize);
        Assert.Equal(6, p.MinFontSize);
        Assert.Equal(4, PatchGeometry.CornerRadius(2));
        Assert.Equal(8, PatchGeometry.MaxFontSize(4));
    }

    [Fact]
    public void FontSizeBinarySearchPrecision()
    {
        var size = PatchGeometry.FitFontSize(6, 30, s => s <= 17.3);
        Assert.InRange(size, 16.8, 17.3);
        Assert.Equal(30, PatchGeometry.FitFontSize(6, 30, _ => true));
        Assert.Equal(6, PatchGeometry.FitFontSize(6, 30, _ => false));
    }

    [Fact]
    public void ShiftToNextSliceUsesCenteringOffset()
    {
        var p = Assert.Single(PatchGeometry.Layout([S(1, 10, 990, 50, 30)], 1000));
        var shifted = PatchGeometry.ShiftToNextSlice(p, 600, 1000, 720);
        Assert.Equal(p.Rect.X + 60, shifted.Rect.X, 3);
        Assert.Equal(p.Rect.Y - 1000, shifted.Rect.Y, 3);
    }

    [Fact]
    public void LuminanceDecidesTextColor()
    {
        Assert.False(new BubbleBackground(1, 1, 1, true).UsesWhiteText);
        Assert.True(new BubbleBackground(0.1, 0.1, 0.1, true).UsesWhiteText);
    }
}

public class EditResolverTests
{
    private static readonly RectD R = new(100, 120, 140, 260);

    private static TranslatedBubble Machine(int id, string source, string? tr, RectD? rect = null) => new()
    {
        Id = id, Rect = rect ?? R, GlyphSize = 24, IsVertical = true, Source = source, Translation = tr, Background = BubbleBackground.White,
    };

    [Fact]
    public void PriorityOrder()
    {
        var edits = PageEditsEditor.Empty("p", TranslationScope.Page);
        (edits, var i) = PageEditsEditor.Ensure(edits, "こんにちは", R);
        var machine = new[] { Machine(0, "こんにちは", "안녕하세요") };

        // 5 기계
        Assert.Equal(BubbleTextState.Machine, EditResolver.Resolve(machine, null, "ko")[0].State);

        // 4 원문 수정, 재번역 전
        edits = PageEditsEditor.SetCorrectedSource(edits, i, "こんばんは");
        var d = EditResolver.Resolve(machine, edits, "ko")[0];
        Assert.Equal(BubbleTextState.CorrectedNeedsRetranslation, d.State);
        Assert.Equal("안녕하세요", d.Text);
        Assert.Equal("こんばんは", d.EffectiveSource);
        Assert.Equal(BubbleTextState.CorrectedPending, EditResolver.Resolve(machine, edits, "ko", _ => true)[0].State);

        // 3 재번역
        edits = PageEditsEditor.SetRetranslation(edits, i, "ko", "안녕하세요(저녁)");
        Assert.Equal("안녕하세요(저녁)", EditResolver.Resolve(machine, edits, "ko")[0].Text);

        // 2 사용자 번역(해당 언어만)
        edits = PageEditsEditor.SetTranslation(edits, i, "ko", "안녕");
        Assert.Equal("안녕", EditResolver.Resolve(machine, edits, "ko")[0].Text);
        Assert.Equal(BubbleTextState.CorrectedNeedsRetranslation, EditResolver.Resolve(machine, edits, "en")[0].State);

        // 1 숨김(모든 언어)
        edits = PageEditsEditor.SetHidden(edits, i, true);
        Assert.True(EditResolver.Resolve(machine, edits, "ko")[0].IsHidden);
        Assert.True(EditResolver.Resolve(machine, edits, "en")[0].IsHidden);
        Assert.Empty(EditResolver.ToPatchSources(EditResolver.Resolve(machine, edits, "ko"), true));
    }

    [Fact]
    public void MatchingUsesSourceAndRectNotId()
    {
        var edits = PageEditsEditor.Empty("p", TranslationScope.Page);
        (edits, var i) = PageEditsEditor.Ensure(edits, "こんにちは", R);
        edits = PageEditsEditor.SetTranslation(edits, i, "ko", "안녕");
        // 새 분석: ID가 바뀌고 rect가 약간 이동
        var moved = Machine(7, "こんにちは", "안녕하세요", R.Offset(10, 5));
        Assert.Equal("안녕", EditResolver.Resolve([moved], edits, "ko")[0].Text);
        // 다른 원문은 매칭되지 않음
        Assert.Equal("x", EditResolver.Resolve([Machine(7, "さようなら", "x")], edits, "ko")[0].Text);
        // 겹치지 않는 rect도 매칭되지 않음
        Assert.Equal("y", EditResolver.Resolve([Machine(7, "こんにちは", "y", new RectD(900, 900, 10, 10))], edits, "ko")[0].Text);
    }

    [Fact]
    public void RevertKinds()
    {
        var edits = PageEditsEditor.Empty("p", TranslationScope.Page);
        (edits, var i) = PageEditsEditor.Ensure(edits, "src", R);
        edits = PageEditsEditor.SetCorrectedSource(edits, i, "fixed");
        edits = PageEditsEditor.SetTranslation(edits, i, "ko", "mine");
        var t = PageEditsEditor.Revert(edits, i, RevertKind.Translation, "ko");
        Assert.Equal("fixed", t.Bubbles[0].CorrectedSource);
        Assert.Empty(t.Bubbles[0].Translations);
        var o = PageEditsEditor.Revert(edits, i, RevertKind.Original, "ko");
        Assert.Null(o.Bubbles[0].CorrectedSource);
        Assert.Equal("mine", o.Bubbles[0].Translations["ko"]);
        Assert.Empty(PageEditsEditor.Revert(edits, i, RevertKind.Both, "ko").Bubbles);
    }

    [Fact]
    public void CorrectedSourceEqualToRecognizedClears()
    {
        var edits = PageEditsEditor.Empty("p", TranslationScope.Page);
        (edits, var i) = PageEditsEditor.Ensure(edits, "src", R);
        edits = PageEditsEditor.SetCorrectedSource(edits, i, "src");
        Assert.Empty(edits.Bubbles);
    }

    [Fact]
    public void AddedBalloonStates()
    {
        var edits = PageEditsEditor.Empty("p", TranslationScope.Page);
        (edits, var i) = PageEditsEditor.AddBalloon(edits, new RectD(0, 0, 50, 50), "", new AddedBalloon(20, false, BubbleBackground.White));
        var d = Assert.Single(EditResolver.Resolve([], edits, "ko"));
        Assert.True(d.IsAdded);
        Assert.Equal(EditResolver.AddedIdBase + i, d.Id);
        Assert.Equal(BubbleTextState.AddedNoText, d.State);

        edits = PageEditsEditor.SetCorrectedSource(edits, i, "テキスト");
        Assert.Equal(BubbleTextState.AddedNotTranslated, EditResolver.Resolve([], edits, "ko")[0].State);
        edits = PageEditsEditor.SetRetranslation(edits, i, "ko", "텍스트");
        Assert.Equal("텍스트", EditResolver.Resolve([], edits, "ko")[0].Text);
        // 추가 풍선은 원문을 되돌려도 항목이 남는다
        Assert.Single(PageEditsEditor.Revert(edits, i, RevertKind.Original, "ko").Bubbles);
        Assert.Empty(PageEditsEditor.RemoveAt(edits, i).Bubbles);
    }

    [Fact]
    public void UnhideAllRestores()
    {
        var edits = PageEditsEditor.Empty("p", TranslationScope.Page);
        (edits, var i) = PageEditsEditor.Ensure(edits, "a", R);
        edits = PageEditsEditor.SetHidden(edits, i, true);
        (edits, var j) = PageEditsEditor.Ensure(edits, "b", new RectD(500, 500, 10, 10));
        edits = PageEditsEditor.SetHidden(edits, j, true);
        Assert.Empty(PageEditsEditor.UnhideAll(edits).Bubbles);
    }

    [Fact]
    public void PendingBubblesShowEllipsisInPatchSources()
    {
        var display = EditResolver.Resolve([Machine(0, "src", null)], null, "ko");
        Assert.Equal(BubbleTextState.Pending, display[0].State);
        Assert.Equal("…", Assert.Single(EditResolver.ToPatchSources(display, true)).Text);
        Assert.Empty(EditResolver.ToPatchSources(display, false));
    }
}

public class AiChatExchangeTests
{
    private static DisplayBubble B(int id, string src, string? tr, bool hidden = false) => new()
    {
        Id = id, Rect = new RectD(0, 0, 1, 1), GlyphSize = 10, IsVertical = false, Background = BubbleBackground.White,
        RecognizedSource = src, EffectiveSource = src, Text = tr, State = BubbleTextState.Machine, IsHidden = hidden,
    };

    [Fact]
    public void FileMatchesExample()
    {
        var snapshot = AiChatExchange.BuildSnapshot("book", "Sample", "ja", "ko",
        [
            (1, (IReadOnlyList<DisplayBubble>)[B(0, "ありがとう\nまたね", "고마워\n또 봐")]),
            (0, [B(3, "こんにちは", "안녕하세요"), B(1, "hidden", "x", hidden: true), B(2, "empty", "")]),
        ]);
        Assert.Equal(["p1-1", "p2-1"], snapshot.Entries.Select(e => e.Id));
        var file = AiChatExchange.BuildFile(snapshot, "Japanese", "Korean");
        Assert.Equal(TrVectors.ExampleText("ai-chat-request.txt").Replace("\r\n", "\n").TrimEnd(), file.Replace("\r\n", "\n").TrimEnd());
    }

    [Fact]
    public void ParsesExampleReplyAndBom()
    {
        var parsed = AiChatExchange.Parse("﻿" + TrVectors.ExampleText("ai-chat-reply.txt"));
        Assert.Equal("안녕", parsed["p1-1"]);
        Assert.Equal("고마워\n다음에 봐", parsed["p2-1"]);
    }

    [Theory]
    [InlineData("* [p3-2]. 「こんにちは」", "p3-2", "こんにちは")]
    [InlineData("• [p1-1]： \"Hi\"", "p1-1", "Hi")]
    [InlineData("`[p1-1] - a → b → c`", "p1-1", "c`")]
    public void ParserPrefixesAndSeparators(string line, string id, string expected)
        => Assert.Equal(expected, AiChatExchange.Parse(line)[id]);

    [Fact]
    public void EmptyContentIgnored() => Assert.Empty(AiChatExchange.Parse("[p1-1]   \n[p1-2] “”"));

    [Fact]
    public void PageDescription()
    {
        Assert.Equal("page 3", AiChatExchange.DescribePages([3]));
        Assert.Equal("pages 1–3", AiChatExchange.DescribePages([1, 2, 3]));
        Assert.Equal("pages 1, 4", AiChatExchange.DescribePages([1, 4]));
    }

    [Fact]
    public void TranslationOrderStartsAtCurrentPage()
    {
        Assert.Equal([3, 4, 0, 1, 2], TranslationOrder.Pages(5, 3));
        Assert.Empty(TranslationOrder.Pages(0, 3));
        Assert.Equal([0, 1], TranslationOrder.Pages(2, -5));
    }
}
