using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

/// <summary>Internal owning-worker binding; no request can supply its admission delegate.</summary>
internal sealed class TranslationWorkerAdapter(IBridgeClient bridge, string root, string projectPath, string plcPath,
    Action<TranslationImportContext, string> admission, string sourceCulture = "de-DE", string targetCulture = "en-GB", string[]? plcPaths = null) : ITranslationImportAdapter
{
    private readonly TranslationImportArtifacts artifacts = new(root);
    private object Selector => new { projectPath, plcPath, plcPaths = plcPaths ?? [plcPath], sourceCulture, targetCulture, artifactDirectory = root };
    internal Action<string>? AfterNativeReturn { get; set; }
    internal bool DriftSourceAfterCheckpoint { get; set; }
    public void RequireQualification(TranslationImportContext context, string action) => admission(context, action);
    internal async Task<(TranslationImportObservation Observation, WorkbookProfile Profile)> Capture(CancellationToken token = default) =>
        Decode(await bridge.CallAsync("translation_capture", Selector, token));
    public async Task<ITranslationImportSession> OpenExclusive(TranslationImportContext context, CancellationToken token)
    {
        RequireQualification(context, "openExclusive");
        var opened = JsonNode.Parse((await bridge.CallAsync("translation_begin", new { context, selector = Selector }, token)).GetRawText())!;
        Require(opened["operationId"]!.GetValue<string>() == context.Operation.Id && opened["planSha256"]!.GetValue<string>() == context.Operation.PlanSha256,
            "InvalidLease", "Worker did not acknowledge the exact translation operation.");
        return new Session(this, context, opened["leaseToken"]!.GetValue<string>());
    }
    internal byte[] ReadNative(JsonNode reference)
    { byte[] bytes = ReadArtifact(root, reference); artifacts.RetainBytes(bytes); return bytes; }
    internal static byte[] ReadArtifact(string root, JsonNode reference)
    {
        string path = Path.GetFullPath(reference["path"]!.GetValue<string>());
        Require(path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "InvalidArtifact", "Native evidence must remain inside the admitted artifact root.");
        TranslationRetention.NoLinks(path);
        Require(new FileInfo(path).Length <= TranslationRetention.WorkbookLimit, "TranslationArtifactLimit", "Native artifact exceeds bound.");
        byte[] bytes = File.ReadAllBytes(path);
        Require(Sha256(bytes) == reference["sha256"]!.GetValue<string>(), "TranslationArtifactChanged", "Native artifact changed.");
        return bytes;
    }
    internal (TranslationImportObservation, WorkbookProfile) Decode(JsonElement element)
    {
        var value = JsonNode.Parse(element.GetRawText())!;
        artifacts.RetainBytes(Encoding.UTF8.GetBytes(element.GetRawText()));
        byte[] bytes = ReadNative(value["workbook"]!);
        var profile = NativeProfile(bytes);
        string path = value["projectPath"]!.GetValue<string>(), name = value["projectName"]!.GetValue<string>();
        var provisional = new EngineeringObjectReference(new(path, null, null, "/project/" + name, "project"), new(Guid.NewGuid().ToString("N"), new('0', 64)), new('0', 64));
        var workbook = TranslationWorkbook.Open(bytes, profile, provisional, Sha256(bytes));
        string content = TranslationCategoryScope.NativeContentSha256(workbook);
        var reference = provisional with { Snapshot = provisional.Snapshot with { SourceSha256 = content }, ContentSha256 = content };
        var peers = value["peers"]!.AsArray().Select(p => {
            var evidence = artifacts.RetainBytes(ReadNative(p!["artifact"]!));
            return new TranslationImportPeer(p["identity"]!.GetValue<string>(), evidence.Sha256, evidence, p["profile"]!.GetValue<string>());
        }).ToArray();
        Require(profile.SourceCulture == sourceCulture && TranslationCategoryScope.NativeTargetCulture(workbook) == targetCulture,
            "TranslationScopeMismatch", "Exported workbook cultures differ from the requested source/target pair.");
        var scope = new TranslationImportScope(sourceCulture, targetCulture, value["activeCultures"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(),
            ["whole-project:legacy-bilingual"], value["categoryIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        var target = new NativeTargetObservation(reference, path, value["sessionEpoch"]!.GetValue<string>(), value["workerSha256"]!.GetValue<string>(),
            HashJson(profile), value["hardwareIdentity"]!.GetValue<string>(), name, value["offline"]!.GetValue<bool>(),
            [new("translation.languages", "observed", HashJson(scope.ActiveCultures)), new("translation.scope", "observed", HashJson(scope)),
                new("translation.preservation", "observed", HashJson(peers))]);
        var receipt = JsonBytes(new TranslationObservationReceipt(target, Sha256(bytes), HashJson(scope), HashJson(peers)));
        return (new(target, bytes, scope, peers, receipt), profile);
    }
    // Cultures come from the native header row: E1 is "<source>*", then one column per target until ref="0".
    internal static WorkbookProfile NativeProfile(byte[] bytes, string id = "tia21-whole-project-bilingual-qualification-v1")
    {
        var package = new BoundedOpcPackage(bytes, new()); var s = BoundedOpcPackage.Sheet;
        var strings = package.Xml["xl/sharedStrings.xml"].Root!.Elements(s + "si").ToArray();
        string Cell(System.Xml.Linq.XElement row, string column)
        {
            string address = column + (string)row.Attribute("r")!;
            var cell = row.Elements(s + "c").Single(c => (string?)c.Attribute("r") == address);
            Require((string?)cell.Attribute("t") == "s", "UnsupportedTranslationScopeLayout", "Native capture requires shared-string cells.");
            var text = strings[int.Parse(cell.Element(s + "v")!.Value, System.Globalization.CultureInfo.InvariantCulture)];
            Require(text.Elements().Count() == 1 && text.Element(s + "t") != null, "UnsupportedTranslationScopeLayout", "Plain native cells required.");
            return text.Element(s + "t")!.Value;
        }
        var header = package.Xml["xl/worksheets/sheet1.xml"].Descendants(s + "row").First();
        string source = Cell(header, "E");
        Require(source.EndsWith('*'), "UnsupportedTranslationScopeLayout", "Source culture header must end with '*'.");
        var targets = new List<string>();
        for (string name; !(name = Cell(header, TranslationWorkbook.ColumnName(6 + targets.Count))).StartsWith("ref=\"", StringComparison.Ordinal);)
        { targets.Add(name); Require(targets.Count <= 64, "UnsupportedTranslationScopeLayout", "At most 64 target culture columns are supported."); }
        var rows = new List<TranslationRow>();
        for (int i = 1; i <= 2; i++)
        {
            string part = $"xl/worksheets/sheet{i}.xml";
            foreach (var row in package.Xml[part].Descendants(s + "row").Skip(1))
            {
                string number = (string)row.Attribute("r")!;
                rows.Add(new(Cell(row, "C"), Cell(row, "A"), new(part, "C" + number), new(part, "E" + number),
                    targets.Select((culture, index) => new TranslationCell(culture, new(part, TranslationWorkbook.ColumnName(6 + index) + number))).ToArray(), "brace-literals-v1"));
            }
        }
        return new(id, "unqualified-explicit", source[..^1], rows.ToArray(), AllowSharedStringToInline: true);
    }
    internal static byte[] JsonBytes<T>(T value) => Encoding.UTF8.GetBytes(CanonicalJson.Serialize(Node(value)));
    private sealed class Session(TranslationWorkerAdapter owner, TranslationImportContext context, string lease) : ITranslationImportSession
    {
        private bool closed, failed;
        private int checkpoint, import, restore, sourceDrift;
        private TranslationImportObservation? baseline;
        private readonly SemaphoreSlim serial = new(1, 1);
        private string ProjectKey => context.Operation.Plan.Resources.Single().ResourceKey;
        private async Task<JsonElement> Call(string stage, object? payload, CancellationToken token)
        {
            await serial.WaitAsync(token);
            try
            {
                Require(!closed && !failed, "RecoveryRequired", "Closed or interrupted native translation session cannot be reused.");
                var op = new EngineeringOperationStore(context.ArtifactRoot).Read(context.Operation.Id);
                var reply = await owner.bridgeCall(new { stage, payload, leaseToken = lease, operation = op }, token);
                owner.artifacts.RetainBytes(Encoding.UTF8.GetBytes(reply.GetRawText()));
                owner.AfterNativeReturn?.Invoke(stage);
                return reply.GetProperty("value").Clone();
            }
            catch { failed = true; throw; }
            finally { serial.Release(); }
        }
        private static void Once(ref int value)
        { Require(Interlocked.Exchange(ref value, 1) == 0, "RecoveryRequired", "A native attempt cannot be replayed."); }
        public async Task<TranslationImportObservation> Observe(CancellationToken token)
        {
            if (owner.DriftSourceAfterCheckpoint && checkpoint != 0 && sourceDrift == 0)
            { Once(ref sourceDrift); await Call("sourceDrift", null, CancellationToken.None); }
            var value = owner.Decode(await Call("observe", null, token)).Item1; baseline ??= value; return value;
        }
        public async Task<TranslationImportCheckpoint> Checkpoint(CancellationToken token)
        {
            Once(ref checkpoint);
            byte[] bytes = owner.ReadNative(JsonNode.Parse((await Call("checkpoint", null, CancellationToken.None)).GetRawText())!);
            Require(baseline != null, "TranslationCheckpointMismatch", "Fresh baseline observation required.");
            return new(bytes, JsonBytes(new TranslationCheckpointReceipt(context.Operation.Id, context.Operation.PlanSha256, ProjectKey,
                baseline!.Target.SessionEpoch, Sha256(bytes), baseline.Target.Reference.ContentSha256!)));
        }
        public async Task<TranslationImportReturn> Import(byte[] exactCandidate, CancellationToken token)
        {
            Once(ref import); Require(Sha256(exactCandidate) == context.CandidateSha256, "TranslationArtifactChanged", "Candidate changed.");
            string folder = Path.Combine(context.ArtifactRoot, "translation-native", "candidate-" + context.Operation.Id);
            Directory.CreateDirectory(folder); string path = Path.Combine(folder, "candidate.xlsx");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(exactCandidate); file.Flush(true); }
            var value = JsonNode.Parse((await Call("import", new { path }, CancellationToken.None)).GetRawText())!;
            byte[] raw = owner.ReadNative(value["raw"]!), log = owner.ReadNative(value["log"]!);
            // Preserve the original vendor result before reading its fields. The durable
            // result is the typed operation binding; the original remains content-addressed.
            var returned = JsonNode.Parse(raw)!;
            bool accepted = returned["state"]!.GetValue<string>() == "Info" && returned["updateSourceLanguage"]!.GetValue<bool>() == context.UpdateSourceLanguage &&
                returned["operationId"]!.GetValue<string>() == context.Operation.Id && returned["planSha256"]!.GetValue<string>() == context.Operation.PlanSha256 &&
                returned["candidateSha256"]!.GetValue<string>() == context.CandidateSha256;
            return new(JsonBytes(new TranslationImportResultReceipt(context.Operation.Id, context.Operation.PlanSha256, ProjectKey,
                context.CandidateSha256, accepted, log.Length > 0, accepted ? 0 : 1, Sha256(log))), log);
        }
        private byte[] Step(string name, bool passed, JsonElement detail) => JsonBytes(new TranslationImportStepReceipt(context.Operation.Id,
            context.Operation.PlanSha256, ProjectKey, name, passed ? "passed" : "failed", detail.GetRawText()));
        public async Task<byte[]> Compile(CancellationToken token)
        {
            var result = await Call("compile", null, CancellationToken.None);
            return Step("compile", result.GetProperty("passed").GetBoolean(), result);
        }
        public async Task<byte[]> Save(CancellationToken token)
        { var value = await Call("save", null, CancellationToken.None); return Step("save", value.GetProperty("saved").GetBoolean() && !value.GetProperty("modified").GetBoolean(), value); }
        public async Task<TranslationImportObservation> Reopen(CancellationToken token) => owner.Decode(await Call("reopen", null, token)).Item1;
        public async Task<byte[]> Restore(byte[] archive, EngineeringRecoveryDestination destination, CancellationToken token)
        {
            Once(ref restore);
            string folder = Path.Combine(context.ArtifactRoot, "translation-native", "restore-" + context.Operation.Id); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "checkpoint.zap21");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(archive); file.Flush(true); }
            return Encoding.UTF8.GetBytes((await Call("restore", new { archive = path, sha256 = Sha256(archive), destination = destination.RecoveryLocation }, CancellationToken.None)).GetRawText());
        }
        public async Task<TranslationRecoveryReadback> ObserveRecovered(EngineeringRecoveryDestination destination, bool freshReopen, CancellationToken token)
        {
            var value = await Call("recovered", new { destination = destination.RecoveryLocation, freshReopen }, token);
            if (value.TryGetProperty("absent", out var absent) && absent.GetBoolean())
                return new(null, JsonBytes(new TranslationRecoveryAbsenceReceipt(context.Operation.Id, context.Operation.PlanSha256, ProjectKey,
                    destination.RecoveryLocation, context.Operation.Plan.Target.AdapterSha256, "absent", value.GetRawText())));
            return new(owner.Decode(value).Item1, null);
        }
        public async Task<byte[]> Close(CancellationToken token)
        {
            await DisposeAsync();
            return JsonBytes(new TranslationImportStepReceipt(context.Operation.Id, context.Operation.PlanSha256, ProjectKey,
                "cleanup", "passed", "Exact owning-worker native lease released; coordinator separately verifies worker and Portal lifetimes."));
        }
        public async ValueTask DisposeAsync()
        {
            await serial.WaitAsync(CancellationToken.None);
            try
            {
                if (closed) return; closed = true;
                var value = await owner.endCall(new { leaseToken = lease, operationId = context.Operation.Id });
                Require(value.GetProperty("released").GetBoolean() && value.GetProperty("operationId").GetString() == context.Operation.Id,
                    "NativeCleanupUnverified", "Exact worker lease release was not proved.");
            }
            finally { serial.Release(); }
        }
    }
    private Task<JsonElement> bridgeCall(object args, CancellationToken token) => bridge.CallAsync("translation_stage", args, token);
    private Task<JsonElement> endCall(object args) => bridge.CallAsync("translation_end", args, CancellationToken.None);
}
