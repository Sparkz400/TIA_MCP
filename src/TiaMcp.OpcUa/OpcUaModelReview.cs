using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using TiaMcp.Core;

namespace TiaMcp.OpcUa;

/// <summary>Offline, bounded NodeSet envelope review. Never imports, edits or connects to a target.</summary>
public static class OpcUaModelReview
{
    public const int MaxBytes = 16 * 1024 * 1024;
    public const int MaxNodes = 20000;
    public const int MaxXmlEvents = 500000;
    /// <summary>Stands for the empty first namespace URI of a TIA server export, the PLC's own namespace.</summary>
    public const string UnnamedLocalNamespace = "urn:tiamcp:unnamed-local-namespace";
    public const int MaxReportBytes = 32 * 1024 * 1024;
    public const string SchemaSha256 = "c7ac09ea7504d54174e1eb4c5a8123c0cc491040703dfcd6bfe085bf6c53f9f0";
    private static readonly XNamespace Ns = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
    private static readonly Lazy<XmlSchemaSet> Schema = new(LoadSchema);
    private static readonly HashSet<string> NodeClasses = new(StringComparer.Ordinal)
        { "UAObject", "UAVariable", "UAMethod", "UAView", "UAObjectType", "UAVariableType", "UADataType", "UAReferenceType" };
    internal sealed record Dependency(string Source, OpcUaNodeIdentity Target, string Kind);
    internal sealed record Model(string Hash, int Bytes, string[] Namespaces, JsonArray Models,
        SortedDictionary<string, JsonObject> Nodes, JsonArray Diagnostics, string EnvelopeHash, Dependency[] Dependencies);

    public static JsonObject Inspect(byte[] source)
    {
        var model = Parse(source);
        var result = Header("opcua-model-review.v1");
        result["sourceSha256"] = model.Hash;
        result["sourceBytes"] = model.Bytes;
        result["namespaceUris"] = JsonSerializer.SerializeToNode(model.Namespaces);
        result["models"] = model.Models;
        result["nodeCount"] = model.Nodes.Count;
        result["nodes"] = new JsonArray(model.Nodes.Values.Select(v => (JsonNode)v).ToArray());
        result["diagnostics"] = model.Diagnostics;
        result["envelopeSha256"] = model.EnvelopeHash;
        result["schemaValidation"] = "passedPinnedGeneralNodeSetEnvelope";
        return Bounded(result);
    }

    public static JsonObject Compare(byte[] beforeSource, byte[] afterSource, JsonObject? consumerManifest = null)
    {
        var before = Parse(beforeSource);
        var after = Parse(afterSource);
        var changes = NodeChanges(before.Nodes, after.Nodes);
        if (!JsonNode.DeepEquals(before.Models, after.Models)) changes.Add(new JsonObject { ["nodeKey"] = null,
            ["field"] = "modelMetadata", ["severity"] = "reviewRequired", ["before"] = before.Models.DeepClone(), ["after"] = after.Models.DeepClone() });
        if (before.EnvelopeHash != after.EnvelopeHash)
            changes.Add(new JsonObject { ["nodeKey"] = null, ["field"] = "envelopeOrExtensions", ["severity"] = "reviewRequired",
                ["before"] = before.EnvelopeHash, ["after"] = after.EnvelopeHash });
        var result = Header("opcua-model-comparison.v1");
        result["beforeSha256"] = before.Hash;
        result["afterSha256"] = after.Hash;
        result["changes"] = changes;
        result["breakingChangeCount"] = changes.Count(c => c!["severity"]!.GetValue<string>() == "breaking");
        result["clientImpacts"] = ClientImpacts(consumerManifest, before, changes);
        result["unknownConsumers"] = true;
        result["compatibilityCertified"] = false;
        result["beforeDiagnostics"] = before.Diagnostics;
        result["afterDiagnostics"] = after.Diagnostics;
        result["outcome"] = changes.Count == 0 ? "noChangesInReviewedFields" : "changesDetected";
        return Bounded(result);
    }

