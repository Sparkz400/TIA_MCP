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

        // Production combined apply (on by default since 29 October 2026) runs the shapes the component route verified
        // natively: fresh FB/dependency import, a new global instance DB and a new FBD FC or an existing one after its last network.
        // Each further feature below has a builder. A plan's exact feature combination (ShapeKey) runs in production only once
        // that combination has its own native evidence and is listed in VerifiedShapes; a verified feature does not open its
        // untested combinations. Until then only a qualification Host (--component-qualification) runs it, on its own owned
        // scratch (candidates=true).
        internal static readonly string[] VerifiedShapes = { };
        internal static string CombinedApplyRefusal(JObject preview, XDocument callerExport, bool candidates = false)
        {
            var payload = preview?["payload"] as JObject; var plan = preview?["integrationPlan"] as JObject; var caller = plan?["caller"] as JObject;
            if (payload == null || caller == null || PlcHmiIntegrationPlanner.IsRecipe(payload)) return "Only a prepared PLC component plan is eligible; HMI targets and PLC/HMI recipes stay plan-only.";
            if (caller.Value<bool?>("obCallAdded") != false || caller.Value<string>("kind") == "OB") return "OB insertion is refused; add the OB call as its own reviewed change.";
            if (!(caller.Value<string>("kind") is "FC" or "FB") || !(caller.Value<string>("language") is "FBD" or "LAD"))
                return "Only LAD/FBD FC and FB callers have a combined apply builder.";
            bool reused = plan["fb"]?.Value<bool?>("reused") == true;
            if ((plan["reuse"]?["versions"] as JArray)?.Count != (reused ? ((JArray)plan["versions"]).Count : 0) || plan["instanceDb"]?.Value<bool?>("reused") == true)
                return "Only a fresh FB/dependency import or the complete installed FB, each with new instance storage, has a builder; partial dependency reuse and reused instance DBs stay plan-only.";
            if (!(plan["blockers"] is JArray blockers) || blockers.Count != 0 || !(plan["steps"] is JArray steps) || steps.Any(s => s["planOnly"] != null))
                return "Resolve every planner blocker and planOnly step first.";
            return candidates ? null : (string)UnverifiedFeatures(plan, payload, callerExport).Properties().FirstOrDefault()?.Value;
        }

        // The plan's features beyond the first verified shape, each with its refusal, when their exact combination is not verified.
        internal static JObject UnverifiedFeatures(JObject plan, JObject payload, XDocument callerExport, System.Collections.Generic.IEnumerable<string> verifiedShapes = null)
        {
            var features = Features(plan, payload, callerExport);
            return (verifiedShapes ?? VerifiedShapes).Contains(ShapeKey(features), StringComparer.Ordinal) ? new JObject() : features;
        }
        internal static string ShapeKey(JObject features) => string.Join("+", features.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        private static JObject Features(JObject plan, JObject payload, XDocument callerExport)
        {
            var caller = (JObject)plan["caller"]; string kind = caller.Value<string>("kind"), language = caller.Value<string>("language");
            var features = new JObject();
            if (language == "LAD") features["ladCaller"] = "Only a new or existing FBD FC caller is verified; LAD " + kind + " callers stay plan-only.";
            if (kind == "FB") features["fbCaller"] = "Only a new or existing FBD FC caller is verified; " + language + " FB callers stay plan-only.";
            if (plan["multiInstance"] != null) features["multiInstance"] = "Only a fresh FB/dependency import with a new global instance DB is verified; multi-instances stay plan-only.";
            if (plan["fb"]?.Value<bool?>("reused") == true) features["reusedComponent"] = "Only a fresh FB/dependency import with a new global instance DB is verified; reuse of the installed FB stays plan-only.";
            if (!PlcLibraryIntegrationPlanner.CreatesStandaloneCaller(payload) &&
                (string)callerExport?.Descendants("SW.Blocks.CompileUnit").LastOrDefault()?.Attribute("ID") != payload["integration"]["caller"].Value<string>("insertAfterNetworkId"))
                features["earlierPlacement"] = "Only insertion after the existing caller's last network is verified; earlier placements stay plan-only.";
            return features;
        }
        // Step 1 imports and saves before step 2 creates the instance DB, so what step 2 needs from the target is decided
        // before the claim. The verified runs were de-DE only; another active language changes the installed FB/UDT exports.
        internal static string CombinedApplyLanguageRefusal(string[] active, string reference, string editing) =>
            active?.Length == 1 && active[0] == "de-DE" && reference == "de-DE" && editing == "de-DE" ? null
                : "Only de-DE-only projects are verified; this project has active languages " + string.Join(", ", active ?? new string[0]) +
                  " (reference " + reference + ", editing " + editing + ").";
        // The template route of the instance DB step, decided on the planned FB export instead of after its import.
        internal static string CombinedApplyInstanceRefusal(XDocument callee, string instanceName, System.Collections.Generic.IEnumerable<XDocument> templates)
        {
            BridgeException last = null;
            foreach (var template in templates)
                try { TypedGraphicalInstanceTemplate.Create(instanceName, 1, callee, template); return null; }
                catch (BridgeException error) { last = error; }
            return "No instance creation profile covers the FB, so the instance DB step would fail after the import: " +
                (last?.Message ?? "no instance template exists for this CPU and route.");
        }
        // The combined apply's steps live in a short workspace beside the plan's change directory, not nested inside it: TIA
        // refuses an archive whose path (the step's change directory plus "\before") exceeds 143 characters.
        internal const int TiaArchivePathLimit = 143;
        internal static string CombinedApplyWorkspace(string planDirectory, string planId) =>
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(planDirectory), "c", planId.Substring(planId.Length - 32, 8));
        // The import step's directory change-lib-<32 hex> is the longest; the typed step's change-<32 hex> is shorter.
        internal static int CombinedApplyArchivePathLength(string workspace) => System.IO.Path.Combine(workspace, "change-lib-" + new string('0', 32), "before").Length;
        internal static JObject CombinedApplyPathRefusal(string workspace)
        {
            int length = CombinedApplyArchivePathLength(workspace);
            return length <= TiaArchivePathLimit ? null : new JObject { ["code"] = "ComponentPathTooLong", ["length"] = length, ["limit"] = TiaArchivePathLimit,
                ["message"] = "The step checkpoint archive path would be " + length + " characters, over TIA's " + TiaArchivePathLimit + "; start the Host with an artifact root at least " + (length - TiaArchivePathLimit) + " characters shorter." };
        }

        // CreateFrom keeps a library block's number only while it is free; an occupied number renumbers the imported block, and a
        // captured instance profile compares the reference FB export with its number, so the typed step would fail after the import is saved.
        internal static JObject CombinedApplyNumberRefusal(System.Collections.Generic.IEnumerable<XDocument> imported, System.Collections.Generic.IEnumerable<JObject> target,
            string capturedFbName = null, int? capturedFbNumber = null)
        {
            foreach (var document in imported)
            {
                var block = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "SW.Blocks.FB" || e.Name.LocalName == "SW.Blocks.FC");
                if (block == null) continue; // PLC data types have no block number
                string kind = block.Name.LocalName.Substring("SW.Blocks.".Length), name = (string)block.Element("AttributeList")?.Element("Name");
                if (!int.TryParse((string)block.Element("AttributeList")?.Element("Number"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number))
                    return Refusal("ComponentBlockNumberUnknown", kind + " " + name + " exports no block number, so the number its import takes cannot be checked before the claim.");
                if (kind == "FB" && name == capturedFbName && number != capturedFbNumber)
                    return Refusal("CapturedStorageProfileUnavailable", "The captured instance profile binds " + name + " as FB" + capturedFbNumber + ", but the library version exports FB" + number + ".");
                var used = target.FirstOrDefault(t => t.Value<string>("kind") == kind && t.Value<int?>("number") == number);
                if (used != null)
                {
                    var refusal = Refusal("ComponentBlockNumberOccupied", kind + number + " is already used by " + used.Value<string>("path") + ". Importing " + name +
                        " would renumber it" + (name == capturedFbName ? ", which the captured instance profile does not cover" : "") + "; free or renumber that block first.");
                    refusal["conflictingBlock"] = used.Value<string>("path"); refusal["number"] = number; refusal["kind"] = kind; refusal["importedBlock"] = name;
                    return refusal;
                }
            }
            return null;
        }
        private static JObject Refusal(string code, string message) => new JObject { ["code"] = code, ["message"] = message };

        internal static string CombinedApplyStep(string state, bool reusedFb = false) =>
            state == "created" ? (reusedFb ? "prepare_graphical_typed_edit" : "prepare_library_instantiate") : state == "library_prepared" ? "apply_library_instantiate" : state == "library_applied" ? "verify_import_readback" :
            state == "import_verified" ? "prepare_graphical_typed_edit" : state == "graphical_prepared" ? "apply_graphical_typed_edit" : state == "graphical_applied" ? "verify_installation_readback" : state;
    }
}
