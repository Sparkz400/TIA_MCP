using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

internal static class RuntimeHandoffChecks
{
    internal static void Run(string root, string reviewId, string accessId, JsonObject access, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "handoff"); Directory.CreateDirectory(directory);
        var cases = new JsonArray();
        JsonObject Identity(string value) => new() { ["scheme"] = "synthetic.fixture", ["value"] = value };
        var context = JsonSerializer.SerializeToNode(WebRuntimeProtocol.Context("https://cpu.example.test", new string('1',32), new string('2',32), new string('3',64)), BridgeProtocol.Json)!;
        var credential = new JsonObject { ["schemaVersion"] = "protected-credential-reference.v1", ["vaultId"] = new string('4',32),
            ["credentialId"] = new string('5',32), ["bindingSha256"] = new string('6',64), ["cipherSha256"] = new string('7',64) };
        var expected = new JsonObject { ["hostId"] = new string('8',32), ["context"] = context.DeepClone(),
            ["targetIdentity"] = Identity("cpu:original"), ["programIdentity"] = Identity("program:original"),
            ["subjectId"] = access["subjectId"]!.DeepClone(), ["clientId"] = "fixture-client", ["credential"] = credential,
            ["securityDomain"] = "cpuRuntime", ["securityMode"] = "supplied-mode" };
        JsonObject Row(string role)
        {
            var row = new JsonObject { ["schemaVersion"] = "runtime-handoff-" + role + ".v1", ["sourceKind"] = "synthetic",
                ["hostId"] = expected["hostId"]!.DeepClone(), ["context"] = context.DeepClone(), ["sequence"] = 1L, ["freshness"] = "current" };
            if (role != "session") row["identity"] = expected[role + "Identity"]!.DeepClone();
            else
            {
                foreach (string key in new[] { "subjectId", "clientId", "credential", "securityDomain", "securityMode" }) row[key] = expected[key]!.DeepClone();
                row["peerVerified"] = true; row["authenticated"] = true; row["state"] = "connected"; row["permissionsComplete"] = true;
                row["permissions"] = new JsonArray(new JsonObject { ["id"] = "ReadTag", ["scope"] = "cpu:A", ["outcome"] = "allowed" },
                    new JsonObject { ["id"] = "ModifyTag", ["scope"] = "cpu:A", ["outcome"] = "denied" });
            }
            return row;
        }
        JsonObject Retain(JsonObject row)
        {
            byte[] data = Encoding.UTF8.GetBytes(row.ToJsonString() + "\n");
            string hash = Convert.ToHexStringLower(SHA256.HashData(data));
            File.WriteAllBytes(Path.Combine(directory, hash + ".input"), data);
            return new() { ["path"] = "handoff/" + hash + ".input", ["sha256"] = hash };
        }
        var originals = new JsonObject { ["target"] = Row("target"), ["program"] = Row("program"), ["session"] = Row("session") };
        JsonObject References(JsonObject rows) => new(rows.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Retain(p.Value!.AsObject()))));
        var request = new JsonObject { ["schemaVersion"] = "runtime-handoff-request.v1", ["reviewId"] = reviewId, ["accessRecordId"] = accessId,
            ["accessPhase"] = "after", ["owner"] = access["owner"]!.DeepClone(), ["expected"] = expected,
            ["operations"] = new JsonArray(new JsonObject { ["id"] = "ReadTag", ["scope"] = "cpu:A", ["requiredOutcome"] = "allowed" },
                new JsonObject { ["id"] = "ModifyTag", ["scope"] = "cpu:A", ["requiredOutcome"] = "denied" }),
            ["baseline"] = References(originals), ["current"] = References(originals) };
        JsonObject Build(JsonObject input) => RuntimeHandoffReview.Build(root, JsonSerializer.SerializeToUtf8Bytes(input, BridgeProtocol.Json));
        void Gates(JsonObject report)
        {
            foreach (string key in new[] { "nativeExecutable", "nativeQualified", "sourceAuthenticityVerified", "liveFreshnessVerified",
                "deviceIdentityVerified", "programMatchVerified", "effectiveRightsVerified", "operationAdmitted", "assertionsAdmitted" })
                if (report[key]!.GetValue<bool>()) throw new Exception("Promoted authority: " + key);
            if (report["permissions"]!.AsArray().Any(p => p!["operationAdmitted"]!.GetValue<bool>())) throw new Exception("Admitted operation");
        }
        void Case(string label, Action<JsonObject>? change, bool invalid, Action<JsonObject>? requestChange = null)
        {
            var rows = originals.DeepClone().AsObject(); change?.Invoke(rows);
            var input = request.DeepClone().AsObject(); input["current"] = References(rows); requestChange?.Invoke(input);
            var report = Build(input); Gates(report);
            check(report["summary"]!["requiresFreshEvidence"]!.GetValue<bool>() == invalid, "Handoff: " + label);
            cases.Add(new JsonObject { ["label"] = label, ["request"] = input, ["report"] = report, ["expectedInvalid"] = invalid });
        }
        Case("consistent independent roles remain declarations", null, false);
        Case("same address wrong CPU", p => p["target"]!["identity"] = Identity("cpu:other"), true);
        Case("same CPU changed program", p => p["program"]!["identity"] = Identity("program:other"), true);
        Case("unknown program cannot be filled from symbol metadata", p => p["program"]!["identity"] = null, true);
        Case("unknown expected program remains unknown", null, true, p => p["expected"]!["programIdentity"] = null);
        Case("unknown CPU remains unknown", p => p["target"]!["identity"] = null, true);
        foreach (string role in new[] { "target", "program", "session" })
        {
            Case(role + " different endpoint with equal certificate", p => p[role]!["context"]!["endpoint"] = "https://other.example.test", true);
            Case(role + " certificate rotation", p => p[role]!["context"]!["peerCertificateSha256"] = new string('9',64), true);
            Case(role + " reconnect", p => p[role]!["context"]!["epoch"] = new string('9',32), true);
            Case(role + " different connection", p => p[role]!["context"]!["connectionId"] = new string('9',32), true);
            Case(role + " different Host", p => p[role]!["hostId"] = new string('9',32), true);
            Case(role + " expired", p => p[role]!["freshness"] = "expired", true);
            Case(role + " unknown freshness", p => p[role]!["freshness"] = "unknown", true);
            Case(role + " regressed sequence", p => p[role]!["sequence"] = 0L, true);
            Case(role + " advanced sequence is still a declaration", p => p[role]!["sequence"] = long.MaxValue, false);
        }
        foreach (string key in new[] { "subjectId", "clientId", "securityDomain", "securityMode" })
            Case("changed " + key, p => p["session"]![key] = "different", true);
        Case("rotated credential", p => p["session"]!["credential"]!["cipherSha256"] = new string('9',64), true);
        Case("missing credential", p => p["session"]!["credential"] = null, true);
        foreach (string key in new[] { "peerVerified", "authenticated", "permissionsComplete" })
            Case("unknown " + key, p => p["session"]![key] = false, true);
        foreach (string state in new[] { "closed", "expired", "unknown" }) Case("session " + state, p => p["session"]!["state"] = state, true);
        Case("denied required read", p => p["session"]!["permissions"]![0]!["outcome"] = "denied", true);
        Case("unexpected permitted write", p => p["session"]!["permissions"]![1]!["outcome"] = "allowed", true);
        Case("unknown read outcome", p => p["session"]!["permissions"]![0]!["outcome"] = "unknown", true);
        Case("same permission name wrong scope", p => p["session"]!["permissions"]![0]!["scope"] = "cpu:B", true);
        Case("missing denied-operation evidence", p => p["session"]!["permissions"]!.AsArray().RemoveAt(1), true);
        Case("new unrelated permission invalidates previous observation", p => p["session"]!["permissions"]!.AsArray().Add(
            new JsonObject { ["id"] = "Download", ["scope"] = "cpu:A", ["outcome"] = "allowed" }), true);
        Case("successful read does not admit mode change", null, true, p => p["operations"]!.AsArray().Add(
            new JsonObject { ["id"] = "ChangeMode", ["scope"] = "cpu:A", ["requiredOutcome"] = "allowed" }));
        Case("baseline failure cannot be hidden by current agreement", null, true, p => {
            var old = originals.DeepClone().AsObject(); old["program"]!["identity"] = Identity("program:other"); p["baseline"] = References(old);
        });
        Case("all supplied observations still grant no authority", null, false, p => {
            var rows = originals.DeepClone().AsObject(); foreach (var row in rows) row.Value!["sourceKind"] = "supplied";
            p["baseline"] = References(rows); p["current"] = References(rows);
        });
        Case("order-independent permissions with fresh sequence", p => {
            var rows = p["session"]!["permissions"]!.AsArray(); var first = rows[0]!.DeepClone(); rows.RemoveAt(0); rows.Add(first); p["session"]!["sequence"] = 2L;
        }, false);
        void Reject(string label, Action<JsonObject> change, Action<JsonObject>? observationChange = null, string code = "InvalidRuntimeHandoff")
        {
            var input = request.DeepClone().AsObject(); change(input);
            if (observationChange is not null) { var rows = originals.DeepClone().AsObject(); observationChange(rows); input["current"] = References(rows); }
            try { Build(input); } catch (BridgeException e) when (e.Code == code) { check(!e.Message.Contains("SECRET-SENTINEL"), "Handoff refuses " + label); return; }
            throw new Exception("Expected handoff refusal: " + label);
        }
        Reject("foreign saved review", p => p["reviewId"] = new string('0',64));
        Reject("different access owner", p => p["owner"]!["nodeId"] = "another");
        Reject("unexpected request field", p => p["password"] = "SECRET-SENTINEL");
        Reject("extra credential field", p => p["expected"]!["credential"]!["password"] = "SECRET-SENTINEL");
        Reject("duplicate requested operation", p => p["operations"]!.AsArray().Add(p["operations"]![0]!.DeepClone()));
        Reject("empty requested operations", p => p["operations"] = new JsonArray());
        Reject("cross-domain public grants", p => p["expected"]!["securityDomain"] = "projectUmac");
        Reject("wrong original role", p => p["current"]!["program"] = p["current"]!["target"]!.DeepClone());
        Reject("native producer assertion", _ => { }, p => p["target"]!["sourceKind"] = "nativeQualified");
        Reject("negative sequence", _ => { }, p => p["target"]!["sequence"] = -1L);
        Reject("duplicate permission", _ => { }, p => p["session"]!["permissions"]!.AsArray().Add(p["session"]!["permissions"]![0]!.DeepClone()));
        Reject("secret in session", _ => { }, p => p["session"]!["password"] = "SECRET-SENTINEL");
        Reject("invented identity fields", _ => { }, p => p["program"]!["identity"]!["verified"] = true);
        Reject("malformed permission state", _ => { }, p => p["session"]!["permissions"]![0]!["outcome"] = "true");
        Reject("permission capacity", _ => { }, p => p["session"]!["permissions"] = new JsonArray(Enumerable.Range(0,257).Select(i =>
            (JsonNode?)new JsonObject { ["id"] = "p" + i, ["scope"] = "cpu:A", ["outcome"] = "allowed" }).ToArray()));
        var targetRef = request["current"]!["target"]!; string targetPath = Path.Combine(root, targetRef["path"]!.GetValue<string>());
        byte[] original = File.ReadAllBytes(targetPath); File.WriteAllText(targetPath, "tampered");
        try { Reject("corrupt original bytes", _ => { }, code: "ArtifactChanged"); } finally { File.WriteAllBytes(targetPath, original); }
        string accessPath = Path.Combine(root, "access-reviews", accessId + ".json"); byte[] accessBytes = File.ReadAllBytes(accessPath);
        File.WriteAllText(accessPath, "tampered");
        try { Reject("corrupt access dependency", _ => { }, code: "ArtifactChanged"); } finally { File.WriteAllBytes(accessPath, accessBytes); }
        byte[] duplicate = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"schemaVersion\":2}");
        foreach (byte[] input in new[] { duplicate, new byte[RuntimeHandoffReview.InputLimit + 1] })
        {
            try { RuntimeHandoffReview.Build(root, input); throw new Exception("Bad handoff input accepted"); }
            catch (BridgeException e) when (e.Code == "InvalidRuntimeHandoff") { check(true, "Handoff duplicate field or byte bound refused"); }
        }
        // An owned PLCSIM binding replaces the supplied target/program claims; its absence is stated, legacy requests keep their bytes.
        var bound = new PlcsimTargetBinding("plcsim-target-binding.v1", 3, "Sim1", new string('a',32), @"C:\sim\Sim1", new string('b',64),
            ["192.168.0.10"], new string('c',64), DateTimeOffset.UnixEpoch);
        var bindingRef = PlcsimTargetBindings.Retain(root, bound);
        JsonNode Endpoint(string endpoint) => JsonSerializer.SerializeToNode(WebRuntimeProtocol.Context(endpoint, new string('1',32), new string('2',32), new string('3',64)), BridgeProtocol.Json)!;
        JsonObject Bound(Action<JsonObject>? rowChange = null, Action<JsonObject>? requestChange = null, string endpoint = "https://192.168.0.10")
        {
            var rows = originals.DeepClone().AsObject();
            foreach (var row in rows) row.Value!["context"] = Endpoint(endpoint);
            rows["target"]!["identity"] = PlcsimTargetBindings.TargetIdentity(bound); rows["program"]!["identity"] = PlcsimTargetBindings.ProgramIdentity(bound);
            rowChange?.Invoke(rows);
            var input = request.DeepClone().AsObject(); input["expected"]!["context"] = Endpoint(endpoint);
            input["expected"]!["targetIdentity"] = PlcsimTargetBindings.TargetIdentity(bound); input["expected"]!["programIdentity"] = PlcsimTargetBindings.ProgramIdentity(bound);
            input["baseline"] = References(rows); input["current"] = References(rows); input["plcsimBinding"] = bindingRef.DeepClone();
            requestChange?.Invoke(input);
            var report = Build(input); Gates(report); return report;
        }
        bool Invalid(JsonObject report) => report["summary"]!["requiresFreshEvidence"]!.GetValue<bool>();
        var matched = Bound();
        check(!Invalid(matched) && (string?)matched["targetBinding"]!["state"] == "boundOwnedPlcsim" &&
            (string?)matched["targetBinding"]!["programIdentitySource"] == "boundOnlineFingerprint", "Handoff: bound owned PLCSIM identity matches");
        check(Invalid(Bound(rows => rows["target"]!["identity"] = Identity("cpu:original"), p => p["expected"]!["targetIdentity"] = Identity("cpu:original"))),
            "Handoff: a supplied target claim cannot replace the bound identity");
        check(Invalid(Bound(rows => rows["program"]!["identity"] = Identity("program:original"))), "Handoff: program must match the bound fingerprint");
        check(Invalid(Bound(endpoint: "https://192.168.0.11")), "Handoff: endpoint outside the binding stays unbound");
        check((string?)Bound(requestChange: p => p["plcsimBinding"] = null)["targetBinding"]!["state"] == "unbound", "Handoff: explicit unbound statement");
        check(Build(request)["targetBinding"] is null, "Handoff: requests without plcsimBinding keep their original report shape");
        Reject("binding outside the binding store", p => p["plcsimBinding"] = new JsonObject { ["path"] = targetRef["path"]!.DeepClone(), ["sha256"] = targetRef["sha256"]!.DeepClone() });
        File.WriteAllText(Path.Combine(directory, "request.json"), request.ToJsonString(BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(directory, "report.json"), Build(request).ToJsonString(BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(directory, "cases.json"), cases.ToJsonString(BridgeProtocol.Json));
    }
}
