using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using static TiaMcp.Core.HmiComponents.HmiComponentReview;

namespace TiaMcp.Core.HmiComponents;

internal static class HmiComponentLibraryAssociations
{
    internal static void Add(JsonObject report, JsonObject request, Func<JsonObject, XDocument> readXml)
    {
        var files = Files(request).ToDictionary(f => Text(f, "id"), StringComparer.Ordinal);
        var selections = Array(request, "libraryAssociations", 128);
        if (selections.Count == 0 && (string?)request["schemaVersion"] is not ("hmi-component-review-request.v6" or "hmi-component-review-request.v7")) throw Error("V5 requires at least one explicit library-export association.");
        var selected = new HashSet<(string Scope, string Path)>();
        var rows = new JsonArray(); var coverage = new JsonArray();
        foreach (var value in selections)
        {
            var selection = Object(value); Fields(selection, "scope objectPath fileId profile reason");
            string scope = Text(selection, "scope", 32), path = Text(selection, "objectPath"), fileId = Text(selection, "fileId", 128);
            Text(selection, "reason", 2000);
            if (scope is not ("beforeComponent" or "afterComponent") || report[scope] is not JsonObject component ||
                !selected.Add((scope, path)) || !files.TryGetValue(fileId, out var file))
                throw Error("Select a unique before/after component object and an existing original file.");
            if (Text(selection, "profile", 64) != "v21-full-fb-library-version.v1") throw Error("Unsupported library-export profile.");
            var node = component["objects"]!.AsArray().Select(o => Object(o!["capturedObject"])).SingleOrDefault(n => (string?)n["objectPath"] == path);
            var metadata = node?["metadata"] as JsonObject;
            if (node is null || (string?)node["kind"] != "block" || String(metadata?["kind"]) != "FB")
                throw Error("This profile requires an exact selected captured FB; names and XML IDs cannot select another object.");
            JsonObject identity;
            try { identity = Identity(readXml(file)); }
            catch (Exception e) when (e is XmlException or ArgumentException) { throw Error("Original file is outside the explicit bounded V21 full FB library-version profile."); }
            var observation = metadata?["libraryInstance"] as JsonObject;
            string status = "unavailable";
            if (String(observation?["schemaVersion"]) == "plc-library-instance-observation.v1" &&
                String(observation?["source"]) == "LibraryTypeInstanceInfo.LibraryTypeVersion" && String(observation?["status"]) == "linked" &&
                ((string?)request["schemaVersion"] is not ("hmi-component-review-request.v6" or "hmi-component-review-request.v7") || String(observation?["typeGuid"])?.Length == 36 && String(observation?["versionGuid"])?.Length == 36) &&
                Guid.TryParseExact(String(observation?["typeGuid"]), "D", out var type) && type != Guid.Empty &&
                Guid.TryParseExact(String(observation?["versionGuid"]), "D", out var version) && version != Guid.Empty)
                status = type.ToString("D") == (string?)identity["typeGuid"] && version.ToString("D") == (string?)identity["versionGuid"] ? "match" : "different";
            rows.Add(new JsonObject { ["selection"] = selection.DeepClone(), ["reviewId"] = component["selection"]!["reviewId"]!.DeepClone(),
                ["file"] = HmiComponentXml.Descriptor(file), ["exportIdentity"] = identity, ["capturedObservation"] = observation?.DeepClone(),
                ["identifierAgreement"] = status, ["sourceAuthenticityVerified"] = false, ["libraryIdentityVerified"] = false,
                ["contentAssociationVerified"] = false, ["dependencyClosureVerified"] = false, ["runtimeInstanceVerified"] = false });
        }
        foreach (string scope in new[] { "beforeComponent", "afterComponent" })
        {
            if (report[scope] is not JsonObject component) continue;
            foreach (var item in component["objects"]!.AsArray())
            {
                string path = (string)item!["capturedObject"]!["objectPath"]!;
                coverage.Add(new JsonObject { ["scope"] = scope, ["reviewId"] = component["selection"]!["reviewId"]!.DeepClone(),
                    ["objectPath"] = path, ["associationSelected"] = selected.Contains((scope, path)), ["libraryIdentityVerified"] = false });
            }
        }
        report["schemaVersion"] = "hmi-component-review.v5";
        report["libraryAssociations"] = rows; report["libraryCoverage"] = coverage;
        report["summary"]!["libraryAssociationCount"] = rows.Count;
        report["summary"]!["libraryIdentifierMismatchCount"] = rows.Count(r => (string?)r!["identifierAgreement"] == "different");
        report["summary"]!["libraryIdentifierUnavailableCount"] = rows.Count(r => (string?)r!["identifierAgreement"] == "unavailable");
        report["summary"]!["unassociatedComponentCount"] = coverage.Count(r => !(bool)r!["associationSelected"]!);
        report["libraryAssociationPolicy"] = "Explicit supplied V21 full FB library-version exports and exact saved component paths only. Compare nonempty type/version GUID pairs; names, XML-local IDs and matching bytes do not establish native identity. Missing or inconsistent saved observations remain unavailable. Matching identifiers do not prove source authenticity, matching component content, complete dependencies/consumers, runtime DB identity, native compatibility or safe generation. Original bytes remain retained; no native operation is admitted. All selected XML roles share UTF-8, DTD refusal, 64 depth, 200000 events and 64 MiB examined-byte bounds.";
    }

