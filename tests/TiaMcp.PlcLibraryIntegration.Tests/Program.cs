using Newtonsoft.Json.Linq;
using System.Xml.Linq;
using TiaMcp.Openness.V21;

if (args.Length == 2 && args[0] == "--scale-qualification")
    return ScaleQualification.Run(args[1]);

int passed = 0, failed = 0;
void Test(string name, Action body) { try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); } }
void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
void Reject(string code, Action<Fixture> change) { var f = new Fixture(); change(f); try { f.Build(); } catch (PlcIntegrationPlanException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }

Test("FBD motor plan emits independently preparable pinned import and typed instance/call steps", () => {
    var f = new Fixture(); var p = f.Build();
    Equal(false, p.Value<bool>("applySupported")); Equal(false, p.Value<bool>("nativeMutationDispatched"));
    Equal("planned_only", p.Value<string>("status")); Equal(2, ((JArray)p["steps"]).Count);
    Equal("library_instantiate", p["steps"][0].Value<string>("kind"));
    Equal("graphical_typed_edit", p["steps"][1].Value<string>("kind"));
    var second = (JObject)p["steps"][1]["prepareArguments"]["payload"];
    TypedGraphicalPayload.Validate(second);
    Equal("createGlobal", second["instances"][0].Value<string>("kind"));
    Equal("/plc/blocks/Motor", second["model"]["nodes"][0].Value<string>("calleePath"));
    Equal("/plc/tags/IO", second["model"]["operands"][0].Value<string>("ownerPath"));
    Equal("call", second["model"]["connections"][1]["from"].Value<string>("node"));
    Equal("Active", second["model"]["connections"][1]["from"].Value<string>("port"));
    Equal(2, p["instanceDb"].Value<int>("provisionalNumber")); Equal(false, p["recoveryPlan"].Value<bool>("separateStepsAreAtomic"));
});
Test("LAD explicit unconditional rail and InOut wiring validate against current typed payload", () => {
    var f = new Fixture(); f.Caller.Descendants("ProgrammingLanguage").ToList().ForEach(e => e.Value = "LAD"); f.Inventory["objects"][0]["language"] = "LAD";
    f.Fb.Descendants().Single(e => e.Name.LocalName == "Section" && (string)e.Attribute("Name") == "Output").SetAttributeValue("Name", "InOut");
    f.Payload["integration"]["mappings"][1]["direction"] = "InOut";
    var p = f.Build(); var typed = (JObject)p["steps"][1]["prepareArguments"]["payload"]; TypedGraphicalPayload.Validate(typed);
    Equal("powerrail", typed["model"]["nodes"][0].Value<string>("kind")); Equal("call", typed["model"]["connections"][2]["to"].Value<string>("node"));
});
Test("native following IDs are explicit blockers, not a false executable plan", () => {
    var f = new Fixture(); f.Caller.Root.Element("SW.Blocks.FC").Element("ObjectList").Add(new XElement("MultilingualText", new XAttribute("ID", "9")));
    Equal("NativeIdPreservationUnsupported", f.Build()["blockers"][0].Value<string>("code"));
});
Test("predictable following ID shift no longer blocks existing-caller insertion", () => {
    var f = new Fixture(); f.Caller = XDocument.Parse("<Document><SW.Blocks.FC ID=\"0\"><AttributeList><Name>Main</Name><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList><ObjectList><SW.Blocks.CompileUnit ID=\"1\"><AttributeList><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit><MultilingualText ID=\"2\" CompositionName=\"Title\"><ObjectList><MultilingualTextItem ID=\"3\" /></ObjectList></MultilingualText></ObjectList></SW.Blocks.FC></Document>");
    f.Payload["integration"]["caller"]["insertAfterNetworkId"] = "1"; Equal(0, ((JArray)f.Build()["blockers"]).Count);
});
Test("OB caller exported as another block kind rejects", () => Reject("IntegrationCallerMismatch", f => f.Inventory["objects"][0]["kind"] = "OB"));
Test("plan does not mutate payload or captured evidence", () => { var f = new Fixture(); string before = f.Payload.ToString() + f.Inventory.ToString() + f.Versions.ToString() + f.Fb.ToString() + f.Caller.ToString(); f.Build(); Equal(before, f.Payload.ToString() + f.Inventory.ToString() + f.Versions.ToString() + f.Fb.ToString() + f.Caller.ToString()); });
Test("dependency versions all pinned", () => { var f = new Fixture(); f.AddDependency(); Equal(2, ((JArray)f.Build()["versions"]).Count); });
Test("case variants of valid GUID pins match native identities", () => { var f = new Fixture(); f.Payload["selection"][0]["typeGuid"] = Fixture.Type.ToUpperInvariant(); Equal("planned_only", f.Build().Value<string>("status")); });
Test("indexed dependency closure preserves type-version pairing", () => Reject("DependencyPinMismatch", f => {
    f.AddDependency(); var root = f.Payload["selection"][0]; var dependency = f.Payload["integration"]["dependencyPins"][0];
    var rootVersion = root["versionGuid"].DeepClone(); root["versionGuid"] = dependency["versionGuid"].DeepClone(); dependency["versionGuid"] = rootVersion;
}));
Test("indexed closure preserves case-insensitive native edges and GUID identities", () => {
    var f = new Fixture(); f.AddDependency();
    foreach (JObject version in f.Versions) { version["typeGuid"] = version.Value<string>("typeGuid").ToUpperInvariant(); version["versionGuid"] = version.Value<string>("versionGuid").ToUpperInvariant(); }
    Equal(2, ((JArray)f.Build()["versions"]).Count);
});
Test("unmatched native type does not inspect its malformed version before pin mismatch", () => Reject("DependencyPinMismatch", f => {
    f.Versions[0]["typeGuid"] = "cccccccc-cccc-cccc-cccc-cccccccccccc"; f.Versions[0]["versionGuid"] = "malformed";
}));
Test("matching native type still rejects a malformed version", () => Reject("InvalidIntegrationRecipe", f => f.Versions[0]["versionGuid"] = "malformed"));
Test("indexed dependency collision remains case insensitive", () => Reject("LibraryConflict", f => {
    f.AddDependency(); ((JArray)f.Inventory["objects"]).Add(new JObject { ["name"] = f.Versions[1].Value<string>("typeName").ToUpperInvariant() });
}));
Test("missing dependency pin rejects", () => Reject("DependencyPinMismatch", f => { f.AddDependency(); ((JArray)f.Payload["integration"]["dependencyPins"]).Clear(); }));
Test("wrong version rejects", () => Reject("DependencyPinMismatch", f => f.Payload["selection"][0]["versionGuid"] = Guid.NewGuid().ToString()));
Test("duplicate root pin rejects", () => Reject("DependencyConflict", f => ((JArray)f.Payload["integration"]["dependencyPins"]).Add(f.Payload["selection"][0].DeepClone())));
Test("two versions of one dependency type rejects", () => Reject("DependencyConflict", f => { f.AddDependency(); f.Versions[1]["typeGuid"] = Fixture.Type; }));
Test("missing dependency edge target rejects", () => Reject("DependencyUnresolved", f => ((JArray)f.Versions[0]["requiredVersionGuids"]).Add(Guid.NewGuid().ToString())));
Test("missing native content proof rejects", () => Reject("DependencyUnresolved", f => ((JObject)f.Versions[0]).Remove("contentSha256")));
Test("in-work dependencies reject", () => Reject("DependencyConflict", f => f.Versions[0]["state"] = "InWork"));
Test("DoNotUse dependencies reject", () => Reject("DependencyConflict", f => f.Versions[0]["doNotUse"] = true));
Test("same-name different-GUID dependency rejects", () => Reject("LibraryConflict", f => { f.AddDependency(); f.Versions[1]["typeName"] = "motor"; }));
Test("same-name installed content is never adopted", () => Reject("LibraryConflict", f => ((JArray)f.Inventory["objects"]).Add(new JObject { ["name"] = "MOTOR" })));
Test("planned DB and imported FB cannot collide", () => Reject("LibraryConflict", f => f.Payload["integration"]["instanceDb"]["name"] = "Motor"));
Test("existing DB collision rejects", () => Reject("LibraryConflict", f => f.Payload["integration"]["instanceDb"]["name"] = "ExistingDb"));
Test("incomplete inventory rejects", () => Reject("IntegrationInventoryIncomplete", f => f.Inventory["complete"] = false));
Test("missing parameter rejects", () => Reject("IntegrationMappingMismatch", f => ((JArray)f.Payload["integration"]["mappings"]).RemoveAt(1)));
Test("duplicate parameter rejects", () => Reject("IntegrationMappingMismatch", f => f.Payload["integration"]["mappings"][1] = f.Payload["integration"]["mappings"][0].DeepClone()));
Test("wrong parameter direction rejects", () => Reject("IntegrationMappingMismatch", f => f.Payload["integration"]["mappings"][1]["direction"] = "Input"));
Test("tag type differs from native rejects", () => Reject("OperandTypeMismatch", f => f.Inventory["tags"][1]["dataType"] = "Int"));
Test("missing exact tag path rejects", () => Reject("IntegrationTagMissing", f => f.Payload["integration"]["mappings"][0]["tagPath"] = "/wrong"));
Test("duplicate emitted tag name rejects", () => Reject("AmbiguousOperand", f => { var tag = f.Inventory["tags"][0].DeepClone(); tag["path"] = "/other"; ((JArray)f.Inventory["tags"]).Add(tag); }));
Test("safety tags reject", () => Reject("SafetyBoundary", f => f.Inventory["tags"][0]["isSafety"] = true));
Test("output cannot write physical input tags", () => Reject("OperandReadOnly", f => f.Inventory["tags"][1]["logicalAddress"] = "%I0.1"));
Test("InOut cannot write localized physical input address", () => Reject("OperandReadOnly", f => { f.Fb.Descendants().Single(e => e.Name.LocalName == "Section" && (string)e.Attribute("Name") == "Output").SetAttributeValue("Name", "InOut"); f.Payload["integration"]["mappings"][1]["direction"] = "InOut"; f.Inventory["tags"][1]["logicalAddress"] = "E0.1"; }));
Test("array parameter rejects", () => Reject("UnsupportedIntegrationType", f => f.Fb.Descendants().First(e => e.Name.LocalName == "Member").SetAttributeValue("Datatype", "Array[0..1] of Bool")));
Test("structured parameter rejects", () => Reject("UnsupportedIntegrationType", f => f.Fb.Descendants().First(e => e.Name.LocalName == "Member").Add(new XElement("Sections"))));
Test("FC roots reject", () => Reject("UnsupportedIntegrationType", f => f.Fb.Root.Element("SW.Blocks.FB").Name = "SW.Blocks.FC"));
Test("wrong root native name rejects", () => Reject("UnsupportedIntegrationType", f => f.Fb.Descendants("Name").First().Value = "Other"));
Test("safety FB rejects", () => Reject("UnsupportedIntegrationType", f => f.Fb.Descendants("ProgrammingLanguage").First().Value = "F_FBD"));
Test("missing caller exact path rejects", () => Reject("IntegrationCallerMismatch", f => f.Payload["integration"]["caller"]["blockPath"] = "/wrong"));
Test("protected caller rejects", () => Reject("UnsupportedIntegrationCaller", f => f.Inventory["objects"][0]["protected"] = true));
Test("namespace caller rejects", () => Reject("UnsupportedIntegrationCaller", f => f.Inventory["objects"][0]["namespace"] = "Motor"));
Test("caller source-language conversion rejects", () => Reject("UnsupportedIntegrationCaller", f => f.Inventory["objects"][0]["language"] = "SCL"));
Test("stale caller export rejects", () => Reject("IntegrationCallerMismatch", f => f.Caller.Descendants("Name").First().Value = "Other"));
Test("missing network ID rejects", () => Reject("NetworkNotFound", f => f.Payload["integration"]["caller"]["insertAfterNetworkId"] = "FF"));
Test("unknown recipe fields reject", () => Reject("InvalidIntegrationRecipe", f => f.Payload["overwrite"] = true));
Test("coerced bool rejects", () => Reject("InvalidIntegrationRecipe", f => f.Payload["planOnly"] = "true"));
Test("coerced schema version rejects", () => Reject("InvalidIntegrationRecipe", f => f.Payload["schemaVersion"] = "1"));
Test("implicit or enabled execution rejects", () => Reject("InvalidIntegrationRecipe", f => f.Payload["planOnly"] = false));
Test("safety-unit scope rejects", () => Reject("UnsupportedIntegrationScope", f => f.Payload["target"]["scopePath"] = "/plc/safetyUnits/Unit1"));
Test("nested unit scope rejects", () => Reject("UnsupportedIntegrationScope", f => f.Payload["target"]["scopePath"] = "/plc/units/Unit1/blocks"));
Test("number override rejects", () => Reject("InvalidIntegrationRecipe", f => f.Payload["integration"]["instanceDb"]["number"] = 123));
Test("Date is unavailable in the typed call route", () => Reject("UnsupportedIntegrationType", f => f.Payload["integration"]["mappings"][0]["dataType"] = "Date"));
Test("Time_Of_Day is unavailable in the typed call route", () => Reject("UnsupportedIntegrationType", f => f.Payload["integration"]["mappings"][0]["dataType"] = "Time_Of_Day"));
Test("master copy cannot become a version pin", () => Reject("InvalidIntegrationRecipe", f => f.Payload["selection"][0] = new JObject { ["masterCopyPath"] = "/masterCopies/Motor" }));
Test("supported scalar set equals production compiler scalar set", () => {
    var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
    var planner = (HashSet<string>)typeof(PlcLibraryIntegrationPlanner).GetField("Scalars", flags).GetValue(null);
    var compiler = (HashSet<string>)typeof(TypedGraphical).GetField("ScalarTypes", flags).GetValue(null);
    Equal(true, planner.SetEquals(compiler));
});
foreach (string language in new[] { "LAD", "FBD" })
    Test(language + " emitted call passes actual production compiler and retained native profiles", () => {
        var f = new Fixture(); f.Caller.Descendants("ProgrammingLanguage").ToList().ForEach(e => e.Value = language); f.Inventory["objects"][0]["language"] = language;
        var plan = f.Build(); var payload = (JObject)plan["steps"][1]["prepareArguments"]["payload"];
        var model = payload["model"].ToObject<TypedGraphicalModel>();
        string repository = RepositoryFiles.Root();
        var profiles = JObject.Parse(File.ReadAllText(Path.Combine(repository, "fixtures/typed-graphical/profiles.v1.json")))["profiles"].ToObject<TypedGraphicalProfile[]>();
        var context = new TypedGraphicalContext { CpuFamily = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1", Profiles = profiles };
        context.Callees["/plc/blocks/Motor"] = new TypedGraphicalCallee { Path = "/plc/blocks/Motor", Name = "Motor", BlockType = "FB", InterfaceHash = "fixture", Parameters = [new() { Name = "Enable", Section = "Input", DataType = "Bool" }, new() { Name = "Active", Section = "Output", DataType = "Bool" }] };
        context.Instances["instance"] = new TypedGraphicalInstance { Identity = "/plc/blocks/Motor_1", Scope = "GlobalVariable", Components = ["Motor_1"], CalleePath = "/plc/blocks/Motor", DataType = "\"Motor\"", DeclarationHash = "fixture" };
        foreach (JObject operand in (JArray)payload["model"]["operands"])
            context.Operands[operand.Value<string>("key")] = new TypedGraphicalOperand { Key = operand.Value<string>("key"), Identity = operand.Value<string>("key"), Scope = operand.Value<string>("scope") == "constant" ? "LiteralConstant" : "GlobalVariable", DataType = "Bool", Components = operand["components"]?.ToObject<string[]>() ?? [], Value = operand.Value<string>("value"), Writable = operand.Value<string>("key") == "p1", DeclarationHash = "fixture" };
        var built = TypedGraphical.Build(f.Caller, model, context);
        Equal(1, built.Document.Descendants(TypedGraphical.Net + "Call").Count());
        Equal(true, built.Document.Descendants(TypedGraphical.Net + "NameCon").Any(e => (string)e.Attribute("Name") == "en"));
    });

// The native guard must remain before durable claims/checkpoints. This is a
// source-boundary check, not a substitute for the explicitly unrun native harness.
Test("native apply gate is before idempotency claims and checkpoints", () => {
    string path = Path.Combine(RepositoryFiles.Root(), "src/TiaMcp.Openness.V21/OpennessSession.LibraryChanges.cs");
    string source = File.ReadAllText(path); int apply = source.IndexOf("private JObject LibraryChangeApply", StringComparison.Ordinal); int gate = source.IndexOf("PlcIntegrationNotQualified", apply, StringComparison.Ordinal);
    // The code uses the shared constant, not a duplicated literal.
    gate = source.IndexOf("PlcLibraryIntegrationPlanner.ApplyError", apply, StringComparison.Ordinal);
    int claim = source.IndexOf("LibraryJournalStore.Claim", apply, StringComparison.Ordinal);
    if (apply < 0 || gate < apply || claim <= gate) throw new Exception("Guard must precede durable claim.");
});
TextBoundaryTests.Run(Test);
CoordinatorTests.Run(Test);
StandaloneTests.Run(Test);
CallerInsertionTests.Run(Test);
QualificationTests.Run(Test);
ContentContractTests.Run(Test);
ContentArtifactTests.Run(Test);
PlanArtifactTests.Run(Test);
LibraryDependencyCaptureTests.Run(Test);
PlcHmiTests.Run(Test);
BroadenedPlanTests.Run(Test);
if (failed == 0 && args.Length != 0)
{
    if (args.Length != 2 || args[0] != "--observation-fixtures") throw new ArgumentException("Expected --observation-fixtures <fresh-directory>.");
    ObservationFixtures.Write(args[1]);
}
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class Fixture
{
    internal const string Type = "aaaaaaaa-1111-1111-1111-111111111111", Version = "bbbbbbbb-2222-2222-2222-222222222222";
    internal JObject Payload = JObject.Parse("""
    {"schemaVersion":1,"action":"plc_component_plan","planOnly":true,"source":{"libraryId":"lib-source","revision":"revision"},"selection":[{"typeGuid":"aaaaaaaa-1111-1111-1111-111111111111","versionGuid":"bbbbbbbb-2222-2222-2222-222222222222"}],"target":{"projectName":"Fixture","plcPath":"/plc"},"integration":{"dependencyPins":[],"instanceDb":{"name":"Motor_1","numberPolicy":"firstAvailable"},"caller":{"blockPath":"/plc/blocks/Main","insertAfterNetworkId":"3"},"mappings":[{"parameter":"Enable","direction":"Input","dataType":"Bool","tagPath":"/plc/tags/IO/tags/EnableMotor"},{"parameter":"Active","direction":"Output","dataType":"Bool","tagPath":"/plc/tags/IO/tags/MotorRunning"}]}}
    """);
    internal JArray Versions = new(new JObject { ["typeGuid"] = Type, ["versionGuid"] = Version, ["typeName"] = "Motor", ["state"] = "Committed", ["doNotUse"] = false, ["contentSha256"] = new string('a', 64), ["requiredVersionGuids"] = new JArray() });
    internal XDocument Fb = XDocument.Parse("""
    <Document><SW.Blocks.FB ID="0"><AttributeList><Name>Motor</Name><Namespace/><ProgrammingLanguage>SCL</ProgrammingLanguage><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Input"><Member Name="Enable" Datatype="Bool"/></Section><Section Name="Output"><Member Name="Active" Datatype="Bool"/></Section><Section Name="InOut"/><Section Name="Static"/></Sections></Interface></AttributeList></SW.Blocks.FB></Document>
    """);
    internal XDocument Caller = XDocument.Parse("""
    <Document><SW.Blocks.FC ID="0"><AttributeList><Name>Main</Name><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList><ObjectList><SW.Blocks.CompileUnit ID="3"><AttributeList><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>
    """);
    internal JObject Inventory = JObject.Parse("""
    {"complete":true,"objects":[{"name":"Main","path":"/plc/blocks/Main","kind":"FC","number":1,"language":"FBD","protected":false,"namespace":""},{"name":"ExistingDb","path":"/plc/blocks/ExistingDb","kind":"DB","number":1}],"tags":[{"name":"EnableMotor","path":"/plc/tags/IO/tags/EnableMotor","tablePath":"/plc/tags/IO","dataType":"Bool","logicalAddress":"%I0.0","isSafety":false},{"name":"MotorRunning","path":"/plc/tags/IO/tags/MotorRunning","tablePath":"/plc/tags/IO","dataType":"Bool","logicalAddress":"%Q0.0","isSafety":false}]}
    """);
    internal JObject Build() => PlcLibraryIntegrationPlanner.Build(Payload, Versions, Fb, Caller, Inventory);
    internal void Standalone(string language = "FBD") {
        Payload["integration"]["caller"] = new JObject { ["mode"] = "createStandaloneFc", ["name"] = "MotorCaller", ["language"] = language, ["numberPolicy"] = "firstAvailable" };
        Caller = null;
    }
    internal void AddDependency() {
        var pin = new JObject { ["typeGuid"] = "cccccccc-3333-3333-3333-333333333333", ["versionGuid"] = "dddddddd-4444-4444-4444-444444444444" };
        ((JArray)Payload["integration"]["dependencyPins"]).Add(pin.DeepClone());
        ((JArray)Versions[0]["requiredVersionGuids"]).Add(pin["versionGuid"].DeepClone());
        pin["typeName"] = "MotorState"; pin["state"] = "Committed"; pin["doNotUse"] = false; pin["contentSha256"] = new string('b', 64); pin["requiredVersionGuids"] = new JArray(); Versions.Add(pin);
    }
}

