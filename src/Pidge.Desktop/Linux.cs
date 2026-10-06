using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pidge.Desktop;

/// <summary>What the window needs from GLib that the window host leaves unset.</summary>
[SupportedOSPlatform("linux")]
internal static partial class Linux
{
    /// <summary>
    /// Names the program for GTK, which sends the name as the window's app id
    /// on Wayland. A compositor finds the window's icon through the desktop
    /// entry with the same name, so the two have to agree, whatever the
    /// executable was started as.
    /// </summary>
    public static void SetProgramName(string name) => SetPrgname(name);

    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_set_prgname", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void SetPrgname(string name);
}
