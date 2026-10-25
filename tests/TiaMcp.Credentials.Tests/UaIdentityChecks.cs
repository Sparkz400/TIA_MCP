using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opc.Ua;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Host;
using TiaMcp.OpcUa.Runtime;

internal static class UaIdentityChecks
{
    internal static void Probe(string root)
    {
        try { using var identity = new CredentialVault(root).OpenUaIdentity(); Console.WriteLine(identity.Status.CertificateSha256); }
        catch (BridgeException e) { Console.WriteLine(e.Code); }
    }

    internal static void Run(string output, Action<bool, string> check)
    {
        Directory.CreateDirectory(output);
        void Reject(Action action, string code, string name)
        {
            try { action(); } catch (BridgeException e) when (e.Code == code) { check(true, name); return; }
            throw new Exception("Expected " + code + ": " + name);
        }
        string root = Path.Combine(output, "identity"); var vault = new CredentialVault(root);
        string recordPath = Path.Combine(root, "ua-client-identity.json");
        Reject(() => vault.OpenUaIdentity(), "UaClientIdentityMissing", "Missing identity refuses without bootstrap");
        check(!File.Exists(Path.Combine(root, "vault.json")), "Inspect of missing identity creates no identity metadata");
        var created = vault.CreateUaIdentity();
        check(created.State == "current" && created.PrivateKeyStorage == "windowsDpapiCurrentUser" && !created.PrivateKeyExported && !created.PhysicalQualified,
            "Creation reports protected local identity without physical qualification");
        check(created.ExpiresAt > DateTime.UtcNow.AddMonths(11), "Persistent certificate has a bounded twelve-month lifetime");
        byte[] original = File.ReadAllBytes(recordPath);
        Reject(() => vault.CreateUaIdentity(), "UaClientIdentityAlreadyExists", "Create never overwrites an existing identity");
        Reject(() => vault.RotateUaIdentity(new string('0', 64)), "UaClientIdentityChanged", "Rotation requires exact current fingerprint");
        Reject(() => vault.RotateUaIdentity("wrong"), "UaClientIdentityFingerprintInvalid", "Malformed rotation fingerprint refuses");
        check(File.ReadAllBytes(recordPath).SequenceEqual(original), "Rejected create and rotation preserve stored bytes");
        using (var identity = vault.OpenUaIdentity())
        {
            check(identity.Status == created && identity.Certificate.HasPrivateKey, "Reopen imports exact certificate and its private key");
            using var rsa = identity.Certificate.GetRSAPrivateKey();
            using var publicKey = identity.Certificate.GetRSAPublicKey();
            var nonce = RandomNumberGenerator.GetBytes(32);
            check(publicKey!.VerifyData(nonce, rsa!.SignData(nonce, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                "Imported key proves possession against public certificate");
            Reject(() => new CredentialVault(root).OpenUaIdentity(), "CredentialStoreBusy", "Second owner cannot open live identity");
            Reject(() => vault.RotateUaIdentity(created.CertificateSha256), "CredentialStoreBusy", "Rotation refuses while a Host owns identity");
            check(ProbeProcess(root) == "CredentialStoreBusy", "Cross-process identity lease blocks concurrent use");
        }
        check(ProbeProcess(root) == created.CertificateSha256, "Fresh process reopens same certificate after owner exit");
        var holdInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { typeof(UaIdentityChecks).Assembly.Location, "--ua-hold", root }) holdInfo.ArgumentList.Add(argument);
        using (var child = Process.Start(holdInfo)!)
        {
            try
            {
                check(child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult() == "held", "Owned child acquired identity lease");
                Reject(() => vault.OpenUaIdentity(), "CredentialStoreBusy", "Original child process holds exclusive identity lock");
            }
            finally { if (!child.HasExited) child.Kill(); child.WaitForExit(); }
        }
        check(ProbeProcess(root) == created.CertificateSha256, "Actual owner process loss releases lease without replacing identity");
        using (var locked = new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool refused = false;
            try { vault.RotateUaIdentity(created.CertificateSha256); } catch (IOException) { refused = true; }
            check(refused, "Interrupted replacement refuses while original file is held");
        }
        check(File.ReadAllBytes(recordPath).SequenceEqual(original), "Failed publication preserves complete original identity");
        check(!Directory.EnumerateFiles(root, ".pending-*").Any(), "Failed publication removes its temporary ciphertext");
        var rotated = vault.RotateUaIdentity(created.CertificateSha256);
        check(rotated.ApplicationUri == created.ApplicationUri && rotated.CertificateSha256 != created.CertificateSha256,
            "Rotation preserves application URI and replaces certificate/key");
        check(ProbeProcess(root) == rotated.CertificateSha256, "Rotation persists across process restart");
        Reject(() => vault.RotateUaIdentity(created.CertificateSha256), "UaClientIdentityChanged", "Lost rotation reply cannot rotate again with stale fingerprint");
        original = File.ReadAllBytes(recordPath);
        void Tamper(Action<JsonNode> mutate, string code, string label)
        {
            try { var node = JsonNode.Parse(original)!; mutate(node); File.WriteAllBytes(recordPath, JsonSerializer.SerializeToUtf8Bytes(node)); Reject(() => vault.OpenUaIdentity(), code, label); }
            finally { File.WriteAllBytes(recordPath, original); }
        }
        Tamper(n => { byte[] bytes = Convert.FromBase64String(n["ciphertext"]!.GetValue<string>()); bytes[^1] ^= 1; n["ciphertext"] = Convert.ToBase64String(bytes); },
            "UaClientIdentityInvalid", "Damaged ciphertext fails DPAPI integrity without replacement");
        Tamper(n => n["certificateDerBase64"] = created.CertificateDerBase64, "UaClientIdentityInvalid", "Public certificate substitution is bound to protected key bytes");
        Tamper(n => n["applicationUri"] = "urn:tiamcp:client:" + Guid.NewGuid().ToString("N"), "UaClientIdentityInvalid", "Application URI substitution refuses");
        Tamper(n => n["extra"] = true, "CredentialInputInvalid", "Surplus identity record fields refuse");
        try { File.WriteAllText(recordPath, "{\"schemaVersion\":null,\"schemaVersion\":null}"); Reject(() => vault.OpenUaIdentity(), "CredentialInputInvalid", "Duplicate identity fields refuse"); }
        finally { File.WriteAllBytes(recordPath, original); }
        try { File.Move(recordPath, recordPath + ".held"); Reject(() => vault.OpenUaIdentity(), "UaClientIdentityMissing", "Missing record does not regenerate key"); Reject(() => vault.CreateUaIdentity(), "UaClientIdentityAlreadyExists", "Partial existing store cannot be recreated"); }
        finally { File.Move(recordPath + ".held", recordPath); }
        string header = Path.Combine(root, "vault.json");
        try { File.Move(header, header + ".held"); Reject(() => vault.OpenUaIdentity(), "UaClientIdentityMissing", "Missing owner header does not regenerate identity"); }
        finally { File.Move(header + ".held", header); }
        string copiedRoot = Path.Combine(output, "copied-identity"); var copied = new CredentialVault(copiedRoot);
        foreach (string source in Directory.EnumerateFiles(root).Where(p => Path.GetFileName(p) != ".lock"))
        {
            string target = Path.Combine(copiedRoot, Path.GetFileName(source)); File.Copy(source, target);
            var acl = new FileSecurity();
            acl.SetSecurityDescriptorSddlForm(new FileInfo(source).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner), AccessControlSections.Access | AccessControlSections.Owner);
            new FileInfo(target).SetAccessControl(acl);
        }
        Reject(() => copied.OpenUaIdentity(), "UaClientIdentityInvalid", "Copied protected identity cannot rebind to another path");
        var file = new FileInfo(recordPath); var originalAcl = file.GetAccessControl(); var unsafeAcl = file.GetAccessControl();
        unsafeAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        try { file.SetAccessControl(unsafeAcl); Reject(() => vault.OpenUaIdentity(), "CredentialAclUnsafe", "Widened identity ACL refuses"); }
        finally { var restore = new FileSecurity(); restore.SetSecurityDescriptorSddlForm(originalAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner), AccessControlSections.Access | AccessControlSections.Owner); file.SetAccessControl(restore); }
        string alias = Path.Combine(root, "alias");
        if (!Links.CreateHardLink(alias, recordPath, IntPtr.Zero)) throw new Exception("Hard-link fixture failed");
        try { Reject(() => vault.OpenUaIdentity(), "InvalidResourceKey", "Hard-linked identity record refuses"); }
        finally { File.Delete(alias); }
        foreach (bool future in new[] { false, true })
        {
            using var timed = CertificateFactory.CreateCertificate(rotated.ApplicationUri, "Synthetic identity time", "CN=Synthetic identity time", [System.Net.Dns.GetHostName()])
                .SetNotBefore(DateTime.UtcNow.AddDays(future ? 2 : -4)).SetNotAfter(DateTime.UtcNow.AddDays(future ? 4 : -2)).SetRSAKeySize(2048).CreateForRSA();
            ReplaceProtectedFixture(root, timed);
            using (var identity = vault.OpenUaIdentity())
            {
                check(identity.Status.State == (future ? "notYetValid" : "expired"), "Inspection accurately reports certificate time state");
                var store = new OpcUaRuntimeSessionStore(Path.Combine(output, future ? "future-runtime" : "expired-runtime"), protectedClientCertificate: identity.Certificate);
                try
                {
                    var selection = new OpcUaRuntimeSelection("opc.tcp://127.0.0.1:1/", "urn:synthetic:server", new string('a', 64), "anonymous", null, null, null);
                    Reject(() => store.Connect(Guid.NewGuid().ToString("N"), selection).GetAwaiter().GetResult(), "UaClientIdentityTimeInvalid", "Invalid client certificate refuses before any connection");
                }
                finally { store.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            using (var identity = vault.OpenUaIdentity()) rotated = identity.Status;
            rotated = vault.RotateUaIdentity(rotated.CertificateSha256);
            check(rotated.State == "current", "Expired or not-yet-valid identity can be explicitly rotated");
        }
        foreach (var invalid in new[] { "../escape", "", "DEFAULT", new string('A', 32) })
            Reject(() => CredentialVault.UaIdentityRoot(invalid), "UaClientIdentityNameInvalid", "Invalid identity name refuses path substitution");
        check(HostOptions.Parse(["--no-native-workers", "--ua-client-identity", "default"]).UaClientIdentity == "default", "Host selects explicit persistent identity");
        check(HostOptions.Parse(["--no-native-workers"]).UaClientIdentity is null, "Existing Host workflow retains ephemeral mode");
        File.WriteAllText(Path.Combine(output, "public-identity.json"), JsonSerializer.Serialize(rotated, CredentialVault.Json));
    }

    private static string ProbeProcess(string root)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { typeof(UaIdentityChecks).Assembly.Location, "--ua-probe", root }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(); process.WaitForExit(); throw new Exception("Owned identity probe timed out"); }
        if (process.ExitCode != 0 || error.GetAwaiter().GetResult().Length != 0) throw new Exception("Identity probe failed");
        return text.GetAwaiter().GetResult().Trim();
    }

    private static void ReplaceProtectedFixture(string root, X509Certificate2 certificate)
    {
        var header = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "vault.json")))!;
        var status = CredentialVault.UaStatus(certificate);
        byte[] entropy = Encoding.UTF8.GetBytes("TiaMcp.OpcUaClientIdentity.v1\n" + Path.GetFullPath(root).ToUpperInvariant() + "\n" + header["userSid"]!.GetValue<string>() + "\n" + header["vaultId"]!.GetValue<string>() + "\n" + status.ApplicationUri + "\n" + status.CertificateSha256);
        byte[] clear = certificate.Export(X509ContentType.Pkcs12);
        try
        {
            byte[] cipher = ProtectedData.Protect(clear, entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Path.Combine(root, "ua-client-identity.json"), JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "opcua-protected-client-store.v1", vaultId = header["vaultId"]!.GetValue<string>(), applicationUri = status.ApplicationUri, certificateDerBase64 = status.CertificateDerBase64, ciphertext = cipher }));
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
