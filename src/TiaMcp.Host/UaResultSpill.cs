using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.Host;

/// <summary>Keeps a UA tool reply small: at most 128 KiB goes inline (the reply carries it twice); a larger result is written whole as
/// a content-addressed artifact under the artifact directory, for artifact_read. Spills older than a day, and the oldest ones beyond
/// 256 MiB in all, are removed on the next spill, except any written since the oldest live session opened (its replies may still be read).</summary>
internal static class UaResultSpill
{
    internal const int InlineLimitBytes = 128 * 1024;
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(1);
    internal const long MaximumBytes = 256L << 20;

    internal static object Bounded<T>(string artifactsRoot, T result, string kind, int count, Func<OpcUaRuntimeSpill, T> spilled, DateTimeOffset? protectFrom = null) where T : notnull
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json);
        if (bytes.Length <= InlineLimitBytes) return result;
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes)), relative = $"runtime-ua-results/ua-{kind}-{hash[..32]}.json";
        string full = Path.Combine(artifactsRoot, relative);
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temp, bytes); File.Move(temp, full, true); }
            finally { File.Delete(temp); }
        }
        Prune(Path.GetDirectoryName(full)!, full, protectFrom, DateTime.UtcNow);
        return spilled(new(relative, hash, bytes.Length, count));
    }

    /// <summary>Best effort: a file in use or already gone is left for the next spill. The file just written is never removed.</summary>
    internal static void Prune(string directory, string keep, DateTimeOffset? protectFrom, DateTime now)
    {
        var files = new DirectoryInfo(directory).EnumerateFiles("ua-*.json").Where(f => !string.Equals(f.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase) &&
            (protectFrom is null || f.LastWriteTimeUtc < protectFrom.Value.UtcDateTime)).OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = new DirectoryInfo(directory).EnumerateFiles("ua-*.json").Sum(f => f.Length);
        foreach (var file in files)
        {
            if (now - file.LastWriteTimeUtc <= MaximumAge && total <= MaximumBytes) break; // oldest first: everything after is younger
            try { long length = file.Length; file.Delete(); total -= length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
