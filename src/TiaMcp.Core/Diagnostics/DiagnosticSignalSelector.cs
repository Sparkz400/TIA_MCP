using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Bounded dependency selection with evidence. Never manufactures an online address.</summary>
internal sealed class DiagnosticSignalSelector(DiagnosticEvidence source, JsonObject instance, JsonArray hypotheses)
{
    private readonly JsonArray gaps = new();
    private readonly JsonArray output = new();
    private readonly Dictionary<string, JsonObject> retained = new(StringComparer.Ordinal);
    private readonly HashSet<string> visited = new(StringComparer.Ordinal);
    private int edgeVisits;
    private int resolutionVisits;
    private bool truncated;

    internal JsonObject OperandStorage(string[] members)
    {
        var rows = new JsonArray(); string block = D.Text(instance, "blockPath");
        var calls = D.Rows(instance["callSites"]);
        if (calls.Count > 64 || calls.Sum(c => D.Rows(c?["parameterBindings"]).Count) > 256)
        { truncated = true; Gap("Operand call/parameter budget exceeded", new JsonObject { ["callSites"] = calls.Count }); }
        void Add(JsonObject row, string origin, JsonObject? call = null, JsonObject? binding = null)
        {
            if (rows.Count == 64) { truncated = true; return; }
            row["origin"] = origin;
            if (call is not null) row["callSite"] = call.DeepClone();
            if (binding is not null) row["parameterBinding"] = binding.DeepClone();
            rows.Add(row);
        }
        foreach (var row in truncated ? [] : Resolve(block, instance, new("LocalVariable", members), new HashSet<string>(), 0))
            Add(row, "selectedMemberOrAlias");
        foreach (var call in calls.OfType<JsonObject>().TakeWhile(_ => !truncated))
        foreach (var binding in D.Rows(call["parameterBindings"]).OfType<JsonObject>().Where(b => D.Text(b, "parameter") == members[0]))
        {
            if (binding["actual"] is null) { Gap("Unresolved call parameter", binding); continue; }
            var actual = Symbol(binding["actual"]!);
            actual = actual with { Components = [.. actual.Components, .. members.Skip(1)] };
            if (actual.Components.Length > 16) { Gap("Operand member depth exceeded", binding); continue; }
            foreach (var row in Resolve(D.Text(call, "objectPath"), CallerContext(call), actual, new HashSet<string>(), 0))
                Add(row, "actualParameter", call, binding);
        }
        return new JsonObject { ["candidates"] = rows, ["gaps"] = gaps.DeepClone(), ["truncated"] = truncated,
            ["maxCandidates"] = 64, ["maxCallSites"] = 64, ["maxParameterBindings"] = 256,
            ["maxResolutionVisits"] = 1000, ["runtimeCallContextVerified"] = false };
    }

