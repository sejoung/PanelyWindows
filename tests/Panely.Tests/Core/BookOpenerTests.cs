using System.IO.Compression;
using Panely.Core.Abstractions;
using Panely.Core.Archives;
using Panely.Domain.Model;
using static Panely.Tests.Core.CoreTestKit;

namespace Panely.Tests.Core;

public class BookOpenerTests
{
    private static Task<OpenedBook> Open(CoreEnv env, string path, string? inner = null, OpenIntent intent = OpenIntent.Normal)
        => env.Opener.OpenAsync(new OpenRequest(path, inner, null, intent), null, CancellationToken.None);

    [Fact]
    public async Task FolderBook_NaturalOrder_ExcludesHidden()
    {
        using var env = new CoreEnv();
        var dir = env.Temp.Dir("lib", "Book");
        foreach (var n in new[] { "10", "2", "1", "01", "Ａ", "c", "B", "한글" }) env.Temp.Png($"lib/Book/{n}.png");
        env.Temp.Png("lib/Book/.dot.png");
        env.Temp.Png("lib/Book/._fork.png");
        var hidden = env.Temp.Png("lib/Book/hidden.png");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        File.WriteAllText(env.Temp.P("lib/Book/notes.txt"), "x");
        Directory.CreateDirectory(env.Temp.P("lib/Book/__MACOSX"));

        using var book = await Open(env, dir);
        var names = book.Pages.Select(p => System.IO.Path.GetFileNameWithoutExtension(p.FileName)).ToList();
        Assert.Equal(8, names.Count); // Windows 파일 이름은 대소문자 무시이므로 서로 다른 글자 사용
        Assert.Equal(new[] { "01", "1", "2", "10" }, names.Take(4));
        Assert.True(names.IndexOf("Ａ") < names.IndexOf("B"));
        Assert.True(names.IndexOf("B") < names.IndexOf("c"));
        Assert.Equal("한글", names[^1]);
        Assert.All(book.Pages, p => Assert.StartsWith(book.BookKey + "|", p.PageId));
        Assert.Equal(Enumerable.Range(0, 8), book.Pages.Select(p => p.Index));
    }

    [Fact]
    public async Task ZipNamedCbr_IsDetectedByHeader()
    {
        using var env = new CoreEnv();
        var path = env.Temp.P("lib", "Book.cbr");
        Zip(path, Pages("", 3));
        Assert.Equal(ArchiveFormat.Zip, ArchiveSniffer.DetectFile(path));
        using var book = await Open(env, path);
        Assert.Equal(3, book.Pages.Count);
        Assert.Null(book.Data.TryGetFilePath(book.Pages[0]));
        var bytes = await book.Data.ReadAsync(book.Pages[0], CancellationToken.None);
        Assert.Equal(PngBytes(11, 20), bytes);
        var prefix = await book.Data.ReadPrefixAsync(book.Pages[1], 8, CancellationToken.None);
        Assert.Equal(8, prefix.Length);
    }

    [Fact]
    public void MagicBytes()
    {
        Assert.Equal(ArchiveFormat.Rar, ArchiveSniffer.Detect([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]));
        Assert.Equal(ArchiveFormat.Zip, ArchiveSniffer.Detect([0x50, 0x4B, 0x05, 0x06]));
        Assert.Equal(ArchiveFormat.Unknown, ArchiveSniffer.Detect([1, 2, 3]));
    }

