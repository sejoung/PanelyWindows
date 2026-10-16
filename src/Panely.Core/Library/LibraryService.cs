using Panely.Core.Abstractions;
using Panely.Core.Sources;
using Panely.Domain.Model;

namespace Panely.Core.Library;

/// <summary>
/// 파일 트리 지연 탐색과 자동 갱신(04-files.md). 재귀 변경 알림을 1.5초 debounce하고
/// cache·로그·무관한 파일을 걸러 바뀐 디렉터리만 알린다. 버퍼 overflow는 한 번 알리고 재스캔하지 않는다.
/// </summary>
public sealed class LibraryService : ILibraryService
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(6);

    private readonly IDiagnostics _diagnostics;
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private readonly string _appDataRoot;
    private FileSystemWatcher? _watcher;
    private bool _autoRefresh;
    private bool _overflowReported;
    private DateTime _firstPendingAt;
    private bool _disposed;

    public LibraryService(IDiagnostics diagnostics) : this(diagnostics, DefaultDebounce, AppPaths.Default.Root) { }

    internal LibraryService(IDiagnostics diagnostics, TimeSpan debounce, string appDataRoot)
    {
        _diagnostics = diagnostics;
        Debounce = debounce;
        _appDataRoot = Path.GetFullPath(appDataRoot).TrimEnd('\\');
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal TimeSpan Debounce { get; }

    public string? RootPath { get; private set; }

    public event EventHandler<IReadOnlyList<string>>? DirectoriesChanged;
    public event EventHandler? WatchOverflowed;

    public void SetRoot(string? path, bool autoRefresh)
    {
        lock (_gate)
        {
            RootPath = path is null ? null : Path.GetFullPath(path).TrimEnd('\\');
            _autoRefresh = autoRefresh;
            _overflowReported = false;
            _pending.Clear();
            RestartWatcher();
        }
    }

    /// <summary>사용자가 수동 새로고침을 했으면 overflow 안내를 다시 할 수 있게 한다.</summary>
    public void NotifyManualRefresh()
    {
        lock (_gate) _overflowReported = false;
    }

    public void SetAutoRefresh(bool enabled)
    {
        lock (_gate)
        {
            if (_autoRefresh == enabled) return;
            _autoRefresh = enabled;
            RestartWatcher();
        }
    }

    private void RestartWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_disposed || !_autoRefresh || RootPath is null || !Directory.Exists(RootPath)) return;
        try
        {
            var w = new FileSystemWatcher(RootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => OnEvent(e.FullPath, null);
            w.Deleted += (_, e) => OnEvent(e.FullPath, null);
            w.Renamed += (_, e) => OnEvent(e.FullPath, e.OldFullPath);
            w.Error += (_, e) => OnError(e.GetException());
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _diagnostics.Log(LogLevel.Warning, LogCategory.Library, $"Library watcher could not start: {ex.GetType().Name}");
        }
    }

    /// <summary>관련 있는 변경인지: 폴더·아카이브(이름 변경/생성/삭제)만. 앱 데이터·숨김·임시 파일 제외.</summary>
    internal bool IsRelevant(string fullPath)
    {
        if (fullPath.StartsWith(_appDataRoot + "\\", StringComparison.OrdinalIgnoreCase) || string.Equals(fullPath, _appDataRoot, StringComparison.OrdinalIgnoreCase))
            return false;
        var root = RootPath;
        if (root is not null)
        {
            var rel = Path.GetRelativePath(root, fullPath);
            if (SupportedFormats.IsHiddenRelativePath(rel)) return false;
        }
        var name = Path.GetFileName(fullPath);
        if (name.Length == 0 || name.StartsWith('~') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            return false;
        if (SupportedFormats.IsArchive(name)) return true;
        if (SupportedFormats.IsImage(name)) return false; // 이미지는 트리에 표시하지 않는다
        try
        {
            if (Directory.Exists(fullPath)) return true;
            if (File.Exists(fullPath)) return false;
        }
        catch (IOException) { }
        // 삭제된 항목: 확장자가 없으면 폴더였을 가능성
        return Path.GetExtension(name).Length == 0;
    }

    private void OnEvent(string path, string? oldPath)
    {
        var relevant = IsRelevant(path) || (oldPath is not null && IsRelevant(oldPath));
        if (!relevant) return;
        lock (_gate)
        {
            if (_disposed) return;
            AddParent(path);
            if (oldPath is not null) AddParent(oldPath);
            var now = DateTime.UtcNow;
            if (_pending.Count == 1 || _firstPendingAt == default) _firstPendingAt = now;
            // trailing debounce, 단 연속 변경이 계속돼도 MaxDelay 안에는 알린다
            var due = now - _firstPendingAt >= MaxDelay ? TimeSpan.Zero : Debounce;
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    private void AddParent(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is not null) _pending.Add(parent.TrimEnd('\\'));
    }

    private void OnError(Exception ex)
    {
        if (ex is not InternalBufferOverflowException)
        {
            _diagnostics.Log(LogLevel.Warning, LogCategory.Library, $"Library watcher error: {ex.GetType().Name}");
            return;
        }
        bool report;
        lock (_gate)
        {
            report = !_overflowReported;
            _overflowReported = true;
        }
        if (report)
        {
            _diagnostics.Log(LogLevel.Warning, LogCategory.Library, "Library watcher buffer overflowed; manual refresh suggested");
            WatchOverflowed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Fire()
    {
        List<string> dirs;
        lock (_gate)
        {
            if (_pending.Count == 0 || _disposed) return;
            dirs = _pending.ToList();
            _pending.Clear();
            _firstPendingAt = default;
        }
        try { DirectoriesChanged?.Invoke(this, dirs); }
        catch (Exception ex) { _diagnostics.Log(LogLevel.Error, LogCategory.Library, $"Library change handler failed: {ex.GetType().Name}"); }
    }

    public Task<IReadOnlyList<LibraryNode>> GetChildrenAsync(string folderPath, CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<LibraryNode>>(() => ListChildren(folderPath, cancellationToken), cancellationToken);

    /// <summary>자연 정렬된 하위 폴더+아카이브(폴더와 아카이브 혼합 정렬).</summary>
    public static IReadOnlyList<LibraryNode> ListChildren(string folderPath, CancellationToken cancellationToken = default)
    {
        var result = new List<LibraryNode>();
        DirectoryInfo dir;
        try { dir = new DirectoryInfo(folderPath); }
        catch (ArgumentException) { return result; }
        if (!dir.Exists) return result;
        IEnumerable<FileSystemInfo> infos;
        try { infos = dir.EnumerateFileSystemInfos().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }
        foreach (var info in infos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FolderAnalyzer.IsHidden(info)) continue;
            if (info is DirectoryInfo d)
                result.Add(new LibraryNode(d.Name, d.FullName, LibraryNodeKind.Folder, string.Empty, HasNavigableChildren(d)));
            else if (SupportedFormats.IsArchive(info.Name))
                result.Add(new LibraryNode(Path.GetFileNameWithoutExtension(info.Name), info.FullName, LibraryNodeKind.Archive, SupportedFormats.ExtensionOf(info.Name), false));
        }
        var cmp = FolderAnalyzer.Comparer;
        result.Sort((a, b) => cmp.Compare(Path.GetFileName(a.Path), Path.GetFileName(b.Path)));
        return result;
    }

    private static bool HasNavigableChildren(DirectoryInfo d)
    {
        try
        {
            foreach (var info in d.EnumerateFileSystemInfos())
            {
                if (FolderAnalyzer.IsHidden(info)) continue;
                if (info is DirectoryInfo || SupportedFormats.IsArchive(info.Name)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
        }
        _timer.Dispose();
    }
}
