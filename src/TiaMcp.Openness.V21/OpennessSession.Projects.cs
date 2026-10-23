using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private JObject ProjectRetrieve(JObject args)
        {
            EnsureAttached();
            if (!_ownsPortal || _portal.Projects.Count != 0)
                throw new BridgeException("ProjectIsolationRequired", "Archive retrieval requires an empty worker-owned TIA instance.");
            string path = args.Value<string>("archivePath");
            string expected = args.Value<string>("expectedSha256");
            if (!ProjectPathIsAbsolute(path) || !string.Equals(Path.GetExtension(path), ".zap21", StringComparison.OrdinalIgnoreCase) ||
                expected == null || !Regex.IsMatch(expected, "^[0-9a-fA-F]{64}$"))
                throw new BridgeException("InvalidRequest", "An absolute .zap21 archive path and its 64-character SHA-256 are required.");
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists) throw new BridgeException("ArchiveNotFound", "The selected archive does not exist.");
            var artifact = FileArtifact(file);
            if (!string.Equals(expected, artifact.Value<string>("sha256"), StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("CheckpointInvalid", "The archive content does not match the expected hash.");
            string directory = SharedMutationPlannedDestination("projectRetrieve", () => Path.Combine(ArtifactDirectory(args), "r_" + Guid.NewGuid().ToString("N").Substring(0, 12)));
            // Reserve space for a native project name. An archive may still have a longer
            // name; native retrieval fails without modifying an existing open project.
            if (directory.Length > 80) throw new BridgeException("RecoveryPathTooLong", "Use a shorter artifact directory for TIA's project path limit.");
            Directory.CreateDirectory(directory);
            var result = Envelope();
            result["archive"] = artifact;
            result["destination"] = directory;
            try
            {
                var project = _portal.Projects.Retrieve(file, new DirectoryInfo(directory));
                result["project"] = ProjectInfo(project);
                result["retrieved"] = true;
                return result;
            }
            catch (Exception exception)
            {
                result["retrieved"] = false;
                result["failure"] = exception.Message;
                throw new BridgeException("ArchiveRetrieveFailed", exception.Message, result);
            }
        }

        private JObject ProjectOpen(JObject args)
        {
            EnsureAttached();
            if (!_ownsPortal || _portal.Projects.Count != 0)
                throw new BridgeException("ProjectIsolationRequired", "project_open requires an empty worker-owned instance started with tia_start.");
            string path = args.Value<string>("path");
            if (!ProjectPathIsAbsolute(path) || !string.Equals(Path.GetExtension(path), ".ap21", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("InvalidRequest", "An absolute existing .ap21 project path on the TIA host is required. Automatic upgrades are not performed.");
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists) throw new BridgeException("ProjectNotFound", "The .ap21 project file does not exist on this host.");
            var project = _portal.Projects.Open(file);
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["opened"] = true;
            result["saved"] = false;
            return result;
        }

        private static bool ProjectPathIsAbsolute(string path) => !string.IsNullOrWhiteSpace(path) &&
            (Regex.IsMatch(path, "^[A-Za-z]:[\\\\/]") ||
             path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal) &&
             path.Substring(2).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length >= 3);
    }
}
