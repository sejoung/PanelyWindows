using System.Diagnostics;
using System.Text;

namespace Panely.App.Tests;

/// <summary>
/// tools/*-local.ps1의 판단 함수만 스크립트에서 뽑아 실행한다. 스크립트 전체(빌드·복사·레지스트리)는 실행하지 않는다.
/// </summary>
public sealed class LocalScriptTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("panely-script-").FullName;

    private static string Call(string script, string function, string call)
    {
        var path = Path.Combine(Repo.Root, "tools", script);
        var body = $$"""
            $ErrorActionPreference = 'Stop'
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{path}}', [ref]$null, [ref]$null)
            $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq '{{function}}' }, $true)
            Invoke-Expression $fn.Extent.Text
            try { {{call}} | Out-String } catch { 'THROW' }
            """;
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(body)) })
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));
        return output.Trim();
    }

    private string InstallCheck(string directory) => Call("install-local.ps1", "Assert-InstallTarget", $"Assert-InstallTarget '{directory}'");

    [Fact]
    public void Install_refuses_only_a_foreign_non_empty_target()
    {
        Assert.Equal("", InstallCheck(Path.Combine(_root, "missing")));

        var empty = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        Assert.Equal("", InstallCheck(empty));

        var reinstall = Directory.CreateDirectory(Path.Combine(_root, "reinstall")).FullName;
        File.WriteAllText(Path.Combine(reinstall, "install-info.json"), "{}");
        File.WriteAllText(Path.Combine(reinstall, "Panely.exe"), "");
        Assert.Equal("", InstallCheck(reinstall));

        var foreign = Directory.CreateDirectory(Path.Combine(_root, "foreign")).FullName;
        var keep = Path.Combine(foreign, "keep.txt");
        File.WriteAllText(keep, "");
        Assert.Equal("THROW", InstallCheck(foreign));
        Assert.True(File.Exists(keep));

        var hidden = Directory.CreateDirectory(Path.Combine(_root, "hidden")).FullName;
        var secret = Path.Combine(hidden, "secret");
        File.WriteAllText(secret, "");
        File.SetAttributes(secret, FileAttributes.Hidden);
        Assert.Equal("THROW", InstallCheck(hidden));

        var file = Path.Combine(_root, "file");
        File.WriteAllText(file, "");
        Assert.Equal("THROW", InstallCheck(file));
    }

    [Theory]
    [InlineData("\"C:\\Apps\\Panely\\Panely.exe\" \"%1\"", true)]
    [InlineData("\"c:\\apps\\panely\\PANELY.EXE\" \"%1\"", true)]
    [InlineData("\"D:\\dev\\bin\\Panely.exe\" \"%1\"", false)]
    [InlineData("", false)]
    public void Uninstall_recognizes_only_this_install(string command, bool expected)
    {
        var quoted = command.Replace("'", "''", StringComparison.Ordinal);
        var result = Call("uninstall-local.ps1", "Test-CommandTargetsExe", $@"Test-CommandTargetsExe '{quoted}' 'C:\Apps\Panely\Panely.exe'");
        Assert.Equal(expected ? "True" : "False", result);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