    internal static JsonArray NodeChanges(IReadOnlyDictionary<string, JsonObject> before, IReadOnlyDictionary<string, JsonObject> after)
    {
        var changes = new JsonArray();
        void Change(string? node, string field, string severity, JsonNode? oldValue, JsonNode? newValue) =>
            changes.Add(new JsonObject { ["nodeKey"] = node, ["field"] = field, ["severity"] = severity,
                ["before"] = oldValue?.DeepClone(), ["after"] = newValue?.DeepClone() });
        foreach (var key in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            if (!after.TryGetValue(key, out var newer))
            { Change(key, "nodeRemoved", "breaking", before[key]["identity"], null); continue; }
            if (!before.TryGetValue(key, out var older))
            { Change(key, "nodeAdded", "additiveCandidate", null, newer["identity"]); continue; }
            foreach (string field in older.Select(p => p.Key).Union(newer.Select(p => p.Key)).Order(StringComparer.Ordinal))
            {
                if (field is "identity" or "nodeKey" || JsonNode.DeepEquals(older[field], newer[field])) continue;
                string severity = field is "nodeClass" or "browseName" or "dataType" or "valueRank" or "arrayDimensions"
                    ? "breaking" : "reviewRequired";
                if (field == "definitionSha256") severity = "breaking";
                if (field == "valueSha256" && older["browseName"]?["name"]?.GetValue<string>() is "InputArguments" or "OutputArguments")
                    severity = "breaking";
                Change(key, field, severity, older[field], newer[field]);
            }
        }
        return changes;
    }

