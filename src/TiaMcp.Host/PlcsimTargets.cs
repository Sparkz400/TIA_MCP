using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

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

    /// <summary>Refuses unless the endpoint is a controller address of the bound instance, routed through the PLCSIM adapter, and the worker still observes that exact owned instance.</summary>
    public async Task Require(string bindingSha256, string endpoint)
    {
        var recorded = PlcsimTargetBindings.Read(options.ArtifactsDirectory, bindingSha256);
        PlcsimTargetBindings.RequireEndpoint(recorded, endpoint, RouteAdapter(endpoint));
        PlcsimTargetBindings.RequireSameInstance(recorded, await Observe(recorded.InstanceId, recorded.InstanceName, recorded.CreationId));
    }

    // The description of the adapter Windows' best route to the endpoint's IPv4 address leaves through; null when undetermined.
    private static string? RouteAdapter(string endpoint)
    {
        try
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.IPv4) return null;
            var destination = new byte[16]; // sockaddr_in: AF_INET, port 0, the address
            destination[0] = 2;
            IPAddress.Parse(uri.Host).GetAddressBytes().CopyTo(destination, 4);
            if (GetBestInterfaceEx(destination, out uint index) != 0) return null;
            var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.Supports(NetworkInterfaceComponent.IPv4) && n.GetIPProperties().GetIPv4Properties()?.Index == index).ToArray();
            return adapters.Length == 1 ? adapters[0].Description : null;
        }
        catch (NetworkInformationException) { return null; }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterfaceEx(byte[] destination, out uint index);

    // Native simulator calls are never cancelled: a cancelled bridge call faults the simulator worker for every later tool.
    private async Task<PlcsimTargetBinding> Observe(int instanceId, string instanceName, string creationId) => PlcsimTargetBindings.Observed(
        await simulator.Bridge.CallAsync("sim_identity", new { instanceId, instanceName, creationId }, CancellationToken.None),
        instanceId, instanceName, creationId, DateTimeOffset.UtcNow);
}
