using Newtonsoft.Json.Linq;
using System.Xml.Linq;
using TiaMcp.Openness.V21;

// Software units/namespaces, OB callers, multi-instances, exact reuse and UDT/DB-member bindings (27 October).
internal static class BroadenedPlanTests
{
    private const string Interface = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

    internal static void Run(Action<string, Action> test)
    {
        test("software-unit plan keeps unit scope and namespaces in exact plan-only steps", () => {
            var p = Unit().Build(); var typed = Typed(p); TypedGraphicalPayload.Validate(typed);
            Equal("/plc/units/U1/blocks/namespaces/Lib/Motor", p["fb"].Value<string>("path"));
            Equal("/plc/units/U1/blocks/namespaces/Lib/Motor", typed["model"]["nodes"][0].Value<string>("calleePath"));
            Equal("/plc/units/U1", typed.Value<string>("scopePath")); Equal("App", typed.Value<string>("namespace")); Equal("/plc/units/U1/blocks", typed.Value<string>("groupPath"));
            Equal("/plc/units/U1/blocks/namespaces/App/Motor_1", p["instanceDb"].Value<string>("path"));
            Equal("/plc/units/U1", p["steps"][0]["prepareArguments"]["payload"]["target"].Value<string>("scopePath"));
            Equal("ComponentScopeUnqualified", Codes(p["steps"][0])); Equal("ComponentScopeUnqualified,UnsupportedScopeReference", Codes(p["steps"][1]));
        });
        test("software-unit standalone caller uses the unit namespace preset", () => {
            var f = Unit(); f.Standalone(); var p = f.Build(); TypedGraphicalPayload.Validate(Typed(p));
            Equal("/plc/units/U1/blocks/namespaces/App/MotorCaller", p["caller"].Value<string>("blockPath")); Equal("App", Typed(p).Value<string>("namespace"));
        });
        test("software-unit plan without resolved native unit rejects", () => Reject("IntegrationInventoryIncomplete", Unit(), f => ((JObject)f.Inventory).Remove("scope")));
        test("namespaced FB in the standard scope rejects", () => Reject("UnsupportedIntegrationScope", new Fixture(), f => f.Fb.Descendants("Namespace").Single().Value = "Lib"));
        test("unit caller path must match its native namespace", () => Reject("IntegrationCallerMismatch", Unit(), f => f.Inventory["objects"][0]["namespace"] = "Other"));

        test("OB caller gets an exact insertion step that adds the OB call", () => {
            var f = new Fixture(); f.Caller.Root.Element("SW.Blocks.FC").Name = "SW.Blocks.OB"; f.Inventory["objects"][0]["kind"] = "OB";
            var p = f.Build(); Equal("OB", p["caller"].Value<string>("kind")); Equal(true, p["caller"].Value<bool>("obCallAdded"));
            Equal(2, ((JArray)p["steps"]).Count); Equal(null, p["steps"][1]["planOnly"]); Compile(p, f.Caller);
        });

        test("multi-instance plan creates a static member in the FB caller and is plan-only", () => {
            var f = Multi(); var p = f.Build(); var typed = Typed(p); TypedGraphicalPayload.Validate(typed);
            Equal("createMulti", typed["instances"][0].Value<string>("kind")); Equal("Motor1", typed["instances"][0].Value<string>("name"));
            Equal("\"Motor\"", p["multiInstance"].Value<string>("dataType")); Equal(null, p["instanceDb"]);
            Equal("ComponentMultiInstanceUnqualified", Codes(p["steps"][1])); Equal(null, p["steps"][0]["planOnly"]);
        });
        test("multi-instance needs an FB caller", () => Reject("UnsupportedIntegrationCaller", Multi(), f => { f.Caller.Root.Element("SW.Blocks.FB").Name = "SW.Blocks.FC"; f.Inventory["objects"][0]["kind"] = "FC"; }));
        test("existing caller member is not adopted as a multi-instance", () => Reject("DeclarationCollision", Multi(), f => f.Payload["integration"]["multiInstance"]["name"] = "existing"));
        test("instanceDb and multiInstance are exclusive", () => Reject("InvalidIntegrationRecipe", Multi(), f => f.Payload["integration"]["instanceDb"] = new JObject { ["name"] = "Motor_1", ["numberPolicy"] = "firstAvailable" }));
        test("one instance kind is required", () => Reject("InvalidIntegrationRecipe", new Fixture(), f => ((JObject)f.Payload["integration"]).Remove("instanceDb")));
        test("standalone FC cannot own a multi-instance", () => Reject("InvalidIntegrationRecipe", Multi(), f => f.Standalone()));

        test("exact installed dependency is reused with its proof and the import step stays", () => {
            var f = new Fixture(); f.AddDependency(); Add(f, "MotorState", "/plc/types/MotorState", "UDT"); Prove(f, 1);
            var p = f.Build(); Equal(2, ((JArray)p["steps"]).Count); Equal("MotorState", p["reuse"]["versions"][0].Value<string>("typeName"));
            Equal(false, p["fb"].Value<bool>("reused")); Equal(null, p["steps"][0]["planOnly"]); Equal(null, p["steps"][1]["planOnly"]);
        });
        test("same-name dependency without proof is never adopted", () => Reject("LibraryConflict", new Fixture(), f => { f.AddDependency(); Add(f, "MotorState", "/plc/types/MotorState", "UDT"); }));
        test("proof of another version is not reuse", () => Reject("LibraryConflict", new Fixture(), f => { f.AddDependency(); Add(f, "MotorState", "/plc/types/MotorState", "UDT"); Prove(f, 1); f.Inventory["reusedVersions"][0]["versionGuid"] = Guid.NewGuid().ToString(); }));
        test("reused FB drops the import and reuses an unused exact instance DB", () => {
            var f = ReusedRoot(); InstanceDb(f, new JArray());
            var p = f.Build(); var typed = Typed(p); TypedGraphicalPayload.Validate(typed);
            Equal(1, ((JArray)p["steps"]).Count); Equal(1, p["steps"][0].Value<int>("order")); Equal("graphical_typed_edit", p["steps"][0].Value<string>("kind"));
            Equal("existingGlobal", typed["instances"][0].Value<string>("kind")); Equal("/plc/blocks/Motor_1", typed["instances"][0].Value<string>("blockPath"));
            Equal(true, p["instanceDb"].Value<bool>("reused")); Equal("/plc/blocks/Motor_1", p["reuse"].Value<string>("instanceDbPath")); Equal(true, p["fb"].Value<bool>("reused"));
            Equal("ComponentRecoveryAnchorUnavailable", Codes(p["steps"][0]));
        });
        test("reused FB with a new instance DB keeps createGlobal", () => { var p = ReusedRoot().Build(); Equal("createGlobal", Typed(p)["instances"][0].Value<string>("kind")); Equal(2, p["instanceDb"].Value<int>("provisionalNumber")); });
        test("instance DB with unproven users is not reused", () => Reject("InstanceUseUnproven", ReusedRoot(), f => InstanceDb(f, null)));
        test("instance DB already used elsewhere is not reused", () => Reject("InstanceAlreadyUsed", ReusedRoot(), f => InstanceDb(f, new JArray("/plc/blocks/Other"))));
        test("same-name DB of another FB is not reused", () => Reject("LibraryConflict", ReusedRoot(), f => { InstanceDb(f, new JArray()); f.Inventory["objects"].Last["instanceOfName"] = "Other"; }));
        test("reused FB needs every dependency proven", () => Reject("DependencyConflict", ReusedRoot(), f => f.AddDependency()));
        test("root proof without the native FB rejects", () => Reject("LibraryConflict", new Fixture(), f => Prove(f, 0)));

        test("DB member through a UDT binds a typed db operand", () => {
            var f = DbMember(); var p = f.Build(); var typed = Typed(p); TypedGraphicalPayload.Validate(typed);
            var operand = typed["model"]["operands"][0]; Equal("db", operand.Value<string>("scope")); Equal("/plc/blocks/Data", operand.Value<string>("ownerPath"));
            Equal("cmd.enable", string.Join(".", operand["components"].Values<string>())); Equal("Bool", p["mappings"][0]["nativeMember"].Value<string>("dataType"));
            Equal("ComponentReadbackUnavailable", Codes(p["steps"][1])); Compile(p, f.Caller);
        });
        test("DB member type mismatch rejects", () => Reject("OperandTypeMismatch", DbMember(), f => f.Inventory["members"]["/plc/blocks/Data"][1]["dataType"] = "Int"));
        test("structured DB member rejects", () => Reject("UnsupportedIntegrationType", DbMember(), f => f.Payload["integration"]["mappings"][0]["members"] = new JArray("cmd")));
        test("missing DB member rejects", () => Reject("IntegrationMappingMismatch", DbMember(), f => f.Payload["integration"]["mappings"][0]["members"] = new JArray("cmd", "start")));
        test("missing member evidence rejects", () => Reject("IntegrationInventoryIncomplete", DbMember(), f => ((JObject)f.Inventory).Remove("members")));
        test("missing DB rejects", () => Reject("IntegrationOperandMissing", DbMember(), f => f.Payload["integration"]["mappings"][0]["dbPath"] = "/plc/blocks/Other"));
        test("protected DB rejects", () => Reject("ProtectedObject", DbMember(), f => f.Inventory["objects"].Last["protected"] = true));
        test("ambiguous DB symbol rejects", () => Reject("AmbiguousOperand", DbMember(), f => Add(f, "DATA", "/plc/blocks/groups/G/DATA", "DB")));
        test("tagPath and dbPath are exclusive", () => Reject("InvalidIntegrationRecipe", DbMember(), f => f.Payload["integration"]["mappings"][0]["tagPath"] = "/plc/tags/IO/tags/EnableMotor"));
        test("dbPath needs members", () => Reject("InvalidIntegrationRecipe", DbMember(), f => ((JObject)f.Payload["integration"]["mappings"][0]).Remove("members")));
        test("UDT-typed tag member is exact but plan-only on the typed route", () => {
            var f = TagMember(); var p = f.Build(); var operand = Typed(p)["model"]["operands"][1];
            Equal("MotorRunning.running", string.Join(".", operand["components"].Values<string>())); Equal("UnsupportedOperandRoute", Codes(p["steps"][1]));
        });
        test("scalar tag cannot select members", () => Reject("OperandTypeMismatch", TagMember(), f => f.Inventory["tags"][1]["dataType"] = "Bool"));

        test("combined HMI instanceMember needs a global instance DB", () => {
            var plan = Multi().Build(); var payload = (JObject)Multi().Payload.DeepClone(); payload["action"] = PlcHmiIntegrationPlanner.InstallAction;
            payload["hmi"] = JObject.Parse("""{"family":"unified","profile":"unified-comfort","hmiPath":"/hmi","connection":"C","tagTable":"T","tags":[{"name":"Speed","dataType":"Bool","source":{"kind":"instanceMember","members":["Active"]}}]}""");
            payload["expectedCompilerOutcomes"] = JObject.Parse("""{"plc":{"errors":0,"maxWarnings":0},"hmi":{"errors":0,"maxWarnings":0}}""");
            var observed = JObject.Parse("""{"family":"unified","profile":"unified-comfort","connection":{"plcPath":"/plc"},"tagTable":{"exists":true},"instanceMembers":[{"path":["Active"],"dataType":"Bool","scalar":true}]}""");
            try { PlcHmiIntegrationPlanner.BuildInstall(payload, plan, observed); } catch (PlcIntegrationPlanException e) when (e.Code == "UnsupportedIntegrationType") { return; }
            throw new Exception("Expected UnsupportedIntegrationType");
        });

        // Production combined apply (--enable-library-integrate-apply) admits only the natively verified shapes.
        static string Refusal(Fixture f) => PlcComponentQualificationGate.CombinedApplyRefusal(new JObject { ["payload"] = f.Payload, ["integrationPlan"] = f.Build() }, f.Caller);
        static Fixture Titled(string networks, int title) { var f = new Fixture(); f.Caller = XDocument.Parse("<Document><SW.Blocks.FC ID=\"0\"><AttributeList><Name>Main</Name><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList><ObjectList>" + networks + "<MultilingualText ID=\"" + title + "\" CompositionName=\"Title\" /></ObjectList></SW.Blocks.FC></Document>"); f.Payload["integration"]["caller"]["insertAfterNetworkId"] = "1"; return f; }
        static Fixture Ob() { var f = new Fixture(); f.Caller.Root.Element("SW.Blocks.FC").Name = "SW.Blocks.OB"; f.Inventory["objects"][0]["kind"] = "OB"; return f; }
        test("combined apply admits the verified existing FBD FC caller", () => Equal(null, Refusal(new Fixture())));
        test("combined apply admits the verified standalone FBD FC", () => { var f = new Fixture(); f.Standalone("FBD"); Equal(null, Refusal(f)); });
        const string Unit1 = "<SW.Blocks.CompileUnit ID=\"1\"><AttributeList><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit>";
        test("combined apply admits insertion after the last network with shifted title IDs", () => Equal(null, Refusal(Titled(Unit1, 2))));
        test("combined apply refuses insertion before another network", () => Equal(true, Refusal(Titled(Unit1 + Unit1.Replace("\"1\"", "\"2\""), 3)).StartsWith("Only insertion after", StringComparison.Ordinal)));
        test("combined apply refuses OB insertion by name", () => Equal(true, Refusal(Ob()).StartsWith("OB insertion is refused", StringComparison.Ordinal)));
        foreach (var (name, shape) in new (string, Func<Fixture>)[] {
            ("standalone LAD FC", () => { var f = new Fixture(); f.Standalone("LAD"); return f; }),
            ("existing LAD FC", () => { var f = new Fixture(); f.Caller.Descendants("ProgrammingLanguage").ToList().ForEach(e => e.Value = "LAD"); f.Inventory["objects"][0]["language"] = "LAD"; return f; }),
            ("FB caller", () => { var f = new Fixture(); f.Caller.Root.Element("SW.Blocks.FC").Name = "SW.Blocks.FB"; f.Inventory["objects"][0]["kind"] = "FB"; return f; }),
            ("multi-instance", Multi), ("software unit", Unit), ("DB-member binding", DbMember), ("UDT tag member", TagMember), ("reused FB", ReusedRoot),
            ("reused dependency", () => { var f = new Fixture(); f.AddDependency(); Add(f, "MotorState", "/plc/types/MotorState", "UDT"); Prove(f, 1); return f; }),
            ("planner blocker", () => { var f = new Fixture(); f.Caller.Root.Element("SW.Blocks.FC").Element("ObjectList").Add(new XElement("MultilingualText", new XAttribute("ID", "9"))); return f; }) })
            test("combined apply refuses " + name, () => { if (Refusal(shape()) == null) throw new Exception("Unverified shape admitted."); });
        test("combined apply refuses PLC/HMI recipes and missing plans", () => {
            var f = new Fixture(); var preview = new JObject { ["payload"] = f.Payload.DeepClone(), ["integrationPlan"] = f.Build() };
            preview["payload"]["action"] = PlcHmiIntegrationPlanner.InstallAction;
            Equal(false, PlcComponentQualificationGate.CombinedApplyRefusal(preview, f.Caller) == null); Equal(false, PlcComponentQualificationGate.CombinedApplyRefusal(new JObject(), null) == null);
        });
        test("combined apply names each coordinator step", () => Equal("apply_graphical_typed_edit", PlcComponentQualificationGate.CombinedApplyStep("graphical_prepared")));
    }

