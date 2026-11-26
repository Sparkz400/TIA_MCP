using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

// The production WebRuntimeClient against a loopback TLS peer that never closes a connection first, so a reused connection is
// observed on every run instead of racing the peer's close. Loopback only; no Siemens peer, no real credential.
internal static class RuntimeConnectionReuseChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Runtime connection reuse fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // The Windows TLS server needs a stored key, not the ephemeral one of CreateSelfSigned.
        using var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
        string pin = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));

        foreach (var (version, keepAliveHeader) in new[] { ("1.0", false), ("1.0", true), ("1.1", false) })
        {
            string label = $"HTTP/{version}{(keepAliveHeader ? " keep-alive" : "")}";
            await using var peer = new Peer(certificate, version, keepAliveHeader);
            using var client = new WebRuntimeClient(WebRuntimeProtocol.Context($"https://127.0.0.1:{peer.Port}",
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), pin));
            await client.ProbeAsync(CancellationToken.None);
            await client.LoginAsync(Guid.NewGuid().ToString("N"), "reader", "local", Encoding.UTF8.GetBytes("\"synthetic-reuse-password\""), CancellationToken.None);
            await client.PermissionsAsync(CancellationToken.None);
            await client.PermissionsAsync(CancellationToken.None);
            peer.OversizeNext = true;
            try { await client.PermissionsAsync(CancellationToken.None); throw new Exception("Oversized reply accepted: " + label); }
            catch (BridgeException refused) when (refused.Code == "RuntimeResponseLimit") { }
            await client.PermissionsAsync(CancellationToken.None);
            var reads = peer.Requests.Where(r => r.Method == "Api.GetPermissions").Select(r => r.Connection).ToArray();
            Check(reads.Length == 4, "Every read reaches the peer once, none is replayed: " + label);
            if (version == "1.0" && !keepAliveHeader)
                Check(peer.Requests.GroupBy(r => r.Connection).All(g => g.Count() == 1),
                    "A connection the peer did not keep open carries no second request, also after a refused oversized reply: " + label);
            else
                Check(reads[0] == reads[1], "A connection the peer keeps open is reused by consecutive reads: " + label);
        }
        return checks;
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 certificate;
        private readonly string version;
        private readonly bool keepAliveHeader;
        private readonly CancellationTokenSource stop = new();
        private readonly List<Task> connections = [];
        private readonly Task accepting;
        private int connectionCount;
        public readonly List<(int Connection, string Method)> Requests = [];
        public volatile bool OversizeNext;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public Peer(X509Certificate2 certificate, string version, bool keepAliveHeader)
        {
            this.certificate = certificate; this.version = version; this.keepAliveHeader = keepAliveHeader;
            listener.Start(); accepting = Accept();
        }

        private async Task Accept()
        {
            try
            {
                while (true)
                {
                    var socket = await listener.AcceptTcpClientAsync(stop.Token);
                    lock (connections) connections.Add(Serve(socket, Interlocked.Increment(ref connectionCount)));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        private async Task Serve(TcpClient socket, int connection)
        {
            using var _ = socket;
            try
            {
                await using var tls = new SslStream(socket.GetStream());
                await tls.AuthenticateAsServerAsync(certificate);
                // Answers every request on this connection and never closes it first: only the client ends it.
                while (await ReadRequest(tls) is { } body)
                {
                    using var json = JsonDocument.Parse(body);
                    string method = json.RootElement.GetProperty("method").GetString()!, id = json.RootElement.GetProperty("id").GetString()!;
                    lock (Requests) Requests.Add((connection, method));
                    object result = method switch
                    {
                        "Api.Browse" => new[] { "Api.Login", "Api.Logout", "Api.GetPermissions", "PlcProgram.Browse", "PlcProgram.Read" }.Select(n => new { name = n }),
                        "Api.Login" => new { token = "synthetic-reuse-token" },
                        "Api.GetPermissions" => new[] { new { name = "read_value" } },
                        _ => true
                    };
                    byte[] reply = method == "Api.GetPermissions" && OversizeNext
                        ? Encoding.ASCII.GetBytes(new string(' ', 300_000))
                        : JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, result });
                    if (method == "Api.GetPermissions") OversizeNext = false;
                    string head = $"HTTP/{version} 200 OK\r\nContent-Type: application/json\r\nContent-Length: {reply.Length}\r\n" +
                        (keepAliveHeader ? "Connection: keep-alive\r\n" : "") + "\r\n";
                    await tls.WriteAsync(Encoding.ASCII.GetBytes(head), stop.Token);
                    await tls.WriteAsync(reply, stop.Token);
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException) { }
        }

        private async Task<byte[]?> ReadRequest(SslStream tls)
        {
            var head = new List<byte>(); var one = new byte[1];
            while (head.Count < 4 || head[^4] != '\r' || head[^3] != '\n' || head[^2] != '\r' || head[^1] != '\n')
            {
                if (await tls.ReadAsync(one, stop.Token) == 0) return null;
                head.Add(one[0]);
            }
            int length = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n")
                .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                .Select(line => int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture)).Single();
            var body = new byte[length];
            await tls.ReadExactlyAsync(body, stop.Token);
            return body;
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            await accepting;
            Task[] open; lock (connections) open = connections.ToArray();
            await Task.WhenAll(open);
            stop.Dispose();
        }
    }
}
