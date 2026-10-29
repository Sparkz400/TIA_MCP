using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using TiaMcp.Openness.V21;
using WorkerError = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests
{
    internal static class DeviceCompilationTests
    {
        internal static async Task AuthorizedScope()
        {
            var device = Device("A"); var project = new Project { Name = "P", Devices = [device] };
            string[] targets = ["/projects/P/devices/A"];
            DeviceCompilation.RequireTargets(project, targets);
            DeviceCompilation.Compile(project, expectedTargets: targets);
            Check.Equal(1, device.Compiler!.Calls);
            foreach (var bad in new[] { Array.Empty<string>(), new[] { "/projects/P/devices/B" }, new[] { targets[0], targets[0] } })
                await Refuse("CompileScopeChanged", () => DeviceCompilation.Compile(project, expectedTargets: bad));
            var empty = Device("Empty"); project.Devices = [device, empty];
            await Refuse("CompileScopeChanged", () => DeviceCompilation.RequireTargets(project, targets));
            await Refuse("CompileScopeChanged", () => DeviceCompilation.Compile(project, expectedTargets: targets));
            Check.Equal(1, device.Compiler.Calls); Check.Equal(0, empty.Compiler!.Calls);
        }

        internal static async Task HardwarePaths()
        {
            var cpu = new DeviceItem { Name = "CPU/日本%" };
            var module = new DeviceItem { Name = "CPU/日本%" };
            var first = Device("A"); first.DeviceItems = [cpu];
            var second = Device("IO"); second.DeviceItems = [module];
            var project = new Project { Name = "P", Devices = [first], DeviceGroups = [new() { Name = "Remote", Devices = [second] }] };
            var rows = DeviceCompilation.Items(project);
            Check.Equal(2, rows.Count);
            Check.Equal("/projects/P/devices/A/items/CPU%2F%E6%97%A5%E6%9C%AC%25", rows[0].Path);
            Check.Equal("/projects/P/groups/Remote/devices/IO", rows[1].DevicePath);
            Check.Equal(true, ReferenceEquals(module, rows[1].Item));
            second.DeviceItems = [cpu];
            await Refuse("AmbiguousTarget", () => DeviceCompilation.Items(project));
            second.DeviceItems = [module]; cpu.DeviceItems = [cpu];
            await Refuse("AmbiguousTarget", () => DeviceCompilation.Items(project));
            cpu.DeviceItems = [new() { Name = "same" }, new() { Name = "same" }];
            await Refuse("AmbiguousTarget", () => DeviceCompilation.Items(project));
            cpu.DeviceItems = [new() { Name = "" }];
            await Refuse("TargetIdentityUnavailable", () => DeviceCompilation.Items(project));
        }

        internal static async Task HardwareBounds()
        {
            var device = Device(); var project = new Project { Devices = [device] };
            device.DeviceItems = Enumerable.Range(0, 50001).Select(i => new DeviceItem { Name = "Item" + i });
            await Refuse("TraversalLimit", () => DeviceCompilation.Items(project));
            var root = new DeviceItem(); var last = root; device.DeviceItems = [root];
            for (int i = 0; i < 66; i++) { var child = new DeviceItem(); last.DeviceItems = [child]; last = child; }
            await Refuse("TraversalLimit", () => DeviceCompilation.Items(project));
            Check.Equal(0, device.Compiler!.Calls);
        }

        internal static Task DiagnosticBounds()
        {
            var result = new CompilerResult { State = "Warning", WarningCount = 1,
                Messages = [new() { State = "Warning", Path = "/remote/io", Messages = [new() { State = "Error" }] }] };
            var described = DeviceCompilation.Describe(result, "/selected");
            Check.Equal("Warning", (string?)described["state"]);
            Check.Equal("Error", (string?)described["messages"]![1]!["state"]);
            Check.Equal(1, (int)described["messages"]![1]!["depth"]!);
            Check.Equal(false, (bool)described["messagesTruncated"]!);
            foreach (int count in new[] { 500, 501 })
            {
                result.Messages = Enumerable.Range(0, count).Select(_ => new CompilerResultMessage());
                described = DeviceCompilation.Describe(result, "/selected");
                Check.Equal(500, ((JArray)described["messages"]!).Count);
                Check.Equal(count > 500, (bool)described["messagesTruncated"]!);
            }
            var root = new CompilerResultMessage(); var last = root; result.Messages = [root];
            for (int i = 0; i < 66; i++) { var child = new CompilerResultMessage(); last.Messages = [child]; last = child; }
            described = DeviceCompilation.Describe(result, "/selected");
            Check.Equal(65, ((JArray)described["messages"]!).Count);
            Check.Equal(true, (bool)described["messagesTruncated"]!);
            return Task.CompletedTask;
        }

        private static Device Device(string name = "Same") => new() { Name = name, Compiler = new Compiler() };
        private static async Task Refuse(string code, Action action)
        { var error = await Check.ThrowsAsync<WorkerError>(() => { action(); return Task.CompletedTask; }); Check.Equal(code, error.Code); }

        internal static Task ExactPaths()
        {
            var first = Device(); var second = Device(); var grouped = Device("CPU/日本%");
            var project = new Project { Name = "P/日本%", Devices = [first],
                UngroupedDevicesGroup = new() { Devices = [second] },
                DeviceGroups = [new() { Name = "Outer", Groups = [new() { Name = "Inner", Devices = [grouped] }] }] };
            var events = new List<string>();
            var receipt = OpennessSession.CompileDevicesForTest(project, (path, phase) => events.Add(path + ":" + phase));
            var paths = receipt["requiredTargets"]!.Values<string>().ToArray();
            Check.Equal(3, paths.Length); Check.Equal(3, paths.Distinct().Count());
            Check.Equal("/projects/P%2F%E6%97%A5%E6%9C%AC%25/devices/Same", paths[0]);
            Check.Equal("/projects/P%2F%E6%97%A5%E6%9C%AC%25/ungrouped/Same", paths[1]);
            Check.Equal("/projects/P%2F%E6%97%A5%E6%9C%AC%25/groups/Outer/groups/Inner/devices/CPU%2F%E6%97%A5%E6%9C%AC%25", paths[2]);
            Check.Equal("device-path-v1", (string?)receipt["targetIdentityPolicy"]); Check.Equal(true, (bool)receipt["scopeComplete"]!);
            Check.Equal("Same", (string?)receipt["targets"]![0]!["target"]); Check.Equal("Same", (string?)receipt["targets"]![1]!["target"]);
            for (int i = 0; i < paths.Length; i++) {
                Check.Equal(paths[i], (string?)receipt["targets"]![i]!["targetPath"]);
                Check.Equal(paths[i] + ":dispatching", events[2*i]); Check.Equal(paths[i] + ":completed", events[2*i+1]);
            }
            Check.Equal(1, first.Compiler!.Calls); Check.Equal(1, second.Compiler!.Calls); Check.Equal(1, grouped.Compiler!.Calls);
            Console.WriteLine("Device compiler fixture: " + receipt.ToString(Newtonsoft.Json.Formatting.None));
            return Task.CompletedTask;
        }

        internal static async Task ServicesBeforeDispatch()
        {
            var first = Device("A"); var last = Device("Z"); last.Compiler = null;
            var project = new Project { Devices = [first, last] };
            // A device without a compiler service (a Startdrive drive) is listed under notCompiled, never counted as compiled.
            var receipt = OpennessSession.CompileDevicesForTest(project);
            Check.Equal(1, first.Compiler!.Calls);
            Check.Equal("/projects/Fixture/devices/A", (string?)receipt["targets"]!.Single()["targetPath"]);
            Check.Equal("/projects/Fixture/devices/Z", (string?)receipt["notCompiled"]!.Single()["targetPath"]);
            Check.Equal("Z", (string?)receipt["notCompiled"]![0]!["target"]);
            Check.Equal(2, receipt["requiredTargets"]!.Count());
            Check.Equal(0, ((JArray)OpennessSession.CompileDevicesForTest(new() { Devices = [first] })["notCompiled"]!).Count);
            first.Compiler.Calls = 0; var none = Device("N"); none.Compiler = null;
            await Refuse("UnsupportedCapability", () => OpennessSession.CompileDevicesForTest(new() { Devices = [none, last] }));
            await Refuse("CompileScopeChanged", () => DeviceCompilation.Compile(project, expectedTargets: ["/projects/Fixture/devices/A"]));
            Check.Equal(0, first.Compiler.Calls);
            last.Read = () => throw new InvalidOperationException("Synthetic getter failure");
            await Check.ThrowsAsync<InvalidOperationException>(() => { OpennessSession.CompileDevicesForTest(project); return Task.CompletedTask; });
            Check.Equal(0, first.Compiler.Calls);
            await Refuse("UnsupportedCapability", () => OpennessSession.CompileDevicesForTest(new()));
        }

        internal static async Task AmbiguityAndEnumeration()
        {
            var first = Device(); var second = Device(); var project = new Project { Devices = [first, second] };
            await Refuse("AmbiguousTarget", () => OpennessSession.CompileDevicesForTest(project));
            Check.Equal(0, first.ServiceReads); Check.Equal(0, second.ServiceReads);
            project.Devices = [first]; project.UngroupedDevicesGroup.Devices = [first];
            await Refuse("AmbiguousTarget", () => OpennessSession.CompileDevicesForTest(project));
            project.UngroupedDevicesGroup.Devices = [new Device { Name = "Alias", NativeId = first.NativeId, Compiler = first.Compiler }];
            await Refuse("AmbiguousTarget", () => OpennessSession.CompileDevicesForTest(project));
            project.UngroupedDevicesGroup.Devices = []; project.DeviceGroups = [new() { Name = "G" }, new() { Name = "G" }];
            await Refuse("AmbiguousTarget", () => OpennessSession.CompileDevicesForTest(project));
            IEnumerable<Device> Broken() { yield return first; throw new IOException("Synthetic enumeration failure"); }
            project.Devices = Broken(); project.DeviceGroups = [];
            await Check.ThrowsAsync<IOException>(() => { OpennessSession.CompileDevicesForTest(project); return Task.CompletedTask; });
            Check.Equal(0, first.ServiceReads); Check.Equal(0, first.Compiler!.Calls);
            project.Devices = [first]; first.Name = " ";
            await Refuse("TargetIdentityUnavailable", () => OpennessSession.CompileDevicesForTest(project));
            first.Name = "A"; project.Name = "";
            await Refuse("TargetIdentityUnavailable", () => OpennessSession.CompileDevicesForTest(project));
            project.Name = "Fixture"; project.DeviceGroups = [new() { Name = "" }];
            await Refuse("TargetIdentityUnavailable", () => OpennessSession.CompileDevicesForTest(project));
            Check.Equal(0, first.ServiceReads);
        }

        internal static async Task Bounds()
        {
            var device = Device(); var group = new DeviceUserGroup { Name = "G", Devices = [device] };
            for (int i = 0; i < 64; i++) group = new() { Name = "G", Groups = [group] };
            var project = new Project { DeviceGroups = [group] };
            OpennessSession.CompileDevicesForTest(project); Check.Equal(1, device.Compiler!.Calls);
            project.DeviceGroups = [new() { Name = "G", Groups = [group] }];
            await Refuse("TraversalLimit", () => OpennessSession.CompileDevicesForTest(project)); Check.Equal(1, device.Compiler.Calls);
            var first = Device("A");
            IEnumerable<Device> Many() { yield return first; for (int i = 0; i < 50000; i++) yield return Device(i.ToString()); }
            project = new() { Devices = Many() };
            await Refuse("TraversalLimit", () => OpennessSession.CompileDevicesForTest(project)); Check.Equal(0, first.ServiceReads);
        }

        internal static async Task ScopeDrift()
        {
            foreach (string defect in new[] { "add", "remove", "replace", "rename" })
            {
                var first = Device("A"); var second = Device("B"); var project = new Project { Devices = [first, second] };
                first.Compiler!.After = () => {
                    if (defect == "add") project.Devices = [first, second, Device("C")];
                    if (defect == "remove") project.Devices = [first];
                    if (defect == "replace") project.Devices = [first, Device("B")];
                    if (defect == "rename") second.Name = "C";
                };
                await Refuse("CompileScopeChanged", () => OpennessSession.CompileDevicesForTest(project));
                Check.Equal(1, first.Compiler.Calls); Check.Equal(1, second.Compiler!.Calls);
            }
            // Native enumerations may return distinct wrappers for the same engineering object.
            var original = Device("Stable");
            IEnumerable<Device> Wrappers() { yield return new Device { Name = original.Name, NativeId = original.NativeId, Compiler = original.Compiler }; }
            var stable = OpennessSession.CompileDevicesForTest(new() { Devices = Wrappers() });
            Check.Equal(true, (bool)stable["scopeComplete"]!); Check.Equal(1, original.Compiler!.Calls);
        }

        internal static async Task CompilerFailureNeverReplays()
        {
            var first = Device("A"); var second = Device("B"); var events = new List<string>();
            first.Compiler!.After = () => throw new IOException("Synthetic compiler outcome unknown");
            await Check.ThrowsAsync<IOException>(() => { OpennessSession.CompileDevicesForTest(new() { Devices = [first, second] }, (_, state) => events.Add(state)); return Task.CompletedTask; });
            Check.Equal(1, first.Compiler.Calls); Check.Equal(0, second.Compiler!.Calls); Check.Equal("dispatching", string.Join(",", events));
            first.Compiler.After = null; first.Compiler.Result.ErrorCount = 2; first.Compiler.Result.WarningCount = 3;
            var receipt = OpennessSession.CompileDevicesForTest(new() { Devices = [first, second] });
            Check.Equal(2, (int)receipt["errorCount"]!); Check.Equal(3, (int)receipt["warningCount"]!);
        }
    }
}

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        internal static JObject CompileDevicesForTest(Project project, Action<string, string>? journal = null) => CompileDeviceProject(project, journal);
        private static string Segment(string value) => Uri.EscapeDataString(value ?? "");

    }
}
// Synthetic API shapes exercise the linked production traversal and dispatch, not Siemens ABI compatibility.
namespace Siemens.Engineering.Compiler
{
    internal interface ICompilable { CompilerResult Compile(); }
    internal sealed class CompilerResult { internal int ErrorCount, WarningCount; internal string State = "Success"; internal IEnumerable<CompilerResultMessage> Messages = []; }
    internal sealed class CompilerResultMessage { internal string State = "Information", Description = "diagnostic", Path = "/fixture"; internal IEnumerable<CompilerResultMessage> Messages = []; }
    internal sealed class Compiler : ICompilable
    {
        internal int Calls; internal Action? After; internal CompilerResult Result = new();
        public CompilerResult Compile() { Calls++; After?.Invoke(); return Result; }
    }
}
namespace Siemens.Engineering.HW
{
    internal sealed class Device
    {
        internal IEnumerable<DeviceItem> DeviceItems = [];
        internal string Name = "Device"; internal Compiler.Compiler? Compiler; internal Func<object?>? Read; internal int ServiceReads;
        internal Guid NativeId = Guid.NewGuid();
        public override bool Equals(object? other) => other is Device device && NativeId == device.NativeId;
        public override int GetHashCode() => NativeId.GetHashCode();
        internal T? GetService<T>() where T : class { ServiceReads++; return (Read is null ? Compiler : Read()) as T; }
    }
    internal sealed class DeviceUserGroup
    {
        internal string Name = "Group"; internal IEnumerable<Device> Devices = []; internal IEnumerable<DeviceUserGroup> Groups = [];
    }
}
