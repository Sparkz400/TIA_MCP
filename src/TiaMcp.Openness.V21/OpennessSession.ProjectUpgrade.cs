using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private sealed class UpgradeSource { internal string Path, Folder, Version, Name, Sha256, SourceSha256; internal bool Archive; }

        // Shared by admission and execution: the version is refused by file name before any file is read, then the untouched
        // source is hashed, its declared version checked and its whole content hashed (archive bytes, or the project folder manifest).
        private static UpgradeSource ProjectUpgradeSource(JObject args)
        {
            string path = ChangeText(args, "path", 4096), expected = args.Value<string>("expectedSha256");
            if (!ProjectPathIsAbsolute(path)) throw new BridgeException("InvalidRequest", "An absolute project file or archive path on the TIA host is required.");
            string version = ProjectUpgradeRules.SourceVersion(path, expected, out bool archive);
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new BridgeException("ProjectNotFound", "The project file or archive does not exist on this host.");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("PackageInvalid", "A linked upgrade source is not accepted.");
            if (!string.Equals(ProjectUpgradeRead(() => ChangeFileHash(path)), expected, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("SourceChanged", "The source file does not match expectedSha256; nothing was copied.");
            var source = new UpgradeSource { Path = path, Folder = Path.GetDirectoryName(path), Version = version, Archive = archive, Sha256 = expected.ToLowerInvariant() };
            try { source.Name = ProjectUpgradeRead(() => ProjectUpgradeRules.MarkerName(path, archive, version)); }
            catch (InvalidDataException) { throw new BridgeException("InvalidRequest", "The archive is not a readable project archive."); }
            source.SourceSha256 = ProjectUpgradeSourceHash(source);
            if (!archive) ProjectUpgradeRead(() => { ProjectUpgradeRules.RequireSingleProject(source.Folder, path); return null; });
            return source;
        }

        private static string ProjectUpgradeSourceHash(UpgradeSource source) => ProjectUpgradeRead(() => source.Archive ? ChangeFileHash(source.Path) : LibraryDirectoryHash(source.Folder));

        // A locked or unreadable source file is refused without its path in the message.
        private static string ProjectUpgradeRead(Func<string> read)
        {
            try { return read(); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            { throw new BridgeException("SourceLocked", "A file of the upgrade source is locked or unreadable (" + error.GetType().Name + "); nothing was opened or changed."); }
        }

        private string ProjectUpgradeDestination(JObject args) => SharedMutationPlannedDestination("projectUpgrade",
            () => Path.Combine(SharedMutationArtifactDirectory(args), "u_" + Guid.NewGuid().ToString("N").Substring(0, 8)));

        /// <summary>
        /// Upgrades an owned copy, never the source: a project folder is copied whole, an archive byte for byte, and TIA's
        /// OpenWithUpgrade / RetrieveWithUpgrade runs on that copy. Nothing is compiled or saved beyond what the upgrade does.
        /// The upgraded project stays open.
        /// </summary>
        private JObject ProjectUpgrade(JObject args)
        {
            EnsureAttached();
            if (!_ownsPortal || _portal.Projects.Count != 0)
                throw new BridgeException("ProjectIsolationRequired", "project_upgrade requires an empty worker-owned instance started with tia_start.");
            var source = ProjectUpgradeSource(args);
            var layout = ProjectUpgradeRules.Plan(ProjectUpgradeDestination(args), source.Path, source.Archive, source.Name);
            if (Directory.Exists(layout.Root) || File.Exists(layout.Root)) throw new BridgeException("ArtifactExists", "The planned upgrade destination already exists.");
            var result = Envelope();
            result["source"] = new JObject { ["path"] = source.Path, ["version"] = source.Version, ["format"] = source.Archive ? "archive" : "project",
                ["projectName"] = source.Name, ["sha256"] = source.Sha256, ["sourceSha256"] = source.SourceSha256,
                ["hashScope"] = source.Archive ? "Archive bytes" : "Sorted SHA-256 manifest of every file in the project folder" };
            result["destination"] = layout.Root;
            var receipt = new JObject { ["schemaVersion"] = 1, ["state"] = "copying", ["source"] = result["source"].DeepClone() };
            Directory.CreateDirectory(layout.CopyDirectory);
            WriteLibraryRecord(layout.Receipt, receipt);
            if (source.Archive) LibraryCopyArchiveSnapshot(source.Path, layout.Input);
            else LibraryCopyWorkingDirectory(source.Folder, layout.CopyDirectory);
            if (ChangeFileHash(layout.Input) != source.Sha256 || (!source.Archive && LibraryDirectoryHash(layout.CopyDirectory) != source.SourceSha256) ||
                ProjectUpgradeSourceHash(source) != source.SourceSha256)
                throw new BridgeException("SourceChanged", "The source changed while its owned copy was made; nothing was upgraded.", result);
            result["ownedCopy"] = layout.Input;
            receipt["state"] = "upgrading"; WriteLibraryRecord(layout.Receipt, receipt);
            Project project;
            try
            {
                Directory.CreateDirectory(layout.Target);
                project = source.Archive ? _portal.Projects.RetrieveWithUpgrade(new FileInfo(layout.Input), new DirectoryInfo(layout.Target))
                    : _portal.Projects.OpenWithUpgrade(new FileInfo(layout.Input));
            }
            catch (Exception error)
            {
                result["upgraded"] = false; result["nativeType"] = error.GetType().FullName; result["failure"] = error.Message;
                ProjectUpgradeEvidence(result, source, layout.Root);
                throw new BridgeException("ProjectUpgradeFailed", error.Message, result);
            }
            var info = ProjectInfo(project); info["isModified"] = project.IsModified;
            result["project"] = info;
            ProjectUpgradeEvidence(result, source, layout.Root);
            if (!ProjectUpgradeRules.InLayout(layout, project.Path?.FullName))
            {
                project.Close();
                throw new BridgeException("UpgradeVerificationFailed", "TIA returned a project that is not the V21 upgrade in the observed owned layout; it was closed unsaved.", result);
            }
            if (result.Value<bool>("sourceUnchanged") != true) throw new BridgeException("SourceChanged", "The source changed during the upgrade; the upgraded copy stays open.", result);
            result["upgraded"] = true;
            result["compiled"] = false;
            receipt["state"] = "upgraded"; receipt["project"] = info.DeepClone(); WriteLibraryRecord(layout.Receipt, receipt);
            return result;
        }

        // Evidence never replaces TIA's outcome: each part records its own failure.
        private static void ProjectUpgradeEvidence(JObject result, UpgradeSource source, string root)
        {
            try { result["sourceUnchanged"] = ProjectUpgradeSourceHash(source) == source.SourceSha256; }
            catch (Exception error) { result["sourceUnchanged"] = false; result["sourceCheckFailure"] = error.Message; }
            result.Merge(ProjectUpgradeRules.Evidence(root, file => FileArtifact(file)));
        }
    }
}
