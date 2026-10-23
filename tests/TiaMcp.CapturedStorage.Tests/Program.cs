using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;
using static TiaMcp.Openness.V21.CapturedStorageTemplate;

// Synthetic documents test contract mechanics only. No synthetic XML becomes
// a native profile, and this executable cannot call Siemens APIs.
const string plc = "/projects/FreshConsumer/devices/Station/items/PLC/plc/PLC";
XNamespace ns = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
var tests = new List<(string Name, Action Body)>();
Artifact ArtifactOf(XDocument doc)
{
    byte[] bytes = Encoding.UTF8.GetBytes((doc.Declaration == null ? "" : doc.Declaration + "\n") + doc.ToString(SaveOptions.DisableFormatting));
    return new(bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
}
XElement Member(string name, string type) => new(ns + "Member", new XAttribute("Name", name), new XAttribute("Datatype", type));
XElement State() => new(ns + "Member", new XAttribute("Name", "State"), new XAttribute("Datatype", "\"" + TypeName + "\""),
    new XElement(ns + "Sections", new XElement(ns + "Section", new XAttribute("Name", "None"), Member("Active", "Bool"))));
XElement StorageSections(bool db) => new(ns + "Sections",
    new XElement(ns + "Section", new XAttribute("Name", "Input"), Member("Enable", "Bool")),
    new XElement(ns + "Section", new XAttribute("Name", "Output"), Member("Active", "Bool")),
    new XElement(ns + "Section", new XAttribute("Name", "InOut")),
    new XElement(ns + "Section", new XAttribute("Name", "Static"), State()),
    db ? null : new[] { new XElement(ns + "Section", new XAttribute("Name", "Temp")), new XElement(ns + "Section", new XAttribute("Name", "Constant")) });
XDocument Document(string kind, params object[] content) => new(new XElement("Document", new XElement("Engineering", new XAttribute("version", "V21")),
    new XElement(kind, new XAttribute("ID", "0"), new XElement("AttributeList", content))));
var fb = Document("SW.Blocks.FB", new XElement("Name", FbName), new XElement("Namespace"), new XElement("MemoryLayout", "Optimized"),
    new XElement("ProgrammingLanguage", "SCL"), new XElement("Interface", StorageSections(false)), new XElement("Number", "2"), new XElement("AutoNumber", "true"));
var typeDoc = Document("SW.Types.PlcStruct", new XElement("Name", TypeName), new XElement("Namespace"),
    new XElement("Interface", new XElement(ns + "Sections", new XElement(ns + "Section", new XAttribute("Name", "None"), Member("Active", "Bool")))));
var db = Document("SW.Blocks.InstanceDB", new XElement("Name", "CapturedDb"), new XElement("Namespace"),
    new XElement("InstanceOfName", FbName), new XElement("InstanceOfType", "FB"), new XElement("ProgrammingLanguage", "DB"),
    new XElement("AutoNumber", "true"), new XElement("Number", "3"), new XElement("Interface", StorageSections(true)),
    new XComment("captured comment retained"), new XElement("UnmappedNative", new XAttribute("ID", "9"), "value"));
Profile profile = new(ProfileId, Cpu, "V21 Update 2", "2100.0.121.1", "standard", "FBD", ArtifactOf(fb), ArtifactOf(typeDoc), ArtifactOf(db));
Binding MakeBinding(string suffix, string typeGuid, string version, string[] deps, XDocument doc) => new(plc + suffix, plc + suffix, plc, "",
    typeGuid, version, "Committed", false, true, 1, deps, ArtifactOf(doc));
Binding boundFb = MakeBinding("/blocks/" + FbName, FbTypeGuid, FbVersionGuid, new[] { UdtVersionGuid }, fb);
Binding boundType = MakeBinding("/types/" + TypeName, UdtTypeGuid, UdtVersionGuid, Array.Empty<string>(), typeDoc);
XDocument Build(Profile? chosen = null, Binding? callee = null, Binding? udt = null, string cpu = Cpu, string release = "V21 Update 2", string route = "standard", string language = "FBD", string api = "2100.0.121.1") =>
    Create("NewInstance", 123, chosen ?? profile, plc, cpu, release, api, route, language, callee ?? boundFb, udt ?? boundType);
void Reject(Action action, string code = "CapturedStorageProfileUnavailable")
{
    try { action(); }
    catch (BridgeException error) when (error.Code == code) { return; }
    throw new Exception("Expected strict rejection: " + code);
}
void Require(bool result, string why) { if (!result) throw new Exception(why); }
XDocument Mutate(XDocument original, Action<XDocument> change) { var result = new XDocument(original); change(result); return result; }
tests.Add(("Complete native template clone changes only allowed direct fields", () => {
    string before = db.ToString(SaveOptions.DisableFormatting);
    var result = Build(); var expected = new XDocument(db); var attrs = expected.Root!.Element("SW.Blocks.InstanceDB")!.Element("AttributeList")!;
    attrs.Element("Name")!.Value = "NewInstance"; attrs.Element("Number")!.Value = "123"; attrs.Element("AutoNumber")!.Value = "false";
    EngineeringSources.RequireNativePreservation(expected, result);
    Require(before == db.ToString(SaveOptions.DisableFormatting), "Capture was mutated");
    Require(result.Descendants("UnmappedNative").Single().Attribute("ID")!.Value == "9", "Unknown payload/ID lost");
}));
tests.Add(("Exact UDT identity tuple required even with equal content", () => {
    Reject(() => Build(udt: boundType with { TypeGuid = FbTypeGuid }));
    Reject(() => Build(udt: boundType with { VersionGuid = FbVersionGuid }));
    Reject(() => Build(callee: boundFb with { TypeGuid = UdtTypeGuid }));
    Reject(() => Build(callee: boundFb with { VersionGuid = UdtVersionGuid }));
}));
tests.Add(("Current plan and native object paths are exact", () => {
    Reject(() => Build(udt: boundType with { NativePath = plc + "/types/Folder/" + TypeName }));
    Reject(() => Build(udt: boundType with { PlannedPath = plc + "/types/Folder/" + TypeName }));
    Reject(() => Build(udt: boundType with { ScopePath = plc + "/units/Unit" }));
    Reject(() => Build(udt: boundType with { Namespace = "Alternate" }));
}));
tests.Add(("Committed usable complete native identity required", () => {
    Reject(() => Build(udt: boundType with { ScopeComplete = false }));
    Reject(() => Build(udt: boundType with { NativeUseCount = 2 }));
    Reject(() => Build(udt: boundType with { NativeUseCount = 0 }));
    Reject(() => Build(callee: boundFb with { VersionState = "InWork" }));
    Reject(() => Build(callee: boundFb with { DoNotUse = true }));
}));
tests.Add(("Exact one-edge dependency closure", () => {
    Reject(() => Build(callee: boundFb with { RequiredVersionGuids = Array.Empty<string>() }));
    Reject(() => Build(callee: boundFb with { RequiredVersionGuids = new[] { UdtVersionGuid, UdtVersionGuid } }));
    Reject(() => Build(udt: boundType with { RequiredVersionGuids = new[] { FbVersionGuid } }));
}));
tests.Add(("Full callee tree binds implementation IDs comments unknown nodes", () => {
    foreach (Action<XDocument> change in new Action<XDocument>[] {
        d => d.Root!.Element("SW.Blocks.FB")!.SetAttributeValue("ID", "8"),
        d => d.Root!.Add(new XComment("different comment")),
        d => d.Root!.Element("SW.Blocks.FB")!.Add(new XElement("UnexpectedPayload", "value")),
        d => d.Descendants(ns + "Member").First().SetAttributeValue("Accessibility", "Private"),
        d => d.Descendants("Number").Single().Value = "99" })
        Reject(() => Build(callee: boundFb with { Document = ArtifactOf(Mutate(fb, change)) }));
}));
tests.Add(("UDT full content binds attributes and nested member", () => {
    Reject(() => Build(udt: boundType with { Document = ArtifactOf(Mutate(typeDoc, d => d.Descendants(ns + "Member").Single().SetAttributeValue("Datatype", "Int"))) }));
    Reject(() => Build(udt: boundType with { Document = ArtifactOf(Mutate(typeDoc, d => d.Descendants(ns + "Member").Single().Add(new XComment("changed")))) }));
}));
tests.Add(("Significant whitespace processing instructions and QName bindings remain content", () => {
    var rich = Mutate(fb, d => d.Root!.Element("SW.Blocks.FB")!.Add(new XElement("FutureNative",
        new XAttribute(XNamespace.Xml + "space", "preserve"), new XAttribute(XNamespace.Xmlns + "kind", "urn:captured"),
        new XAttribute("QualifiedKind", "kind:Structured"), new XProcessingInstruction("captured", "payload"), "  value  ")));
    var pinned = ArtifactOf(rich); var chosen = profile with { ReferenceCallee = pinned }; var current = boundFb with { Document = pinned };
    Build(chosen, current);
    foreach (Action<XDocument> change in new Action<XDocument>[] {
        d => d.Descendants("FutureNative").Single().SetAttributeValue(XNamespace.Xmlns + "kind", "urn:different"),
        d => d.DescendantNodes().OfType<XProcessingInstruction>().Single().Data = "changed",
        d => d.Descendants("FutureNative").Single().Nodes().OfType<XText>().Single().Value = " value " })
        Reject(() => Build(chosen, current with { Document = ArtifactOf(Mutate(rich, change)) }));
}));
tests.Add(("Exact reference comparison retains unused and default namespace bindings and formatting", () => {
    var rich = Mutate(fb, d => d.Root!.Element("SW.Blocks.FB")!.Add(new XElement("FutureNative",
        new XAttribute(XNamespace.Xmlns + "unused", "urn:captured"), new XElement(XNamespace.Get("urn:context") + "Value",
            new XAttribute("xmlns", "urn:context"), "UnprefixedType"))));
    var pinned = ArtifactOf(rich); var chosen = profile with { ReferenceCallee = pinned }; var current = boundFb with { Document = pinned };
    Build(chosen, current);
    foreach (Action<XDocument> change in new Action<XDocument>[] {
        d => d.Descendants("FutureNative").Single().SetAttributeValue(XNamespace.Xmlns + "unused", "urn:changed"),
        d => d.Root!.AddFirst(new XText("\n  ")),
        d => d.Declaration = new XDeclaration("1.0", "utf-8", "yes") })
        Reject(() => Build(chosen, current with { Document = ArtifactOf(Mutate(rich, change)) }));
    // Adding a prefixed namespace declaration whose lexical use is opaque also
    // changes the complete tree, even if element expanded names are unchanged.
    Reject(() => Build(chosen, current with { Document = ArtifactOf(Mutate(rich,
        d => d.Descendants("FutureNative").Single().SetAttributeValue(XNamespace.Xmlns + "other", "urn:other"))) }));
    var defaultBinding = Mutate(fb, d => d.Root!.Element("SW.Blocks.FB")!.Add(XElement.Parse(
        "<q:FutureNative xmlns:q='urn:object' xmlns='urn:captured'>UnprefixedType</q:FutureNative>")));
    var defaultPinned = ArtifactOf(defaultBinding);
    Reject(() => Build(profile with { ReferenceCallee = defaultPinned }, boundFb with { Document = ArtifactOf(Mutate(defaultBinding,
        d => d.Descendants(XNamespace.Get("urn:object") + "FutureNative").Single().SetAttributeValue("xmlns", "urn:changed"))) }));
}));
tests.Add(("Source starts and arrays rejected even in self-consistent unapproved input", () => {
    foreach (Action<XDocument> change in new Action<XDocument>[] {
        d => d.Descendants(ns + "Member").First().Add(new XElement(ns + "StartValue", "true")),
        d => d.Descendants(ns + "Member").First().Add(new XElement(ns + "DefaultValue", "true")),
        d => d.Descendants(ns + "Member").First().SetAttributeValue("Datatype", "Array[0..1] of Bool"),
        d => d.Descendants(ns + "Section").First(s => (string?)s.Attribute("Name") == "Static").Add(Member("Other", "Bool")) }) {
        var altered = ArtifactOf(Mutate(fb, change));
        Reject(() => Build(profile with { ReferenceCallee = altered }, boundFb with { Document = altered }));
    }
}));
tests.Add(("Nested dependent UDT rejected even with an updated reference hash", () => {
    var altered = ArtifactOf(Mutate(typeDoc, d => d.Descendants(ns + "Member").Single().Add(new XElement(ns + "Sections",
        new XElement(ns + "Section", new XAttribute("Name", "None"), Member("Nested", "Bool"))))));
    Reject(() => Build(profile with { ReferenceType = altered }, udt: boundType with { Document = altered }));
}));
tests.Add(("Complete profile and observed artifact hashes checked", () => {
    foreach (var field in new[] { "fb", "type", "db" }) {
        var original = field == "fb" ? profile.ReferenceCallee : field == "type" ? profile.ReferenceType : profile.InstanceDocument;
        var drift = original with { Bytes = original.Bytes.Concat(new byte[] { 32 }).ToArray() };
        Reject(() => Build(field == "fb" ? profile with { ReferenceCallee = drift } : field == "type" ? profile with { ReferenceType = drift } : profile with { InstanceDocument = drift }));
    }
    Reject(() => Build(udt: boundType with { Document = boundType.Document with { Sha256 = new string('0', 64) } }));
}));
tests.Add(("Storage family namespace binding fields and ID uniqueness remain strict", () => {
    foreach (Action<XDocument> change in new Action<XDocument>[] {
        d => d.Descendants("Namespace").Single().Value = "Alternate",
        d => d.Descendants("InstanceOfName").Single().Value = "AnotherFB",
        d => d.Descendants("AttributeList").First().Add(new XElement("InstanceOfNumber", "2")),
        d => d.Descendants("AttributeList").First().Add(new XElement("Name", "Duplicate")),
        d => d.Descendants("UnmappedNative").Single().SetAttributeValue("ID", "0") })
        Reject(() => Build(profile with { InstanceDocument = ArtifactOf(Mutate(db, change)) }));
}));
tests.Add(("Unobserved DB defaults and absent MemoryLayout remain absent", () => {
    var stored = Mutate(db, d => d.Descendants(ns + "Member").Last().Add(new XElement(ns + "StartValue", "false")));
    Reject(() => Build(profile with { InstanceDocument = ArtifactOf(stored) }));
    Reject(() => Build(profile with { InstanceDocument = ArtifactOf(Mutate(db,
        d => d.Descendants("AttributeList").First().Add(new XElement("MemoryLayout", "Optimized")))) }));
    Require(!Build().Descendants(ns + "StartValue").Any(), "An omitted serialized value was inferred");
    Require(!Build().Descendants("MemoryLayout").Any(), "An omitted memory layout property was inferred");
}));
tests.Add(("Unproved release CPU software-unit and namespace routes reject", () => {
    Reject(() => Build(cpu: "different")); Reject(() => Build(release: "V21 Update 3"));
    Reject(() => Build(language: "LAD")); Reject(() => Build(api: "2100.0.999.1"));
    Reject(() => Build(route: "softwareUnit")); Reject(() => Build(route: "namespace"));
    Reject(() => Build(profile with { Id = "another-profile" }));
}));
tests.Add(("Document provenance is required absent rather than normalized away", () => {
    var altered = ArtifactOf(Mutate(fb, d => d.Root!.AddFirst(new XElement("DocumentInfo", "provenance"))));
    Reject(() => Build(profile with { ReferenceCallee = altered }, boundFb with { Document = altered }));
}));
tests.Add(("Existing scalar builder still rejects this nested callee", () => {
    Reject(() => TypedGraphicalInstanceTemplate.Create("NewInstance", 123, fb, db), "GraphicalInstanceTemplateUnavailable");
}));
tests.Add(("Changed dependency after prepare is rejected by repeated contract evaluation", () => {
    Build();
    Reject(() => Build(udt: boundType with { Document = ArtifactOf(Mutate(typeDoc, d => d.Descendants(ns + "Member").Single().SetAttributeValue("Datatype", "Byte"))) }));
    Reject(() => Build(callee: boundFb with { VersionGuid = UdtVersionGuid }));
}));
tests.Add(("Unsafe target names and numbers fail before document creation", () => {
    foreach (string name in new[] { "", "Nested.Name", "A/B" }) Reject(() => Create(name, 123, profile, plc, Cpu, "V21 Update 2", "2100.0.121.1", "standard", "FBD", boundFb, boundType));
    foreach (int number in new[] { 0, 65536 }) Reject(() => Create("NewInstance", number, profile, plc, Cpu, "V21 Update 2", "2100.0.121.1", "standard", "FBD", boundFb, boundType));
}));
tests.Add(("Accepted sc01 derived FB UDT and complete DB match the exact product profile", () => {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory != null && !File.Exists(Path.Combine(directory.FullName, "fixtures", "typed-graphical", "component04-storage-profile.json"))) directory = directory.Parent;
    Require(directory != null, "Frozen captured storage assets missing");
    string root = Path.Combine(directory!.FullName, "fixtures", "typed-graphical");
    string manifestPath = Path.Combine(root, "component04-storage-profile.json");
    var rawManifest = File.ReadAllBytes(manifestPath);
    Require(Convert.ToHexString(SHA256.HashData(rawManifest)).ToLowerInvariant() == "642a0895d7be9ac45c135e4704a0822446dcb6a183c228ddd9f38a426357afa0", "Trusted manifest changed");
    using var json = System.Text.Json.JsonDocument.Parse(rawManifest); var descriptor = json.RootElement;
    Artifact ReadCaptured(string key) { var asset = descriptor.GetProperty("artifacts").GetProperty(key); return new(File.ReadAllBytes(Path.Combine(root, asset.GetProperty("path").GetString()!)), asset.GetProperty("sha256").GetString()!); }
    var exact = profile with { ReferenceCallee = ReadCaptured("callee"), ReferenceType = ReadCaptured("type"), InstanceDocument = ReadCaptured("instance") };
    var actual = Build(exact, boundFb with { Document = exact.ReferenceCallee }, boundType with { Document = exact.ReferenceType });
    var expected = EngineeringSources.ReadXml(Encoding.UTF8.GetString(exact.InstanceDocument.Bytes).TrimStart('\uFEFF'));
    var attrs = expected.Root!.Element("SW.Blocks.InstanceDB")!.Element("AttributeList")!;
    attrs.Element("Name")!.Value = "NewInstance"; attrs.Element("Number")!.Value = "123"; attrs.Element("AutoNumber")!.Value = "false";
    Require(XNode.DeepEquals(expected, actual), "Complete captured DB changed outside three actual target substitutions");
    Require(expected.Declaration!.ToString() == actual.Declaration!.ToString(), "XML declaration changed");
    var roundtrip = EngineeringSources.ReadXml(TypedGraphicalCapturedStorageTemplate.SerializePreparedDocument(actual));
    TypedGraphicalCapturedStorageTemplate.RequireReadback(expected, roundtrip);
    // Reproduce the failed native06 producer: a second separator changed the
    // retained prolog text even though every node under Document was equal.
    var duplicateSeparator = EngineeringSources.ReadXml(actual.Declaration + "\r\n" + actual.ToString(SaveOptions.DisableFormatting));
    Reject(() => TypedGraphicalCapturedStorageTemplate.RequireReadback(expected, duplicateSeparator), "CapturedStorageReadbackMismatch");
    Require(!actual.Descendants("MemoryLayout").Any() && !actual.Descendants(ns + "StartValue").Any() && !actual.Descendants(ns + "DefaultValue").Any(), "An absent serialized field was inferred");
}));
tests.Add(("Committed component recipe selects the compatible captured storage and native FC route", () => {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory != null && !File.Exists(Path.Combine(directory.FullName, "fixtures", "libraries", "component-qualification", "manifest.json"))) directory = directory.Parent;
    Require(directory != null, "Committed component recipe missing");
    string root = directory!.FullName;
    string typed = Path.Combine(root, "fixtures", "typed-graphical");
    var descriptor = JObject.Parse(File.ReadAllText(Path.Combine(typed, "component04-storage-profile.json")));
    var evidence = descriptor["callerLanguageEvidence"]!;
    byte[] recipeBytes = File.ReadAllBytes(Path.Combine(root, evidence.Value<string>("recipePath")!));
    Require(Convert.ToHexString(SHA256.HashData(recipeBytes)).ToLowerInvariant() == evidence.Value<string>("recipeSha256"), "Recorded recipe bytes changed");
    var recipe = JObject.Parse(Encoding.UTF8.GetString(recipeBytes));
    Require(recipe.Value<string>("id") == evidence.Value<string>("recipeId") && recipe.Value<string>("callerMode") == "createStandaloneFc", "Captured profile is for the exact standalone recipe");
    string language = recipe.Value<string>("language")!;
    Require(language == descriptor.Value<string>("callerLanguage"), "Actual harness recipe and storage profile select different caller languages");
    Require(descriptor.Value<string>("route") == "standard" && descriptor.Value<string>("namespace") == "", "Recipe uses the captured standard empty namespace");
    var catalog = JObject.Parse(File.ReadAllText(Path.Combine(typed, "profiles.v1.json")));
    var creation = ((JArray)catalog["creationTemplates"]!).OfType<JObject>().Single(c => c.Value<string>("cpuFamily") == descriptor.Value<string>("cpuIdentifier") &&
        c.Value<string>("route") == descriptor.Value<string>("route") && c.Value<string>("language") == language && c.Value<string>("blockType") == "FC");
    Require(creation.Value<string>("id") == evidence.Value<string>("creationProfileId"), "An unreviewed creation route was selected");
    byte[] callerBytes = File.ReadAllBytes(Path.Combine(typed, creation.Value<string>("path")!));
    string callerHash = Convert.ToHexString(SHA256.HashData(callerBytes)).ToLowerInvariant();
    Require(callerHash == creation.Value<string>("sha256") && callerHash == evidence.Value<string>("creationTemplateSha256"), "Exact native FC capture changed");
    var callerXml = EngineeringSources.ReadXml(Encoding.UTF8.GetString(callerBytes).TrimStart('\uFEFF'));
    Require(EngineeringSources.BlockDeclarations(callerXml).Single().Language == language &&
        callerXml.Root!.Element("SW.Blocks.FC")!.Element("AttributeList")!.Element("Namespace")!.Value == "", "Native FC template language/namespace differs");
    Artifact ReadCaptured(string key) { var row = descriptor["artifacts"]![key]!; return new(File.ReadAllBytes(Path.Combine(typed, row.Value<string>("path")!)), row.Value<string>("sha256")!); }
    var exact = new Profile(descriptor.Value<string>("id")!, descriptor.Value<string>("cpuIdentifier")!, descriptor.Value<string>("nativeRelease")!,
        descriptor.Value<string>("nativeApiFileVersion")!, descriptor.Value<string>("route")!, descriptor.Value<string>("callerLanguage")!,
        ReadCaptured("callee"), ReadCaptured("type"), ReadCaptured("instance"));
    var currentFb = boundFb with { Document = exact.ReferenceCallee }; var currentType = boundType with { Document = exact.ReferenceType };
    var built = Create(recipe.Value<string>("instance")!, 123, exact, plc, exact.CpuIdentifier, exact.NativeRelease, exact.NativeApiFileVersion, exact.Route, language, currentFb, currentType);
    Require(built.Root!.Element("SW.Blocks.InstanceDB")!.Element("AttributeList")!.Element("ProgrammingLanguage")!.Value == "DB", "Storage is the captured DB family");
    Reject(() => Create(recipe.Value<string>("instance")!, 123, exact with { CallerLanguage = "LAD" }, plc, exact.CpuIdentifier, exact.NativeRelease, exact.NativeApiFileVersion, exact.Route, language, currentFb, currentType));
}));
tests.Add(("Prepared storage serialization preserves declaration prolog epilog and opaque payload", () => {
    foreach (string declaration in new[] { "", "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>" })
    foreach (string prolog in new[] { "", "\r\n", "\r\n\r\n", "\r\n<?native opaque?>\r\n<!-- retained -->\r\n" }) {
        var expected = EngineeringSources.ReadXml(declaration + prolog +
            "<Document xmlns:q=\"urn:captured\"><Future xml:space=\"preserve\" Kind=\"q:Type\"><![CDATA[  opaque  ]]><!-- inside --> value </Future></Document>\r\n<!-- after -->\r\n");
        var before = new XDocument(expected);
        var actual = EngineeringSources.ReadXml(TypedGraphicalCapturedStorageTemplate.SerializePreparedDocument(expected));
        TypedGraphicalCapturedStorageTemplate.RequireReadback(before, actual);
        TypedGraphicalCapturedStorageTemplate.RequireReadback(before, expected);
    }
}));
tests.Add(("Strict captured storage readback rejects namespace whitespace and declaration drift", () => {
    var expected = Build(); TypedGraphicalCapturedStorageTemplate.RequireReadback(expected, new XDocument(expected));
    foreach (Action<XDocument> change in new Action<XDocument>[] {
        d => d.Root!.AddFirst(new XText("\n ")),
        d => d.Root!.AddBeforeSelf(new XText("\n")),
        d => d.Descendants("UnmappedNative").Single().SetAttributeValue(XNamespace.Xmlns + "unused", "urn:unexpected"),
        d => d.Declaration = new XDeclaration("1.0", "utf-8", "yes"),
        d => d.Descendants("UnmappedNative").Single().Add(new XComment("changed")) })
        Reject(() => TypedGraphicalCapturedStorageTemplate.RequireReadback(expected, Mutate(expected, change)), "CapturedStorageReadbackMismatch");
}));
tests.Add(("Exact archive plan rejects new identities source content or broadened gates", () => {
    var pins = new JArray(new JObject { ["typeGuid"] = FbTypeGuid, ["versionGuid"] = FbVersionGuid, ["typeName"] = FbName, ["contentSha256"] = "source",
        ["state"] = "Committed", ["doNotUse"] = false, ["requiredVersionGuids"] = new JArray(UdtVersionGuid), ["contentPolicy"] = new JObject { ["sourceOracleSha256"] = "oracle" } });
    var pin = (JObject)pins[0]; var expectedPin = (JObject)pin.DeepClone(); expectedPin["sourceOracleSha256"] = "oracle";
    var descriptor = new JObject { ["qualificationOnly"] = true, ["productionApplySupported"] = false, ["sourceArchiveSha256"] = "archive", ["sourcePins"] = new JArray(expectedPin) };
    CapturedStorageContract.RequirePlan(descriptor, pins, "archive");
    foreach (Action<JObject> change in new Action<JObject>[] {
        p => p["versionGuid"] = UdtVersionGuid, p => p["typeGuid"] = UdtTypeGuid, p => p["contentSha256"] = "changed", p => p["contentPolicy"]!["sourceOracleSha256"] = "changed",
        p => p["requiredVersionGuids"] = new JArray(), p => p["state"] = "InWork", p => p["doNotUse"] = true }) {
        var changed = (JObject)pin.DeepClone(); change(changed);
        Reject(() => CapturedStorageContract.RequirePlan(descriptor, new JArray(changed), "archive"), "StaleCapturedStorageContract");
    }
    Reject(() => CapturedStorageContract.RequirePlan(descriptor, pins, "different-archive"), "StaleCapturedStorageContract");
    foreach (string field in new[] { "qualificationOnly", "productionApplySupported" }) {
        var changed = (JObject)descriptor.DeepClone(); changed[field] = !changed.Value<bool>(field);
        Reject(() => CapturedStorageContract.RequirePlan(changed, pins, "archive"), "StaleCapturedStorageContract");
    }
}));
tests.Add(("Durable receipt binds native values and private plan across each recheck", () => {
    var receipt = new JObject { ["kind"] = "capturedStorageQualification", ["qualificationOnly"] = true, ["productionApplySupported"] = false,
        ["planChangeId"] = "plan", ["planPreviewHash"] = "preview", ["planContentHash"] = "content", ["profileSha256"] = "profile",
        ["bindings"] = new JArray(new JObject { ["versionGuid"] = FbVersionGuid, ["installedSha256"] = "installed", ["sourceSha256"] = "source", ["nativePath"] = plc + "/blocks/" + FbName }),
        ["environment"] = new JObject { ["files"] = new JArray("BaseHash", "Step7Hash") }, ["cpuIdentifier"] = Cpu, ["route"] = "standard", ["callerLanguage"] = "FBD" };
    var observed = (JObject)receipt.DeepClone();
    CapturedStorageContract.RequireReceipt(receipt, "plan", "preview", "content", "profile", observed);
    foreach (string field in new[] { "bindings", "environment", "cpuIdentifier", "route", "callerLanguage" }) {
        var changed = (JObject)observed.DeepClone(); changed[field] = "drift";
        Reject(() => CapturedStorageContract.RequireReceipt(receipt, "plan", "preview", "content", "profile", changed), "StaleCapturedStorageContract");
    }
    foreach (string field in new[] { "kind", "planChangeId", "planPreviewHash", "planContentHash", "profileSha256" }) {
        var changed = (JObject)receipt.DeepClone(); changed[field] = "other";
        Reject(() => CapturedStorageContract.RequireReceipt(changed, "plan", "preview", "content", "profile", observed), "StaleCapturedStorageContract");
    }
    var durable = JObject.Parse(receipt.ToString(Newtonsoft.Json.Formatting.None));
    CapturedStorageContract.RequireReceipt(durable, "plan", "preview", "content", "profile", observed);
    var changedInstalled = (JObject)observed.DeepClone(); changedInstalled["bindings"]![0]!["installedSha256"] = "changed-installed";
    Reject(() => CapturedStorageContract.RequireReceipt(durable, "plan", "preview", "content", "profile", changedInstalled), "StaleCapturedStorageContract");
}));
int failed = 0;
foreach (var test in tests) {
    try { test.Body(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + error); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} offline captured-storage tests passed; native builder qualification and production enablement remain false.");
return failed == 0 ? 0 : 1;
