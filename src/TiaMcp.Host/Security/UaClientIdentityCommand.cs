using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Host;

internal static class UaClientIdentityCommand
{
    internal static int Run(string[] args)
    {
        try
        {
            if (args is ["help"])
            {
                Console.WriteLine("Protected OPC UA client identity (Windows current user):\n  --ua-identity-store create|inspect [--name default|32-hex-id]\n  --ua-identity-store export --output new-public-certificate.der [--name default|32-hex-id]\n  --ua-identity-store rotate --expected-sha256 current-certificate-sha256 [--name default|32-hex-id]\nLaunch the Host with --ua-client-identity NAME. Stop all Hosts using that identity before inspection/export/rotation. Only public certificate/status is returned. Rotation retains the application URI but changes the key; provision the new public certificate in server trust before connecting. No private-key import/export, automatic replacement, session adoption or remote trust changes.");
                return 0;
            }
            if (args.Length == 0 || args[0] is not ("create" or "inspect" or "export" or "rotate")) throw CredentialVault.Error("UaClientIdentityCommandInvalid");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || args[i] is not ("--name" or "--output" or "--expected-sha256") || !options.TryAdd(args[i], args[i + 1]))
                    throw CredentialVault.Error("UaClientIdentityCommandInvalid");
            }
            if (options.ContainsKey("--output") != (args[0] == "export") || options.ContainsKey("--expected-sha256") != (args[0] == "rotate"))
                throw CredentialVault.Error("UaClientIdentityCommandInvalid");
            var vault = new CredentialVault(CredentialVault.UaIdentityRoot(options.GetValueOrDefault("--name", "default")));
            UaClientIdentityStatus status;
            if (args[0] == "create") status = vault.CreateUaIdentity();
            else if (args[0] == "rotate") status = vault.RotateUaIdentity(options["--expected-sha256"]);
            else
            {
                using var identity = vault.OpenUaIdentity(); status = identity.Status;
                if (args[0] == "export")
                {
                    string path = Path.GetFullPath(options["--output"]);
                    _ = EngineeringResourceKey.ForPath("workspace", path);
                    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    output.Write(Convert.FromBase64String(status.CertificateDerBase64)); output.Flush(true);
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(status, CredentialVault.Json));
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = e is BridgeException b ? b.Code : "UaClientIdentityUnavailable", privateKeyExported = false }, CredentialVault.Json));
            return 1;
        }
    }
}
