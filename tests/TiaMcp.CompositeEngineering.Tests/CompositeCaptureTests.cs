using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;

/// <summary>Native composite producer and rebind recipe against fake worker responses; no TIA Portal.</summary>
internal static class CompositeCaptureTests
{
    private sealed class FakeWorker(Func<string, JsonObject, JsonObject> answer) : IBridgeClient
    {
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(answer(operation, JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject()), BridgeProtocol.Json));
    }

    internal static void Run(string parent)
    {
        string root = Path.Combine(parent, "capture"); Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
        const string project = "/projects/Plant", plc = project + "/PLC", module = plc + "/Module", replacement = plc + "/Module2", peer = plc + "/Peer", axis = plc + "/technology/Axis";
        const string projectPath = "D:/Plant/Plant.ap21", amlPath = "/CAEXFile[1]/InstanceHierarchy[1]/InternalElement[1]";
        JsonObject Node(string path, string kind, string? owner) => new() { ["nodeId"] = path, ["objectPath"] = path, ["kind"] = kind, ["name"] = path[(path.LastIndexOf('/') + 1)..],
            ["parentId"] = owner, ["plcPath"] = plc, ["metadata"] = new JsonObject() };
        string Review(string hardware) => (string)new ProjectReviewStore(root).Save(new JsonObject { ["schemaVersion"] = "project-model.v1",
            ["project"] = new JsonObject { ["name"] = "Plant", ["projectPath"] = projectPath, ["host"] = "synthetic" },
            ["nodes"] = new JsonArray(Node(project, "project", null), Node(plc, "plc", project), Node(hardware, "hardware", plc), Node(hardware + "/port", "hardware", hardware),
                Node(peer, "hardware", plc), Node(peer + "/port", "hardware", peer), Node(axis, "technologyObject", plc)), ["edges"] = new JsonArray() }, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
        string beforeReview = Review(module), afterReview = Review(replacement);

        // Fake responses follow the worker output shapes of cax_export, connection_inspect, drive_observe and technology_read.
        JsonObject Project() => new() { ["name"] = "Plant", ["path"] = projectPath };
        JsonObject Hardware(string path) => new() { ["valueSemantics"] = "engineeringReference", ["resolution"] = "resolved", ["kind"] = "hardware", ["hardwarePath"] = path };
        JsonObject Slot(string role, string slot, string? target, JsonObject? reference) => new() { ["role"] = role, ["service"] = "axis", ["observationPath"] = axis + "/hardwareConnections/axis/" + slot,
            ["status"] = "observed", ["isConnected"] = target is not null, ["attributes"] = new JsonArray(new JsonObject { ["name"] = "Target", ["value"] = target is null ? null : reference }) };
        JsonObject Technology(string? target) => new() { ["project"] = Project(), ["plcPath"] = plc,
            ["technologyObject"] = new JsonObject { ["path"] = axis, ["name"] = "Axis", ["systemLibraryType"] = "TO_PositioningAxis", ["systemLibraryVersion"] = "8.0" },
            ["hardwareConnections"] = new JsonArray(Slot("actor", "actor", target, target is null ? null : Hardware(target)),
                Slot("sensor", "sensors/0", target, target is null ? null : new JsonObject { ["valueSemantics"] = "engineeringReference", ["resolution"] = "resolved", ["kind"] = "channel",
                    ["parent"] = Hardware(target), ["number"] = 3, ["type"] = "Digital", ["ioType"] = "Input" }),
                new JsonObject { ["role"] = "torque", ["service"] = "axis", ["observationPath"] = axis + "/hardwareConnections/axis/torque", ["status"] = "notExposed" }),
            ["hardwareConnectionCoverage"] = new JsonObject { ["complete"] = false },
            ["parameters"] = new JsonArray(
                new JsonObject { ["name"] = "Actor.Type", ["type"] = "System.Int32", ["valueAccess"] = "ReadWrite", ["writable"] = true, ["valueCaptured"] = true, ["valueComplete"] = true, ["valueStatus"] = "captured", ["value"] = 1 },
                new JsonObject { ["name"] = "Password", ["type"] = null, ["writable"] = null, ["excluded"] = true, ["value"] = "<excluded>", ["valueCaptured"] = false, ["valueComplete"] = false, ["valueStatus"] = "excluded" }),
            ["parametersComplete"] = false };
        JsonObject Cax(string id)
        {
            byte[] bytes = Encoding.UTF8.GetBytes($"""<CAEXFile SchemaVersion="2.15" FileName="plant.aml"><InstanceHierarchy Name="Plant"><InternalElement Name="Module" ID="{id}"><ExternalInterface Name="P" ID="{id}-p"/></InternalElement></InstanceHierarchy></CAEXFile>""");
            string file = Path.Combine(root, "exports", id + "-" + Guid.NewGuid().ToString("N") + ".aml"); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, bytes);
            return new() { ["project"] = Project(), ["artifact"] = new JsonObject { ["path"] = file, ["bytes"] = bytes.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["mediaType"] = "application/xml" } };
        }
        JsonObject Endpoint(string owner) => new() { ["ownerPath"] = owner, ["interfacePath"] = owner + "/port", ["reason"] = null };
        JsonObject Connections(string hardware) => new() { ["project"] = Project(), ["reviewSnapshot"] = new JsonObject { ["connectionsComplete"] = true, ["consumersComplete"] = false,
            ["connections"] = new JsonArray(new JsonObject { ["id"] = "c", ["origin"] = "engineering", ["protocol"] = "S7", ["local"] = Endpoint(hardware), ["partner"] = Endpoint(peer), ["properties"] = new JsonObject() }),
            ["consumers"] = new JsonArray() } };
        JsonObject Drives(string hardware) => new() { ["project"] = Project(), ["drives"] = new JsonArray(new JsonObject { ["hardwarePath"] = hardware, ["driveObjects"] = new JsonArray(new JsonObject {
            ["driveObjectNumber"] = 2, ["snapshot"] = JsonNode.Parse("""{"identity":{"driveId":"DO2","family":null,"orderNumber":"6SL3","firmware":"5.2","parameterSet":null,"unknownReason":"Drive family and parameter set are not exposed."},"valueDomain":"offline","parametersComplete":false,"parameters":[{"name":"p1","number":1,"arrayIndex":-1,"parentName":null,"unit":"","access":"unknown","value":{"kind":"scalar","type":"opaque","text":"1"},"metadata":{}}],"telegramsComplete":true,"telegrams":[]}""") }) }) };
        Func<string, JsonObject, JsonObject> Answer(string hardware, string amlId, string? target, Dictionary<string, string>? errors = null) => (operation, args) =>
        {
            if (errors?.TryGetValue(operation, out string? code) == true) throw new BridgeOperationException(code, "Fake worker refusal for " + operation);
            if ((string?)args["projectName"] != "Plant") throw new Exception("Captures must select the saved review's project");
            return operation switch { "cax_export" => Cax(amlId), "connection_inspect" => Connections(hardware), "drive_observe" => Drives(hardware),
                "technology_read" when (string?)args["technologyPath"] == axis => Technology(target), _ => throw new BridgeOperationException("UnknownOperation", "Unknown worker operation: " + operation) };
        };
        JsonObject Capture(Func<string, JsonObject, JsonObject> answer, string review) =>
            CompositeEngineeringCapture.Capture(new FakeWorker(answer), root, review, null, CancellationToken.None).GetAwaiter().GetResult();
        JsonObject Domain(JsonObject capture, string domain) => capture["captures"]!.AsArray().Single(c => (string?)c!["domain"] == domain)!.AsObject();
        string Status(JsonObject capture, string domain) => (string)Domain(capture, domain)["status"]!;

        // A base without the CAx, connection and drive producers: only technology_read is native.
        var absent = new Dictionary<string, string> { ["cax_export"] = "UnknownOperation", ["connection_inspect"] = "UnknownOperation", ["drive_observe"] = "UnknownOperation" };
        var partial = Capture(Answer(module, "m1", module, absent), beforeReview);
        Check(Status(partial, "automationMl") == "unavailable" && Status(partial, "connections") == "unavailable" && Status(partial, "drives") == "unavailable" && Status(partial, "technology") == "native",
            "Absent worker operations are recorded unavailable while technology_read stays native");
        Check(partial["phase"]!["automationMlRef"] is null && !(bool)partial["composable"]! && partial["phase"]!["connections"]!.AsArray().Count == 0 && partial["phase"]!["technology"]!.AsArray().Count == 1,
            "An unavailable AML capture leaves the phase explicitly non-composable instead of failing");
        string toRecord = (string)partial["phase"]!["technology"]![0]!;
        var relations = new TechnologyProfileWorkflowStore(root).Page(toRecord, "relationships")["rows"]!.AsArray().Select(r => r!["value"]!).ToArray();
        Check(relations.Length == 4 && relations.All(r => (string?)r["relationship"]!["state"] == "declared" && (string?)r["relationship"]!["targetPath"] == module && (string?)r["targetResolution"] == "capturedObject")
            && relations.Any(r => (int?)r["relationship"]!["metadata"]!["sensorIndex"] == 0 && (string?)r["relationship"]!["metadata"]!["target"]!["kind"] == "channel"),
            "technology_read slots become exact TO hardware relationships; a channel targets its module and not-exposed slots are omitted");
        var parameters = new TechnologyProfileWorkflowStore(root).Page(toRecord, "observations")["rows"]!.AsArray().Select(r => r!["value"]!["parameter"]!).ToArray();
        Check(parameters.Any(p => (string?)p["name"] == "Password" && (string?)p["value"]!["kind"] == "unavailable") && parameters.Any(p => (string?)p["access"] == "readWrite" && (string?)p["value"]!["text"] == "1"),
            "Excluded parameter values stay unavailable; captured values keep their exact text");

        var refusing = Capture(Answer(module, "m1", module, new() { ["drive_observe"] = "UnsupportedCapability", ["connection_inspect"] = "HardwareNotFound" }), beforeReview);
        Check(Status(refusing, "drives") == "unavailable" && Status(refusing, "connections") == "failed" && (string?)Domain(refusing, "connections")["errorCode"] == "HardwareNotFound",
            "A missing product option is unavailable; any other completed refusal is retained as failed with its code");
        try { Capture((op, args) => { var r = Answer(module, "m1", module)(op, args); r["project"]!["path"] = "D:/Other/Other.ap21"; return r; }, beforeReview); throw new Exception("Expected refusal"); }
        catch (BridgeException e) when (e.Code == "ProjectMismatch") { Check(true, "A capture answered for another project refuses the whole phase"); }

        var before = Capture(Answer(module, "m1", module), beforeReview);
        var after = Capture(Answer(replacement, "m2", null), afterReview);
        Check(new[] { before, after }.All(c => (bool)c["composable"]! && c["captures"]!.AsArray().All(d => (string?)d!["status"] == "native")
            && new[] { "connections", "drives", "technology" }.All(f => c["phase"]![f]!.AsArray().Count == 1)), "All four native captures fill both phases with retained records");

        var uploads = new ArtifactUploadStore(root);
        string Upload(JsonObject value)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json);
            var begun = uploads.Begin("composite.json", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            uploads.Write(begun.UploadId, 0, Convert.ToBase64String(bytes)); return uploads.Commit(begun.UploadId).ArtifactRef!;
        }
        JsonObject Aml(string phase, JsonObject capture) => new() { ["kind"] = "amlObject", ["phase"] = phase, ["recordRef"] = capture["phase"]!["automationMlRef"]!.DeepClone(), ["path"] = amlPath };
        JsonObject Model(string phase, string review, string path) => new() { ["kind"] = "model", ["phase"] = phase, ["reviewId"] = review, ["nodeId"] = path, ["objectPath"] = path };
        JsonObject Mapping(string phase, JsonObject capture, string review, string hardware) => new() { ["id"] = phase + "-hardware", ["phase"] = phase, ["purpose"] = "hardware",
            ["reason"] = "Fixture ECAD association", ["source"] = Aml(phase, capture), ["target"] = Model(phase, review, hardware) };
        var store = new CompositeEngineeringWorkflowStore(root);
        JsonObject Plan(JsonObject afterCapture, JsonArray mappings, JsonArray correspondences, bool available = true)
        {
            var request = new JsonObject { ["schemaVersion"] = "composite-engineering-request.v3", ["before"] = before["phase"]!.DeepClone(), ["after"] = afterCapture["phase"]!.DeepClone(),
                ["mappings"] = mappings, ["correspondences"] = correspondences, ["allowDifferentProjectCopy"] = false };
            string recordRef = (string)JsonSerializer.SerializeToNode(store.Review(Upload(request)), BridgeProtocol.Json)!["recordRef"]!;
            return (JsonObject)store.RebindPlan(recordRef, available);
        }
        JsonArray AmlRoute() => new(Mapping("before", before, beforeReview, module), Mapping("after", after, afterReview, replacement));
        JsonArray Swap() => new(new JsonObject { ["id"] = "module-swap", ["kind"] = "replacement", ["reason"] = "ECAD module replaced", ["before"] = Aml("before", before), ["after"] = Aml("after", after) });
        var plan = Plan(after, AmlRoute(), Swap());
        var actor = plan["entries"]!.AsArray().Single(e => (string?)e!["role"] == "actor")!; var sensor = plan["entries"]!.AsArray().Single(e => (string?)e!["role"] == "sensor")!;
        Check((int)plan["brokenConnectionCount"]! == 2 && plan["entries"]!.AsArray().All(e => (string?)e!["state"] == "planned" && (string?)e!["afterState"] == "disconnected" && (string?)e!["change"]!["kind"] == "to_connect")
            && !(bool)plan["applied"]!, "An AML module replacement that disconnects both TO slots plans one to_connect per slot without applying");
        Check(JsonNode.DeepEquals(actor["change"]!["payload"], new JsonObject { ["technologyPath"] = axis, ["role"] = "actor", ["hardwarePath"] = replacement }),
            "Actor rebind retargets the exact TO slot to the declared replacement module");
        Check(JsonNode.DeepEquals(sensor["change"]!["payload"], new JsonObject { ["technologyPath"] = axis, ["role"] = "sensor", ["hardwarePath"] = replacement, ["sensorIndex"] = 0,
            ["channelType"] = "Digital", ["channelIoType"] = "Input", ["channelNumber"] = 3 }) && (bool)sensor["selectorCarriedOver"]!,
            "Sensor rebind keeps its index and carries the channel selector over to the replacement, flagged as carried over");
        Check(Plan(after, AmlRoute(), Swap(), false)["entries"]!.AsArray().All(e => (string?)e!["state"] == "changeKindUnavailable" && e!["change"] is null),
            "Without a to_connect change kind the plan names the retarget but emits no payload");
        var direct = Plan(after, new JsonArray(), new JsonArray(new JsonObject { ["id"] = "model-swap", ["kind"] = "replacement", ["reason"] = "Saved hardware replaced",
            ["before"] = Model("before", beforeReview, module), ["after"] = Model("after", afterReview, replacement) }));
        Check(direct["entries"]!.AsArray().All(e => (string?)e!["state"] == "planned" && (string?)e!["correspondenceIds"]![0] == "model-swap"),
            "A direct saved-model correspondence is an equivalent replacement declaration");
        Check(Plan(after, AmlRoute(), new JsonArray())["entries"]!.AsArray().All(e => (string?)e!["state"] == "noDeclaredReplacement" && e!["change"] is null),
            "A broken connection without a declared replacement is never guessed");
        var rebound = Capture(Answer(replacement, "m2", replacement), afterReview);
        Check((int)Plan(rebound, new JsonArray(), new JsonArray())["brokenConnectionCount"]! == 0, "An after phase whose TO already resolves its target needs no rebind");

        Console.WriteLine($"PASS composite engineering capture and rebind plan: {checks.Count} checks; fake worker responses only.");
    }
}
