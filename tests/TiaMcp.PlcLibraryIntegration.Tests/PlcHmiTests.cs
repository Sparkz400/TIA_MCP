using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

internal static class PlcHmiTests
{
    const string Udt = """<Document><SW.Types.PlcStruct ID="0"><AttributeList><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="None"><Member Name="speed" Datatype="Real"/><Member Name="running" Datatype="Bool"/></Section></Sections></Interface><Name>MotorStatus</Name></AttributeList></SW.Types.PlcStruct></Document>""";

    static JObject Install(Fixture f)
    {
        var payload = (JObject)f.Payload.DeepClone();
        payload["action"] = PlcHmiIntegrationPlanner.InstallAction;
        payload["hmi"] = JObject.Parse("""
        {"family":"unified","profile":"unified-comfort","hmiPath":"/d/HMI_1/hmi/HMI_RT_1","connection":"HMI_Connection_1","tagTable":"Motor",
         "tags":[{"name":"MotorSpeed","dataType":"Real","source":{"kind":"instanceMember","members":["status","speed"]}},
                 {"name":"MotorEnable","dataType":"Bool","source":{"kind":"plcTag","tagPath":"/plc/tags/IO/tags/EnableMotor"}}],
         "screenItems":[{"screenPath":"/d/HMI_1/hmi/HMI_RT_1/screens/Main","itemName":"SpeedField","rectangle":{"left":10,"top":10,"width":100,"height":20},"tag":"MotorSpeed"}]}
        """);
        payload["expectedCompilerOutcomes"] = JObject.Parse("""{"plc":{"errors":0,"maxWarnings":0},"hmi":{"errors":0,"maxWarnings":5}}""");
        return payload;
    }

    static JObject Observed(Fixture f) => new JObject {
        ["hmiPath"] = "/d/HMI_1/hmi/HMI_RT_1", ["family"] = "unified", ["profile"] = "unified-comfort",
        ["connection"] = new JObject { ["name"] = "HMI_Connection_1", ["plcPath"] = "/plc" }, ["tagTable"] = new JObject { ["exists"] = true },
        ["existingTagNames"] = new JArray(), ["plcTags"] = new JObject { ["/plc/tags/IO/tags/EnableMotor"] = new JObject { ["name"] = "EnableMotor", ["dataType"] = "Bool" } },
        ["instanceMembers"] = PlcHmiIntegrationPlanner.Members(WithStatus(f.Fb), new Dictionary<string, XDocument> { ["MotorStatus"] = XDocument.Parse(Udt) }),
        ["screens"] = new JArray(new JObject { ["screenPath"] = "/d/HMI_1/hmi/HMI_RT_1/screens/Main", ["exists"] = true, ["width"] = 800, ["height"] = 480, ["itemNames"] = new JArray("Title") }) };

    static XDocument WithStatus(XDocument fb)
    {
        var copy = new XDocument(fb); var ns = XNamespace.Get("http://www.siemens.com/automation/Openness/SW/Interface/v5");
        copy.Descendants(ns + "Section").Single(s => (string)s.Attribute("Name") == "Static").Add(new XElement(ns + "Member", new XAttribute("Name", "status"), new XAttribute("Datatype", "\"MotorStatus\"")));
        return copy;
    }

    static JObject UpdatePayload() => JObject.Parse("""
    {"schemaVersion":1,"action":"plc_hmi_update_plan","planOnly":true,"source":{"libraryId":"lib","revision":"r"},
     "selection":[{"typeGuid":"aaaaaaaa-1111-1111-1111-111111111111","versionGuid":"eeeeeeee-5555-5555-5555-555555555555"}],
     "target":{"projectName":"Fixture","plcPath":"/plc"},"hmi":{"family":"unified","profile":"unified-comfort"},
     "expectedCompilerOutcomes":{"plc":{"errors":0,"maxWarnings":0},"hmi":{"errors":0,"maxWarnings":0}}}
    """);

