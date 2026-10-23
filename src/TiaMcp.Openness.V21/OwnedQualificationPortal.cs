using System;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    // Can only create a new UI Portal; never accepts an attached connection or PID.
    internal sealed class OwnedQualificationPortal : IDisposable
    {
        private readonly TiaPortal _connection;
        private bool _disposed;

        internal OwnedQualificationPortal()
        {
            _connection = new TiaPortal(TiaPortalMode.WithUserInterface);
        }

        internal TiaPortal Connection => _connection;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; // A failed close is an unknown outcome, never replayed.
            var process = _connection.GetCurrentProcess();
            _connection.Dispose();
            // The controller independently proves exit through its original OS handle.
            process.Dispose();
        }
    }
}
