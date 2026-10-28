using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Host;

// Explicit local qualification only. Normal Hosts neither advertise these tools
// nor inject faults or admit unqualified document edits.
internal sealed class WatchQualificationBridge(IBridgeClient inner, HostOptions options) : IBridgeClient
{
    private string? admitted; // the operation of the last mutation_begin; mutation_dispatch carries only its lease

    public async Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
    {
        var point = options.WatchQualificationPoint!;
        if (operation == "mutation_begin") admitted = JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)?["operation"]?.GetValue<string>();
        var marker = Path.Combine(options.ArtifactsDirectory, "interruption-marker.json");
        if (operation is "watch_checkpoint" or "watch_apply_native" or "watch_restore_native")
        {
            var input = JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject();
            input["qualificationInterruptAt"] = point;
            input["qualificationMarkerPath"] = marker;
            arguments = input;
        }
        var result = await inner.CallAsync(operation, arguments, cancellationToken);
        // An admitted mutation's Host point fires after its native dispatch returned, before the Host records the outcome.
        if (point == "host." + (operation == "mutation_dispatch" ? admitted : operation))
        {
            using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(file, new { point, hostProcessId = Environment.ProcessId,
                    hostStartedAtUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                    response = result, automaticReplay = false }, BridgeProtocol.Json);
                file.Flush(true);
            }
            Environment.Exit(74);
        }
        return result;
    }
}

[McpServerToolType]
public sealed class WatchQualificationTools(IBridgeClient bridge, HostOptions options)
{
    private void RequireQualification()
    {
        if (!options.ComponentQualification || options.WatchQualificationPoint is null)
            throw new InvalidOperationException("An explicit isolated qualification Host is required.");
    }

    [McpServerTool(Name = "qualification_watch_fixture_seed", Destructive = true, OpenWorld = false)]
    [Description("Isolated test-only owned scratch fixture seed; unavailable on normal Hosts.")]
    public async Task<JsonElement> Seed(string inputPath, string inputSha256, CancellationToken cancellationToken)
    {
        RequireQualification();
        return await bridge.CallAsync("qualification_watch_fixture_seed", new { inputPath, inputSha256,
            tableName = "MilestoneWatch", artifactDirectory = Path.Combine(options.ArtifactsDirectory, "seed") }, cancellationToken);
    }

    [McpServerTool(Name = "qualification_watch_compete", Destructive = false, OpenWorld = false)]
    [Description("Isolated test-only competing durable owner probe; makes no native call.")]
    public object Compete(string operationId)
    {
        RequireQualification();
        var store = new EngineeringOperationStore(options.ArtifactsDirectory);
        var original = store.Read(operationId);
        var competitor = store.Prepare(original.Plan, "competing-host-" + Environment.ProcessId);
        try
        {
            var acquired = store.Acquire(competitor.Id, competitor.Revision);
            store.Stop(acquired.Id, acquired.Revision);
            throw new InvalidOperationException("Competing owner unexpectedly acquired interrupted resources.");
        }
        catch (BridgeException error) when (error.Code == "ResourceBusy")
        {
            return new { code = error.Code, operationId, competitorId = competitor.Id,
                hostProcessId = Environment.ProcessId, nativeDispatchInvoked = false };
        }
    }
}