    private static Fixture Unit()
    {
        var f = new Fixture();
        f.Payload["target"]["scopePath"] = "/plc/units/U1"; f.Payload["integration"]["caller"]["blockPath"] = "/plc/units/U1/blocks/namespaces/App/Main";
        f.Fb.Descendants("Namespace").Single().Value = "Lib";
        f.Inventory["objects"][0]["path"] = "/plc/units/U1/blocks/namespaces/App/Main"; f.Inventory["objects"][0]["namespace"] = "App";
        f.Inventory["scope"] = new JObject { ["path"] = "/plc/units/U1", ["kind"] = "softwareUnit", ["namespacePreset"] = "App" };
        return f;
    }
    private static Fixture Multi()
    {
        var f = new Fixture(); var recipe = (JObject)f.Payload["integration"]; recipe.Remove("instanceDb"); recipe["multiInstance"] = new JObject { ["name"] = "Motor1" };
        f.Caller = XDocument.Parse("<Document><SW.Blocks.FB ID=\"0\"><AttributeList><Interface><Sections xmlns=\"" + Interface + "\"><Section Name=\"Static\"><Member Name=\"Existing\" Datatype=\"Bool\"/></Section></Sections></Interface><Name>Main</Name><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList><ObjectList><SW.Blocks.CompileUnit ID=\"3\"><AttributeList><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FB></Document>");
        f.Inventory["objects"][0]["kind"] = "FB";
        return f;
    }
    private static Fixture ReusedRoot() { var f = new Fixture(); Add(f, "Motor", "/plc/blocks/Motor", "FB"); Prove(f, 0); return f; }
    private static void InstanceDb(Fixture f, JArray users) { Add(f, "Motor_1", "/plc/blocks/Motor_1", "DB"); f.Inventory["objects"].Last["number"] = 7; f.Inventory["objects"].Last["instanceOfName"] = "Motor"; f.Inventory["objects"].Last["users"] = users; }
    private static Fixture DbMember()
    {
        var f = new Fixture();
        f.Payload["integration"]["mappings"][0] = JObject.Parse("""{"parameter":"Enable","direction":"Input","dataType":"Bool","dbPath":"/plc/blocks/Data","members":["cmd","enable"]}""");
        Add(f, "Data", "/plc/blocks/Data", "DB"); f.Inventory["objects"].Last["number"] = 5;
        f.Inventory["members"] = JObject.Parse("""{"/plc/blocks/Data":[{"path":["cmd"],"dataType":"\"MotorCmd\"","scalar":false},{"path":["cmd","enable"],"dataType":"Bool","scalar":true}]}""");
        return f;
    }
    private static Fixture TagMember()
    {
        var f = new Fixture(); f.Payload["integration"]["mappings"][1]["members"] = new JArray("running"); f.Inventory["tags"][1]["dataType"] = "\"MotorStatus\"";
        f.Inventory["members"] = JObject.Parse("""{"/plc/tags/IO/tags/MotorRunning":[{"path":["running"],"dataType":"Bool","scalar":true}]}""");
        return f;
    }
    private static void Add(Fixture f, string name, string path, string kind) => ((JArray)f.Inventory["objects"]).Add(new JObject { ["name"] = name, ["path"] = path, ["kind"] = kind, ["number"] = 9, ["language"] = kind == "DB" ? "DB" : "SCL", ["protected"] = false, ["namespace"] = "" });
    private static void Prove(Fixture f, int index)
    {
        var proofs = f.Inventory["reusedVersions"] as JArray ?? (JArray)(f.Inventory["reusedVersions"] = new JArray());
        proofs.Add(new JObject { ["typeName"] = f.Versions[index]["typeName"].DeepClone(), ["typeGuid"] = f.Versions[index]["typeGuid"].DeepClone(), ["versionGuid"] = f.Versions[index]["versionGuid"].DeepClone() });
    }
    private static JObject Typed(JObject plan) => (JObject)((JArray)plan["steps"]).Last["prepareArguments"]["payload"];
    private static string Codes(JToken step) => string.Join(",", (step["planOnlyReasons"] as JArray ?? new JArray()).Select(r => r.Value<string>("code")));

