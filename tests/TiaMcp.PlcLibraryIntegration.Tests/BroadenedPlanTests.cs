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

        test("multi-instance plan creates a static member in the FB caller with no planOnly step", () => {
            var f = Multi(); var p = f.Build(); var typed = Typed(p); TypedGraphicalPayload.Validate(typed);
            Equal("createMulti", typed["instances"][0].Value<string>("kind")); Equal("Motor1", typed["instances"][0].Value<string>("name"));
            Equal("\"Motor\"", p["multiInstance"].Value<string>("dataType")); Equal(null, p["instanceDb"]);
            Equal(null, p["steps"][1]["planOnly"]); Equal(null, p["steps"][0]["planOnly"]);
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
            Equal("ComponentInstanceReuseUnqualified", Codes(p["steps"][0]));
        });
        test("reused FB with a new instance DB keeps createGlobal in one typed step", () => { var p = ReusedRoot().Build(); Equal("createGlobal", Typed(p)["instances"][0].Value<string>("kind")); Equal(2, p["instanceDb"].Value<int>("provisionalNumber")); Equal(1, ((JArray)p["steps"]).Count); Equal(null, p["steps"][0]["planOnly"]); });
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

        // Production combined apply (on every Host since 29 October 2026) admits only the natively verified shapes.
        static string Refusal(Fixture f, bool candidates = false) => PlcComponentQualificationGate.CombinedApplyRefusal(new JObject { ["payload"] = f.Payload, ["integrationPlan"] = f.Build() }, f.Caller, candidates);
        static string Features(Fixture f) => string.Join(",", PlcComponentQualificationGate.UnverifiedFeatures(f.Build(), f.Payload, f.Caller).Properties().Select(p => p.Name));
        static Fixture Titled(string networks, int title) { var f = new Fixture(); f.Caller = XDocument.Parse("<Document><SW.Blocks.FC ID=\"0\"><AttributeList><Name>Main</Name><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList><ObjectList>" + networks + "<MultilingualText ID=\"" + title + "\" CompositionName=\"Title\" /></ObjectList></SW.Blocks.FC></Document>"); f.Payload["integration"]["caller"]["insertAfterNetworkId"] = "1"; return f; }
        static Fixture Ob(string eventClass = "ProgramCycle") { var f = new Fixture(); var ob = f.Caller.Root.Element("SW.Blocks.FC"); ob.Name = "SW.Blocks.OB"; f.Inventory["objects"][0]["kind"] = "OB";
            if (eventClass != null) ob.Element("AttributeList").Add(new XElement("SecondaryType", eventClass)); return f; }
        test("combined apply admits the verified existing FBD FC caller", () => Equal(null, Refusal(new Fixture())));
        test("combined apply admits the verified standalone FBD FC", () => { var f = new Fixture(); f.Standalone("FBD"); Equal(null, Refusal(f)); });
        const string Unit1 = "<SW.Blocks.CompileUnit ID=\"1\"><AttributeList><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit>";
        test("combined apply admits insertion after the last network with shifted title IDs", () => Equal(null, Refusal(Titled(Unit1, 2))));
        test("combined apply admits the verified insertion before another network", () => Equal(null, Refusal(Titled(Unit1 + Unit1.Replace("\"1\"", "\"2\""), 3))));
        test("combined apply admits the natively verified FBD OB shape", () => {
            Equal(null, Refusal(Ob())); Equal("", Features(Ob()));
            // Startup and cyclic-interrupt OBs passed natively on 8 November (local.library_integrate.candidate_shapes.ob_event_classes.20261108).
            Equal(null, Refusal(Ob("Startup"))); Equal(null, Refusal(Ob("CyclicInterrupt"))); Equal("", Features(Ob("CyclicInterrupt")));
            Equal(null, Refusal(Ob("TimeOfDay"))); Equal("", Features(Ob("HardwareInterrupt")));
            Equal("obCaller,obEventClass.ProgrammingError", Features(Ob("ProgrammingError"))); Equal("obCaller,obEventClass.Unknown", Features(Ob(null)));
            Equal("Only program-cycle OB callers are verified; this ProgrammingError OB stays plan-only.", Refusal(Ob("ProgrammingError")));
            var lad = Ob("Startup"); lad.Caller.Descendants("ProgrammingLanguage").ToList().ForEach(e => e.Value = "LAD"); lad.Inventory["objects"][0]["language"] = "LAD";
            Equal("", Features(lad)); // LAD startup OB, verified on 8 November (ob_combinations_2)
        });
        // Passed natively on 7 November (local.library_integrate.candidate_shapes.ob_combinations.20261107).
        test("combined apply admits the natively verified LAD OB combination", () => {
            var f = Ob(); f.Caller.Descendants("ProgrammingLanguage").ToList().ForEach(e => e.Value = "LAD");
            f.Inventory["objects"][0]["language"] = "LAD";
            Equal(null, Refusal(f)); Equal("", Features(f));
        });
        Func<Fixture> ladExisting = () => { var f = new Fixture(); f.Caller.Descendants("ProgrammingLanguage").ToList().ForEach(e => e.Value = "LAD"); f.Inventory["objects"][0]["language"] = "LAD"; return f; };
        Func<Fixture> fbCaller = () => { var f = new Fixture(); f.Caller.Root.Element("SW.Blocks.FC").Name = "SW.Blocks.FB"; f.Inventory["objects"][0]["kind"] = "FB"; return f; };
        Func<Fixture> ladFb = () => { var f = fbCaller(); f.Caller.Descendants("ProgrammingLanguage").ToList().ForEach(e => e.Value = "LAD"); f.Inventory["objects"][0]["language"] = "LAD"; return f; };
        Func<Fixture> ladEarlier = () => { var f = Titled(Unit1 + Unit1.Replace("\"1\"", "\"2\""), 3); f.Caller.Descendants("ProgrammingLanguage").ToList().ForEach(e => e.Value = "LAD"); f.Inventory["objects"][0]["language"] = "LAD"; return f; };
        // Passed natively on 31 October (local.library_integrate.candidate_shapes.rerun.20261031): each exact single-feature shape runs in production.
        foreach (var (name, shape) in new (string, Func<Fixture>)[] {
            ("standalone LAD FC", () => { var f = new Fixture(); f.Standalone("LAD"); return f; }), ("existing LAD FC", ladExisting), ("FB caller", fbCaller),
            ("reused FB", ReusedRoot), ("insertion before another network", () => Titled(Unit1 + Unit1.Replace("\"1\"", "\"2\""), 3)) })
        {
            test("combined apply admits the verified " + name + " in production", () => { Equal(null, Refusal(shape())); Equal("", Features(shape())); });
        }
        // Passed natively on 1 November (local.library_integrate.candidate_shapes.multi.20261101): the exact combination of a
        // multi-instance of the reused FB in an existing FBD FB caller. A multi-instance after a fresh import stays a candidate.
        test("combined apply admits the verified reused-FB multi-instance in production", () => { Equal(null, Refusal(ReusedMulti())); Equal("", Features(ReusedMulti())); });
        // A multi-instance caller with dependents (its instance DB, an embedding parent) is refused before admission and claim.
        test("a multi-instance caller with dependents is refused with DependencyProfileUnavailable", () => {
            var plan = ReusedMulti().Build();
            Equal(null, PlcComponentQualificationGate.CombinedApplyDependentsRefusal(plan, new string[0]));
            Equal(null, PlcComponentQualificationGate.CombinedApplyDependentsRefusal(ReusedRoot().Build(), new[] { "/plc/blocks/Main_DB" }));
            var refused = PlcComponentQualificationGate.CombinedApplyDependentsRefusal(plan, new[] { "/plc/blocks/Main_DB", "/plc/blocks/Cycle" });
            Equal("DependencyProfileUnavailable", refused.Value<string>("code"));
            Equal("/plc/blocks/Cycle,/plc/blocks/Main_DB", string.Join(",", refused["dependents"].Select(d => (string)d)));
            Equal(true, refused.Value<string>("message").Contains("/plc/blocks/Main_DB") && refused.Value<string>("message").Contains(plan["caller"].Value<string>("blockPath")));
            // The caller's direct instance DBs have the captured component effect; every other dependent stays refused.
            Equal(null, PlcComponentQualificationGate.CombinedApplyDependentsRefusal(plan, new[] { "/plc/blocks/Main_DB" }, new[] { "/plc/blocks/Main_DB" }));
            Equal("/plc/blocks/Cycle", string.Join(",", PlcComponentQualificationGate.CombinedApplyDependentsRefusal(plan, new[] { "/plc/blocks/Main_DB", "/plc/blocks/Cycle" },
                new[] { "/plc/blocks/Main_DB" })["dependents"].Select(d => (string)d)));
            // With the component transitive effect installed, the typed preparation decides embedding parents and users itself.
            Equal(null, PlcComponentQualificationGate.CombinedApplyDependentsRefusal(plan, new[] { "/plc/blocks/Main_DB", "/plc/blocks/Cycle" }, new[] { "/plc/blocks/Main_DB" }, true));
        });
        // Passed natively on 2 November (local.library_integrate.candidate_shapes.dependent_db.20261102).
        test("a multi-instance caller with its own instance DB is its own verified combination", () => {
            var f = ReusedMulti();
            var features = PlcComponentQualificationGate.UnverifiedFeatures(f.Build(), f.Payload, f.Caller, new string[0], true);
            Equal("dependentInstanceDb+fbCaller+multiInstance+reusedComponent", PlcComponentQualificationGate.ShapeKey(features));
            Equal(0, PlcComponentQualificationGate.UnverifiedFeatures(f.Build(), f.Payload, f.Caller, null, true).Count);
            // The plain multi-instance key does not open its combination with the caller's own DB.
            Equal(4, PlcComponentQualificationGate.UnverifiedFeatures(f.Build(), f.Payload, f.Caller, new[] { "fbCaller+multiInstance+reusedComponent" }, true).Count);
            Equal(null, PlcComponentQualificationGate.CombinedApplyRefusal(new JObject { ["payload"] = f.Payload, ["integrationPlan"] = f.Build() }, f.Caller, false, true));
            // Only a multi-instance changes the caller's interface, so a global-DB plan never names the feature.
            Equal("", string.Join(",", PlcComponentQualificationGate.UnverifiedFeatures(ReusedRoot().Build(), ReusedRoot().Payload, ReusedRoot().Caller, null, true).Properties().Select(p => p.Name)));
        });
        test("the reused-FB multi-instance is exactly the verified combination", () => Equal("fbCaller+multiInstance+reusedComponent",
            PlcComponentQualificationGate.ShapeKey(PlcComponentQualificationGate.UnverifiedFeatures(ReusedMulti().Build(), ReusedMulti().Payload, ReusedMulti().Caller, new string[0]))));
        // Passed natively on 2 November (local.library_integrate.candidate_shapes.combinations.20261102): the combinations run too.
        foreach (var (name, shape) in new (string, Func<Fixture>)[] { ("LAD FB caller", ladFb), ("LAD FC with an earlier placement", ladEarlier) })
            test("combined apply admits the verified combination " + name + " in production", () => { Equal(null, Refusal(shape())); Equal("", Features(shape())); });
        // Candidate shapes: each has a builder but no native evidence, so only a qualification Host's owned scratch admits it.
        foreach (var (name, shape, features) in new (string, Func<Fixture>, string)[] { ("multi-instance", Multi, "fbCaller,multiInstance") })
        {
            test("combined apply refuses candidate " + name + " in production", () => { if (Refusal(shape()) == null) throw new Exception("Unverified shape admitted."); });
            test("qualification Host admits candidate " + name, () => { Equal(null, Refusal(shape(), true)); Equal(features, Features(shape())); });
        }
        foreach (var (name, shape) in new (string, Func<Fixture>)[] {
            ("software unit", Unit), ("DB-member binding", DbMember), ("UDT tag member", TagMember),
            ("reused instance DB", () => { var f = ReusedRoot(); InstanceDb(f, new JArray()); return f; }),
            ("reused dependency", () => { var f = new Fixture(); f.AddDependency(); Add(f, "MotorState", "/plc/types/MotorState", "UDT"); Prove(f, 1); return f; }),
            ("planner blocker", () => { var f = new Fixture(); f.Caller.Root.Element("SW.Blocks.FC").Element("ObjectList").Add(new XElement("MultilingualText", new XAttribute("ID", "9"))); return f; }) })
            test("combined apply refuses " + name + " even on a qualification Host", () => { if (Refusal(shape(), true) == null) throw new Exception("Shape without a builder admitted."); });
        // A shape opens as its exact feature combination; verified single features never open their combinations.
        test("verified shapes open exact feature combinations only", () => {
            JObject Unverified(Fixture f, params string[] shapes) => PlcComponentQualificationGate.UnverifiedFeatures(f.Build(), f.Payload, f.Caller, shapes);
            Equal(0, Unverified(ladExisting(), "ladCaller").Count); Equal(1, Unverified(ladExisting(), "fbCaller").Count);
            Equal("fbCaller+multiInstance", PlcComponentQualificationGate.ShapeKey(Unverified(Multi())));
            Equal(2, Unverified(Multi(), "fbCaller", "multiInstance").Count); Equal(0, Unverified(Multi(), "fbCaller+multiInstance").Count);
            Equal(2, Unverified(Multi(), "multiInstance+fbCaller").Count);
        });
        test("verified shapes have no candidate features", () => { Equal("", Features(new Fixture())); var f = new Fixture(); f.Standalone("FBD"); Equal("", Features(f)); Equal("", Features(Titled(Unit1, 2))); });
        test("candidate refusals name the first unverified feature, else the unverified combination", () => {
            Equal("A multi-instance is verified only for the reused installed FB in an existing LAD or FBD FB caller; other multi-instances stay plan-only.", Refusal(Multi()));
        });
        test("combined apply refuses PLC/HMI recipes and missing plans", () => {
            var f = new Fixture(); var preview = new JObject { ["payload"] = f.Payload.DeepClone(), ["integrationPlan"] = f.Build() };
            preview["payload"]["action"] = PlcHmiIntegrationPlanner.InstallAction;
            Equal(false, PlcComponentQualificationGate.CombinedApplyRefusal(preview, f.Caller) == null); Equal(false, PlcComponentQualificationGate.CombinedApplyRefusal(new JObject(), null) == null);
        });
        test("combined apply names each coordinator step", () => {
            Equal("apply_graphical_typed_edit", PlcComponentQualificationGate.CombinedApplyStep("graphical_prepared"));
            Equal("prepare_library_instantiate", PlcComponentQualificationGate.CombinedApplyStep("created")); Equal("prepare_graphical_typed_edit", PlcComponentQualificationGate.CombinedApplyStep("created", true));
        });
        // Decided before the claim, so step 1 never imports and saves a component that step 2 must refuse (30 October review).
        test("combined apply admits only de-DE-only projects before the claim", () => {
            Equal(null, PlcComponentQualificationGate.CombinedApplyLanguageRefusal(["de-DE"], "de-DE", "de-DE"));
            foreach (var (active, reference, editing) in new[] { (new[] { "de-DE", "en-US" }, "de-DE", "de-DE"), (new[] { "en-US" }, "en-US", "en-US"),
                         (new[] { "de-DE" }, "de-DE", "en-US"), (Array.Empty<string>(), null, null), (null, "de-DE", "de-DE") })
                Equal(true, PlcComponentQualificationGate.CombinedApplyLanguageRefusal(active, reference, editing)?.StartsWith("Only de-DE-only projects", StringComparison.Ordinal));
        });
        // 31 October native finding: the FB caller fixture took FB2, the component FB imported as FB3, and the captured profile
        // refused after the import was saved. An occupied number is now refused before the claim.
        XDocument Version(string kind, string name, string number) => XDocument.Parse("<Document><SW.Blocks.CodeBlockLibraryTypeVersion ID=\"0\"><ObjectList><SW.Blocks." + kind +
            " ID=\"1\" CompositionName=\"ContentObject\"><AttributeList><Name>" + name + "</Name>" + (number == null ? "" : "<Number>" + number + "</Number>") + "</AttributeList></SW.Blocks." + kind + "></ObjectList></SW.Blocks.CodeBlockLibraryTypeVersion></Document>");
        var udt = XDocument.Parse("<Document><SW.Types.PlcTypeLibraryTypeVersion ID=\"0\"><ObjectList><SW.Types.PlcStruct ID=\"1\"><AttributeList><Name>State</Name></AttributeList></SW.Types.PlcStruct></ObjectList></SW.Types.PlcTypeLibraryTypeVersion></Document>");
        JObject Block(string kind, int number, string path) => new JObject { ["kind"] = kind, ["number"] = number, ["path"] = path };
        test("combined apply refuses an import whose block number is occupied, naming the block", () => {
            var refusal = PlcComponentQualificationGate.CombinedApplyNumberRefusal([udt, Version("FB", "Gate", "2")], [Block("OB", 2, "/plc/blocks/Main2"), Block("FC", 2, "/plc/blocks/Peer"), Block("FB", 2, "/plc/blocks/Host")], "Gate", 2);
            Equal("ComponentBlockNumberOccupied", refusal.Value<string>("code")); Equal("/plc/blocks/Host", refusal.Value<string>("conflictingBlock"));
            Equal(true, refusal.Value<string>("message").Contains("FB2 is already used by /plc/blocks/Host") && refusal.Value<string>("message").Contains("captured instance profile"));
            Equal(null, PlcComponentQualificationGate.CombinedApplyNumberRefusal([udt, Version("FB", "Gate", "2")], [Block("FB", 1, "/plc/blocks/Other"), Block("FC", 2, "/plc/blocks/Peer"), Block("DB", 2, "/plc/blocks/Data")], "Gate", 2));
            Equal("ComponentBlockNumberOccupied", PlcComponentQualificationGate.CombinedApplyNumberRefusal([Version("FC", "Helper", "7")], [Block("FC", 7, "/plc/blocks/X")]).Value<string>("code"));
        });
        test("combined apply refuses an FB the captured profile binds to another number, or without a number", () => {
            Equal("CapturedStorageProfileUnavailable", PlcComponentQualificationGate.CombinedApplyNumberRefusal([Version("FB", "Gate", "5")], [], "Gate", 2).Value<string>("code"));
            Equal(null, PlcComponentQualificationGate.CombinedApplyNumberRefusal([Version("FB", "Other", "5")], [], "Gate", 2));
            Equal("ComponentBlockNumberUnknown", PlcComponentQualificationGate.CombinedApplyNumberRefusal([Version("FB", "Gate", null)], [], "Gate", 2).Value<string>("code"));
            // Without a captured context the profile's FB name binds no number, and an occupied number names no profile.
            Equal(null, PlcComponentQualificationGate.CombinedApplyNumberRefusal([Version("FB", "Gate", "5")], [], "Gate", null));
            var occupied = PlcComponentQualificationGate.CombinedApplyNumberRefusal([Version("FB", "Gate", "5")], [Block("FB", 5, "/plc/blocks/Host")], "Gate", null);
            Equal("ComponentBlockNumberOccupied", occupied.Value<string>("code")); Equal(false, occupied.Value<string>("message").Contains("captured instance profile"));
        });
        // Why the refusal runs only before the claim: after a combined apply the installed FB occupies its own number.
        test("an installed component's own FB number would be refused, so only unclaimed changes are checked", () =>
            Equal("/plc/blocks/Gate", PlcComponentQualificationGate.CombinedApplyNumberRefusal([Version("FB", "Gate", "2")], [Block("FB", 2, "/plc/blocks/Gate")], "Gate", 2).Value<string>("conflictingBlock")));
        // 31 October native finding: the nested layout put the import archive at 164 characters under a 51-character root; now 121.
        test("combined apply steps live in a short workspace whose archive path fits TIA's limit for a normal Host root", () => {
            string id = "lib-05d85a3573b8471db3ded20b4118bb2b", root51 = @"D:\Entwicklung\TIA_MCP-public\artifacts\ic1\ie6fdb2";
            string workspace = PlcComponentQualificationGate.CombinedApplyWorkspace(Path.Combine(root51, "changes", "change-" + id), id);
            Equal(Path.Combine(root51, "changes", "c", "05d85a35"), workspace); Equal(121, PlcComponentQualificationGate.CombinedApplyArchivePathLength(workspace));
            string root60 = @"C:\Users\someone\AppData\Local\TiaMcp\artifacts\run-00000001"; Equal(60, root60.Length);
            var fits = PlcComponentQualificationGate.CombinedApplyWorkspace(Path.Combine(root60, "changes", "change-" + id), id);
            const string scratch = "TiaMcpScratch_20261031_150445_6d8af7f2";
            Equal(130, PlcComponentQualificationGate.CombinedApplyArchivePathLength(fits)); Equal(null, PlcComponentQualificationGate.CombinedApplyPathRefusal(Path.Combine(root60, "changes", "change-" + id), id, scratch));
            string root80 = root60 + new string('x', 20);
            var refusal = PlcComponentQualificationGate.CombinedApplyPathRefusal(Path.Combine(root80, "changes", "change-" + id), id, scratch);
            Equal("ComponentPathTooLong", refusal.Value<string>("code")); Equal(150, refusal.Value<int>("length")); Equal(150, refusal.Value<int>("archivePathLength"));
            Equal(true, refusal.Value<string>("message").StartsWith("The step checkpoint archive path would be 150 characters", StringComparison.Ordinal));
        });
        // change_restore retrieves into <changes>/lr_<10 hex>/<project>; a long project name binds before the step archive does.
        test("combined apply refuses a recovery directory over TIA's limit before the claim", () => {
            string id = "lib-05d85a3573b8471db3ded20b4118bb2b", root73 = @"C:\Users\someone\AppData\Local\TiaMcp\artifacts\run-00000001" + new string('y', 13);
            string plan = Path.Combine(root73, "changes", "change-" + id);
            Equal(143, PlcComponentQualificationGate.CombinedApplyArchivePathLength(PlcComponentQualificationGate.CombinedApplyWorkspace(plan, id)));
            Equal(134, PlcComponentQualificationGate.CombinedApplyRecoveryPathLength(plan, "TiaMcpScratch_20261031_150445_6d8af7f2"));
            Equal(null, PlcComponentQualificationGate.CombinedApplyPathRefusal(plan, id, "TiaMcpScratch_20261031_150445_6d8af7f2"));
            var refusal = PlcComponentQualificationGate.CombinedApplyPathRefusal(plan, id, new string('P', 48));
            Equal("ComponentPathTooLong", refusal.Value<string>("code")); Equal(144, refusal.Value<int>("recoveryPathLength")); Equal(144, refusal.Value<int>("length"));
            Equal(true, refusal.Value<string>("message").StartsWith("The recovered project directory of change_restore would be 144 characters", StringComparison.Ordinal));
        });
        test("reuse records an ignored version-comment difference in the plan", () => {
            var f = ReusedRoot(); f.Inventory["reusedVersions"][0]["ignoredDifferences"] = new JArray(new JObject { ["description"] = "Version comment", ["status"] = "ObjectsDifferent" });
            var p = f.Build(); Equal("Version comment", p["reuse"]["versions"][0]["ignoredDifferences"][0].Value<string>("description"));
            Equal(0, ((JArray)ReusedRoot().Build()["reuse"]["versions"][0]["ignoredDifferences"]).Count);
        });
        test("combined apply needs an instance profile covering the planned FB before the claim", () => {
            var callee = new Fixture().Fb; callee.Root.Element("SW.Blocks.FB").Element("AttributeList").Add(new XElement("MemoryLayout", "Optimized"));
            var covering = InstanceTemplate(callee);
            Equal(null, PlcComponentQualificationGate.CombinedApplyInstanceRefusal(callee, "Motor_1", [covering]));
            Equal(true, PlcComponentQualificationGate.CombinedApplyInstanceRefusal(callee, "Motor_1", []).EndsWith("no instance template exists for this CPU and route.", StringComparison.Ordinal));
            var other = new XDocument(callee); other.Descendants(XName.Get("Member", Interface)).First().SetAttributeValue("Datatype", "Int");
            Equal(true, PlcComponentQualificationGate.CombinedApplyInstanceRefusal(other, "Motor_1", [covering]).StartsWith("No instance creation profile covers the FB", StringComparison.Ordinal));
        });
    }

    // A standard global instance DB template whose persistent members match the callee's.
    private static XDocument InstanceTemplate(XDocument callee)
    {
        var template = new XDocument(callee); var db = template.Root.Element("SW.Blocks.FB"); db.Name = "SW.Blocks.InstanceDB";
        var attributes = db.Element("AttributeList"); attributes.Element("ProgrammingLanguage").Value = "DB";
        attributes.Add(new XElement("AutoNumber", "true"), new XElement("Number", "9"), new XElement("InstanceOfName", "Prototype"), new XElement("InstanceOfType", "FB"));
        return template;
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
    private static Fixture ReusedMulti() { var f = Multi(); Add(f, "Motor", "/plc/blocks/Motor", "FB"); Prove(f, 0); return f; }
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
