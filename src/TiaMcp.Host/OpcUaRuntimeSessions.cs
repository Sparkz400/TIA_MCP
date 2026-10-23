using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.OpcUa.Runtime;

namespace TiaMcp.Host;

public sealed class OpcUaRuntimeSessions : IAsyncDisposable
{
    internal OpcUaRuntimeSessionStore Store { get; }
    internal OpcUaRuntimeCaptures Captures { get; }
    internal OpcUaRuntimeSubscriptions Subscriptions { get; }
    public OpcUaRuntimeSessions(HostOptions options)
    {
        Store = new(Path.Combine(options.ArtifactsDirectory, "runtime-ua"), (selection, consumer) =>
            new CredentialVault(CredentialVault.UserRoot(selection.VaultName!)).Use(selection.Credential!,
                OpcUaRuntimeSessionStore.Binding(selection), consumer));
        try { Captures = new(Store, options.ArtifactsDirectory); }
        catch { Store.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
        try { Subscriptions = new(Store, options.ArtifactsDirectory); }
        catch
        {
            try { Captures.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally { Store.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        try { await Subscriptions.DisposeAsync(); }
        finally
        {
            try { await Captures.DisposeAsync(); }
            finally { await Store.DisposeAsync(); }
        }
    }
}
