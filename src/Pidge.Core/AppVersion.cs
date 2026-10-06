namespace Pidge.Core;

public static class AppVersion
{
    /// <summary>
    /// The app's version, which every component shares. Set once, in
    /// Directory.Build.props.
    /// </summary>
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
}
