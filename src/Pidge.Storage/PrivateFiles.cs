using System.Text;

namespace Pidge.Storage;

/// <summary>
/// Files only the user can read. On Windows, %APPDATA% is already private to
/// the user, so only the atomic write matters there.
/// </summary>
internal static class PrivateFiles
{
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Others =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// Writes a sibling temp file and renames it over <paramref name="path"/>,
    /// so a crash mid-write cannot leave half a file behind.
    /// </summary>
    public static void WriteFile(string path, string text)
    {
        var temp = path + ".tmp";
        WriteAndSync(temp, new UTF8Encoding(false).GetBytes(text));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Created readable by this user only. The mode is set again after opening,
    /// because a temp file left behind by a crash keeps the mode it was made with.
    /// </summary>
    private static void WriteAndSync(string path, byte[] bytes)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerFile;
        }

        using var file = new FileStream(path, options);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file.SafeFileHandle, OwnerFile);
        }
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    /// <summary>Only affects directories it creates; an existing one is left as it is.</summary>
    public static void CreatePrivateDir(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
        }
        else
        {
            Directory.CreateDirectory(dir, OwnerDir);
        }
    }

    /// <summary>Takes away access for anyone but the owner, if anyone else has it.</summary>
    public static void MakePrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            if ((File.GetUnixFileMode(path) & Others) != 0)
            {
                File.SetUnixFileMode(path, OwnerFile);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StorageLog.Warn($"could not make the state file private: {e.Message}");
        }
    }
}
