#nullable disable
using System;
using System.Collections.Generic;

namespace TiaMcp.Openness.V21
{
    /// <summary>Reads the group chain out of an exact block path, so a lookup descends group by group instead of walking every block.
    /// Block paths are built by ObjectIdentity.ObjectPath: scope path + "/blocks", then "/groups/{group}" per level, then
    /// "/namespaces/{namespace}" when the block has one, then "/{name}"; every segment is escaped with Uri.EscapeDataString.</summary>
    internal static class BlockPathChain
    {
        /// <summary>The escaped group segments of path below blocksRoot (empty for a block in the top group), or null when the path is
        /// not a plain block path under that root: another scope, a system group or a shape ObjectPath never produces.</summary>
        internal static string[] Groups(string path, string blocksRoot)
        {
            if (path == null || blocksRoot == null || !path.StartsWith(blocksRoot + "/", StringComparison.Ordinal)) return null;
            string[] parts = path.Substring(blocksRoot.Length + 1).Split('/');
            var groups = new List<string>();
            int index = 0;
            // A group needs a following segment for the block itself, so a block that is named "groups" is not read as a group.
            while (parts.Length - index >= 3 && parts[index] == "groups") { groups.Add(parts[index + 1]); index += 2; }
            int rest = parts.Length - index;
            bool named = rest == 1 || (rest == 3 && parts[index] == "namespaces");
            if (!named || parts[parts.Length - 1].Length == 0) return null;
            foreach (string group in groups) if (group.Length == 0) return null;
            return groups.ToArray();
        }
    }
}
