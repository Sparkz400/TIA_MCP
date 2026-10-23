using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcp.Core;

/// <summary>Managed, non-executing package inspection. No Siemens software is required.</summary>
public sealed class PackageStore
{
    private readonly string _artifacts;
    private readonly string _root;
    private readonly string _packages;
    private readonly PackageIntakeLimits _limits;

    public PackageStore(string artifactsDirectory, PackageIntakeLimits? limits = null)
    {
        _artifacts = artifactsDirectory;
        _limits = limits ?? new PackageIntakeLimits();
        _limits.Validate();
        _root = PackageStorage.Root(artifactsDirectory);
        _packages = PackageStorage.Under(_root, "packages");
        Directory.CreateDirectory(_packages);
    }

    public PackageStatus Stage(string? artifactRef = null, string? hostPath = null, string? sourceUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artifactRef) == string.IsNullOrWhiteSpace(hostPath))
            throw new BridgeException("InvalidRequest", "Specify exactly one committed artifactRef or explicit hostPath.");
        if (sourceUrl is not null && (sourceUrl.Length > 4096 || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
            throw new BridgeException("InvalidRequest", "sourceUrl must be a bounded HTTP(S) provenance URL; it is not fetched or authenticated.");
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        string source, label;
        string? expectedHash = null;
        if (artifactRef is not null)
        {
            var artifact = new ArtifactUploadStore(_artifacts, _limits).ResolveCommittedUnderLock(artifactRef);
            (source, label, expectedHash) = artifact;
        }
        else
        {
            source = Path.GetFullPath(hostPath!);
            PackageStorage.EnsureNoLinks(source);
            label = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        }
        if (Directory.EnumerateDirectories(_packages).Count() >= _limits.MaxPackages)
            throw new BridgeException("PackageLimitExceeded", "Package count limit reached; release unreferenced packages explicitly.");
        var isDirectory = Directory.Exists(source);
        if (isDirectory && (_root.Equals(source, StringComparison.OrdinalIgnoreCase) || _root.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new BridgeException("PackageInvalid", "A source directory cannot contain the managed intake root.");
        var budget = new IntakeBudget(_limits, _root, ReservedUploadBytes(), cancellationToken);
        var inputs = isDirectory ? EnumerateSource(source, budget) : [new InputFile(source, PackageStorage.Relative(label))];
        if (!isDirectory) budget.Entry();
        if (inputs.Count == 0) throw new BridgeException("PackageInvalid", "Package directory contains no files.");
        var id = Guid.NewGuid().ToString("N");
        var directory = PackageStorage.Under(_packages, id);
        Directory.CreateDirectory(directory);
        PackageStorage.Save(Path.Combine(directory, "state.json"), new { schemaVersion = 1, packageId = id, state = "staging", createdUtc = DateTimeOffset.UtcNow });
        var files = new List<PackageFile>();
        var diagnostics = new List<string>();
        try
        {
            foreach (var input in inputs)
            {
                budget.Check();
                var outputRelative = "original/" + input.Relative;
                var output = PackageStorage.Under(directory, outputRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                PackageStorage.EnsureNoLinks(input.Path);
                using var inputStream = new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var file = Copy(inputStream, output, outputRelative, "original", budget, original: true);
                files.Add(file);
            }
            var originals = files.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
            if (budget.InputBytes == 0) throw new BridgeException("PackageInvalid", "A package cannot consist only of empty files.");
            var sourceHash = isDirectory
                ? Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(originals.Select(x => new { path = x.RelativePath[9..], x.Bytes, x.Sha256 }), BridgeProtocol.Json)))
                : originals[0].Sha256;
            if (expectedHash is not null && sourceHash != expectedHash)
                throw new BridgeException("PackageChanged", "Committed artifact changed between resolution and staging.");
            var nested = 0;
            foreach (var file in originals.Where(x => Path.GetExtension(x.RelativePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)))
                Extract(directory, file.RelativePath, "content/" + (++nested).ToString("D6"), 1, budget, files, diagnostics, ref nested);
            var sorted = files.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
            var candidates = sorted.Select(file => Classify(file, sorted)).Where(x => x is not null).Cast<PackageCandidate>().ToArray();
            var documents = sorted.Where(x => IsDocumentation(x.RelativePath)).Select(x => x.RelativePath).ToArray();
            if (candidates.Length == 0) diagnostics.Add("No native library, example project or supported source candidate was found. Files were inventoried only.");
            if (candidates.Length > 1) diagnostics.Add("Multiple candidates are present. Select an exact candidateId; intake never selects the first candidate automatically.");
            diagnostics.Add("Source hashes identify bytes, not publisher authenticity. Package documentation and scripts are untrusted reference material and are never executed.");
            var manifest = new PackageManifest(1, id, "ready", DateTimeOffset.UtcNow, Environment.MachineName,
                artifactRef is not null ? "upload" : isDirectory ? "hostDirectory" : "hostFile", label, sourceUrl, sourceHash,
                budget.InputBytes, budget.ExpandedBytes, _limits, sorted, candidates, documents, diagnostics.ToArray());
            PackageStorage.Save(Path.Combine(directory, "manifest.json"), manifest);
            PackageStorage.Save(Path.Combine(directory, "references.json"), Array.Empty<string>());
            PackageStorage.Save(Path.Combine(directory, "state.json"), new { schemaVersion = 1, packageId = id, state = "ready", createdUtc = manifest.CreatedUtc });
            return ToStatus(directory, manifest);
        }
        catch (Exception error)
        {
            // Preserve the original bytes and failure evidence. Never publish a partial extraction as ready.
            PackageStorage.Save(Path.Combine(directory, "state.json"), new { schemaVersion = 1, packageId = id, state = "failed", createdUtc = DateTimeOffset.UtcNow,
                error = new { code = (error as BridgeException)?.Code ?? "PackageInvalid", message = error.Message } });
            if (error is OperationCanceledException) throw;
            throw new BridgeOperationException((error as BridgeException)?.Code ?? "PackageInvalid", error.Message,
                JsonSerializer.SerializeToElement(new { packageId = id, state = "failed", recoveryPath = directory, retrySafe = true }, BridgeProtocol.Json));
        }
    }

    public PackagePage Inspect(string packageId, string section = "candidates", int offset = 0, int count = 100)
    {
        if (offset < 0 || count < 1 || count > _limits.MaxPageSize)
            throw new BridgeException("InvalidRequest", "offset must be nonnegative and count within the server page limit.");
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        var directory = DirectoryFor(packageId);
        var manifest = ReadReady(directory, packageId, inspection: true);
        Array items = section switch
        {
            "candidates" => manifest.Candidates,
            "files" => manifest.Files,
            "documents" => manifest.Documents,
            "diagnostics" => manifest.Diagnostics,
            _ => throw new BridgeException("InvalidRequest", "section must be candidates, files, documents or diagnostics.")
        };
        if (offset > items.Length) throw new BridgeException("InvalidRequest", "offset exceeds the section length.");
        var end = Math.Min(items.Length, offset + count);
        return new PackagePage(1, packageId, section, offset, items.Length, end < items.Length ? end : null,
            Enumerable.Range(offset, end - offset).Select(index => InspectionItem(items.GetValue(index))).ToArray(),
            Path.Combine(directory, "manifest.json"), Environment.MachineName);
    }

    private static object InspectionItem(object? item) => item is PackageCandidate candidate
        ? candidate with { Notes = candidate.Notes is null ? null! : (string[])candidate.Notes.Clone() }
        : item!;

    public object Status(string packageId)
    {
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        var directory = DirectoryFor(packageId);
        var state = PackageStorage.Load<JsonElement>(Path.Combine(directory, "state.json"));
        if (state.GetProperty("state").GetString() != "ready")
            return new { schemaVersion = 1, packageId, state = state.GetProperty("state").GetString(), evidence = state, host = Environment.MachineName, hostPath = directory,
                retention = "Retained until explicit release; staging after process restart is incomplete and never automatically replayed.", limits = _limits };
        return ToStatus(directory, ReadReady(directory, packageId));
    }

    /// <summary>Pin a package before persisting a plan, lock manifest, session or recovery journal that relies on it.</summary>
    public void Retain(string packageId, string reference)
    {
        ValidateReference(reference);
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        var directory = DirectoryFor(packageId);
        _ = ReadReady(directory, packageId);
        var references = References(directory);
        PackageStorage.Save(Path.Combine(directory, "references.json"), references.Append(reference).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    public void Unretain(string packageId, string reference)
    {
        ValidateReference(reference);
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        var directory = DirectoryFor(packageId);
        PackageStorage.Save(Path.Combine(directory, "references.json"), References(directory).Where(x => x != reference).ToArray());
    }

    public object Release(string packageId)
    {
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        var directory = DirectoryFor(packageId);
        if (!Directory.Exists(directory)) return new { packageId, released = true };
        if (References(directory).Length != 0) throw new BridgeException("PackageInUse", "Package is retained by a plan, session, lock manifest or recovery journal.");
        PackageStorage.DeleteOwned(_packages, directory);
        return new { packageId, released = true };
    }

    public string ResolveCandidate(string packageId, string candidateId) => ResolveCandidateReceipt(packageId, candidateId).Path;

    public ResolvedPackageCandidate ResolveCandidateReceipt(string packageId, string candidateId)
    {
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        var directory = DirectoryFor(packageId);
        var manifest = ReadReady(directory, packageId);
        var candidate = manifest.Candidates.SingleOrDefault(x => x.CandidateId == candidateId)
            ?? throw new BridgeException("PackageInvalid", "The exact candidateId is not part of this package.");
        if (!candidate.ReadyForNativeInspection) throw new BridgeException("PackageInvalid", "Candidate has no complete native container. Supply its full directory or native archive.");
        // Revalidate all package files, including companions and dependencies, before native use.
        foreach (var file in manifest.Files)
        {
            var path = PackageStorage.Under(directory, file.RelativePath);
            if (new FileInfo(path).Length != file.Bytes || PackageStorage.FileHash(path) != file.Sha256)
                throw new BridgeException("PackageChanged", "Staged content changed after inspection: " + file.RelativePath);
        }
        var selectedFile = manifest.Files.SingleOrDefault(file => file.RelativePath == candidate.RelativePath)
            ?? throw new BridgeException("PackageChanged", "Candidate is missing from the staged file manifest.");
        return new ResolvedPackageCandidate(PackageStorage.Under(directory, candidate.RelativePath), selectedFile.Sha256, selectedFile.Bytes);
    }

    /// <summary>Expected files for a native library directory, with paths relative to the marker's parent.
    /// Values come from the immutable intake manifest, not from freshly trusting whatever files exist now.</summary>
    public PackageFile[] GetCandidateFiles(string packageId, string candidateId)
    {
        using var gate = PackageStorage.Lock(Path.Combine(_root, "intake.lock"));
        var directory = DirectoryFor(packageId);
        var manifest = ReadReady(directory, packageId);
        var candidate = manifest.Candidates.SingleOrDefault(x => x.CandidateId == candidateId)
            ?? throw new BridgeException("PackageInvalid", "The exact candidateId is not part of this package.");
        if (candidate.Kind != "globalLibraryDirectory" || !candidate.ReadyForNativeInspection)
            throw new BridgeException("PackageInvalid", "A complete native library directory candidate is required for a companion manifest.");
        var prefix = candidate.RelativePath[..(candidate.RelativePath.LastIndexOf('/') + 1)];
        var expected = manifest.Files.Where(file => file.RelativePath.StartsWith(prefix, StringComparison.Ordinal))
            .Select(file => file with { RelativePath = file.RelativePath[prefix.Length..] }).OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        var candidateDirectory = PackageStorage.Under(directory, prefix.TrimEnd('/'));
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var visited = 0;
        void Visit(string current)
        {
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(current))
            {
                if (++visited > manifest.Limits.MaxEntries) throw new BridgeException("PackageChanged", "Candidate directory exceeds its staged traversal budget.");
                PackageStorage.EnsureNoLinks(entry);
                if (System.IO.Directory.Exists(entry)) Visit(entry);
                else
                {
                    paths.Add(PackageStorage.Relative(Path.GetRelativePath(candidateDirectory, entry)));
                    if (paths.Count > manifest.Limits.MaxEntries) throw new BridgeException("PackageChanged", "Candidate gained unexpected files after staging.");
                }
            }
        }
        Visit(candidateDirectory);
        if (paths.Count != expected.Length) throw new BridgeException("PackageChanged", "Candidate file set differs from its staged manifest.");
        foreach (var file in expected)
        {
            var path = PackageStorage.Under(candidateDirectory, file.RelativePath);
            if (!paths.Contains(file.RelativePath) || new FileInfo(path).Length != file.Bytes || PackageStorage.FileHash(path) != file.Sha256)
                throw new BridgeException("PackageChanged", "Candidate companion differs from its staged manifest: " + file.RelativePath);
        }
        return expected;
    }

    private void Extract(string package, string archiveRelative, string outputRelative, int depth, IntakeBudget budget,
        List<PackageFile> files, List<string> diagnostics, ref int nested)
    {
        if (depth > _limits.MaxNestingDepth) throw new BridgeException("PackageLimitExceeded", "Ordinary ZIP nesting exceeds the configured depth limit.");
        var archivePath = PackageStorage.Under(package, archiveRelative);
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateZipDirectory(stream, _limits.MaxEntries);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
        if (archive.Entries.Count > _limits.MaxEntries) throw new BridgeException("PackageLimitExceeded", "ZIP entry count exceeds the configured limit.");
        var destinations = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var explicitEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toExtract = new List<string>();
        foreach (var entry in archive.Entries)
        {
            budget.Entry();
            var directoryEntry = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType != 0 && unixType != 0x8000 && unixType != 0x4000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new BridgeException("PackageInvalid", "ZIP contains a link or unsupported filesystem entry: " + entry.FullName);
            if (unixType == 0x4000 && !directoryEntry) throw new BridgeException("PackageInvalid", "ZIP directory attributes conflict with its path.");
            var relative = PackageStorage.Relative(entry.FullName, directoryEntry);
            if (!explicitEntries.Add(relative)) throw new BridgeException("PackageInvalid", "ZIP has duplicate or case-colliding entries: " + relative);
            var parts = relative.Split('/');
            // Preserve one spelling for every implicit and explicit path component, including on case-sensitive hosts.
            for (var i = 1; i <= parts.Length; i++)
            {
                var spelling = string.Join('/', parts.Take(i));
                if (spellings.TryGetValue(spelling, out var prior) && prior != spelling)
                    throw new BridgeException("PackageInvalid", "ZIP has case-colliding directory spelling: " + spelling);
                spellings[spelling] = spelling;
            }
            for (var i = 1; i < parts.Length; i++)
            {
                var parent = string.Join('/', parts.Take(i));
                if (destinations.TryGetValue(parent, out var isDirectory) && !isDirectory)
                    throw new BridgeException("PackageInvalid", "ZIP has a file/directory collision: " + parent);
                destinations[parent] = true;
            }
            if (destinations.TryGetValue(relative, out var priorDirectory) && (!directoryEntry || !priorDirectory))
                throw new BridgeException("PackageInvalid", "ZIP has a file/directory collision: " + relative);
            destinations[relative] = directoryEntry;
            var storedRelative = outputRelative + "/" + relative;
            var output = PackageStorage.Under(package, storedRelative);
            if (directoryEntry) { Directory.CreateDirectory(output); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var input = entry.Open();
            var copied = Copy(input, output, storedRelative, "extracted", budget, original: false, expectedCrc: entry.Crc32);
            if (copied.Bytes != entry.Length) throw new BridgeException("PackageInvalid", "ZIP entry length does not match streamed contents.");
            files.Add(copied);
            if (copied.Bytes > 0 && (entry.CompressedLength == 0 || copied.Bytes / Math.Max(1, entry.CompressedLength) > 1000))
                diagnostics.Add("High compression ratio observed: " + storedRelative);
            if (Path.GetExtension(relative).Equals(".zip", StringComparison.OrdinalIgnoreCase)) toExtract.Add(storedRelative);
        }
        foreach (var inner in toExtract)
            Extract(package, inner, "nested/" + (++nested).ToString("D6"), depth + 1, budget, files, diagnostics, ref nested);
    }

    // ZipArchive materializes its complete central directory. Reject excessive counts before it can allocate entries.
    private static void ValidateZipDirectory(FileStream stream, int maximumEntries)
    {
        var length = stream.Length;
        if (length < 22) throw new BridgeException("PackageInvalid", "ZIP has no end-of-directory record.");
        var tail = new byte[(int)Math.Min(length, 65_557)];
        stream.Position = length - tail.Length;
        stream.ReadExactly(tail);
        var position = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50 &&
                i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length) { position = i; break; }
        if (position < 0) throw new BridgeException("PackageInvalid", "ZIP end-of-directory record is absent or malformed.");
        var end = tail.AsSpan(position);
        if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0)
            throw new BridgeException("PackageInvalid", "Multi-volume ZIP packages are unsupported.");
        ulong entries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        ulong centralSize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        ulong centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        var endOffset = length - tail.Length + position;
        if (entries == ushort.MaxValue || centralSize == uint.MaxValue || centralOffset == uint.MaxValue)
        {
            if (endOffset < 20) throw new BridgeException("PackageInvalid", "ZIP64 locator is absent.");
            stream.Position = endOffset - 20;
            Span<byte> locator = stackalloc byte[20];
            stream.ReadExactly(locator);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50 || BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) != 1)
                throw new BridgeException("PackageInvalid", "ZIP64 locator is invalid or multi-volume.");
            var zip64Offset = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
            if (zip64Offset > (ulong)Math.Max(0, length - 56)) throw new BridgeException("PackageInvalid", "ZIP64 directory offset is invalid.");
            stream.Position = (long)zip64Offset;
            Span<byte> zip64 = stackalloc byte[56];
            stream.ReadExactly(zip64);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064b50 || BinaryPrimitives.ReadUInt32LittleEndian(zip64[16..]) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(zip64[20..]) != 0)
                throw new BridgeException("PackageInvalid", "ZIP64 directory is invalid or multi-volume.");
            entries = BinaryPrimitives.ReadUInt64LittleEndian(zip64[32..]);
            centralSize = BinaryPrimitives.ReadUInt64LittleEndian(zip64[40..]);
            centralOffset = BinaryPrimitives.ReadUInt64LittleEndian(zip64[48..]);
        }
        if (entries > (ulong)maximumEntries) throw new BridgeException("PackageLimitExceeded", "ZIP entry count exceeds the configured limit before directory allocation.");
        if (centralOffset > (ulong)length || centralSize > (ulong)length - centralOffset)
            throw new BridgeException("PackageInvalid", "ZIP central directory is out of bounds.");
        stream.Position = (long)centralOffset;
        Span<byte> header = stackalloc byte[46];
        for (ulong i = 0; i < entries; i++)
        {
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
                throw new BridgeException("PackageInvalid", "ZIP central directory entry is malformed.");
            if ((BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) & 0x41) != 0)
                throw new BridgeException("PackageInvalid", "Encrypted ZIP entries are unsupported.");
            var name = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var extra = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            var comment = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            if (name > 4096) throw new BridgeException("PackageInvalid", "ZIP entry path exceeds the bounded encoded path length.");
            stream.Position += name + extra + comment;
            if ((ulong)stream.Position > centralOffset + centralSize) throw new BridgeException("PackageInvalid", "ZIP central directory entry exceeds its bounds.");
        }
        if ((ulong)stream.Position != centralOffset + centralSize)
            throw new BridgeException("PackageInvalid", "ZIP central directory size/count mismatch.");
        stream.Position = 0;
    }

    private static PackageFile Copy(Stream input, string output, string relative, string role, IntakeBudget budget, bool original, uint? expectedCrc = null)
    {
        PackageStorage.EnsureNoLinks(output);
        using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long bytes = 0;
        var crc = uint.MaxValue;
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            budget.Add(count, original);
            target.Write(buffer, 0, count);
            hash.AppendData(buffer, 0, count);
            bytes += count;
            if (expectedCrc.HasValue)
                for (var i = 0; i < count; i++) crc = CrcTable[(crc ^ buffer[i]) & 0xff] ^ (crc >> 8);
        }
        target.Flush(true);
        if (expectedCrc.HasValue && ~crc != expectedCrc.Value) throw new BridgeException("PackageInvalid", "ZIP entry CRC does not match its contents: " + relative);
        return new PackageFile(relative, bytes, Convert.ToHexStringLower(hash.GetHashAndReset()), role);
    }

    private List<InputFile> EnumerateSource(string root, IntakeBudget budget)
    {
        var inputs = new List<InputFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string directory)
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                budget.Entry();
                PackageStorage.EnsureNoLinks(item);
                var relative = PackageStorage.Relative(Path.GetRelativePath(root, item));
                if (!paths.Add(relative)) throw new BridgeException("PackageInvalid", "Source has case-insensitive path collisions.");
                if (Directory.Exists(item)) Walk(item);
                else inputs.Add(new InputFile(item, relative));
            }
        }
        Walk(root);
        return inputs;
    }

    private static PackageCandidate? Classify(PackageFile file, PackageFile[] all)
    {
        var extension = Path.GetExtension(file.RelativePath).ToLowerInvariant();
        var native = Regex.Match(extension, "^\\.(zal|al|zap|ap)([0-9]{1,2})(?:_[0-9]+)?$", RegexOptions.CultureInvariant);
        string kind;
        int? major = null;
        var ready = true;
        var completeness = "nativeVerificationRequired";
        var notes = new List<string>();
        if (native.Success)
        {
            major = int.Parse(native.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            kind = native.Groups[1].Value switch { "zal" => "globalLibraryArchive", "al" => "globalLibraryDirectory", "zap" => "exampleProjectArchive", _ => "exampleProjectDirectory" };
            if (kind.EndsWith("Directory", StringComparison.Ordinal))
            {
                var parent = file.RelativePath[..(file.RelativePath.LastIndexOf('/') + 1)];
                var companion = all.Any(x => x.RelativePath != file.RelativePath && x.RelativePath.StartsWith(parent, StringComparison.Ordinal) &&
                    !IsDocumentation(x.RelativePath) && !Regex.IsMatch(Path.GetExtension(x.RelativePath), "^\\.(?:al|ap|zal|zap)[0-9]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                ready = companion;
                if (!companion) { completeness = "missingCompanionFiles"; notes.Add("A standalone native marker is not a complete library/project. Supply the complete directory or native archive."); }
                else notes.Add("Companion payload exists. The native adapter must verify completeness and compatibility; intake does not parse private TIA storage.");
            }
            else notes.Add("Native archive is retained opaque. Retrieval and compatibility checks belong to Siemens Openness.");
        }
        else if (extension is ".scl" or ".awl" or ".udt" or ".xml")
        {
            kind = "sourceFile";
            ready = false;
            completeness = "sourceImportRequired";
            notes.Add("A loose source is not a version-linked library type; select an explicit source-import or type-authoring workflow.");
        }
        else return null;
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file.RelativePath + "\n" + file.Sha256)));
        return new PackageCandidate(id, file.RelativePath, kind, major, ready, completeness, notes.ToArray());
    }

    private static bool IsDocumentation(string path)
    {
        var name = Path.GetFileName(path);
        return Path.GetExtension(path).ToLowerInvariant() is ".pdf" or ".md" or ".txt" or ".html" or ".htm" ||
            name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("README", StringComparison.OrdinalIgnoreCase);
    }

    private long ReservedUploadBytes()
    {
        var uploads = PackageStorage.Under(_root, "uploads");
        if (!Directory.Exists(uploads)) return 0;
        return Directory.EnumerateDirectories(uploads).Where(path => File.Exists(Path.Combine(path, "upload.json")))
            .Select(path => PackageStorage.Load<ArtifactUploadStatus>(Path.Combine(path, "upload.json")))
            .Where(x => x.State == "receiving").Sum(x => x.RemainingBytes);
    }

    private string DirectoryFor(string id) => PackageStorage.Under(_packages, PackageStorage.Id(id));
    private static string[] References(string directory)
    {
        var path = Path.Combine(directory, "references.json");
        if (File.Exists(path)) return PackageStorage.Load<string[]>(path);
        var state = PackageStorage.Load<JsonElement>(Path.Combine(directory, "state.json"));
        if (state.GetProperty("state").GetString() == "ready")
            throw new BridgeException("PackageChanged", "Ready package lost its retention ledger; deletion is blocked.");
        return [];
    }
    private static void ValidateReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 512 || reference.Any(char.IsControl))
            throw new BridgeException("InvalidRequest", "A bounded nonempty retention reference is required.");
    }
    private static PackageManifest ReadReady(string directory, string id, bool inspection = false)
    {
        var state = PackageStorage.Load<JsonElement>(Path.Combine(directory, "state.json"));
        if (state.GetProperty("state").GetString() != "ready") throw new BridgeException("PackageIncomplete", "Package staging did not complete; inspect status and stage a new copy.");
        var manifest = inspection ? PackageInspectionManifestCache.Load(Path.Combine(directory, "manifest.json"))
            : PackageStorage.Load<PackageManifest>(Path.Combine(directory, "manifest.json"));
        if (manifest.PackageId != id || manifest.State != "ready") throw new BridgeException("PackageChanged", "Package manifest identity/state mismatch.");
        return manifest;
    }
    private static PackageStatus ToStatus(string directory, PackageManifest manifest) => new(1, manifest.PackageId, manifest.State,
        manifest.CreatedUtc, manifest.SourceSha256, manifest.InputBytes, manifest.ExpandedBytes, manifest.Files.Length,
        manifest.Candidates.Length, References(directory), "Retained until explicit release; referenced packages cannot be released.",
        Path.Combine(directory, "manifest.json"), Environment.MachineName, manifest.Limits);

    private sealed record InputFile(string Path, string Relative);
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var value = (uint)n;
        for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : 0xedb88320 ^ (value >> 1);
        return value;
    }).ToArray();

    private sealed class IntakeBudget(PackageIntakeLimits limits, string root, long reserved, CancellationToken cancellationToken)
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly long _stored = PackageStorage.StoredBytes(root) + reserved;
        private int _entries;
        public long InputBytes { get; private set; }
        public long ExpandedBytes { get; private set; }
        public void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_watch.Elapsed.TotalSeconds > limits.MaxWorkSeconds) throw new BridgeException("PackageLimitExceeded", "Package work exceeded the configured elapsed-time budget.");
        }
        public void Entry() { Check(); if (++_entries > limits.MaxEntries) throw new BridgeException("PackageLimitExceeded", "Package entry budget exceeded."); }
        public void Add(int count, bool original)
        {
            Check();
            if (original) InputBytes += count; else ExpandedBytes += count;
            if (InputBytes > limits.MaxInputBytes || ExpandedBytes > limits.MaxExpandedBytes || _stored + InputBytes + ExpandedBytes > limits.MaxStoredBytes)
                throw new BridgeException("PackageLimitExceeded", "Streamed bytes exceed the configured input, expansion or total storage limit.");
            PackageStorage.FreeSpace(root, count);
        }
    }
}
