using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace TiaMcp.Core.AutomationMl;

/// <summary>Bounded declared CAEX structure. Does not expand inheritance, mirrors or external files.</summary>
public sealed class AutomationMlDocument
{
    public const int MaxInputBytes = 8 << 20, MaxReportBytes = 64 << 20;
    public sealed record Diagnostic(string Code, string Path, string Message);
    public sealed record Item(string Path, string? Parent, string Kind, string Scope, string? Id, string? Name, string? ClassPath, string ContentSha256);
    public sealed record AttributeValue(string Path, string? Owner, string? Id, string? Name, string? DataType, string? Unit, string?[] Values, string?[] Defaults);
    public sealed record Reference(string Path, string? Owner, string Attribute, string Value, string State, string? Target);
    public sealed record Link(string Path, string? Owner, string Scope, string? Name, string? SideA, string? SideB, string? TargetA, string? TargetB);
    public sealed record Opaque(string Path, string Kind, string ContentSha256);
    public int SchemaVersion => 1;
    public string SourceSha256 { get; private init; } = "";
    public string Profile { get; private init; } = "";
    public string SchemaSha256 { get; private init; } = "";
    public bool SchemaValid { get; private set; } = true;
    public bool NativeQualified => false;
    public bool ImportabilityCertified => false;
    public bool ImpactScopeComplete => false;
    public List<Item> Objects { get; } = [];
    public List<AttributeValue> Attributes { get; } = [];
    public List<Reference> References { get; } = [];
    public List<Link> Links { get; } = [];
    public List<Opaque> OpaqueContent { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
    internal HashSet<string> AmbiguousIdPaths { get; } = new(StringComparer.Ordinal);
    public bool HasErrors => !SchemaValid || Diagnostics.Count != 0;
    public bool MatchingReliable => !Diagnostics.Any(d => d.Code == "DuplicateId") && Objects.All(o => Key(o) is not null) &&
        Objects.Select(Key).Distinct(StringComparer.Ordinal).Count() == Objects.Count;
    public string[] Limitations => ["Document identities are file-local, not native project identities.",
        "Only declared hierarchy, attributes, class references and links are inspected. Inheritance, mirrors, vendor semantics and companion files are not expanded.",
        "Schema validity does not certify AML conformance, Siemens importability, catalog availability or physical wiring.",
        "Original bytes remain authoritative; XML formatting, annotations and opaque changes require review. No native operation is admitted."];
    private static readonly HashSet<string> ObjectKinds = ["InstanceHierarchy", "InternalElement", "ExternalInterface", "SystemUnitClassLib", "SystemUnitClass", "RoleClassLib", "RoleClass", "InterfaceClassLib", "InterfaceClass", "AttributeTypeLib", "AttributeType"];
    private static readonly HashSet<string> RefNames = ["RefBaseSystemUnitPath", "RefBaseClassPath", "RefBaseRoleClassPath", "RefRoleClassPath", "RefAttributeType"];
    private static readonly HashSet<string> Containers = ["CAEXFile", .. ObjectKinds, "Attribute", "RoleRequirements", "SupportedRoleClass"];

    public static AutomationMlDocument Parse(byte[] bytes)
    {
        Require(bytes.Length is > 0 and <= MaxInputBytes, "AutomationMlCapacity", "CAEX input must be 1 byte..8 MiB.");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxInputBytes };
        try
        {
            using (var scan = XmlReader.Create(new MemoryStream(bytes), settings))
            {
                int events = 0, elementCount = 0;
                while (scan.Read())
                {
                    Require(++events <= 250000 && scan.Depth <= 64, "AutomationMlCapacity", "XML exceeds 250000 events or depth 64.");
                    if (scan.NodeType != XmlNodeType.Element) continue;
                    Require(++elementCount <= 50000 && scan.AttributeCount <= 128, "AutomationMlCapacity", "XML exceeds 50000 elements or 128 attributes per element.");
                    while (scan.MoveToNextAttribute())
                        Require(scan.Value.Length <= 8192, "AutomationMlCapacity", "XML attribute exceeds 8192 characters.");
                    scan.MoveToElement();
                }
            }
            using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
            var xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var root = xml.Root!; string? version = (string?)root.Attribute("SchemaVersion");
            Require(root.Name.LocalName == "CAEXFile" &&
                (version == "2.15" && root.Name.NamespaceName == "" || version == "3.0" && root.Name.NamespaceName == "http://www.dke.de/CAEX"),
                "AutomationMlUnsupportedProfile", "Only unnamespaced CAEX 2.15 and CAEX 3.0 in http://www.dke.de/CAEX are supported.");
            string schemaName = version == "2.15" ? "CAEX_ClassModel_V2.15.xsd" : "CAEX_ClassModel_V.3.0.xsd";
            using var resource = typeof(AutomationMlDocument).Assembly.GetManifestResourceStream("TiaMcp.AutomationMl." + schemaName)!;
            using var schemaBytes = new MemoryStream(); resource.CopyTo(schemaBytes);
            var result = new AutomationMlDocument { SourceSha256 = Hash(bytes), Profile = "CAEX " + version, SchemaSha256 = Hash(schemaBytes.ToArray()) };
            var schemas = new XmlSchemaSet { XmlResolver = null };
            using var schemaReader = XmlReader.Create(new MemoryStream(schemaBytes.ToArray()), settings);
            schemas.Add(null, schemaReader); schemas.Compile();
            xml.Validate(schemas, (_, e) => { result.SchemaValid = false; result.Add("SchemaValidation", "/", e.Message); });
            var elements = new Dictionary<string, XElement>(StringComparer.Ordinal);
            var declaredIds = new List<(string Id, string Path)>();
            string?[] Values(XElement attribute, string path, string kind) => attribute.Elements(root.Name.Namespace + kind).Select((value, index) =>
            {
                // CAEX anyType permits vendor XML; concatenated descendant text is not a scalar observation.
                if (value.Attributes().Any(a => !a.IsNamespaceDeclaration) || value.Nodes().Any(n => n is not XText))
                { result.Add("OpaqueAttributeValue", path + "/" + kind + "[" + (index + 1) + "]", "Attributed or non-text value retained as opaque; scalar is unknown."); return null; }
                return value.Value;
            }).ToArray();
            void Walk(XElement element, string path, string? owner, string scope, string? classPath)
            {
                string kind = element.Name.LocalName;
                if (element.Name.Namespace == root.Name.Namespace && (Containers.Contains(kind) || kind == "InternalLink") && element.Attribute("ID") is { Value.Length: > 0 } identity)
                    declaredIds.Add((identity.Value, path));
                if (element.Name.Namespace != root.Name.Namespace || !Containers.Contains(kind))
                {
                    if (kind == "InternalLink" && element.Name.Namespace == root.Name.Namespace)
                        result.Links.Add(new(path, owner, scope, (string?)element.Attribute("Name"), (string?)element.Attribute("RefPartnerSideA"), (string?)element.Attribute("RefPartnerSideB"), null, null));
                    else if (kind == "ExternalReference" && element.Parent == root && element.Name.Namespace == root.Name.Namespace)
                        result.References.Add(new(path, owner, "ExternalReference", element.ToString(SaveOptions.DisableFormatting), "external-not-loaded", null));
                    else result.OpaqueContent.Add(new(path, element.Name.ToString(), Hash(Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting)))));
                    return;
                }
                if (kind == "InstanceHierarchy") scope = "instance";
                if (kind.EndsWith("Lib", StringComparison.Ordinal)) { scope = "definition"; classPath = (string?)element.Attribute("Name"); }
                else if (scope == "definition" && kind is "SystemUnitClass" or "RoleClass" or "InterfaceClass" or "AttributeType")
                    classPath += "/" + (string?)element.Attribute("Name");
                if (ObjectKinds.Contains(kind))
                {
                    Require((classPath?.Length ?? 0) <= 16384, "AutomationMlCapacity", "Class path exceeds 16384 characters.");
                    result.Objects.Add(new(path, owner, kind, scope, (string?)element.Attribute("ID"), (string?)element.Attribute("Name"),
                        kind.EndsWith("Lib", StringComparison.Ordinal) || kind is "SystemUnitClass" or "RoleClass" or "InterfaceClass" or "AttributeType" ? classPath : null,
                        Hash(Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting)))));
                    owner = path; elements.Add(path, element);
                }
                if (kind == "Attribute") result.Attributes.Add(new(path, owner, (string?)element.Attribute("ID"), (string?)element.Attribute("Name"), (string?)element.Attribute("AttributeDataType"),
                    (string?)element.Attribute("Unit"), Values(element, path, "Value"), Values(element, path, "DefaultValue")));
                foreach (var attribute in element.Attributes().Where(a => a.Name.NamespaceName == "" && RefNames.Contains(a.Name.LocalName)))
                    result.References.Add(new(path, owner, attribute.Name.LocalName, attribute.Value, "unresolved", null));
                var counts = new Dictionary<XName, int>();
                foreach (var child in element.Elements())
                {
                    counts.TryGetValue(child.Name, out int index); counts[child.Name] = ++index;
                    Walk(child, path + "/" + child.Name.LocalName + "[" + index + "]", owner, scope, classPath);
                }
            }
            Walk(root, "/CAEXFile[1]", null, "document", null);
            // Exact bytes include comments/PIs outside the root and text positions inside opaque payloads.
            foreach (var node in xml.Nodes().Where(n => n is not XElement))
                result.OpaqueContent.Add(new("/", node.NodeType.ToString(), Hash(Encoding.UTF8.GetBytes(node.ToString(SaveOptions.DisableFormatting)))));
            var ids = result.Objects.Where(o => !string.IsNullOrEmpty(o.Id)).ToLookup(o => o.Id!, StringComparer.Ordinal);
            var allIds = declaredIds.ToLookup(o => o.Id, StringComparer.Ordinal);
            foreach (var duplicate in allIds.Where(g => g.Count() != 1))
            {
                result.Add("DuplicateId", duplicate.First().Path, "ID is not unique: " + duplicate.Key);
                result.AmbiguousIdPaths.UnionWith(duplicate.Select(d => d.Path));
            }
            foreach (var item in result.Objects.Where(o => o.Kind is "InternalElement" or "ExternalInterface" && string.IsNullOrEmpty(o.Id)))
                result.Add("MissingId", item.Path, "No ID; cross-document identity is not inferred from a name or position.");
            foreach (var group in result.Objects.Where(o => o.Name is not null).GroupBy(o => (o.Parent, o.Kind, o.Name)).Where(g => g.Count() > 1))
                result.Add("DuplicateName", group.First().Path, "Same-kind siblings have the same name; paths or interface endpoints may be ambiguous.");
            var classes = result.Objects.Where(o => o.ClassPath is not null).ToLookup(o => o.ClassPath!, StringComparer.Ordinal);
            for (int i = 0; i < result.References.Count; i++)
            {
                var reference = result.References[i]; if (reference.State == "external-not-loaded") continue;
                string expected = reference.Attribute switch { "RefBaseSystemUnitPath" => "SystemUnitClass", "RefBaseRoleClassPath" or "RefRoleClassPath" => "RoleClass",
                    "RefAttributeType" => "AttributeType", _ => elements.TryGetValue(reference.Owner ?? "", out var e) && e.Name.LocalName is "RoleClass" or "SystemUnitClass" ? e.Name.LocalName : "InterfaceClass" };
                var targets = classes[reference.Value].Where(o => o.Kind == expected).Take(2).ToArray();
                string state = reference.Value.Contains('@') ? "external-not-loaded" : targets.Length == 1 ? "resolved" : targets.Length == 0 ? "unresolved" : "ambiguous";
                result.References[i] = reference with { State = state, Target = state == "resolved" ? targets[0].Path : null };
                if (state != "resolved") result.Add("ClassReference", reference.Path, state + ": " + reference.Value);
            }
            var objectsByPath = result.Objects.ToDictionary(o => o.Path);
            // Index the declared composite string: colons can also occur in names or IDs.
            var interfaces = result.Objects.Where(o => o.Kind == "ExternalInterface" && o.Name is not null && o.Parent is not null &&
                objectsByPath[o.Parent].Kind == "InternalElement" && !string.IsNullOrEmpty(objectsByPath[o.Parent].Id))
                .ToLookup(o => objectsByPath[o.Parent!].Id + ":" + o.Name, StringComparer.Ordinal);
            string? Resolve(string? value, string linkScope, string path)
            {
                Item[] found = [];
                if (!string.IsNullOrEmpty(value))
                {
                    if (version == "3.0") { if (allIds[value].Count() == 1) found = ids[value].Take(2).ToArray(); }
                    else
                    {
                        found = interfaces[value].Take(2).ToArray();
                        if (found.Length == 1 && allIds[objectsByPath[found[0].Parent!].Id!].Count() != 1) found = [];
                    }
                }
                if (found.Length == 1 && found[0].Kind == "ExternalInterface" && found[0].Scope == linkScope) return found[0].Path;
                result.Add("LinkEndpoint", path, "Endpoint is missing, ambiguous, unsupported or in another scope: " + value); return null;
            }
            for (int i = 0; i < result.Links.Count; i++)
            { var link = result.Links[i]; result.Links[i] = link with { TargetA = Resolve(link.SideA, link.Scope, link.Path), TargetB = Resolve(link.SideB, link.Scope, link.Path) }; }
            return result;
        }
        catch (XmlException) { throw new BridgeException("AutomationMlInvalidXml", "Malformed XML or prohibited DTD. No external content was loaded."); }
    }

    public object CompareTo(AutomationMlDocument after)
    {
        bool changed = SourceSha256 != after.SourceSha256;
        var left = Objects.Where(o => Key(o) is not null).ToLookup(o => Key(o)!, StringComparer.Ordinal);
        var right = after.Objects.Where(o => Key(o) is not null).ToLookup(o => Key(o)!, StringComparer.Ordinal);
        var beforePaths = Objects.ToDictionary(o => o.Path); var afterPaths = after.Objects.ToDictionary(o => o.Path);
        var changes = new List<object>(); var seedsBefore = new HashSet<string>(); var seedsAfter = new HashSet<string>();
        if (changed)
        {
            foreach (string key in left.Select(g => g.Key).Union(right.Select(g => g.Key), StringComparer.Ordinal))
            {
                var a = left[key].ToArray(); var b = right[key].ToArray();
                bool ambiguous = a.Length > 1 || b.Length > 1 || Profile != after.Profile;
                if (!ambiguous && a.Length == 1 && b.Length == 1 && a[0].ContentSha256 == b[0].ContentSha256 &&
                    ParentKey(a[0], beforePaths) == ParentKey(b[0], afterPaths)) continue;
                changes.Add(new { key, kind = ambiguous ? "ambiguous" : a.Length == 0 ? "added" : b.Length == 0 ? "removed" : "changed", before = a, after = b });
                seedsBefore.UnionWith(a.Select(o => o.Path)); seedsAfter.UnionWith(b.Select(o => o.Path));
            }
            foreach (var item in Objects.Where(o => Key(o) is null)) { changes.Add(new { key = (string?)null, kind = "unmatched-before", before = new[] { item }, after = Array.Empty<Item>() }); seedsBefore.Add(item.Path); }
            foreach (var item in after.Objects.Where(o => Key(o) is null)) { changes.Add(new { key = (string?)null, kind = "unmatched-after", before = Array.Empty<Item>(), after = new[] { item } }); seedsAfter.Add(item.Path); }
        }
        return new { schemaVersion = 1, beforeSha256 = SourceSha256, afterSha256 = after.SourceSha256, beforeProfile = Profile, afterProfile = after.Profile,
            sourceBytesChanged = changed, matchingReliable = Profile == after.Profile && MatchingReliable && after.MatchingReliable,
            nativeQualified = false, importabilityCertified = false, impactScopeComplete = false,
            wholeDocumentReviewRequired = changed, changes,
            beforeImpacts = Impacts(seedsBefore), afterImpacts = after.Impacts(seedsAfter),
            beforeDiagnostics = Diagnostics, afterDiagnostics = after.Diagnostics, beforeSchemaValid = SchemaValid, afterSchemaValid = after.SchemaValid,
            limitations = Limitations };
    }
    private object[] Impacts(HashSet<string> seeds)
    {
        var edges = Objects.ToDictionary(o => o.Path, _ => new HashSet<string>(), StringComparer.Ordinal);
        foreach (var item in Objects.Where(o => o.Parent is not null)) { edges[item.Path].Add(item.Parent!); edges[item.Parent!].Add(item.Path); }
        foreach (var link in Links.Where(l => l.TargetA is not null && l.TargetB is not null)) { edges[link.TargetA!].Add(link.TargetB!); edges[link.TargetB!].Add(link.TargetA!); }
        foreach (var reference in References.Where(r => r.Owner is not null && r.Target is not null)) edges[reference.Target!].Add(reference.Owner!);
        var seen = new HashSet<string>(seeds); var queue = new Queue<string>(seeds);
        while (queue.TryDequeue(out string? path)) foreach (string target in edges[path]) if (seen.Add(target)) queue.Enqueue(target);
        return Objects.Where(o => seen.Contains(o.Path) && o.Scope == "instance").Select(o => (object)new { o.Path, o.Id, o.Name, o.Kind,
            reason = seeds.Contains(o.Path) ? "changed-or-unmatched-declaration" : "declared-hierarchy-link-or-class-consumer" }).ToArray();
    }
    private static string? ParentKey(Item item, Dictionary<string, Item> items) => item.Parent is null ? null : Key(items[item.Parent]) ?? "unreliable:" + items[item.Parent].Path + ":" + items[item.Parent].Name;
    private static string? Key(Item item) => !string.IsNullOrEmpty(item.Id) ? "id:" + item.Id : item.ClassPath is not null ? item.Kind + ":" + item.ClassPath : null;
    public string ToJson() => Json(this);
    public static string Json(object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json);
        Require(bytes.Length <= MaxReportBytes, "AutomationMlCapacity", "Report exceeds 64 MiB."); return Encoding.UTF8.GetString(bytes);
    }
    private void Add(string code, string path, string message)
    { Require(Diagnostics.Count < 2048, "AutomationMlCapacity", "More than 2048 diagnostics; split the input or repair it before inspecting."); Diagnostics.Add(new(code, path, message)); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool value, string code, string message) { if (!value) throw new BridgeException(code, message); }
}
