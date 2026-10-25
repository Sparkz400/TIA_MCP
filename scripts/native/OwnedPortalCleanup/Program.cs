using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

// Qualification-only termination of the exact owned orphan named by an actual fault marker.
// No attachment, save, import, online call, process-tree kill or name-based termination.
internal static class Program
{
    private static readonly string Api = InstalledApiDirectory();

    // Siemens registers the installed V21 API location; C:\Program Files is only the setup default.
    private static string InstalledApiDirectory()
    {
        using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
        using (var key = hklm.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48"))
        {
            var basePath = key?.GetValue("Siemens.Engineering.Base") as string;
            return string.IsNullOrWhiteSpace(basePath) ? @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48" : Path.GetDirectoryName(basePath);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [STAThread] static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var name = new AssemblyName(e.Name).Name;
            var path = Path.Combine(Api, name + ".dll");
            return name.StartsWith("Siemens.Engineering.", StringComparison.Ordinal) && File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Run(args); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static string Hash(string path) { using (var s = File.OpenRead(path)) using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
    static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Run(string[] args)
    {
        Need(args.Length == 1, "One immutable interrupted run root required.");
        string root = Path.GetFullPath(args[0]), output = Path.Combine(root, "coordinator-native-close.json");
        Need(!File.Exists(output) && !File.Exists(Path.Combine(root, "coordinator-native-close-intent.json")) && !File.Exists(Path.Combine(root, "host-01-close.json")), "Cleanup was already attempted or Host close completed; no automatic replay.");
        var admission = JObject.Parse(File.ReadAllText(Path.Combine(root, "admission.json")));
        var marker = JObject.Parse(File.ReadAllText(Path.Combine(root, "interruption-marker.json")));
        var start = JObject.Parse(File.ReadAllText(Path.Combine(root, "host-01-start.json")));
        var monitored = JArray.Parse(File.ReadAllText(Path.Combine(root, "host-01-processes.json")));
        var helper = (JObject)admission["nativeCleanupHelper"];
        Need(helper != null && helper.Value<string>("method") == "TerminateProcess(exactOwnedHandle)" && helper.Value<string>("sha256") == Hash(Assembly.GetExecutingAssembly().Location) && string.Equals(Path.GetFullPath(helper.Value<string>("path")), Assembly.GetExecutingAssembly().Location, StringComparison.OrdinalIgnoreCase), "Admitted cleanup helper differs.");
        var sources = helper["sources"] as JArray;
        Need(sources != null && sources.Count == 2, "Exactly two frozen helper sources required.");
        foreach (JObject source in sources) Need(Hash(source.Value<string>("path")) == source.Value<string>("sha256"), "Cleanup helper source changed.");
        Need(new[] { "checkpoint.beforeArchive", "checkpoint.afterArchive", "import.before", "import.after", "save.before", "save.after", "retrieve.before", "retrieve.after" }.Contains(marker.Value<string>("point")), "Only actual native worker faults allow coordinator cleanup.");
        Need(admission.Value<bool>("actualMcpHost") && admission.Value<bool>("nativeSequential") && admission.Value<string>("mode") == marker.Value<string>("point"), "Actual sequential fault admission mismatch.");
        Need(Hash(admission.Value<string>("worker")) == admission.Value<string>("workerSha256") && marker.Value<string>("workerSha256") == admission.Value<string>("workerSha256"), "Fault worker hash mismatch.");
        int pid = marker.Value<int>("processId"), workerPid = marker.Value<int>("workerProcessId");
        var worker = monitored.OfType<JObject>().Single(p => p.Value<int>("pid") == workerPid);
        var native = monitored.OfType<JObject>().Single(p => p.Value<int>("pid") == pid);
        Need(worker.Value<int>("parentPid") == start["identity"].Value<int>("pid") && native.Value<int>("parentPid") == workerPid && native.Value<string>("name") == "Siemens.Automation.Portal.exe", "Exact original owned ancestry required.");
        using (var process = Process.GetProcessById(pid))
        {
            var handle = process.Handle; // retained across identity checks, request and exit proof
            double born = (process.StartTime.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            Need(Math.Abs(born - native.Value<double>("created")) < 0.00001 && string.Equals(process.MainModule.FileName, native.Value<string>("exe"), StringComparison.OrdinalIgnoreCase), "PID reuse or executable mismatch.");
            Need(native["command"] is JArray command && command.Any(c => c.Value<string>() == "-bootstrapper=Siemens.Automation.Opns.Loader.Impl:Siemens.Automation.Openness.Loader.BootStrapper"), "Only a captured owned headless process may be terminated.");
            string path = marker.Value<string>("projectPath");
            Need(path == null ? marker.Value<string>("point") == "retrieve.before" : Path.GetFullPath(path).StartsWith(Path.GetFullPath(admission.Value<string>("root")).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase), "Marker is not an empty retrieval or this run's exact scratch project.");
            Need(!process.HasExited, "Process exited before explicit close; do not reopen PID.");
            var receipt = new JObject { ["identity"] = native.DeepClone(), ["markerSha256"] = Hash(Path.Combine(root, "interruption-marker.json")), ["helperSha256"] = Hash(Assembly.GetExecutingAssembly().Location), ["projectPath"] = path,
                ["method"] = "TerminateProcess(exactOwnedHandle)", ["projectIdentityBasis"] = "Durable interruption marker from the exact admitted worker, not a new native attachment", ["explicitNativeTermination"] = true, ["automaticReplay"] = false, ["requestedAtUtc"] = DateTime.UtcNow, ["sameOsHandleRetained"] = true };
            using (var stream = new FileStream(Path.Combine(root, "coordinator-native-close-intent.json"), FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream)) { writer.Write(receipt.ToString()); writer.Flush(); stream.Flush(true); }
            Need(TerminateProcess(handle, 75), "Exact owned orphan termination failed: " + Marshal.GetLastWin32Error());
            receipt["completed"] = process.WaitForExit(45000);
            if (receipt.Value<bool>("completed")) receipt["exitCode"] = process.ExitCode;
            receipt["closedAtUtc"] = DateTime.UtcNow;
            using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream)) { writer.Write(receipt.ToString()); writer.Flush(); stream.Flush(true); }
            Need(receipt.Value<bool>("completed"), "Exact native process did not exit.");
            Console.WriteLine(receipt);
        }
    }
}
