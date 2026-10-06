using System.Text.Json.Serialization;
using Pidge.Core;
using Pidge.Variables;

namespace Pidge.Storage;

public static class Clock
{
    /// <summary>Milliseconds since the Unix epoch.</summary>
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// How the request and response panes are arranged.
///
/// Named for the layout rather than for the divider, because "horizontal
/// split" means opposite things depending on who you ask.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PaneLayout>))]
public enum PaneLayout
{
    /// <summary>Request above, response below.</summary>
    [JsonStringEnumMemberName("rows")] Rows,

    /// <summary>Request beside response.</summary>
    [JsonStringEnumMemberName("columns")] Columns,
}

[JsonConverter(typeof(JsonStringEnumConverter<Theme>))]
public enum Theme
{
    /// <summary>Follow the operating system's light/dark preference.</summary>
    [JsonStringEnumMemberName("system")] System,
    [JsonStringEnumMemberName("light")] Light,
    [JsonStringEnumMemberName("dark")] Dark,

    /// <summary>Dark, with warm neutrals in place of the plain greys.</summary>
    [JsonStringEnumMemberName("warmDark")] WarmDark,

    /// <summary>Light, on paper rather than white.</summary>
    [JsonStringEnumMemberName("warmLight")] WarmLight,
}

/// <summary>
/// Which colours the editors highlight with.
///
/// Separate from <see cref="Theme"/> because the two answer different questions:
/// the theme is the application's own furniture, and this is the code inside it.
/// Each of the borrowed palettes has a light and a dark form, and the one used
/// follows whichever the theme above resolves to.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SyntaxTheme>))]
public enum SyntaxTheme
{
    /// <summary>The application's own palette, the one the status codes are drawn in.</summary>
    [JsonStringEnumMemberName("app")] App,

    /// <summary>Visual Studio Code's defaults, Dark+ and Light+.</summary>
    [JsonStringEnumMemberName("vsCode")] VsCode,

    /// <summary>One Dark and One Light, from Atom.</summary>
    [JsonStringEnumMemberName("one")] One,

    /// <summary>GitHub's defaults, Dark Default and Light Default.</summary>
    [JsonStringEnumMemberName("github")] Github,
}

/// <summary>Every field is defaulted, so settings written before a field existed still load.</summary>
public sealed class Settings
{
    public Theme Theme { get; set; } = Theme.System;
    public SyntaxTheme SyntaxTheme { get; set; } = SyntaxTheme.App;
    public ulong TimeoutMs { get; set; } = 30_000;
    public bool FollowRedirects { get; set; } = true;
    public ulong MaxHistory { get; set; } = 500;
    public ulong MaxResponseBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Reopen the scratch tabs that were open last time.</summary>
    public bool RestoreTabs { get; set; } = true;

    public bool WrapResponseLines { get; set; }

    /// <summary>
    /// Every size in the interface, as a percentage of its designed size.
    /// 100 is the design; the UI clamps what it applies, so a hand-edited
    /// state file cannot leave the text unreadably small or off the screen.
    /// </summary>
    public byte FontScale { get; set; } = 100;

    public PaneLayout PaneLayout { get; set; } = PaneLayout.Rows;

    /// <summary>
    /// The request pane's share of the split, as a percentage. Clamped when
    /// applied, so a hand-edited state file cannot collapse a pane entirely.
    /// </summary>
    public byte SplitPercent { get; set; } = 42;

    /// <summary>
    /// Trust and client-certificate settings. Changing any of these rebuilds
    /// the HTTP client, so they take effect on the next send.
    /// </summary>
    public TlsSettings Tls { get; set; } = new();
}

/// <summary>A request the user chose to keep. Flat list, no folders, no collections.</summary>
public sealed class SavedRequest
{
    public SavedRequest() { }

    public SavedRequest(string name, HttpRequest request)
    {
        var now = Clock.NowMs();
        Id = Ids.NewId();
        Name = name;
        Request = request;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public HttpRequest Request { get; set; } = new();
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

/// <summary>
/// One past send. Response bodies are deliberately not kept: history is for
/// getting back to a request, not for archiving payloads.
/// </summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = "";
    public long Timestamp { get; set; }
    public HttpRequest Request { get; set; } = new();
    public ushort? Status { get; set; }
    public string? StatusText { get; set; }
    public ulong? DurationMs { get; set; }
    public ulong? SizeBytes { get; set; }

    /// <summary>Set instead of the status fields when the request never completed.</summary>
    public string? Error { get; set; }

    public static HistoryEntry Success(HttpRequest request, HttpResponse response) => new()
    {
        Id = Ids.NewId(),
        Timestamp = Clock.NowMs(),
        Request = request,
        Status = response.Status,
        StatusText = response.StatusText,
        DurationMs = response.DurationMs,
        SizeBytes = response.SizeBytes,
        Error = null,
    };

    public static HistoryEntry Failure(HttpRequest request, RequestError error) => new()
    {
        Id = Ids.NewId(),
        Timestamp = Clock.NowMs(),
        Request = request,
        Error = error.Message,
    };
}

/// <summary>An open editor tab. Tabs exist whether or not they were ever saved.</summary>
public sealed class ScratchTab
{
    public string Id { get; set; } = "";

