using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Panely.Core.Abstractions;
using Panely.Core.Archives;
using static Panely.Tests.Core.CoreTestKit;

namespace Panely.Tests.Core;

public class ZipReaderTests
{
    [Fact]
    public void Crc32_MatchesStandardCheckValue()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
        Assert.Equal(0u, Crc32.Compute([]));
        // 8바이트 묶음 처리와 나머지 바이트 처리가 이어서 계산해도 같다
        var data = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");
        Assert.Equal(0x414FA339u, Crc32.Compute(data));
        Assert.Equal(Crc32.Compute(data), Crc32.Append(Crc32.Compute(data.AsSpan(0, 13)), data.AsSpan(13)));
    }

    [Fact]
    public async Task ParallelEntryReads_ReturnEachEntry()
    {
        using var t = new TempDir();
        var zip = t.P("book.cbz");
        var entries = Enumerable.Range(0, 24).Select(i => ($"p{i:00}.png", (byte[]?)PngBytes(8 + i, 8))).ToList();
        Zip(zip, entries);
        using var reader = new ZipArchiveReader(zip);

        var reads = entries.Select(e => reader.ReadEntryAsync(e.Item1, CancellationToken.None)).ToList();
        var prefixes = entries.Select(e => reader.ReadPrefixAsync(e.Item1, 16, CancellationToken.None)).ToList();
        var results = await Task.WhenAll(reads);
        var prefixResults = await Task.WhenAll(prefixes);

        for (var i = 0; i < entries.Count; i++)
        {
            Assert.Equal(entries[i].Item2, results[i]);
            Assert.Equal(entries[i].Item2!.Take(16), prefixResults[i]);
        }
    }

    [Fact]
    public async Task ReadsAfterDispose_Fail()
    {
        using var t = new TempDir();
        var zip = t.P("book.cbz");
        Zip(zip, [("a.png", PngBytes(1, 1))]);
        var reader = new ZipArchiveReader(zip);
        reader.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadEntryAsync("a.png", CancellationToken.None));
    }

    private static Encoding CodePage(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }

    /// <summary>UTF-8 표시 없이 주어진 인코딩으로 이름을 쓴다. 쪽 내용은 위치마다 다르다.</summary>
    private static void LegacyZip(string path, Encoding encoding, params string[] names)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, false, encoding);
        for (var i = 0; i < names.Length; i++)
        {
            using var s = zip.CreateEntry(names[i], CompressionLevel.NoCompression).Open();
            s.Write(PngBytes(10 + i, 20));
        }
    }

    [Fact]
    public async Task LegacyNames_Cp949_AreRecovered()
    {
        using var t = new TempDir();
        var zip = t.P("kr.cbz");
        LegacyZip(zip, CodePage(949), "가001.jpg", "가002.jpg");
        using var reader = new ZipArchiveReader(zip, systemCodePage: 0);
        Assert.Equal(new[] { "가001.jpg", "가002.jpg" }, reader.Entries.Select(e => e.Path));
        Assert.Equal(PngBytes(11, 20), await reader.ReadEntryAsync("가002.jpg", CancellationToken.None));
    }

    [Fact]
    public async Task LegacyNames_ShiftJis_AreRecovered_IncludingTrailByte5C()
    {
        // 表·ソ의 둘째 바이트는 0x5C라 바이트를 잘못 풀면 경로 구분자로 보인다. 949로는 풀리지 않는 이름이다.
        using var t = new TempDir();
        var zip = t.P("jp.cbz");
        LegacyZip(zip, CodePage(932), "表紙/ソ001.jpg", "表紙/ソ002.jpg");
        using var reader = new ZipArchiveReader(zip, systemCodePage: 0);
        Assert.Equal(new[] { "表紙/ソ001.jpg", "表紙/ソ002.jpg" }, reader.Entries.Select(e => e.Path));
        Assert.Equal(PngBytes(10, 20), await reader.ReadEntryAsync("表紙/ソ001.jpg", CancellationToken.None));
    }

    [Fact]
    public void LegacyNames_SystemCodePageIsTriedBeforeCommonOnes()
    {
        // テスト의 Shift-JIS 바이트는 949로도 오류 없이 풀린다. 시스템 코드 페이지가 먼저여야 한다.
        using var t = new TempDir();
        var zip = t.P("jp.cbz");
        LegacyZip(zip, CodePage(932), "テスト.jpg");
        using (var japanese = new ZipArchiveReader(zip, systemCodePage: 932))
            Assert.Equal("テスト.jpg", Assert.Single(japanese.Entries).Path);
        using var korean = new ZipArchiveReader(zip, systemCodePage: 949);
        Assert.NotEqual("テスト.jpg", Assert.Single(korean.Entries).Path);
    }

    [Fact]
    public void LegacyNames_SingleByteSystemCodePage_DoesNotHideCjkNames()
    {
        // 1252는 어떤 바이트든 풀어내므로 먼저 시도하면 서유럽 시스템에서 CP949 이름이 깨진다
        using var t = new TempDir();
        var zip = t.P("kr.cbz");
        LegacyZip(zip, CodePage(949), "가001.jpg");
        using var reader = new ZipArchiveReader(zip, systemCodePage: 1252);
        Assert.Equal("가001.jpg", Assert.Single(reader.Entries).Path);
    }

    [Fact]
    public void LegacyNames_SingleByteSystemCodePage_IsTheLastResort()
    {
        // 0xE9 0x20은 CJK 후보 어디에서도 풀리지 않는다(0xE9는 2바이트 문자의 첫 바이트인데 0x20은 둘째 바이트가 될 수 없다)
        using var t = new TempDir();
        var zip = t.P("fr.cbz");
        LegacyZip(zip, Encoding.Latin1, "é .jpg");
        using var reader = new ZipArchiveReader(zip, systemCodePage: 1252);
        Assert.Equal("é .jpg", Assert.Single(reader.Entries).Path);
    }

    [Fact]
    public void Utf8Names_StayAsTheyAre_FlaggedOrNot()
    {
        using var t = new TempDir();
        var flagged = t.P("flagged.cbz");
        Zip(flagged, [("한글/가.jpg", PngBytes(1, 1))]);
        using (var reader = new ZipArchiveReader(flagged, systemCodePage: 949))
            Assert.Equal("한글/가.jpg", Assert.Single(reader.Entries).Path);

        // 표시는 없지만 바이트는 UTF-8인 이름(일부 macOS·Linux 도구)
        var unflagged = t.P("unflagged.cbz");
        LegacyZip(unflagged, Encoding.Latin1, Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("한글/가.jpg")));
        using var plain = new ZipArchiveReader(unflagged, systemCodePage: 949);
        Assert.Equal("한글/가.jpg", Assert.Single(plain.Entries).Path);
    }

    [Fact]
    public void LegacyNames_ThatCollideUnderUtf8_BothSurvive()
    {
        using var t = new TempDir();
        var zip = t.P("kr.cbz");
        LegacyZip(zip, CodePage(949), "가.jpg", "각.jpg");
        using var reader = new ZipArchiveReader(zip, systemCodePage: 0);
        Assert.Equal(new[] { "가.jpg", "각.jpg" }, reader.Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task UndecodableNames_KeepEveryPage_UnderUniqueNames()
    {
        // 0x81 0x20, 0xFF 0x20은 어떤 후보로도 풀리지 않고 UTF-8 대체 후에는 같은 이름이 된다
        using var t = new TempDir();
        var zip = t.P("bad.cbz");
        LegacyZip(zip, Encoding.Latin1, "\u0081 .jpg", "ÿ .jpg");
        using var reader = new ZipArchiveReader(zip, systemCodePage: 0);
        Assert.Equal(2, reader.Entries.Count);
        Assert.All(reader.Entries, e =>
        {
            Assert.Contains('�', e.Path);
            Assert.EndsWith(".jpg", e.Path);
        });
        Assert.Equal(PngBytes(10, 20), await reader.ReadEntryAsync(reader.Entries[0].Path, CancellationToken.None));
        Assert.Equal(PngBytes(11, 20), await reader.ReadEntryAsync(reader.Entries[1].Path, CancellationToken.None));
    }

    [Fact]
    public async Task DeclaredSizeAboveCap_FailsBeforeAllocating()
    {
        using var t = new TempDir();
        var zip = t.P("lie.cbz");
        Zip(zip, [("a.png", PngBytes(1, 1))]);
        var bytes = File.ReadAllBytes(zip);
        var cd = bytes.AsSpan().LastIndexOf<byte>([0x50, 0x4B, 0x01, 0x02]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(cd + 24), 0xFFFFFFFE); // 중앙 디렉터리의 비압축 크기
        File.WriteAllBytes(zip, bytes);

        using var reader = new ZipArchiveReader(zip);
        var ex = await Assert.ThrowsAsync<BookOpenException>(() => reader.ReadEntryAsync("a.png", CancellationToken.None));
        Assert.Equal(OpenErrorKind.ExtractionLimit, ex.Kind);
    }

    [Fact]
    public async Task EntryCap_AllowsSizesUpToTheLimit()
    {
        using var t = new TempDir();
        var zip = t.P("book.cbz");
        var data = PngBytes(1, 1);
        Zip(zip, [("a.png", data)]);
        using var exact = new ZipArchiveReader(zip) { MaxEntryBytes = data.Length };
        Assert.Equal(data, await exact.ReadEntryAsync("a.png", CancellationToken.None));
        using var below = new ZipArchiveReader(zip) { MaxEntryBytes = data.Length - 1 };
        var ex = await Assert.ThrowsAsync<BookOpenException>(() => below.ReadEntryAsync("a.png", CancellationToken.None));
        Assert.Equal(OpenErrorKind.ExtractionLimit, ex.Kind);
        Assert.True(ExtractionLimits.DefaultMaxEntryBytes >= 1024L * 1024 * 1024);
    }
}
