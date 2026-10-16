using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Panely.App.Shell;

internal static partial class NativeMethods
{
    public const int WM_MOUSEHWHEEL = 0x020E;
    public const int WM_GETMINMAXINFO = 0x0024;

    [LibraryImport("shell32.dll")]
    public static partial void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    /// <summary>Windows 캡션을 다크로(시스템 버튼·Snap·시스템 메뉴는 유지). Win10 이하에서는 무시된다.</summary>
    public static void ApplyDarkCaption(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        try
        {
            var on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            // COLORREF = 0x00BBGGRR
            var caption = 0x00151110; // #0F1115
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            var text = 0x00F2EAE6; // #E6EAF2
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
            var border = 0x00261F1C;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch (Exception)
        {
            // dwmapi 미지원 환경
        }
    }

    public static int HiWord(IntPtr value) => unchecked((short)((long)value >> 16));
}
