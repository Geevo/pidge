using Photino.NET;
using Pidge.Core;
using Pidge.Desktop.Windows;
using Pidge.Session;
using Pidge.Storage;

namespace Pidge.Desktop;

/// <summary>
/// The desktop shell: one undecorated window with the UI in it, and the
/// session behind it. Nothing here decides anything about requests or storage.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        if (!NativeLibraries.Load(Path.Combine(LocalDataRoot(), "native")))
        {
            Console.Error.WriteLine("could not start: the window host could not be written out");
            return 1;
        }

        DataPaths.AdoptLegacyDataDir();
        var dataDir = DataPaths.DefaultDataDir();
        var store = Store.InDir(dataDir).WithSystemKeyring();

        AppSession session;
        try
        {
            session = AppSession.Start(store);
        }
        catch (RequestErrorException e)
        {
            // Nothing useful can happen without an HTTP client.
            Console.Error.WriteLine($"could not start: {e.Error.Message}");
            return 1;
        }

        if (OperatingSystem.IsLinux())
        {
            Linux.SetProgramName("pidge");
        }

        var devServer = Environment.GetEnvironmentVariable("PIDGE_DEV_SERVER");
        var app = new PhotinoApplication();
        var window = new PhotinoWindow()
            .SetLogVerbosity(IsDebugBuild ? 1 : 0)
            .SetTitle("pidge")
            .SetChromeless(true)
            .SetResizable(true)
            .SetSize(1100, 740)
            .SetMinSize(640, 420)
            .SetUseOsDefaultLocation(false)
            .SetUseOsDefaultSize(false)
            .SetDevToolsEnabled(devServer is not null || IsDebugBuild)
            .SetStatusBarEnabled(false)
            .SetZoomEnabled(false)
            .SetUserDataFolder(Path.Combine(LocalDataRoot(), "webview"));
        window.CenterOnInitialize = true;

        if (Assets.IconFile(Path.Combine(LocalDataRoot(), "icon")) is { } icon)
        {
            window.SetIconFile(icon);
        }

        if (devServer is null)
        {
            window.RegisterCustomSchemeHandler(Assets.Scheme, (PhotinoWindow _, string _, string url, out string? contentType) =>
                Assets.Open(url, out contentType));
            window.Load(Assets.StartUrl);
        }
        else
        {
            window.Load(new Uri(devServer));
        }

        var host = new Host(window, session);
        window.RegisterWebMessageReceivedHandler(host.OnMessage);
        window.RegisterNavigationStartingHandler((_, e) =>
        {
            // The window shows the app and nothing else. A link that would
            // replace it goes nowhere.
            var allowed = devServer is null
                ? e.Uri.Scheme == Assets.Scheme
                : e.Uri.GetLeftPart(UriPartial.Authority) == new Uri(devServer).GetLeftPart(UriPartial.Authority);
            if (!allowed)
            {
                e.Cancel = true;
            }
        });

        window.RegisterLocationChangedHandler((_, _) => WindowState.Remember(window, session));
        window.RegisterSizeChangedHandler((_, _) =>
        {
            WindowState.Remember(window, session);
            host.Resized();
        });
        window.RegisterStateChangedHandler((_, _) =>
        {
            WindowState.Remember(window, session);
            host.Resized();
        });
        window.RegisterClosingHandler((_, _) =>
        {
            WindowState.Remember(window, session);
            try
            {
                session.Persist();
            }
            catch (Exception e)
            {
                Log.Warn($"could not save on close: {e.Message}");
            }
        });

        // Created hidden and put in place before it is shown, so it never
        // appears in one spot and jumps to another.
        window.Initialize();
        if (OperatingSystem.IsWindows())
        {
            WindowsFrame.Apply(window.WindowHandle);
        }
        WindowState.Restore(window, session.GetWindowPlacement());

        return app.Run(window);
    }

#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    /// <summary>
    /// Where the window keeps what it writes for itself (the webview's profile,
    /// the icon, the native libraries): the machine-local data folder, not the
    /// roaming one the app's state lives in, because none of it is worth
    /// carrying between machines. Not checked for existence: on a first start
    /// on Linux it may not be there yet, and the check would answer "" and put
    /// everything in the current directory.
    /// </summary>
    private static string LocalDataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify), "pidge");
}
