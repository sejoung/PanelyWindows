using Panely.Core.Abstractions;
using Panely.Core.Archives;
using static Panely.Tests.Core.CoreTestKit;

namespace Panely.Tests.Core;

public class ExtractionTests
{
    [Theory]
    [InlineData("../evil.png")]
    [InlineData("a/../../evil.png")]
    [InlineData("/abs.png")]
    [InlineData("\\\\server\\share\\x.png")]
    [InlineData("C:/Windows/x.png")]
    [InlineData("c:x.png")]
    [InlineData("file.png:stream")]
    [InlineData("CON.png")]
    [InlineData("dir/nul")]
    [InlineData("dir/LPT1.txt")]
    [InlineData("trailing. /x.png")]
    [InlineData("dot./x.png")]
    [InlineData("a/./../b.png")]
    [InlineData("")]
    public void UnsafeEntryPaths_AreRejected(string path) => Assert.Null(EntryPathGuard.Sanitize(path));

    [Theory]
    [InlineData("ok.png", "ok.png")]
    [InlineData("dir\\sub/a.png", "dir/sub/a.png")]
    [InlineData("한글 폴더/1권.png", "한글 폴더/1권.png")]
    [InlineData("a//b.png", "a/b.png")]
    [InlineData("console.png", "console.png")]
    public void SafeEntryPaths_AreNormalized(string path, string expected) => Assert.Equal(expected, EntryPathGuard.Sanitize(path));

    [Fact]
    public void Extraction_SkipsTraversalEntries()
    {
        using var t = new TempDir();
        var zip = t.P("evil.zip");
        Zip(zip, [("../escape.png", PngBytes(1, 1)), ("ok/a.png", PngBytes(1, 1)), ("C:/x.png", PngBytes(1, 1))]);
        var dest = t.P("out");
        using var reader = new ZipArchiveReader(zip);
        var warnings = new List<string>();
        new SafeExtractor(ExtractionLimits.Default, warnings.Add).ExtractAll(reader, dest, expandNested: false, CancellationToken.None);
        Assert.True(File.Exists(t.P("out", "ok", "a.png")));
        Assert.False(File.Exists(t.P("escape.png")));
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void Budget_ReplaceArchiveAccounting_AndSaturation()
    {
        var b = new ExtractionBudget(new ExtractionLimits(1000, 10, 3));
        b.AddWritten(600);
        b.ReplaceArchive(200, 500); // 600 - 200 + 500
        Assert.Equal(900, b.TotalBytes);
        Assert.Throws<BookOpenException>(() => b.AddWritten(200));
        Assert.Equal(long.MaxValue, ExtractionBudget.SaturatingAdd(long.MaxValue, 5));
        Assert.Equal(long.MaxValue, ExtractionBudget.SaturatingAdd(long.MaxValue - 1, long.MaxValue));

        var c = new ExtractionBudget(new ExtractionLimits(1000, 10, 3));
        c.AddWritten(400);
        c.CheckDeclared(700, replacedArchiveBytes: 200); // 400 - 200 + 700 = 900
        Assert.Throws<BookOpenException>(() => c.CheckDeclared(900, replacedArchiveBytes: 200));
        Assert.Throws<BookOpenException>(() => c.CheckDeclared(long.MaxValue));
    }

    [Fact]
    public void Budget_FileCountLimit()
    {
        var b = new ExtractionBudget(new ExtractionLimits(1000, 2, 3));
        b.AddFile();
        b.AddFile();
        var ex = Assert.Throws<BookOpenException>(() => b.AddFile());
        Assert.Equal(OpenErrorKind.ExtractionLimit, ex.Kind);
    }

    [Fact]
    public void Walk_DepthBudgetCountsDirectoriesAndArchives()
    {
        using var t = new TempDir();
        var inner = ZipBytes(Pages("", 1));
        var zipPath = t.P("outer.zip");
        Zip(zipPath, [
            ("a.zip", inner),                 // walk(0)에서 펼침
            ("d1/b.zip", inner),              // d1=walk(1) → 펼침
            ("d1/d2/c.zip", inner),           // d2=walk(2) → 펼침
            ("d1/d2/d3/d.zip", inner),        // d3=walk(3) → 중지, 펼치지 않음
            ("nest.zip", ZipBytes([("lvl2.zip", ZipBytes([("lvl3.zip", ZipBytes([("lvl4.zip", inner)]))]))])),
        ]);
        var dest = t.P("out");
        using var reader = new ZipArchiveReader(zipPath);
        new SafeExtractor(ExtractionLimits.Default).ExtractAll(reader, dest, expandNested: true, CancellationToken.None);

        Assert.True(Directory.Exists(t.P("out", "a.zip")));
        Assert.True(Directory.Exists(t.P("out", "d1", "b.zip")));
        Assert.True(Directory.Exists(t.P("out", "d1", "d2", "c.zip")));
        Assert.True(File.Exists(t.P("out", "d1", "d2", "d3", "d.zip")));
        // nest.zip(0) → lvl2.zip(1) → lvl3.zip(2) 펼침, lvl4.zip은 depth 3이라 파일로 남음
        Assert.True(Directory.Exists(t.P("out", "nest.zip", "lvl2.zip", "lvl3.zip")));
        Assert.True(File.Exists(t.P("out", "nest.zip", "lvl2.zip", "lvl3.zip", "lvl4.zip")));
    }

    [Fact]
    public void Walk_CorruptInnerArchiveIsKeptAsFile()
    {
        using var t = new TempDir();
        var zipPath = t.P("outer.zip");
        Zip(zipPath, [("bad.zip", [0x50, 0x4B, 0x03, 0x04, 9, 9, 9]), ("good.zip", ZipBytes(Pages("", 1)))]);
        var dest = t.P("out");
        using var reader = new ZipArchiveReader(zipPath);
        new SafeExtractor(ExtractionLimits.Default).ExtractAll(reader, dest, expandNested: true, CancellationToken.None);
        Assert.True(File.Exists(t.P("out", "bad.zip")));
        Assert.True(Directory.Exists(t.P("out", "good.zip")));
    }

    [Fact]
    public void ZipReader_CrcHelperMatchesKnownValue()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }
}
