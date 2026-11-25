using System.Collections.Concurrent;

namespace TiaMcp.Core;

/// <summary>
/// Age- and size-bounded cleanup of the per-call block export files (<c>block-&lt;guid&gt;.xml</c>/<c>.txt</c> and
/// <c>block-export-&lt;guid&gt;.json</c> indexes) that block_read and block_export leave in the exports directory. Other exports
/// (CAx, inventories, type and tag-table reads) are not touched; they may be named by a later step's path and digest.
/// </summary>
public static class ExportRetention
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    public const long MaxBytes = 1L << 30;
    // A file this young may still be named by the response that has just been returned.
    private static readonly TimeSpan MinAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, DateTime> LastRun = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Throttled to once per interval and directory; never throws, because a cleanup must not fail the export that triggered it.</summary>
    public static void Prune(string exportsDirectory) => Prune(exportsDirectory, MaxAge, MaxBytes, DateTime.UtcNow, throttle: true);

    /// <summary>The same bounds for the self-contained Explorer pages (<c>explorer-&lt;guid&gt;.html</c>, each embedding its
    /// snapshot) that an HTML project-model export writes beside the snapshots; the snapshots themselves are not touched.</summary>
    public static void PruneExplorerViewers(string directory) => Prune(directory, MaxAge, MaxBytes, DateTime.UtcNow, throttle: true, viewers: true);

    internal static void Prune(string exportsDirectory, TimeSpan maxAge, long maxBytes, DateTime nowUtc, bool throttle, bool viewers = false)
    {
        try
        {
            string key = Path.GetFullPath(exportsDirectory);
            if (!Directory.Exists(key)) return;
            if (throttle)
            {
                string run = viewers ? key + "|viewers" : key;
                if (LastRun.TryGetValue(run, out var last) && nowUtc - last < Interval) return;
                LastRun[run] = nowUtc;
            }
            var files = new DirectoryInfo(key).EnumerateFiles(viewers ? "explorer-*.html" : "block-*")
                .Where(f => viewers || (f.Name.StartsWith("block-export-", StringComparison.Ordinal) ? f.Extension == ".json" : f.Extension is ".xml" or ".txt"))
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0).OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                bool old = nowUtc - file.LastWriteTimeUtc > maxAge, over = total > maxBytes;
                if (!old && !over) break; // Sorted oldest first: nothing later is older or needed to get under the size bound.
                if (nowUtc - file.LastWriteTimeUtc < MinAge) break;
                long length = file.Length;
                try { file.Delete(); total -= length; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
