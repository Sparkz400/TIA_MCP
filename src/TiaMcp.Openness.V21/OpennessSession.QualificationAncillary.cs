using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.VersionControl;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // A global ancillary observation, deliberately independent of PLC-unit traversal.
        // The typed qualification's existing snapshot remains authoritative for unit blocks/types/tags.
        private JObject QualificationVciCapture(Project project)
        {
            var service = project.GetService<VersionControlInterface>() ?? throw new BridgeException("ComponentScopeIncomplete", "VCI service is absent.");
            var groups = new JArray(); var workspaces = new JArray(); var budget = new ScanBudget(10000);
            void Visit(WorkspaceGroup group, int depth)
            {
                if (depth > 32 || !budget.Take()) throw new BridgeException("ComponentScopeIncomplete", "VCI group traversal exceeds its bound.");
                string groupName = (group as WorkspaceSystemGroup)?.Name ?? (group as WorkspaceUserGroup)?.Name
                    ?? throw new BridgeException("ComponentScopeIncomplete", "VCI group has no observed named system/user profile.");
                groups.Add(new JObject { ["identity"] = QualificationNativeIdentity(group), ["name"] = groupName });
                foreach (var workspace in group.Workspaces.OrderBy(w => w.Name, StringComparer.Ordinal))
                {
                    if (!budget.Take() || workspaces.Count >= 64) throw new BridgeException("ComponentScopeIncomplete", "VCI workspace traversal exceeds its bound.");
                    var mappings = new JArray();
                    foreach (var mapping in workspace.MappedObjects)
                    {
                        if (!budget.Take() || mappings.Count >= 1000) throw new BridgeException("ComponentScopeIncomplete", "VCI mapping traversal exceeds its bound.");
                        mappings.Add(new JObject { ["identity"] = QualificationNativeIdentity(mapping.EngineeringObject), ["format"] = mapping.FileFormat,
                            ["directory"] = mapping.DirectoryPath.FullName, ["fileName"] = mapping.FileNameWithoutExtension, ["compareState"] = mapping.GetStatus().CompareState.ToString() });
                    }
                    workspaces.Add(new JObject { ["identity"] = QualificationNativeIdentity(workspace), ["name"] = workspace.Name,
                        ["root"] = workspace.RootPath.FullName, ["culture"] = workspace.WorkspaceLanguage.Name,
                        ["mappings"] = new JArray(mappings.OrderBy(m => SharedMutationHash(m), StringComparer.Ordinal)) });
                }
                foreach (var child in group.Groups.OrderBy(g => g.Name, StringComparer.Ordinal)) Visit(child, depth + 1);
            }
            Visit(service.WorkspaceGroup, 0);
            var settings = _portal.SettingsFolders.Find("VCI") ?? throw new BridgeException("ComponentScopeIncomplete", "VCI settings folder is absent.");
            var values = new JObject();
            foreach (var setting in settings.Settings.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                if (values.Count >= 64 || values[setting.Name] != null) throw new BridgeException("ComponentScopeIncomplete", "VCI settings are ambiguous or exceed the bound.");
                var value = setting.Value;
                if (value != null && !(value is string || value is bool || value.GetType().IsPrimitive || value.GetType().IsEnum || value is decimal))
                    throw new BridgeException("ComponentScopeIncomplete", "VCI setting has an unsupported native value type.");
                values[setting.Name] = new JObject { ["nativeType"] = value?.GetType().FullName, ["value"] = ChangeScalar(value) };
            }
            if (values["ProjectLanguageImportOptions"] == null) throw new BridgeException("ComponentScopeIncomplete", "VCI language setting is absent.");
            return new JObject { ["scopeComplete"] = true, ["settings"] = values, ["groups"] = groups, ["workspaces"] = workspaces,
                ["identityCoverage"] = "Native ancestor type and exposed Name/Number chain, including software-unit ancestors; no unit payload traversal or workspace writes." };
        }

        private static JArray QualificationNativeIdentity(IEngineeringObject value)
        {
            if (value == null) throw new BridgeException("ComponentScopeIncomplete", "VCI mapping has no native engineering identity.");
            var identity = new JArray(); int depth = 0; var visited = new List<IEngineeringObject>();
            // The Portal root (or an object reporting itself/an ancestor as Parent) ends
            // the chain; otherwise VCI groups never reach null on some V21 installs.
            for (var current = value; current != null && !visited.Any(seen => ReferenceEquals(seen, current) || seen.Equals(current)); current = current is TiaPortal ? null : current.Parent)
            {
                if (++depth > 64) throw new BridgeException("ComponentScopeIncomplete", "VCI native identity ancestry exceeds its bound.");
                visited.Add(current);
                var row = new JObject { ["type"] = current.GetType().FullName };
                foreach (var info in current.GetAttributeInfos().Where(i => i.Name == "Name" || i.Name == "Number").OrderBy(i => i.Name, StringComparer.Ordinal))
                {
                    if ((info.AccessMode & EngineeringAttributeAccessMode.Read) == 0) throw new BridgeException("ComponentScopeIncomplete", "VCI native identity attribute is unreadable.");
                    row[info.Name] = Convert.ToString(current.GetAttribute(info.Name), CultureInfo.InvariantCulture);
                }
                identity.AddFirst(row);
            }
            return identity;
        }

        private PlcSelection QualificationAncillaryTarget(JObject args)
        {
            PlcComponentQualificationGate.RequireEnabled(_componentQualificationEnabled); EnsureAttached();
            if (!_ownsPortal || _portal.Projects.Count != 1 || args.Value<int?>("processId") != _processId)
                throw new BridgeException("OwnedScratchRequired", "One exact owned native process/project is required for ancillary capture.");
            string projectPath = ChangeText(args, "projectPath", 4096); ChangeText(args, "plcPath", 4096);
            var plc = SelectPlc(args); EnsureProjectOffline(plc.Project);
            if (!ProjectPathIsAbsolute(projectPath) || plc.Project.Path.FullName != projectPath || plc.Project.IsModified ||
                ChangeFileHash(projectPath) != ChangeText(args, "expectedProjectFileSha256", 64))
                throw new BridgeException("ComponentReadbackMismatch", "Ancillary capture requires the exact saved admitted project bytes.");
            return plc;
        }

        private JObject QualificationAncillaryScope(JObject args)
        {
            var plc = QualificationAncillaryTarget(args);
            string output = SharedMutationPlannedDestination("ancillaryCapture", () => Path.Combine(SharedMutationArtifactDirectory(args), "q", "a" + Guid.NewGuid().ToString("N").Substring(0, 12)));
            if (Directory.Exists(output) || File.Exists(output)) throw new BridgeException("ComponentReplayRefused", "Ancillary output must be an absent exact destination.");
            var resources = new JArray(ComponentResource("project", Path.GetDirectoryName(plc.Project.Path.FullName), "readSavedProjectAndCompanions"), ComponentResource("workspace", output, "newReadOnlyNativeExports"));
            var vci = QualificationVciCapture(plc.Project);
            foreach (var workspace in (JArray)vci["workspaces"])
            {
                foreach (string path in new[] { workspace.Value<string>("root") }.Concat(((JArray)workspace["mappings"]).Select(m => m.Value<string>("directory"))))
                {
                    if (!ProjectPathIsAbsolute(path)) throw new BridgeException("MutationScopeIncomplete", "VCI mapped directories must resolve to absolute resources.");
                    if (!resources.Any(r => string.Equals(r.Value<string>("path"), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) resources.Add(ComponentResource("workspace", path, "readVciMappingStatus"));
                }
            }
            if (resources.Count > 64) throw new BridgeException("MutationScopeIncomplete", "VCI resources exceed bounded durable admission.");
            var scope = new JObject { ["operation"] = "component_window_ancillary_read", ["requestSha256"] = SharedMutationHash(args), ["sessionEpoch"] = _libraryGeneration,
                ["adapterSha256"] = ChangeFileHash(Assembly.GetExecutingAssembly().Location), ["processId"] = _processId,
                ["resources"] = new JArray(resources.OrderBy(r => r.Value<string>("kind") + ":" + r.Value<string>("path"), StringComparer.Ordinal)),
                ["observationSha256"] = SharedMutationHash(new JObject { ["projectPath"] = plc.Project.Path.FullName, ["projectFileSha256"] = ChangeFileHash(plc.Project.Path.FullName), ["plcPath"] = plc.Path, ["vci"] = vci }) };
            scope["scopeSha256"] = SharedMutationHash(scope); return scope;
        }

        private JObject QualificationAncillaryRead(JObject args)
        {
            var plc = QualificationAncillaryTarget(args);
            string directory = SharedMutationPlannedDestination("ancillaryCapture", () => { throw new BridgeException("MutationAdmissionRequired", "Ancillary output requires complete shared admission."); });
            Directory.CreateDirectory(directory);
            var metadata = ComponentWindowPeerMetadata(plc.Project, plc, directory, out var tables);
            if (plc.Project.IsModified || ChangeFileHash(plc.Project.Path.FullName) != args.Value<string>("expectedProjectFileSha256"))
                throw new BridgeException("ComponentReadbackMismatch", "Ancillary capture altered the saved project state.");
            using (var process = Process.GetProcessById(_processId.Value))
            {
                var result = new JObject { ["schemaVersion"] = 1, ["profile"] = "qualification-global-ancillary-v1", ["projectPath"] = plc.Project.Path.FullName, ["plcPath"] = plc.Path,
                    ["processId"] = _processId, ["nativeBirthUtc"] = process.StartTime.ToUniversalTime().ToString("O"), ["sessionEpoch"] = _libraryGeneration, ["projectFileSha256"] = ChangeFileHash(plc.Project.Path.FullName),
                    ["readOnly"] = true, ["saveInvoked"] = false, ["projectModified"] = false, ["scopeCompleteWithinProfile"] = true, ["peerMetadata"] = metadata, ["watchForceTables"] = tables,
                    ["coverage"] = "Root PLC watch/force XML and groups/access, exposed configured hardware, project languages, sole software owner, global VCI settings/workspaces/mapping identity ancestry. Existing software units are permitted; their blocks/types/tags require the independent typed Host snapshot. Unexposed vendor payloads, project-library contents and runtime are outside this ancillary profile." };
                string path = Path.Combine(directory, "ancillary.json"); File.WriteAllText(path, result.ToString()); result["receipt"] = ChangeArtifact(path); return result;
            }
        }
    }
}
