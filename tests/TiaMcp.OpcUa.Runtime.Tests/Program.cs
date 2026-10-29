using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Opc.Ua;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Core.Security;
using TiaMcp.OpcUa.Runtime;
using TiaMcp.OpcUa.Runtime.Tests;

if (args.Length == 3 && args[0] == "--subscription-input-checks" && args[1] == "--output")
{
    string inputOutput = Path.GetFullPath(args[2]);
    if (Directory.Exists(inputOutput)) throw new IOException("Output must be fresh.");
    Directory.CreateDirectory(inputOutput);
    var inputChecks = new List<string>();
    SubscriptionInputChecks.Run((condition, name) => { if (!condition) throw new Exception(name); inputChecks.Add(name); });
    File.WriteAllText(Path.Combine(inputOutput, "result.json"), JsonSerializer.Serialize(new { status = "passed", checks = inputChecks }, BridgeProtocol.Json));
    return;
}

if (args.Length == 4 && args[0] == "--crash-client")
{
    var selection = JsonSerializer.Deserialize<OpcUaRuntimeSelection>(File.ReadAllText(args[2]), BridgeProtocol.Json)!;
    var store = new OpcUaRuntimeSessionStore(args[1]);
    var status = await store.Connect(args[3], selection);
    if (status.State != "connected") Environment.Exit(74);
    Environment.Exit(73); // Intentional process exit without session close; parent retains the original process handle.
}
bool serve = args.Length == 3 && args[0] == "--serve" && args[1] == "--output";
bool eventsOnly = args.Length==3 && args[0]=="--events-only" && args[1]=="--output";
if (!serve && !eventsOnly && (args.Length != 2 || args[0] != "--output")) throw new ArgumentException("Use --output fresh-directory or --serve --output fresh-directory");
string output = Path.GetFullPath(args[^1]);
if (Directory.Exists(output)) throw new IOException("Output must be fresh.");
Directory.CreateDirectory(output);
var checks = new List<string>(); var servers = new List<FixtureServer>(); var scenarios = new List<object>();
bool passed = false;
static string Id() => Guid.NewGuid().ToString("N");
void Check(bool condition, string name) { if (!condition) throw new Exception("Check failed: " + name); checks.Add(name); }
void Save(string name, object value) => File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(value, BridgeProtocol.Json));
async Task<FixtureServer> Start(string name, string mode = "normal")
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int number = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    var server = new FixtureServer(); await server.StartFixture(number, Path.Combine(output, name + "-pki"), mode);
    servers.Add(server); return server;
}
async Task Refused(Func<Task> action, string code, string name)
{
    string? actual = null;
    try { await action(); } catch (BridgeException e) { actual = e.Code; }
    Check(actual == code, name + " (" + actual + ")");
}
OpcUaRuntimeSessionStore Store(string name, string password = "fixture-only-secret") => new(Path.Combine(output, name),
    (_, consume) => { var bytes = JsonSerializer.SerializeToUtf8Bytes(password); try { consume(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); } });
