using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TiaMcp.Core;

/// <summary>One verified artifact_read chunk; sha256 covers the whole file.</summary>
public sealed record ArtifactChunk(string RelativePath, long Bytes, string Sha256, long Offset, int Count, long NextOffset,
    bool Complete, string Encoding, string Data);

/// <summary>Explicit, bounded transfer of artifacts from the engineering machine.</summary>
public sealed class ArtifactStore(string root)
{
    private sealed record Digest(long Length, DateTime Written, string Sha256);
    private static readonly ConcurrentDictionary<string, Digest> Digests = new(StringComparer.Ordinal);
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public ArtifactChunk Read(string path, long offset = 0, int count = 65536)
    {
        if (offset < 0 || count is < 1 or > 65536)
            throw new BridgeException("InvalidRequest", "offset must be nonnegative and count must be between 1 and 65536 bytes.");
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, comparison))
            throw new BridgeException("ArtifactPathDenied", "The file must be inside this host's configured artifact directory.");
        // Junctions/symlinks cannot be used to turn this tool into unrestricted filesystem access.
        for (FileSystemInfo? item = new FileInfo(full); item is not null; item = item switch
        {
            FileInfo file => file.Directory,
            DirectoryInfo directory => directory.Parent,
            _ => null
        })
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(item.FullName); }
            catch (Exception error) when (IsUnavailable(error))
            {
                // FileSystemInfo.Attributes returns -1 for a missing item, which
                // includes every flag and falsely identifies it as a reparse
                // point. Journal replacement can expose this transient state.
                throw new BridgeException("ArtifactUnavailable", "The artifact path is missing, being replaced or temporarily locked during inspection. A read may be attempted again; no engineering operation is retried.");
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new BridgeException("ArtifactPathDenied", "Artifact transfer does not follow symbolic links or junctions.");
        }
        // Permit atomic journal replacement while retaining this handle's immutable
        // contents; reads must never block a concurrent engineering outcome journal.
        using var stream = OpenRead(full);
        if (offset > stream.Length) throw new BridgeException("InvalidRequest", "offset is beyond the end of the file.");
        var length = stream.Length;
        // Offset 0 always hashes; later chunks reuse that digest while this handle shows the same length and last write.
        // A stale digest is still the true hash of an earlier version, so reassembled bytes can fail verification, never pass wrongly.
        var written = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        string hash;
        if (offset > 0 && Digests.TryGetValue(full, out var known) && known.Length == length && known.Written == written) hash = known.Sha256;
        else
        {
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (Digests.Count >= 64) Digests.Clear(); // shortcut: crude bound for a few concurrent transfers; use an LRU if many interleave.
            Digests[full] = new(length, written, hash);
        }
        stream.Position = offset;
        var buffer = new byte[(int)Math.Min(count, length - offset)];
        stream.ReadExactly(buffer);
        return new(Path.GetRelativePath(_root, full), length, hash, offset, buffer.Length, offset + buffer.Length,
            offset + buffer.Length == length, "base64", Convert.ToBase64String(buffer));
    }

    private static FileStream OpenRead(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (Exception error) when (IsUnavailable(error))
        {
            throw new BridgeException("ArtifactUnavailable", "The artifact path is missing, being replaced or temporarily locked before it could be opened. A read may be attempted again; no engineering operation is retried.");
        }
    }

    private static bool IsUnavailable(Exception error) =>
        error is FileNotFoundException or DirectoryNotFoundException ||
        (OperatingSystem.IsWindows() && error is IOException && (error.HResult & 0xffff) is 32 or 33);
}
