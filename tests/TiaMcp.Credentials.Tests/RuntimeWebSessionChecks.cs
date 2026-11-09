using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TiaMcp.Core;
using TiaMcp.Core.Security;
using TiaMcp.Host;

// The production PhysicalRuntimeSessions against a tiny pinned-TLS loopback Web API: connect, browse, read, per-session locking.
internal static class RuntimeWebSessionChecks
{
    private static string Id() => Guid.NewGuid().ToString("N");

    internal static async Task Run(string output, Action<bool, string> check)
    {
        string vaultName = Id(), vaultRoot = CredentialVault.UserRoot(vaultName);
        // Opened once more after its folder exists, so the vault's path identity matches the Host's later Existing() open.
        _ = new CredentialVault(vaultRoot); var vault = new CredentialVault(vaultRoot);
        await using var first = new FakeWebApi();
        await using var second = new FakeWebApi();
        try
        {
            RuntimeLoginSelection Login(string endpoint, string pin)
            {
                var binding = PhysicalRuntimeSessions.Binding(endpoint, pin, "reader", "local");
                var created = vault.Create(binding, Encoding.UTF8.GetBytes("\"fixture-web-password\""));
                return new(endpoint, pin, "reader", "local", vaultName, created.Reference);
            }
            await using (var sessions = new PhysicalRuntimeSessions(new HostOptions("", output, null, 5, false, "", null)))
            {
                string records = Path.Combine(output, "runtime-web");
                File.WriteAllText(Path.Combine(records, "notes.json"), "{}");
                string a = Id(), b = Id();
                var connected = await sessions.Connect(a, Login(first.Endpoint, first.Pin), default);
                check(connected.State == "connected" && connected.PeerVerified, "A stray non-attempt JSON file does not block connect");
                check((await sessions.Connect(b, Login(second.Endpoint, second.Pin), default)).State == "connected", "Second pinned session connects");
                string Select(object browsed) => JsonSerializer.SerializeToElement(browsed).GetProperty("selectionId").GetString()!;
                string sa = Select(await sessions.Browse(a, "\"DB\".Counter", false, default));
                string sb = Select(await sessions.Browse(b, "\"DB\".Counter", false, default));

                // Connect -> browse -> read twice: equal status, and an unchanged session record is not rewritten per sample.
                int permissions = first.Calls.Count(c => c == "Api.GetPermissions");
                var one = await sessions.ReadValue(a, sa, "simple", default);
                string record = Path.Combine(records, a + ".json");
                byte[] bytes = File.ReadAllBytes(record); var written = File.GetLastWriteTimeUtc(record);
                string status = JsonSerializer.Serialize(await sessions.Status(a, default), BridgeProtocol.Json);
                var two = await sessions.ReadValue(a, sa, "simple", default);
                check(one.Status == "read" && two.Status == "read" && one.Value!.Value.GetInt32() == 7 && two.Value!.Value.GetInt32() == 7, "Two real reads return the value");
                check(JsonSerializer.Serialize(await sessions.Status(a, default), BridgeProtocol.Json) == status, "Status is equal after the second read");
                check(File.ReadAllBytes(record).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(record) == written, "An unchanged session record is not rewritten by a read");
                check(first.Calls.Count(c => c == "Api.GetPermissions") == permissions, "A read sends no Api.GetPermissions");

                // The PLC enforces read permission itself: its denial is a permissionDenied read result, not a stale value.
                first.DenyRead = true;
                var denied = await sessions.ReadValue(a, sa, "simple", default);
                first.DenyRead = false;
                check(denied.Status == "permissionDenied" && denied.Value is null && (await sessions.Status(a, default)).State == "connected",
                    "A PLC-side read denial is reported as permissionDenied and keeps the session");

                // One session's slow read holds only that session's lock.
                first.ReadDelayMs = 3000; first.ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending = sessions.ReadValue(a, sa, "simple", default);
                await first.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var clock = Stopwatch.StartNew();
                var quick = await sessions.ReadValue(b, sb, "simple", default);
                check(quick.Status == "read" && clock.ElapsedMilliseconds < 1500 && !pending.IsCompleted,
                    "A read on another session completes while one session's read is in flight (" + clock.ElapsedMilliseconds + " ms)");
                check((await pending).Status == "read", "The slow read still completes");
                first.ReadDelayMs = 0;
                check((await sessions.Disconnect(a, default)).State == "closed" && (await sessions.Disconnect(b, default)).State == "closed", "Both sessions log out");

                // A flipped certificate pin is a TLS refusal, not a transport failure.
                string flipped = (first.Pin[0] == '0' ? "1" : "0") + first.Pin[1..];
                var refused = await sessions.Connect(Id(), Login(first.Endpoint, flipped), default);
                check(refused.State == "notConnected" && refused.ErrorCode == "RuntimeTlsUnverified" && !refused.PeerVerified,
                    "A pin mismatch reports RuntimeTlsUnverified (" + refused.ErrorCode + ")");
            }
            string corrupt = Path.Combine(output, "corrupt");
            await using (var sessions = new PhysicalRuntimeSessions(new HostOptions("", corrupt, null, 5, false, "", null)))
            {
                File.WriteAllText(Path.Combine(corrupt, "runtime-web", Id() + ".json"), "{");
                bool thrown = false;
                try { await sessions.Connect(Id(), Login(second.Endpoint, second.Pin), default); } catch (Exception) { thrown = true; }
                check(thrown, "A corrupt attempt-named record still refuses a new connect");
            }
        }
        finally { Directory.Delete(vaultRoot, true); }
    }

