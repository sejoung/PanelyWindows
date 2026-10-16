using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;

namespace Panely.App.Tests;

/// <summary>
/// 실제 Panely.exe를 격리된 데이터 폴더(PANELY_DATA_ROOT)로 띄우는 종단 간 smoke 테스트.
/// 데스크톱 세션이 필요하다. 사용자가 띄워 둔 Panely와는 single-instance 키가 달라 서로 간섭하지 않는다.
/// 필터: --filter Category=Smoke  (제외: Category!=Smoke)
/// </summary>
[Trait("Category", "Smoke")]
[Collection("Smoke")] // 앱 프로세스를 순서대로 띄운다
public sealed class SmokeTests : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(40);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "panely-smoke-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    private string DataRoot => Path.Combine(_root, "data");

    private string MakeFolderBook(string name, int pages)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "library", name)).FullName;
        for (var i = 1; i <= pages; i++)
            File.WriteAllBytes(Path.Combine(dir, $"{i:000}.png"), TestImages.Make(300, 450, "png", Color.FromRgb((byte)(40 * i), 180, 200)));
        return dir;
    }

    private Process Launch(params string[] args)
    {
        var exe = Repo.AppExe;
        Assert.True(File.Exists(exe), $"Panely.exe가 없습니다: {exe}");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PANELY_DATA_ROOT"] = DataRoot;
        // 프레임워크 종속 실행 파일: 테스트를 실행 중인 .NET 런타임을 그대로 쓴다(사용자 폴더 SDK 설치 대응)
        var runtimeDir = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        psi.Environment["DOTNET_ROOT"] = runtimeDir.Parent!.Parent!.Parent!.FullName;
        var p = Process.Start(psi)!;
        _processes.Add(p);
        return p;
    }

    private static string WaitForTitle(Process p, Func<string, bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var last = "";
        while (sw.Elapsed < timeout)
        {
            if (p.HasExited) Assert.Fail($"Panely가 일찍 종료되었습니다(exit {p.ExitCode}). 마지막 제목: '{last}'");
            p.Refresh();
            last = p.MainWindowTitle;
            if (p.MainWindowHandle != IntPtr.Zero && predicate(last)) return last;
            Thread.Sleep(200);
        }
        Assert.Fail($"창 제목이 기대와 다릅니다. 마지막 제목: '{last}'");
        return last;
    }

    private static void CloseGracefully(Process p)
    {
        if (p.HasExited) return;
        p.CloseMainWindow();
        if (!p.WaitForExit(20_000)) { p.Kill(); Assert.Fail("창 닫기 요청 후 20초 안에 종료되지 않았습니다."); }
    }

    private string ReadLog() => File.ReadAllText(Path.Combine(DataRoot, "Diagnostics", "recent-log.txt"));

    [Fact]
    public void Opens_a_folder_book_and_saves_state_on_exit()
    {
        var book = MakeFolderBook("Smoke Book", 3);
        var app = Launch(book);
        WaitForTitle(app, t => t.StartsWith("Smoke Book", StringComparison.Ordinal), StartTimeout);
        Thread.Sleep(1000);
        Assert.False(app.HasExited);

        CloseGracefully(app);
        Assert.Equal(0, app.ExitCode);

        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataRoot, "Data", "state.json")));
        Assert.Equal(1, state.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Contains(state.RootElement.GetProperty("recents").EnumerateArray(), r => r.GetProperty("title").GetString() == "Smoke Book");
        var log = ReadLog();
        Assert.Contains("outcome=success", log, StringComparison.Ordinal);
        Assert.Contains("[app] Exit", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Survives_a_corrupt_archive()
    {
        var corrupt = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "bad")).FullName, "broken.cbz");
        File.WriteAllBytes(corrupt, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8]);
        var app = Launch(corrupt);
        WaitForTitle(app, t => t.Length > 0, StartTimeout);
        Thread.Sleep(2500);
        Assert.False(app.HasExited, "손상된 아카이브를 열다 앱이 종료되었습니다.");
        Assert.Contains("Corrupt", ReadLog(), StringComparison.Ordinal);
        CloseGracefully(app);
        Assert.Equal(0, app.ExitCode);
    }

    [Fact]
    public void Second_launch_forwards_to_the_running_reader()
    {
        var first = MakeFolderBook("First Book", 2);
        var zipDir = MakeFolderBook("zip-src", 2);
        var second = Path.Combine(_root, "library", "Second Book.cbz");
        ZipFile.CreateFromDirectory(zipDir, second);

        var app = Launch(first);
        WaitForTitle(app, t => t.StartsWith("First Book", StringComparison.Ordinal), StartTimeout);

        var forwarder = Launch(second);
        Assert.True(forwarder.WaitForExit(15_000), "두 번째 실행이 종료되지 않았습니다(요청 전달 실패).");
        Assert.Equal(0, forwarder.ExitCode);

        // 마지막 명시 요청이 화면을 차지한다(APP-01)
        WaitForTitle(app, t => t.StartsWith("Second Book", StringComparison.Ordinal), TimeSpan.FromSeconds(20));
        CloseGracefully(app);
    }

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            p.Dispose();
        }
        for (var i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}

[CollectionDefinition("Smoke", DisableParallelization = true)]
public sealed class SmokeTestGroup;
