using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.VersionControl;

internal static class PackageChecks
{
    public static async Task<int> Run(string root, string git, string first, string second)
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS package " + name); }
        var store = new VciWorkspaceStore(root, git); var uploads = new ArtifactUploadStore(root);
        JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
        string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
        string Upload(byte[] bytes)
        {
            var state = uploads.Begin("mapping.json", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            for (int offset = 0; offset < bytes.Length; offset += state.MaxChunkBytes)
                uploads.Write(state.UploadId, offset, Convert.ToBase64String(bytes, offset, Math.Min(state.MaxChunkBytes, bytes.Length - offset)));
            return uploads.Commit(state.UploadId).ArtifactRef!;
        }
        void Reject(string code, Action action)
        {
            try { action(); } catch (BridgeException error) { Check(error.Code == code, "rejects " + code + ": " + error.Code); return; }
            throw new Exception("Expected " + code);
        }
        var snapshot = new EngineeringSnapshotIdentity("authored-mapping-fixture", new string('a', 64));
        var identity = new EngineeringObjectReference(new("D:/Fixture/P.ap21", "/PLC", null, "/PLC/Motor", "block"), snapshot);
        var profile = new VciProfileInput("authored-document", "nativeDocument", "block", "AuthoredFixtureFormat", new string('b', 64),
            "authoredFixture", [new("PLC/Über Motor.s7dcl", "document")]);
        var mapping = new VciMappingEvidence("vci-mapping-evidence.v1", "workspace-fixture", snapshot, true,
            [new("motor", identity, profile, "Equal")]);
        byte[] Bytes(object value) => JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json);
        JsonElement Capture(string reference, VciMappingEvidence evidence) => Json(store.CapturePackages(reference, Upload(Bytes(evidence))));
        string baselineUpload = Upload(Bytes(mapping));
        var baseline = Json(store.CapturePackages(first, baselineUpload)); var project = Capture(second, mapping);
        string baselineRef = Text(baseline, "recordRef"), projectRef = Text(project, "recordRef");
        Check(baseline.GetProperty("packageCount").GetInt32() == 1 && baseline.GetProperty("unmanagedFileCount").GetInt32() == 1 &&
            !baseline.GetProperty("targetProbed").GetBoolean(), "retains one explicit mapping, separately counts unmanaged file and never probes native state");
        uploads.Release(baselineUpload["upload:".Length..]);
        store = new(root, git);
        Check(Text(Json(store.Reopen(baselineRef)), "manifestSha256") == Text(baseline, "manifestSha256"), "retained metadata survives upload release and store restart");
        JsonElement Compare(JsonElement b, JsonElement p, JsonElement w, VciExpectedHashes? expected = null) => Json(store.ComparePackages(
            Text(b, "recordRef"), Text(p, "recordRef"), Text(w, "recordRef"), expected ?? new(Text(b, "manifestSha256"), Text(p, "manifestSha256"), Text(w, "manifestSha256"))));
        var oneSide = Compare(baseline, project, baseline);
        Check(oneSide.GetProperty("stateCounts").GetProperty("projectOnly").GetInt32() == 1 && !oneSide.GetProperty("canSynchronize").GetBoolean(), "one-sided change stays review-only");
        var preview = Json(await store.PrepareAsync([new("PLC/Über Motor.s7dcl", Upload(Encoding.UTF8.GetBytes("third")))],
            new("Fixture", "fixture@example.invalid"), "third side", first));
        var workspace = Capture(Text(preview, "recordRef"), mapping);
        var conflict = Compare(baseline, project, workspace);
        Check(conflict.GetProperty("stateCounts").GetProperty("divergentChange").GetInt32() == 1 && conflict.GetProperty("conflictCount").GetInt32() == 1,
            "raw divergent bytes conflict despite three Equal native-status labels");
        var same = Compare(baseline, project, project);
        Check(same.GetProperty("conflictCount").GetInt32() == 0 && !same.GetProperty("resolutionApplied").GetBoolean(), "identical two-sided changes do not apply a resolution");
        var incomplete = Capture(first, mapping with { EnumerationComplete = false });
        Check(Compare(baseline, incomplete, workspace).GetProperty("stateCounts").GetProperty("unknown").GetInt32() == 1, "incomplete enumeration never proves deletion");
        var missing = Capture(first, mapping with { Packages = [new("motor", identity, profile with { Family = "plcSimaticSd", ExpectedFiles =
            [new("PLC/Über Motor.s7dcl", "source"), new("PLC/Über Motor.s7res", "resources")] })] });
        Check(missing.GetProperty("incompletePackageCount").GetInt32() == 1 &&
            Compare(baseline, missing, workspace).GetProperty("stateCounts").GetProperty("incompletePackage").GetInt32() == 1, "missing companions remain explicit incomplete packages");
        var stale = Compare(baseline, project, workspace, new(new string('f', 64), Text(project, "manifestSha256"), Text(workspace, "manifestSha256")));
        Check(stale.GetProperty("stateCounts").GetProperty("stale").GetInt32() == 1, "wrong reviewed manifest hash produces a stale conflict");
        var foreign = Capture(first, mapping with { WorkspaceId = "other-workspace" });
        Reject("VciWorkspaceMismatch", () => Compare(baseline, project, foreign));
        var collision = Capture(first, mapping with { Packages = [mapping.Packages[0], mapping.Packages[0] with { PackageId = "duplicate-owner" }] });
        Check(Compare(baseline, collision, workspace).GetProperty("stateCounts").GetProperty("ownershipConflict").GetInt32() == 2, "multiply owned paths remain conflicts");
        var wrongObject = Capture(first, mapping with { Packages = [mapping.Packages[0] with { Object = identity with { Object = identity.Object with { ObjectPath = "/Other" } } }] });
        Check(Compare(baseline, wrongObject, workspace).GetProperty("stateCounts").GetProperty("identityCollision").GetInt32() == 1, "same package name cannot replace exact object identity");
        var extra = JsonNode.Parse(Bytes(mapping))!.AsObject(); extra["nativeQualified"] = true;
        Reject("VciInvalidMapping", () => store.CapturePackages(first, Upload(Bytes(extra))));
        var implicitComplete = JsonNode.Parse(Bytes(mapping))!.AsObject();
        implicitComplete["packages"]![0]!["profile"]!.AsObject().Remove("complete");
        Reject("VciInvalidMapping", () => store.CapturePackages(first, Upload(Bytes(implicitComplete))));
        var implicitVersion = JsonNode.Parse(Bytes(mapping))!.AsObject();
        implicitVersion["snapshot"]!.AsObject().Remove("tiaVersion");
        Reject("VciInvalidMapping", () => store.CapturePackages(first, Upload(Bytes(implicitVersion))));
        extra.Remove("nativeQualified"); extra.Remove("enumerationComplete");
        Reject("VciInvalidMapping", () => store.CapturePackages(first, Upload(Bytes(extra))));
        var duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Bytes(mapping)).Replace("\"schemaVersion\":", "\"workspaceId\":\"duplicate\",\"schemaVersion\":"));
        Reject("VciInvalidMapping", () => store.CapturePackages(first, Upload(duplicate)));
        Reject("VciInputLimit", () => store.CapturePackages(first, Upload(new byte[(1 << 20) + 1])));
        Reject("VciInvalidReference", () => store.CapturePackages(baselineRef, Upload(Bytes(mapping))));
        Reject("VciLimitExceeded", () => Capture(first, mapping with { Packages = Enumerable.Repeat<VciPackageInput>(null!, 1001).ToArray() }));
        string directory = Path.Combine(root, "vci-workspaces", "records", baselineRef.Split(':')[2]);
        string mappingPath = Path.Combine(directory, "mapping.json"); var saved = File.ReadAllBytes(mappingPath);
        File.AppendAllText(mappingPath, " ");
        Reject("VciIntegrityFailure", () => store.Reopen(Text(conflict, "recordRef")));
        File.WriteAllBytes(mappingPath, saved);
        Check(Text(Json(store.Reopen(Text(conflict, "recordRef"))), "reportSha256") == Text(conflict, "reportSha256"), "comparison revalidates complete input chain after exact restoration");
        string reportDirectory = Path.Combine(root, "vci-workspaces", "records", Text(conflict, "recordRef").Split(':')[2]);
        File.AppendAllText(Path.Combine(reportDirectory, "comparison.json"), " ");
        Reject("VciIntegrityFailure", () => store.ReadArtifact(Text(conflict, "recordRef"), "comparison.json"));
        Check(Json(store.List(count:50)).GetProperty("rows").EnumerateArray().Any(r => r.GetProperty("state").GetString() == "integrity-failed" &&
            r.GetProperty("id").GetString() == Text(conflict, "recordRef").Split(':')[2]), "discovery exposes damaged package publication without replay");
        return checks;
    }
}
