using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ModelContextProtocol.Protocol;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Host;
using TiaMcp.Translations;

if (args.Length == 4 && args[0] == "--restart")
{
    var reopened = Check.Ok(Check.Tools(args[1]).Reopen(args[2]));
    Check.Equal(args[3], Check.S(reopened["candidate"]!, "recordSha256"));
    Check.Equal("complete", Check.S(reopened["candidate"]!, "state"));
    Console.WriteLine("PASS separate managed process reopened exact review, approval and candidate; no native dependency");
    return 0;
}
if (args.Length < 1) throw new ArgumentException("Provide a new isolated output directory; optional second argument is retained managed-cases.json.");
string output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new ArgumentException("Use a new isolated output directory.");
Directory.CreateDirectory(output);
var checks = new List<(string Name, Action Run)>();
var outcomes = new JsonArray();
void Test(string name, Action test) => checks.Add((name, test));
Fixture New() => new(Path.Combine(output, Guid.NewGuid().ToString("N")));

Test("capture requires committed uploads and refuses filesystem paths", () =>
{
    var f = New();
    Check.Error("InvalidRequest", f.Tools.Capture(@"C:\outside.xlsx", f.ProfileUpload, f.ProjectUpload));
    var receiving = f.Uploads.Begin("partial.xlsx", f.Original.Length, TranslationJson.Sha256(f.Original));
    Check.Error("UploadIncomplete", f.Tools.Capture("upload:" + receiving.UploadId, f.ProfileUpload, f.ProjectUpload));
    Check.Error("InvalidTranslationReference", f.Tools.Reopen("../capture"));
    Check.Error("InvalidTranslationReference", f.Tools.ReadArtifact("translation:capture:" + new string('a', 32) + ":../outside"));
});
Test("captured inputs survive release of all committed uploads", () =>
{
    var f = New(); var capture = f.Capture();
    foreach (string upload in new[] { f.WorkbookUpload, f.ProfileUpload, f.ProjectUpload }) f.Uploads.Release(upload[7..]);
    var reopened = Check.Ok(Check.Tools(f.Root).Reopen(Check.Ref(capture)));
    Check.Equal(Check.S(capture, "recordSha256"), Check.S(reopened, "recordSha256"));
    Check.Bytes(f.Original, f.Download(Check.Artifact(reopened, "original.xlsx")));
    Check.Equal(f.Identity, TranslationJson.Parse<EngineeringObjectReference>(Encoding.UTF8.GetString(f.Download(Check.Artifact(reopened, "project.json")))));
});
Test("inspection pages preserve missing empty whitespace and exact multilingual states", () =>
{
    var f = New(); var capture = f.Capture(f.Changed([f.Edit("K-004", " \t ")]));
    var rows = new List<JsonNode>(); TranslationInspectionCursor? cursor = null;
    do
    {
        var page = Check.Ok(f.Tools.Inspect(Check.Ref(capture), 2, cursor));
        rows.AddRange(page["rows"]!.AsArray().Select(n => n!.DeepClone()));
        cursor = page["nextCursor"] is null ? null : TranslationJson.Parse<TranslationInspectionCursor>(page["nextCursor"]!.ToJsonString());
    } while (cursor is not null);
    Check.Equal(12, rows.Count);
    Check.Equal(12, rows.Select(r => Check.S(r, "nativeKey") + ":" + Check.S(r, "culture")).Distinct().Count());
    Check.Equal("missing", Check.S(rows.Single(r => Check.S(r, "nativeKey") == "K-002")["target"]!, "state"));
    Check.Equal("empty", Check.S(rows.Single(r => Check.S(r, "nativeKey") == "K-006")["target"]!, "state"));
    Check.True(rows.Any(r => r["findings"]!.AsArray().Any(v => v!.GetValue<string>() == "whitespace-only")));
});
Test("inspection cursors reject newer captures and paging bounds", () =>
{
    var f = New(); var first = f.Capture(); var next = f.Capture(project: f.NewIdentity());
    var page = Check.Ok(f.Tools.Inspect(Check.Ref(first), 1));
    var cursor = TranslationJson.Parse<TranslationInspectionCursor>(page["nextCursor"]!.ToJsonString());
    Check.Error("StaleTranslationCursor", f.Tools.Inspect(Check.Ref(next), 1, cursor));
    foreach (int size in new[] { 0, 1001 }) Check.Error("TranslationPageLimit", f.Tools.Inspect(Check.Ref(first), size));
    Check.Error("InvalidTranslationCursor", f.Tools.Inspect(Check.Ref(first), 1, cursor with { Offset = -1 }));
});
Test("inspection bounds oversized repeated sources before collecting a full page", () =>
{
    var f = New(); var capture = f.Capture(f.Changed([f.Edit("K-001", new string('A', 200000), "en-US")], new(UpdateSourceLanguage: true)));
    var page = Check.Ok(f.Tools.Inspect(Check.Ref(capture), 1000));
    Check.Equal(1, page["rows"]!.AsArray().Count);
    Check.True(page["rows"]![0]!["omitted"]!.GetValue<bool>());
    Check.True(Encoding.UTF8.GetByteCount(page.ToJsonString()) < 256 * 1024);
    var cursor = TranslationJson.Parse<TranslationInspectionCursor>(page["nextCursor"]!.ToJsonString());
    Check.Equal(1, cursor.Offset);
    var next = Check.Ok(f.Tools.Inspect(Check.Ref(capture), 1000, cursor));
    Check.Equal(1, next["offset"]!.GetValue<int>());
    Check.Equal(1, next["rows"]![0]!["entryIndex"]!.GetValue<int>());
});
Test("preapproved proposals are rejected without manufacturing an approval", () =>
{
    var f = New(); var capture = f.Capture();
    Check.Error("TranslationApprovalNotAllowed", f.Tools.Prepare(Check.Ref(capture), f.Batch([f.Edit("K-002", "Neu") with { Approved = true, Reviewer = "injected" }])));
    Check.Equal(0, Check.Ok(f.Tools.List("approval"))["total"]!.GetValue<int>());
});
Test("approval requires explicit every-change consent reviewer and exact digest", () =>
{
    var f = New(); var review = f.Prepare(f.Capture(), [f.Edit("K-002", "Neu")]);
    string reference = Check.Ref(review), digest = Check.Digest(review);
    Check.Error("ExplicitTranslationApprovalRequired", f.Tools.Candidate(reference, digest, new string('0', 64)));
    Check.Error("ExplicitTranslationApprovalRequired", f.Tools.Approve(reference, digest, "reviewer", false));
    Check.Error("ExplicitTranslationApprovalRequired", f.Tools.Approve(reference, digest, " ", true));
    Check.Error("ReviewApprovalMismatch", f.Tools.Approve(reference, new string('0', 64), "reviewer", true));
    var approval = f.Approve(review);
    Check.Equal(Check.S(approval, "recordSha256"), Check.S(f.Approve(review), "recordSha256"));
    Check.Error("ReviewApprovalMismatch", f.Tools.Approve(reference, digest, "different reviewer", true));
    Check.Error("ReviewApprovalMismatch", f.Tools.Candidate(reference, digest, new string('f', 64)));
});
Test("Host-to-library candidate preserves independent XML peers and Unicode", () =>
{
    var f = New(); var capture = f.Capture();
    const string unicode = " Ω e\u0301 é 東京 😀\r\n\t ";
    var review = f.Prepare(capture, [f.Edit("K-002", "Kühlung aktiviert"), f.Edit("K-004", unicode), f.Edit("K-007", "Betriebsbereit")]);
    var page = Check.Ok(f.Tools.ReviewPage(Check.Ref(review), pageSize: 2));
    Check.Equal(2, page["rows"]!.AsArray().Count);
    Check.True(page["rows"]!.AsArray().All(r => r!["edit"]!["approved"]!.GetValue<bool>() == false));
    var candidate = f.Candidate(review);
    byte[] result = f.Download(Check.Artifact(candidate, "candidate.xlsx"));
    var workbook = f.Open(result);
    Check.Equal("Kühlung aktiviert", workbook.Read("K-002", "de-DE").Target.Text);
    Check.Equal(unicode, workbook.Read("K-004", "de-DE").Target.Text);
    Check.Equal("Betriebsbereit", workbook.Read("K-007", "de-DE").Target.Text);
    Check.Preservation(f.Original, result, ["C3", "C5", "C8"]);
    var handoff = JsonNode.Parse(f.Download(Check.Artifact(candidate, "native-handoff.json")))!;
    Check.Equal("disabled-pending-qualification", Check.S(handoff, "state"));
    Check.Equal(f.Identity, TranslationJson.Parse<EngineeringObjectReference>(handoff["destination"]!.ToJsonString()));
    foreach (string flag in new[] { "nativeQualified", "nativeExecuted", "nativeImportAllowed", "nativeFreshnessVerified" }) Check.True(!candidate[flag]!.GetValue<bool>());
});
Test("completed review and candidate reopen in a fresh managed process", () =>
{
    var f = New(); var review = f.Prepare(f.Capture(), [f.Edit("K-002", "Neu")]); var candidate = f.Candidate(review);
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string value in new[] { Assembly.GetExecutingAssembly().Location, "--restart", f.Root, Check.Ref(review), Check.S(candidate, "recordSha256") }) start.ArgumentList.Add(value);
    using var process = Process.Start(start)!;
    string stdout = process.StandardOutput.ReadToEnd(), stderr = process.StandardError.ReadToEnd();
    Check.True(process.WaitForExit(30000), "Restart verification process timed out");
    Check.Equal(0, process.ExitCode, stderr + stdout);
    Check.True(stdout.Contains("PASS separate managed process", StringComparison.Ordinal));
    Check.Equal(Check.Ref(review), Check.S(Check.Ok(Check.Tools(f.Root).List("review"))["rows"]![0]!, "recordRef"));
});
Test("review pages bind exact record and section and retain complete report", () =>
{
    var f = New(); var capture = f.Capture();
    var a = f.Prepare(capture, [f.Edit("K-002", "A"), f.Edit("K-004", "B")]);
    var b = f.Prepare(capture, [f.Edit("K-002", "A"), f.Edit("K-004", "C")]);
    var cursor = TranslationJson.Parse<TranslationReviewCursor>(Check.Ok(f.Tools.ReviewPage(Check.Ref(a), pageSize: 1))["nextCursor"]!.ToJsonString());
    Check.Error("StaleTranslationCursor", f.Tools.ReviewPage(Check.Ref(b), cursor: cursor));
    Check.Error("StaleTranslationCursor", f.Tools.ReviewPage(Check.Ref(a), "issues", cursor: cursor));
    Check.Error("InvalidTranslationCursor", f.Tools.ReviewPage(Check.Ref(a), cursor: cursor with { Offset = 999 }));
    Check.Error("TranslationPageLimit", f.Tools.ReviewPage(Check.Ref(a), pageSize: 1001));
    Check.Error("TranslationPageLimit", f.Tools.List("review", count: 101));
    Check.Error("TranslationPageLimit", f.Tools.ReadArtifact(Check.Artifact(a, "review.json"), count: 65537));
    var report = JsonNode.Parse(f.Download(Check.Artifact(a, "review.json")))!;
    Check.Equal(2, report["edits"]!.AsArray().Count);
    Check.True(report["edits"]!.AsArray().All(e => !e!["edit"]!["approved"]!.GetValue<bool>()));
});
Test("large individual review rows are explicit omissions with exact artifact retrieval", () =>
{
    var f = New(); string text = new('Ω', 210000);
    var review = f.Prepare(f.Capture(), [f.Edit("K-004", text)]);
    var page = Check.Ok(f.Tools.ReviewPage(Check.Ref(review)));
    Check.True(page["rows"]![0]!["omitted"]!.GetValue<bool>());
    Check.True(Encoding.UTF8.GetByteCount(page.ToJsonString()) < 256 * 1024);
    var report = JsonNode.Parse(f.Download(Check.Artifact(review, "review.json")))!;
    Check.Equal(text, Check.S(report["edits"]![0]!, "after"));
    var candidate = f.Candidate(review);
    Check.Equal(text, f.Open(f.Download(Check.Artifact(candidate, "candidate.xlsx"))).Read("K-004", "de-DE").Target.Text);
});
Test("proposal count is bounded before review", () =>
{
    var f = New(); var capture = f.Capture();
    Check.Error("TranslationProposalLimit", f.Tools.Prepare(Check.Ref(capture), f.Batch(Enumerable.Repeat(f.Edit("K-002", "A"), 10001).ToArray())));
});
Test("complete review amplification is bounded before report allocation", () =>
{
    var f = New(); var capture = f.Capture(); string text = new('a', 950000);
    Check.Error("TranslationReviewLimit", f.Tools.Prepare(Check.Ref(capture), f.Batch([f.Edit("K-004", text), f.Edit("K-005", text), f.Edit("K-011", text)])));
    Check.Equal(0, Check.Ok(f.Tools.List("review"))["total"]!.GetValue<int>());
});
Test("oversized reconciliation is refused without truncating its conflicts or proposals", () =>
{
    var f = New(); var baseline = f.Capture(); string text = new('a', 950000);
    byte[] edited = f.Changed([f.Edit("K-004", text), f.Edit("K-005", text), f.Edit("K-011", text)]);
    Check.Error("TranslationReconciliationLimit", f.Tools.Reconcile(Check.Ref(baseline), f.Upload(edited, "edited.xlsx"), Check.Ref(baseline)));
    Check.Equal(0, Check.Ok(f.Tools.List("reconciliation"))["total"]!.GetValue<int>());
});
Test("retention refuses excessive ancestry before publishing an unreopenable complete record", () =>
{
    var root = Path.Combine(output, Guid.NewGuid().ToString("N"));
    var retention = new TranslationRetention(root);
    var previous = retention.Publish("capture", Guid.NewGuid().ToString("N"), new JsonObject(), new Dictionary<string, byte[]>());
    for (int depth = 1; depth <= 64; ++depth)
        previous = retention.Publish("review", Guid.NewGuid().ToString("N"), new JsonObject(), new Dictionary<string, byte[]>(), previous);
    Check.Equal(previous.RecordSha256, retention.Load(TranslationRetention.Reference(previous)).RecordSha256);
    string refused = Guid.NewGuid().ToString("N");
    Check.Reject("TranslationDependencyLimit", () => retention.Publish("review", refused, new JsonObject(), new Dictionary<string, byte[]>(), previous));
    Check.True(!retention.Exists("review", refused));
});
foreach (string file in new[] { "original.xlsx", "profile.json", "project.json", "proposals.json", "review.json", "approved-proposals.json", "approved-review.json", "candidate.xlsx", "candidate-manifest.json" })
{
    string selected = file;
    Test("restart rejects changed retained " + selected, () =>
    {
        var f = New(); var capture = f.Capture(); var review = f.Prepare(capture, [f.Edit("K-002", "Neu")]);
        var approval = f.Approve(review); var candidate = f.Candidate(review);
        string path = Directory.GetFiles(Path.Combine(f.Root, "translation-workflow"), selected, SearchOption.AllDirectories).Single();
        File.AppendAllText(path, "tampered");
        Check.Error("TranslationIntegrityFailure", Check.Tools(f.Root).Reopen(Check.Ref(candidate)));
        Check.Error("TranslationIntegrityFailure", f.Tools.Candidate(Check.Ref(review), Check.Digest(review), Check.S(approval, "recordSha256")));
        Check.Equal("integrity-failed", Check.S(Check.Ok(f.Tools.List("candidate"))["rows"]![0]!, "state"));
    });
}
foreach (var (name, edit, expected) in new (string, Func<Fixture, TranslationEdit>, string)[]
{
    ("expected source", f => f.Edit("K-002", "Neu") with { ExpectedSourceSha256 = new string('0', 64) }, "SourceTextConflict"),
    ("expected target", f => f.Edit("K-002", "Neu") with { ExpectedTargetSha256 = new string('0', 64) }, "TargetTextConflict"),
    ("exact native key", f => f.Edit("K-002", "Neu") with { NativeKey = "recreated" }, "StaleTextKey"),
    ("rich text", f => f.Edit("K-010", "flattened"), "RichTextEditUnsupported"),
    ("placeholder", f => f.Edit("K-003", "omitted tokens"), "PlaceholderMismatch"),
    ("length", f => f.Edit("K-001", new string('x', 21)), "TextLengthExceeded"),
    ("source-language", f => f.Edit("K-001", "Prepared", "en-US"), "SourceEditNotApproved"),
    ("clear-cell restriction", f => f.Edit("K-001", "ignored", "fr-FR") with { Kind = TranslationEditKind.Clear, Value = null }, "ClearSemanticsUnqualified")
})
{
    Test("unapproved review exposes " + name + " restriction before approval", () =>
    {
        var f = New(); var review = f.Prepare(f.Capture(), [edit(f)]);
        var issues = Check.Ok(f.Tools.ReviewPage(Check.Ref(review), "issues"))["rows"]!.AsArray();
        Check.True(issues.Any(i => Check.S(i!, "code") == expected), "Expected issue " + expected);
        Check.Error("TranslationReviewBlocked", f.Tools.Approve(Check.Ref(review), Check.Digest(review), "reviewer", true));
    });
}
Test("clear missing creation and whitespace remain distinct through candidates", () =>
{
    var f = New(); var review = f.Prepare(f.Capture(), [f.Edit("K-001", "ignored") with { Kind = TranslationEditKind.Clear, Value = null }, f.Edit("K-002", "  ")]);
    var candidate = f.Candidate(review); var wb = f.Open(f.Download(Check.Artifact(candidate, "candidate.xlsx")));
    Check.Equal("empty", wb.Read("K-001", "de-DE").Target.State);
    Check.Equal("text", wb.Read("K-002", "de-DE").Target.State);
    Check.Equal("  ", wb.Read("K-002", "de-DE").Target.Text);
    var rows = f.Profile.Rows.Select(r => r with { Targets = r.Targets.Select(t => t with { AllowCreateMissing = false }).ToArray() }).ToArray();
    var capture = f.Capture(profile: f.Profile with { Rows = rows });
    var blocked = f.Prepare(capture, [f.Edit("K-002", "Neu")]);
    Check.Error("TranslationReviewBlocked", f.Tools.Approve(Check.Ref(blocked), Check.Digest(blocked), "reviewer", true));
});
Test("new review cannot reuse an approval from concurrent proposals", () =>
{
    var f = New(); var capture = f.Capture();
    var a = f.Prepare(capture, [f.Edit("K-002", "A")]); var b = f.Prepare(capture, [f.Edit("K-004", "B")]);
    var approvedA = f.Approve(a); f.Approve(b);
    Check.Error("ReviewApprovalMismatch", f.Tools.Candidate(Check.Ref(b), Check.Digest(b), Check.S(approvedA, "recordSha256")));
    var ca = f.Candidate(a); var cb = f.Candidate(b);
    Check.Equal("missing", f.Open(f.Download(Check.Artifact(cb, "candidate.xlsx"))).Read("K-002", "de-DE").Target.State);
    Check.True(Check.Artifact(ca, "candidate.xlsx") != Check.Artifact(cb, "candidate.xlsx"));
});
Test("concurrent publication has one writer and preserves both reviewed intents", () =>
{
    var f = New(); var capture = f.Capture(); string batch = f.Batch([f.Edit("K-002", "A")]);
    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
    var writer = new TranslationWorkflowStore(f.Root, null, point => { if (point == "review:intent") { entered.Set(); Check.True(release.Wait(10000)); } });
    var pending = Task.Run(() => writer.Prepare(Check.Ref(capture), batch));
    try
    {
        Check.True(entered.Wait(10000));
        Check.Error("TranslationStoreBusy", f.Tools.Prepare(Check.Ref(capture), batch));
    }
    finally { release.Set(); }
    Check.Equal("complete", Check.S(pending.GetAwaiter().GetResult(), "state"));
    var other = f.Prepare(capture, [f.Edit("K-004", "B")]);
    Check.True(Check.Ref(other) != Check.Ref(pending.Result));
});
Test("reconciliation uses current capture and always creates fresh unapproved proposals", () =>
{
    var f = New(); var baseline = f.Capture(); var review = f.Prepare(baseline, [f.Edit("K-002", "Reviewed")]);
    var approved = f.Approve(review); var candidate = f.Candidate(review);
    byte[] currentBytes = f.Changed([f.Edit("K-001", "Sentinelle", "fr-FR")]);
    var current = f.Capture(currentBytes, project: f.NewIdentity());
    var reconciled = Check.Ok(f.Tools.Reconcile(Check.Ref(baseline), Check.Artifact(candidate, "candidate.xlsx"), Check.Ref(current)));
    Check.Equal(0, reconciled["metadata"]!["conflicts"]!.GetValue<int>());
    var proposals = JsonNode.Parse(f.Download(Check.Artifact(reconciled, "proposals.json")))!;
    Check.True(!proposals["edits"]![0]!["approved"]!.GetValue<bool>());
    Check.Error("StaleWorkbook", f.Tools.Prepare(Check.Ref(baseline), Check.Artifact(reconciled, "proposals.json")));
    var next = Check.Ok(f.Tools.Prepare(Check.Ref(current), Check.Artifact(reconciled, "proposals.json")));
    Check.Error("ExplicitTranslationApprovalRequired", f.Tools.Candidate(Check.Ref(next), Check.Digest(next), Check.S(approved, "recordSha256")));
    var nextCandidate = f.Candidate(next);
    var wb = f.Open(f.Download(Check.Artifact(nextCandidate, "candidate.xlsx")));
    Check.Equal("Reviewed", wb.Read("K-002", "de-DE").Target.Text);
    Check.Equal("Sentinelle", wb.Read("K-001", "fr-FR").Target.Text);
});
foreach (bool source in new[] { false, true })
{
    Test("reconciliation returns concurrent " + (source ? "source" : "target") + " conflicts", () =>
    {
        var f = New(); var baseline = f.Capture(); var candidate = f.Candidate(f.Prepare(baseline, [f.Edit("K-002", "Reviewed")]));
        var current = f.Capture(f.Changed([f.Edit("K-002", "Concurrent", source ? "en-US" : "de-DE")], new(UpdateSourceLanguage: source)), project: f.NewIdentity());
        var report = Check.Ok(f.Tools.Reconcile(Check.Ref(baseline), Check.Artifact(candidate, "candidate.xlsx"), Check.Ref(current)));
        Check.Equal(1, report["metadata"]!["conflicts"]!.GetValue<int>());
        Check.Equal(0, report["metadata"]!["proposals"]!.GetValue<int>());
        var rows = Check.Ok(f.Tools.ReviewPage(Check.Ref(report), "rows"))["rows"]!.AsArray();
        Check.Equal(source ? "SourceTextConflict" : "TargetTextConflict", Check.S(rows[0]!, "conflictCode"));
    });
}
Test("reconciliation refuses foreign projects profile drift and unrelated edited package changes", () =>
{
    var f = New(); var baseline = f.Capture(); var candidate = f.Candidate(f.Prepare(baseline, [f.Edit("K-002", "Reviewed")]));
    var foreign = f.Capture(project: f.Identity with { Object = f.Identity.Object with { ProjectPath = @"D:\foreign.ap21" } });
    Check.Error("IdentityMismatch", f.Tools.Reconcile(Check.Ref(baseline), Check.Artifact(candidate, "candidate.xlsx"), Check.Ref(foreign)));
    var changedProfile = f.Capture(profile: f.Profile with { Id = "different" });
    Check.Error("ReconciliationProfileMismatch", f.Tools.Reconcile(Check.Ref(baseline), Check.Artifact(candidate, "candidate.xlsx"), Check.Ref(changedProfile)));
    var parts = Check.Unpack(f.Download(Check.Artifact(candidate, "candidate.xlsx")));
    // Shared strings are an existing unrelated part; even semantically neutral
    // package rewrites must not escape the conservative reconciliation gate.
    parts["xl/sharedStrings.xml"] = [.. parts["xl/sharedStrings.xml"], (byte)' '];
    Check.Error("TranslationPreservationFailure", f.Tools.Reconcile(Check.Ref(baseline), f.Upload(Check.Pack(parts), "edited.xlsx"), Check.Ref(baseline)));
});
foreach (string point in new[] { "intent", "candidate.xlsx", "candidate-manifest.json", "native-handoff.json", "before-receipt", "complete" })
{
    string selected = point;
    Test("candidate publication interruption at " + selected + " survives restart without overwrite", () =>
    {
        var f = New(); var review = f.Prepare(f.Capture(), [f.Edit("K-002", "Neu")]); var approval = f.Approve(review);
        var store = new TranslationWorkflowStore(f.Root, null, p => { if (p == "candidate:" + selected) throw new IOException("injected publication loss"); });
        Check.Reject(selected == "complete" ? "TranslationPublicationResponseLost" : "TranslationPublicationIncomplete",
            () => store.Candidate(Check.Ref(review), Check.Digest(review), Check.S(approval, "recordSha256")));
        var listed = Check.Ok(Check.Tools(f.Root).List("candidate"));
        Check.Equal(selected == "complete" ? "complete" : "incomplete", Check.S(listed["rows"]![0]!, "state"));
        string reference = Check.S(listed["rows"]![0]!, "recordRef");
        string candidatePath = Path.Combine(f.Root, "translation-workflow", "candidate", reference.Split(':')[2], "candidate.xlsx");
        byte[]? before = File.Exists(candidatePath) ? File.ReadAllBytes(candidatePath) : null;
        if (selected == "complete")
        {
            var repeated = f.Candidate(review);
            Check.Equal(reference, Check.Ref(repeated));
        }
        else
        {
            Check.Error("TranslationPublicationIncomplete", f.Tools.Candidate(Check.Ref(review), Check.Digest(review), Check.S(approval, "recordSha256")));
            Check.Equal("incomplete", Check.S(Check.Ok(f.Tools.Reopen(Check.Ref(review)))["candidate"]!, "state"));
            var fresh = f.Prepare(Check.Ok(f.Tools.Reopen(Check.S(review["metadata"]!, "captureRef"))), [f.Edit("K-002", "Neu")]);
            Check.True(Check.Ref(f.Candidate(fresh)) != reference);
        }
        if (before is not null) Check.Bytes(before, File.ReadAllBytes(candidatePath));
    });
}
foreach (string kind in new[] { "capture", "review", "approval", "reconciliation" })
{
    string selected = kind;
    Test("incomplete " + selected + " publication is discoverable but unusable", () =>
    {
        var f = New(); var capture = f.Capture(); var review = f.Prepare(capture, [f.Edit("K-002", "Neu")]);
        var store = new TranslationWorkflowStore(f.Root, null, p => { if (p == selected + ":before-receipt") throw new IOException("injected"); });
        Check.Reject("TranslationPublicationIncomplete", () =>
        {
            _ = selected switch
            {
                "capture" => store.Capture(f.WorkbookUpload, f.ProfileUpload, f.ProjectUpload),
                "review" => store.Prepare(Check.Ref(capture), f.Batch([f.Edit("K-002", "Other")])),
                "approval" => store.Approve(Check.Ref(review), Check.Digest(review), "reviewer", true),
                _ => store.Reconcile(Check.Ref(capture), f.WorkbookUpload, Check.Ref(capture))
            };
        });
        var partial = Check.Ok(Check.Tools(f.Root).List(selected))["rows"]!.AsArray().Single(r => Check.S(r!, "state") == "incomplete")!;
        Check.Error("TranslationPublicationIncomplete", f.Tools.Reopen(Check.S(partial, "recordRef")));
    });
}

