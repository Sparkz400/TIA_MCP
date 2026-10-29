using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Security;
using TiaMcp.Security;

namespace TiaMcp.Host;

internal sealed record CredentialRouteSlot(string Slot, string VaultName, CredentialReference Reference, CredentialBinding Binding);

/// <summary>
/// Hands credential-store secrets to exactly one admitted worker change apply. It sits between the admission dispatcher and
/// the worker: the worker names the public slots in its mutation_begin reply, the Host validates and seals them before
/// admission, and only mutation_dispatch for that lease carries the sealed envelopes. No secret enters tool arguments.
/// </summary>
internal sealed class CredentialRoute(IBridgeClient inner, Func<string, CredentialVault>? vaults = null) : IBridgeClient
{
    // Purposes a worker may request. The CPU protection level's full-access password would join as "cpuProtection".
    private static readonly string[] Purposes = ["blockProtection"];
    private readonly Func<string, CredentialVault> open = vaults ?? (name => new CredentialVault(CredentialVault.UserRoot(name)));
    private string? lease;
    private List<(string Slot, byte[] Envelope)>? envelopes;

    public async Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
    {
        if (operation == "mutation_begin")
        {
            Clear();
            var begin = await inner.CallAsync(operation, arguments, cancellationToken);
            if (!begin.TryGetProperty("credentials", out var requested) || requested.ValueKind == JsonValueKind.Null) return begin;
            string token = begin.TryGetProperty("leaseToken", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid();
            try { envelopes = Seal(JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject(), requested, token); lease = token; return begin; }
            catch
            {
                // A refused credential never reaches admission; release the native lease the dispatcher has not seen yet.
                try { await inner.CallAsync("mutation_end", new { leaseToken = token }, CancellationToken.None); } catch { }
                throw;
            }
        }
        if (operation == "mutation_dispatch" && envelopes is not null)
        {
            var request = JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject();
            try
            {
                if (request["leaseToken"]?.GetValue<string>() != lease) throw Invalid();
                request["credentials"] = new JsonArray(envelopes.Select(e => (JsonNode)new JsonObject { ["slot"] = e.Slot, ["envelope"] = Convert.ToBase64String(e.Envelope) }).ToArray());
                return await inner.CallAsync(operation, request, cancellationToken);
            }
            finally { Clear(); }
        }
        if (operation == "mutation_end") Clear();
        return await inner.CallAsync(operation, arguments, cancellationToken);
    }

    private List<(string, byte[])> Seal(JsonObject begin, JsonElement requested, string token)
    {
        if (begin["operation"]?.GetValue<string>() != "change_apply" || begin["arguments"] is not JsonObject apply ||
            requested.ValueKind != JsonValueKind.Array || requested.GetArrayLength() is < 1 or > 4) throw Invalid();
        string changeId = Text(apply["changeId"]), previewHash = Text(apply["previewHash"]);
        CredentialRouteSlot[] slots;
        try { slots = requested.EnumerateArray().Select(row => row.Deserialize<CredentialRouteSlot>(CredentialVault.Json) ?? throw Invalid()).ToArray(); }
        catch (JsonException) { throw Invalid(); }
        if (slots.Select(s => s.Slot).Distinct(StringComparer.Ordinal).Count() != slots.Length || slots.Any(s => s.Slot is null || s.Slot.Length is < 1 or > 32 || !s.Slot.All(char.IsAsciiLetter)) ||
            slots.Select(s => s.VaultName).Distinct(StringComparer.Ordinal).Count() != 1 || slots.Select(s => s.Reference?.VaultId).Distinct(StringComparer.Ordinal).Count() != 1)
            throw Invalid();
        foreach (var slot in slots)
        {
            ProtectedCredentialGuard.ValidateReference(slot.Reference); ProtectedCredentialGuard.ValidateBinding(slot.Binding);
            if (!Purposes.Contains(slot.Binding.Purpose, StringComparer.Ordinal)) throw Invalid();
            string expected = ProtectedCredentialGuard.BindingHash(slot.Binding);
            // The public binding the worker derived from the exact target goes back to the caller, so a matching credential can be created.
            if (slot.Reference.BindingSha256 != expected)
                throw new BridgeOperationException("CredentialContextMismatch", "The credential reference is not bound to this exact target and purpose. Create a credential with the expected binding; no secret was read.",
                    JsonSerializer.SerializeToElement(new { slot = slot.Slot, expectedBinding = slot.Binding, expectedBindingSha256 = expected }, BridgeProtocol.Json));
        }
        var sealedSlots = new List<(string, byte[])>();
        try
        {
            foreach (var slot in slots)
            {
                byte[]? envelope = null; bool password = true;
                try
                {
                    open(slot.VaultName).Use(slot.Reference, slot.Binding, secret =>
                    {
                        if (password = CredentialRouteEnvelope.IsPassword(secret))
                            envelope = CredentialRouteEnvelope.Seal(secret, CredentialRouteEnvelope.Entropy(token, changeId, previewHash, slot.Slot, slot.Reference.CredentialId, slot.Reference.BindingSha256));
                    });
                }
                // A missing or unreadable record surfaces as a file-system error before any decryption.
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw ProtectedCredentialGuard.Error("CredentialUnavailable"); }
                if (!password) throw ProtectedCredentialGuard.Error("CredentialFormatInvalid");
                sealedSlots.Add((slot.Slot, envelope ?? throw ProtectedCredentialGuard.Error("CredentialUnavailable")));
            }
            return sealedSlots;
        }
        catch { foreach (var (_, bytes) in sealedSlots) CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    private void Clear()
    {
        foreach (var (_, bytes) in envelopes ?? []) CryptographicOperations.ZeroMemory(bytes);
        envelopes = null; lease = null;
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length is > 0 and <= 128 ? text : throw Invalid();
    private static BridgeException Invalid() => new("CredentialRouteInvalid", "The worker's credential request does not name exact public slots for one admitted change apply; no secret was read.");
}
