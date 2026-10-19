using System.Text;
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
}
