using System.Text.Json.Nodes;
using TiaMcp.Core;

/// <summary>
/// Host loss at every durable publication boundary of an offline HMI text-list tool. The tools publish content-addressed
/// immutable artifacts and write the final record last, so abandoning the Host before publication k leaves exactly the first
/// k-1 artifacts (orphans), never a record. Restarting and submitting the same request must publish the rest once and return
/// the uninterrupted result: no replay of finished work, no partial candidate set, no stale record.
/// </summary>
internal static class PublishInterruptionSweep
{
    private sealed class HostLoss : Exception { }

    internal static int Run(string label, string root, string recordExtension, int expectedPublications, Func<HmiComponentWorkflowStore, JsonObject> call, Action<bool, string> check)
    {
        var baseline = Names(Path.Combine(root, "hmi-component-reviews"));
        string Copy(string name)
        {
            string target = root + "-" + label + "-" + name; CopyDirectory(root, target); return target;
        }
        // The uninterrupted run records the publication order and the expected result.
        // A publication of bytes that already exist (an unchanged candidate) adds no file, so the files expected after
        // k publications are read from the store at each boundary.
        var order = new List<(string Extension, string Hash)>(); var afterEach = new List<HashSet<string>>(); string whole = Copy("whole");
        string wholeStore = Path.Combine(whole, "hmi-component-reviews");
        var expected = call(new HmiComponentWorkflowStore(whole) { BeforePublish = (extension, hash) => { afterEach.Add(Names(wholeStore)); order.Add((extension, hash)); } });
        var expectedFiles = Hashes(wholeStore);
        var added = expectedFiles.Keys.Except(baseline).ToList();
        check(order.Count == expectedPublications && order[^1].Extension == recordExtension && added.Count(n => n.EndsWith(recordExtension, StringComparison.Ordinal)) == 1,
            label + ": the final record is the last publication");
        for (int boundary = 0; boundary < order.Count; boundary++)
            foreach (bool partialWrite in new[] { false, true })
            {
                string trial = Copy("b" + boundary + (partialWrite ? "t" : "n")), trialStore = Path.Combine(trial, "hmi-component-reviews"); int seen = 0;
                bool lost = false;
                try { call(new HmiComponentWorkflowStore(trial) { BeforePublish = (_, _) => { if (seen++ == boundary) throw new HostLoss(); } }); }
                catch (HostLoss) { lost = true; }
                check(lost, label + ": Host loss before publication " + (boundary + 1));
                var left = Names(trialStore).Except(baseline).ToList(); var leftHashes = Hashes(trialStore);
                check(left.ToHashSet().SetEquals(afterEach[boundary].Except(baseline)) && left.All(name => expectedFiles.TryGetValue(name, out string? h) && h == leftHashes[name]),
                    label + ": only the first " + boundary + " publications remain, byte-identical to the final ones");
                check(!left.Any(name => name.EndsWith(recordExtension, StringComparison.Ordinal)), label + ": no record before the last boundary (" + (boundary + 1) + ")");
                // Temporary files are never published artifacts and are ignored by the comparisons. A kill inside the write leaves one under the artifact's hash; it is never a published artifact.
                if (partialWrite) File.WriteAllBytes(Path.Combine(trialStore, order[boundary].Hash + ".00000000000000000000000000000000.tmp"), new byte[] { 1, 2, 3 });
                var restarted = call(new HmiComponentWorkflowStore(trial));
                check(JsonNode.DeepEquals(restarted, expected), label + ": restart returns the uninterrupted result after boundary " + (boundary + 1) + (partialWrite ? " with a stale temporary file" : ""));
                var after = Hashes(trialStore);
                check(after.Count == expectedFiles.Count && after.All(p => expectedFiles.TryGetValue(p.Key, out string? h) && h == p.Value),
                    label + ": reconciled store equals the uninterrupted store, nothing duplicated or replayed (boundary " + (boundary + 1) + ", extra: " +
                    string.Join(",", after.Keys.Except(expectedFiles.Keys)) + ", missing: " + string.Join(",", expectedFiles.Keys.Except(after.Keys)) + ")");
                Directory.Delete(trial, true);
            }
        // Reply lost after the last publication: submitting the same request again changes nothing.
        var again = call(new HmiComponentWorkflowStore(whole));
        check(JsonNode.DeepEquals(again, expected) && Hashes(Path.Combine(whole, "hmi-component-reviews")).OrderBy(p => p.Key).SequenceEqual(expectedFiles.OrderBy(p => p.Key)),
            label + ": repeating a completed request is idempotent");
        Directory.Delete(whole, true);
        return order.Count;
    }

    private static HashSet<string> Names(string directory) => Directory.GetFiles(directory).Select(p => Path.GetFileName(p)!).Where(n => n != ".write.lock" && !n.EndsWith(".tmp", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
    private static Dictionary<string, string> Hashes(string directory) => Directory.GetFiles(directory).Where(p => Path.GetFileName(p) != ".write.lock" && !p.EndsWith(".tmp", StringComparison.Ordinal))
        .ToDictionary(p => Path.GetFileName(p)!, p => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))), StringComparer.Ordinal);
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (string directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}
