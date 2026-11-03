using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    // No Siemens objects: reporting must still work after the native session dies.
    internal sealed class HmiPreservationCapture
    {
        private readonly string directory, projectPath, family, hmiPath, stage;
        private readonly Action<JObject> writeRecord;
        private JObject current;
        private int sequence;
        internal JObject Failure { get; private set; }
        internal string ProgressPath => Path.Combine(directory, "capture-progress.jsonl");

        internal HmiPreservationCapture(string directory, string projectPath, string family, string hmiPath, string stage,
            Action<JObject> writeRecord = null)
        {
            this.directory = directory; this.projectPath = projectPath; this.family = family; this.hmiPath = hmiPath; this.stage = stage;
            this.writeRecord = writeRecord ?? AppendRecord;
            Record("captureStarted");
        }

        internal void Capture(Action collect, Func<int> completedArtifactCount)
        {
            try { collect(); Record("captureCompleted"); }
            catch (Exception error)
            {
                Failure = Describe(error);
                Failure["stage"] = stage;
                Failure["completedArtifactCount"] = completedArtifactCount();
                Failure["activeOperation"] = current?.DeepClone();
                // Failure to report must never replace the original native error.
                try { Record("captureFailed", Failure); }
                catch (Exception reportingError) { Failure["progressWriteFailure"] = Describe(reportingError); }
            }
        }

        internal void Run(string operation, string identity, string artifactPath, Action action)
        {
            var parent = current;
            current = new JObject { ["operation"] = operation, ["objectPath"] = identity, ["artifactPath"] = artifactPath };
            Record("started"); // Flush to disk before entering any native call.
            action();
            Record("completed");
            current = parent;
        }

        internal JObject Manifest(string kind, JObject artifacts, JArray missing, JArray targets)
        {
            if (Failure != null)
                missing.Add("Native preservation capture failed; already-produced artifacts are retained and cannot authorize mutation.");
            var manifest = new JObject { ["schemaVersion"] = 1, ["policyVersion"] = HmiPreservationRules.PolicyVersion,
                ["family"] = family, ["hmiPath"] = hmiPath, ["projectPath"] = projectPath,
                ["targets"] = targets, ["scope"] = "all discovered project HMI targets; complete public export surface per family plus Unified typed object-model readback per node (target-N/typed-model.json), subject to explicit missing coverage",
                ["complete"] = missing.Count == 0 && Failure == null, ["artifacts"] = artifacts, ["missingCoverage"] = missing,
                ["contentHash"] = null, ["nativeFailure"] = Failure?.DeepClone(),
                ["status"] = Failure != null ? "failed" : missing.Count == 0 ? "complete" : "incomplete",
                ["progressPath"] = ProgressPath, ["capturedAtUtc"] = DateTime.UtcNow.ToString("O") };
            var hashes = artifacts.Properties().ToDictionary(p => p.Name,
                p => p.Value.Value<string>("contentSha256") ?? p.Value.Value<string>("sha256"), StringComparer.Ordinal);
            string scopePolicy = kind == "hmi_classic_screen_edit" ? HmiPreservationRules.ClassicScreenPolicy : null;
            if (scopePolicy != null)
            {
                manifest["scopePolicy"] = scopePolicy;
                manifest["scope"] = "Every regular screen of the selected Classic HMI only; explicit excluded coverage below.";
            }
            manifest["observedArtifactsHash"] = hashes.Count == 0 ? null : HmiPreservationRules.ScopeHash(family, hashes, true, scopePolicy);
            if (Failure == null && missing.Count == 0) manifest["contentHash"] = manifest["observedArtifactsHash"].DeepClone();
            else if (Failure == null && HmiEngineeringRules.ScopeAccepted(kind, manifest))
            {
                manifest["contentHash"] = manifest["observedArtifactsHash"].DeepClone();
                manifest["scopeAcceptance"] = scopePolicy ?? (kind + (family == "classic" ? ": only the standing Classic export note" : ": only the standing Unified tag-table export and typed-readback notes"));
            }
            return manifest;
        }

        internal string SaveManifest(JObject manifest)
        {
            string path = Path.Combine(directory, "manifest.json");
            try { WriteFlushed(path, manifest.ToString(Formatting.Indented), FileMode.CreateNew); }
            catch (Exception reportingError) when (Failure != null)
            {
                manifest["manifestWriteFailure"] = Describe(reportingError);
                throw new BridgeException("HmiPreservationUnavailable", "Preservation failed: " + Failure.Value<string>("message") +
                    " The diagnostic manifest could not be written; the original failure is retained in these details.", manifest);
            }
            return path;
        }

        private void Record(string state, JObject failure = null)
        {
            var record = new JObject { ["schemaVersion"] = 1, ["sequence"] = ++sequence, ["state"] = state,
                ["projectPath"] = projectPath, ["family"] = family, ["hmiPath"] = hmiPath, ["stage"] = stage,
                ["atUtc"] = DateTime.UtcNow.ToString("O"), ["activeOperation"] = current?.DeepClone() };
            if (failure != null) record["failure"] = failure.DeepClone();
            writeRecord(record);
        }

        private void AppendRecord(JObject record) => WriteFlushed(ProgressPath, record.ToString(Formatting.None) + "\n", FileMode.Append);

        private static void WriteFlushed(string path, string text, FileMode mode)
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            using (var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static JObject Describe(Exception error)
        {
            string detail = error.ToString();
            return new JObject { ["type"] = error.GetType().FullName, ["code"] = (error as BridgeException)?.Code,
                ["message"] = error.Message, ["exception"] = detail.Length > 16384 ? detail.Substring(0, 16384) : detail,
                ["details"] = (error as BridgeException)?.Details?.DeepClone() };
        }
    }
}
