using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

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
            artifact["mediaType"] = "application/octet-stream";
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
            bool readOnly = args.Value<bool?>("readOnly") == true;
            var project = readOnly ? _portal.Projects.Open(file, null, ProjectOpenMode.Secondary) : _portal.Projects.Open(file);
            // Mutation refusal keys on the native IsPrimary state, so a read-only request must read back as secondary.
            if (readOnly && project.IsPrimary)
            {
                project.Close();
                throw new BridgeException("ReadOnlyOpenUnverified", "TIA opened the project as primary; it was closed again without saving.");
            }
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["opened"] = true;
            result["readOnly"] = !project.IsPrimary;
            result["saved"] = false;
            return result;
        }

        // Archive the saved source, retrieve it as a secondary project into a fresh owned directory and close that
        // copy again. Project.SaveAs is not used: it saves unsaved edits and moves the open project to the new location.
        private JObject ProjectCopy(JObject args)
        {
            EnsureAttached();
            if (!_ownsPortal) throw new BridgeException("OwnedSessionRequired", "project_copy requires a worker-owned instance started with tia_start.");
            var source = SelectProject(args);
            if (source.IsModified) throw new BridgeException("UnsavedProject", "project_copy copies the saved state and never saves the source; save or discard its edits first.");
            string root = ArtifactDirectory(args);
            string archive = SharedMutationPlannedDestination("projectCopyArchive", () => Path.Combine(root, EngineeringPaths.NewRecoveryArchiveName(root)));
            string directory = SharedMutationPlannedDestination("projectCopy", () => EngineeringPaths.NewRecoveryDirectory(root, source.Name));
            if (File.Exists(archive) || Directory.Exists(archive) || File.Exists(directory) || Directory.Exists(directory))
                throw new BridgeException("ArtifactExists", "The planned copy destination already exists.");
            var result = Envelope();
            result["source"] = ProjectInfo(source);
            result["destination"] = directory;
            Project copy = null;
            try
            {
                source.Archive(new DirectoryInfo(Path.GetDirectoryName(archive)), Path.GetFileName(archive), ProjectArchivationMode.Compressed);
                if (!File.Exists(archive)) throw new BridgeException("ArchiveFailed", "TIA did not produce the copy archive.");
                var artifact = FileArtifact(new FileInfo(archive));
                artifact["mediaType"] = "application/octet-stream";
                result["archive"] = artifact;
                Directory.CreateDirectory(directory);
                // Secondary mode keeps the open source as the only primary project.
                copy = _portal.Projects.Retrieve(new FileInfo(archive), new DirectoryInfo(directory), null, ProjectOpenMode.Secondary);
                result["project"] = ProjectInfo(copy);
                if (copy.Name != source.Name || !copy.Path.FullName.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("CopyVerificationFailed", "The retrieved copy is not the source project inside its new directory.");
            }
            catch (Exception exception)
            {
                result["copied"] = false;
                result["failure"] = exception.Message;
                throw new BridgeException((exception as BridgeException)?.Code ?? "ProjectCopyFailed", exception.Message, result);
            }
            finally
            {
                // Two open projects with one name would make every later selector ambiguous.
                if (copy != null)
                    try { copy.Close(); result["copyClosed"] = true; }
                    catch (Exception exception) { result["copyClosed"] = false; result["closeFailure"] = exception.Message; }
            }
            result["copied"] = true;
            result["sourceSaved"] = false;
            return result;
        }

        private static bool ProjectPathIsAbsolute(string path) => !string.IsNullOrWhiteSpace(path) &&
            (Regex.IsMatch(path, "^[A-Za-z]:[\\\\/]") ||
             path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal) &&
             path.Substring(2).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length >= 3);
    }
}
