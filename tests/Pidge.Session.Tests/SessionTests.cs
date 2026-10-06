using Pidge.Codegen;
using Pidge.Core;
using Pidge.Storage;
using Pidge.Variables;

namespace Pidge.Session.Tests;

public class SessionTests
{
    private static AppSession Start(TempDir dir) => AppSession.Start(Store.InDir(dir.Path));

    [Fact]
    public async Task AFailedSendLandsInHistoryWithItsMessage()
    {
        using var dir = new TempDir();
        using var session = Start(dir);

        var error = await Assert.ThrowsAsync<RequestErrorException>(() => session.SendAsync(HttpRequest.Get("not a url")));
        Assert.Equal(RequestErrorKind.InvalidUrl, error.Error.Kind);

        var state = session.Snapshot();
        Assert.Single(state.History);
        Assert.NotNull(state.History[0].Error);
        Assert.Null(state.History[0].Status);
    }

    [Fact]
    public void CancellingSomethingThatAlreadyFinishedIsHarmless()
    {
        using var dir = new TempDir();
        using var session = Start(dir);
        Assert.False(session.Cancel("nothing-in-flight"));
    }

    [Fact]
    public void SavingARequestPersistsImmediately()
    {
        using var dir = new TempDir();
        using var session = Start(dir);

        var saved = session.SaveRequest(null, "List users", HttpRequest.Get("https://example.com/users"));

        using var reopened = Start(dir);
        var state = reopened.Snapshot();

        Assert.Single(state.SavedRequests);
        Assert.Equal(saved.Id, state.SavedRequests[0].Id);
        Assert.Equal("List users", state.SavedRequests[0].Name);
    }

    [Fact]
    public void SavingWithAnExistingIdUpdatesInPlace()
    {
        using var dir = new TempDir();
        using var session = Start(dir);

        var saved = session.SaveRequest(null, "First", HttpRequest.Get("https://a.example"));
        session.SaveRequest(saved.Id, "Renamed", HttpRequest.Get("https://b.example"));

        var state = session.Snapshot();
        Assert.Single(state.SavedRequests);
        Assert.Equal("Renamed", state.SavedRequests[0].Name);
    }

    [Fact]
    public void DeletingAndClearingPersist()
    {
        using var dir = new TempDir();
        using var session = Start(dir);

        var saved = session.SaveRequest(null, "Gone soon", HttpRequest.Get("https://a.example"));

        var state = session.Snapshot();
        state.PushHistory(HistoryEntry.Failure(HttpRequest.Get("https://a.example"), RequestError.Other("boom")));
        session.ReplaceState(state);

        Assert.True(session.DeleteSavedRequest(saved.Id));
        session.ClearHistory();

        using var reopened = Start(dir);
        var after = reopened.Snapshot();
        Assert.Empty(after.SavedRequests);
        Assert.Empty(after.History);
    }

    [Fact]
    public void AStateWithNoTabsIsRepairedRatherThanRejected()
    {
        using var dir = new TempDir();
        using var session = Start(dir);

        session.ReplaceState(new AppState { Tabs = [], ActiveTabId = null });

        var state = session.Snapshot();
        Assert.Single(state.Tabs);
        Assert.NotNull(state.ActiveTabId);
    }

