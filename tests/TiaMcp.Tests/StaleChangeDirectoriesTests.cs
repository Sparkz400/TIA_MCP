using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class StaleChangeDirectoriesTests
{
    // Only old, unheld preparations go; every state change_status, change_restore or reconciliation reads stays.
    internal static Task OnlyOldUnheldPreparationsArePruned()
    {
        var root = Path.Combine(Path.GetTempPath(), "tia-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTime.UtcNow;
            string Make(string state, TimeSpan age, Action<JObject>? edit = null)
            {
                var id = Guid.NewGuid().ToString("N");
                var directory = Directory.CreateDirectory(Path.Combine(root, "change-" + id)).FullName;
                var journal = new JObject { ["changeId"] = id, ["kind"] = "tag_set", ["state"] = state };
                edit?.Invoke(journal);
                var path = Path.Combine(directory, "journal.json");
                File.WriteAllText(path, journal.ToString());
                File.WriteAllText(Path.Combine(directory, "requested.xml"), "<x/>");
                File.SetLastWriteTimeUtc(path, now - age);
                return id;
            }
            var old = TimeSpan.FromDays(3);
            var prepared = Make("prepared", old);
            var failed = Make("prepare_failed", old);
            var recent = Make("prepared", TimeSpan.FromHours(2));
            var held = Make("prepared", old);
            var keyed = Make("prepared", old, j => j["idempotencyKey"] = "k");
            var kept = new[] { "applied", "applying", "checkpointing", "recovery_required", "restored", "restoring", "rolled_back", "not_applied", "preparing" }
                .Select(state => Make(state, old)).ToArray();
            Directory.CreateDirectory(Path.Combine(root, "change-not-a-guid"));
            int removed = StaleChangeDirectories.Sweep(root, new HashSet<string>([held], StringComparer.Ordinal), now);
            bool Exists(string id) => Directory.Exists(Path.Combine(root, "change-" + id));
            Check.Equal(1, removed);
            Check.Equal(false, Exists(prepared));
            // prepare_failed keeps its journal as the failure record.
            Check.Equal(true, Exists(failed));
            Check.Equal(true, Exists(recent) && Exists(held) && Exists(keyed) && kept.All(Exists));
            Check.Equal(true, Directory.Exists(Path.Combine(root, "change-not-a-guid")));
            var log = File.ReadAllLines(Path.Combine(root, "pruned-changes.jsonl")).Select(JObject.Parse).ToArray();
            Check.Equal(1, log.Length);
            Check.Equal(prepared, log[0].Value<string>("changeId"));
            // A missing directory is not an error.
            Check.Equal(0, StaleChangeDirectories.Sweep(Path.Combine(root, "missing"), new HashSet<string>(), now));
            // A journal whose changeId does not match its directory is left alone.
            var mismatch = Make("prepared", old);
            var journalPath = Path.Combine(root, "change-" + mismatch, "journal.json");
            File.WriteAllText(journalPath, new JObject { ["changeId"] = Guid.NewGuid().ToString("N"), ["state"] = "prepared" }.ToString());
            File.SetLastWriteTimeUtc(journalPath, now - old);
            Check.Equal(0, StaleChangeDirectories.Sweep(root, new HashSet<string>([held], StringComparer.Ordinal), now));
            Check.Equal(true, Directory.Exists(Path.Combine(root, "change-" + mismatch)));
            // Once no worker holds it, the old preparation goes too.
            Check.Equal(1, StaleChangeDirectories.Sweep(root, new HashSet<string>(), now));
            Check.Equal(false, Exists(held));
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
