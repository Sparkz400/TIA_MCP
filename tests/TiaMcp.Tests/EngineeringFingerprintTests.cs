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
        return Task.CompletedTask;
    }
}
