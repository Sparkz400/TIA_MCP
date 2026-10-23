using Newtonsoft.Json.Linq;
using Siemens.Engineering.Compiler;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class PlcCompilationTests
{
    private const string Path = "/projects/P/devices/S/items/CPU/plc/PLC";

    internal static async Task Admission()
    {
        var compiler = new Compiler();
        Check.Equal(Path, (string)PlcCompilation.Prepare(new(), Path, Path, compiler)["plcPath"]!);
        foreach (var payload in new[] { JObject.Parse("{\"parameters\":{}}"), JObject.Parse("{\"xml\":\"data\"}") })
            await Check.ThrowsAsync<BridgeException>(() => { PlcCompilation.Prepare(payload, Path, Path, compiler); return Task.CompletedTask; });
        foreach (var selected in new[] { "", " ", "/another/plc" })
            await Check.ThrowsAsync<BridgeException>(() => { PlcCompilation.Prepare(new(), Path, selected, compiler); return Task.CompletedTask; });
        await Check.ThrowsAsync<BridgeException>(() => { PlcCompilation.Prepare(new(), Path, Path, null!); return Task.CompletedTask; });
        Check.Equal(0, compiler.Calls);
    }

    internal static Task Diagnostics()
    {
        var compiler = new Compiler { Result = new() { ErrorCount = 1, WarningCount = 2, State = "Error",
            Messages = [new() { State = "Error", Description = "original failure", Path = "/block" }] } };
        var events = new List<string>();
        var result = PlcCompilation.Compile(compiler, Path, events.Add);
        Check.Equal(1, compiler.Calls); Check.Equal("compiler_dispatching", string.Join(",", events));
        Check.Equal(1, (int)result["errorCount"]!); Check.Equal(2, (int)result["warningCount"]!);
        Check.Equal(Path, (string)result["targets"]![0]!["target"]!);
        Check.Equal("original failure", (string)result["targets"]![0]!["messages"]![0]!["description"]!);
        Check.Equal("Error", (string)result["targets"]![0]!["state"]!);
        return Task.CompletedTask;
    }

    internal static async Task DispatchFailure()
    {
        var compiler = new Compiler();
        await Check.ThrowsAsync<IOException>(() => { PlcCompilation.Compile(compiler, Path, _ => throw new IOException("journal unavailable")); return Task.CompletedTask; });
        Check.Equal(0, compiler.Calls);
        compiler.After = () => throw new IOException("native outcome unknown");
        var events = new List<string>();
        await Check.ThrowsAsync<IOException>(() => { PlcCompilation.Compile(compiler, Path, events.Add); return Task.CompletedTask; });
        Check.Equal(1, compiler.Calls); Check.Equal("compiler_dispatching", string.Join(",", events));
    }
}
