using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pidge.Storage;

public static class Migration
{
    /// <summary>Bump this whenever the on-disk shape changes, and add a step below.</summary>
    public const uint SchemaVersion = 2;

    /// <summary>
    /// Brings a parsed state file up to <see cref="SchemaVersion"/>.
    ///
    /// Files written before versioning existed report version 0 and are treated
    /// as a best-effort read against the current shape; the defaults on
    /// <see cref="AppState"/> fill in anything that was missing.
    /// </summary>
    /// <exception cref="StorageException">The file is from a newer build, or is not a state file.</exception>
    public static AppState Migrate(JsonNode? value)
    {
        var version = ReadVersion(value);

        if (version > SchemaVersion)
        {
            throw StorageException.Migration(
                $"this file was written by a newer version of the app (schema {version}, this build understands {SchemaVersion})");
        }

        while (version < SchemaVersion)
        {
            value = Step(version, value);
            version += 1;
        }

        AppState? state;
        try
        {
            state = value.Deserialize(StorageJsonContext.Wire.AppState);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw StorageException.Parse(e.Message);
        }
        if (state is null)
        {
            throw StorageException.Parse("invalid type: null, expected struct AppState");
        }
        state.Version = SchemaVersion;
        state.EnsureOneTab();
        return state;
    }

    /// <summary>The <c>version</c> field as an unsigned number, or 0 when there is none to read.</summary>
    private static uint ReadVersion(JsonNode? value)
    {
        if (value is not JsonObject obj
            || obj["version"] is not JsonValue number
            || number.GetValueKind() != JsonValueKind.Number)
        {
            return 0;
        }
        if (number.TryGetValue<ulong>(out var wide))
        {
            return (uint)wide;
        }
        return number.TryGetValue<uint>(out var narrow) ? narrow : 0;
    }

    /// <summary>One migration step, from <paramref name="from"/> to <paramref name="from"/> + 1.</summary>
    private static JsonNode? Step(uint from, JsonNode? value)
    {
        switch (from)
        {
            // Pre-versioning files: stamp the version and let the defaults do the rest.
            case 0:
                if (value is not JsonObject zero)
                {
                    throw StorageException.Migration("the state file is not a JSON object");
                }
                zero["version"] = 1u;
                return value;

            // Secrets may be encrypted from 2 on. A version 1 file is a version 2
            // file with every secret in plain text, which the next save encrypts.
            case 1:
                if (value is JsonObject one)
                {
                    one["version"] = 2u;
                }
                return value;

            default:
                throw StorageException.Migration($"no migration from schema {from}");
        }
    }
}
