using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pidge.Desktop.Windows;

/// <summary>The parts of user32 and dwmapi the frame is built from.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class NativeMethods
{
    public const int GwlStyle = -16;
    public const int GwlpWndProc = -4;
    public const uint GwOwner = 4;

    public const uint WsPopup = 0x80000000;
    public const uint WsCaption = 0x00C00000;
    public const uint WsSysMenu = 0x00080000;
    public const uint WsThickFrame = 0x00040000;
    public const uint WsMinimizeBox = 0x00020000;
    public const uint WsMaximizeBox = 0x00010000;

    public const uint WsExToolWindow = 0x00000080;
    public const uint WsExLayered = 0x00080000;
    public const uint WsExNoActivate = 0x08000000;

    public const uint WmMouseActivate = 0x0021;
    public const uint WmWindowPosChanged = 0x0047;
    public const uint WmNcCalcSize = 0x0083;
    public const uint WmNcHitTest = 0x0084;
    public const uint WmNcLButtonDown = 0x00A1;
    public const uint WmNcLButtonDblClk = 0x00A3;

    public const nint HtCaption = 2;
    public const nint HtTop = 12;
    public const nint HtTopLeft = 13;
    public const nint HtTopRight = 14;

    public const nint MaNoActivate = 3;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint SwpShowWindow = 0x0040;

    public const int SwHide = 0;

    public const uint LwaAlpha = 0x00000002;

    public const uint MonitorDefaultToNearest = 2;

    public const uint DwmwaWindowCornerPreference = 33;
    public const int DwmwcpRound = 2;

    public const int SmCyFrame = 33;
    public const int SmCxPaddedBorder = 92;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowClass
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    /// <summary>As thick as the sizing border Windows gives a framed window.</summary>
    public static int ResizeBorderThickness(nint window)
    {
        var dpi = GetDpiForWindow(window);
        return GetSystemMetricsForDpi(SmCyFrame, dpi) + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
    }

    [LibraryImport("user32.dll")]
    public static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll")]
    public static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    public static partial nint CallWindowProcW(nint previous, nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint window, Point* point);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint window, uint relation);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(Point* point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromRect(Rect* rect, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(nint monitor, MonitorInfo* info);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindowDpiAwarenessContext(nint window);

    [LibraryImport("user32.dll")]
    public static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    public static partial ushort RegisterClassExW(WindowClass* windowClass);

    [LibraryImport("user32.dll")]
    public static partial nint CreateWindowExW(
        uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetModuleHandleW(char* name);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint window, uint attribute, void* value, uint size);
}
