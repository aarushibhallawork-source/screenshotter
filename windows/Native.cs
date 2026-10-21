// Windows API calls the app needs that .NET doesn't wrap.
using System.Runtime.InteropServices;

namespace ScreenshotVault;

static class Native
{
    [DllImport("shell32.dll")]
    public static extern int SHGetKnownFolderPath([In] ref Guid rfid, uint flags, IntPtr token, out IntPtr path);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    // Dragging a borderless window, and keeping the widget below other windows.
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    public const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2, WM_WINDOWPOSCHANGING = 0x46;
    public const uint SWP_NOZORDER = 0x4;

    // Whether the left mouse button is held (works while the browser view has the mouse captured).
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    public const int VK_LBUTTON = 0x01;
    public static bool LeftButtonDown => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    // The desktop (wallpaper + icons) window, and making the widget belong to it.
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    public const int GWLP_HWNDPARENT = -8;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    // Current wallpaper path.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(uint action, uint param, System.Text.StringBuilder value, uint winIni);
    public const uint SPI_GETDESKWALLPAPER = 0x73;

    // Windows 11 window frame: rounded corners and border colour (ignored on Windows 10).
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;
}
