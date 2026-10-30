using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.Security;
using TiaMcp.Core.WatchTables;
using TiaMcp.Host;
using TiaMcp.Security;

/// <summary>The Host-to-worker credential route through the real admission dispatcher, a fake worker and actual current-user DPAPI.</summary>
internal static class RouteChecks
{
    private const string Lease = "0123456789abcdef0123456789abcdef";
    private const string Plc = "/projects/RouteFixture/devices/PLC_1/items/PLC_1/plc/PLC_1";

    internal static void Run(string output, Action<bool, string> check)
    {
        Directory.CreateDirectory(output);
        string root = Path.Combine(output, "artifacts"); Directory.CreateDirectory(root);
        var vault = new CredentialVault(Path.Combine(output, "default"));
        CredentialRoute Route(IBridgeClient native) => new(native, name => new CredentialVault(Path.Combine(output, name)));
        CredentialBinding Block(string block, string kind) => new(new("siemensTiaPortalV21", Plc, Plc + "/blocks/" + block, "blockProtection", kind), "blockProtection", "blockPassword");
        string password = "SYNTHETIC-ROUTE-" + Guid.NewGuid().ToString("N") + "-Ω", other = "SYNTHETIC-NEXT-" + Guid.NewGuid().ToString("N");
        var knowHow = vault.Create(Block("FB_A", "knowHow"), Encoding.UTF8.GetBytes(password));
        var write = vault.Create(Block("FB_A", "write"), Encoding.UTF8.GetBytes(password));
        var next = vault.Create(Block("FB_A", "write"), Encoding.UTF8.GetBytes(other));
        int project = 0;
        JsonObject Slot(string slot, CredentialStatus status, CredentialBinding? binding = null, string vaultName = "default") => new()
        {
            ["slot"] = slot, ["vaultName"] = vaultName, ["reference"] = JsonSerializer.SerializeToNode(status.Reference, BridgeProtocol.Json),
            ["binding"] = JsonSerializer.SerializeToNode(binding ?? status.Binding, BridgeProtocol.Json)
        };
        async Task<(RouteNative Native, JsonElement? Result, BridgeException? Error)> Apply(params JsonObject[] slots)
        {
            var native = new RouteNative(slots.Length == 0 ? null : new JsonArray(slots), Path.Combine(root, "p" + project++ + ".ap21"));
            try { return (native, await new WatchTableDispatcher(Route(native), root).CallAsync("change_apply", new { changeId = "c1", previewHash = new string('a', 64), idempotencyKey = "k" + project, checkpointSave = true }), null); }
            catch (BridgeException error) { return (native, null, error); }
        }
        int Admissions() => Directory.GetDirectories(Path.Combine(root, "engineering-admissions")).Length;
        bool Leaks(byte[] haystack) => new[] { password, other }.Any(text => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0 ||
            haystack.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0 || haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)))) >= 0);
        string Opened(JsonObject dispatched, string slot, CredentialStatus status, string lease = Lease, string change = "c1", string? asSlot = null,
            string? previewHash = null, string? credentialId = null, string? bindingSha256 = null)
        {
            var envelope = dispatched["credentials"]!.AsArray().Single(row => row!["slot"]!.GetValue<string>() == slot)!["envelope"]!.GetValue<string>();
            using var secure = CredentialRouteEnvelope.Open(Convert.FromBase64String(envelope), CredentialRouteEnvelope.Entropy(lease, change, previewHash ?? new string('a', 64),
                asSlot ?? slot, credentialId ?? status.Reference.CredentialId, bindingSha256 ?? status.Reference.BindingSha256));
            return secure is null ? "<sealed>" : new NetworkCredential(string.Empty, secure).Password;
        }

        var applied = Apply(Slot("credential", knowHow)).GetAwaiter().GetResult();
        check(applied.Error is null && applied.Native.Calls.SequenceEqual(["mutation_begin", "mutation_dispatch", "mutation_end"]), "Route seals the vault secret before admission and dispatches once");
        var dispatched = applied.Native.Dispatched!;
        check(Opened(dispatched, "credential", knowHow) == password, "Worker opens the routed password under the exact lease, change, slot and reference");
        check(Opened(dispatched, "credential", knowHow, lease: new string('1', 32)) == "<sealed>" && Opened(dispatched, "credential", knowHow, change: "c2") == "<sealed>" &&
              Opened(dispatched, "credential", knowHow, asSlot: "newCredential") == "<sealed>" && Opened(dispatched, "credential", write) == "<sealed>",
            "Envelope does not open for another lease, change, slot or reference");
        check(Opened(dispatched, "credential", knowHow, previewHash: new string('b', 64)) == "<sealed>", "Envelope does not open for another preview hash alone");
        check(Opened(dispatched, "credential", knowHow, credentialId: write.Reference.CredentialId) == "<sealed>", "Envelope does not open for another credential ID alone");
        check(Opened(dispatched, "credential", knowHow, bindingSha256: write.Reference.BindingSha256) == "<sealed>", "Envelope does not open for another binding hash alone");
        check(!CredentialRouteEnvelope.Entropy(Lease, "c1\nX", "p", "credential", "i", "b").SequenceEqual(CredentialRouteEnvelope.Entropy(Lease, "c1", "X\np", "credential", "i", "b")) &&
              !CredentialRouteEnvelope.Entropy(Lease, "c1", "p", "credential", "i", "b").SequenceEqual(CredentialRouteEnvelope.Entropy(Lease, "c1p", "", "credential", "i", "b")),
            "Entropy fields carry their lengths, so moving a separator or characters between fields changes the entropy");
        check(!applied.Native.Frames.Any(frame => Leaks(Encoding.UTF8.GetBytes(frame))), "No bridge frame carries the plaintext secret");

        CredentialBinding Cpu(string purpose = "cpuProtection", string kind = "cpuProtection") => new(new("siemensTiaPortalV21", Plc, Plc.Split("/plc/")[0], kind, "fullAccess"), purpose, "fullAccessPassword");
        var fullAccess = vault.Create(Cpu(), Encoding.UTF8.GetBytes(password));
        var cpu = Apply(Slot("credential", fullAccess)).GetAwaiter().GetResult();
        check(cpu.Error is null && Opened(cpu.Native.Dispatched!, "credential", fullAccess) == password, "The CPU full-access password routes under the cpuProtection purpose");

        var changed = Apply(Slot("credential", write), Slot("newCredential", next)).GetAwaiter().GetResult();
        check(changed.Error is null && Opened(changed.Native.Dispatched!, "credential", write) == password && Opened(changed.Native.Dispatched!, "newCredential", next) == other,
            "Two slots route their own secrets, each bound to its slot");
        var plain = Apply().GetAwaiter().GetResult();
        check(plain.Error is null && plain.Native.Dispatched!["credentials"] is null, "A change without credential slots dispatches unchanged");

        void Refused((RouteNative Native, JsonElement? Result, BridgeException? Error) outcome, string code, string label)
        {
            check(outcome.Error?.Code == code && outcome.Native.Calls.SequenceEqual(["mutation_begin", "mutation_end"]), label);
        }
        int admitted = Admissions();
        var mismatch = Apply(Slot("credential", vault.Create(Block("FB_B", "knowHow"), Encoding.UTF8.GetBytes(password)), Block("FB_A", "knowHow"))).GetAwaiter().GetResult();
        Refused(mismatch, "CredentialContextMismatch", "A reference bound to another block refuses before admission and releases the lease");
        var details = (mismatch.Error as BridgeOperationException)!.Details!.Value;
        check(details.GetProperty("expectedBinding").GetProperty("resource").GetProperty("ownerId").GetString() == Plc + "/blocks/FB_A" &&
              details.GetProperty("expectedBindingSha256").GetString() == ProtectedCredentialGuard.BindingHash(Block("FB_A", "knowHow")), "Mismatch names the expected public binding");
        Refused(Apply(Slot("credential", knowHow, Block("FB_A", "write"))).GetAwaiter().GetResult(), "CredentialContextMismatch", "A know-how reference cannot serve write protection");
        var missing = knowHow with { Reference = knowHow.Reference with { CredentialId = Guid.NewGuid().ToString("N") } };
        Refused(Apply(Slot("credential", missing)).GetAwaiter().GetResult(), "CredentialUnavailable", "A missing credential refuses as CredentialUnavailable");
        var retired = vault.Create(Block("FB_A", "knowHow"), Encoding.UTF8.GetBytes(password)); vault.Retire(retired.Reference, retired.Binding);
        Refused(Apply(Slot("credential", retired)).GetAwaiter().GetResult(), "CredentialRetired", "A retired credential refuses");
        Refused(Apply(Slot("credential", vault.Create(Block("FB_A", "knowHow"), [0xff, 0xfe]))).GetAwaiter().GetResult(), "CredentialFormatInvalid", "A non-UTF-8 secret refuses before sealing");
        var login = new CredentialBinding(new("siemensWebApiRuntime", "https://plc.example", new string('c', 64), "authenticationMode", "local"), "runtimeLogin", "user");
        Refused(Apply(Slot("credential", vault.Create(login, Encoding.UTF8.GetBytes(password)))).GetAwaiter().GetResult(), "CredentialRouteInvalid", "A runtime login credential is not a routable purpose");
        Refused(Apply(Slot("credential", vault.Create(Cpu(kind: "blockProtection"), Encoding.UTF8.GetBytes(password)))).GetAwaiter().GetResult(), "CredentialRouteInvalid",
            "A routed binding must name a resource of its own purpose");
        Refused(Apply(Slot("credential", fullAccess, Block("FB_A", "knowHow"))).GetAwaiter().GetResult(), "CredentialContextMismatch", "A CPU full-access credential cannot serve block protection");
        Refused(Apply(Slot("credential", knowHow, Cpu())).GetAwaiter().GetResult(), "CredentialContextMismatch", "A block credential cannot serve the CPU protection level");
        Refused(Apply(Slot("credential", knowHow), Slot("newCredential", next, vaultName: new string('e', 32))).GetAwaiter().GetResult(), "CredentialRouteInvalid", "Slots of one apply share one vault");
        Refused(Apply(Slot("credential", knowHow), Slot("credential", write)).GetAwaiter().GetResult(), "CredentialRouteInvalid", "Duplicate slot names refuse");
        var saved = CredentialVault.LockWait; CredentialVault.LockWait = TimeSpan.FromMilliseconds(300);
        try
        {
            using var held = new FileStream(Path.Combine(output, "default", ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Refused(Apply(Slot("credential", knowHow)).GetAwaiter().GetResult(), "CredentialStoreBusy", "A held vault refuses as CredentialStoreBusy before admission");
        }
        finally { CredentialVault.LockWait = saved; }
        check(Admissions() == admitted, "No refused credential leaves an admission record");

        var direct = new RouteNative(new JsonArray(Slot("credential", knowHow)), Path.Combine(root, "direct.ap21"));
        try { Route(direct).CallAsync("mutation_begin", new { operation = "change_restore", arguments = new { changeId = "c1", previewHash = new string('a', 64) } }).GetAwaiter().GetResult(); check(false, "Expected refusal"); }
        catch (BridgeException error) when (error.Code == "CredentialRouteInvalid") { check(direct.Calls.SequenceEqual(["mutation_begin", "mutation_end"]), "Only change_apply may request credentials"); }
        // mutation_end needs the exact token, so a reply without one cannot be ended; nothing is sealed or dispatched.
        var tokenless = new RouteNative(new JsonArray(Slot("credential", knowHow)), Path.Combine(root, "tokenless.ap21"), leaseToken: false);
        try { new WatchTableDispatcher(Route(tokenless), root).CallAsync("change_apply", new { changeId = "c1", previewHash = new string('a', 64), idempotencyKey = "tokenless", checkpointSave = true }).GetAwaiter().GetResult(); check(false, "Expected refusal"); }
        catch (BridgeException error) when (error.Code == "CredentialRouteInvalid") { check(tokenless.Calls.SequenceEqual(["mutation_begin"]), "A begin reply with credentials but no lease token refuses before admission and dispatch"); }

        foreach (string path in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith(".credential.json", StringComparison.Ordinal)))
            if (Leaks(File.ReadAllBytes(path))) throw new Exception("Plaintext secret retained in " + path);
        check(true, "No admission record, response or receipt retains the plaintext secret");
    }

    private sealed class RouteNative(JsonArray? credentials, string project, bool leaseToken = true) : IBridgeClient
    {
        internal List<string> Calls { get; } = [];
        internal List<string> Frames { get; } = [];
        internal JsonObject? Dispatched;
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(operation); Frames.Add(BridgeProtocol.EncodeRequest("fixture", operation, arguments));
            if (operation == "mutation_dispatch") Dispatched = JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject();
            var scope = new EngineeringMutationScope("change_apply", new('b', 64), new('c', 64), "worker-a", new('d', 64), 123, new('f', 64),
                [new("project", project, "checkpointMutationCompileSave")]);
            JsonNode value = operation switch
            {
                "mutation_begin" => new JsonObject { ["leaseToken"] = leaseToken ? Lease : null, ["scope"] = JsonSerializer.SerializeToNode(scope, BridgeProtocol.Json), ["credentials"] = credentials?.DeepClone() },
                "mutation_dispatch" => new JsonObject { ["state"] = "applied" },
                "mutation_end" => new JsonObject { ["released"] = true },
                _ => throw new Exception("Unadmitted native operation: " + operation)
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(value, BridgeProtocol.Json));
        }
    }
}
