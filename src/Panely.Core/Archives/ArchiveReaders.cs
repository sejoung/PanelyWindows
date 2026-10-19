using System.Buffers.Binary;
using System.IO.Compression;
using Panely.Core.Abstractions;
using Panely.Core.Localization;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Readers;

namespace Panely.Core.Archives;

public enum ArchiveFormat { Unknown, Zip, Rar }

/// <summary>아카이브 entry 정보. Path는 '/' 구분 정규화 내부 경로.</summary>
public sealed record ArchiveEntryInfo(string Path, long Size, DateTimeOffset? Modified, bool IsEncrypted, uint? Crc32);

/// <summary>
/// ArchiveReader 계약(04-files.md). 한 아카이브당 접근을 직렬화한다.
/// listEntries / readEntry / readPrefix. 디렉터리 entry는 제외한다.
/// </summary>
public interface IArchiveReader : IDisposable
{
    string SourcePath { get; }
    ArchiveFormat Format { get; }
    bool IsSolid { get; }
    IReadOnlyList<ArchiveEntryInfo> Entries { get; }
    ArchiveEntryInfo? Find(string path);
    Task<byte[]> ReadEntryAsync(string path, CancellationToken cancellationToken);
    Task<byte[]> ReadPrefixAsync(string path, int maxBytes, CancellationToken cancellationToken);
    /// <summary>모든 entry를 순서대로 연다(solid RAR에 효율적). 콜백 안에서 stream을 끝까지 읽지 않아도 된다.</summary>
    void ForEachEntry(Action<ArchiveEntryInfo, Stream> visitor, CancellationToken cancellationToken);
}

public static class ArchiveSniffer
{
    private static readonly byte[] ZipLocal = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEmpty = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] Rar = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07];

    public static ArchiveFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(ZipLocal) || header.StartsWith(ZipEmpty)) return ArchiveFormat.Zip;
        if (header.StartsWith(Rar)) return ArchiveFormat.Rar;
        return ArchiveFormat.Unknown;
    }

    /// <summary>실제 헤더 우선. 판별 불가면 확장자로 선택한다.</summary>
    public static ArchiveFormat DetectFile(string path)
    {
        Span<byte> buffer = stackalloc byte[8];
        var read = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch (FileNotFoundException) { throw; }
        catch (DirectoryNotFoundException) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (IOException) { read = 0; }
        var detected = Detect(buffer[..read]);
        if (detected != ArchiveFormat.Unknown) return detected;
        return SupportedFormats.ExtensionOf(path) switch
        {
            "zip" or "cbz" => ArchiveFormat.Zip,
            "rar" or "cbr" => ArchiveFormat.Rar,
            _ => ArchiveFormat.Unknown,
        };
    }

    public static string NormalizeEntryPath(string key) => key.Replace('\\', '/').TrimStart('/');
}

public static class ArchiveReaderFactory
{
    public static IArchiveReader Open(string path, ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => new ZipArchiveReader(path),
        ArchiveFormat.Rar => new RarArchiveReader(path),
        _ => throw new BookOpenException(OpenErrorKind.Unsupported, Loc.T("Unsupported file type: %@", System.IO.Path.GetFileName(path))),
    };
}

/// <summary>
/// 같은 아카이브를 여러 핸들로 열어 두고 빌려 쓴다. 핸들 하나는 한 번에 한 작업만 쓰므로
/// 여러 쪽의 압축 해제·CRC 확인이 병렬로 진행된다(핸들은 필요할 때만 최대 개수까지 늘린다).
/// </summary>
internal sealed class ArchiveHandlePool<T> : IDisposable where T : class, IDisposable
{
    private readonly Func<T> _create;
    private readonly SemaphoreSlim _slots;
    private readonly Stack<T> _idle = new();
    private bool _disposed;

    public ArchiveHandlePool(T first, Func<T> create)
    {
        _create = create;
        MaxHandles = Math.Clamp(Environment.ProcessorCount, 2, 6);
        _slots = new SemaphoreSlim(MaxHandles, MaxHandles);
        _idle.Push(first);
    }

    public int MaxHandles { get; }

