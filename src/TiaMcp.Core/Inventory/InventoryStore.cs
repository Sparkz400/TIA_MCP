using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TiaMcp.Core.Inventory;

/// <summary>Bounded immutable observations. Paging never re-reads a changing engineering project.</summary>
public sealed class InventoryStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly Dictionary<string, Snapshot> snapshots = new(StringComparer.Ordinal);
    private const long MemoryLimit = 256L * 1024 * 1024;
    private const int RowLimit = 50000;

    public sealed class Snapshot
    {
        private readonly InventoryMetadata metadata;
        public InventoryMetadata Metadata => metadata with { Coverage = metadata.Coverage with { Warnings = metadata.Coverage.Warnings.ToArray() } };
        internal string[] Lines { get; }
        internal long Bytes { get; }
        internal Snapshot(InventoryMetadata metadata, string[] lines) { this.metadata = metadata; Lines = lines; Bytes = lines.Sum(l => (long)l.Length * 2); }
        public IEnumerable<InventoryRow> ReadRows() => Lines.Select(l => JsonSerializer.Deserialize<InventoryRow>(l, Json)!);
    }

    public Snapshot Capture(IEnumerable<InventoryRow> rows, string projectPath, InventoryCoverage coverage,
        DateTimeOffset capturedAtUtc, string? parentSnapshotId = null)
    {
        var lines = new List<string>(); var ids = new HashSet<string>(StringComparer.Ordinal); long bytes = 0;
        foreach (var row in rows)
        {
            Validate(row, projectPath);
            if (!ids.Add(row.RowId)) throw new BridgeException("InventoryDuplicateIdentity", "The capture contains duplicate native paths.");
            var line = JsonSerializer.Serialize(row.Compare(), Json);
            bytes += line.Length * 2L;
            if (lines.Count >= RowLimit || bytes > MemoryLimit / 2 || Encoding.UTF8.GetByteCount(line) > 128 * 1024)
                throw new BridgeException("InventoryLimit", "Capture exceeds 50000 rows, 128 MiB memory, or 128 KiB per row; narrow the project scope or lower maxVisited.");
            lines.Add(line);
        }
        var metadata = new InventoryMetadata(Guid.NewGuid().ToString("N"), capturedAtUtc.ToUniversalTime(), projectPath, lines.Count,
            coverage with { Warnings = coverage.Warnings.ToArray() }, parentSnapshotId);
        var snapshot = new Snapshot(metadata, lines.ToArray());
        lock (gate)
        {
            while (snapshots.Count >= 4 || snapshots.Values.Sum(s => s.Bytes) + bytes > MemoryLimit)
                snapshots.Remove(snapshots.Values.OrderBy(s => s.Metadata.CapturedAtUtc).First().Metadata.SnapshotId);
            snapshots.Add(metadata.SnapshotId, snapshot);
        }
        return snapshot;
    }

    public Snapshot Get(string id)
    {
        lock (gate) return snapshots.TryGetValue(id, out var snapshot) ? snapshot :
            throw new BridgeException("InventorySnapshotUnavailable", "Snapshot is unknown, released, evicted, or belongs to another host process. Capture again.");
    }

    public bool Release(string id) { lock (gate) return snapshots.Remove(id); }

    /// <summary>Rows offset..offset+count of an immutable snapshot, at most 256 KiB; the snapshot ID binds the page to its capture.</summary>
    public InventoryPage Page(string id, int offset = 0, int count = 100)
    {
        if (count is < 1 or > 200) throw new BridgeException("InvalidRequest", "count must be 1..200.");
        var snapshot = Get(id);
        if (offset < 0 || offset > snapshot.Lines.Length) throw new BridgeException("InvalidCursor", "offset must lie within the snapshot.");
        var rows = new List<InventoryRow>(); int bytes = 0;
        for (int i = offset; i < snapshot.Lines.Length && rows.Count < count; i++)
        {
            var size = Encoding.UTF8.GetByteCount(snapshot.Lines[i]);
            if (rows.Count > 0 && bytes + size > 256 * 1024) break;
            rows.Add(JsonSerializer.Deserialize<InventoryRow>(snapshot.Lines[i], Json)!); bytes += size;
        }
        int next = offset + rows.Count;
        long? total = snapshot.Metadata.Coverage.Truncated ? null : snapshot.Lines.Length;
        return new(snapshot.Metadata, rows.ToArray(), offset, next < snapshot.Lines.Length ? next : null, total, PageTotals.PageCount(total, count));
    }

    private static void Validate(InventoryRow row, string projectPath)
    {
        if (string.IsNullOrEmpty(row.RowId) || row.RowId != row.Path || row.ProjectPath != projectPath ||
            row.Configured is null || row.Actual is null || InventoryRow.ConfiguredKeys.Any(k => !row.Configured.ContainsKey(k)) ||
            InventoryRow.ActualKeys.Any(k => !row.Actual.ContainsKey(k)))
            throw new BridgeException("InvalidInventory", "Inventory row identity, project, or mandatory field states are missing.");
        foreach (var field in row.Configured.Values.Concat(row.Actual.Values))
            if (field is null || string.IsNullOrEmpty(field.State) || (field.State == "available" ? string.IsNullOrWhiteSpace(field.Value) : field.Value != null || string.IsNullOrEmpty(field.Reason)))
                throw new BridgeException("InvalidInventory", "Every field needs either an available value or an explicit missing-data reason.");
    }
}
