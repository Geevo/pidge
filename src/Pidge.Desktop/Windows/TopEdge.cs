using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Pidge.Desktop.Windows.NativeMethods;

namespace Pidge.Desktop.Windows;

/// <summary>
/// The top of the sizing border, where the title bar was.
///
/// The page fills the window, and the webview's own windows take the mouse
/// over it, so the frame never hears of a press along its top. A window of
/// its own, owned by the frame and so always just above it, answers there
/// instead as the top of the sizing border: a drag resizes, and a double click
/// stretches the window to the height of the screen, as on any other window.
///
/// It is layered and all but transparent, so it hides none of the page; fully
/// transparent, the mouse would pass straight through it. A top-level window
/// rather than a child, because a layered child needs the application manifest
/// to name Windows 8 or later.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class TopEdge
{
    private const string ClassName = "PidgeTopEdge";

    /// <summary>Makes the edge for <paramref name="owner"/>, hidden until <see cref="Follow"/> places it.</summary>
    public static nint Create(nint owner)
    {
        var instance = GetModuleHandleW(null);
        fixed (char* name = ClassName)
        {
            var windowClass = new WindowClass
            {
                Size = (uint)sizeof(WindowClass),
                WindowProc = &WindowProc,
                Instance = instance,
                ClassName = name,
            };
            if (RegisterClassExW(&windowClass) == 0)
            {
                return 0;
            }

            // Made as DPI aware as its owner, or Windows would scale one of the
            // two and the edge would sit somewhere else.
            var previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(owner));
            var edge = CreateWindowExW(
                WsExLayered | WsExToolWindow | WsExNoActivate, name, null, WsPopup,
                0, 0, 0, 0, owner, 0, instance, 0);
            SetThreadDpiAwarenessContext(previous);

            if (edge != 0)
            {
                SetLayeredWindowAttributes(edge, 0, 1, LwaAlpha);
            }
            return edge;
        }
    }

    /// <summary>
    /// Lays the edge along the top of <paramref name="owner"/>, or hides it
    /// while the owner is hidden, minimized or maximized, when there is no top
    /// edge to drag.
    /// </summary>
    public static void Follow(nint edge, nint owner)
    {
        if (edge == 0)
        {
            return;
        }
        if (!IsWindowVisible(owner) || IsZoomed(owner) || IsIconic(owner))
        {
            ShowWindow(edge, SwHide);
            return;
        }

        GetClientRect(owner, out var client);
        var origin = new Point();
        ClientToScreen(owner, &origin);
        SetWindowPos(
            edge, 0, origin.X, origin.Y, client.Right, ResizeBorderThickness(owner),
            SwpNoActivate | SwpNoZOrder | SwpShowWindow);
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmNcHitTest:
                {
                    GetWindowRect(window, out var rect);
                    var x = (short)(lParam & 0xFFFF);
                    var corner = 2 * ResizeBorderThickness(window);
                    return x < rect.Left + corner ? HtTopLeft
                        : x >= rect.Right - corner ? HtTopRight
                        : HtTop;
                }

            case WmNcLButtonDown:
            case WmNcLButtonDblClk:
                {
                    // The owner takes these as presses on its own border. This
                    // window is never active, so a press activates the owner,
                    // as one on its border would.
                    var owner = GetWindow(window, GwOwner);
                    if (message == WmNcLButtonDown)
                    {
                        SetForegroundWindow(owner);
                    }
                    return SendMessageW(owner, message, wParam, lParam);
                }

            case WmMouseActivate:
                return MaNoActivate;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }
}
