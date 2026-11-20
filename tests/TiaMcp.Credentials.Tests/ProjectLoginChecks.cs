using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;
using TiaMcp.Core;
using TiaMcp.Core.Security;
using TiaMcp.Host;
using RoutedCredentials = TiaMcp.Openness.V21.RoutedCredentials;
using WorkerError = TiaMcp.Openness.V21.BridgeException;

/// <summary>The project_open UMAC login route: the Host seals a vault password for one exact project path and user, and the
/// worker half opens it only for that path and user, against actual current-user DPAPI and a fake worker.</summary>
internal static class ProjectLoginChecks
{
    internal static void Run(string output, Action<bool, string> check)
    {
        Directory.CreateDirectory(output);
        var vault = new CredentialVault(Path.Combine(output, "default"));
        CredentialRoute Route(IBridgeClient native) => new(native, name => new CredentialVault(Path.Combine(output, name)));
        string project = Path.GetFullPath(Path.Combine(output, "Protected", "Protected.ap21"));
        string password = "SYNTHETIC-UMAC-" + Guid.NewGuid().ToString("N") + "-Ω";
        var binding = CredentialRoute.ProjectLoginBinding(project, "Engineer", "project");
        var stored = vault.Create(binding, Encoding.UTF8.GetBytes(password));

        // The worker derives the same binding the Host hashes, so a credential created from either matches.
        var workerBinding = JsonSerializer.Deserialize<CredentialBinding>(RoutedCredentials.UmacBinding(project, "Engineer", "project").ToString(), BridgeProtocol.Json)!;
        check(ProtectedCredentialGuard.BindingHash(workerBinding) == stored.Reference.BindingSha256 && binding.Purpose == "projectUmac" &&
              binding.Resource.ResourceKind == binding.Purpose && binding.SubjectId == "projectUser" &&
              CredentialRoute.ProjectLoginBinding(project, "Engineer", "global").SubjectId == "globalUser",
            "Host and worker derive one UMAC binding: the project path, the user and its type, under the projectUmac purpose");

        async Task<(LoginNative Native, BridgeException? Error)> Open(object arguments)
        {
            var native = new LoginNative();
            try { await Route(native).CallAsync("project_open", arguments); return (native, null); }
            catch (BridgeException error) { return (native, error); }
        }
        object Login(CredentialStatus status, string path, string user = "Engineer", string type = "project", string? vaultName = null) => new
        {
            path, readOnly = false,
            umac = new { userName = user, userType = type, vaultName, reference = JsonSerializer.SerializeToElement(status.Reference, BridgeProtocol.Json) },
        };

        var opened = Open(Login(stored, project)).GetAwaiter().GetResult();
        check(opened.Error is null && opened.Native.Calls.SequenceEqual(["project_open"]), "A matching reference seals the login and calls the worker once");
        var sent = JObject.Parse(opened.Native.Sent!.ToJsonString());
        check(sent["umac"]!["reference"] is null && sent["umac"]!["vaultName"] is null && (string?)sent["umac"]!["userName"] == "Engineer" &&
              (string?)sent["umac"]!["credentialId"] == stored.Reference.CredentialId, "The worker receives the user, credential ID and envelope, not the reference");
        check(!Leaks(opened.Native.Frame!, password), "No bridge frame carries the plaintext password");
        string Opened(string path, Action<JObject>? tamper = null)
        {
            var umac = (JObject)sent["umac"]!.DeepClone();
            tamper?.Invoke(umac);
            try
            {
                using var secure = RoutedCredentials.OpenUmac(RoutedCredentials.ParseUmac(umac), path);
                return new NetworkCredential(string.Empty, secure).Password;
            }
            catch (WorkerError error) { return error.Code; }
        }
        check(Opened(project) == password, "The worker opens the password for the exact project path");
        check(Opened(Path.Combine(output, "Other", "Other.ap21")) == "CredentialContextMismatch", "The envelope does not open for another project path");
        check(Opened(project, u => u["userName"] = "Admin") == "CredentialContextMismatch" && Opened(project, u => u["userType"] = "global") == "CredentialContextMismatch",
            "The envelope does not open for another user or user type");
        check(Opened(project, u => u["nonce"] = new string('0', 32)) == "CredentialContextMismatch", "The envelope does not open under another nonce");
        check(Opened(project, u => u["password"] = "x") == "CredentialRouteInvalid" && Opened(project, u => u["userType"] = "admin") == "CredentialInputInvalid",
            "A login with extra fields or an unknown user type is refused");
        var second = Open(Login(stored, project)).GetAwaiter().GetResult();
        check(second.Error is null && (string?)JObject.Parse(second.Native.Sent!.ToJsonString())["umac"]!["nonce"] != (string?)sent["umac"]!["nonce"], "Every open seals under a fresh nonce");

        void Refused((LoginNative Native, BridgeException? Error) outcome, string code, string label) =>
            check(outcome.Error?.Code == code && outcome.Native.Calls.Count == 0, label);
        var mismatch = Open(Login(stored, Path.Combine(output, "Other", "Other.ap21"))).GetAwaiter().GetResult();
        Refused(mismatch, "CredentialContextMismatch", "A reference bound to another project path refuses before the worker is called");
        check((mismatch.Error as BridgeOperationException)?.Details?.GetProperty("expectedBinding").GetProperty("resource").GetProperty("resourceId").GetString() == "Engineer",
            "The mismatch names the expected public binding");
        Refused(Open(Login(stored, project, user: "Admin")).GetAwaiter().GetResult(), "CredentialContextMismatch", "A reference of another user refuses");
        Refused(Open(Login(stored, project, type: "global")).GetAwaiter().GetResult(), "CredentialContextMismatch", "A project user's reference cannot log in as a global user");
        Refused(Open(Login(stored, project, type: "admin")).GetAwaiter().GetResult(), "CredentialInputInvalid", "An unknown user type refuses");
        var missing = stored with { Reference = stored.Reference with { CredentialId = Guid.NewGuid().ToString("N") } };
        Refused(Open(Login(missing, project)).GetAwaiter().GetResult(), "CredentialUnavailable", "A missing credential refuses as CredentialUnavailable");
        var retired = vault.Create(binding, Encoding.UTF8.GetBytes(password)); vault.Retire(retired.Reference, retired.Binding);
        Refused(Open(Login(retired, project)).GetAwaiter().GetResult(), "CredentialRetired", "A retired credential refuses");
        Refused(Open(Login(vault.Create(binding, [0xff, 0xfe]), project)).GetAwaiter().GetResult(), "CredentialFormatInvalid", "A non-UTF-8 secret refuses before sealing");

        var plain = Open(new { path = project, readOnly = true }).GetAwaiter().GetResult();
        check(plain.Error is null && plain.Native.Sent!["umac"] is null && plain.Native.Sent!["readOnly"]!.GetValue<bool>(), "project_open without a login passes through unchanged");
        check(RoutedCredentials.ParseUmac(null) is null && RoutedCredentials.ParseUmac(JValue.CreateNull()) is null, "The worker opens no login when none was sent");
    }

    private static bool Leaks(string frame, string password)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(frame);
        return bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(password)) >= 0 || bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(password)))) >= 0;
    }

    private sealed class LoginNative : IBridgeClient
    {
        internal List<string> Calls { get; } = [];
        internal JsonObject? Sent;
        internal string? Frame;
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(operation); Frame = BridgeProtocol.EncodeRequest("fixture", operation, arguments);
            Sent = JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject();
            return Task.FromResult(JsonSerializer.SerializeToElement(new { opened = true }, BridgeProtocol.Json));
        }
    }
}