    [Fact]
    public async Task SeriesArchive_722Wrapper_NextVolumeIsSibling()
    {
        using var env = new CoreEnv();
        Zip(env.Temp.P("SeriesA", "Vol01.zip"), Pages("722/722 pages/", 3));
        Zip(env.Temp.P("SeriesA", "Vol02.zip"), Pages("722/722 pages/", 2));
        Zip(env.Temp.P("SeriesA", "Vol10.zip"), Pages("", 2));

        using var book = await Open(env, env.Temp.P("SeriesA", "Vol01.zip"));
        Assert.Equal(3, book.Pages.Count);
        Assert.Equal(3, book.Volumes.Count);
        Assert.Equal(0, book.VolumeIndex);
        Assert.Equal(env.Temp.P("SeriesA", "Vol02.zip"), book.NextVolume!.Path);
        Assert.Null(book.PreviousVolume);
        Assert.Equal("Vol02", book.NextVolume.DisplayName);
        Assert.False(book.IsNestedCacheBook);

        using var next = await env.Opener.OpenAsync(book.NextVolume.ToRequest(OpenIntent.NextVolume), null, CancellationToken.None);
        Assert.Equal(2, next.Pages.Count);
        Assert.Equal(1, next.VolumeIndex);
        Assert.Equal(book.SeriesKey, next.SeriesKey);
    }

    [Fact]
    public async Task SeriesFolder_WrapperDoesNotOverrideOuterSeries()
    {
        using var env = new CoreEnv();
        env.Temp.Png("Comics/Series/Vol01/722/722 pages/001.png");
        env.Temp.Png("Comics/Series/Vol01/722/722 pages/002.png");
        env.Temp.Png("Comics/Series/Vol02/inner/001.png");
        env.Temp.Png("Comics/Other/001.png");

        using var book = await Open(env, env.Temp.P("Comics", "Series"));
        Assert.Equal(2, book.Pages.Count);
        Assert.Equal(2, book.Volumes.Count);
        Assert.Equal(0, book.VolumeIndex);
        Assert.Equal(env.Temp.P("Comics", "Series", "Vol02"), book.NextVolume!.Path);
        Assert.Equal(env.Temp.P("Comics", "Series", "Vol01", "722", "722 pages"), book.LibraryPath);

        using var next = await env.Opener.OpenAsync(book.NextVolume.ToRequest(OpenIntent.NextVolume), null, CancellationToken.None);
        Assert.Single(next.Pages);
        Assert.Equal(1, next.VolumeIndex);
        Assert.Equal(env.Temp.P("Comics", "Series", "Vol01"), next.PreviousVolume!.Path);
    }

    [Fact]
    public async Task DirectFileInWrapper_ClimbsToNearestMultiVolumeLevel()
    {
        using var env = new CoreEnv();
        Zip(env.Temp.P("S", "Vol01", "Vol01.cbz"), Pages("", 1));
        Zip(env.Temp.P("S", "Vol02", "Vol02.cbz"), Pages("", 1));
        using var book = await Open(env, env.Temp.P("S", "Vol01", "Vol01.cbz"));
        Assert.Equal(2, book.Volumes.Count);
        Assert.Equal(env.Temp.P("S", "Vol02"), book.NextVolume!.Path);
    }

    [Fact]
    public async Task SingleEpisodeFolder_DoesNotClimbIntoUnrelatedLibraryRoot()
    {
        using var env = new CoreEnv();
        env.Temp.Png("Lib/Webtoon/Ep 1/001.png");
        env.Temp.Png("Lib/Folder Book/001.png");
        Zip(env.Temp.P("Lib", "Series A", "Volume 01.cbz"), Pages("", 1));

        using var book = await Open(env, env.Temp.P("Lib", "Webtoon", "Ep 1"));
        Assert.True(book.Volumes.Count <= 1);
        Assert.Null(book.NextVolume);
        Assert.Null(book.PreviousVolume);
    }

    [Fact]
    public async Task ImageFolderInsideWrapper_ClimbsToSameSeriesVolumes()
    {
        using var env = new CoreEnv();
        env.Temp.Png("Lib/Series/Vol01/chapter/001.png");
        env.Temp.Png("Lib/Series/Vol02/chapter/001.png");

        using var book = await Open(env, env.Temp.P("Lib", "Series", "Vol01", "chapter"));
        Assert.Equal(2, book.Volumes.Count);
        Assert.Equal(env.Temp.P("Lib", "Series", "Vol02"), book.NextVolume!.Path);
    }

