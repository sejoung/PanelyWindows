using Panely.Core.Reader;
using Panely.Domain.Model;
using Panely.Domain.Reading;

namespace Panely.Tests.Reader;

public class ZoomControllerTests
{
    [Fact]
    public void FirstBaseline_AlwaysResets()
    {
        var z = new ZoomController();
        z.ResetForNewDocument(FitMode.FitScreen);
        Assert.True(z.ApplyBaseline(0.5, force: false, sizeLocked: true));
        Assert.Equal(0.5, z.Zoom);
        Assert.True(z.IsAtFit(FitMode.FitScreen));
    }

    [Fact]
    public void Resize_WithoutManualZoom_Refits_UnlessLocked()
    {
        var z = new ZoomController();
        z.ApplyBaseline(0.5, false, false);
        Assert.True(z.ApplyBaseline(0.7, false, sizeLocked: false));
        Assert.Equal(0.7, z.Zoom);
        Assert.False(z.ApplyBaseline(0.9, false, sizeLocked: true));
        Assert.Equal(0.7, z.Zoom);
        Assert.Equal(0.9, z.Baseline);
    }

    [Fact]
    public void ManualZoom_IsPreserved_EvenWithAutoFit()
    {
        var z = new ZoomController();
        z.ApplyBaseline(0.5, false, false);
        z.ZoomIn();
        Assert.Equal(0.625, z.Zoom, 6);
        Assert.False(z.IsAtFit(FitMode.FitScreen));
        Assert.False(z.ApplyBaseline(0.8, false, sizeLocked: false));
        Assert.Equal(0.625, z.Zoom, 6);
        Assert.True(z.ApplyBaseline(0.8, force: true, sizeLocked: true));
        Assert.Equal(0.8, z.Zoom);
    }

    [Fact]
    public void SessionFactor_AppliesOnFirstBaselineOfNewBook()
    {
        var z = new ZoomController();
        z.ResetForNewDocument(FitMode.FitWidth);
        z.ApplyBaseline(0.4, force: false, sizeLocked: false, sessionFactor: 1.5);
        Assert.Equal(0.6, z.Zoom, 6);
        Assert.Equal(1.5, z.ManualFactor, 6);
    }

    [Fact]
    public void SelectFit_SameModeAgain_ReturnsToBaseline()
    {
        var z = new ZoomController();
        z.SelectFit(FitMode.FitWidth, 0.5);
        z.ZoomIn();
        z.SelectFit(FitMode.FitWidth, 0.5);
        Assert.True(z.IsAtFit(FitMode.FitWidth));
    }

    [Fact]
    public void DoubleClick_TogglesBaselineAndDouble()
    {
        var z = new ZoomController();
        z.ApplyBaseline(0.5, false, false);
        z.DoubleClick();
        Assert.Equal(1, z.Zoom);
        z.DoubleClick();
        Assert.Equal(0.5, z.Zoom);
    }

    [Fact]
    public void SessionZoomMemory_RemembersPerSeries()
    {
        var m = new SessionZoomMemory();
        Assert.Null(m.Recall("series:a"));
        m.Remember("series:a", 1.25);
        m.Remember("series:b", double.NaN);
        Assert.Equal(1.25, m.Recall("series:a"));
        Assert.Null(m.Recall("series:b"));
    }
}

