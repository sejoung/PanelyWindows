using Panely.Core.Abstractions;
using Panely.Core.Archives;

namespace Panely.Tests.Core;

/// <summary>SharpCompress 테스트 아카이브(MIT)로 RAR4/RAR5/solid/암호 경로를 확인한다.</summary>
public class RarReaderTests
{
    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "tests", "Panely.Tests", "Core", "Fixtures", "rar", name);
            if (File.Exists(candidate)) return candidate;
            candidate = System.IO.Path.Combine(dir.FullName, "Core", "Fixtures", "rar", name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(name);
    }

    [Theory]
    [InlineData("Rar.rar", false)]
    [InlineData("Rar5.rar", false)]
    [InlineData("Rar.solid.rar", true)]
    [InlineData("Rar5.solid.rar", true)]
    public async Task ReadsEntries_RandomAndSequentialAgree(string name, bool solid)
    {
        var path = Fixture(name);
        Assert.Equal(ArchiveFormat.Rar, ArchiveSniffer.DetectFile(path));
        using var reader = new RarArchiveReader(path);
        Assert.Equal(solid, reader.IsSolid);
        Assert.NotEmpty(reader.Entries);

        var sequential = new Dictionary<string, byte[]>();
        reader.ForEachEntry((info, s) =>
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            sequential[info.Path] = ms.ToArray();
        }, CancellationToken.None);
        Assert.Equal(reader.Entries.Count, sequential.Count);

        // 마지막 entry를 임의 접근(solid에서는 앞쪽 해제가 필요한 경로)
        var last = reader.Entries[^1];
        var bytes = await reader.ReadEntryAsync(last.Path, CancellationToken.None);
        Assert.Equal(sequential[last.Path], bytes);
        Assert.Equal(last.Size, bytes.Length);
        var prefix = await reader.ReadPrefixAsync(last.Path, 16, CancellationToken.None);
        Assert.Equal(bytes.Take(16), prefix);
    }

    [Fact]
    public void SolidAndNonSolidHaveSameContent()
    {
        using var a = new RarArchiveReader(Fixture("Rar5.rar"));
        using var b = new RarArchiveReader(Fixture("Rar5.solid.rar"));
        var ca = new Dictionary<string, uint?>();
        foreach (var e in a.Entries) ca[e.Path] = e.Crc32;
        foreach (var e in b.Entries)
            if (ca.TryGetValue(e.Path, out var crc)) Assert.Equal(crc, e.Crc32);
    }

    [Fact]
    public void SolidExtraction_WritesAllFiles()
    {
        using var t = new TempDir();
        using var reader = new RarArchiveReader(Fixture("Rar.solid.rar"));
        var dest = t.P("out");
        var bytes = new SafeExtractor(ExtractionLimits.Default).ExtractAll(reader, dest, expandNested: false, CancellationToken.None);
        Assert.Equal(reader.Entries.Sum(e => e.Size), bytes);
        foreach (var e in reader.Entries)
            Assert.True(File.Exists(System.IO.Path.Combine(dest, EntryPathGuard.Sanitize(e.Path)!.Replace('/', '\\'))), e.Path);
    }

    [Fact]
    public void HeaderEncryptedRar_IsEncryptedError()
    {
        var ex = Assert.Throws<BookOpenException>(() => new RarArchiveReader(Fixture("Rar.encrypted_filesAndHeader.rar")));
        Assert.Equal(OpenErrorKind.Encrypted, ex.Kind);
    }

    [Fact]
    public async Task FileEncryptedRar5_IsEncryptedOnRead()
    {
        BookOpenException? error = null;
        try
        {
            using var reader = new RarArchiveReader(Fixture("Rar5.encrypted_filesOnly.rar"));
            Assert.Contains(reader.Entries, e => e.IsEncrypted);
            var entry = reader.Entries.First(e => e.IsEncrypted);
            await reader.ReadEntryAsync(entry.Path, CancellationToken.None);
        }
        catch (BookOpenException ex)
        {
            error = ex;
        }
        Assert.NotNull(error);
        Assert.Equal(OpenErrorKind.Encrypted, error!.Kind);
    }

    [Theory]
    [InlineData("Rar5.rar", false)]
    [InlineData("Rar5.solid.rar", true)]
    [InlineData("Rar.solid.rar", true)]
    public async Task Opener_RarBooks_SolidServedFromCache(string name, bool solid)
    {
        using var env = new CoreEnv();
        var path = env.Temp.P("lib", System.IO.Path.ChangeExtension(name, ".cbr"));
        Directory.CreateDirectory(env.Temp.P("lib"));
        File.Copy(Fixture(name), path);
        using var book = await env.Opener.OpenAsync(new OpenRequest(path), null, CancellationToken.None);
        Assert.NotEmpty(book.Pages);
        Assert.All(book.Pages, p => Assert.True(SupportedFormats.IsImage(p.FileName)));
        Assert.Equal(solid, book.Data.TryGetFilePath(book.Pages[0]) is not null);
        Assert.Equal(solid, book.Lease is not null);
        var bytes = await book.Data.ReadAsync(book.Pages[^1], CancellationToken.None);
        using var reader = new RarArchiveReader(path);
        var entry = reader.Entries.First(e => e.Path.EndsWith(book.Pages[^1].FileName, StringComparison.Ordinal));
        Assert.Equal(entry.Size, bytes.Length);
        Assert.Equal(entry.Crc32, Crc32.Compute(bytes));
    }

    [Fact]
    public void RarNamedCbz_IsDetectedAsRar()
    {
        using var t = new TempDir();
        var path = t.P("book.cbz");
        File.Copy(Fixture("Rar5.rar"), path);
        Assert.Equal(ArchiveFormat.Rar, ArchiveSniffer.DetectFile(path));
    }
}
