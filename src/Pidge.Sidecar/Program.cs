using System.Diagnostics;
using Pidge.Core;
using Pidge.Protocol;
using Pidge.Sidecar;
using Pidge.Storage;

/*
 * The VS Code sidecar.
 *
 * The extension host starts one of these per window and talks to it over
 * stdin/stdout. It owns no UI logic; it is the same engine the desktop app
 * calls directly, wrapped in a line reader.
 */

string? stateDir = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        // VS Code passes its global storage directory here, so the client
        // keeps its data outside any workspace.
        case "--state-dir":
            if (i + 1 >= args.Length)
            {
                Console.Error.Write("--state-dir needs a path\n");
                return 1;
            }
            stateDir = args[++i];
            break;
        case "--version" or "-V":
            PrintLine(AppVersion.Current);
            return 0;
        case "--protocol-version":
            PrintLine(WireFormat.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return 0;
        case "--help" or "-h":
            PrintHelp();
            return 0;
        default:
            Console.Error.Write($"unknown argument: {args[i]}\n");
            PrintHelp();
            return 1;
    }
}

Logging.Init();

Store store;
if (stateDir is not null)
{
    store = Store.InDir(stateDir);
}
else
{
    DataPaths.AdoptLegacyDataDir();
    store = Store.InDir(DataPaths.DefaultDataDir());
}
store = store.WithSystemKeyring();

try
{
    await SidecarServer.RunAsync(store, Console.OpenStandardInput(), Console.OpenStandardOutput());
    return 0;
}
catch (Exception e)
{
    Trace.TraceError($"sidecar exiting: {e.Message}");
    return 1;
}

// Not Console.WriteLine: stdout ends lines with \n on every platform.
static void PrintLine(string text)
{
    using var stdout = Console.OpenStandardOutput();
    stdout.Write(System.Text.Encoding.UTF8.GetBytes(text + "\n"));
}

static void PrintHelp() =>
    Console.Error.Write(
        $"api-client-sidecar {AppVersion.Current}\n\n"
        + "Reads newline-delimited JSON protocol messages on stdin and writes them on stdout.\n\n"
        + "Options:\n"
        + "      --state-dir <path>      directory for the state file\n"
        + "  -V, --version           print the binary version\n"
        + "      --protocol-version      print the supported protocol version\n"
        + "  -h, --help              print this message\n");
