using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.PhysicalRuntime;

public sealed record RuntimeCaptureRequest(string AttemptId, string[] SelectionIds, int IntervalMilliseconds,
    int DurationSeconds, int MaximumSamples = 100000, long MaximumBytes = 64L << 20, string? ReviewId = null, string Representation = "simple",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RuntimeCaptureAssociationRequest? Association = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HandoffRef = null);
public sealed record RuntimeCaptureSignal(string SelectionId, WebRuntimeSymbol Selection, JsonElement Metadata);
public sealed record RuntimeCapturePlan(string CaptureId, string HostId, DateTimeOffset CreatedUtc,
    RuntimeCaptureRequest Request, RuntimeCaptureSignal[] Signals,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RuntimeCaptureAssociation? Association = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RuntimeCaptureHandoff? Handoff = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OpcUaCaptureContext? UaContext = null);
public sealed record RuntimeCaptureRow(long Sequence, string Kind, string? SelectionId, long Cycle,
    double ScheduledMilliseconds, double ElapsedMilliseconds, DateTimeOffset ObservedUtc,
    string? ReadResultJson, string? ErrorCode, long SkippedPolls,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OpcUaReconnectEvent? Reconnect = null);
public sealed record RuntimeCaptureProgress(string State, string? TerminalReason, double ElapsedMilliseconds,
    DateTimeOffset ObservedUtc, long Rows, long ReadAttempts, long SkippedPolls, long DroppedReadResults);

