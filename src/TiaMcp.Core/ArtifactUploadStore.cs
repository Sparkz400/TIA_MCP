using System.Security.Cryptography;

namespace TiaMcp.Core;

/// <summary>Durable sequential upload with byte-identical retry and cross-process file locks.</summary>
public sealed class ArtifactUploadStore
{
    private readonly string _root;
    private readonly string _uploads;
    private readonly PackageIntakeLimits _limits;

    public ArtifactUploadStore(string artifactsDirectory, PackageIntakeLimits? limits = null)
    {
        _limits = limits ?? new PackageIntakeLimits();
        _limits.Validate();
        _root = PackageStorage.Root(artifactsDirectory);
        _uploads = PackageStorage.Under(_root, "uploads");
        Directory.CreateDirectory(_uploads);
    }

    public ArtifactUploadStatus Begin(string fileName, long expectedBytes, string sha256)
    {
        fileName = PackageStorage.Relative(fileName);
        if (fileName.Contains('/')) throw new BridgeException("InvalidRequest", "fileName must be a display filename, not a path.");
        if (expectedBytes is < 1 || expectedBytes > _limits.MaxInputBytes)
            throw new BridgeException("PackageLimitExceeded", "Upload size exceeds the configured input limit or is empty.");
        sha256 = PackageStorage.Hash(sha256);
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        var records = Directory.EnumerateDirectories(_uploads).Where(path => File.Exists(Path.Combine(path, "upload.json")))
            .Select(path => PackageStorage.Load<ArtifactUploadStatus>(Path.Combine(path, "upload.json"))).ToArray();
        if (records.Count(x => x.State != "aborted") >= _limits.MaxUploads)
            throw new BridgeException("PackageLimitExceeded", "The configured upload count limit has been reached.");
        var reserved = records.Where(x => x.State == "receiving").Sum(x => x.RemainingBytes);
        if (PackageStorage.StoredBytes(_root) + reserved + expectedBytes > _limits.MaxStoredBytes)
            throw new BridgeException("PackageLimitExceeded", "The intake storage quota, including reserved uploads, would be exceeded.");
        PackageStorage.FreeSpace(_root, expectedBytes);
        var id = Guid.NewGuid().ToString("N");
        var directory = PackageStorage.Under(_uploads, id);
        Directory.CreateDirectory(directory);
        using (File.Create(Path.Combine(directory, "data.bin"))) { }
        var now = DateTimeOffset.UtcNow;
        var status = new ArtifactUploadStatus(1, id, fileName, expectedBytes, sha256, 0, expectedBytes, "receiving", null,
            now, now.AddHours(_limits.UploadLifetimeHours), _limits.MaxChunkBytes, Environment.MachineName, directory, Limits: _limits);
        PackageStorage.Save(Path.Combine(directory, "upload.json"), status);
        return status;
    }

