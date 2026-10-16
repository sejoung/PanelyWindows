using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Panely.Core.Abstractions;
using Panely.Domain.Model;

namespace Panely.Core.Diagnostics;

/// <summary>
/// 제한된 파일 로그와 진단 리포트(05-data.md).
/// recent-log.txt 한 개, 768KiB 초과 시 최신 약 384KiB로 trim(줄 경계), 리포트는 최신 256KiB.
/// 로그 I/O 실패로 리더를 중단하지 않는다.
/// </summary>
public sealed class DiagnosticsService : IDiagnostics, IDisposable
{
    public const string LogFileName = "recent-log.txt";
    public const long MaxLogBytes = 786_432;
    public const long TrimLogBytes = 393_216;
    public const int ReportLogBytes = 262_144;
    private const int MaxRecentErrors = 20;
    private const int MaxLoadEvents = 200;

    private readonly object _gate = new();
    private readonly string _logPath;
    private readonly LinkedList<string> _recentErrors = new();
    private readonly LinkedList<LoadEvent> _loadEvents = new();
    private long _approxSize = -1;

    public DiagnosticsService(AppPaths paths)
    {
        LogFolder = paths.Diagnostics;
        _logPath = Path.Combine(LogFolder, LogFileName);
        SessionId = Guid.NewGuid();
        LaunchedAt = DateTimeOffset.UtcNow;
        try { Directory.CreateDirectory(LogFolder); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Log(LogLevel.Notice, LogCategory.App, $"Session {SessionId:D} launched at {LaunchedAt:yyyy-MM-ddTHH:mm:ssZ}");
    }

    /// <summary>테스트용: trim 임계값 조정.</summary>
    internal long MaxBytes { get; init; } = MaxLogBytes;
    internal long TrimBytes { get; init; } = TrimLogBytes;

    public Guid SessionId { get; }
    public DateTimeOffset LaunchedAt { get; }
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public string LogFolder { get; }

    public long LogFileBytes
    {
        get
        {
            try { return File.Exists(_logPath) ? new FileInfo(_logPath).Length : 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }
    }

    public IReadOnlyList<string> RecentErrors
    {
        get { lock (_gate) return _recentErrors.ToList(); }
    }

    public IReadOnlyList<LoadEvent> RecentLoadEvents
    {
        get { lock (_gate) return _loadEvents.ToList(); }
    }

    public void Log(LogLevel level, LogCategory category, string message)
    {
        if (level < MinimumLevel) return;
        var safe = PathRedactor.Redact(message).Replace('\r', ' ').Replace('\n', ' ');
        var line = $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{LevelName(level)}] [{CategoryName(category)}] {safe}\n";
        lock (_gate)
        {
            if (level >= LogLevel.Error)
            {
                _recentErrors.AddLast($"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} [{CategoryName(category)}] {safe}");
                while (_recentErrors.Count > MaxRecentErrors) _recentErrors.RemoveFirst();
            }
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                using (var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Write(bytes);
                    _approxSize = fs.Length;
                }
                if (_approxSize > MaxBytes) TrimLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 로그 실패로 앱을 멈추지 않는다
            }
        }
    }

    public void RecordLoad(LoadEvent loadEvent)
    {
        lock (_gate)
        {
            _loadEvents.AddLast(loadEvent with { FileName = PathRedactor.FileNameOnly(loadEvent.FileName) });
            while (_loadEvents.Count > MaxLoadEvents) _loadEvents.RemoveFirst();
        }
        Log(loadEvent.Outcome.Equals("success", StringComparison.OrdinalIgnoreCase) ? LogLevel.Info : LogLevel.Warning,
            LogCategory.Load, FormatLoad(loadEvent));
    }

    private static string FormatLoad(LoadEvent e)
        => $"{e.Kind} {PathRedactor.FileNameOnly(e.FileName)} ext={e.Extension} outcome={e.Outcome}" + (e.Duration is { } d ? $" {d.TotalMilliseconds:0}ms" : string.Empty);

