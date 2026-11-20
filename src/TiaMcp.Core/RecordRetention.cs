namespace TiaMcp.Core;

/// <summary>
/// Bounded record directories (<c>root/&lt;id&gt;</c>) that must not refuse forever once full. At the cap the oldest record the caller
/// calls evictable is deleted to make room; a store whose every record is in use still refuses with its own capacity error.
/// Callers hold their store lock.
/// </summary>
internal static class RecordRetention
{
    /// <param name="rank">Eviction order of a record directory: negative keeps it, otherwise the lowest rank (then the oldest) goes first.</param>
    internal static void MakeRoom(string root, int maxRecords, Func<string, int> rank, Func<BridgeException> full)
    {
        var records = Directory.EnumerateDirectories(root).ToList();
        if (records.Count < maxRecords) return;
        // An abandoned incomplete publication is the cheapest to lose, then the oldest of the next rank.
        foreach (var (directory, _) in records.Select(d => (Directory: d, Rank: rank(d))).Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank).ThenBy(x => Directory.GetCreationTimeUtc(x.Directory)).ThenBy(x => x.Directory, StringComparer.Ordinal).ToList())
        {
            if (records.Count < maxRecords) return;
            // Git writes read-only objects; links are refused before attributes are cleared so deletion can succeed.
            _ = PackageStorage.StoredBytes(directory);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            PackageStorage.DeleteOwned(root, directory);
            records.Remove(directory);
        }
        if (records.Count >= maxRecords) throw full();
    }

    /// <summary>True when any file named <paramref name="fileName"/> directly below a record of <paramref name="root"/> mentions <paramref name="needle"/>.</summary>
    internal static bool Mentions(string root, string fileName, string needle, int maxBytes)
    {
        if (!Directory.Exists(root)) return false;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var file = new FileInfo(Path.Combine(directory, fileName));
            if (file.Exists && file.Length <= maxBytes && File.ReadAllText(file.FullName).Contains(needle, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