    static JObject UpdateObserved(string afterSpeedType = "Real", bool removeSpeed = false)
    {
        var after = XDocument.Parse(Udt); var speed = after.Descendants().Single(e => (string)e.Attribute("Name") == "speed");
        if (removeSpeed) speed.Remove(); else speed.SetAttributeValue("Datatype", afterSpeedType);
        after.Descendants().Single(e => e.Name.LocalName == "Section").Add(new XElement(XNamespace.Get("http://www.siemens.com/automation/Openness/SW/Interface/v5") + "Member", new XAttribute("Name", "current"), new XAttribute("Datatype", "Real")));
        JObject Binding(string tag, string member, string type) => new JObject { ["hmiPath"] = "/d/HMI_1/hmi/HMI_RT_1", ["profile"] = "unified-comfort", ["tagPath"] = "/d/HMI_1/hmi/HMI_RT_1/tagTables/Motor/tags/" + tag,
            ["tagName"] = tag, ["connection"] = "HMI_Connection_1", ["plcPath"] = "/plc", ["plcTag"] = "MotorDB.status." + member, ["dataType"] = type, ["blockPath"] = "/plc/blocks/MotorDB",
            ["typeMemberPath"] = new JArray("status"), ["remainder"] = new JArray(member),
            ["consumers"] = new JArray(new JObject { ["screenPath"] = "/d/HMI_1/hmi/HMI_RT_1/screens/Main", ["itemPath"] = "/d/HMI_1/hmi/HMI_RT_1/screens/Main/items/" + tag + "Field", ["property"] = "ProcessValue" }) };
        return new JObject { ["complete"] = true, ["type"] = new JObject { ["name"] = "MotorStatus" },
            ["before"] = PlcHmiIntegrationPlanner.Members(XDocument.Parse(Udt)), ["after"] = PlcHmiIntegrationPlanner.Members(after),
            ["bindings"] = new JArray(Binding("MotorSpeed", "speed", "Real"), Binding("MotorRunning", "running", "Bool")), ["unresolved"] = new JArray() };
    }