/// <summary>Immutable hash-linked segments. A head names a committed prefix; uncommitted tails are never adopted.</summary>
public sealed class RuntimeCaptureStore : IDisposable
{
    public const long MaximumCaptureBytes = 64L << 20, MaximumStoreBytes = 512L << 20;
    private const int SegmentBytes = 1 << 20, PlanBytes = 128 << 10;
    private sealed record Segment(int Version, int Index, string PreviousSha256, RuntimeCaptureProgress Progress, RuntimeCaptureRow[] Rows);
    private sealed record SegmentRef(int Index, string Sha256, long FirstRow, int Rows);
    private sealed record Snapshot(string Reference, RuntimeCapturePlan Plan, RuntimeCaptureProgress Progress, SegmentRef[] Segments, long Bytes);
    private readonly string root;
    private readonly TimeProvider clock;
    private readonly FileStream owner;
    private readonly object quotaGate = new();
    private readonly object headGate = new();
    private readonly Dictionary<string,long> reservations = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, MaxDepth = 48 };

    public RuntimeCaptureStore(string artifactsRoot):this(artifactsRoot,TimeProvider.System){}
    public RuntimeCaptureStore(string artifactsRoot,TimeProvider? timeProvider)
    {
        clock=timeProvider??TimeProvider.System;
        root = Path.GetFullPath(Path.Combine(artifactsRoot,"runtime-captures")); Plain(root); Directory.CreateDirectory(root); Plain(root);
        string path = Path.Combine(root,"owner.lock"); Plain(path);
        owner = new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    public static void Validate(RuntimeCaptureRequest request)
    {
        Id(request.AttemptId);
        Require(request.SelectionIds is { Length: >= 1 and <= 64 } && request.SelectionIds.Distinct(StringComparer.Ordinal).Count() == request.SelectionIds.Length,
            "RuntimeCaptureSelectionInvalid");
        foreach (string id in request.SelectionIds) Id(id);
        Require(request.IntervalMilliseconds is >= 100 and <= 60000 && request.DurationSeconds is >= 1 and <= 3600 &&
            request.MaximumSamples is >= 1 and <= 100000 && request.MaximumBytes is >= (1L << 20) and <= MaximumCaptureBytes,"RuntimeCaptureLimit");
        if (request.ReviewId is not null) Digest(request.ReviewId);
        Require(request.Representation is "simple" or "raw","RuntimeCaptureSelectionInvalid");
        RuntimeCaptureAssociations.ValidateRequest(request);
        RuntimeCaptureHandoffs.ValidateRequest(request);
    }
    public bool Exists(string id) => Directory.Exists(PathFor(id));
    public RuntimeCapturePlan Plan(string id) => Decode<RuntimeCapturePlan>(Read(PathFor(id,"plan.json"),PlanBytes));
    private static void ValidatePlan(RuntimeCapturePlan plan)
    {
        Id(plan.CaptureId); Id(plan.HostId); Validate(plan.Request);
        if (plan.UaContext is not null) { OpcUaCaptureContracts.ValidatePlan(plan); return; }
        Require(plan.Signals.Length == plan.Request.SelectionIds.Length && plan.Signals.Select(x=>x.SelectionId).SequenceEqual(plan.Request.SelectionIds),"RuntimeCaptureSelectionInvalid");
        foreach (var signal in plan.Signals)
        {
            var selected=signal.Selection;
            Require(selected.Context.ConnectionId == plan.Request.AttemptId && selected.Representation == plan.Request.Representation &&
                WebRuntimeProtocol.Symbol(selected.Context,selected.Symbol,selected.Representation,signal.Metadata) == selected,"RuntimeCaptureSelectionInvalid");
        }
        RuntimeCaptureAssociations.ValidateRetained(plan);
        RuntimeCaptureHandoffs.ValidateRetained(plan);
    }
    public Writer Create(RuntimeCapturePlan plan)
    {
        ValidatePlan(plan);
        byte[] bytes = Encode(plan); Require(bytes.Length <= PlanBytes,"RuntimeCaptureLimit");
        lock (quotaGate)
        {
            Require(!Exists(plan.CaptureId),"RuntimeCaptureExists"); Capacity(plan.Request.MaximumBytes);
            Require(Directory.EnumerateDirectories(root).Take(65).Count() < 64,"RuntimeCaptureCapacity");
            Directory.CreateDirectory(PathFor(plan.CaptureId)); WriteNew(PathFor(plan.CaptureId,"plan.json"),bytes);
            string reference = Reference(plan.CaptureId,0,Hash(bytes)); Head(plan.CaptureId,reference);
            reservations.Add(plan.CaptureId,plan.Request.MaximumBytes);
            return new Writer(this,plan,reference,bytes.Length);
        }
    }
    public string Head(string id) { lock(headGate)return Decode<string>(Read(PathFor(id,"head.json"),512)); }
    public object Status(string id, bool live, string? localFailure = null)
    {
        var snapshot = Load(Head(id)); Require(snapshot.Plan.CaptureId == id,"RuntimeCaptureIntegrityFailure");
        return Summary(snapshot,live,localFailure);
    }
    public object Page(string snapshotRef, int offset = 0, int count = 50)
    {
        Require(offset >= 0 && count is >= 1 and <= 200,"RuntimeCapturePageLimit");
        var snapshot = Load(snapshotRef); Require(offset <= snapshot.Progress.Rows,"RuntimeCapturePageLimit");
        var rows = new List<object>(); int used = 0, next = offset;
        foreach (var row in Rows(snapshot,offset))
        {
            object item = row; int size = Encode(row).Length;
            if (size > 240*1024) { item = new { row.Sequence, row.Kind, row.SelectionId, omitted = true, reason = "Row exceeds page budget; use exact capture export." }; size = Encode(item).Length; }
            if (rows.Count >= count || used + size > 240*1024) break;
            rows.Add(item); used += size; next++;
        }
        return new { schemaVersion = snapshot.Plan.UaContext is null ? "runtime-capture-page.v1" : "opcua-runtime-capture-page.v1", snapshotRef, offset, totalRows = snapshot.Progress.Rows, rows,
            nextOffset = next == snapshot.Progress.Rows ? (int?)null : next, nativeQualified = false };
    }
    internal (RuntimeCapturePlan Plan,RuntimeCaptureProgress Progress,IEnumerable<RuntimeCaptureRow> Rows) ReadSnapshot(string snapshotRef)
    { var snapshot=Load(snapshotRef);return(snapshot.Plan,snapshot.Progress,Rows(snapshot)); }
    public object Export(string snapshotRef, string format, bool live)
    {
        Require(format is "json" or "csv","RuntimeCaptureFormatInvalid"); var snapshot = Load(snapshotRef);
        lock (quotaGate)
        {
            Capacity((snapshot.Plan.Request.MaximumBytes+PlanBytes) * (format == "csv" ? 3 : 1));
            string exportId = Guid.NewGuid().ToString("N"); var artifacts = new List<object>();
            string jsonPath = PathFor(snapshot.Plan.CaptureId,exportId+".json");
            using (var output = New(jsonPath))
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject(); writer.WriteString("schemaVersion",snapshot.Plan.UaContext is null ? "runtime-capture-export.v1" : "opcua-runtime-capture-export.v1");
                writer.WritePropertyName("capture"); JsonSerializer.Serialize(writer,Summary(snapshot,live,null,true),Json);
                writer.WritePropertyName("rows"); writer.WriteStartArray();
                foreach (var row in Rows(snapshot)) { JsonSerializer.Serialize(writer,row,Json); writer.Flush(); Require(output.Length <= MaximumCaptureBytes+PlanBytes,"RuntimeCaptureCapacity"); }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush(); output.Flush(true);
            }
            artifacts.Add(Artifact(jsonPath));
            if (format == "csv")
            {
                string csvPath = PathFor(snapshot.Plan.CaptureId,exportId+".csv");
                using (var output = New(csvPath))
                using (var writer = new StreamWriter(output,new UTF8Encoding(false)))
                {
                    bool recovery=snapshot.Plan.UaContext?.Request.MaximumReconnectAttempts>0;
                    writer.WriteLine("sequence,kind,selection_id,cycle,scheduled_ms,elapsed_ms,observed_utc,error_code,skipped_polls,read_result_json"+(recovery?",reconnect_json":""));
                    foreach (var row in Rows(snapshot))
                    {
                        string[] cells = [row.Sequence.ToString(CultureInfo.InvariantCulture),row.Kind,row.SelectionId??"",row.Cycle.ToString(CultureInfo.InvariantCulture),
                            row.ScheduledMilliseconds.ToString("R",CultureInfo.InvariantCulture),row.ElapsedMilliseconds.ToString("R",CultureInfo.InvariantCulture),row.ObservedUtc.ToString("O"),
                            row.ErrorCode??"",row.SkippedPolls.ToString(CultureInfo.InvariantCulture),row.ReadResultJson??""];
                        if(recovery)cells=[..cells,row.Reconnect is null?"":JsonSerializer.Serialize(row.Reconnect,Json)];
                        writer.WriteLine(string.Join(',',cells.Select(x=>"\""+x.Replace("\"","\"\"")+"\""))); writer.Flush();
                        Require(output.Length <= 2*(MaximumCaptureBytes+PlanBytes),"RuntimeCaptureCapacity");
                    }
                    output.Flush(true);
                }
                artifacts.Add(Artifact(csvPath));
            }
            return new { snapshotRef, artifacts, nativeQualified = false, csvCompanionJson = format == "csv" };
        }
    }
    private object Summary(Snapshot snapshot, bool live, string? localFailure, bool pinned = false)
    {
        string state = pinned && snapshot.Reference != Head(snapshot.Plan.CaptureId) ? "historicalPrefix" :
            snapshot.Progress.State == "running" && !live ? "interrupted" : snapshot.Progress.State;
        string directory = PathFor(snapshot.Plan.CaptureId);
        bool tail = Directory.EnumerateFiles(directory).Take(100001).Any(p => Path.GetFileName(p).EndsWith(".tmp",StringComparison.Ordinal) ||
            (Path.GetFileName(p).EndsWith(".segment",StringComparison.Ordinal) && int.TryParse(Path.GetFileNameWithoutExtension(p),out int n) && n > snapshot.Segments.Length));
        return new { schemaVersion = snapshot.Plan.UaContext is null ? "runtime-capture-status.v1" : "opcua-runtime-capture-status.v1", captureId = snapshot.Plan.CaptureId, snapshotRef = snapshot.Reference,
            state, recordedProgress = snapshot.Progress, localFailure, plan = snapshot.Plan, committedBytes = snapshot.Bytes, uncommittedTailPresent = tail,
            uncommittedDataUnknown = snapshot.Progress.State == "running", plcChangesBetweenPollsUnknown = true, acquisitionMode = "polling",
            atomicAcrossSignals = false, scanSynchronized = false, programMatchVerified = false, nativeQualified = false };
    }
    private Snapshot Load(string reference)
    {
        var parts = reference.Split(':'); Require(parts.Length == 4 && parts[0] == "capture","RuntimeCaptureReferenceInvalid");
        string id = parts[1]; Id(id); Digest(parts[3]);
        Require(int.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out int count) && count is >= 0 and <= 100001 && parts[2] == count.ToString(CultureInfo.InvariantCulture),"RuntimeCaptureReferenceInvalid");
        byte[] planBytes = Read(PathFor(id,"plan.json"),PlanBytes); var plan = Decode<RuntimeCapturePlan>(planBytes);
        Require(plan.CaptureId == id,"RuntimeCaptureIntegrityFailure"); ValidatePlan(plan);
        if (plan.UaContext is not null)
        {
            using var originalPlan = JsonDocument.Parse(planBytes);
            Require(JsonElement.DeepEquals(originalPlan.RootElement,JsonSerializer.SerializeToElement(plan,Json)),"RuntimeCaptureIntegrityFailure");
        }
        string expected = parts[3]; long bytes = planBytes.Length, nextSequence = -1; RuntimeCaptureProgress? last = null;
        var segments = new List<SegmentRef>(); long attempts = 0, skipped = 0;
        for (int index = count; index > 0; index--)
        {
            var raw = Read(PathFor(id,SegmentName(index)),SegmentBytes); bytes += raw.Length;
            Require(bytes <= plan.Request.MaximumBytes && Hash(raw) == expected,"RuntimeCaptureIntegrityFailure");
            var segment = Decode<Segment>(raw); Digest(segment.PreviousSha256); ValidateProgress(segment.Progress);
            Require(segment.Version == 1 && segment.Index == index && segment.Rows.Length <= 128 &&
                segment.Progress.State is "running" or "ended" && double.IsFinite(segment.Progress.ElapsedMilliseconds) && segment.Progress.ElapsedMilliseconds >= 0,"RuntimeCaptureIntegrityFailure");
            last ??= segment.Progress; long first = segment.Progress.Rows-segment.Rows.Length;
            Require(first >= 0 && (nextSequence < 0 || segment.Progress.Rows == nextSequence) && (index == count || segment.Progress.State == "running"),"RuntimeCaptureIntegrityFailure");
            for (int i = 0; i < segment.Rows.Length; i++)
            {
                var row = segment.Rows[i]; ValidateRow(plan,row); Require(row.Sequence == first+i && row.Kind is "read" or "gap" && double.IsFinite(row.ElapsedMilliseconds) && row.ElapsedMilliseconds >= 0 && row.SkippedPolls >= 0,"RuntimeCaptureIntegrityFailure");
                if (row.Kind == "read")
                {
                    Require(plan.Request.SelectionIds.Contains(row.SelectionId) && (row.ReadResultJson is null) != (row.ErrorCode is null) && row.SkippedPolls == 0,"RuntimeCaptureIntegrityFailure"); attempts++;
                }
                else skipped += row.SkippedPolls;
            }
            segments.Add(new(index,expected,first,segment.Rows.Length)); expected = segment.PreviousSha256; nextSequence = first;
        }
        Require(Hash(planBytes) == expected && (count == 0 || nextSequence == 0),"RuntimeCaptureIntegrityFailure");
        var progress = last ?? new("running",null,0,plan.CreatedUtc,0,0,0,0);
        Require(progress.ReadAttempts == attempts && progress.SkippedPolls == skipped && attempts + progress.DroppedReadResults <= plan.Request.MaximumSamples,"RuntimeCaptureIntegrityFailure");
        segments.Reverse(); var snapshot=new Snapshot(reference,plan,progress,segments.ToArray(),bytes);
        if(plan.UaContext?.Request.MaximumReconnectAttempts>0)OpcUaCaptureContracts.ValidateReconnectRows(plan,Rows(snapshot));
        return snapshot;
    }
    private static void ValidateProgress(RuntimeCaptureProgress progress)
    {
        Require(progress.Rows is >= 0 and <= 200000 && progress.ReadAttempts is >= 0 and <= 100000 &&
            progress.SkippedPolls is >= 0 and <= 36000 && progress.DroppedReadResults is >= 0 and <= 1 &&
            double.IsFinite(progress.ElapsedMilliseconds) && progress.ElapsedMilliseconds >= 0 &&
            (progress.State == "running" && progress.TerminalReason is null || progress.State == "ended" &&
                progress.TerminalReason is "durationLimit" or "sampleLimit" or "sessionUnavailable" or "byteLimit" or "valueLimit" or "storageFailure" or "stopped" or "hostShutdown"),"RuntimeCaptureIntegrityFailure");
    }
    private static void ValidateRow(RuntimeCapturePlan plan,RuntimeCaptureRow row)
    {
        Require(row.Sequence >= 0 && row.Cycle is >= 0 and <= 36000 && double.IsFinite(row.ScheduledMilliseconds) &&
            row.ScheduledMilliseconds == row.Cycle * (double)plan.Request.IntervalMilliseconds &&
            double.IsFinite(row.ElapsedMilliseconds) && row.ElapsedMilliseconds >= 0,"RuntimeCaptureIntegrityFailure");
        if(row.Kind == "gap")
        {
            Require(row.SelectionId is null && row.ReadResultJson is null && row.ErrorCode is null,"RuntimeCaptureIntegrityFailure");
            if(row.Reconnect is not null)
            {
                Require(plan.UaContext is not null && row.SkippedPolls==0,"RuntimeCaptureIntegrityFailure");
                OpcUaReconnectHistory.Validate(row.Reconnect,plan.UaContext!.Request.MaximumReconnectAttempts);
            }
            else Require(row.SkippedPolls is > 0 and <= 36000,"RuntimeCaptureIntegrityFailure");
            return;
        }
        Require(row.Reconnect is null && row.Kind == "read" && row.SkippedPolls == 0 && plan.Request.SelectionIds.Contains(row.SelectionId) &&
            (row.ReadResultJson is null) != (row.ErrorCode is null),"RuntimeCaptureIntegrityFailure");
        if (plan.UaContext is not null) { OpcUaCaptureContracts.ValidateRead(plan,row); return; }
        if(row.ErrorCode is not null)
        {
            Require(row.ErrorCode is "RuntimeTimeout" or "RuntimeTransportFailure" or "RuntimeCancelled" or "RuntimePermissionDenied" or
                "RuntimeSessionUnavailable" or "RuntimeSessionExpired" or "RuntimeSessionIdentityMismatch" or "RuntimeSymbolMetadataChanged" or
                "RuntimeRecordChanged" or "CredentialRetired" or "RuntimeCaptureHandoffMismatch" or "RuntimeCaptureHandoffUnavailable" or "RuntimeCaptureReadFailed","RuntimeCaptureIntegrityFailure");return;
        }
        var result = Decode<WebRuntimeReadResult>(Encoding.UTF8.GetBytes(row.ReadResultJson!));
        Id(result.RequestId);
        using var original = JsonDocument.Parse(row.ReadResultJson!);
        Require(result.Selection == plan.Signals.Single(x=>x.SelectionId==row.SelectionId).Selection &&
            double.IsFinite(result.ElapsedMilliseconds) && result.ElapsedMilliseconds >= 0 &&
            JsonElement.DeepEquals(JsonSerializer.SerializeToElement(result,Json),original.RootElement),"RuntimeCaptureIntegrityFailure");
        // Reuse the pure response parser for status/value validation, including raw-byte bounds.
        var request=WebRuntimeProtocol.Read(result.Selection.Context,result.Selection,result.RequestId);
        byte[] response=Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":\""+result.RequestId+"\","+
            (result.ProviderErrorCode is null ? "\"result\":"+(result.Value?.GetRawText()??"null") : "\"error\":{\"code\":"+result.ProviderErrorCode.Value.ToString(CultureInfo.InvariantCulture)+"}")+"}");
        var validated=WebRuntimeProtocol.ReadResult(result.Selection.Context,request,response,result.RequestStartedUtc,result.ResponseReceivedUtc,TimeSpan.FromMilliseconds(result.ElapsedMilliseconds));
        Require(validated.Status==result.Status && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(validated.Value,Json),JsonSerializer.SerializeToElement(result.Value,Json)),"RuntimeCaptureIntegrityFailure");
    }
    private IEnumerable<RuntimeCaptureRow> Rows(Snapshot snapshot, int offset = 0)
    {
        foreach (var reference in snapshot.Segments)
        {
            if (reference.FirstRow+reference.Rows <= offset) continue;
            var raw = Read(PathFor(snapshot.Plan.CaptureId,SegmentName(reference.Index)),SegmentBytes);
            Require(Hash(raw) == reference.Sha256,"RuntimeCaptureIntegrityFailure");
            foreach (var row in Decode<Segment>(raw).Rows) if (row.Sequence >= offset) yield return row;
        }
    }
    private void Capacity(long additional)
    {
        long bytes = additional+reservations.Values.Sum(); int entries = 0;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory)) foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            Plain(path); Require(++entries <= 100000,"RuntimeCaptureCapacity");
            if (Directory.Exists(path)) pending.Push(path); else bytes += new FileInfo(path).Length;
            Require(bytes <= MaximumStoreBytes,"RuntimeCaptureCapacity");
        }
    }
    private object Artifact(string path)
    { using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read); return new { path, bytes=stream.Length, sha256=Convert.ToHexStringLower(SHA256.HashData(stream)) }; }
    private void Head(string id, string reference)
    {
        lock(headGate)
        {
        string path = PathFor(id,"head.json"), temporary = PathFor(id,Guid.NewGuid().ToString("N")+".tmp"); WriteNew(temporary,Encode(reference));
        if (File.Exists(path)) JournalFileReplacement.Replace(temporary,path); else File.Move(temporary,path);
        }
    }
    private string PathFor(string id, string? name = null) { Id(id); string path = Path.Combine(root,id); if (name is not null) path=Path.Combine(path,name); Plain(path); return path; }
    private static string SegmentName(int index) => index.ToString("D6",CultureInfo.InvariantCulture)+".segment";
    private static string Reference(string id,int count,string hash) => $"capture:{id}:{count}:{hash}";
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value,Json);
    private static T Decode<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes,Json) ?? throw Error("RuntimeCaptureIntegrityFailure");
    private static byte[] Read(string path,int limit) { Plain(path); using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete); Require(input.Length<=limit,"RuntimeCaptureLimit");byte[] bytes=new byte[(int)input.Length];input.ReadExactly(bytes);return bytes; }
    private static FileStream New(string path) { Plain(path); return new(path,FileMode.CreateNew,FileAccess.Write,FileShare.None); }
    private static void WriteNew(string path,byte[] bytes) { using var stream=New(path);stream.Write(bytes);stream.Flush(true); }
    private static void Plain(string path) => _ = EngineeringResourceKey.ForPath("workspace",Path.GetFullPath(path));
    private static void Id(string id) => Require(Security.ProtectedCredentialGuard.IsId(id),"RuntimeCaptureIdentityInvalid");
    private static void Digest(string hash) => Require(hash is { Length:64 } && hash.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f'),"RuntimeCaptureReferenceInvalid");
    private static void Require(bool value,string code) { if (!value) throw Error(code); }
    private static BridgeException Error(string code) => new(code,"Capture request, retained integrity or capacity check failed. No target activity is resumed.");
    public void Dispose() => owner.Dispose();
    public static long NextCycle(long previous,double elapsed,int interval)
    {
        Require(previous is >= 0 and <= 36000 && double.IsFinite(elapsed) && elapsed >= 0 && elapsed <= 3600000 && interval is >= 100 and <= 60000,"RuntimeCaptureTimingInvalid");
        return Math.Max(previous+1,(long)Math.Floor(elapsed/interval)+1);
    }

    public sealed class Writer
    {
        private readonly RuntimeCaptureStore store; private readonly RuntimeCapturePlan plan;
        private readonly List<RuntimeCaptureRow> pending = [];
        private int index, pendingBytes; private long storedBytes; private double lastPublication;
        private bool ended;
        public string Reference { get; private set; }
        public long Rows { get; private set; }
        public long ReadAttempts { get; private set; }
        public long SkippedPolls { get; private set; }
        public long DroppedReadResults { get; private set; }
        public void RecordDroppedResult() => DroppedReadResults++;
        internal Writer(RuntimeCaptureStore store,RuntimeCapturePlan plan,string reference,long storedBytes)
        { this.store=store;this.plan=plan;Reference=reference;this.storedBytes=storedBytes; }
        public void Append(RuntimeCaptureRow row)
        {
            Require(!ended && row.Sequence==Rows && (row.Kind != "read" || ReadAttempts+DroppedReadResults < plan.Request.MaximumSamples),"RuntimeCaptureStateInvalid");
            ValidateRow(plan,row);
            int size=Encode(row).Length; Require(size<=SegmentBytes-8192,"RuntimeCaptureValueLimit");
            if (pending.Count==128 || pendingBytes+size>SegmentBytes-8192) Publish("running",null,row.ElapsedMilliseconds);
            Require(storedBytes+pendingBytes+size+16384<=plan.Request.MaximumBytes,"RuntimeCaptureByteLimit");
            pending.Add(row);pendingBytes+=size;Rows++;if(row.Kind=="read")ReadAttempts++;else SkippedPolls+=row.SkippedPolls;
            if(row.ElapsedMilliseconds-lastPublication>=1000) Publish("running",null,row.ElapsedMilliseconds);
        }
        public void Finish(string reason,double elapsed)
        {
            if(ended)return;
            try { Publish("ended",reason,elapsed); }
            finally { ended=true;lock(store.quotaGate)store.reservations.Remove(plan.CaptureId); }
        }
        public void Flush(double elapsed) { if(pending.Count>0)Publish("running",null,elapsed); }
        private void Publish(string state,string? reason,double elapsed)
        {
            Require(store.Head(plan.CaptureId)==Reference,"RuntimeCaptureRecordChanged");
            var progress=new RuntimeCaptureProgress(state,reason,elapsed,store.clock.GetUtcNow(),Rows,ReadAttempts,SkippedPolls,DroppedReadResults);
            ValidateProgress(progress);
            var segment=new Segment(1,index+1,Reference.Split(':')[3],progress,pending.ToArray());byte[] bytes=Encode(segment);
            Require(bytes.Length<=SegmentBytes && storedBytes+bytes.Length+4096<=plan.Request.MaximumBytes,"RuntimeCaptureByteLimit");
            string temporary=store.PathFor(plan.CaptureId,Guid.NewGuid().ToString("N")+".tmp");WriteNew(temporary,bytes);
            string destination=store.PathFor(plan.CaptureId,SegmentName(index+1));File.Move(temporary,destination);
            string reference=RuntimeCaptureStore.Reference(plan.CaptureId,index+1,Hash(bytes));store.Head(plan.CaptureId,reference);
            Reference=reference;index++;storedBytes+=bytes.Length;pending.Clear();pendingBytes=0;lastPublication=elapsed;
        }
    }
}
