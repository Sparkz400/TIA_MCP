using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Opc.Ua;
using TiaMcp.OpcUa.Runtime;

namespace TiaMcp.Host;

internal sealed record UaClientIdentityStatus(string SchemaVersion, string ApplicationUri, string CertificateSha256,
    string CertificateDerBase64, DateTime NotBefore, DateTime ExpiresAt, string State)
{
    public string PrivateKeyStorage => "windowsDpapiCurrentUser";
    public bool PrivateKeyExported => false;
    public bool PhysicalQualified => false;
}

internal sealed class UaClientIdentityLease(X509Certificate2 certificate, FileStream owner) : IDisposable
{
    internal X509Certificate2 Certificate { get; } = certificate;
    internal UaClientIdentityStatus Status => CredentialVault.UaStatus(Certificate);
    public void Dispose() { try { Certificate.Dispose(); } finally { owner.Dispose(); } }
}

internal sealed partial class CredentialVault
{
    private const string UaIdentityFile = "ua-client-identity.json";
    private sealed record StoredUaIdentity(string SchemaVersion, string VaultId, string ApplicationUri,
        string CertificateDerBase64, byte[] Ciphertext);

    internal static string UaIdentityRoot(string name)
    {
        if (name != "default" && !IsId(name)) throw Error("UaClientIdentityNameInvalid");
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(profile)) throw Error("CredentialOwnerUnavailable");
        string parent = Path.Combine(profile, "TiaMcpOpcUaIdentities");
        _ = new CredentialVault(parent);
        return Path.Combine(parent, name);
    }

    internal UaClientIdentityStatus CreateUaIdentity()
    {
        using var held = Lock();
        // Explicit creation cannot repair an interrupted or partially deleted store.
        if (Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) != ".lock"))
            throw Error("UaClientIdentityAlreadyExists");
        var header = Header();
        using var certificate = NewUaCertificate("urn:tiamcp:client:" + Guid.NewGuid().ToString("N"));
        WriteUaIdentity(certificate, header, false);
        return UaStatus(certificate);
    }

    internal UaClientIdentityLease OpenUaIdentity()
    {
        var held = Lock();
        try { return new(ReadUaIdentity(), held); }
        catch { held.Dispose(); throw; }
    }

    internal UaClientIdentityStatus RotateUaIdentity(string expectedSha256)
    {
        if (expectedSha256 is not { Length: 64 } || expectedSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Error("UaClientIdentityFingerprintInvalid");
        using var held = Lock();
        using var previous = ReadUaIdentity();
        var status = UaStatus(previous);
        if (status.CertificateSha256 != expectedSha256) throw Error("UaClientIdentityChanged");
        using var replacement = NewUaCertificate(status.ApplicationUri);
        WriteUaIdentity(replacement, Header(), true);
        return UaStatus(replacement);
    }

    private static X509Certificate2 NewUaCertificate(string uri) =>
        CertificateFactory.CreateCertificate(uri, "TIA MCP read-only UA", "CN=TIA MCP read-only UA",
            [System.Net.Dns.GetHostName()]).SetLifeTime(12).SetRSAKeySize(2048).CreateForRSA();

    internal static UaClientIdentityStatus UaStatus(X509Certificate2 certificate) => new(
        "opcua-protected-client-identity.v1", X509Utils.GetApplicationUrisFromCertificate(certificate).Single(),
        Hash(certificate.RawData), Convert.ToBase64String(certificate.RawData), certificate.NotBefore.ToUniversalTime(),
        certificate.NotAfter.ToUniversalTime(), OpcUaRuntimeSessionStore.ClientCertificateState(certificate));

    private byte[] UaEntropy(VaultHeader header, string uri, byte[] certificate) => Encoding.UTF8.GetBytes(
        "TiaMcp.OpcUaClientIdentity.v1\n" + root.ToUpperInvariant() + "\n" + header.UserSid + "\n" + header.VaultId + "\n" + uri + "\n" + Hash(certificate));

    private void WriteUaIdentity(X509Certificate2 certificate, VaultHeader header, bool replace)
    {
        byte[]? clear = null, cipher = null;
        try
        {
            string uri = X509Utils.GetApplicationUrisFromCertificate(certificate).Single();
            clear = certificate.Export(X509ContentType.Pkcs12);
            if (clear.Length > MaxSecretBytes) throw Error("CredentialCapacity");
            cipher = ProtectedData.Protect(clear, UaEntropy(header, uri, certificate.RawData), DataProtectionScope.CurrentUser);
            Publish(UaIdentityFile, new StoredUaIdentity("opcua-protected-client-store.v1", header.VaultId,
                uri, Convert.ToBase64String(certificate.RawData), cipher), replace);
        }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
            if (cipher is not null) CryptographicOperations.ZeroMemory(cipher);
        }
    }

    private X509Certificate2 ReadUaIdentity()
    {
        string path = Path.Combine(root, UaIdentityFile);
        if (!File.Exists(Path.Combine(root, "vault.json")) || !File.Exists(path)) throw Error("UaClientIdentityMissing");
        var header = Header();
        var record = ReadJson<StoredUaIdentity>(path);
        const string prefix = "urn:tiamcp:client:";
        if (record.SchemaVersion != "opcua-protected-client-store.v1" || record.VaultId != header.VaultId ||
            record.ApplicationUri is null || !record.ApplicationUri.StartsWith(prefix, StringComparison.Ordinal) ||
            !IsId(record.ApplicationUri[prefix.Length..]) || record.CertificateDerBase64 is not { Length: > 0 and <= 16384 } ||
            record.Ciphertext is not { Length: > 0 and <= 100000 }) throw Error("UaClientIdentityInvalid");
        byte[]? clear = null;
        X509Certificate2? certificate = null;
        try
        {
            byte[] der = Convert.FromBase64String(record.CertificateDerBase64);
            clear = ProtectedData.Unprotect(record.Ciphertext, UaEntropy(header, record.ApplicationUri, der), DataProtectionScope.CurrentUser);
            if (clear.Length is < 1 or > MaxSecretBytes) throw Error("UaClientIdentityInvalid");
            certificate = X509CertificateLoader.LoadPkcs12(clear, null, X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey || !certificate.RawData.SequenceEqual(der) ||
                !X509Utils.GetApplicationUrisFromCertificate(certificate).SequenceEqual([record.ApplicationUri]))
                throw Error("UaClientIdentityInvalid");
            using var key = certificate.GetRSAPrivateKey();
            using var publicKey = certificate.GetRSAPublicKey();
            byte[] challenge = RandomNumberGenerator.GetBytes(32);
            if (key is null || publicKey is null || key.KeySize < 2048 ||
                !publicKey.VerifyData(challenge, key.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw Error("UaClientIdentityInvalid");
            var result = certificate; certificate = null; return result;
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { throw Error("UaClientIdentityInvalid"); }
        finally { certificate?.Dispose(); if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }
}
