using System.Text.Json;
using Panely.Core;
using Panely.Core.Abstractions;
using Panely.Core.Diagnostics;
using Panely.Core.State;
using Panely.Domain.Model;

namespace Panely.Tests.Core;

public class StateStoreTests
{
    private static readonly Guid SampleSource = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private const string SampleKey = "source:11111111-1111-4111-8111-111111111111#Volume 01";

    private static (TempDir T, AppPaths Paths, DiagnosticsService Diag) Env()
    {
        var t = new TempDir();
        var paths = new AppPaths(t.P("appdata"));
        paths.EnsureCreated();
        return (t, paths, new DiagnosticsService(paths));
    }

    private static string ExamplePath => System.IO.Path.Combine(AppContext.BaseDirectory, "Vectors", "examples", "state.json");

    [Fact]
    public void LoadsExampleStateJson()
    {
        var (t, paths, diag) = Env();
        using var scope = t;
        File.Copy(ExamplePath, paths.StateFile);
        using var store = new StateStore(paths, diag);

        Assert.False(store.IsReadOnly);
        Assert.Null(store.LoadProblem);
        Assert.Equal(ReadingLayout.Single, store.Settings.Layout);
        Assert.Equal(FitMode.FitScreen, store.Settings.FitMode);
        Assert.True(store.Settings.LibraryAutoRefreshEnabled);
        Assert.Equal(2, store.GetPosition(SampleKey));
        var progress = store.GetProgress(SampleKey)!;
        Assert.Equal(24, progress.Total);
        Assert.False(progress.Finished);
        var source = store.FindSource(SampleSource)!;
        Assert.Equal(SourceKind.Archive, source.Kind);
        Assert.Equal(@"C:\Comics\Sample Series.zip", source.Path);
        Assert.Single(store.Recents);
        var marks = store.GetBookmarks(SampleKey);
        Assert.Equal(2, Assert.Single(marks).PageIndex);
        Assert.Equal("Volume 01", store.AllBookmarks[0].BookRef.InnerPath);
        var series = store.GetSeries($"series:{SampleSource}")!;
        Assert.Equal(ReadingDirection.RightToLeft, series.Direction);
        Assert.Null(series.Layout);
        Assert.Equal("ko", series.TranslationTarget);
        Assert.Equal(ExportFormat.Cbz, store.ExportOptions!.Format);
        Assert.Equal(UntranslatedPolicy.TranslateFirst, store.ExportOptions.Untranslated);
        Assert.Null(store.LastLibraryRoot);
    }

