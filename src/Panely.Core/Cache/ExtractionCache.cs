using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Panely.Core.Abstractions;

namespace Panely.Core.Cache;

/// <summary>캐시 entry 사용권. Dispose하면 lease 해제(LRU/Clear 대상이 됨).</summary>
public sealed class CacheLease : IDisposable
{
    private readonly ExtractionCache _owner;
    private int _disposed;

    internal CacheLease(ExtractionCache owner, string key, string contentPath)
    {
        _owner = owner;
        Key = key;
        ContentPath = contentPath;
    }

    public string Key { get; }
    /// <summary>해제된 내용 루트 디렉터리.</summary>
    public string ContentPath { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Release(Key);
    }
}

/// <summary>
/// 영속 해제 캐시(04-files.md). 예산 10GiB, 최근 접근 LRU.
/// key = SHA-256(`정규화경로|크기|mtimeMillis`) 전체 hex, 형식 버전 폴더(v1) 아래 저장.
/// 완성된 entry만 히트로 인정하고, 새 해제는 독립 staging에서 끝낸 뒤 원자적으로 publish한다.
/// lease가 있는 entry는 LRU와 수동 Clear 모두에서 제외한다.
/// 디렉터리 구조: extraction/v1/entries/{key}/content/, extraction/v1/entries/{key}/entry.json, extraction/v1/staging/,
/// extraction/v1/trash/(지우는 중인 폴더. 잠금 안에서는 여기로 옮기기만 하고 삭제는 잠금 밖에서 한다, ADR 0009).
/// </summary>
public sealed class ExtractionCache : IExtractionCache
{
    public const int FormatVersion = 1;
    public const long DefaultLimitBytes = 10L * 1024 * 1024 * 1024;

    private readonly IDiagnostics _diagnostics;
    private readonly string _entriesDir;
    private readonly string _stagingDir;
    private readonly string _trashDir;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new(StringComparer.Ordinal);

    public ExtractionCache(AppPaths paths, IDiagnostics diagnostics) : this(paths, diagnostics, DefaultLimitBytes) { }

    public ExtractionCache(AppPaths paths, IDiagnostics diagnostics, long limitBytes)
    {
        _diagnostics = diagnostics;
        LimitBytes = limitBytes;
        var root = Path.Combine(paths.ExtractionCache, $"v{FormatVersion}");
        _entriesDir = Path.Combine(root, "entries");
        _stagingDir = Path.Combine(root, "staging");
        _trashDir = Path.Combine(root, "trash");
        Directory.CreateDirectory(_entriesDir);
        Directory.CreateDirectory(_stagingDir);
        Directory.CreateDirectory(_trashDir);
    }

    public long LimitBytes { get; }

    /// <summary>lease 해제 뒤의 예산 정리를 worker에서 한다. 앱은 켜고(책을 닫는 UI 스레드를 막지 않게), 기본은 호출 스레드에서 끝낸다.</summary>
    public bool EnforceInBackground { get; init; }

    /// <summary>현재 로딩 중이면 UI가 Clear를 비활성화한다(참고용 플래그).</summary>
    public bool IsBusy => _keyLocks.Values.Any(s => s.CurrentCount == 0);

