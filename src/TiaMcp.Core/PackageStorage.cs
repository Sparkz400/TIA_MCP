using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcp.Core;

internal static class PackageStorage
{
    internal static string Root(string artifacts)
    {
        var root = Path.GetFullPath(Path.Combine(artifacts, "library-intake"));
        EnsureNoLinks(root);
        Directory.CreateDirectory(root);
        EnsureNoLinks(root);
        return root;
    }

    internal static string Id(string id)
    {
        if (id is null || !Regex.IsMatch(id, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new BridgeException("InvalidRequest", "Expected a server-issued intake ID.");
        return id;
    }

    internal static string Hash(string value)
    {
        if (value is null || !Regex.IsMatch(value, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new BridgeException("InvalidRequest", "Expected a 64-character SHA-256 hex digest.");
        return value.ToLowerInvariant();
    }

    // Apply Windows rules on every platform, so a package cannot become unsafe when transferred to TIA.
    internal static string Relative(string raw, bool directory = false)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 1024 || raw[0] is '/' or '\\' || raw.Contains(':') || raw.Contains('\0'))
            throw new BridgeException("PackageInvalid", "Package contains a rooted, empty, oversized or alternate-stream path.");
        var value = raw.Replace('\\', '/');
        if (directory) value = value.TrimEnd('/');
        var parts = value.Split('/');
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 255 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(c => c < 32 || "<>\"|?*".Contains(c)) || part != part.Normalize(NormalizationForm.FormC) ||
                Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new BridgeException("PackageInvalid", "Package contains a Windows device name, traversal, invalid or ambiguous normalized path: " + raw);
        }
        return string.Join('/', parts);
    }

    internal static string Under(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("PackageInvalid", "Path escapes the managed intake directory.");
        EnsureNoLinks(full);
        return full;
    }

    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new BridgeException("PackageInvalid", "Intake never follows symbolic links or junctions: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    // A short holder (a status poll, another Host) is waited out, so a returned native call is still recorded; a longer hold refuses.
    internal static TimeSpan LockWait = TimeSpan.FromSeconds(5);

    internal static FileStream Lock(string path)
    {
        EnsureNoLinks(path);
        try { return WaitForLock(() => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), System.Diagnostics.Stopwatch.StartNew(), LockWait); }
        catch (IOException error) { throw new BridgeException("IntakeBusy", "Another intake operation holds this resource. Retry the same read or upload request later.", error); }
    }

    /// <summary>The one bounded lock-file wait (also the credential vault's): only a sharing or lock violation is retried.</summary>
    internal static FileStream WaitForLock(Func<FileStream> open, System.Diagnostics.Stopwatch elapsed, TimeSpan bound)
    {
        while (true)
        {
            try { return open(); }
            catch (IOException error) when (error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) && elapsed.Elapsed < bound) { Thread.Sleep(25); }
        }
    }

    internal static void PublishDirectory(string staging, string destination) =>
        PublishDirectory(staging, destination, Directory.Move, Thread.Sleep);

    internal static void PublishDirectory(string staging, string destination, Action<string, string> move, Action<int> delay)
    {
        staging = Path.GetFullPath(staging); destination = Path.GetFullPath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(staging), Path.GetDirectoryName(destination), comparison))
            throw new BridgeException("PackageInvalid", "Journal publication must rename within its existing parent directory.");
        for (int attempt = 1; ; attempt++)
        {
            EnsureNoLinks(staging); EnsureNoLinks(destination);
            try { move(staging, destination); return; }
            // Retry only an unpublished local rename; never re-run preparation or native dispatch.
            catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 10 &&
                error.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021) &&
                Directory.Exists(staging) && !Directory.Exists(destination) && !File.Exists(destination))
            { delay(25); }
        }
    }

    internal static void Save<T>(string path, T value)
    {
        EnsureNoLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, BridgeProtocol.Json);
            stream.Flush(true);
        }
        PublishFile(temporary, path);
    }

    internal static void PublishFile(string temporary, string destination) =>
        PublishFile(temporary, destination, (source, target) => File.Move(source, target, true), Thread.Sleep);

    internal static void PublishFile(string temporary, string destination, Action<string, string> move, Action<int> delay)
    {
        temporary = Path.GetFullPath(temporary); destination = Path.GetFullPath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(temporary, destination, comparison) ||
            !string.Equals(Path.GetDirectoryName(temporary), Path.GetDirectoryName(destination), comparison))
            throw new BridgeException("PackageInvalid", "Manifest publication requires distinct files in the same parent directory.");
        EnsureNoLinks(temporary); EnsureNoLinks(destination);
        string prepared = FileHash(temporary); string? prior = File.Exists(destination) ? FileHash(destination) : null;
        void Unchanged()
        {
            EnsureNoLinks(temporary); EnsureNoLinks(destination);
            if (Directory.Exists(destination) || !File.Exists(temporary) || FileHash(temporary) != prepared ||
                (File.Exists(destination) ? FileHash(destination) : null) != prior)
                throw new BridgeException("PackageChanged", "Prepared manifest or prior destination changed before publication.");
        }
        for (int attempt = 1; ; attempt++)
        {
            Unchanged();
            try { move(temporary, destination); }
            // Only retry the pinned local rename; never replay serialization, upload or native operations.
            catch (Exception error) when (OperatingSystem.IsWindows() && attempt < 10 &&
                error is IOException or UnauthorizedAccessException &&
                error.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021) &&
                File.Exists(temporary))
            { Unchanged(); delay(25); continue; }
            EnsureNoLinks(destination);
            if (File.Exists(temporary) || !File.Exists(destination) || FileHash(destination) != prepared)
                throw new BridgeException("PackageChanged", "Manifest publication did not preserve the prepared bytes.");
            return;
        }
    }

    internal static T Load<T>(string path)
    {
        EnsureNoLinks(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64L << 20) throw new BridgeException("PackageInvalid", "Intake manifest exceeds its read limit.");
            return JsonSerializer.Deserialize<T>(stream, BridgeProtocol.Json) ?? throw new BridgeException("PackageInvalid", "Empty intake manifest.");
        }
        catch (JsonException error) { throw new BridgeException("PackageInvalid", "Intake manifest is corrupt.", error); }
        catch (FileNotFoundException error) { throw new BridgeException("IntakeNotFound", "The requested intake record does not exist.", error); }
        catch (DirectoryNotFoundException error) { throw new BridgeException("IntakeNotFound", "The requested intake record does not exist.", error); }
    }

    internal static string FileHash(string path)
    {
        EnsureNoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    internal static void FreeSpace(string path, long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (drive.IsReady && drive.AvailableFreeSpace < bytes)
            throw new BridgeException("PackageLimitExceeded", "Insufficient free space for this bounded intake operation.");
    }

    internal static long StoredBytes(string root)
    {
        long total = 0;
        foreach (var item in Directory.EnumerateFileSystemEntries(root))
        {
            EnsureNoLinks(item);
            total = checked(total + (Directory.Exists(item) ? StoredBytes(item) : new FileInfo(item).Length));
        }
        return total;
    }

    internal static void DeleteOwned(string root, string directory)
    {
        var full = Under(root, Path.GetRelativePath(root, directory));
        if (!Directory.Exists(full)) return;
        // Validate every descendant before recursive deletion; never traverse a link.
        _ = StoredBytes(full);
        Directory.Delete(full, true);
    }
}
