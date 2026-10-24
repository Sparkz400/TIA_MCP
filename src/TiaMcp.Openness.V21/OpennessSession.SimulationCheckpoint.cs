#nullable disable
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using TiaMcp.Simulation;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static string SimulationProvisionCheckpointPath(string storage, bool projectModified)
        {
            if (projectModified)
                throw new BridgeException("UnsavedProject", "Provisioning requires a saved scratch project for its full recovery archive. No automatic save is performed.");
            const string name = "before-provision.zap21";
            EngineeringPaths.ValidateProjectArchivePath(storage, name);
            string archive = Path.Combine(storage, name), staging = Path.Combine(storage, Path.GetFileNameWithoutExtension(name));
            VerifySimulationProvisionChild(storage, archive, false);
            VerifySimulationProvisionChild(storage, staging, false);
            return archive;
        }

        private static void VerifySimulationProvisionChild(string storage, string path, bool mustExist)
        {
            SimulationTicket.VerifyPlainDirectory(storage);
            string root = Path.GetFullPath(storage).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string child = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root) || !string.Equals(Path.GetDirectoryName(child), root, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("SimulationProvisionStorageScopeInvalid", "Program-card staging is restricted to direct children of the exact owned local storage directory.");
            var entries = new DirectoryInfo(root).EnumerateFileSystemInfos().Where(entry =>
                string.Equals(entry.FullName, child, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (mustExist)
            {
                if (entries.Length != 1 || !(entries[0] is DirectoryInfo) || (entries[0].Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new BridgeException("SimulationProvisionStorageScopeInvalid", "The program-card source must be one existing plain directory within owned storage.");
                SimulationTicket.VerifyPlainDirectory(child);
            }
            else if (entries.Length != 0)
                throw new BridgeException("SimulationProvisionStorageCollision", "A staging or restoration destination already exists. Existing files and directories are never overwritten.");
        }

        private static void SimulationProvisionProjectCheckpoint(Project project, JObject baseline, Action<string> checkpoint)
        {
            string archive = baseline.Value<string>("projectArchivePath");
            if (archive != SimulationProvisionCheckpointPath(Path.GetDirectoryName(archive), project.IsModified))
                throw new BridgeException("SimulationProvisionCheckpointMismatch", "The project checkpoint differs from its preflight destination.");
            checkpoint("beforeProjectArchive");
            project.Archive(new DirectoryInfo(Path.GetDirectoryName(archive)), Path.GetFileName(archive), ProjectArchivationMode.Compressed);
            SimulationTicket.VerifyPlainDirectory(Path.GetDirectoryName(archive));
            var file = new FileInfo(archive);
            if (project.IsModified || !file.Exists || file.Length == 0 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new BridgeException("SimulationProvisionCheckpointFailed", "A nonempty plain archive of the saved project is required before provisioning.");
            var artifact = FileArtifact(file); artifact["mediaType"] = "application/octet-stream";
            baseline["projectArchive"] = artifact;
            checkpoint("afterProjectArchive");
        }
    }
}
