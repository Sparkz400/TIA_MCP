using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.OpcUa.Runtime;

/// <summary>Immutable hash-linked segments. A head names a committed prefix; uncommitted tails are never adopted.</summary>
internal sealed class OpcUaSubscriptionStore : IDisposable
{
    public const long MaximumCaptureBytes = 64L << 20, MaximumStoreBytes = 512L << 20;
    private const int SegmentBytes = 1 << 20, PlanBytes = 128 << 10;
    private sealed record Segment(int Version, int Index, string PreviousSha256, OpcUaSubscriptionProgress Progress, OpcUaSubscriptionRow[] Rows);
    private sealed record SegmentRef(int Index, string Sha256, long FirstRow, int Rows);
    private sealed record Snapshot(string Reference, OpcUaSubscriptionPlan Plan, OpcUaSubscriptionProgress Progress, SegmentRef[] Segments, long Bytes);
    private readonly string root;
    private readonly TimeProvider clock;
    private readonly FileStream owner;
    private readonly object quotaGate = new();
    private readonly object headGate = new();
    private readonly Dictionary<string,long> reservations = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, MaxDepth = 48 };

    public OpcUaSubscriptionStore(string artifactsRoot):this(artifactsRoot,TimeProvider.System){}
    public OpcUaSubscriptionStore(string artifactsRoot,TimeProvider? timeProvider)
    {
        clock=timeProvider??TimeProvider.System;
        root = Path.GetFullPath(Path.Combine(artifactsRoot,"runtime-ua-subscriptions")); Plain(root); Directory.CreateDirectory(root); Plain(root);
        string path = Path.Combine(root,"owner.lock"); Plain(path);
        owner = new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    public bool Exists(string id) => Directory.Exists(PathFor(id));
    public OpcUaSubscriptionPlan Plan(string id) => Decode<OpcUaSubscriptionPlan>(Read(PathFor(id,"plan.json"),PlanBytes));
    private static void ValidatePlan(OpcUaSubscriptionPlan plan) => OpcUaSubscriptionContracts.ValidatePlan(plan);
    public Writer Create(OpcUaSubscriptionPlan plan)
    {
        ValidatePlan(plan);
        byte[] bytes = Encode(plan); Require(bytes.Length <= PlanBytes,"UaSubscriptionLimit");
        lock (quotaGate)
        {
            Require(!Exists(plan.CaptureId),"OpcUaSubscriptionExists"); Capacity(plan.Request.MaximumBytes);
            Require(Directory.EnumerateDirectories(root).Take(65).Count() < 64,"UaSubscriptionCapacity");
            Directory.CreateDirectory(PathFor(plan.CaptureId)); WriteNew(PathFor(plan.CaptureId,"plan.json"),bytes);
            string reference = Reference(plan.CaptureId,0,Hash(bytes)); Head(plan.CaptureId,reference);
            reservations.Add(plan.CaptureId,plan.Request.MaximumBytes);
            return new Writer(this,plan,reference,bytes.Length);
        }
    }
    public string Head(string id) { lock(headGate)return Decode<string>(Read(PathFor(id,"head.json"),512)); }
    public object Status(string id, bool live, string? localFailure = null)
    {
        var snapshot = Load(Head(id)); Require(snapshot.Plan.CaptureId == id,"UaSubscriptionIntegrityFailure");
        return Summary(snapshot,live,localFailure);
    }
    public object Page(string snapshotRef, int offset = 0, int count = 50)
    {
        Require(offset >= 0 && count is >= 1 and <= 200,"OpcUaSubscriptionPageLimit");
        var snapshot = Load(snapshotRef); Require(offset <= snapshot.Progress.Rows,"OpcUaSubscriptionPageLimit");
        var rows = new List<object>(); int used = 0, next = offset;
        foreach (var row in Rows(snapshot,offset))
        {
            object item = row; int size = Encode(row).Length;
            if (size > 240*1024) { item = new { row.Sequence, row.Event.Kind, omitted = true, reason = "Row exceeds page budget; use exact capture export." }; size = Encode(item).Length; }
            if (rows.Count >= count || used + size > 240*1024) break;
            rows.Add(item); used += size; next++;
        }
        return new { schemaVersion = "opcua-runtime-subscription-page.v1", snapshotRef, offset, totalRows = snapshot.Progress.Rows, rows,
            nextOffset = next == snapshot.Progress.Rows ? (int?)null : next, nativeQualified = false };
    }
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
        lock (quotaGate)
        {
            Capacity((snapshot.Plan.Request.MaximumBytes+PlanBytes) * (format == "csv" ? 3 : 1) + (timeline is null ? 0 : Encoding.UTF8.GetByteCount(timeline)));
            string exportId = Guid.NewGuid().ToString("N"); var artifacts = new List<object>();
            string jsonPath = PathFor(snapshot.Plan.CaptureId,exportId+".json");
            using (var output = New(jsonPath))
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject(); writer.WriteString("schemaVersion","opcua-runtime-subscription-export.v1");
                writer.WritePropertyName("subscription"); JsonSerializer.Serialize(writer,Summary(snapshot,live,null,true),Json);
                writer.WritePropertyName("rows"); writer.WriteStartArray();
                foreach (var row in Rows(snapshot)) { JsonSerializer.Serialize(writer,row,Json); writer.Flush(); Require(output.Length <= MaximumCaptureBytes+PlanBytes,"UaSubscriptionCapacity"); }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush(); output.Flush(true);
            }
            artifacts.Add(Artifact(jsonPath));
            if (format == "csv")
            {
                string csvPath = PathFor(snapshot.Plan.CaptureId,exportId+".csv");
                using (var output = New(csvPath))
                using (var writer = new StreamWriter(output,new UTF8Encoding(false)))
                {
                    writer.WriteLine("sequence,elapsed_ms,observed_utc,event_json");
                    foreach (var row in Rows(snapshot))
                    {
                        string[] cells = [row.Sequence.ToString(CultureInfo.InvariantCulture),
                            row.ElapsedMilliseconds.ToString("R",CultureInfo.InvariantCulture),row.ObservedUtc.ToString("O"),JsonSerializer.Serialize(row.Event,Json)];
                        writer.WriteLine(string.Join(',',cells.Select(x=>"\""+x.Replace("\"","\"\"")+"\""))); writer.Flush();
                        Require(output.Length <= 2*(MaximumCaptureBytes+PlanBytes),"UaSubscriptionCapacity");
                    }
                    output.Flush(true);
                }
                artifacts.Add(Artifact(csvPath));
            }
            if (timeline is not null)
            {
                string timelinePath=PathFor(snapshot.Plan.CaptureId,exportId+(format=="html"?".html":".md"));
                WriteNew(timelinePath,Encoding.UTF8.GetBytes(timeline)); artifacts.Add(Artifact(timelinePath));
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
        return new { schemaVersion = "opcua-runtime-subscription-status.v1", captureId = snapshot.Plan.CaptureId, snapshotRef = snapshot.Reference,
            state, recordedProgress = snapshot.Progress, localFailure, plan = snapshot.Plan, committedBytes = snapshot.Bytes, uncommittedTailPresent = tail,
            uncommittedDataUnknown = snapshot.Progress.State == "running", acquisitionMode = "subscription",
            reconnectPolicy = snapshot.Plan.Request.MaximumReconnectAttempts>0?"boundedSameSession":"none", transferPolicy = "none", sourceCompletenessVerified = false, physicalQualified = false,
            atomicAcrossSignals = false, scanSynchronized = false, programMatchVerified = false, nativeQualified = false };
    }
    private Snapshot Load(string reference)
    {
        var parts = reference.Split(':'); Require(parts.Length == 4 && parts[0] == "ua-subscription","OpcUaSubscriptionReferenceInvalid");
        string id = parts[1]; Id(id); Digest(parts[3]);
        Require(int.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out int count) && count is >= 0 and <= 100001 && parts[2] == count.ToString(CultureInfo.InvariantCulture),"OpcUaSubscriptionReferenceInvalid");
        byte[] planBytes = Read(PathFor(id,"plan.json"),PlanBytes); var plan = Decode<OpcUaSubscriptionPlan>(planBytes);
        Require(plan.CaptureId == id,"UaSubscriptionIntegrityFailure"); ValidatePlan(plan);
        using var originalPlan = JsonDocument.Parse(planBytes);
        Require(JsonElement.DeepEquals(originalPlan.RootElement,JsonSerializer.SerializeToElement(plan,Json)),"UaSubscriptionIntegrityFailure");
        string expected = parts[3]; long bytes = planBytes.Length, nextSequence = -1; OpcUaSubscriptionProgress? last = null;
        var segments = new List<SegmentRef>(); long notifications = 0, duplicates = 0, missing = 0; bool cleanup = false;
        for (int index = count; index > 0; index--)
        {
            var raw = Read(PathFor(id,SegmentName(index)),SegmentBytes); bytes += raw.Length;
            Require(bytes <= plan.Request.MaximumBytes && Hash(raw) == expected,"UaSubscriptionIntegrityFailure");
            var segment = Decode<Segment>(raw); Digest(segment.PreviousSha256); ValidateProgress(segment.Progress);
            Require(segment.Version == 1 && segment.Index == index && segment.Rows.Length <= 128 &&
                segment.Progress.State is "running" or "ended" && double.IsFinite(segment.Progress.ElapsedMilliseconds) && segment.Progress.ElapsedMilliseconds >= 0,"UaSubscriptionIntegrityFailure");
            last ??= segment.Progress; long first = segment.Progress.Rows-segment.Rows.Length;
            Require(first >= 0 && (nextSequence < 0 || segment.Progress.Rows == nextSequence) && (index == count || segment.Progress.State == "running"),"UaSubscriptionIntegrityFailure");
            for (int i = 0; i < segment.Rows.Length; i++)
            {
                var row = segment.Rows[i]; ValidateRow(plan,row); Require(row.Sequence == first+i,"UaSubscriptionIntegrityFailure");
                notifications += OpcUaSubscriptionContracts.Count(row.Event);
                duplicates += row.Event.Duplicate ? 1 : 0; missing += row.Event.MissingCount;
                cleanup |= row.Event.Kind=="deleted" && row.Event.StatusCode=="0x00000000";
            }
            segments.Add(new(index,expected,first,segment.Rows.Length)); expected = segment.PreviousSha256; nextSequence = first;
        }
        Require(Hash(planBytes) == expected && (count == 0 || nextSequence == 0),"UaSubscriptionIntegrityFailure");
        var progress = last ?? new("running",null,0,plan.CreatedUtc,0,0,0,0,false);
        Require(progress.Notifications == notifications && progress.DuplicateMessages == duplicates && progress.MissingSequences == missing && progress.RemoteCleanupConfirmed == cleanup && notifications <= plan.Request.MaximumNotifications,"UaSubscriptionIntegrityFailure");
        segments.Reverse();
        var snapshot=new Snapshot(reference,plan,progress,segments.ToArray(),bytes);
        ValidateTimeline(snapshot);
        return snapshot;
    }
    private void ValidateTimeline(Snapshot snapshot)
    {
        var state=new OpcUaSubscriptionTimeline(snapshot.Plan);
        double previousProgress=0;
        foreach(var reference in snapshot.Segments)
        {
            byte[] raw=Read(PathFor(snapshot.Plan.CaptureId,SegmentName(reference.Index)),SegmentBytes);
            Require(Hash(raw)==reference.Sha256,"UaSubscriptionIntegrityFailure");
            var segment=Decode<Segment>(raw);
            using var original=JsonDocument.Parse(raw);
            Require(JsonElement.DeepEquals(original.RootElement,JsonSerializer.SerializeToElement(segment,Json)),"UaSubscriptionIntegrityFailure");
            state.BeginSegment();
            foreach(var row in segment.Rows)state.Apply(row);
            var p=segment.Progress;
            Require(p.Rows==state.Rows && p.Notifications==state.Notifications && p.DuplicateMessages==state.Duplicates &&
                p.MissingSequences==state.Missing && p.RemoteCleanupConfirmed==state.Cleanup &&
                p.ElapsedMilliseconds>=state.Elapsed && p.ElapsedMilliseconds>=previousProgress,"UaSubscriptionIntegrityFailure");
            state.ValidateTerminal(p);
            previousProgress=p.ElapsedMilliseconds;
        }
    }
    private static void ValidateProgress(OpcUaSubscriptionProgress p)
    {
        Require(p.Rows is >= 0 and <= 200000 && p.Notifications is >= 0 and <= 100000 && p.DuplicateMessages>=0 && p.MissingSequences>=0 &&
            double.IsFinite(p.ElapsedMilliseconds) && p.ElapsedMilliseconds>=0 &&
            (p.State=="running" && p.TerminalReason is null || p.State=="ended" && p.TerminalReason is
                "durationLimit" or "notificationLimit" or "sessionUnavailable" or "byteLimit" or "valueLimit" or "storageFailure" or "stopped" or "hostShutdown" or "sequenceGapLimit" or "serverStatusChanged" or "allItemsFailed"),"UaSubscriptionIntegrityFailure");
    }
    private static void ValidateRow(OpcUaSubscriptionPlan p,OpcUaSubscriptionRow r) => OpcUaSubscriptionContracts.ValidateRow(p,r);
    private IEnumerable<OpcUaSubscriptionRow> Rows(Snapshot snapshot, int offset = 0)
    {
        foreach (var reference in snapshot.Segments)
        {
            if (reference.FirstRow+reference.Rows <= offset) continue;
            var raw = Read(PathFor(snapshot.Plan.CaptureId,SegmentName(reference.Index)),SegmentBytes);
            Require(Hash(raw) == reference.Sha256,"UaSubscriptionIntegrityFailure");
            foreach (var row in Decode<Segment>(raw).Rows) if (row.Sequence >= offset) yield return row;
        }
    }
    private void Capacity(long additional)
    {
        long bytes = additional+reservations.Values.Sum(); int entries = 0;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory)) foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            Plain(path); Require(++entries <= 100000,"UaSubscriptionCapacity");
            if (Directory.Exists(path)) pending.Push(path); else bytes += new FileInfo(path).Length;
            Require(bytes <= MaximumStoreBytes,"UaSubscriptionCapacity");
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
    private static string Reference(string id,int count,string hash) => $"ua-subscription:{id}:{count}:{hash}";
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value,Json);
    private static T Decode<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes,Json) ?? throw Error("UaSubscriptionIntegrityFailure");
    private static byte[] Read(string path,int limit) { Plain(path); using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete); Require(input.Length<=limit,"UaSubscriptionLimit");byte[] bytes=new byte[(int)input.Length];input.ReadExactly(bytes);return bytes; }
    private static FileStream New(string path) { Plain(path); return new(path,FileMode.CreateNew,FileAccess.Write,FileShare.None); }
    private static void WriteNew(string path,byte[] bytes) { using var stream=New(path);stream.Write(bytes);stream.Flush(true); }
    private static void Plain(string path) => _ = EngineeringResourceKey.ForPath("workspace",Path.GetFullPath(path));
    private static void Id(string id) => Require(TiaMcp.Core.Security.ProtectedCredentialGuard.IsId(id),"OpcUaSubscriptionIdentityInvalid");
    private static void Digest(string hash) => Require(hash is { Length:64 } && hash.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f'),"OpcUaSubscriptionReferenceInvalid");
    private static void Require(bool value,string code) { if (!value) throw Error(code); }
    private static BridgeException Error(string code) => new(code,"Capture request, retained integrity or capacity check failed. No target activity is resumed.");
    public void Dispose() => owner.Dispose();
    public sealed class Writer
    {
        private readonly OpcUaSubscriptionStore store; private readonly OpcUaSubscriptionPlan plan;
        private readonly List<OpcUaSubscriptionRow> pending = [];
        private int index, pendingBytes; private long storedBytes; private double lastPublication;
        private bool ended;
        public string Reference { get; private set; }
        public long Rows { get; private set; }
        public long Notifications { get; private set; }
        public long DuplicateMessages { get; private set; }
        public long MissingSequences { get; private set; }
        public bool RemoteCleanupConfirmed { get; private set; }
        internal Writer(OpcUaSubscriptionStore store,OpcUaSubscriptionPlan plan,string reference,long storedBytes)
        { this.store=store;this.plan=plan;Reference=reference;this.storedBytes=storedBytes; }
        public void Append(OpcUaSubscriptionRow row)
        {
            Require(!ended && row.Sequence==Rows && Rows<199999 && Notifications+OpcUaSubscriptionContracts.Count(row.Event)<=plan.Request.MaximumNotifications,"UaSubscriptionStateInvalid");
            ValidateRow(plan,row);
            int size=Encode(row).Length; Require(size<=SegmentBytes-8192,"UaSubscriptionValueLimit");
            if (pending.Count==128 || pendingBytes+size>SegmentBytes-8192) Publish("running",null,row.ElapsedMilliseconds);
            Require(storedBytes+pendingBytes+size+16384<=plan.Request.MaximumBytes,"UaSubscriptionByteLimit");
            pending.Add(row);pendingBytes+=size;Rows++;Notifications+=OpcUaSubscriptionContracts.Count(row.Event);
            DuplicateMessages+=row.Event.Duplicate?1:0; MissingSequences+=row.Event.MissingCount;
            RemoteCleanupConfirmed |= row.Event.Kind=="deleted" && row.Event.StatusCode=="0x00000000";
            if(row.Event.Kind=="created" || row.ElapsedMilliseconds-lastPublication>=1000) Publish("running",null,row.ElapsedMilliseconds);
        }
        public void Flush(double elapsed) {if(pending.Count>0)Publish("running",null,elapsed);}
        public void Finish(string reason,double elapsed)
        {
            if(ended)return;
            try { Publish("ended",reason,elapsed); }
            finally { ended=true;lock(store.quotaGate)store.reservations.Remove(plan.CaptureId); }
        }
        private void Publish(string state,string? reason,double elapsed)
        {
            Require(store.Head(plan.CaptureId)==Reference,"UaSubscriptionRecordChanged");
            var progress=new OpcUaSubscriptionProgress(state,reason,elapsed,store.clock.GetUtcNow(),Rows,Notifications,DuplicateMessages,MissingSequences,RemoteCleanupConfirmed);
            ValidateProgress(progress);
            var segment=new Segment(1,index+1,Reference.Split(':')[3],progress,pending.ToArray());byte[] bytes=Encode(segment);
            Require(bytes.Length<=SegmentBytes && storedBytes+bytes.Length+4096<=plan.Request.MaximumBytes,"UaSubscriptionByteLimit");
            string temporary=store.PathFor(plan.CaptureId,Guid.NewGuid().ToString("N")+".tmp");WriteNew(temporary,bytes);
            string destination=store.PathFor(plan.CaptureId,SegmentName(index+1));File.Move(temporary,destination);
            string reference=OpcUaSubscriptionStore.Reference(plan.CaptureId,index+1,Hash(bytes));store.Head(plan.CaptureId,reference);
            Reference=reference;index++;storedBytes+=bytes.Length;pending.Clear();pendingBytes=0;lastPublication=elapsed;
        }
    }
}
