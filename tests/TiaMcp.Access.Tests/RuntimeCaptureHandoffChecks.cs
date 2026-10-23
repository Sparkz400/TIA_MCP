using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Core.Security;

internal static class RuntimeCaptureHandoffChecks
{
    internal static void Run(string root)
    {
        var checks = new List<string>();
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
        void Reject(Action action, string label)
        { try { action(); } catch (BridgeException) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
        var uploads = new ArtifactUploadStore(root); var handoffs = new RuntimeHandoffWorkflowStore(root);
        JsonObject Json(object value) => JsonSerializer.SerializeToNode(value, BridgeProtocol.Json)!.AsObject();
        string Upload(JsonObject value)
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json);
            var upload = uploads.Begin("fixture.json", data.Length, Convert.ToHexStringLower(SHA256.HashData(data)));
            for (int i = 0; i < data.Length; i += upload.MaxChunkBytes)
                uploads.Write(upload.UploadId, i, Convert.ToBase64String(data, i, Math.Min(upload.MaxChunkBytes, data.Length - i)));
            return uploads.Commit(upload.UploadId).ArtifactRef!;
        }
        var packet = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "handoff-workflow/request.json")))!.AsObject();
        var access = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "access-reviews", (string)packet["accessRecordId"]! + ".json")))!;
        var accessInput = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, access["input"]!.AsObject()))!.AsObject();
        foreach (JsonNode row in new[] { accessInput, accessInput["before"]!, accessInput["after"]! }) row["identity"]!["securityMode"] = "local";
        packet["accessRecordId"] = new AccessWorkflowStore(root).Review((string)packet["reviewId"]!, Upload(accessInput))["recordId"]!.DeepClone();
        packet["expected"]!["securityMode"] = "local";
        foreach (string phase in new[] { "baseline", "current" })
        {
            var session = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, packet[phase]!["session"]!.AsObject()))!.AsObject();
            session["securityMode"] = "local";
            packet[phase]!["session"] = Json(handoffs.RetainObservation("session", Upload(session)))["artifact"]!.DeepClone();
        }
        string reference = (string)Json(handoffs.Review(Upload(packet)))["recordRef"]!;
        var expected = packet["expected"]!;
        var context = expected["context"]!.Deserialize<WebRuntimeContext>(BridgeProtocol.Json)!;
        var observed = new RuntimeCaptureSession((string)expected["hostId"]!, context, (string)expected["subjectId"]!, "local",
            expected["credential"]!.Deserialize<CredentialReference>(BridgeProtocol.Json)!, true);
        string selection = new('a',32);
        var metadata = JsonSerializer.SerializeToElement(new { name = "Value", datatype = "bool" });
        RuntimeCaptureSignal[] signals = [new(selection, WebRuntimeProtocol.Symbol(context, "\"DB\".Value", "simple", metadata), metadata)];
        var request = new RuntimeCaptureRequest(context.ConnectionId, [selection], 100, 1, ReviewId: (string)packet["reviewId"]!, HandoffRef: reference);
        var binding = RuntimeCaptureHandoffs.Resolve(root, request, observed, signals)!;
        Check(binding.Session == observed && binding.RecordRef == reference, "Exact independent session owner and handoff retained");
        RuntimeCaptureHandoffs.ValidateRead(root, binding, observed); Check(true, "Original evidence and matching live context validate");
        Check(RuntimeCaptureHandoffs.Resolve(root, request with { HandoffRef = null }, null!, signals) is null, "Legacy capture has no invented session/handoff fields");
        foreach (var changed in new[] {
            observed with { HostId = new('b',32) }, observed with { SubjectId = "other" }, observed with { SecurityMode = "umc" },
            observed with { Credential = observed.Credential with { CredentialId = new('b',32) } }, observed with { PeerVerified = false },
            observed with { Context = context with { Epoch = new('b',32) } }, observed with { Context = context with { ConnectionId = new('b',32) } },
            observed with { Context = context with { PeerCertificateSha256 = new('b',64) } }, observed with { Context = context with { Endpoint = "https://other.example.test" } } })
        {
            Reject(() => RuntimeCaptureHandoffs.Resolve(root, request, changed, signals), "Changed start context refuses: " + checks.Count);
            Reject(() => RuntimeCaptureHandoffs.ValidateRead(root, binding, changed), "Changed read context refuses: " + checks.Count);
        }
        Reject(() => RuntimeCaptureHandoffs.Resolve(root, request with { ReviewId = new('b',64) }, observed, signals), "Different saved source refuses");
        Reject(() => RuntimeCaptureHandoffs.Resolve(root, request with { ReviewId = null }, observed, signals), "Handoff requires explicit saved review");
        Reject(() => RuntimeCaptureHandoffs.Resolve(root, request with { HandoffRef = reference.ToUpperInvariant() }, observed, signals), "Noncanonical handoff handle refuses");
        Reject(() => RuntimeCaptureHandoffs.Resolve(root, request, observed with { SubjectId = "bad\ud800" }, signals), "Malformed session Unicode refuses before serialization");
        using var captures = new RuntimeCaptureStore(root);
        var plan = new RuntimeCapturePlan(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, request, signals, Handoff: binding);
        foreach (var forged in new[] { binding with { DeviceIdentityVerified = true }, binding with { ProgramMatchVerified = true },
            binding with { EffectiveRightsVerified = true }, binding with { SourceAuthenticityVerified = true },
            binding with { OperationAdmitted = true }, binding with { AssertionsAdmitted = true }, binding with { SchemaVersion = "native" } })
            Reject(() => captures.Create(plan with { Handoff = forged }), "Forged retained authority/schema refuses before publication: " + checks.Count);
        Check(!captures.Exists(plan.CaptureId), "Refused binding creates no capture directory");
        captures.Create(plan).Finish("stopped", 0);
        string snapshot = captures.Head(plan.CaptureId);
        var status = Json(captures.Status(plan.CaptureId, false));
        Check(status["plan"]!["hostId"]!.GetValue<string>() != binding.Session.HostId, "Capture owner remains distinct from session owner");
        Check(JsonNode.DeepEquals(status["plan"]!["handoff"], Json(binding)), "Exact versioned handoff survives capture storage");
        foreach (string code in new[] { "RuntimeCaptureHandoffMismatch", "RuntimeCaptureHandoffUnavailable" })
        {
            var errorPlan=plan with { CaptureId=Guid.NewGuid().ToString("N") };
            var writer=captures.Create(errorPlan);
            writer.Append(new(0,"read",selection,0,0,1,DateTimeOffset.UtcNow,null,code,0));writer.Finish("sessionUnavailable",2);
            var page=Json(captures.Page(writer.Reference,0,10));
            Check((string?)page["rows"]![0]!["errorCode"]==code,"Original handoff refusal survives capture publication: "+code);
        }
        string source = Path.Combine(root, "runtime-handoff/records", reference.Split(':')[1], "request.json");
        byte[] original = File.ReadAllBytes(source); File.AppendAllText(source, " ");
        try
        {
            Reject(() => RuntimeCaptureHandoffs.ValidateRead(root, binding, observed), "Changed original stops further bound reads");
            Reject(() => RuntimeCaptureHandoffs.ValidateEvidence(root, binding), "Changed original refuses new analysis");
            Check(JsonNode.DeepEquals(status, Json(captures.Status(plan.CaptureId, false))), "Historical capture remains readable after dependency changes");
            Check(Json(captures.Export(snapshot, "json", false))["artifacts"]!.AsArray().Count == 1, "Historical capture remains exportable without dependency adoption");
        }
        finally { File.WriteAllBytes(source, original); }
        var changedProgram = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, packet["current"]!["program"]!.AsObject()))!.AsObject();
        changedProgram["identity"]!["value"] = "different-program"; changedProgram["sequence"] = 2;
        packet["current"]!["program"] = Json(handoffs.RetainObservation("program", Upload(changedProgram)))["artifact"]!.DeepClone();
        var invalid = Json(handoffs.Review(Upload(packet)));
        Reject(() => RuntimeCaptureHandoffs.Resolve(root, request with { HandoffRef = (string)invalid["recordRef"]! }, observed, signals), "Requires-fresh-evidence declaration cannot bind a new capture");
        string evidence = Path.Combine(root, "capture-handoff"); Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, "verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, checks, recordRef = reference, snapshotRef = snapshot }, BridgeProtocol.Json));
        Console.WriteLine($"PASS capture handoff: {checks.Count} checks; original evidence and owned context, no native authority.");
    }
}
