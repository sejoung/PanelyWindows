using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Panely.Core.State;

/// <summary>
/// volume serial + file index(GetFileInformationByHandle). 폴더는 FILE_FLAG_BACKUP_SEMANTICS로 연다.
/// 경로가 바뀌어도 같은 파일을 찾아 SourceRef 경로를 갱신하는 데 쓴다.
/// </summary>
public static partial class FileIdentity
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint OpenExisting = 3;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    public static bool TryGet(string path, out string volumeId, out string fileId)
    {
        volumeId = fileId = string.Empty;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var extended = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
                : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..]
                : @"\\?\" + full;
            using var handle = CreateFile(extended, 0, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
            if (handle.IsInvalid) return false;
            if (!GetFileInformationByHandle(handle, out var info)) return false;
            volumeId = info.VolumeSerialNumber.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
            fileId = (((ulong)info.FileIndexHigh << 32) | info.FileIndexLow).ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
