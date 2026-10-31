using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Pure decisions of vci_conflict_resolve against the live mapping; the session supplies the native observations.
    internal static class VciResolutionRules
    {
        internal sealed class PackageFile
        {
            internal string Sha256;
            internal byte[] Content;
        }

        // Package files of a Host-bound payload: root-relative safe paths with exact SHA-256 and, when required, their bytes.
        internal static Dictionary<string, PackageFile> Files(JObject payload, string name, bool content, Func<string, bool> safeSegment)
        {
            var rows = payload[name] as JArray ?? throw new BridgeException("InvalidRequest", name + " must be an array bound by the Host.");
            if (rows.Count > 16) throw new BridgeException("InvalidRequest", name + " holds at most 16 package files.");
            var result = new Dictionary<string, PackageFile>(StringComparer.Ordinal);
            foreach (var token in rows)
            {
                var row = token as JObject ?? throw new BridgeException("InvalidRequest", name + " rows must be objects.");
                string path = Text(row, "path") ?? "", sha = Text(row, "sha256") ?? "";
                if (path.Length is 0 or > 240 || !path.Split('/').All(safeSegment) || !Regex.IsMatch(sha, "^[0-9a-f]{64}$"))
                    throw new BridgeException("InvalidRequest", name + " paths must be safe and relative to the workspace root with exact SHA-256 values.");
                byte[] bytes = null;
                if (content)
                {
                    try { bytes = Convert.FromBase64String(Text(row, "contentBase64") ?? throw new FormatException()); }
                    catch (FormatException) { throw new BridgeException("InvalidRequest", name + " content must be base64."); }
                    if (Sha256(bytes) != sha || row["bytes"]?.Type != JTokenType.Integer || bytes.Length != (long)row["bytes"])
                        throw new BridgeException("ArtifactChanged", name + " content differs from its bound SHA-256 or length.");
                }
                if (result.ContainsKey(path)) throw new BridgeException("InvalidRequest", name + " repeats a path.");
                result.Add(path, new PackageFile { Sha256 = sha, Content = bytes });
            }
            return result;
        }

        // The refusal a resolution meets on the live mapping as code and message, or null when it is admitted.
        internal static string[] Refusal(string take, string direction, bool workspaceChanged, string liveContentSha256, string reviewedContentSha256,
            IDictionary<string, string> package, IDictionary<string, PackageFile> reviewed, IDictionary<string, PackageFile> result, Func<string, bool> inPackage)
        {
            if (!(take == "native" && direction == "ProjectToWorkspace" || (take == "remote" || take == "merged") && direction == "WorkspaceToProject") || reviewedContentSha256 == null)
                return new[] { "InvalidRequest", "Prepare vci_conflict_resolve through the Host with a reviewed comparisonRef." };
            if (!workspaceChanged)
                return new[] { "VciResolutionNotNeeded", "The mapping reports no workspace-side change. Check the reviewed remote side out into the workspace first, or synchronize normally." };
            if (reviewed.Count == 0 || !reviewed.Keys.All(inPackage))
                return new[] { "VciResolutionMismatch", "The reviewed package files are not the package of this mapping." };
            if (package.Count != reviewed.Count || reviewed.Any(f => !package.TryGetValue(f.Key, out var sha) || sha != f.Value.Sha256))
                return new[] { "VciResolutionStale", "The workspace package is not exactly the reviewed remote side. Check it out again or review a new comparison." };
            if (liveContentSha256 != reviewedContentSha256)
                return new[] { "VciResolutionStale", "The project object changed after the reviewed comparison; review a new comparison." };
            if (take == "native" ? result.Count != 0 : !result.Keys.OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(reviewed.Keys.OrderBy(p => p, StringComparer.Ordinal)))
                return new[] { "InvalidRequest", "A remote or merged resolution writes exactly the reviewed package files; take=native writes none." };
            return null;
        }

        private static string Text(JObject row, string key) => row[key]?.Type == JTokenType.String ? (string)row[key] : null;

        internal static string Sha256(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