    public async Task<T> RentAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return TakeOrCreate();
    }

    public T Rent(CancellationToken cancellationToken)
    {
        _slots.Wait(cancellationToken);
        return TakeOrCreate();
    }

    private T TakeOrCreate()
    {
        lock (_idle)
        {
            if (_disposed)
            {
                // 다음 대기자도 깨어나 같은 예외를 받게 자리를 돌려준다
                _slots.Release();
                throw new ObjectDisposedException(nameof(ArchiveHandlePool<T>));
            }
            if (_idle.TryPop(out var handle)) return handle;
        }
        try
        {
            return _create();
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    public void Return(T handle)
    {
        lock (_idle)
        {
            if (!_disposed) _idle.Push(handle);
            else handle.Dispose();
        }
        _slots.Release();
    }

    /// <summary>쉬는 핸들은 바로, 빌려 간 핸들은 Return에서 닫는다.</summary>
    public void Dispose()
    {
        lock (_idle)
        {
            if (_disposed) return;
            _disposed = true;
            while (_idle.TryPop(out var handle)) handle.Dispose();
        }
    }
}

/// <summary>System.IO.Compression 기반 ZIP reader. entry 단위 streaming, 전체 읽기 시 CRC 확인.</summary>
public sealed class ZipArchiveReader : IArchiveReader
{
    private sealed class Handle(FileStream stream, ZipArchive zip) : IDisposable
    {
        public ZipArchive Zip => zip;

        public void Dispose()
        {
            zip.Dispose();
            stream.Dispose();
        }
    }

    /// <summary>정규화 경로 → (ZipArchive.Entries 안의 위치, 정보). 위치는 모든 핸들에서 같다.</summary>
    private readonly Dictionary<string, (int Index, ArchiveEntryInfo Info)> _byPath = new(StringComparer.Ordinal);
    private readonly ArchiveHandlePool<Handle> _pool;

    public ZipArchiveReader(string path)
    {
        SourcePath = path;
        var first = OpenHandle(path);
        try
        {
            var entries = first.Zip.Entries;
            var list = new List<ArchiveEntryInfo>(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var p = ArchiveSniffer.NormalizeEntryPath(e.FullName);
                if (p.Length == 0 || p.EndsWith('/')) continue;
                if (IsSymlink(e)) continue;
                var info = new ArchiveEntryInfo(p, e.Length, e.LastWriteTime, e.IsEncrypted, e.Crc32);
                if (!_byPath.TryAdd(p, (i, info))) continue;
                list.Add(info);
            }
            Entries = list;
        }
        catch
        {
            first.Dispose();
            throw;
        }
        _pool = new ArchiveHandlePool<Handle>(first, () => OpenHandle(path));
    }

    private static Handle OpenHandle(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
        try
        {
            return new Handle(stream, new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false));
        }
        catch (InvalidDataException ex)
        {
            stream.Dispose();
            throw new BookOpenException(OpenErrorKind.Corrupt, Loc.T("The archive could not be opened."), ex);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool IsSymlink(ZipArchiveEntry e)
    {
        // Unix 모드 상위 16비트의 S_IFLNK
        var mode = (e.ExternalAttributes >> 16) & 0xF000;
        return mode == 0xA000;
    }

    public string SourcePath { get; }
    public ArchiveFormat Format => ArchiveFormat.Zip;
    public bool IsSolid => false;
    public IReadOnlyList<ArchiveEntryInfo> Entries { get; }

    public ArchiveEntryInfo? Find(string path) => _byPath.TryGetValue(path, out var e) ? e.Info : null;

    public async Task<byte[]> ReadEntryAsync(string path, CancellationToken cancellationToken)
    {
        var index = IndexOf(path);
        var handle = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = handle.Zip.Entries[index];
            if (entry.IsEncrypted) throw new BookOpenException(OpenErrorKind.Encrypted, Loc.T("Password-protected archives are not supported."));
            try
            {
                using var s = entry.Open();
                var buffer = new byte[entry.Length];
                var read = await s.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                if (read != buffer.Length) throw new InvalidDataException("Truncated entry");
                if (Crc32.Compute(buffer) != entry.Crc32)
                    throw new BookOpenException(OpenErrorKind.Corrupt, Loc.T("The archive is damaged: %@", entry.Name));
                return buffer;
            }
            catch (InvalidDataException ex)
            {
                throw new BookOpenException(OpenErrorKind.Corrupt, Loc.T("The archive is damaged: %@", entry.Name), ex);
            }
        }
        finally
        {
            _pool.Return(handle);
        }
    }

    public async Task<byte[]> ReadPrefixAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        var index = IndexOf(path);
        var handle = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = handle.Zip.Entries[index];
            if (entry.IsEncrypted) throw new BookOpenException(OpenErrorKind.Encrypted, Loc.T("Password-protected archives are not supported."));
            try
            {
                using var s = entry.Open();
                var buffer = new byte[(int)Math.Min(maxBytes, entry.Length)];
                var read = await s.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                return read == buffer.Length ? buffer : buffer[..read];
            }
            catch (InvalidDataException ex)
            {
                throw new BookOpenException(OpenErrorKind.Corrupt, Loc.T("The archive is damaged: %@", entry.Name), ex);
            }
        }
        finally
        {
            _pool.Return(handle);
        }
    }

    public void ForEachEntry(Action<ArchiveEntryInfo, Stream> visitor, CancellationToken cancellationToken)
    {
        var handle = _pool.Rent(cancellationToken);
        try
        {
            foreach (var info in Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = handle.Zip.Entries[_byPath[info.Path].Index];
                if (entry.IsEncrypted) throw new BookOpenException(OpenErrorKind.Encrypted, Loc.T("Password-protected archives are not supported."));
                try
                {
                    using var s = entry.Open();
                    using var crc = new Crc32Stream(s, info.Crc32);
                    visitor(info, crc);
                    crc.DrainAndVerify(entry.Name);
                }
                catch (InvalidDataException ex)
                {
                    throw new BookOpenException(OpenErrorKind.Corrupt, Loc.T("The archive is damaged: %@", entry.Name), ex);
                }
            }
        }
        finally
        {
            _pool.Return(handle);
        }
    }

    private int IndexOf(string path)
        => _byPath.TryGetValue(path, out var e)
            ? e.Index
            : throw new BookOpenException(OpenErrorKind.InnerPathMissing, Loc.T("The archive has no entry named \"%@\".", path));

    public void Dispose() => _pool.Dispose();
}

