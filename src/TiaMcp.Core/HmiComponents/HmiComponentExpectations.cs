using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Model;
using static TiaMcp.Core.HmiComponents.HmiComponentReview;

namespace TiaMcp.Core.HmiComponents;

/// <summary>Derive supported expected documents from original current files, independently of proposed/observed bytes.</summary>
internal static class HmiComponentExpectations
{
    internal static void Add(JsonObject report, JsonObject request, Func<JsonObject, XDocument> readXml)
    {
        var files = Files(request).ToDictionary(f => Text(f, "id"), StringComparer.Ordinal);
        var outputs = report["outputs"]!.AsArray().OfType<JsonObject>().ToDictionary(o => Text(o, "id"), StringComparer.Ordinal);
        var observations = request["observations"]!.AsArray().OfType<JsonObject>().ToDictionary(o => Text(o, "outputId"), StringComparer.Ordinal);
        var selected = new HashSet<(string Output, string File)>();
        var checks = new JsonArray();
        var expectations = Array(request, "expectations", 256);
        if (expectations.Count == 0 && (string?)request["schemaVersion"] is not ("hmi-component-review-request.v5" or "hmi-component-review-request.v6" or "hmi-component-review-request.v7")) throw Error("V4 requires at least one explicit expected-effect rule.");
        foreach (var value in expectations)
        {
            var rule = Object(value); Fields(rule, "outputId sourceFileId profile ownerId expectedValue replacementValue");
            string id = Text(rule, "outputId", 128), sourceId = Text(rule, "sourceFileId", 128);
            if (Text(rule, "profile", 64) != "classic-screen-backcolor.v1") throw Error("Unsupported expected-effect profile.");
            if (!outputs.TryGetValue(id, out var output) || !files.TryGetValue(sourceId, out var source)) throw Error("Select an existing output and original file ID.");
            var selection = Object(output["selection"]);
            if ((string?)request["destination"]?["family"] != "classic" || (string?)selection["kind"] != "hmiScreen" ||
                (string?)selection["action"] != "update" || output["capturedObject"] is not JsonObject ||
                !selection["current"]!.AsArray().Any(f => (string?)f == sourceId))
                throw Error("Classic screen expectations require an update of a captured screen and an exact current file selection.");
            string name = Text(source, "fileName", 256), ownerId = Text(rule, "ownerId", 128);
            if (!selected.Add((id, name))) throw Error("Only one expected-effect rule per output filename is allowed.");
            XDocument original;
            try { original = readXml(source); }
            catch (Exception e) when (e is XmlException or ArgumentException) { throw Error("Expected-effect source is not a bounded UTF-8 XML document."); }
            var owners = original.Root?.Elements("Hmi.Screen.Screen").ToArray() ?? [];
            var attributes = owners.Length == 1 ? owners[0].Elements("AttributeList").ToArray() : [];
            var names = attributes.Length == 1 ? attributes[0].Elements("Name").ToArray() : [];
            if (original.Root?.Name != "Document" || owners.Length != 1 || (string?)owners[0].Attribute("ID") != ownerId ||
                names.Length != 1 || names[0].HasAttributes || names[0].Nodes().Count() != 1 || names[0].FirstNode?.GetType() != typeof(XText) ||
                names[0].Value != (string?)selection["name"])
                throw Error("Source must contain one exact Classic screen with the selected export-local ID and output name.");
            XDocument expected;
            try { expected = HmiPreservationRules.PatchClassicScalar(original, "Hmi.Screen.Screen", ownerId, "BackColor",
                Text(rule, "expectedValue", 64), Text(rule, "replacementValue", 64)); }
            catch (ArgumentException) { throw Error("Expected-effect source scalar is stale, ambiguous or outside the supported profile."); }
            observations.TryGetValue(id, out var observation);
            var proposed = Compare(selection["proposed"]!.AsArray(), Boolean(selection, "proposedComplete"));
            var observed = Compare(observation?["files"]?.AsArray() ?? [], observation is not null && Boolean(observation, "complete"));
            checks.Add(new JsonObject { ["expectation"] = rule.DeepClone(), ["source"] = HmiComponentXml.Descriptor(source),
                ["proposal"] = proposed, ["observation"] = observed, ["nativeEffectVerified"] = false,
                ["contentAssociationVerified"] = false, ["wholeProjectPreservationVerified"] = false });

            JsonObject Compare(JsonArray ids, bool complete)
            {
                var candidate = ids.Select(n => files[(string)n!]).SingleOrDefault(f => Text(f, "fileName") == name);
                string status = complete ? "missing" : "unobserved";
                if (candidate is not null)
                {
                    XDocument actual;
                    try { actual = readXml(candidate); }
                    catch (Exception e) when (e is XmlException or ArgumentException) { return Result("invalidDocument"); }
                    try { HmiPreservationRules.RequireExactDocument(expected, actual); status = "match"; }
                    catch (ArgumentException) { status = "different"; }
                }
                return Result(status);
                JsonObject Result(string state) => new() { ["status"] = state, ["file"] = candidate is null ? null : HmiComponentXml.Descriptor(candidate) };
            }
        }
        var coverage = new JsonArray();
        foreach (var output in outputs.Values)
        {
            string id = Text(output, "id");
            var names = new[] { "baselineFiles", "currentFiles", "proposedFiles" }.SelectMany(k => output[k]!.AsArray()).Select(f => Text(Object(f), "fileName"))
                .Concat(observations.TryGetValue(id, out var observation) ? observation["files"]!.AsArray().Select(n => Text(files[(string)n!], "fileName")) : [])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            JsonArray Names(bool assessed) => new(names.Where(n => selected.Contains((id, n)) == assessed).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            coverage.Add(new JsonObject { ["outputId"] = id, ["derivedFileNames"] = Names(true), ["unassessedFileNames"] = Names(false),
                ["completeOutputEffectsVerified"] = false });
        }
        report["schemaVersion"] = "hmi-component-review.v4";
        report["derivedEffects"] = checks; report["expectationCoverage"] = coverage;
        report["summary"]!["derivedEffectCount"] = checks.Count;
        report["derivedEffectPolicy"] = "Derive each selected Classic screen BackColor document from its exact current file and explicit stale-value guard using the shared HMI preservation rules. Independently compare proposed and observed XML trees, including declaration, comments, instructions, whitespace, unknown content and all peers. No timestamp normalization, scripts or native generation. Other files and operations remain unassessed; exact file association, completeness, ownership, library identity and native effects remain unverified. UTF-8 only, 64 XML depth, 200000 reader events per document, 64 MiB examined including repeated roles.";
    }
}
