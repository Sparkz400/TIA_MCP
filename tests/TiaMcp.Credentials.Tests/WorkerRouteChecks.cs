using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.Security;
using TiaMcp.Core.WatchTables;
using TiaMcp.Host;
using RoutedCredentials = TiaMcp.Openness.V21.RoutedCredentials;
using WorkerError = TiaMcp.Openness.V21.BridgeException;

/// <summary>The worker half of the credential route (RoutedCredentials) against the real Host route, dispatcher and current-user DPAPI.</summary>
internal static class WorkerRouteChecks
{
    private const string Lease = "fedcba9876543210fedcba9876543210";
    private const string Plc = "/projects/RouteFixture/devices/PLC_1/items/PLC_1/plc/PLC_1";
    private const string Cpu = "/projects/RouteFixture/devices/PLC_1/items/PLC_1";

    internal static void Run(string output, Action<bool, string> check)
    {
        Directory.CreateDirectory(output);
        string root = Path.Combine(output, "artifacts"); Directory.CreateDirectory(root);
        var vault = new CredentialVault(Path.Combine(output, "default"));
        string password = "SYNTHETIC-WORKER-" + Guid.NewGuid().ToString("N") + "-Ω", previewHash = new string('a', 64);
        void Refuses(Action action, string code, string label)
        {
            try { action(); }
            catch (WorkerError error) when (error.Code == code) { check(true, label); return; }
            throw new Exception("Expected " + code + ": " + label);
        }

        // The binding the worker derives for the CPU full-access password is the one the Host hashes and the vault stores.
        var binding = RoutedCredentials.Binding(Plc, Cpu, "cpuProtection", "fullAccess", "fullAccessPassword");
        var status = vault.Create(JsonSerializer.Deserialize<CredentialBinding>(binding.ToString(), BridgeProtocol.Json)!, Encoding.UTF8.GetBytes(password));
        check(status.Reference.BindingSha256 == ProtectedCredentialGuard.BindingHash(status.Binding) &&
              (string?)binding["resource"]!["provider"] == "siemensTiaPortalV21" && (string?)binding["resource"]!["resourceKind"] == (string?)binding["purpose"],
            "The worker-derived CPU binding names TIA V21, the PLC path, the CPU item and its own purpose");
        var payload = new JObject { ["hardwarePath"] = Cpu, ["credential"] = JObject.Parse(JsonSerializer.Serialize(status.Reference, BridgeProtocol.Json)) };
        RoutedCredentials.RequireVaultName(payload); RoutedCredentials.RequireReference(payload, "credential");
        var slots = RoutedCredentials.Slots(payload, binding, "credential", "newCredential");
        check(slots.Count == 1 && (string?)slots[0]["vaultName"] == "default" && JToken.DeepEquals(slots[0]["binding"], binding), "Preview slots list only the named references with their public binding");

        // Round trip: the Host seals, the worker accepts and opens under the exact lease, change and preview hash.
        var native = new WorkerNative(slots, previewHash, Path.Combine(root, "worker.ap21"));
        var result = new WatchTableDispatcher(new CredentialRoute(native, name => new CredentialVault(Path.Combine(output, name))), root)
            .CallAsync("change_apply", new { changeId = "c1", previewHash, idempotencyKey = "worker-route", checkpointSave = true }).GetAwaiter().GetResult();
        check(native.Opened == password && native.Calls.SequenceEqual(["mutation_begin", "mutation_dispatch", "mutation_end"]), "The worker opens the Host-sealed password at dispatch");
        check(native.Cleared, "Clear zeroes every routed envelope after the dispatch");
        var envelopes = native.Accepted!;

        // Accept: the dispatch must carry exactly the admitted change's slots.
        JArray Rows(params (string Slot, JToken Envelope)[] rows) => new(rows.Select(row => new JObject { ["slot"] = row.Slot, ["envelope"] = row.Envelope }));
        var valid = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        check(RoutedCredentials.Accept(null, slots) is null, "A dispatch without credentials routes none");
        Refuses(() => RoutedCredentials.Accept(Rows(("credential", valid)), null), "CredentialRouteInvalid", "Credentials for a change that requires none refuse");
        Refuses(() => RoutedCredentials.Accept(new JArray(), slots), "CredentialRouteInvalid", "A missing slot refuses");
        Refuses(() => RoutedCredentials.Accept(Rows(("newCredential", valid)), slots), "CredentialRouteInvalid", "A renamed slot refuses (slot-set mismatch)");
        Refuses(() => RoutedCredentials.Accept(Rows(("credential", valid), ("newCredential", valid)), slots), "CredentialRouteInvalid", "An extra slot refuses");
        Refuses(() => RoutedCredentials.Accept(new JArray(new JObject { ["slot"] = "credential", ["envelope"] = valid, ["password"] = "x" }), slots), "CredentialRouteInvalid", "A row with any other field refuses");
        Refuses(() => RoutedCredentials.Accept(Rows(("credential", 42)), slots), "CredentialRouteInvalid", "A non-string envelope refuses");
        Refuses(() => RoutedCredentials.Accept(Rows(("credential", "not base64!")), slots), "CredentialRouteInvalid", "A malformed envelope refuses");
        Refuses(() => RoutedCredentials.Accept(new JObject(), slots), "CredentialRouteInvalid", "Credentials that are not an array refuse");

        // Open: CredentialRequired without an envelope or lease, CredentialContextMismatch for any other apply.
        var sealedRows = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["credential"] = native.Envelope! };
        string? Open(Dictionary<string, byte[]>? routed, string? lease = Lease, string change = "c1", string? hash = null, string slot = "credential")
        {
            using SecureString secure = RoutedCredentials.Open(slots, routed, lease, change, hash ?? previewHash, slot);
            return new NetworkCredential(string.Empty, secure).Password;
        }
        check(Open(sealedRows) == password, "Open returns the password for the exact lease, change, preview hash and slot");
        Refuses(() => Open(null), "CredentialRequired", "No routed envelopes refuse as CredentialRequired");
        Refuses(() => Open(sealedRows, lease: null), "CredentialRequired", "No admitted lease refuses as CredentialRequired");
        Refuses(() => Open(new Dictionary<string, byte[]>(StringComparer.Ordinal)), "CredentialRequired", "An envelope missing for the slot refuses as CredentialRequired");
        Refuses(() => Open(sealedRows, slot: "newCredential"), "CredentialRouteInvalid", "A slot the prepared change does not name refuses");
        Refuses(() => Open(sealedRows, lease: new string('1', 32)), "CredentialContextMismatch", "Another lease does not open the envelope");
        Refuses(() => Open(sealedRows, change: "c2"), "CredentialContextMismatch", "Another change does not open the envelope");
        Refuses(() => Open(sealedRows, hash: new string('b', 64)), "CredentialContextMismatch", "Another preview hash does not open the envelope");
        RoutedCredentials.Clear(sealedRows); RoutedCredentials.Clear(null);
        check(sealedRows["credential"].All(b => b == 0) && envelopes.Values.All(bytes => bytes.All(b => b == 0)), "Clear zeroes envelopes and accepts none");

