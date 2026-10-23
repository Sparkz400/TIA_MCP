using System.Security.Cryptography;

namespace TiaMcp.Core;

/// <summary>Explicit, bounded transfer of artifacts from the engineering machine.</summary>
public sealed class ArtifactStore(string root)
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public object Read(string path, long offset = 0, int count = 65536)
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
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        stream.Position = offset;
        var buffer = new byte[(int)Math.Min(count, length - offset)];
        stream.ReadExactly(buffer);
        return new
        {
            relativePath = Path.GetRelativePath(_root, full), bytes = length, sha256 = hash,
            offset, count = buffer.Length, nextOffset = offset + buffer.Length,
            complete = offset + buffer.Length == length, encoding = "base64", data = Convert.ToBase64String(buffer)
        };
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