    public ArtifactUploadStatus Write(string uploadId, long offset, string base64)
    {
        if (offset < 0 || base64 is null || base64.Length > ((_limits.MaxChunkBytes + 2) / 3) * 4)
            throw new BridgeException("InvalidRequest", "The chunk offset or encoded size is invalid.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException error) { throw new BridgeException("InvalidRequest", "Chunk data must be base64.", error); }
        if (bytes.Length is < 1 || bytes.Length > _limits.MaxChunkBytes)
            throw new BridgeException("PackageLimitExceeded", "The decoded chunk is empty or exceeds the chunk limit.");
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        var (directory, status) = Read(uploadId);
        RequireReceiving(status);
        if (bytes.Length > status.MaxChunkBytes) throw new BridgeException("PackageLimitExceeded", "Chunk exceeds the limit recorded when this upload began.");
        if (offset > status.ReceivedBytes || offset > status.ExpectedBytes - bytes.Length)
            throw new BridgeException("UploadOffsetMismatch", "Chunks must append at receivedBytes or repeat already persisted bytes without gaps.");
        var data = PackageStorage.Under(directory, "data.bin");
        using var stream = new FileStream(data, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        // A crash after data flush but before the durable status update may leave an unacknowledged tail.
        if (stream.Length < status.ReceivedBytes) throw new BridgeException("PackageChanged", "Persisted upload bytes were removed.");
        if (stream.Length > status.ReceivedBytes) stream.SetLength(status.ReceivedBytes);
        if (offset < status.ReceivedBytes)
        {
            if (offset + bytes.Length > status.ReceivedBytes)
                throw new BridgeException("UploadOffsetMismatch", "A retry must fit entirely within the received range.");
            var existing = new byte[bytes.Length];
            stream.Position = offset;
            stream.ReadExactly(existing);
            if (!CryptographicOperations.FixedTimeEquals(existing, bytes))
                throw new BridgeException("UploadChunkMismatch", "Previously received bytes differ from this retry.");
            return status with { AcceptedOffset = offset, AcceptedBytes = bytes.Length, ChunkSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        }
        stream.Position = offset;
        stream.Write(bytes);
        stream.Flush(true);
        status = status with { ReceivedBytes = offset + bytes.Length, RemainingBytes = status.ExpectedBytes - offset - bytes.Length,
            AcceptedOffset = offset, AcceptedBytes = bytes.Length, ChunkSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        PackageStorage.Save(Path.Combine(directory, "upload.json"), status);
        return status;
    }

    public ArtifactUploadStatus Status(string uploadId)
    {
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        return Read(uploadId).Status;
    }

    public ArtifactUploadStatus Commit(string uploadId)
    {
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        var (directory, status) = Read(uploadId);
        if (status.State == "committed") { VerifyCommitted(directory, status); return status; }
        RequireReceiving(status);
        if (status.RemainingBytes != 0) throw new BridgeException("UploadIncomplete", "Upload has not received the expected byte count.");
        var data = PackageStorage.Under(directory, "data.bin");
        if (new FileInfo(data).Length != status.ExpectedBytes || PackageStorage.FileHash(data) != status.ExpectedSha256)
            throw new BridgeException("PackageChanged", "Final uploaded bytes do not match the expected length and SHA-256. Abort and upload the correct bytes.");
        status = status with { State = "committed", ArtifactRef = "upload:" + uploadId };
        PackageStorage.Save(Path.Combine(directory, "upload.json"), status);
        return status;
    }

    public ArtifactUploadStatus Abort(string uploadId)
    {
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        var (directory, status) = Read(uploadId);
        if (status.State == "committed") throw new BridgeException("UploadCommitted", "Committed artifacts cannot be aborted.");
        status = status with { State = "aborted", ReceivedBytes = 0, RemainingBytes = status.ExpectedBytes };
        PackageStorage.Save(Path.Combine(directory, "upload.json"), status);
        var data = PackageStorage.Under(directory, "data.bin");
        if (File.Exists(data)) File.Delete(data);
        return status;
    }

    public (string Path, string FileName, string Sha256) ResolveCommitted(string artifactRef)
    {
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        return ResolveCommittedUnderLock(artifactRef);
    }

    internal (string Path, string FileName, string Sha256) ResolveCommittedUnderLock(string artifactRef)
    {
        if (artifactRef is null || !artifactRef.StartsWith("upload:", StringComparison.Ordinal))
            throw new BridgeException("InvalidRequest", "Use the artifactRef returned by artifact_upload_commit.");
        var (directory, status) = Read(artifactRef[7..]);
        if (status.State != "committed") throw new BridgeException("UploadIncomplete", "The artifact is not committed.");
        VerifyCommitted(directory, status);
        return (PackageStorage.Under(directory, "data.bin"), status.FileName, status.ExpectedSha256);
    }

    /// <summary>Packages retain independent copies. The global gate excludes both staging and concurrent transfer.</summary>
    public object Release(string uploadId)
    {
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        var directory = PackageStorage.Under(_uploads, PackageStorage.Id(uploadId));
        if (!Directory.Exists(directory)) return new { uploadId, released = true, host = Environment.MachineName };
        var (_, status) = Read(uploadId);
        if (status.State == "receiving" && status.ExpiresUtc > DateTimeOffset.UtcNow)
            throw new BridgeException("UploadInUse", "Active incomplete uploads must be aborted before release.");
        if (status.State is not ("receiving" or "committed" or "aborted"))
            throw new BridgeException("UploadInUse", "Unrecognized upload state cannot be released.");
        PackageStorage.DeleteOwned(_uploads, directory);
        return new { uploadId, released = true, host = Environment.MachineName };
    }

    private (string Directory, ArtifactUploadStatus Status) Read(string id)
    {
        var directory = PackageStorage.Under(_uploads, PackageStorage.Id(id));
        var status = PackageStorage.Load<ArtifactUploadStatus>(Path.Combine(directory, "upload.json"));
        if (status.UploadId != id) throw new BridgeException("PackageChanged", "Upload identity does not match its storage directory.");
        return (directory, status);
    }

    private static void RequireReceiving(ArtifactUploadStatus status)
    {
        if (status.State != "receiving") throw new BridgeException("UploadNotWritable", "Only incomplete uploads accept bytes.");
        if (status.ExpiresUtc <= DateTimeOffset.UtcNow) throw new BridgeException("UploadExpired", "Upload expired. Abort it to release its reservation and begin a new upload.");
    }

    private static void VerifyCommitted(string directory, ArtifactUploadStatus status)
    {
        var path = PackageStorage.Under(directory, "data.bin");
        if (new FileInfo(path).Length != status.ExpectedBytes || PackageStorage.FileHash(path) != status.ExpectedSha256)
            throw new BridgeException("PackageChanged", "The committed artifact no longer matches its immutable digest.");
    }
}
