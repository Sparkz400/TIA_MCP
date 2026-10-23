using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;

internal static class AlarmBindingChecks
{
    internal static void AddEvidence(Fixture f)
    {
        f.AddDependencyEvidence(); SuppliedAlarmChecks.AddEvidence(f);
        foreach (var (name, member, role) in new[] { ("condition", "Enable", "condition"), ("parameter", "RunCmd", "parameter") })
            AddOperand(f, name, member, role);
        f.SaveReview();
    }
    private static void AddOperand(Fixture f, string name, string member, string role)
    {
        string path = Fixture.Plc + "/operands/" + name;
        f.Model["nodes"]!.AsArray().Add(new JsonObject { ["nodeId"] = name, ["objectPath"] = path, ["kind"] = "member",
            ["plcPath"] = Fixture.Plc, ["scopePath"] = Fixture.Plc,
            ["metadata"] = new JsonObject { ["syntheticFixture"] = true, ["rawLabel"] = "Überlast / overload",
                ["alarmOperand"] = new JsonObject { ["schemaVersion"] = "alarm-operand.v1", ["role"] = role,
                    ["blockPath"] = Fixture.Block("FB_Conveyor"), ["instance"] = f.Request()["instance"]!.DeepClone(),
                    ["memberPath"] = new JsonArray(member), ["datatype"] = "Bool" } } });
        f.Model["edges"]!.AsArray().Add(new JsonObject { ["kind"] = "reads",
            ["source"] = new JsonObject { ["nodeId"] = "supervision", ["objectPath"] = Fixture.Plc + "/supervisions/17" },
            ["target"] = new JsonObject { ["nodeId"] = name, ["objectPath"] = path },
            ["evidence"] = new JsonObject { ["origin"] = "parsedSupported", ["resolution"] = "resolved", ["location"] = "Normalized supplied test observation only" } });
    }
    internal static JsonObject Request(Fixture f)
    {
        var request = SuppliedAlarmChecks.Request(f);
        request["alarmBindings"] = new JsonArray(
            Selection("condition", "enable", 4), Selection("condition-actual", "enableSource", 4, "condition"), Selection("parameter", "final", 5));
        return request;
    }
    private static JsonObject Selection(string id, string signal, int edge, string? node = null) => new() {
        ["id"] = id, ["signalId"] = signal, ["operand"] = new JsonObject { ["nodeId"] = node ?? id, ["objectPath"] = Fixture.Plc + "/operands/" + (node ?? id) },
        ["edgeIndices"] = new JsonArray(1, edge) };
    private static JsonObject Node(Fixture f, string name = "condition") => f.Model["nodes"]!.AsArray().OfType<JsonObject>().Single(n => (string?)n["nodeId"] == name);
    private static JsonObject Watch() => new() { ["tableName"] = "Alarm source inspection", ["tiaVersion"] = 21,
        ["externalStorage"] = new JsonArray(new JsonObject { ["objectPath"] = Fixture.Block("DB_IO"), ["reason"] = "Selected original call arguments." }) };
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok) { if (!ok) throw new Exception("Alarm binding check " + (checks + 1)); checks++; }
        void Reject(string code, Action action) { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        using var f = new Fixture(); AddEvidence(f);
        var request = Request(f); string input = request.ToJsonString(); var receipt = f.Planner.Plan(request); var plan = receipt["plan"]!;
        string id = receipt["id"]!.GetValue<string>(); var bindings = plan["alarmBindings"]!;
        Check(input == request.ToJsonString()); Check(bindings["allSelectedMatched"]!.GetValue<bool>());
        Check(bindings["bindings"]!.AsArray().Count == 3);
        foreach (var row in bindings["bindings"]!.AsArray())
        {
            Check((string?)row!["status"] == "capturedDeclarationMatch");
            Check(JsonNode.DeepEquals(row["operand"], Node(f, (string?)row["id"] == "parameter" ? "parameter" : "condition")));
            foreach (var edge in row["relationships"]!.AsArray()) Check(JsonNode.DeepEquals(edge!["edge"], f.Model["edges"]![edge["index"]!.GetValue<int>()]));
            Check((string?)row["declaration"]!["objectPath"] == Fixture.Block("FB_Conveyor") && (string?)row["declaration"]!["datatype"] == "Bool");
            Check(row["storage"]!["candidates"]!.AsArray().Count == 2 && row["storage"]!["gaps"]!.AsArray().Count == 0);
        }
        var actual = bindings["bindings"]![1]!["storage"]!["candidates"]![1]!;
        Check((string?)actual["storagePath"] == Fixture.Block("DB_IO") && (string?)actual["memberPath"]![0] == "Enable2");
        Check((string?)actual["parameterBinding"]!["parameter"] == "Enable" && (string?)actual["callSite"]!["objectPath"] == Fixture.Block("FB_Line"));
        foreach (string field in new[] { "nativeQualified", "sourceAuthenticityVerified", "runtimeAlarmAssociationVerified", "coverageComplete" }) Check(!bindings[field]!.GetValue<bool>());
        var watch = new DiagnosticWatchPlanning(f.Root).Plan(id, Watch());
        Check(JsonNode.DeepEquals(new DiagnosticStore(f.Root).Read(watch["id"]!.GetValue<string>())["alarmBindings"], bindings));
        Check(JsonNode.DeepEquals(f.Prepare(receipt)["specification"]!["alarmBindings"], bindings));
        var hmi = Request(f); hmi["alarmContext"]!["nodeId"] = "hmi"; hmi["alarmContext"]!["objectPath"] = "/projects/Fixture/hmi/alarms/42";
        foreach (var row in hmi["alarmBindings"]!.AsArray()) row!["edgeIndices"]!.AsArray().Insert(0, JsonValue.Create(2));
        Check(f.Planner.Plan(hmi)["plan"]!["alarmBindings"]!["allSelectedMatched"]!.GetValue<bool>());
        foreach (var (name, mutate, code) in new (string, Action<JsonObject>, string)[] {
            ("no context", r => r.Remove("alarmContext"), "InvalidRequest"),
            ("null", r => r["alarmBindings"] = null, "InvalidRequest"),
            ("empty", r => r["alarmBindings"] = new JsonArray(), "InvalidRequest"),
            ("duplicate", r => r["alarmBindings"]!.AsArray().Add(r["alarmBindings"]![0]!.DeepClone()), "InvalidRequest"),
            ("signal", r => r["alarmBindings"]![0]!["signalId"] = "absent", "InvalidRequest"),
            ("node", r => r["alarmBindings"]![0]!["operand"]!["nodeId"] = "parameter", "InvalidRequest"),
            ("extra", r => r["alarmBindings"]![0]!["nativeQualified"] = true, "InvalidRequest"),
            ("bad index", r => r["alarmBindings"]![0]!["edgeIndices"]![0] = 50000, "InvalidRequest"),
            ("fraction", r => r["alarmBindings"]![0]!["edgeIndices"]![0] = 0.5, "InvalidRequest"),
            ("repeated edge", r => r["alarmBindings"]![0]!["edgeIndices"] = new JsonArray(1,1), "InvalidRequest"),
            ("long path", r => r["alarmBindings"]![0]!["edgeIndices"] = new JsonArray(0,1,2,3,4,5,6,7,8), "InvalidRequest") })
        { var bad = Request(f); mutate(bad); Reject(code, () => f.Planner.Plan(bad)); }
        foreach (var (status, mutate) in new (string, Action<Fixture>)[] {
            ("observationUnavailable", x => Node(x)["metadata"]!.AsObject().Remove("alarmOperand")),
            ("observationUnavailable", x => Node(x)["metadata"]!["alarmOperand"]!["schemaVersion"] = "unknown-vendor-layout"),
            ("observationUnavailable", x => Node(x)["access"] = new JsonObject { ["state"] = "protected" }),
            ("invalidObservation", x => Node(x)["metadata"]!["alarmOperand"]!["nativeQualified"] = true),
            ("invalidObservation", x => Node(x)["metadata"]!["alarmOperand"]!["schemaVersion"] = true),
            ("invalidObservation", x => Node(x)["metadata"]!["isKnowHowProtected"] = "false"),
            ("invalidObservation", x => Node(x)["metadata"]!["alarmOperand"]!["instance"]!["memberPath"] = new JsonArray(true)),
            ("invalidObservation", x => Node(x)["metadata"]!["alarmOperand"]!["memberPath"] = new JsonArray("Enable\u0001")),
            ("ownerMismatch", x => Node(x)["scopePath"] = "another-unit"),
            ("ownerMismatch", x => Node(x)["metadata"]!["alarmOperand"]!["instance"]!["memberPath"] = new JsonArray("Conv1")),
            ("datatypeMismatch", x => Node(x)["metadata"]!["alarmOperand"]!["datatype"] = "Int"),
            ("memberUnavailable", x => Node(x)["metadata"]!["alarmOperand"]!["memberPath"] = new JsonArray("Missing")),
            ("relationshipUnresolved", x => x.Model["edges"]![4]!["evidence"]!["resolution"] = "unresolved"),
            ("relationshipUnresolved", x => x.Model["edges"]![4]!["kind"] = "usesType"),
            ("storageMismatch", x => Node(x)["metadata"]!["alarmOperand"]!["memberPath"] = new JsonArray("Fault")) })
        {
            using var changed = new Fixture(); AddEvidence(changed); mutate(changed); changed.SaveReview();
            var result = changed.Planner.Plan(Request(changed)); var record = result["plan"]!;
            Check((string?)record["alarmBindings"]!["bindings"]![0]!["status"] == status);
            Check(!record["alarmBindings"]!["allSelectedMatched"]!.GetValue<bool>());
            Reject("AlarmBindingUnresolved", () => new DiagnosticWatchPlanning(changed.Root).Plan(result["id"]!.GetValue<string>(), Watch()));
            Reject("AlarmBindingUnresolved", () => changed.Prepare(result));
        }
        foreach (string field in new[] { "status", "declaration", "operand", "allSelectedMatched", "runtimeAlarmAssociationVerified" })
        {
            var forged = plan.DeepClone().AsObject(); var value = forged["alarmBindings"]!;
            if (field == "status") value["bindings"]![0]!["status"] = "storageMismatch";
            if (field == "declaration") value["bindings"]![0]!["declaration"]!["xmlLine"] = 999;
            if (field == "operand") value["bindings"]![0]!["operand"]!["metadata"]!["rawLabel"] = "forged";
            if (field == "allSelectedMatched") value[field] = false;
            if (field == "runtimeAlarmAssociationVerified") value[field] = true;
            string forgedId = new DiagnosticStore(f.Root).Save(forged)["id"]!.GetValue<string>();
            Reject("CaptureMismatch", () => new DiagnosticWatchPlanning(f.Root).Plan(forgedId, Watch()));
            Reject("CaptureMismatch", () => f.Traces.Prepare(forgedId, f.Settings()));
        }
        using (var changed = new Fixture())
        {
            AddEvidence(changed); var before = changed.Planner.Plan(Request(changed));
            changed.ReplaceXml("FB_Conveyor", s => s.Replace("Name=\"Enable\" Datatype=\"Bool\"", "Name=\"Enable\" Datatype=\"Int\""));
            changed.ReplaceXml("DB_Line", s => s.Replace("Name=\"Enable\" Datatype=\"Bool\"", "Name=\"Enable\" Datatype=\"Int\""));
            var nextRequest = Request(changed); nextRequest["hypotheses"]![0]!.AsObject().Remove("prediction");
            var after = changed.Planner.Plan(nextRequest);
            Check((string?)after["plan"]!["alarmBindings"]!["bindings"]![0]!["status"] == "datatypeMismatch");
            Check((string?)before["plan"]!["reviewId"] != (string?)after["plan"]!["reviewId"]);
            var oldSettings = changed.Settings(); oldSettings["reviewId"] = before["plan"]!["reviewId"]!.DeepClone();
            Check(changed.Traces.Prepare(before["id"]!.GetValue<string>(), oldSettings)["id"] is not null);
        }
        using (var oversized = new Fixture())
        {
            AddEvidence(oversized); Node(oversized)["metadata"]!["opaque"] = new string('x', 530000); oversized.SaveReview();
            Reject("ArtifactCapacity", () => oversized.Planner.Plan(Request(oversized)));
        }
        var many = Request(f); var selections = many["alarmBindings"]!.AsArray(); selections.Clear();
        for (int i = 0; i < 16; i++) selections.Add(Selection("binding-" + i, "enable", 4, "condition"));
        Check(f.Planner.Plan(many)["plan"]!["alarmBindings"]!["bindings"]!.AsArray().Count == 16);
        selections.Add(Selection("extra", "enable", 4, "condition")); Reject("InvalidRequest", () => f.Planner.Plan(many));
        var malformed = Request(f); malformed["alarmBindings"]![0]!["id"] = true; Reject("InvalidRequest", () => f.Planner.Plan(malformed));
        var mappings = new JsonArray(plan["signals"]!.AsArray().Select(s => (JsonNode?)new JsonObject {
            ["id"] = s!["id"]!.DeepClone(), ["storagePath"] = s["storagePath"]!.DeepClone(), ["memberPath"] = s["memberPath"]!.DeepClone(), ["reason"] = "Explicit same declaration." }).ToArray());
        var replan = new JsonObject { ["reviewId"] = f.ReviewId, ["blockPath"] = Fixture.Block("FB_Conveyor"),
            ["instance"] = f.Request()["instance"]!.DeepClone(), ["reason"] = "Recheck selected operand observations.", ["signalMappings"] = mappings };
        Check(new DiagnosticReplanner(f.Root).Replan(id, replan)["plan"]!["alarmBindings"] is null);
        replan["alarmBindings"] = Request(f)["alarmBindings"]!.DeepClone();
        Reject("InvalidRequest", () => new DiagnosticReplanner(f.Root).Replan(id, replan));
        replan["alarmContext"] = Request(f)["alarmContext"]!.DeepClone();
        Check(new DiagnosticReplanner(f.Root).Replan(id, replan)["plan"]!["alarmBindings"]!["allSelectedMatched"]!.GetValue<bool>());
        foreach (int count in new[] { 2, 65 })
        {
            using var repeated = new Fixture(); AddEvidence(repeated);
            repeated.ReplaceXml("FB_Line", xml => {
                var doc = XDocument.Parse(xml); var call = doc.Descendants().Single(e => e.Name.LocalName == "CallInfo" &&
                    e.Elements().Where(c => c.Name.LocalName == "Instance").Descendants().Any(c => (string?)c.Attribute("Name") == "Conv2"));
                for (int i = 1; i < count; i++) call.AddAfterSelf(new XElement(call));
                return doc.ToString(); });
            var row = repeated.Planner.Plan(Request(repeated))["plan"]!["alarmBindings"]!["bindings"]![0]!;
            Check((string?)row["status"] == (count == 2 ? "capturedDeclarationMatch" : "resolutionIncomplete"));
            Check(count == 2 ? row["storage"]!["candidates"]!.AsArray().Count == 3 : row["storage"]!["truncated"]!.GetValue<bool>());
        }
        foreach (int count in new[] { 256, 257 })
        {
            using var parameters = new Fixture(); AddEvidence(parameters);
            parameters.ReplaceXml("FB_Line", xml => {
                var doc = XDocument.Parse(xml); var call = doc.Descendants().Single(e => e.Name.LocalName == "CallInfo" &&
                    e.Elements().Where(c => c.Name.LocalName == "Instance").Descendants().Any(c => (string?)c.Attribute("Name") == "Conv2"));
                int existing = call.Elements().Count(e => e.Name.LocalName == "Parameter");
                for (int i = existing; i < count; i++) call.Add(new XElement(call.Name.Namespace + "Parameter", new XAttribute("Name", "Unused" + i)));
                return doc.ToString(); });
            var row = parameters.Planner.Plan(Request(parameters))["plan"]!["alarmBindings"]!["bindings"]![0]!;
            Check((string?)row["status"] == (count == 256 ? "capturedDeclarationMatch" : "resolutionIncomplete"));
            Check(row["storage"]!["truncated"]!.GetValue<bool>() == (count > 256));
        }
        using (var opaque = new Fixture())
        {
            AddEvidence(opaque); opaque.ReplaceXml("FB_Line", s => s.Replace("Name='DB_IO'", "Name='MissingDB'"));
            var record = opaque.Planner.Plan(Request(opaque))["plan"]!.AsObject();
            Check((string?)record["alarmBindings"]!["bindings"]![0]!["status"] == "resolutionIncomplete");
            record["alarmBindings"]!["allSelectedMatched"] = true;
            foreach (var row in record["alarmBindings"]!["bindings"]!.AsArray()) row!["status"] = "capturedDeclarationMatch";
            string forged = new DiagnosticStore(opaque.Root).Save(record)["id"]!.GetValue<string>();
            Reject("CaptureMismatch", () => opaque.Traces.Prepare(forged, opaque.Settings()));
        }
        using (var missing = new Fixture())
        {
            AddEvidence(missing); missing.Documents.Single(d => (string?)d!["objectPath"] == Fixture.Block("Main"))!["status"] = "unavailable"; missing.SaveReview();
            Check((string?)missing.Planner.Plan(Request(missing))["plan"]!["alarmBindings"]!["bindings"]![0]!["status"] == "callEvidenceUnavailable");
        }
        using (var alias = new Fixture())
        {
            AddEvidence(alias);
            alias.ReplaceXml("FB_Conveyor", s => s.Replace("<Section Name=\"Temp\">", "<Section Name=\"InOut\"><Member Name=\"Alias\" Datatype=\"Bool\"/></Section><Section Name=\"Temp\">"));
            alias.ReplaceXml("FB_Line", s => s.Replace("</Parameter></CallInfo>", "</Parameter><Parameter Name='Alias'><Token Text='&lt;=&gt;'/><Access Scope='GlobalVariable'><Symbol><Component Name='DB_IO'/><Component Name='Permit2'/></Symbol></Access></Parameter></CallInfo>"));
            Node(alias)["metadata"]!["alarmOperand"]!["memberPath"] = new JsonArray("Alias"); alias.SaveReview();
            var selection = Request(alias); selection["alarmBindings"] = new JsonArray(Selection("alias", "permit", 4, "condition"));
            var row = alias.Planner.Plan(selection)["plan"]!["alarmBindings"]!["bindings"]![0]!;
            Check((string?)row["status"] == "capturedDeclarationMatch");
            Check(row["storage"]!["candidates"]!.AsArray().All(c => (string?)c!["storagePath"] == Fixture.Block("DB_IO") && (string?)c["memberPath"]![0] == "Permit2"));
        }
        Console.WriteLine($"PASS alarm executable bindings: {checks} checks; normalized supplied operands, no native alarm association claim.");
    }
}
