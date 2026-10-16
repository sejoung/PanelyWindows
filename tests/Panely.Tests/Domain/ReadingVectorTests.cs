using Panely.Domain.Model;
using Panely.Domain.Reading;

namespace Panely.Tests.Domain;

public class ReadingVectorTests
{
    public static IEnumerable<object[]> SpreadCases => Vectors.Cases("spreads");
    public static IEnumerable<object[]> FitCases => Vectors.Cases("fits");
    public static IEnumerable<object[]> JumpCases => Vectors.Cases("jumps");
    public static IEnumerable<object[]> VerticalPageCases => Vectors.Cases("verticalCurrentPage");
    public static IEnumerable<object[]> ZoomCases => Vectors.Cases("zoom");
    public static IEnumerable<object[]> FitResetCases => Vectors.Cases("fitReset");

    [Theory]
    [MemberData(nameof(SpreadCases))]
    public void Spread_matches_vector(int i)
    {
        var c = Vectors.Case("spreads", i);
        var input = c.GetProperty("input");
        var index = input.GetProperty("index").GetInt32();
        var n = input.GetProperty("pageCount").GetInt32();
        var step = input.GetProperty("step").GetInt32();
        var cover = input.GetProperty("coverAlone").GetBoolean();
        var range = c.GetProperty("range").Doubles();

        var spread = SpreadCalculator.Spread(index, n, step, cover);
        Assert.Equal((int)range[0], spread.Start);
        Assert.Equal((int)range[1], spread.End);
        Assert.Equal(c.GetProperty("nextStart").NullableInt(), SpreadCalculator.NextStart(index, n, step, cover));
        Assert.Equal(c.GetProperty("previousStart").NullableInt(), SpreadCalculator.PreviousStart(index, n, step, cover));
    }

    [Theory]
    [MemberData(nameof(FitCases))]
    public void Fit_matches_vector(int i)
    {
        var c = Vectors.Case("fits", i);
        var doc = c.GetProperty("doc").Doubles();
        var vp = c.GetProperty("viewport").Doubles();
        var d = new SizeD(doc[0], doc[1]);
        var v = new SizeD(vp[0], vp[1]);
        Assert.Equal(c.GetProperty("fitScreen").GetDouble(), FitCalculator.Fit(d, v, FitMode.FitScreen), 9);
        Assert.Equal(c.GetProperty("fitWidth").GetDouble(), FitCalculator.Fit(d, v, FitMode.FitWidth), 9);
        Assert.Equal(c.GetProperty("fitHeight").GetDouble(), FitCalculator.Fit(d, v, FitMode.FitHeight), 9);
    }

    [Theory]
    [MemberData(nameof(JumpCases))]
    public void JumpPage_matches_vector(int i)
    {
        var c = Vectors.Case("jumps", i);
        var layout = c.GetProperty("layout").GetString() == "double" ? ReadingLayout.Double : ReadingLayout.Single;
        var result = SpreadCalculator.JumpPage(
            c.GetProperty("inputPageNumber").GetInt32(),
            c.GetProperty("pageCount").GetInt32(),
            layout.Step(),
            c.GetProperty("coverAlone").GetBoolean());
        Assert.Equal(c.GetProperty("expectedPageIndex").GetInt32(), result);
    }

