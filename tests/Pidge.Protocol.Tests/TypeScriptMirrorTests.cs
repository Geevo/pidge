using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Pidge.Codegen;
using Pidge.Core;
using Pidge.Session;
using Pidge.Storage;
using Pidge.Variables;

namespace Pidge.Protocol.Tests;

/// <summary>
/// The TypeScript declarations in <c>packages/ui/src/generated/</c> are written
/// by hand, one file per .NET type. These tests hold each one to the JSON
/// contract of its type: the same fields, the same nullability, the same tags
/// on a tagged union, the same strings for an enum. A field added, renamed or
/// dropped on one side fails here rather than in a request.
/// </summary>
public partial class TypeScriptMirrorTests
{
    /// <summary>Each declaration file, and the type it mirrors. Most share a name.</summary>
    private static readonly Dictionary<string, Type> Mirrors = new()
    {
        ["ApiKeyPlacement"] = typeof(ApiKeyPlacement),
        ["AppState"] = typeof(AppState),
        ["AuthConfig"] = typeof(AuthConfig),
        ["ClientEnvelope"] = typeof(ClientEnvelope),
        ["ClientIdentitySettings"] = typeof(ClientIdentitySettings),
        ["ClientMessage"] = typeof(ClientMessage),
        ["CodeTarget"] = typeof(CodeTarget),
        ["Environment"] = typeof(VariableEnvironment),
        ["ExportFormat"] = typeof(ExportFormat),
        ["HistoryEntry"] = typeof(HistoryEntry),
        ["HttpMethod"] = typeof(RequestMethod),
        ["HttpRequest"] = typeof(HttpRequest),
        ["HttpResponse"] = typeof(HttpResponse),
        ["ImportOutcome"] = typeof(ImportOutcome),
        ["KeyValueEntry"] = typeof(KeyValueEntry),
        ["MultipartEntry"] = typeof(MultipartEntry),
        ["MultipartValue"] = typeof(MultipartValue),
        ["OAuth1Settings"] = typeof(OAuth1Settings),
        ["OAuth1Signature"] = typeof(OAuth1Signature),
        ["OAuth2ClientAuth"] = typeof(OAuth2ClientAuth),
        ["OAuth2Grant"] = typeof(OAuth2Grant),
        ["OAuth2Settings"] = typeof(OAuth2Settings),
        ["PaneLayout"] = typeof(PaneLayout),
        ["PeerCertificate"] = typeof(PeerCertificate),
        ["RequestBody"] = typeof(RequestBody),
        ["RequestError"] = typeof(Pidge.Core.RequestError),
        ["RequestErrorKind"] = typeof(RequestErrorKind),
        ["SavedRequest"] = typeof(SavedRequest),
        ["ScratchTab"] = typeof(ScratchTab),
        ["SendOutcome"] = typeof(SendOutcome),
        ["ServerEnvelope"] = typeof(ServerEnvelope),
        ["ServerMessage"] = typeof(ServerMessage),
        ["Settings"] = typeof(Settings),
        ["SyntaxTheme"] = typeof(SyntaxTheme),
        ["Theme"] = typeof(Theme),
        ["TlsDetails"] = typeof(TlsDetails),
        ["TlsSettings"] = typeof(TlsSettings),
    };

    /// <summary>
    /// Fields the hosts keep to themselves. The webview never reads them, and
    /// the hosts carry them across a save it sends.
    /// </summary>
    private static readonly HashSet<(Type, string)> HostOnly =
    [
        (typeof(AppState), "window"),
    ];

    private static readonly JsonSerializerOptions Options = WireOptions();

    private static readonly Lazy<Dictionary<string, string>> Declarations = new(ReadDeclarations);

    public static TheoryData<string> Names => [.. Mirrors.Keys.Order(StringComparer.Ordinal)];

