using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using TiaMcp.Indexing;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private readonly ProjectIndexStore _projectIndexes = new ProjectIndexStore();
        private readonly Dictionary<string, JObject> _indexRequests = new Dictionary<string, JObject>();

        // A new operation is invalidating until explicitly classified here. Invalidate
        // before execution, including failed/partially committed mutations.
        private void ObserveIndexOperation(string operation)
        {
            if (new[] { "attach", "start", "detach", "project_open", "project_retrieve", "scratch_create", "change_restore" }.Contains(operation)) InvalidateHmiSession();
            switch (operation)
            {
                case "hmi_inspect": case "hmi_references": case "hmi_capabilities": return;
                case "project_index": case "project_model": case "bridge_info": case "discover": case "session_status": case "addin_owner_context":
                case "project_inventory": case "device_inventory_capture": case "block_read": case "xref_query": case "catalog_query":
                case "software_inspect": case "type_read": case "tag_table_read": case "block_interface":
                case "hardware_inspect": case "technology_read": case "safety_inspect": case "call_graph":
                case "symbol_trace": case "change_impact": case "engineering_read": case "fixture_sources":
                case "plc_document_inventory": case "plc_loadable_generate":
                case "change_status": case "object_resolve": case "target_capabilities":
                case "library_list": case "library_query": case "library_compare": case "library_export": case "library_capabilities": return;
                default: _projectIndexes.Invalidate(operation); _indexRequests.Clear(); InvalidateProjectModels(operation); return;
            }
        }

        private JObject ProjectIndex(JObject args)
        {
            try
            {
                string action = args.Value<string>("action") ?? "capture";
                string id = args.Value<string>("indexId");
                if (action == "release")
                {
                    bool released = _projectIndexes.Release(id); _indexRequests.Remove(id ?? "");
                    var result = Envelope(); result["released"] = released; return result;
                }
                if (action == "capture")
                {
                    int lifetime = IndexInteger(args, "lifetimeSeconds", 300, 30, 900);
                    var capture = CaptureIndex(args);
                    var snapshot = _projectIndexes.Capture(capture.Rows, capture.ProjectPath, capture.Revision,
                        capture.Complete, capture.Warnings, lifetime);
                    // The store retains at most two snapshots; drop request metadata with them.
                    foreach (var old in _indexRequests.Keys.ToArray())
                        try { _projectIndexes.Get(old); } catch (ProjectIndexStore.IndexException) { _indexRequests.Remove(old); }
                    _indexRequests[snapshot.Id] = new JObject { ["projectName"] = capture.ProjectName,
                        ["plcPath"] = args["plcPath"], ["maxVisited"] = capture.MaxVisited };
                    var result = IndexHeader(snapshot); result["visitedItems"] = capture.Visited;
                    result["maxVisited"] = capture.MaxVisited; return result;
                }
                if (action != "query" && action != "verify" && action != "status")
                    throw new BridgeException("InvalidRequest", "action must be capture, query, verify, status or release.");
                var existing = _projectIndexes.Get(id);
                bool verify = action == "verify" || args.Value<bool?>("verifyCurrent") == true;
                if (verify)
                {
                    var current = CaptureIndex(_indexRequests[existing.Id]);
                    // A native recapture may outlive the snapshot's remaining TTL.
                    existing = _projectIndexes.Get(existing.Id);
                    bool same = current.ProjectPath == existing.ProjectPath && current.Revision == existing.Revision;
                    if (!same) existing.InvalidatedBy = "observed metadata change";
                    if (action == "verify")
                    {
                        var checkedResult = IndexHeader(existing);
                        checkedResult["matchesCurrentMetadata"] = same;
                        checkedResult["currentMetadataRevision"] = current.Revision;
                        checkedResult["freshness"] = "metadataVerifiedAtResponse";
                        return checkedResult;
                    }
                    if (!same) throw new BridgeException("IndexInvalidated", "Indexed metadata changed; capture a new snapshot before paging.");
                }
                if (action == "status")
                {
                    var status = IndexHeader(existing);
                    if (verify) status["freshness"] = "metadataVerifiedAtResponse";
                    return status;
                }
                var page = _projectIndexes.Query(id, args.Value<string>("text"), args.Value<string>("kind"),
                    args.Value<string>("plcPath"), args.Value<string>("scopePath"), IndexInteger(args, "maxItems", 100, 1, 200), args.Value<string>("cursor"));
                var response = IndexHeader(page.Snapshot);
                response["rows"] = new JArray(page.Rows.Select(row => JObject.Parse(row.Json)));
                response["offset"] = page.Offset; response["totalMatches"] = page.Total;
                response["nextCursor"] = page.NextCursor; response["rowPayloadBytes"] = page.PayloadBytes;
                response["freshness"] = verify ? "metadataVerifiedAtResponse" : "snapshotOnly";
                return response;
            }
            catch (ProjectIndexStore.IndexException error) { throw new BridgeException(error.Code, error.Message); }
        }

        private JObject IndexHeader(ProjectIndexStore.Snapshot snapshot)
        {
            var result = Envelope();
            result["indexId"] = snapshot.Id; result["projectPath"] = snapshot.ProjectPath;
            result["metadataRevision"] = snapshot.Revision; result["rowCount"] = snapshot.Rows.Count;
            result["createdAtUtc"] = snapshot.CreatedAtUtc.ToString("O"); result["expiresAtUtc"] = snapshot.ExpiresAtUtc.ToString("O");
            result["retainedPayloadBytes"] = snapshot.PayloadBytes; result["freshness"] = "snapshotOnly";
            result["valid"] = snapshot.InvalidatedBy == null;
            result["warnings"] = new JArray(snapshot.Warnings);
            result["completeness"] = new JObject { ["complete"] = false, ["scopeComplete"] = snapshot.ScopeComplete,
                ["scope"] = "Exposed device/PLC/unit/block/type/tag-table/tag/constant/technology identities and selected metadata.",
                ["gaps"] = new JArray("Not a PLC-logic or whole-project revision", "No block bodies, type members, TO parameters, detailed hardware attributes, libraries or online values",
                    "External edits require explicit verify; verification itself is point-in-time", "Traversal, memory and lifetime are bounded") };
            return result;
        }

        private IndexCapture CaptureIndex(JObject args)
        {
            var project = SelectProject(args);
            int maximum = IndexInteger(args, "maxVisited", 50000, 100, 50000);
            var budget = new ScanBudget(maximum);
            var rows = new List<ProjectIndexStore.Row>(); var warnings = new JArray();
            long bytes = 0;
            Action<JObject, string, string, string> add = (data, kind, plcPath, scopePath) =>
            {
                data["objectKind"] = kind; data["plcPath"] = plcPath; data["scopePath"] = scopePath;
                string json = data.ToString(Formatting.None), path = data.Value<string>("path"), name = data.Value<string>("name") ?? "";
                int size = Encoding.UTF8.GetByteCount(json);
                bytes += size + Encoding.UTF8.GetByteCount(path + name + kind + plcPath + scopePath);
                if (size > 65536 || rows.Count >= ProjectIndexStore.MaximumRows || bytes > ProjectIndexStore.MaximumBytes)
                    throw new BridgeException("IndexCapacity", "Metadata exceeds index capacity; capture a selected plcPath or reduce maxVisited.");
                rows.Add(new ProjectIndexStore.Row(path, kind, name, plcPath, scopePath, json));
            };
            string selectedPlc = args.Value<string>("plcPath"); bool plcFound = string.IsNullOrEmpty(selectedPlc);
            using (_portal.ExclusiveAccess("Capture bounded project metadata index"))
            {
                foreach (var entry in DeviceItems(project, budget))
                {
                    if (string.IsNullOrEmpty(selectedPlc)) add(new JObject { ["path"] = entry.Path, ["name"] = entry.Item.Name,
                        ["positionNumber"] = entry.Item.PositionNumber, ["classification"] = entry.Item.Classification.ToString() }, "hardware", null, null);
                    PlcSoftware software = entry.Item.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                    if (software == null) continue;
                    string plcPath = entry.Path + "/plc/" + Segment(software.Name);
                    if (!string.IsNullOrEmpty(selectedPlc) && plcPath != selectedPlc) continue;
                    plcFound = true;
                    add(new JObject { ["path"] = plcPath, ["name"] = software.Name }, "plc", plcPath, plcPath);
                    var plc = new PlcSelection { Project = project, Software = software, Path = plcPath };
                    foreach (var scope in SoftwareScopes(plc, budget, warnings))
                    {
                        if (scope.Path != plcPath) add(new JObject { ["path"] = scope.Path, ["name"] = scope.Name }, scope.Kind, plcPath, scope.Path);
                        foreach (var block in ScopeBlocks(scope, budget))
                        {
                            var data = BlockInfo(block, plcPath); data["namespace"] = block.Item.Namespace;
                            data["modified"] = block.Item.ModifiedDate.ToString("O"); add(data, "block", plcPath, scope.Path);
                        }
                        foreach (var type in ScopeTypes(scope, budget)) add(new JObject { ["path"] = type.Path, ["name"] = type.Item.Name,
                            ["namespace"] = type.Item.Namespace, ["modified"] = type.Item.ModifiedDate.ToString("O"),
                            ["isKnowHowProtected"] = type.Item.IsKnowHowProtected, ["isConsistent"] = type.Item.IsConsistent }, "type", plcPath, scope.Path);
                        foreach (var table in TagTables(scope.Tags, scope.Path + "/tags", budget))
                        {
                            add(new JObject { ["path"] = table.Path, ["name"] = table.Item.Name, ["modified"] = table.Item.ModifiedTimeStamp.ToString("O") }, "tagTable", plcPath, scope.Path);
                            foreach (var tag in table.Item.Tags)
                            {
                                if (!budget.Take()) break;
                                add(new JObject { ["path"] = table.Path + "/tags/" + Segment(tag.Name), ["name"] = tag.Name,
                                    ["dataType"] = tag.DataTypeName, ["logicalAddress"] = tag.LogicalAddress, ["isSafety"] = tag.IsSafety }, "tag", plcPath, scope.Path);
                            }
                            foreach (var constant in table.Item.UserConstants)
                            {
                                if (!budget.Take()) break;
                                add(new JObject { ["path"] = table.Path + "/constants/" + Segment(constant.Name), ["name"] = constant.Name,
                                    ["dataType"] = constant.DataTypeName, ["value"] = InspectionValue(constant.Value) }, "constant", plcPath, scope.Path);
                            }
                            foreach (var constant in table.Item.SystemConstants)
                            {
                                if (!budget.Take()) break;
                                add(new JObject { ["path"] = table.Path + "/systemConstants/" + Segment(constant.Name), ["name"] = constant.Name,
                                    ["dataType"] = constant.DataTypeName, ["value"] = InspectionValue(constant.Value) }, "systemConstant", plcPath, scope.Path);
                            }
                        }
                    }
                    foreach (var technology in TechnologyObjects(software.TechnologicalObjectGroup, plcPath + "/technology", budget))
                    {
                        var data = TechnologyInfo(technology); data["modified"] = technology.Item.ModifiedDate.ToString("O");
                        add(data, "technologyObject", plcPath, plcPath);
                    }
                }
                if (!plcFound) throw new BridgeException(budget.Truncated ? "TraversalIncomplete" : "ObjectNotFound", "Selected plcPath was not found within the capture budget.");
                bool complete = !budget.Truncated && warnings.Count == 0;
                if (budget.Truncated) warnings.Add("Traversal budget or depth reached; capture is partial.");
                string[] messages = warnings.Take(100).Select(w => w.ToString(Formatting.None)).Select(w => w.Length > 1000 ? w.Substring(0, 1000) : w).ToArray();
                string projectPath = project.Path.FullName;
                string revision = ProjectIndexStore.Hash(projectPath + "\n" + project.Name + "\n" + complete + "\n" +
                    string.Join("\n", rows.OrderBy(r => r.Path, StringComparer.Ordinal).Select(r => r.Json)) + "\n" + string.Join("\n", messages));
                return new IndexCapture { Rows = rows, ProjectPath = projectPath, ProjectName = project.Name,
                    Complete = complete, Warnings = messages, Revision = revision, MaxVisited = maximum, Visited = budget.Used };
            }
        }

        private static int IndexInteger(JObject args, string name, int fallback, int min, int max)
        {
            var value = args[name];
            if (value == null || value.Type == JTokenType.Null) return fallback;
            if (value.Type != JTokenType.Integer || !long.TryParse(value.ToString(), out long number) || number < min || number > max)
                throw new BridgeException("InvalidRequest", name + " must be an integer from " + min + " to " + max + ".");
            return (int)number;
        }
        private sealed class IndexCapture
        {
            internal List<ProjectIndexStore.Row> Rows; internal string ProjectPath, ProjectName, Revision;
            internal string[] Warnings; internal bool Complete; internal int Visited, MaxVisited;
        }
    }
}