if (args.Length > 1)
{
    var root = JsonNode.Parse(File.ReadAllText(args[1]))!;
    var cases = root is JsonArray array ? array : root["cases"]!.AsArray();
    foreach (var item in cases)
    {
        var native = item!.DeepClone();
        Test("genuine retained capture through Host: " + Check.S(native, "mode"), () =>
        {
            var f = New(); byte[] bytes = File.ReadAllBytes(Check.S(native, "workbookPath"));
            Check.Equal(Check.S(native, "workbookSha256"), TranslationJson.Sha256(bytes));
            var profile = TranslationJson.Parse<WorkbookProfile>(File.ReadAllText(Check.S(native, "profilePath")));
            var project = TranslationJson.Parse<EngineeringObjectReference>(native["identity"]!.ToJsonString());
            var capture = f.Capture(bytes, profile, project);
            var page = Check.Ok(f.Tools.Inspect(Check.Ref(capture), 1000));
            Check.Equal(native["rows"]!.AsArray().Count, page["rows"]!.AsArray().Count);
            foreach (var expected in native["rows"]!.AsArray())
            {
                var row = page["rows"]!.AsArray().Single(r => Check.S(r!, "nativeKey") == Check.S(expected!, "nativeKey"))!;
                Check.Equal(Check.S(expected!, "source"), Check.S(row["source"]!, "text"));
                Check.Equal(Check.S(expected!, "target"), Check.S(row["target"]!, "text"));
            }
            Check.Bytes(bytes, f.Download(Check.Artifact(capture, "original.xlsx")));
            if (profile.Rows.Length > 0)
            {
                var wb = TranslationWorkbook.Open(bytes, profile, project, TranslationJson.Sha256(bytes));
                var row = profile.Rows[0]; var pair = wb.Read(row.NativeKey, row.Targets[0].Culture);
                var review = f.Prepare(capture, [new(row.NativeKey, row.Targets[0].Culture, TranslationEditKind.Set, "Unqualified write probe", pair.Source.Sha256, pair.Target.Sha256)]);
                Check.True(review["metadata"]!["blockingIssues"]!.GetValue<int>() > 0);
                Check.Error("TranslationReviewBlocked", f.Tools.Approve(Check.Ref(review), Check.Digest(review), "reviewer", true));
            }
        });
    }
}
int failures = 0;
foreach (var test in checks)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); outcomes.Add(new JsonObject { ["name"] = test.Name, ["passed"] = true }); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex); outcomes.Add(new JsonObject { ["name"] = test.Name, ["passed"] = false, ["error"] = ex.ToString() }); }
}
File.WriteAllText(Path.Combine(output, "verification.json"), new JsonObject { ["passed"] = checks.Count - failures, ["failed"] = failures, ["nativeExecuted"] = false, ["checks"] = outcomes }.ToJsonString());
Console.WriteLine($"{checks.Count - failures}/{checks.Count} offline Host translation checks passed; native execution: false");
return failures == 0 ? 0 : 1;

