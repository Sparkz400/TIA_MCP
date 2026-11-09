using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.PhysicalRuntime;

/// <summary>Immutable hash-linked segments. A head names a committed prefix; uncommitted tails are never adopted.
/// Derived stores supply plan, progress and row validation; error codes come from <c>code(name)</c>.</summary>
public abstract class CaptureSegmentStore<TPlan,TProgress,TRow> : IDisposable where TPlan : class where TProgress : class where TRow : class
{
    public const long MaximumCaptureBytes = 64L << 20, MaximumStoreBytes = 512L << 20;
    protected const int SegmentBytes = 1 << 20, PlanBytes = 128 << 10;
    protected sealed record Segment(int Version, int Index, string PreviousSha256, TProgress Progress, TRow[] Rows);
    protected sealed record SegmentRef(int Index, string Sha256, long FirstRow, int Rows);
    protected sealed record Snapshot(string Reference, TPlan Plan, TProgress Progress, SegmentRef[] Segments, long Bytes);
    private sealed record Verified(Snapshot Snapshot, (long Length, DateTime Written)[] Files);
    private readonly string root, referencePrefix;
    private readonly Func<string,string> code;
    private readonly TimeProvider clock;
    private readonly FileStream owner;
    private readonly object quotaGate = new();
    private readonly object headGate = new();
    private readonly Dictionary<string,long> reservations = new(StringComparer.Ordinal);
    private readonly LinkedList<Verified> verified = new();
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, MaxDepth = 48 };

    protected CaptureSegmentStore(string artifactsRoot, string directory, string referencePrefix, Func<string,string> code, TimeProvider? timeProvider)
    {
        clock=timeProvider??TimeProvider.System; this.referencePrefix=referencePrefix; this.code=code;
        root = Path.GetFullPath(Path.Combine(artifactsRoot,directory)); Plain(root); Directory.CreateDirectory(root); Plain(root);
        string path = Path.Combine(root,"owner.lock"); Plain(path);
        owner = new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    protected abstract string CaptureId(TPlan plan);
    protected abstract long MaximumBytes(TPlan plan);
    protected abstract void ValidatePlan(TPlan plan);
    /// <summary>True when retained plan bytes must equal their re-serialized form.</summary>
    protected abstract bool CanonicalPlan(TPlan plan);
    protected abstract void ValidateProgress(TProgress progress);
    protected abstract (string State, double Elapsed, long Rows) Facts(TProgress progress);
    protected abstract TProgress Initial(TPlan plan);
    /// <summary>Validates one retained row at its expected sequence and adds it to the store's tallies.</summary>
    protected abstract void ValidateRow(TPlan plan, TRow row, long sequence, long[] tallies);
    protected abstract void ValidateTallies(TPlan plan, TProgress progress, long[] tallies);
    protected virtual void ValidateSnapshot(Snapshot snapshot) { }
    protected abstract long Sequence(TRow row);
    protected abstract string Schema(TPlan plan, string kind);
    protected abstract object Omitted(TRow row);
    protected abstract object Summary(Snapshot snapshot, bool live, string? localFailure, bool pinned = false);

    public bool Exists(string id) => Directory.Exists(PathFor(id));
    public TPlan Plan(string id) => Decode<TPlan>(Read(PathFor(id,"plan.json"),PlanBytes),Code("IntegrityFailure"));
    protected T Create<T>(TPlan plan, Func<string,long,T> writer)
    {
        ValidatePlan(plan);
        byte[] bytes = Encode(plan); Require(bytes.Length <= PlanBytes,Code("Limit"));
        string id = CaptureId(plan);
        lock (quotaGate)
        {
            Require(!Exists(id),Code("Exists")); Capacity(MaximumBytes(plan));
            Require(Directory.EnumerateDirectories(root).Take(65).Count() < 64,Code("Capacity"));
            Directory.CreateDirectory(PathFor(id)); WriteNew(PathFor(id,"plan.json"),bytes);
            string reference = Reference(id,0,Hash(bytes)); Head(id,reference);
            reservations.Add(id,MaximumBytes(plan));
            return writer(reference,bytes.Length);
        }
    }
    public string Head(string id) { lock(headGate)return Decode<string>(Read(PathFor(id,"head.json"),512),Code("IntegrityFailure")); }
    public object Status(string id, bool live, string? localFailure = null)
    {
        var snapshot = Load(Head(id)); Require(CaptureId(snapshot.Plan) == id,Code("IntegrityFailure"));
        return Summary(snapshot,live,localFailure);
    }
    public object Page(string snapshotRef, int offset = 0, int count = 50)
    {
        Require(offset >= 0 && count is >= 1 and <= 200,Code("PageLimit"));
        var snapshot = Load(snapshotRef); long total = Facts(snapshot.Progress).Rows; Require(offset <= total,Code("PageLimit"));
        var rows = new List<object>(); int used = 0, next = offset;
        foreach (var row in Rows(snapshot,offset))
        {
            object item = row; int size = Encode(row).Length;
            if (size > 240*1024) { item = Omitted(row); size = Encode(item).Length; }
            if (rows.Count >= count || used + size > 240*1024) break;
            rows.Add(item); used += size; next++;
        }
        return new { schemaVersion = Schema(snapshot.Plan,"page"), snapshotRef, offset, totalRows = total, rows,
            nextOffset = next == total ? (int?)null : next, nativeQualified = false };
    }
    /// <summary>Writes the JSON export, an optional CSV companion and an optional timeline; each is published by rename.</summary>
    protected object Export(Snapshot snapshot, string format, bool live, string section, string csvHeader, Func<TRow,string[]> csvCells, string? timeline = null, string? timelineExtension = null)
    {
        lock (quotaGate)
        {
            Capacity((MaximumBytes(snapshot.Plan)+PlanBytes) * (format == "csv" ? 3 : 1) + (timeline is null ? 0 : Encoding.UTF8.GetByteCount(timeline)));
            string id = CaptureId(snapshot.Plan), exportId = Guid.NewGuid().ToString("N"); var artifacts = new List<object>();
            // Taken before the export's own temporary file exists, which would otherwise read as an uncommitted tail.
            byte[] summary = Encode(Summary(snapshot,live,null,true));
            artifacts.Add(Commit(id,exportId+".json",output =>
            {
                using var writer = new Utf8JsonWriter(output);
                writer.WriteStartObject(); writer.WriteString("schemaVersion",Schema(snapshot.Plan,"export"));
                writer.WritePropertyName(section); writer.WriteRawValue(summary,skipInputValidation:true);
                writer.WritePropertyName("rows"); writer.WriteStartArray();
                foreach (var row in Rows(snapshot)) { JsonSerializer.Serialize(writer,row,Json); writer.Flush(); Require(output.Length <= MaximumCaptureBytes+PlanBytes,Code("Capacity")); }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
            }));
            if (format == "csv") artifacts.Add(Commit(id,exportId+".csv",output =>
            {
                using var writer = new StreamWriter(output,new UTF8Encoding(false),leaveOpen:true);
                writer.WriteLine(csvHeader);
                foreach (var row in Rows(snapshot))
                {
                    writer.WriteLine(string.Join(',',csvCells(row).Select(x=>"\""+x.Replace("\"","\"\"")+"\""))); writer.Flush();
                    Require(output.Length <= 2*(MaximumCaptureBytes+PlanBytes),Code("Capacity"));
                }
                writer.Flush();
            }));
            if (timeline is not null) artifacts.Add(Commit(id,exportId+timelineExtension,output => output.Write(Encoding.UTF8.GetBytes(timeline))));
            return new { snapshotRef = snapshot.Reference, artifacts, nativeQualified = false, csvCompanionJson = format == "csv" };
        }
    }
    // A failed export never leaves a final-named partial file; an undeletable leftover keeps its .tmp name and shows as an uncommitted tail.
    private object Commit(string id, string name, Action<FileStream> write)
    {
        string path = PathFor(id,name), temporary = PathFor(id,name+".tmp");
        try
        {
            using (var output = New(temporary)) { write(output); output.Flush(true); }
            File.Move(temporary,path);
        }
        catch { try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } throw; }
        return Artifact(path);
    }
    protected string State(Snapshot snapshot, bool live, bool pinned)
    {
        string state = Facts(snapshot.Progress).State;
        return pinned && snapshot.Reference != Head(CaptureId(snapshot.Plan)) ? "historicalPrefix" : state == "running" && !live ? "interrupted" : state;
    }
    protected bool UncommittedTail(Snapshot snapshot) =>
        Directory.EnumerateFiles(PathFor(CaptureId(snapshot.Plan))).Take(100001).Any(p => Path.GetFileName(p).EndsWith(".tmp",StringComparison.Ordinal) ||
            (Path.GetFileName(p).EndsWith(".segment",StringComparison.Ordinal) && int.TryParse(Path.GetFileNameWithoutExtension(p),out int n) && n > snapshot.Segments.Length));
    /// <summary>The reference pins every byte through the hash chain, so a verified snapshot is reused while the
    /// referenced files keep their length and write time; any on-disk edit forces full re-verification.</summary>
    protected Snapshot Load(string reference)
    {
        var parts = reference.Split(':'); Require(parts.Length == 4 && parts[0] == referencePrefix,Code("ReferenceInvalid"));
        string id = parts[1]; Id(id,Code("IdentityInvalid")); Digest(parts[3],Code("ReferenceInvalid"));
        Require(int.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out int count) && count is >= 0 and <= 100001 && parts[2] == count.ToString(CultureInfo.InvariantCulture),Code("ReferenceInvalid"));
        // Stamped before verification, so a concurrent edit can only cause a later miss, never a stale hit.
        var files = new (long,DateTime)[count+1];
        for (int i = 0; i <= count; i++) { var info = new FileInfo(PathFor(id,i == 0 ? "plan.json" : SegmentName(i))); files[i] = info.Exists ? (info.Length,info.LastWriteTimeUtc) : (-1,default); }
        lock (verified)
            for (var node = verified.First; node is not null; node = node.Next)
                if (node.Value.Snapshot.Reference == reference && node.Value.Files.AsSpan().SequenceEqual(files))
                { verified.Remove(node); verified.AddFirst(node); return node.Value.Snapshot; }
        var snapshot = Verify(reference,id,count,parts[3]);
        lock (verified) { verified.AddFirst(new Verified(snapshot,files)); if (verified.Count > 8) verified.RemoveLast(); }
        return snapshot;
    }
    private Snapshot Verify(string reference, string id, int count, string expected)
    {
        string integrity = Code("IntegrityFailure");
        byte[] planBytes = Read(PathFor(id,"plan.json"),PlanBytes); var plan = Decode<TPlan>(planBytes,integrity);
        Require(CaptureId(plan) == id,integrity); ValidatePlan(plan);
        if (CanonicalPlan(plan))
        {
            using var originalPlan = JsonDocument.Parse(planBytes);
            Require(JsonElement.DeepEquals(originalPlan.RootElement,JsonSerializer.SerializeToElement(plan,Json)),integrity);
        }
        long bytes = planBytes.Length, nextSequence = -1; TProgress? last = null;
        var segments = new List<SegmentRef>(); var tallies = new long[4];
        for (int index = count; index > 0; index--)
        {
            var raw = Read(PathFor(id,SegmentName(index)),SegmentBytes); bytes += raw.Length;
            Require(bytes <= MaximumBytes(plan) && Hash(raw) == expected,integrity);
            var segment = Decode<Segment>(raw,integrity); Digest(segment.PreviousSha256,Code("ReferenceInvalid")); ValidateProgress(segment.Progress);
            var facts = Facts(segment.Progress);
            Require(segment.Version == 1 && segment.Index == index && segment.Rows.Length <= 128 &&
                facts.State is "running" or "ended" && double.IsFinite(facts.Elapsed) && facts.Elapsed >= 0,integrity);
            last ??= segment.Progress; long first = facts.Rows-segment.Rows.Length;
            Require(first >= 0 && (nextSequence < 0 || facts.Rows == nextSequence) && (index == count || facts.State == "running"),integrity);
            for (int i = 0; i < segment.Rows.Length; i++) ValidateRow(plan,segment.Rows[i],first+i,tallies);
            segments.Add(new(index,expected,first,segment.Rows.Length)); expected = segment.PreviousSha256; nextSequence = first;
        }
        Require(Hash(planBytes) == expected && (count == 0 || nextSequence == 0),integrity);
        var progress = last ?? Initial(plan);
        ValidateTallies(plan,progress,tallies);
        segments.Reverse(); var snapshot = new Snapshot(reference,plan,progress,segments.ToArray(),bytes);
        ValidateSnapshot(snapshot);
        return snapshot;
    }
    /// <summary>Reads one committed segment and re-checks its hash; the raw bytes allow canonical-form checks.</summary>
    protected (Segment Segment, byte[] Raw) ReadSegment(Snapshot snapshot, SegmentRef reference)
    {
        var raw = Read(PathFor(CaptureId(snapshot.Plan),SegmentName(reference.Index)),SegmentBytes);
        Require(Hash(raw) == reference.Sha256,Code("IntegrityFailure"));
        return (Decode<Segment>(raw,Code("IntegrityFailure")),raw);
    }
    protected IEnumerable<TRow> Rows(Snapshot snapshot, int offset = 0)
    {
        foreach (var reference in snapshot.Segments)
        {
            if (reference.FirstRow+reference.Rows <= offset) continue;
            foreach (var row in ReadSegment(snapshot,reference).Segment.Rows) if (Sequence(row) >= offset) yield return row;
        }
    }
    private void Capacity(long additional)
    {
        long bytes = additional+reservations.Values.Sum(); int entries = 0;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory)) foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            Plain(path); Require(++entries <= 100000,Code("Capacity"));
            if (Directory.Exists(path)) pending.Push(path); else bytes += new FileInfo(path).Length;
            Require(bytes <= MaximumStoreBytes,Code("Capacity"));
        }
    }
    private static object Artifact(string path)
    { using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read); return new { path, bytes=stream.Length, sha256=Convert.ToHexStringLower(SHA256.HashData(stream)) }; }
    private void Head(string id, string reference)
    {
        lock(headGate)
        {
        string path = PathFor(id,"head.json"), temporary = PathFor(id,Guid.NewGuid().ToString("N")+".tmp"); WriteNew(temporary,Encode(reference));
        if (File.Exists(path)) JournalFileReplacement.Replace(temporary,path); else File.Move(temporary,path);
        }
    }
    private string Code(string name) => code(name);
    private string PathFor(string id, string? name = null) { Id(id,Code("IdentityInvalid")); string path = Path.Combine(root,id); if (name is not null) path=Path.Combine(path,name); Plain(path); return path; }
    private static string SegmentName(int index) => index.ToString("D6",CultureInfo.InvariantCulture)+".segment";
    private string Reference(string id,int count,string hash) => $"{referencePrefix}:{id}:{count}:{hash}";
    protected static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    protected static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value,Json);
    protected static T Decode<T>(byte[] bytes,string code) => JsonSerializer.Deserialize<T>(bytes,Json) ?? throw Error(code);
    private byte[] Read(string path,int limit) { Plain(path); using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete); Require(input.Length<=limit,Code("Limit"));byte[] bytes=new byte[(int)input.Length];input.ReadExactly(bytes);return bytes; }
    private static FileStream New(string path) { Plain(path); return new(path,FileMode.CreateNew,FileAccess.Write,FileShare.None); }
    private static void WriteNew(string path,byte[] bytes) { using var stream=New(path);stream.Write(bytes);stream.Flush(true); }
    private static void Plain(string path) => _ = EngineeringResourceKey.ForPath("workspace",Path.GetFullPath(path));
    protected static void Id(string id,string code) => Require(Security.ProtectedCredentialGuard.IsId(id),code);
    protected static void Digest(string hash,string code) => Require(hash is { Length:64 } && hash.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f'),code);
    protected static void Require(bool value,string code) { if (!value) throw Error(code); }
    protected static BridgeException Error(string code) => new(code,"Capture request, retained integrity or capacity check failed. No target activity is resumed.");
    public void Dispose() => owner.Dispose();

    public abstract class SegmentWriter
    {
        private readonly CaptureSegmentStore<TPlan,TProgress,TRow> store; private readonly string id; private readonly long maximumBytes;
        private readonly List<TRow> pending = [];
        private int index, pendingBytes; private long storedBytes; private double lastPublication;
        protected bool Ended { get; private set; }
        public string Reference { get; private set; }
        public long Rows { get; private set; }
        protected SegmentWriter(CaptureSegmentStore<TPlan,TProgress,TRow> store,TPlan plan,string reference,long storedBytes)
        { this.store=store;id=store.CaptureId(plan);maximumBytes=store.MaximumBytes(plan);Reference=reference;this.storedBytes=storedBytes; }
        protected abstract TProgress Progress(string state,string? reason,double elapsed,DateTimeOffset observed);
        /// <summary>Stages a validated row; <paramref name="count"/> updates the derived tallies once the row is accepted.</summary>
        protected void Add(TRow row,double elapsed,Action count,bool publish = false)
        {
            int size=Encode(row).Length; Require(size<=SegmentBytes-8192,store.Code("ValueLimit"));
            if (pending.Count==128 || pendingBytes+size>SegmentBytes-8192) Publish("running",null,elapsed);
            Require(storedBytes+pendingBytes+size+16384<=maximumBytes,store.Code("ByteLimit"));
            pending.Add(row);pendingBytes+=size;Rows++;count();
            if(publish || elapsed-lastPublication>=1000) Publish("running",null,elapsed);
        }
        public void Flush(double elapsed) { if(pending.Count>0)Publish("running",null,elapsed); }
        public void Finish(string reason,double elapsed)
        {
            if(Ended)return;
            try { Publish("ended",reason,elapsed); }
            finally { Ended=true;lock(store.quotaGate)store.reservations.Remove(id); }
        }
        private void Publish(string state,string? reason,double elapsed)
        {
            Require(store.Head(id)==Reference,store.Code("RecordChanged"));
            var progress=Progress(state,reason,elapsed,store.clock.GetUtcNow());
            store.ValidateProgress(progress);
            var segment=new Segment(1,index+1,Reference.Split(':')[3],progress,pending.ToArray());byte[] bytes=Encode(segment);
            Require(bytes.Length<=SegmentBytes && storedBytes+bytes.Length+4096<=maximumBytes,store.Code("ByteLimit"));
            string temporary=store.PathFor(id,Guid.NewGuid().ToString("N")+".tmp");WriteNew(temporary,bytes);
            string destination=store.PathFor(id,SegmentName(index+1));File.Move(temporary,destination);
            string reference=store.Reference(id,index+1,Hash(bytes));store.Head(id,reference);
            Reference=reference;index++;storedBytes+=bytes.Length;pending.Clear();pendingBytes=0;lastPublication=elapsed;
        }
    }
}