    private static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject Identity(XDocument document)
    {
        var root = document.Root;
        if (root?.Name != "Document" || !root.Elements().Select(e => e.Name).SequenceEqual(new XName[] { "Engineering", "SW.Blocks.CodeBlockLibraryTypeVersion" }))
            throw new ArgumentException("Expected the observed V21 envelope without DocumentInfo or other export objects.");
        var engineering = One(root, "Engineering");
        if (engineering.HasElements || engineering.Attributes().Count() != 1 || (string?)engineering.Attribute("version") != "V21") throw new ArgumentException("V21 is required.");
        var version = One(root, "SW.Blocks.CodeBlockLibraryTypeVersion");
        var block = One(One(version, "ObjectList"), "SW.Blocks.FB");
        if (document.Descendants("SW.Blocks.CodeBlockLibraryTypeVersion").Count() != 1 || document.Descendants("SW.Blocks.FB").Count() != 1 ||
            (string?)block.Attribute("CompositionName") != "ContentObject") throw new ArgumentException("Expected one direct version and content FB.");
        var attributes = One(version, "AttributeList");
        string type = Scalar(attributes, "LibraryTypeGuid", true), id = Scalar(attributes, "Guid", true);
        if (!Guid.TryParseExact(type, "D", out var typeGuid) || typeGuid == Guid.Empty || !Guid.TryParseExact(id, "D", out var versionGuid) || versionGuid == Guid.Empty)
            throw new ArgumentException("Nonempty GUID pairs are required.");
        string name = Scalar(attributes, "LibraryTypeName", true);
        if (name != Scalar(One(block, "AttributeList"), "Name", false) || Scalar(attributes, "State", true) != "Committed")
            throw new ArgumentException("Content name and committed type version must agree.");
        var ids = document.Descendants().Attributes("ID").Select(a => a.Value).ToArray();
        if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id != id.Trim()) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new ArgumentException("Duplicate or empty XML identities.");
        return new JsonObject { ["profile"] = "v21-full-fb-library-version.v1", ["typeGuid"] = typeGuid.ToString("D"),
            ["versionGuid"] = versionGuid.ToString("D"), ["typeName"] = name, ["versionNumber"] = Scalar(attributes, "VersionNumber", false) };
    }
    private static XElement One(XContainer parent, XName name)
    {
        var rows = parent.Elements(name).ToArray();
        return rows.Length == 1 ? rows[0] : throw new ArgumentException("Missing or ambiguous direct XML field.");
    }
    private static string Scalar(XContainer parent, XName name, bool readOnly)
    {
        var scalar = One(parent, name);
        if (scalar.Nodes().Count() != 1 || scalar.FirstNode?.GetType() != typeof(XText) || string.IsNullOrWhiteSpace(scalar.Value) ||
            scalar.Value.Length > 256 || scalar.Value != scalar.Value.Trim() || scalar.Attributes().Count() != (readOnly ? 1 : 0) ||
            readOnly && (string?)scalar.Attribute("ReadOnly") != "true") throw new ArgumentException("Expected one exact scalar.");
        return scalar.Value;
    }
}