    internal static Model Parse(byte[] source, bool collectUnresolved = true, bool includeDefinitionDependencies = false)
    {
        if (source.Length == 0 || source.Length > MaxBytes) throw Error("OpcUaCapacity", "NodeSet input must be 1 byte to 16 MiB.");
        XDocument document;
        try
        {
            // Bound depth before building a DOM, including extension content not covered by the XSD.
            using (var scan = XmlReader.Create(new MemoryStream(source, false), Settings()))
            {
                int events = 0;
                while (scan.Read())
                    if (scan.Depth > 64 || ++events > MaxXmlEvents) throw Error("OpcUaCapacity", "XML exceeds depth 64 or 500000 reader events.");
            }
            var settings = Settings();
            settings.ValidationType = ValidationType.Schema;
            settings.Schemas = Schema.Value;
            settings.ValidationFlags = XmlSchemaValidationFlags.None; // Never process remote schema locations/inline schemas.
            settings.ValidationEventHandler += (_, args) =>
            {
                if (args.Severity == XmlSeverityType.Error) throw Error("OpcUaSchemaMismatch", args.Message);
            };
            using var reader = XmlReader.Create(new MemoryStream(source, false), settings);
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is XmlException or XmlSchemaException)
        { throw new BridgeException("OpcUaInvalidXml", "Invalid NodeSet XML: " + ex.Message, ex); }
        var root = document.Root!;
        if (root.Name != Ns + "UANodeSet") throw Error("OpcUaSchemaMismatch", "Expected the UANodeSet root in the pinned namespace.");
        var namespaces = new[] { OpcUaNodeIdentity.StandardNamespace }.Concat(
            root.Element(Ns + "NamespaceUris")?.Elements(Ns + "Uri").Select(e => e.Value) ?? []).ToArray();
        // TIA's whole-server export leaves the PLC's own namespace (index 1) empty; the server names it only at runtime.
        if (namespaces.Length > 1 && namespaces[1].Length == 0) namespaces[1] = UnnamedLocalNamespace;
        if (namespaces.Length > ushort.MaxValue + 1 || namespaces.Any(n => n.Length is 0 or > 4096 || !Uri.TryCreate(n, UriKind.Absolute, out _))
            || namespaces.Distinct(StringComparer.Ordinal).Count() != namespaces.Length)
            throw Error("OpcUaInvalidIdentity", "Namespace URIs must be unique absolute URIs, at most 4096 characters each.");
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alias in root.Element(Ns + "Aliases")?.Elements(Ns + "Alias") ?? [])
        {
            string name = (string?)alias.Attribute("Alias") ?? "";
            if (name.Length is 0 or > 4096 || !aliases.TryAdd(name, alias.Value))
                throw Error("OpcUaInvalidIdentity", "Aliases must have unique, bounded names.");
            _ = OpcUaNodeIdentity.Parse(alias.Value, namespaces); // No recursive aliases.
        }
        OpcUaNodeIdentity Id(string value) => OpcUaNodeIdentity.Parse(value, namespaces, aliases);
        var nodes = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var diagnostics = new JsonArray();
        var edges = new List<Dependency>();
        void Edge(string from, OpcUaNodeIdentity target, string kind)
        {
            if (edges.Count >= 100000) throw Error("OpcUaCapacity", "NodeSet exceeds 100000 reviewed dependencies.");
            edges.Add(new(from, target, kind));
        }
        foreach (var element in root.Elements().Where(e => e.Name.Namespace == Ns && NodeClasses.Contains(e.Name.LocalName)))
        {
            if (nodes.Count >= MaxNodes) throw Error("OpcUaCapacity", "NodeSet exceeds 20000 nodes.");
            var identity = Id(Required(element, "NodeId"));
            var node = new JsonObject { ["nodeKey"] = identity.Key, ["identity"] = JsonSerializer.SerializeToNode(identity, BridgeProtocol.Json),
                ["nodeClass"] = element.Name.LocalName, ["browseName"] = BrowseName(Required(element, "BrowseName"), namespaces) };
            if (!nodes.TryAdd(identity.Key, node)) throw Error("OpcUaIdentityCollision", "Duplicate expanded node identity: " + identity.Key);
            var attributes = new JsonObject();
            foreach (var attr in element.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal))
            {
                string name = attr.Name.ToString();
                if (name is "NodeId" or "BrowseName" or "DataType" or "ValueRank" or "ArrayDimensions") continue;
                if (name is "ParentNodeId" or "MethodDeclarationId")
                {
                    var target = Id(attr.Value); attributes[name] = target.Key; Edge(identity.Key, target, name);
                }
                else attributes[name] = attr.Value;
            }
            node["attributes"] = attributes;
            if (element.Name.LocalName is "UAVariable" or "UAVariableType")
            {
                var dataType = Id((string?)element.Attribute("DataType") ?? "i=24");
                Edge(identity.Key, dataType, "dataType");
                node["dataType"] = dataType.Key;
                int rank = int.Parse((string?)element.Attribute("ValueRank") ?? "-1", CultureInfo.InvariantCulture);
                if (rank < -3) throw Error("OpcUaInvalidShape", "ValueRank must be at least -3.");
                var dimensions = ((string?)element.Attribute("ArrayDimensions") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var dimension) ? dimension
                        : throw Error("OpcUaInvalidShape", "Array dimension is outside UInt32.")).ToArray();
                if (dimensions.Length != 0 && (rank <= 0 || dimensions.Length != rank))
                    throw Error("OpcUaInvalidShape", "ArrayDimensions must match a positive ValueRank.");
                node["valueRank"] = rank;
                node["arrayDimensions"] = JsonSerializer.SerializeToNode(dimensions);
            }
            var references = new List<string>();
            foreach (var reference in element.Element(Ns + "References")?.Elements(Ns + "Reference") ?? [])
            {
                var target = Id(reference.Value); var type = Id(Required(reference, "ReferenceType"));
                bool forward = XmlConvert.ToBoolean((string?)reference.Attribute("IsForward") ?? "true");
                // JSON tuple avoids delimiter ambiguities in string identifiers.
                references.Add(JsonSerializer.Serialize(new object[] { type.Key, forward, target.Key }));
                Edge(identity.Key, target, "referenceTarget"); Edge(identity.Key, type, "referenceType");
            }
            node["references"] = JsonSerializer.SerializeToNode(references.Order(StringComparer.Ordinal).ToArray());
            if (includeDefinitionDependencies)
            {
                foreach (var field in element.Element(Ns + "Definition")?.Elements(Ns + "Field") ?? [])
                    Edge(identity.Key, Id((string?)field.Attribute("DataType") ?? "i=24"), "definitionFieldDataType");
                foreach (var role in element.Element(Ns + "RolePermissions")?.Elements(Ns + "RolePermission") ?? [])
                    Edge(identity.Key, Id(role.Value), "rolePermission");
            }
            node["valueSha256"] = ElementHash(element.Element(Ns + "Value"));
            node["definitionSha256"] = ElementHash(element.Element(Ns + "Definition"));
            node["annotationsSha256"] = AnnotationHash(element);
            // Includes access, localized metadata and unknown/vendor extensions. Never drops them as equivalent.
            node["otherContentSha256"] = Hash(Encoding.UTF8.GetBytes(string.Concat(element.Elements()
                .Where(e => e.Name != Ns + "References" && e.Name != Ns + "Value" && e.Name != Ns + "Definition")
                .Select(Canonical))));
        }
        foreach (var edge in edges.Distinct().Where(e => collectUnresolved && !nodes.ContainsKey(e.Target.Key)))
        {
            if (diagnostics.Count >= 10000) throw Error("OpcUaCapacity", "More than 10000 unresolved dependencies; provide a smaller review scope.");
            diagnostics.Add(new JsonObject { ["code"] = edge.Target.NamespaceUri == OpcUaNodeIdentity.StandardNamespace
                    ? "StandardModelNotResolved" : "DependencyNodeNotProvided",
                ["sourceNodeKey"] = edge.Source, ["targetNodeKey"] = edge.Target.Key, ["kind"] = edge.Kind });
        }
        var models = new JsonArray();
        foreach (var entry in root.Element(Ns + "Models")?.Elements(Ns + "Model") ?? [])
        {
            string uri = Required(entry, "ModelUri");
            if (!namespaces.Contains(uri, StringComparer.Ordinal)) throw Error("OpcUaInvalidIdentity", "Model URI is absent from the namespace table.");
            models.Add(new JsonObject { ["modelUri"] = uri, ["version"] = (string?)entry.Attribute("Version"),
                ["publicationDate"] = (string?)entry.Attribute("PublicationDate"), ["modelVersion"] = (string?)entry.Attribute("ModelVersion"), ["metadataSha256"] = ElementHash(entry),
                ["requiredModels"] = new JsonArray(entry.Descendants(Ns + "RequiredModel").Select(e => (JsonNode)new JsonObject {
                    ["modelUri"] = Required(e, "ModelUri"), ["version"] = (string?)e.Attribute("Version"),
                    ["publicationDate"] = (string?)e.Attribute("PublicationDate"), ["modelVersion"] = (string?)e.Attribute("ModelVersion"), ["resolution"] = "notEvaluated" }).ToArray()) });
        }
        string envelope = JsonSerializer.Serialize(namespaces.Order(StringComparer.Ordinal).ToArray()) + string.Concat(root.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal)
            .Select(a => JsonSerializer.Serialize(new[] { a.Name.ToString(), a.Value }))) + string.Concat(root.Elements()
            .Where(e => e.Name != Ns + "NamespaceUris" && e.Name != Ns + "Aliases" && e.Name != Ns + "Models" && !NodeClasses.Contains(e.Name.LocalName)).Select(Canonical));
        envelope += AnnotationHash(document);
        return new(Hash(source), source.Length, namespaces, models, nodes, diagnostics, Hash(Encoding.UTF8.GetBytes(envelope)), edges.Distinct().ToArray());
    }

    private static JsonArray ClientImpacts(JsonObject? manifest, Model before, JsonArray changes)
    {
        var impacts = new JsonArray();
        if (manifest is null) return impacts;
        if (Encoding.UTF8.GetByteCount(manifest.ToJsonString()) > 1024 * 1024) throw Error("OpcUaCapacity", "Consumer manifest exceeds 1 MiB.");
        if (manifest.Any(p => p.Key is not ("schemaVersion" or "modelSha256" or "consumers")))
            throw Error("OpcUaInvalidConsumers", "Unknown consumer manifest field.");
        if (StringValue(manifest["schemaVersion"]) != "opcua-consumers.v1" || StringValue(manifest["modelSha256"]) != before.Hash)
            throw Error("OpcUaStaleConsumers", "Consumer manifest must bind the exact before-model SHA-256 and schema version.");
        if (manifest["consumers"] is not JsonArray consumers || consumers.Count > 1000)
            throw Error("OpcUaInvalidConsumers", "Expected at most 1000 consumers.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int total = 0, impactCount = 0;
        foreach (var item in consumers)
        {
            if (item is not JsonObject consumer || consumer.Any(p => p.Key is not ("id" or "nodeKeys")))
                throw Error("OpcUaInvalidConsumers", "Expected a consumer object with id and nodeKeys.");
            string id = StringValue(consumer["id"]) ?? "";
            if (id.Length is 0 or > 256 || !ids.Add(id) || consumer["nodeKeys"] is not JsonArray keys)
                throw Error("OpcUaInvalidConsumers", "Consumers require unique IDs and nodeKeys.");
            total += keys.Count;
            if (total > 20000) throw Error("OpcUaCapacity", "Consumer manifest exceeds 20000 dependencies.");
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in keys)
            {
                string key = StringValue(value) ?? "";
                if (!before.Nodes.ContainsKey(key) || !selected.Add(key))
                    throw Error("OpcUaInvalidConsumers", "Consumer node key is duplicate or absent from the before model.");
            }
            var direct = changes.Where(c => c?["nodeKey"] is not null && selected.Contains(c["nodeKey"]!.GetValue<string>()))
                .Select(c => (JsonNode)new JsonObject { ["nodeKey"] = c!["nodeKey"]!.DeepClone(),
                    ["field"] = c["field"]!.DeepClone(), ["severity"] = c["severity"]!.DeepClone() }).ToArray();
            impactCount += direct.Length;
            if (impactCount > 50000) throw Error("OpcUaCapacity", "Consumer impacts exceed 50000 rows; reduce scope.");
            impacts.Add(new JsonObject { ["consumerId"] = id, ["directChanges"] = new JsonArray(direct),
                ["status"] = direct.Length > 0 ? "affected" : changes.Count > 0 ? "indirectImpactUnknown" : "noChangesInReviewedFields",
                ["dependencyCoverage"] = "declaredNodeKeysOnly" });
        }
        return impacts;
    }

    private static JsonObject Header(string version) => new() { ["schemaVersion"] = version, ["schemaSha256"] = SchemaSha256,
        ["nativeQualified"] = false, ["targetProbed"] = false, ["tiaImportAccepted"] = false,
        ["plcMappingsResolved"] = false, ["dependencyClosureComplete"] = false,
        ["limitations"] = new JsonArray("General NodeSet schema and selected identities/shapes only; no Siemens mapping dialect validation.",
            "Values, type definitions, permissions, vendor extensions and XML annotations are retained as hashes; changes require review.",
            "Required model versions, inherited access, effective permissions and transitive client dependencies are not resolved.",
            "Keep the original hash-bound input bytes; this report is not an importable or lossless replacement model.") };
    private static string? StringValue(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    private static JsonObject Bounded(JsonObject result)
    {
        if (Encoding.UTF8.GetByteCount(result.ToJsonString()) > MaxReportBytes) throw Error("OpcUaCapacity", "Review exceeds 32 MiB; reduce scope.");
        return result;
    }
    private static JsonObject BrowseName(string value, IReadOnlyList<string> namespaces)
    {
        int index = 0, colon = value.IndexOf(':');
        if (colon > 0 && value[..colon].All(char.IsAsciiDigit))
        {
            if (!ushort.TryParse(value.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                throw Error("OpcUaInvalidIdentity", "Invalid BrowseName namespace index.");
            index = parsed; value = value[(colon + 1)..];
        }
        if (index >= namespaces.Count || value.Length > 4096) throw Error("OpcUaInvalidIdentity", "Invalid BrowseName namespace or length.");
        return new JsonObject { ["namespaceUri"] = namespaces[index], ["name"] = value };
    }
    // XML annotations have their own position-sensitive hash, including projected-away containers.
    private static string? AnnotationHash(XContainer container)
    {
        var buffer = new StringBuilder();
        void Visit(XContainer current, string path)
        {
            int index = 0, textOffset = 0;
            foreach (var node in current.Nodes())
            {
                string location = path + "/" + index.ToString(CultureInfo.InvariantCulture); index++;
                if (node is XComment or XProcessingInstruction)
                    buffer.Append(JsonSerializer.Serialize(new[] { location, textOffset.ToString(CultureInfo.InvariantCulture), node.ToString(SaveOptions.DisableFormatting) }));
                else if (node is XText text) textOffset += text.Value.Length;
                else if (node is XElement element && !(container is XDocument doc && element.Parent == doc.Root &&
                    element.Name.Namespace == Ns && NodeClasses.Contains(element.Name.LocalName))) Visit(element, location);
            }
        }
        Visit(container, "");
        return buffer.Length == 0 ? null : Hash(Encoding.UTF8.GetBytes(buffer.ToString()));
    }
    // Length-delimited tokens preserve element/text payload; AnnotationHash retains comments and PIs.
    private static string Canonical(XElement element)
    {
        var buffer = new StringBuilder();
        void Token(string value) { buffer.Append(value.Length).Append(':').Append(value); }
        // Opaque vendor text/attribute values can contain QNames. Retain their in-scope prefix
        // bindings even when the element/attribute names themselves already use expanded names.
        var context = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var ancestor in element.AncestorsAndSelf().Reverse())
            foreach (var attr in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration)) context[attr.Name.ToString()] = attr.Value;
        foreach (var pair in context) { buffer.Append('N'); Token(pair.Key); Token(pair.Value); }
        void Visit(XElement current)
        {
            buffer.Append('E'); Token(current.Name.ToString());
            foreach (var attr in current.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal))
            { buffer.Append('A'); Token(attr.Name.ToString()); Token(attr.Value); }
            buffer.Append('C');
            var text = new StringBuilder(); bool hasText = false;
            void FlushText() { if (hasText) { buffer.Append('T'); Token(text.ToString()); text.Clear(); hasText = false; } }
            foreach (var child in current.Nodes())
                if (child is XElement e) { FlushText(); Visit(e); }
                else if (child is XText t) { hasText = true; text.Append(t.Value); }
            FlushText();
            buffer.Append('Z');
        }
        Visit(element); return buffer.ToString();
    }
    private static string? ElementHash(XElement? element) => element is null ? null : Hash(Encoding.UTF8.GetBytes(Canonical(element)));
    private static string Required(XElement element, string attribute) => (string?)element.Attribute(attribute)
        ?? throw Error("OpcUaSchemaMismatch", "Missing required attribute: " + attribute);
    private static string Hash(byte[] source) => Convert.ToHexStringLower(SHA256.HashData(source));
    private static BridgeException Error(string code, string message) => new(code, message);
    private static XmlReaderSettings Settings() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
        MaxCharactersInDocument = MaxBytes, MaxCharactersFromEntities = 0, IgnoreComments = false, IgnoreProcessingInstructions = false };
    private static XmlSchemaSet LoadSchema()
    {
        using var stream = typeof(OpcUaModelReview).Assembly.GetManifestResourceStream("TiaMcp.OpcUa.UANodeSet.xsd")!;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); byte[] bytes = buffer.ToArray();
        if (Hash(bytes) != SchemaSha256) throw Error("OpcUaSchemaChanged", "Embedded NodeSet schema differs from its pinned hash.");
        var schemas = new XmlSchemaSet { XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(bytes), Settings());
        schemas.Add(Ns.NamespaceName, reader); schemas.Compile(); return schemas;
    }
}
