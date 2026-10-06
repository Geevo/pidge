using System.Text;
using System.Text.Json;
using Pidge.Codegen;
using Pidge.Core;
using Pidge.Storage;

namespace Pidge.Session;

/*
 * Saved requests read back in from a file: the JSON this app exports, or a
 * .http file from REST Client, JetBrains, or this app.
 *
 * Importing only ever adds. Every request gets new ids, so the same file twice
 * is two copies to delete, never an edit overwritten, and a file of the wrong
 * kind is refused whole rather than half-imported.
 */
internal static class Import
{
    /// <summary>
    /// Larger than any export, and small enough not to hold the app up reading
    /// something that was never one.
    /// </summary>
    public const int MaxImportBytes = 20 * 1024 * 1024;

    internal sealed class Parsed
    {
        public List<SavedRequest> Saved { get; init; } = [];

        /// <summary>A sentence for each request the file held that could not come across.</summary>
        public List<string> Skipped { get; init; } = [];
    }

    /// <summary>JSON if it looks like JSON, which a <c>.http</c> file never does.</summary>
    /// <exception cref="ImportRefusedException">The file is not one that can be imported.</exception>
    public static Parsed Parse(string contents)
    {
        if (contents.Length > MaxImportBytes / 3 && Encoding.UTF8.GetByteCount(contents) > MaxImportBytes)
        {
            throw new ImportRefusedException("The file is too large to be a file of saved requests.");
        }
        contents = contents.TrimStart('﻿');

        Parsed parsed;
        if (contents.TrimStart().StartsWith('{'))
        {
            parsed = FromJson(contents);
        }
        else
        {
            var file = HttpFile.Parse(contents);
            parsed = new Parsed
            {
                Saved = file.Requests.Select(request => new SavedRequest(request.Name, request.Request)).ToList(),
                Skipped = file.Skipped,
            };
        }

        if (parsed.Saved.Count == 0)
        {
            throw new ImportRefusedException(parsed.Skipped.Count > 0
                ? $"Nothing in the file could be imported. {parsed.Skipped[0]}"
                : "There are no requests in the file.");
        }
        return new Parsed
        {
            Saved = parsed.Saved.Select(Fresh).ToList(),
            Skipped = parsed.Skipped,
        };
    }

    private static Parsed FromJson(string contents)
    {
        ImportFile? file;
        try
        {
            file = JsonSerializer.Deserialize(contents, ExportFileJsonContext.Wire.ImportFile);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new ImportRefusedException($"The file is not a file of saved requests: {e.Message.TrimEnd('.')}.");
        }
        if (file?.Kind is not (Export.ExportKind or Export.LegacyExportKind))
        {
            throw new ImportRefusedException("This JSON is not a file of saved requests exported from pidge.");
        }
        if ((file.Version ?? 0) > 1)
        {
            throw new ImportRefusedException("This file was exported by a newer version of pidge. Update to import it.");
        }
        return new Parsed { Saved = file.SavedRequests };
    }

    /// <summary>New ids throughout, and new timestamps: it is new here.</summary>
    private static SavedRequest Fresh(SavedRequest saved)
    {
        var request = saved.Request;
        FreshIds(request);
        return new SavedRequest(saved.Name, request);
    }

    private static void FreshIds(HttpRequest request)
    {
        request.Id = Ids.NewId();
        foreach (var entry in request.QueryParams.Concat(request.Headers))
        {
            entry.Id = Ids.NewId();
        }
        switch (request.Body)
        {
            case UrlEncodedBody body:
                foreach (var entry in body.Entries)
                {
                    entry.Id = Ids.NewId();
                }
                break;
            case MultipartBody body:
                foreach (var entry in body.Entries)
                {
                    entry.Id = Ids.NewId();
                }
                break;
        }
    }
}

/// <summary>Why a file was refused; the message is shown as it is.</summary>
internal sealed class ImportRefusedException(string message) : Exception(message);

internal sealed class ImportFile
{
    public string? Kind { get; set; }
    public uint? Version { get; set; }
    public List<SavedRequest> SavedRequests { get; set; } = [];
}
