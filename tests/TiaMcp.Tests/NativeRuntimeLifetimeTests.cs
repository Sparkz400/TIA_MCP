using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class NativeRuntimeLifetimeTests
{
    internal static Task DefersShutdownUntilWorkerDisposal()
    {
        int calls = 0;
        var lifetime = new NativeRuntimeLifetime();
        Action shutdown = () => calls++;
        Check.Equal(false, lifetime.Registered);
        lifetime.RegisterShutdown(shutdown);
        lifetime.RegisterShutdown(shutdown);
        Check.Equal(true, lifetime.Registered);
        Check.Equal(0, calls);
        lifetime.Dispose();
        Check.Equal(false, lifetime.Registered);
        lifetime.Dispose();
        Check.Equal(1, calls);
        return Task.CompletedTask;
    }

    internal static async Task DoesNotRetryShutdownFailures()
    {
        int calls = 0;
        var lifetime = new NativeRuntimeLifetime();
        lifetime.RegisterShutdown(() => { calls++; throw new IOException("native shutdown failed"); });
        await Check.ThrowsAsync<IOException>(() => Task.Run(lifetime.Dispose));
        lifetime.Dispose();
        Check.Equal(1, calls);
        await Check.ThrowsAsync<ObjectDisposedException>(() => Task.Run(() => lifetime.RegisterShutdown(() => calls++)));
    }

    internal static Task UnusedWorkerHasNoNativeShutdown()
    {
        var lifetime = new NativeRuntimeLifetime();
        lifetime.Dispose();
        lifetime.Dispose();
        return Task.CompletedTask;
    }
}
