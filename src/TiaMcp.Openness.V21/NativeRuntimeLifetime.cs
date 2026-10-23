#nullable disable
using System;

namespace TiaMcp.Openness.V21
{
    // The STA worker owns the API lifetime. Individual operations release their
    // instance interfaces, but must not tear down a process-wide runtime manager.
    internal sealed class NativeRuntimeLifetime : IDisposable
    {
        private Action _shutdown;
        private bool _disposed;

        internal void RegisterShutdown(Action shutdown)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativeRuntimeLifetime));
            if (shutdown == null) throw new ArgumentNullException(nameof(shutdown));
            if (_shutdown == null) _shutdown = shutdown;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var shutdown = _shutdown;
            _shutdown = null;
            shutdown?.Invoke();
        }
    }
}