    /// <summary>Minimal JSON-RPC Web API over pinned loopback TLS; one request per connection, as the client asks.</summary>
    private sealed class FakeWebApi : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 certificate;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        internal string Endpoint { get; }
        internal string Pin { get; }
        internal volatile int ReadDelayMs;
        internal volatile bool DenyRead;
        internal TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentQueue<string> Calls = new();

        internal FakeWebApi()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Runtime session fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null); // SChannel needs a persisted key form.
            Pin = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
            listener.Start(); Endpoint = "https://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Serve();
        }

        private async Task Serve()
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); } catch { return; }
                _ = Handle(client);
            }
        }

        private async Task Handle(TcpClient tcp)
        {
            using (tcp)
            try
            {
                using var ssl = new SslStream(tcp.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate });
                var head = new List<byte>(); var one = new byte[1];
                while (head.Count < 4 || !(head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
                {
                    if (await ssl.ReadAsync(one) == 0) return;
                    head.Add(one[0]);
                }
                int length = int.Parse(Regex.Match(Encoding.ASCII.GetString(head.ToArray()), @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase).Groups[1].Value);
                var body = new byte[length]; await ssl.ReadExactlyAsync(body);
                using var json = JsonDocument.Parse(body);
                string method = json.RootElement.GetProperty("method").GetString()!, id = json.RootElement.GetProperty("id").GetString()!;
                Calls.Enqueue(method);
                object? result = method switch
                {
                    "Api.Browse" => new[] { "Api.Login", "Api.Logout", "Api.GetPermissions", "PlcProgram.Browse", "PlcProgram.Read", "Api.GetSessionInfo" }.Select(n => new { name = n }),
                    "Api.Login" => new { token = "fixture-token-" + id },
                    "Api.GetSessionInfo" => new { username = "reader", authentication_mode = "local" },
                    "Api.GetPermissions" => new[] { new { name = "read_value" } },
                    "PlcProgram.Browse" => new[] { new { name = "Counter", datatype = "int" } },
                    "PlcProgram.Read" => 7,
                    "Api.Logout" => true,
                    _ => null
                };
                object? error = null;
                if (method == "PlcProgram.Read")
                {
                    ReadEntered.TrySetResult();
                    if (ReadDelayMs > 0) await Task.Delay(ReadDelayMs);
                    if (DenyRead) error = new { code = 2, message = "denied" };
                }
                byte[] reply = JsonSerializer.SerializeToUtf8Bytes(error is null ? new { jsonrpc = "2.0", id, result } : (object)new { jsonrpc = "2.0", id, error });
                await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + reply.Length + "\r\nConnection: close\r\n\r\n"));
                await ssl.WriteAsync(reply); await ssl.FlushAsync();
            }
            catch (Exception) { } // A refused handshake (wrong pin) ends the connection here.
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop(); await loop; certificate.Dispose(); stop.Dispose();
        }
    }
}
