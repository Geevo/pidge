using System.Text.Json;
using System.Text.Json.Nodes;
using Pidge.Core;
using Pidge.Variables;

namespace Pidge.Storage.Tests;

public class PersistenceTests
{
    private static HistoryEntry Entry(string url) =>
        HistoryEntry.Failure(HttpRequest.Get(url), RequestError.Other("boom"));

    [Fact]
    public void AFirstLaunchHasExactlyOneBlankTab()
    {
        using var dir = new TempDir();
        var outcome = Store.InDir(dir.Path).Load();

        Assert.Null(outcome.Recovery);
        Assert.Single(outcome.State.Tabs);
        Assert.True(outcome.State.Tabs[0].Request.IsUntouched);
        Assert.Equal(outcome.State.Tabs[0].Id, outcome.State.ActiveTabId);
        Assert.Empty(outcome.State.SavedRequests);
        Assert.Empty(outcome.State.History);
    }

    [Fact]
    public void StateSurvivesARoundTrip()
    {
        using var dir = new TempDir();
        var store = Store.InDir(dir.Path);

        var state = new AppState();
        state.UpsertSavedRequest(new SavedRequest("List users", HttpRequest.Get("https://api.example.com/users")));
        state.PushHistory(Entry("https://api.example.com/one"));
        state.Tabs.Add(ScratchTab.Blank());

        store.Save(state);
        var loaded = store.Load();

        Assert.Null(loaded.Recovery);
        Assert.Single(loaded.State.SavedRequests);
        Assert.Equal("List users", loaded.State.SavedRequests[0].Name);
        Assert.Single(loaded.State.History);
        Assert.Equal(2, loaded.State.Tabs.Count);
        Assert.True(PidgeJson.Same(state, loaded.State, StorageJsonContext.Wire.AppState));
    }

    [Fact]
    public void TheStateFileIsReadableJson()
    {
        using var dir = new TempDir();
        var store = Store.InDir(dir.Path);
        store.Save(new AppState());

        var raw = File.ReadAllText(store.Path);
        Assert.Contains($"\n  \"version\": {Migration.SchemaVersion}", raw);
        Assert.NotNull(JsonNode.Parse(raw));
    }

    [Fact]
    public void TheStateFileIsLaidOutTheWaySerdeWritesIt()
    {
        using var dir = new TempDir();
        var store = Store.InDir(dir.Path);
        var state = new AppState();
        state.Tabs[0].Id = "tab-1";
        state.Tabs[0].Request.Id = "req-1";
        state.ActiveTabId = "tab-1";
        store.Save(state);

        var raw = File.ReadAllText(store.Path);
        Assert.Equal(
            """
            {
              "version": 2,
              "settings": {
                "theme": "system",
                "syntaxTheme": "app",
                "timeoutMs": 30000,
                "followRedirects": true,
                "maxHistory": 500,
                "maxResponseBytes": 52428800,
                "restoreTabs": true,
                "wrapResponseLines": false,
                "fontScale": 100,
                "paneLayout": "rows",
                "splitPercent": 42,
                "tls": {
                  "useSystemRoots": true,
                  "extraCaFiles": [],
                  "clientIdentity": null,
                  "acceptInvalidCerts": false
                }
              },
              "savedRequests": [],
              "history": [],
              "tabs": [
                {
                  "id": "tab-1",
                  "name": null,
                  "request": {
                    "id": "req-1",
                    "method": "GET",
                    "url": "",
                    "queryParams": [],
                    "headers": [],
                    "auth": {
                      "type": "none"
                    },
                    "body": {
                      "type": "none"
                    },
                    "timeoutMs": null,
                    "encodeQuery": true
                  },
                  "savedRequestId": null,
                  "dirty": false,
                  "splitPercent": null
                }
              ],
              "activeTabId": "tab-1",
              "environments": [],
              "activeEnvironmentId": null,
              "secrets": "plainText"
            }
            """.ReplaceLineEndings("\n"),
            raw);
    }

    [Fact]
    public void TheWindowIsWrittenOnlyOnceThereIsOne()
    {
        var state = new AppState();
        Assert.DoesNotContain("\"window\"", JsonSerializer.Serialize(state, StorageJsonContext.Wire.AppState));

        state.Window = new WindowPlacement { Monitor = "DP-1", X = -10, Y = 20, Width = 800, Height = 600 };
        Assert.Contains(
            "\"window\":{\"monitor\":\"DP-1\",\"x\":-10,\"y\":20,\"width\":800,\"height\":600,\"maximized\":false}",
            JsonSerializer.Serialize(state, StorageJsonContext.Wire.AppState));
    }