    static PlcIntegrationPlanException Refused(string code, Action body)
    {
        try { body(); } catch (PlcIntegrationPlanException e) when (e.Code == code) { return e; }
        throw new Exception("Expected " + code);
    }
    static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }

    internal static void Run(Action<string, Action> test)
    {
        test("combined install keeps PLC steps and proposes only qualified HMI steps", () => {
            var f = new Fixture(); var plan = PlcHmiIntegrationPlanner.BuildInstall(Install(f), f.Build(), Observed(f));
            Equal(false, plan.Value<bool>("applySupported")); Equal("PlcIntegrationNotQualified", plan.Value<string>("applyError"));
            Equal(3, ((JArray)plan["steps"]).Count); Equal("external_tag_create", plan["steps"][2].Value<string>("operation"));
            Equal("EnableMotor", plan["hmi"]["tags"][1].Value<string>("plcSymbol")); Equal("Motor_1.status.speed", plan["hmi"]["tags"][0].Value<string>("plcSymbol"));
            var codes = plan["blockers"].Select(b => b.Value<string>("code")).ToArray();
            Equal(true, codes.Contains("HmiMemberMappingUnqualified") && codes.Contains("HmiScreenItemCreationUnavailable") && codes.Contains("HmiPreservationGateClosed"));
            Equal(5L, plan["expectedCompilerOutcomes"]["hmi"].Value<long>("maxWarnings"));
        });
        test("combined recipe validation refuses before native observation", () => {
            var f = new Fixture();
            foreach (var (code, change) in new (string, Action<JObject>)[] {
                ("HmiFamilyNotQualified", p => p["hmi"]["family"] = "classic"),
                ("UnsupportedHmiProfile", p => p["hmi"]["profile"] = "classic-comfort"),
                ("InvalidIntegrationRecipe", p => p["expectedCompilerOutcomes"]["plc"]["errors"] = 1),
                ("InvalidIntegrationRecipe", p => p["hmi"]["tags"][1]["name"] = "motorspeed"),
                ("InvalidIntegrationRecipe", p => p["hmi"]["screenItems"][0]["tag"] = "Unknown"),
                ("InvalidIntegrationRecipe", p => p["hmi"]["extra"] = true),
                ("InvalidIntegrationRecipe", p => p["hmi"]["tags"][0]["source"]["members"] = new JArray("status.speed")),
                ("UnsupportedIntegrationType", p => p["hmi"]["tags"][0]["dataType"] = "\"MotorStatus\""),
                ("InvalidIntegrationRecipe", p => p["integration"]["caller"]["insertAfterNetworkId"] = "zz") })
            { var p = Install(f); change(p); Refused(code, () => PlcHmiIntegrationPlanner.ValidatePayload(p)); }
        });
        test("combined install refuses mismatched native HMI observations", () => {
            foreach (var (code, change) in new (string, Action<JObject>)[] {
                ("HmiConnectionPlcMismatch", o => o["connection"]["plcPath"] = "/otherPlc"),
                ("UnsupportedHmiProfile", o => o["profile"] = "unified-pc"),
                ("HmiNameConflict", o => o["existingTagNames"] = new JArray("MotorSpeed")),
                ("ObjectNotFound", o => o["tagTable"]["exists"] = false),
                ("IntegrationTagMissing", o => o["plcTags"] = new JObject()),
                ("OperandTypeMismatch", o => o["plcTags"]["/plc/tags/IO/tags/EnableMotor"]["dataType"] = "Int"),
                ("IntegrationMappingMismatch", o => o["instanceMembers"] = new JArray()),
                ("HmiPlacementInvalid", o => o["screens"][0]["width"] = 50),
                ("HmiNameConflict", o => o["screens"][0]["itemNames"] = new JArray("speedfield")) })
            { var f = new Fixture(); var o = Observed(f); change(o); Refused(code, () => PlcHmiIntegrationPlanner.BuildInstall(Install(f), f.Build(), o)); }
        });
        test("member flattening expands one UDT level and leaves arrays and nested UDTs opaque", () => {
            var f = new Fixture(); var members = PlcHmiIntegrationPlanner.Members(WithStatus(f.Fb), new Dictionary<string, XDocument> { ["MotorStatus"] = XDocument.Parse(Udt) });
            Equal(true, members.Any(m => m["path"].ToString(Newtonsoft.Json.Formatting.None) == "[\"status\",\"speed\"]" && m.Value<bool>("scalar")));
            var nested = PlcHmiIntegrationPlanner.Members(XDocument.Parse(Udt.Replace("Datatype=\"Bool\"", "Datatype=\"Array[0..3] of Bool\"").Replace("Datatype=\"Real\"", "Datatype=\"&quot;Inner&quot;\"")));
            Equal(2, nested.Count); Equal(false, nested.Any(m => m.Value<bool>("scalar")));
            Equal("speed", PlcHmiIntegrationPlanner.SplitAtType(members, ["status", "speed"], "MotorStatus")[1][0]);
            Equal(null, PlcHmiIntegrationPlanner.SplitAtType(members, ["Enable"], "MotorStatus"));
        });
        test("non-breaking UDT update previews every binding and proposes a fresh update step", () => {
            var plan = PlcHmiIntegrationPlanner.BuildUpdate(UpdatePayload(), UpdateObserved());
            Equal(2, ((JArray)plan["impact"]["bindings"]).Count); Equal(0, plan["impact"].Value<int>("breakingCount"));
            Equal("added", plan["impact"]["memberChanges"][0].Value<string>("change"));
            Equal("library_update_project", plan["steps"][0].Value<string>("kind")); Equal("entireSelectedPlc", plan["steps"][0]["prepareArguments"]["payload"].Value<string>("updateScope"));
        });
        test("field type change refuses with a precise impact report", () => {
            var error = Refused(PlcHmiIntegrationPlanner.BreakError, () => PlcHmiIntegrationPlanner.BuildUpdate(UpdatePayload(), UpdateObserved("LReal")));
            var report = error.Details; Equal(1, report.Value<int>("breakingCount")); Equal(1, report.Value<int>("affectedConsumerCount"));
            var broken = report["bindings"].Single(b => b.Value<bool>("breaking"));
            Equal("MotorSpeed", broken.Value<string>("tagName")); Equal("typeChanged", broken.Value<string>("status"));
            Equal("Real", broken.Value<string>("beforeDataType")); Equal("LReal", broken.Value<string>("afterDataType"));
            Equal("ProcessValue", broken["consumers"][0].Value<string>("property"));
            Equal("unchanged", report["bindings"].Single(b => b.Value<string>("tagName") == "MotorRunning").Value<string>("status"));
        });
        test("removed field and whole-UDT bindings are breaking", () => {
            Equal("memberRemoved", Refused(PlcHmiIntegrationPlanner.BreakError, () => PlcHmiIntegrationPlanner.BuildUpdate(UpdatePayload(), UpdateObserved(removeSpeed: true))).Details["bindings"][0].Value<string>("status"));
            var o = UpdateObserved(); o["bindings"][0]["remainder"] = new JArray();
            Equal("structureChanged", Refused(PlcHmiIntegrationPlanner.BreakError, () => PlcHmiIntegrationPlanner.BuildUpdate(UpdatePayload(), o)).Details["bindings"][0].Value<string>("status"));
        });
        test("unproven bindings make the impact incomplete instead of passing", () => {
            foreach (var change in new Action<JObject>[] {
                o => o["unresolved"] = new JArray(new JObject { ["tagPath"] = "x", ["reason"] = "connection" }),
                o => o["complete"] = false,
                o => o["bindings"][0]["profile"] = "unified-pc",
                o => o["bindings"][0]["remainder"] = new JArray("missing"),
                o => o["bindings"][1]["dataType"] = "Int" })
            { var o = UpdateObserved(); change(o); Refused(PlcHmiIntegrationPlanner.IncompleteError, () => PlcHmiIntegrationPlanner.BuildUpdate(UpdatePayload(), o)); }
        });
        test("update validation requires one UDT pin and unified profile", () => {
            foreach (var change in new Action<JObject>[] { p => p["selection"] = new JArray(), p => p["hmi"]["family"] = "classic", p => p["planOnly"] = false, p => p["target"]["scopePath"] = "/plc/u" })
            { var p = UpdatePayload(); change(p); try { PlcHmiIntegrationPlanner.ValidatePayload(p); throw new Exception("accepted"); } catch (PlcIntegrationPlanException) { } }
        });
        test("published combined recipe examples pass the worker validator", () => {
            foreach (string file in Directory.GetFiles(Path.Combine(RepositoryFiles.Root(), "fixtures/libraries/plc-hmi"), "*.example.json"))
                PlcHmiIntegrationPlanner.ValidatePayload(JObject.Parse(File.ReadAllText(file)));
        });
        test("combined recipe validation precedes native project selection; coordinator excludes combined plans", () => {
            string root = RepositoryFiles.Root(); string source = File.ReadAllText(Path.Combine(root, "src/TiaMcp.Openness.V21/OpennessSession.LibraryChanges.cs"));
            int start = source.IndexOf("private JObject LibraryChangePrepare", StringComparison.Ordinal);
            int validate = source.IndexOf("ValidatePlcHmiRecipe(payload)", start, StringComparison.Ordinal), native = source.IndexOf("SelectProject(", start, StringComparison.Ordinal);
            if (start < 0 || validate < 0 || validate > native) throw new Exception("Combined validation must run before native lookup.");
            foreach (string file in new[] { "OpennessSession.PlcComponentCoordinator.cs", "OpennessSession.ComponentWindow.cs" })
                if (!File.ReadAllText(Path.Combine(root, "src/TiaMcp.Openness.V21", file)).Contains("PlcHmiIntegrationPlanner.IsRecipe(plan.Payload)")) throw new Exception(file + " must refuse combined plans.");
        });
    }
}
