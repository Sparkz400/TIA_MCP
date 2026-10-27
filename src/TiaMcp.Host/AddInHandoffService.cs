using Microsoft.Extensions.Hosting;
using TiaMcp.Core;
using TiaMcp.Core.AddIns;

namespace TiaMcp.Host;

internal sealed class AddInHandoffService(HostOptions options, IBridgeClient bridge) : BackgroundService
{
    internal bool Failed { get; private set; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Without explicit --addin-enrollment no producer is admitted. Production never injects a synthetic authority.
            await using var receiver = new AddInHandoffReceiver(options.ArtifactsDirectory, options.AddInEnrollment?.Authority(bridge, options.ArtifactsDirectory));
            await receiver.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { Failed = true; throw; }
    }
}
