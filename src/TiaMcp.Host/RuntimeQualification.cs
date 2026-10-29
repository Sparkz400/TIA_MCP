using System.Text.Json;
using TiaMcp.Core;

namespace TiaMcp.Host;

// Explicit local qualification only: a Host started with --component-qualification --runtime-qualification <point>
// exits with 74 after a runtime write was sent and before its readback, so a new Host meets an unknown outcome.
internal static class RuntimeQualification
{
    internal static Action? AfterSend(HostOptions options, string point, object intent) =>
        !options.ComponentQualification || options.RuntimeQualificationPoint != point ? null : () =>
        {
            using (var file = new FileStream(Path.Combine(options.ArtifactsDirectory, "runtime-interruption-marker.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(file, new { point, hostProcessId = Environment.ProcessId,
                    hostStartedAtUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(), intent, automaticReplay = false }, BridgeProtocol.Json);
                file.Flush(true);
            }
            Environment.Exit(74);
        };
}
