using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.PhysicalRuntime;

public sealed record RuntimeCaptureRequest(string AttemptId, string[] SelectionIds, int IntervalMilliseconds,
    int DurationSeconds, int MaximumSamples = 100000, long MaximumBytes = 64L << 20, string? ReviewId = null, string Representation = "simple",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RuntimeCaptureAssociationRequest? Association = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HandoffRef = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SimulatorCaptureSource? Simulator = null);
/// <summary>Selection is the Web symbol; it is null for PLCSIM signals, whose identity is the plan's request.simulator.</summary>
public sealed record RuntimeCaptureSignal(string SelectionId, WebRuntimeSymbol? Selection, JsonElement Metadata);
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


/// <summary>Polling captures (Web, PLCSIM and OPC UA reads) over the shared hash-linked segment store.</summary>
public sealed class RuntimeCaptureStore : CaptureSegmentStore<RuntimeCapturePlan,RuntimeCaptureProgress,RuntimeCaptureRow>
{
    public RuntimeCaptureStore(string artifactsRoot):this(artifactsRoot,TimeProvider.System){}
    public RuntimeCaptureStore(string artifactsRoot,TimeProvider? timeProvider):base(artifactsRoot,"runtime-captures","capture",name=>"RuntimeCapture"+name,timeProvider){}
    public static void Validate(RuntimeCaptureRequest request)
    {
        Id(request.AttemptId,"RuntimeCaptureIdentityInvalid");
        Require(request.SelectionIds is { Length: >= 1 and <= 64 } && request.SelectionIds.Distinct(StringComparer.Ordinal).Count() == request.SelectionIds.Length,
            "RuntimeCaptureSelectionInvalid");
        foreach (string id in request.SelectionIds) Id(id,"RuntimeCaptureIdentityInvalid");
        Require(request.IntervalMilliseconds is >= 100 and <= 60000 && request.DurationSeconds is >= 1 and <= 3600 &&
            request.MaximumSamples is >= 1 and <= 100000 && request.MaximumBytes is >= (1L << 20) and <= MaximumCaptureBytes,"RuntimeCaptureLimit");
        if (request.ReviewId is not null) Digest(request.ReviewId,"RuntimeCaptureReferenceInvalid");
        Require(request.Representation is "simple" or "raw","RuntimeCaptureSelectionInvalid");
        RuntimeCaptureAssociations.ValidateRequest(request);
        RuntimeCaptureHandoffs.ValidateRequest(request);
        SimulatorCaptureContracts.ValidateRequest(request);
    }
    // A plan retained before RFC 8785 re-derives its selection metadata hashes in the form it was written with.
    protected override void ValidatePlan(RuntimeCapturePlan plan) => CanonicalJson.Compatible(() => { ValidatePlanExact(plan); return true; });
    private static void ValidatePlanExact(RuntimeCapturePlan plan)
    {
        Id(plan.CaptureId,"RuntimeCaptureIdentityInvalid"); Id(plan.HostId,"RuntimeCaptureIdentityInvalid"); Validate(plan.Request);
        if (plan.UaContext is not null) { OpcUaCaptureContracts.ValidatePlan(plan); return; }
        if (plan.Request.Simulator is not null) { SimulatorCaptureContracts.ValidatePlan(plan); RuntimeCaptureAssociations.ValidateRetained(plan); return; }
        Require(plan.Signals.Length == plan.Request.SelectionIds.Length && plan.Signals.Select(x=>x.SelectionId).SequenceEqual(plan.Request.SelectionIds),"RuntimeCaptureSelectionInvalid");
        foreach (var signal in plan.Signals)
        {
            var selected=signal.Selection;
            Require(selected is not null && selected.Context.ConnectionId == plan.Request.AttemptId && selected.Representation == plan.Request.Representation &&
                WebRuntimeProtocol.Symbol(selected.Context,selected.Symbol,selected.Representation,signal.Metadata) == selected,"RuntimeCaptureSelectionInvalid");
        }
        RuntimeCaptureAssociations.ValidateRetained(plan);
        RuntimeCaptureHandoffs.ValidateRetained(plan);
    }
    public Writer Create(RuntimeCapturePlan plan) => Create(plan,(reference,bytes)=>new Writer(this,plan,reference,bytes));
    internal (RuntimeCapturePlan Plan,RuntimeCaptureProgress Progress,IEnumerable<RuntimeCaptureRow> Rows) ReadSnapshot(string snapshotRef)
    { var snapshot=Load(snapshotRef);return(snapshot.Plan,snapshot.Progress,Rows(snapshot)); }
    public object Export(string snapshotRef, string format, bool live)
    {
        Require(format is "json" or "csv","RuntimeCaptureFormatInvalid"); var snapshot = Load(snapshotRef);
        bool recovery=snapshot.Plan.UaContext?.Request.MaximumReconnectAttempts>0;
        return Export(snapshot,format,live,"capture",
            "sequence,kind,selection_id,cycle,scheduled_ms,elapsed_ms,observed_utc,error_code,skipped_polls,read_result_json"+(recovery?",reconnect_json":""),row =>
            {
                string[] cells = [row.Sequence.ToString(CultureInfo.InvariantCulture),row.Kind,row.SelectionId??"",row.Cycle.ToString(CultureInfo.InvariantCulture),
                    row.ScheduledMilliseconds.ToString("R",CultureInfo.InvariantCulture),row.ElapsedMilliseconds.ToString("R",CultureInfo.InvariantCulture),row.ObservedUtc.ToString("O"),
                    row.ErrorCode??"",row.SkippedPolls.ToString(CultureInfo.InvariantCulture),row.ReadResultJson??""];
                return recovery?[..cells,row.Reconnect is null?"":JsonSerializer.Serialize(row.Reconnect,Json)]:cells;
            });
    }
    protected override object Summary(Snapshot snapshot, bool live, string? localFailure, bool pinned = false) =>
        new { schemaVersion = Schema(snapshot.Plan,"status"), captureId = snapshot.Plan.CaptureId, snapshotRef = snapshot.Reference,
            state = State(snapshot,live,pinned), recordedProgress = snapshot.Progress, localFailure, plan = snapshot.Plan, committedBytes = snapshot.Bytes, uncommittedTailPresent = UncommittedTail(snapshot),
            uncommittedDataUnknown = snapshot.Progress.State == "running", plcChangesBetweenPollsUnknown = true, acquisitionMode = "polling",
            atomicAcrossSignals = false, scanSynchronized = false, programMatchVerified = false, nativeQualified = false };
    protected override string Schema(RuntimeCapturePlan plan, string kind) => plan.UaContext is null ? $"runtime-capture-{kind}.v1" : $"opcua-runtime-capture-{kind}.v1";
    protected override object Omitted(RuntimeCaptureRow row) => new { row.Sequence, row.Kind, row.SelectionId, omitted = true, reason = "Row exceeds page budget; use exact capture export." };
    protected override string CaptureId(RuntimeCapturePlan plan) => plan.CaptureId;
    protected override long MaximumBytes(RuntimeCapturePlan plan) => plan.Request.MaximumBytes;
    protected override bool CanonicalPlan(RuntimeCapturePlan plan) => plan.UaContext is not null;
    protected override (string State, double Elapsed, long Rows) Facts(RuntimeCaptureProgress progress) => (progress.State,progress.ElapsedMilliseconds,progress.Rows);
    protected override RuntimeCaptureProgress Initial(RuntimeCapturePlan plan) => new("running",null,0,plan.CreatedUtc,0,0,0,0);
    protected override long Sequence(RuntimeCaptureRow row) => row.Sequence;
    // Tallies: [0] read attempts, [1] skipped polls.
    protected override void ValidateRow(RuntimeCapturePlan plan, RuntimeCaptureRow row, long sequence, long[] tallies)
    {
        ValidateRow(plan,row); Require(row.Sequence == sequence && row.Kind is "read" or "gap" && double.IsFinite(row.ElapsedMilliseconds) && row.ElapsedMilliseconds >= 0 && row.SkippedPolls >= 0,"RuntimeCaptureIntegrityFailure");
        if (row.Kind == "read")
        {
            Require(plan.Request.SelectionIds.Contains(row.SelectionId) && (row.ReadResultJson is null) != (row.ErrorCode is null) && row.SkippedPolls == 0,"RuntimeCaptureIntegrityFailure"); tallies[0]++;
        }
        else tallies[1] += row.SkippedPolls;
    }
    protected override void ValidateTallies(RuntimeCapturePlan plan, RuntimeCaptureProgress progress, long[] tallies) =>
        Require(progress.ReadAttempts == tallies[0] && progress.SkippedPolls == tallies[1] && tallies[0] + progress.DroppedReadResults <= plan.Request.MaximumSamples,"RuntimeCaptureIntegrityFailure");
    protected override void ValidateSnapshot(Snapshot snapshot)
    { if(snapshot.Plan.UaContext?.Request.MaximumReconnectAttempts>0)OpcUaCaptureContracts.ValidateReconnectRows(snapshot.Plan,Rows(snapshot)); }
    protected override void ValidateProgress(RuntimeCaptureProgress progress)
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
        if (plan.Request.Simulator is not null) { SimulatorCaptureContracts.ValidateRead(plan.Request,row.SelectionId!,row.ErrorCode,row.ReadResultJson); return; }
        if(row.ErrorCode is not null)
        {
            Require(row.ErrorCode is "RuntimeTimeout" or "RuntimeTransportFailure" or "RuntimeCancelled" or "RuntimePermissionDenied" or
                "RuntimeSessionUnavailable" or "RuntimeSessionExpired" or "RuntimeSessionIdentityMismatch" or "RuntimeSymbolMetadataChanged" or
                "RuntimeRecordChanged" or "CredentialRetired" or "RuntimeCaptureHandoffMismatch" or "RuntimeCaptureHandoffUnavailable" or "RuntimeCaptureReadFailed","RuntimeCaptureIntegrityFailure");return;
        }
        var result = Decode<WebRuntimeReadResult>(Encoding.UTF8.GetBytes(row.ReadResultJson!),"RuntimeCaptureIntegrityFailure");
        Id(result.RequestId,"RuntimeCaptureIdentityInvalid");
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
    public static long NextCycle(long previous,double elapsed,int interval)
    {
        Require(previous is >= 0 and <= 36000 && double.IsFinite(elapsed) && elapsed >= 0 && elapsed <= 3600000 && interval is >= 100 and <= 60000,"RuntimeCaptureTimingInvalid");
        return Math.Max(previous+1,(long)Math.Floor(elapsed/interval)+1);
    }

    public sealed class Writer : SegmentWriter
    {
        private readonly RuntimeCapturePlan plan;
        public long ReadAttempts { get; private set; }
        public long SkippedPolls { get; private set; }
        public long DroppedReadResults { get; private set; }
        public void RecordDroppedResult() => DroppedReadResults++;
        internal Writer(RuntimeCaptureStore store,RuntimeCapturePlan plan,string reference,long storedBytes):base(store,plan,reference,storedBytes) { this.plan=plan; }
        public void Append(RuntimeCaptureRow row)
        {
            Require(!Ended && row.Sequence==Rows && (row.Kind != "read" || ReadAttempts+DroppedReadResults < plan.Request.MaximumSamples),"RuntimeCaptureStateInvalid");
            ValidateRow(plan,row);
            Add(row,row.ElapsedMilliseconds,()=>{if(row.Kind=="read")ReadAttempts++;else SkippedPolls+=row.SkippedPolls;});
        }
        protected override RuntimeCaptureProgress Progress(string state,string? reason,double elapsed,DateTimeOffset observed) =>
            new(state,reason,elapsed,observed,Rows,ReadAttempts,SkippedPolls,DroppedReadResults);
    }
}
