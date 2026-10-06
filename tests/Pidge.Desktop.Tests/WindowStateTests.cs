using Pidge.Storage;

namespace Pidge.Desktop.Tests;

public class WindowStateTests
{
    private static WindowPlacement Placement(string? monitor, int x, int y) => new()
    {
        Monitor = monitor,
        X = x,
        Y = y,
        Width = 1100,
        Height = 740,
        Maximized = false,
    };

    private static Screen Screen(string? name, int x, int y) => new(name, x, y, 1920, 1080);

    [Fact]
    public void ComesBackOnTheSameMonitor()
    {
        Screen[] screens = [Screen("DP-1", 0, 0), Screen("HDMI-1", 1920, 0)];
        Assert.True(WindowState.OnScreen(Placement("HDMI-1", 2200, 100), screens));
    }

    [Fact]
    public void DoesNotWhenThatMonitorIsUnplugged()
    {
        Screen[] screens = [Screen("DP-1", 0, 0)];
        Assert.False(WindowState.OnScreen(Placement("HDMI-1", 2200, 100), screens));
    }

    [Fact]
    public void DoesNotWhenAnotherMonitorNowHasThatSpot()
    {
        // Same desktop coordinates, different monitor: the layout changed.
        Screen[] screens = [Screen("DP-1", 0, 0), Screen("DP-2", 1920, 0)];
        Assert.False(WindowState.OnScreen(Placement("HDMI-1", 2200, 100), screens));
    }

    [Fact]
    public void AllowsABorderHangingOffTheEdge()
    {
        Screen[] screens = [Screen("DP-1", 0, 0)];
        Assert.True(WindowState.OnScreen(Placement("DP-1", -7, 0), screens));
    }

    [Fact]
    public void DoesNotWhenTheTitleBarWouldBeOffScreen()
    {
        Screen[] screens = [Screen("DP-1", 0, 0)];
        Assert.False(WindowState.OnScreen(Placement("DP-1", 100, -200), screens));
        Assert.False(WindowState.OnScreen(Placement("DP-1", 1900, 100), screens));
    }

    [Fact]
    public void GoesByPositionAloneWithoutNames()
    {
        Assert.True(WindowState.OnScreen(Placement("DP-1", 100, 100), [Screen(null, 0, 0)]));
        Assert.True(WindowState.OnScreen(Placement(null, 100, 100), [Screen("DP-1", 0, 0)]));
    }

    [Fact]
    public void CopesWithMonitorsLeftOfAndAboveTheMainOne()
    {
        Screen[] screens = [Screen("DP-1", 0, 0), Screen("DP-2", -1920, -300)];
        Assert.True(WindowState.OnScreen(Placement("DP-2", -1800, -200), screens));
    }

    [Fact]
    public void DoesNotOverflowNearTheEdgeOfTheCoordinateSpace()
    {
        Screen[] screens = [Screen("DP-1", 0, 0)];
        Assert.False(WindowState.OnScreen(Placement("DP-1", int.MaxValue, int.MaxValue), screens));
    }
}

public class WindowButtonsTests
{
    [Fact]
    public void ReadsTheDesktopOutOfTheXdgVariable()
    {
        Assert.Equal(WindowButtons.Kde, Host.ButtonsForDesktop("KDE"));
        Assert.Equal(WindowButtons.Kde, Host.ButtonsForDesktop("kde"));
        // Several desktops, in the order the session set them.
        Assert.Equal(WindowButtons.Kde, Host.ButtonsForDesktop("KDE:X-Cinnamon"));
        Assert.Equal(WindowButtons.Gnome, Host.ButtonsForDesktop("ubuntu:GNOME"));
    }

    /// <summary>A desktop nobody here has heard of, and a session that sets nothing.</summary>
    [Fact]
    public void FallsBackToTheMoreCommonShape()
    {
        Assert.Equal(WindowButtons.Gnome, Host.ButtonsForDesktop("Sway"));
        Assert.Equal(WindowButtons.Gnome, Host.ButtonsForDesktop(null));
    }

    [Fact]
    public void NamesTheButtonsAsTheUiExpects()
    {
        Assert.Equal("windows", Host.ButtonsName(WindowButtons.Windows));
        Assert.Equal("kde", Host.ButtonsName(WindowButtons.Kde));
        Assert.Equal("gnome", Host.ButtonsName(WindowButtons.Gnome));
    }
}

public class AssetsTests
{
    [Fact]
    public void ServesTheAppWithAPolicy()
    {
        using var stream = Assets.Open("app://localhost/index.html", out var contentType);
        Assert.NotNull(stream);
        Assert.Equal("text/html; charset=utf-8", contentType);
        var html = new StreamReader(stream).ReadToEnd();
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("<div id=\"root\">", html);
    }

    [Fact]
    public void TheRootIsTheIndex()
    {
        using var stream = Assets.Open("app://localhost/", out _);
        Assert.NotNull(stream);
    }

    [Fact]
    public void RefusesWhatIsNotPartOfTheApp()
    {
        Assert.Null(Assets.Open("app://localhost/missing.js", out _));
        Assert.Null(Assets.Open("app://localhost/../secrets.txt", out _));
        Assert.Null(Assets.Open("not a url", out _));
    }

    [Fact]
    public void ServesScriptsAsJavaScript()
    {
        Assert.Equal("text/javascript; charset=utf-8", Assets.ContentTypeOf("assets/index-abc.js"));
        Assert.Equal("font/woff2", Assets.ContentTypeOf("assets/plex.woff2"));
    }
}
