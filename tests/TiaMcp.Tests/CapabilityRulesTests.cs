using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class CapabilityRulesTests
{
    internal static Task AvailabilitySeparatesEvidence()
    {
        Check.Equal("available", CapabilityRules.Availability("available", "implemented", "verified"));
        Check.Equal("conditional", CapabilityRules.Availability("available", "implemented", "unverified"));
        Check.Equal("conditional", CapabilityRules.Availability("available", "partial", "verified"));
        Check.Equal("conditional", CapabilityRules.Availability("available", "implemented", "verified", hasConditions: true));
        Check.Equal("unknown", CapabilityRules.Availability("unknown", "implemented", "verified"));
        Check.Equal("unsupported", CapabilityRules.Availability("unavailable", "implemented", "verified"));
        Check.Equal("unsupported", CapabilityRules.Availability("available", "not_implemented", "unverified"));
        return Task.CompletedTask;
    }

    internal static Task AvailabilityPreservesTargetBlockers()
    {
        Check.Equal("blocked", CapabilityRules.Availability("available", "implemented", "unverified", blocked: true));
        Check.Equal("blocked", CapabilityRules.Availability("unknown", "partial", "unverified", blocked: true));
        Check.Equal("blocked", CapabilityRules.Availability("available", "implemented", "failed"));
        Check.Equal("unsupported", CapabilityRules.Availability("available", "implemented", "verified", applicable: false));
        Check.Equal("unsupported", CapabilityRules.Availability("unavailable", "implemented", "unverified", blocked: true));
        return Task.CompletedTask;
    }

    internal static async Task AvailabilityRejectsUnknownStates()
    {
        await Check.ThrowsAsync<ArgumentOutOfRangeException>(() => Task.FromResult(CapabilityRules.Availability("supported", "implemented", "verified")));
        await Check.ThrowsAsync<ArgumentOutOfRangeException>(() => Task.FromResult(CapabilityRules.Availability("available", "full", "verified")));
        await Check.ThrowsAsync<ArgumentOutOfRangeException>(() => Task.FromResult(CapabilityRules.Availability("available", "implemented", "passed")));
    }

    internal static Task SafetyImportEligibilityPreservesNativeBoundary()
    {
        foreach (string language in new[] { "F_LAD", "F_FBD", "F_DB" })
        {
            Check.Equal<string?>(null, CapabilityRules.SafetyImportBlocker(language, false, true));
            Check.Equal(true, CapabilityRules.SafetyImportBlocker(language, true, true)!.Contains("know-how protected"));
            Check.Equal(true, CapabilityRules.SafetyImportBlocker(language, false, false)!.Contains("inconsistent"));
        }
        foreach (string language in new[] { "F_CALL", "F_OB", "F_SCL", "SCL", "", "UnknownFutureFLanguage" })
            Check.Equal(true, CapabilityRules.SafetyImportBlocker(language, false, true)!.Contains("language is excluded"));
        // A reported protection failure may never become conditional just because the
        // CPU's SafetyAdministration service exists and the user is logged in.
        string reason = CapabilityRules.SafetyImportBlocker("F_FBD", true, true)!;
        Check.Equal("blocked", CapabilityRules.Availability("available", "partial", "unverified", blocked: reason != null));
        return Task.CompletedTask;
    }
}
