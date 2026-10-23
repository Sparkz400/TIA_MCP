using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Core.DbSnapshots;

/// <summary>Documented snapshot export shape; supplied values never become current PLC values.</summary>
public static class DbSnapshotReview
{
    public const int InputLimit = 4 * 1024 * 1024;
    public const int ValueLimit = 20000;
    private const string SnapshotNamespace = "http://www.siemens.com/automation/Openness/SW/Interface/Snapshot/v1";

    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > InputLimit) throw Error("Snapshot XML must contain 1 byte..4 MiB.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = InputLimit, IgnoreWhitespace = false });
        // Bound depth and node count before building a tree, including opaque extensions.
        int events = 0;
        while (reader.Read()) if (reader.Depth > 32 || ++events > 200000) throw Error("Snapshot XML exceeds depth/event bounds.");
        stream.Position = 0;
        using var treeReader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = InputLimit });
        var document = XDocument.Load(treeReader, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root?.Name != "Document") throw Error("Expected an unqualified Document export root.");
        var block = One(root, "SW.Blocks.InterfaceSnapshot");
        if (root.Elements().Count(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)) != 1)
            throw Error("One snapshot block per document is required.");
        var attributes = One(block, "AttributeList");
        string name = Scalar(One(attributes, "Name"), 8192, false);
        var snapshot = One(attributes, "Snapshot");
        var containers = snapshot.Elements().Where(e => e.Name.LocalName == "SnapshotValues").ToArray();
        if (containers.Length != 1 || containers[0].Name.NamespaceName is not ("" or SnapshotNamespace))
            throw Error("Expected one documented SnapshotValues container.");
        var values = new JsonArray(); var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in containers[0].Elements())
        {
            if (element.Name != containers[0].Name.Namespace + "Value") throw Error("Unsupported snapshot value element; original upload is unchanged.");
            string path = Selector(element.Attribute("Path")?.Value), type = Selector(element.Attribute("Type")?.Value);
            if (!paths.Add(path) || values.Count == ValueLimit) throw Error("Duplicate value path or more than 20000 values.");
            values.Add(new JsonObject { ["path"] = path, ["type"] = type, ["text"] = Scalar(element, InputLimit, true) });
        }
        string? snapshotDate = Optional(attributes, "SnapshotDate"), structureModified = Optional(attributes, "StructureModified");
        return new JsonObject { ["name"] = name, ["snapshotDate"] = snapshotDate, ["structureModified"] = structureModified,
            ["values"] = values, ["valueCount"] = values.Count, ["formatProfile"] = "documented-interface-snapshot-shape",
            ["valueDomain"] = "suppliedSnapshotValues", ["xsdValidated"] = false,
            ["metadataPolicy"] = "Dates, types and values remain exact XML text. Other content is retained in the original artifact, without interpretation." };
    }

    public static JsonObject Build(string reviewId, JsonObject review, string objectPath, byte[] beforeBytes, byte[]? afterBytes)
    {
        Selector(objectPath);
        var matches = review["model"]!["nodes"]!.AsArray().Where(n => (string?)n?["objectPath"] == objectPath && (string?)n?["origin"] != "virtual").ToArray();
        if (matches.Length != 1 || (string?)matches[0]!["kind"] != "block" ||
            (string?)matches[0]!["metadata"]?["kind"] is not ("GlobalDB" or "InstanceDB" or "ArrayDB"))
            throw Error("Select an exact captured GlobalDB, InstanceDB or ArrayDB block.");
        var selected = matches[0]!;
        string plcPath = Selector((string?)selected["plcPath"]);
        var before = Parse(beforeBytes); var after = afterBytes is null ? null : Parse(afterBytes);
        if ((string?)before["name"] != (string?)selected["name"] || after is not null && (string?)after["name"] != (string?)selected["name"])
            throw Error("Export name contradicts the selected captured DB. A name match still does not prove native origin.");
        var changes = new JsonArray();
        if (after is not null)
        {
            var left = before["values"]!.AsArray().ToDictionary(v => (string)v!["path"]!, StringComparer.Ordinal);
            var right = after["values"]!.AsArray().ToDictionary(v => (string)v!["path"]!, StringComparer.Ordinal);
            foreach (string path in left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                left.TryGetValue(path, out var a); right.TryGetValue(path, out var b);
                if (!JsonNode.DeepEquals(a, b)) changes.Add(new JsonObject { ["path"] = path,
                    ["status"] = a is null ? "onlyAfter" : b is null ? "onlyBefore" : "differentTextOrType",
                    ["before"] = a?.DeepClone(), ["after"] = b?.DeepClone() });
            }
        }
        var summary = new JsonObject { ["beforeCount"] = before["valueCount"]!.DeepClone(), ["afterCount"] = after?["valueCount"]?.DeepClone(),
            ["changedPathCount"] = changes.Count, ["comparisonRequested"] = after is not null,
            ["originalBytesEqual"] = afterBytes is null ? null : beforeBytes.AsSpan().SequenceEqual(afterBytes),
            ["structureTimestampComparison"] = after is null ? "notCompared" : before["structureModified"] is null || after["structureModified"] is null ? "unknown" :
                JsonNode.DeepEquals(before["structureModified"], after["structureModified"]) ? "sameReportedText" : "differentReportedText" };
        return new JsonObject { ["schemaVersion"] = "db-snapshot-review.v1", ["reviewId"] = reviewId, ["objectPath"] = objectPath, ["plcPath"] = plcPath,
            ["association"] = "userSelectedCapturedDB", ["before"] = before, ["after"] = after, ["changes"] = changes, ["summary"] = summary,
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false,
            ["programIdentityVerified"] = false, ["structureCompatibilityVerified"] = false,
            ["limits"] = "Supplied documents only. Equal names, timestamps or text do not prove CPU/program identity, freshness, structural compatibility or equal runtime values. No start/actual value conversion, snapshot import, engineering change or online access." };
    }
    private static XElement One(XElement parent, string name)
    {
        var elements = parent.Elements().Where(e => e.Name.LocalName == name).ToArray();
        if (elements.Length != 1 || elements[0].Name != name) throw Error("Expected one unqualified " + name + ".");
        return elements[0];
    }
    private static string? Optional(XElement parent, string name) => parent.Elements().Any(e => e.Name.LocalName == name) ? Scalar(One(parent, name), 256, false) : null;
    private static string Scalar(XElement element, int limit, bool empty)
    {
        if (element.HasElements || element.Value.Length > limit || !empty && string.IsNullOrWhiteSpace(element.Value)) throw Error("Invalid scalar snapshot field.");
        return element.Value;
    }
    private static string Selector(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 8192 || text.Any(char.IsControl)) throw Error("Expected a bounded exact selector.");
        try { _ = new System.Text.UTF8Encoding(false, true).GetByteCount(text); }
        catch (System.Text.EncoderFallbackException) { throw Error("Selector contains invalid Unicode."); }
        return text;
    }
    private static BridgeException Error(string message) => new("InvalidDbSnapshot", message);
}
