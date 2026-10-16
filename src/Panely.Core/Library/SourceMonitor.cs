using Panely.Core.Abstractions;
using Panely.Core.Sources;

namespace Panely.Core.Library;

/// <summary>
/// 열린 책 소스 변경 감시(04-files.md). 아카이브 파일 또는 폴더와 그 이미지의 추가·삭제·수정을
/// rename/delete burst debounce 후 다시 확인해 알린다. 드라이브 일시 분리는 삭제로 보지 않는다.
/// 자동 페이지 이동은 하지 않는다(배너만).
/// </summary>
public sealed class SourceMonitor : ISourceMonitor
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(700);

    private readonly object _gate = new();
    private readonly Timer _timer;
    private FileSystemWatcher? _watcher;
    private string? _path;
    private bool _isFolder;
    private string? _baseline;
    private bool _disposed;

    public SourceMonitor() : this(DefaultDebounce) { }

    internal SourceMonitor(TimeSpan debounce)
    {
        Debounce = debounce;
        _timer = new Timer(_ => Verify(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal TimeSpan Debounce { get; }

    public event EventHandler? SourceChanged;

    public void Watch(OpenedBook? book) => Watch(book?.WatchPath);

    /// <summary>경로를 직접 감시(테스트·내부용).</summary>
    public void Watch(string? path)
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _path = path;
            _baseline = null;
            if (path is null || _disposed) return;
            try
            {
                _isFolder = Directory.Exists(path);
                _baseline = Signature(path, _isFolder);
                var w = _isFolder
                    ? new FileSystemWatcher(path) { IncludeSubdirectories = false }
                    : new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path));
                w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName;
                w.Changed += (_, _) => Schedule();
                w.Created += (_, _) => Schedule();
                w.Deleted += (_, _) => Schedule();
                w.Renamed += (_, _) => Schedule();
                w.Error += (_, _) => Schedule();
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                _watcher = null;
            }
        }
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed || _path is null) return;
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Verify()
    {
        string? path;
        bool isFolder;
        string? baseline;
        lock (_gate)
        {
            path = _path;
            isFolder = _isFolder;
            baseline = _baseline;
        }
        if (path is null || baseline is null) return;

        // 드라이브/네트워크 root가 없으면 일시 끊김: 알리지 않고 기다린다
        if (BookOpener.ClassifyMissing(path) == OpenErrorKind.TemporarilyUnavailable && !Exists(path, isFolder)) return;

        var current = Signature(path, isFolder);
        if (current == baseline) return;
        lock (_gate)
        {
            if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return;
            _baseline = current;
        }
        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool Exists(string path, bool isFolder) => isFolder ? Directory.Exists(path) : File.Exists(path);

    /// <summary>아카이브: 크기+mtime. 폴더: 이미지 파일 목록과 각각의 크기+mtime.</summary>
    internal static string Signature(string path, bool isFolder)
    {
        try
        {
            if (!isFolder)
            {
                var fi = new FileInfo(path);
                return fi.Exists ? $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}" : "missing";
            }
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) return "missing";
            var parts = dir.EnumerateFiles()
                .Where(f => !FolderAnalyzer.IsHidden(f) && SupportedFormats.IsImage(f.Name))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}");
            return string.Join('\n', parts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
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

/// <summary>최근/이어보기 가용성 확인. UI 계산 때마다 아카이브를 열지 않는다.</summary>
public sealed class AvailabilityChecker : IAvailabilityChecker
{
    public Task<SourceAvailability> CheckAsync(Domain.Model.SourceRef source, string? innerPath, CancellationToken cancellationToken)
        => Task.Run(() => Check(source), cancellationToken);

    public static SourceAvailability Check(Domain.Model.SourceRef source)
    {
        try
        {
            var exists = source.Kind == Domain.Model.SourceKind.Folder ? Directory.Exists(source.Path) : File.Exists(source.Path);
            if (exists) return SourceAvailability.Available;
            return BookOpener.ClassifyMissing(source.Path) == OpenErrorKind.TemporarilyUnavailable
                ? SourceAvailability.TemporarilyUnavailable
                : SourceAvailability.InvalidReference;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return SourceAvailability.Unknown;
        }
    }
}
