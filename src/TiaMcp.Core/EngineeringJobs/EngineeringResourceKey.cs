using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TiaMcp.Core.Engineering;
using System.Text.Json;

namespace TiaMcp.Core.EngineeringJobs;

/// <summary>Shared local-store reservation keys. External identity normalization belongs to its trusted provider.</summary>
public static class EngineeringResourceKey
{
    public static string ForPath(string kind, string absolutePath)
    {
        if (kind is not ("project" or "library" or "workspace") || string.IsNullOrWhiteSpace(absolutePath) ||
            !Path.IsPathFullyQualified(absolutePath) || absolutePath.Length > 8192 || absolutePath.Any(char.IsControl) ||
            absolutePath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new BridgeException("InvalidResourceKey", "An exact absolute project/library/workspace path is required.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolutePath));
        string relative = Path.GetRelativePath(Path.GetPathRoot(full)!, full);
        _ = PackageStorage.Relative(relative);
        PackageStorage.EnsureNoLinks(full);
        if (OperatingSystem.IsWindows()) full = WindowsIdentity(full);
        return kind + ":" + (OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full);
    }

    internal static void Validate(EngineeringResourceEffect resource)
    {
        if (resource.Kind == "external")
        {
            if (resource.External is null || ForExternal(resource.External.Identity) != resource.ResourceKey)
                throw new BridgeException("InvalidResourceKey", "External effects require an exact provider identity and canonical key.");
            EngineeringExternalEvidence.ValidateState(resource.External.Before); EngineeringExternalEvidence.ValidateState(resource.External.After);
            return;
        }
        if (resource.External is not null) throw new BridgeException("InvalidResourceKey", "A local path resource cannot carry an external identity.");
        string prefix = resource.Kind + ":";
        if (!resource.ResourceKey.StartsWith(prefix, StringComparison.Ordinal) ||
            ForPath(resource.Kind, resource.ResourceKey[prefix.Length..]) != resource.ResourceKey)
            throw new BridgeException("InvalidResourceKey", "Use the shared canonical resource key factory.");
    }

    internal static bool Overlaps(string first, string second)
    {
        if (first.StartsWith("external:", StringComparison.Ordinal) || second.StartsWith("external:", StringComparison.Ordinal))
            return first == second || first.StartsWith(second + ":", StringComparison.Ordinal) || second.StartsWith(first + ":", StringComparison.Ordinal);
        string a = first[(first.IndexOf(':') + 1)..];
        string b = second[(second.IndexOf(':') + 1)..];
        return a == b || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>Null resource kind/id reserves the whole exact owner. No URL/name alias guessing.</summary>
    public static string ForExternal(EngineeringExternalIdentity identity)
    {
        if (identity is null) throw new BridgeException("InvalidResourceKey", "External identity is required.");
        Exact(identity.Provider); Exact(identity.Authority); Exact(identity.OwnerId);
        if ((identity.ResourceKind is null) != (identity.ResourceId is null)) throw new BridgeException("InvalidResourceKey", "External resource kind and identity must both be present or both null for owner scope.");
        string owner = "external:" + CanonicalJson.Sha256(JsonSerializer.SerializeToNode(new { identity.Provider, identity.Authority, identity.OwnerId }, BridgeProtocol.Json)!);
        if (identity.ResourceKind is null) return owner;
        Exact(identity.ResourceKind); Exact(identity.ResourceId!);
        return owner + ":" + CanonicalJson.Sha256(JsonSerializer.SerializeToNode(new { identity.ResourceKind, identity.ResourceId }, BridgeProtocol.Json)!);
    }
    private static void Exact(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl)) throw new BridgeException("InvalidResourceKey", "External selectors must be exact bounded public text.");
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { throw new BridgeException("InvalidResourceKey", "External selectors contain invalid Unicode."); }
    }

    private static string WindowsIdentity(string full)
    {
        if (GetDriveType(Path.GetPathRoot(full)!) == 4) // DRIVE_REMOTE
            throw new BridgeException("InvalidResourceKey", "Mapped/network resource aliases require a separately identified remote owner.");
        string existing = full;
        var suffix = new Stack<string>();
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            suffix.Push(Path.GetFileName(existing)); existing = Path.GetDirectoryName(existing)!;
            if (existing is null) throw new BridgeException("InvalidResourceKey", "No existing resource ancestor can establish filesystem identity.");
        }
        // The caller supplies a validated local path; native lookup must not depend on process MAX_PATH opt-in.
        using var handle = CreateFile(@"\\?\" + existing, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
            throw new BridgeException("InvalidResourceKey", "Cannot establish native filesystem identity for the resource.");
        if ((info.Attributes & 0x10) == 0 && info.NumberOfLinks > 1)
            throw new BridgeException("InvalidResourceKey", "Multiply linked resource files cannot establish exclusive path ownership.");
        var name = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandle(handle, name, (uint)name.Capacity, 0);
        if (count == 0 || count >= name.Capacity) throw new BridgeException("InvalidResourceKey", "Cannot resolve the resource's canonical filesystem path.");
        string canonical = name.ToString();
        if (!canonical.StartsWith(@"\\?\", StringComparison.Ordinal) || canonical.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("InvalidResourceKey", "Only locally identified filesystem resources are supported by this reservation provider.");
        canonical = canonical[4..];
        while (suffix.TryPop(out var part)) canonical = Path.Combine(canonical, part);
        return Path.TrimEndingDirectorySeparator(canonical);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder name, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")]
    private static extern uint GetDriveType(string root);
}
