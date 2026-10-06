using System.Diagnostics;

namespace Pidge.Sidecar;

/// <summary>
/// Logs go to stderr only; stdout belongs to the protocol. <c>PIDGE_LOG</c>
/// sets the level: <c>off</c>, <c>error</c>, <c>warn</c>, <c>info</c> (the
/// default), <c>debug</c> or <c>trace</c>.
/// </summary>
internal static class Logging
{
    public static void Init()
    {
        var level = (Environment.GetEnvironmentVariable("PIDGE_LOG") ?? "").Trim().ToLowerInvariant() switch
        {
            "off" => SourceLevels.Off,
            "error" => SourceLevels.Error,
            "warn" or "warning" => SourceLevels.Warning,
            "debug" or "trace" => SourceLevels.Verbose,
            _ => SourceLevels.Information,
        };

        // The default listener writes to the debugger; nothing else is wanted.
        Trace.Listeners.Clear();
        Trace.Listeners.Add(new StderrListener { Filter = new EventTypeFilter(level) });
        Trace.AutoFlush = true;
    }

    private sealed class StderrListener : TraceListener
    {
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            if (Filter is not null && !Filter.ShouldTrace(eventCache, source, eventType, id, message, null, null, null))
            {
                return;
            }
            var label = eventType switch
            {
                TraceEventType.Critical or TraceEventType.Error => "ERROR",
                TraceEventType.Warning => " WARN",
                TraceEventType.Information => " INFO",
                _ => "DEBUG",
            };
            Console.Error.Write($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.ffffffZ} {label} {message}\n");
        }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
            TraceEvent(eventCache, source, eventType, id, args is { Length: > 0 } && format is not null ? string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args) : format);

        public override void Write(string? message) => Console.Error.Write(message);

        public override void WriteLine(string? message) => Console.Error.Write(message + "\n");
    }
}
