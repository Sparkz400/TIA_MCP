#nullable disable
using System;

namespace TiaMcp.Openness.V21
{
    // Pure policy: observations, implementation coverage and qualification must never
    // collapse into one optimistic Boolean. No Siemens assembly is used here.
    internal static class CapabilityRules
    {
        internal static string Availability(string api, string implementation, string qualification,
            bool applicable = true, bool blocked = false, bool hasConditions = false)
        {
            Require(api, "api", "available", "unavailable", "unknown");
            Require(implementation, "implementation", "implemented", "partial", "not_implemented");
            Require(qualification, "qualification", "verified", "unverified", "failed");
            if (!applicable || implementation == "not_implemented" || api == "unavailable") return "unsupported";
            if (blocked || qualification == "failed") return "blocked";
            if (api == "unknown") return "unknown";
            if (hasConditions || implementation == "partial" || qualification == "unverified") return "conditional";
            return "available";
        }

        // Mirrors the exposed safety import envelope and existing-target eligibility,
        // without performing an export or asking the native API to change protection.
        internal static string SafetyImportBlocker(string language, bool knowHowProtected, bool consistent)
        {
            if (knowHowProtected) return "The selected safety block is know-how protected.";
            if (!consistent) return "The selected safety block is inconsistent; only consistent exportable user F-blocks are eligible.";
            if (language != "F_LAD" && language != "F_FBD" && language != "F_DB")
                return "The selected block language is excluded from safety XML import; only F_LAD, F_FBD and F_DB are supported.";
            return null;
        }

        private static void Require(string value, string parameter, params string[] allowed)
        {
            if (Array.IndexOf(allowed, value) < 0) throw new ArgumentOutOfRangeException(parameter, value, "Unknown capability state.");
        }
    }
}
