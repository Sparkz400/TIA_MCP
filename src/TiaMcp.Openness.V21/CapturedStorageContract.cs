#nullable disable
using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Pure validation of the durable qualification contract. Native readers
    // supply observations; callers cannot create a live qualification context.
    internal static class CapturedStorageContract
    {
        internal static void RequirePlan(JObject profile, JArray pins, string archiveHash)
        {
            if (profile?.Value<bool?>("qualificationOnly") != true || profile.Value<bool?>("productionApplySupported") != false ||
                profile.Value<string>("sourceArchiveSha256") != archiveHash || profile["sourcePins"] is not JArray expected || pins == null || pins.Count != expected.Count)
                Fail("The exact archived source and explicit qualification-only profile are required.");
            foreach (JObject pin in pins)
            {
                var matches = ((JArray)profile["sourcePins"]).OfType<JObject>().Where(p => p.Value<string>("typeGuid") == pin.Value<string>("typeGuid") && p.Value<string>("versionGuid") == pin.Value<string>("versionGuid")).ToArray();
                if (matches.Length != 1 || pins.Count(p => p.Value<string>("versionGuid") == pin.Value<string>("versionGuid")) != 1)
                    Fail("Every exact source identity must occur once.");
                var match = matches[0];
                if (match.Value<string>("typeName") != pin.Value<string>("typeName") || match.Value<string>("contentSha256") != pin.Value<string>("contentSha256") ||
                    match.Value<string>("sourceOracleSha256") != pin["contentPolicy"]?.Value<string>("sourceOracleSha256") ||
                    !JToken.DeepEquals(match["requiredVersionGuids"], pin["requiredVersionGuids"]) || pin.Value<string>("state") != "Committed" || pin.Value<bool?>("doNotUse") != false)
                    Fail("Current plan differs from exact source identity/content/closure pins.");
            }
        }
        internal static void RequireReceipt(JObject receipt, string planId, string previewHash, string contentHash, string profileHash, JObject observation)
        {
            if (receipt?.Value<string>("kind") != "capturedStorageQualification" || receipt.Value<bool?>("qualificationOnly") != true ||
                receipt.Value<bool?>("productionApplySupported") != false || receipt.Value<string>("planChangeId") != planId ||
                receipt.Value<string>("planPreviewHash") != previewHash || receipt.Value<string>("planContentHash") != contentHash ||
                receipt.Value<string>("profileSha256") != profileHash)
                Fail("Durable captured storage receipt differs from its private current-session plan/profile.");
            foreach (string field in new[] { "bindings", "environment", "cpuIdentifier", "route", "callerLanguage" })
                if (receipt[field] == null || observation?[field] == null || !JToken.DeepEquals(receipt[field], observation[field]))
                    Fail("Captured native identity/content/environment changed: " + field);
        }
        private static void Fail(string message) => throw new BridgeException("StaleCapturedStorageContract", message);
    }
}
