using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

// Dedicated opt-in probe. The Python qualifier owns the exact loopback fixtures
// and expected acceptance/refusal cases; no production parsing is replaced.
internal static class RuntimeAdvertisedProfileChecks
{
    internal static async Task Run(string endpoint, string pin, string input, string output)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "127.0.0.1" || uri.Port <= 0 || File.Exists(output))
            throw new ArgumentException("A disposable loopback TLS endpoint and fresh result path are required.");
        using var request = JsonDocument.Parse(File.ReadAllText(input));
        string action = request.RootElement.GetProperty("action").GetString()!;
        string user = request.RootElement.GetProperty("user").GetString()!;
        string mode = request.RootElement.GetProperty("mode").GetString()!;
        using var client = new WebRuntimeClient(WebRuntimeProtocol.Context(endpoint, Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"), pin));
        string? error = null, cleanupError = null;
        try
        {
            await client.ProbeAsync(CancellationToken.None);
            if (action != "probe")
            {
                client.ValidateLoginMode(user, mode);
                if (action != "validate")
                {
                    await client.LoginAsync(Guid.NewGuid().ToString("N"), user, mode,
                        Encoding.UTF8.GetBytes("\"synthetic-profile-password\""), CancellationToken.None);
                    if (action is "verify" or "verifyAgain") await client.VerifySessionAsync(user, mode, CancellationToken.None);
                    if (action == "verifyAgain") await client.VerifySessionAsync(user, mode, CancellationToken.None);
                }
            }
        }
        catch (BridgeException failure) { error = failure.Code; }
        var observed = new
        {
            error, client.Profile, client.SessionUnusable, client.LoginDispatched, client.LoginRejected,
            client.PeerVerified, idleLimitSeconds = client.IdleLimit.TotalSeconds, client.Methods
        };
        if (client.LoginDispatched && !client.LoginRejected)
            try { await client.LogoutAsync(CancellationToken.None); }
            catch (BridgeException failure) { cleanupError = failure.Code; }
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        object Loaded(Type type)
        {
            string path = type.Assembly.Location;
            return new { path, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) };
        }
        JsonSerializer.Serialize(stream, new
        {
            schemaVersion = "runtime-advertised-profile-probe.v1", processId = Environment.ProcessId,
            loadedCore = Loaded(typeof(WebRuntimeClient)), loadedGraph = Loaded(typeof(TiaMcp.Graph.GraphDocument)),
            observed, cleanupError, actualPinnedLoopbackTls = true, nativeExecuted = false,
            deviceIdentityVerified = false, programMatchVerified = false
        }, BridgeProtocol.Json);
        stream.Flush(true);
    }
}
