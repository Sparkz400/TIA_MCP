using TiaMcp.Openness.V21;
using FingerprintException = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests;

internal static class EngineeringFingerprintTests
{
    private const string Plc = "/projects/Fixture/devices/Station/items/CPU/plc/PLC_1";
    private const string Generated = Plc + "/blocks/systemGroups/System/groups/Safety/DB1_C";

    internal static Task ProtectedNativeGeneratedBlocksDoNotRequestChecksums()
    {
        foreach (bool consistent in new[] { true, false })
        {
            var content = EngineeringFingerprintRules.ReadBlockContent<string>(true, true, consistent, true, Generated,
                () => throw new InvalidOperationException("Native checksum unavailable for generated DB1_C"));
            Check.Equal("protectedGeneratedMetadata", content.Mode);
            Check.Equal<string?>(null, content.Payload);
        }
        return Task.CompletedTask;
    }

    internal static async Task UserFingerprintFailuresCannotBecomeGeneratedExclusions()
    {
        foreach (string path in new[] { Plc + "/blocks/DB1_C", Plc + "/blocks/groups/systemGroups/DB1_C",
            Plc + "/blocks/groups/Safety/DB1_C", Generated })
        {
            var nativeFailure = new InvalidOperationException("Native user checksum failure");
            var actual = await Check.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(
                EngineeringFingerprintRules.ReadBlockContent<string>(false, true, true, true, path, () => throw nativeFailure)));
            Check.Equal(true, ReferenceEquals(nativeFailure, actual));
            var inconsistency = await Check.ThrowsAsync<FingerprintException>(() => Task.FromResult(
                EngineeringFingerprintRules.ReadBlockContent<string>(false, false, false, true, path, () => null!)));
            Check.Equal("FingerprintUnavailable", inconsistency.Code);
        }
        // Native membership alone cannot skip an editable/unprotected system block.
        await Check.ThrowsAsync<FingerprintException>(() => Task.FromResult(
            EngineeringFingerprintRules.ReadBlockContent<string>(true, false, false, true, Generated, () => "unexpected")));
        // Missing owning-PLC safety signatures cannot authorize reduced payload coverage.
        await Check.ThrowsAsync<FingerprintException>(() => Task.FromResult(
            EngineeringFingerprintRules.ReadBlockContent<string>(true, true, false, false, Generated, () => "unexpected")));
        // A contradictory canonical path must fail rather than broaden the exclusion.
        await Check.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(
            EngineeringFingerprintRules.ReadBlockContent<string>(true, true, true, true, Plc + "/blocks/DB1_C", () => "unexpected")));
    }

    internal static async Task ProtectedInWorkTestInstancesUseNativeFingerprintsOnly()
    {
        var plain = EngineeringFingerprintRules.ReadInWorkBlockContent(false, () => "export", () => throw new InvalidOperationException("unprotected blocks are exported"));
        Check.Equal("nativeExport", plain.Mode);
        Check.Equal("export", plain.Payload);
        var protectedBlock = EngineeringFingerprintRules.ReadInWorkBlockContent(true, () => throw new InvalidOperationException("protected blocks cannot be exported"), () => "fingerprints");
        Check.Equal("protectedNativeFingerprints", protectedBlock.Mode);
        Check.Equal("fingerprints", protectedBlock.Payload);
        var missing = await Check.ThrowsAsync<FingerprintException>(() => Task.FromResult(
            EngineeringFingerprintRules.ReadInWorkBlockContent(true, () => "export", () => null!)));
        Check.Equal("LibraryContentFingerprintUnavailable", missing.Code);
    }

    internal static async Task LibraryChangesRefuseInheritedInconsistentTypes()
    {
        string path = Plc + "/types/Broken";
        var refused = await Check.ThrowsAsync<FingerprintException>(() => Task.FromResult(
            EngineeringFingerprintRules.RequireConsistentType("library", () => false, path, "entry")));
        Check.Equal("InheritedDiagnostics", refused.Code);
        Check.Equal("entry", EngineeringFingerprintRules.RequireConsistentType("library", () => true, path, "entry"));
        // Other change kinds keep their type policy and never read native consistency.
        Check.Equal("entry", EngineeringFingerprintRules.RequireConsistentType<string>("source_upsert", () => throw new InvalidOperationException("not read"), path, "entry"));
    }

    private static FingerprintSaveState Saved(string lastModified = "2026-11-10T10:00:00.0000000|Utc", bool modified = false, int? process = 100, string path = @"C:\p\P.ap21") =>
        new(process, path, lastModified, modified);

    internal static Task KeptProjectContentNeedsTheSameSavedState()
    {
        var cache = new FingerprintContentCache<string>();
        Check.Equal(true, cache.Store(Saved(), Saved(), "tag_set\n", "content"));
        Check.Equal("content", cache.Find(Saved(), "tag_set\n"));
        Check.Equal("content", cache.Find(Saved(path: @"c:\P\p.AP21"), "tag_set\n")); // Windows paths compare without case.
        // Unsaved edits, a later save, another TIA process, another project, kind or target set all read in full.
        Check.Equal<string?>(null, cache.Find(Saved(modified: true), "tag_set\n"));
        Check.Equal<string?>(null, cache.Find(Saved("2026-11-10T10:00:01.0000000|Utc"), "tag_set\n"));
        Check.Equal<string?>(null, cache.Find(Saved("2026-11-10T10:00:00.0000000|Local"), "tag_set\n"));
        Check.Equal<string?>(null, cache.Find(Saved(process: 101), "tag_set\n"));
        Check.Equal<string?>(null, cache.Find(Saved(process: null), "tag_set\n"));
        Check.Equal<string?>(null, cache.Find(Saved(path: @"C:\p\Other.ap21"), "tag_set\n"));
        Check.Equal<string?>(null, cache.Find(Saved(), "source_upsert\nFC1"));
        Check.Equal<string?>(null, cache.Find(null!, "tag_set\n"));
        cache.Clear();
        Check.Equal<string?>(null, cache.Find(Saved(), "tag_set\n"));
        return Task.CompletedTask;
    }

    internal static Task ProjectContentReadDuringAChangeIsNotKept()
    {
        var cache = new FingerprintContentCache<string>();
        Check.Equal(true, cache.Store(Saved(), Saved(), "tag_set\n", "old"));
        // Modified before or after the read, or saved during it: nothing is kept, and the earlier entry is gone too.
        Check.Equal(false, cache.Store(Saved(modified: true), Saved(), "tag_set\n", "new"));
        Check.Equal<string?>(null, cache.Find(Saved(), "tag_set\n"));
        Check.Equal(false, cache.Store(Saved(), Saved(modified: true), "tag_set\n", "new"));
        Check.Equal(false, cache.Store(Saved(), Saved("2026-11-10T10:00:05.0000000|Utc"), "tag_set\n", "new"));
        Check.Equal(false, cache.Store(Saved(process: null), Saved(process: null), "tag_set\n", "new"));
        Check.Equal(false, cache.Store(Saved(), Saved(), "tag_set\n", null!));
        Check.Equal<string?>(null, cache.Find(Saved(), "tag_set\n"));
        // One entry: a newer store replaces the earlier one.
        cache.Store(Saved(), Saved(), "tag_set\n", "a");
        cache.Store(Saved("2026-11-10T11:00:00.0000000|Utc"), Saved("2026-11-10T11:00:00.0000000|Utc"), "tag_set\n", "b");
        Check.Equal<string?>(null, cache.Find(Saved(), "tag_set\n"));
        Check.Equal("b", cache.Find(Saved("2026-11-10T11:00:00.0000000|Utc"), "tag_set\n"));
        return Task.CompletedTask;
    }

    internal static Task OnlyPreparedChangeStepsKeepProjectContent()
    {
        static bool Keeps(string operation, string? kind = null, string? mutation = null, string? changeId = null, string? admitted = null, string? admittedId = null) =>
            FingerprintContentRules.Keeps(operation, kind!, mutation!, changeId!, admitted!, admittedId!);
        const string id = "0123456789abcdef0123456789abcdef";
        Check.Equal(true, Keeps("change_prepare", kind: "tag_set"));
        Check.Equal(true, Keeps("change_status") && Keeps("session_status"));
        Check.Equal(true, Keeps("mutation_begin", mutation: "change_apply", changeId: id));
        Check.Equal(true, Keeps("mutation_dispatch", admitted: "change_apply", admittedId: id) && Keeps("mutation_end", admitted: "change_apply", admittedId: id));
        // Library changes, other admitted mutations and every other operation drop it.
        Check.Equal(false, Keeps("change_prepare", kind: "library_integrate") || Keeps("change_prepare"));
        Check.Equal(false, Keeps("mutation_begin", mutation: "change_apply", changeId: "lib-1") || Keeps("mutation_begin", mutation: "scratch_create")
            || Keeps("mutation_begin", mutation: "change_apply"));
        Check.Equal(false, Keeps("mutation_dispatch", admitted: "project_retrieve", admittedId: id) || Keeps("mutation_dispatch") || Keeps("mutation_end", admitted: "change_apply", admittedId: "lib-1"));
        foreach (string other in new[] { "block_read", "software_inspect", "project_open", "attach", "detach", "start", "watch_apply_native", "translation_stage", "plc_loadable_generate", "" })
            Check.Equal(false, Keeps(other, kind: "tag_set", mutation: "change_apply", changeId: id, admitted: "change_apply", admittedId: id));
        // The kind and the target blocks are part of what was read.
        Check.Equal(FingerprintContentRules.Variant("source_upsert", new[] { "Fc2", "FC1" }), FingerprintContentRules.Variant("source_upsert", new[] { "fc1", "FC2", "fc2" }));
        Check.Equal(false, FingerprintContentRules.Variant("source_upsert", new[] { "FC1" }) == FingerprintContentRules.Variant("source_upsert", new[] { "FC1", "FC2" }));
        Check.Equal(false, FingerprintContentRules.Variant("tag_set", null!) == FingerprintContentRules.Variant("tag_delete", null!));
        return Task.CompletedTask;
    }

    internal static Task NativeFingerprintValuesAndMissingProvidersStayDistinct()
    {
        var observed = EngineeringFingerprintRules.ReadBlockContent(false, false, true, false, Plc + "/blocks/User", () => "native:1");
        var changed = EngineeringFingerprintRules.ReadBlockContent(false, false, true, false, Plc + "/blocks/User", () => "native:2");
        var missing = EngineeringFingerprintRules.ReadBlockContent<string>(false, false, true, false, Plc + "/blocks/User", () => null!);
        Check.Equal("nativeFingerprints", observed.Mode);
        Check.Equal("native:1", observed.Payload);
        Check.Equal("native:2", changed.Payload);
        Check.Equal(false, observed.Payload == changed.Payload);
        Check.Equal("metadataNoNativeProvider", missing.Mode);
        Check.Equal<string?>(null, missing.Payload);
        // #221: an inconsistent bystander is metadata-only and never read; an inconsistent target still refuses.
        var bystander = EngineeringFingerprintRules.ReadBlockContent<string>(false, false, false, false, Plc + "/blocks/Broken", () => throw new InvalidOperationException("not read"), target: false);
        Check.Equal("inconsistentMetadataOnly", bystander.Mode);
        return Task.CompletedTask;
    }
}