    [Fact]
    public async Task EmptyFolder_IsEmptyError()
    {
        using var env = new CoreEnv();
        var dir = env.Temp.Dir("empty");
        File.WriteAllText(env.Temp.P("empty", "a.txt"), "x");
        var ex = await Assert.ThrowsAsync<BookOpenException>(() => Open(env, dir));
        Assert.Equal(OpenErrorKind.Empty, ex.Kind);
    }

    [Fact]
    public async Task MissingPath_IsNotFound()
    {
        using var env = new CoreEnv();
        var ex = await Assert.ThrowsAsync<BookOpenException>(() => Open(env, env.Temp.P("nope.cbz")));
        Assert.Equal(OpenErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task CorruptZip_IsCorrupt()
    {
        using var env = new CoreEnv();
        var path = env.Temp.P("bad.cbz");
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var ex = await Assert.ThrowsAsync<BookOpenException>(() => Open(env, path));
        Assert.Equal(OpenErrorKind.Corrupt, ex.Kind);
    }

    [Fact]
    public async Task CrcMismatch_IsCorruptOnRead()
    {
        using var env = new CoreEnv();
        var path = env.Temp.P("crc.cbz");
        var data = new byte[4096];
        new Random(1).NextBytes(data);
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("001.png", CompressionLevel.NoCompression);
            using var s = e.Open();
            s.Write(data);
        }
        // stored 데이터 한 바이트 변조(로컬 헤더 30 + 이름 7 이후)
        var bytes = File.ReadAllBytes(path);
        bytes[30 + 7 + 100] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        using var book = await Open(env, path);
        var ex = await Assert.ThrowsAsync<BookOpenException>(() => book.Data.ReadAsync(book.Pages[0], CancellationToken.None));
        Assert.Equal(OpenErrorKind.Corrupt, ex.Kind);
    }

    [Fact]
    public async Task EncryptedZip_IsEncrypted()
    {
        using var env = new CoreEnv();
        var path = env.Temp.P("enc.cbz");
        Zip(path, Pages("", 1));
        // general purpose bit 0(암호화) 설정: 로컬 헤더 offset 6, 중앙 디렉터리 offset 8
        var bytes = File.ReadAllBytes(path);
        bytes[6] |= 1;
        var cd = FindSignature(bytes, [0x50, 0x4B, 0x01, 0x02]);
        bytes[cd + 8] |= 1;
        File.WriteAllBytes(path, bytes);
        var ex = await Assert.ThrowsAsync<BookOpenException>(() => Open(env, path));
        Assert.Equal(OpenErrorKind.Encrypted, ex.Kind);
    }

    private static int FindSignature(byte[] data, byte[] sig)
    {
        for (var i = data.Length - sig.Length; i >= 0; i--)
            if (data.AsSpan(i, sig.Length).SequenceEqual(sig)) return i;
        return -1;
    }

    [Fact]
    public async Task NestedArchive_VolumesAndInnerPathRules()
    {
        using var env = new CoreEnv();
        var outer = env.Temp.P("lib", "Box.cbz");
        Zip(outer, [
            ("Vol1.zip", ZipBytes(Pages("", 2))),
            ("Vol2.zip", ZipBytes(Pages("p/", 3))),
            ("readme.txt", "hi"u8.ToArray()),
        ]);

        using var book = await Open(env, outer);
        Assert.True(book.IsNestedCacheBook);
        Assert.Equal(2, book.Pages.Count);
        Assert.Equal("Vol1.zip", book.Book.InnerPath);
        Assert.Equal(2, book.Volumes.Count);
        Assert.All(book.Volumes, v => Assert.True(v.IsNested));
        Assert.Equal("Vol2.zip", book.Volumes[1].InnerPath);
        Assert.Equal("Vol2", book.Volumes[1].DisplayName);
        Assert.Equal(outer, book.WatchPath);
        Assert.NotNull(book.Data.TryGetFilePath(book.Pages[0]));
        Assert.True(env.Cache.GetUsage().TotalBytes > 0);
        Assert.Equal(0, env.Cache.GetUsage().ClearableBytes); // lease 보호

        using var second = await env.Opener.OpenAsync(book.Volumes[1].ToRequest(OpenIntent.NextVolume), null, CancellationToken.None);
        Assert.Equal(3, second.Pages.Count);
        Assert.Equal("Vol2.zip/p", second.Book.InnerPath);
        Assert.Equal(1, second.VolumeIndex);
        // 재해제에도 같은 pageId
        var pageId = second.Pages[0].PageId;
        second.Dispose();
        book.Dispose();
        await env.Cache.ClearAsync(CancellationToken.None);
        using var again = await Open(env, outer, "Vol2.zip/p", OpenIntent.Continue);
        Assert.Equal(pageId, again.Pages[0].PageId);

        var strict = await Assert.ThrowsAsync<BookOpenException>(() => Open(env, outer, "Missing.zip", OpenIntent.Bookmark));
        Assert.Equal(OpenErrorKind.InnerPathMissing, strict.Kind);
        using var fallback = await Open(env, outer, "Missing.zip", OpenIntent.Favorite);
        Assert.Equal("Vol1.zip", fallback.Book.InnerPath);
    }

    [Fact]
    public async Task FlatArchive_InnerFolderInnerPath()
    {
        using var env = new CoreEnv();
        var path = env.Temp.P("Sample Series.zip");
        Zip(path, [.. Pages("Volume 01/", 2), .. Pages("Volume 02/", 3)]);
        using var whole = await Open(env, path);
        Assert.Equal(5, whole.Pages.Count);
        using var vol = await Open(env, path, "Volume 02", OpenIntent.Continue);
        Assert.Equal(3, vol.Pages.Count);
        Assert.Equal($"source:{vol.Source.Id:D}#Volume 02", vol.BookKey);
        await Assert.ThrowsAsync<BookOpenException>(() => Open(env, path, "Volume 09", OpenIntent.Continue));
    }

    [Fact]
    public async Task NestedExtraction_LimitExceeded()
    {
        using var env = new CoreEnv(limits: new ExtractionLimits(500, 1000, 3));
        var outer = env.Temp.P("big.zip");
        Zip(outer, [("a.zip", ZipBytes(Pages("", 40)))]);
        var ex = await Assert.ThrowsAsync<BookOpenException>(() => Open(env, outer));
        Assert.Equal(OpenErrorKind.ExtractionLimit, ex.Kind);
        // 실패한 staging은 남지 않는다
        Assert.Equal(0, env.Cache.GetUsage().TotalBytes);
        var staging = System.IO.Path.Combine(env.Paths.ExtractionCache, "v1", "staging");
        Assert.Empty(Directory.GetDirectories(staging));
    }

    [Fact]
    public async Task Cancellation_ThrowsOperationCanceled()
    {
        using var env = new CoreEnv();
        var path = env.Temp.P("c.cbz");
        Zip(path, Pages("", 2));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Opener.OpenAsync(new OpenRequest(path), null, cts.Token));
    }

    [Fact]
    public async Task ProgressStagesReported()
    {
        using var env = new CoreEnv();
        var path = env.Temp.P("p.cbz");
        Zip(path, Pages("", 2));
        var stages = new List<LoadStage>();
        var progress = new SyncProgress<LoadStage>(stages.Add);
        using var book = await env.Opener.OpenAsync(new OpenRequest(path), progress, CancellationToken.None);
        Assert.Contains(LoadStage.Opening, stages);
        Assert.Contains(LoadStage.Building, stages);
        Assert.NotEmpty(env.Diagnostics.RecentLoadEvents);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) { lock (this) report(value); }
    }
}
