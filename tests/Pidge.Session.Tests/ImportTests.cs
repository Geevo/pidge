using Pidge.Codegen;
using Pidge.Core;
using Pidge.Storage;

namespace Pidge.Session.Tests;

public class ImportTests
{
    private static SavedRequest Saved()
    {
        var request = HttpRequest.Get("https://api.example.com/users");
        request.Headers.Add(new KeyValueEntry("Accept", "application/json"));
        request.Auth = new BearerAuth { Token = "tok" };
        return new SavedRequest("Users", request);
    }

    private static string RefusalOf(string contents) =>
        Assert.Throws<ImportRefusedException>(() => Import.Parse(contents)).Message;

    [Fact]
    public void ReadsBackWhatWasExportedAsNewRequests()
    {
        var original = Saved();
        var json = Export.Write([original], ExportFormat.Json, includeSecrets: true);

        var parsed = Import.Parse(json);
        var back = Assert.Single(parsed.Saved);
        Assert.Equal("Users", back.Name);
        Assert.Equal("tok", Assert.IsType<BearerAuth>(back.Request.Auth).Token);
        Assert.Equal(original.Request.Url, back.Request.Url);
        Assert.NotEqual(original.Id, back.Id);
        Assert.NotEqual(original.Request.Id, back.Request.Id);
        Assert.NotEqual(original.Request.Headers[0].Id, back.Request.Headers[0].Id);
    }

    [Fact]
    public void ReadsAHttpFile()
    {
        var http = Export.Write([Saved()], ExportFormat.Http, includeSecrets: false);
        var parsed = Import.Parse(http);
        Assert.Equal("Users", parsed.Saved[0].Name);
        Assert.Equal("{{token}}", Assert.IsType<BearerAuth>(parsed.Saved[0].Request.Auth).Token);
    }

    [Fact]
    public void ReadsAnExportFromBeforeTheRename()
    {
        var json = Export.Write([Saved()], ExportFormat.Json, includeSecrets: true)
            .Replace("pidge/saved-requests", "api-client/saved-requests", StringComparison.Ordinal);
        Assert.Single(Import.Parse(json).Saved);
    }

    [Fact]
    public void ReadsAFileThatStartsWithAByteOrderMark()
    {
        var json = "﻿" + Export.Write([Saved()], ExportFormat.Json, includeSecrets: true);
        Assert.Single(Import.Parse(json).Saved);
    }

    [Fact]
    public void RefusesJsonThatIsNotAnExport()
    {
        var error = RefusalOf("""{"version": 2, "tabs": []}""");
        Assert.Contains("not a file of saved requests", error);

        error = RefusalOf("""{"kind": "api-client/saved-requests", "version": 9}""");
        Assert.Contains("newer version", error);

        error = RefusalOf("{ not json");
        Assert.Contains("not a file of saved requests", error);
        Assert.StartsWith("The file is not a file of saved requests: ", error);
        Assert.EndsWith(".", error);
        Assert.False(error.EndsWith("..", StringComparison.Ordinal), error);
    }

    [Fact]
    public void RefusesAFileWithNothingInIt()
    {
        Assert.Equal("There are no requests in the file.", RefusalOf("# only a comment\n"));
        Assert.Equal("There are no requests in the file.", RefusalOf("""{"kind": "pidge/saved-requests", "version": 1}"""));
        var error = RefusalOf("TRACE https://x.test\n");
        Assert.Contains("TRACE is not a method", error);
    }

    [Fact]
    public void RefusesAFileTooLargeToBeAnExport()
    {
        var huge = new string('#', Import.MaxImportBytes + 1);
        Assert.Equal("The file is too large to be a file of saved requests.", RefusalOf(huge));
    }
}
