using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.AddIns;

namespace TiaMcp.Tests;

internal static class AddInEnrollmentTests
{
    private sealed class Owner(JsonObject context) : IBridgeClient
    {
        internal JsonObject Context = context;
        internal int Calls;
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            Calls++; Check.Equal("addin_owner_context", operation);
            Check.Equal(false, cancellationToken.CanBeCanceled); // A cancelled admitted call would fault the shared engineering session.
            return Task.FromResult(JsonSerializer.SerializeToElement(Context));
        }
    }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static readonly AddInHandoffPeer Portal = new(4000, "2026-10-27T08:00:00.0000000Z", @"C:\TIA\Bin\Siemens.Automation.Portal.exe", 1);
    private static readonly AddInHandoffPeer Loader = new(4200, "2026-10-27T08:05:00.0000000Z", @"C:\TIA\Bin\PublicAPI\AddIn\Loader\Siemens.Engineering.AddIn.Loader.x64.exe", 1);
    private static JsonObject Context(string generation) => new() {
        ["schemaVersion"] = "addin-owner-context.v1", ["workerInstanceId"] = new string('9', 32), ["workerProcessId"] = 4100, ["sessionGeneration"] = generation,
        ["ownsPortalProcess"] = false, ["state"] = "available", ["reason"] = null, ["process"] = JsonSerializer.SerializeToNode(Portal, BridgeProtocol.Json),
        ["osProcessIdentityVerified"] = true, ["nativeProcessIdMatched"] = true, ["producerAuthenticated"] = false, ["liveSelectionVerified"] = false, ["nativeQualified"] = false };

    internal static async Task FileIsExact()
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-enrollment-" + Id()); Directory.CreateDirectory(root);
        try
        {
            AddInEnrollment Read(string text) { string path = Path.Combine(root, Id() + ".json"); File.WriteAllText(path, text); return AddInEnrollment.Read(path); }
            string a = new('a', 64), c = new('c', 64);
            Check.Equal(new AddInEnrollment(a, c), Read($"{{\"schemaVersion\":\"addin-enrollment.v1\",\"assemblySha256\":\"{a}\",\"configurationSha256\":\"{c}\"}}\n"));
            foreach (string bad in new[] {
                $"{{\"schemaVersion\":\"addin-enrollment.v1\",\"assemblySha256\":\"{a}\"}}",
                $"{{\"schemaVersion\":\"addin-enrollment.v1\",\"assemblySha256\":\"{a}\",\"configurationSha256\":\"{c}\",\"trusted\":true}}",
                $"{{\"schemaVersion\":\"addin-enrollment.v1\",\"assemblySha256\":\"{a.ToUpperInvariant()}\",\"configurationSha256\":\"{c}\"}}",
                $"{{\"schemaVersion\":\"addin-enrollment.v2\",\"assemblySha256\":\"{a}\",\"configurationSha256\":\"{c}\"}}",
                $"{{\"schemaVersion\":\"addin-enrollment.v1\",\"assemblySha256\":\"{a}\",\"assemblySha256\":\"{a}\",\"configurationSha256\":\"{c}\"}}",
                "[]", "", new string(' ', 4097) })
                await Check.BridgeError("InvalidAddInEnrollment", () => Task.FromResult(Read(bad)));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task BindsOwnerLoader()
    {
        await using var f = new AddInHandoffTests.Fixture(Environment.MachineName);
        await f.Store.DisposeAsync(); // Release the fixture's synthetic owner lock; this store uses the production authority only.
        string generation = (string)f.Request["references"]![0]!["sessionGeneration"]!;
        var owner = new Owner(Context(generation)); int parent = Portal.ProcessId;
        var enrollment = new AddInEnrollment(new string('b', 64), new string('c', 64));
        await using var store = new AddInHandoff(f.Root, enrollment.Authority(owner, f.Root, _ => parent), f.Clock);

        string id = Id(); var challenge = await store.BeginAsync(id, f.ReviewId, f.Bytes, Loader);
        Check.Equal(false, (bool)challenge["syntheticAuthority"]!); Check.Equal(false, (bool)challenge["producerAuthenticated"]!);
        var status = await store.CompleteAsync(id, (string)challenge["challenge"]!, Loader);
        Check.Equal("completed", (string)status["state"]!); Check.Equal(3, owner.Calls);
        Check.Equal(generation, (string)status["binding"]!["sessionGeneration"]!); Check.Equal(false, (bool)status["binding"]!["syntheticAuthority"]!);
        Check.Equal(Loader.ExecutablePath, (string)status["binding"]!["tiaProcess"]!["executablePath"]!);

        async Task Refused(string code, AddInHandoffPeer? peer = null) =>
            await Check.BridgeError(code, () => store.BeginAsync(Id(), f.ReviewId, f.Bytes, peer ?? Loader));
        await Refused("AddInProducerNotEnrolled", Loader with { ExecutablePath = @"C:\TIA\Bin\Siemens.Automation.Portal.exe" });
        await Refused("AddInProducerNotEnrolled", Loader with { ExecutablePath = @"C:\Other\Bin\PublicAPI\AddIn\Loader\Siemens.Engineering.AddIn.Loader.x64.exe" });
        await Refused("AddInProducerNotEnrolled", Loader with { WindowsSessionId = 2 });
        await Refused("AddInProducerNotEnrolled", Loader with { StartedAtUtc = "2026-10-27T07:59:59.0000000Z" });
        parent = 1; await Refused("AddInProducerNotEnrolled"); parent = Portal.ProcessId;
        owner.Context = Context(generation); owner.Context["state"] = "unavailable"; owner.Context["process"] = null; await Refused("AddInOwnerUnavailable");
        owner.Context = Context(Id()); await Refused("HandoffOwnerMismatch"); // Review captured in an earlier worker session.

        owner.Context = Context(generation); id = Id(); challenge = await store.BeginAsync(id, f.ReviewId, f.Bytes, Loader);
        owner.Context["workerInstanceId"] = new string('8', 32); // Worker re-attached between challenge and completion.
        await Check.BridgeError("HandoffOwnerChanged", () => store.CompleteAsync(id, (string)challenge["challenge"]!, Loader));
        owner.Context = Context(generation);
        await Check.BridgeError("HandoffChallengeUnavailable", () => store.CompleteAsync(id, (string)challenge["challenge"]!, Loader));
        Check.Equal("consumedNoReceipt", (string)(await store.ReadAsync(id))["state"]!);
    }

    internal static async Task ActualParentLookup()
    {
        var start = new System.Diagnostics.ProcessStartInfo(BridgeFixture.DotnetPath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--identity-child");
        using var child = System.Diagnostics.Process.Start(start)!;
        try
        {
            Check.Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Check.Equal(Environment.ProcessId, AddInEnrollment.ParentProcessId(child.Id));
            Check.Equal(true, AddInEnrollment.ParentProcessId(Environment.ProcessId) != Environment.ProcessId);
        }
        finally { child.StandardInput.Close(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    internal static async Task PinnedHashes()
    {
        string b = new('b', 64), c = new('c', 64), d = new('d', 64);
        // Each row differs from the admitted case in one binding only: assembly, configuration, or a review from another host.
        foreach (var (host, enrollment) in new[] { (Environment.MachineName, new AddInEnrollment(d, c)), (Environment.MachineName, new AddInEnrollment(b, d)), ((string?)null, new AddInEnrollment(b, c)) })
        {
            await using var f = new AddInHandoffTests.Fixture(host);
            await f.Store.DisposeAsync();
            var owner = new Owner(Context((string)f.Request["references"]![0]!["sessionGeneration"]!));
            await using var store = new AddInHandoff(f.Root, enrollment.Authority(owner, f.Root, _ => Portal.ProcessId), f.Clock);
            await Check.BridgeError("HandoffOwnerMismatch", () => store.BeginAsync(Id(), f.ReviewId, f.Bytes, Loader));
            Check.Equal(0, Directory.EnumerateDirectories(Path.Combine(f.Root, "addin-handoffs")).Count());
        }
    }
}