    // The emitted call must pass the actual production compiler against retained native profiles.
    private static void Compile(JObject plan, XDocument caller)
    {
        var payload = Typed(plan); var model = payload["model"].ToObject<TypedGraphicalModel>();
        var profiles = JObject.Parse(File.ReadAllText(Path.Combine(RepositoryFiles.Root(), "fixtures/typed-graphical/profiles.v1.json")))["profiles"].ToObject<TypedGraphicalProfile[]>();
        var context = new TypedGraphicalContext { CpuFamily = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1", Profiles = profiles };
        context.Callees["/plc/blocks/Motor"] = new TypedGraphicalCallee { Path = "/plc/blocks/Motor", Name = "Motor", BlockType = "FB", InterfaceHash = "fixture", Parameters = [new() { Name = "Enable", Section = "Input", DataType = "Bool" }, new() { Name = "Active", Section = "Output", DataType = "Bool" }] };
        context.Instances["instance"] = new TypedGraphicalInstance { Identity = "/plc/blocks/Motor_1", Scope = "GlobalVariable", Components = ["Motor_1"], CalleePath = "/plc/blocks/Motor", DataType = "\"Motor\"", DeclarationHash = "fixture" };
        foreach (JObject operand in (JArray)payload["model"]["operands"])
            context.Operands[operand.Value<string>("key")] = new TypedGraphicalOperand { Key = operand.Value<string>("key"), Identity = operand.Value<string>("key"), Scope = operand.Value<string>("scope") == "constant" ? "LiteralConstant" : "GlobalVariable", DataType = "Bool", Components = operand["components"]?.ToObject<string[]>() ?? [], Value = operand.Value<string>("value"), Writable = operand.Value<string>("key") == "p1", DeclarationHash = "fixture" };
        var built = TypedGraphical.Build(caller, model, context);
        Equal(1, built.Document.Descendants(TypedGraphical.Net + "Call").Count());
    }
    private static void Reject(string code, Fixture f, Action<Fixture> change) { change(f); try { f.Build(); } catch (PlcIntegrationPlanException e) when (e.Code == code) { return; } catch (PlcIntegrationPlanException e) { throw new Exception("Expected " + code + "; got " + e.Code + ": " + e.Message); } throw new Exception("Expected " + code); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
}
