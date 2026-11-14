using System;
using System.Linq;
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
                _retrievedProjectPaths.Add(project.Path.FullName);
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
            if (_portal == null) throw new BridgeException("NotAttached", "project_open needs a worker-owned TIA instance.",
                new JObject { ["remediation"] = "Call tia_start, then project_open." });
            if (!_ownsPortal || _portal.Projects.Count != 0)
                throw new BridgeException("ProjectIsolationRequired", "project_open requires an empty worker-owned instance started with tia_start.",
                    new JObject { ["ownsPortal"] = _ownsPortal, ["openProjects"] = new JArray(_portal.Projects.Select(p => p.Path?.FullName ?? p.Name)),
                        ["remediation"] = _ownsPortal ? "Close the open project with project_close (or tia_detach and tia_start), then project_open."
                            : "This session is attached to a user-owned TIA. tia_detach, then tia_start for an owned instance." });
            string path = args.Value<string>("path");
            if (!ProjectPathIsAbsolute(path) || !string.Equals(Path.GetExtension(path), ".ap21", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("InvalidRequest", "An absolute existing .ap21 project path on the TIA host is required. Automatic upgrades are not performed; use project_upgrade for V14-V20 projects.");
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

        // Close one project in a worker-owned instance so another can be opened without restarting the Portal. A user-owned
        // TIA is never touched. Unsaved edits are refused unless discardUnsaved is set; nothing is ever saved here.
        private JObject ProjectClose(JObject args)
        {
            EnsureAttached();
            if (!_ownsPortal) throw new BridgeException("OwnedSessionRequired", "project_close closes projects only in a worker-owned instance started with tia_start; a user-owned TIA is never changed.");
            var project = SelectProject(args);
            bool discard = args.Value<bool?>("discardUnsaved") == true;
            bool modified = project.IsModified;
            if (modified && !discard) throw new BridgeException("UnsavedProject", "The project has unsaved edits. Save them through a change, or pass discardUnsaved:true to close without saving.",
                new JObject { ["project"] = ProjectInfo(project) });
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            project.Close();
            result["closed"] = true; result["saved"] = false; result["unsavedEditsDiscarded"] = modified;
            result["openProjects"] = new JArray(_portal.Projects.Select(p => p.Path?.FullName ?? p.Name));
            return result;
        }

        // Replace the one open project of a worker-owned instance by a read-write copy of its saved state. A primary project
        // is archived, closed and retrieved; a read-only (secondary) one, which V21 cannot archive, is closed and its saved
        // folder copied. Unsaved edits are refused; the copy is left open as the primary project. The original files are never written; work that needs a writable project
        // (block exports, snapshot capture) then runs on the copy instead of the real project.
        private JObject ProjectOpenCopy(JObject args)
        {
            EnsureAttached();
            if (!_ownsPortal) throw new BridgeException("OwnedSessionRequired", "project_open_copy requires a worker-owned instance started with tia_start.");
            if (_portal.Projects.Count != 1) throw new BridgeException("ProjectIsolationRequired", "project_open_copy needs exactly one open project in this instance.",
                new JObject { ["openProjects"] = new JArray(_portal.Projects.Select(p => p.Path?.FullName ?? p.Name)) });
            var source = SelectProject(args);
            if (source.IsModified) throw new BridgeException("UnsavedProject", "The open project has unsaved edits; the copy holds only the saved state. Save or discard them first.");
            string root = ArtifactDirectory(args);
            string archive = Path.Combine(root, EngineeringPaths.NewRecoveryArchiveName(root));
            string directory = EngineeringPaths.NewRecoveryDirectory(root, source.Name);
            if (File.Exists(archive) || Directory.Exists(directory)) throw new BridgeException("ArtifactExists", "The planned copy destination already exists.");
            var result = Envelope();
            string sourceName = source.Name;
            result["source"] = ProjectInfo(source);
            bool readOnlySource = !source.IsPrimary;
            result["sourceReadOnly"] = readOnlySource;
            Project copy;
            if (!readOnlySource)
            {
                result["copyRoute"] = "archiveAndRetrieve";
                source.Archive(new DirectoryInfo(Path.GetDirectoryName(archive)), Path.GetFileName(archive), ProjectArchivationMode.Compressed);
                if (!File.Exists(archive)) throw new BridgeException("ArchiveFailed", "TIA did not produce the copy archive; the original stays open.", result);
                result["archive"] = FileArtifact(new FileInfo(archive), "application/octet-stream");
                source.Close();
                result["sourceClosed"] = true;
                Directory.CreateDirectory(directory);
                copy = _portal.Projects.Retrieve(new FileInfo(archive), new DirectoryInfo(directory), null, ProjectOpenMode.Primary);
            }
            else
            {
                // V21 refuses Project.Archive on a secondary project ("not permitted in a read-only context", observed natively
                // on 14 November 2026). A secondary project has nothing to save, so close it and copy its saved folder instead;
                // the copy is opened as the primary project. The original folder is only read.
                result["copyRoute"] = "closeAndCopyFolder";
                var file = new FileInfo(source.Path.FullName);
                source.Close();
                result["sourceClosed"] = true;
                var copied = CopyProjectFolder(file.Directory, new DirectoryInfo(Path.Combine(directory, file.Directory.Name)));
                result["copiedFiles"] = copied;
                copy = _portal.Projects.Open(new FileInfo(Path.Combine(directory, file.Directory.Name, file.Name)));
            }
            if (copy.Name != sourceName || !copy.Path.FullName.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("CopyVerificationFailed", "The copy is not the source project inside its new directory.", result);
            result["project"] = ProjectInfo(copy);
            result["readOnly"] = !copy.IsPrimary;
            result["temporaryCopy"] = true;
            result["note"] = "The open project is now the copy; the original is closed and unchanged. Changes apply to the copy only.";
            return result;
        }

        // Copies a closed project's folder file by file; links and junctions are refused so nothing outside the folder is read.
        private static int CopyProjectFolder(DirectoryInfo source, DirectoryInfo destination)
        {
            if (destination.Exists) throw new BridgeException("ArtifactExists", "The planned copy destination already exists.");
            if ((source.Attributes & FileAttributes.ReparsePoint) != 0) throw new BridgeException("UnsupportedProjectFolder", "The project folder is a link or junction; it is not copied.");
            int count = 0;
            destination.Create();
            foreach (var directory in source.GetDirectories())
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new BridgeException("UnsupportedProjectFolder", "The project folder contains a link or junction; it is not copied.");
                count += CopyProjectFolder(directory, new DirectoryInfo(Path.Combine(destination.FullName, directory.Name)));
            }
            foreach (var file in source.GetFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new BridgeException("UnsupportedProjectFolder", "The project folder contains a linked file; it is not copied.");
                file.CopyTo(Path.Combine(destination.FullName, file.Name), false);
                count++;
            }
            return count;
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
    }
}
