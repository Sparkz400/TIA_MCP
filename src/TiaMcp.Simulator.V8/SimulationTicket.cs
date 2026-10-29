using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Simulation
{
    // Shared by the simulator and engineering workers. User-scoped DPAPI binds an
    // opaque, short-lived ticket to an adapter-created instance and fresh storage.
    internal static class SimulationTicket
    {
        internal const string MarkerName = ".tiamcp-instance.json";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TiaMcp.Simulation.V8.MemoryCard.v1");

        internal static JObject Create(int id, string name, string storagePath, string communicationInterface = "Softbus")
        {
            var marker = new JObject { ["schemaVersion"] = 1, ["instanceId"] = id, ["instanceName"] = name,
                ["storagePath"] = Path.GetFullPath(storagePath), ["nonce"] = Guid.NewGuid().ToString("N"),
                ["issuedAtUtc"] = DateTime.UtcNow.ToString("O"), ["expiresAtUtc"] = DateTime.UtcNow.AddMinutes(15).ToString("O") };
            // Softbus markers stay byte-compatible; only a TCP/IP creation names its interface for the engineering worker.
            if (communicationInterface != "Softbus") marker["communicationInterface"] = communicationInterface;
            using (var output = new StreamWriter(new FileStream(Path.Combine(storagePath, MarkerName), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
                output.Write(marker.ToString(Newtonsoft.Json.Formatting.None));
            return marker;
        }

        internal static string Interface(JObject marker) => marker.Value<string>("communicationInterface") ?? "Softbus";

        internal static string Encode(JObject marker) => Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(marker.ToString(Newtonsoft.Json.Formatting.None)), Entropy, DataProtectionScope.CurrentUser));

        internal static JObject Decode(string ticket)
        {
            if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 16384) throw new InvalidOperationException("A valid simulator download ticket is required.");
            var marker = JObject.Parse(Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ticket), Entropy, DataProtectionScope.CurrentUser)));
            if (marker.Value<int?>("schemaVersion") != 1 || marker.Value<DateTime>("expiresAtUtc").ToUniversalTime() < DateTime.UtcNow)
                throw new InvalidOperationException("The simulation ticket is invalid or expired; create a fresh owned instance.");
            string path = marker.Value<string>("storagePath");
            VerifyPlainDirectory(path);
            var file = new FileInfo(Path.Combine(path, MarkerName));
            if (!file.Exists || file.Length > 16384 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The simulator ownership marker is unavailable or invalid.");
            var disk = JObject.Parse(File.ReadAllText(file.FullName));
            if (!JToken.DeepEquals(marker, disk)) throw new InvalidOperationException("The instance ownership marker does not match the ticket.");
            return marker;
        }

        internal static void VerifyPlainDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' ||
                (path[2] != Path.DirectorySeparatorChar && path[2] != Path.AltDirectorySeparatorChar))
                throw new InvalidOperationException("Simulation storage must be an absolute local directory.");
            for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory != null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Simulation storage cannot traverse a reparse point.");
        }
    }
}