OpcUaRuntimeSelection User(OpcUaRuntimeSelection selection)
{
    var user = selection with { Authentication = "username", User = "reader", VaultName = "default" };
    var binding = OpcUaRuntimeSessionStore.Binding(user);
    return user with { Credential = new("protected-credential-reference.v1", new('a', 32), new('b', 32),
        ProtectedCredentialGuard.BindingHash(binding), new('c', 64)) };
}
try
{
    var server = await Start("primary");
    Save("ready", new { selection = server.Selection, processId = Environment.ProcessId,
        processBirthUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(), namespaceUri = FixtureServer.Namespace,
        fixtureOnly = true, serverCertificateDer = Convert.ToBase64String(server.Certificate.RawData) });
    if (serve)
    {
        Console.WriteLine("{\"ready\":true}"); Console.Out.Flush();
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var command = JsonDocument.Parse(line);
            string op = command.RootElement.GetProperty("operation").GetString()!;
            if (op == "stop") break;
            if (op == "mode") server.Mode = command.RootElement.GetProperty("value").GetString()!;
            if (op == "readDelay") server.ReadDelayMs = command.RootElement.GetProperty("value").GetInt32();
            if (op == "trustClient") server.TrustedClientSha256 = command.RootElement.GetProperty("sha256").GetString();
            if (op == "failValueReads") server.FailValueReads = command.RootElement.GetProperty("value").GetInt32();
            if (op == "dropPublishResponses") server.DropPublishResponses = command.RootElement.GetProperty("value").GetInt32();
            if (op == "emitEvent") server.EmitEvent(command.RootElement.GetProperty("number").GetInt32(),command.RootElement.GetProperty("severity").GetUInt16());
            Save("server-live", new { sessions = server.SessionCount, activePublishes=server.ActivePublishes, activeReads=server.ActiveReads, audit = server.Audit.ToArray() });
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, sessions = server.SessionCount, activePublishes=server.ActivePublishes, activeReads=server.ActiveReads, audit = server.Audit.ToArray(), clientCertificates = server.ClientCertificateChecks.ToArray() }, BridgeProtocol.Json));
            Console.Out.Flush();
        }
        passed = true;
    }
    else if(eventsOnly) {await EventChecks.Run(server,output,Check);passed=true;}
    else
    {
        SubscriptionInputChecks.Run(Check);
        await EventChecks.Run(server,output,Check);
        Save("event-server-audit",server.Audit.ToArray());server.Audit.Clear();
        var reconnectServer=await Start("reconnect");
        await ReconnectChecks.Run(reconnectServer,output,Check);
        Save("reconnect-server-audit",reconnectServer.Audit.ToArray());
        await ArrayValueChecks.Run(server, output, Check);
        await SubscriptionChecks.Run(server, output, Check);
        Save("subscription-server-audit",server.Audit.ToArray());
        server.Audit.Clear();
        await CaptureChecks.Run(server, output, Check);
        Save("capture-server-audit",server.Audit.ToArray());
        server.Audit.Clear();
        await using (var store = Store("healthy"))
        {
            string id = Id();
            var status = await store.Connect(id, server.Selection);
            Check(status.State == "connected" && status.PeerVerified, "Pinned encrypted real session");
            int created = server.Audit.Count;
            Check((await store.Connect(id, server.Selection)) == status && server.Audit.Count == created, "Repeated attempt does not replay");
            Check((await store.Status(id)).State == "connected" && server.Audit.Count == created, "Status performs no network");
            await Refused(() => store.Connect(Id(), server.Selection), "UaEndpointSessionUnresolved", "Fresh ID cannot bypass owned endpoint");
            var values = await store.Read(id, FixtureServer.Values.Keys.Select(FixtureServer.Node).Append(FixtureServer.Node("absent")).ToArray());
            Save("values", values); var items = values.Items.ToDictionary(x => x.Node.Identifier);
            Check((string?)items["int64"].Value == "-9223372036854775808" && items["int64"].BuiltInType == "Int64", "Exact Int64 minimum");
            Check((string?)items["uint64"].Value == "18446744073709551615" && items["uint64"].BuiltInType == "UInt64", "Exact UInt64 maximum");
            Check(items["uncertain"].Quality == "Uncertain" && (string?)items["uncertain"].Value == "42", "Uncertain value remains explicitly uncertain");
            Check(items["bad"].Quality == "Bad" && items["bad"].Value is null && items["denied"].Outcome == "itemError", "Bad and denied item outcomes suppress value");
            Check(items["absent"].Outcome == "itemError" && items["bool"].Outcome == "value", "Partial missing-node error does not erase successful items");
            Check(items["missingTimes"].SourceTimestamp is null && items["missingTimes"].ServerTimestamp is null, "No fabricated missing timestamps");
            Check(items["int64"].SourceTimestamp == new DateTimeOffset(FixtureServer.SourceTime) && items["int64"].ServerTimestamp == new DateTimeOffset(FixtureServer.ServerTime) &&
                items["int64"].SourcePicoseconds == 12 && items["int64"].ServerPicoseconds == 34, "Exact original timestamps and picoseconds");
            Check(items["array"].Outcome == "value" && items["array"].ValueRank == 1 && items["array"].ArrayDimensions!.SequenceEqual([3u]) &&
                items["array"].DataType!.NamespaceUri == Opc.Ua.Namespaces.OpcUa, "Array preserves dimensions and type");
            Check(items["structure"].Outcome == "unsupported" && items["structure"].Value is null, "Unsupported structure cannot become default value");
            Check(items["byteArray"].Encoding == "array" && items["byteString"].Outcome == "value" && (string?)items["byteString"].Value == "AQID",
                "Byte array differs from scalar ByteString");
            Check(items["minimumDate"].Value is string, "Decoded minimum scalar DateTime is retained rather than null");
            var browse = await store.Browse(id, FixtureServer.Node("fixture"), 4); Save("browse", browse);
            Check(browse.References.Count == 4 && browse.Truncated && browse.ContinuationReleased, "Bounded browse releases remaining continuation");
            var full = await store.Browse(id, FixtureServer.Node("fixture")); Save("browse-full", full);
            Check(!full.Truncated && full.References.Count == FixtureServer.Values.Count, "Multiple continuation pages complete exact fixture");
            Check(full.References.All(x => x.ReferenceType?.Identifier == "35" && x.TypeDefinition?.Identifier == "63"), "Reference and type-definition identities retained");
            created = server.Audit.Count;
            await Refused(() => store.Read(id, Enumerable.Repeat(FixtureServer.Node("bool"), 65).ToArray()), "UaRequestLimit", "Read capacity rejected before network");
            await Refused(() => store.Read(id, [new(FixtureServer.Namespace, "numeric", "0001")]), "UaNodeInvalid", "Noncanonical NodeId rejected before network");
            Check(server.Audit.Count == created, "Input refusal sends no service");
            Check((await store.Disconnect(id)).State == "closed" && server.SessionCount == 0, "Owned CloseSession confirmed at server");
            Save("healthy-status", await store.Status(id));
        }
        await using (var store = Store("advertised-form"))
        {
            // Siemens servers advertise opc.tcp://host:port without the root slash; the store canonicalizes it once at entry.
            var bare = server.Selection with { Endpoint = server.Selection.Endpoint.TrimEnd('/') };
            string id = Id(); var status = await store.Connect(id, bare);
            Check(status.State == "connected" && status.Selection == server.Selection, "Endpoint without the root slash connects in canonical form");
            Check((await store.Connect(id, server.Selection)) == status, "Both endpoint forms name the same attempt");
            Check(OpcUaRuntimeSessionStore.Binding(User(bare)) == OpcUaRuntimeSessionStore.Binding(User(server.Selection)), "Credential binding uses the canonical endpoint");
            await Refused(() => store.Connect(Id(), bare with { Endpoint = bare.Endpoint + "?x=1" }), "UaEndpointSelectionInvalid", "Canonicalization keeps query refusal");
            Check((await store.Disconnect(id)).State == "closed", "Canonical session closes");
        }
        foreach (var (name, selection) in new[] {
            ("wrongPin", server.Selection with { CertificateSha256 = new('0',64) }),
            ("wrongUri", server.Selection with { ApplicationUri = "urn:wrong:server" }) })
        {
            await using var store = Store(name); int before = server.Audit.Count;
            var status = await store.Connect(Id(), selection);
            Check(status.State == "notConnected" && !status.PeerVerified, name + " refused before session");
            scenarios.Add(new { name, status, audit = server.Audit.Skip(before).ToArray() });
        }
        foreach (string mode in new[] { "wrongHost", "expired", "wrongApplication" })
        {
            var bad = await Start(mode, mode); await using var store = Store(mode);
            var status = await store.Connect(Id(), bad.Selection);
            Check(status.State == "notConnected" && !status.PeerVerified, mode + " refused");
            scenarios.Add(new { name = mode, status, audit = bad.Audit.ToArray() });
        }
        var rotated = await Start("rotation");
        rotated.AdvertisedCertificate = CertificateFactory.CreateCertificate(FixtureServer.Application, "Alternate", "CN=Alternate",
            ["localhost", "127.0.0.1", Dns.GetHostName()]).SetRSAKeySize(2048).CreateForRSA();
        await using (var store = Store("rotation"))
        {
            var selected = rotated.Selection with { CertificateSha256 = Convert.ToHexStringLower(SHA256.HashData(rotated.AdvertisedCertificate.RawData)) };
            var status = await store.Connect(Id(), selected);
            Check(status.State != "connected", "Rotation between discovery and secure channel refused");
            scenarios.Add(new { name = "rotation", status, audit = rotated.Audit.ToArray() });
        }
        foreach (string mode in new[] { "changedNamespace", "changedStart", "stale", "releaseDenied" })
        {
            await using var store = Store(mode); string id = Id();
            Check((await store.Connect(id, server.Selection)).State == "connected", mode + " initial session");
            int before = server.Audit.Count; server.Mode = mode;
            string? refusal = null;
            try { if (mode == "releaseDenied") await store.Browse(id, FixtureServer.Node("fixture"), 1); else await store.Read(id, [FixtureServer.Node("bool")]); }
            catch (BridgeException e) { refusal = e.Code; }
            Check(refusal is not null && (await store.Status(id)).State == "remoteSessionUnknown", mode + " invalidates owned session");
            int calls = server.Audit.Count;
            await Refused(() => store.Read(id, [FixtureServer.Node("bool")]), "UaSessionUnavailable", mode + " cannot reuse invalid session");
            Check(server.Audit.Count == calls, mode + " has no replay");
            scenarios.Add(new { name = mode, errorCode = refusal, status = await store.Status(id), audit = server.Audit.Skip(before).ToArray() });
            server.Mode = "normal"; Check((await store.Disconnect(id)).State == "closed", mode + " owned cleanup");
        }
        await using (var store = Store("cancel"))
        {
            string id = Id(); await store.Connect(id, server.Selection); server.ReadDelayMs = 1500;
            using var cancel = new CancellationTokenSource();
            var pending = store.Read(id, [FixtureServer.Node("bool")], cancel.Token);
            await server.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
            await Refused(async () => await pending, "UaCancelled", "In-flight read cancellation retained");
            Check((await store.Status(id)).State == "remoteSessionUnknown", "Cancelled request cannot revive session");
            server.ReadDelayMs = 0; Check((await store.Disconnect(id)).State == "closed", "Cancelled request owned cleanup");
        }
        foreach (string echoNode in new[] { "text", "stringArray" })
        await using (var store = Store("username-" + echoNode))
        {
            string id = Id(); var status = await store.Connect(id, User(server.Selection));
            Check(status.State == "connected", "Protected-consumer username fixture authenticates");
            server.Mode = "secretEcho";
            await Refused(() => store.Read(id, [FixtureServer.Node(echoNode)]), "UaSecretEchoRejected", "Server value cannot echo credential: " + echoNode);
            server.Mode = "normal"; await store.Disconnect(id);
        }
        {
            // A vault busy past its bounded wait is not a missing credential, and it leaves the session as it was.
            bool busy = false;
            await using var store = new OpcUaRuntimeSessionStore(Path.Combine(output, "vault-busy"), (_, consume) => {
                if (busy) throw new BridgeException("CredentialStoreBusy", "Fixture vault is busy.");
                var bytes = JsonSerializer.SerializeToUtf8Bytes("fixture-only-secret");
                try { consume(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); } });
            string id = Id();
            Check((await store.Connect(id, User(server.Selection))).State == "connected", "Busy-vault fixture connects");
            busy = true;
            await Refused(() => store.Read(id, [FixtureServer.Node("bool")]), "UaCredentialStoreBusy", "A busy vault is reported busy, never as a missing credential");
            Check((await store.Status(id)).State == "connected", "A busy vault leaves the session connected");
            busy = false;
            Check((await store.Read(id, [FixtureServer.Node("bool")])).Items[0].Outcome == "value", "The session reads again once the vault is free");
            Check((await store.Disconnect(id)).State == "closed", "Busy-vault fixture closes");
        }
        await using (var store = Store("browse-cancel"))
        {
            string id = Id(); await store.Connect(id, server.Selection); server.NextDelayMs = 1500;
            int beforeBrowseCancel = server.Audit.Count;
            using var cancel = new CancellationTokenSource();
            var pending = store.Browse(id, FixtureServer.Node("fixture"), 256, cancel.Token);
            await server.NextEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
            string? code = null;
            try { await pending; } catch (BridgeException e) { code = e.Code; }
            var status = await store.Status(id);
            Check(code is "UaCancelled" or "UaContinuationCleanupUnknown", "Browse cancellation retains explicit cancellation or cleanup unknown");
            Check(status.State == "remoteSessionUnknown" && server.Audit.Skip(beforeBrowseCancel).Any(x => {
                var row = JsonSerializer.SerializeToElement(x);
                return row.GetProperty("operation").GetString() == "Continuation" && row.TryGetProperty("release", out var release) && release.GetBoolean();
            }), "Browse cancellation attempts bounded owned continuation release");
            Save("browse-cancel-status", status);
            server.NextDelayMs = 0; await store.Disconnect(id);
        }
        await using (var store = Store("denied-login", "wrong-fixture-password"))
        {
            var status = await store.Connect(Id(), User(server.Selection));
            Check(status.State != "connected" && status.ErrorCode == "UaAuthenticationDenied", "Denied username cannot become connected");
            scenarios.Add(new { name = "deniedLogin", status });
        }
        string crashDir = Path.Combine(output, "restart"); string crashId = Id();
        Save("crash-selection", server.Selection);
        var processInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { typeof(FixtureServer).Assembly.Location, "--crash-client", crashDir, Path.Combine(output, "crash-selection.json"), crashId })
            processInfo.ArgumentList.Add(argument);
        using (var worker = Process.Start(processInfo)!)
        {
            _ = worker.SafeHandle; var birth = worker.StartTime.ToUniversalTime();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Check(worker.ExitCode == 73, "Actual owned crash worker exits at intentional boundary");
            Save("crash-process", new { processId = worker.Id, birthUtc = birth, exitCode = worker.ExitCode, originalHandleWaited = true, forcedTermination = false });
        }
        await using (var store = Store("restart"))
        {
            Check((await store.Status(crashId)).State == "hostEndedRemoteUnknown", "Actual process restart retains unknown remote state");
            await Refused(() => store.Connect(Id(), server.Selection), "UaEndpointSessionUnresolved", "Restart fresh ID cannot bypass unresolved endpoint");
            Check((await store.Disconnect(crashId, true)).State == "abandonedRemoteUnknown", "Explicit abandonment never claims remote closure");
            string id = Id(); Check((await store.Connect(id, server.Selection)).State == "connected", "Explicit fresh recovery succeeds");
            await store.Disconnect(id);
        }
        var second = await Start("second");
        foreach (string fault in new[] { "delete", "corrupt", "publish", "terminal" })
        {
            var store = Store("cleanup-" + fault); string id1 = Id(), id2 = Id();
            await store.Connect(id1, server.Selection); await store.Connect(id2, second.Selection);
            string path = Path.Combine(output, "cleanup-" + fault, id1 + ".json");
            if (fault == "delete") File.Delete(path);
            if (fault == "corrupt") File.WriteAllText(path, "{");
            if (fault == "publish") File.WriteAllText(Path.ChangeExtension(path, ".tmp"), "owned collision");
            if (fault == "terminal")
            {
                var record = JsonSerializer.Deserialize<OpcUaRuntimeStatus>(File.ReadAllText(path), BridgeProtocol.Json)!;
                File.WriteAllText(path, JsonSerializer.Serialize(record with { State = "closed" }, BridgeProtocol.Json));
            }
            int firstBefore = server.SessionCount, secondBefore = second.SessionCount;
            if (fault == "terminal") await store.DisposeAsync();
            else await Refused(async () => await store.DisposeAsync(), "UaCleanupPublicationFailed", fault + " cleanup publication failure remains explicit");
            Check(server.SessionCount == firstBefore - 1 && second.SessionCount == secondBefore - 1, fault + " closes both original live sessions despite corrupt record");
            using var owner = new FileStream(Path.Combine(output, "cleanup-" + fault, "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Check(owner.CanWrite, fault + " releases original owner lock");
        }
        var third = await Start("third"); var fourth = await Start("fourth");
        {
            var store = Store("idle-publication"); string id1 = Id(), id2 = Id();
            await store.Connect(id1, server.Selection); await store.Connect(id2, second.Selection);
            File.WriteAllText(Path.Combine(output, "idle-publication", id1 + ".tmp"), "owned collision");
            // Test-only inactivity trigger; the actual timer, server session and failing publication are real.
            var liveField = typeof(OpcUaRuntimeSessionStore).GetField("live", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var entries = (System.Collections.IDictionary)liveField.GetValue(store)!;
            var entry = entries[id1]!;
            var clientObject = entry.GetType().GetProperty("Client")!.GetValue(entry)!;
            clientObject.GetType().GetProperty("LastUsed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(clientObject, DateTimeOffset.UtcNow.AddMinutes(-3));
            int first = server.SessionCount, other = second.SessionCount;
            var wait = Stopwatch.StartNew();
            while (server.SessionCount == first && wait.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
            Check(server.SessionCount == first - 1, "Actual idle timer closes owned session before publication failure");
            await Refused(async () => await store.DisposeAsync(), "UaCleanupPublicationFailed", "Idle failure remains explicit during disposal");
            Check(second.SessionCount == other - 1, "Idle publication failure does not skip another live client's disposal");
            using var owner = new FileStream(Path.Combine(output, "idle-publication", "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Check(owner.CanWrite, "Idle failure releases owner lock");
        }
        await using (var store = Store("capacity"))
        {
            var ids = new[] { Id(), Id(), Id(), Id() }; var endpoints = new[] { server, second, third, fourth };
            for (int i = 0; i < 4; i++) Check((await store.Connect(ids[i], endpoints[i].Selection)).State == "connected", "Capacity slot " + i);
            int calls = server.Audit.Count;
            await Refused(() => store.Connect(Id(), server.Selection), "UaSessionCapacity", "Fifth owned session refused");
            Check(server.Audit.Count == calls, "Capacity refusal has no wire activity");
            for (int i = 0; i < 4; i++) Check((await store.Disconnect(ids[i])).State == "closed", "Capacity slot released " + i);
            string recovered = Id(); Check((await store.Connect(recovered, server.Selection)).State == "connected", "Capacity recovery opens healthy session");
            await store.Disconnect(recovered);
        }
        // Retention: the oldest resolved records make room for a new attempt; unresolved records are never removed and still fill capacity.
        string[] Seed(string name, Func<int, (OpcUaRuntimeSelection Selection, string State)> pick)
        {
            var ids = Enumerable.Range(0, 1024).Select(_ => Id()).ToArray(); var oldest = DateTimeOffset.UtcNow.AddHours(-1);
            for (int i = 0; i < ids.Length; i++)
            {
                var (selection, state) = pick(i);
                File.WriteAllText(Path.Combine(output, name, ids[i] + ".json"), JsonSerializer.Serialize(new OpcUaRuntimeStatus("opcua-runtime-session.v1",
                    ids[i], Id(), selection, state, null, false, oldest.AddSeconds(i)), BridgeProtocol.Json));
            }
            return ids;
        }
        await using (var store = Store("retention"))
        {
            var ids = Seed("retention", i => i == 0 ? (second.Selection, "remoteSessionUnknown") : (server.Selection, "closed"));
            string id = Id(); Check((await store.Connect(id, server.Selection)).State == "connected", "A full store of resolved records admits a new attempt");
            Check(File.Exists(Path.Combine(output, "retention", ids[0] + ".json")) && !File.Exists(Path.Combine(output, "retention", ids[1] + ".json")) &&
                Directory.EnumerateFiles(Path.Combine(output, "retention"), "*.json").Count() == 1024, "Only the oldest resolved record makes room; an older unresolved one stays");
            int calls = server.Audit.Count;
            Check((await store.Connect(ids[^1], server.Selection)).State == "closed" && server.Audit.Count == calls, "A retained attempt ID still observes its outcome without network");
            await store.Disconnect(id);
        }
        await using (var store = Store("retention-full"))
        {
            Seed("retention-full", _ => (server.Selection, "remoteSessionUnknown"));
            await Refused(() => store.Connect(Id(), server.Selection), "UaSessionCapacity", "1,024 unresolved records still refuse a new attempt");
            Check(Directory.EnumerateFiles(Path.Combine(output, "retention-full"), "*.json").Count() == 1024, "A capacity refusal removes no record");
        }
        var allowed = new HashSet<string> { "GetEndpoints", "CreateSession", "ActivateSession", "Read", "Browse", "BrowseNext", "Continuation", "CloseSession", "CreateSubscription", "CreateMonitoredItems", "Publish", "Republish", "DeleteSubscriptions", "SubscriptionCreated", "ItemsCreated", "PublishReturned", "RepublishRequested", "SubscriptionDeleteRequested", "FixtureDroppedPublish" };
        allowed.UnionWith(["PublishEntered","PublishCompleted","FixtureTransportReadFailure","FixtureLostPublishResponse"]);
        allowed.UnionWith(["FixtureEventEmitted","FixtureEventFilter","TransferSubscriptions","TransferRequested"]);
        foreach (var audit in servers.SelectMany(s => s.Audit))
            Check(allowed.Contains(JsonSerializer.SerializeToElement(audit).GetProperty("operation").GetString()!), "Wire operation allowed");
        passed = true;
    }
}
finally
{
    for (int i = 0; i < servers.Count; i++)
    {
        Save("server-audit-" + i, servers[i].Audit.ToArray());
        await servers[i].StopFixture();
    }
    Save("scenarios", scenarios);
    Save("result", new { schemaVersion = "opcua-runtime-qualification.v1", status = passed ? "passed" : "failed", checks,
        realLoopbackServer = true, physicalQualified = false, writesExposed = false, nativeStarted = false,
        processId = Environment.ProcessId, processBirthUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
        binaries = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) &&
            (a.GetName().Name!.StartsWith("TiaMcp") || a.GetName().Name!.StartsWith("Opc.Ua"))).Select(a => new {
                name = a.GetName().Name, path = a.Location, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(a.Location))) }) });
}
Console.WriteLine($"OPC UA qualification passed: {checks.Count} checks.");
