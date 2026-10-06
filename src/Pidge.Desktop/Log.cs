namespace Pidge.Desktop;

/// <summary>
/// Diagnostics on stderr, for whoever starts the app from a terminal.
/// <c>PIDGE_LOG=debug</c> shows more; the default is warnings and up.
/// </summary>
internal static class Log
{
    private static readonly bool Verbose =
        string.Equals(Environment.GetEnvironmentVariable("PIDGE_LOG"), "debug", StringComparison.OrdinalIgnoreCase);

    public static void Warn(string message) => Write("WARN", message);

    public static void Debug(string message)
    {
        if (Verbose)
        {
            Write("DEBUG", message);
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            Console.Error.WriteLine($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {level,5} {message}");
        }
        catch (IOException)
        {
            // No console to write to is no reason to stop.
        }
    }
}
