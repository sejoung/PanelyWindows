using System.Text.Json;
using Panely.Domain.Export;
using Panely.Domain.Library;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Tests.Translation;

internal static class TrVectors
{
    private static readonly Lazy<JsonDocument> Doc = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "test-vectors.json"))));

    public static JsonElement Get(string name) => Doc.Value.RootElement.GetProperty(name);

    public static double[] Doubles(this JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();

    public static string ExampleText(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "examples", name));
}

public class TranslationVectorTests
{
    [Fact]
    public void OcrTilesMatchVectors()
    {
        foreach (var v in TrVectors.Get("ocrTiles").EnumerateArray())
        {
            var tiles = TilePlanner.OcrTiles(v.GetProperty("width").GetInt32(), v.GetProperty("height").GetInt32());
            var expected = v.GetProperty("expected").EnumerateArray().Select(e => e.Doubles()).ToList();
            Assert.Equal(expected.Count, tiles.Count);
            for (var i = 0; i < tiles.Count; i++)
            {
                Assert.Equal(expected[i][0], tiles[i].Top);
                Assert.Equal(expected[i][1], tiles[i].Height);
            }
            if (v.TryGetProperty("tileHeight", out var th))
            {
                Assert.Equal(th.GetInt32(), TilePlanner.OcrTileHeight(v.GetProperty("width").GetInt32()));
                Assert.Equal(v.GetProperty("overlap").GetInt32(), TilePlanner.OcrOverlap(th.GetInt32()));
            }
        }
    }

    [Fact]
    public void StripPlanMatchesVectors()
    {
        foreach (var v in TrVectors.Get("stripPlan").EnumerateArray())
        {
            var sizes = v.GetProperty("pageSizes").EnumerateArray().Select(e => { var d = e.Doubles(); return new SizeD(d[0], d[1]); }).ToList();
            var plan = StripPlan.Create(sizes);
            var bands = v.GetProperty("bands").EnumerateArray().ToList();
            Assert.Equal(bands.Count, plan.Bands.Count);
            for (var i = 0; i < bands.Count; i++)
            {
                Assert.Equal(bands[i].GetProperty("top").GetInt32(), plan.Bands[i].Top);
                Assert.Equal(bands[i].GetProperty("height").GetInt32(), plan.Bands[i].Height);
                var owned = bands[i].GetProperty("ownedRange").Doubles();
                Assert.Equal(owned[0], plan.Bands[i].OwnedStart);
                Assert.Equal(owned[1], plan.Bands[i].OwnedEnd);
                Assert.Equal(bands[i].GetProperty("pageIndices").EnumerateArray().Select(x => x.GetInt32()), plan.Bands[i].PageIndices);
            }
            Assert.Equal(v.GetProperty("completedPagesAfterUnit0").EnumerateArray().Select(x => x.GetInt32()),
                plan.CompletedPages(new HashSet<int> { 0 }));
            Assert.Equal(v.GetProperty("completedPagesAfterBoth").EnumerateArray().Select(x => x.GetInt32()),
                plan.CompletedPages(new HashSet<int> { 0, 1 }));
        }
    }

    [Fact]
    public void DetectorDecodeMatchesVectors()
    {
        foreach (var v in TrVectors.Get("detectorDecode").EnumerateArray())
        {
            var size = v.GetProperty("imageSize").Doubles();
            var kind = v.GetProperty("kind").GetInt32();
            var box = v.GetProperty("normalizedBox").Doubles().Select(d => (float)d).ToArray();
            var scores = new float[3];
            scores[kind] = (float)v.GetProperty("score").GetDouble();
            var decoded = DetectorDecoder.Decode(scores, box, 1, size[0], size[1]);
            var r = Assert.Single(decoded);
            Assert.Equal((DetectionKind)kind, r.Kind);
            var expected = v.GetProperty("expectedRect").Doubles();
            Assert.Equal(expected[0], r.Rect.X, 3);
            Assert.Equal(expected[1], r.Rect.Y, 3);
            Assert.Equal(expected[2], r.Rect.Width, 3);
            Assert.Equal(expected[3], r.Rect.Height, 3);
        }
    }

    [Fact]
    public void PatchRectsMatchVectors()
    {
        foreach (var v in TrVectors.Get("patchRects").EnumerateArray())
        {
            var size = v.GetProperty("imageSize").Doubles();
            var r = v.GetProperty("rect").Doubles();
            var b = v.GetProperty("balloonRect").Doubles();
            var patch = PatchGeometry.PatchRect(new RectD(r[0], r[1], r[2], r[3]), v.GetProperty("glyph").GetDouble(),
                v.GetProperty("vertical").GetBoolean(), v.GetProperty("uniform").GetBoolean(), new RectD(b[0], b[1], b[2], b[3]), size[0]);
            var e = v.GetProperty("expectedRect").Doubles();
            Assert.Equal(e[0], patch.X, 3);
            Assert.Equal(e[1], patch.Y, 3);
            Assert.Equal(e[2], patch.Width, 3);
            Assert.Equal(e[3], patch.Height, 3);
        }
    }

    [Fact]
    public void AiReplyParserMatchesVectors()
    {
        foreach (var v in TrVectors.Get("aiReply").EnumerateArray())
        {
            var parsed = AiChatExchange.Parse(v.GetProperty("input").GetString()!);
            var expected = v.GetProperty("expectedParsed").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            Assert.Equal(expected.Count, parsed.Count);
            foreach (var (k, value) in expected) Assert.Equal(value, parsed[k]);
        }
    }

    [Fact]
    public void AiChangesMatchVectors()
    {
        foreach (var v in TrVectors.Get("aiChanges").EnumerateArray())
        {
            var sent = v.GetProperty("sent").EnumerateObject().Select(p => (p.Name, p.Value.GetString()!)).ToList();
            var reply = v.GetProperty("reply").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            var changes = AiChatExchange.ComputeChanges(sent, reply);
            var updates = v.GetProperty("updates").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            Assert.Equal(updates, changes.Updates);
            Assert.Equal(v.GetProperty("unchanged").GetInt32(), changes.Unchanged);
            Assert.Equal(v.GetProperty("unknown").GetInt32(), changes.Unknown);
            Assert.Equal(v.GetProperty("missing").EnumerateArray().Select(x => x.GetString()), changes.Missing);
        }
    }

    [Fact]
    public void SeriesStemsMatchVectors()
    {
        foreach (var v in TrVectors.Get("seriesStems").EnumerateArray())
            Assert.Equal(v.GetProperty("stem").GetString(), SeriesStem.Compute(v.GetProperty("name").GetString()!));
    }

    [Fact]
    public void ExportNamesMatchVectors()
    {
        foreach (var v in TrVectors.Get("exportNames").EnumerateArray())
            Assert.Equal(v.GetProperty("expected").GetString(),
                ExportNaming.PageFileName(v.GetProperty("pageCount").GetInt32(), v.GetProperty("index").GetInt32(), v.GetProperty("extension").GetString()!));
    }
}
