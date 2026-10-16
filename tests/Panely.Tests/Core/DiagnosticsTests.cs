using System.IO.Compression;
using System.Text;
using Panely.Core;
using Panely.Core.Abstractions;
using Panely.Core.Diagnostics;
using Panely.Domain.Model;

namespace Panely.Tests.Core;

public class DiagnosticsTests
{
    [Fact]
    public void Trim_KeepsLatestOnLineBoundary()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.P("appdata"));
        var diag = new DiagnosticsService(paths) { MaxBytes = 4096, TrimBytes = 2048 };
        for (var i = 0; i < 400; i++) diag.Log(LogLevel.Info, LogCategory.Reader, $"line {i:0000} 한글 텍스트");
        var bytes = File.ReadAllBytes(System.IO.Path.Combine(paths.Diagnostics, "recent-log.txt"));
        Assert.True(bytes.Length <= 4096 + 200);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", text);
        Assert.Contains("line 0399", text);
        Assert.DoesNotContain("line 0000", text);
        Assert.DoesNotContain("\uFFFD", text);
    }

    [Fact]
    public void TailOffset_RespectsUtf8AndLines()
    {
        var data = Encoding.UTF8.GetBytes("aaa\n가나다\nbbb\n");
        var offset = DiagnosticsService.TailOffset(data, 6);
        Assert.Equal("bbb\n", Encoding.UTF8.GetString(data, offset, data.Length - offset));
        var noNewline = Encoding.UTF8.GetBytes("가나다라");
        var o2 = DiagnosticsService.TailOffset(noNewline, 4);
        Assert.Equal("라", Encoding.UTF8.GetString(noNewline, o2, noNewline.Length - o2));
    }

    [Fact]
    public void LevelFilter_AndRecentErrors()
    {
        using var t = new TempDir();
        var diag = new DiagnosticsService(new AppPaths(t.P("appdata"))) { MinimumLevel = LogLevel.Warning };
        diag.Log(LogLevel.Info, LogCategory.App, "hidden-info");
        diag.Log(LogLevel.Error, LogCategory.Load, @"failed C:\Users\someone\secret\book.cbz");
        var log = File.ReadAllText(System.IO.Path.Combine(t.P("appdata"), "Diagnostics", "recent-log.txt"));
        Assert.DoesNotContain("hidden-info", log);
        Assert.Contains("[error] [load]", log);
        Assert.DoesNotContain("secret", log);
        Assert.Single(diag.RecentErrors);
    }

    [Fact]
    public async Task Report_HasThreeFiles()
    {
        using var t = new TempDir();
        var diag = new DiagnosticsService(new AppPaths(t.P("appdata")));
        diag.Log(LogLevel.Info, LogCategory.App, "hello");
        diag.RecordLoad(new LoadEvent(DateTimeOffset.UtcNow, "Normal", @"D:\Comics\Vol01.cbz", "cbz", "success", TimeSpan.FromMilliseconds(12)));
        var name = diag.DefaultReportFileName(new DateTimeOffset(2026, 10, 16, 1, 2, 3, TimeSpan.Zero));
        Assert.Matches(@"^Panely-Diagnostic-Report-\d{8}-\d{6}\.zip$", name);
        var zipPath = t.P("out", name);
        var snapshot = new DiagnosticSnapshot("1.0.0", "test", "Windows 11", new CacheUsage(10, 5, 100),
            new Dictionary<string, string> { ["layout"] = "single", ["lastRoot"] = @"C:\Comics\Private" },
            new Dictionary<string, string> { ["pages"] = "24" });
        await diag.ExportReportAsync(zipPath, snapshot, CancellationToken.None);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(new[] { "diagnostic-report.md", "recent-load-events.txt", "recent-log.txt" }, zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        using var report = new StreamReader(zip.GetEntry("diagnostic-report.md")!.Open());
        var md = report.ReadToEnd();
        Assert.Contains(diag.SessionId.ToString("D"), md);
        Assert.Contains("Clearable: 5", md);
        Assert.DoesNotContain(@"C:\Comics", md);
        using var loads = new StreamReader(zip.GetEntry("recent-load-events.txt")!.Open());
        var loadText = loads.ReadToEnd();
        Assert.Contains("Vol01.cbz", loadText);
        Assert.DoesNotContain(@"D:\Comics", loadText);
        Assert.Empty(Directory.GetFiles(t.P("out"), "*.partial"));
    }

    [Fact]
    public void ClearLogs_RemovesOnlyLogFile()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.P("appdata"));
        paths.EnsureCreated();
        File.WriteAllText(paths.StateFile, "{}");
        var diag = new DiagnosticsService(paths);
        diag.Log(LogLevel.Info, LogCategory.App, "x");
        Assert.True(diag.LogFileBytes > 0);
        diag.ClearLogs();
        Assert.Equal(0, diag.LogFileBytes);
        Assert.True(File.Exists(paths.StateFile));
    }

    [Theory]
    [InlineData(@"open C:\Users\bob\Comics\x.cbz failed", @"open %USERPROFILE%\…\x.cbz failed")]
    [InlineData(@"D:\Comics\Series A\a.cbz", @"…\a.cbz")]
    [InlineData(@"D:\My Comics\Private Name\Vol 1.cbz opened, then E:\x\y.zip", @"…\Vol 1.cbz opened, then …\y.zip")]
    [InlineData(@"\\nas\share\manga\v1.zip", @"…\v1.zip")]
    [InlineData(@"\\bob:pw@server\share\a.zip", @"…\a.zip")]
    [InlineData("https://user:secret@example.com/x", "https://***@example.com/x")]
    [InlineData("no path here", "no path here")]
    public void Redaction(string input, string expected)
        => Assert.Equal(expected, PathRedactor.Redact(input, @"C:\Users\bob", "someoneelse"));

    [Fact]
    public void Redaction_UserName()
        => Assert.Equal("hello <user> there", PathRedactor.Redact("hello bob there", null, "bob"));
}
