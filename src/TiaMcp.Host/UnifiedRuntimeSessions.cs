using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Security;
using TiaMcp.Core.UnifiedRuntime;

namespace TiaMcp.Host;

public sealed record UnifiedLoginSelection(string Endpoint, string PeerCertificateSha256, string User, string VaultName, CredentialReference Credential);

/// <summary>
/// WinCC Unified PC Runtime GraphQL sessions owned by this Host, in memory only: a Host restart drops them, and the server ends
/// an idle session itself (the user's autoLoggOffSec). Reads work on any pinned endpoint; acknowledging alarms is a runtime
/// write and is admitted only for a runtime on this PC (every address of the endpoint's host name is local).
/// </summary>
public sealed class UnifiedRuntimeSessions : IDisposable
{
    private sealed record Live(UnifiedGraphQlClient Client, UnifiedLoginSelection Login, Uri Endpoint, DateTimeOffset ConnectedAt);
    private readonly Dictionary<string, Live> live = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1);

    public static CredentialBinding Binding(string endpoint, string certificate, string user)
    {
        var uri = UnifiedAlarmRules.Endpoint(endpoint);
        if (certificate is not { Length: 64 } || certificate.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new BridgeException("UnifiedCertificatePinInvalid", "peerCertificateSha256 is the lowercase SHA-256 of the Runtime web server's leaf certificate.");
        if (string.IsNullOrWhiteSpace(user) || user.Length > 256 || user.Any(char.IsControl)) throw new BridgeException("InvalidRequest", "user is the UMC user name configured for the runtime.");
        var binding = new CredentialBinding(new("siemensWinCCUnifiedGraphQl", uri.ToString(), certificate, "authentication", "umcLogin"), "runtimeLogin", user);
        ProtectedCredentialGuard.ValidateBinding(binding);
        return binding;
    }

    public async Task<JsonObject> Connect(string attemptId, UnifiedLoginSelection login, CancellationToken cancellationToken)
    {
        Id(attemptId);
        var binding = Binding(login.Endpoint, login.PeerCertificateSha256, login.User);
        ProtectedCredentialGuard.ValidateReference(login.Credential);
        if (login.Credential.BindingSha256 != ProtectedCredentialGuard.BindingHash(binding))
            throw new BridgeException("RuntimeCredentialContextMismatch", "The credential reference was created for another endpoint, certificate or user.");
        if (login.VaultName != "default") Id(login.VaultName);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (live.TryGetValue(attemptId, out var existing))
            {
                if (existing.Login != login) throw new BridgeException("RuntimeAttemptContextMismatch", "attemptId belongs to another login.");
                return Status(attemptId, existing);
            }
            if (live.Count >= 4) throw new BridgeException("RuntimeSessionCapacity", "This Host holds four Unified sessions; disconnect one first.");
            var endpoint = UnifiedAlarmRules.Endpoint(login.Endpoint);
            var client = new UnifiedGraphQlClient(endpoint, login.PeerCertificateSha256);
            try
            {
                string? failure = null;
                CredentialVault.Existing(CredentialVault.UserRoot(login.VaultName)).Use(login.Credential, binding, bytes =>
                {
                    try { client.LoginAsync(login.User, bytes, cancellationToken).GetAwaiter().GetResult(); }
                    catch (BridgeException error) { failure = error.Code + "\n" + error.Message; }
                    catch (Exception) { failure = "UnifiedLoginFailed\nThe login failed; no secret was returned."; }
                });
                if (failure is not null) { var parts = failure.Split('\n', 2); throw new BridgeException(parts[0], parts[1]); }
            }
            catch { client.Dispose(); throw; }
            var entry = new Live(client, login, endpoint, DateTimeOffset.UtcNow);
            live[attemptId] = entry;
            return Status(attemptId, entry);
        }
        finally { gate.Release(); }
    }

    public async Task<JsonObject> Alarms(string attemptId, string kind, string[]? languages, string? filterString, string? filterLanguage,
        DateTimeOffset? startTime, DateTimeOffset? endTime, int maxItems, CancellationToken cancellationToken)
    {
        if (maxItems is < 1 or > UnifiedAlarmRules.MaxAlarms) throw new BridgeException("InvalidRequest", "maxItems must be 1.." + UnifiedAlarmRules.MaxAlarms + ".");
        if (kind is not ("active" or "logged")) throw new BridgeException("InvalidRequest", "kind is active or logged.");
        if (kind == "active" && (startTime is not null || endTime is not null)) throw new BridgeException("InvalidRequest", "startTime and endTime apply to logged alarms only.");
        var entry = Get(attemptId);
        var chosen = UnifiedAlarmRules.Languages(languages, entry.Client.UserLanguage);
        var request = kind == "active"
            ? UnifiedAlarmRules.ActiveRequest(entry.Client.ActiveFields, chosen, filterString, filterLanguage)
            // One more than asked, so a full page says whether the server had more.
            : UnifiedAlarmRules.LoggedRequest(entry.Client.LoggedFields, chosen, filterString, filterLanguage, startTime, endTime, maxItems + 1);
        var data = await entry.Client.QueryAsync(request, cancellationToken);
        var rows = UnifiedAlarmRules.Rows(data.GetProperty(kind == "active" ? "activeAlarms" : "loggedAlarms"), chosen, kind == "active", maxItems, out int total);
        return new JsonObject
        {
            ["attemptId"] = attemptId, ["kind"] = kind, ["languages"] = new JsonArray(chosen.Select(l => (JsonNode)l).ToArray()),
            ["languageSource"] = languages is { Length: > 0 } ? "request" : "umcUserLanguage",
            ["alarms"] = rows, ["returned"] = rows.Count, ["totalReturnedByServer"] = total, ["truncated"] = total > rows.Count,
            ["note"] = kind == "active"
                ? "Acknowledge with runtime_unified_alarm_acknowledge using each row's acknowledge object: path is the fully qualified name (System::Tag:Alarm) the server needs; name alone does not resolve."
                : "Logged alarms have no path and cannot be acknowledged; acknowledge the matching active alarm.",
        };
    }

    public async Task<JsonObject> Acknowledge(string attemptId, IReadOnlyList<UnifiedAlarmIdentifier> alarms, bool acknowledgeWrite, CancellationToken cancellationToken)
    {
        if (!acknowledgeWrite) throw new BridgeException("WriteAcknowledgementRequired", "Acknowledging alarms changes the runtime's alarm state; pass acknowledgeWrite:true after the user asked for it.");
        var entry = Get(attemptId);
        var local = await LocalEndpoint(entry.Endpoint, cancellationToken);
        if (!local.IsLocal)
            throw new BridgeOperationException("UnifiedWriteRouteRefused", "Alarms are acknowledged only on a WinCC Unified PC Runtime running on this PC; " + entry.Endpoint.Host + " resolves to another machine.",
                System.Text.Json.JsonSerializer.SerializeToElement(new { host = entry.Endpoint.Host, addresses = local.Addresses }));
        var request = UnifiedAlarmRules.AcknowledgeRequest(entry.Client.ActiveFields, alarms);
        var data = await entry.Client.QueryAsync(request, cancellationToken);
        var rows = UnifiedAlarmRules.Rows(data.GetProperty("acknowledgeAlarms"), [], active: true, UnifiedAlarmRules.MaxAcknowledge * 64, out int total);
        var failed = rows.OfType<JsonObject>().Where(r => r["error"]?["code"] is JsonValue code && code.ToString() is not ("0" or "")).ToArray();
        return new JsonObject
        {
            ["attemptId"] = attemptId, ["requested"] = alarms.Count, ["results"] = rows, ["returned"] = total,
            ["itemErrors"] = failed.Length, ["acknowledged"] = failed.Length == 0 && total > 0,
            ["note"] = "Item codes from the server: 2 cannot resolve the name, 304 invalid object state, 305 not acknowledgeable in the current state. Read the active alarms again to see the new state.",
        };
    }

    public JsonObject Status(string attemptId) => Status(attemptId, Get(attemptId));

    public async Task<JsonObject> Disconnect(string attemptId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            bool removed = live.Remove(attemptId, out var entry);
            entry?.Client.Dispose();
            return new JsonObject { ["attemptId"] = attemptId, ["closed"] = removed,
                ["note"] = "The token is dropped here; the server ends the session after its idle time (autoLoggOffSec)." };
        }
        finally { gate.Release(); }
    }

    private Live Get(string attemptId)
    {
        Id(attemptId);
        return live.TryGetValue(attemptId, out var entry) ? entry
            : throw new BridgeException("RuntimeSessionNotFound", "No Unified session with this attemptId in this Host; sessions are not kept across a Host restart.");
    }

    private static JsonObject Status(string attemptId, Live entry) => new()
    {
        ["schemaVersion"] = "runtime-unified-session.v1", ["attemptId"] = attemptId, ["endpoint"] = entry.Endpoint.ToString(), ["user"] = entry.Login.User,
        ["state"] = "connected", ["connectedAtUtc"] = entry.ConnectedAt.UtcDateTime.ToString("O"), ["peerVerified"] = entry.Client.Verified,
        ["userLanguage"] = entry.Client.UserLanguage, ["autoLogoffSeconds"] = entry.Client.AutoLogoffSeconds,
        ["activeAlarmFields"] = entry.Client.ActiveFields.Length, ["loggedAlarmFields"] = entry.Client.LoggedFields.Length,
        ["trust"] = "explicitLeafPinAndHostnameAndValidity", ["secretReturned"] = false,
    };

    internal static async Task<(bool IsLocal, string[] Addresses)> LocalEndpoint(Uri endpoint, CancellationToken cancellationToken)
    {
        IPAddress[] resolved;
        try { resolved = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost, cancellationToken); }
        catch (System.Net.Sockets.SocketException) { return (false, []); }
        var local = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToHashSet();
        return (resolved.Length > 0 && resolved.All(a => IPAddress.IsLoopback(a) || local.Contains(a)), resolved.Select(a => a.ToString()).ToArray());
    }

    private static void Id(string value)
    {
        if (!ProtectedCredentialGuard.IsId(value)) throw new BridgeException("InvalidRequest", "attemptId (and a vault name other than default) is 32 lowercase hex characters.");
    }

    public void Dispose()
    {
        foreach (var entry in live.Values) entry.Client.Dispose();
        live.Clear();
    }
}
