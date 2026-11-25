using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests
{
    internal static class WorkerLoopTests
    {
        // The Host writes protocolVersion and id first; a frame Newtonsoft refuses (here a repeated name) must still be
        // answered under its id, or the Host faults the bridge and the worker session ends.
        internal static Task UnloadableRequestKeepsItsId()
        {
            string frame = TiaMcp.Core.BridgeProtocol.EncodeRequest("0123456789abcdef0123456789abcdef", "block_read", new { name = "A" });
            Check.Equal("0123456789abcdef0123456789abcdef", WorkerLoop.RecoverRequestId(frame));
            Check.Equal("0123456789abcdef0123456789abcdef", WorkerLoop.RecoverRequestId("{\"protocolVersion\":1,\"id\":\"0123456789abcdef0123456789abcdef\",\"operation\":\"x\",\"arguments\":{\"a\":1,\"a\":2}}"));
            Check.Equal(null, WorkerLoop.RecoverRequestId("{\"id\":\"abc\"}"));
            Check.Equal(null, WorkerLoop.RecoverRequestId("not json"));
            Check.Equal(null, WorkerLoop.RecoverRequestId(null));
            return Task.CompletedTask;
        }

        // Only the exact Siemens exception types map to AccessDenied/LicenseUnavailable; a type name that merely contains the word does not.
        internal static Task ErrorCodesMatchExactSiemensTypes()
        {
            static string Code(Exception exception) => WorkerLoop.Error("1", exception)["error"]!.Value<string>("code")!;
            Check.Equal("AccessDenied", Code(new Siemens.Engineering.EngineeringSecurityException()));
            Check.Equal("LicenseUnavailable", Code(new Siemens.Engineering.LicenseNotFoundException()));
            Check.Equal("EngineeringError", Code(new System.Security.SecurityException("x")));
            Check.Equal("EngineeringError", Code(new LicenseLookalike.LicenseNotFoundException()));
            Check.Equal("InvalidRequest", Code(new ArgumentException("x")));
            Check.Equal("ProjectChanged", Code(new TiaMcp.Openness.V21.BridgeException("ProjectChanged", "x")));
            return Task.CompletedTask;
        }

        private static JObject Big(string operation, bool success = true) => new JObject { ["protocolVersion"] = 1, ["id"] = "1", ["success"] = success,
            ["result"] = new JObject { ["changeId"] = "chg-1", ["state"] = "applied", ["previewHash"] = "abc", ["journalPath"] = "j.json",
                ["stages"] = new JArray("apply", "compile"), ["preview"] = new JObject { ["payload"] = new string('x', WorkerLoop.MaxResponseBytes) } } };

        // A write that already happened is answered with its summary; a read that is too large is still refused.
        internal static Task OversizeWriteIsSummarizedAndOversizeReadIsRefused()
        {
            var write = JObject.Parse(WorkerLoop.Frame("1", "change_apply", Big("change_apply")));
            Check.Equal(true, write.Value<bool>("success"));
            var result = (JObject)write["result"]!;
            Check.Equal("chg-1", result.Value<string>("changeId"));
            Check.Equal("applied", result.Value<string>("state"));
            Check.Equal("oversizeSummary", result.Value<string>("responseDetail"));
            Check.Equal(true, result["omitted"]!.Values<string>().Contains("preview"));
            Check.Equal(true, System.Text.Encoding.UTF8.GetByteCount(write.ToString()) < WorkerLoop.MaxResponseBytes);
            var read = JObject.Parse(WorkerLoop.Frame("1", "software_inspect", Big("software_inspect")));
            Check.Equal(false, read.Value<bool>("success"));
            Check.Equal("ResponseTooLarge", read["error"]!.Value<string>("code"));
            var failed = JObject.Parse(WorkerLoop.Frame("1", "change_apply", Big("change_apply", false)));
            Check.Equal("ResponseTooLarge", failed["error"]!.Value<string>("code"));
            var small = new JObject { ["protocolVersion"] = 1, ["id"] = "1", ["success"] = true, ["result"] = new JObject { ["a"] = 1 } };
            Check.Equal(small.ToString(Newtonsoft.Json.Formatting.None), WorkerLoop.Frame("1", "change_apply", small));
            return Task.CompletedTask;
        }

        // Status calls skip the full collect, but a run of them is still cut by one, and any other operation collects.
        internal static Task LightOperationsCollectOnlyEveryTenth()
        {
            int light = 0;
            Check.Equal(true, WorkerLoop.ShouldCollect("block_read", ref light));
            Check.Equal(true, WorkerLoop.ShouldCollect(null!, ref light));
            for (int i = 0; i < WorkerLoop.LightOperationsPerCollect; i++) Check.Equal(false, WorkerLoop.ShouldCollect("session_status", ref light));
            Check.Equal(true, WorkerLoop.ShouldCollect("session_status", ref light));
            Check.Equal(false, WorkerLoop.ShouldCollect("bridge_info", ref light));
            Check.Equal(true, WorkerLoop.ShouldCollect("project_inventory", ref light));
            Check.Equal(0, light);
            return Task.CompletedTask;
        }

        internal static async Task BoundedLineRefusesPartialFrames()
        {
            Check.Equal("{}", WorkerLoop.ReadBoundedLine(new StringReader("{}\n{")));
            Check.Equal(null, WorkerLoop.ReadBoundedLine(new StringReader("")));
            await Check.ThrowsAsync<InvalidDataException>(() => { WorkerLoop.ReadBoundedLine(new StringReader("{")); return Task.CompletedTask; });
        }
    }
}

namespace LicenseLookalike
{
    internal sealed class LicenseNotFoundException : Exception { }
}

namespace Siemens.Engineering
{
    // Synthetic exception types named like the Siemens ones the worker maps to error codes.
    internal sealed class EngineeringSecurityException : Exception { }
    internal sealed class LicenseNotFoundException : Exception { }
}
