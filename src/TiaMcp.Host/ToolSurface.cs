using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

/// <summary>Applies --toolsets, titles, output schemas, server identity and progress heartbeats to the advertised MCP surface.</summary>
internal static class ToolSurface
{
    private static readonly string[] SchemaKeywords = new[] { "type", "$ref", "anyOf", "oneOf", "allOf", "enum", "const" };
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    // Workflow contracts whose per-tool x-tool-contracts outputs the offline Host tests validate on every call.
    // shortcut: composite-engineering-workflow and runtime-handoff use prefixItems, which draft-07 client validators reject, and the
    // diagnostic UA binding/observation/comparison/export outputs reference other contract files; add them once those are resolved.
    internal static readonly string[] OutputContracts =
        ["automationml-workflow.v1", "graph-workflow.v1", "opc-ua-workflow.v1", "project-review-import.v1", "vci-workspace.v1",
         "opcua-runtime-subscription.v1", "diagnostic-watch-plan.v1"];

    // Supplied-evidence reviews: the review tool returns the contract's receipt; the page tool returns a page, or the receipt for its summary section.
    internal static readonly (string Contract, string Review, string Page)[] ReviewContracts =
    [
        ("access-review.v1", "access_review", "access_review_page"),
        ("addin-selection.v1", "addin_selection_review", "addin_selection_review_page"),
        ("connection-review.v1", "connection_review", "connection_review_page"),
        ("db-snapshot-review.v1", "db_snapshot_review", "db_snapshot_review_page"),
        ("hmi-component-review.v1", "hmi_component_review", "hmi_component_review_page"),
        ("multiuser-observations.v1", "multiuser_observation_review", "multiuser_observation_review_page"),
        ("public-certificate-review.v1", "public_certificate_review", "public_certificate_review_page"),
        ("safety-evidence-review.v1", "safety_evidence_review", "safety_evidence_review_page"),
        ("software-unit-review.v1", "software_unit_review", "software_unit_review_page"),
        ("teamcenter-observations.v1", "teamcenter_observation_review", "teamcenter_observation_review_page"),
        ("technology-profile-review.v1", "technology_profile_review", "technology_profile_review_page"),
        ("web-application-review.v1", "web_application_review", "web_application_review_page"),
    ];

    // Record kinds of the review_* tools whose family contract declares their x-tool-contracts outputs.
    internal static readonly (string Kind, string Contract)[] ReviewKindContracts =
    [
        ("automationml", "automationml-workflow.v1"), ("graph", "graph-workflow.v1"), ("opcua", "opc-ua-workflow.v1"), ("vci", "vci-workspace.v1"),
        ("composite_engineering", "composite-engineering-workflow.v1"), ("runtime_handoff", "runtime-handoff.v1"),
    ];

    /// <summary>Output schemas up to this many bytes are advertised inline in tools/list; larger ones only through
    /// tia://output-schemas/{tool}, named in the tool's _meta, so every client does not pay for them on each listing.</summary>
    internal const int InlineOutputSchemaBytes = 1024;
    internal const string OutputSchemaMeta = "tiaMcp/outputSchema";

