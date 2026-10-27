using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Runs before any native lookup so a malformed combined recipe never reaches TIA.
        private static void ValidatePlcHmiRecipe(JObject payload)
        {
            try { PlcHmiIntegrationPlanner.ValidatePayload(payload); }
            catch (PlcIntegrationPlanException error) { throw new BridgeException(error.Code, error.Message, error.Details); }
        }

        private void PreparePlcHmiIntegrationPlan(LibraryChange change)
        {
            JObject plan;
            if (change.Payload.Value<string>("action") == PlcHmiIntegrationPlanner.InstallAction)
            {
                var combined = change.Payload;
                change.Payload = PlcHmiIntegrationPlanner.PlcPayload(combined);
                try { PreparePlcLibraryIntegrationPlan(change); } finally { change.Payload = combined; }
                var plcPlan = (JObject)change.Preview["integrationPlan"];
                try { plan = PlcHmiIntegrationPlanner.BuildInstall(combined, plcPlan, PlcHmiInstallObservation(change, plcPlan)); }
                catch (PlcIntegrationPlanException error) { throw new BridgeException(error.Code, error.Message, error.Details); }
            }
            else
            {
                try { plan = PlcHmiIntegrationPlanner.BuildUpdate(change.Payload, PlcHmiUpdateObservation(change)); }
                catch (PlcIntegrationPlanException error) { throw new BridgeException(error.Code, error.Message, error.Details); }
                ((JArray)change.Preview["effects"]).Add(new JObject { ["action"] = "planPlcHmiUpdate", ["nativeMutation"] = false, ["proposedSteps"] = plan["steps"].DeepClone() });
            }
            change.Preview["integrationPlan"] = plan;
            change.Preview["applySupported"] = false;
            change.Preview["applyError"] = PlcLibraryIntegrationPlanner.ApplyError;
            ((JArray)change.Preview["warnings"]).Add("Combined PLC/HMI planning only. HMI steps stay behind the closed Unified preservation gate; every step needs fresh preparation.");
        }

        private JObject PlcHmiInstallObservation(LibraryChange change, JObject plcPlan)
        {
            var hmi = (JObject)change.Payload["hmi"]; var project = change.Project;
            var target = HmiResolveNativeTarget(project, new JObject { ["hmiPath"] = hmi["hmiPath"].DeepClone(), ["family"] = "unified" });
            RequireHmiModule(target.Family);
            var software = (Siemens.Engineering.HmiUnified.HmiSoftware)target.Software;
            var ownership = new JObject { ["connection"] = hmi["connection"].DeepClone() };
            var plc = HmiProveIntegratedOwner(project, target, hmi.Value<string>("connection"), ownership);
            var observed = new JObject { ["hmiPath"] = target.Path, ["family"] = target.Family, ["profile"] = target.Profile,
                ["connection"] = new JObject { ["name"] = hmi["connection"].DeepClone(), ["plcPath"] = plc.Path, ["evidence"] = ownership } };
            string table = target.Path + "/tagTables/" + Segment(hmi.Value<string>("tagTable"));
            observed["tagTable"] = new JObject { ["path"] = table, ["exists"] = software.TagTables.Any(t => target.Path + "/tagTables/" + Segment(t.Name) == table) };
            observed["existingTagNames"] = new JArray(hmi["tags"].Select(t => t.Value<string>("name")).Where(n => software.Tags.Find(n) != null));
            var plcTags = new JObject(); var budget = new ScanBudget(50000);
            var wanted = new HashSet<string>(hmi["tags"].Where(t => t["source"].Value<string>("kind") == "plcTag").Select(t => t["source"].Value<string>("tagPath")), StringComparer.Ordinal);
            foreach (var entry in TagTables(plc.Software.TagTableGroup, plc.Path + "/tags", budget))
                foreach (var tag in entry.Item.Tags)
                {
                    string path = entry.Path + "/tags/" + Segment(tag.Name);
                    if (!budget.Take() || !wanted.Contains(path)) continue;
                    var symbol = HmiInspectionRules.ScalarSymbolPath(tag.Name);
                    // Only an unquoted one-component root symbol has a documented Unified spelling.
                    if (symbol != null && symbol.Length == 1 && symbol[0] == tag.Name) plcTags[path] = new JObject { ["name"] = tag.Name, ["dataType"] = tag.DataTypeName };
                }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "PLC tag lookup is incomplete; mapped tags cannot be proven unique.");
            observed["plcTags"] = plcTags;
            var versions = plcPlan["versions"].OfType<JObject>().ToList(); var udts = new Dictionary<string, XDocument>(StringComparer.Ordinal); XDocument fb = null;
            string rootVersion = change.Payload["selection"][0].Value<string>("versionGuid");
            foreach (var version in versions)
            {
                var xml = EngineeringSources.ReadXml(PlcComponentContentContract.Artifact((JObject)version["artifact"], version.Value<string>("contentSha256")));
                if (string.Equals(version.Value<string>("versionGuid"), rootVersion, StringComparison.OrdinalIgnoreCase)) fb = xml;
                else if (xml.Root?.Descendants().Any(e => e.Name.LocalName == "SW.Types.PlcStruct") == true) udts[version.Value<string>("typeName")] = xml;
            }
            observed["instanceMembers"] = PlcHmiIntegrationPlanner.Members(fb ?? throw new BridgeException("DependencyUnresolved", "The pinned root FB export is missing."), udts);
            var screens = new JArray();
            foreach (JObject item in hmi["screenItems"] as JArray ?? new JArray())
            {
                var screen = software.Screens.FirstOrDefault(s => target.Path + "/screens/" + Segment(s.Name) == item.Value<string>("screenPath"));
                screens.Add(screen == null ? new JObject { ["screenPath"] = item["screenPath"].DeepClone(), ["exists"] = false } : new JObject {
                    ["screenPath"] = item["screenPath"].DeepClone(), ["exists"] = true, ["width"] = screen.Width, ["height"] = screen.Height,
                    ["itemNames"] = new JArray(screen.ScreenItems.Select(i => i.Name)) });
            }
            observed["screens"] = screens;
            return observed;
        }

        private JObject PlcHmiUpdateObservation(LibraryChange change)
        {
            if (change.Versions.Count != 1 || change.Masters.Count != 0 || !(change.Versions[0] is PlcTypeLibraryTypeVersion))
                throw new BridgeException("UnsupportedIntegrationType", "Select one exact PLC data-type (UDT) library version.");
            var version = change.Versions[0]; string typeName = version.TypeObject.Name;
            if (version.State != LibraryTypeVersionState.Committed || version.TypeObject.DoNotUse) throw new BridgeException("DependencyConflict", "The selected version must be committed and usable.");
            var scope = LibraryPlcScope(change, out var plc);
            if (scope.Unit != null) throw new BridgeException("UnsupportedIntegrationScope", "Combined update planning requires the standard PLC scope.");
            var installed = change.Project.ProjectLibrary.FindType(version.TypeObject.Guid) ?? throw new BridgeException("NoMatchingUses", "The selected type is not installed in this project library.");
            var uses = installed.Versions.SelectMany(v => v.FindInstances(plc.Software).Select(u => Tuple.Create(v, u.LibraryTypeInstance as PlcType))).ToList();
            if (uses.Count != 1 || uses[0].Item2 == null || uses[0].Item2.Name != typeName)
                throw new BridgeException("NoMatchingUses", "The selected PLC must contain exactly one native use of the selected UDT type.");
            string beforeFile = Path.Combine(change.Directory, "plchmi-before.xml"), afterFile = Path.Combine(change.Directory, "plchmi-after.xml");
            uses[0].Item2.Export(new FileInfo(beforeFile), ExportOptions.WithDefaults);
            version.Export(new FileInfo(afterFile), ExportOptions.WithDefaults, DocumentInfoOptions.None);
            var observed = new JObject { ["complete"] = true,
                ["type"] = new JObject { ["typeGuid"] = version.TypeObject.Guid.ToString("D"), ["name"] = typeName, ["beforeVersionGuid"] = uses[0].Item1.Guid.ToString("D"),
                    ["beforeVersion"] = uses[0].Item1.VersionNumber.ToString(), ["afterVersionGuid"] = version.Guid.ToString("D"), ["afterVersion"] = version.VersionNumber.ToString(),
                    ["beforeArtifact"] = ChangeArtifact(beforeFile), ["afterArtifact"] = ChangeArtifact(afterFile) },
                ["before"] = PlcHmiIntegrationPlanner.Members(EngineeringSources.ReadXml(File.ReadAllText(beforeFile))), ["after"] = PlcHmiIntegrationPlanner.Members(EngineeringSources.ReadXml(File.ReadAllText(afterFile))) };
            var budget = new ScanBudget(50000);
            var blocks = ScopeBlocks(scope, budget).Where(b => b.Item is DataBlock).GroupBy(b => b.Item.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var udtTags = new HashSet<string>(TagTables(plc.Software.TagTableGroup, plc.Path + "/tags", budget).SelectMany(t => t.Item.Tags)
                .Where(t => t.DataTypeName.Trim('"') == typeName).Select(t => t.Name), StringComparer.Ordinal);
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "PLC block/tag inventory is incomplete; HMI impact cannot be established.");
            var dbMembers = new Dictionary<string, JArray>(StringComparer.Ordinal);
            var bindings = new JArray(); var unresolved = new JArray(); var targets = new JArray();
            var capture = new HmiCapture(50000);
            foreach (var target in HmiDiscover(change.Project, capture))
            {
                targets.Add(new JObject { ["hmiPath"] = target.Path, ["family"] = target.Family, ["profile"] = target.Profile });
                if (target.Family != "unified" || target.Software == null)
                {
                    unresolved.Add(new JObject { ["hmiPath"] = target.Path, ["reason"] = "Only WinCC Unified symbolic bindings are analyzed; this HMI's bindings to the PLC are unknown." });
                    continue;
                }
                var software = (Siemens.Engineering.HmiUnified.HmiSoftware)target.Software;
                var owners = new Dictionary<string, PlcSelection>(StringComparer.Ordinal); Dictionary<string, JArray> consumers = null;
                foreach (var tag in software.Tags)
                {
                    if (!capture.Budget.Take()) break;
                    if (string.IsNullOrEmpty(tag.PlcTag)) continue;
                    string tagPath = target.Path + "/tagTables/" + Segment(tag.TagTableName) + "/tags/" + Segment(tag.Name);
                    JObject Gap(string reason) => new JObject { ["hmiPath"] = target.Path, ["tagPath"] = tagPath, ["plcTag"] = tag.PlcTag, ["reason"] = reason };
                    if (!owners.TryGetValue(tag.Connection ?? "", out var owner))
                    {
                        try { owner = HmiProveIntegratedOwner(change.Project, target, tag.Connection, new JObject()); } catch (BridgeException) { owner = null; }
                        owners[tag.Connection ?? ""] = owner;
                    }
                    if (owner == null) { unresolved.Add(Gap("The tag's connection has no proven native PLC owner.")); continue; }
                    if (owner.Path != plc.Path) continue;
                    var components = HmiInspectionRules.ScalarSymbolPath(tag.PlcTag);
                    if (tag.AccessMode.ToString() != "SymbolicAccess" || components == null) { unresolved.Add(Gap("Only symbolic scalar PlcTag spellings are analyzed.")); continue; }
                    if (components.Length == 1) { if (udtTags.Contains(components[0])) unresolved.Add(Gap("PLC tag-table tags of the selected UDT type are not analyzed.")); continue; }
                    if (!blocks.TryGetValue(components[0], out var db) || db.Count != 1) { unresolved.Add(Gap("The bound DB does not resolve uniquely in the standard PLC scope.")); continue; }
                    if (!dbMembers.TryGetValue(db[0].Path, out var members))
                    {
                        string file = Path.Combine(change.Directory, "plchmi-db-" + dbMembers.Count + ".xml");
                        ((DataBlock)db[0].Item).Export(new FileInfo(file), ExportOptions.WithDefaults);
                        dbMembers[db[0].Path] = members = PlcHmiIntegrationPlanner.Members(EngineeringSources.ReadXml(File.ReadAllText(file)));
                    }
                    var rest = components.Skip(1).ToArray();
                    var split = PlcHmiIntegrationPlanner.SplitAtType(members, rest, typeName);
                    if (split == null)
                    {
                        // A missing path may pass through another opaque UDT that nests the selected type.
                        if (!members.OfType<JObject>().Any(m => m["path"].Select(c => (string)c).SequenceEqual(rest, StringComparer.Ordinal))) unresolved.Add(Gap("The bound member path does not resolve in the DB's flattened interface."));
                        continue;
                    }
                    consumers = consumers ?? PlcHmiScreenConsumers(change.Project, target);
                    bindings.Add(new JObject { ["hmiPath"] = target.Path, ["profile"] = target.Profile, ["tagPath"] = tagPath, ["tagName"] = tag.Name, ["connection"] = tag.Connection,
                        ["plcPath"] = plc.Path, ["plcTag"] = tag.PlcTag, ["dataType"] = tag.DataType, ["blockPath"] = db[0].Path, ["typeMemberPath"] = new JArray(split[0]), ["remainder"] = new JArray(split[1]),
                        ["consumers"] = consumers.TryGetValue(tag.Name, out var used) ? used.DeepClone() : new JArray(),
                        ["consumerCoverage"] = "Direct screen-item tag dynamizations on root screens. Scripts, indirect expressions, alarms and faceplate ports are not parsed." });
                }
            }
            if (capture.Budget.Truncated) observed["complete"] = false;
            observed["bindings"] = bindings; observed["unresolved"] = unresolved; observed["hmiTargets"] = targets;
            return observed;
        }

        private Dictionary<string, JArray> PlcHmiScreenConsumers(Project project, HmiDiscoveredTarget target)
        {
            var result = new Dictionary<string, JArray>(StringComparer.Ordinal);
            foreach (var screen in ((Siemens.Engineering.HmiUnified.HmiSoftware)target.Software).Screens)
            {
                string screenPath = target.Path + "/screens/" + Segment(screen.Name);
                var capture = new HmiCapture(50000);
                HmiCaptureUnified(project, target, "items", new JObject { ["screenPath"] = screenPath }, capture, includeScreenOwner: true);
                if (capture.Budget.Truncated) throw new BridgeException("TraversalLimit", "Screen item traversal is incomplete; HMI consumers cannot be listed.");
                foreach (var entry in capture.Entries)
                    foreach (JObject dynamic in entry.Row["dynamizations"] as JArray ?? new JArray())
                    {
                        string tag = HmiStringProperty(dynamic["properties"] as JObject ?? new JObject(), "Tag");
                        if (string.IsNullOrEmpty(tag)) continue;
                        if (!result.TryGetValue(tag, out var rows)) result[tag] = rows = new JArray();
                        rows.Add(new JObject { ["screenPath"] = screenPath, ["itemPath"] = entry.Identity["objectPath"].DeepClone(), ["property"] = dynamic["property"]?.DeepClone(), ["resolution"] = dynamic["resolution"]?.DeepClone() });
                    }
            }
            return result;
        }
    }
}
