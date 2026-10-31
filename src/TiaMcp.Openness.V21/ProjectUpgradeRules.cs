#nullable disable
using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>Pure project_upgrade rules: they read no file and make no native call.</summary>
    internal static class ProjectUpgradeRules
    {
        // V15.1 projects carry .ap15_1; every other release keeps its major number (V14 SP1 is .ap14).
        private static readonly Regex Supported = new Regex(@"^\.(z?)ap(1[4-9]|20|15_1)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AnyRelease = new Regex(@"^\.z?ap\d+(_\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal sealed class Layout { internal string Root, CopyDirectory, Input, Target, Receipt; }

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
                throw new BridgeException("ProjectVersionUnsupported", "project_upgrade upgrades V14 to V20 projects and archives. V13 and older are refused before any native call; " +
                    "V21 projects open with project_open or project_retrieve, and newer projects cannot be down-converted.");
            }
            archive = match.Groups[1].Value.Length == 1;
            return "V" + match.Groups[2].Value.Replace('_', '.');
        }

        /// <summary>The owned layout under a new root: a project folder is copied whole into p, an archive into s and retrieved into w.</summary>
        internal static Layout Plan(string root, string path, bool archive)
        {
            root = Path.GetFullPath(root).TrimEnd('\\', '/');
            string folder = Path.GetFullPath(Path.GetDirectoryName(path)).TrimEnd('\\', '/');
            if (!archive && (root + Path.DirectorySeparatorChar).StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("InvalidRequest", "The upgrade destination would lie inside the source project folder; keep the project outside the artifacts directory.");
            var layout = new Layout { Root = root, CopyDirectory = Path.Combine(root, archive ? "s" : "p"), Receipt = Path.Combine(root, "upgrade-receipt.json") };
            layout.Input = Path.Combine(layout.CopyDirectory, Path.GetFileName(path));
            layout.Target = archive ? Path.Combine(root, "w") : layout.CopyDirectory;
            // TIA saves the upgrade as <name>_V21; that directory must fit TIA's project directory limit.
            if (Path.Combine(layout.Target, Path.GetFileNameWithoutExtension(path) + "_V21").Length > EngineeringPaths.MaximumProjectDirectoryLength)
                throw new BridgeException("ProjectPathTooLong", "TIA limits the upgraded project directory to 143 characters. Use a shorter artifacts directory or project name; nothing was copied.");
            return layout;
        }

        /// <summary>TIA's conversion log: its project success marker and message counts.</summary>
        internal static JObject ConversionLog(string text) => new JObject
        {
            ["succeeded"] = text.Contains("ConversionInfoTabProjectSucceeded"),
            ["errors"] = Regex.Matches(text, "MessageClass=\"Error\"").Count,
            ["warnings"] = Regex.Matches(text, "MessageClass=\"Warning\"").Count,
        };
    }
}
