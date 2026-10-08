using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Pidge.Desktop.Gtk.NativeMethods;

namespace Pidge.Desktop.Gtk;

/// <summary>
/// The window's frame on Linux, and the page's title bar moving it.
///
/// Under Wayland the window keeps GTK's own frame, for its shadow and for
/// resizing from just outside the edges. GTK draws that frame itself only for a
/// window with a custom titlebar; without one it asks the compositor (KWin, for
/// one) for a title bar. So the window gets an empty titlebar, kept hidden,
/// since shown the theme would give it a header bar's height. Under X11 the
/// window stays undecorated, and resizes from a strip just inside each edge.
///
/// A press in one of the page's drag regions starts a move once the pointer has
/// moved, as on a GTK title bar, and a double press toggles maximise. Started
/// on the press itself, the move hands the pointer to the compositor, and under
/// KDE Plasma on Wayland the page then never saw the release, nor the second
/// press of a double click: the window jumped to the pointer instead of
/// maximising, and clicks went missing after.
///
/// Everything here goes by the GtkWindow alone, so it does not care which
/// library made the window, only that the webview is its child.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe class GtkFrame
{
    /// <summary>The strip inside each edge that resizes an undecorated window.</summary>
    private const int InsideResizeBorder = 8;

    private static nint _window;
    private static int _resizeBorder;
    private static DragRegion[] _dragRegions = [];

    /// <summary>The press a move waits on, until the pointer moves or lets go.</summary>
    private static GdkEventButton? _press;

    /// <summary>
    /// Call once, after the window exists and before it is shown. Answers how
    /// far inside its edges the window should still resize from: none when GTK's
    /// frame does it from outside.
    /// </summary>
    public static int Apply(nint window)
    {
        _window = window;
        _resizeBorder = IsWayland(window) ? 0 : InsideResizeBorder;

        if (_resizeBorder == 0)
        {
            var titlebar = GtkWindowGetTitlebar(window);
            if (titlebar == 0)
            {
                titlebar = GtkBoxNew(0, 0);
                GtkWindowSetTitlebar(window, titlebar);
            }
            GtkWidgetSetNoShowAll(titlebar, 1);
            GtkWidgetHide(titlebar);
            GtkWindowSetDecorated(window, 1);
        }

        var webview = GtkBinGetChild(window);
        GtkWidgetAddEvents(webview, GdkButtonPressMask | GdkButtonReleaseMask | GdkButton1MotionMask);
        Connect(webview, "button-press-event", (nint)(delegate* unmanaged<nint, GdkEventButton*, nint, int>)&OnButtonPress);
        Connect(webview, "motion-notify-event", (nint)(delegate* unmanaged<nint, GdkEventMotion*, nint, int>)&OnMotion);
        Connect(webview, "button-release-event", (nint)(delegate* unmanaged<nint, GdkEventButton*, nint, int>)&OnButtonRelease);

        return _resizeBorder;
    }

    /// <summary>Where the page's title bar is, in the webview's pixels. Replaces the last set.</summary>
    public static void SetDragRegions(IEnumerable<DragRegion> regions) => _dragRegions = [.. regions];

    private static bool IsWayland(nint window)
    {
        try
        {
            return GTypeCheckInstanceIsA(GtkWidgetGetDisplay(window), GdkWaylandDisplayGetType()) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            // GDK built without Wayland.
            return false;
        }
    }

    private static void Connect(nint widget, string signal, nint handler) =>
        GSignalConnectData(widget, signal, handler, 0, 0, 0);

    [UnmanagedCallersOnly]
    private static int OnButtonPress(nint widget, GdkEventButton* e, nint data)
    {
        if (e->Button != PrimaryButton || !InDragRegion(widget, e->X, e->Y))
        {
            return 0;
        }

        if (e->Type == Gdk2ButtonPress)
        {
            _press = null;
            if (GtkWindowIsMaximized(_window) != 0)
            {
                GtkWindowUnmaximize(_window);
            }
            else
            {
                GtkWindowMaximize(_window);
            }
            return 1;
        }

        // The third press of a triple click is the title bar's too.
        if (e->Type == GdkButtonPress)
        {
            _press = *e;
        }
        return 1;
    }

    [UnmanagedCallersOnly]
    private static int OnMotion(nint widget, GdkEventMotion* e, nint data)
    {
        if (_press is not { } press)
        {
            return 0;
        }

        if ((e->State & GdkButton1Mask) == 0)
        {
            // Let go somewhere the release never reached.
            _press = null;
            return 0;
        }

        if (GtkDragCheckThreshold(widget, (int)press.X, (int)press.Y, (int)e->X, (int)e->Y) == 0)
        {
            return 1;
        }

        _press = null;
        GtkWindowBeginMoveDrag(_window, (int)press.Button, (int)press.XRoot, (int)press.YRoot, e->Time);
        return 1;
    }

    [UnmanagedCallersOnly]
    private static int OnButtonRelease(nint widget, GdkEventButton* e, nint data)
    {
        if (_press is not { } press || e->Button != press.Button)
        {
            return 0;
        }

        // The press never reached the page, so neither does its release.
        _press = null;
        return 1;
    }

    private static bool InDragRegion(nint widget, double pointX, double pointY)
    {
        var x = (int)pointX;
        var y = (int)pointY;

        // The resize strip wins over the title bar, except when maximised,
        // where there is nothing to resize.
        var border = GtkWindowIsMaximized(_window) != 0 ? 0 : _resizeBorder;
        if (x < border || y < border ||
            x >= GtkWidgetGetAllocatedWidth(widget) - border ||
            y >= GtkWidgetGetAllocatedHeight(widget) - border)
        {
            return false;
        }

        foreach (var region in _dragRegions)
        {
            if (x >= region.X && x < region.X + region.Width &&
                y >= region.Y && y < region.Y + region.Height)
            {
                return true;
            }
        }
        return false;
    }
}
