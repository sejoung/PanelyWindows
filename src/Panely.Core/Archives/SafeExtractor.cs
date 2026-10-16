using Panely.Core.Abstractions;
using Panely.Core.Localization;

namespace Panely.Core.Archives;

/// <summary>
/// 추출 한도(04-files.md). 누적 5GiB, 파일 수 상한(원본에 숫자가 없어 100,000으로 정함).
/// 테스트에서는 작은 값을 주입한다.
/// </summary>
public sealed record ExtractionLimits(long MaxTotalBytes, int MaxFileCount, int MaxWalkDepth)
{
    public const long DefaultMaxTotalBytes = 5L * 1024 * 1024 * 1024;
    /// <summary>Windows 결정: 원본에 없는 파일 수 상한. 대형 책 샘플 기준으로 넉넉히 정했다.</summary>
    public const int DefaultMaxFileCount = 100_000;
    public const int DefaultWalkDepth = 3;

    public static ExtractionLimits Default { get; } = new(DefaultMaxTotalBytes, DefaultMaxFileCount, DefaultWalkDepth);
}

/// <summary>누적 해제량. overflow는 포화 덧셈으로 처리한다.</summary>
public sealed class ExtractionBudget(ExtractionLimits limits)
{
    public ExtractionLimits Limits { get; } = limits;
    public long TotalBytes { get; private set; }
    public int FileCount { get; private set; }

    public static long SaturatingAdd(long a, long b)
    {
        if (b > 0 && a > long.MaxValue - b) return long.MaxValue;
        if (b < 0 && a < long.MinValue - b) return long.MinValue;
        return a + b;
    }

    /// <summary>해제 전에 선언 크기로 검사한다. 내부 아카이브 교체면 archiveBytes만큼 빠질 예정.</summary>
    public void CheckDeclared(long declaredBytes, long replacedArchiveBytes = 0)
    {
        var projected = SaturatingAdd(SaturatingAdd(TotalBytes, -Math.Max(0, replacedArchiveBytes)), Math.Max(0, declaredBytes));
        if (projected > Limits.MaxTotalBytes) throw LimitError();
    }

    /// <summary>실제 쓰기 중 한도 적용.</summary>
    public void AddWritten(long bytes)
    {
        TotalBytes = SaturatingAdd(TotalBytes, bytes);
        if (TotalBytes > Limits.MaxTotalBytes) throw LimitError();
    }

    public void AddFile()
    {
        FileCount++;
        if (FileCount > Limits.MaxFileCount)
            throw new BookOpenException(OpenErrorKind.ExtractionLimit, Loc.T("Archive has too many files (more than %lld).", Limits.MaxFileCount));
    }

    /// <summary>내부 아카이브를 지우고 해제 폴더로 바꿀 때: oldTotal - archiveBytes + expandedBytes.</summary>
    public void ReplaceArchive(long archiveBytes, long expandedBytes)
    {
        TotalBytes = SaturatingAdd(SaturatingAdd(TotalBytes, -archiveBytes), expandedBytes);
        if (TotalBytes < 0) TotalBytes = 0;
        if (TotalBytes > Limits.MaxTotalBytes) throw LimitError();
    }

    /// <summary>내부 해제 중 쓰기 집계를 위해 일시 counter.</summary>
    internal void AddTemporary(long bytes) => AddWritten(bytes);

    internal void Subtract(long bytes) => TotalBytes = Math.Max(0, SaturatingAdd(TotalBytes, -bytes));

    private BookOpenException LimitError()
        => new(OpenErrorKind.ExtractionLimit, Loc.T("Archive expanded past the safety limit (%lld MB).", Limits.MaxTotalBytes / (1024 * 1024)));
}

/// <summary>
/// 경로 방어(Windows 보완 결정): `..`, 절대경로, 드라이브, UNC, ADS ':', reserved device name,
/// 끝 공백/마침표, 목적지 밖 symlink/junction을 거부하고 canonical destination이 root 안인지 확인한다.
/// </summary>
public static class EntryPathGuard
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>안전한 상대 경로면 '/' 구분 정규화 경로, 아니면 null.</summary>
    public static string? Sanitize(string entryPath)
    {
        if (string.IsNullOrEmpty(entryPath)) return null;
        if (entryPath.Contains('\0')) return null;
        var raw = entryPath.Replace('\\', '/');
        if (raw.StartsWith('/')) return null; // 절대 경로·UNC(\\server)
        if (raw.Contains(':')) return null; // 드라이브(C:)·ADS(file:stream)
        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        foreach (var part in parts)
        {
            if (part is "." or "..") return null;
            if (part.EndsWith(' ') || part.EndsWith('.')) return null;
            if (part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) return null;
            if (part.Any(c => c < 32)) return null;
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (Reserved.Contains(stem)) return null;
        }
        return string.Join('/', parts);
    }

    /// <summary>root 안의 canonical 목적지. 밖으로 나가면 null.</summary>
    public static string? ResolveDestination(string root, string safeRelative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dest = Path.GetFullPath(Path.Combine(fullRoot, safeRelative.Replace('/', Path.DirectorySeparatorChar)));
        return dest.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? dest : null;
    }

    /// <summary>root부터 대상 부모까지 reparse point(symlink/junction)가 없는지 확인.</summary>
    public static bool HasNoReparsePoints(string root, string destination, HashSet<string> verifiedDirectories)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var dir = Path.GetDirectoryName(destination);
        var stack = new Stack<string>();
        while (dir is not null && dir.Length > fullRoot.Length && !verifiedDirectories.Contains(dir))
        {
            stack.Push(dir);
            dir = Path.GetDirectoryName(dir);
        }
        foreach (var d in stack)
        {
            if (Directory.Exists(d) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) return false;
            verifiedDirectories.Add(d);
        }
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}

