using System;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Pure launch/state boundary, shared by the native adapter and offline tests.
    internal static class PlcComponentQualificationGate
    {
        internal const string Kind = "library_component_qualification";
        internal static bool IsId(string id) => id?.StartsWith("lib-component-", StringComparison.Ordinal) == true;
        internal static void RequireEnabled(bool enabled)
        {
            if (!enabled) throw new PlcComponentException("ComponentQualificationDisabled", "Start the explicit owned-fixture host with --component-qualification. Production combined apply remains unavailable.");
        }
        internal static void RequireOwned(bool enabled, bool ownsPortal, bool exactScratch, int projectCount)
        {
            RequireEnabled(enabled);
            if (!ownsPortal || !exactScratch || projectCount != 1)
                throw new PlcComponentException("OwnedScratchRequired", "Component qualification requires the worker's exact newly created scratch in its single-project owned TIA instance.");
        }
        internal static void RequireReadOnlyAdvance(string state)
        {
            if (state != "created" && state != "library_applied" && state != "import_verified" && state != "graphical_applied" && state != "installation_verified")
                throw new PlcComponentException("ComponentStepReviewRequired", "Preparation cannot dispatch a native mutation. Review the current step and use change_apply with its exact previewHash; uncertain outcomes allow status reconciliation only.");
        }
        internal static void RequireApply(JObject record, JObject args)
        {
            string state = record.Value<string>("state"), step = state == "library_prepared" ? "library" : state == "graphical_prepared" ? "graphical" : null;
            if (step == null) throw new PlcComponentException("ComponentOutcomeUnknown", "Only an undispatched prepared step can be applied. A dispatched step is never replayed.");
            foreach (string field in new[] { "checkpointSave", "compile", "save" })
                if (args[field]?.Type != JTokenType.Boolean || args.Value<bool>(field) != true)
                    throw new PlcComponentException("InvalidRequest", "Component qualification requires explicit " + field + "=true.");
            if (args.Value<string>("previewHash") != record[step]["prepared"].Value<string>("previewHash"))
                throw new PlcComponentException("ComponentStepReviewRequired", "The exact current step previewHash is required.");
            // The actual dispatch key is fixed by the coordinator, so a caller cannot
            // turn a lost-response retry into a second native dispatch.
            if (string.IsNullOrWhiteSpace(args.Value<string>("idempotencyKey")))
                throw new PlcComponentException("InvalidRequest", "An explicit caller idempotencyKey is required for the review request.");
        }
    }
}
