using System.Text;
using System.Text.Json;
using Photino.NET;
using Pidge.Codegen;
using Pidge.Core;
using Pidge.Desktop.Windows;
using Pidge.Session;
using Pidge.Storage;

namespace Pidge.Desktop;

/// <summary>Which desktop's title-bar buttons the UI should draw.</summary>
internal enum WindowButtons
{
    Windows,
    Kde,
    Gnome,
}

/// <summary>
/// The other end of the page's bridge.
///
/// Every message from the page is <c>{ id, command, args }</c>, answered with
/// <c>{ id, ok, value | error }</c>; a message without an id wants no answer.
/// Commands here do argument shuffling and nothing else; the behaviour lives in
/// Pidge.Session, which the VS Code sidecar drives through the same API.
/// </summary>
internal sealed class Host(PhotinoWindow window, AppSession session)
{
    private static DesktopJsonContext Json => DesktopJsonContext.Wire;

    public void OnMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        long? id = null;
        string command;
        JsonElement args;
        try
        {
            using var document = JsonDocument.Parse(e.Message);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
            {
                id = idElement.GetInt64();
            }
            command = root.GetProperty("command").GetString() ?? "";
            args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Debug($"ignored a message that is not a command: {ex.Message}");
            return;
        }

        _ = Run(id, command, args);
    }

    /// <summary>Tells the page the window changed size or state.</summary>
    public void Resized() => Post("{\"event\":\"resized\"}");

    private async Task Run(long? id, string command, JsonElement args)
    {
        try
        {
            var reply = await Dispatch(command, args);
            if (id is { } value)
            {
                Post(Reply(value, ok: true, reply));
            }
        }
        catch (RequestErrorException ex)
        {
            // The one command that fails with the engine's own error hands it
            // over whole; the UI knows how to show it.
            if (id is { } value)
            {
                Post(Reply(value, ok: false, JsonSerializer.Serialize(ex.Error, Json.RequestError)));
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"{command} failed: {ex}");
            if (id is { } value)
            {
                Post(Reply(value, ok: false, JsonSerializer.Serialize(ex.Message, Json.String)));
            }
        }
    }

    /// <summary>Runs one command and returns its answer as JSON.</summary>
    private async Task<string> Dispatch(string command, JsonElement args)
    {
        switch (command)
        {
            case "window_buttons":
                return JsonSerializer.Serialize(ButtonsName(CurrentButtons()), Json.String);

            case "send_http_request":
                {
                    var request = JsonArgs.Required(args, "request", Json.HttpRequest);
                    var variables = JsonArgs.Optional(args, "variables", Json.DictionaryStringString) ?? [];
                    // A failed request is part of the outcome, not a command
                    // failure; the UI renders it in the response pane either way.
                    var outcome = await Task.Run(() => session.SendWithOverridesAsync(request, variables));
                    return JsonSerializer.Serialize(outcome, Json.SendOutcome);
                }

            case "generate_code":
                {
                    var request = JsonArgs.Required(args, "request", Json.HttpRequest);
                    var target = JsonArgs.Required(args, "target", Json.CodeTarget);
                    var variables = JsonArgs.Optional(args, "variables", Json.DictionaryStringString) ?? [];
                    var code = await Task.Run(() => session.GenerateCode(request, variables, target));
                    return JsonSerializer.Serialize(code, Json.String);
                }

            case "cancel_http_request":
                {
                    var requestId = JsonArgs.Required(args, "requestId", Json.String);
                    return JsonSerializer.Serialize(session.Cancel(requestId), Json.Boolean);
                }

            case "load_state":
                {
                    var loaded = new LoadedState
                    {
                        State = session.Snapshot(),
                        Recovery = session.Recovery?.Message,
                        StoragePath = session.StoragePath,
                        Version = AppVersion.Current,
                    };
                    return JsonSerializer.Serialize(loaded, Json.LoadedState);
                }

            case "save_state":
                {
                    var state = JsonArgs.Required(args, "state", Json.AppState);
                    await Task.Run(() => session.ReplaceState(state));
                    return Snapshot();
                }

            case "save_request":
                {
                    var savedRequestId = JsonArgs.Optional(args, "savedRequestId", Json.String);
                    var name = JsonArgs.Required(args, "name", Json.String);
                    var request = JsonArgs.Required(args, "request", Json.HttpRequest);
                    await Task.Run(() => session.SaveRequest(savedRequestId, name, request));
                    return Snapshot();
                }

            case "delete_saved_request":
                {
                    var savedRequestId = JsonArgs.Required(args, "savedRequestId", Json.String);
                    await Task.Run(() => session.DeleteSavedRequest(savedRequestId));
                    return Snapshot();
                }

            case "clear_history":
                await Task.Run(session.ClearHistory);
                return Snapshot();

            case "export_saved_requests":
                return await ExportSavedRequests(args);

            case "import_saved_requests":
                return await ImportSavedRequests();

            case "pick_file":
                return await PickFile(args);

            case "window_minimize":
                await OnUi(() => window.Minimize());
                return "null";

            case "window_toggle_maximize":
                await OnUi(() =>
                {
                    if (window.WindowState == PhotinoWindowState.Maximized)
                    {
                        window.Restore();
                    }
                    else
                    {
                        window.Maximize();
                    }
                });
                return "null";

            case "window_close":
                await OnUi(window.Close);
                return "null";

            case "window_is_maximized":
                return JsonSerializer.Serialize(window.WindowState == PhotinoWindowState.Maximized, Json.Boolean);

            case "window_start_drag":
                await OnUi(() =>
                {
                    if (OperatingSystem.IsWindows())
                    {
                        WindowsFrame.BeginDrag(window.WindowHandle);
                    }
                    else
                    {
                        window.BeginWindowDrag();
                    }
                });
                return "null";

            case "window_drag_regions":
                {
                    if (OperatingSystem.IsLinux())
                    {
                        var regions = JsonArgs.Optional(args, "regions", Json.ListDragRegion) ?? [];
                        var layout = regions
                            .Select(r => new LayoutRegion(
                                r.Width, r.Height, new Thickness(r.X, r.Y, 0, 0),
                                HorizontalAlignment.Left, VerticalAlignment.Top))
                            .ToList();
                        await OnUi(() => window.SetLinuxChromelessDragRegions(layout, []));
                    }
                    return "null";
                }

            default:
                throw new InvalidOperationException($"Unknown command `{command}`.");
        }
    }

    private string Snapshot() => JsonSerializer.Serialize(session.Snapshot(), Json.AppState);

    /// <summary>
    /// Asks where to save, then writes the export there. Null when the dialog
    /// was dismissed.
    ///
    /// The dialog is opened here rather than from the page, so the path written
    /// to is always one the user just chose: the page can suggest a name, never
    /// a place.
    /// </summary>
    private async Task<string> ExportSavedRequests(JsonElement args)
    {
        var ids = JsonArgs.Optional(args, "savedRequestIds", Json.ListString) ?? [];
        var format = JsonArgs.Required(args, "format", Json.ExportFormat);
        var includeSecrets = JsonArgs.Optional(args, "includeSecrets", Json.Boolean);
        var fileName = JsonArgs.Optional(args, "fileName", Json.String) ?? "";

        var (filter, extension) = format switch
        {
            ExportFormat.Json => ("JSON", "json"),
            _ => ("HTTP requests", "http"),
        };
        var path = await OnUi(() => window.ShowSaveFileAsync(
            "Export saved requests", null!, [(filter, [Pattern(extension)])], fileName));
        if (string.IsNullOrEmpty(path))
        {
            return "null";
        }

        var contents = session.ExportSavedRequests(ids, format, includeSecrets);
        try
        {
            await File.WriteAllTextAsync(path, contents, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not write {path}: {ex.Message}");
        }
        return JsonSerializer.Serialize(path, Json.String);
    }

    /// <summary>
    /// Asks for a file, then adds the saved requests in it. Null when the
    /// dialog was dismissed. Like export, the dialog is opened here, so what is
    /// read is always a file the user just picked.
    /// </summary>
    private async Task<string> ImportSavedRequests()
    {
        var chosen = await OnUi(() => window.ShowOpenFileAsync(
            "Import saved requests", null!, false,
            [("Saved requests", [Pattern("json"), Pattern("http"), Pattern("rest")])]));
        var path = chosen?.FirstOrDefault();
        if (string.IsNullOrEmpty(path))
        {
            return "null";
        }

        string contents;
        try
        {
            if (new FileInfo(path).Length > AppSession.MaxImportBytes)
            {
                throw new InvalidOperationException("The file is too large to be a file of saved requests.");
            }
            contents = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not read {path}: {ex.Message}");
        }

        var outcome = await Task.Run(() => session.ImportSavedRequests(contents));
        return JsonSerializer.Serialize(outcome, Json.ImportOutcome);
    }

    /// <summary>The platform's own file chooser, for certificates and keys.</summary>
    private async Task<string> PickFile(JsonElement args)
    {
        var title = JsonArgs.Optional(args, "title", Json.String) ?? "";
        var filters = (JsonArgs.Optional(args, "filters", Json.ListFilePickFilter) ?? [])
            .Select(f => (f.Name, f.Extensions.Select(Pattern).ToArray()))
            .ToArray();
        var chosen = await OnUi(() => window.ShowOpenFileAsync(title, null!, false, filters));
        var path = chosen?.FirstOrDefault();
        return string.IsNullOrEmpty(path) ? "null" : JsonSerializer.Serialize(path, Json.String);
    }

    private static string Pattern(string extension) => "*." + extension.TrimStart('.');

    internal static WindowButtons CurrentButtons() =>
        OperatingSystem.IsWindows()
            ? WindowButtons.Windows
            : ButtonsForDesktop(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"));

    /// <summary>
    /// KDE and GNOME look nothing alike, so the desktop decides as much as the
    /// operating system does. <c>XDG_CURRENT_DESKTOP</c> can name several,
    /// colon separated and in no fixed case — "ubuntu:GNOME" is a real value.
    /// Anything that is not KDE is closer to Adwaita than to Breeze.
    /// </summary>
    internal static WindowButtons ButtonsForDesktop(string? desktop)
    {
        var isKde = desktop is not null
            && desktop.Split(':').Any(name => name.Trim().Equals("kde", StringComparison.OrdinalIgnoreCase));
        return isKde ? WindowButtons.Kde : WindowButtons.Gnome;
    }

    internal static string ButtonsName(WindowButtons buttons) => buttons switch
    {
        WindowButtons.Windows => "windows",
        WindowButtons.Kde => "kde",
        _ => "gnome",
    };

    private static string Reply(long id, bool ok, string payload)
    {
        var builder = new StringBuilder(payload.Length + 40);
        builder.Append("{\"id\":").Append(id).Append(",\"ok\":").Append(ok ? "true" : "false");
        builder.Append(ok ? ",\"value\":" : ",\"error\":").Append(payload).Append('}');
        return builder.ToString();
    }

    /// <summary>Sends to the page from whichever thread the work finished on.</summary>
    private void Post(string message)
    {
        void Send()
        {
            try
            {
                if (!window.IsClosed)
                {
                    window.SendWebMessage(message);
                }
            }
            catch (InvalidOperationException)
            {
                // The window went while the work was running; nobody is waiting.
            }
        }

        if (window.Dispatcher.CheckAccess())
        {
            Send();
        }
        else
        {
            window.Dispatcher.BeginInvoke(Send);
        }
    }

    private Task OnUi(Action action)
    {
        if (window.Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return window.Dispatcher.InvokeAsync(action, CancellationToken.None);
    }

    private async Task<T> OnUi<T>(Func<Task<T>> action)
    {
        if (window.Dispatcher.CheckAccess())
        {
            return await action();
        }
        Task<T>? started = null;
        await window.Dispatcher.InvokeAsync(() => { started = action(); }, CancellationToken.None);
        return await started!;
    }
}
