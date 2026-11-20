using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // One bridge call exports at most this many blocks, so it stays far below the Host's call timeout.
        internal const int BlockExportBatchLimit = 50;

        // What one batch has already read: the scopes of the PLC and, per group, its blocks by path. The paths of a batch mostly share a
        // group, so each group is listed once instead of once per path.
        private sealed class BlockLookup
        {
            internal List<SoftwareScope> Scopes;
            internal readonly Dictionary<string, Dictionary<string, BlockEntry>> Groups = new Dictionary<string, Dictionary<string, BlockEntry>>(StringComparer.Ordinal);
        }

        /// <summary>Finds a block by its exact path by descending the group chain of the path, reading only the groups on the way and the
        /// names of the blocks in the last one. null when the path is not a plain block path or names no block there; the caller then
        /// falls back to the full lookup, which keeps its error behavior. Without a lookup the last group is read until the block is found.</summary>
        private static BlockEntry ResolveBlockPath(PlcSelection plc, string path, BlockLookup lookup = null)
        {
            if (lookup != null && lookup.Scopes == null) lookup.Scopes = SoftwareScopes(plc, new ScanBudget(50000));
            foreach (var scope in lookup != null ? lookup.Scopes : SoftwareScopes(plc, new ScanBudget(50000)))
            {
                string root = scope.Path + "/blocks";
                string[] chain = BlockPathChain.Groups(path, root);
                if (chain == null) continue;
                PlcBlockGroup group = scope.Blocks;
                foreach (string segment in chain)
                {
                    group = group.Groups.FirstOrDefault(child => Segment(child.Name) == segment);
                    if (group == null) break;
                    root += "/groups/" + segment;
                }
                if (group == null) continue;
                if (lookup == null)
                {
                    foreach (var block in group.Blocks)
                        if (ObjectIdentity.ObjectPath(root, block.Name, block.Namespace) == path) return new BlockEntry { Item = block, Path = path };
                    continue;
                }
                Dictionary<string, BlockEntry> listed;
                if (!lookup.Groups.TryGetValue(root, out listed))
                {
                    listed = new Dictionary<string, BlockEntry>(StringComparer.Ordinal);
                    foreach (var block in group.Blocks)
                    {
                        string blockPath = ObjectIdentity.ObjectPath(root, block.Name, block.Namespace);
                        listed[blockPath] = new BlockEntry { Item = block, Path = blockPath };
                    }
                    lookup.Groups[root] = listed;
                }
                BlockEntry found;
                if (listed.TryGetValue(path, out found)) return found;
            }
            return null;
        }

        // block_read with blockPaths: the exports of several exact blocks of one PLC in one call. The project and PLC are selected once and
        // each block is found by its path; a block that cannot be exported is an entry with its error, not a failure of the call. Entries
        // are compact: the files, their size and the network count, never the XML or the rendered text.
        private JObject BlockReadBatch(JObject args, string render)
        {
            if (args.Value<string>("format") != null) throw new BridgeException("InvalidRequest", "blockPaths cannot be combined with format.");
            var paths = (args["blockPaths"] as JArray)?.Select(token => token.Type == JTokenType.String ? (string)token : null).ToList();
            if (paths == null || paths.Count == 0 || paths.Count > BlockExportBatchLimit || paths.Any(string.IsNullOrWhiteSpace))
                throw new BridgeException("InvalidRequest", "blockPaths must list 1 to " + BlockExportBatchLimit + " block paths.");
            string plcPath = args.Value<string>("plcPath") ?? ObjectIdentity.PlcPath(paths[0]);
            if (plcPath == null || paths.Any(path => ObjectIdentity.PlcPath(path) != plcPath))
                throw new BridgeException("InvalidRequest", "blockPaths must be block paths of one PLC.");
            string directory = ArtifactDirectory(args);
            var project = SelectProject(args);
            var effective = (JObject)args.DeepClone();
            effective["plcPath"] = plcPath;
            var plc = SelectPlc(project, effective);
            var lookup = new BlockLookup();
            var results = new JArray();
            foreach (string path in paths)
            {
                var entry = new JObject { ["path"] = path };
                results.Add(entry);
                try
                {
                    var found = ResolveBlockPath(plc, path, lookup);
                    var selected = found != null ? new BlockSelection { Project = project, PlcPath = plc.Path, Block = found }
                        : SelectBlock(new JObject { ["blockPath"] = path, ["plcPath"] = plc.Path, ["projectName"] = args["projectName"] });
                    var exported = ExportBlock(selected, render, args.Value<string>("culture"), false, directory);
                    entry["ok"] = true;
                    entry["artifact"] = new JObject { ["path"] = exported["artifact"]["path"], ["bytes"] = exported["artifact"]["bytes"] };
                    if (exported["textArtifact"] != null) entry["textArtifact"] = new JObject { ["path"] = exported["textArtifact"]["path"] };
                    if (exported["networks"] is JArray networks) entry["networks"] = networks.Count;
                    if (exported["textUnavailable"] != null) entry["textUnavailable"] = new JObject { ["reason"] = exported["textUnavailable"]["reason"] };
                }
                catch (Exception exception) when (!(exception is OutOfMemoryException))
                {
                    // The code a single block_read would have answered with.
                    var error = WorkerLoop.Error(null, exception)["error"];
                    entry["ok"] = false;
                    entry["error"] = new JObject { ["code"] = error["code"], ["message"] = error["message"] };
                }
            }
            var result = Envelope();
            result["results"] = results;
            return result;
        }
    }
}
