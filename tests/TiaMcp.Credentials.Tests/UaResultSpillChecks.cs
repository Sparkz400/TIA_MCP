using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Host;

// runtime_ua_read/browse replies: small results stay inline, a result over 128 KiB comes back as a hashed artifact.
internal static class UaResultSpillChecks
{
    internal static void Run(string output, Action<bool, string> check)
    {
        string root = Path.Combine(output, "ua-spill"); Directory.CreateDirectory(root);
        var node = new OpcUaRuntimeNode("urn:fixture", "string", "n");
        OpcUaRuntimeValue Value(int i) => new(node with { Identifier = "n" + i }, "value", "String", null, -1, null, "0x00000000", "Good",
            null, null, 0, 0, "string", new string('x', 1024), null);
        var small = new OpcUaRuntimeReadResult(new('a', 32), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [Value(0)]);
        check(ReferenceEquals(UaResultSpill.Bounded(root, small, "read", 1, x => small with { Items = [], Spill = x }), small) && !Directory.Exists(Path.Combine(root, "runtime-ua-results")),
            "A small UA result stays inline and writes no artifact");
        var big = small with { Items = Enumerable.Range(0, 200).Select(Value).ToArray() };
        var spilled = (OpcUaRuntimeReadResult)UaResultSpill.Bounded(root, big, "read", 200, x => big with { Items = [], Spill = x });
        check(spilled.Items.Count == 0 && spilled.Spill is { Count: 200 } && spilled.Spill.Bytes > UaResultSpill.InlineLimitBytes &&
            JsonSerializer.SerializeToUtf8Bytes(spilled, BridgeProtocol.Json).Length < UaResultSpill.InlineLimitBytes, "A UA result over 128 KiB comes back with a spill and no inline items");
        // The artifact is the complete original result, readable through artifact_read's store, and its hash is the reply's.
        var store = new ArtifactStore(root); var content = new List<byte>(); long offset = 0;
        while (true)
        {
            var chunk = store.Read(spilled.Spill!.Artifact, offset, 65536);
            content.AddRange(Convert.FromBase64String(chunk.Data));
            if (chunk.Complete) break;
            offset = chunk.NextOffset;
        }
        var restored = JsonSerializer.Deserialize<OpcUaRuntimeReadResult>(content.ToArray(), BridgeProtocol.Json)!;
        check(Convert.ToHexStringLower(SHA256.HashData(content.ToArray())) == spilled.Spill.Sha256 && content.Count == spilled.Spill.Bytes &&
            restored.Items.Count == 200 && restored.Items[199].Node.Identifier == "n199" && restored.Spill is null, "The spilled artifact is the exact complete result under the hash the reply names");
        var again = (OpcUaRuntimeReadResult)UaResultSpill.Bounded(root, big, "read", 200, x => big with { Items = [], Spill = x });
        check(again.Spill == spilled.Spill && Directory.EnumerateFiles(Path.Combine(root, "runtime-ua-results")).Count() == 1, "The same result spills to the same content-addressed artifact");
    }
}
