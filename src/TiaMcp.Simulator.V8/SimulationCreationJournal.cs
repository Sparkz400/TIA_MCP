using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaMcp.Simulation;

namespace TiaMcp.Simulator.V8
{
    // Receipt integrity is bound to the Windows user. A registration intent is
    // never ownership proof: adoption additionally needs a returned native ID,
    // the exact storage marker, and the original worker's observed exit.
    internal static class SimulationCreationJournal
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TiaMcp.Simulator.Creation.v1");

        internal static string ValidateId(string value)
        {
            Guid id;
            if (value == null || value.Length != 32 || !Guid.TryParseExact(value, "N", out id) || id == Guid.Empty)
                throw new SimulatorException("InvalidRequest", "creationId must be a nonzero 32-character GUID without separators.");
            return id.ToString("N");
        }

        internal static string PathFor(string root, string id)
        {
            SimulationTicket.VerifyPlainDirectory(root);
            return Path.Combine(Path.GetFullPath(root), "creation-" + ValidateId(id) + ".json");
        }

        internal static JObject Begin(string root, string id, string name, string storage, string cpu)
        {
            using (var process = Process.GetCurrentProcess())
            {
                var receipt = new JObject { ["schemaVersion"] = 1, ["creationId"] = ValidateId(id),
                    ["instanceName"] = name, ["storagePath"] = Path.GetFullPath(storage), ["cpuType"] = cpu,
                    ["workerPid"] = process.Id, ["workerStartedAtUtc"] = process.StartTime.ToUniversalTime().ToString("O"),
                    ["stage"] = "registrationIntent", ["createdAtUtc"] = DateTime.UtcNow.ToString("O") };
                Save(root, receipt, true);
                return receipt;
            }
        }

        internal static void Save(string root, JObject receipt, bool create = false)
        {
            string path = PathFor(root, receipt.Value<string>("creationId"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            receipt["updatedAtUtc"] = DateTime.UtcNow.ToString("O");
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(receipt.ToString(Formatting.None)), Entropy, DataProtectionScope.CurrentUser);
            byte[] envelope = Encoding.UTF8.GetBytes(new JObject { ["schemaVersion"] = 1, ["protectedReceipt"] = Convert.ToBase64String(data) }.ToString(Formatting.None));
            if (create)
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete))
                { stream.Write(envelope, 0, envelope.Length); stream.Flush(true); }
                return;
            }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete))
                { stream.Write(envelope, 0, envelope.Length); stream.Flush(true); }
                TiaMcp.Core.JournalFileReplacement.Replace(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static JObject Read(string root, string id)
        {
            string path = PathFor(root, id);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 65536 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new SimulatorException("SimulationCreationUnavailable", "The exact creation receipt is unavailable or invalid.");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
            {
                var envelope = JObject.Parse(reader.ReadToEnd());
                string decoded = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(envelope.Value<string>("protectedReceipt")), Entropy, DataProtectionScope.CurrentUser));
                JObject receipt;
                using (var json = new JsonTextReader(new StringReader(decoded)) { DateParseHandling = DateParseHandling.None, MaxDepth = 16 })
                    receipt = JObject.Load(json);
                if (receipt.Value<int?>("schemaVersion") != 1 || receipt.Value<string>("creationId") != ValidateId(id))
                    throw new SimulatorException("SimulationTargetMismatch", "Creation receipt identity mismatch.");
                return receipt;
            }
        }

        internal static bool WorkerExited(JObject receipt)
        {
            try
            {
                using (var process = Process.GetProcessById(receipt.Value<int>("workerPid")))
                    return process.HasExited || process.StartTime.ToUniversalTime().ToString("O") != receipt.Value<string>("workerStartedAtUtc");
            }
            catch (ArgumentException) { return true; }
            // Access denied and all other failures leave ownership unresolved.
        }

        internal static string MarkerHash(string storage)
        {
            SimulationTicket.VerifyPlainDirectory(storage);
            string path = Path.Combine(storage, SimulationTicket.MarkerName);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 16384 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new SimulatorException("SimulationTargetMismatch", "Exact instance ownership marker is unavailable.");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static void VerifyAdoption(JObject receipt, int id, string name, string storage, string cpu, bool workerExited)
        {
            if (!workerExited || receipt.Value<string>("stage") != "ready" || receipt.Value<int?>("instanceId") != id
                || receipt.Value<string>("instanceName") != name || receipt.Value<string>("cpuType") != cpu
                || !string.Equals(receipt.Value<string>("storagePath"), Path.GetFullPath(storage), StringComparison.OrdinalIgnoreCase)
                || receipt.Value<string>("markerSha256") != MarkerHash(storage))
                throw new SimulatorException("SimulationOwnershipUnverified", "Adoption requires a completed signed creation receipt, exact native identity/storage/CPU and marker, and the original worker's verified exit. Intent or matching names alone never authorize adoption.");
        }
    }
}