/// <summary>
/// 아카이브를 staging 디렉터리에 안전하게 해제하고 중첩 아카이브를 폴더로 펼친다.
/// walk(depth=0); depth>=3이면 중지; 하위 폴더/내부 해제 폴더는 depth+1 (04-files.md).
/// </summary>
public sealed class SafeExtractor(ExtractionLimits limits, Action<string>? warn = null)
{
    public ExtractionLimits Limits { get; } = limits;

    /// <summary>바깥 아카이브를 destination에 해제하고(필요하면) 중첩 아카이브를 펼친다. 해제 바이트 수 반환.</summary>
    public long ExtractAll(IArchiveReader reader, string destination, bool expandNested, CancellationToken cancellationToken)
    {
        var budget = new ExtractionBudget(Limits);
        ExtractInto(reader, destination, budget, 0, cancellationToken);
        if (expandNested) Walk(destination, 0, budget, cancellationToken);
        return budget.TotalBytes;
    }

    internal void ExtractInto(IArchiveReader reader, string destination, ExtractionBudget budget, long replacedArchiveBytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        long declared = 0;
        foreach (var e in reader.Entries) declared = ExtractionBudget.SaturatingAdd(declared, Math.Max(0, e.Size));
        budget.CheckDeclared(declared, replacedArchiveBytes);

        var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[81920];
        reader.ForEachEntry((info, stream) =>
        {
            var safe = EntryPathGuard.Sanitize(info.Path);
            if (safe is null)
            {
                warn?.Invoke($"Skipped unsafe archive entry ({Path.GetFileName(info.Path)})");
                return;
            }
            var target = EntryPathGuard.ResolveDestination(destination, safe);
            if (target is null || !EntryPathGuard.HasNoReparsePoints(destination, target, verified))
            {
                warn?.Invoke($"Skipped archive entry outside destination ({Path.GetFileName(info.Path)})");
                return;
            }
            if (info.IsEncrypted) throw new BookOpenException(OpenErrorKind.Encrypted, Loc.T("Password-protected archives are not supported."));
            budget.AddFile();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) return; // 같은 이름 중복 entry는 첫 항목 유지
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
            {
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    budget.AddWritten(n);
                    output.Write(buffer, 0, n);
                }
            }
            if (info.Modified is { } m)
            {
                try { File.SetLastWriteTimeUtc(target, m.UtcDateTime); } catch (IOException) { } catch (ArgumentOutOfRangeException) { }
            }
        }, cancellationToken);
    }

    internal void Walk(string directory, int depth, ExtractionBudget budget, CancellationToken cancellationToken)
    {
        if (depth >= Limits.MaxWalkDepth) return;
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var file in Directory.GetFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (name.StartsWith('.') || !SupportedFormats.IsArchive(file)) continue;
            var format = ArchiveSniffer.DetectFile(file);
            if (format == ArchiveFormat.Unknown) continue;

            var archiveBytes = new FileInfo(file).Length;
            var temp = Path.Combine(directory, $".panely-expand-{Guid.NewGuid():N}");
            var before = budget.TotalBytes;
            try
            {
                using (var inner = ArchiveReaderFactory.Open(file, format))
                {
                    ExtractInto(inner, temp, budget, archiveBytes, cancellationToken);
                }
                var expanded = budget.TotalBytes - before;
                // 집계: oldTotal - archiveBytes + expandedBytes
                budget.Subtract(expanded);
                File.Delete(file);
                budget.ReplaceArchive(archiveBytes, expanded);
                // 폴더 이름은 아카이브 파일 이름 그대로(innerPath가 원래 entry 이름을 반영)
                Directory.Move(temp, file);
            }
            catch (BookOpenException ex) when (ex.Kind is OpenErrorKind.Corrupt or OpenErrorKind.Encrypted or OpenErrorKind.Unsupported)
            {
                // 내부 아카이브 하나가 깨졌어도 나머지 책은 연다. 해당 파일은 그대로 둔다.
                warn?.Invoke($"Nested archive could not be expanded ({name}): {ex.Kind}");
                budget.Subtract(budget.TotalBytes - before);
                TryDelete(temp);
                continue;
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
            Walk(file, depth + 1, budget, cancellationToken);
        }

        foreach (var sub in Directory.GetDirectories(directory))
        {
            if (Path.GetFileName(sub).StartsWith('.')) continue;
            if (SupportedFormats.IsArchive(sub)) continue; // 방금 펼친 폴더는 위에서 depth+1로 처리했다
            Walk(sub, depth + 1, budget, cancellationToken);
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
