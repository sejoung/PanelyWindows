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

/// <summary>System.IO.Compression 기반 ZIP reader. entry 단위 streaming, 전체 읽기 시 CRC 확인.</summary>
public sealed class ZipArchiveReader : IArchiveReader
{
    private readonly FileStream _stream;
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _byPath = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ZipArchiveReader(string path)
    {
        SourcePath = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
        try
        {
            _zip = new ZipArchive(_stream, ZipArchiveMode.Read, leaveOpen: false);
            var list = new List<ArchiveEntryInfo>(_zip.Entries.Count);
            foreach (var e in _zip.Entries)
            {
                var p = ArchiveSniffer.NormalizeEntryPath(e.FullName);
                if (p.Length == 0 || p.EndsWith('/')) continue;
                if (IsSymlink(e)) continue;
                if (!_byPath.TryAdd(p, e)) continue;
                list.Add(new ArchiveEntryInfo(p, e.Length, e.LastWriteTime, e.IsEncrypted, e.Crc32));
            }
            Entries = list;
        }
        catch (InvalidDataException ex)
        {
            _stream.Dispose();
            throw new BookOpenException(OpenErrorKind.Corrupt, Loc.T("The archive could not be opened."), ex);
        }
        catch
        {
            _stream.Dispose();
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

    public ArchiveEntryInfo? Find(string path) => _byPath.TryGetValue(path, out var e)
        ? new ArchiveEntryInfo(path, e.Length, e.LastWriteTime, e.IsEncrypted, e.Crc32)
        : null;

    public async Task<byte[]> ReadEntryAsync(string path, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = Get(path);
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
            _gate.Release();
        }
    }

    public async Task<byte[]> ReadPrefixAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = Get(path);
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
            _gate.Release();
        }
    }

    public void ForEachEntry(Action<ArchiveEntryInfo, Stream> visitor, CancellationToken cancellationToken)
    {
        _gate.Wait(cancellationToken);
        try
        {
            foreach (var info in Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = _byPath[info.Path];
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
            _gate.Release();
        }
    }

    private ZipArchiveEntry Get(string path)
        => _byPath.TryGetValue(path, out var e)
            ? e
            : throw new BookOpenException(OpenErrorKind.InnerPathMissing, Loc.T("The archive has no entry named \"%@\".", path));

    public void Dispose()
    {
        _zip.Dispose();
        _stream.Dispose();
        _gate.Dispose();
    }
}

/// <summary>
/// SharpCompress 기반 RAR4/5 reader. solid 아카이브는 임의 접근이 O(n)이므로
/// 호출자(BookOpener)가 캐시에 한 번 전체 해제해 디스크에서 읽는다.
/// </summary>
public sealed class RarArchiveReader : IArchiveReader
{
    private readonly IArchive _archive;
    private readonly Dictionary<string, IArchiveEntry> _byPath = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RarArchiveReader(string path)
    {
        SourcePath = path;
        try
        {
            _archive = RarArchive.OpenArchive(path, new ReaderOptions { LookForHeader = false });
            var list = new List<ArchiveEntryInfo>();
            foreach (var e in _archive.Entries)
            {
                if (e.IsDirectory || e.Key is null) continue;
                if (!string.IsNullOrEmpty(e.LinkTarget)) continue;
                var p = ArchiveSniffer.NormalizeEntryPath(e.Key);
                if (p.Length == 0) continue;
                if (!_byPath.TryAdd(p, e)) continue;
                DateTimeOffset? modified = e.LastModifiedTime is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null;
                list.Add(new ArchiveEntryInfo(p, e.Size, modified, e.IsEncrypted, unchecked((uint)e.Crc)));
            }
            Entries = list;
            IsSolid = _archive.IsSolid;
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

    public ArchiveEntryInfo? Find(string path) => Entries.FirstOrDefault(e => e.Path == path);

    public async Task<byte[]> ReadEntryAsync(string path, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = Get(path);
            return Translate(path, () => ReadCore(entry, path, long.MaxValue, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> ReadPrefixAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = Get(path);
            return Translate(path, () => ReadCore(entry, path, maxBytes, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ForEachEntry(Action<ArchiveEntryInfo, Stream> visitor, CancellationToken cancellationToken)
    {
        _gate.Wait(cancellationToken);
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
                        var e = _byPath[info.Path];
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
            _gate.Release();
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

    private IArchiveEntry Get(string path)
    {
        if (!_byPath.TryGetValue(path, out var e))
            throw new BookOpenException(OpenErrorKind.InnerPathMissing, Loc.T("The archive has no entry named \"%@\".", path));
        if (e.IsEncrypted) throw new BookOpenException(OpenErrorKind.Encrypted, Loc.T("Password-protected archives are not supported."));
        return e;
    }

    public void Dispose()
    {
        _archive.Dispose();
        _gate.Dispose();
    }
}

/// <summary>CRC-32(IEEE) — ZIP entry 검증용.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var c = ~crc;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
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
