using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Model;

int passed = 0, failed = 0;
void Check(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); failed++; }
}
void Reject(Action action)
{
    try { action(); }
    catch (ArgumentException) { return; }
    catch (System.Xml.XmlException) { return; }
    throw new Exception("Expected a rejection.");
}
void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }
JObject Identity(string path = "/devices/HMI/hmi/Runtime/screens/Main/items/Widget") => new JObject {
    ["family"] = "unified", ["projectCopyId"] = "project-copy-a", ["sessionGeneration"] = "generation-a", ["hmiPath"] = "/devices/HMI/hmi/Runtime", ["objectPath"] = path };
JObject Scalar() => new JObject { ["schemaVersion"] = 1, ["operation"] = "scalar_property_set", ["target"] = Identity(),
    ["expectedContentHash"] = new string('a', 64), ["properties"] = new JObject { ["Enabled"] = true } };
const string xml = "<Document><Engineering version='V21'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>Main</Name><BackColor>204, 204, 204</BackColor><UnknownX>preserve me</UnknownX><Event script='DoNotExecute()'/></AttributeList><ObjectList><Item ID='1'><AttributeList><Name>Main</Name><BackColor>204, 204, 204</BackColor><Binding plc='PLC_A' member='DB.A'/><Translation culture='de-DE'>Über</Translation><Version guid='abc'/></AttributeList></Item></ObjectList></Hmi.Screen.Screen></Document>";

