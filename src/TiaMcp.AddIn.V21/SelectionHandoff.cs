using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;

// TIA sandboxes Add-Ins with the declared permission set; under Level 2 every partially trusted assembly is transparent and cannot use the declared UnmanagedCode.
[assembly: System.Security.SecurityRules(System.Security.SecurityRuleSet.Level1)]

namespace TiaMcp.AddIn
{
    /// <summary>Copies exact saved references for the selected objects and hands them to the Host's one-use challenge exchange. Writes no files.</summary>
    /// <remarks>assemblySha256 is SHA-256 of the deterministic build's module version ID: TIA's loader may load Add-Ins from bytes, without a file to hash.</remarks>
    internal static class SelectionHandoff
    {
        internal const int MaximumReferences = 64;
        private const string ProductId = "TiaMcp.AddIn.V21", Version = "0.1.0";
        private const long DiscoveryLimit = 64 << 10, ReviewLimit = 64 << 20, ResponseLimit = 2 << 20;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        internal static string Send(IEnumerable<IEngineeringObject> selected)
        {
            string artifacts = Environment.GetEnvironmentVariable("TIAMCP_ARTIFACTS");
            if (string.IsNullOrWhiteSpace(artifacts) || !Path.IsPathRooted(artifacts))
                throw new InvalidOperationException("Set TIAMCP_ARTIFACTS to the Host's absolute --artifacts directory, then restart TIA Portal.");
            var objects = selected.Take(MaximumReferences + 1).ToList();
            if (objects.Count == 0 || objects.Count > MaximumReferences) throw new InvalidOperationException("Select 1 to 64 objects.");
            var paths = objects.Select(PathOf).ToList();
            var discovery = Object(Read(Path.Combine(artifacts, "addin-handoffs", "receiver.json"), DiscoveryLimit));
            if (Text(discovery, "state") != "listening" || !(discovery.TryGetValue("authorityConfigured", out var enrolled) && enrolled is true))
                throw new InvalidOperationException("No enrolled TIA MCP receiver is listening. Start the Host with --addin-handoff --addin-enrollment <file>.");
            string projectPath = ProjectOf(objects[0]).Path.FullName;
            var (reviewId, model) = Review(artifacts, projectPath);
            var nodes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in ((IEnumerable)model["nodes"]).Cast<object>().Select(Object))
                if (Text(node, "origin") == "native" && Text(node, "objectPath") is string path && !nodes.ContainsKey(path)) nodes[path] = Text(node, "nodeId");
            var references = paths.Select(path => nodes.TryGetValue(path, out var nodeId) ? new Dictionary<string, object> {
                ["schemaVersion"] = "project-model.v1", ["snapshotId"] = model["snapshotId"], ["sessionGeneration"] = model["sessionGeneration"],
                ["projectPath"] = projectPath, ["nodeId"] = nodeId, ["objectPath"] = path }
                : throw new InvalidOperationException(path + " is not in saved review " + reviewId + ". Capture a new review of this project, then retry.")).ToList();
            var document = new Dictionary<string, object> {
                ["schemaVersion"] = "addin-selection.v1", ["reviewId"] = reviewId, ["selectionId"] = Guid.NewGuid().ToString("N"),
                ["selectedAtUtc"] = DateTime.UtcNow.ToString("O"), ["surface"] = "projectTree", ["projectHost"] = Environment.MachineName,
                ["producer"] = new Dictionary<string, object> { ["productId"] = ProductId, ["version"] = Version, ["tiaVersion"] = 21,
                    ["assemblySha256"] = Hash(typeof(SelectionHandoff).Module.ModuleVersionId.ToByteArray()), ["configurationSha256"] = Hash(Configuration()) },
                ["references"] = references };
            string requestId = Guid.NewGuid().ToString("N");
            var challenge = Exchange(discovery, new Dictionary<string, object> { ["protocolVersion"] = 1, ["action"] = "begin", ["requestId"] = requestId,
                ["reviewId"] = reviewId, ["selectionBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Serialize(document))) });
            var status = Exchange(discovery, new Dictionary<string, object> { ["protocolVersion"] = 1, ["action"] = "complete", ["requestId"] = requestId,
                ["challenge"] = Text(challenge, "challenge") });
            return "Record " + Text(Object(status["selection"]), "recordId") + ": " + references.Count + " object(s) bound to saved review " + reviewId + ". Open it with addin_selection_page.";
        }

        // Mirrors the worker's project-model object paths; anything else stays unsupported instead of being matched by name.
        internal static string PathOf(IEngineeringObject value)
        {
            switch (value)
            {
                case Project p: return "/projects/" + Segment(p.Name);
                case DeviceUserGroup g: return PathOf(g.Parent) + "/groups/" + Segment(g.Name);
                case DeviceSystemGroup g: return PathOf(g.Parent) + "/ungrouped";
                case Device d: return PathOf(d.Parent) + (d.Parent is DeviceSystemGroup ? "/" : "/devices/") + Segment(d.Name);
                case DeviceItem i: return PathOf(i.Parent) + "/items/" + Segment(i.Name);
                case SoftwareContainer c: return PathOf(c.Parent);
                case PlcSoftware s: return PathOf(s.Parent) + "/plc/" + Segment(s.Name);
                case PlcUnitSystemGroup g: return PathOf(g.Parent) + "/units";
                case PlcUnit u: return PathOf(u.Parent) + "/" + Segment(u.Name);
                case PlcSafetyUnit u: return PathOf(((IEngineeringInstance)u.Parent).Parent) + "/safetyUnits/" + Segment(u.Name);
                case PlcBlockSystemGroup g: return PathOf(g.Parent) + "/blocks";
                case PlcBlockUserGroup g: return PathOf(g.Parent) + "/groups/" + Segment(g.Name);
                case PlcBlock b: return Named(PathOf(b.Parent), b.Name, b.Namespace);
                case PlcTypeSystemGroup g: return PathOf(g.Parent) + "/types";
                case PlcTypeUserGroup g: return PathOf(g.Parent) + "/groups/" + Segment(g.Name);
                case PlcType t: return Named(PathOf(t.Parent), t.Name, t.Namespace);
                case PlcTagTableSystemGroup g: return PathOf(g.Parent) + "/tags";
                case PlcTagTableUserGroup g: return PathOf(g.Parent) + "/groups/" + Segment(g.Name);
                case PlcTagTable t: return PathOf(t.Parent) + "/" + Segment(t.Name);
                case PlcTag t: return PathOf(t.Parent) + "/tags/" + Segment(t.Name);
                default: throw new NotSupportedException((value == null ? "An empty" : value.GetType().Name) + " selection is not supported by this companion.");
            }
        }
        private static string Segment(string value) => Uri.EscapeDataString(value ?? "");
        private static string Named(string parent, string name, string nameSpace) => parent + (string.IsNullOrEmpty(nameSpace) ? "" : "/namespaces/" + Segment(nameSpace)) + "/" + Segment(name);
        private static Project ProjectOf(IEngineeringObject value)
        {
            while (!(value is Project)) value = (value as IEngineeringInstance)?.Parent ?? throw new NotSupportedException("The selection has no project.");
            return (Project)value;
        }

        // The newest saved review of this project copy; the Host revalidates it and refuses it unless captured in the live worker session.
        private static (string Id, IDictionary<string, object> Model) Review(string artifacts, string projectPath)
        {
            var directory = new DirectoryInfo(Path.Combine(artifacts, "project-reviews"));
            if (directory.Exists)
                foreach (var file in directory.GetFiles("*.json").Where(f => f.Name.Length == 69).OrderByDescending(f => f.LastWriteTimeUtc).Take(16))
                {
                    var model = Object(Object(Read(file.FullName, ReviewLimit))["model"]); var project = Object(model["project"]);
                    if (Text(project, "projectPath") == projectPath && Text(project, "host") == Environment.MachineName) return (Path.GetFileNameWithoutExtension(file.Name), model);
                }
            throw new InvalidOperationException("No saved review of " + projectPath + " was found. Capture one with the TIA MCP Host (project_snapshot_import) first.");
        }

        private static IDictionary<string, object> Exchange(IDictionary<string, object> discovery, Dictionary<string, object> request)
        {
            var host = Object(discovery["host"]);
            using (var pipe = new NamedPipeClientStream(".", Text(discovery, "pipeName"), PipeDirection.InOut))
            {
                pipe.Connect(5000);
                // Discovery is only a hint: send nothing unless the server is the exact Host process instance it names.
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint id) || id != Convert.ToUInt32(host["processId"]) || StartedAtUtc(id) != Text(host, "startedAtUtc"))
                    throw new InvalidOperationException("The pipe server is not the Host named in discovery.");
                byte[] frame = Encoding.UTF8.GetBytes(Json.Serialize(request) + "\n");
                pipe.Write(frame, 0, frame.Length); pipe.Flush();
                var reader = new BufferedStream(pipe); var response = new MemoryStream(); int next;
                while ((next = reader.ReadByte()) != '\n')
                {
                    if (next < 0 || response.Length >= ResponseLimit) throw new InvalidOperationException("The Host response was truncated or exceeded its bound.");
                    response.WriteByte((byte)next);
                }
                var value = Object(Json.DeserializeObject(new UTF8Encoding(false, true).GetString(response.ToArray())));
                if (!(value.TryGetValue("success", out var success) && success is true))
                    throw new InvalidOperationException("The TIA MCP Host refused the selection: " + Text(value, "error") + ". Its request is retained; nothing was replayed.");
                return Object(value["result"]);
            }
        }

        private static string StartedAtUtc(uint processId)
        {
            using (var process = OpenProcess(0x1000, false, processId))
                return !process.IsInvalid && GetProcessTimes(process, out long created, out _, out _, out _)
                    ? DateTime.FromFileTimeUtc(created).ToString("O") : throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        private static object Read(string path, long limit)
        {
            if (new FileInfo(path).Length > limit) throw new InvalidOperationException(path + " exceeds its size bound.");
            return Json.DeserializeObject(File.ReadAllText(path, new UTF8Encoding(false, true)));
        }
        private static byte[] Configuration()
        {
            using (var stream = typeof(SelectionHandoff).Assembly.GetManifestResourceStream("TiaMcp.AddIn.Publisher.xml"))
            using (var copy = new MemoryStream()) { stream.CopyTo(copy); return copy.ToArray(); }
        }
        private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2"))); }
        private static IDictionary<string, object> Object(object value) => value as IDictionary<string, object> ?? throw new InvalidDataException("Expected a JSON object.");
        private static string Text(IDictionary<string, object> value, string key) => value.TryGetValue(key, out var text) ? text as string : null;

        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(int access, bool inherit, uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    }
}
