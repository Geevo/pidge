using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pidge.Desktop.Gtk;

/// <summary>The parts of GTK 3, GDK and GObject the frame is built from.</summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class NativeMethods
{
    private const string Gtk = "libgtk-3.so.0";
    private const string Gdk = "libgdk-3.so.0";
    private const string GObject = "libgobject-2.0.so.0";

    public const int GdkButtonPress = 4;
    public const int Gdk2ButtonPress = 5;

    public const int GdkButtonPressMask = 1 << 8;
    public const int GdkButtonReleaseMask = 1 << 9;
    public const int GdkButton1MotionMask = 1 << 5;

    /// <summary>The modifier state bit for the primary button being held.</summary>
    public const uint GdkButton1Mask = 1 << 8;

    public const uint PrimaryButton = 1;

    /// <summary>GdkEventButton, as laid out on 64-bit Linux.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GdkEventButton
    {
        public int Type;
        public nint Window;
        public sbyte SendEvent;
        public uint Time;
        public double X;
        public double Y;
        public nint Axes;
        public uint State;
        public uint Button;
        public nint Device;
        public double XRoot;
        public double YRoot;
    }

    /// <summary>GdkEventMotion, as laid out on 64-bit Linux.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GdkEventMotion
    {
        public int Type;
        public nint Window;
        public sbyte SendEvent;
        public uint Time;
        public double X;
        public double Y;
        public nint Axes;
        public uint State;
        public short IsHint;
        public nint Device;
        public double XRoot;
        public double YRoot;
    }

    [LibraryImport(Gtk, EntryPoint = "gtk_bin_get_child")]
    public static partial nint GtkBinGetChild(nint bin);

    [LibraryImport(Gtk, EntryPoint = "gtk_widget_get_display")]
    public static partial nint GtkWidgetGetDisplay(nint widget);

    [LibraryImport(Gtk, EntryPoint = "gtk_widget_add_events")]
    public static partial void GtkWidgetAddEvents(nint widget, int events);

    [LibraryImport(Gtk, EntryPoint = "gtk_widget_get_allocated_width")]
    public static partial int GtkWidgetGetAllocatedWidth(nint widget);

    [LibraryImport(Gtk, EntryPoint = "gtk_widget_get_allocated_height")]
    public static partial int GtkWidgetGetAllocatedHeight(nint widget);

    [LibraryImport(Gtk, EntryPoint = "gtk_widget_set_no_show_all")]
    public static partial void GtkWidgetSetNoShowAll(nint widget, int noShowAll);

    [LibraryImport(Gtk, EntryPoint = "gtk_widget_hide")]
    public static partial void GtkWidgetHide(nint widget);

    [LibraryImport(Gtk, EntryPoint = "gtk_box_new")]
    public static partial nint GtkBoxNew(int orientation, int spacing);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_get_titlebar")]
    public static partial nint GtkWindowGetTitlebar(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_set_titlebar")]
    public static partial void GtkWindowSetTitlebar(nint window, nint titlebar);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_set_decorated")]
    public static partial void GtkWindowSetDecorated(nint window, int decorated);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_is_maximized")]
    public static partial int GtkWindowIsMaximized(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_maximize")]
    public static partial void GtkWindowMaximize(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_unmaximize")]
    public static partial void GtkWindowUnmaximize(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_begin_move_drag")]
    public static partial void GtkWindowBeginMoveDrag(nint window, int button, int rootX, int rootY, uint timestamp);

    [LibraryImport(Gtk, EntryPoint = "gtk_drag_check_threshold")]
    public static partial int GtkDragCheckThreshold(nint widget, int startX, int startY, int currentX, int currentY);

    [LibraryImport(Gdk, EntryPoint = "gdk_wayland_display_get_type")]
    public static partial nuint GdkWaylandDisplayGetType();

    [LibraryImport(GObject, EntryPoint = "g_type_check_instance_is_a")]
    public static partial int GTypeCheckInstanceIsA(nint instance, nuint type);

    [LibraryImport(GObject, StringMarshalling = StringMarshalling.Utf8, EntryPoint = "g_signal_connect_data")]
    public static partial nuint GSignalConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);
}
