#nullable disable
using System;
using System.ComponentModel;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Only an OS query handle is owned here. Disposing it never closes or terminates TIA.
    internal sealed class CapturedProcessIdentity : IDisposable
    {
        private Process _process;
        private JObject _identity;
        private string _failure;

        private CapturedProcessIdentity() { }

        internal static CapturedProcessIdentity Capture(int processId, DateTime? existedByUtc = null)
        {
            var capture = new CapturedProcessIdentity();
            var latestBirth = existedByUtc ?? DateTime.UtcNow;
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException();
                if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
                capture._process = Process.GetProcessById(processId);
                // Force retention while the original process is alive; never reacquire by PID.
                var handle = capture._process.SafeHandle;
                if (capture._process.HasExited) throw new InvalidOperationException("Process exited during capture.");
                var startedAt = capture._process.StartTime.ToUniversalTime();
                if (latestBirth.Kind != DateTimeKind.Utc || startedAt > latestBirth)
                    throw new InvalidOperationException("Process was created after the native identity observation began.");
                string image = capture._process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(image)) throw new InvalidOperationException("Process image unavailable.");
                capture._identity = new JObject { ["processId"] = processId,
                    ["startedAtUtc"] = startedAt.ToString("O"),
                    ["executablePath"] = image, ["windowsSessionId"] = capture._process.SessionId };
                if (capture._process.HasExited) throw new InvalidOperationException("Process exited during capture.");
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is Win32Exception || error is NotSupportedException || error is System.Security.SecurityException)
            {
                capture._failure = "CaptureUnavailable";
                capture._process?.Dispose(); capture._process = null;
                capture._identity = null;
            }
            return capture;
        }

        internal JObject Observe(int currentProcessId)
        {
            if (_failure == null)
            {
                try
                {
                    if (currentProcessId != _identity.Value<int>("processId")) _failure = "ProcessChanged";
                    else if (_process.HasExited) _failure = "ProcessExited";
                }
                catch (Exception error) when (error is InvalidOperationException || error is Win32Exception || error is NotSupportedException)
                { _failure = "IdentityQueryUnavailable"; }
            }
            return new JObject { ["state"] = _failure == null ? "available" : "unavailable", ["reason"] = _failure,
                ["process"] = _identity?.DeepClone(), ["osProcessIdentityVerified"] = _failure == null };
        }

        public void Dispose()
        {
            _failure = "Disposed";
            _process?.Dispose(); _process = null;
        }
    }
}