    // Every Host refusal is { error: { code, message, ... } } with isError; families add their own detail fields.
    private static JsonObject HostError() => new()
    {
        ["type"] = "object", ["required"] = new JsonArray("error"),
        ["properties"] = new JsonObject { ["error"] = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("code", "message"),
            ["properties"] = new JsonObject { ["code"] = new JsonObject { ["type"] = "string" }, ["message"] = new JsonObject { ["type"] = "string" } } } }
    };

    /// <summary>Runs after the SDK collected the registered primitives.</summary>
    internal static void Apply(McpServerOptions server, HostOptions options)
    {
        // Product version from Directory.Build.props; the catalog version stays visible as the data contract version.
        server.ServerInfo = new Implementation { Name = "TiaMcp.Host", Title = "TIA MCP",
            Version = typeof(ToolSurface).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion };
        server.Capabilities ??= new ServerCapabilities();
        (server.Capabilities.Experimental ??= new Dictionary<string, object>())["tiaMcp/featureCatalog"] =
            new JsonObject { ["version"] = FeatureCatalog.LoadBundled().Version };
        var schemas = AllOutputSchemas.Value;
        var tools = server.ToolCollection!;
        foreach (var tool in tools.ToArray())
        {
            var name = tool.ProtocolTool.Name;
            if (!ToolGroups.Advertised(name, options.Toolsets) || !TiaVersionSurface.Advertised(name, options)) { tools.Remove(tool); continue; }
            tool.ProtocolTool.Title = ToolTitles.All.TryGetValue(name, out var title) ? title
                : throw new InvalidOperationException("Tool " + name + " has no title; add one to ToolTitles.");
            if (TiaVersionSurface.Description(name, options) is { } description) tool.ProtocolTool.Description = description;
            // JsonElement has an unconstrained SDK schema. Clients must see the same
            // object contract that ObjectArguments enforces at the Host boundary.
            string? objectArgument = name == "change_prepare" ? "payload"
                : new[] { "hmi_classic_screen_capture", "hmi_classic_screen_compose", "hmi_content_fingerprint", "hmi_inspect", "hmi_references", "hmi_capabilities" }.Contains(name) ? "request" : null;
            var input = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
            var structured = StructuredArguments(input, objectArgument);
            if (structured.Length > 0) JsonArguments[name] = structured;
            if (structured.Length > 0 | Enumerations(name, input)) tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(input);
            if (name.StartsWith("review_", StringComparison.Ordinal))
                (tool.ProtocolTool.Meta ??= new JsonObject())[OutputSchemaMeta] = OutputSchemaResources.Prefix + name + ".{kind}";
            else
            {
                if (!schemas.TryGetValue(name, out var schema))
                {
                    if (tool.ProtocolTool.OutputSchema is not { } declared) continue;
                    TypedSchemas[name] = schema = WithError(declared);
                }
                // Compact size, as tools/list sends it.
                bool inline = JsonSerializer.Serialize(schema).Length <= InlineOutputSchemaBytes;
                tool.ProtocolTool.OutputSchema = inline ? schema : null;
                if (!inline) (tool.ProtocolTool.Meta ??= new JsonObject())[OutputSchemaMeta] = OutputSchemaResources.Prefix + name;
            }
        }
        var prompts = server.PromptCollection!;
        foreach (var prompt in prompts.ToArray())
            // The prompts guide V21 workflows (changes, libraries, runtime); a V19 Host has none.
            if (options.TiaVersion == 19 || options.Toolsets is { } selected && !TiaPrompts.Groups[prompt.ProtocolPrompt.Name].All(group => selected.Contains(group)))
                prompts.Remove(prompt);
    }

    /// <summary>Declares the closed value sets of string arguments the SDK schema leaves open; true when it changed the schema.</summary>
    internal static bool Enumerations(string tool, JsonObject input)
    {
        if (!tool.StartsWith("review_", StringComparison.Ordinal) || input["properties"] is not JsonObject properties) return false;
        if (properties["kind"] is JsonObject kind) kind["enum"] = new JsonArray([.. ReviewRecordTools.Kinds.Select(value => (JsonNode)value)]);
        if (properties["recordType"] is JsonObject type)
            type["enum"] = new JsonArray([.. ReviewRecordTools.TranslationRecordTypes.Select(value => (JsonNode)value), null]);
        return true;
    }

    // Tool arguments bound to JsonElement, by tool name. Filled by Apply before any call is served.
    private static readonly Dictionary<string, string[]> JsonArguments = new(StringComparer.Ordinal);

    /// <summary>Types every argument the SDK left unconstrained (a JsonElement parameter) and returns their names.
    /// An unconstrained schema makes some clients send the value as a JSON-encoded string, which the tools then refuse
    /// ("The node must be of type 'JsonObject'"). A required <c>request</c> and the named object argument are declared as objects.</summary>
    internal static string[] StructuredArguments(JsonObject input, string? objectArgument)
    {
        if (input["properties"] is not JsonObject properties) return [];
        var names = new List<string>();
        foreach (var (property, schema) in properties.ToArray())
        {
            bool unconstrained = schema is JsonValue flag && flag.TryGetValue<bool>(out var any) && any
                || schema is JsonObject declared && !SchemaKeywords.Any(declared.ContainsKey);
            if (!unconstrained && property != objectArgument) continue;
            names.Add(property);
            if (property == objectArgument || property == "request")
                properties[property] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true };
        }
        return [.. names];
    }

    /// <summary>A JSON object or array that arrived as a string, parsed; null for anything else, which is passed on unchanged.</summary>
    internal static JsonElement? StructuredValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.TrimStart() is not { Length: > 0 } text || text[0] is not ('{' or '[')) return null;
        try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    internal static void Configure(IMcpRequestFilterBuilder filters) =>
        filters.AddCallToolFilter(next => async (context, token) =>
        {
            if (context.Params?.Arguments is { } arguments && JsonArguments.TryGetValue(context.Params.Name, out var structured))
                foreach (var argument in structured)
                    if (arguments.TryGetValue(argument, out var value) && StructuredValue(value) is { } parsed) arguments[argument] = parsed;
            var started = Stopwatch.GetTimestamp();
            if (context.Params?.ProgressToken is not { } progress) return Timed(await next(context, token), started);
            var tool = context.Params.Name;
            // Sent before dispatch so a broken transport fails the call before any native work starts.
            await context.Server.NotifyProgressAsync(progress, new ProgressNotificationValue { Progress = 0, Message = tool + " started" }, cancellationToken: token);
            using var done = CancellationTokenSource.CreateLinkedTokenSource(token);
            var stage = ToolProgress.Begin();
            var heartbeat = Heartbeat(HeartbeatInterval, tool, done.Token,
                async (beat, message) => await context.Server.NotifyProgressAsync(progress, new ProgressNotificationValue { Progress = beat,
                    Message = stage.Stage is { } current ? message + ": " + current : message }));
            try { return Timed(await next(context, token), started); }
            finally { done.Cancel(); await heartbeat; }
        });

    internal const string DurationMeta = "tiaMcp/durationMs";

    /// <summary>Wall-clock time of the whole tool call, Host queueing included, in the result's _meta, so declared
    /// output schemas (some closed with additionalProperties:false) stay valid.</summary>
    internal static CallToolResult Timed(CallToolResult result, long started)
    {
        (result.Meta ??= new JsonObject())[DurationMeta] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return result;
    }

    // Keeps clients that reset their request timeout on progress from abandoning long native calls.
    internal static async Task Heartbeat(TimeSpan interval, string tool, CancellationToken stop, Func<int, string, Task> notify)
    {
        var started = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(interval);
        try
        {
            for (var beat = 1; await timer.WaitForNextTickAsync(stop); beat++)
                // Not cancelled mid-write: the result waits for this notification, so none follows it.
                await notify(beat, $"{tool} still in progress after {Stopwatch.GetElapsedTime(started).TotalSeconds:0} s");
        }
        // A lost heartbeat must never change the outcome of the native call it accompanies.
        catch (Exception) { }
    }

    /// <summary>The contract-derived output schemas keyed by tool name, review_* tools by tool.kind.</summary>
    internal static readonly Lazy<Dictionary<string, JsonElement>> AllOutputSchemas = new(OutputSchemas);
    /// <summary>Typed result schemas (OutputSchemaType) with the Host error envelope, filled by Apply for the advertised tools.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonElement> TypedSchemas = new(StringComparer.Ordinal);

    /// <summary>The complete output schema of an advertised tool, or of review_* tool.kind; null when none is declared.</summary>
    internal static JsonElement? OutputSchemaOf(string name) =>
        AllOutputSchemas.Value.TryGetValue(name, out var schema) || TypedSchemas.TryGetValue(name, out schema) ? schema : null;

    internal static Dictionary<string, JsonElement> OutputSchemas()
    {
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var contract in OutputContracts)
        {
            var definitions = Definitions(contract, out var document);
            foreach (var (tool, entry) in document["x-tool-contracts"]!.AsObject())
                if (!tool.StartsWith("review_", StringComparison.Ordinal)) schemas.Add(tool, Closure(contract, definitions, entry!["output"]!.DeepClone()));
        }
        foreach (var (kind, contract) in ReviewKindContracts)
        {
            // Served only as resources, never inline, so the 2020-12 prefixItems keyword some of these contracts use is allowed.
            var definitions = Definitions(contract, out var document);
            // ReviewRecordTools refuses before the family's store runs (unknown kind, bounds) with the plain Host envelope.
            definitions["hostError"] = HostError();
            foreach (var (tool, entry) in document["x-tool-contracts"]!.AsObject())
                if (tool.StartsWith("review_", StringComparison.Ordinal))
                    schemas.Add(tool + "." + kind, Closure(contract, definitions,
                        [entry!["output"]!.DeepClone(), .. ErrorOf(entry), new JsonObject { ["$ref"] = "#/$defs/hostError" }], prefixItems: true));
        }
        foreach (var (contract, review, page) in ReviewContracts)
        {
            var definitions = Definitions(contract, out _);
            schemas.Add(review, Closure(contract, definitions, new JsonObject { ["$ref"] = "#/$defs/receipt" }));
            schemas.Add(page, Closure(contract, definitions, new JsonObject { ["$ref"] = "#/$defs/page" }, new JsonObject { ["$ref"] = "#/$defs/receipt" }));
        }
        return schemas;
    }

    private static JsonObject Definitions(string contract, out JsonObject document)
    {
        using var stream = typeof(ToolSurface).Assembly.GetManifestResourceStream("TiaMcp.Contracts." + contract + ".schema.json")
            ?? typeof(ToolSurface).Assembly.GetManifestResourceStream("TiaMcp.PublicContracts." + contract + ".schema.json")
            ?? throw new InvalidDataException("Embedded output contract " + contract + " is missing.");
        document = JsonNode.Parse(stream)!.AsObject();
        var definitions = document["$defs"]!.AsObject();
        if (definitions["error"] is null) definitions["error"] = HostError();
        return definitions;
    }

    private static JsonNode[] ErrorOf(JsonNode? entry) => entry?["error"] is { } error ? [error.DeepClone()] : [];

    private static JsonElement Closure(string contract, JsonObject definitions, params JsonNode[] outputs) => Closure(contract, definitions, outputs, false);

    // One tool's outputs or its error, with only the definitions they reach, so each schema stays small and self-contained.
    private static JsonElement Closure(string contract, JsonObject definitions, JsonNode[] outputs, bool prefixItems)
    {
        var root = new JsonObject { ["type"] = "object", ["anyOf"] = new JsonArray([.. outputs, new JsonObject { ["$ref"] = "#/$defs/error" }]) };
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<JsonNode?>([root]);
        while (pending.TryPop(out var node))
            if (node is JsonObject item)
                foreach (var (key, value) in item)
                {
                    if (key == "prefixItems" && !prefixItems) throw new InvalidDataException(contract + " uses prefixItems, which draft-07 clients reject.");
                    if (key != "$ref") { pending.Push(value); continue; }
                    var reference = value!.GetValue<string>();
                    if (!reference.StartsWith("#/$defs/", StringComparison.Ordinal))
                        throw new InvalidDataException(contract + " output reference " + reference + " leaves its own $defs.");
                    var name = reference["#/$defs/".Length..].Split('/')[0];
                    if (used.Add(name)) pending.Push(definitions[name] ?? throw new InvalidDataException(contract + " lacks $defs/" + name + "."));
                }
            else if (node is JsonArray list) foreach (var value in list) pending.Push(value);
        root["$defs"] = new JsonObject(used.Select(name => KeyValuePair.Create<string, JsonNode?>(name, definitions[name]!.DeepClone())));
        return JsonSerializer.SerializeToElement(root);
    }

    // The SDK generates the declared type's schema with its own serializer defaults; the tools serialize with Web defaults,
    // which differ only in null and enum handling that these result types do not depend on. The surface test checks real responses.
    internal static JsonElement WithError(JsonElement declared)
    {
        var result = JsonNode.Parse(declared.GetRawText())!.AsObject();
        result.Remove("$schema");
        var pending = new Stack<JsonNode?>([result]);
        while (pending.TryPop(out var node))
            if (node is JsonObject item)
            {
                if (item["$ref"]?.GetValue<string>() is { } reference && reference.StartsWith('#'))
                    item["$ref"] = "#/$defs/result" + reference[1..];
                foreach (var (_, value) in item) pending.Push(value);
            }
            else if (node is JsonArray list) foreach (var value in list) pending.Push(value);
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["anyOf"] = new JsonArray(new JsonObject { ["$ref"] = "#/$defs/result" }, new JsonObject { ["$ref"] = "#/$defs/error" }),
            ["$defs"] = new JsonObject { ["result"] = result, ["error"] = HostError() }
        });
    }
}