Check("HMI scalar request accepts only the operation-specific property allowlist", () => {
    HmiEngineeringRules.RequireRequest(Scalar());
    foreach (string key in new[] { "Password", "ScriptCode", "VisibleScript", "DataType", "ContainedType", "Name" }) {
        var request = Scalar(); request["properties"] = new JObject { [key] = "arbitrary" }; Reject(() => HmiEngineeringRules.RequireRequest(request));
    }
});
Check("A caller cannot turn native qualification on or substitute metadata revision", () => {
    foreach (string field in new[] { "qualified", "force", "preservationVerified", "metadataRevision" }) {
        var request = Scalar(); request[field] = true; Reject(() => HmiEngineeringRules.RequireRequest(request));
    }
    var noHash = Scalar(); noHash.Remove("expectedContentHash"); Reject(() => HmiEngineeringRules.RequireRequest(noHash));
});
Check("Scalar properties reject wrong JSON types and unbounded integers", () => {
    var request = Scalar(); request["properties"]["Enabled"] = "true"; Reject(() => HmiEngineeringRules.RequireRequest(request));
    request["properties"] = new JObject { ["TabIndex"] = -1 }; Reject(() => HmiEngineeringRules.RequireRequest(request));
    request["properties"] = new JObject { ["TabIndex"] = 1.5 }; Reject(() => HmiEngineeringRules.RequireRequest(request));
});
Check("Bindings require exact project/session/HMI ownership, never name evidence", () => {
    JObject Request() { var r = Scalar(); r.Remove("properties"); r["operation"] = "existing_binding_set";
        r["bindings"] = new JArray(new JObject { ["property"] = "ProcessValue", ["tagIdentity"] = Identity("/devices/HMI/hmi/Runtime/tags/Pump"), ["dataType"] = "Bool", ["expectedValue"] = "Before" }); return r; }
    HmiEngineeringRules.RequireRequest(Request());
    foreach (string key in new[] { "family", "projectCopyId", "sessionGeneration", "hmiPath" }) {
        var r = Request(); r["bindings"][0]["tagIdentity"][key] = "same-name-other-owner"; Reject(() => HmiEngineeringRules.RequireRequest(r));
    }
    var nameOnly = Request(); nameOnly["bindings"][0]["tagIdentity"] = new JObject { ["name"] = "Pump" }; Reject(() => HmiEngineeringRules.RequireRequest(nameOnly));
    var fakeEvidence = Request(); fakeEvidence["bindings"][0]["nativeConfirmed"] = true; Reject(() => HmiEngineeringRules.RequireRequest(fakeEvidence));
});
Check("Operation shapes cannot smuggle external bindings into internal tag creation", () => {
    var r = Scalar(); r.Remove("properties"); r["operation"] = "tag_create"; r["name"] = "Internal"; r["dataType"] = "Bool";
    HmiEngineeringRules.RequireRequest(r); r["connection"] = "PLC"; Reject(() => HmiEngineeringRules.RequireRequest(r));
    r.Remove("connection"); r["bindings"] = new JArray(); Reject(() => HmiEngineeringRules.RequireRequest(r));
});
JObject External() {
    var connection = Identity("/devices/HMI/hmi/Runtime/connections/HMI_Connection_1"); connection["kind"] = "hmiConnection";
    var target = Identity("/devices/HMI/hmi/Runtime/tagTables/Default"); target["kind"] = "hmiTagTable";
    return new JObject { ["schemaVersion"] = 1, ["operation"] = "external_tag_create", ["target"] = target, ["expectedContentHash"] = new string('a', 64),
        ["name"] = "Linked", ["dataType"] = "Bool", ["connection"] = connection,
        ["plcTag"] = new JObject { ["projectPath"] = "C:/p.ap21", ["plcPath"] = "/devices/PLC_1/items/PLC_1/plc/PLC_1", ["scopePath"] = "/devices/PLC_1/items/PLC_1/plc/PLC_1",
            ["objectPath"] = "/devices/PLC_1/items/PLC_1/plc/PLC_1/tags/Default/tags/SharedSignal", ["kind"] = "tag", ["name"] = "SharedSignal", ["namespace"] = null } };
}
Check("External tag creation requires exact same-HMI connection and exact root PLC tag identities", () => {
    HmiEngineeringRules.RequireRequest(External());
    var classic = External(); classic["target"]["family"] = "classic"; classic["connection"]["family"] = "classic"; Reject(() => HmiEngineeringRules.RequireRequest(classic));
    foreach (string key in new[] { "family", "projectCopyId", "sessionGeneration", "hmiPath" }) {
        var r = External(); r["connection"][key] = "other-owner"; Reject(() => HmiEngineeringRules.RequireRequest(r));
    }
    var nameOnly = External(); nameOnly["connection"] = "HMI_Connection_1"; Reject(() => HmiEngineeringRules.RequireRequest(nameOnly));
    var wrongKind = External(); wrongKind["connection"]["kind"] = "hmiTag"; Reject(() => HmiEngineeringRules.RequireRequest(wrongKind));
    var symbolOnly = External(); symbolOnly["plcTag"] = "SharedSignal"; Reject(() => HmiEngineeringRules.RequireRequest(symbolOnly));
    var evidence = External(); evidence["plcTag"]["nativeConfirmed"] = true; Reject(() => HmiEngineeringRules.RequireRequest(evidence));
    var block = External(); block["plcTag"]["kind"] = "block"; Reject(() => HmiEngineeringRules.RequireRequest(block));
    var unit = External(); unit["plcTag"]["scopePath"] = "/devices/PLC_1/items/PLC_1/plc/PLC_1/units/U"; Reject(() => HmiEngineeringRules.RequireRequest(unit));
    var noScope = External(); ((JObject)noScope["plcTag"]).Remove("scopePath"); HmiEngineeringRules.RequireRequest(noScope);
    foreach (string type in new[] { "String", "Array[0..1] of Bool", "UDT_Motor", "bool" }) {
        var r = External(); r["dataType"] = type; Reject(() => HmiEngineeringRules.RequireRequest(r));
    }
    foreach (string field in new[] { "properties", "placement", "bindings" }) {
        var r = External(); r[field] = new JObject(); Reject(() => HmiEngineeringRules.RequireRequest(r));
    }
});
Check("Other operations cannot smuggle external connection or PLC tag fields", () => {
    foreach (string field in new[] { "connection", "plcTag" }) {
        var internalTag = Scalar(); internalTag.Remove("properties"); internalTag["operation"] = "tag_create"; internalTag["name"] = "Internal"; internalTag["dataType"] = "Bool";
        internalTag[field] = External()[field].DeepClone(); Reject(() => HmiEngineeringRules.RequireRequest(internalTag));
        var scalar = Scalar(); scalar[field] = External()[field].DeepClone(); Reject(() => HmiEngineeringRules.RequireRequest(scalar));
    }
});
Check("External tag readback verifies native derivation without writing derived fields", () => {
    void Readback(string name = "Linked", string type = "Bool", string connection = "HMI_Connection_1", string plcTag = "SharedSignal", string access = "SymbolicAccess", string plcName = "PLC_1") =>
        HmiRecipeRules.RequireExternalTagReadback("Linked", "Bool", "HMI_Connection_1", "SharedSignal", "PLC_1", name, type, connection, plcTag, access, plcName);
    Readback(); Readback(plcName: ""); Readback(plcName: null);
    Reject(() => Readback(name: "Other")); Reject(() => Readback(type: "Int")); Reject(() => Readback(connection: "HMI_Connection_2"));
    Reject(() => Readback(plcTag: "\"SharedSignal\"")); Reject(() => Readback(access: "AbsoluteAccess")); Reject(() => Readback(plcName: "PLC_2"));
});
Check("Tag-table preservation delta admits only the changed table's export files", () => {
    const string prefix = "/hmi/tagTables/Default/export/";
    var before = new Dictionary<string, string> { [prefix + "Tags.xml"] = "1", ["/hmi/tagTables/Other/export/Tags.xml"] = "2" };
    var after = new Dictionary<string, string>(before) { [prefix + "Tags.xml"] = "3", [prefix + "Linked.xml"] = "4" };
    HmiPreservationRules.RequireUnrelatedPreservedOutside(before, after, prefix);
    var unrelated = new Dictionary<string, string>(after) { ["/hmi/tagTables/Other/export/Tags.xml"] = "5" };
    Reject(() => HmiPreservationRules.RequireUnrelatedPreservedOutside(before, unrelated, prefix));
    var removed = new Dictionary<string, string>(after); removed.Remove("/hmi/tagTables/Other/export/Tags.xml");
    Reject(() => HmiPreservationRules.RequireUnrelatedPreservedOutside(before, removed, prefix));
    var added = new Dictionary<string, string>(after) { ["/hmi/tagTables/New/export/Tags.xml"] = "6" };
    Reject(() => HmiPreservationRules.RequireUnrelatedPreservedOutside(before, added, prefix));
    var emptied = new Dictionary<string, string> { ["/hmi/tagTables/Other/export/Tags.xml"] = "2" };
    Reject(() => HmiPreservationRules.RequireUnrelatedPreservedOutside(before, emptied, prefix));
    Reject(() => HmiPreservationRules.RequireUnrelatedPreservedOutside(before, after, "/hmi/tagTables/Default"));
});
Check("Classic scalar patch selects export-local owner, preserving duplicate member names", () => {
    var before = HmiPreservationRules.ReadXml(xml);
    var expected = HmiPreservationRules.PatchClassicScalar(before, "Hmi.Screen.Screen", "0", "BackColor", "204, 204, 204", "128, 128, 128");
    Assert(expected.Descendants("BackColor").Select(n => n.Value).SequenceEqual(new[] { "128, 128, 128", "204, 204, 204" }));
    Assert(before.Descendants("Name").First().Value == "Main");
    Reject(() => HmiPreservationRules.PatchClassicScalar(before, "Hmi.Screen.Screen", "1", "BackColor", "204, 204, 204", "128, 128, 128"));
    Reject(() => HmiPreservationRules.PatchClassicScalar(before, "Hmi.Screen.Screen", "0", "BackColor", "Stale", "128, 128, 128"));
});
Check("Classic patch refuses guessed owner types and arbitrary export properties", () => {
    var before = HmiPreservationRules.ReadXml(xml);
    Reject(() => HmiPreservationRules.PatchClassicScalar(before, "Item", "1", "BackColor", "204, 204, 204", "128, 128, 128"));
    Reject(() => HmiPreservationRules.PatchClassicScalar(before, "Hmi.Screen.Screen", "0", "UnknownX", "preserve me", "replace"));
});
Check("Classic scalar limits reject rename and out-of-range colors", () => {
    var request = Scalar(); request["target"]["family"] = "classic";
    request["properties"] = new JObject { ["BackColor"] = "204, 204, 204" }; HmiEngineeringRules.RequireRequest(request);
    request["properties"] = new JObject { ["Name"] = "Renamed" }; Reject(() => HmiEngineeringRules.RequireRequest(request));
    foreach (string rgb in new[] { "256, 0, 0", "-1, 0, 0", "0, 0", "0.5, 0, 0", "FF, FF, FF" }) {
        request["properties"] = new JObject { ["BackColor"] = rgb }; Reject(() => HmiEngineeringRules.RequireRequest(request));
    }
});
Check("Preservation retains unknown payloads, bindings, events, translations, IDs and versions", () => {
    var before = HmiPreservationRules.ReadXml(xml);
    HmiPreservationRules.RequireExactDocument(before, new XDocument(before));
    foreach (string tag in new[] { "UnknownX", "Event", "Binding", "Translation", "Version" }) {
        var after = new XDocument(before); after.Descendants(tag).Single().Remove(); Reject(() => HmiPreservationRules.RequireExactDocument(before, after));
    }
    var changedId = new XDocument(before); changedId.Descendants("Item").Single().SetAttributeValue("ID", "2"); Reject(() => HmiPreservationRules.RequireExactDocument(before, changedId));
});
Check("XML preservation rejects external entity and DTD documents", () => {
    Reject(() => HmiPreservationRules.ReadXml("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///secret'>]><root>&x;</root>"));
});
Check("Classic export timestamp normalization is narrow and rejects unknown shapes", () => {
    string first = xml.Replace("<Engineering version='V21'/>", "<Engineering version='V21'/><DocumentInfo><Created>2026-10-08T11:34:06.723447Z</Created><Product>V21 Update 2</Product></DocumentInfo>");
    string second = first.Replace("11:34:06.723447", "11:35:06.723448");
    Assert(HmiPreservationRules.ClassicContentHash(first) == HmiPreservationRules.ClassicContentHash(second));
    Assert(HmiPreservationRules.ClassicContentHash(first) != HmiPreservationRules.ClassicContentHash(second.Replace("V21 Update 2</Product>", "V21 Update 3</Product>")));
    Reject(() => HmiPreservationRules.ClassicContentHash(first.Replace("<Created>", "<Created custom='payload'>")));
    var nested = first.Replace("<UnknownX>", "<Created>2026-10-08T00:00:00Z</Created><UnknownX>");
    Assert(HmiPreservationRules.ClassicContentHash(nested) != HmiPreservationRules.ClassicContentHash(nested.Replace("00:00:00Z", "00:00:01Z")));
});
Check("Partial native exports never establish a complete content fingerprint", () => {
    var artifacts = new Dictionary<string, string> { ["/a"] = new string('a', 64) };
    Reject(() => HmiPreservationRules.ScopeHash("classic", artifacts, false));
    Assert(HmiPreservationRules.ScopeHash("classic", artifacts, true) != HmiPreservationRules.ScopeHash("unified", artifacts, true));
    Reject(() => HmiPreservationRules.ScopeHash("unknown", artifacts, true));
});
Check("Scalar edits and timestamp normalization reject hidden XML payloads", () => {
    foreach (string payload in new[] { "<!--retain-->", "<?native retain?>", "<![CDATA[]]>" }) {
        var original = HmiPreservationRules.ReadXml(xml.Replace("<BackColor>", "<BackColor>" + payload));
        Reject(() => HmiPreservationRules.PatchClassicScalar(original, "Hmi.Screen.Screen", "0", "BackColor", "204, 204, 204", "128, 128, 128"));
        string timestamp = "<Document><DocumentInfo><Created>2026-10-08T11:34:06Z" + payload + "</Created></DocumentInfo></Document>";
        Reject(() => HmiPreservationRules.ClassicContentHash(timestamp));
    }
    Reject(() => HmiPreservationRules.PatchClassicScalar(HmiPreservationRules.ReadXml(xml.Replace("<BackColor>", "<BackColor unknown='content'>")),
        "Hmi.Screen.Screen", "0", "BackColor", "204, 204, 204", "128, 128, 128"));
});
Check("Operation prerequisites include independent recovery and precise operation evidence", () => {
    var expected = new Dictionary<string, string[]> {
        ["scalar_property_set"] = new[] { "exactScalarPropertyDelta" }, ["item_properties_set"] = new[] { "exactItemPropertyDelta" },
        ["tag_create"] = new[] { "newTagNamespaceUnique", "internalTagIsolation" },
        ["item_place"] = new[] { "exactRectangleWithinScreen", "faceplateTypeIdentityPreserved" },
        ["existing_binding_set"] = new[] { "existingBindingGuard", "exactTypedBindingOwnership" },
        ["explicit_binding_set"] = new[] { "exactTypedBindingOwnership", "bindingDependencyClosure" },
        ["external_tag_create"] = new[] { "newTagNamespaceUnique", "exactConnectionOwnership", "exactPlcDeclarationType" } };
    foreach (var operation in expected) {
        var gates = HmiEngineeringRules.RequiredGates(operation.Key).Values<string>().ToArray();
        Assert(gates.Distinct().Count() == gates.Length && operation.Value.All(gates.Contains));
        Assert(new[] { "cleanBaselineCompile", "staleDependencyRejected", "idempotentApply", "interruptedWriteRecovery" }.All(gates.Contains));
    }
    Reject(() => HmiEngineeringRules.RequiredGates("invented"));
});
Check("Unrelated content preservation catches deletion, addition and resource changes", () => {
    var before = new Dictionary<string, string> { ["/screen"] = "1", ["/resource"] = "2" };
    var after = new Dictionary<string, string>(before) { ["/screen"] = "3" };
    HmiPreservationRules.RequireUnrelatedPreserved(before, after, new[] { "/screen" }, Array.Empty<string>());
    after["/resource"] = "4"; Reject(() => HmiPreservationRules.RequireUnrelatedPreserved(before, after, new[] { "/screen" }, Array.Empty<string>()));
    after.Remove("/resource"); Reject(() => HmiPreservationRules.RequireUnrelatedPreserved(before, after, new[] { "/screen" }, Array.Empty<string>()));
    after = new Dictionary<string, string>(before) { ["/unknown"] = "5" }; Reject(() => HmiPreservationRules.RequireUnrelatedPreserved(before, after, Array.Empty<string>(), Array.Empty<string>()));
});
Check("Every exact family/profile operation retains unpassed qualification gates", () => {
    foreach (string profile in new[] { "classic-comfort", "classic-rt-advanced", "classic-basic", "classic-rt-professional", "unified-comfort", "unified-pc" })
        foreach (string operation in HmiEngineeringRules.Operations) {
            var gate = HmiEngineeringRules.Gate(profile.StartsWith("classic-") ? "classic" : "unified", profile, operation);
            Assert(!gate.Value<bool>("available") && !gate.Value<bool>("runtimeVerified") && gate.Value<string>("nativeQualification") == "unverified");
            Assert(((JArray)gate["requiredGates"]).Count >= 8 && !string.IsNullOrWhiteSpace(gate.Value<string>("reason")));
        }
});
Check("Optional assembly identity validates name, loaded-core token, culture and version separately", () => {
    const string expected = "Siemens.Engineering.WinCC", token = "observed-native-key";
    Assert(HmiAssemblyIdentityRules.Rejection(expected, expected, token, token, 21, "") == null);
    Assert(HmiAssemblyIdentityRules.Rejection(expected, expected, token, token, 20, "") == "HmiVersionMismatch");
    Assert(HmiAssemblyIdentityRules.Rejection(expected, "Unrelated.V21", token, token, 21, "") == "HmiAssemblyIdentityMismatch");
    Assert(HmiAssemblyIdentityRules.Rejection(expected, expected, token, "", 21, "") == "HmiAssemblyIdentityMismatch");
    Assert(HmiAssemblyIdentityRules.Rejection(expected, expected, "", "", 21, "") == "HmiAssemblyIdentityMismatch");
    Assert(HmiAssemblyIdentityRules.Rejection(expected, expected, token, token, 21, "de-DE") == "HmiAssemblyIdentityMismatch");
});
Check("Whole-document preservation retains declaration presence, encoding and standalone", () => {
    var original = HmiPreservationRules.ReadXml("<?xml version='1.0' encoding='utf-8' standalone='yes'?>" + xml);
    HmiPreservationRules.RequireExactDocument(original, new XDocument(original));
    foreach (string declaration in new[] { "", "<?xml version='1.0' encoding='utf-16' standalone='yes'?>", "<?xml version='1.0' encoding='utf-8' standalone='no'?>" })
    {
        var changed = HmiPreservationRules.ReadXml(declaration + xml);
        Reject(() => HmiPreservationRules.RequireExactDocument(original, changed));
        Reject(() => HmiPreservationRules.RequireClassicDocumentPreservation(original, changed));
    }
});
Check("Whole-document preservation retains opaque CDATA node boundaries", () => {
    var before = HmiPreservationRules.ReadXml("<Document><Opaque><![CDATA[unchanged]]></Opaque></Document>");
    var after = HmiPreservationRules.ReadXml("<Document><Opaque>unchanged</Opaque></Document>");
    Reject(() => HmiPreservationRules.RequireExactDocument(before, after));
});
Check("Classic XML import accepts one exact exported object and refuses other shapes", () => {
    JObject Import(string document = xml) { var r = Scalar(); r.Remove("properties"); r["operation"] = "classic_xml_import"; r["target"]["family"] = "classic"; r["xml"] = document; return r; }
    HmiEngineeringRules.RequireRequest(Import());
    var unified = Import(); unified["target"]["family"] = "unified"; Reject(() => HmiEngineeringRules.RequireRequest(unified));
    var mixed = Import(); mixed["properties"] = new JObject { ["BackColor"] = "1, 2, 3" }; Reject(() => HmiEngineeringRules.RequireRequest(mixed));
    var smuggled = Scalar(); smuggled["xml"] = xml; Reject(() => HmiEngineeringRules.RequireRequest(smuggled));
    var notText = Import(); notText["xml"] = new JObject(); Reject(() => HmiEngineeringRules.RequireRequest(notText));
    foreach (string document in new[] { "<Document>", "<Root/>", "<Document><Engineering/></Document>", xml.Replace("</Document>", "<Hmi.Tag.TagTable ID='9'/></Document>"),
        xml.Replace("Hmi.Screen.Screen", "Screen"), "<?xml version='1.0' encoding='utf-16'?>" + xml, "<!DOCTYPE Document [<!ENTITY x 'y'>]>" + xml })
        Reject(() => HmiEngineeringRules.RequireRequest(Import(document)));
    var current = HmiPreservationRules.ClassicImportRoot(HmiPreservationRules.ReadXml(xml));
    HmiPreservationRules.ClassicImportRoot(HmiPreservationRules.ReadXml(xml.Replace("204, 204, 204</BackColor><UnknownX>", "1, 2, 3</BackColor><UnknownX>")), current);
    Reject(() => HmiPreservationRules.ClassicImportRoot(HmiPreservationRules.ReadXml(xml.Replace("<Name>Main</Name><BackColor>204, 204, 204</BackColor><UnknownX>", "<Name>Other</Name><BackColor>204, 204, 204</BackColor><UnknownX>")), current));
    Reject(() => HmiPreservationRules.ClassicImportRoot(HmiPreservationRules.ReadXml(xml.Replace("Hmi.Screen.Screen", "Hmi.Screen.ScreenPopup")), current));
    Assert(HmiEngineeringRules.RequiredGates("classic_xml_import").Values<string>().Contains("exactDocumentDelta"));
    var gate = HmiEngineeringRules.Gate("classic", "classic-comfort", "classic_xml_import");
    Assert(gate.Value<string>("bridgeImplementation") == "guarded_body" && !gate.Value<bool>("available"));
    Assert(HmiEngineeringRules.Gate("unified", "unified-comfort", "classic_xml_import").Value<string>("bridgeImplementation") == "unavailable");
});
Console.WriteLine($"{passed} passed; {failed} failed. These are pure guard/preservation tests, not native qualification.");
return failed == 0 ? 0 : 1;
