using System.Globalization;
using System.Text.Json;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.OpcUa.Runtime;

/// <summary>OPC UA subscription envelopes over the shared hash-linked segment store.</summary>
internal sealed class OpcUaSubscriptionStore : CaptureSegmentStore<OpcUaSubscriptionPlan,OpcUaSubscriptionProgress,OpcUaSubscriptionRow>
{
    private const string Integrity = "UaSubscriptionIntegrityFailure";
    public OpcUaSubscriptionStore(string artifactsRoot):this(artifactsRoot,TimeProvider.System){}
    public OpcUaSubscriptionStore(string artifactsRoot,TimeProvider? timeProvider):base(artifactsRoot,"runtime-ua-subscriptions","ua-subscription",
        name=>(name is "Exists" or "PageLimit" or "FormatInvalid" or "ReferenceInvalid" or "IdentityInvalid" ? "OpcUaSubscription" : "UaSubscription")+name,timeProvider){}
    public Writer Create(OpcUaSubscriptionPlan plan) => Create(plan,(reference,bytes)=>new Writer(this,plan,reference,bytes));
    internal (OpcUaSubscriptionPlan Plan,OpcUaSubscriptionProgress Progress,IEnumerable<OpcUaSubscriptionRow> Rows) ReadSnapshot(string snapshotRef)
    { var snapshot=Load(snapshotRef);return(snapshot.Plan,snapshot.Progress,Rows(snapshot)); }
    public object Export(string snapshotRef, string format, bool live)
    {
        Require(format is "json" or "csv" or "html" or "markdown","OpcUaSubscriptionFormatInvalid"); var snapshot = Load(snapshotRef);
        string? timeline = null;
        if (format is "html" or "markdown")
        {
            Require(snapshot.Progress.Rows <= 10000,"UaSubscriptionLimit");
            timeline = DiagnosticUaTimeline.Export(snapshotRef,JsonSerializer.SerializeToNode(snapshot.Progress,Json)!,
                JsonSerializer.SerializeToNode(Rows(snapshot),Json)!.AsArray(),format=="html");
        }
        return Export(snapshot,format,live,"subscription","sequence,elapsed_ms,observed_utc,event_json",row =>
            [row.Sequence.ToString(CultureInfo.InvariantCulture),row.ElapsedMilliseconds.ToString("R",CultureInfo.InvariantCulture),row.ObservedUtc.ToString("O"),JsonSerializer.Serialize(row.Event,Json)],
            timeline,format=="html"?".html":".md");
    }
    protected override object Summary(Snapshot snapshot, bool live, string? localFailure, bool pinned = false) =>
        new { schemaVersion = Schema(snapshot.Plan,"status"), captureId = snapshot.Plan.CaptureId, snapshotRef = snapshot.Reference,
            state = State(snapshot,live,pinned), recordedProgress = snapshot.Progress, localFailure, plan = snapshot.Plan, committedBytes = snapshot.Bytes, uncommittedTailPresent = UncommittedTail(snapshot),
            uncommittedDataUnknown = snapshot.Progress.State == "running", acquisitionMode = "subscription",
            reconnectPolicy = snapshot.Plan.Request.MaximumReconnectAttempts>0?"boundedSameSession":"none", transferPolicy = snapshot.Plan.Request.MaximumReconnectAttempts>0?"transferThenRecreateAfterSessionLoss":"none", sourceCompletenessVerified = false, physicalQualified = false,
            atomicAcrossSignals = false, scanSynchronized = false, programMatchVerified = false, nativeQualified = false };
    protected override string Schema(OpcUaSubscriptionPlan plan, string kind) => $"opcua-runtime-subscription-{kind}.v1";
    protected override object Omitted(OpcUaSubscriptionRow row) => new { row.Sequence, row.Event.Kind, omitted = true, reason = "Row exceeds page budget; use exact capture export." };
    protected override string CaptureId(OpcUaSubscriptionPlan plan) => plan.CaptureId;
    protected override long MaximumBytes(OpcUaSubscriptionPlan plan) => plan.Request.MaximumBytes;
    protected override void ValidatePlan(OpcUaSubscriptionPlan plan) => OpcUaSubscriptionContracts.ValidatePlan(plan);
    protected override bool CanonicalPlan(OpcUaSubscriptionPlan plan) => true;
    protected override (string State, double Elapsed, long Rows) Facts(OpcUaSubscriptionProgress progress) => (progress.State,progress.ElapsedMilliseconds,progress.Rows);
    protected override OpcUaSubscriptionProgress Initial(OpcUaSubscriptionPlan plan) => new("running",null,0,plan.CreatedUtc,0,0,0,0,false);
    protected override long Sequence(OpcUaSubscriptionRow row) => row.Sequence;
    // Tallies: [0] notifications, [1] duplicate messages, [2] missing sequences, [3] confirmed remote cleanup.
    protected override void ValidateRow(OpcUaSubscriptionPlan plan, OpcUaSubscriptionRow row, long sequence, long[] tallies)
    {
        OpcUaSubscriptionContracts.ValidateRow(plan,row); Require(row.Sequence == sequence,Integrity);
        tallies[0] += OpcUaSubscriptionContracts.Count(row.Event);
        tallies[1] += row.Event.Duplicate ? 1 : 0; tallies[2] += row.Event.MissingCount;
        if (Cleanup(row)) tallies[3] = 1;
    }
    protected override void ValidateTallies(OpcUaSubscriptionPlan plan, OpcUaSubscriptionProgress progress, long[] tallies) =>
        Require(progress.Notifications == tallies[0] && progress.DuplicateMessages == tallies[1] && progress.MissingSequences == tallies[2] && progress.RemoteCleanupConfirmed == (tallies[3] == 1) && tallies[0] <= plan.Request.MaximumNotifications,Integrity);
    private static bool Cleanup(OpcUaSubscriptionRow row) => row.Event.Kind=="deleted" && row.Event.StatusCode=="0x00000000";
    protected override void ValidateSnapshot(Snapshot snapshot)
    {
        var state=new OpcUaSubscriptionTimeline(snapshot.Plan);
        double previousProgress=0;
        foreach(var reference in snapshot.Segments)
        {
            var (segment,raw)=ReadSegment(snapshot,reference);
            using var original=JsonDocument.Parse(raw);
            Require(JsonElement.DeepEquals(original.RootElement,JsonSerializer.SerializeToElement(segment,Json)),Integrity);
            state.BeginSegment();
            foreach(var row in segment.Rows)state.Apply(row);
            var p=segment.Progress;
            Require(p.Rows==state.Rows && p.Notifications==state.Notifications && p.DuplicateMessages==state.Duplicates &&
                p.MissingSequences==state.Missing && p.RemoteCleanupConfirmed==state.Cleanup &&
                p.ElapsedMilliseconds>=state.Elapsed && p.ElapsedMilliseconds>=previousProgress,Integrity);
            state.ValidateTerminal(p);
            previousProgress=p.ElapsedMilliseconds;
        }
    }
    protected override void ValidateProgress(OpcUaSubscriptionProgress p)
    {
        Require(p.Rows is >= 0 and <= 200000 && p.Notifications is >= 0 and <= 100000 && p.DuplicateMessages>=0 && p.MissingSequences>=0 &&
            double.IsFinite(p.ElapsedMilliseconds) && p.ElapsedMilliseconds>=0 &&
            (p.State=="running" && p.TerminalReason is null || p.State=="ended" && p.TerminalReason is
                "durationLimit" or "notificationLimit" or "sessionUnavailable" or "byteLimit" or "valueLimit" or "storageFailure" or "stopped" or "hostShutdown" or "sequenceGapLimit" or "serverStatusChanged" or "allItemsFailed"),Integrity);
    }
    public sealed class Writer : SegmentWriter
    {
        private readonly OpcUaSubscriptionPlan plan;
        public long Notifications { get; private set; }
        public long DuplicateMessages { get; private set; }
        public long MissingSequences { get; private set; }
        public bool RemoteCleanupConfirmed { get; private set; }
        internal Writer(OpcUaSubscriptionStore store,OpcUaSubscriptionPlan plan,string reference,long storedBytes):base(store,plan,reference,storedBytes) { this.plan=plan; }
        public void Append(OpcUaSubscriptionRow row)
        {
            Require(!Ended && row.Sequence==Rows && Rows<199999 && Notifications+OpcUaSubscriptionContracts.Count(row.Event)<=plan.Request.MaximumNotifications,"UaSubscriptionStateInvalid");
            OpcUaSubscriptionContracts.ValidateRow(plan,row);
            Add(row,row.ElapsedMilliseconds,()=>
            {
                Notifications+=OpcUaSubscriptionContracts.Count(row.Event);
                DuplicateMessages+=row.Event.Duplicate?1:0; MissingSequences+=row.Event.MissingCount;
                RemoteCleanupConfirmed |= Cleanup(row);
            },row.Event.Kind=="created");
        }
        protected override OpcUaSubscriptionProgress Progress(string state,string? reason,double elapsed,DateTimeOffset observed) =>
            new(state,reason,elapsed,observed,Rows,Notifications,DuplicateMessages,MissingSequences,RemoteCleanupConfirmed);
    }
}
