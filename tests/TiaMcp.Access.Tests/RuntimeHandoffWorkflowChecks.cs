using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

internal static class RuntimeHandoffWorkflowChecks
{
    internal static void Run(string root)
    {
        var checks = new List<string>(); var processes = new List<object>();
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
        void Reject(Action run, string label, string code)
        { try { run(); } catch (BridgeException e) when (e.Code == code) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
        string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
        JsonObject Json(object value) => JsonSerializer.SerializeToNode(value, BridgeProtocol.Json)!.AsObject();
        var uploads = new ArtifactUploadStore(root); var store = new RuntimeHandoffWorkflowStore(root);
        (string Ref, string Id) Upload(byte[] bytes)
        {
            var u = uploads.Begin("handoff.json", bytes.Length, Hash(bytes));
            for (int i = 0; i < bytes.Length; i += u.MaxChunkBytes) uploads.Write(u.UploadId, i, Convert.ToBase64String(bytes, i, Math.Min(u.MaxChunkBytes, bytes.Length - i)));
            return (uploads.Commit(u.UploadId).ArtifactRef!, u.UploadId);
        }
        var request = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "handoff/request.json")))!.AsObject();
        var sourceBytes = new Dictionary<string, byte[]>();
        foreach (string phase in new[] { "baseline", "current" }) foreach (string role in new[] { "target", "program", "session" })
        {
            byte[] bytes = ProjectReviewStore.ReadArtifact(root, request[phase]![role]!.AsObject()); sourceBytes[phase + "." + role + ".json"] = bytes;
            var upload = Upload(bytes); var receipt = Json(store.RetainObservation(role, upload.Ref));
            Check(JsonNode.DeepEquals(receipt, Json(store.RetainObservation(role, upload.Ref))), "Exact observation intake is idempotent: " + phase + "." + role);
            Reject(() => store.RetainObservation(role == "target" ? "program" : "target", upload.Ref), "Observation role cannot be relabeled", "InvalidRuntimeHandoff");
            request[phase]![role] = receipt["artifact"]!.DeepClone(); uploads.Release(upload.Id);
            Check(ProjectReviewStore.ReadArtifact(root, request[phase]![role]!.AsObject()).AsSpan().SequenceEqual(bytes), "Original observation survives upload release");
        }
        byte[] requestBytes = Encoding.UTF8.GetBytes(request.ToJsonString(BridgeProtocol.Json) + "\n");
        var incoming = Upload(requestBytes); var summary = Json(store.Review(incoming.Ref)); string reference = (string)summary["recordRef"]!;
        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(RuntimeHandoffReview.Build(root, requestBytes), BridgeProtocol.Json);
        string storeRoot = Path.Combine(root, "runtime-handoff"), directory = Path.Combine(storeRoot, "records", reference.Split(':')[1]);
        byte[] Download(string name, string? selected = null)
        {
            using var output = new MemoryStream(); bool done = false;
            while (!done)
            {
                var part = Json(store.ReadArtifact(selected ?? reference, name, output.Length, 8192)); byte[] bytes = Convert.FromBase64String((string)part["data"]!);
                Check((long)part["offset"]! == output.Length && (int)part["count"]! == bytes.Length, "Exact download offset and length: " + name);
                output.Write(bytes); done = (bool)part["complete"]!;
                Check((long)part["nextOffset"]! == output.Length, "Exact next offset: " + name);
                if (done) Check((long)part["bytes"]! == output.Length && (string)part["sha256"]! == Hash(output.ToArray()), "Exact full artifact digest: " + name);
            }
            return output.ToArray();
        }
        Check(Download("request.json").AsSpan().SequenceEqual(requestBytes), "Caller request bytes retained without rewriting");
        Check(Download("report.json").AsSpan().SequenceEqual(expected), "Stored report equals independent original-input evaluation");
        foreach (var pair in sourceBytes) Check(Download(pair.Key).AsSpan().SequenceEqual(pair.Value), "Exact original observation download: " + pair.Key);
        byte[] receiptBytes = Download("receipt.json"); Check(Hash(receiptBytes) == reference.Split(':')[2], "Exact receipt binds public handle");
        Check(JsonNode.DeepEquals(summary, Json(new RuntimeHandoffWorkflowStore(root).Reopen(reference))), "Fresh store reopens without acquisition");
        foreach (string section in new[] { "constraints", "permissions", "baselineObservations", "currentObservations" })
        {
            int offset = 0; var rows = new JsonArray(); JsonObject page;
            do
            {
                page = Json(store.Page(reference, section, offset, 2));
                foreach (var row in page["rows"]!.AsArray()) { Check((int)row!["index"]! == rows.Count, "Ordered complete page: " + section); rows.Add(row["value"]!.DeepClone()); }
                if (page["nextOffset"] is null) break; offset = (int)page["nextOffset"]!;
            } while (true);
            Check(rows.Count == (int)page["total"]!, "No lost or duplicated page rows: " + section);
        }
        foreach (string flag in new[] { "nativeExecutable", "nativeQualified", "sourceAuthenticityVerified", "liveFreshnessVerified", "operationAdmitted", "assertionsAdmitted" })
            Check(!(bool)summary[flag]!, "Retained review does not promote " + flag);
        Reject(() => store.Page(reference, "../request.json"), "Invalid page section", "HandoffInvalidSection");
        Reject(() => store.Page(reference, count: 201), "Page bound", "HandoffPageLimit");
        Reject(() => store.ReadArtifact(reference, "intent.json"), "No arbitrary internal artifact download", "HandoffArtifactMissing");
        Reject(() => store.ReadArtifact(reference, "request.json", requestBytes.Length + 1), "Download past end", "HandoffPageLimit");
        Reject(() => store.Reopen(reference.ToUpperInvariant()), "Noncanonical handle", "HandoffInvalidReference");
        Reject(() => store.List(count: 21), "List page bound", "HandoffPageLimit");
        foreach (string name in new[] { "intent.json", "receipt.json", "request.json", "report.json" })
        {
            string path = Path.Combine(directory, name); byte[] saved = File.ReadAllBytes(path); File.AppendAllText(path, " ");
            try { Reject(() => store.Reopen(reference), "Changed retained " + name, "HandoffIntegrityFailure"); }
            finally { File.WriteAllBytes(path, saved); }
        }
        var forgedReport = JsonNode.Parse(expected)!.AsObject(); forgedReport["operationAdmitted"] = true;
        byte[] forged = JsonSerializer.SerializeToUtf8Bytes(forgedReport, BridgeProtocol.Json); var changedReceipt = JsonNode.Parse(receiptBytes)!;
        changedReceipt["artifacts"]![1]!["bytes"] = forged.Length; changedReceipt["artifacts"]![1]!["sha256"] = Hash(forged);
        byte[] changed = JsonSerializer.SerializeToUtf8Bytes(changedReceipt, BridgeProtocol.Json);
        File.WriteAllBytes(Path.Combine(directory, "report.json"), forged); File.WriteAllBytes(Path.Combine(directory, "intent.json"), changed); File.WriteAllBytes(Path.Combine(directory, "receipt.json"), changed);
        try { Reject(() => store.Reopen(reference[..(reference.LastIndexOf(':') + 1)] + Hash(changed)), "Rehashed forgery cannot grant authority", "HandoffIntegrityFailure"); }
        finally { File.WriteAllBytes(Path.Combine(directory, "report.json"), expected); File.WriteAllBytes(Path.Combine(directory, "intent.json"), receiptBytes); File.WriteAllBytes(Path.Combine(directory, "receipt.json"), receiptBytes); }
        string extra = Path.Combine(directory, "unexpected.json"); File.WriteAllText(extra, "{}");
        try { Reject(() => store.Reopen(reference), "Unexpected record file", "HandoffIntegrityFailure"); } finally { File.Delete(extra); }
        var dependencyPaths = new[] { (string)request["baseline"]!["target"]!["path"]!, (string)request["baseline"]!["program"]!["path"]!,
            (string)request["baseline"]!["session"]!["path"]!, "access-reviews/" + (string)request["accessRecordId"]! + ".json",
            "project-reviews/" + (string)request["reviewId"]! + ".json" };
        foreach (string relative in dependencyPaths)
        {
            string path = Path.Combine(root, relative); byte[] original = File.ReadAllBytes(path); File.AppendAllText(path, " ");
            try
            {
                Reject(() => store.ReadArtifact(reference, "report.json"), "Every download checks original dependency: " + relative, "ArtifactChanged");
                Check(Json(store.List())["rows"]!.AsArray().Any(r => (string?)r!["state"] == "verificationFailed"), "Discovery exposes changed dependency");
            }
            finally { File.WriteAllBytes(path, original); }
        }
        string originalTarget = Path.Combine(root, dependencyPaths[0]); byte[] target = File.ReadAllBytes(originalTarget); string? incomplete = null;
        var disrupted = new RuntimeHandoffWorkflowStore(root, null, (phase, path) => { if (phase == "reportWritten") { incomplete = path; File.AppendAllText(originalTarget, " "); } });
        try { Reject(() => disrupted.Review(incoming.Ref), "Changed input before receipt prevents publication", "ArtifactChanged"); }
        finally { File.WriteAllBytes(originalTarget, target); }
        Check(incomplete is not null && !File.Exists(Path.Combine(incomplete, "receipt.json")), "Changed-input intent remains incomplete");
        foreach (string phase in new[] { "observationWritten", "observationPublished", "intentWritten", "requestWritten", "reportWritten", "receiptWritten" })
        {
            string signal = Path.Combine(root, "pause-handoff-" + phase + ".txt"); var childUpload = incoming;
            if (phase.StartsWith("observation", StringComparison.Ordinal))
            {
                var row = JsonNode.Parse(target)!; row["sequence"] = phase == "observationWritten" ? 100 : 101;
                childUpload = Upload(JsonSerializer.SerializeToUtf8Bytes(row, BridgeProtocol.Json));
            }
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { Assembly.GetExecutingAssembly().Location, "--handoff-publication-child", root, childUpload.Ref, phase, signal }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new Exception("Child failed to start");
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync(); string? reached = null; var watch = Stopwatch.StartNew();
            try
            {
                while (watch.Elapsed < TimeSpan.FromSeconds(30) && !child.HasExited)
                {
                    try { if (File.Exists(signal)) { string value = File.ReadAllText(signal); if (File.Exists(value) || Directory.Exists(value)) { reached = value; break; } } } catch (IOException) { }
                    Thread.Sleep(10);
                }
                Check(reached is not null && !child.HasExited, "Actual process reached flushed boundary: " + phase);
                Reject(() => store.Review(incoming.Ref), "Competing writer refused while actual publisher owns lock: " + phase, "IntakeBusy");
                Check(JsonNode.DeepEquals(summary, Json(store.Reopen(reference))), "Existing record remains readable during another publication");
                child.Kill(entireProcessTree: true); Check(child.WaitForExit(10000), "Owned publication child terminated: " + phase);
                bool published = phase is "observationPublished" or "receiptWritten";
                if (phase.StartsWith("observation", StringComparison.Ordinal))
                {
                    Check(File.Exists(reached) && reached!.EndsWith(published ? ".json" : ".tmp", StringComparison.Ordinal), "Observation commit boundary survives process exit");
                    if (!published) Check((int)Json(store.List())["incompleteObservationFiles"]! >= 1, "Pending observation remains visible and is not adopted");
                    var recovered = Json(store.RetainObservation("target", childUpload.Ref));
                    Check(recovered["artifact"] is JsonObject, "Exact observation retry after lost reply is recoverable"); uploads.Release(childUpload.Id);
                }
                else
                {
                    var row = Json(store.List())["rows"]!.AsArray().Single(r => (string?)r!["id"] == Path.GetFileName(reached));
                    Check((string)row!["state"]! == (published ? "complete" : "incomplete"), "Discovery never completes partial record: " + phase);
                    if (published) Check((string)Json(store.Reopen((string)row["recordRef"]!))["state"]! == "complete", "Lost committed response recovered by discovery");
                }
                processes.Add(new { phase, path = reached, childId = child.Id, childExitCode = child.ExitCode, published, nativeExecuted = false });
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(10000); }
                File.WriteAllText(Path.Combine(root, "handoff-child-" + phase + ".log"), stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
            }
        }
        string link = Path.Combine(storeRoot, "records", new string('f',32)), peer = Path.Combine(root, "handoff-foreign"); Directory.CreateDirectory(peer); File.WriteAllText(Path.Combine(peer, "keep"), "original");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "/c", "mklink", "/J", link, peer }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!; Check(child.WaitForExit(10000) && child.ExitCode == 0, "Created isolated junction fixture");
        }
        else Directory.CreateSymbolicLink(link, peer);
        try { Reject(() => store.List(), "Record inventory refuses links", "PackageInvalid"); Check(File.ReadAllText(Path.Combine(peer, "keep")) == "original", "Link refusal preserves target"); }
        finally { Directory.Delete(link); }
        Check(File.ReadAllText(Path.Combine(peer, "keep")) == "original", "Unlink leaves target intact");
        string filler = Path.Combine(storeRoot, "quota.tmp");
        try
        {
            using (var file = new FileStream(filler, FileMode.CreateNew, FileAccess.Write)) file.SetLength(512L << 20);
            Reject(() => store.Review(incoming.Ref), "Abandoned bytes count toward quota", "HandoffCapacity");
            Check(JsonNode.DeepEquals(summary, Json(store.Reopen(reference))), "Exact existing handle remains readable over quota");
        }
        finally { File.Delete(filler); }
        var extras = new List<string>();
        try
        {
            for (int i = 0; i < 128; i++) { string path = Path.Combine(storeRoot, "records", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); extras.Add(path); }
            Reject(() => store.Review(incoming.Ref), "Incomplete records count toward quota", "HandoffCapacity");
        }
        finally { foreach (string path in extras) Directory.Delete(path); extras.Clear(); }
        try
        {
            for (int i = 0; i < 2048; i++) { string path = Path.Combine(storeRoot, "entry-" + i + ".tmp"); File.WriteAllText(path, "x"); extras.Add(path); }
            Reject(() => store.Review(incoming.Ref), "Abandoned entries count toward quota", "HandoffCapacity");
        }
        finally { foreach (string path in extras) File.Delete(path); }
        var pageSource = new JsonArray((JsonNode?)null, JsonValue.Create("雪\"\\"), JsonValue.Create(new string('x', 500)));
        int firstBytes = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["index"] = 0, ["value"] = null }, BridgeProtocol.Json).Length;
        var exactPage = ReportPageRows.Read(pageSource, 0, 1, firstBytes, "Download original.");
        Check(exactPage.Next == 1 && exactPage.Rows.Count == 1 && exactPage.Rows[0]!["value"] is null, "Exact wrapped null row fits and advances");
        var escaped = ReportPageRows.Read(pageSource, 1, 1, 128, "Download original.");
        Check(escaped.Next == 2 && JsonNode.DeepEquals(escaped.Rows[0]!["value"], pageSource[1]), "Escaped Unicode remains exact in page");
        int escapedBytes = JsonSerializer.SerializeToUtf8Bytes(escaped.Rows[0], BridgeProtocol.Json).Length;
        var split = ReportPageRows.Read(pageSource, 0, 2, firstBytes + escapedBytes - 1, "Download original.");
        Check(split.Next == 1 && split.Rows.Count == 1, "Combined row budget leaves next row for the following page");
        var omitted = ReportPageRows.Read(pageSource, 2, 1, 128, "Download original.");
        Check(omitted.Next == 3 && (bool)omitted.Rows[0]!["omitted"]!, "Oversized wrapped row advances with explicit omission");
        var empty = ReportPageRows.Read(pageSource, 3, 1, 128, "Download original.");
        Check(empty.Next == 3 && empty.Rows.Count == 0, "End offset is an empty terminal page");
        Reject(() => ReportPageRows.Read(pageSource, 2, 1, 1, "Download original."), "Impossible omission budget refuses instead of stalling", "ArtifactCapacity");
        Check(pageSource[0] is null && (string)pageSource[2]! == new string('x', 500), "Paging never changes original rows");
        var large = JsonNode.Parse(sourceBytes["baseline.session.json"])!.AsObject(); var permissionRows = new JsonArray();
        for (int i = 0; i < 140; i++) permissionRows.Add(new JsonObject { ["id"] = i.ToString(), ["scope"] = "scope", ["outcome"] = "denied" });
        large["permissions"] = permissionRows;
        var boundaryRow = new JsonObject { ["role"] = "session", ["observation"] = large };
        int padding = (128 << 10) - 4096 - JsonSerializer.SerializeToUtf8Bytes(boundaryRow, BridgeProtocol.Json).Length;
        foreach (var row in permissionRows)
            foreach (string field in new[] { "id", "scope" })
            {
                string value = (string)row![field]!; int add = Math.Min(padding, 512 - value.Length);
                row[field] = value + new string('x', add); padding -= add;
            }
        Check(padding == 0 && JsonSerializer.SerializeToUtf8Bytes(boundaryRow, BridgeProtocol.Json).Length == (128 << 10) - 4096,
            "Original row exactly fills the page row budget before index/value wrapping");
        var largeUpload = Upload(JsonSerializer.SerializeToUtf8Bytes(large, BridgeProtocol.Json));
        var largeEvidence = Json(store.RetainObservation("session", largeUpload.Ref)); uploads.Release(largeUpload.Id);
        var largeRequest = request.DeepClone().AsObject(); largeRequest["baseline"]!["session"] = largeEvidence["artifact"]!.DeepClone(); largeRequest["current"]!["session"] = largeEvidence["artifact"]!.DeepClone();
        var wideUpload = Upload(JsonSerializer.SerializeToUtf8Bytes(largeRequest, BridgeProtocol.Json)); var wide = Json(store.Review(wideUpload.Ref)); uploads.Release(wideUpload.Id);
        var widePage = Json(store.Page((string)wide["recordRef"]!, "currentObservations", offset: 2, count: 1));
        Check(widePage["rows"]!.AsArray().Any(r => (bool?)r!["omitted"] == true) && JsonSerializer.SerializeToUtf8Bytes(widePage, BridgeProtocol.Json).Length <= 128 << 10, "Wrapped boundary observation is explicit in bounded page");
        Check(widePage["rows"]!.AsArray().Count == 1 && widePage["nextOffset"] is null, "Boundary row advances pagination to completion");
        Check(Download("current.session.json", (string)wide["recordRef"]!).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(large, BridgeProtocol.Json)), "Oversized original remains fully downloadable");
        uploads.Release(incoming.Id); Check(JsonNode.DeepEquals(summary, Json(new RuntimeHandoffWorkflowStore(root).Reopen(reference))), "Request upload release and fresh store preserve exact review");
        string evidence = Path.Combine(root, "handoff-workflow"); Directory.CreateDirectory(evidence);
        File.WriteAllBytes(Path.Combine(evidence, "request.json"), requestBytes); File.WriteAllBytes(Path.Combine(evidence, "report.json"), expected);
        File.WriteAllText(Path.Combine(evidence, "mcp-fixture.json"), JsonSerializer.Serialize(new { artifactRoot = root, requestPath = Path.Combine(evidence, "request.json"), reportPath = Path.Combine(evidence, "report.json") }, BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(evidence, "verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, checks, processes, recordRef = reference, wideRecordRef = (string)wide["recordRef"]! }, BridgeProtocol.Json));
        Console.WriteLine($"PASS handoff workflow: {checks.Count} checks; six actual publication-process interruptions, no native execution.");
    }
}
