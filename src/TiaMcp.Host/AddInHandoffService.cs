using Microsoft.Extensions.Hosting;
using TiaMcp.Core.AddIns;

namespace TiaMcp.Host;

internal sealed class AddInHandoffService(HostOptions options) : BackgroundService
{
    internal bool Failed { get; private set; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // No producer enrollment route has been qualified. Production cannot inject a synthetic authority.
            await using var receiver = new AddInHandoffReceiver(options.ArtifactsDirectory);
            await receiver.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { Failed = true; throw; }
    }
}
