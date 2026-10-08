using System.Runtime.Versioning;
using Tmds.DBus.Protocol;

namespace Pidge.Storage;

/// <summary>
/// Just enough of the freedesktop Secret Service API (GNOME Keyring, KDE
/// Wallet) to keep one key in it. Never unlocks anything and never runs a
/// prompt: those are what show a password dialog.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class SecretService
{
    private const string Bus = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    /// <summary>What KDE Wallet and Seahorse show for it.</summary>
    private const string Label = "pidge: key for saved passwords";

    /// <summary>
    /// A keyring that has not answered by now is not going to; the app opens
    /// with plain-text secrets rather than waiting on it.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The key in the unlocked item made for <paramref name="item"/>, or null
    /// when there is no such item at all.
    /// </summary>
    /// <exception cref="SecretServiceException">With what to tell the user.</exception>
    public static string? Read(string item) => Run<string?>(async () =>
    {
        Opened opened;
        try
        {
            opened = await OpenAsync();
        }
        catch (Exception e)
        {
            throw new SecretServiceException("the keyring service did not answer", e);
        }

        using (opened.Connection)
        {
            ObjectPath[] unlocked;
            ObjectPath[] locked;
            try
            {
                (unlocked, locked) = await SearchItemsAsync(opened.Connection, item);
            }
            catch (Exception e)
            {
                throw new SecretServiceException("the keyring could not be searched", e);
            }

            if (unlocked.Length == 0)
            {
                return locked.Length > 0 ? throw new SecretServiceException("the keyring is locked") : null;
            }

            byte[] secret;
            try
            {
                secret = await GetSecretAsync(opened.Connection, unlocked[0].ToString(), opened.Session);
            }
            catch (Exception e)
            {
                throw new SecretServiceException("the keyring would not hand over the key", e);
            }

            try
            {
                return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(secret);
            }
            catch (System.Text.DecoderFallbackException)
            {
                throw new SecretServiceException("the keyring holds a damaged key");
            }
        }
    });

    /// <summary>Stores <paramref name="key"/> in the default collection, if it is unlocked.</summary>
    /// <exception cref="SecretServiceException">Why not, already starting with "keyring: ".</exception>
    public static void Create(string item, string key) => Run<object?>(async () =>
    {
        try
        {
            var opened = await OpenAsync();
            using (opened.Connection)
            {
                var collection = await ReadAliasAsync(opened.Connection, "default");
                if (collection == "/")
                {
                    throw new SecretServiceException("keyring: there is no default collection");
                }
                if (await IsLockedAsync(opened.Connection, collection))
                {
                    throw new SecretServiceException("keyring: locked");
                }
                var prompt = await CreateItemAsync(opened.Connection, collection, opened.Session, item, key);
                if (prompt != "/")
                {
                    throw new SecretServiceException("keyring: it asked for a prompt");
                }
                return null;
            }
        }
        catch (Exception e) when (e is not SecretServiceException)
        {
            throw new SecretServiceException($"keyring: {Describe(e)}", e);
        }
    });

    private readonly record struct Opened(Connection Connection, string Session);

    /// <summary>
    /// Connects to the session bus and opens a session with the "plain"
    /// algorithm: the bus is local to the user, and the key is random bytes
    /// already encoded as text.
    /// </summary>
    private static async Task<Opened> OpenAsync()
    {
        var address = Address.Session
            ?? throw new InvalidOperationException("there is no session bus");
        var connection = new Connection(address);
        try
        {
            await connection.ConnectAsync();
            var session = await OpenSessionAsync(connection);
            return new Opened(connection, session);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static Task<string> OpenSessionAsync(Connection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ServicePath, ServiceInterface, "OpenSession", "sv");
        writer.WriteString("plain");
        writer.WriteVariantString("");
        return connection.CallMethodAsync(
            writer.CreateMessage(),
            static (Message message, object? _) =>
            {
                var reader = message.GetBodyReader();
                reader.ReadVariantValue();
                return reader.ReadObjectPathAsString();
            });
    }

    private static Task<(ObjectPath[] Unlocked, ObjectPath[] Locked)> SearchItemsAsync(Connection connection, string item)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Bus, ServicePath, ServiceInterface, "SearchItems", "a{ss}");
            WriteAttributes(ref writer, item);
            return connection.CallMethodAsync(
                writer.CreateMessage(),
                static (Message message, object? _) =>
                {
                    var reader = message.GetBodyReader();
                    var unlocked = reader.ReadArrayOfObjectPath();
                    var locked = reader.ReadArrayOfObjectPath();
                    return (unlocked, locked);
                });
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static Task<byte[]> GetSecretAsync(Connection connection, string item, string session)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, item, ItemInterface, "GetSecret", "o");
        writer.WriteObjectPath(session);
        return connection.CallMethodAsync(
            writer.CreateMessage(),
            static (Message message, object? _) =>
            {
                // (session, parameters, value, content type)
                var reader = message.GetBodyReader();
                reader.AlignStruct();
                reader.ReadObjectPath();
                reader.ReadArrayOfByte();
                return reader.ReadArrayOfByte();
            });
    }

    private static Task<string> ReadAliasAsync(Connection connection, string alias)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ServicePath, ServiceInterface, "ReadAlias", "s");
        writer.WriteString(alias);
        return connection.CallMethodAsync(
            writer.CreateMessage(),
            static (Message message, object? _) => message.GetBodyReader().ReadObjectPathAsString());
    }

    private static Task<bool> IsLockedAsync(Connection connection, string collection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, collection, PropertiesInterface, "Get", "ss");
        writer.WriteString(CollectionInterface);
        writer.WriteString("Locked");
        return connection.CallMethodAsync(
            writer.CreateMessage(),
            static (Message message, object? _) => message.GetBodyReader().ReadVariantValue().GetBool());
    }

    /// <summary>The prompt the service wants run, "/" for none.</summary>
    private static Task<string> CreateItemAsync(Connection connection, string collection, string session, string item, string key)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Bus, collection, CollectionInterface, "CreateItem", "a{sv}(oayays)b");

            var properties = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("org.freedesktop.Secret.Item.Label");
            writer.WriteVariantString(Label);
            writer.WriteDictionaryEntryStart();
            writer.WriteString("org.freedesktop.Secret.Item.Attributes");
            // A variant is its signature followed by the value.
            writer.WriteSignature("a{ss}");
            WriteAttributes(ref writer, item);
            writer.WriteDictionaryEnd(properties);

            writer.WriteStructureStart();
            writer.WriteObjectPath(session);
            writer.WriteArray(Array.Empty<byte>());
            writer.WriteArray(System.Text.Encoding.UTF8.GetBytes(key));
            writer.WriteString("text/plain");

            // Replace an item with the same attributes.
            writer.WriteBool(true);

            return connection.CallMethodAsync(
                writer.CreateMessage(),
                static (Message message, object? _) =>
                {
                    var reader = message.GetBodyReader();
                    reader.ReadObjectPath();
                    return reader.ReadObjectPathAsString();
                });
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// What a key is found by. <c>api-client</c> is the app's old name, kept so
    /// that keys made before the rename are still found. The writer is a
    /// struct that tracks its own position, so it has to be passed by ref: a
    /// copy writes the attributes but leaves the caller's message empty.
    /// </summary>
    private static void WriteAttributes(ref MessageWriter writer, string item)
    {
        var attributes = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("application");
        writer.WriteString("api-client");
        writer.WriteDictionaryEntryStart();
        writer.WriteString("key");
        writer.WriteString(item);
        writer.WriteDictionaryEnd(attributes);
    }

    private static T Run<T>(Func<Task<T>> work)
    {
        var task = Task.Run(work);
        try
        {
            if (!task.Wait(Timeout))
            {
                throw new SecretServiceException("the keyring service did not answer", new TimeoutException());
            }
            return task.Result;
        }
        catch (AggregateException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static string Describe(Exception e) => e is DBusException dbus ? $"{dbus.ErrorName}: {dbus.ErrorMessage}" : e.Message;
}

/// <summary>A Secret Service failure. The message is what is worth showing; the cause goes to the log.</summary>
internal sealed class SecretServiceException(string message, Exception? inner = null) : Exception(message, inner);
