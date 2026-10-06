namespace Pidge.Storage;

public static class DataPaths
{
    private const string AppDir = "pidge";

    /// <summary>Where the data lived before the app was called pidge.</summary>
    private const string LegacyAppDir = "api-client";

    /// <summary>
    /// Where the desktop app keeps its data.
    ///
    /// Resolved by hand from the environment rather than with
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>, whose
    /// answers differ by platform and runtime; these rules are three lines long
    /// and this way there is nothing to disagree about.
    /// </summary>
    public static string DefaultDataDir() => DataDirNamed(AppDir);

    /// <summary>
    /// Moves the data directory from before the rename into place, so an upgrade
    /// opens on the same tabs, history and saved requests. See <see cref="AdoptLegacyDir"/>.
    /// </summary>
    public static void AdoptLegacyDataDir()
    {
        var (legacy, current) = (DataDirNamed(LegacyAppDir), DefaultDataDir());
        try
        {
            if (AdoptLegacyDir(legacy, current))
            {
                StorageLog.Info($"moved the data directory from {legacy} to {current}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The old directory is still there, so nothing is lost; the app opens
            // fresh and the move is tried again next time.
            StorageLog.Warn($"could not move the old data directory: {e.Message}");
        }
    }

    /// <summary>
    /// Renames <paramref name="legacy"/> to <paramref name="current"/> when only
    /// <paramref name="legacy"/> exists. True if it did.
    ///
    /// Once, and never over anything: if <paramref name="current"/> already
    /// exists, both are left alone. The key file moves with the state file, and
    /// nothing that protects the key depends on where it is, so encrypted
    /// secrets still open.
    /// </summary>
    public static bool AdoptLegacyDir(string legacy, string current)
    {
        if (Path.Exists(current) || !Directory.Exists(legacy))
        {
            return false;
        }
        Directory.Move(legacy, current);
        return true;
    }

    private static string DataDirNamed(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            if (NonEmpty("APPDATA") is { } appData)
            {
                return Path.Combine(appData, name);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (Environment.GetEnvironmentVariable("HOME") is { } home)
            {
                return Path.Combine(home, "Library", "Application Support", name);
            }
        }
        else
        {
            if (NonEmpty("XDG_DATA_HOME") is { } dataHome)
            {
                return Path.Combine(dataHome, name);
            }
            if (Environment.GetEnvironmentVariable("HOME") is { } home)
            {
                return Path.Combine(home, ".local", "share", name);
            }
        }

        return Path.Combine(Path.GetTempPath(), name);
    }

    /// <summary>
    /// The variable's value, treating an empty one as unset. Windows cannot hold
    /// an empty variable at all, and XDG says an empty XDG_DATA_HOME is unset.
    /// </summary>
    private static string? NonEmpty(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : null;
}
