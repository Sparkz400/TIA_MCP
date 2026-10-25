using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class LibraryJournalStoreTests
{
    internal static async Task AtomicRecordsAndTamper()
    {
        using var files = new Fixture();
        var record = Record();
        LibraryJournalStore.Write(files.Journal, record);
        record["state"] = "applying";
        LibraryJournalStore.Write(files.Journal, record);
        Check.Equal("outcome_unknown", LibraryJournalStore.ReconciledState(LibraryJournalStore.Read(files.Journal, "lib-fixture")));
        // A preview modified independently of its approval token must never be accepted after restart.
        record["preview"]!["target"] = "another-project";
        LibraryJournalStore.Write(files.Journal, record);
        await Check.ThrowsAsync<InvalidDataException>(() => Task.FromResult(LibraryJournalStore.Read(files.Journal, "lib-fixture")));
    }

    internal static async Task DurableClaimNoReplay()
    {
        using var files = new Fixture();
        LibraryJournalStore.Claim(files.Root, "operator-key", "lib-original");
        // A completely fresh caller cannot reuse a dispatched key, even for the same operation.
        await Check.ThrowsAsync<IOException>(() => { LibraryJournalStore.Claim(files.Root, "operator-key", "lib-original"); return Task.CompletedTask; });
        await Check.ThrowsAsync<IOException>(() => { LibraryJournalStore.Claim(files.Root, "operator-key", "lib-other"); return Task.CompletedTask; });
        Check.Equal("lib-original", File.ReadAllText(Directory.GetFiles(files.Root, "*.claim").Single()));
    }

    internal static Task RecoveryOutcomeSurvivesRestart()
    {
        using var files = new Fixture();
        var record = Record(); record["state"] = "recovery_required";
        record["recoveryAttempts"] = new JArray(new JObject { ["attemptId"] = "one", ["state"] = "dispatching", ["resources"] = new JArray(new JObject { ["destination"] = "fresh-owned-copy", ["state"] = "dispatching" }) });
        LibraryJournalStore.Write(files.Journal, record);
        var afterCrash = LibraryJournalStore.Read(files.Journal, "lib-fixture");
        Check.Equal("recovery_outcome_unknown", LibraryJournalStore.ReconciledState(afterCrash));
        Check.Equal("fresh-owned-copy", afterCrash["recoveryAttempts"]![0]!["resources"]![0]!.Value<string>("destination"));
        afterCrash["recoveryAttempts"]![0]!["state"] = "completed";
        LibraryJournalStore.Write(files.Journal, afterCrash);
        Check.Equal("restored_to_new_locations", LibraryJournalStore.ReconciledState(LibraryJournalStore.Read(files.Journal, "lib-fixture")));
        return Task.CompletedTask;
    }

    internal static Task NativeTimestampKeepsPreviewHash()
    {
        using var files = new Fixture();
        // Seven fraction digits with a trailing zero, as TIA reports them; date parsing would drop the zero.
        var preview = new JObject { ["target"] = "original-project", ["observedAtUtc"] = "2026-10-25T19:01:02.8245720Z" };
        var record = new JObject { ["changeId"] = "lib-fixture", ["state"] = "prepared", ["preview"] = preview, ["previewHash"] = LibraryJournalStore.Hash(preview.ToString(Formatting.None)) };
        LibraryJournalStore.Write(files.Journal, record);
        var read = LibraryJournalStore.Read(files.Journal, "lib-fixture");
        Check.Equal("2026-10-25T19:01:02.8245720Z", read["preview"]!.Value<string>("observedAtUtc"));
        return Task.CompletedTask;
    }

    private static JObject Record()
    {
        var preview = new JObject { ["target"] = "original-project", ["kind"] = "library_instantiate" };
        return new JObject { ["changeId"] = "lib-fixture", ["state"] = "prepared", ["preview"] = preview, ["previewHash"] = LibraryJournalStore.Hash(preview.ToString(Formatting.None)) };
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "tia-library-journal-" + Guid.NewGuid().ToString("N"));
        internal string Journal => Path.Combine(Root, "journal.json");
        internal Fixture() { Directory.CreateDirectory(Root); }
        public void Dispose() { Directory.Delete(Root, true); }
    }
}
