using Panely.Core;
using Panely.Core.Abstractions;
using Panely.Core.Diagnostics;
using Panely.Core.Library;
using Panely.Core.State;
using Panely.Domain.Model;

namespace Panely.Tests.Core;

public class LibraryTests
{
    [Fact]
    public void ListChildren_FoldersAndArchives_NaturalSorted()
    {
        using var t = new TempDir();
        t.Dir("lib", "Vol 10");
        t.Dir("lib", "Vol 2");
        t.Dir("lib", ".hidden");
        var sys = t.Dir("lib", "sys");
        File.SetAttributes(sys, FileAttributes.Directory | FileAttributes.Hidden);
        File.WriteAllText(t.P("lib", "Vol 3.cbz"), "x");
        File.WriteAllText(t.P("lib", "cover.png"), "x");
        File.WriteAllText(t.P("lib", "notes.txt"), "x");
        t.Png("lib/Vol 2/sub/a.png");

        var nodes = LibraryService.ListChildren(t.P("lib"));
        Assert.Equal(new[] { "Vol 2", "Vol 3", "Vol 10" }, nodes.Select(n => n.Name));
        Assert.Equal(LibraryNodeKind.Archive, nodes[1].Kind);
        Assert.Equal("cbz", nodes[1].Extension);
        Assert.True(nodes[0].MayHaveChildren);
        Assert.False(nodes[2].MayHaveChildren);
    }

    [Fact]
    public async Task Watcher_DebouncesAndReportsParentDirectory()
    {
        using var t = new TempDir();
        var root = t.Dir("lib");
        var diag = new DiagnosticsService(new AppPaths(t.P("appdata")));
        using var service = new LibraryService(diag, TimeSpan.FromMilliseconds(200), t.P("appdata"));
        var got = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        service.DirectoriesChanged += (_, dirs) => { Interlocked.Increment(ref calls); got.TrySetResult(dirs); };
        service.SetRoot(root, autoRefresh: true);
        File.WriteAllText(t.P("lib", "ignored.png"), "x"); // 이미지는 트리 변경 아님
        Directory.CreateDirectory(t.P("lib", "New Series"));
        File.WriteAllText(t.P("lib", "Vol.cbz"), "x");
        var dirs = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(dirs, d => string.Equals(d, root, StringComparison.OrdinalIgnoreCase));
        await Task.Delay(500);
        Assert.Equal(1, calls); // 한 번으로 합쳐짐
    }

    [Fact]
    public void IsRelevant_FiltersAppDataAndImages()
    {
        using var t = new TempDir();
        var diag = new DiagnosticsService(new AppPaths(t.P("appdata")));
        using var service = new LibraryService(diag, TimeSpan.FromSeconds(1), t.P("appdata"));
        service.SetRoot(t.Root, autoRefresh: false);
        Assert.False(service.IsRelevant(t.P("appdata", "Cache", "x.zip")));
        Assert.False(service.IsRelevant(t.P("a.png")));
        Assert.False(service.IsRelevant(t.P(".git", "objects")));
        Assert.True(service.IsRelevant(t.P("b.cbr")));
        Assert.True(service.IsRelevant(t.P("deleted folder")));
    }

    [Fact]
    public async Task SourceMonitor_ReportsImageAddition()
    {
        using var t = new TempDir();
        var dir = t.Dir("book");
        t.Png("book/001.png");
        using var monitor = new SourceMonitor(TimeSpan.FromMilliseconds(150));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SourceChanged += (_, _) => changed.TrySetResult();
        monitor.Watch(dir);
        File.WriteAllText(t.P("book", "readme.txt"), "x"); // 무관한 파일 → signature 동일
        await Task.Delay(500);
        Assert.False(changed.Task.IsCompleted);
        t.Png("book/002.png");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SourceMonitor_ArchiveRewrite()
    {
        using var t = new TempDir();
        var file = t.P("a.cbz");
        File.WriteAllText(file, "one");
        using var monitor = new SourceMonitor(TimeSpan.FromMilliseconds(150));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SourceChanged += (_, _) => changed.TrySetResult();
        monitor.Watch(file);
        await Task.Delay(100);
        File.WriteAllText(file, "two and more");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Availability()
    {
        using var t = new TempDir();
        var file = t.P("x.cbz");
        File.WriteAllText(file, "x");
        Assert.Equal(SourceAvailability.Available, AvailabilityChecker.Check(new SourceRef { Id = Guid.NewGuid(), Path = file, Kind = SourceKind.Archive, Title = "x" }));
        Assert.Equal(SourceAvailability.InvalidReference, AvailabilityChecker.Check(new SourceRef { Id = Guid.NewGuid(), Path = t.P("gone.cbz"), Kind = SourceKind.Archive, Title = "g" }));
        var missingDrive = Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\").FirstOrDefault(d => !Directory.Exists(d));
        if (missingDrive is not null)
            Assert.Equal(SourceAvailability.TemporarilyUnavailable, AvailabilityChecker.Check(new SourceRef { Id = Guid.NewGuid(), Path = missingDrive + @"Comics\a.cbz", Kind = SourceKind.Archive, Title = "u" }));
    }

    [Fact]
    public void ContinueReading_MostRecentUnfinishedFirst()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.P("appdata"));
        paths.EnsureCreated();
        var diag = new DiagnosticsService(paths);
        using var store = new StateStore(paths, diag);
        var a = store.GetOrCreateSource(t.P("a"), SourceKind.Folder, "a");
        var b = store.GetOrCreateSource(t.P("b"), SourceKind.Folder, "b");
        var c = store.GetOrCreateSource(t.P("c"), SourceKind.Folder, "c");
        store.SetProgress(BookKeys.Make(a.Id, null), 1, 10, false);
        Thread.Sleep(5);
        store.SetProgress(BookKeys.Make(b.Id, null), 9, 10, true);
        Thread.Sleep(5);
        store.SetProgress(BookKeys.Make(c.Id, "v1"), 2, 10, false);
        store.SetProgress(BookKeys.Make(Guid.NewGuid(), null), 0, 0, false); // total 0 제외

        var keys = ContinueReading.SuggestBookKeys(store, _ => true);
        Assert.Equal(new[] { BookKeys.Make(c.Id, "v1"), BookKeys.Make(a.Id, null) }, keys);
        var filtered = ContinueReading.SuggestBookKeys(store, k => !k.Contains(c.Id.ToString()));
        Assert.Equal(BookKeys.Make(a.Id, null), Assert.Single(filtered));

        var request = ContinueReading.ToRequest(store, BookKeys.Make(c.Id, "v1"), OpenIntent.Continue)!;
        Assert.Equal("v1", request.InnerPath);
        Assert.Equal(c.Path, request.Path);
    }
}
