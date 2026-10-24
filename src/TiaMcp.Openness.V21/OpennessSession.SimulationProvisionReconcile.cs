using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
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

            var original = baseline["originalCardManifest"] as JArray;
            var manifest = SimulationCardFiles(Path.Combine(storage, "SIMATIC_MC"));
            result["cardManifest"] = manifest;
            result["cardManifestMatches"] = original != null && JToken.DeepEquals(manifest, original) &&
                SimulationProvisionManifestHash(manifest) == report.Value<string>("originalProgramRevision");

            var archive = baseline["projectArchive"] as JObject;
            string archivePath = archive?.Value<string>("path");
            bool archiveMatches = archive != null && SameSimulationPath(Path.GetDirectoryName(archivePath), storage) && File.Exists(archivePath) &&
                string.Equals(ChangeFileHash(archivePath), archive.Value<string>("sha256"), StringComparison.OrdinalIgnoreCase);
            result["archiveHashMatches"] = archiveMatches;
            result["projectFingerprintMatches"] = false;
            if (archiveMatches)
            {
                if (File.Exists(retrieveDirectory) || Directory.Exists(retrieveDirectory))
                    throw new BridgeException("MutationScopeStale", "The reconciliation retrieval destination already exists.");
                if (retrieveDirectory.Length > 80) throw new BridgeException("RecoveryPathTooLong", "Use a shorter artifact directory for TIA's project path limit.");
                Directory.CreateDirectory(retrieveDirectory);
                var archived = new EngineeringChange { Kind = "simulation_provision_reconcile" };
                var copy = _portal.Projects.Retrieve(new FileInfo(archivePath), new DirectoryInfo(retrieveDirectory));
                try { result["archiveFingerprint"] = EngineeringFingerprint(archived, copy, true); result["archiveCopyPath"] = copy.Path.FullName; }
                finally { copy.Close(); }
                var observed = new EngineeringChange { Kind = "simulation_provision_reconcile" };
                var current = _portal.Projects.Open(new FileInfo(projectPath));
                try
                {
                    result["currentFingerprint"] = EngineeringFingerprint(observed, current, true);
                    result["currentProjectModifiedAfterOpen"] = current.IsModified;
                }
                finally { current.Close(); }
                result["fingerprintCoverage"] = observed.FingerprintCoverage;
                result["projectFingerprintMatches"] = result.Value<string>("archiveFingerprint") == result.Value<string>("currentFingerprint");
            }
            if (observerPath != null)
            {
                string file = Path.Combine(observerPath, SimulationDiscoveryObserver.ProjectName, SimulationDiscoveryObserver.ProjectName + ".ap21");
                result["observerProjectEmpty"] = false;
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
