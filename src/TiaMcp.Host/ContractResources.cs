using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.Host;

/// <summary>The bundled JSON contracts as MCP resources, and one payload schema per change_prepare kind, so clients need not guess
/// the free-form change_prepare.payload and hmi_inspect.request shapes.</summary>
[McpServerResourceType]
public sealed class ContractResources
{
    public const string ContractTemplate = "tia://contracts/{name}";
    public const string PayloadTemplate = "tia://change-payloads/{kind}";
    internal const string PayloadContract = "change-payloads.v1.schema.json";
    private const string Prefix = "TiaMcp.Contracts.";

    private static readonly Lazy<string[]> Names = new(() => typeof(ContractResources).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)).Select(name => name[Prefix.Length..]).Order(StringComparer.Ordinal).ToArray());
    private static readonly Lazy<JsonObject> Payloads = new(() => JsonNode.Parse(Text(PayloadContract))!.AsObject());

    internal static IReadOnlyList<string> ContractNames => Names.Value;

    internal static string Text(string name)
    {
        if (!Regex.IsMatch(name, "\\A[a-z0-9][a-z0-9.-]*\\.schema\\.json\\z") || !Names.Value.Contains(name, StringComparer.Ordinal))
            throw new McpProtocolException("ContractNotFound: no bundled contract " + name + "; read tia://contracts for the list.", McpErrorCode.ResourceNotFound);
        using var stream = typeof(ContractResources).Assembly.GetManifestResourceStream(Prefix + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [McpServerResource(UriTemplate = "tia://contracts", Name = "contracts", Title = "Bundled JSON contracts", MimeType = "application/json")]
    [Description("Names and tia://contracts/{name} URIs of every bundled JSON Schema contract (requests, results, reviews, payloads).")]
    public static string Index() => JsonSerializer.Serialize(new
    {
        contracts = Names.Value.Select(name => new { name, uri = "tia://contracts/" + name }),
        changePayloads = "tia://change-payloads",
    });

    [McpServerResource(UriTemplate = ContractTemplate, Name = "contract", Title = "Bundled JSON contract", MimeType = "application/schema+json")]
    [Description("One bundled JSON Schema contract by file name, e.g. hmi-inspection.v1.schema.json (the hmi_inspect request) or change-payloads.v1.schema.json.")]
    public static string Contract(string name) => Text(name);

    [McpServerResource(UriTemplate = "tia://change-payloads", Name = "change-payloads", Title = "change_prepare payload schemas", MimeType = "application/json")]
    [Description("Every change_prepare kind with the URI of its payload schema. Read tia://change-payloads/{kind} before preparing a change of that kind.")]
    public static string PayloadIndex()
    {
        var external = Payloads.Value["x-external-payloads"]!.AsObject();
        return JsonSerializer.Serialize(new
        {
            note = "Schemas describe the public payload; the worker stays authoritative and checks native identities and target-dependent allowlists a schema cannot express.",
            kinds = Payloads.Value["x-change-kinds"]!.AsArray().Select(kind => kind!.GetValue<string>()).Select(kind => new
            {
                kind, uri = "tia://change-payloads/" + kind,
                contract = external[kind]?["contract"]?.GetValue<string>() ?? PayloadContract,
            }),
        });
    }

    [McpServerResource(UriTemplate = PayloadTemplate, Name = "change-payload", Title = "change_prepare payload schema", MimeType = "application/schema+json")]
    [Description("The JSON Schema of change_prepare.payload for one kind: self-contained for most kinds; for HMI, library and typed graphical kinds it names the contract (tia://contracts/{name}) and JSON pointer that define the payload.")]
    public static string Payload(string kind) => PayloadSchema(kind).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    internal static JsonObject PayloadSchema(string kind)
    {
        var document = Payloads.Value;
        if (!document["x-change-kinds"]!.AsArray().Any(k => k!.GetValue<string>() == kind))
            throw new McpProtocolException("UnknownChangeKind: " + kind + " is not a change_prepare kind; read tia://change-payloads.", McpErrorCode.ResourceNotFound);
        if (document["x-external-payloads"]![kind] is JsonObject external)
        {
            var reference = new JsonObject { ["kind"] = kind, ["contract"] = external["contract"]!.DeepClone(),
                ["contractUri"] = "tia://contracts/" + external["contract"]!.GetValue<string>() };
            if (external["pointer"] is { } pointer) reference["pointer"] = pointer.DeepClone();
            if (external["note"] is { } note) reference["note"] = note.DeepClone();
            return reference;
        }
        var definitions = document["$defs"]!.AsObject();
        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = "tia://change-payloads/" + kind,
            ["title"] = "change_prepare payload of kind " + kind,
            ["description"] = document["description"]!.DeepClone(),
            ["$ref"] = "#/$defs/" + kind,
        };
        // Only the definitions this kind reaches, so each resource stays small and self-contained.
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([kind]);
        while (pending.TryPop(out var name))
        {
            if (!used.Add(name)) continue;
            foreach (var reference in References(definitions[name]!))
                pending.Push(reference);
        }
        root["$defs"] = new JsonObject(used.Select(name => KeyValuePair.Create<string, JsonNode?>(name, definitions[name]!.DeepClone())));
        return root;
    }

    private static IEnumerable<string> References(JsonNode node)
    {
        var pending = new Stack<JsonNode?>([node]);
        while (pending.TryPop(out var current))
            if (current is JsonObject item)
                foreach (var (key, value) in item)
                {
                    if (key == "$ref" && value!.GetValue<string>() is var target && target.StartsWith("#/$defs/", StringComparison.Ordinal))
                        yield return target["#/$defs/".Length..];
                    else pending.Push(value);
                }
            else if (current is JsonArray list) foreach (var value in list) pending.Push(value);
    }

    /// <summary>completion/complete for the contract and payload templates.</summary>
    internal static IEnumerable<string>? Completions(string? template, string argument) => (template, argument) switch
    {
        (ContractTemplate, "name") => Names.Value,
        (PayloadTemplate, "kind") => Payloads.Value["x-change-kinds"]!.AsArray().Select(kind => kind!.GetValue<string>()),
        _ => null,
    };
}
