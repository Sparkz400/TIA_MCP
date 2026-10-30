using System.Diagnostics;
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
        ("addin-selection.v1", "addin_selection_review", "addin_selection_page"),
        ("connection-review.v1", "connection_review", "connection_review_page"),
        ("db-snapshot-review.v1", "db_snapshot_review", "db_snapshot_review_page"),
        ("hmi-component-review.v1", "hmi_component_review", "hmi_component_review_page"),
        ("multiuser-observations.v1", "multiuser_observation_review", "multiuser_observation_page"),
        ("public-certificate-review.v1", "public_certificate_review", "public_certificate_review_page"),
        ("safety-evidence-review.v1", "safety_evidence_review", "safety_evidence_report"),
        ("software-unit-review.v1", "software_unit_review", "software_unit_review_page"),
        ("teamcenter-observations.v1", "teamcenter_observation_review", "teamcenter_observation_page"),
        ("technology-profile-review.v1", "technology_profile_review", "technology_profile_report"),
        ("web-application-review.v1", "web_application_review", "web_application_review_page"),
    ];

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
        // Until 1.0 picks a product version, the bundled catalog version identifies what this build advertises.
        server.ServerInfo = new Implementation { Name = "TiaMcp.Host", Title = "TIA MCP", Version = FeatureCatalog.LoadBundled().Version };
        var schemas = OutputSchemas();
        var tools = server.ToolCollection!;
        foreach (var tool in tools.ToArray())
        {
            var name = tool.ProtocolTool.Name;
            if (!ToolGroups.Advertised(name, options.Toolsets)) { tools.Remove(tool); continue; }
            tool.ProtocolTool.Title = ToolTitles.All.TryGetValue(name, out var title) ? title
                : throw new InvalidOperationException("Tool " + name + " has no title; add one to ToolTitles.");
            if (schemas.TryGetValue(name, out var schema)) tool.ProtocolTool.OutputSchema = schema;
            // A typed result declared with OutputSchemaType describes success only; refusals carry the Host error envelope.
            else if (tool.ProtocolTool.OutputSchema is { } declared) tool.ProtocolTool.OutputSchema = WithError(declared);
        }
        var prompts = server.PromptCollection!;
        foreach (var prompt in prompts.ToArray())
            if (options.Toolsets is { } selected && !TiaPrompts.Groups[prompt.ProtocolPrompt.Name].All(group => selected.Contains(group)))
                prompts.Remove(prompt);
    }

    internal static void Configure(IMcpRequestFilterBuilder filters) =>
        filters.AddCallToolFilter(next => async (context, token) =>
        {
            if (context.Params?.ProgressToken is not { } progress) return await next(context, token);
            var tool = context.Params.Name;
            // Sent before dispatch so a broken transport fails the call before any native work starts.
            await context.Server.NotifyProgressAsync(progress, new ProgressNotificationValue { Progress = 0, Message = tool + " started" }, cancellationToken: token);
            using var done = CancellationTokenSource.CreateLinkedTokenSource(token);
            var heartbeat = Heartbeat(HeartbeatInterval, tool, done.Token,
                async (beat, message) => await context.Server.NotifyProgressAsync(progress, new ProgressNotificationValue { Progress = beat, Message = message }));
            try { return await next(context, token); }
            finally { done.Cancel(); await heartbeat; }
        });

    // Keeps clients that reset their request timeout on progress from abandoning long native calls.
    internal static async Task Heartbeat(TimeSpan interval, string tool, CancellationToken stop, Func<int, string, Task> notify)
    {
        var started = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(interval);
        try
        {
            for (var beat = 1; await timer.WaitForNextTickAsync(stop); beat++)
                // Not cancelled mid-write: the result waits for this notification, so none follows it.
                await notify(beat, $"{tool} still running after {Stopwatch.GetElapsedTime(started).TotalSeconds:0} s");
        }
        // A lost heartbeat must never change the outcome of the native call it accompanies.
        catch (Exception) { }
    }

    internal static Dictionary<string, JsonElement> OutputSchemas()
    {
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var contract in OutputContracts)
        {
            var definitions = Definitions(contract, out var document);
            foreach (var (tool, entry) in document["x-tool-contracts"]!.AsObject())
                schemas.Add(tool, Closure(contract, definitions, entry!["output"]!.DeepClone()));
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
            ?? throw new InvalidDataException("Embedded output contract " + contract + " is missing.");
        document = JsonNode.Parse(stream)!.AsObject();
        var definitions = document["$defs"]!.AsObject();
        if (definitions["error"] is null) definitions["error"] = HostError();
        return definitions;
    }

    // One tool's outputs or its error, with only the definitions they reach, so each schema stays small and self-contained.
    private static JsonElement Closure(string contract, JsonObject definitions, params JsonNode[] outputs)
    {
        var root = new JsonObject { ["type"] = "object", ["anyOf"] = new JsonArray([.. outputs, new JsonObject { ["$ref"] = "#/$defs/error" }]) };
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<JsonNode?>([root]);
        while (pending.TryPop(out var node))
            if (node is JsonObject item)
                foreach (var (key, value) in item)
                {
                    if (key == "prefixItems") throw new InvalidDataException(contract + " uses prefixItems, which draft-07 clients reject.");
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
