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
        // Passed natively on 31 October (local.library_integrate.candidate_shapes.rerun.20261031): a new or existing LAD FC caller, an
        // existing FBD FB caller, an existing FBD FC after an earlier network, and reuse of the installed FB with a new FBD FC caller.
        // Passed natively on 1 November (local.library_integrate.candidate_shapes.multi.20261101): a multi-instance of the reused
        // installed FB in an existing FBD FB caller, through the captured template local.typed.component-storage-capture.20261101.
        // Passed natively on 2 November (local.library_integrate.candidate_shapes.combinations.20261102): every combination of
        // ladCaller, fbCaller, earlierPlacement, reusedComponent and multiInstance a plan can have; and with the captured instance-DB
        // effect local.typed.component-effect-capture.20261102, each multi-instance combination whose caller FB has its own instance
        // DB (local.library_integrate.candidate_shapes.dependent_db.20261102).
        // local.library_integrate.candidate_shapes.embedding_parent.20261107: a multi-instance caller FB embedded in a parent FB, through the captured component transitive effect.
        // local.component.ob.20261102 also verifies a fresh component with a global instance DB after the last network of an FBD OB, and
        // local.library_integrate.candidate_shapes.ob_combinations.20261107 every combination of an OB caller with ladCaller, earlierPlacement and reusedComponent.
        // local.library_integrate.candidate_shapes.ob_event_classes.20261108: a fresh component after the last network of an FBD startup OB and of an FBD cyclic-interrupt OB.
        // local.library_integrate.candidate_shapes.ob_event_classes_2.20261108: time-of-day, time-delay, hardware and diagnostic error interrupt OBs, and the cyclic-interrupt OB as LAD, after an earlier network and with reuse.
        // local.library_integrate.candidate_shapes.ob_combinations_2.20261108: the startup, time-of-day, time-delay, hardware and diagnostic error interrupt OBs as LAD, after an earlier network and with reuse.
        internal static readonly string[] VerifiedShapes = { "earlierPlacement+obCaller+obEventClass.Startup", "ladCaller+obCaller+obEventClass.Startup", "obCaller+obEventClass.Startup+reusedComponent", "earlierPlacement+obCaller+obEventClass.TimeOfDay", "ladCaller+obCaller+obEventClass.TimeOfDay", "obCaller+obEventClass.TimeOfDay+reusedComponent", "earlierPlacement+obCaller+obEventClass.TimeDelayInterrupt", "ladCaller+obCaller+obEventClass.TimeDelayInterrupt", "obCaller+obEventClass.TimeDelayInterrupt+reusedComponent", "earlierPlacement+obCaller+obEventClass.HardwareInterrupt", "ladCaller+obCaller+obEventClass.HardwareInterrupt", "obCaller+obEventClass.HardwareInterrupt+reusedComponent", "earlierPlacement+obCaller+obEventClass.DiagnosticErrorInterrupt", "ladCaller+obCaller+obEventClass.DiagnosticErrorInterrupt", "obCaller+obEventClass.DiagnosticErrorInterrupt+reusedComponent", "earlierPlacement+obCaller+obEventClass.CyclicInterrupt", "ladCaller+obCaller+obEventClass.CyclicInterrupt", "obCaller+obEventClass.CyclicInterrupt+reusedComponent", "obCaller+obEventClass.DiagnosticErrorInterrupt", "obCaller+obEventClass.HardwareInterrupt", "obCaller+obEventClass.TimeDelayInterrupt", "obCaller+obEventClass.TimeOfDay", "obCaller+obEventClass.CyclicInterrupt", "obCaller+obEventClass.Startup", "dependentInstanceDb+earlierPlacement+embeddingParent+fbCaller+ladCaller+multiInstance+reusedComponent", "dependentInstanceDb+earlierPlacement+embeddingParent+fbCaller+multiInstance+reusedComponent", "dependentInstanceDb+earlierPlacement+fbCaller+ladCaller+multiInstance+reusedComponent", "dependentInstanceDb+earlierPlacement+fbCaller+multiInstance+reusedComponent", "dependentInstanceDb+embeddingParent+fbCaller+ladCaller+multiInstance+reusedComponent", "dependentInstanceDb+embeddingParent+fbCaller+multiInstance+reusedComponent", "dependentInstanceDb+fbCaller+ladCaller+multiInstance+reusedComponent", "dependentInstanceDb+fbCaller+multiInstance+reusedComponent", "earlierPlacement", "earlierPlacement+embeddingParent+fbCaller+ladCaller+multiInstance+reusedComponent", "earlierPlacement+embeddingParent+fbCaller+multiInstance+reusedComponent", "earlierPlacement+fbCaller", "earlierPlacement+fbCaller+ladCaller", "earlierPlacement+fbCaller+ladCaller+multiInstance+reusedComponent", "earlierPlacement+fbCaller+ladCaller+reusedComponent", "earlierPlacement+fbCaller+multiInstance+reusedComponent", "earlierPlacement+fbCaller+reusedComponent", "earlierPlacement+ladCaller", "earlierPlacement+ladCaller+obCaller", "earlierPlacement+ladCaller+obCaller+reusedComponent", "earlierPlacement+ladCaller+reusedComponent", "earlierPlacement+obCaller", "earlierPlacement+obCaller+reusedComponent", "earlierPlacement+reusedComponent", "embeddingParent+fbCaller+ladCaller+multiInstance+reusedComponent", "embeddingParent+fbCaller+multiInstance+reusedComponent", "fbCaller", "fbCaller+ladCaller", "fbCaller+ladCaller+multiInstance+reusedComponent", "fbCaller+ladCaller+reusedComponent", "fbCaller+multiInstance+reusedComponent", "fbCaller+reusedComponent", "ladCaller", "ladCaller+obCaller", "ladCaller+obCaller+reusedComponent", "ladCaller+reusedComponent", "obCaller", "obCaller+reusedComponent", "reusedComponent" };
        internal static string CombinedApplyRefusal(JObject preview, XDocument callerExport, bool candidates = false, bool dependentInstanceDb = false, bool embeddingParent = false)
        {
            var payload = preview?["payload"] as JObject; var plan = preview?["integrationPlan"] as JObject; var caller = plan?["caller"] as JObject;
            if (payload == null || caller == null || PlcHmiIntegrationPlanner.IsRecipe(payload)) return "Only a prepared PLC component plan is eligible; HMI targets and PLC/HMI recipes stay plan-only.";
            if (caller.Value<bool?>("obCallAdded") != (caller.Value<string>("kind") == "OB")) return "The caller's OB identity is inconsistent.";
            if (!(caller.Value<string>("kind") is "FC" or "FB" or "OB") || !(caller.Value<string>("language") is "FBD" or "LAD"))
                return "Only LAD/FBD FC, FB and OB callers have a combined apply builder.";
            bool reused = plan["fb"]?.Value<bool?>("reused") == true;
            if ((plan["reuse"]?["versions"] as JArray)?.Count != (reused ? ((JArray)plan["versions"]).Count : 0) || plan["instanceDb"]?.Value<bool?>("reused") == true)
                return "Only a fresh FB/dependency import or the complete installed FB, each with new instance storage, has a builder; partial dependency reuse and reused instance DBs stay plan-only.";
            if (!(plan["blockers"] is JArray blockers) || blockers.Count != 0 || !(plan["steps"] is JArray steps) || steps.Any(s => s["planOnly"] != null))
                return "Resolve every planner blocker and planOnly step first.";
            var unverified = candidates ? new JObject() : UnverifiedFeatures(plan, payload, callerExport, null, dependentInstanceDb, embeddingParent);
            if (unverified.Count == 0) return null;
            return (string)unverified.Properties().FirstOrDefault(p => !VerifiedShapes.Contains(p.Name, StringComparer.Ordinal))?.Value
                ?? "The combination " + ShapeKey(unverified) + " of separately verified features has no native evidence; it stays plan-only.";
        }

        // The plan's features beyond the first verified shape, each with its refusal, when their exact combination is not verified.
        internal static JObject UnverifiedFeatures(JObject plan, JObject payload, XDocument callerExport, System.Collections.Generic.IEnumerable<string> verifiedShapes = null,
            bool dependentInstanceDb = false, bool embeddingParent = false)
        {
            var features = Features(plan, payload, callerExport, dependentInstanceDb, embeddingParent);
            return (verifiedShapes ?? VerifiedShapes).Contains(ShapeKey(features), StringComparer.Ordinal) ? new JObject() : features;
        }
        internal static string ShapeKey(JObject features) => string.Join("+", features.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        // dependentInstanceDb: the multi-instance caller FB has its own instance DBs, which gain the new member (worker-observed).
        // embeddingParent: it has dependents beyond them (parent FBs embedding it, their DBs and users), covered by the transitive effect.
        private static JObject Features(JObject plan, JObject payload, XDocument callerExport, bool dependentInstanceDb, bool embeddingParent = false)
        {
            var caller = (JObject)plan["caller"]; string kind = caller.Value<string>("kind"), language = caller.Value<string>("language");
            var features = new JObject();
            if (kind == "OB") features["obCaller"] = "OB insertion is refused until its exact shape passes native qualification; add the OB call as its own reviewed change.";
            // The OB evidence ran on program-cycle OBs; each other event class (or a missing one) is its own feature until it has its own run.
            string eventClass = kind == "OB" ? callerExport?.Descendants("SW.Blocks.OB").FirstOrDefault()?.Element("AttributeList")?.Element("SecondaryType")?.Value : null;
            if (kind == "OB" && eventClass != "ProgramCycle")
                features["obEventClass." + (eventClass ?? "Unknown")] = "Only program-cycle OB callers are verified; this " + (eventClass ?? "unknown") + " OB stays plan-only.";
            if (language == "LAD") features["ladCaller"] = "A LAD caller is verified as a new or existing FC and as an existing FB, alone and in the natively run combinations; this LAD " + kind + " combination stays plan-only.";
            if (kind == "FB") features["fbCaller"] = "An existing LAD or FBD FB caller is verified with a new global instance DB or a multi-instance of the reused FB, alone and in the natively run combinations; this " + language + " FB combination stays plan-only.";
            if (plan["multiInstance"] != null) features["multiInstance"] = "A multi-instance is verified only for the reused installed FB in an existing LAD or FBD FB caller; other multi-instances stay plan-only.";
            if (dependentInstanceDb && plan["multiInstance"] != null)
                features["dependentInstanceDb"] = "A multi-instance caller FB with its own instance DB is verified only in the natively run multi-instance combinations; this one stays plan-only.";
            if (embeddingParent && plan["multiInstance"] != null)
                features["embeddingParent"] = "A multi-instance caller FB embedded in a parent FB is verified for the reused FB in an existing FBD FB caller; this combination stays plan-only.";
            if (plan["fb"]?.Value<bool?>("reused") == true) features["reusedComponent"] = "Reuse of the installed FB is verified with a new or existing FC or an existing FB caller and a new global instance DB, or as a multi-instance in an existing FB caller; this reuse combination stays plan-only.";
            if (!PlcLibraryIntegrationPlanner.CreatesStandaloneCaller(payload) &&
                (string)callerExport?.Descendants("SW.Blocks.CompileUnit").LastOrDefault()?.Attribute("ID") != payload["integration"]["caller"].Value<string>("insertAfterNetworkId"))
                features["earlierPlacement"] = "An earlier placement is verified in existing LAD and FBD FC and FB callers, alone and in the natively run combinations; this combination stays plan-only.";
            return features;
        }
        // Step 1 imports and saves before step 2 creates the instance DB, so what step 2 needs from the target is decided
        // before the claim. The verified runs were de-DE only; another active language changes the installed FB/UDT exports.
        internal static string CombinedApplyLanguageRefusal(string[] active, string reference, string editing) =>
            active?.Length == 1 && active[0] == "de-DE" && reference == "de-DE" && editing == "de-DE" ? null
                : "Only de-DE-only projects are verified; this project has active languages " + string.Join(", ", active ?? new string[0]) +
                  " (reference " + reference + ", editing " + editing + ").";
        // A multi-instance changes the caller FB's interface, so the typed step also changes each dependent the typed route finds
        // (its instance DBs, embedding parents and their users). Only the caller's direct instance DBs have a captured effect for
        // the component's structured member (instance-effects-component.v1.json); any other dependent is refused before admission and claim.
        // transitiveCovered: the component's transitive effect is installed and admitted here, so the typed preparation, which runs
        // before any mutation, decides each embedding parent, its DBs and users itself.
        internal static JObject CombinedApplyDependentsRefusal(JObject plan, System.Collections.Generic.IEnumerable<string> dependents,
            System.Collections.Generic.IEnumerable<string> directInstanceDbs = null, bool transitiveCovered = false)
        {
            if (transitiveCovered) return null;
            var covered = new System.Collections.Generic.HashSet<string>(directInstanceDbs ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var paths = plan?["multiInstance"] == null ? new string[0] : dependents.Where(p => !covered.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (paths.Length == 0) return null;
            return new JObject { ["code"] = "DependencyProfileUnavailable", ["dependents"] = new JArray(paths),
                ["message"] = "The multi-instance caller " + plan["caller"]?.Value<string>("blockPath") + " has dependents (" + string.Join(", ", paths.Take(3)) +
                    (paths.Length > 3 ? " and " + (paths.Length - 3) + " more" : "") + ") other than its own instance DBs; embedding parents and their users would need a captured transitive effect for the new member, and none covers it." };
        }
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
        // change_restore retrieves the checkpoint into <changes>/lr_<10 hex>/<project> (SharedMutationLibraryRecoveryDirectory);
        // TIA limits that project directory to 143 characters as well.
        internal static int CombinedApplyRecoveryPathLength(string planDirectory, string projectName) =>
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(planDirectory), "lr_" + new string('0', 10), projectName).Length;
        internal static JObject CombinedApplyPathRefusal(string planDirectory, string planId, string projectName)
        {
            int archive = CombinedApplyArchivePathLength(CombinedApplyWorkspace(planDirectory, planId)), recovery = CombinedApplyRecoveryPathLength(planDirectory, projectName);
            int length = Math.Max(archive, recovery);
            return length <= TiaArchivePathLimit ? null : new JObject { ["code"] = "ComponentPathTooLong", ["length"] = length, ["limit"] = TiaArchivePathLimit,
                ["archivePathLength"] = archive, ["recoveryPathLength"] = recovery,
                ["message"] = (archive >= recovery ? "The step checkpoint archive path" : "The recovered project directory of change_restore") + " would be " + length +
                    " characters, over TIA's " + TiaArchivePathLimit + "; start the Host with an artifact root at least " + (length - TiaArchivePathLimit) + " characters shorter." };
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
                if (kind == "FB" && name == capturedFbName && capturedFbNumber != null && number != capturedFbNumber)
                    return Refusal("CapturedStorageProfileUnavailable", "The captured instance profile binds " + name + " as FB" + capturedFbNumber + ", but the library version exports FB" + number + ".");
                var used = target.FirstOrDefault(t => t.Value<string>("kind") == kind && t.Value<int?>("number") == number);
                if (used != null)
                {
                    var refusal = Refusal("ComponentBlockNumberOccupied", kind + number + " is already used by " + used.Value<string>("path") + ". Importing " + name +
                        " would renumber it" + (name == capturedFbName && capturedFbNumber != null ? ", which the captured instance profile does not cover" : "") + "; free or renumber that block first.");
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