    internal JsonObject Select(JsonObject request)
    {
        int budget = request["maxSignals"]?.GetValue<int>() ?? 16, depth = request["maxDepth"]?.GetValue<int>() ?? 3;
        if (budget is < 1 or > 64 || depth is < 0 or > 8) throw new BridgeException("InvalidRequest", "Signal budgets: maxSignals 1..64 and maxDepth 0..8.");
        if (request["seeds"] is not JsonArray seeds || seeds.Count is < 1 or > 16) throw new BridgeException("InvalidRequest", "autoSignals requires 1..16 exact seed symbols; no inferred equipment address.");
        string block = D.Text(instance, "blockPath");
        var queue = new Queue<(DiagnosticCodeReader.Symbol Symbol, int Depth, string Reason, JsonArray Evidence, string Caller)>();
        foreach (var seed in seeds)
        {
            string scope = D.Required(seed!, "scope", 40);
            if (scope is not ("LocalVariable" or "GlobalVariable")) throw new BridgeException("InvalidRequest", "Seed scope must be LocalVariable or GlobalVariable.");
            var symbol = new DiagnosticCodeReader.Symbol(scope, D.Path(seed?["components"]));
            queue.Enqueue((symbol, 0, D.Required(seed!, "reason", 2000), new JsonArray(), block));
        }
        var byCaller = new Dictionary<string, JsonObject[]>(StringComparer.Ordinal); int graphReferences = 0;
        foreach (var path in source.Documents.Keys.Where(p => source.Nodes[p]["plcPath"]?.ToJsonString() == source.Nodes[block]["plcPath"]?.ToJsonString()).OrderBy(p => p == block ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                var rows = DiagnosticCodeReader.Dependencies(source, path).OfType<JsonObject>().ToArray();
                graphReferences += rows.Sum(r => D.Rows(r["inputs"]).Count + 1);
                if (graphReferences > 20000) { truncated = true; Gap("Total dependency reference budget exceeded", source.Nodes[path]); break; }
                byCaller[path] = rows;
            }
            catch (BridgeException e) when (e.Code == "TraversalLimit") { truncated = true; Gap(e.Message, source.Nodes[path]); }
        }
        while (queue.TryDequeue(out var item))
        {
            if (!visited.Add(item.Caller + ":" + item.Symbol.Key)) continue;
            if (visited.Count > 256) { truncated = true; break; }
            var resolved = Resolve(item.Caller, item.Caller == block ? instance : null, item.Symbol, new HashSet<string>(StringComparer.Ordinal), 0);
            foreach (var row in resolved)
            {
                string key = D.Text(row, "storagePath") + ":" + row["memberPath"]!.ToJsonString();
                var trail = (JsonArray)item.Evidence.DeepClone();
                foreach (var e in D.Rows(row["bindingEvidence"])) trail.Add(e!.DeepClone());
                if (retained.TryGetValue(key, out var existing))
                {
                    if (D.Rows(existing["selectionEvidence"]).Count < 32) D.Rows(existing["selectionEvidence"]).Add(new JsonObject { ["reason"] = item.Reason, ["evidence"] = trail });
                    continue;
                }
                if (output.Count == budget) { truncated = true; continue; }
                var signal = new JsonObject { ["id"] = "auto" + (output.Count + 1), ["role"] = item.Depth == 0 ? "symptomSeed" : "dependencyOrBinding",
                    ["storagePath"] = row["storagePath"]!.DeepClone(), ["memberPath"] = row["memberPath"]!.DeepClone(),
                    ["reason"] = item.Reason, ["hypothesisIds"] = new JsonArray(hypotheses.Select(h => h!["id"]!.DeepClone()).ToArray()),
                    ["selectionOrigin"] = "boundedDependencyEvidence", ["selectionEvidence"] = new JsonArray(new JsonObject { ["reason"] = item.Reason, ["evidence"] = trail }) };
                retained.Add(key, signal); output.Add(signal);
            }
            foreach (var pair in byCaller.Where(p => p.Key == item.Caller || item.Symbol.Scope == "GlobalVariable" && D.Text(source.Nodes[p.Key], "scopePath") == D.Text(source.Nodes[item.Caller], "scopePath")))
            foreach (var dependency in pair.Value)
            {
                if (++edgeVisits > 5000) { truncated = true; break; }
                if (!Same(item.Symbol, dependency["output"])) continue;
                if (item.Depth == depth) { truncated = true; continue; }
                foreach (var input in D.Rows(dependency["inputs"]))
                {
                    var evidence = (JsonArray)item.Evidence.DeepClone(); evidence.Add(dependency["evidence"]!.DeepClone());
                    queue.Enqueue((Symbol(input!), item.Depth + 1, "Captured " + D.Text(dependency, "kind") + " feeding seed/dependency " + string.Join(".", item.Symbol.Components), evidence, pair.Key));
                }
            }
            // Formal input bindings retain actual storage in every captured call context. IN_OUT is
            // substituted by actual storage in Resolve; ordinary IN/OUT keep both sides observable.
            if (item.Symbol.Scope == "LocalVariable" && item.Caller == block)
                foreach (var call in D.Rows(instance["callSites"]).OfType<JsonObject>())
                foreach (var binding in D.Rows(call["parameterBindings"]).OfType<JsonObject>().Where(b => D.Text(b, "parameter") == item.Symbol.Components[0]))
                {
                    if (binding["actual"] is null) { Gap("Unresolved call parameter", binding); continue; }
                    var actual = Symbol(binding["actual"]!);
                    actual = actual with { Components = [.. actual.Components, .. item.Symbol.Components.Skip(1)] };
                    if (actual.Scope == "GlobalVariable" && item.Depth < depth)
                        queue.Enqueue((actual, item.Depth + 1, "Actual parameter storage and competing writers for " + item.Symbol.Components[0], new JsonArray(binding.DeepClone()), D.Text(call, "objectPath")));
                    var context = CallerContext(call);
                    if (actual.Scope == "LocalVariable" && context is null) { Gap("Caller instance context absent", call); continue; }
                    foreach (var row in Resolve(D.Text(call, "objectPath"), context, actual, new HashSet<string>(), 0))
                    {
                        string key = D.Text(row, "storagePath") + ":" + row["memberPath"]!.ToJsonString();
                        if (retained.ContainsKey(key)) continue;
                        if (output.Count == budget) { truncated = true; continue; }
                        var signal = new JsonObject { ["id"] = "auto" + (output.Count + 1), ["role"] = "actualParameter",
                            ["storagePath"] = row["storagePath"]!.DeepClone(), ["memberPath"] = row["memberPath"]!.DeepClone(),
                            ["reason"] = "Explicit captured argument for " + item.Symbol.Components[0] + "; retain actual storage alongside instance member.",
                            ["hypothesisIds"] = new JsonArray(hypotheses.Select(h => h!["id"]!.DeepClone()).ToArray()), ["selectionOrigin"] = "boundedDependencyEvidence",
                            ["selectionEvidence"] = new JsonArray(binding.DeepClone(), call.DeepClone()) };
                        retained.Add(key, signal); output.Add(signal);
                    }
                }
            if (edgeVisits > 5000) break;
        }
        return new JsonObject { ["signals"] = output, ["gaps"] = gaps, ["truncated"] = truncated,
            ["visitedSymbols"] = visited.Count, ["edgeVisits"] = edgeVisits, ["maxSignals"] = budget,
            ["complete"] = false, ["semantics"] = "Conservative dependency supersets and explicit simple parameter bindings; no causality, alias completeness or runtime invocation proof." };
    }

