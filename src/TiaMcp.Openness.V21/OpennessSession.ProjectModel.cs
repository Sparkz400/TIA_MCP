using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using TiaMcp.Analysis;
using TiaMcp.Indexing;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private readonly ProjectModelStore _projectModels = new ProjectModelStore();
        private readonly Dictionary<string, JObject> _modelRequests = new Dictionary<string, JObject>(StringComparer.Ordinal);
        private string _modelSessionGeneration = Guid.NewGuid().ToString("N");
        private ProjectModelProviders _registeredProjectModelProviders;
        // Optional workstream hooks. Their implementations own HMI native APIs;
        // no second HMI adapter or instruction compiler belongs in this file.
        partial void RegisterProjectModelProviders(List<IProjectModelProvider> providers);
        partial void RegisterTypedProjectModelProviders(List<IProjectModelProvider> providers);
        partial void CaptureHmiProjectModel(Project project, string hmiPath, string parentId, ModelCapture capture);
        partial void ReadHmiProjectModelRelationships(JObject snapshot, JObject reference, JObject node, JObject args, ref JObject result);

        private ProjectModelProviders RegisteredProjectModelProviders
        {
            get
            {
                if (_registeredProjectModelProviders == null)
                {
                    var providers = new List<IProjectModelProvider>();
                    RegisterProjectModelProviders(providers);
                    RegisterTypedProjectModelProviders(providers);
                    _registeredProjectModelProviders = new ProjectModelProviders(providers);
                }
                return _registeredProjectModelProviders;
            }
        }

        private void InvalidateProjectModels(string operation)
        {
            _projectModels.Invalidate(operation);
            _modelRequests.Clear();
            if (operation == "attach" || operation == "start" || operation == "detach")
                _modelSessionGeneration = Guid.NewGuid().ToString("N");
        }

        // This dispatcher intentionally has no mutation operation. All objects in the
        // store are JSON values; Siemens proxies stay on this worker's STA thread.
        private JObject ProjectModel(JObject args)
        {
            try
            {
                string action = args.Value<string>("action") ?? "capture";
                string id = args.Value<string>("snapshotId") ?? (args["reference"] as JObject)?.Value<string>("snapshotId");
                if (args["reference"] is JObject suppliedReference && id != suppliedReference.Value<string>("snapshotId"))
                    throw new BridgeException("InvalidSelection", "snapshotId and exact reference belong to different captures; repair the selection.");
                if (action == "release")
                {
                    bool released = _projectModels.Release(id);
                    _modelRequests.Remove(id ?? "");
                    return new JObject { ["schemaVersion"] = "project-model.v1", ["snapshotId"] = id, ["released"] = released };
                }
                if (action == "capture")
                {
                    var document = CaptureProjectModel(args);
                    var snapshot = _projectModels.Capture(document, IndexInteger(args, "lifetimeSeconds", 300, 30, 900));
                    id = snapshot.Value<string>("snapshotId");
                    foreach (string old in _modelRequests.Keys.ToArray())
                        try { _projectModels.Header(old); } catch { _modelRequests.Remove(old); }
                    _modelRequests[id] = new JObject { ["projectName"] = document["project"]["name"],
                        ["plcPath"] = args["plcPath"], ["scopePath"] = args["scopePath"], ["maxVisited"] = document["limits"]["maxVisited"],
                        ["maxDepth"] = document["limits"]["maxDepth"], ["maxBytes"] = document["limits"]["maxBytes"] };
                    var result = _projectModels.Header(id);
                    result["roots"] = new JArray(((JArray)snapshot["nodes"]).OfType<JObject>().Where(n => n["parentId"] == null || n["parentId"].Type == JTokenType.Null));
                    return result;
                }
                if (action == "status") return _projectModels.Header(id);
                if (action == "verify")
                {
                    var exact = args["reference"] as JObject;
                    if (exact != null) _projectModels.ResolveReference(exact, _modelSessionGeneration);
                    var verified = VerifyProjectModel(id);
                    verified["referenceVerified"] = exact != null && verified.Value<bool>("matchesCurrentMetadata");
                    verified["valid"] = verified.Value<bool>("matchesCurrentMetadata");
                    if (exact != null) verified["reference"] = exact.DeepClone();
                    return verified;
                }
                if (action == "children")
                {
                    string parentId = args.Value<string>("parentId");
                    if (args["reference"] is JObject parentReference)
                        parentId = _projectModels.ResolveReference(parentReference, _modelSessionGeneration).Value<string>("nodeId");
                    return _projectModels.Children(id, parentId, IndexInteger(args, "maxItems", 100, 1, 200), args.Value<string>("cursor"));
                }
                if (action == "search") return _projectModels.Search(id, args.Value<string>("text"), args.Value<string>("kind"),
                    args.Value<string>("plcPath"), args.Value<string>("scopePath"), IndexInteger(args, "maxItems", 100, 1, 200), args.Value<string>("cursor"));
                if (action == "export")
                {
                    var snapshot = _projectModels.Get(id, _modelSessionGeneration);
                    string directory = ArtifactDirectory(args);
                    var file = new FileInfo(Path.Combine(directory, "project-model-" + Guid.NewGuid().ToString("N") + ".json"));
                    File.WriteAllText(file.FullName, snapshot.ToString(Formatting.None), new UTF8Encoding(false));
                    var artifact = FileArtifact(file); artifact["mediaType"] = "application/json";
                    return new JObject { ["schemaVersion"] = "project-model.v1", ["snapshotId"] = id, ["artifact"] = artifact,
                        ["coverage"] = snapshot["coverage"], ["freshness"] = "snapshotOnly", ["exportedAtUtc"] = DateTime.UtcNow.ToString("O") };
                }
                if (action == "details" || action == "relationships")
                {
                    var reference = args["reference"] as JObject;
                    if (reference == null) throw new BridgeException("InvalidRequest", "An exact snapshot-bound reference is required; names are not accepted.");
                    var node = _projectModels.ResolveReference(reference, _modelSessionGeneration);
                    VerifyProjectModel(id, true);
                    return action == "details" ? ProjectModelDetails(id, node, reference, args) : ProjectModelRelationships(id, node, reference, args);
                }
                throw new BridgeException("InvalidRequest", "action must be capture, children, search, details, relationships, verify, status, release or export.");
            }
            catch (ProjectModelStore.ModelException error) { throw new BridgeException(error.Code, error.Message); }
        }

        private JObject VerifyProjectModel(string id, bool requireMatch = false)
        {
            var snapshot = _projectModels.Get(id, _modelSessionGeneration);
            if (!_modelRequests.TryGetValue(id, out var request))
                throw new BridgeException("SnapshotInvalidated", "Snapshot context is no longer available; capture again and repair the selection.");
            var current = CaptureProjectModel(request);
            // Get again because a slow native read may have exceeded the snapshot TTL.
            _projectModels.Get(id, _modelSessionGeneration);
            bool same = snapshot["project"].Value<string>("projectPath") == current["project"].Value<string>("projectPath") &&
                snapshot["metadataRevision"].Value<string>("value") == current["metadataRevision"].Value<string>("value");
            var result = _projectModels.Header(id);
            result["matchesCurrentMetadata"] = same;
            result["currentMetadataRevision"] = current["metadataRevision"];
            result["verifiedAtUtc"] = DateTime.UtcNow.ToString("O");
            result["verificationUsage"] = current["usage"];
            result["contentVerified"] = false;
            result["freshness"] = same ? "metadataVerifiedAtResponse" : "stale";
            if (!same)
            {
                _projectModels.Invalidate("Observed exposed metadata change; exact selection must be repaired.");
                _modelRequests.Clear();
                if (requireMatch) throw new BridgeException("SnapshotInvalidated", "Exposed metadata changed; capture again and explicitly reselect. No same-named replacement was selected.");
            }
            return result;
        }

        private JObject CaptureProjectModel(JObject args)
        {
            var project = SelectProject(args);
            int maximum = IndexInteger(args, "maxVisited", 50000, 1, 50000);
            int maxDepth = IndexInteger(args, "maxDepth", 64, 1, 64), maxBytes = IndexInteger(args, "maxBytes", 32 * 1024 * 1024, 65536, 32 * 1024 * 1024);
            var capture = new ModelCapture(maximum, maxDepth, maxBytes, args.Value<string>("plcPath"), args.Value<string>("scopePath"));
            var timer = Stopwatch.StartNew();
            string started = DateTime.UtcNow.ToString("O"), root = "/projects/" + Segment(project.Name);
            using (_portal.ExclusiveAccess("Capture bounded read-only project explorer hierarchy"))
            {
                string rootId = capture.Add(root, null, "project", project.Name, null, null, new JObject { ["projectPath"] = project.Path.FullName });
                if (rootId != null)
                {
                    string devices = capture.Add(root + "/devices", rootId, "folder", "Devices", null, null, null, false);
                    if (devices != null) CaptureModelDevices(project.Devices, root + "/devices", devices, capture, 1, project);
                    string ungrouped = capture.Add(root + "/ungrouped", rootId, "deviceGroup", "Ungrouped devices", null, null);
                    if (ungrouped != null) CaptureModelDevices(project.UngroupedDevicesGroup.Devices, root + "/ungrouped", ungrouped, capture, 1, project);
                    CaptureModelDeviceGroups(project.DeviceGroups, root, rootId, capture, 1, project);
                }
            }
            timer.Stop();
            if (capture.PlcPath != null && !capture.SelectedPlcFound)
                throw new BridgeException(capture.Budget.Truncated ? "TraversalIncomplete" : "ObjectNotFound", "Selected plcPath was not found within the capture budget.");
            if (capture.ScopePath != null && !capture.SelectedScopeFound)
                throw new BridgeException(capture.Budget.Truncated ? "TraversalIncomplete" : "ObjectNotFound", "Selected scopePath was not found within the capture budget.");
            if (capture.Budget.Truncated) capture.Warnings.Add("Native traversal or retained payload bound reached; narrow plcPath/scopePath or increase maxVisited within its ceiling.");
            var coverage = new JArray(ModelCoverage("nativeHierarchy", capture.ScopePath ?? capture.PlcPath ?? root,
                capture.Budget.Truncated || capture.Warnings.Count != 0 ? "partial" : "complete", capture.Budget.Used, capture.Nodes.Count, capture.Warnings),
                ModelCoverage("hmi", root, capture.HmiCount == 0 && !capture.Budget.Truncated ? "complete" : "partial", capture.HmiCount, capture.HmiCount,
                    new JArray(capture.HmiCount == 0 ? "No HMI software identity was returned in this capture scope; partial native traversal cannot establish absence." :
                        "HMI software identities and registered section contributions are enumerated. Consult per-target provider coverage; screen items and content are separate detail observations and family qualification is independent.")),
                ModelCoverage("typedLadFbd", root, "partial", 0, 0, new JArray("The registered read-only typed preview provider uses the existing native LAD/FBD reader. Select eligible block details for separate content capture and supported/opaque network coverage; metadata capture does not read logic.")),
                ModelCoverage("plcLibraryInstances", root, "partial",
                    capture.Nodes.Count(n => n["metadata"]?["libraryInstance"] != null),
                    capture.Nodes.Count(n => n["metadata"]?["libraryInstance"]?.Value<string>("status") == "linked"),
                    new JArray("Per-object FB/FC/PLC-type service observations only. Missing services, unsupported objects and failed reads remain distinct. GUID observations do not prove complete library/dependency inventories, export correspondence, runtime DB instances or native qualification.")),
                ModelCoverage("plcLibraryDependencies", root, "partial", capture.LibraryDependencyBudget.Used,
                    capture.Nodes.Count(n => n["metadata"]?["libraryInstance"]?["dependencies"]?.Value<string>("status") == "observed"),
                    new JArray("Direct version GUID lists only, limited to 1000 entries per observation and 20000 collection/iterator read operations across the capture. Duplicate observations remain distinct; unavailable lists never mean empty. No complete library or project-consumer closure is established.")),
                ModelCoverage("physicalTopology", root, "unsupported", 0, 0, new JArray("Device/item composition and exposed position numbers do not prove racks, cables, port connectivity or electrical wiring.")));
            foreach (var providerCoverage in capture.ProviderCoverage) coverage.Add(providerCoverage);
            var warnings = ModelWarnings(capture.Warnings);
            string revision = ProjectIndexStore.Hash(project.Path.FullName + "\n" + capture.Nodes.ToString(Formatting.None));
            var document = new JObject { ["schemaVersion"] = "project-model.v1", ["sessionGeneration"] = _modelSessionGeneration,
                ["project"] = new JObject { ["name"] = project.Name, ["projectPath"] = project.Path.FullName, ["host"] = Environment.MachineName },
                ["captureStartedAtUtc"] = started, ["captureCompletedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["metadataRevision"] = new JObject { ["value"] = revision, ["scope"] = "exposedMetadata", ["includesContent"] = false },
                ["limits"] = new JObject { ["maxVisited"] = maximum, ["maxDepth"] = maxDepth, ["maxNodes"] = 50000, ["maxBytes"] = maxBytes,
                    ["maxPageItems"] = 200, ["maxPageBytes"] = 512 * 1024, ["maxLibraryDependencyReadOperations"] = 20000 },
                ["usage"] = new JObject { ["visited"] = capture.Budget.Used, ["returned"] = capture.Nodes.Count, ["accountedBytes"] = capture.Bytes,
                    ["nativeCaptureMilliseconds"] = timer.Elapsed.TotalMilliseconds, ["truncated"] = capture.Budget.Truncated,
                    ["libraryDependencyReadOperations"] = capture.LibraryDependencyBudget.Used, ["libraryDependencyBudgetExhausted"] = capture.LibraryDependencyBudget.Truncated },
                ["coverage"] = coverage, ["nodes"] = capture.Nodes, ["edges"] = new JArray(), ["warnings"] = warnings,
                ["freshness"] = "snapshotOnly", ["consistency"] = "Bounded sequential native traversal; capture interval is exposed. Metadata does not certify content or a complete project revision." };
            long documentBytes = Encoding.UTF8.GetByteCount(document.ToString(Formatting.None));
            if (documentBytes > maxBytes)
            {
                // Provider coverage also consumes the requested byte budget. Remove
                // nodes from the end of parent-first traversal, preserving ancestry.
                long excess = documentBytes - maxBytes + 2048;
                while (capture.Nodes.Count > 1 && excess > 0)
                {
                    var last = capture.Nodes.Last;
                    excess -= Encoding.UTF8.GetByteCount(last.ToString(Formatting.None)) + 1;
                    last.Remove();
                }
                document["usage"]["truncated"] = true; document["usage"]["returned"] = capture.Nodes.Count;
                document["coverage"][0]["status"] = "truncated"; document["coverage"][0]["returned"] = capture.Nodes.Count;
                ((JArray)document["warnings"]).Add("Requested snapshot byte budget reached; captured hierarchy is partial.");
                document["metadataRevision"]["value"] = ProjectIndexStore.Hash(project.Path.FullName + "\n" + capture.Nodes.ToString(Formatting.None));
                if (Encoding.UTF8.GetByteCount(document.ToString(Formatting.None)) > maxBytes)
                    throw new BridgeException("ModelCapacity", "Provider coverage exceeds the requested maxBytes; increase the budget or narrow the selected scope.");
            }
            return document;
        }

        private static JObject ModelCoverage(string provider, string scope, string status, int visited, int returned, JArray warnings) => new JObject
        { ["provider"] = provider, ["scope"] = scope, ["status"] = status, ["visited"] = visited, ["returned"] = returned,
            ["warnings"] = ModelWarnings(warnings), ["continuationAvailable"] = false };

        private static JArray ModelWarnings(JArray warnings) => new JArray(warnings.Take(100)
            .Select(w => w.Type == JTokenType.String ? w.Value<string>() : w.ToString(Formatting.None)).Select(w => w.Length > 1000 ? w.Substring(0, 1000) : w));

        // Test capacity before MoveNext: a child may exhaust the shared budget,
        // and resuming its parent's foreach must not fetch every remaining native sibling.
        private static IEnumerable<T> BoundedModelItems<T>(IEnumerable<T> source, ModelCapture capture, string parent)
        {
            if (!capture.CanReadChildren(parent)) yield break;
            using (var iterator = source.GetEnumerator())
                while (capture.CanReadChildren(parent) && iterator.MoveNext()) yield return iterator.Current;
        }

        private void CaptureModelDeviceGroups(IEnumerable<DeviceUserGroup> groups, string path, string parent, ModelCapture capture, int depth, Project project)
        {
            if (!capture.Depth(depth)) return;
            foreach (var group in BoundedModelItems(groups, capture, parent))
            {
                string childPath = path + "/groups/" + Segment(group.Name);
                if (!capture.Relevant(childPath)) { capture.VisitSkipped(); continue; }
                string id = capture.Add(childPath, parent, "deviceGroup", group.Name, null, null);
                if (id == null) break;
                try
                {
                    CaptureModelDevices(group.Devices, childPath + "/devices", id, capture, depth + 1, project);
                    CaptureModelDeviceGroups(group.Groups, childPath, id, capture, depth + 1, project);
                }
                catch (Exception error) { capture.Unavailable(childPath, id, error); }
            }
        }

        private void CaptureModelDevices(IEnumerable<Device> devices, string path, string parent, ModelCapture capture, int depth, Project project)
        {
            if (!capture.Depth(depth)) return;
            foreach (var device in BoundedModelItems(devices, capture, parent))
            {
                string childPath = path + "/" + Segment(device.Name);
                if (!capture.Relevant(childPath)) { capture.VisitSkipped(); continue; }
                string id = capture.Add(childPath, parent, "device", device.Name, null, null);
                if (id == null) break;
                try { CaptureModelItems(device.DeviceItems, childPath, id, capture, depth + 1, project); }
                catch (Exception error) { capture.Unavailable(childPath, id, error); }
            }
        }

        private void CaptureModelItems(IEnumerable<DeviceItem> items, string path, string parent, ModelCapture capture, int depth, Project project)
        {
            if (!capture.Depth(depth)) return;
            foreach (var item in BoundedModelItems(items, capture, parent))
            {
                string childPath = path + "/items/" + Segment(item.Name);
                if (!capture.Relevant(childPath)) { capture.VisitSkipped(); continue; }
                string id = capture.Add(childPath, parent, "hardware", item.Name, null, null,
                    new JObject { ["positionNumber"] = item.PositionNumber, ["classification"] = item.Classification.ToString(),
                        ["isBuiltIn"] = item.IsBuiltIn, ["isPlugged"] = item.IsPlugged, ["topologyVerified"] = false });
                if (id == null) break;
                try
                {
                    var software = item.GetService<SoftwareContainer>()?.Software;
                    if (software is PlcSoftware plcSoftware)
                    {
                        string plcPath = childPath + "/plc/" + Segment(software.Name);
                        if (capture.PlcPath == null || capture.PlcPath == plcPath)
                        {
                            capture.SelectedPlcFound = true;
                            string plcId = capture.Add(plcPath, id, "plc", software.Name, plcPath, plcPath);
                            if (plcId != null) CaptureModelPlc(new PlcSelection { Project = project, Software = plcSoftware, Path = plcPath }, plcId, capture, depth + 1);
                        }
                    }
                    else if (software != null && capture.PlcPath == null)
                    {
                        string type = software.GetType().FullName;
                        string family = type == "Siemens.Engineering.Hmi.HmiTarget" ? "classic" : type == "Siemens.Engineering.HmiUnified.HmiSoftware" ? "unified" : null;
                        string softwarePath = childPath + (family == null ? "/software/" : "/hmi/") + Segment(software.Name);
                        string softwareId = capture.Add(softwarePath, id, family == null ? "unsupportedSoftware" : "hmi", software.Name, null, null,
                            new JObject { ["nativeType"] = type, ["family"] = family, ["contentCoverage"] = family == null ? "unsupported" : "boundedInventoryViaDetails" });
                        if (softwareId != null && family != null)
                        {
                            capture.HmiCount++;
                            CaptureHmiProjectModel(project, softwarePath, softwareId, capture);
                        }
                    }
                }
                catch (Exception error) { capture.Unavailable(childPath, id, error); }
                try { CaptureModelItems(item.DeviceItems, childPath, id, capture, depth + 1, project); }
                catch (Exception error) { capture.Unavailable(childPath, id, error); }
            }
        }

        private void CaptureModelPlc(PlcSelection plc, string parent, ModelCapture capture, int depth)
        {
            if (!capture.Depth(depth)) return;
            string unitParent = null;
            try
            {
                // PlcUnitSystemGroup is an actual engineering object, including
                // when both its Units and SafetyUnits compositions are empty.
                // Both compositions have this native parent; their established
                // /units/ and /safetyUnits/ object paths remain unchanged.
                var provider = plc.Software.GetService<PlcUnitProvider>();
                if (provider?.UnitGroup != null && capture.Depth(depth + 1))
                    unitParent = capture.Add(plc.Path + "/units", parent, "softwareUnitGroup", provider.UnitGroup.Name, plc.Path, plc.Path,
                        new JObject { ["nativeType"] = "Siemens.Engineering.SW.Units.PlcUnitSystemGroup", ["compositions"] = new JArray("Units", "SafetyUnits") });
                else if (provider == null)
                    capture.ProviderCoverage.Add(ModelCoverage("softwareUnits", plc.Path, "unsupported", 0, 0,
                        new JArray("PlcUnitProvider is unavailable for this PLC configuration; absent service does not mean an empty native unit group.")));
            }
            catch (Exception error)
            {
                capture.Warnings.Add(InspectionWarning(plc.Path + "/units", error));
                capture.ProviderCoverage.Add(ModelCoverage("softwareUnits", plc.Path, "unavailable", 0, 0, new JArray(error.Message)));
            }
            foreach (var scope in SoftwareScopes(plc, capture.Budget, capture.Warnings))
            {
                if (capture.ScopePath != null && capture.ScopePath != scope.Path) continue;
                capture.SelectedScopeFound = true;
                int scopeDepth = scope.Unit == null ? depth : depth + 2;
                if (!capture.Depth(scopeDepth) || scope.Unit != null && unitParent == null) break;
                // SoftwareScopes already charged each native unit to this budget.
                string id = scope.Unit == null ? parent : capture.Add(scope.Path, unitParent, scope.Kind, scope.Name, plc.Path, scope.Path, alreadyVisited: true);
                if (id == null) break;
                try { CaptureModelBlocks(scope.Blocks, scope.Path + "/blocks", id, plc.Path, scope.Path, capture, scopeDepth + 1); }
                catch (Exception error) { capture.Unavailable(scope.Path + "/blocks", id, error); }
                try { CaptureModelTypes(scope.Types, scope.Path + "/types", id, plc.Path, scope.Path, capture, scopeDepth + 1); }
                catch (Exception error) { capture.Unavailable(scope.Path + "/types", id, error); }
                try { CaptureModelTags(scope.Tags, scope.Path + "/tags", id, plc.Path, scope.Path, capture, scopeDepth + 1); }
                catch (Exception error) { capture.Unavailable(scope.Path + "/tags", id, error); }
            }
            if (capture.ScopePath == null || capture.ScopePath == plc.Path)
                try { CaptureModelTechnology(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", parent, plc.Path, capture, depth + 1); }
                catch (Exception error) { capture.Unavailable(plc.Path + "/technology", parent, error); }
        }

        private void CaptureModelBlocks(PlcBlockGroup group, string path, string parent, string plc, string scope, ModelCapture capture, int depth)
        {
            if (group == null || !capture.Depth(depth)) return;
            string id = capture.Add(path, parent, "blockGroup", group.Name, plc, scope);
            if (id == null) return;
            foreach (var block in BoundedModelItems(group.Blocks, capture, id)) { if (!CaptureModelBlock(block, path, id, plc, scope, capture, false)) return; }
            foreach (var child in BoundedModelItems(group.Groups, capture, id)) CaptureModelBlocks(child, path + "/groups/" + Segment(child.Name), id, plc, scope, capture, depth + 1);
            if (group is PlcBlockSystemGroup system)
                foreach (var child in BoundedModelItems(system.SystemBlockGroups, capture, id)) CaptureModelSystemBlocks(child, path + "/systemGroups/" + Segment(child.Name), id, plc, scope, capture, depth + 1);
        }

        private void CaptureModelSystemBlocks(PlcSystemBlockGroup group, string path, string parent, string plc, string scope, ModelCapture capture, int depth)
        {
            if (!capture.Depth(depth)) return;
            string id = capture.Add(path, parent, "systemBlockGroup", group.Name, plc, scope);
            if (id == null) return;
            foreach (var block in BoundedModelItems(group.Blocks, capture, id)) { if (!CaptureModelBlock(block, path, id, plc, scope, capture, true)) return; }
            foreach (var child in BoundedModelItems(group.Groups, capture, id)) CaptureModelSystemBlocks(child, path + "/groups/" + Segment(child.Name), id, plc, scope, capture, depth + 1);
        }

        private static bool CaptureModelBlock(PlcBlock block, string group, string parent, string plc, string scope, ModelCapture capture, bool generated)
        {
            string path = BlockObjectPath(group, block);
            parent = capture.NamespaceParent(group, parent, block.Namespace, plc, scope);
            if (parent == null) return false;
            var metadata = BlockInfo(new BlockEntry { Item = block, Path = path }, plc);
            metadata.Remove("path"); metadata.Remove("plcPath"); metadata.Remove("name");
            metadata["namespace"] = block.Namespace; metadata["modified"] = block.ModifiedDate.ToString("O"); metadata["generated"] = generated;
            if (block is InstanceDB instance) metadata["instanceOfName"] = instance.InstanceOfName;
            metadata["libraryInstance"] = PlcLibraryInstanceCapture.Read(block, capture.LibraryDependencyBudget.Take);
            return capture.Add(path, parent, "block", block.Name, plc, scope, metadata) != null;
        }

        private void CaptureModelTypes(PlcTypeGroup group, string path, string parent, string plc, string scope, ModelCapture capture, int depth)
        {
            if (group == null || !capture.Depth(depth)) return;
            string id = capture.Add(path, parent, "typeGroup", group.Name, plc, scope);
            if (id == null) return;
            foreach (var type in BoundedModelItems(group.Types, capture, id)) { if (!CaptureModelType(type, path, id, plc, scope, capture)) return; }
            foreach (var child in BoundedModelItems(group.Groups, capture, id)) CaptureModelTypes(child, path + "/groups/" + Segment(child.Name), id, plc, scope, capture, depth + 1);
            if (group is PlcTypeSystemGroup system)
                foreach (var child in BoundedModelItems(system.SystemTypeGroups, capture, id))
                {
                    string childPath = path + "/systemGroups/" + Segment(child.Name);
                    string childId = capture.Add(childPath, id, "systemTypeGroup", child.Name, plc, scope);
                    if (childId == null) return;
                    foreach (var type in BoundedModelItems(child.Types, capture, childId)) { if (!CaptureModelType(type, childPath, childId, plc, scope, capture)) return; }
                }
        }

        private static bool CaptureModelType(PlcType type, string group, string parent, string plc, string scope, ModelCapture capture)
        {
            parent = capture.NamespaceParent(group, parent, type.Namespace, plc, scope);
            return parent != null && capture.Add(ObjectIdentity.ObjectPath(group, type.Name, type.Namespace), parent, "type", type.Name, plc, scope,
                new JObject { ["namespace"] = type.Namespace, ["modified"] = type.ModifiedDate.ToString("O"),
                    ["isKnowHowProtected"] = type.IsKnowHowProtected, ["isConsistent"] = type.IsConsistent,
                    ["libraryInstance"] = PlcLibraryInstanceCapture.Read(type, capture.LibraryDependencyBudget.Take) }) != null;
        }

        private void CaptureModelTags(PlcTagTableGroup group, string path, string parent, string plc, string scope, ModelCapture capture, int depth)
        {
            if (group == null || !capture.Depth(depth)) return;
            string id = capture.Add(path, parent, "tagGroup", group.Name, plc, scope);
            if (id == null) return;
            foreach (var table in BoundedModelItems(group.TagTables, capture, id))
            {
                string tablePath = path + "/" + Segment(table.Name);
                string tableId = capture.Add(tablePath, id, "tagTable", table.Name, plc, scope, new JObject { ["modified"] = table.ModifiedTimeStamp.ToString("O") });
                if (tableId == null) return;
                foreach (var tag in BoundedModelItems(table.Tags, capture, tableId))
                    if (capture.Add(tablePath + "/tags/" + Segment(tag.Name), tableId, "tag", tag.Name, plc, scope,
                        new JObject { ["dataType"] = tag.DataTypeName, ["logicalAddress"] = tag.LogicalAddress, ["isSafety"] = tag.IsSafety,
                            ["externalAccessible"] = tag.ExternalAccessible, ["externalVisible"] = tag.ExternalVisible, ["externalWritable"] = tag.ExternalWritable,
                            ["valueSemantics"] = "Offline declaration; no live PLC value" }) == null) return;
                foreach (var constant in BoundedModelItems(table.UserConstants, capture, tableId))
                    if (capture.Add(tablePath + "/constants/" + Segment(constant.Name), tableId, "constant", constant.Name, plc, scope,
                        new JObject { ["dataType"] = constant.DataTypeName, ["value"] = InspectionValue(constant.Value), ["valueSemantics"] = "Offline constant declaration" }) == null) return;
                foreach (var constant in BoundedModelItems(table.SystemConstants, capture, tableId))
                    if (capture.Add(tablePath + "/systemConstants/" + Segment(constant.Name), tableId, "systemConstant", constant.Name, plc, scope,
                        new JObject { ["dataType"] = constant.DataTypeName, ["value"] = InspectionValue(constant.Value), ["valueSemantics"] = "Offline constant declaration" }) == null) return;
            }
            foreach (var child in BoundedModelItems(group.Groups, capture, id)) CaptureModelTags(child, path + "/groups/" + Segment(child.Name), id, plc, scope, capture, depth + 1);
        }

        private void CaptureModelTechnology(TechnologicalInstanceDBGroup group, string path, string parent, string plc, ModelCapture capture, int depth)
        {
            if (group == null || !capture.Depth(depth)) return;
            string id = capture.Add(path, parent, "technologyGroup", group.Name, plc, plc);
            if (id == null) return;
            foreach (var item in BoundedModelItems(group.TechnologicalObjects, capture, id))
                CaptureModelTechnologyObject(TechnologyEntryFor(item, path, "TechnologicalObjects"), id, plc, capture);
            foreach (var child in BoundedModelItems(group.Groups, capture, id)) CaptureModelTechnology(child, path + "/groups/" + Segment(child.Name), id, plc, capture, depth + 1);
        }

        private void CaptureModelTechnologyObject(TechnologyEntry entry, string parent, string plc, ModelCapture capture)
        {
            var metadata = TechnologyInfo(entry);
            metadata.Remove("path"); metadata.Remove("name"); metadata["modified"] = entry.Item.ModifiedDate.ToString("O");
            string id = capture.Add(entry.Path, parent, "technologyObject", entry.Item.Name, plc, plc, metadata);
            if (id == null) return;
            foreach (var child in BoundedModelItems(TechnologyOwnedChildren(entry), capture, id))
                CaptureModelTechnologyObject(child, id, plc, capture);
        }

        private JObject ModelInspectionArguments(string id, JObject node, JObject request)
        {
            var snapshot = _projectModels.Header(id);
            var args = new JObject { ["projectName"] = snapshot["project"]["name"], ["plcPath"] = node["plcPath"], ["scopePath"] = node["scopePath"],
                ["maxItems"] = IndexInteger(request, "maxItems", 200, 1, 2000), ["artifactDirectory"] = request["artifactDirectory"] };
            string path = node.Value<string>("objectPath");
            switch (node.Value<string>("kind"))
            {
                case "block": args["blockPath"] = path; break;
                case "type": args["typePath"] = path; break;
                case "tagTable": args["tagTablePath"] = path; break;
                case "hardware": args["hardwarePath"] = path; break;
                case "technologyObject": args["technologyPath"] = path; break;
            }
            return args;
        }

        private JObject ProjectModelDetails(string id, JObject node, JObject reference, JObject request)
        {
            var timer = Stopwatch.StartNew();
            node.Remove("details");
            var result = new JObject { ["schemaVersion"] = "project-model.v1", ["reference"] = reference.DeepClone(), ["node"] = node,
                ["status"] = "available", ["freshness"] = "metadataVerifiedThenSeparateContentRead", ["readOnly"] = true,
                ["warnings"] = new JArray("Details were read after metadata capture; this is not an atomic cross-provider revision.") };
            var args = ModelInspectionArguments(id, node, request);
            var providerOptions = new JObject { ["artifactDirectory"] = args["artifactDirectory"], ["maxItems"] = Math.Min(200, args.Value<int>("maxItems")) };
            try
            {
                if (node["access"]?.Value<string>("state") == "protected")
                    throw new BridgeException("ProtectedObject", "Selected object is know-how protected; only its exposed metadata is available.");
                if (node.Value<string>("kind").StartsWith("hmi", StringComparison.OrdinalIgnoreCase))
                {
                    result["details"] = RegisteredProjectModelProviders.Inspect(new ProjectModelProviderRequest(reference, node, "hmiDetails", Math.Min(200, args.Value<int>("maxItems")), options: providerOptions));
                    result["status"] = result["details"]["status"];
                }
                else switch (node.Value<string>("kind"))
                {
                    case "block": result["details"] = BlockInterface(args); break;
                    case "type": result["details"] = TypeRead(args); break;
                    case "tagTable": result["details"] = TagTableRead(args); break;
                    case "hardware": result["details"] = HardwareInspect(args); break;
                    case "technologyObject": result["details"] = TechnologyRead(args); break;
                    default: result["details"] = node["metadata"].DeepClone(); result["detailScope"] = "exposedMetadata"; break;
                }
            }
            catch (BridgeException error)
            {
                result["status"] = error.Code == "ProtectedObject" ? "protected" : "unavailable";
                result["error"] = new JObject { ["code"] = error.Code, ["message"] = error.Message };
            }
            catch (Exception error)
            { result["status"] = "unavailable"; result["error"] = new JObject { ["code"] = error.GetType().Name, ["message"] = error.Message }; }
            result["capturedAtUtc"] = DateTime.UtcNow.ToString("O");
            var artifact = result["details"]?["artifact"] as JObject;
            result["contentRevision"] = result["details"]?["contentRevision"]?.DeepClone() ?? new JObject { ["status"] = "unavailable", ["scope"] = "No complete content revision supplied by this provider" };
            if (artifact?.Value<string>("sha256") != null) result["contentRevision"] = new JObject { ["status"] = "available", ["value"] = artifact["sha256"], ["algorithm"] = "sha256", ["scope"] = "exportedNativeArtifact" };
            result["previews"] = new JArray(RegisteredProjectModelProviders.Inspect(new ProjectModelProviderRequest(reference, node, "hmiPreview", options: providerOptions)),
                RegisteredProjectModelProviders.Inspect(new ProjectModelProviderRequest(reference, node, "typedLadFbdPreview", options: providerOptions)));
            timer.Stop(); result["nativeDetailMilliseconds"] = timer.Elapsed.TotalMilliseconds;
            // Keep portable observations within the node budget. Full native
            // exports remain separately hashed artifacts on the engineering host.
            if (result["details"] is JObject detailObject) detailObject.Remove("xml");
            if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > 60000)
            {
                var file = new FileInfo(Path.Combine(ArtifactDirectory(args), "project-detail-" + Guid.NewGuid().ToString("N") + ".json"));
                File.WriteAllText(file.FullName, result.ToString(Formatting.None), new UTF8Encoding(false));
                var detailArtifact = FileArtifact(file); detailArtifact["mediaType"] = "application/json";
                result["details"] = new JObject { ["artifact"] = detailArtifact, ["status"] = "bounded", ["reason"] = "Detailed payload exceeds the 60 KiB inline budget; use the hashed artifact." };
                result["previews"] = new JArray(); result["status"] = "partial";
            }
            _projectModels.RememberDetails(id, node.Value<string>("nodeId"), result);
            return result;
        }

        private JObject ProjectModelRelationships(string id, JObject selected, JObject reference, JObject request)
        {
            int depth = IndexInteger(request, "depth", 1, 1, 5), maxNodes = IndexInteger(request, "maxNodes", 100, 1, 500), maxEdges = IndexInteger(request, "maxEdges", 200, 1, 1000);
            string direction = request.Value<string>("direction") ?? "both";
            string[] kinds = (request["kinds"] as JArray)?.Values<string>().ToArray();
            var document = _projectModels.Get(id, _modelSessionGeneration);
            if (selected.Value<string>("plcPath") == null)
            {
                if (selected.Value<string>("kind") == "hardware")
                    return ProjectModelHardwareRelationships(id, document, selected, reference, request, kinds, direction, depth, maxNodes, maxEdges);
                JObject providerResult = null;
                ReadHmiProjectModelRelationships(document, reference, selected, request, ref providerResult);
                if (providerResult != null)
                {
                    var retained = new JArray((providerResult["edges"] as JArray ?? new JArray()).OfType<JObject>()
                        .Concat((providerResult["unresolvedObservations"] as JArray ?? new JArray()).OfType<JObject>())
                        .GroupBy(e => e.Value<string>("edgeId"), StringComparer.Ordinal).Select(g => g.First().DeepClone()));
                    try { _projectModels.RememberRelationships(id, retained, new JArray((providerResult["coverage"] as JArray ?? new JArray()).OfType<JObject>().Where(c => c.Value<string>("provider") == "hmi.relationships")), providerResult); providerResult["retainedForExport"] = true; }
                    catch (ProjectModelStore.ModelException error) when (error.Code == "ModelCapacity")
                    {
                        providerResult["retainedForExport"] = false;
                        if (!(providerResult["warnings"] is JArray)) providerResult["warnings"] = new JArray();
                        ((JArray)providerResult["warnings"]).Add("Provider query completed but snapshot observation retention is full: " + error.Message);
                    }
                    return BoundProjectModelGraphResponse(providerResult);
                }
                return new JObject { ["schemaVersion"] = "project-model.v1", ["reference"] = reference, ["status"] = "unsupported",
                    ["nodes"] = new JArray(selected), ["edges"] = new JArray(), ["warnings"] = new JArray("Native cross-reference analysis requires a PLC object. Hardware composition is available in the tree; physical topology is unverified.") };
            }
            var timer = Stopwatch.StartNew();
            var args = ModelInspectionArguments(id, selected, request);
            // Query the PLC service so incoming references and bounded multi-hop
            // expansion are possible; the existing native analyzer controls bounds.
            args.Remove("blockPath"); args["maxItems"] = IndexInteger(request, "maxVisited", 2000, 1, 2000);
            NativeAnalysis analysis;
            try { analysis = ReadNativeAnalysis(args); }
            catch (Exception error)
            {
                return new JObject { ["schemaVersion"] = "project-model.v1", ["reference"] = reference, ["status"] = "unavailable",
                    ["nodes"] = new JArray(selected), ["edges"] = new JArray(), ["warnings"] = new JArray(error.Message), ["capturedAtUtc"] = DateTime.UtcNow.ToString("O") };
            }
            var nodes = ((JArray)document["nodes"]).OfType<JObject>().Where(n => n.Value<string>("objectPath") != null)
                .ToDictionary(n => n.Value<string>("objectPath"), StringComparer.Ordinal);
            var nativePaths = ModelNativeEndpointPaths(analysis, IndexInteger(request, "maxIdentityVisited", 5000, 1, 50000));
            string captured = DateTime.UtcNow.ToString("O");
            var edges = new JArray();
            foreach (var relation in analysis.References)
            {
                var evidence = ReferenceJson(relation);
                var sourceCandidate = ModelAnalysisEndpoint(relation.Actor ?? relation.Source, nodes, nativePaths, true);
                var targetCandidate = ModelAnalysisEndpoint(relation.Subject ?? relation.Target, nodes, nativePaths, false);
                var relationKinds = new List<string>();
                if (IsNativeCall(relation.Access) && OfflineEvidence.IsCallRelation(relation.Relation.ToString())) relationKinds.Add("calls");
                if (IsNativeRead(relation.Access)) relationKinds.Add("reads");
                if (IsNativeWrite(relation.Access)) relationKinds.Add("writes");
                if (IsInstanceAccess(relation.Access)) relationKinds.Add("instanceOf");
                if (relation.Relation == ReferenceType.InstanceType || relation.Relation == ReferenceType.TypeInstance) relationKinds.Add("usesType");
                if (relation.Actor != null) relationKinds.Add("dependsOn");
                if (relationKinds.Count == 0) relationKinds.Add("dependsOn");
                foreach (string kind in relationKinds)
                {
                    var kindTargetCandidate = kind == "calls" ? ModelCallTarget(relation, analysis, nodes, targetCandidate) : targetCandidate;
                    JObject source = sourceCandidate.Value<string>("resolution") == "resolved" ? sourceCandidate : null;
                    JObject target = kindTargetCandidate.Value<string>("resolution") == "resolved" ? kindTargetCandidate : null;
                    bool resolved = relation.Actor != null && source != null && target != null;
                    var unresolved = resolved ? null : new JObject { ["source"] = sourceCandidate.DeepClone(), ["target"] = kindTargetCandidate.DeepClone(),
                        ["reason"] = relation.Actor == null ? "Native relation direction is unresolved" : "At least one native endpoint is not an exact captured object" };
                    edges.Add(new JObject { ["edgeId"] = "xref-" + ProjectIndexStore.Hash(evidence.ToString(Formatting.None) + "\n" + kind).Substring(0, 20), ["kind"] = kind,
                        ["source"] = source?.DeepClone(), ["target"] = target?.DeepClone(), ["unresolved"] = unresolved,
                        ["evidence"] = new JObject { ["provider"] = "nativeCrossReference", ["origin"] = resolved ? "nativeConfirmed" : "unresolved", ["resolution"] = resolved ? "resolved" : "unresolved",
                            ["sourcePath"] = relation.Actor?.BlockPath, ["capturedAtUtc"] = captured,
                            ["contentRevision"] = new JObject { ["status"] = "unavailable", ["scope"] = "nativeCrossReferences" },
                            ["location"] = relation.Location, ["native"] = evidence.DeepClone() } });
                }
            }
            // Address matches carry the existing analyzer's controller-ownership
            // classification. Unverified overlaps remain unresolved candidates.
            string address = selected["metadata"]?.Value<string>("logicalAddress");
            if (address != null)
                foreach (var match in MatchHardwareAddresses(analysis, new[] { address }).OfType<JObject>())
                {
                    bool verified = match.Value<bool>("plcOwnershipVerified");
                    nodes.TryGetValue(match.Value<string>("hardwarePath"), out var hardware);
                    edges.Add(new JObject { ["edgeId"] = ModelIoEdgeId(selected.Value<string>("objectPath"), match),
                        ["kind"] = "configuredIo", ["source"] = ModelEndpoint(selected), ["target"] = hardware == null ? null : ModelEndpoint(hardware),
                        ["unresolved"] = verified && hardware != null ? null : new JObject { ["reason"] = "Configured overlap requires exact hardware identity and verified selected PLC ownership", ["native"] = match.DeepClone() },
                        ["evidence"] = new JObject { ["provider"] = "nativeConfiguredAddress", ["sourcePath"] = match["hardwarePath"], ["location"] = null,
                            ["origin"] = verified ? "nativeConfirmed" : "unresolved", ["resolution"] = verified && hardware != null ? "resolved" : "unresolved", ["capturedAtUtc"] = captured,
                            ["contentRevision"] = new JObject { ["status"] = "unavailable", ["scope"] = "configuredAddressRange" }, ["native"] = match } });
                }
            document["edges"] = edges;
            var result = ProjectModelStore.QueryRelationships(document, selected.Value<string>("nodeId"), kinds, direction, depth, maxNodes, maxEdges);
            result["reference"] = reference;
            result["freshness"] = "metadataVerifiedThenSeparateNativeRead";
            result["nativeCoverage"] = AnalysisCoverage(analysis, "Native calls, instance, reads, writes, dependencies and configured address evidence; unresolved endpoints retain original native identities.");
            result["nativeCoverage"]["hardwareVisitedNativeItems"] = analysis.HardwareVisited;
            result["warnings"] = ModelWarnings(analysis.Warnings);
            ((JArray)result["warnings"]).Add("Relationship evidence was read after metadata capture. This is a mixed observation, not a complete project/content revision.");
            timer.Stop(); result["nativeAnalysisMilliseconds"] = timer.Elapsed.TotalMilliseconds; result["capturedAtUtc"] = captured;
            try
            {
                _projectModels.RememberRelationships(id, (JArray)result["edges"], new JArray(ModelCoverage("nativeRelationships", selected.Value<string>("objectPath"),
                    analysis.Budget.Truncated || analysis.IndexTruncated || analysis.Warnings.Count != 0 ? "partial" : "complete", analysis.Budget.Used, ((JArray)result["edges"]).Count, analysis.Warnings)), result);
                result["retainedForExport"] = true;
            }
            catch (ProjectModelStore.ModelException error) when (error.Code == "ModelCapacity")
            {
                result["retainedForExport"] = false;
                ((JArray)result["warnings"]).Add("This bounded query completed, but aggregate snapshot observation retention is full. Export the current snapshot then recapture a narrower scope: " + error.Message);
            }
            return BoundProjectModelGraphResponse(result);
        }

        private static JObject ModelEndpoint(JObject node) => new JObject { ["nodeId"] = node["nodeId"], ["objectPath"] = node["objectPath"], ["resolution"] = "resolved" };

        private static string ModelIoEdgeId(string tagPath, JObject match) => "io-" + ProjectIndexStore.Hash(tagPath + "\n" + match.ToString(Formatting.None)).Substring(0, 20);

        private static JObject BoundProjectModelGraphResponse(JObject result)
        {
            const int maximum = 512 * 1024;
            int bytes = Encoding.UTF8.GetByteCount(result.ToString(Formatting.None));
            if (bytes <= maximum - 32) return AccountProjectModelGraphResponse(result);
            result["warningsTruncated"] = true; result["truncated"] = true;
            if (result["relationshipScope"] is JObject relationshipScope)
            {
                relationshipScope["status"] = "truncated";
                relationshipScope["returnedEdges"] = (result["edges"] as JArray)?.Count ?? 0;
                var reasons = relationshipScope["stopReasons"] as JArray ?? new JArray();
                if (!reasons.Values<string>().Contains("adapterBytes")) reasons.Add("adapterBytes");
                relationshipScope["stopReasons"] = reasons;
            }
            if (result["warnings"] is JArray warnings)
            {
                while (warnings.Count > 0 && Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > maximum - 256) warnings.Last.Remove();
                warnings.Add("Additional native diagnostics omitted at the complete 512 KiB graph response bound; relationship evidence and exact endpoints are retained.");
            }
            if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > maximum - 128 && result["nativeCoverage"] != null)
                result["nativeCoverage"] = new JObject { ["status"] = "partial", ["reason"] = "Expanded native provider diagnostics exceeded the graph response budget. Edge-level source evidence remains present." };
            if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > maximum)
                throw new BridgeException("ModelCapacity", "The provider graph response exceeds 512 KiB; narrow the graph scope or relationship kinds.");
            return AccountProjectModelGraphResponse(result);
        }

        private static JObject AccountProjectModelGraphResponse(JObject result)
        {
            int bytes;
            do { bytes = Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)); if (result.Value<int?>("payloadBytes") == bytes) break; result["payloadBytes"] = bytes; } while (true);
            if (bytes > 512 * 1024) throw new BridgeException("ModelCapacity", "Graph response exceeds its complete serialized byte budget.");
            return result;
        }

        private JObject ProjectModelHardwareRelationships(string id, JObject document, JObject hardware, JObject reference, JObject request,
            string[] kinds, string direction, int depth, int maxNodes, int maxEdges)
        {
            var timer = Stopwatch.StartNew();
            int maxTags = IndexInteger(request, "maxIoTags", 200, 1, 2000), maxPlcs = IndexInteger(request, "maxIoPlcs", 10, 1, 32);
            int maximum = IndexInteger(request, "maxVisited", 2000, 1, 2000);
            var warnings = new JArray(); var edges = new JArray(); var coverage = new JArray();
            var eligible = document["nodes"].OfType<JObject>().Where(n => n.Value<string>("kind") == "tag" && n.Value<string>("plcPath") != null &&
                OfflineEvidence.ParseIoAddress(n["metadata"]?.Value<string>("logicalAddress")) != null).OrderBy(n => n.Value<string>("objectPath"), StringComparer.Ordinal).ToArray();
            var candidates = eligible.Take(maxTags).ToArray();
            var groups = candidates.GroupBy(n => n.Value<string>("plcPath"), StringComparer.Ordinal).ToArray();
            bool bounded = eligible.Length > maxTags || groups.Length > maxPlcs;
            string captured = DateTime.UtcNow.ToString("O");
            foreach (var group in groups.Take(maxPlcs))
            {
                try
                {
                    var plc = SelectPlc(new JObject { ["projectName"] = document["project"]["name"], ["plcPath"] = group.Key });
                    var analysis = new NativeAnalysis { Plc = plc, MaxItems = maximum, Budget = new ScanBudget(maximum), Scope = hardware.Value<string>("objectPath") };
                    var tagsByAddress = group.GroupBy(n => n["metadata"].Value<string>("logicalAddress"), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
                    var matches = MatchHardwareAddresses(analysis, tagsByAddress.Keys, hardware.Value<string>("objectPath"));
                    foreach (var match in matches.OfType<JObject>())
                    {
                        if (!tagsByAddress.TryGetValue(match.Value<string>("queryAddress"), out var tags)) continue;
                        foreach (var tag in tags)
                        {
                            if (edges.Count >= 1000) { bounded = true; break; }
                            bool verified = match.Value<bool>("plcOwnershipVerified");
                            var evidence = new JObject { ["provider"] = "nativeConfiguredAddress", ["origin"] = verified ? "nativeConfirmed" : "unresolved",
                                ["resolution"] = verified ? "resolved" : "unresolved", ["sourcePath"] = hardware["objectPath"], ["location"] = null,
                                ["capturedAtUtc"] = captured, ["contentRevision"] = new JObject { ["status"] = "unavailable", ["scope"] = "configuredAddressRange" },
                                ["native"] = match.DeepClone(), ["tagDeclarationPath"] = tag["objectPath"], ["selectedPlcPath"] = group.Key };
                            edges.Add(new JObject { ["edgeId"] = ModelIoEdgeId(tag.Value<string>("objectPath"), match),
                                ["kind"] = "configuredIo", ["source"] = ModelEndpoint(tag), ["target"] = ModelEndpoint(hardware), ["evidence"] = evidence,
                                ["unresolved"] = verified ? null : new JObject { ["reason"] = "Address overlap does not establish association until selected-PLC controller ownership is verified.", ["native"] = match.DeepClone() } });
                        }
                    }
                    foreach (var warning in ModelWarnings(analysis.Warnings)) warnings.Add(warning);
                    coverage.Add(ModelCoverage("configuredIo", group.Key, analysis.Warnings.Count != 0 || bounded ? "partial" : "complete",
                        analysis.HardwareVisited, matches.Count, analysis.Warnings));
                }
                catch (Exception error)
                {
                    warnings.Add("Configured I/O inspection for " + group.Key + ": " + error.Message);
                    coverage.Add(ModelCoverage("configuredIo", group.Key, "unavailable", 0, 0, new JArray(error.Message)));
                }
            }
            if (bounded) warnings.Add("Configured I/O candidate bounds reached. Narrow the PLC capture or increase maxIoTags/maxIoPlcs within their ceilings.");
            warnings.Add("Only configured address ranges with controller evidence are compared. This view does not prove channels, terminals, cables or physical topology.");
            if (candidates.Length == 0) coverage.Add(ModelCoverage("configuredIo", hardware.Value<string>("objectPath"), "partial", 0, 0,
                new JArray("No I/O-address tag declarations are present in the captured scope; this does not establish that the hardware is unused.")));
            document["edges"] = edges;
            var result = ProjectModelStore.QueryRelationships(document, hardware.Value<string>("nodeId"), kinds, direction, depth, maxNodes, maxEdges);
            result["reference"] = reference; result["nativeCoverage"] = coverage; result["warnings"] = ModelWarnings(warnings);
            result["freshness"] = "metadataVerifiedThenSeparateNativeRead";
            ((JArray)result["warnings"]).Add("Configured I/O evidence was read after metadata capture; no atomic configuration/content revision is asserted.");
            result["candidateBounds"] = new JObject { ["maxIoTags"] = maxTags, ["maxIoPlcs"] = maxPlcs, ["maxNativeItemsPerPlc"] = maximum,
                ["eligibleCapturedTags"] = eligible.Length, ["selectedTags"] = candidates.Length, ["truncated"] = bounded };
            result["capturedAtUtc"] = captured;
            timer.Stop(); result["nativeAnalysisMilliseconds"] = timer.Elapsed.TotalMilliseconds;
            try { _projectModels.RememberRelationships(id, (JArray)result["edges"], coverage, result); result["retainedForExport"] = true; }
            catch (ProjectModelStore.ModelException error) when (error.Code == "ModelCapacity")
            { result["retainedForExport"] = false; ((JArray)result["warnings"]).Add("Query completed but snapshot observation retention is full: " + error.Message); }
            return BoundProjectModelGraphResponse(result);
        }

        private static JObject ModelCallTarget(NativeReference reference, NativeAnalysis analysis, Dictionary<string, JObject> nodes, JObject fallback)
        {
            string path = reference.Subject?.BlockPath;
            if (path == null || !analysis.Blocks.TryGetValue(path, out var block) || !(block.Item is InstanceDB instance)) return fallback;
            var nativeTypes = analysis.References.Where(item =>
                (item.Relation == ReferenceType.InstanceType || item.Relation == ReferenceType.TypeInstance) && item.Actor?.Key == path &&
                item.Subject?.Key == item.Subject?.BlockPath && item.Subject?.BlockPath != null &&
                analysis.Blocks.TryGetValue(item.Subject.BlockPath, out var declared) && declared.Item is FB).Select(item => item.Subject.BlockPath);
            var resolution = OfflineEvidence.ResolveInstanceTarget(analysis.Plc.Path, path, instance.Namespace, instance.InstanceOfName, analysis.IndexTruncated,
                analysis.Blocks.Values.Where(entry => entry.Item is FB).Select(entry => new OfflineEvidence.InstanceTarget { Path = entry.Path, Name = entry.Item.Name, Namespace = entry.Item.Namespace }), nativeTypes);
            if (resolution.Path != null && nodes.TryGetValue(resolution.Path, out var callee))
            {
                var endpoint = ModelEndpoint(callee); endpoint["instancePath"] = path; endpoint["instanceResolution"] = resolution.Reason; return endpoint;
            }
            return new JObject { ["resolution"] = "unresolved", ["instancePath"] = path, ["instanceOfName"] = instance.InstanceOfName,
                ["reason"] = resolution.Reason, ["nativeIdentity"] = IdentityJson(reference.Subject) };
        }

        private static JObject ModelAnalysisEndpoint(AnalysisIdentity identity, Dictionary<string, JObject> nodes, Dictionary<IEngineeringObject, string> nativePaths, bool actor)
        {
            if (identity != null && nodes.TryGetValue(identity.Key, out var node)) return ModelEndpoint(node);
            if (identity?.EngineeringObject != null && nativePaths.TryGetValue(identity.EngineeringObject, out var exactPath) && nodes.TryGetValue(exactPath, out node)) return ModelEndpoint(node);
            // A member owner is useful navigation, but is never substituted for a
            // member's identity or inferred from a duplicate display name/address.
            JObject owner = null;
            if (identity?.BlockPath != null) nodes.TryGetValue(identity.BlockPath, out owner);
            if (actor && owner != null)
            {
                var endpoint = ModelEndpoint(owner);
                endpoint["semantics"] = "Operation in this owning block; original member identity remains in native evidence";
                return endpoint;
            }
            return new JObject { ["resolution"] = "unresolved", ["nativeKey"] = identity?.Key, ["name"] = identity?.Name,
                ["ownerNodeId"] = owner?["nodeId"], ["ownerObjectPath"] = identity?.BlockPath, ["nativeIdentity"] = IdentityJson(identity),
                ["reason"] = "Native endpoint has no exact captured project node; its owner/name is not a replacement identity." };
        }

        private static Dictionary<IEngineeringObject, string> ModelNativeEndpointPaths(NativeAnalysis analysis, int maximum)
        {
            var paths = new Dictionary<IEngineeringObject, string>();
            var budget = new ScanBudget(maximum);
            foreach (var scope in SoftwareScopes(analysis.Plc, budget, analysis.Warnings))
                foreach (var table in TagTables(scope.Tags, scope.Path + "/tags", budget))
                {
                    paths[table.Item] = table.Path;
                    foreach (var tag in table.Item.Tags)
                    { if (!budget.Take()) break; paths[tag] = table.Path + "/tags/" + Segment(tag.Name); }
                    foreach (var constant in table.Item.UserConstants)
                    { if (!budget.Take()) break; paths[constant] = table.Path + "/constants/" + Segment(constant.Name); }
                    foreach (var constant in table.Item.SystemConstants)
                    { if (!budget.Take()) break; paths[constant] = table.Path + "/systemConstants/" + Segment(constant.Name); }
                    if (budget.Truncated) break;
                }
            if (budget.Truncated) analysis.Warnings.Add("Exact native tag/constant identity traversal reached its bound; unassociated endpoints remain unresolved.");
            return paths;
        }

        private sealed class ModelCapture
        {
            internal readonly ScanBudget Budget;
            internal readonly ScanBudget LibraryDependencyBudget = new ScanBudget(20000);
            internal readonly JArray Nodes = new JArray(), Warnings = new JArray(), ProviderCoverage = new JArray();
            internal readonly string PlcPath, ScopePath;
            internal readonly int Maximum, MaxDepth, MaxBytes;
            internal int Remaining => Math.Max(0, Maximum - Budget.Used);
            internal bool SelectedPlcFound, SelectedScopeFound;
            internal int HmiCount;
            internal long Bytes;
            private readonly Dictionary<string, JObject> _byId = new Dictionary<string, JObject>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _depthById = new Dictionary<string, int>(StringComparer.Ordinal);
            internal ModelCapture(int maximum, int maxDepth, int maxBytes, string plc, string scope)
            { Maximum = maximum; MaxDepth = maxDepth; MaxBytes = maxBytes; Budget = new ScanBudget(maximum); PlcPath = string.IsNullOrWhiteSpace(plc) ? null : plc; ScopePath = string.IsNullOrWhiteSpace(scope) ? null : scope; }
            internal bool Relevant(string path) => PlcPath == null || PlcPath == path || PlcPath.StartsWith(path + "/", StringComparison.Ordinal) || path.StartsWith(PlcPath + "/", StringComparison.Ordinal);
            internal bool CanReadNext { get { if (Budget.Used >= Maximum) Budget.Truncated = true; return !Budget.Truncated; } }
            internal bool CanReadChildren(string parent) { if (parent != null && _depthById[parent] >= MaxDepth) Budget.Truncated = true; return CanReadNext; }
            internal void VisitSkipped() { Budget.Take(); }
            internal bool Depth(int depth) { if (depth > MaxDepth) Budget.Truncated = true; return CanReadNext; }
            internal string Add(string path, string parent, string kind, string name, string plc, string scope, JObject metadata = null, bool native = true, bool alreadyVisited = false)
            {
                if (Budget.Truncated) return null;
                string id = (native ? "n-" : "v-") + ProjectIndexStore.Hash(path + "\n" + kind).Substring(0, 24);
                if (_byId.ContainsKey(id)) return id;
                if (!alreadyVisited && !Budget.Take()) return null;
                int depth = parent == null ? 0 : _depthById[parent] + 1;
                if (depth > MaxDepth) { Budget.Truncated = true; return null; }
                metadata = metadata ?? new JObject();
                var node = new JObject { ["nodeId"] = id, ["parentId"] = parent, ["kind"] = kind, ["name"] = name ?? kind,
                    ["origin"] = native ? "native" : "virtual", ["plcPath"] = plc, ["scopePath"] = scope,
                    ["access"] = new JObject { ["state"] = !native ? "virtual" : metadata.Value<bool?>("isKnowHowProtected") == true ? "protected" : "readable" },
                    ["detailAvailability"] = native ? "available" : "displayOnly", ["metadata"] = metadata };
                if (native) node["objectPath"] = path;
                int bytes = Encoding.UTF8.GetByteCount(node.ToString(Formatting.None));
                if (bytes > 65536 || Bytes + bytes > MaxBytes - 16384 || Nodes.Count >= 50000)
                { Budget.Truncated = true; return null; }
                Bytes += bytes; Nodes.Add(node); _byId[id] = node; _depthById[id] = depth; return id;
            }
            internal string NamespaceParent(string path, string parent, string nameSpace, string plc, string scope) => string.IsNullOrEmpty(nameSpace) ? parent :
                Add(path + "/namespaces/" + Segment(nameSpace), parent, "namespace", nameSpace, plc, scope, new JObject { ["semantics"] = "Virtual namespace display folder; namespace is a native property of its children" }, false);
            internal void Unavailable(string path, string id, Exception error)
            {
                if (Warnings.Count < 100) Warnings.Add(InspectionWarning(path, error));
                if (_byId.TryGetValue(id, out var node))
                { node["detailAvailability"] = "partial"; node["access"] = new JObject { ["state"] = "unavailable", ["reason"] = error.Message }; }
            }
        }
    }
}
