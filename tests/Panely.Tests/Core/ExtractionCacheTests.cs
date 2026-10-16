using Panely.Core;
using Panely.Core.Cache;
using Panely.Core.Diagnostics;

namespace Panely.Tests.Core;

public class ExtractionCacheTests
{
    private static (TempDir Temp, ExtractionCache Cache) Create(long limit)
    {
        var t = new TempDir();
        var paths = new AppPaths(t.P("appdata"));
        paths.EnsureCreated();
        return (t, new ExtractionCache(paths, new DiagnosticsService(paths), limit));
    }

    private static Task<CacheLease> Put(ExtractionCache cache, string key, int bytes)
        => cache.GetOrCreateAsync(key, (dir, _) =>
        {
            File.WriteAllBytes(System.IO.Path.Combine(dir, "data.bin"), new byte[bytes]);
            return bytes;
        }, CancellationToken.None);

    [Fact]
    public void Key_IsFullSha256OfIdentity()
    {
        var key = ExtractionCache.ComputeKey(@"C:\a.zip", 10, 20);
        Assert.Equal(64, key.Length);
        Assert.Equal(key, ExtractionCache.ComputeKey(@"C:\a.zip", 10, 20));
        Assert.NotEqual(key, ExtractionCache.ComputeKey(@"C:\a.zip", 10, 21));
    }

    [Fact]
    public async Task Hit_ReturnsSameContent_AndPopulateRunsOnce()
    {
        var (t, cache) = Create(10_000);
        using var scope = t;
        var runs = 0;
        Func<string, CancellationToken, long> populate = (dir, _) => { runs++; File.WriteAllText(System.IO.Path.Combine(dir, "x"), "1"); return 1; };
        using (var a = await cache.GetOrCreateAsync("k", populate, CancellationToken.None))
        using (var b = await cache.GetOrCreateAsync("k", populate, CancellationToken.None))
        {
            Assert.Equal(a.ContentPath, b.ContentPath);
            Assert.True(File.Exists(System.IO.Path.Combine(a.ContentPath, "x")));
        }
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Lru_EvictsOldestUnleased_KeepsLeased()
    {
        var (t, cache) = Create(250);
        using var scope = t;
        var leased = await Put(cache, "a", 100);       // 보호
        (await Put(cache, "b", 100)).Dispose();
        await Task.Delay(20);
        (await Put(cache, "c", 100)).Dispose();        // 300 > 250 → b 제거
        var usage = cache.GetUsage();
        Assert.Equal(200, usage.TotalBytes);
        Assert.NotNull(cache.TryAcquire("a"));
        Assert.Null(cache.TryAcquire("b"));
        Assert.True(cache.IsLeased("a"));
        leased.Dispose();
    }

    [Fact]
    public async Task LeaseCanTemporarilyExceedBudget()
    {
        var (t, cache) = Create(150);
        using var scope = t;
        using var a = await Put(cache, "a", 100);
        using var b = await Put(cache, "b", 100);
        Assert.Equal(200, cache.GetUsage().TotalBytes);
        Assert.Equal(0, cache.GetUsage().ClearableBytes);
        b.Dispose(); // 해제 후 정리
        Assert.Equal(100, cache.GetUsage().TotalBytes);
    }

    [Fact]
    public async Task Clear_RemovesOnlyUnleased()
    {
        var (t, cache) = Create(10_000);
        using var scope = t;
        using var keep = await Put(cache, "keep", 10);
        (await Put(cache, "drop", 20)).Dispose();
        var usage = cache.GetUsage();
        Assert.Equal(30, usage.TotalBytes);
        Assert.Equal(20, usage.ClearableBytes);
        Assert.Equal(10_000, usage.LimitBytes);
        var removed = await cache.ClearAsync(CancellationToken.None);
        Assert.Equal(20, removed);
        Assert.Equal(10, cache.GetUsage().TotalBytes);
    }

    [Fact]
    public async Task FailedPopulate_LeavesNoEntryOrStaging_AndOrphansAreCleaned()
    {
        var (t, cache) = Create(10_000);
        using var scope = t;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrCreateAsync("x", (dir, _) =>
        {
            File.WriteAllText(System.IO.Path.Combine(dir, "partial"), "p");
            throw new InvalidOperationException();
        }, CancellationToken.None));
        Assert.Null(cache.TryAcquire("x"));
        var staging = t.P("appdata", "Cache", "extraction", "v1", "staging");
        Assert.Empty(Directory.GetDirectories(staging));

        Directory.CreateDirectory(System.IO.Path.Combine(staging, "orphan-123"));
        Directory.CreateDirectory(t.P("appdata", "Cache", "extraction", "v1", "entries", "incomplete"));
        cache.CleanupOrphans();
        Assert.Empty(Directory.GetDirectories(staging));
        Assert.False(Directory.Exists(t.P("appdata", "Cache", "extraction", "v1", "entries", "incomplete")));
    }
}
