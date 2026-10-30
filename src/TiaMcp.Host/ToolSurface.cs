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
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    // Workflow contracts whose per-tool outputs and shared error envelope the offline Host tests validate on every call.
    // shortcut: composite-engineering-workflow uses prefixItems, which draft-07 client validators reject; add it once clients default to JSON Schema 2020-12.
    private static readonly string[] OutputContracts =
        ["automationml-workflow.v1", "graph-workflow.v1", "opc-ua-workflow.v1", "project-review-import.v1", "vci-workspace.v1"];

    /// <summary>Runs after the SDK collected the registered primitives.</summary>
    internal static void Apply(McpServerOptions server, HostOptions options)
    {
        // Product version from Directory.Build.props; the catalog version stays visible as the data contract version.
        server.ServerInfo = new Implementation { Name = "TiaMcp.Host", Title = "TIA MCP",
            Version = typeof(ToolSurface).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion };
        server.Capabilities ??= new ServerCapabilities();
        (server.Capabilities.Experimental ??= new Dictionary<string, object>())["tiaMcp/featureCatalog"] =
            new JsonObject { ["version"] = FeatureCatalog.LoadBundled().Version };
        var schemas = OutputSchemas();
        var tools = server.ToolCollection!;
        foreach (var tool in tools.ToArray())
        {
            var name = tool.ProtocolTool.Name;
            if (!ToolGroups.Advertised(name, options.Toolsets)) { tools.Remove(tool); continue; }
            tool.ProtocolTool.Title = ToolTitles.All.TryGetValue(name, out var title) ? title
                : throw new InvalidOperationException("Tool " + name + " has no title; add one to ToolTitles.");
            if (schemas.TryGetValue(name, out var schema)) tool.ProtocolTool.OutputSchema = schema;
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

    private static Dictionary<string, JsonElement> OutputSchemas()
    {
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var contract in OutputContracts)
        {
            using var stream = typeof(ToolSurface).Assembly.GetManifestResourceStream("TiaMcp.Contracts." + contract + ".schema.json")
                ?? throw new InvalidDataException("Embedded output contract " + contract + " is missing.");
            var document = JsonNode.Parse(stream)!.AsObject();
            var definitions = document["$defs"]!.AsObject();
            foreach (var (tool, entry) in document["x-tool-contracts"]!.AsObject())
            {
                var root = new JsonObject { ["type"] = "object", ["anyOf"] = new JsonArray(entry!["output"]!.DeepClone(), new JsonObject { ["$ref"] = "#/$defs/error" }) };
                // Only the definitions this tool's output reaches, so each schema stays small.
                var used = new SortedSet<string>(StringComparer.Ordinal);
                var pending = new Stack<JsonNode?>([root]);
                while (pending.TryPop(out var node))
                    if (node is JsonObject item)
                        foreach (var (key, value) in item)
                        {
                            if (key == "$ref" && value?.GetValue<string>() is { } reference && reference.StartsWith("#/$defs/", StringComparison.Ordinal))
                            {
                                var name = reference["#/$defs/".Length..];
                                if (used.Add(name)) pending.Push(definitions[name] ?? throw new InvalidDataException(contract + " lacks $defs/" + name + "."));
                            }
                            else pending.Push(value);
                        }
                    else if (node is JsonArray list) foreach (var value in list) pending.Push(value);
                root["$defs"] = new JsonObject(used.Select(name => KeyValuePair.Create<string, JsonNode?>(name, definitions[name]!.DeepClone())));
                schemas.Add(tool, JsonSerializer.SerializeToElement(root));
            }
        }
        return schemas;
    }
}
