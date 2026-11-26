using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Security;
using TiaMcp.Security;

namespace TiaMcp.Host;

internal sealed record CredentialRouteSlot(string Slot, string VaultName, CredentialReference Reference, CredentialBinding Binding);

/// <summary>
/// Hands credential-store secrets to exactly one admitted worker change apply, or to one project_open with a UMAC login. It
/// sits between the admission dispatcher and the worker: for a change apply the worker names the public slots in its
/// mutation_begin reply, the Host validates and seals them before admission, and only mutation_dispatch for that lease
/// carries the sealed envelopes; for project_open the Host seals the login to a fresh nonce, the path and the user
/// (SealProjectLogin). No secret enters tool arguments.
/// </summary>
internal sealed class CredentialRoute(IBridgeClient inner, Func<string, CredentialVault>? vaults = null) : IBridgeClient
{
    // Purposes a worker may request: block know-how/write passwords and the CPU protection level's full-access password.
    private static readonly string[] Purposes = ["blockProtection", "cpuProtection"];
    private readonly Func<string, CredentialVault> open = vaults ?? (name => CredentialVault.Existing(CredentialVault.UserRoot(name)));
    private string? lease;
    private List<(string Slot, byte[] Envelope)>? envelopes;

    public async Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
    {
        if (operation == "project_open")
        {
            var request = SealProjectLogin(arguments, out byte[]? envelope);
            try { return await inner.CallAsync(operation, request, cancellationToken); }
            finally { if (envelope is not null) CryptographicOperations.ZeroMemory(envelope); }
        }
        if (operation == "mutation_begin")
        {
            Clear();
            var begin = await inner.CallAsync(operation, arguments, cancellationToken);
            if (!begin.TryGetProperty("credentials", out var requested) || requested.ValueKind == JsonValueKind.Null) return begin;
            // mutation_end needs the exact token; a reply without one names no lease the Host could end (tia_detach releases it).
            string token = begin.TryGetProperty("leaseToken", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid();
            try { envelopes = Seal(JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject(), requested, token); lease = token; return begin; }
            catch
            {
                // A refused credential never reaches admission; release the native lease the dispatcher has not seen yet.
                // The refusal is rethrown either way; stderr names a failed release (code only, never the token).
                try { await inner.CallAsync("mutation_end", new { leaseToken = token }, CancellationToken.None); }
                catch (Exception e) { Console.Error.WriteLine($"credential route: mutation_end after a refused credential failed: {(e is BridgeException b ? b.Code : e.GetType().FullName)}"); }
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
            // A routed binding always names a V21 resource of its own purpose, so a credential of one purpose never serves another.
            if (!Purposes.Contains(slot.Binding.Purpose, StringComparer.Ordinal) || slot.Binding.Resource.Provider != "siemensTiaPortalV21" ||
                slot.Binding.Resource.ResourceKind != slot.Binding.Purpose) throw Invalid();
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

    // project_open of a UMAC-protected project: the tool argument names a public reference, user and type. The Host derives the
    // binding from the exact full project path, checks the reference against it, and seals the password to a fresh nonce,
    // that path and user, so the worker receives only an envelope for this one open (docs/reference/protected-credentials.md).
    private object? SealProjectLogin(object? arguments, out byte[]? envelope)
    {
        envelope = null;
        if (JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json) is not JsonObject request || request["umac"] is not JsonObject umac) return arguments;
        string path;
        try { path = Path.GetFullPath(Text(request["path"], 32767)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { throw new BridgeException("InvalidRequest", "project_open needs an absolute .ap21 path for a UMAC login."); }
        string user = Text(umac["userName"], 256), type = Text(umac["userType"], 16), vaultName = umac["vaultName"] is null ? "default" : Text(umac["vaultName"], 32);
        if (user.Any(char.IsControl) || type is not ("project" or "global")) throw ProtectedCredentialGuard.Error("CredentialInputInvalid");
        CredentialReference reference;
        try { reference = umac["reference"].Deserialize<CredentialReference>(CredentialVault.Json) ?? throw ProtectedCredentialGuard.Error("CredentialInputInvalid"); }
        catch (JsonException) { throw ProtectedCredentialGuard.Error("CredentialInputInvalid"); }
        ProtectedCredentialGuard.ValidateReference(reference);
        var binding = ProjectLoginBinding(path, user, type);
        string expected = ProtectedCredentialGuard.BindingHash(binding);
        if (reference.BindingSha256 != expected)
            throw new BridgeOperationException("CredentialContextMismatch", "The credential reference is not bound to this exact project path, UMAC user and user type. Create a credential with the expected binding; no secret was read.",
                JsonSerializer.SerializeToElement(new { expectedBinding = binding, expectedBindingSha256 = expected }, BridgeProtocol.Json));
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        byte[]? sealedLogin = null; bool password = true;
        try
        {
            open(vaultName).Use(reference, binding, secret =>
            {
                if (password = CredentialRouteEnvelope.IsPassword(secret))
                    sealedLogin = CredentialRouteEnvelope.Seal(secret, CredentialRouteEnvelope.ProjectOpenEntropy(nonce, path, user, type, reference.CredentialId, reference.BindingSha256));
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw ProtectedCredentialGuard.Error("CredentialUnavailable"); }
        if (!password) throw ProtectedCredentialGuard.Error("CredentialFormatInvalid");
        envelope = sealedLogin ?? throw ProtectedCredentialGuard.Error("CredentialUnavailable");
        request["umac"] = new JsonObject
        {
            ["userName"] = user,
            ["userType"] = type,
            ["credentialId"] = reference.CredentialId,
            ["bindingSha256"] = reference.BindingSha256,
            ["nonce"] = nonce,
            ["envelope"] = Convert.ToBase64String(envelope)
        };
        return request;
    }

    /// <summary>The binding a UMAC project login credential is created with; the V21 worker derives the same (RoutedCredentials.UmacBinding).</summary>
    internal static CredentialBinding ProjectLoginBinding(string projectPath, string userName, string userType) =>
        new(new("siemensTiaPortalV21", projectPath, projectPath, "projectUmac", userName), "projectUmac", userType == "global" ? "globalUser" : "projectUser");

    private static string Text(JsonNode? node, int max) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 && text.Length <= max ? text
        : throw ProtectedCredentialGuard.Error("CredentialInputInvalid");

    private void Clear()
    {
        foreach (var (_, bytes) in envelopes ?? []) CryptographicOperations.ZeroMemory(bytes);
        envelopes = null; lease = null;
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length is > 0 and <= 128 ? text : throw Invalid();
    private static BridgeException Invalid() => new("CredentialRouteInvalid", "The worker's credential request does not name exact public slots for one admitted change apply; no secret was read.");
}
