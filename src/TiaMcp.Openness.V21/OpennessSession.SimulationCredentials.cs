using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using TiaMcp.Simulation;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private sealed class SimulationCredential
        {
            internal string ProtectedPath;
            internal JObject Evidence;
        }

        private static SimulationCredential ValidateSimulationCredential(JObject args, Project project, DeviceItem cpu)
        {
            string metadataPath = args.Value<string>("credentialMetadataPath");
            if (string.IsNullOrWhiteSpace(metadataPath)) return null;
            var metadata = ReadSimulationCredentialJson(metadataPath);
            string id = metadata.Value<string>("credentialId");
            Guid parsedId;
            if (!Guid.TryParseExact(id, "N", out parsedId) || Path.GetFileName(metadataPath) != id + ".json" ||
                metadata.Value<string>("format") != "Windows DPAPI CurrentUser" || metadata.Value<string>("state") != "configured" ||
                metadata.Value<string>("purpose") != "Generated confidential configuration protection for a new isolated scratch CPU")
                throw new BridgeException("InvalidSimulationCredential", "Only a configured adapter-generated scratch CPU master-secret credential can be used for this callback.");
            if (metadata.Value<string>("cpuName") != cpu.Name || metadata.Value<string>("typeIdentifier") != cpu.TypeIdentifier)
                throw new BridgeException("SimulationCredentialTargetMismatch", "Credential CPU name/type does not match the selected engineering CPU.");
            string originalProject = metadata.Value<string>("projectPath");
            string currentProject = project.Path.FullName;
            if (!project.Name.StartsWith("TiaMcpScratch_", StringComparison.Ordinal) ||
                !string.Equals(Path.GetFileNameWithoutExtension(originalProject), project.Name, StringComparison.Ordinal))
                throw new BridgeException("SimulationCredentialTargetMismatch", "The generated credential's original scratch project does not match the selected project.");
            var lineageEvidence = new JArray();
            if (!SameSimulationPath(originalProject, currentProject))
            {
                lineageEvidence = VerifySimulationCredentialLineage(args, originalProject, currentProject);
            }
            string directory = Path.GetDirectoryName(Path.GetFullPath(metadataPath));
            if (Path.GetFileName(directory) != "scratch-credentials") throw new BridgeException("InvalidSimulationCredential", "Expected the adapter's scratch-credentials directory.");
            string protectedPath = Path.Combine(directory, id + ".dpapi");
            if (!SameSimulationPath(protectedPath, metadata.Value<string>("protectedPath")))
                throw new BridgeException("InvalidSimulationCredential", "Credential metadata cannot redirect to another encrypted file.");
            SimulationCredentialFile(protectedPath, 65536);
            return new SimulationCredential { ProtectedPath = protectedPath, Evidence = new JObject {
                ["credentialMetadataPath"] = Path.GetFullPath(metadataPath), ["credentialId"] = id,
                ["cpuName"] = cpu.Name, ["typeIdentifier"] = cpu.TypeIdentifier,
                ["projectMatch"] = lineageEvidence.Count == 0 ? "exact-path" : "verified-recovery-journal-chain-and-archive-hashes",
                ["recoveryLineage"] = lineageEvidence, ["secretReturned"] = false } };
        }

        private static JArray VerifySimulationCredentialLineage(JObject args, string originalProject, string currentProject)
        {
            string single = args.Value<string>("credentialLineageJournalPath");
            JToken supplied = args["credentialLineageJournalPaths"];
            if (supplied?.Type == JTokenType.Null) supplied = null;
            if (!string.IsNullOrWhiteSpace(single) && supplied != null)
                throw new BridgeException("InvalidSimulationCredential", "Supply either the singular lineage journal or credentialLineageJournalPaths, not both.");
            var paths = supplied as JArray ?? (!string.IsNullOrWhiteSpace(single) ? new JArray(single) : null);
            if (paths == null || paths.Count < 1 || paths.Count > 16 || paths.Any(path => path.Type != JTokenType.String))
                throw new BridgeException("SimulationCredentialLineageRequired", "Supply credentialLineageJournalPaths as 1–16 explicit recovery journal paths linking the credential's original scratch project to the selected project.");
            var hops = new List<JObject>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths.Values<string>())
            {
                if (!unique.Add(Path.GetFullPath(path))) throw new BridgeException("SimulationCredentialLineageMismatch", "Duplicate recovery journal path.");
                var journal = ReadSimulationCredentialJson(path, 4 * 1024 * 1024);
                string from = journal.Value<string>("projectPath"); string to = journal["recoveredProject"]?.Value<string>("path");
                if (journal.Value<string>("state") != "restored" || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || SameSimulationPath(from, to))
                    throw new BridgeException("SimulationCredentialLineageMismatch", "Every lineage journal must record a completed recovery to a different project path.");
                string archive = journal.Value<string>("recoveryArchive");
                var file = SimulationCredentialFile(archive, 2L * 1024 * 1024 * 1024);
                string hash;
                using (var input = file.OpenRead()) using (var sha = SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(hash, journal.Value<string>("recoveryArchiveSha256"), StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("SimulationCredentialLineageMismatch", "Recovery archive contents no longer match a recorded lineage hash.");
                hops.Add(new JObject { ["journalPath"] = Path.GetFullPath(path), ["fromProjectPath"] = from,
                    ["toProjectPath"] = to, ["archiveSha256"] = hash });
            }
            var evidence = new JArray(); string current = originalProject;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(current) };
            while (!SameSimulationPath(current, currentProject))
            {
                var next = hops.Where(hop => SameSimulationPath(hop.Value<string>("fromProjectPath"), current)).ToArray();
                if (next.Length != 1) throw new BridgeException("SimulationCredentialLineageMismatch", "Recovery proof has a gap or branch before reaching the selected project.");
                var hop = next[0]; hops.Remove(hop); current = hop.Value<string>("toProjectPath");
                if (!visited.Add(Path.GetFullPath(current))) throw new BridgeException("SimulationCredentialLineageMismatch", "Recovery proof contains a cycle.");
                evidence.Add(hop);
            }
            if (hops.Count != 0) throw new BridgeException("SimulationCredentialLineageMismatch", "Recovery proof contains unused journals unrelated to the selected lineage.");
            return evidence;
        }

        private static SecureString OpenSimulationCredential(SimulationCredential credential)
        {
            byte[] encrypted = null; byte[] clear = null; char[] characters = null; SecureString password = null;
            try
            {
                var file = SimulationCredentialFile(credential.ProtectedPath, 65536);
                encrypted = File.ReadAllBytes(file.FullName);
                clear = ProtectedData.Unprotect(encrypted, ScratchCredentialEntropy, DataProtectionScope.CurrentUser);
                if (clear.Length == 0 || clear.Length > 4096) throw new BridgeException("InvalidSimulationCredential", "Invalid generated credential length.");
                characters = new UTF8Encoding(false, true).GetChars(clear);
                password = new SecureString();
                foreach (char character in characters) password.AppendChar(character);
                password.MakeReadOnly();
                return password;
            }
            catch
            {
                password?.Dispose();
                throw new BridgeException("SimulationCredentialUnavailable", "The generated credential could not be opened under this Windows user. No password was returned or logged.");
            }
            finally
            {
                if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
                if (clear != null) Array.Clear(clear, 0, clear.Length);
                if (characters != null) Array.Clear(characters, 0, characters.Length);
            }
        }

        private static JObject ReadSimulationCredentialJson(string path, long maximum = 16384)
        {
            var file = SimulationCredentialFile(path, maximum);
            return JObject.Parse(File.ReadAllText(file.FullName));
        }

        private static FileInfo SimulationCredentialFile(string path, long maximum)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new BridgeException("InvalidSimulationCredential", "A local credential or lineage file path is required.");
            SimulationTicket.VerifyPlainDirectory(Path.GetDirectoryName(path));
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length > maximum || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new BridgeException("InvalidSimulationCredential", "The credential or lineage file is missing, oversized, or a reparse point.");
            return file;
        }

        private static bool SameSimulationPath(string first, string second) => !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
            string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    }
}
