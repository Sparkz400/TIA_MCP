#nullable disable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>Siemens-independent write-ahead records. No method replays native work.</summary>
    internal static class LibraryJournalStore
    {
        internal static void Write(string path, JObject record)
        {
            var bytes = Encoding.UTF8.GetBytes(record.ToString(Formatting.Indented));
            if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Library journal exceeds 8 MiB.");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            // Never delete the only previous record before the replacement is durable.
            if (File.Exists(path)) TiaMcp.Core.JournalFileReplacement.Replace(temporary, path); else File.Move(temporary, path);
        }

        internal static JObject Read(string path, string expectedId)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Library journal is absent or oversized.");
            // Keep native timestamps as written; date parsing reserializes them and breaks the preview hash.
            JObject result;
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None, MaxDepth = 128 })
                result = JObject.Load(reader);
            if (result.Value<string>("changeId") != expectedId || result["preview"] == null || Hash(result["preview"].ToString(Formatting.None)) != result.Value<string>("previewHash"))
                throw new InvalidDataException("Library journal identity or preview hash is invalid.");
            return result;
        }

        internal static string Hash(string text)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }

        internal static void Claim(string directory, string key, string operationId)
        {
            string path = Path.Combine(directory, "library-key-" + Hash(key) + ".claim");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { var bytes = Encoding.UTF8.GetBytes(operationId); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }

        internal static string ReconciledState(JObject journal)
        {
            var attempts = journal["recoveryAttempts"] as JArray;
            if (attempts != null && attempts.Count != 0)
            {
                string state = attempts[attempts.Count - 1].Value<string>("state");
                return state == "completed" ? "restored_to_new_locations" : "recovery_outcome_unknown";
            }
            string original = journal.Value<string>("state");
            return original == "prepared" || original == "applied" ? original : "outcome_unknown";
        }
    }
}
