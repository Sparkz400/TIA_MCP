using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // A matching copy of the reviewed installation registry is required at
        // the owning worker as well. Neither IPC context nor artifact uploads
        // supply this path. Host validates the full executable package and exact
        // normalized content key; the worker independently pins its image,
        // exact selector and original evidence bytes before opening a lease.
        private JObject TranslationInstalledEntry(string projectPath)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "translation-admission");
            string registry = Path.Combine(root, "registry.json");
            if (!File.Exists(registry)) throw new BridgeException("TranslationImportProfileUnqualified", "No reviewed translation registry is installed at the owning worker.");
            TranslationAdmissionNoLinks(registry);
            if (new FileInfo(registry).Length > 1024 * 1024) throw new BridgeException("TranslationAdmissionInvalid", "Installed registry exceeds its bound.");
            JObject document;
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(registry))) { DateParseHandling = DateParseHandling.None, MaxDepth = 32 })
                document = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            TranslationAdmissionFields(document, "version", "entries");
            var entries = document["entries"] as JArray;
            if (document.Value<int>("version") != 1 || entries == null || entries.Count == 0 || entries.Count > 32 ||
                entries.Any(value => !(value is JObject)) || entries.Select(value => value.Value<string>("id")).Distinct(StringComparer.Ordinal).Count() != entries.Count)
                throw new BridgeException("TranslationAdmissionInvalid", "Bounded unique installed admission entries required.");
            foreach (JObject entry in entries)
            {
                TranslationAdmissionFields(entry, "id", "projectPath", "plcPath", "profile", "evidence");
                var profile = entry["profile"] as JObject;
                TranslationAdmissionFields(profile, "id", "key", "sourcePackageSha256", "proofs");
                var key = profile["key"] as JObject;
                TranslationAdmissionFields(key, "contract", "binaries", "projectResourceKey", "tiaVersion", "hardwareIdentity", "nativeIdentity",
                    "profileSha256", "scope", "baselineContentSha256", "candidateContentSha256", "rows", "peers");
                var binaries = key["binaries"] as JObject;
                TranslationAdmissionFields(binaries, "hostPackageSha256", "translationAssemblySha256", "workerSha256", "siemensApiSha256");
                if (binaries.Value<string>("workerSha256") != ChangeFileHash(typeof(OpennessSession).Assembly.Location) ||
                    key.Value<string>("contract") != "translation.whole-project-de-DE-en-GB-main-ob1.exact.v1" || key.Value<int>("tiaVersion") != 21 ||
                    profile.Value<string>("id") != entry.Value<string>("id"))
                    throw new BridgeException("TranslationAdmissionMismatch", "Installed worker/profile identity changed.");
                var proofs = profile["proofs"] as JArray; var evidence = entry["evidence"] as JArray;
                if (proofs == null || proofs.Count != 20 || evidence == null || evidence.Count == 0 || evidence.Count > 128)
                    throw new BridgeException("TranslationAdmissionIncomplete", "Complete reviewed native and portable proof ledger required.");
                foreach (JObject proof in proofs) TranslationAdmissionFields(proof, "case", "keySha256", "sourcePackageSha256", "evidenceSha256", "mode");
                foreach (JObject file in evidence)
                {
                    TranslationAdmissionFields(file, "path", "sha256");
                    string hash = file.Value<string>("sha256"), relative = file.Value<string>("path");
                    if (hash == null || hash.Length != 64 || hash.Any(c => !(c >= 'a' && c <= 'f' || c >= '0' && c <= '9')) || relative != "evidence/" + hash + ".json" ||
                        !proofs.Any(proof => proof.Value<string>("evidenceSha256") == hash))
                        throw new BridgeException("TranslationAdmissionInvalid", "Exact installed evidence path/hash required.");
                    string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); TranslationAdmissionNoLinks(path);
                    if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024 || ChangeFileHash(path) != hash)
                        throw new BridgeException("TranslationAdmissionMismatch", "Installed native evidence changed.");
                }
                if (evidence.Select(file => file.Value<string>("sha256")).Distinct(StringComparer.Ordinal).Count() != evidence.Count ||
                    !evidence.Select(file => file.Value<string>("sha256")).OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(
                        proofs.Select(proof => proof.Value<string>("evidenceSha256")).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)))
                    throw new BridgeException("TranslationAdmissionIncomplete", "Every proof requires its retained evidence file.");
                if (projectPath == null || entry.Value<string>("projectPath") == projectPath) return entry;
            }
            throw new BridgeException("TranslationImportProfileUnqualified", "Exact project has no installed translation admission.");
        }
        private static void TranslationAdmissionFields(JObject value, params string[] names)
        {
            if (value == null || !value.Properties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(names.OrderBy(name => name, StringComparer.Ordinal)))
                throw new BridgeException("TranslationAdmissionInvalid", "Unknown or missing installed admission fields.");
        }
        private static void TranslationAdmissionNoLinks(string path)
        {
            for (string cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new BridgeException("TranslationAdmissionInvalid", "Linked installation/evidence paths are refused.");
        }
    }
}
