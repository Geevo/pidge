using System.Runtime.InteropServices;
using Photino.NET;

namespace Pidge.Desktop;

/// <summary>A monitor's name and its rectangle in desktop coordinates.</summary>
internal readonly record struct Screen(string? Name, int X, int Y, uint Width, uint Height)
{
    public bool Contains(int x, int y)
    {
        var right = (long)X + Width;
        var bottom = (long)Y + Height;
        return x >= X && y >= Y && x < right && y < bottom;
    }
}

/// <summary>
/// Where the window is and what it is on, in the units the saved placement
/// uses: the outer position in physical pixels, the inner size in logical ones.
///
/// On Windows that is read from the window itself, because the monitor names
/// are the operating system's (<c>\\.\DISPLAY1</c>), the same ones every
/// earlier build saved, and the size the user sees is the client area. Elsewhere
/// the window's own numbers are already logical and monitors have no names, so
/// placement goes by position alone.
/// </summary>
internal static partial class Screens
{
    public static List<Screen> All(PhotinoWindow window)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32.Monitors();
        }
        return window.Monitors
            .Select(m => new Screen(null, m.MonitorArea.X, m.MonitorArea.Y, (uint)m.MonitorArea.Width, (uint)m.MonitorArea.Height))
            .ToList();
    }

    /// <summary>The main monitor's working area, in logical pixels.</summary>
    public static (uint Width, uint Height)? PrimaryRoom(PhotinoWindow window)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32.PrimaryRoom();
        }
        var monitor = window.Monitors.FirstOrDefault();
        var scale = monitor.Scale > 0 ? monitor.Scale : 1.0;
        return monitor.WorkArea.Width > 0
            ? ((uint)(monitor.WorkArea.Width / scale), (uint)(monitor.WorkArea.Height / scale))
            : null;
    }

    public static string? MonitorOf(PhotinoWindow window) =>
        OperatingSystem.IsWindows() ? Win32.MonitorName(window.WindowHandle) : null;

    public static (int X, int Y) Position(PhotinoWindow window) =>
        OperatingSystem.IsWindows() && Win32.WindowRect(window.WindowHandle) is { } rect
            ? (rect.Left, rect.Top)
            : (window.Location.X, window.Location.Y);

    public static (uint Width, uint Height) InnerSize(PhotinoWindow window)
    {
        if (OperatingSystem.IsWindows() && Win32.ClientSize(window.WindowHandle) is { } client)
        {
            var scale = Win32.Scale(window.WindowHandle);
            return ((uint)Math.Round(client.Width / scale), (uint)Math.Round(client.Height / scale));
        }
        return ((uint)Math.Max(0, window.Size.Width), (uint)Math.Max(0, window.Size.Height));
    }

    public static void MoveTo(PhotinoWindow window, int x, int y)
    {
        if (OperatingSystem.IsWindows())
        {
            Win32.Move(window.WindowHandle, x, y);
            return;
        }
        window.SetLocation(new System.Drawing.Point(x, y));
    }

    /// <summary>
    /// Sizes the inner area in logical pixels, measured in the pixels of the
    /// monitor the window is on now — which is why it comes after the move.
    /// </summary>
    public static void Resize(PhotinoWindow window, uint width, uint height)
    {
        if (OperatingSystem.IsWindows())
        {
            Win32.ResizeClient(window.WindowHandle, width, height);
            return;
        }
        window.SetSize((int)width, (int)height);
    }

    private static partial class Win32
    {
        private const uint MonitorDefaultToNearest = 2;
        private const uint MonitorInfoPrimary = 1;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private unsafe struct MonitorInfoEx
        {
            public uint Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
            public fixed char Device[32];
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool EnumDisplayMonitors(
            nint hdc, nint clip, delegate* unmanaged<nint, nint, Rect*, nint, int> callback, nint data);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool GetMonitorInfo(nint monitor, MonitorInfoEx* info);

        [LibraryImport("user32.dll")]
        private static partial nint MonitorFromWindow(nint window, uint flags);

        [LibraryImport("user32.dll")]
        private static partial uint GetDpiForWindow(nint window);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(nint window, out Rect rect);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetClientRect(nint window, out Rect rect);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

        [ThreadStatic]
        private static List<(Screen Screen, bool Primary, Rect Work, uint Dpi)>? _found;

        private static unsafe List<(Screen Screen, bool Primary, Rect Work, uint Dpi)> Enumerate()
        {
            // The callback has nowhere else to put what it finds.
            _found = [];
            EnumDisplayMonitors(0, 0, &Collect, 0);
            var found = _found;
            _found = null;
            return found;
        }

        [UnmanagedCallersOnly]
        private static unsafe int Collect(nint monitor, nint hdc, Rect* clip, nint data)
        {
            var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
            if (GetMonitorInfo(monitor, &info))
            {
                var name = new string(info.Device);
                var r = info.Monitor;
                _found?.Add((
                    new Screen(name, r.Left, r.Top, (uint)(r.Right - r.Left), (uint)(r.Bottom - r.Top)),
                    (info.Flags & MonitorInfoPrimary) != 0,
                    info.Work,
                    DpiOf(monitor)));
            }
            return 1;
        }

        [LibraryImport("shcore.dll")]
        private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

        private static uint DpiOf(nint monitor) =>
            GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi : 96;

        public static List<Screen> Monitors() => Enumerate().Select(m => m.Screen).ToList();

        public static (uint Width, uint Height)? PrimaryRoom()
        {
            foreach (var (_, primary, work, dpi) in Enumerate())
            {
                if (primary)
                {
                    var scale = dpi / 96.0;
                    return ((uint)((work.Right - work.Left) / scale), (uint)((work.Bottom - work.Top) / scale));
                }
            }
            return null;
        }

        public static unsafe string? MonitorName(nint window)
        {
            var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
            var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
            return monitor != 0 && GetMonitorInfo(monitor, &info) ? new string(info.Device) : null;
        }

        public static double Scale(nint window)
        {
            var dpi = GetDpiForWindow(window);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }

        public static Rect? WindowRect(nint window) => GetWindowRect(window, out var rect) ? rect : null;

        public static (int Width, int Height)? ClientSize(nint window) =>
            GetClientRect(window, out var rect) ? (rect.Right - rect.Left, rect.Bottom - rect.Top) : null;

        public static void Move(nint window, int x, int y) =>
            SetWindowPos(window, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

        public static void ResizeClient(nint window, uint width, uint height)
        {
            if (!GetWindowRect(window, out var outer) || !GetClientRect(window, out var inner))
            {
                return;
            }
            var scale = Scale(window);
            var frameWidth = (outer.Right - outer.Left) - (inner.Right - inner.Left);
            var frameHeight = (outer.Bottom - outer.Top) - (inner.Bottom - inner.Top);
            SetWindowPos(
                window, 0, 0, 0,
                (int)Math.Round(width * scale) + frameWidth,
                (int)Math.Round(height * scale) + frameHeight,
                SwpNoMove | SwpNoZOrder | SwpNoActivate);
        }
    }
}