    [Fact]
    public void SavingLeavesNoTempFileBehind()
    {
        using var dir = new TempDir();
        Store.InDir(dir.Path).Save(new AppState());

        var names = Directory.GetFileSystemEntries(dir.Path).Select(Path.GetFileName).ToList();
        Assert.Equal(["state.json"], names);
    }

    [Fact]
    public void SavingCreatesTheDirectory()
    {
        using var dir = new TempDir();
        var store = Store.InDir(dir.Join("nested"));
        store.Save(new AppState());
        Assert.True(File.Exists(store.Path));
    }

    [Fact]
    public void HistoryIsCappedAndNewestFirst()
    {
        var state = new AppState();
        state.Settings.MaxHistory = 3;

        for (var index = 0; index < 10; index++)
        {
            state.PushHistory(Entry($"https://example.com/{index}"));
        }

        Assert.Equal(3, state.History.Count);
        Assert.Equal("https://example.com/9", state.History[0].Request.Url);
        Assert.Equal("https://example.com/7", state.History[2].Request.Url);
    }

    [Fact]
    public void HistoryKeepsTheRequestButNotTheResponseBody()
    {
        var response = new HttpResponse
        {
            Status = 200,
            StatusText = "OK",
            Body = Enumerable.Repeat((byte)'x', 4096).ToArray(),
            DurationMs = 12,
            SizeBytes = 4096,
            FinalUrl = "https://example.com",
        };

        var entry = HistoryEntry.Success(HttpRequest.Get("https://example.com"), response);
        var json = JsonSerializer.Serialize(entry, StorageJsonContext.Wire.HistoryEntry);

        Assert.DoesNotContain("xxxx", json);
        Assert.DoesNotContain("eHh4", json);
        Assert.Equal(4096UL, entry.SizeBytes);
        Assert.Equal((ushort)200, entry.Status);
    }

    [Fact]
    public void SavedRequestsUpdateInPlaceAndDelete()
    {
        var state = new AppState();
        var saved = new SavedRequest("First", HttpRequest.Get("https://example.com/a"));
        var id = saved.Id;
        var createdAt = saved.CreatedAt;

        state.UpsertSavedRequest(saved);

        var updated = new SavedRequest("Renamed", HttpRequest.Get("https://example.com/b")) { Id = id };
        state.UpsertSavedRequest(updated);

        Assert.Single(state.SavedRequests);
        Assert.Equal("Renamed", state.SavedRequests[0].Name);
        Assert.Equal(createdAt, state.SavedRequests[0].CreatedAt);

        Assert.True(state.DeleteSavedRequest(id));
        Assert.False(state.DeleteSavedRequest(id));
        Assert.Empty(state.SavedRequests);
    }