    [Fact]
    public void EveryDeclarationFileMirrorsAType()
    {
        Assert.Equal(
            Mirrors.Keys.Order(StringComparer.Ordinal),
            Declarations.Value.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheDeclarationMatchesTheJsonContract(string name)
    {
        var typeInfo = Options.GetTypeInfo(Mirrors[name]);
        var declared = Alternatives(Declarations.Value[name]);

        if (typeInfo.Type.IsEnum)
        {
            var expected = Enum.GetValues(typeInfo.Type).Cast<object>()
                .Select(value => JsonSerializer.Serialize(value, typeInfo.Type, Options))
                .Order(StringComparer.Ordinal);
            Assert.Equal(expected, declared.Order(StringComparer.Ordinal));
            return;
        }

        if (typeInfo.PolymorphismOptions is { } polymorphism)
        {
            var tag = polymorphism.TypeDiscriminatorPropertyName;
            var variants = declared.Select(alternative => Variant(alternative, tag)).ToList();
            Assert.Equal(
                polymorphism.DerivedTypes.Select(derived => (string)derived.TypeDiscriminator!).Order(StringComparer.Ordinal),
                variants.Select(variant => variant.Tag).Order(StringComparer.Ordinal));

            foreach (var derived in polymorphism.DerivedTypes)
            {
                var variant = variants.Single(variant => variant.Tag == (string)derived.TypeDiscriminator!);
                AssertSameFields($"{name} \"{variant.Tag}\"", Options.GetTypeInfo(derived.DerivedType), variant.Fields);
            }

            return;
        }

        AssertSameFields(name, typeInfo, Fields(Assert.Single(declared)));
    }

    private static void AssertSameFields(string what, JsonTypeInfo typeInfo, Dictionary<string, string> declared)
    {
        Assert.True(typeInfo.Kind == JsonTypeInfoKind.Object, $"{what}: {typeInfo.Type.Name} is not a JSON object");
        var properties = typeInfo.Properties
            // A [JsonIgnore] property is still listed, with neither accessor.
            .Where(property => property.Get is not null || property.Set is not null)
            .Where(property => !HostOnly.Contains((typeInfo.Type, property.Name)))
            .ToDictionary(property => property.Name);

        Assert.True(
            properties.Keys.Order(StringComparer.Ordinal).SequenceEqual(declared.Keys.Order(StringComparer.Ordinal)),
            $"{what}: .NET has [{string.Join(", ", properties.Keys.Order(StringComparer.Ordinal))}], " +
            $"TypeScript has [{string.Join(", ", declared.Keys.Order(StringComparer.Ordinal))}]");

        foreach (var (name, property) in properties)
        {
            var nullable = Alternatives(declared[name]).Contains("null");
            Assert.True(
                property.IsGetNullable == nullable,
                $"{what}.{name}: nullable in {(nullable ? "TypeScript" : ".NET")} only");
        }
    }

    /// <summary>One arm of a tagged union: its tag, and its fields, including any it takes from a type it is joined with.</summary>
    private static (string Tag, Dictionary<string, string> Fields) Variant(string alternative, string tag)
    {
        var parts = SplitTopLevel(alternative, '&');
        var fields = Fields(parts[0]);
        Assert.True(fields.Remove(tag, out var literal), $"no \"{tag}\" in {alternative}");
        foreach (var joined in parts.Skip(1))
        {
            foreach (var (name, type) in Fields(Assert.Single(Alternatives(Declarations.Value[joined]))))
            {
                fields.Add(name, type);
            }
        }

        return (Unquote(literal), fields);
    }

    /// <summary>The fields of an object type, by name, each with its type as written.</summary>
    private static Dictionary<string, string> Fields(string objectType)
    {
        Assert.True(objectType.StartsWith('{') && objectType.EndsWith('}'), $"not an object type: {objectType}");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in SplitTopLevel(objectType[1..^1], ','))
        {
            if (field.Length == 0)
            {
                continue;
            }

            var colon = field.IndexOf(':', StringComparison.Ordinal);
            fields.Add(Unquote(field[..colon].Trim()), field[(colon + 1)..].Trim());
        }

        return fields;
    }

    private static List<string> Alternatives(string type) => SplitTopLevel(type, '|');

    /// <summary>Splits on a separator that is not inside braces, brackets, parentheses or angle brackets.</summary>
    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{' or '[' or '(' or '<':
                    depth++;
                    break;
                case '}' or ']' or ')' or '>':
                    depth--;
                    break;
                default:
                    if (text[i] == separator && depth == 0)
                    {
                        parts.Add(text[start..i].Trim());
                        start = i + 1;
                    }

                    break;
            }
        }

        parts.Add(text[start..].Trim());
        return parts;
    }

    private static string Unquote(string text) => text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

    private static Dictionary<string, string> ReadDeclarations()
    {
        var directory = Path.Combine(RepositoryRoot(), "packages", "ui", "src", "generated");
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.ts"))
        {
            var source = Comments().Replace(File.ReadAllText(file), "");
            var match = Declaration().Match(source);
            Assert.True(match.Success, $"no exported type in {file}");
            Assert.Equal(Path.GetFileNameWithoutExtension(file), match.Groups["name"].Value);
            declarations.Add(match.Groups["name"].Value, Whitespace().Replace(match.Groups["type"].Value, " ").Trim());
        }

        return declarations;
    }

    private static string RepositoryRoot() =>
        typeof(TypeScriptMirrorTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value!;

    /// <summary>Every context the hosts serialize through, together, the way the wire sees them.</summary>
    private static JsonSerializerOptions WireOptions()
    {
        var options = PidgeJson.CreateOptions();
        options.TypeInfoResolver = JsonTypeInfoResolver.Combine(
            ProtocolJsonContext.Wire, SessionJsonContext.Wire, StorageJsonContext.Wire, CoreJsonContext.Wire);
        options.MakeReadOnly();
        return options;
    }

    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"export type (?<name>\w+) = (?<type>.*);", RegexOptions.Singleline)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