    [Fact]
    public void Horizontal_frames_match_vectors()
    {
        foreach (var c in Vectors.Root.GetProperty("horizontalFrames").EnumerateArray())
        {
            var sizes = c.GetProperty("sizes").EnumerateArray()
                .Select((s, idx) => (idx, new SizeD(s[0].GetDouble(), s[1].GetDouble()))).ToList();
            var direction = c.GetProperty("direction").GetString() == "rightToLeft" ? ReadingDirection.RightToLeft : ReadingDirection.LeftToRight;
            var layout = PageLayoutCalculator.Horizontal(sizes, direction);
            var doc = c.GetProperty("document").Doubles();
            Assert.Equal(doc[0], layout.Size.Width, 6);
            Assert.Equal(doc[1], layout.Size.Height, 6);

            var framesKey = c.TryGetProperty("framesInDisplayOrder", out var fd) ? fd : c.GetProperty("frames");
            var expected = framesKey.EnumerateArray().Select(f => f.Doubles()).ToList();
            Assert.Equal(expected.Count, layout.FramesInDisplayOrder.Count);
            for (var k = 0; k < expected.Count; k++)
            {
                var r = layout.FramesInDisplayOrder[k].Rect;
                Assert.Equal(expected[k][0], r.X, 6);
                Assert.Equal(expected[k][1], r.Y, 6);
                Assert.Equal(expected[k][2], r.Width, 6);
                Assert.Equal(expected[k][3], r.Height, 6);
            }

            if (c.TryGetProperty("displayLogicalIndices", out var order))
            {
                Assert.Equal(order.EnumerateArray().Select(x => x.GetInt32()), layout.FramesInDisplayOrder.Select(f => f.LogicalIndex));
            }
        }
    }

    [Fact]
    public void Vertical_frames_match_vectors()
    {
        foreach (var c in Vectors.Root.GetProperty("verticalFrames").EnumerateArray())
        {
            var sizes = c.GetProperty("sizes").EnumerateArray().Select(s => new SizeD(s[0].GetDouble(), s[1].GetDouble())).ToList();
            var layout = PageLayoutCalculator.Vertical(sizes);
            var doc = c.GetProperty("document").Doubles();
            Assert.Equal(doc[0], layout.Size.Width);
            Assert.Equal(doc[1], layout.Size.Height);
            var frames = c.GetProperty("frames").EnumerateArray().Select(f => f.Doubles()).ToList();
            for (var k = 0; k < frames.Count; k++)
            {
                var r = layout.FramesInDisplayOrder[k].Rect;
                Assert.Equal(new RectD(frames[k][0], frames[k][1], frames[k][2], frames[k][3]), r);
            }
            var fitRef = c.GetProperty("fitReference").Doubles();
            Assert.Equal(new SizeD(fitRef[0], fitRef[1]), layout.FitReference);
        }
    }

    [Theory]
    [MemberData(nameof(VerticalPageCases))]
    public void Vertical_current_page_matches_vector(int i)
    {
        var c = Vectors.Case("verticalCurrentPage", i);
        var heights = c.GetProperty("pageHeights").Doubles();
        var vp = c.GetProperty("viewportY").Doubles();
        Assert.Equal(c.GetProperty("expectedIndex").GetInt32(), VerticalStrip.CurrentPage(heights, vp[0], vp[1]));
    }

    [Theory]
    [MemberData(nameof(ZoomCases))]
    public void Zoom_matches_vector(int i)
    {
        var c = Vectors.Case("zoom", i);
        var baseline = c.GetProperty("baseline").GetDouble();
        var current = c.GetProperty("current").GetDouble();
        if (c.TryGetProperty("zoomIn", out var zin)) Assert.Equal(zin.GetDouble(), ZoomPolicy.ZoomIn(current), 9);
        if (c.TryGetProperty("zoomOut", out var zout)) Assert.Equal(zout.GetDouble(), ZoomPolicy.ZoomOut(current), 9);
        Assert.Equal(c.GetProperty("doubleClick").GetDouble(), ZoomPolicy.DoubleClick(baseline, current), 9);
    }

    [Theory]
    [MemberData(nameof(FitResetCases))]
    public void Fit_reset_matches_vector(int i)
    {
        var c = Vectors.Case("fitReset", i);
        var autoFit = c.GetProperty("autoFit").GetBoolean();
        var result = ZoomPolicy.ShouldReset(
            c.GetProperty("force").GetBoolean(),
            c.GetProperty("initialFitApplied").GetBoolean(),
            sizeLocked: !autoFit,
            userHasZoomedAway: c.GetProperty("manuallyZoomed").GetBoolean());
        Assert.Equal(c.GetProperty("expected").GetBoolean(), result);
    }

