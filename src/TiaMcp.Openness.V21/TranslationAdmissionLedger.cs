using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Pure ledger rules of one installed translation admission entry, mirroring the Host's TranslationProductionAdmission
    // and TranslationProductionRegistry. Linked into tests/TiaMcp.Tests; field shapes and evidence bytes are checked by the caller.
    internal static class TranslationAdmissionLedger
    {
        internal const string WholeProjectContract = "translation.whole-project-de-DE-en-GB-main-ob1.exact.v1";
        internal const string RestrictedContract = "translation.plc-block-comments-de-DE-en-GB-main-ob1.exact.v1";
        private static readonly string[] DefaultModeCases = { "default-mode-lifecycle", "worker-loss-at-checkpoint-intent", "worker-loss-during-import", "host-loss-during-import" };

        // A provisional restricted entry (no default-mode packet yet) is valid only in the qualification deployment it was built
        // for. The worker is told no Host path, so it binds its own directory; the Host binds its installation directory itself.
        internal static void Check(JObject entry, string workerDirectory)
        {
            var key = (JObject)entry["profile"]["key"];
            var proofs = (JArray)entry["profile"]["proofs"];
            string contract = key.Value<string>("contract");
            bool restricted = contract == RestrictedContract, provisional = entry["qualification"] != null;
            if (!restricted && contract != WholeProjectContract)
                throw new BridgeException("TranslationAdmissionMismatch", "Installed worker/profile identity changed.");
            if (provisional && (!restricted || !SameDirectory((entry["qualification"] as JObject)?.Value<string>("workerDirectory"), workerDirectory)))
                throw new BridgeException("TranslationAdmissionProvisional", "A provisional restricted admission is valid only in its qualification deployment.");
            // 14 native and 6 portable cases; restricted adds its category-filter case and, once final, the four default-mode
            // cases. Those cite one evidence file that no other proof cites, so the first packet's receipt cannot stand in.
            var defaultMode = proofs.Where(proof => DefaultModeCases.Contains(proof.Value<string>("case"))).ToArray();
            var run2 = defaultMode.Select(proof => proof.Value<string>("evidenceSha256")).Distinct(StringComparer.Ordinal).ToArray();
            bool final = restricted && !provisional;
            if (proofs.Count != (!restricted ? 20 : provisional ? 21 : 25) ||
                (final ? defaultMode.Length != DefaultModeCases.Length || defaultMode.Select(proof => proof.Value<string>("case")).Distinct(StringComparer.Ordinal).Count() != DefaultModeCases.Length ||
                    run2.Length != 1 || proofs.Any(proof => !DefaultModeCases.Contains(proof.Value<string>("case")) && proof.Value<string>("evidenceSha256") == run2[0])
                    : defaultMode.Length != 0))
                throw new BridgeException("TranslationAdmissionIncomplete", "Complete reviewed native and portable proof ledger required.");
        }

        internal static bool SameDirectory(string path, string directory) => path != null && Path.IsPathRooted(path) &&
            string.Equals(Path.GetFullPath(path).TrimEnd('\\', '/'), Path.GetFullPath(directory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }
}