internal sealed class Fixture
{
    internal string Root { get; }
    internal TranslationTools Tools { get; }
    internal ArtifactUploadStore Uploads { get; }
    internal WorkbookProfile Profile { get; }
    internal EngineeringObjectReference Identity { get; } = new(new(@"D:\synthetic\TranslationHost.ap21", null, null, "/project/TranslationHost", "project"), new("capture-1", new string('a', 64)), new string('b', 64));
    internal byte[] Original { get; }
    internal string WorkbookUpload { get; }
    internal string ProfileUpload { get; }
    internal string ProjectUpload { get; }
    internal Fixture(string root)
    {
        Root = root; Directory.CreateDirectory(root); Tools = Check.Tools(root); Uploads = new(root);
        string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures");
        Original = Check.Pack(Directory.GetFiles(fixture, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".json", StringComparison.Ordinal)).ToDictionary(p => Path.GetRelativePath(fixture, p).Replace('\\', '/'), File.ReadAllBytes));
        Profile = TranslationJson.Parse<WorkbookProfile>(File.ReadAllText(Path.Combine(fixture, "profile.json")));
        WorkbookUpload = Upload(Original, "original.xlsx"); ProfileUpload = Upload(Encoding.UTF8.GetBytes(TranslationJson.Node(Profile).ToJsonString()), "profile.json");
        ProjectUpload = Upload(Encoding.UTF8.GetBytes(TranslationJson.Node(Identity).ToJsonString()), "project.json");
    }
    internal string Upload(byte[] bytes, string name)
    {
        var upload = Uploads.Begin(name, bytes.Length, TranslationJson.Sha256(bytes));
        for (int offset = 0; offset < bytes.Length; offset += 65536) Uploads.Write(upload.UploadId, offset, Convert.ToBase64String(bytes, offset, Math.Min(65536, bytes.Length - offset)));
        return Uploads.Commit(upload.UploadId).ArtifactRef!;
    }
    internal string Batch(TranslationEdit[] edits, TranslationReviewOptions? options = null) => Upload(Encoding.UTF8.GetBytes(TranslationJson.Node(new TranslationProposalBatch(edits, options)).ToJsonString()), "proposals.json");
    internal JsonObject Capture(byte[]? bytes = null, WorkbookProfile? profile = null, EngineeringObjectReference? project = null) => Check.Ok(Tools.Capture(
        bytes is null ? WorkbookUpload : Upload(bytes, "current.xlsx"), profile is null ? ProfileUpload : Upload(Encoding.UTF8.GetBytes(TranslationJson.Node(profile).ToJsonString()), "profile.json"),
        project is null ? ProjectUpload : Upload(Encoding.UTF8.GetBytes(TranslationJson.Node(project).ToJsonString()), "project.json")));
    internal JsonObject Prepare(JsonObject capture, TranslationEdit[] edits, TranslationReviewOptions? options = null) => Check.Ok(Tools.Prepare(Check.Ref(capture), Batch(edits, options)));
    internal JsonObject Approve(JsonObject review) => Check.Ok(Tools.Approve(Check.Ref(review), Check.Digest(review), "reviewer", true));
    internal JsonObject Candidate(JsonObject review) => Check.Ok(Tools.Candidate(Check.Ref(review), Check.Digest(review), Check.S(Approve(review), "recordSha256")));
    internal TranslationWorkbook Open(byte[]? bytes = null) => TranslationWorkbook.Open(bytes ?? Original, Profile, Identity, TranslationJson.Sha256(bytes ?? Original));
    internal TranslationEdit Edit(string key, string text, string culture = "de-DE")
    { var pair = Open().Read(key, culture); return new(key, culture, TranslationEditKind.Set, text, pair.Source.Sha256, pair.Target.Sha256); }
    internal byte[] Changed(TranslationEdit[] edits, TranslationReviewOptions? options = null)
    {
        var wb = Open(); var approved = edits.Select(e => e with { Approved = true, Reviewer = "test fixture setup" }).ToArray();
        var report = TranslationReview.Review(wb, wb, approved, options);
        return TranslationReview.BuildCandidate(wb, wb, approved, Check.S(report, "reviewSha256"), options).Bytes;
    }
    internal EngineeringObjectReference NewIdentity() => Identity with { Snapshot = Identity.Snapshot with { ObservationId = "current-2" }, ContentSha256 = new string('c', 64) };
    internal byte[] Download(string reference)
    {
        using var output = new MemoryStream(); long offset = 0;
        while (true)
        {
            var page = Check.Ok(Tools.ReadArtifact(reference, offset));
            output.Write(Convert.FromBase64String(Check.S(page, "data")));
            if (page["complete"]!.GetValue<bool>()) return output.ToArray();
            offset = page["nextOffset"]!.GetValue<long>();
        }
    }
}

