using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private readonly bool _componentQualificationEnabled;
        private readonly Dictionary<string, ComponentQualificationRun> _componentQualificationRuns = new Dictionary<string, ComponentQualificationRun>(StringComparer.Ordinal);
        private sealed class ComponentQualificationRun { internal PlcComponentCoordinator Coordinator; internal Project Project; }

        private JObject ComponentQualificationPrepare(JObject args) => ComponentQualificationCall(() => {
            PlcComponentQualificationGate.RequireEnabled(_componentQualificationEnabled);
            var payload = args["payload"] as JObject;
            if (payload == null || payload["schemaVersion"]?.Type != JTokenType.Integer || payload.Value<int>("schemaVersion") != 1)
                throw new BridgeException("InvalidRequest", "Qualification payload requires schemaVersion=1.");
            string action = ChangeText(payload, "action", 16);
            var allowed = action == "start" ? new[] { "schemaVersion", "action", "planChangeId", "planPreviewHash" } : new[] { "schemaVersion", "action", "changeId" };
            if (payload.Properties().Any(p => !allowed.Contains(p.Name))) throw new BridgeException("InvalidRequest", "Unknown component qualification field.");
            if (action == "start")
            {
                RequireComponentScratch(_scratchProject);
                var coordinator = CreatePlcComponentQualificationCoordinator(ChangeText(payload, "planChangeId", 64), ChangeText(payload, "planPreviewHash", 64), ArtifactDirectory(args));
                var run = new ComponentQualificationRun { Coordinator = coordinator, Project = _scratchProject };
                string id = "lib-" + coordinator.Status().Value<string>("operationId");
                _componentQualificationRuns.Add(id, run);
                // created -> library_prepared only. No checkpoint, compile, save or apply.
                return ComponentQualificationView(coordinator.Advance());
            }
            var current = ComponentQualificationRunFor(ChangeText(payload, "changeId", 64));
            if (action == "reconcile") return ComponentQualificationView(current.Coordinator.Reconcile());
            if (action != "advance") throw new BridgeException("InvalidRequest", "Qualification action must be start, advance or reconcile.");
            PlcComponentQualificationGate.RequireReadOnlyAdvance(current.Coordinator.Status().Value<string>("state"));
            return ComponentQualificationView(current.Coordinator.Advance());
        });

        private JObject ComponentQualificationApply(JObject args) => ComponentQualificationCall(() => {
            var run = ComponentQualificationRunFor(ChangeText(args, "changeId", 64));
            PlcComponentQualificationGate.RequireApply(run.Coordinator.Status(), args);
            return ComponentQualificationView(run.Coordinator.Advance(ChangeText(args, "previewHash", 64)));
        });

        private JObject ComponentQualificationStatus(JObject args) => ComponentQualificationCall(() => {
            PlcComponentQualificationGate.RequireEnabled(_componentQualificationEnabled);
            string id = ChangeText(args, "changeId", 64);
            if (!Regex.IsMatch(id, "^lib-component-[0-9a-f]{32}$")) throw new BridgeException("InvalidRequest", "Invalid qualification operation identity.");
            if (_componentQualificationRuns.TryGetValue(id, out var run)) return ComponentQualificationView(run.Coordinator.Status());
            // Restart inspection is journal-only. No native adapter or live handle is
            // reconstructed, so this cannot resume or reconcile an expired operation.
            return ComponentQualificationView(PlcComponentCoordinator.Open(Path.Combine(ArtifactDirectory(args), id.Substring(4)), null).Status());
        });

        private ComponentQualificationRun ComponentQualificationRunFor(string id)
        {
            PlcComponentQualificationGate.RequireEnabled(_componentQualificationEnabled);
            if (!_componentQualificationRuns.TryGetValue(id, out var run)) throw new BridgeException("ComponentFreshSessionRequired", "Prepared component handles expired or are unknown. Read durable change_status and recover from the first checkpoint; never replay dispatch.");
            if (_componentWindowManifest == null || _componentWindowDispatchStep == null || !_sharedMutationDispatched)
                throw new BridgeException("MutationScopeIncomplete", "The exact durable parent and current child intent must be bound before component advancement.");
            RequireComponentScratch(run.Project);
            return run;
        }
        private void RequireComponentScratch(Project project)
        {
            PlcComponentQualificationGate.RequireOwned(_componentQualificationEnabled, _ownsPortal,
                project != null && _scratchProject != null && project.Equals(_scratchProject), _portal?.Projects.Count ?? 0);
            EnsureProjectOffline(project);
        }
        private static JObject ComponentQualificationView(JObject record)
        {
            string state = record.Value<string>("state"), step = state == "library_prepared" ? "library" : state == "graphical_prepared" ? "graphical" : null;
            return new JObject { ["schemaVersion"] = 1, ["changeId"] = "lib-" + record.Value<string>("operationId"), ["kind"] = PlcComponentQualificationGate.Kind,
                ["state"] = state, ["previewHash"] = step == null ? null : record[step]["prepared"]["previewHash"].DeepClone(),
                ["review"] = step == null ? null : record[step]["prepared"].DeepClone(), ["record"] = record,
                ["qualificationOnly"] = true, ["productionApplySupported"] = false, ["automaticRetryAllowed"] = false,
                ["nextAction"] = step != null ? "Review exact step then change_apply" : state == "outcome_unknown" || state.StartsWith("dispatching_", StringComparison.Ordinal) ? "Read-only reconcile; never replay" : "Read status; advance preparation only when applicable" };
        }
        private static JObject ComponentQualificationCall(Func<JObject> call)
        {
            try { return call(); } catch (PlcComponentException error) { throw new BridgeException(error.Code, error.Message); }
        }
    }
}