/// <summary>
/// SharpCompress 기반 RAR4/5 reader. solid 아카이브는 임의 접근이 O(n)이므로
/// 호출자(BookOpener)가 캐시에 한 번 전체 해제해 디스크에서 읽는다.
/// </summary>
public sealed class RarArchiveReader : IArchiveReader
{
    private sealed class Handle(IArchive archive) : IDisposable
    {
        /// <summary>디렉터리 포함 전체 entry. 순서는 모든 핸들에서 같다.</summary>
        public IReadOnlyList<IArchiveEntry> All { get; } = archive.Entries.ToList();

        public IArchive Archive => archive;

        public void Dispose() => archive.Dispose();
    }

    /// <summary>정규화 경로 → (Handle.All 안의 위치, 정보).</summary>
    private readonly Dictionary<string, (int Index, ArchiveEntryInfo Info)> _byPath = new(StringComparer.Ordinal);
    private readonly ArchiveHandlePool<Handle> _pool;

    public RarArchiveReader(string path)
    {
        SourcePath = path;
        try
        {
            var first = OpenHandle(path);
            try
            {
                var list = new List<ArchiveEntryInfo>();
                for (var i = 0; i < first.All.Count; i++)
                {
                    var e = first.All[i];
                    if (e.IsDirectory || e.Key is null) continue;
                    if (!string.IsNullOrEmpty(e.LinkTarget)) continue;
                    var p = ArchiveSniffer.NormalizeEntryPath(e.Key);
                    if (p.Length == 0) continue;
                    DateTimeOffset? modified = e.LastModifiedTime is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null;
                    var info = new ArchiveEntryInfo(p, e.Size, modified, e.IsEncrypted, unchecked((uint)e.Crc));
                    if (!_byPath.TryAdd(p, (i, info))) continue;
                    list.Add(info);
                }
                Entries = list;
                IsSolid = first.Archive.IsSolid;
            }
            catch
            {
                first.Dispose();
                throw;
            }
            _pool = new ArchiveHandlePool<Handle>(first, () => OpenHandle(path));
        }
        catch (BookOpenException) { throw; }
        catch (Exception ex) when (IsPasswordError(ex))
        {
            throw new BookOpenException(OpenErrorKind.Encrypted, Loc.T("Password-protected archives are not supported."), ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException))
        {
            throw new BookOpenException(OpenErrorKind.Corrupt, Loc.T("The archive could not be opened."), ex);
        }
    }