    private List<JsonObject> Resolve(string caller, JsonObject? context, DiagnosticCodeReader.Symbol symbol, HashSet<string> resolving, int depth)
    {
        var result = new List<JsonObject>(); string recursion = caller + ":" + symbol.Key;
        if (++resolutionVisits > 1000) { truncated = true; Gap("Storage resolution visit budget exceeded", symbol.Json()); return result; }
        if (depth > 8 || !resolving.Add(recursion)) { Gap("Recursive/over-depth parameter binding", symbol.Json()); return result; }
        try
        {
            if (symbol.Scope == "GlobalVariable")
            {
                var names = source.Nodes.Values.Where(n => D.Text(n, "scopePath") == D.Text(source.Nodes[caller], "scopePath")
                    && D.Text(n, "plcPath") == D.Text(instance, "plcPath") && DiagnosticEvidence.QualifiedName(n) == symbol.Components[0]
                    && D.Text(n, "kind") is "block" or "tag").ToArray();
                if (names.Length != 1) { Gap("Ambiguous or unavailable global symbol", symbol.Json()); return result; }
                var target = names[0]; bool tag = D.Text(target, "kind") == "tag";
                if (tag && symbol.Components.Length != 1 || !tag && symbol.Components.Length < 2) { Gap("Structured/tag access needs explicit leaf resolution", symbol.Json()); return result; }
                result.Add(new JsonObject { ["storagePath"] = target["objectPath"]!.DeepClone(), ["memberPath"] = D.Strings(tag ? [D.Text(target, "name")] : symbol.Components.Skip(1)), ["bindingEvidence"] = new JsonArray() });
                return result;
            }
            if (context is null) { Gap("Local symbol has no exact caller instance", symbol.Json()); return result; }
            var declaration = source.Declaration(caller, symbol.Components);
            if (declaration is null) { Gap("Local declaration unavailable", symbol.Json()); return result; }
            var sections = declaration.Ancestors().Where(e => e.Name.LocalName == "Section").Select(e => (string?)e.Attribute("Name")).ToArray();
            string section = sections.Contains("InOut") ? "InOut" : sections.Contains("Temp") ? "Temp" : sections.Contains("Constant") ? "Constant" : sections.FirstOrDefault() ?? "";
            if (section is "Temp" or "Constant") { Gap("Local TEMP/constant is not stable trace storage", source.Evidence(caller, declaration)); return result; }
            if (section == "InOut")
            {
                foreach (var call in D.Rows(context["callSites"]).OfType<JsonObject>())
                foreach (var binding in D.Rows(call["parameterBindings"]).OfType<JsonObject>().Where(b => D.Text(b, "parameter") == symbol.Components[0]))
                {
                    if (binding["actual"] is null) { Gap("IN_OUT actual is unresolved", binding); continue; }
                    var actual = Symbol(binding["actual"]!); actual = actual with { Components = [.. actual.Components, .. symbol.Components.Skip(1)] };
                    foreach (var row in Resolve(D.Text(call, "objectPath"), CallerContext(call), actual, resolving, depth + 1))
                    { D.Rows(row["bindingEvidence"]).Add(binding.DeepClone()); result.Add(row); }
                }
                if (result.Count == 0) Gap("IN_OUT has no resolved actual storage", symbol.Json());
                return result;
            }
            result.Add(new JsonObject { ["storagePath"] = context["dbPath"]!.DeepClone(),
                ["memberPath"] = D.Strings(D.Rows(context["memberPath"]).Select(n => n!.GetValue<string>()).Concat(symbol.Components)),
                ["bindingEvidence"] = new JsonArray(source.Evidence(caller, declaration)) });
            return result;
        }
        finally { resolving.Remove(recursion); }
    }
    private JsonObject? CallerContext(JsonObject call)
    {
        if (call["callerDbPath"] is null) return null;
        var evidenceCalls = D.Rows(instance["evidence"]).OfType<JsonObject>().Where(c => D.Text(c, "calleePath") == D.Text(call, "objectPath"));
        return new JsonObject { ["dbPath"] = call["callerDbPath"]!.DeepClone(), ["memberPath"] = call["callerMemberPath"]!.DeepClone(),
            ["callSites"] = new JsonArray(evidenceCalls.Select(c => (JsonNode?)c.DeepClone()).ToArray()) };
    }
    private void Gap(string reason, JsonNode evidence) { if (gaps.Count < 100) gaps.Add(new JsonObject { ["reason"] = reason, ["evidence"] = evidence.DeepClone() }); else truncated = true; }
    private static bool Same(DiagnosticCodeReader.Symbol symbol, JsonNode? json) => json is not null && symbol.Key == Symbol(json).Key;
    private static DiagnosticCodeReader.Symbol Symbol(JsonNode json) => new(D.Text(json, "scope"), D.Path(json["components"]));
}
