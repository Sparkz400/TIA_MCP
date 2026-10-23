using System.Diagnostics;
using System.Reflection;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests
{
    internal static class AddInOwnerTests
    {
        private static async Task<Process> Child()
        {
            var start = new ProcessStartInfo(BridgeFixture.DotnetPath()) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--identity-child");
            var child = Process.Start(start)!;
            try { Check.Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))); return child; }
            catch { if (!child.HasExited) child.Kill(); await child.WaitForExitAsync(); child.Dispose(); throw; }
        }
        private static async Task Stop(Process child)
        {
            if (!child.HasExited)
            {
                await child.StandardInput.WriteLineAsync("exit");
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            }
            Check.Equal(0, child.ExitCode);
        }
        internal static async Task ProcessIdentity()
        {
            using var child = await Child();
            try
            {
                using var identity = CapturedProcessIdentity.Capture(child.Id);
                var observed = identity.Observe(child.Id);
                Check.Equal(true, observed.Value<bool>("osProcessIdentityVerified"));
                Check.Equal(child.Id, observed["process"]!.Value<int>("processId"));
                Check.Equal(child.StartTime.ToUniversalTime().ToString("O"), observed["process"]!.Value<string>("startedAtUtc"));
                Check.Equal(child.MainModule!.FileName, observed["process"]!.Value<string>("executablePath"));
                Check.Equal(child.SessionId, observed["process"]!.Value<int>("windowsSessionId"));
                observed["process"]!["processId"] = -1;
                Check.Equal(child.Id, identity.Observe(child.Id)["process"]!.Value<int>("processId"));
                identity.Dispose(); identity.Dispose();
                Check.Equal(false, child.HasExited);
                Check.Equal(false, identity.Observe(child.Id).Value<bool>("osProcessIdentityVerified"));
                Check.Equal("Disposed", identity.Observe(child.Id).Value<string>("reason"));
            }
            finally { await Stop(child); }
        }
        internal static async Task Refusals()
        {
            using var child = await Child();
            try
            {
                using var changed = CapturedProcessIdentity.Capture(child.Id);
                using var tooNew = CapturedProcessIdentity.Capture(child.Id, child.StartTime.ToUniversalTime().AddTicks(-1));
                Check.Equal("CaptureUnavailable", tooNew.Observe(child.Id).Value<string>("reason"));
                Check.Equal("ProcessChanged", changed.Observe(Environment.ProcessId).Value<string>("reason"));
                Check.Equal(false, changed.Observe(child.Id).Value<bool>("osProcessIdentityVerified"));
                using var exited = CapturedProcessIdentity.Capture(child.Id);
                await Stop(child);
                Check.Equal("ProcessExited", exited.Observe(child.Id).Value<string>("reason"));
                Check.Equal(false, exited.Observe(child.Id).Value<bool>("osProcessIdentityVerified"));
                foreach (int pid in new[] { 0, -1, int.MaxValue })
                {
                    using var absent = CapturedProcessIdentity.Capture(pid);
                    Check.Equal("CaptureUnavailable", absent.Observe(pid).Value<string>("reason"));
                    Check.Equal(false, absent.Observe(Environment.ProcessId).Value<bool>("osProcessIdentityVerified"));
                    Check.Equal(JTokenType.Null, absent.Observe(pid)["process"]!.Type);
                }
            }
            finally { await Stop(child); }
        }
        internal static async Task Context()
        {
            using var identity = CapturedProcessIdentity.Capture(Environment.ProcessId);
            var owner = OpennessSession.OwnerForTest(identity, Environment.ProcessId);
            var first = owner.ReadOwnerForTest(new());
            Check.Equal("addin-owner-context.v1", first.Value<string>("schemaVersion"));
            Check.Equal(true, first.Value<bool>("nativeProcessIdMatched")); // Synthetic native-ID route only.
            Check.Equal(Environment.ProcessId, first.Value<int>("workerProcessId"));
            foreach (string field in new[] { "producerAuthenticated", "liveSelectionVerified", "nativeQualified", "ownsPortalProcess" })
                Check.Equal(false, first.Value<bool>(field));
            Check.Equal(true, JToken.DeepEquals(first, owner.ReadOwnerForTest(new())));
            owner.ChangeGenerationForTest();
            Check.Equal(false, first.Value<string>("sessionGeneration") == owner.ReadOwnerForTest(new()).Value<string>("sessionGeneration"));
            var other = OpennessSession.OwnerForTest(null, null).ReadOwnerForTest(new());
            Check.Equal(false, first.Value<string>("workerInstanceId") == other.Value<string>("workerInstanceId"));
            Check.Equal("NotAttached", other.Value<string>("reason"));
            Check.Equal(false, other.Value<bool>("nativeProcessIdMatched"));
            await Check.ThrowsAsync<TiaMcp.Openness.V21.BridgeException>(() => Task.Run(() => owner.ReadOwnerForTest(new() { ["processId"] = Environment.ProcessId })));
            owner.ChangeOwnerForTest(Environment.ProcessId + 1);
            Check.Equal(false, owner.ReadOwnerForTest(new()).Value<bool>("nativeProcessIdMatched"));
            owner.ChangeOwnerForTest(Environment.ProcessId);
            Check.Equal(false, owner.ReadOwnerForTest(new()).Value<bool>("nativeProcessIdMatched"));
        }
    }
}

namespace TiaMcp.Openness.V21
{
    // Only the Siemens current-process-ID route is synthetic; production owner projection is linked unchanged.
    internal static class PortalProcesses { internal static int CurrentId(object portal) => (int)portal; }
    internal sealed partial class OpennessSession
    {
        private object? _portal;
        private int? _processId;
        private bool _ownsPortal = false;
        private string _modelSessionGeneration = Guid.NewGuid().ToString("N");
        internal static OpennessSession OwnerForTest(CapturedProcessIdentity? identity, int? processId) => new()
        { _portalProcessIdentity = identity!, _processId = processId, _portal = processId };
        internal JObject ReadOwnerForTest(JObject args) => AddInOwnerContext(args);
        internal void ChangeOwnerForTest(int processId) => _portal = processId;
        internal void ChangeGenerationForTest() => _modelSessionGeneration = Guid.NewGuid().ToString("N");
    }
}