    [Fact]
    public void Wheel_vectors()
    {
        foreach (var c in Vectors.Root.GetProperty("wheel").EnumerateArray())
        {
            var engine = new WheelPageTurnEngine();
            var device = c.GetProperty("device").GetString();
            if (device == "discrete")
            {
                var times = c.GetProperty("timesSeconds").Doubles();
                var expected = c.GetProperty("expected").EnumerateArray().Select(e => e.GetString()).ToList();
                for (var k = 0; k < times.Length; k++)
                {
                    var outcome = engine.Process(new WheelInput(WheelDevice.Discrete, WheelPhase.None, 120, true, false, TimeSpan.FromSeconds(times[k])));
                    Assert.Equal(expected[k], Name(outcome));
                }
            }
            else if (device == "precisePhased")
            {
                var atEdge = c.GetProperty("startsBlocked").GetBoolean();
                var reachesAfter = c.TryGetProperty("reachesBottomAfterEvent", out var r) ? r.GetInt32() : int.MaxValue;
                var deltas = c.GetProperty("deltasDIP").Doubles();
                var expected = c.GetProperty("expectedChanged").EnumerateArray().Select(e => e.GetString()).ToList();
                var t = TimeSpan.Zero;
                engine.Process(new WheelInput(WheelDevice.PrecisePhased, WheelPhase.Began, 0, atEdge, false, t));
                for (var k = 0; k < deltas.Length; k++)
                {
                    t += TimeSpan.FromMilliseconds(16);
                    var edge = atEdge || k > reachesAfter;
                    var outcome = engine.Process(new WheelInput(WheelDevice.PrecisePhased, WheelPhase.Changed, deltas[k], edge, false, t));
                    Assert.Equal(expected[k], Name(outcome));
                }
                if (c.TryGetProperty("momentumDeltasDIP", out var momentum))
                {
                    var expectedMomentum = c.GetProperty("expectedMomentum").EnumerateArray().Select(e => e.GetString()).ToList();
                    var md = momentum.Doubles();
                    for (var k = 0; k < md.Length; k++)
                    {
                        t += TimeSpan.FromMilliseconds(16);
                        var outcome = engine.Process(new WheelInput(WheelDevice.PrecisePhased, WheelPhase.Momentum, md[k], false, false, t));
                        Assert.Equal(expectedMomentum[k], Name(outcome));
                    }
                }
            }
        }
    }

    [Fact]
    public void Wheel_phaseless_latches_once_and_resets_after_idle()
    {
        var engine = new WheelPageTurnEngine();
        WheelOutcome P(double d, double ms, bool edge = true)
            => engine.Process(new WheelInput(WheelDevice.PrecisePhaseless, WheelPhase.None, d, edge, false, TimeSpan.FromMilliseconds(ms)));

        Assert.Equal(WheelOutcome.Swallow, P(30, 0));
        Assert.Equal(WheelOutcome.TurnForward, P(30, 10));
        Assert.Equal(WheelOutcome.Swallow, P(80, 20));
        Assert.Equal(WheelOutcome.Swallow, P(80, 200));
        // 250ms 넘게 쉬면 reset
        Assert.Equal(WheelOutcome.Swallow, P(30, 500));
        Assert.Equal(WheelOutcome.TurnForward, P(40, 510));
        // 경계가 아니면 pan
        Assert.Equal(WheelOutcome.Pan, P(40, 900, edge: false));
    }

