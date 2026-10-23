using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class FrameworkFixture
{
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Framework owner fixture failed."); }
    private static void Main(string[] args)
    {
        if (args.Length != 1 || File.Exists(args[0])) throw new ArgumentException("Select a new receipt path.");
        using (var self = Process.GetCurrentProcess())
        using (var identity = CapturedProcessIdentity.Capture(self.Id))
        {
            var owner = OpennessSession.Create(identity, self.Id);
            var available = owner.Read();
            Require(available.Value<bool>("osProcessIdentityVerified") && available.Value<bool>("nativeProcessIdMatched"));
            Require(available["process"].Value<string>("startedAtUtc") == self.StartTime.ToUniversalTime().ToString("O"));
            Require(!available.Value<bool>("producerAuthenticated") && !available.Value<bool>("nativeQualified"));
            Require(JToken.DeepEquals(available, owner.Read()));
            identity.Dispose(); identity.Dispose();
            var disposed = owner.Read();
            Require(!disposed.Value<bool>("osProcessIdentityVerified") && !self.HasExited);
            using (var unavailable = CapturedProcessIdentity.Capture(0))
            {
                var failed = OpennessSession.Create(unavailable, self.Id).Read();
                Require(failed.Value<string>("reason") == "CaptureUnavailable" && !failed.Value<bool>("nativeProcessIdMatched"));
                var detached = OpennessSession.Create(null, null).Read();
                Require(detached.Value<string>("reason") == "NotAttached" && !detached.Value<bool>("osProcessIdentityVerified"));
                var report = new JObject { ["completed"] = true, ["nativeExecuted"] = false,
                    ["nativeIdRoute"] = "synthetic", ["clrVersion"] = Environment.Version.ToString(),
                    ["mscorlibVersion"] = FileVersionInfo.GetVersionInfo(typeof(object).Assembly.Location).FileVersion,
                    ["available"] = available, ["disposed"] = disposed, ["captureUnavailable"] = failed, ["detached"] = detached };
                using (var output = new StreamWriter(new FileStream(args[0], FileMode.CreateNew, FileAccess.Write))) output.WriteLine(report);
            }
        }
        Console.WriteLine("PASS .NET Framework owner identity: original OS handle, disposal, failed capture and detached context; synthetic native-ID route only.");
    }
}

namespace TiaMcp.Openness.V21
{
    internal static class PortalProcesses { internal static int CurrentId(object portal) { return (int)portal; } }
    internal sealed partial class OpennessSession
    {
        private object _portal;
        private int? _processId;
        private bool _ownsPortal = false;
        private string _modelSessionGeneration = Guid.NewGuid().ToString("N");
        internal static OpennessSession Create(CapturedProcessIdentity identity, int? processId)
        { return new OpennessSession { _portalProcessIdentity = identity, _processId = processId, _portal = processId }; }
        internal JObject Read() { return AddInOwnerContext(new JObject()); }
    }
}
