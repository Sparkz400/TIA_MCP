using System.Collections.Concurrent;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Simulation;

namespace TiaMcp.Host;

/// <summary>Binds runtime endpoints to an owned PLCSIM Advanced instance through the simulator worker. Physical PLCs never qualify.</summary>
public sealed class PlcsimTargets(SimulatorClient simulator, IBridgeClient engineering, HostOptions options)
{
    public async Task<object> Bind(int instanceId, string instanceName, string creationId, string? downloadTicket, string? projectName, string? plcPath, CancellationToken cancellationToken)
    {
        var binding = await Observe(instanceId, instanceName, creationId);
        // The fingerprint comes only from the engineering worker's own compare against this owned instance, never from the caller.
        if (downloadTicket is not null)
            binding = binding with { ProgramFingerprintSha256 = PlcsimTargetBindings.Fingerprint(binding,
                await engineering.CallAsync("plc_online_compare", new { downloadTicket, projectName, plcPath, maxItems = 500 }, cancellationToken)) };
        return new { binding = PlcsimTargetBindings.Retain(options.ArtifactsDirectory, binding), record = binding,
            programFingerprint = binding.ProgramFingerprintSha256 is null ? "notObserved" : "boundOnlineFingerprint", physicalPlc = false, nativeQualified = false };
    }

    // The adapter each runtime session's endpoint was routed through when it connected (null if undetermined or it changed during the
    // connect), keyed by "web:"/"ua:" and the attemptId. Sessions never survive a Host restart, so memory is enough.
    private readonly ConcurrentDictionary<string, string?> connectedRoutes = new(StringComparer.Ordinal);

    /// <summary>Runs one session connect and records the route it was opened on; the first attempt of a session ID is the one kept.</summary>
    public async Task<T> Connect<T>(string session, string endpoint, Func<Task<T>> connect)
    {
        string? before = SimulationNetworkRoute.Adapter(endpoint);
        try { return await connect(); }
        finally { string? after = SimulationNetworkRoute.Adapter(endpoint); connectedRoutes.TryAdd(session, before == after ? before : null); }
    }

    /// <summary>Refuses unless the endpoint is a controller address of the bound instance, routed through the PLCSIM adapter both when the session
    /// connected and now, and the worker still observes that exact owned instance.</summary>
    public async Task Require(string bindingSha256, string session, string endpoint)
    {
        var recorded = PlcsimTargetBindings.Read(options.ArtifactsDirectory, bindingSha256);
        PlcsimTargetBindings.RequireEndpoint(recorded, endpoint, connectedRoutes.TryGetValue(session, out var connected) ? connected : null, SimulationNetworkRoute.Adapter(endpoint));
        PlcsimTargetBindings.RequireSameInstance(recorded, await Observe(recorded.InstanceId, recorded.InstanceName, recorded.CreationId));
    }

    // Native simulator calls are never cancelled: a cancelled bridge call faults the simulator worker for every later tool.
    private async Task<PlcsimTargetBinding> Observe(int instanceId, string instanceName, string creationId) => PlcsimTargetBindings.Observed(
        await simulator.Bridge.CallAsync("sim_identity", new { instanceId, instanceName, creationId }, CancellationToken.None),
        instanceId, instanceName, creationId, DateTimeOffset.UtcNow);
}
