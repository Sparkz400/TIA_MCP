using System;
using System.Linq;
using System.Xml.Linq;
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

        // Production combined apply (on by default since 29 October 2026) runs only the shapes the component route
        // verified natively: fresh FB/dependency import, a new global instance DB and a new FBD FC or an existing one after its last network.
        internal static string CombinedApplyRefusal(JObject preview, XDocument callerExport)
        {
            var payload = preview?["payload"] as JObject; var plan = preview?["integrationPlan"] as JObject; var caller = plan?["caller"] as JObject;
            if (payload == null || caller == null || PlcHmiIntegrationPlanner.IsRecipe(payload)) return "Only a prepared PLC component plan is eligible; HMI targets and PLC/HMI recipes stay plan-only.";
            if (caller.Value<bool?>("obCallAdded") != false || caller.Value<string>("kind") == "OB") return "OB insertion is refused; add the OB call as its own reviewed change.";
            if (caller.Value<string>("kind") != "FC" || caller.Value<string>("language") != "FBD")
                return "Only a new or existing FBD FC caller is verified; " + caller.Value<string>("language") + " " + caller.Value<string>("kind") + " callers stay plan-only.";
            if (plan["fb"]?.Value<bool?>("reused") != false || (plan["reuse"]?["versions"] as JArray)?.Count != 0 || plan["instanceDb"] == null || plan["instanceDb"].Value<bool?>("reused") == true)
                return "Only a fresh FB/dependency import with a new global instance DB is verified; reuse and multi-instances stay plan-only.";
            if (!(plan["blockers"] is JArray blockers) || blockers.Count != 0 || !(plan["steps"] is JArray steps) || steps.Any(s => s["planOnly"] != null))
                return "Resolve every planner blocker and planOnly step first.";
            if (!PlcLibraryIntegrationPlanner.CreatesStandaloneCaller(payload) &&
                (string)callerExport?.Descendants("SW.Blocks.CompileUnit").LastOrDefault()?.Attribute("ID") != payload["integration"]["caller"].Value<string>("insertAfterNetworkId"))
                return "Only insertion after the existing caller's last network is verified; earlier placements stay plan-only.";
            return null;
        }
        internal static string CombinedApplyStep(string state) =>
            state == "created" ? "prepare_library_instantiate" : state == "library_prepared" ? "apply_library_instantiate" : state == "library_applied" ? "verify_import_readback" :
            state == "import_verified" ? "prepare_graphical_typed_edit" : state == "graphical_prepared" ? "apply_graphical_typed_edit" : state == "graphical_applied" ? "verify_installation_readback" : state;
    }
}