    [Fact]
    public void RoundTrip_UsesSchemaNames()
    {
        var (t, paths, diag) = Env();
        using var scope = t;
        File.Copy(ExamplePath, paths.StateFile);
        using (var store = new StateStore(paths, diag))
        {
            store.UpdateSettings(s => { s.Direction = ReadingDirection.RightToLeft; s.Layout = ReadingLayout.Double; s.TranslationTargetLanguage = "zh-Hans"; });
            store.ExportOptions = new Panely.Core.Settings.ExportOptions { Target = "ja", Format = ExportFormat.Folder, Untranslated = UntranslatedPolicy.ExportAsIs };
            store.Flush();
        }
        var json = File.ReadAllText(paths.StateFile);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var settings = root.GetProperty("settings");
        Assert.Equal("rightToLeft", settings.GetProperty("direction").GetString());
        Assert.Equal("double", settings.GetProperty("layout").GetString());
        Assert.Equal("fitScreen", settings.GetProperty("fitMode").GetString());
        Assert.Equal("system", settings.GetProperty("appLanguage").GetString());
        Assert.Equal("info", settings.GetProperty("logLevel").GetString());
        Assert.Equal("zh-Hans", settings.GetProperty("translationTargetLanguage").GetString());
        Assert.Equal("folder", root.GetProperty("exportOptions").GetProperty("format").GetString());
        Assert.Equal("exportAsIs", root.GetProperty("exportOptions").GetProperty("untranslated").GetString());
        Assert.Equal("archive", root.GetProperty("sources")[0].GetProperty("kind").GetString());
        Assert.EndsWith("Z", root.GetProperty("recents")[0].GetProperty("openedAt").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastLibraryRoot").ValueKind);
        foreach (var name in new[] { "sources", "positions", "progress", "recents", "favorites", "bookmarks", "seriesPreferences", "exportHistory", "exportOptions", "exportDestination", "lastLibraryRoot" })
            Assert.True(root.TryGetProperty(name, out _), name);

        using var reloaded = new StateStore(paths, diag);
        Assert.Equal(ReadingDirection.RightToLeft, reloaded.Settings.Direction);
        Assert.Equal(2, reloaded.GetPosition(SampleKey));
        Assert.True(File.Exists(paths.StateFile + ".bak"));
    }

    [Fact]
    public void PositionDebounce_PendingReadable_FlushWrites()
    {
        var (t, paths, diag) = Env();
        using var scope = t;
        using var store = new StateStore(paths, diag, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        store.SetPosition("source:x", 7);
        Assert.Equal(7, store.GetPosition("source:x"));
        Assert.False(File.Exists(paths.StateFile));
        store.Flush();
        Assert.Contains("\"source:x\"", File.ReadAllText(paths.StateFile));
    }

    [Fact]
    public void Limits_PositionsRecentsBookmarksSeries()
    {
        var (t, paths, diag) = Env();
        using var scope = t;
        using var store = new StateStore(paths, diag, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        for (var i = 0; i < StateStore.MaxPositions + 5; i++) store.SetPosition($"source:{i}", i);
        Assert.Null(store.GetPosition("source:0"));
        Assert.Equal(StateStore.MaxPositions + 4, store.GetPosition($"source:{StateStore.MaxPositions + 4}"));

        for (var i = 0; i < 51; i++)
            store.TouchRecent(new SourceRef { Id = Guid.NewGuid(), Path = $@"C:\x{i}", Kind = SourceKind.Folder, Title = $"t{i}" });
        Assert.Equal(StateStore.MaxRecents, store.Recents.Count);
        Assert.Equal("t50", store.Recents[0].Title);

        var book = new BookRef(Guid.NewGuid(), null);
        for (var p = 0; p < StateStore.MaxBookmarksPerBook + 1; p++) Assert.True(store.ToggleBookmark(book, "B", p));
        var marks = store.GetBookmarks(book.Key);
        Assert.Equal(StateStore.MaxBookmarksPerBook, marks.Count);
        Assert.DoesNotContain(marks, m => m.PageIndex == 0); // 가장 오래된 것 제거
        Assert.False(store.ToggleBookmark(book, "B", 10)); // 중복 toggle = 제거
        Assert.DoesNotContain(store.GetBookmarks(book.Key), m => m.PageIndex == 10);

        var first = new BookRef(Guid.NewGuid(), "inner");
        store.ToggleBookmark(first, "first", 1);
        for (var i = 0; i < StateStore.MaxBookmarkedBooks; i++) store.ToggleBookmark(new BookRef(Guid.NewGuid(), null), "b", 0);
        Assert.Equal(StateStore.MaxBookmarkedBooks, store.AllBookmarks.Count);
        Assert.Empty(store.GetBookmarks(first.Key)); // 오래된 책부터 제거

        for (var i = 0; i < StateStore.MaxSeries + 3; i++) store.UpdateSeries($"series:{i}", s => s.Layout = ReadingLayout.Double);
        Assert.Null(store.GetSeries("series:0"));
        Assert.NotNull(store.GetSeries($"series:{StateStore.MaxSeries + 2}"));
    }

    [Fact]
    public void RecentsRemoval_RemovesProgressOnly()
    {
        var (t, paths, diag) = Env();
        using var scope = t;
        using var store = new StateStore(paths, diag);
        var src = new SourceRef { Id = Guid.NewGuid(), Path = @"C:\a.cbz", Kind = SourceKind.Archive, Title = "a" };
        var key = BookKeys.Make(src.Id, "inner");
        store.TouchRecent(src);
        store.SetProgress(key, 3, 10, false);
        store.SetPosition(key, 3);
        store.ToggleBookmark(new BookRef(src.Id, "inner"), "a", 3);
        store.RemoveRecent(src.Id);
        Assert.Empty(store.Recents);
        Assert.Null(store.GetProgress(key));
        Assert.Single(store.GetBookmarks(key));

        store.RestartBook(key, 10);
        Assert.Equal(0, store.GetPosition(key));
        Assert.False(store.GetProgress(key)!.Finished);
    }

    [Fact]
    public void Favorites_ToggleAndExportHistoryCap()
    {
        var (t, paths, diag) = Env();
        using var scope = t;
        using var store = new StateStore(paths, diag);
        var book = new BookRef(Guid.NewGuid(), "v1");
        Assert.True(store.ToggleFavorite(book, "Book"));
        Assert.True(store.IsFavorite(book.Key));
        Assert.False(store.ToggleFavorite(book, "Book"));
        Assert.Empty(store.Favorites);
        for (var i = 0; i < 55; i++)
            store.AddExport(new ExportRecord(Guid.NewGuid(), $"t{i}", "ko", ExportFormat.Cbz, $@"C:\o{i}.cbz", 1, 1, DateTimeOffset.UtcNow));
        Assert.Equal(StateStore.MaxExportHistory, store.ExportHistory.Count);
        Assert.Equal("t54", store.ExportHistory[0].Title);
    }

    [Fact]
    public void CorruptState_IsQuarantined_DefaultsUsed()
    {
        var (t, paths, diag) = Env();
        using var scope = t;
        File.WriteAllText(paths.StateFile, "{ this is not json");
        using var store = new StateStore(paths, diag);
        Assert.NotNull(store.LoadProblem);
        Assert.Equal(ReadingLayout.Single, store.Settings.Layout);
        var quarantined = Directory.GetFiles(paths.Data, "state.corrupt-*.json");
        Assert.Single(quarantined);
        Assert.Equal("{ this is not json", File.ReadAllText(quarantined[0]));
        Assert.Equal("{ this is not json", File.ReadAllText(paths.StateFile)); // 원본을 바로 덮지 않음
        Assert.NotEmpty(diag.RecentErrors);
    }

    [Fact]
    public void CorruptState_FallsBackToBackup()
    {
        var (t, paths, diag) = Env();
        using var scope = t;
        File.Copy(ExamplePath, paths.StateFile + ".bak");
        File.WriteAllText(paths.StateFile, "[1,2");
        using var store = new StateStore(paths, diag);
        Assert.Equal(2, store.GetPosition(SampleKey));
    }

    [Fact]
    public void FutureSchema_IsReadOnly()
    {
        var (t, paths, diag) = Env();
        using var scope = t;
        var future = File.ReadAllText(ExamplePath).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");
        File.WriteAllText(paths.StateFile, future);
        using var store = new StateStore(paths, diag);
        Assert.True(store.IsReadOnly);
        store.SetPosition("source:new", 1);
        store.UpdateSettings(s => s.Layout = ReadingLayout.Vertical);
        store.Flush();
        Assert.Equal(future, File.ReadAllText(paths.StateFile));
    }

    [Fact]
    public void SourceIdentity_SamePathReturnsSameId_MovedFileUpdatesPath()
    {
        var (t, paths, diag) = Env();
        using var scope = t;
        using var store = new StateStore(paths, diag);
        var a = t.P("lib", "Book.cbz");
        Directory.CreateDirectory(t.P("lib"));
        File.WriteAllText(a, "x");
        var s1 = store.GetOrCreateSource(a, SourceKind.Archive, "Book");
        var s2 = store.GetOrCreateSource(a.ToUpperInvariant(), SourceKind.Archive, "Book");
        Assert.Equal(s1.Id, s2.Id);
        Assert.NotNull(s1.FileId);

        var b = t.P("lib", "Moved.cbz");
        File.Move(a, b);
        var s3 = store.GetOrCreateSource(b, SourceKind.Archive, "Moved");
        Assert.Equal(s1.Id, s3.Id);
        Assert.Equal(b, s3.Path);
        Assert.Equal(b, store.FindSource(s1.Id)!.Path);
    }

    [Fact]
    public void Startup_RemovesUnreferencedSources()
    {
        var (t, paths, diag) = Env();
        using var scope = t;
        Guid orphan;
        using (var store = new StateStore(paths, diag))
        {
            orphan = store.GetOrCreateSource(t.P("orphan"), SourceKind.Folder, "o").Id;
            var kept = store.GetOrCreateSource(t.P("kept"), SourceKind.Folder, "k");
            store.TouchRecent(kept);
            store.Flush();
        }
        using var reloaded = new StateStore(paths, diag);
        Assert.Null(reloaded.FindSource(orphan));
        Assert.Single(reloaded.Sources);
    }
}