        // Payload references: only an exact protected-credential-reference.v1, never plaintext.
        Refuses(() => RoutedCredentials.RequireReference(new JObject { ["credential"] = password }, "credential"), "CredentialInputInvalid", "A plaintext password is refused as a reference");
        var extra = (JObject)payload["credential"]!.DeepClone(); extra["password"] = password;
        Refuses(() => RoutedCredentials.RequireReference(new JObject { ["credential"] = extra }, "credential"), "CredentialInputInvalid", "A reference with an extra field is refused");
        var upper = (JObject)payload["credential"]!.DeepClone(); upper["credentialId"] = upper.Value<string>("credentialId")!.ToUpperInvariant();
        Refuses(() => RoutedCredentials.RequireReference(new JObject { ["credential"] = upper }, "credential"), "CredentialInputInvalid", "A reference with non-canonical hex is refused");
        RoutedCredentials.RequireVaultName(new JObject { ["vaultName"] = new string('e', 32) });
        Refuses(() => RoutedCredentials.RequireVaultName(new JObject { ["vaultName"] = "../default" }), "CredentialInputInvalid", "A vault name outside default or 32 lowercase hex characters is refused");
        Refuses(() => RoutedCredentials.RequireVaultName(new JObject { ["vaultName"] = 7 }), "CredentialInputInvalid", "A non-string vault name is refused");
        _ = result;
    }

    /// <summary>A fake worker whose dispatch runs the real worker half: accept, open, clear.</summary>
    private sealed class WorkerNative(JArray slots, string previewHash, string project) : IBridgeClient
    {
        internal List<string> Calls { get; } = [];
        internal string? Opened;
        internal bool Cleared;
        internal byte[]? Envelope;
        internal Dictionary<string, byte[]>? Accepted;
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(operation);
            var scope = new EngineeringMutationScope("change_apply", new('b', 64), new('c', 64), "worker-a", new('d', 64), 123, new('f', 64),
                [new("project", project, "checkpointMutationCompileSave")]);
            JsonNode value;
            if (operation == "mutation_begin")
                value = new JsonObject { ["leaseToken"] = Lease, ["scope"] = JsonSerializer.SerializeToNode(scope, BridgeProtocol.Json), ["credentials"] = JsonNode.Parse(slots.ToString()) };
            else if (operation == "mutation_dispatch")
            {
                var args = JObject.Parse(JsonSerializer.Serialize(arguments, BridgeProtocol.Json));
                Accepted = RoutedCredentials.Accept(args["credentials"], slots);
                Envelope = (byte[])Accepted["credential"].Clone();
                using (var secure = RoutedCredentials.Open(slots, Accepted, args.Value<string>("leaseToken"), "c1", previewHash, "credential"))
                    Opened = new NetworkCredential(string.Empty, secure).Password;
                RoutedCredentials.Clear(Accepted);
                Cleared = Accepted.Values.All(bytes => bytes.All(b => b == 0));
                value = new JsonObject { ["state"] = "applied" };
            }
            else if (operation == "mutation_end") value = new JsonObject { ["released"] = true };
            else throw new Exception("Unadmitted native operation: " + operation);
            return Task.FromResult(JsonSerializer.SerializeToElement(value, BridgeProtocol.Json));
        }
    }
}