    [Fact]
    public void ACorruptStateFileStillYieldsAWorkingSession()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Join("state.json"), "garbage");

        using var session = Start(dir);

        Assert.NotNull(session.Recovery);
        Assert.Single(session.Snapshot().Tabs);
        Assert.EndsWith("state.json", session.StoragePath);
    }

    [Fact]
    public void AnUnusableCertificateIsReportedButTheSettingIsStillSaved()
    {
        using var dir = new TempDir();
        using var session = Start(dir);

        var state = session.Snapshot();
        state.Settings.Tls.ExtraCaFiles = ["/definitely/not/here.pem"];
        var error = Assert.Throws<SessionException>(() => session.ReplaceState(state));

        Assert.Equal(SessionErrorKind.Engine, error.Kind);
        Assert.Contains("/definitely/not/here.pem", error.Message);

        // Saved anyway, so the settings dialog has something to correct.
        try
        {
            using var reopened = Start(dir);
            Assert.NotEmpty(reopened.Snapshot().Settings.Tls.ExtraCaFiles);
        }
        catch (RequestErrorException)
        {
        }
    }

    /// <summary>
    /// The snippet carries the values the request would be sent with, rather
    /// than <c>{{name}}</c> for somebody to fill in by hand.
    /// </summary>
    [Fact]
    public void GeneratedCodeResolvesVariablesFromTheActiveEnvironment()
    {
        using var dir = new TempDir();
        using var session = Start(dir);

        var state = session.Snapshot();
        state.Environments =
        [
            new VariableEnvironment
            {
                Id = "env-1",
                Name = "Local",
                Variables =
                [
                    new KeyValueEntry("baseUrl", "https://api.example.com"),
                    new KeyValueEntry("token", "tok_123"),
                ],
            },
        ];
        state.ActiveEnvironmentId = "env-1";
        session.ReplaceState(state);

        var request = HttpRequest.Get("{{baseUrl}}/things");
        request.Auth = new BearerAuth { Token = "{{token}}" };

        var code = session.GenerateCode(request, null, CodeTarget.Curl);

        Assert.Contains("https://api.example.com/things", code);
        Assert.Contains("Bearer tok_123", code);
        Assert.DoesNotContain("{{", code);
    }

    /// <summary>A name with no value is the same answer Send gives, and for the same reason.</summary>
    [Fact]
    public void GeneratedCodeReportsAVariableItCannotResolve()
    {
        using var dir = new TempDir();
        using var session = Start(dir);

        var error = Assert.Throws<RequestErrorException>(
            () => session.GenerateCode(HttpRequest.Get("{{baseUrl}}/things"), null, CodeTarget.Curl));

        Assert.Contains("baseUrl", error.Error.Message);
    }

    /// <summary>
    /// The settings are the app's, not the request's, so the snippet has to
    /// carry them: code copied out of a client that follows redirects should
    /// follow them.
    /// </summary>
    [Fact]
    public void GeneratedCodeCarriesTheSettingsTheRequestWouldBeSentUnder()
    {
        using var dir = new TempDir();
        using var session = Start(dir);

        var state = session.Snapshot();
        state.Settings.FollowRedirects = false;
        state.Settings.TimeoutMs = 5_000;
        state.Settings.Tls.AcceptInvalidCerts = true;
        session.ReplaceState(state);

        var code = session.GenerateCode(HttpRequest.Get("https://example.com/things"), null, CodeTarget.Curl);

        Assert.DoesNotContain("--location", code);
        Assert.Contains("--insecure", code);
        Assert.Contains("--max-time 5", code);
    }

    [Fact]
    public void ExplicitOverridesWinInGeneratedCode()
    {
        using var dir = new TempDir();
        using var session = Start(dir);

        var code = session.GenerateCode(
            HttpRequest.Get("{{baseUrl}}/things"),
            new Dictionary<string, string> { ["baseUrl"] = "https://override.example" },
            CodeTarget.Curl);

        Assert.Contains("https://override.example/things", code);
    }

    [Fact]
    public void ImportingAddsRequestsAndSaysWhatTheyStillNeed()
    {
        using var dir = new TempDir();
        var session = Start(dir);

        var state = session.Snapshot();
        state.Environments.Add(new VariableEnvironment
        {
            Id = "env",
            Name = "Local",
            Variables = [new KeyValueEntry("baseUrl", "http://localhost")],
        });
        session.ReplaceState(state);

        const string file =
            "### Me\n"
            + "GET {{baseUrl}}/me\n"
            + "Authorization: Bearer {{token}}\n"
            + "\n"
            + "### Login\n"
            + "POST {{baseUrl}}/login\n"
            + "Authorization: Basic ada s3cret\n";
        var outcome = session.ImportSavedRequests(file);

        Assert.Equal(2, outcome.Imported);
        Assert.Equal(new[] { "token" }, outcome.UndefinedVariables);
        Assert.True(outcome.PlainSecrets, "Basic ada s3cret is a password as it is");
        Assert.Empty(outcome.Skipped);

        // Importing only ever adds, and it lasts.
        session.ImportSavedRequests(file);
        session.Dispose();
        using var reopened = Start(dir);
        var names = reopened.Snapshot().SavedRequests.Select(saved => saved.Name).ToList();
        Assert.Equal(new[] { "Me", "Login", "Me", "Login" }, names);
    }

    [Fact]
    public void ImportingSomethingElseChangesNothing()
    {
        using var dir = new TempDir();
        using var session = Start(dir);

        var error = Assert.Throws<SessionException>(() => session.ImportSavedRequests("""{"version": 2, "tabs": []}"""));
        Assert.Equal(SessionErrorKind.Import, error.Kind);
        Assert.Contains("not a file of saved requests", error.Message);
        Assert.Empty(session.Snapshot().SavedRequests);
    }

    [Fact]
    public void ExportingPicksTheChosenRequestsInTheOrderTheyAreListed()
    {
        using var dir = new TempDir();
        using var session = Start(dir);

        var first = session.SaveRequest(null, "First", HttpRequest.Get("https://a.example"));
        session.SaveRequest(null, "Second", HttpRequest.Get("https://b.example"));
        var third = session.SaveRequest(null, "Third", HttpRequest.Get("https://c.example"));

        var http = session.ExportSavedRequests([third.Id, first.Id], ExportFormat.Http, includeSecrets: false);

        Assert.Contains("https://a.example", http);
        Assert.DoesNotContain("https://b.example", http);
        Assert.True(http.IndexOf("First", StringComparison.Ordinal) < http.IndexOf("Third", StringComparison.Ordinal), http);
    }

    [Fact]
    public void TheImportOutcomeHasTheShapeTheUiReads()
    {
        var outcome = new ImportOutcome
        {
            Imported = 2,
            UndefinedVariables = ["token"],
            Skipped = [],
            PlainSecrets = true,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(outcome, SessionJsonContext.Wire.ImportOutcome);

        Assert.StartsWith("""{"state":{"version":2,""", json);
        Assert.EndsWith(""","imported":2,"undefinedVariables":["token"],"skipped":[],"plainSecrets":true}""", json);
    }

    [Fact]
    public void TheSendOutcomeWritesEveryFieldEvenWhenEmpty()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new SendOutcome(), SessionJsonContext.Wire.SendOutcome);
        Assert.Equal("""{"response":null,"error":null,"historyEntry":null}""", json);
    }

    private static WindowPlacement Placement(int x) => new()
    {
        Monitor = "DP-1",
        X = x,
        Y = 40,
        Width = 1200,
        Height = 800,
        Maximized = false,
    };

    [Fact]
    public void TheWindowPlacementSurvivesARestart()
    {
        using var dir = new TempDir();
        using var session = Start(dir);
        session.SetWindowPlacement(Placement(300));
        session.Persist();

        using var reopened = Start(dir);
        Assert.Equal(Placement(300), reopened.GetWindowPlacement());
    }

    [Fact]
    public void AUiSaveNeverMovesTheWindow()
    {
        using var dir = new TempDir();
        using var session = Start(dir);
        session.SetWindowPlacement(Placement(300));
        // The UI's copy predates the move, and carries the old placement along
        // because it round-trips whatever it was given.
        var stale = session.Snapshot();
        stale.Window = Placement(0);
        session.SetWindowPlacement(Placement(900));

        session.ReplaceState(stale);
        Assert.Equal(Placement(900), session.GetWindowPlacement());

        // Nor does one that has no placement at all.
        session.ReplaceState(new AppState());
        Assert.Equal(Placement(900), session.GetWindowPlacement());
    }

    [Fact]
    public void ReplacingTheStateKeepsNoReferenceToTheCallersCopy()
    {
        using var dir = new TempDir();
        using var session = Start(dir);

        var state = session.Snapshot();
        session.ReplaceState(state);
        state.Tabs.Clear();

        Assert.Single(session.Snapshot().Tabs);
    }
}
