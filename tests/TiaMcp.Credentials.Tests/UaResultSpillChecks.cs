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
        // Retention: day-old spills go, a protected (live-session) or fresh one stays, and the cap removes the oldest first.
        string directory = Path.Combine(root, "runtime-ua-results");
        string Make(string name, DateTime written, int length = 10) { string p = Path.Combine(directory, name); File.WriteAllBytes(p, new byte[length]); File.SetLastWriteTimeUtc(p, written); return p; }
        var now = DateTime.UtcNow;
        string old = Make("ua-read-old.json", now.AddDays(-2)), protectedOld = Make("ua-read-protected.json", now.AddDays(-3)), fresh = Make("ua-read-fresh.json", now.AddHours(-1));
        UaResultSpill.Prune(directory, Path.Combine(directory, "ua-read-none.json"), now.AddDays(-4), now);
        check(File.Exists(old) && File.Exists(protectedOld) && File.Exists(fresh), "Spills written since the oldest live session opened are never pruned");
        UaResultSpill.Prune(directory, Path.Combine(directory, "ua-read-none.json"), now.AddDays(-2.5), now);
        check(File.Exists(old) && !File.Exists(protectedOld) && File.Exists(fresh), "A spill older than a day is pruned only outside the protected window; a fresh one stays");
        string kept = Make("ua-read-newest.json", now.AddMinutes(-1), (int)UaResultSpill.MaximumBytes / 2 + 1), big1 = Make("ua-read-big1.json", now.AddHours(-3), (int)UaResultSpill.MaximumBytes / 2 + 1);
        UaResultSpill.Prune(directory, kept, null, now);
        check(File.Exists(kept) && !File.Exists(big1) && !File.Exists(protectedOld) && File.Exists(fresh), "The size cap removes the oldest spills first and never the one just written");
        check(again.Spill == spilled.Spill && File.Exists(Path.Combine(root, again.Spill!.Artifact)), "The same result spills to the same content-addressed artifact");
    }
}
