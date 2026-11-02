using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Panely.App.Controls;
using Panely.Core.Abstractions;

namespace Panely.App.Tests;

public class LibrarySidebarTests
{
    private sealed class ManualLibrary : ILibraryService
    {
        public Dictionary<string, TaskCompletionSource<IReadOnlyList<LibraryNode>>> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? RootPath => null;

        public event EventHandler<IReadOnlyList<string>>? DirectoriesChanged { add { } remove { } }
        public event EventHandler? WatchOverflowed { add { } remove { } }

        public void SetRoot(string? path, bool autoRefresh) { }
        public void SetAutoRefresh(bool enabled) { }
        public void NotifyManualRefresh() { }
        public void Dispose() { }

        public Task<IReadOnlyList<LibraryNode>> GetChildrenAsync(string folderPath, CancellationToken cancellationToken)
        {
            var source = new TaskCompletionSource<IReadOnlyList<LibraryNode>>();
            Pending[folderPath] = source;
            return source.Task;
        }

        public void Complete(string folder, string childName)
            => Pending[folder].SetResult([new LibraryNode(childName, Path.Combine(folder, childName), LibraryNodeKind.Archive, "cbz", false)]);
    }

    [Fact]
    public void A_slow_listing_from_the_old_root_does_not_overwrite_the_new_root() => UiHost.Run(async () =>
    {
        var library = new ManualLibrary();
        var sidebar = new LibrarySidebar();
        sidebar.AttachLibrary(library);

        sidebar.SetRoot(@"C:\RootA");
        sidebar.SetRoot(@"C:\RootB");
        library.Complete(@"C:\RootB", "b.cbz");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal([@"C:\RootB\b.cbz"], sidebar.RootItemPaths);

        library.Complete(@"C:\RootA", "a.cbz");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal([@"C:\RootB\b.cbz"], sidebar.RootItemPaths);

        // C → D → C처럼 같은 루트로 돌아와도 처음 C의 늦은 결과는 버린다
        sidebar.SetRoot(@"C:\RootC");
        var stale = library.Pending[@"C:\RootC"];
        sidebar.SetRoot(@"C:\RootD");
        sidebar.SetRoot(@"C:\RootC");
        stale.SetResult([new LibraryNode("old.cbz", @"C:\RootC\old.cbz", LibraryNodeKind.Archive, "cbz", false)]);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Empty(sidebar.RootItemPaths);
    });

    [Fact]
    public void Context_menu_state_follows_the_menus_of_the_sidebar() => UiHost.Run(() =>
    {
        var sidebar = new LibrarySidebar();
        var closed = 0;
        sidebar.ContextMenuClosed += () => closed++;
        var menu = sidebar.HeaderBar.ContextMenu;
        Assert.False(sidebar.HasOpenContextMenu);

        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
        Assert.True(sidebar.HasOpenContextMenu);

        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent, menu));
        Assert.False(sidebar.HasOpenContextMenu);
        Assert.Equal(1, closed);
        return Task.CompletedTask;
    });
}
