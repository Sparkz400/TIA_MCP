using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

internal static class RuntimeTlsFixtures
{
    internal static void Create(string directory)
    {
        if (Directory.Exists(directory)) throw new IOException("Fresh fixture directory required.");
        Directory.CreateDirectory(directory);
        var pins = new Dictionary<string,string>();
        foreach (string name in new[] { "server", "rotated", "wrong-name", "expired" })
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Runtime protocol fixture",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(name == "wrong-name" ? "wrong.invalid" : "localhost");
            if (name != "wrong-name") san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") },true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2),
                name == "expired" ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(2));
            File.WriteAllText(Path.Combine(directory,name+".crt"),certificate.ExportCertificatePem());
            File.WriteAllText(Path.Combine(directory,name+".key"),key.ExportPkcs8PrivateKeyPem());
            pins[name] = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
        }
        File.WriteAllText(Path.Combine(directory,"pins.json"),JsonSerializer.Serialize(pins));
        Console.WriteLine("PASS generated disposable loopback TLS fixtures; no OS trust store changes.");
    }
}
