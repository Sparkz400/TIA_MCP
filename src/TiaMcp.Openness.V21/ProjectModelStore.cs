#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Indexing
{
    // Detached JSON only. No engineering proxies, persistent object GUID claims or content-revision claims.
    internal sealed class ProjectModelStore
    {
        internal const int MaximumNodes = 50000;
        internal const long MaximumBytes = 32L * 1024 * 1024;
        internal const int MaximumPageBytes = 512 * 1024;
        private readonly Dictionary<string, Snapshot> _snapshots = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        private readonly Func<DateTime> _clock;
        private readonly byte[] _key = new byte[32];

        internal ProjectModelStore(Func<DateTime> clock = null)
        {
            _clock = clock ?? (() => DateTime.UtcNow);
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(_key);
        }

        internal JObject Capture(JObject document, int lifetimeSeconds = 300)
        {
            if (document == null) throw Error("InvalidProjectModel", "A detached snapshot document is required.");
            if (lifetimeSeconds < 30 || lifetimeSeconds > 900) throw Error("InvalidRequest", "Snapshot lifetime must be 30..900 seconds.");
            var now = _clock();
            var copy = (JObject)document.DeepClone();
            copy["schemaVersion"] = "project-model.v1";
            copy["snapshotId"] = Guid.NewGuid().ToString("N");
            copy["captureStartedAtUtc"] = copy["captureStartedAtUtc"] ?? new JValue(now.ToString("O"));
            copy["captureCompletedAtUtc"] = copy["captureCompletedAtUtc"] ?? new JValue(now.ToString("O"));
            copy["expiresAtUtc"] = now.AddSeconds(lifetimeSeconds).ToString("O");
            copy["edges"] = copy["edges"] ?? new JArray();
            copy["warnings"] = copy["warnings"] ?? new JArray();
            copy["coverage"] = copy["coverage"] ?? new JArray();
            copy["limits"] = copy["limits"] ?? new JObject { ["maxVisited"] = MaximumNodes, ["maxDepth"] = 64, ["maxBytes"] = MaximumBytes };
            copy["usage"] = copy["usage"] ?? new JObject();
            var snapshot = new Snapshot(copy);
            // A supplied revision is still explicitly scoped to exposed metadata, never native content.
            copy["metadataRevision"] = new JObject {
                ["value"] = copy["metadataRevision"]?["value"] ?? new JValue(Hash(new JArray(snapshot.Nodes.Values.OrderBy(n => (string)n["nodeId"], StringComparer.Ordinal)).ToString(Formatting.None))),
                ["scope"] = "exposedMetadata", ["includesContent"] = false };
            var usage = copy["usage"] as JObject;
            if (usage == null) throw Error("InvalidProjectModel", "usage must be an object.");
            usage["returnedNodes"] = snapshot.Nodes.Count;
            usage["returnedEdges"] = snapshot.Edges.Length;
            AccountDocument(copy);
            foreach (var expired in _snapshots.Where(p => DateTime.Parse((string)p.Value.Document["expiresAtUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) <= now).Select(p => p.Key).ToArray()) Release(expired);
            while (_snapshots.Count >= 2) Release(_snapshots.Keys.First());
            _snapshots.Add((string)copy["snapshotId"], snapshot);
            return (JObject)copy.DeepClone();
        }

        internal JObject Get(string id, string sessionGeneration = null) => (JObject)Find(id, sessionGeneration).Document.DeepClone();

        internal JObject Header(string id)
        {
            var snapshot = Find(id);
            var result = new JObject(snapshot.Document.Properties().Where(p => p.Name != "nodes" && p.Name != "edges").Select(p => new JProperty(p.Name, p.Value.DeepClone())));
            result["roots"] = new JArray(snapshot.Children[""]);
            result["nodeCount"] = snapshot.Nodes.Count;
            result["edgeCount"] = snapshot.Edges.Length;
            result["freshness"] = "snapshotOnly";
            return result;
        }

        internal bool Release(string id) => id != null && _snapshots.Remove(id);
        internal void Invalidate(string reason) { foreach (var snapshot in _snapshots.Values) snapshot.InvalidatedBy = reason ?? "session changed"; }

        internal void RememberDetails(string id, string nodeId, JObject details)
        {
            var snapshot = Find(id);
            if (nodeId == null || !snapshot.Nodes.ContainsKey(nodeId)) throw Error("SelectionNotFound", "Detail target is absent from this snapshot.");
            if (details == null) throw Error("InvalidRequest", "Details must contain a detached observation.");
            var copy = (JObject)snapshot.Document.DeepClone();
            var node = copy["nodes"].OfType<JObject>().Single(n => (string)n["nodeId"] == nodeId);
            node["details"] = details.DeepClone();
            RememberObservation(id, copy);
        }

        internal void RememberRelationships(string id, JArray edges, JArray coverage, JObject query = null)
        {
            var snapshot = Find(id); var copy = (JObject)snapshot.Document.DeepClone();
            if (edges == null || edges.Count > 1000) throw Error("ModelCapacity", "One relationship observation may contain at most 1,000 edges.");
            if (edges.OfType<JObject>().Count() != edges.Count || edges.OfType<JObject>().Select(e => (string)e["edgeId"]).Distinct(StringComparer.Ordinal).Count() != edges.Count)
                throw Error("InvalidProjectModel", "One observation must contain object edges with unique identifiers.");
            var merged = copy["edges"].OfType<JObject>().ToDictionary(e => (string)e["edgeId"], e => e, StringComparer.Ordinal);
            foreach (var edge in edges.OfType<JObject>())
            {
                string edgeId = (string)edge["edgeId"];
                if (string.IsNullOrEmpty(edgeId)) throw Error("InvalidProjectModel", "Observation edge requires edgeId.");
                merged[edgeId] = (JObject)edge.DeepClone();
            }
            if (merged.Count > 1000) throw Error("ModelCapacity", "At most 1,000 observed relationships are retained per snapshot; export then recapture a narrower scope.");
            copy["edges"] = new JArray(merged.Values.OrderBy(e => (string)e["edgeId"], StringComparer.Ordinal));
            if (coverage != null)
            {
                var all = (JArray)copy["coverage"];
                foreach (var record in coverage.OfType<JObject>())
                {
                    foreach (var old in all.OfType<JObject>().Where(x => (string)x["provider"] == (string)record["provider"] && (string)x["scope"] == (string)record["scope"]).ToArray()) old.Remove();
                    var retainedCoverage = (JObject)record.DeepClone();
                    if (query != null)
                    {
                        retainedCoverage["relationshipScope"] = query["relationshipScope"]?.DeepClone() ?? new JObject { ["scope"] = "selectedGraphQuery", ["projectCompleteness"] = "notEstablished" };
                        retainedCoverage["relationshipScope"]["seedNodeId"] = query["seedNodeId"]?.DeepClone();
                        retainedCoverage["relationshipScope"]["limits"] = query["limits"]?.DeepClone();
                        bool partial = query.Value<bool?>("truncated") == true || query["relationshipScope"]?.Value<int?>("retainedEdges") != edges.Count
                            || query["relationshipScope"]?.Value<string>("direction") != "both" || (query["relationshipScope"]?["kinds"] as JArray)?.Count > 0;
                        if (partial && retainedCoverage.Value<string>("status") == "complete") retainedCoverage["status"] = "partial";
                    }
                    all.Add(retainedCoverage);
                }
            }
            RememberObservation(id, copy);
        }

        private void RememberObservation(string id, JObject copy)
        {
            copy["lastObservationAtUtc"] = _clock().ToString("O");
            copy["contentFreshness"] = "observationsCapturedSeparately";
            var warnings = (JArray)copy["warnings"];
            const string mixed = "Metadata and content observations were captured separately; this is not an atomic project revision.";
            if (!warnings.Values<string>().Contains(mixed)) warnings.Add(mixed);
            var replacement = new Snapshot(copy);
            copy["usage"]["returnedEdges"] = replacement.Edges.Length;
            AccountDocument(copy);
            // Publish atomically only after all bounds and endpoint references pass validation.
            _snapshots[id] = replacement;
        }

        private static void AccountDocument(JObject document)
        {
            var usage = (JObject)document["usage"];
            long maximum = document["limits"]?.Value<long?>("maxBytes") ?? MaximumBytes;
            if (maximum < 1 || maximum > MaximumBytes) throw Error("InvalidRequest", "Snapshot maxBytes must be positive and no greater than 32 MiB.");
            usage["snapshotBytes"] = 0;
            long bytes;
            // The byte counter is part of the JSON; account for its decimal digits too.
            do
            {
                bytes = Encoding.UTF8.GetByteCount(document.ToString(Formatting.None));
                if (bytes > maximum) throw Error("ModelCapacity", "Snapshot exceeds its requested byte budget; capture a narrower scope.");
                long previous = usage.Value<long>("snapshotBytes"); usage["snapshotBytes"] = bytes;
                if (previous == bytes) break;
            } while (true);
        }

        internal JObject Children(string id, string parentId, int maxItems = 100, string cursor = null)
        {
            var snapshot = Find(id);
            if (parentId != null && !snapshot.Nodes.ContainsKey(parentId)) throw Error("SelectionNotFound", "Parent is absent from this snapshot; repair selection.");
            string[] children;
            if (!snapshot.Children.TryGetValue(parentId ?? "", out children)) children = new string[0];
            return Page(snapshot, children, "children:" + (parentId ?? ""), maxItems, cursor, false);
        }

        internal JObject Search(string id, string text, string kind, string plcPath, string scopePath, int maxItems = 100, string cursor = null)
        {
            if ((text ?? "").Length > 256 || (kind ?? "").Length > 80 || (plcPath ?? "").Length > 4096 || (scopePath ?? "").Length > 4096)
                throw Error("InvalidRequest", "Search selector exceeds its bounded length.");
            var snapshot = Find(id);
            string query = "search:" + new JArray(text, kind, plcPath, scopePath).ToString(Formatting.None);
            string[] matches;
            if (!snapshot.Views.TryGetValue(query, out matches))
            {
                matches = snapshot.Ordered.Where(node => (string.IsNullOrEmpty(kind) || (string)node["kind"] == kind)
                    && (string.IsNullOrEmpty(plcPath) || (string)node["plcPath"] == plcPath)
                    && (string.IsNullOrEmpty(scopePath) || (string)node["scopePath"] == scopePath)
                    && (string.IsNullOrEmpty(text) || ((string)node["name"]).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
                        || ((string)node["objectPath"] ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0))
                    .Select(n => (string)n["nodeId"]).ToArray();
                if (snapshot.Views.Count >= 16) snapshot.Views.Clear();
                snapshot.Views[query] = matches;
            }
            return Page(snapshot, matches, query, maxItems, cursor, true);
        }

        internal JObject Reference(string id, string nodeId)
        {
            var snapshot = Find(id);
            JObject node;
            if (nodeId == null || !snapshot.Nodes.TryGetValue(nodeId, out node)) throw Error("SelectionNotFound", "Node is absent from this snapshot.");
            if ((string)node["origin"] != "native") throw Error("VirtualSelection", "A display folder has no engineering object reference.");
            return new JObject { ["schemaVersion"] = "project-model.v1", ["snapshotId"] = id,
                ["sessionGeneration"] = snapshot.Document["sessionGeneration"], ["projectPath"] = snapshot.Document["project"]["projectPath"],
                ["nodeId"] = nodeId, ["objectPath"] = node["objectPath"] };
        }

        internal JObject ResolveReference(JObject reference, string currentSessionGeneration)
        {
            if (reference == null || (string)reference["schemaVersion"] != "project-model.v1") throw Error("InvalidSelection", "A versioned exact reference is required.");
            if (string.IsNullOrEmpty(currentSessionGeneration) || (string)reference["sessionGeneration"] != currentSessionGeneration)
                throw Error("SessionMismatch", "Selection belongs to a different worker session; recapture and select the exact object again.");
            var snapshot = Find((string)reference["snapshotId"], currentSessionGeneration);
            JObject node;
            if ((string)reference["projectPath"] != (string)snapshot.Document["project"]["projectPath"]
                || reference["nodeId"] == null || !snapshot.Nodes.TryGetValue((string)reference["nodeId"], out node))
                throw Error("SelectionNotFound", "Project copy or selected node does not match this snapshot; repair selection.");
            if ((string)node["origin"] != "native") throw Error("VirtualSelection", "Display folders cannot be engineering targets.");
            if ((string)reference["objectPath"] != (string)node["objectPath"])
                throw Error("SelectionNotFound", "Canonical path changed or differs; same-named objects are never substituted.");
            return (JObject)node.DeepClone();
        }

        internal JObject Relationships(string id, string nodeId, string[] kinds, string direction = "both", int depth = 1, int maxNodes = 100, int maxEdges = 200)
            => Graph(Find(id), nodeId, kinds, direction, depth, maxNodes, maxEdges);

        internal static JObject QueryRelationships(JObject document, string nodeId, string[] kinds, string direction = "both", int depth = 1, int maxNodes = 100, int maxEdges = 200)
            => Graph(new Snapshot((JObject)document.DeepClone()), nodeId, kinds, direction, depth, maxNodes, maxEdges);

        private static JObject Graph(Snapshot snapshot, string nodeId, string[] kinds, string direction, int depth, int maxNodes, int maxEdges)
        {
            if (depth < 1 || depth > 5 || maxNodes < 1 || maxNodes > 500 || maxEdges < 1 || maxEdges > 1000)
                throw Error("InvalidRequest", "Graph limits: depth 1..5, nodes 1..500, edges 1..1000.");
            if (direction != "both" && direction != "in" && direction != "out") throw Error("InvalidRequest", "direction must be both, in or out.");
            if (nodeId == null || !snapshot.Nodes.ContainsKey(nodeId)) throw Error("SelectionNotFound", "Graph seed is absent from this snapshot.");
            if (kinds != null && (kinds.Length > 32 || kinds.Any(k => k == null || k.Length > 80))) throw Error("InvalidRequest", "Relationship kind filter is too large.");
            var allowed = kinds == null || kinds.Length == 0 ? null : new HashSet<string>(kinds, StringComparer.Ordinal);
            var selected = new HashSet<string>(StringComparer.Ordinal) { nodeId };
            var selectedEdges = new HashSet<string>(StringComparer.Ordinal);
            var frontier = new List<string> { nodeId };
            var edges = new JArray(); var unresolvedEdges = new JArray();
            const int adapterReserve = 64 * 1024;
            int responseBudget = MaximumPageBytes - adapterReserve;
            const string suggestion = "Select an adjacent node and expand one step, or narrow relationship kinds.";
            var result = new JObject { ["schemaVersion"] = "project-model.v1", ["snapshotId"] = snapshot.Document["snapshotId"],
                ["seedNodeId"] = nodeId, ["nodes"] = new JArray(snapshot.Nodes[nodeId].DeepClone()), ["edges"] = edges, ["unresolved"] = unresolvedEdges,
                ["limits"] = new JObject { ["depth"] = depth, ["maxNodes"] = maxNodes, ["maxEdges"] = maxEdges, ["maxBytes"] = MaximumPageBytes,
                    ["baseResponseMaxBytes"] = responseBudget, ["reservedForAdapterBytes"] = adapterReserve },
                ["truncated"] = false, ["continuationAvailable"] = false, ["narrowScopeSuggestion"] = suggestion,
                ["coverage"] = snapshot.Document["coverage"].DeepClone(), ["freshness"] = "snapshotOnly", ["payloadBytes"] = 0 };
            var reasons = new HashSet<string>(StringComparer.Ordinal);
            var scope = new JObject { ["scope"] = "retainedSnapshotEdges", ["status"] = "complete", ["projectCompleteness"] = "notEstablished",
                ["retainedEdges"] = snapshot.Edges.Length, ["returnedEdges"] = 0, ["direction"] = direction == "out" ? "outgoing" : direction == "in" ? "incoming" : "both",
                ["kinds"] = new JArray(kinds ?? new string[0]), ["stopReasons"] = new JArray() };
            result["relationshipScope"] = scope;
            bool truncated = false;
            // Count the complete envelope/coverage and both copies of unresolved
            // edges. Reserve room for the decimal byte counter and native adapter metadata.
            int bytes = Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) + 512;
            if (bytes > responseBudget) throw Error("ModelCapacity", "Graph seed and coverage exceed the response budget; capture a narrower scope.");
            for (int level = 0; level < depth && frontier.Count > 0; level++)
            {
                var next = new List<string>();
                foreach (string current in frontier)
                {
                    JObject[] adjacent;
                    if (!snapshot.Adjacency.TryGetValue(current, out adjacent)) continue;
                    foreach (var edge in adjacent)
                    {
                        string source = (string)(edge["source"] as JObject)?["nodeId"], target = (string)(edge["target"] as JObject)?["nodeId"];
                        if (allowed != null && !allowed.Contains((string)edge["kind"])) continue;
                        if (direction == "out" && source != current || direction == "in" && target != current) continue;
                        string edgeId = (string)edge["edgeId"];
                        if (selectedEdges.Contains(edgeId)) continue;
                        string other = source == current ? target : source;
                        bool addNode = other != null && !selected.Contains(other);
                        bool unresolved = edge["unresolved"] != null && edge["unresolved"].Type != JTokenType.Null;
                        int itemBytes = Encoding.UTF8.GetByteCount(edge.ToString(Formatting.None)) * (unresolved ? 2 : 1)
                            + (addNode ? Encoding.UTF8.GetByteCount(snapshot.Nodes[other].ToString(Formatting.None)) : 0) + 4;
                        if (edges.Count >= maxEdges) { truncated = true; reasons.Add("edges"); continue; }
                        if (addNode && selected.Count >= maxNodes) { truncated = true; reasons.Add("nodes"); continue; }
                        if (bytes + itemBytes > responseBudget) { truncated = true; reasons.Add("bytes"); continue; }
                        selectedEdges.Add(edgeId); edges.Add(edge.DeepClone()); bytes += itemBytes;
                        if (unresolved) unresolvedEdges.Add(edge.DeepClone());
                        if (addNode) { selected.Add(other); next.Add(other); }
                    }
                }
                frontier = next;
            }
            // Depth is itself a bound: expose available further expansion, including cycles only when unseen edges exist.
            bool continuation = frontier.Any(id => snapshot.Adjacency.ContainsKey(id) && snapshot.Adjacency[id].Any(e => !selectedEdges.Contains((string)e["edgeId"])
                && (allowed == null || allowed.Contains((string)e["kind"]))
                && (direction == "both" || direction == "out" && (string)(e["source"] as JObject)?["nodeId"] == id || direction == "in" && (string)(e["target"] as JObject)?["nodeId"] == id)));
            result["nodes"] = new JArray(selected.OrderBy(x => x, StringComparer.Ordinal).Select(x => snapshot.Nodes[x].DeepClone()));
            result["truncated"] = truncated || continuation;
            result["continuationAvailable"] = truncated || continuation;
            if (continuation) reasons.Add("depth");
            scope["status"] = truncated || continuation ? "truncated" : "complete";
            scope["returnedEdges"] = edges.Count;
            scope["stopReasons"] = new JArray(reasons.OrderBy(x => x, StringComparer.Ordinal));
            result["narrowScopeSuggestion"] = truncated || continuation ? suggestion : null;
            int serialized;
            do { serialized = Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)); if (result.Value<int>("payloadBytes") == serialized) break; result["payloadBytes"] = serialized; } while (true);
            if (serialized > responseBudget) throw Error("ModelCapacity", "Graph response exceeds its serialized byte budget.");
            return result;
        }

        private JObject Page(Snapshot snapshot, string[] matches, string query, int maxItems, string cursor, bool breadcrumbs)
        {
            if (maxItems < 1 || maxItems > 200) throw Error("InvalidRequest", "Page size must be 1..200.");
            string binding = Hash(query + "|pageSize=" + maxItems.ToString(CultureInfo.InvariantCulture));
            int offset = cursor == null ? 0 : Decode(cursor, snapshot, binding);
            if (offset < 0 || offset > matches.Length) throw Error("InvalidCursor", "Cursor position is outside this query.");
            var nodes = new JArray(); int bytes = 0;
            for (int index = offset; index < matches.Length && nodes.Count < maxItems; index++)
            {
                var node = (JObject)snapshot.Nodes[matches[index]].DeepClone();
                if (breadcrumbs) node["ancestors"] = Ancestors(snapshot, node);
                int size = Encoding.UTF8.GetByteCount(node.ToString(Formatting.None));
                if (bytes + size > MaximumPageBytes) { if (nodes.Count == 0) throw Error("ModelCapacity", "One search result with ancestry exceeds page budget."); break; }
                nodes.Add(node); bytes += size;
            }
            int next = offset + nodes.Count;
            return new JObject { ["schemaVersion"] = "project-model.v1", ["snapshotId"] = snapshot.Document["snapshotId"], ["sessionGeneration"] = snapshot.Document["sessionGeneration"],
                ["nodes"] = nodes, ["offset"] = offset, ["totalMatches"] = matches.Length, ["nextCursor"] = next < matches.Length ? Encode(snapshot, binding, next) : null,
                ["rowPayloadBytes"] = bytes, ["freshness"] = "snapshotOnly", ["coverage"] = snapshot.Document["coverage"].DeepClone() };
        }

        private static JArray Ancestors(Snapshot snapshot, JObject node)
        {
            var result = new List<JObject>(); string parent = (string)node["parentId"];
            while (parent != null)
            {
                var ancestor = snapshot.Nodes[parent];
                result.Add(new JObject { ["nodeId"] = parent, ["name"] = ancestor["name"], ["origin"] = ancestor["origin"], ["objectPath"] = ancestor["objectPath"] });
                parent = (string)ancestor["parentId"];
            }
            result.Reverse(); return new JArray(result);
        }

        private Snapshot Find(string id, string generation = null)
        {
            Snapshot snapshot;
            if (id == null || !_snapshots.TryGetValue(id, out snapshot)) throw Error("ModelNotFound", "Snapshot is absent or evicted; recapture in this session.");
            if (DateTime.Parse((string)snapshot.Document["expiresAtUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) <= _clock())
            { Release(id); throw Error("ModelExpired", "Snapshot expired; recapture before selecting an object."); }
            if (generation != null && (string)snapshot.Document["sessionGeneration"] != generation) throw Error("SessionMismatch", "Worker session changed; recapture and repair selection.");
            if (snapshot.InvalidatedBy != null) throw Error("ModelInvalidated", "Snapshot invalidated by " + snapshot.InvalidatedBy + "; recapture and repair selection.");
            return snapshot;
        }

        private string Encode(Snapshot snapshot, string query, int offset)
        {
            string payload = (string)snapshot.Document["snapshotId"] + ":" + query + ":" + offset.ToString(CultureInfo.InvariantCulture);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload + ":" + Sign(payload)));
        }

        private int Decode(string cursor, Snapshot snapshot, string query)
        {
            try
            {
                if (cursor.Length > 512) throw new FormatException();
                string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
                if (parts.Length != 4 || parts[0] != (string)snapshot.Document["snapshotId"] || parts[1] != query) throw new FormatException();
                string expected = Sign(string.Join(":", parts.Take(3)));
                int difference = expected.Length ^ parts[3].Length;
                for (int i = 0; i < Math.Min(expected.Length, parts[3].Length); i++) difference |= expected[i] ^ parts[3][i];
                int offset;
                if (difference != 0 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out offset)) throw new FormatException();
                return offset;
            }
            catch (Exception error) when (error is FormatException || error is ArgumentException)
            { throw Error("InvalidCursor", "Cursor belongs to another snapshot, parent, filter, page size or worker."); }
        }

        private string Sign(string value) { using (var hmac = new HMACSHA256(_key)) return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))); }
        internal static string Hash(string value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
        private static ModelException Error(string code, string message) => new ModelException(code, message);
        internal sealed class ModelException : Exception { internal string Code { get; } internal ModelException(string code, string message) : base(message) { Code = code; } }

        private sealed class Snapshot
        {
            internal readonly JObject Document;
            internal readonly Dictionary<string, JObject> Nodes = new Dictionary<string, JObject>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string[]> Children = new Dictionary<string, string[]>(StringComparer.Ordinal);
            internal readonly Dictionary<string, JObject[]> Adjacency = new Dictionary<string, JObject[]>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string[]> Views = new Dictionary<string, string[]>(StringComparer.Ordinal);
            internal readonly JObject[] Ordered, Edges;
            internal string InvalidatedBy;
            internal Snapshot(JObject document)
            {
                Document = document;
                if (string.IsNullOrEmpty((string)document["sessionGeneration"]) || document["project"] is not JObject || string.IsNullOrEmpty((string)document["project"]["projectPath"]))
                    throw Error("InvalidProjectModel", "Snapshot requires worker generation and exact project copy identity.");
                if (document["nodes"] is not JArray array || array.Count > MaximumNodes) throw Error("ModelCapacity", "Snapshot nodes must be an array of at most 50,000 nodes.");
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in array)
                {
                    var node = token as JObject;
                    string id = (string)node?["nodeId"], origin = (string)node?["origin"], path = (string)node?["objectPath"];
                    if (node == null || string.IsNullOrEmpty(id) || id.Length > 4096 || node["name"]?.Type != JTokenType.String || string.IsNullOrEmpty((string)node["kind"]))
                        throw Error("InvalidProjectModel", "Every node requires nodeId, name and kind.");
                    if (Nodes.ContainsKey(id)) throw Error("AmbiguousModelIdentity", "Duplicate node identity: " + id);
                    if (origin != "native" && origin != "virtual") throw Error("InvalidProjectModel", "Node origin must be native or virtual.");
                    if (origin == "native" && (string.IsNullOrEmpty(path) || !paths.Add(path))) throw Error("AmbiguousModelIdentity", "Native paths must be present and unique.");
                    if (origin == "virtual" && path != null) throw Error("InvalidProjectModel", "Virtual folders cannot carry an engineering objectPath.");
                    if (Encoding.UTF8.GetByteCount(node.ToString(Formatting.None)) > 65536) throw Error("ModelCapacity", "One node exceeds 64 KiB.");
                    Nodes.Add(id, node);
                }
                foreach (var node in Nodes.Values)
                {
                    string parent = (string)node["parentId"]; var seen = new HashSet<string>(StringComparer.Ordinal) { (string)node["nodeId"] }; int depth = 0;
                    while (parent != null)
                    {
                        JObject ancestor;
                        if (!Nodes.TryGetValue(parent, out ancestor)) throw Error("InvalidModelAncestry", "Parent must be included before publication, even for partial captures.");
                        if (!seen.Add(parent)) throw Error("InvalidModelAncestry", "Containment cycle detected.");
                        if (++depth > 64) throw Error("ModelCapacity", "Containment exceeds depth 64.");
                        parent = (string)ancestor["parentId"];
                    }
                }
                Ordered = Nodes.Values.OrderBy(n => (string)n["name"], StringComparer.OrdinalIgnoreCase).ThenBy(n => (string)n["nodeId"], StringComparer.Ordinal).ToArray();
                foreach (var group in Ordered.GroupBy(n => (string)n["parentId"] ?? "", StringComparer.Ordinal)) Children[group.Key] = group.Select(n => (string)n["nodeId"]).ToArray();
                if (!Children.ContainsKey("")) Children[""] = new string[0];
                var edges = document["edges"] as JArray;
                if (edges == null || edges.Count > MaximumNodes) throw Error("ModelCapacity", "Snapshot edges must be an array of at most 50,000 edges.");
                Edges = edges.OfType<JObject>().ToArray();
                if (Edges.Length != edges.Count) throw Error("InvalidProjectModel", "Every edge must be an object.");
                var edgeIds = new HashSet<string>(StringComparer.Ordinal); var adjacency = new Dictionary<string, List<JObject>>(StringComparer.Ordinal);
                foreach (var edge in Edges)
                {
                    if (string.IsNullOrEmpty((string)edge["edgeId"]) || !edgeIds.Add((string)edge["edgeId"]) || string.IsNullOrEmpty((string)edge["kind"]) || edge["evidence"] is not JObject)
                        throw Error("InvalidProjectModel", "Edges require unique IDs, kinds and evidence.");
                    if (Encoding.UTF8.GetByteCount(edge.ToString(Formatting.None)) > 65536) throw Error("ModelCapacity", "One edge exceeds 64 KiB.");
                    foreach (string field in new[] { "source", "target" })
                    {
                        var endpoint = edge[field];
                        if (endpoint == null || endpoint.Type == JTokenType.Null)
                        {
                            if (edge["unresolved"] == null || edge["unresolved"].Type == JTokenType.Null) throw Error("InvalidProjectModel", "Missing endpoints require explicit unresolved evidence.");
                            continue;
                        }
                        string id = (string)endpoint["nodeId"]; JObject node;
                        if (id == null || !Nodes.TryGetValue(id, out node) || (string)endpoint["objectPath"] != (string)node["objectPath"])
                            throw Error("InvalidProjectModel", "Edge endpoint does not match its exact captured node.");
                        List<JObject> adjacent;
                        if (!adjacency.TryGetValue(id, out adjacent)) adjacency[id] = adjacent = new List<JObject>();
                        if (!adjacent.Contains(edge)) adjacent.Add(edge);
                    }
                }
                foreach (var entry in adjacency) Adjacency[entry.Key] = entry.Value.OrderBy(e => (string)e["edgeId"], StringComparer.Ordinal).ToArray();
            }
        }
    }
}