    /// <summary>입력 identity: 정규화 경로·파일 크기·mtime milliseconds.</summary>
    public static string ComputeKey(string normalizedPath, long size, long mtimeMillis)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{normalizedPath}|{size}|{mtimeMillis}"));
        return Convert.ToHexStringLower(bytes);
    }

    public static string ComputeKey(string archivePath)
    {
        var info = new FileInfo(archivePath);
        var normalized = Path.GetFullPath(archivePath);
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        return ComputeKey(normalized, info.Length, mtime);
    }

    /// <summary>완성된 entry가 있으면 lease를 잡아 돌려준다.</summary>
    public CacheLease? TryAcquire(string key)
    {
        lock (_gate)
        {
            var dir = EntryDir(key);
            var meta = ReadMeta(dir);
            if (meta is null || !Directory.Exists(Path.Combine(dir, "content"))) return null;
            meta.LastAccess = DateTimeOffset.UtcNow;
            WriteMeta(dir, meta);
            AddLease(key);
            return new CacheLease(this, key, Path.Combine(dir, "content"));
        }
    }

    /// <summary>
    /// entry를 찾거나 staging에서 populate한 뒤 publish한다. 같은 key의 동시 해제는 하나로 직렬화한다.
    /// populate가 실패/취소되면 미완료 staging만 정리한다.
    /// </summary>
    public async Task<CacheLease> GetOrCreateAsync(string key, Func<string, CancellationToken, long> populate, CancellationToken cancellationToken)
    {
        var keyLock = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hit = TryAcquire(key);
            if (hit is not null) return hit;

            var staging = Path.Combine(_stagingDir, $"{key}-{Guid.NewGuid():N}");
            var content = Path.Combine(staging, "content");
            Directory.CreateDirectory(content);
            long bytes;
            try
            {
                bytes = await Task.Run(() => populate(content, cancellationToken), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                WriteMeta(staging, new EntryMeta { Bytes = bytes, LastAccess = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow });
            }
            catch
            {
                TryDeleteDirectory(staging);
                throw;
            }

            CacheLease lease;
            bool shared;
            lock (_gate)
            {
                var dir = EntryDir(key);
                // entry.json을 잠깐 못 읽어 여기까지 왔을 수 있다. 다른 책이 읽는 중인 entry는 지우지 않고 함께 쓴다(같은 key는 같은 내용).
                shared = _leases.ContainsKey(key) && Directory.Exists(Path.Combine(dir, "content"));
                if (!shared)
                {
                    if (Directory.Exists(dir)) TryDeleteDirectory(dir); // 불완전한 이전 entry
                    Directory.Move(staging, dir);
                }
                AddLease(key);
                lease = new CacheLease(this, key, Path.Combine(dir, "content"));
            }
            if (shared)
            {
                TryDeleteDirectory(staging);
                return lease;
            }
            _diagnostics.Log(Domain.Model.LogLevel.Info, LogCategory.Cache, $"Extraction cache entry published ({bytes} bytes)");
            Enforce();
            return lease;
        }
        finally
        {
            keyLock.Release();
        }
    }

    public CacheUsage GetUsage()
    {
        lock (_gate)
        {
            long total = 0, clearable = 0;
            foreach (var (key, meta) in EnumerateEntries())
            {
                total += meta.Bytes;
                if (!_leases.ContainsKey(key)) clearable += meta.Bytes;
            }
            return new CacheUsage(total, clearable, LimitBytes);
        }
    }

    public Task<long> ClearAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        long removed = 0;
        var trash = new List<string>();
        var entries = EnumerateEntries().ToList();
        lock (_gate)
        {
            foreach (var (key, meta) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_leases.ContainsKey(key)) continue;
                if (_keyLocks.TryGetValue(key, out var l) && l.CurrentCount == 0) continue;
                if (DetachEntry(key) is { } detached)
                {
                    trash.Add(detached);
                    removed += meta.Bytes;
                }
            }
        }
        foreach (var dir in trash) TryDeleteDirectory(dir);
        _diagnostics.Log(Domain.Model.LogLevel.Info, LogCategory.Cache, $"Extraction cache cleared ({removed} bytes)");
        return removed;
    }, cancellationToken);

    /// <summary>시작할 때 호출: 지난 실행의 staging을 해제 작업과 겹치지 않는 곳으로 옮긴다(이름 변경이라 빠르다).</summary>
    public void DetachStaleStaging()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(_stagingDir))
            {
                try { Directory.Move(dir, Path.Combine(_trashDir, $"staging-{Guid.NewGuid():N}")); }
                catch (IOException) { TryDeleteDirectory(dir); }
                catch (UnauthorizedAccessException) { TryDeleteDirectory(dir); }
            }
        }
        catch (IOException ex)
        {
            _diagnostics.Log(Domain.Model.LogLevel.Warning, LogCategory.Cache, $"Orphan cleanup failed: {ex.GetType().Name}");
        }
    }

    public void CleanupOrphans()
    {
        DetachStaleStaging();
        FinishOrphanCleanup();
    }

    /// <summary>
    /// 치워 둔 폴더·미완료 entry·이전 형식 폴더를 지우고 예산을 맞춘다. 파일이 많으면 오래 걸리므로 worker에서 호출할 수 있다.
    /// 새 entry는 entry.json을 쓴 뒤 원자적으로 publish되므로 진행 중인 해제와 겹쳐도 지우지 않는다.
    /// </summary>
    public void FinishOrphanCleanup()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(_trashDir)) TryDeleteDirectory(dir);
            // meta 없는 entry는 미완료로 간주
            foreach (var dir in Directory.GetDirectories(_entriesDir))
            {
                // 잠깐 못 읽는 것은 미완료가 아니다
                if (ReadMeta(dir, out var unreadable) is not null || unreadable) continue;
                string? detached;
                lock (_gate)
                {
                    var key = Path.GetFileName(dir);
                    if (_leases.ContainsKey(key)) continue;
                    if (_keyLocks.TryGetValue(key, out var l) && l.CurrentCount == 0) continue;
                    detached = DetachEntry(key);
                }
                if (detached is not null) TryDeleteDirectory(detached);
            }
            // 이전 형식 버전 폴더
            var parent = Path.GetDirectoryName(Path.GetDirectoryName(_entriesDir)!)!;
            foreach (var dir in Directory.GetDirectories(parent))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('v') && name != $"v{FormatVersion}") TryDeleteDirectory(dir);
            }
        }
        catch (IOException ex)
        {
            _diagnostics.Log(Domain.Model.LogLevel.Warning, LogCategory.Cache, $"Orphan cleanup failed: {ex.GetType().Name}");
        }
        Enforce();
    }

    /// <summary>LRU: 예산을 넘으면 lease 없는 오래된 entry부터 지운다. lease 때문에 잠깐 초과할 수 있다.</summary>
    public void Enforce()
    {
        // 파일 읽기와 삭제는 잠금 밖에서 한다. 잠금 안에서는 이름만 바꿔 entry를 떼어 낸다.
        var entries = EnumerateEntries().ToList();
        var total = entries.Sum(e => e.Meta.Bytes);
        if (total <= LimitBytes) return;
        var trash = new List<string>();
        lock (_gate)
        {
            foreach (var (key, meta) in entries.OrderBy(e => e.Meta.LastAccess))
            {
                if (total <= LimitBytes) break;
                if (_leases.ContainsKey(key)) continue;
                if (_keyLocks.TryGetValue(key, out var l) && l.CurrentCount == 0) continue;
                if (DetachEntry(key) is { } detached)
                {
                    trash.Add(detached);
                    total -= meta.Bytes;
                }
            }
        }
        foreach (var dir in trash) TryDeleteDirectory(dir);
    }

    internal void Release(string key)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(key, out var n))
            {
                if (n <= 1) _leases.Remove(key); else _leases[key] = n - 1;
            }
        }
        if (!EnforceInBackground)
        {
            Enforce();
            return;
        }
        _ = Task.Run(() =>
        {
            try { Enforce(); }
            catch (Exception ex) { _diagnostics.Log(Domain.Model.LogLevel.Warning, LogCategory.Cache, $"Cache cleanup failed: {ex.GetType().Name}"); }
        });
    }

    /// <summary>_gate 안에서 호출. entry를 휴지통 폴더로 옮겨 떼어 낸다. 사용 중이라 옮기지 못하면 null.</summary>
    private string? DetachEntry(string key)
    {
        var target = Path.Combine(_trashDir, $"{key}-{Guid.NewGuid():N}");
        try
        {
            Directory.Move(EntryDir(key), target);
            return target;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public bool IsLeased(string key)
    {
        lock (_gate) return _leases.ContainsKey(key);
    }

    private void AddLease(string key) => _leases[key] = _leases.TryGetValue(key, out var n) ? n + 1 : 1;

    private string EntryDir(string key) => Path.Combine(_entriesDir, key);

    private IEnumerable<(string Key, EntryMeta Meta)> EnumerateEntries()
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(_entriesDir); }
        catch (IOException) { yield break; }
        foreach (var dir in dirs)
        {
            var meta = ReadMeta(dir);
            if (meta is not null) yield return (Path.GetFileName(dir), meta);
        }
    }

    private static EntryMeta? ReadMeta(string dir) => ReadMeta(dir, out _);

    /// <param name="dir">entry 폴더.</param>
    /// <param name="unreadable">entry.json이 있는데 입출력 오류로 읽지 못했으면 true(내용은 멀쩡할 수 있다).</param>
    private static EntryMeta? ReadMeta(string dir, out bool unreadable)
    {
        unreadable = false;
        try
        {
            var file = Path.Combine(dir, "entry.json");
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<EntryMeta>(File.ReadAllText(file));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable = true;
            return null;
        }
    }

    private static void WriteMeta(string dir, EntryMeta meta)
    {
        try
        {
            var file = Path.Combine(dir, "entry.json");
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(meta));
            File.Move(tmp, file, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed class EntryMeta
    {
        public long Bytes { get; set; }
        public DateTimeOffset LastAccess { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