    /// <summary>Null means the tab is titled from its URL.</summary>
    public string? Name { get; set; }

    public HttpRequest Request { get; set; } = new();

    /// <summary>Set when the tab came from, or was written to, a saved request.</summary>
    public string? SavedRequestId { get; set; }

    /// <summary>True when the tab differs from the saved request it is linked to.</summary>
    public bool Dirty { get; set; }

    /// <summary>
    /// This tab's own split position, as the request pane's percentage share.
    /// Null uses <see cref="Settings.SplitPercent"/>, which is also what a new
    /// tab starts from. Defaulted so a state file written before this existed
    /// still loads.
    /// </summary>
    public byte? SplitPercent { get; set; }

    public static ScratchTab Blank() => new() { Id = Ids.NewId(), Request = HttpRequest.Blank() };
}

/// <summary>
/// Where the desktop window was when it last moved, for the next launch.
///
/// Owned by the desktop shell: the UI neither sees it nor sends it back.
/// </summary>
public sealed record WindowPlacement
{
    /// <summary>The monitor it was on, by the name the operating system gives it.</summary>
    public string? Monitor { get; init; }

    /// <summary>Outer position in physical pixels, in desktop coordinates.</summary>
    public int X { get; init; }

    public int Y { get; init; }

    /// <summary>Inner size in logical pixels, so it survives a change of scale.</summary>
    public uint Width { get; init; }

    public uint Height { get; init; }
    public bool Maximized { get; init; }
}

/// <summary>Everything persisted, in one file.</summary>
public sealed class AppState
{
    /// <summary>A first launch: exactly one blank request, nothing else.</summary>
    public AppState()
    {
        var tab = ScratchTab.Blank();
        ActiveTabId = tab.Id;
        Tabs = [tab];
    }

    public uint Version { get; set; } = Migration.SchemaVersion;
    public Settings Settings { get; set; } = new();
    public List<SavedRequest> SavedRequests { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];
    public List<ScratchTab> Tabs { get; set; }
    public string? ActiveTabId { get; set; }
    public List<VariableEnvironment> Environments { get; set; } = [];
    public string? ActiveEnvironmentId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WindowPlacement? Window { get; set; }

    /// <summary>Prepends an entry and trims to the configured cap.</summary>
    public void PushHistory(HistoryEntry entry)
    {
        History.Insert(0, entry);
        var max = Settings.MaxHistory;
        if ((ulong)History.Count > max)
        {
            History.RemoveRange((int)max, History.Count - (int)max);
        }
    }

    /// <summary>Inserts or replaces a saved request, matching on id.</summary>
    public void UpsertSavedRequest(SavedRequest saved)
    {
        var index = SavedRequests.FindIndex(existing => existing.Id == saved.Id);
        if (index < 0)
        {
            SavedRequests.Add(saved);
            return;
        }
        saved.CreatedAt = SavedRequests[index].CreatedAt;
        saved.UpdatedAt = Clock.NowMs();
        SavedRequests[index] = saved;
    }

    public bool DeleteSavedRequest(string id) => SavedRequests.RemoveAll(saved => saved.Id == id) > 0;

    public VariableSet ActiveVariables()
    {
        var environment = ActiveEnvironmentId is { } id ? Environments.FirstOrDefault(env => env.Id == id) : null;
        return environment is null ? new VariableSet() : VariableSet.From(environment);
    }

    /// <summary>
    /// Guarantees the invariant the UI relies on: at least one tab, and an
    /// <see cref="ActiveTabId"/> that actually points at one of them.
    /// </summary>
    public void EnsureOneTab()
    {
        if (Tabs.Count == 0)
        {
            Tabs.Add(ScratchTab.Blank());
        }
        var valid = ActiveTabId is { } id && Tabs.Any(tab => tab.Id == id);
        if (!valid)
        {
            ActiveTabId = Tabs[0].Id;
        }
    }

    /// <summary>A deep copy.</summary>
    public AppState Clone() => PidgeJson.Clone(this, StorageJsonContext.Wire.AppState);
}