    internal static bool IsPasswordError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var name = e.GetType().Name;
            if (name.Contains("Crypto", StringComparison.OrdinalIgnoreCase) || name.Contains("Password", StringComparison.OrdinalIgnoreCase)) return true;
            if (e.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public string SourcePath { get; }
    public ArchiveFormat Format => ArchiveFormat.Rar;
    public bool IsSolid { get; }
    public IReadOnlyList<ArchiveEntryInfo> Entries { get; }

    public ArchiveEntryInfo? Find(string path) => _byPath.TryGetValue(path, out var e) ? e.Info : null;

    private static Handle OpenHandle(string path) => new(RarArchive.OpenArchive(path, new ReaderOptions { LookForHeader = false }));

    public Task<byte[]> ReadEntryAsync(string path, CancellationToken cancellationToken)
        => ReadAsync(path, long.MaxValue, cancellationToken);

    public Task<byte[]> ReadPrefixAsync(string path, int maxBytes, CancellationToken cancellationToken)
        => ReadAsync(path, maxBytes, cancellationToken);

    private async Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        var index = Get(path);
        var handle = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Translate(path, () => ReadCore(handle.All[index], path, maxBytes, cancellationToken));
        }
        finally
        {
            _pool.Return(handle);
        }
    }

    public void ForEachEntry(Action<ArchiveEntryInfo, Stream> visitor, CancellationToken cancellationToken)
    {
        var handle = _pool.Rent(cancellationToken);
        try
        {
            var infoByPath = Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
            Translate(SourcePath, () =>
            {
                if (!IsSolid)
                {
                    foreach (var info in Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var e = handle.All[_byPath[info.Path].Index];
                        if (e.IsEncrypted) throw new BookOpenException(OpenErrorKind.Encrypted, Loc.T("Password-protected archives are not supported."));
                        using var s = e.OpenEntryStream();
                        visitor(info, s);
                        s.CopyTo(Stream.Null);
                    }
                    return 0;
                }
                // solid: 새 인스턴스로 처음부터 순차 해제(한 번에 전체)
                using var archive = RarArchive.OpenArchive(SourcePath, new ReaderOptions { LookForHeader = false });
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var e = reader.Entry;
                    if (e.IsDirectory || e.Key is null) continue;
                    if (e.IsEncrypted) throw new BookOpenException(OpenErrorKind.Encrypted, Loc.T("Password-protected archives are not supported."));
                    if (!infoByPath.TryGetValue(ArchiveSniffer.NormalizeEntryPath(e.Key), out var info)) continue;
                    using var s = reader.OpenEntryStream();
                    visitor(info, s);
                    // 남은 데이터 소비(CRC 검증은 SharpCompress가 스트림 끝에서 수행)
                    s.CopyTo(Stream.Null);
                }
                return 0;
            });
        }
        finally
        {
            _pool.Return(handle);
        }
    }

    /// <summary>
    /// 비solid는 entry stream을 바로 연다. solid는 앞 entry를 모두 풀어야 하므로 새 인스턴스로 순차 진행한다(O(n)).
    /// BookOpener는 solid 아카이브를 캐시에 한 번 해제하므로 이 경로는 드물다.
    /// </summary>
    private byte[] ReadCore(IArchiveEntry entry, string path, long maxBytes, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream(entry.Size is > 0 and < int.MaxValue ? (int)Math.Min(entry.Size, maxBytes) : 0);
        if (!IsSolid)
        {
            using var s = entry.OpenEntryStream();
            CopyWithCancel(s, ms, maxBytes, cancellationToken);
            return ms.ToArray();
        }
        using var archive = RarArchive.OpenArchive(SourcePath, new ReaderOptions { LookForHeader = false });
        using var reader = archive.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var e = reader.Entry;
            if (e.IsDirectory || e.Key is null || ArchiveSniffer.NormalizeEntryPath(e.Key) != path) continue;
            using var s = reader.OpenEntryStream();
            CopyWithCancel(s, ms, maxBytes, cancellationToken);
            return ms.ToArray();
        }
        throw new BookOpenException(OpenErrorKind.InnerPathMissing, Loc.T("The archive has no entry named \"%@\".", path));
    }

    private static T Translate<T>(string path, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (BookOpenException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsPasswordError(ex))
        {
            throw new BookOpenException(OpenErrorKind.Encrypted, Loc.T("Password-protected archives are not supported."), ex);
        }
        catch (Exception ex) when (ex is not (UnauthorizedAccessException or FileNotFoundException))
        {
            throw new BookOpenException(OpenErrorKind.Corrupt, Loc.T("The archive is damaged: %@", System.IO.Path.GetFileName(path)), ex);
        }
    }

    private static void CopyWithCancel(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (total < maxBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(buffer.Length, maxBytes - total);
            var n = source.Read(buffer, 0, want);
            if (n <= 0) break;
            destination.Write(buffer, 0, n);
            total += n;
        }
    }

    /// <summary>Handle.All 안의 위치.</summary>
    private int Get(string path)
    {
        if (!_byPath.TryGetValue(path, out var e))
            throw new BookOpenException(OpenErrorKind.InnerPathMissing, Loc.T("The archive has no entry named \"%@\".", path));
        if (e.Info.IsEncrypted) throw new BookOpenException(OpenErrorKind.Encrypted, Loc.T("Password-protected archives are not supported."));
        return e.Index;
    }

    public void Dispose() => _pool.Dispose();
}

