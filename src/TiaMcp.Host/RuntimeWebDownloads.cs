using TiaMcp.Core;

namespace TiaMcp.Host;

/// <summary>Content-addressed store for Web API ticket downloads (service data, profiling data, backups): a file is named by its SHA-256, so the same
/// bytes are stored once. Bounded: at most <see cref="MaximumFiles"/> files and <see cref="MaximumTotalBytes"/> in total; storing a new file
/// evicts the oldest ones first, and abandoned temporary files are swept. Read a stored file with artifact_read.</summary>
internal static class RuntimeWebDownloads
{
    internal const string Folder = "runtime-web-downloads";
    internal const int MaximumFiles = 8;
    internal const long MaximumTotalBytes = 1L << 30;
    /// <summary>Per-download cap: a backup can be large, service and profiling data are smaller.</summary>
    internal static long Limit(string kind) => kind == "backup" ? 256L << 20 : 64L << 20;

    internal static string NewTemporary(string artifactsRoot)
    {
        string folder = Path.Combine(artifactsRoot, Folder); Directory.CreateDirectory(folder);
        foreach (var stale in Directory.EnumerateFiles(folder, "*.tmp").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)))
            PackageStorage.TryDelete(stale);
        return Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
    }

    /// <summary>Moves the finished temporary file to its content-addressed name and returns the path relative to the artifact directory.</summary>
    internal static string Promote(string artifactsRoot, string kind, string temporary, string sha256)
    {
        string name = $"web-{kind}-{sha256[..32]}.bin", folder = Path.Combine(artifactsRoot, Folder), full = Path.Combine(folder, name);
        if (File.Exists(full)) { File.Delete(temporary); File.SetLastWriteTimeUtc(full, DateTime.UtcNow); }
        else File.Move(temporary, full);
        // Oldest first, never the file just stored; the new file always fits (its own size is capped below the total).
        var files = new DirectoryInfo(folder).EnumerateFiles("web-*.bin").OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        foreach (var old in files.Where(f => f.Name != name).ToList())
        {
            if (files.Count <= MaximumFiles && total <= MaximumTotalBytes) break;
            long size = old.Length;
            try { old.Delete(); total -= size; files.Remove(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return Folder + "/" + name;
    }
}
