#nullable disable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21;

// Exact sc01 captured storage, available only through the owned qualification
// adapter. This matcher never grants production or ordinary typed-edit access.
internal static class TypedGraphicalCapturedStorageTemplate
{
    internal const string ProfileId = "v21u2-component04-state-bool-fbd-global-v1";
    internal const string Cpu = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1";
    internal const string FbName = "TiaMcpComponentGate";
    internal const string TypeName = "TiaMcpComponentState";
    private static readonly XNamespace Interface = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

    internal sealed class Artifact
    {
        internal readonly byte[] Bytes;
        internal readonly string Sha256;
        internal Artifact(byte[] bytes, string sha256)
        { Bytes = bytes; Sha256 = sha256; }
    }
    internal sealed class Profile
    {
        internal readonly string Id;
        internal readonly string CpuIdentifier;
        internal readonly string NativeRelease;
        internal readonly string NativeApiFileVersion;
        internal readonly string Route;
        internal readonly string CallerLanguage;
        internal readonly Artifact ReferenceCallee;
        internal readonly Artifact ReferenceType;
        internal readonly Artifact InstanceDocument;
        internal Profile(string id, string cpuIdentifier, string nativeRelease, string nativeApiFileVersion, string route, string callerLanguage, Artifact referenceCallee, Artifact referenceType, Artifact instanceDocument)
        { Id = id; CpuIdentifier = cpuIdentifier; NativeRelease = nativeRelease; NativeApiFileVersion = nativeApiFileVersion; Route = route; CallerLanguage = callerLanguage; ReferenceCallee = referenceCallee; ReferenceType = referenceType; InstanceDocument = instanceDocument; }
    }
    internal sealed class Binding
    {
        internal readonly string PlannedPath;
        internal readonly string NativePath;
        internal readonly string ScopePath;
        internal readonly string Namespace;
        internal readonly string TypeGuid;
        internal readonly string VersionGuid;
        internal readonly string VersionState;
        internal readonly bool DoNotUse;
        internal readonly bool ScopeComplete;
        internal readonly int NativeUseCount;
        internal readonly string[] RequiredVersionGuids;
        internal readonly Artifact Document;
        internal Binding(string plannedPath, string nativePath, string scopePath, string @namespace, string typeGuid, string versionGuid, string versionState, bool doNotUse, bool scopeComplete, int nativeUseCount, string[] requiredVersionGuids, Artifact document)
        { PlannedPath = plannedPath; NativePath = nativePath; ScopePath = scopePath; Namespace = @namespace; TypeGuid = typeGuid; VersionGuid = versionGuid; VersionState = versionState; DoNotUse = doNotUse; ScopeComplete = scopeComplete; NativeUseCount = nativeUseCount; RequiredVersionGuids = requiredVersionGuids; Document = document; }
    }