/// <summary>CRC-32(IEEE) — ZIP entry 검증용. 쪽마다 entry 전체를 검증하므로 slicing-by-8로 8바이트씩 처리한다.</summary>
public static class Crc32
{
    private static readonly uint[][] Tables = BuildTables();

    private static uint[][] BuildTables()
    {
        var t = new uint[8][];
        for (var k = 0; k < 8; k++) t[k] = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[0][i] = c;
        }
        for (var i = 0; i < 256; i++)
            for (var k = 1; k < 8; k++)
                t[k][i] = (t[k - 1][i] >> 8) ^ t[0][t[k - 1][i] & 0xFF];
        return t;
    }

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var c = ~crc;
        uint[] t0 = Tables[0], t1 = Tables[1], t2 = Tables[2], t3 = Tables[3], t4 = Tables[4], t5 = Tables[5], t6 = Tables[6], t7 = Tables[7];
        while (data.Length >= 8)
        {
            var lo = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ c;
            var hi = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            c = t7[lo & 0xFF] ^ t6[(lo >> 8) & 0xFF] ^ t5[(lo >> 16) & 0xFF] ^ t4[lo >> 24]
                ^ t3[hi & 0xFF] ^ t2[(hi >> 8) & 0xFF] ^ t1[(hi >> 16) & 0xFF] ^ t0[hi >> 24];
            data = data[8..];
        }
        foreach (var b in data) c = t0[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);
}

/// <summary>읽은 데이터의 CRC를 누적하고 끝에서 기대값과 비교하는 읽기 전용 stream.</summary>
internal sealed class Crc32Stream(Stream inner, uint? expected) : Stream
{
    private uint _crc;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        _crc = Crc32.Append(_crc, buffer.AsSpan(offset, n));
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = inner.Read(buffer);
        _crc = Crc32.Append(_crc, buffer[..n]);
        return n;
    }

    public void DrainAndVerify(string name)
    {
        var buffer = new byte[81920];
        while (Read(buffer, 0, buffer.Length) > 0) { }
        if (expected is { } e && e != _crc)
            throw new BookOpenException(OpenErrorKind.Corrupt, Loc.T("The archive is damaged: %@", name));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
