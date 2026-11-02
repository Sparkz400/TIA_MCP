using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        /// <summary>Location-independent project fingerprint plus the project library revision, which the engineering fingerprint omits.</summary>
        private string ReconcileProjectFingerprint(EngineeringChange change, Project project) =>
            ChangeHash(EngineeringFingerprint(change, project, true) + "|" + LibraryRevision(new ManagedLibrary { Native = project.ProjectLibrary, Project = project }));

        // Read-only evidence for releasing a failed provisioning admission. Runs in a fresh
        // empty owned Portal; it never powers, connects, provisions or saves anything.
        private JObject SimulationProvisionReconcileVerify(JObject args)
        {
            EnsureAttached();
            if (!_ownsPortal || _portal.Projects.Count != 0)
                throw new BridgeException("ProjectIsolationRequired", "Provisioning reconciliation requires an empty worker-owned TIA instance started with tia_start.");
            string reportPath = ChangeText(args, "reportPath", 4096), projectPath = ChangeText(args, "projectPath", 4096);
            string retrieveDirectory = ChangeText(args, "retrieveDirectory", 4096), scopeSha256 = ChangeText(args, "scopeSha256", 64);
            string observerPath = args.Value<string>("observerProjectPath");
            foreach (string path in new[] { reportPath, projectPath, retrieveDirectory, observerPath ?? reportPath })
                if (!ProjectPathIsAbsolute(path)) throw new BridgeException("InvalidRequest", "Exact absolute reconciliation paths are required.");
            var report = ReadSimulationCredentialJson(reportPath, 16 * 1024 * 1024);
            var baseline = report["recoveryBaseline"] as JObject ?? throw new BridgeException("ReconciliationIncomplete", "The provisioning report has no recovery baseline.");
            if (baseline["nativeScope"]?.Value<string>("scopeSha256") != scopeSha256 || !SameSimulationPath(baseline["project"]?.Value<string>("path"), projectPath))
                throw new BridgeException("IdentityMismatch", "The provisioning report does not belong to this admission and project.");
            string storage = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            var result = Envelope();
            result["scopeSha256"] = scopeSha256;
            result["projectPath"] = projectPath;
            result["nativeMutationInvoked"] = false;
            result["opaqueStorageContentsRead"] = false;

            var original = baseline["originalCardManifest"] as JArray;
            var manifest = SimulationCardFiles(Path.Combine(storage, "SIMATIC_MC"));
            result["cardManifest"] = manifest;
            result["cardManifestMatches"] = original != null && JToken.DeepEquals(manifest, original) &&
                SimulationProvisionManifestHash(manifest) == report.Value<string>("originalProgramRevision");

            // Storage outside the card, from metadata only, against the inventory taken before this attempt wrote anything.
            string attempt = Path.GetFileNameWithoutExtension(reportPath).Substring("simulation-provision-".Length);
            var storageDifferences = baseline["storageMetadata"] is JArray inventory
                ? AdmissionReconcileRules.ProvisionStorageDifferences(inventory, SimulationProvisionTreeMetadata(storage), attempt)
                : new System.Collections.Generic.List<string> { "The recovery baseline has no storage inventory." };
            result["storageDifferences"] = new JArray(storageDifferences);
            result["storageMatchesBaseline"] = storageDifferences.Count == 0;
            // The attempt's own write-ahead stream bounds what the runtime protection store can hold.
            string stream = report.Value<string>("checkpointPath");
            string credentialRefusal = stream == null || !SameSimulationPath(Path.GetDirectoryName(stream), storage) || !File.Exists(stream)
                ? "The provisioning checkpoint stream is missing."
                : new FileInfo(stream).Length > 16 * 1024 * 1024 ? "The provisioning checkpoint stream is too large to inspect."
                : AdmissionReconcileRules.ProvisionCredentialRefusal(File.ReadAllLines(stream).Where(line => line.Length > 0));
            if (credentialRefusal == null && (report["credentialSubmissionArmed"]?.Type != JTokenType.Boolean || report.Value<bool>("credentialSubmissionArmed")))
                credentialRefusal = "The provisioning report does not record the credential submission as never armed.";
            result["credentialRefusal"] = credentialRefusal;
            result["credentialNeverArmed"] = credentialRefusal == null;

            var archive = baseline["projectArchive"] as JObject;
            string archivePath = archive?.Value<string>("path");
            bool archiveMatches = archive != null && SameSimulationPath(Path.GetDirectoryName(archivePath), storage) && File.Exists(archivePath) &&
                string.Equals(ChangeFileHash(archivePath), archive.Value<string>("sha256"), StringComparison.OrdinalIgnoreCase);
            result["archiveHashMatches"] = archiveMatches;
            result["projectFingerprintMatches"] = false;
            result["projectSavedBeforeArchive"] = false;
            if (archiveMatches)
            {
                if (File.Exists(retrieveDirectory) || Directory.Exists(retrieveDirectory))
                    throw new BridgeException("MutationScopeStale", "The reconciliation retrieval destination already exists.");
                if (retrieveDirectory.Length > 80) throw new BridgeException("RecoveryPathTooLong", "Use a shorter artifact directory for TIA's project path limit.");
                Directory.CreateDirectory(retrieveDirectory);
                var archived = new EngineeringChange { Kind = "simulation_provision_reconcile" };
                var copy = _portal.Projects.Retrieve(new FileInfo(archivePath), new DirectoryInfo(retrieveDirectory));
                try { result["archiveFingerprint"] = ReconcileProjectFingerprint(archived, copy); result["archiveCopyPath"] = copy.Path.FullName; }
                finally { copy.Close(); }
                var observed = new EngineeringChange { Kind = "simulation_provision_reconcile" };
                var current = _portal.Projects.Open(new FileInfo(projectPath));
                try
                {
                    result["currentFingerprint"] = ReconcileProjectFingerprint(observed, current);
                    result["currentProjectModifiedAfterOpen"] = current.IsModified;
                    // TIA persists project edits only by Save: a project last saved before the archive was written still is that archive.
                    var saved = AdmissionReconcileRules.SavedUtc(current.LastModified);
                    var written = File.GetLastWriteTimeUtc(archivePath);
                    result["projectLastSavedUtc"] = saved.ToString("O");
                    result["archiveWrittenUtc"] = written.ToString("O");
                    result["projectSavedBeforeArchive"] = saved < written;
                }
                finally { current.Close(); }
                result["fingerprintCoverage"] = observed.FingerprintCoverage;
                result["projectFingerprintMatches"] = result.Value<string>("archiveFingerprint") == result.Value<string>("currentFingerprint");
            }
            if (observerPath != null)
            {
                string file = Path.Combine(observerPath, SimulationDiscoveryObserver.ProjectName, SimulationDiscoveryObserver.ProjectName + ".ap21");
                result["observerProjectEmpty"] = false;
                // The observer is a new destination: absent (never created) leaves it unchanged.
                result["observerProjectAbsent"] = !File.Exists(observerPath) && !Directory.Exists(observerPath);
                if (File.Exists(file))
                {
                    var observer = _portal.Projects.Open(new FileInfo(file));
                    try { result["observerProjectEmpty"] = observer.Devices.Count == 0; }
                    finally { observer.Close(); }
                }
            }
            return result;
        }
    }
}
