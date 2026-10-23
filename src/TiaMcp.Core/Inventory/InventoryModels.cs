using System.Text.RegularExpressions;

namespace TiaMcp.Core.Inventory;

public sealed record InventoryField(string? Value, string State, string Source, string? Reason)
{
    public static InventoryField Missing(string state, string reason, string source = "") => new(null, state, source, reason);
    public static InventoryField Read(string? value, string source) => string.IsNullOrWhiteSpace(value)
        ? Missing("notConfigured", "The source returned an empty value.", source) : new(value, "available", source, null);
}

public sealed record InventoryOnline(string Status, string? Endpoint = null, uint? HardwareId = null,
    DateTimeOffset? ObservedAtUtc = null, string? Reason = null, string? Association = null);

public sealed record InventoryRow(string RowId, string Name, string DeviceName, string Kind, string Path,
    string? ParentPath, string ProjectPath, Dictionary<string, InventoryField> Configured)
{
    public Dictionary<string, InventoryField> Actual { get; init; } = ActualUnavailable("notRead", "Online identification has not been requested.");
    public InventoryOnline Online { get; init; } = new("notRead");
    public Dictionary<string, string> Comparisons { get; init; } = new();
    public static readonly string[] ConfiguredKeys = ["plantDesignation", "locationIdentifier", "productDesignation", "mlfb", "firmware", "hardwareRevision", "slot", "ip", "typeIdentifier"];
    public static readonly string[] ActualKeys = ["mlfb", "firmware", "serialNumber", "hardwareRevision"];
    public static Dictionary<string, InventoryField> ActualUnavailable(string state, string reason) =>
        ActualKeys.ToDictionary(k => k, _ => InventoryField.Missing(state, reason));

    public InventoryRow Compare() => this with
    {
        Comparisons = new[] { "mlfb", "firmware", "hardwareRevision" }.ToDictionary(k => k,
            k => CompareField(k, Configured.GetValueOrDefault(k), Actual.GetValueOrDefault(k)))
    };

    private static string CompareField(string key, InventoryField? configured, InventoryField? actual)
    {
        if (configured?.State != "available" || actual?.State != "available") return "notComparable";
        var left = configured.Value!.Trim(); var right = actual.Value!.Trim();
        if (key == "mlfb")
        {
            // Catalog placeholders identify a family, not an exact physical article.
            if (left.IndexOfAny(['*', '?']) >= 0 || left.StartsWith("6", StringComparison.Ordinal) && left.Contains('x')) return "notComparable";
            left = Regex.Replace(left, @"\s+", "").ToUpperInvariant();
            right = Regex.Replace(right, @"\s+", "").ToUpperInvariant();
        }
        if (key == "firmware") { left = Firmware(left); right = Firmware(right); }
        return left == right ? "match" : "mismatch";
    }

    private static string Firmware(string value)
    {
        var match = Regex.Match(value, @"^[Vv]?(\d+)\.(\d+)(?:\.(\d+))?$");
        return match.Success ? string.Join(".", Enumerable.Range(1, 3).Select(i =>
        {
            var digits = match.Groups[i].Value.TrimStart('0');
            return digits.Length == 0 ? "0" : digits;
        })) : value;
    }
}

public sealed record InventoryCoverage(bool ScopeComplete, bool Truncated, int VisitedItems, int MaxVisited,
    string[] Warnings, string Scope = "Configured devices and device items; snapshot capture is not a TIA transaction.",
    string? DevicePath = null, bool? ProjectModifiedBefore = null, bool? ProjectModifiedAfter = null);

public sealed record InventoryMetadata(string SnapshotId, DateTimeOffset CapturedAtUtc, string ProjectPath,
    int TotalRows, InventoryCoverage Coverage, string? ParentSnapshotId = null,
    string Freshness = "snapshotOnly", string HardwareQualification = "unverified", int SchemaVersion = 1);

public sealed record InventoryPage(InventoryMetadata Snapshot, InventoryRow[] Rows, int Offset, string? NextCursor);
