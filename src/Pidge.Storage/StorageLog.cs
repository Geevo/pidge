using System.Diagnostics;

namespace Pidge.Storage;

/// <summary>
/// Warnings worth a line in a log and no more. They go to whatever trace
/// listeners the host has set up; the sidecar sends them to stderr.
/// </summary>
internal static class StorageLog
{
    public static void Warn(string message) => Trace.TraceWarning(message);

    public static void Info(string message) => Trace.TraceInformation(message);
}
