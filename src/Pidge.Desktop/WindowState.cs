using Photino.NET;
using Pidge.Session;
using Pidge.Storage;

namespace Pidge.Desktop;

/*
 * Opening the window where it was left: same monitor, place, size, and
 * maximised or not.
 *
 * The window is created hidden and put in place before it is shown, so it never
 * appears in one spot and jumps to another. The placement is kept in memory as
 * the window moves and saved with everything else; closing the window saves
 * once more.
 *
 * Wayland lets no application read or set its own position, so there the
 * compositor picks where the window opens and only the size and the maximised
 * state come back.
 */
internal static class WindowState
{
    /// <summary>
    /// Puts the window where it was last. A placement that cannot be applied is
    /// no reason to have no window, so failures are logged and skipped.
    /// </summary>
    public static void Restore(PhotinoWindow window, WindowPlacement? placement)
    {
        if (placement is null)
        {
            return;
        }
        try
        {
            Apply(window, placement);
        }
        catch (Exception e)
        {
            Log.Warn($"could not restore the window placement: {e.Message}");
        }
    }

    /// <summary>Notes where the window is now. Called as it moves and resizes.</summary>
    public static void Remember(PhotinoWindow window, AppSession session)
    {
        try
        {
            if (PlacementOf(window, session.GetWindowPlacement()) is { } placement)
            {
                session.SetWindowPlacement(placement);
            }
        }
        catch (Exception e)
        {
            Log.Debug($"could not read the window placement: {e.Message}");
        }
    }

    private static void Apply(PhotinoWindow window, WindowPlacement placement)
    {
        var screens = Screens.All(window);
        var (width, height) = (placement.Width, placement.Height);

        if (OnScreen(placement, screens))
        {
            // Position first: on a monitor with a different scale, the logical
            // size is then measured in that monitor's pixels.
            Screens.MoveTo(window, placement.X, placement.Y);
            Screens.Resize(window, width, height);
        }
        else
        {
            // The monitor has gone, or moved: keep the size, as far as the main
            // monitor has room for it, and start in the middle.
            if (Screens.PrimaryRoom(window) is { } room)
            {
                width = Math.Min(width, room.Width);
                height = Math.Min(height, room.Height);
            }
            Screens.Resize(window, width, height);
            window.Center();
        }

        if (placement.Maximized)
        {
            window.SetMaximized(true);
        }
    }

    /// <summary>Null while minimised, when the place on screen means nothing.</summary>
    private static WindowPlacement? PlacementOf(PhotinoWindow window, WindowPlacement? last)
    {
        var state = window.WindowState;
        if (state == PhotinoWindowState.Minimized)
        {
            return null;
        }
        var maximized = state == PhotinoWindowState.Maximized;
        if (maximized && last is not null)
        {
            // Keep the size and place from before, to come back to when it is
            // restored down.
            return new WindowPlacement
            {
                Monitor = last.Monitor,
                X = last.X,
                Y = last.Y,
                Width = last.Width,
                Height = last.Height,
                Maximized = true,
            };
        }

        var (width, height) = Screens.InnerSize(window);
        var (x, y) = Screens.Position(window);
        return new WindowPlacement
        {
            Monitor = Screens.MonitorOf(window),
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Maximized = maximized,
        };
    }

    /// <summary>
    /// Whether the window would come back with its title bar on a monitor that
    /// is still there: the one it was on, when both have a name to go by.
    ///
    /// The point tested is a little inside the top-left corner, clear of the
    /// invisible resize border Windows puts around a window, which can hang off
    /// the edge of a monitor while the window itself does not.
    /// </summary>
    internal static bool OnScreen(WindowPlacement placement, IReadOnlyList<Screen> screens)
    {
        var x = SaturatingAdd(placement.X, 40);
        var y = SaturatingAdd(placement.Y, 16);
        return screens.Any(screen =>
        {
            var sameMonitor = placement.Monitor is null || screen.Name is null || placement.Monitor == screen.Name;
            return sameMonitor && screen.Contains(x, y);
        });
    }

    private static int SaturatingAdd(int value, int amount) =>
        (int)Math.Clamp((long)value + amount, int.MinValue, int.MaxValue);
}
