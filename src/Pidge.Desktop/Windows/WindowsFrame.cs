using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Pidge.Desktop.Windows.NativeMethods;

namespace Pidge.Desktop.Windows;

/// <summary>
/// The window's frame on Windows: an ordinary overlapped window without a
/// title bar.
///
/// A chromeless window starts out as a bare popup, and Windows animates only
/// windows with a caption and a sizing border. A popup snaps to maximized,
/// vanishes when minimized, and cannot be minimized from its taskbar button.
/// So the window gets the usual styles back, and answers WM_NCCALCSIZE by
/// giving the title bar to the page and nothing else. The sides and bottom
/// keep the system's sizing border, invisible but for a line, so they resize
/// from just outside the window as any other window's do; the top is
/// <see cref="TopEdge"/>'s.
///
/// Everything here goes by the window handle alone, so it does not care which
/// library made the window.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WindowsFrame
{
    /// <summary>The window procedure underneath, which handles everything else.</summary>
    private static nint _previous;

    private static nint _topEdge;

    /// <summary>Call once, after the window exists and before it is shown.</summary>
    public static void Apply(nint window)
    {
        var style = (uint)GetWindowLongPtrW(window, GwlStyle);
        style = (style & ~WsPopup) | WsCaption | WsSysMenu | WsThickFrame | WsMinimizeBox | WsMaximizeBox;

        _previous = SetWindowLongPtrW(window, GwlpWndProc, (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WindowProc);
        SetWindowLongPtrW(window, GwlStyle, (nint)style);

        // Has the frame measured again, through the procedure above.
        SetWindowPos(window, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        // Windows 11 rounds a window's corners only when it draws the frame,
        // so ask for them. Earlier versions refuse, and keep square corners.
        var round = DwmwcpRound;
        DwmSetWindowAttribute(window, DwmwaWindowCornerPreference, &round, sizeof(int));

        _topEdge = TopEdge.Create(window);
    }

    /// <summary>Starts a move, as a press on a real title bar would, so dragging to an edge snaps.</summary>
    public static void BeginDrag(nint window)
    {
        Point point;
        if (!GetCursorPos(&point))
        {
            return;
        }

        // The page holds the mouse; let go of it so the system can take it.
        ReleaseCapture();
        PostMessageW(window, WmNcLButtonDown, HtCaption, (nint)((point.Y << 16) | (point.X & 0xFFFF)));
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmNcCalcSize:
                MeasureClient(window, wParam, (Rect*)lParam);
                return 0;

            case WmWindowPosChanged:
                {
                    var result = CallWindowProcW(_previous, window, message, wParam, lParam);
                    TopEdge.Follow(_topEdge, window);
                    return result;
                }
        }

        return CallWindowProcW(_previous, window, message, wParam, lParam);
    }

    /// <summary>
    /// Turns the proposed window rectangle into the client area: the system's
    /// own measure, but reaching to the top of the window.
    /// </summary>
    private static void MeasureClient(nint window, nint wParam, Rect* proposed)
    {
        // Whatever wParam says, lParam starts with the proposed window rectangle.
        var top = proposed->Top;
        DefWindowProcW(window, WmNcCalcSize, wParam, (nint)proposed);
        proposed->Top = top;

        // Maximized, a window with a sizing border overhangs the screen by that
        // border on every side, the top as well; keep to the screen.
        if (!IsZoomed(window))
        {
            return;
        }
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        if (GetMonitorInfoW(MonitorFromRect(proposed, MonitorDefaultToNearest), &info))
        {
            proposed->Left = Math.Max(proposed->Left, info.Work.Left);
            proposed->Top = Math.Max(proposed->Top, info.Work.Top);
            proposed->Right = Math.Min(proposed->Right, info.Work.Right);
            proposed->Bottom = Math.Min(proposed->Bottom, info.Work.Bottom);
        }
    }
}
