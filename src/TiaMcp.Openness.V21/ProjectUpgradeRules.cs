#nullable disable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>project_upgrade rules without any native call; only plain file reads.</summary>
    internal static class ProjectUpgradeRules
    {
        // V15.1 projects carry .ap15_1; every other release keeps its major number (V14 SP1 is .ap14).
        private static readonly Regex Supported = new Regex(@"^\.(z?)ap(1[4-9]|20|15_1)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AnyRelease = new Regex(@"^\.z?ap\d+(_\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Marker = new Regex("<Data\\s[^>]*\\bType=\"Project\"[^>]*>", RegexOptions.CultureInvariant);
        private const int MarkerBytes = 64 * 1024;
        private const long MaximumLogBytes = 16L * 1024 * 1024;

        internal sealed class Layout { internal string Root, CopyDirectory, Input, Target, Receipt, Upgraded; }

        /// <summary>The source version ("V16", "V15.1") of a V14-V20 project file or archive. V13 and older, V21 and newer are refused here.</summary>
        internal static string SourceVersion(string path, string expectedSha256, out bool archive)
        {
            if (expectedSha256 == null || !Regex.IsMatch(expectedSha256, "^[0-9a-fA-F]{64}$"))
                throw new BridgeException("InvalidRequest", "expectedSha256 must be the 64-character SHA-256 of the project file or archive.");
            string extension = Path.GetExtension(path ?? "");
            var match = Supported.Match(extension);
            if (!match.Success)
            {
                if (!AnyRelease.IsMatch(extension))
                    throw new BridgeException("InvalidRequest", "A .ap14-.ap20 project file (V15.1: .ap15_1) or a .zap14-.zap20 archive is required.");
                throw Unsupported();
            }
            archive = match.Groups[1].Value.Length == 1;
            return "V" + match.Groups[2].Value.Replace('_', '.');
        }

        private static BridgeException Unsupported(string detail = "") => new BridgeException("ProjectVersionUnsupported",
            "project_upgrade upgrades V14 to V20 projects and archives. V13 and older are refused before any native call; " +
            "V21 projects open with project_open or project_retrieve, and newer projects cannot be down-converted." + detail);

        /// <summary>
        /// The project name its marker declares, after checking that its ProjectCompatibilityVersion is the version the file
        /// name claims. A project file is its own marker; an archive must hold exactly one top-level .apNN marker of its own release.
        /// </summary>
        internal static string MarkerName(string path, bool archive, string version)
        {
            string text;
            if (archive)
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    var markers = zip.Entries.Where(e => e.FullName.IndexOfAny(new[] { '/', '\\' }) < 0 && AnyRelease.IsMatch(Path.GetExtension(e.FullName))).ToList();
                    if (markers.Count != 1) throw new BridgeException("InvalidRequest", "A project archive must hold exactly one top-level project file.");
                    if (!string.Equals(Path.GetExtension(markers[0].FullName), Path.GetExtension(path).Remove(1, 1), StringComparison.OrdinalIgnoreCase)) throw Unsupported(" The archived project file is of another release.");
                    using (var stream = markers[0].Open()) text = Head(stream);
                }
            }
            else using (var stream = File.OpenRead(path)) text = Head(stream);
            var marker = Marker.Match(text);
            var declared = Regex.Match(marker.Value, "\\bProjectCompatibilityVersion=\"(\\d+)\\.(\\d+)\\.", RegexOptions.CultureInvariant);
            var name = Regex.Match(marker.Value, "\\bName=\"([^\"]+)\"", RegexOptions.CultureInvariant);
            if (!declared.Success || !name.Success) throw new BridgeException("InvalidRequest", "The project file declares no project name and compatibility version.");
            string actual = "V" + declared.Groups[1].Value + (declared.Groups[2].Value == "0" ? "" : "." + declared.Groups[2].Value);
            if (actual != version) throw Unsupported(" The project declares " + actual + ", not the " + version + " its file name claims.");
            string value = WebUtility.HtmlDecode(name.Groups[1].Value);
            if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Trim() != value) throw new BridgeException("InvalidRequest", "The declared project name is not a valid folder name.");
            return value;
        }

        private static string Head(Stream stream)
        {
            var buffer = new byte[MarkerBytes]; int read = 0, count;
            while (read < buffer.Length && (count = stream.Read(buffer, read, buffer.Length - read)) > 0) read += count;
            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        /// <summary>A project folder is copied whole, so it may hold no other project or archive at its top level or below.</summary>
        internal static void RequireSingleProject(string folder, string projectFile)
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                if (AnyRelease.IsMatch(Path.GetExtension(path)) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(projectFile), StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("InvalidRequest", "The project folder holds another project or archive; give the project its own folder.");
        }

        /// <summary>
        /// The owned layout under a new root. Observed on V21 (31 October 2026): OpenWithUpgrade saves the upgrade next to the
        /// project folder as &lt;folder&gt;_V21/&lt;folder&gt;_V21.ap21, named after the folder, so the folder is copied under its own name.
        /// RetrieveWithUpgrade retrieves the archive (copied into s) into w/&lt;name&gt; and saves w/&lt;name&gt;_V21/&lt;name&gt;_V21.ap21,
        /// named after the archived project (projectName), which the archive file name need not match.
        /// </summary>
        internal static Layout Plan(string root, string path, bool archive, string projectName)
        {
            root = Path.GetFullPath(root).TrimEnd('\\', '/');
            string folder = Path.GetFullPath(Path.GetDirectoryName(path)).TrimEnd('\\', '/'), folderName = Path.GetFileName(folder);
            if (!archive && string.IsNullOrEmpty(folderName)) throw new BridgeException("InvalidRequest", "A project file must lie in its own project folder.");
            if (!archive && (root + Path.DirectorySeparatorChar).StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("InvalidRequest", "The upgrade destination would lie inside the source project folder; keep the project outside the artifacts directory.");
            var layout = new Layout { Root = root, CopyDirectory = Path.Combine(root, archive ? "s" : folderName), Receipt = Path.Combine(root, "upgrade-receipt.json") };
            layout.Input = Path.Combine(layout.CopyDirectory, Path.GetFileName(path));
            layout.Target = archive ? Path.Combine(root, "w") : root;
            string name = (archive ? projectName : folderName) + "_V21";
            layout.Upgraded = Path.Combine(layout.Target, name, name + ".ap21");
            if (Path.Combine(layout.Target, name).Length > EngineeringPaths.MaximumProjectDirectoryLength)
                throw new BridgeException("ProjectPathTooLong", "TIA limits the upgraded project directory to 143 characters. Use a shorter artifacts directory or project name; nothing was copied.");
            return layout;
        }

        /// <summary>Whether TIA returned the upgraded V21 project exactly where the observed layout puts it.</summary>
        internal static bool InLayout(Layout layout, string upgraded) =>
            !string.IsNullOrEmpty(upgraded) && string.Equals(Path.GetFullPath(upgraded), layout.Upgraded, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The upgraded project an interrupted upgrade's receipt proves to reconciliation: only an "upgraded" receipt of the admitted
        /// source (the archive, or the folder of the project file) with its current hash (sourceSha256, freshly recomputed) that names
        /// the observed layout's .ap21 under root. An archive's layout name comes from the admitted archive's own marker, never from
        /// the receipt. Otherwise null: a partial copy or an unfinished upgrade is held without being opened in TIA. A malformed
        /// receipt or source throws (the caller holds).
        /// </summary>
        internal static string ReceiptProject(JObject receipt, string root, string admittedSource, bool archive, string sourceSha256)
        {
            var source = receipt?["source"] as JObject;
            string path = source?.Value<string>("path"), project = (receipt?["project"] as JObject)?.Value<string>("path");
            if (receipt?.Value<string>("state") != "upgraded" || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(project) || string.IsNullOrEmpty(sourceSha256) ||
                !string.Equals(source.Value<string>("sourceSha256"), sourceSha256, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(Path.GetFullPath(archive ? path : Path.GetDirectoryName(path)).TrimEnd('\\', '/'), Path.GetFullPath(admittedSource).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
            string name = archive ? MarkerName(admittedSource, true, SourceVersion(admittedSource, sourceSha256, out _)) : null;
            return InLayout(Plan(root, path, archive, name), project) ? project : null;
        }

        /// <summary>TIA's conversion log: its project success marker and message counts.</summary>
        internal static JObject ConversionLog(string text) => new JObject
        {
            ["succeeded"] = text.Contains("ConversionInfoTabProjectSucceeded"),
            ["errors"] = Regex.Matches(text, "MessageClass=\"Error\"").Count,
            ["warnings"] = Regex.Matches(text, "MessageClass=\"Warning\"").Count,
        };

        /// <summary>
        /// The conversion logs (ConversionLog_*_to_21.*.xml) and .ap21 files TIA left under root. Evidence only: a failure to
        /// collect it is recorded as evidenceFailure and never replaces TIA's own outcome; logs over 16 MiB are listed, not parsed.
        /// </summary>
        internal static JObject Evidence(string root, Func<FileInfo, JObject> artifact)
        {
            var result = new JObject { ["upgradeLogs"] = new JArray(), ["upgradedProjects"] = new JArray() };
            try
            {
                foreach (string path in Directory.GetFiles(root, "ConversionLog_*_to_21.*.xml", SearchOption.AllDirectories).Take(10))
                {
                    var file = new FileInfo(path); var log = artifact(file);
                    if (file.Length <= MaximumLogBytes) log.Merge(ConversionLog(File.ReadAllText(path)));
                    else log["parsed"] = false;
                    ((JArray)result["upgradeLogs"]).Add(log);
                }
                result["upgradedProjects"] = new JArray(Directory.GetFiles(root, "*.ap21", SearchOption.AllDirectories).Take(10));
            }
            catch (Exception error) { result["evidenceFailure"] = error.GetType().Name + ": " + error.Message; }
            return result;
        }
    }
}