internal static class Check
{
    internal static TranslationTools Tools(string root) => new(new("intentionally-absent-native-worker.exe", root, null, 1, false, "intentionally-absent-simulator-worker.exe", null));
    internal static string S(JsonNode node, string name) => node[name]!.GetValue<string>();
    internal static string Ref(JsonObject node) => S(node, "recordRef");
    internal static string Digest(JsonObject node) => S(node["metadata"]!, "reviewSha256");
    internal static string Artifact(JsonObject node, string name) => node["artifacts"]!.AsArray().Select(a => S(a!, "artifactRef")).Single(r => r.EndsWith(":" + name, StringComparison.Ordinal));
    internal static JsonObject Ok(CallToolResult result)
    {
        var json = JsonNode.Parse(((TextContentBlock)result.Content[0]).Text)!.AsObject();
        True(result.IsError != true, json.ToJsonString()); return json;
    }
    internal static void Error(string code, CallToolResult result)
    {
        True(result.IsError == true, "Expected tool error " + code);
        var json = JsonNode.Parse(((TextContentBlock)result.Content[0]).Text)!; Equal(code, S(json["error"]!, "code"));
    }
    internal static void Reject(string code, Action action)
    { try { action(); } catch (BridgeException ex) { Equal(code, ex.Code); return; } throw new Exception("Expected " + code); }
    internal static void True(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    internal static void Equal<T>(T expected, T actual, string message = "") { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], actual [{actual}]. {message}"); }
    internal static void Bytes(byte[] expected, byte[] actual) => True(expected.AsSpan().SequenceEqual(actual), "Bytes differ");
    internal static byte[] Pack(Dictionary<string, byte[]> parts)
    { using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var p in parts.OrderBy(p => p.Key, StringComparer.Ordinal)) { using var stream = zip.CreateEntry(p.Key).Open(); stream.Write(p.Value); } return output.ToArray(); }
    internal static Dictionary<string, byte[]> Unpack(byte[] bytes)
    { using var stream = new MemoryStream(bytes); using var zip = new ZipArchive(stream); return zip.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }); }
    internal static void Preservation(byte[] before, byte[] after, string[] changedCells)
    {
        var oldParts = Unpack(before); var newParts = Unpack(after);
        Equal(string.Join('|', oldParts.Keys.Order()), string.Join('|', newParts.Keys.Order()));
        foreach (var part in oldParts)
        {
            if (part.Key != "xl/worksheets/sheet1.xml") { Bytes(part.Value, newParts[part.Key]); continue; }
            var a = XDocument.Parse(Encoding.UTF8.GetString(part.Value), LoadOptions.PreserveWhitespace);
            var b = XDocument.Parse(Encoding.UTF8.GetString(newParts[part.Key]), LoadOptions.PreserveWhitespace);
            XNamespace ss = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            foreach (var doc in new[] { a, b }) foreach (string cell in changedCells) doc.Descendants(ss + "c").Where(c => (string?)c.Attribute("r") == cell).Remove();
            True(XNode.DeepEquals(a, b), "Unedited XML changed");
        }
    }
}
