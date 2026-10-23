using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;

internal static class CompositeWorkflowTests
{
    internal static void Run(string root)
    {
        var checks = new List<string>(); var processes = new List<object>();
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks.Add(label); }
        void Reject(Action action, string label, string code)
        { try { action(); } catch (BridgeException e) when (e.Code == code) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
        string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        JsonObject Json(object value) => JsonSerializer.SerializeToNode(value, BridgeProtocol.Json)!.AsObject();
        byte[] request = File.ReadAllBytes(Path.Combine(root, "request.json")), expected = File.ReadAllBytes(Path.Combine(root, "report.json"));
        var packet = JsonNode.Parse(request)!.AsObject(); var uploads = new ArtifactUploadStore(root);
        var upload = uploads.Begin("composite.json", request.Length, Hash(request));
        for (int i = 0; i < request.Length; i += upload.MaxChunkBytes) uploads.Write(upload.UploadId, i, Convert.ToBase64String(request, i, Math.Min(upload.MaxChunkBytes, request.Length - i)));
        string uploadRef = uploads.Commit(upload.UploadId).ArtifactRef!;
        var store = new CompositeEngineeringWorkflowStore(root); var summary = Json(store.Review(uploadRef));
        string reference = (string)summary["recordRef"]!;
        string directory = Path.Combine(root, "composite-engineering-reviews", reference.Split(':')[1]);
        string storeRoot = Path.GetDirectoryName(directory)!;
        byte[] Download(string name)
        {
            using var result = new MemoryStream(); bool complete = false;
            while (!complete)
            {
                var chunk = Json(store.ReadArtifact(reference, name, result.Length, 8192)); var bytes = Convert.FromBase64String((string)chunk["data"]!);
                Check((long)chunk["offset"]! == result.Length && bytes.Length == (int)chunk["count"]!, "Chunk offset and bytes are exact: " + name);
                result.Write(bytes); complete = (bool)chunk["complete"]!;
                Check((long)chunk["nextOffset"]! == result.Length, "Chunk next offset advances exactly: " + name);
                if (complete) Check((long)chunk["bytes"]! == result.Length && (string)chunk["sha256"]! == Hash(result.ToArray()), "Downloaded complete digest: " + name);
            }
            return result.ToArray();
        }
        Check(Download("request.json").AsSpan().SequenceEqual(request), "Exact original request bytes survive publication");
        Check(Download("report.json").AsSpan().SequenceEqual(expected), "Retained report equals independent evaluator fixture");
        byte[] receipt = Download("receipt.json"); Check(Hash(receipt) == reference.Split(':')[2], "Receipt digest binds exact public handle");
        Check(JsonNode.DeepEquals(summary, Json(new CompositeEngineeringWorkflowStore(root).Reopen(reference))), "Store recreation revalidates immutable record");
        foreach (string section in new[] { "selections", "mappings", "correspondences", "impacts", "coverageGaps", "beforeComponents", "afterComponents", "amlChanges", "beforeAmlImpacts", "afterAmlImpacts" })
        {
            int offset = 0, total = -1; var seen = new HashSet<int>();
            do
            {
                var page = Json(store.Page(reference, section, offset, 1)); total = (int)page["total"]!;
                foreach (var row in page["rows"]!.AsArray()) Check(seen.Add((int)row!["index"]!), "Page row occurs once: " + section);
                Check((string)page["reportSha256"]! == Hash(expected), "Page binds exact report bytes: " + section);
                offset = page["nextOffset"] is null ? -1 : (int)page["nextOffset"]!;
            } while (offset >= 0);
            Check(seen.Count == total, "Complete paged traversal: " + section);
        }
        Reject(() => store.Page(reference, "../request.json"), "Unknown page section refused", "CompositeInvalidSection");
        Reject(() => store.Page(reference, count: 201), "Oversized page count refused", "CompositePageLimit");
        Reject(() => store.ReadArtifact(reference, "intent.json"), "Internal intent is not an arbitrary download route", "CompositeArtifactMissing");
        Reject(() => store.ReadArtifact(reference, "report.json", expected.Length + 1), "Past-end download refused", "CompositePageLimit");
        Reject(() => store.Reopen(reference.ToUpperInvariant()), "Noncanonical handle refused", "CompositeInvalidReference");
        foreach (string name in new[] { "intent.json", "receipt.json", "request.json", "report.json" })
        {
            string path = Path.Combine(directory, name); byte[] original = File.ReadAllBytes(path); File.AppendAllText(path, " ");
            try { Reject(() => store.Reopen(reference), "Changed retained artifact refuses: " + name, "CompositeIntegrityFailure"); }
            finally { File.WriteAllBytes(path, original); }
        }
        var forged = JsonNode.Parse(expected)!.AsObject(); forged["nativeQualified"] = true;
        byte[] forgedReport = JsonSerializer.SerializeToUtf8Bytes(forged, BridgeProtocol.Json);
        var forgedReceipt = JsonNode.Parse(receipt)!.AsObject(); forgedReceipt["artifacts"]![1]!["bytes"] = forgedReport.Length; forgedReceipt["artifacts"]![1]!["sha256"] = Hash(forgedReport);
        byte[] forgedReceiptBytes = JsonSerializer.SerializeToUtf8Bytes(forgedReceipt, BridgeProtocol.Json);
        File.WriteAllBytes(Path.Combine(directory, "report.json"), forgedReport);
        File.WriteAllBytes(Path.Combine(directory, "intent.json"), forgedReceiptBytes); File.WriteAllBytes(Path.Combine(directory, "receipt.json"), forgedReceiptBytes);
        try { Reject(() => store.Reopen(reference[..(reference.LastIndexOf(':') + 1)] + Hash(forgedReceiptBytes)), "Self-consistent forged hashes cannot replace rebuilt report", "CompositeIntegrityFailure"); }
        finally { File.WriteAllBytes(Path.Combine(directory, "report.json"), expected); File.WriteAllBytes(Path.Combine(directory, "intent.json"), receipt); File.WriteAllBytes(Path.Combine(directory, "receipt.json"), receipt); }
        var unknownReceipt = JsonNode.Parse(receipt)!.AsObject(); unknownReceipt["nativeQualified"] = true;
        byte[] unknownBytes = JsonSerializer.SerializeToUtf8Bytes(unknownReceipt, BridgeProtocol.Json);
        File.WriteAllBytes(Path.Combine(directory, "intent.json"), unknownBytes); File.WriteAllBytes(Path.Combine(directory, "receipt.json"), unknownBytes);
        try { Reject(() => store.Reopen(reference[..(reference.LastIndexOf(':') + 1)] + Hash(unknownBytes)), "Self-consistent unknown receipt fields refuse", "CompositeIntegrityFailure"); }
        finally { File.WriteAllBytes(Path.Combine(directory, "intent.json"), receipt); File.WriteAllBytes(Path.Combine(directory, "receipt.json"), receipt); }
        string unexpected = Path.Combine(directory, "unexpected.json"); File.WriteAllText(unexpected, "{}");
        try { Reject(() => store.Reopen(reference), "Unexpected record entries refuse", "CompositeIntegrityFailure"); }
        finally { File.Delete(unexpected); }
        string aml = Path.Combine(root, "automationml-workflow", ((string)packet["before"]!["automationMlRef"]!).Split(':')[1], "original.aml");
        byte[] originalAml = File.ReadAllBytes(aml); File.AppendAllText(aml, " ");
        try
        {
            Reject(() => store.ReadArtifact(reference, "report.json"), "Download revalidates changed original AML", "AutomationMlIntegrityFailure");
            Check(Json(store.List())["rows"]!.AsArray().Any(r => (string?)r!["state"] == "verificationFailed"), "Discovery exposes failed dependency verification");
        }
        finally { File.WriteAllBytes(aml, originalAml); }
        string? interruptedDirectory = null;
        var changed = new CompositeEngineeringWorkflowStore(root, null, (phase, folder) => { if (phase == "reportWritten") { interruptedDirectory = folder; File.AppendAllText(aml, " "); } });
        try { Reject(() => changed.Review(uploadRef), "Changed dependency before receipt prevents publication", "AutomationMlIntegrityFailure"); }
        finally { File.WriteAllBytes(aml, originalAml); }
        Check(interruptedDirectory is not null && !File.Exists(Path.Combine(interruptedDirectory, "receipt.json")), "Rejected publication keeps incomplete intent and artifacts");
        foreach (string phase in new[] { "intentWritten", "requestWritten", "reportWritten", "receiptWritten" })
        {
            string signal = Path.Combine(root, "pause-" + phase + ".txt");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { Assembly.GetExecutingAssembly().Location, "--publication-child", root, uploadRef, phase, signal }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new Exception("Child did not start");
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            string? folder = null; var watch = Stopwatch.StartNew();
            try
            {
                while (watch.Elapsed < TimeSpan.FromSeconds(30) && !child.HasExited)
                {
                    try { if (File.Exists(signal)) { string value = File.ReadAllText(signal); if (Directory.Exists(value)) { folder = value; break; } } }
                    catch (IOException) { }
                    Thread.Sleep(10);
                }
                Check(folder is not null && !child.HasExited, "Actual child reached flushed publication boundary: " + phase);
                child.Kill(entireProcessTree: true); Check(child.WaitForExit(10000), "Owned publication child terminated: " + phase);
                bool committed = File.Exists(Path.Combine(folder!, "receipt.json")); Check(committed == (phase == "receiptWritten"), "Receipt boundary survives actual process exit: " + phase);
                var row = Json(store.List())["rows"]!.AsArray().Single(r => (string?)r!["id"] == Path.GetFileName(folder));
                Check((string)row!["state"]! == (committed ? "complete" : "incomplete"), "Restart discovery never completes an interrupted write: " + phase);
                if (committed) Check((string)Json(store.Reopen((string)row["recordRef"]!))["state"]! == "complete", "Committed record is recoverable after lost process reply");
                processes.Add(new { phase, folder, childId = child.Id, childExitCode = child.ExitCode, committed, nativeExecuted = false });
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(10000); } File.WriteAllText(Path.Combine(root, "child-" + phase + ".log"), stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()); }
        }
        string quota = Path.Combine(storeRoot, "quota.tmp");
        try
        {
            using (var file = new FileStream(quota, FileMode.CreateNew, FileAccess.Write)) file.SetLength(512L << 20);
            Reject(() => store.Review(uploadRef), "Abandoned bytes count toward storage quota", "CompositeCapacity");
            Check(JsonNode.DeepEquals(summary, Json(store.Reopen(reference))), "Existing exact handle remains readable when store is over quota");
        }
        finally { File.Delete(quota); }
        var extraDirectories = new List<string>();
        try
        {
            for (int i = 0; i < 128; i++) { string path = Path.Combine(storeRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); extraDirectories.Add(path); }
            Reject(() => store.Review(uploadRef), "Abandoned record directories count toward record quota", "CompositeCapacity");
        }
        finally { foreach (string path in extraDirectories) Directory.Delete(path); }
        var extraFiles = new List<string>();
        try
        {
            for (int i = 0; i < 1024; i++) { string path = Path.Combine(storeRoot, "entry-" + i + ".tmp"); File.WriteAllText(path, "x"); extraFiles.Add(path); }
            Reject(() => store.Review(uploadRef), "Abandoned filesystem entries count toward entry quota", "CompositeCapacity");
        }
        finally { foreach (string path in extraFiles) File.Delete(path); }
        uploads.Release(upload.UploadId);
        Check(JsonNode.DeepEquals(summary, Json(new CompositeEngineeringWorkflowStore(root).Reopen(reference))), "Source request upload release does not break retained review");
        File.WriteAllText(Path.Combine(root, "workflow-verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, checks, processes }, BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(root, "mcp-fixture.json"), JsonSerializer.Serialize(new { artifactRoot = root, requestPath = Path.Combine(root, "request.json"), reportPath = Path.Combine(root, "report.json") }, BridgeProtocol.Json));
        Console.WriteLine($"PASS composite workflow: {checks.Count} checks; four actual publication-process interruptions; no native execution.");
    }

}
