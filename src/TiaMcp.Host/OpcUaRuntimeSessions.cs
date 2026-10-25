using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.OpcUa.Runtime;

namespace TiaMcp.Host;

public sealed class OpcUaRuntimeSessions : IAsyncDisposable
{
    internal OpcUaRuntimeSessionStore Store { get; }
    internal OpcUaRuntimeCaptures Captures { get; }
    internal OpcUaRuntimeSubscriptions Subscriptions { get; }
    private readonly UaClientIdentityLease? identity;
    public OpcUaRuntimeSessions(HostOptions options)
    {
        identity = options.UaClientIdentity is null ? null :
            new CredentialVault(CredentialVault.UaIdentityRoot(options.UaClientIdentity)).OpenUaIdentity();
        try { Store = new(Path.Combine(options.ArtifactsDirectory, "runtime-ua"), (selection, consumer) =>
            new CredentialVault(CredentialVault.UserRoot(selection.VaultName!)).Use(selection.Credential!,
                OpcUaRuntimeSessionStore.Binding(selection), consumer), identity?.Certificate); }
        catch { identity?.Dispose(); throw; }
        try { Captures = new(Store, options.ArtifactsDirectory); }
        catch { try { Store.DisposeAsync().AsTask().GetAwaiter().GetResult(); } finally { identity?.Dispose(); } throw; }
        try { Subscriptions = new(Store, options.ArtifactsDirectory); }
        catch
        {
            try { Captures.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally { try { Store.DisposeAsync().AsTask().GetAwaiter().GetResult(); } finally { identity?.Dispose(); } }
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        try { await Subscriptions.DisposeAsync(); }
        finally
        {
            try { await Captures.DisposeAsync(); }
            finally { try { await Store.DisposeAsync(); } finally { identity?.Dispose(); } }
        }
    }
}