    private void TrimLocked()
    {
        var data = File.ReadAllBytes(_logPath);
        var keepFrom = TailOffset(data, (int)Math.Min(TrimBytes, data.Length));
        var tmp = _logPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(data, keepFrom, data.Length - keepFrom);
            fs.Flush(true);
        }
        File.Move(tmp, _logPath, overwrite: true);
        _approxSize = data.Length - keepFrom;
    }

    /// <summary>끝에서 maxBytes 이내를 남기되 줄 경계(그리고 유효 UTF-8 경계)에서 시작하는 offset.</summary>
    internal static int TailOffset(byte[] data, int maxBytes)
    {
        if (data.Length <= maxBytes) return 0;
        var start = data.Length - maxBytes;
        var nl = Array.IndexOf(data, (byte)'\n', start);
        if (nl >= 0 && nl + 1 < data.Length) return nl + 1;
        // 줄 경계가 없으면 UTF-8 continuation byte를 건너뛴다
        while (start < data.Length && (data[start] & 0xC0) == 0x80) start++;
        return start;
    }

    public string DefaultReportFileName(DateTimeOffset now)
        => $"Panely-Diagnostic-Report-{now.ToLocalTime():yyyyMMdd-HHmmss}.zip";

    public async Task ExportReportAsync(string zipPath, DiagnosticSnapshot snapshot, CancellationToken cancellationToken)
    {
        string logTail;
        List<LoadEvent> events;
        List<string> errors;
        lock (_gate)
        {
            byte[] data = [];
            try { if (File.Exists(_logPath)) data = File.ReadAllBytes(_logPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var offset = TailOffset(data, ReportLogBytes);
            logTail = Encoding.UTF8.GetString(data, offset, data.Length - offset);
            events = _loadEvents.ToList();
            errors = _recentErrors.ToList();
        }

        var report = BuildReport(snapshot, errors);
        var loads = new StringBuilder();
        foreach (var e in events)
            loads.Append(e.Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)).Append(' ').Append(FormatLoad(e)).Append('\n');

        var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(zipPath)}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteEntryAsync(zip, "diagnostic-report.md", report, cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(zip, "recent-log.txt", logTail, cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(zip, "recent-load-events.txt", loads.ToString(), cancellationToken).ConfigureAwait(false);
            }
            File.Move(tmp, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
        Log(LogLevel.Info, LogCategory.Diagnostics, "Diagnostic report exported");
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string content, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await s.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private string BuildReport(DiagnosticSnapshot s, IReadOnlyList<string> errors)
    {
        var sb = new StringBuilder();
        sb.Append("# Panely Diagnostic Report\n\n");
        sb.Append("Generated: ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)).Append("\n\n");
        sb.Append("## App\n\n");
        sb.Append("- Version: ").Append(s.AppVersion).Append('\n');
        sb.Append("- Build: ").Append(s.Build).Append('\n');
        sb.Append("- OS: ").Append(s.OsVersion).Append('\n');
        sb.Append("- Session: ").Append(SessionId.ToString("D")).Append('\n');
        sb.Append("- Launched (UTC): ").Append(LaunchedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)).Append("\n\n");
        sb.Append("## Extraction cache\n\n");
        sb.Append("- Total: ").Append(s.Cache.TotalBytes).Append(" bytes\n");
        sb.Append("- Clearable: ").Append(s.Cache.ClearableBytes).Append(" bytes\n");
        sb.Append("- Limit: ").Append(s.Cache.LimitBytes).Append(" bytes\n\n");
        sb.Append("## Log policy\n\n");
        sb.Append("- Level: ").Append(LevelName(MinimumLevel)).Append('\n');
        sb.Append("- File log: ").Append(LogFileName).Append(", max ").Append(MaxBytes).Append(" bytes, trimmed to ~").Append(TrimBytes).Append(" bytes\n");
        sb.Append("- Report includes the latest ").Append(ReportLogBytes).Append(" bytes\n\n");
        sb.Append("## Settings\n\n");
        foreach (var (k, v) in s.Settings.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append("- ").Append(k).Append(": ").Append(PathRedactor.Redact(v)).Append('\n');
        sb.Append("\n## Reader state\n\n");
        foreach (var (k, v) in s.ReaderState.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append("- ").Append(k).Append(": ").Append(PathRedactor.Redact(v)).Append('\n');
        sb.Append("\n## Recent errors\n\n");
        if (errors.Count == 0) sb.Append("- (none)\n");
        foreach (var e in errors) sb.Append("- ").Append(e).Append('\n');
        sb.Append("\n## Privacy\n\n");
        sb.Append("Absolute paths are shortened to file names, the user profile folder is shown as %USERPROFILE%, ")
          .Append("and UNC credentials are masked. OCR text and translations are not recorded. ")
          .Append("System error messages may still contain details Panely does not recognize.\n");
        return sb.ToString();
    }

    public void ClearLogs()
    {
        lock (_gate)
        {
            if (File.Exists(_logPath)) File.Delete(_logPath);
            var tmp = _logPath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            _approxSize = 0;
        }
    }

    public static string LevelName(LogLevel level) => level.ToString().ToLowerInvariant();

    public static string CategoryName(LogCategory category) => category.ToString().ToLowerInvariant();

    public void Dispose() { }
}

/// <summary>
/// 로그/리포트 문자열의 경로 축약. 사용자 폴더 → %USERPROFILE%, UNC/URL credential 마스킹,
/// 절대 경로 → …\파일이름. 모든 시스템 오류에서 개인정보가 사라진다고 보장하지 않는다.
/// </summary>
public static partial class PathRedactor
{
    private static readonly string? UserProfile = SafeUserProfile();

    private static string? SafeUserProfile()
    {
        try
        {
            var p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(p) ? null : p.TrimEnd('\\', '/');
        }
        catch { return null; }
    }

    public static string Redact(string? text) => Redact(text, UserProfile, Environment.UserName);

    internal static string Redact(string? text, string? userProfile, string? userName)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var s = text;
        s = UrlCredentials().Replace(s, "$1***@");
        s = UncCredentials().Replace(s, @"\\***@");
        if (!string.IsNullOrEmpty(userProfile))
            s = Regex.Replace(s, Regex.Escape(userProfile), "%USERPROFILE%", RegexOptions.IgnoreCase);
        s = AbsolutePath().Replace(s, m =>
        {
            var value = m.Value.TrimEnd('.', ',', ';', ')', ']');
            var trailing = m.Value[value.Length..];
            var prefix = value.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase) ? "%USERPROFILE%\\…\\" : "…\\";
            var name = FileNameOnly(value);
            return (name.Length == 0 ? prefix.TrimEnd('\\') : prefix + name) + trailing;
        });
        if (!string.IsNullOrEmpty(userName) && userName.Length >= 3)
            s = Regex.Replace(s, $@"(?<![\w]){Regex.Escape(userName)}(?![\w])", "<user>", RegexOptions.IgnoreCase);
        return s;
    }

    public static string FileNameOnly(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var trimmed = path.TrimEnd('\\', '/');
        var i = trimmed.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? trimmed[(i + 1)..] : trimmed;
    }

    [GeneratedRegex(@"([a-zA-Z][a-zA-Z0-9+.-]*://)[^/\s:@]+(?::[^/\s@]*)?@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"\\\\[^\\\s:@]+(?::[^\\\s@]*)?@")]
    private static partial Regex UncCredentials();

    // C:\..., \\server\share\..., %USERPROFILE%\...
    // 중간 폴더 이름은 공백을 포함할 수 있다(뒤에 구분자가 오는 구간). 마지막 구성요소는 공백 전까지.
    // 콜론은 경로 이름에 올 수 없으므로 중간 구간에서 제외해 다음 드라이브 경로까지 삼키지 않는다.
    [GeneratedRegex(@"(?:%USERPROFILE%|(?<![A-Za-z0-9])[A-Za-z]:|\\\\[^\\\s]+)(?:[\\/][^\\/:""'<>|*?\r\n]+(?=[\\/]))*(?:[\\/][^\\/\s""'<>|*?]+)[\\/]?", RegexOptions.IgnoreCase)]
    private static partial Regex AbsolutePath();
}
