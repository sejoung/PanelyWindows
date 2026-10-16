using Microsoft.Win32;

namespace Panely.App.Shell;

/// <summary>
/// 사용자별(HKCU) "Panely로 열기" 등록. cbz/cbr/zip/rar에 ProgID와 OpenWithProgids만 추가하며
/// ZIP/RAR의 시스템 기본 연결은 바꾸지 않는다. cbz/cbr에 기본 연결이 없을 때만 기본 ProgID를 지정한다.
/// 관리자 권한이 필요 없다.
/// </summary>
public static class FileAssociation
{
    public const string ProgId = "Panely.ComicArchive";
    private static readonly string[] Extensions = [".cbz", ".cbr", ".zip", ".rar"];
    private static readonly string[] ComicExtensions = [".cbz", ".cbr"];

    public static string ExecutablePath => Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()!.Location;

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
        var value = key?.GetValue(null) as string;
        return value is not null && value.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void Register()
    {
        var exe = ExecutablePath;
        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue(null, "Comic archive (Panely)");
            using var icon = prog.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exe}\",0");
            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, $"\"{exe}\" \"%1\"");
        }
        foreach (var ext in Extensions)
        {
            using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}");
            using var openWith = extKey.CreateSubKey("OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            if (ComicExtensions.Contains(ext) && extKey.GetValue(null) is null)
                extKey.SetValue(null, ProgId);
        }
        // 폴더 컨텍스트 메뉴 "Open in Panely"
        using (var dir = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\Panely"))
        {
            dir.SetValue(null, "Open in Panely");
            dir.SetValue("Icon", $"\"{exe}\",0");
            using var cmd = dir.CreateSubKey("command");
            cmd.SetValue(null, $"\"{exe}\" \"%1\"");
        }
        NotifyShell();
    }

    public static void Unregister()
    {
        foreach (var ext in Extensions)
        {
            using var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}", writable: true);
            if (extKey is null) continue;
            using (var openWith = extKey.OpenSubKey("OpenWithProgids", writable: true))
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            if (extKey.GetValue(null) as string == ProgId) extKey.DeleteValue(string.Empty, throwOnMissingValue: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\Panely", throwOnMissingSubKey: false);
        NotifyShell();
    }

    private static void NotifyShell() => NativeMethods.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
}