    [Fact]
    public void CorruptFilesArePreservedAndDefaultsAreUsed()
    {
        using var dir = new TempDir();
        var store = Store.InDir(dir.Path);
        File.WriteAllText(store.Path, "{ not json at all");

        var outcome = store.Load();
        var recovery = outcome.Recovery;

        Assert.NotNull(recovery);
        Assert.Contains("could not be loaded", recovery.Message);
        var backup = recovery.BackupPath;
        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));
        Assert.Equal("{ not json at all", File.ReadAllText(backup));
        Assert.StartsWith("state.corrupt-", Path.GetFileName(backup));
        Assert.EndsWith($" The previous file was kept at {backup}.", recovery.Message);
        Assert.Single(outcome.State.Tabs);

        // The app must be usable straight after a recovery.
        store.Save(outcome.State);
        Assert.Null(store.Load().Recovery);
    }

    [Fact]
    public void AFileFromANewerBuildIsRefusedRatherThanMangled()
    {
        var value = new JsonObject { ["version"] = Migration.SchemaVersion + 1 };
        var error = Assert.Throws<StorageException>(() => Migration.Migrate(value));
        Assert.Contains("newer version", error.Message);
        Assert.Equal(
            "this file was written by a newer version of the app (schema 3, this build understands 2)",
            error.Message);
    }

    [Fact]
    public void AnUnversionedFileIsMigratedToTheCurrentSchema()
    {
        var value = JsonNode.Parse("""{ "savedRequests": [], "history": [], "tabs": [] }""");

        var state = Migration.Migrate(value);
        Assert.Equal(Migration.SchemaVersion, state.Version);
        // Migration restores the one-tab invariant.
        Assert.Single(state.Tabs);
    }

    [Fact]
    public void UnknownFieldsAndMissingSettingsDoNotBreakLoading()
    {
        var value = JsonNode.Parse($$"""{ "version": {{Migration.SchemaVersion}}, "somethingFromTheFuture": true }""");

        var state = Migration.Migrate(value);
        Assert.Equal(500UL, state.Settings.MaxHistory);
        Assert.Equal(30_000UL, state.Settings.TimeoutMs);
    }

    [Fact]
    public void ANonObjectStateFileIsAnErrorNotACrash()
    {
        Assert.Throws<StorageException>(() => Migration.Migrate(JsonNode.Parse("[1, 2, 3]")));
    }

    [Fact]
    public void TheActiveEnvironmentSuppliesTheVariables()
    {
        var state = new AppState
        {
            Environments =
            [
                new VariableEnvironment
                {
                    Id = "env-1",
                    Name = "Local",
                    Variables = [new KeyValueEntry("baseUrl", "http://localhost:3000")],
                },
            ],
        };

        Assert.True(state.ActiveVariables().IsEmpty);

        state.ActiveEnvironmentId = "env-1";
        Assert.Equal("http://localhost:3000", state.ActiveVariables().Get("baseUrl"));
    }

    [Fact]
    public void EnsureOneTabRepairsADanglingActiveId()
    {
        var state = new AppState { Tabs = [], ActiveTabId = "gone" };

        state.EnsureOneTab();

        Assert.Single(state.Tabs);
        Assert.Equal(state.Tabs[0].Id, state.ActiveTabId);
    }

    [Fact]
    public void EachTabKeepsItsOwnSplitPosition()
    {
        using var dir = new TempDir();
        var store = Store.InDir(dir.Path);

        var state = new AppState();
        state.Tabs[0].SplitPercent = 70;
        var second = ScratchTab.Blank();
        second.SplitPercent = 25;
        state.Tabs.Add(second);
        // A third is left unset, meaning "use the default".
        state.Tabs.Add(ScratchTab.Blank());

        store.Save(state);
        var loaded = store.Load().State;

        Assert.Equal((byte)70, loaded.Tabs[0].SplitPercent);
        Assert.Equal((byte)25, loaded.Tabs[1].SplitPercent);
        Assert.Null(loaded.Tabs[2].SplitPercent);
    }

    [Fact]
    public void SettingsSavedBeforeTheTextSizeExistedLoadAtTheDesign()
    {
        var value = JsonNode.Parse($$"""
            {
              "version": {{Migration.SchemaVersion}},
              "settings": { "theme": "dark", "timeoutMs": 5000 },
              "tabs": [],
              "activeTabId": null
            }
            """);

        var state = Migration.Migrate(value);
        Assert.Equal((byte)100, state.Settings.FontScale);
        // The fields that were there are not disturbed by the one that was not.
        Assert.Equal(5_000UL, state.Settings.TimeoutMs);
        Assert.Equal(Theme.Dark, state.Settings.Theme);
    }

    [Fact]
    public void TheTextSizeSurvivesARestart()
    {
        using var dir = new TempDir();
        var store = Store.InDir(dir.Path);

        var state = new AppState();
        state.Settings.FontScale = 150;
        store.Save(state);

        Assert.Equal((byte)150, store.Load().State.Settings.FontScale);
    }

    [Fact]
    public void ATabSavedBeforeSplitPositionsExistedStillLoads()
    {
        var value = JsonNode.Parse($$"""
            {
              "version": {{Migration.SchemaVersion}},
              "tabs": [{
                "id": "tab-1",
                "name": null,
                "request": {
                  "id": "req-1",
                  "method": "GET",
                  "url": "https://example.com",
                  "queryParams": [],
                  "headers": [],
                  "auth": { "type": "none" },
                  "body": { "type": "none" },
                  "timeoutMs": null
                },
                "savedRequestId": null,
                "dirty": false
              }],
              "activeTabId": "tab-1"
            }
            """);

        var state = Migration.Migrate(value);
        Assert.Null(state.Tabs[0].SplitPercent);
        Assert.Equal("https://example.com", state.Tabs[0].Request.Url);
    }

    [Fact]
    public void AVersionOneFileIsStampedWithTheCurrentVersion()
    {
        var state = Migration.Migrate(JsonNode.Parse("""{ "version": 1 }"""));
        Assert.Equal(Migration.SchemaVersion, state.Version);
    }
}