public class WheelInputAdapterTests
{
    [Fact]
    public void Multiples_Of_120_AreDiscrete()
    {
        var a = new WheelInputAdapter();
        Assert.Equal(WheelDevice.Discrete, a.Classify(-120, TimeSpan.Zero));
        Assert.Equal(WheelDevice.Discrete, a.Classify(240, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void NonMultiple_MarksPreciseDevice_ForAWhile()
    {
        var a = new WheelInputAdapter();
        Assert.Equal(WheelDevice.PrecisePhaseless, a.Classify(-17, TimeSpan.Zero));
        Assert.Equal(WheelDevice.PrecisePhaseless, a.Classify(-120, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(WheelDevice.Discrete, a.Classify(-120, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Normalization_DownIsForward_AndZoomDeltaIs10PerNotch()
    {
        Assert.True(WheelInputAdapter.ForwardDip(-120) > 0);
        Assert.Equal(WheelInputAdapter.DipPerNotch, WheelInputAdapter.ForwardDip(-120));
        Assert.Equal(10, WheelInputAdapter.ZoomDelta(120));
        Assert.Equal(1.1, ZoomPolicy.ApplyContinuousDelta(1, WheelInputAdapter.ZoomDelta(120)), 6);
    }

    [Fact]
    public void EngineInput_DiscreteNotchAtEdge_TurnsThenCoolsDown()
    {
        var a = new WheelInputAdapter();
        var engine = new WheelPageTurnEngine();
        Assert.Equal(WheelOutcome.TurnForward, engine.Process(a.ToEngineInput(-120, TimeSpan.Zero, true, false)));
        Assert.Equal(WheelOutcome.Swallow, engine.Process(a.ToEngineInput(-120, TimeSpan.FromMilliseconds(100), true, false)));
        Assert.Equal(WheelOutcome.TurnForward, engine.Process(a.ToEngineInput(-120, TimeSpan.FromMilliseconds(310), true, false)));
    }

    [Fact]
    public void EngineInput_PreciseAccumulatesToThreshold()
    {
        var a = new WheelInputAdapter();
        var engine = new WheelPageTurnEngine();
        // 정밀 delta 50 raw ≈ 20 DIP
        var outcomes = Enumerable.Range(0, 4).Select(i => engine.Process(a.ToEngineInput(-50, TimeSpan.FromMilliseconds(i * 16), true, false))).ToList();
        Assert.Equal(WheelOutcome.Swallow, outcomes[0]);
        Assert.Contains(WheelOutcome.TurnForward, outcomes);
    }
}

public class NotificationCenterTests
{
    [Fact]
    public void Current_FollowsPriority()
    {
        var n = new NotificationCenter();
        n.Post(new Notice(NoticeCategory.Translation, "t", []));
        n.Post(new Notice(NoticeCategory.Volume, "v", []));
        Assert.Equal("v", n.Current!.Message);
        n.Post(new Notice(NoticeCategory.SourceChanged, "s", []));
        n.Post(new Notice(NoticeCategory.ReaderError, "e", []));
        Assert.Equal("s", n.Current!.Message);
        n.Clear(NoticeCategory.SourceChanged);
        Assert.Equal("e", n.Current!.Message);
    }

    [Fact]
    public void Dismiss_ById_DoesNotRemoveNewerNotice()
    {
        var n = new NotificationCenter();
        var old = n.Post(new Notice(NoticeCategory.Volume, "first", [], TimeSpan.FromSeconds(3)));
        n.Post(new Notice(NoticeCategory.Volume, "second", []));
        n.Dismiss(old.Id);
        Assert.Equal("second", n.Current!.Message);
    }
}

public class ReaderTextTests
{
    [Theory]
    [InlineData("5", 24, 5)]
    [InlineData(" 999 ", 24, 24)]
    [InlineData("0", 24, 1)]
    [InlineData("-3", 24, 1)]
    [InlineData("１２", 24, 12)]
    public void ParsePageNumber_ClampsValidIntegers(string text, int total, int expected)
        => Assert.Equal(expected, ReaderText.ParsePageNumber(text, total));

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("3.5")]
    public void ParsePageNumber_RejectsInvalid(string text) => Assert.Null(ReaderText.ParsePageNumber(text, 24));

    [Fact]
    public void JumpInputWidth_FollowsFormula()
    {
        Assert.Equal(32, ReaderText.JumpInputWidth(5));
        Assert.Equal(37, ReaderText.JumpInputWidth(999));
        Assert.Equal(46, ReaderText.JumpInputWidth(1000));
    }

    [Fact]
    public void PageRangeText_ShowsSpread()
    {
        Assert.Equal("5-6", ReaderText.PageRangeText(new PageRange(4, 6)));
        Assert.Equal("1", ReaderText.PageRangeText(new PageRange(0, 1)));
    }
}
