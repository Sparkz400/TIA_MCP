using System.Collections.Concurrent;

namespace TiaMcp.Core.EngineeringJobs;

/// <summary>
/// Per-process memo for admission scans. A scanned record that owns nothing is validated from its revision chain alone, so it is
/// reused until its folder's revision count or last revision file (name, length, write time) changes. A record that owns resources is
/// never memoized, and a non-scan read (the record being acted on) always validates the full chain.
/// </summary>
internal static class JournalScanCache
{
    private static readonly ConcurrentDictionary<string, (int Count, string Last, long Length, DateTime Written, object Record)> Entries =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static bool TryGet<T>(string folder, string[] files, out T record) where T : class
    {
        var last = new FileInfo(files[^1]);
        if (Entries.TryGetValue(folder, out var hit) && hit.Count == files.Length && hit.Last == files[^1] && hit.Length == last.Length &&
            hit.Written == last.LastWriteTimeUtc && hit.Record is T cached) { record = cached; return true; }
        record = null!; return false;
    }

    /// <summary>Call only under the store lock, after a scan validated a record that owns nothing.</summary>
    internal static void Remember(string folder, string[] files, object record)
    {
        var last = new FileInfo(files[^1]);
        Entries[folder] = (files.Length, files[^1], last.Length, last.LastWriteTimeUtc, record);
    }
}
