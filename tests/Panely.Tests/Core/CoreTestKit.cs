using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Panely.Core;
using Panely.Core.Archives;
using Panely.Core.Cache;
using Panely.Core.Diagnostics;
using Panely.Core.Sources;
using Panely.Core.State;

namespace Panely.Tests.Core;

/// <summary>임시 디렉터리와 fixture 생성 도구.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "panely-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string P(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public string Dir(params string[] parts)
    {
        var d = P(parts);
        Directory.CreateDirectory(d);
        return d;
    }

    public string Png(string relative, int w = 10, int h = 20)
    {
        var path = P(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, CoreTestKit.PngBytes(w, h));
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class CoreEnv : IDisposable
{
    public CoreEnv(long cacheLimit = ExtractionCache.DefaultLimitBytes, ExtractionLimits? limits = null)
    {
        Temp = new TempDir();
        Paths = new AppPaths(Temp.P("appdata"));
        Paths.EnsureCreated();
        Diagnostics = new DiagnosticsService(Paths);
        State = new StateStore(Paths, Diagnostics);
        Cache = new ExtractionCache(Paths, Diagnostics, cacheLimit);
        Opener = new BookOpener(State, Cache, Diagnostics, limits ?? ExtractionLimits.Default);
    }

    public TempDir Temp { get; }
    public AppPaths Paths { get; }
    public DiagnosticsService Diagnostics { get; }
    public StateStore State { get; }
    public ExtractionCache Cache { get; }
    public BookOpener Opener { get; }

    public void Dispose()
    {
        State.Dispose();
        Diagnostics.Dispose();
        Temp.Dispose();
    }
}

public static class CoreTestKit
{
    /// <summary>헤더 판독용 최소 PNG(signature + IHDR + IEND). 디코딩용 아님.</summary>
    public static byte[] PngBytes(int w, int h)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 2;
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32.Append(Crc32.Compute(typeBytes), data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    /// <summary>entries: 경로 → 내용. 내용이 null이면 디렉터리 entry.</summary>
    public static void Zip(string path, IEnumerable<(string Name, byte[]? Data)> entries)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Fastest);
            if (data is null) continue;
            using var s = e.Open();
            s.Write(data);
        }
    }

    public static byte[] ZipBytes(IEnumerable<(string Name, byte[]? Data)> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Fastest);
                if (data is null) continue;
                using var s = e.Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    public static IEnumerable<(string, byte[]?)> Pages(string prefix, int count, string ext = "png")
    {
        for (var i = 1; i <= count; i++) yield return ($"{prefix}{i:000}.{ext}", PngBytes(10 + i, 20));
    }
}