    internal static XDocument Create(string name, int number, Profile profile, string plcPath,
        string cpuIdentifier, string nativeRelease, string nativeApiFileVersion, string route, string callerLanguage, Binding callee, Binding type)
    {
        if (!Regex.IsMatch(name ?? "", @"^[A-Za-z_][A-Za-z0-9_]{0,79}$") || number < 1 || number > 65535)
            Fail("An absent safe target name and externally reserved number in 1..65535 are required.");
        if (profile == null || profile.Id != ProfileId || profile.CpuIdentifier != Cpu || cpuIdentifier != Cpu ||
            profile.NativeRelease != "V21 Update 2" || nativeRelease != profile.NativeRelease ||
            profile.NativeApiFileVersion != "2100.0.121.1" || nativeApiFileVersion != profile.NativeApiFileVersion ||
            profile.CallerLanguage != "FBD" || callerLanguage != profile.CallerLanguage ||
            profile.Route != "standard" || route != profile.Route)
            Fail("Only the exact captured release/CPU/standard-route profile is eligible.");
        if (string.IsNullOrWhiteSpace(plcPath) || !plcPath.StartsWith("/projects/", StringComparison.Ordinal) ||
            plcPath.EndsWith("/", StringComparison.Ordinal) || plcPath.Contains("//") || plcPath.Split('/').Any(p => p == "." || p == ".."))
            Fail("A complete exact current PLC scope path is required.");
        // Library identities come from the exact reviewed plan (the adapter binds
        // them through FindVersion); storage eligibility is the complete content
        // equality below, so only identity shape and the one-edge closure are fixed here.
        ValidateBinding(callee, plcPath, "/blocks/" + FbName, new[] { type?.VersionGuid });
        ValidateBinding(type, plcPath, "/types/" + TypeName, Array.Empty<string>());
        var identities = new[] { callee.TypeGuid, callee.VersionGuid, type.TypeGuid, type.VersionGuid };
        if (identities.Any(id => !Guid.TryParse(id, out var parsed) || parsed == Guid.Empty || parsed.ToString("D") != id) ||
            identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
            Fail("Committed library identities must be complete, canonical and distinct.");

        var reference = Read(profile.ReferenceCallee); var referenceType = Read(profile.ReferenceType);
        var source = Read(callee.Document); var sourceType = Read(type.Document);
        var result = Read(profile.InstanceDocument);
        Preserve(reference, source); Preserve(referenceType, sourceType);
        var fb = Object(source, "SW.Blocks.FB"); var udt = Object(sourceType, "SW.Types.PlcStruct");
        var db = Object(result, "SW.Blocks.InstanceDB");
        var fa = One(fb, "AttributeList"); var ta = One(udt, "AttributeList"); var da = One(db, "AttributeList");
        Scalar(fa, "Name", FbName); Scalar(fa, "Namespace", ""); Scalar(fa, "MemoryLayout", "Optimized"); Scalar(fa, "ProgrammingLanguage", "SCL");
        Scalar(ta, "Name", TypeName); Scalar(ta, "Namespace", "");
        Scalar(da, "InstanceOfName", FbName); Scalar(da, "InstanceOfType", "FB"); Scalar(da, "Namespace", "");
        Scalar(da, "ProgrammingLanguage", "DB");
        if (da.Elements("MemoryLayout").Any()) Fail("The captured DB omits MemoryLayout; no inferred storage property is added.");
        if (da.Elements("InstanceOfNumber").Any()) Fail("Another native instance binding format needs its own proof.");
        if (source.Descendants().Concat(sourceType.Descendants()).Concat(result.Descendants()).Any(e => e.Name.LocalName == "StartValue" || e.Name.LocalName == "DefaultValue"))
            Fail("This captured profile serializes no start/default values; different value layouts require separate proof.");
        StorageShape(fa, false); StorageShape(da, true);
        var typeSections = Sections(ta);
        var members = typeSections.Elements().ToArray();
        if (members.Length != 1 || members[0].Name != Interface + "Section" || (string)members[0].Attribute("Name") != "None")
            Fail("Only the captured one-member UDT is eligible.");
        RequireMembers(members[0], ("Active", "Bool"));
        if (members[0].Descendants(Interface + "Member").Count() != 1) Fail("Nested dependent types are not qualified.");
        string originalName = Scalar(da, "Name");
        string originalNumber = Scalar(da, "Number");
        string auto = Scalar(da, "AutoNumber");
        if (!Regex.IsMatch(originalName, @"^[A-Za-z_][A-Za-z0-9_]{0,79}$") ||
            !int.TryParse(originalNumber, out var capturedNumber) || capturedNumber < 1 || capturedNumber > 65535 ||
            (auto != "true" && auto != "false")) Fail("Captured target fields are malformed.");
        // Complete stored native document is cloned by Read; no member, default,
        // ID, comment, unknown node or instruction is synthesized or remapped.
        da.Element("Name")!.Value = name!;
        da.Element("Number")!.Value = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        da.Element("AutoNumber")!.Value = "false";
        da.Element("InstanceOfName")!.Value = FbName;
        return result;
    }

    private static void ValidateBinding(Binding binding, string plcPath, string suffix, string[] dependencies)
    {
        if (binding == null || binding.PlannedPath != plcPath + suffix || binding.NativePath != binding.PlannedPath ||
            binding.ScopePath != plcPath || binding.Namespace != "" || !binding.ScopeComplete || binding.NativeUseCount != 1 ||
            binding.VersionState != "Committed" || binding.DoNotUse || binding.RequiredVersionGuids == null ||
            !binding.RequiredVersionGuids.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(dependencies.OrderBy(x => x, StringComparer.Ordinal)))
            Fail("Exact planned/native path, scope, committed library identity and dependency closure must agree.");
    }
    private static XDocument Read(Artifact artifact)
    {
        if (artifact?.Bytes == null || artifact.Bytes.Length == 0 || artifact.Bytes.Length > 16 * 1024 * 1024 ||
            !Regex.IsMatch(artifact.Sha256 ?? "", "^[a-f0-9]{64}$") ||
            Hash(artifact.Bytes) != artifact.Sha256)
            Fail("Every source and captured artifact must match its exact pinned raw hash.");
        string text = new UTF8Encoding(false, true).GetString(artifact!.Bytes).TrimStart('\uFEFF');
        var document = EngineeringSources.ReadXml(text);
        if (document.Root?.Name != "Document" || document.Root.Elements("DocumentInfo").Any())
            Fail("DocumentInfo.None complete native exports are required; provenance is never stripped here.");
        if (document.Root.Elements("Engineering").Count() != 1 || document.Root.Element("Engineering")?.Attribute("version")?.Value != "V21")
            Fail("An exact V21 native envelope is required.");
        var ids = document.Descendants().Attributes("ID").Select(a => a.Value).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            Fail("Native object IDs must remain complete and unique.");
        return document;
    }
    private static XElement Object(XDocument document, string kind)
    {
        var objects = document.Root!.Elements().Where(e => e.Name != "Engineering").ToArray();
        if (objects.Length != 1 || objects[0].Name != kind || objects[0].Attribute("ID") == null)
            Fail("A single exact native object of kind " + kind + " is required.");
        return objects[0];
    }
    private static XElement Sections(XElement attributes) => One(One(attributes, "Interface"), Interface + "Sections");
    private static void StorageShape(XElement attributes, bool instance)
    {
        var sections = Sections(attributes).Elements().ToArray();
        string[] names = instance ? new[] { "Input", "Output", "InOut", "Static" } : new[] { "Input", "Output", "InOut", "Static", "Temp", "Constant" };
        if (!sections.Select(e => (string)e.Attribute("Name")).SequenceEqual(names) || sections.Any(e => e.Name != Interface + "Section"))
            Fail("Only the exact captured persistent section topology is eligible.");
        RequireMembers(sections[0], ("Enable", "Bool")); RequireMembers(sections[1], ("Active", "Bool"));
        RequireMembers(sections[2]); RequireMembers(sections[3], ("State", "\"" + TypeName + "\""));
        foreach (var empty in sections.Skip(4)) RequireMembers(empty);
        var state = sections[3].Elements(Interface + "Member").Single();
        var nested = One(One(state, Interface + "Sections"), Interface + "Section");
        if ((string)nested.Attribute("Name") != "None") Fail("The native UDT storage section differs.");
        RequireMembers(nested, ("Active", "Bool"));
        if (Sections(attributes).Descendants(Interface + "Member").Count() != 4)
            Fail("Arrays, extra members and additional nested storage are not qualified.");
    }
    private static void RequireMembers(XElement section, params (string Name, string Type)[] expected)
    {
        var members = section.Elements().ToArray();
        if (members.Length != expected.Length || members.Where((m, i) => m.Name != Interface + "Member" ||
            (string)m.Attribute("Name") != expected[i].Name || (string)m.Attribute("Datatype") != expected[i].Type).Any())
            Fail("Only the exact captured member names, order and datatypes are eligible.");
    }
    private static XElement One(XElement parent, XName name)
    {
        var values = parent.Elements(name).ToArray();
        if (values.Length != 1) Fail("Required native field is missing or ambiguous: " + name);
        return values[0];
    }
    private static string Scalar(XElement parent, string name, string expected = null)
    {
        var value = One(parent, name);
        if (value.HasAttributes || value.Nodes().Any(n => !(n is XText)) || (expected != null && value.Value != expected))
            Fail("Native scalar differs: " + name);
        return value.Value;
    }
    private static void Preserve(XDocument expected, XDocument actual)
    {
        // This separate exact-reference route has no generic preservation
        // normalization. Preserve all whitespace, namespace declarations and
        // XML declarations as well as IDs, comments and unknown payload.
        if (!XNode.DeepEquals(expected, actual) || expected.Declaration?.ToString() != actual.Declaration?.ToString())
            Fail("Complete reference callee/type content differs, including text, namespace bindings, IDs, comments or unknown XML.");
    }
    internal static string SerializePreparedDocument(XDocument document)
    {
        // XDocument.ToString omits the declaration but retains the native
        // whitespace before the root. Adding a separator would create a new
        // text node that TIA removes on export, breaking exact readback.
        return (document.Declaration?.ToString() ?? "") + document.ToString(SaveOptions.DisableFormatting);
    }
    internal static void RequireReadback(XDocument expected, XDocument actual)
    {
        if (!XNode.DeepEquals(expected, actual) || expected.Declaration?.ToString() != actual.Declaration?.ToString())
            throw new BridgeException("CapturedStorageReadbackMismatch", "The full captured storage readback differs in nodes, text, namespace declarations or XML declaration; recovery is required.");
    }
    private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static void Fail(string message) => throw new BridgeException("CapturedStorageProfileUnavailable", message);
}
