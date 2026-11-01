using Panely.Core;
using Panely.Core.Abstractions;
using Panely.Core.Cache;
using Panely.Core.Diagnostics;
using Panely.Core.Reader;
using Panely.Core.State;
using Panely.Core.Translation;
using Panely.Domain.Model;
using Panely.Domain.Reading;
using Panely.Tests.Reader;
using Panely.Tests.Translation;

namespace Panely.Tests.Core;

/// <summary>성능 수정이 동작을 바꾸지 않았는지, 비싼 경로가 다시 생기지 않는지 지킨다.</summary>
public class PerformanceGuardTests
{
    private static ExtractionCache NewCache(string root, long limit, bool background = false)
    {
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        return new ExtractionCache(paths, new DiagnosticsService(paths), limit) { EnforceInBackground = background };
    }

    private static Task<CacheLease> Put(ExtractionCache cache, string key, int bytes)
        => cache.GetOrCreateAsync(key, (dir, _) =>
        {
            File.WriteAllBytes(System.IO.Path.Combine(dir, "data.bin"), new byte[bytes]);
            return bytes;
        }, CancellationToken.None);

    [Fact]
    public async Task TurningPage_DoesNotReportBookmarkChange()
    {
        var state = new FakeStateStore();
        var opener = new FakeOpener(state);
        opener.AddBook("a.cbz", 10);
        var session = new ReaderSession(opener, state, null, _ => true);
        await session.OpenAsync(new OpenRequest("a.cbz"));
        var changes = new List<ReaderChange>();
        session.Changed += (_, c) => changes.Add(c);

        session.Jump(3);

        var change = Assert.Single(changes);
        Assert.True(change.HasFlag(ReaderChange.Page));
        Assert.False(change.HasFlag(ReaderChange.Bookmarks));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(150, 250)]
    [InlineData(1000, 1200)]
    [InlineData(2850, 3000)]
    public void CurrentPage_WithTops_MatchesOriginal(double top, double bottom)
    {
        double[] heights = [300, 700, 1000, 1000];
        var tops = VerticalStrip.Tops(heights);
        Assert.Equal(VerticalStrip.CurrentPage(heights, top, bottom), VerticalStrip.CurrentPage(heights, tops, top, bottom));
    }

    [Fact]
    public async Task BackgroundEnforce_ReleasesLeaseImmediately_AndEventuallyTrims()
    {
        using var dir = new TempDir();
        var cache = NewCache(dir.P("appdata"), 150, background: true);
        var a = await Put(cache, "a", 100);
        var b = await Put(cache, "b", 100);

        b.Dispose();

        Assert.False(cache.IsLeased("b"));
        await Wait.UntilAsync(() => cache.GetUsage().TotalBytes == 100);
        a.Dispose();
    }

    [Fact]
    public async Task DetachedStaleStaging_DoesNotTouchExtractionStartedAfterIt()
    {
        using var dir = new TempDir();
        var cache = NewCache(dir.P("appdata"), 10_000);
        var staging = System.IO.Path.Combine(new AppPaths(dir.P("appdata")).ExtractionCache, "v1", "staging");
        Directory.CreateDirectory(System.IO.Path.Combine(staging, "orphan"));

        cache.DetachStaleStaging();
        Assert.Empty(Directory.GetDirectories(staging));

        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var extraction = cache.GetOrCreateAsync("k", (content, token) =>
        {
            started.SetResult();
            finish.Task.Wait(token);
            File.WriteAllBytes(System.IO.Path.Combine(content, "data.bin"), new byte[5]);
            return 5;
        }, CancellationToken.None);
        await started.Task;

        cache.FinishOrphanCleanup();
        finish.SetResult();

        using var lease = await extraction;
        Assert.True(File.Exists(System.IO.Path.Combine(lease.ContentPath, "data.bin")));
    }

    [Fact]
    public void SourceLookups_FollowAddedSources()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.P("appdata"));
        paths.EnsureCreated();
        using var store = new StateStore(paths, new DiagnosticsService(paths));
        var folder = Directory.CreateDirectory(dir.P("books")).FullName;

        Assert.Null(store.FindSourceByPath(folder));
        var source = store.GetOrCreateSource(folder, SourceKind.Folder, "books");

        Assert.Equal(source.Id, store.FindSourceByPath(folder)?.Id);
        Assert.Equal(source.Id, store.FindSource(source.Id)?.Id);
        Assert.Equal(source.Id, store.GetOrCreateSource(folder, SourceKind.Folder, "books").Id);
    }

    [Fact]
    public void DetectorTensor_LaysOutChannelsAsIndexerExpects()
    {
        var size = OnnxBalloonDetector.InputSize;
        var data = new byte[size * size * 4];
        new Random(7).NextBytes(data);

        var tensor = OnnxBalloonDetector.ToTensor(new PagePixels(size, size, data, size * 4));

        foreach (var (y, x) in new[] { (0, 0), (0, size - 1), (size - 1, 0), (size - 1, size - 1), (123, 456) })
        {
            var i = (y * size + x) * 4;
            Assert.Equal(data[i + 2] / 255f, tensor[0, 0, y, x]);
            Assert.Equal(data[i + 1] / 255f, tensor[0, 1, y, x]);
            Assert.Equal(data[i] / 255f, tensor[0, 2, y, x]);
        }
    }

    [Fact]
    public async Task IdleScheduler_ReleasesModels()
    {
        using var dir = new TempDir();
        var translator = new ReleasableTranslator();
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(translator: translator), new TranslationCache(dir.P("cache")),
            new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(1)), new TranslationOptions { IdleModelRelease = TimeSpan.FromMilliseconds(20) });

        await Wait.UntilAsync(() => translator.Released > 0);
    }

    [Fact]
    public async Task TurningPagesWhileReading_DoesNotKeepModelsLoaded()
    {
        using var dir = new TempDir();
        var translator = new ReleasableTranslator();
        await using var scheduler = new TranslationScheduler(FakeProviders.Create(translator: translator), new TranslationCache(dir.P("cache")),
            new FakePixels(), new FakeBookAccess(() => FakeBooks.Create(1)), new TranslationOptions { IdleModelRelease = TimeSpan.FromMilliseconds(100) });

        // 유휴 시간보다 짧은 간격으로 계속 쪽을 넘겨도 모델은 내려가야 한다
        for (var page = 0; page < 200 && translator.Released == 0; page++)
        {
            scheduler.SetCurrent("book", "ko", page);
            await Task.Delay(10);
        }

        Assert.True(translator.Released > 0);
    }

    private sealed class ReleasableTranslator : ITranslator, IIdleReleasable
    {
        private readonly PseudoTranslator _inner = new() { Delay = TimeSpan.Zero };
        private int _released;

        public int Released => Volatile.Read(ref _released);
        public string Name => _inner.Name;
        public string Revision => _inner.Revision;
        public Capability GetCapability(string source, string target) => _inner.GetCapability(source, target);
        public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<TranslationResultItem>> TranslateAsync(IReadOnlyList<TranslationRequestItem> items, IReadOnlyList<TranslationContextLine> context,
            string source, string target, CancellationToken cancellationToken)
            => _inner.TranslateAsync(items, context, source, target, cancellationToken);

        public Task ReleaseIdleAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _released);
            return Task.CompletedTask;
        }
    }
}
