using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.Security;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TiaMcp.Credentials.Tests")]
namespace TiaMcp.Host;

internal sealed record CredentialOperationReference(string ArtifactRoot, string OperationId, string PlanSha256);
internal sealed record CredentialStatus(CredentialReference Reference, CredentialBinding Binding, string State, bool DecryptionVerified)
{
    public string Protection => "Windows DPAPI CurrentUser";
    public bool SecretReturned => false;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CredentialOperationReference>? OperationReferences { get; init; }
}

/// <summary>Local current-user storage. Only a trusted synchronous consumer receives temporarily decrypted bytes.</summary>
internal sealed partial class CredentialVault
{
    internal const int MaxSecretBytes = 65536;
    private const int MaxRecordBytes = 128 * 1024;
    private readonly string root;
    private readonly SecurityIdentifier user;
    private sealed record VaultHeader(string SchemaVersion, string VaultId, string UserSid);
    private sealed record StoredCredential(string SchemaVersion, CredentialReference Reference, CredentialBinding Binding, byte[] Ciphertext);
    private sealed record Lifecycle(string SchemaVersion, CredentialReference Reference, string State)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<CredentialOperationReference>? Pins { get; init; }
    }
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, MaxDepth = 8
    };

    internal CredentialVault(string path)
    {
        if (!OperatingSystem.IsWindows()) throw Error("CredentialPlatformUnsupported");
        using var identity = WindowsIdentity.GetCurrent();
        user = identity.User ?? throw Error("CredentialOwnerUnavailable");
        root = EngineeringResourceKey.ForPath("workspace", Path.GetFullPath(path))["workspace:".Length..];
        Plain(root);
        if (!Directory.Exists(root))
        {
            if (!Directory.Exists(Path.GetDirectoryName(root))) throw Error("CredentialDirectoryMissing");
            var acl = new DirectorySecurity(); acl.SetOwner(user); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).Create(acl);
        }
        CheckDirectory();
    }
    internal static string UserRoot(string name)
    {
        if (name != "default" && !IsId(name)) throw Error("InvalidCredentialVault");
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(profile)) throw Error("CredentialOwnerUnavailable");
        string parent = Path.Combine(profile, "TiaMcpProtectedCredentials");
        _ = new CredentialVault(parent);
        return Path.Combine(parent, name);
    }
    internal CredentialStatus Create(CredentialBinding binding, byte[] secret)
    {
        ValidateBinding(binding);
        if (secret is null || secret.Length is < 1 or > MaxSecretBytes) throw Error("InvalidCredentialLength");
        using var held = Lock(); var header = Header();
        if (Directory.EnumerateFileSystemEntries(root).Take(8194).Count() >= 8194) throw Error("CredentialCapacity");
        string id = Guid.NewGuid().ToString("N"); string bindingHash = BindingHash(binding);
        byte[]? encrypted = null;
        try
        {
            encrypted = ProtectedData.Protect(secret, Entropy(header, id, bindingHash), DataProtectionScope.CurrentUser);
            var reference = new CredentialReference("protected-credential-reference.v1", header.VaultId, id, bindingHash, Hash(encrypted));
            Publish(id + ".state.json", new Lifecycle("protected-credential-lifecycle.v1", reference, "active"));
            Publish(id + ".credential.json", new StoredCredential("protected-credential.v1", reference, binding, encrypted));
            return new(reference, binding, "active", false);
        }
        catch (CryptographicException) { throw Error("CredentialProtectionFailed"); }
        finally { if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted); }
    }
    internal CredentialStatus Inspect(CredentialReference reference, CredentialBinding expected)
    {
        using var held = Lock(); var header = Header(); var record = Read(reference, expected, header);
        var state = ReadLifecycle(record.Reference);
        if (state.State == "retired") return Status(record, state, false);
        Decrypt(record, header, _ => { });
        return Status(record, state, true);
    }
    internal void Use(CredentialReference reference, CredentialBinding expected, Action<byte[]> consumer)
    {
        using var held = Lock(); var header = Header(); var record = Read(reference, expected, header);
        var state = ReadLifecycle(record.Reference);
        if (state.State == "retired") throw Error("CredentialRetired");
        if (state.Pins is not null) throw Error("CredentialAdmissionRequired");
        Decrypt(record, header, consumer);
    }
    internal CredentialStatus Retire(CredentialReference reference, CredentialBinding expected)
    {
        using var held = Lock(); var record = Read(reference, expected, Header());
        var state = ReadLifecycle(record.Reference);
        VerifyReleased(state);
        if (state.State != "retired") Publish(reference.CredentialId + ".state.json", state with { State = "retired" }, replace: true);
        return Status(record, state with { State = "retired" }, false);
    }
    internal IReadOnlyList<CredentialStatus> List()
    {
        using var held = Lock(); var header = Header(); var entries = Directory.EnumerateFiles(root, "*.credential.json").Take(4097).ToArray();
        if (entries.Length > 4096) throw Error("CredentialCapacity");
        return entries.Order(StringComparer.Ordinal).Select(path =>
        {
            var record = ReadJson<StoredCredential>(path);
            ValidateRecord(record, header);
            if (Path.GetFileName(path) != record.Reference.CredentialId + ".credential.json") throw Error("CredentialUnavailable");
            return Status(record, ReadLifecycle(record.Reference), false);
        }).ToArray();
    }
    private StoredCredential Read(CredentialReference reference, CredentialBinding expected, VaultHeader header)
    {
        ValidateReference(reference); ValidateBinding(expected);
        if (reference.VaultId != header.VaultId || reference.BindingSha256 != BindingHash(expected)) throw Error("CredentialContextMismatch");
        var record = ReadJson<StoredCredential>(Path.Combine(root, reference.CredentialId + ".credential.json"));
        ValidateRecord(record, header);
        if (record.Reference != reference || BindingHash(record.Binding) != BindingHash(expected)) throw Error("CredentialContextMismatch");
        return record;
    }
    private static void ValidateRecord(StoredCredential record, VaultHeader header)
    {
        if (record is null || record.SchemaVersion != "protected-credential.v1") throw Error("CredentialUnavailable");
        ValidateReference(record.Reference); ValidateBinding(record.Binding);
        if (record.Reference.VaultId != header.VaultId || record.Reference.BindingSha256 != BindingHash(record.Binding) ||
            record.Ciphertext is null || record.Ciphertext.Length is < 1 or > 100000 || record.Reference.CipherSha256 != Hash(record.Ciphertext)) throw Error("CredentialUnavailable");
    }
    private void Decrypt(StoredCredential record, VaultHeader header, Action<byte[]> consumer)
    {
        byte[]? clear = null;
        try
        {
            clear = ProtectedData.Unprotect(record.Ciphertext, Entropy(header, record.Reference.CredentialId, record.Reference.BindingSha256), DataProtectionScope.CurrentUser);
            if (clear.Length is < 1 or > MaxSecretBytes) throw Error("CredentialUnavailable");
            try { consumer(clear); }
            catch { throw Error("CredentialConsumerFailed"); }
        }
        catch (CryptographicException) { throw Error("CredentialUnavailable"); }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }
    private Lifecycle ReadLifecycle(CredentialReference reference)
    {
        string path = Path.Combine(root, reference.CredentialId + ".state.json");
        if (!File.Exists(path)) throw Error("CredentialUnavailable");
        var marker = ReadJson<Lifecycle>(path);
        if (marker.Reference != reference || marker.State is not ("active" or "retired")) throw Error("CredentialUnavailable");
        if (marker.SchemaVersion == "protected-credential-lifecycle.v1")
        {
            if (marker.Pins is not null) throw Error("CredentialUnavailable");
        }
        else if (marker.SchemaVersion == "protected-credential-lifecycle.v2")
        {
            if (marker.Pins is null || marker.Pins.Count is < 1 or > 64 || marker.Pins.Any(p => p is null) ||
                marker.Pins.Select(p => (p.ArtifactRoot, p.OperationId)).Distinct().Count() != marker.Pins.Count) throw Error("CredentialUnavailable");
            foreach (var pin in marker.Pins)
            {
                if (!IsId(pin.OperationId) || CanonicalRoot(pin.ArtifactRoot) != pin.ArtifactRoot) throw Error("CredentialUnavailable");
                EngineeringIdentityGuard.Sha256(pin.PlanSha256, "operation plan");
            }
        }
        else throw Error("CredentialUnavailable");
        return marker;
    }
    private VaultHeader Header()
    {
        string path = Path.Combine(root, "vault.json");
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            // Never invent a new owner after losing metadata for an existing store.
            if (Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) != ".lock")) throw Error("CredentialUnavailable");
            Publish("vault.json", new VaultHeader("protected-credential-vault.v1", Guid.NewGuid().ToString("N"), user.Value));
        }
        var header = ReadJson<VaultHeader>(path);
        if (header.SchemaVersion != "protected-credential-vault.v1" || !IsId(header.VaultId) || header.UserSid != user.Value) throw Error("CredentialOwnerMismatch");
        return header;
    }
    private byte[] Entropy(VaultHeader header, string id, string binding) => Encoding.UTF8.GetBytes("TiaMcp.ProtectedCredential.v1\n" + root.ToUpperInvariant() + "\n" + header.UserSid + "\n" + header.VaultId + "\n" + id + "\n" + binding);
    private FileStream Lock()
    {
        CheckDirectory(); string path = Path.Combine(root, ".lock");
        try
        {
            if (!File.Exists(path))
            {
                try { using var created = SecureCreate(path); created.Flush(true); }
                catch (IOException) when (File.Exists(path)) { }
            }
            CheckFile(path);
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { throw Error("CredentialStoreBusy"); }
    }
    private void Publish<T>(string name, T value, bool replace = false)
    {
        string temp = Path.Combine(root, ".pending-" + Guid.NewGuid().ToString("N"));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaxRecordBytes) throw Error("CredentialCapacity");
        try
        {
            using (var output = SecureCreate(temp)) { output.Write(bytes); output.Flush(true); }
            string destination = Path.Combine(root, name);
            if (replace) { CheckFile(destination); JournalFileReplacement.Replace(temp, destination); }
            else File.Move(temp, destination, false);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temp)) File.Delete(temp); }
    }
    private FileStream SecureCreate(string path)
    {
        Plain(path);
        var acl = new FileSecurity(); acl.SetOwner(user); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, acl);
    }
    private T ReadJson<T>(string path)
    {
        CheckFile(path); return Parse<T>(ReadBounded(path, MaxRecordBytes));
    }
    internal static byte[] ReadBounded(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 || file.Length > maximum) throw Error("CredentialInputInvalid");
        byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw Error("CredentialInputInvalid");
        return bytes;
    }
    internal static T Parse<T>(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            void Unique(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var field in element.EnumerateObject()) { if (!seen.Add(field.Name)) throw Error("CredentialInputInvalid"); Unique(field.Value); }
                }
                else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
            }
            Unique(doc.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Error("CredentialInputInvalid");
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException) { throw Error("CredentialInputInvalid"); }
    }
    private void CheckDirectory() { Plain(root); CheckAcl(new DirectoryInfo(root).GetAccessControl(), true); }
    private void CheckFile(string path) { Plain(path); CheckAcl(new FileInfo(path).GetAccessControl(), false); }
    private void CheckAcl(FileSystemSecurity acl, bool directory)
    {
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        var flags = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        if (acl.GetOwner(typeof(SecurityIdentifier)) != user || !acl.AreAccessRulesProtected || rules.Length != 1 || rules[0].IdentityReference != user ||
            rules[0].AccessControlType != AccessControlType.Allow || rules[0].FileSystemRights != FileSystemRights.FullControl || rules[0].IsInherited ||
            rules[0].InheritanceFlags != flags || rules[0].PropagationFlags != PropagationFlags.None) throw Error("CredentialAclUnsafe");
    }
    private static void Plain(string path) { _ = EngineeringResourceKey.ForPath("workspace", path); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string BindingHash(CredentialBinding binding) => ProtectedCredentialGuard.BindingHash(binding);
    internal static void ValidateBinding(CredentialBinding binding) => ProtectedCredentialGuard.ValidateBinding(binding);
    private static bool IsId(string? value) => ProtectedCredentialGuard.IsId(value);
    private static void ValidateReference(CredentialReference reference) => ProtectedCredentialGuard.ValidateReference(reference);
    internal static BridgeException Error(string code) => ProtectedCredentialGuard.Error(code);
}