    [Fact]
    public void Wheel_reversed_gesture_loses_eligibility()
    {
        var engine = new WheelPageTurnEngine();
        engine.Process(new WheelInput(WheelDevice.PrecisePhased, WheelPhase.Began, 0, true, false, TimeSpan.Zero));
        Assert.Equal(WheelOutcome.Swallow, engine.Process(new WheelInput(WheelDevice.PrecisePhased, WheelPhase.Changed, 20, true, false, TimeSpan.FromMilliseconds(10))));
        Assert.Equal(WheelOutcome.Pan, engine.Process(new WheelInput(WheelDevice.PrecisePhased, WheelPhase.Changed, -40, true, false, TimeSpan.FromMilliseconds(20))));
        Assert.Equal(WheelOutcome.Pan, engine.Process(new WheelInput(WheelDevice.PrecisePhased, WheelPhase.Changed, 100, true, false, TimeSpan.FromMilliseconds(30))));
    }

    private static string Name(WheelOutcome o) => o switch
    {
        WheelOutcome.TurnForward => "turnForward",
        WheelOutcome.TurnBackward => "turnBackward",
        WheelOutcome.Swallow => "swallow",
        _ => "pan",
    };

    [Fact]
    public void Spread_exhaustive_invariants()
    {
        foreach (var n in new[] { 0, 1, 2, 5, 6, 7 })
            foreach (var step in new[] { 1, 2 })
                foreach (var cover in new[] { false, true })
                {
                    var starts = new List<int>();
                    var index = 0;
                    while (n > 0)
                    {
                        var s = SpreadCalculator.Spread(index, n, step, cover);
                        starts.Add(s.Start);
                        Assert.True(s.Length is 1 or 2);
                        for (var k = s.Start; k < s.End; k++)
                            Assert.Equal(s, SpreadCalculator.Spread(k, n, step, cover));
                        var next = SpreadCalculator.NextStart(index, n, step, cover);
                        if (next is null) { Assert.Equal(n, s.End); break; }
                        Assert.Equal(s.Start, SpreadCalculator.PreviousStart(next.Value, n, step, cover));
                        index = next.Value;
                    }
                }
    }

    [Fact]
    public void ViewTransform_round_trips_and_centers()
    {
        var doc = new SizeD(1000, 2000);
        var vp = new SizeD(800, 600);
        var t = ViewTransform.Create(doc, vp, 0.25, 0, 0);
        // 250x500 content centered
        Assert.Equal(275, t.OriginX, 6);
        Assert.Equal(50, t.OriginY, 6);
        var p = new PointD(123, 456);
        var back = t.ViewToDoc(t.DocToView(p));
        Assert.Equal(p.X, back.X, 9);
        Assert.Equal(p.Y, back.Y, 9);

        var zoomed = ViewTransform.Create(doc, vp, 2, 99999, -5);
        Assert.Equal(1000 - 800 / 2.0, zoomed.ScrollX, 6);
        Assert.Equal(0, zoomed.ScrollY, 6);

        // anchor 유지 확대
        var anchor = new PointD(400, 300);
        var before = zoomed.ViewToDoc(anchor);
        var (sx, sy) = ViewTransform.ScrollKeepingAnchor(zoomed, anchor, 3, doc, vp);
        var after = ViewTransform.Create(doc, vp, 3, sx, sy).ViewToDoc(anchor);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);
    }

    [Fact]
    public void Vertical_visible_range_includes_partially_visible_pages()
    {
        var tops = VerticalStrip.Tops([1000, 1000, 80]);
        var range = VerticalStrip.VisibleRange(tops, 900, 2050);
        Assert.Equal(new PageRange(0, 3), range);
        Assert.Equal(new PageRange(1, 2), VerticalStrip.VisibleRange(tops, 1000, 2000));
    }

    [Fact]
    public void Effective_direction_is_ltr_in_vertical()
    {
        Assert.Equal(ReadingDirection.LeftToRight, ReadingLayout.Vertical.EffectiveDirection(ReadingDirection.RightToLeft));
        Assert.Equal(ReadingDirection.RightToLeft, ReadingLayout.Double.EffectiveDirection(ReadingDirection.RightToLeft));
    }
}
