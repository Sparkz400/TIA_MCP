using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests
{
    internal static class WorkerLoopTests
    {
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
