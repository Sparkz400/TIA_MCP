using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TiaMcp.Analysis;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private JObject CallGraph(JObject args)
        {
            var analysis = ReadNativeAnalysis(args);
            var edges = new JArray();
            var unresolved = new JArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in analysis.References)
            {
                if (!IsNativeCall(reference.Access)) continue;
                var actor = reference.Actor;
                var subject = reference.Subject;
                if (!OfflineEvidence.IsCallRelation(reference.Relation.ToString()) || actor == null || subject == null || actor.BlockPath == null || subject.BlockPath == null)
                {
                    unresolved.Add(new JObject { ["reason"] = actor == null ? "Native relation has no supported direction." : "A native call endpoint has no resolvable engineering block object.",
                        ["evidence"] = ReferenceJson(reference) });
                    continue;
                }
                string callee = subject.BlockPath;
                string instancePath = null;
                string instanceResolution = "Native engineering block identity";
                BlockEntry target;
                if (analysis.Blocks.TryGetValue(callee, out target) && target.Item is InstanceDB instance)
                {
                    instancePath = callee;
                    // Only direct DB/FB native identities qualify this relation.
                    // A member's owner BlockPath cannot stand in for the member itself.
                    var nativeTypePaths = analysis.References.Where(item =>
                        (item.Relation == ReferenceType.InstanceType || item.Relation == ReferenceType.TypeInstance) &&
                        item.Actor?.Key == instancePath && item.Subject?.Key == item.Subject?.BlockPath && item.Subject?.BlockPath != null &&
                        analysis.Blocks.TryGetValue(item.Subject.BlockPath, out var declaredBlock) && declaredBlock.Item is FB)
                        .Select(item => item.Subject.BlockPath);
                    var resolution = OfflineEvidence.ResolveInstanceTarget(analysis.Plc.Path, instancePath, instance.Namespace,
                        instance.InstanceOfName, analysis.IndexTruncated,
                        analysis.Blocks.Values.Where(block => block.Item is FB).Select(block => new OfflineEvidence.InstanceTarget
                            { Path = block.Path, Name = block.Item.Name, Namespace = block.Item.Namespace }), nativeTypePaths);
                    if (resolution.Path == null)
                    {
                        unresolved.Add(new JObject { ["reason"] = resolution.Reason,
                            ["instancePath"] = callee, ["instanceOfName"] = instance.InstanceOfName, ["instanceNamespace"] = instance.Namespace,
                            ["indexTruncated"] = analysis.IndexTruncated, ["evidence"] = ReferenceJson(reference) });
                        continue;
                    }
                    callee = resolution.Path;
                    instanceResolution = resolution.Reason;
                }
                string key = actor.BlockPath + "\n" + callee + "\n" + instancePath + "\n" + reference.Location;
                if (!seen.Add(key)) continue;
                edges.Add(new JObject { ["callerPath"] = actor.BlockPath, ["calleePath"] = callee, ["instancePath"] = instancePath,
                    ["classification"] = "nativeCall", ["access"] = reference.Access.ToString(),
                    ["instanceResolution"] = instanceResolution,
                    ["evidence"] = ReferenceJson(reference) });
            }
            var result = AnalysisEnvelope(analysis);
            result["nodes"] = new JArray(analysis.Blocks.Values.Select(block => BlockInfo(block, analysis.Plc.Path)));
            result["edges"] = edges;
            result["unresolvedCalls"] = unresolved;
            result["obRoots"] = new JArray(analysis.Blocks.Values.Where(block => block.Item is OB).Select(block => new JObject
            { ["path"] = block.Path, ["name"] = block.Item.Name, ["number"] = block.Item.Number, ["reachableBlockPaths"] = ReachableCalls(block.Path, edges, analysis.MaxItems) }));
            result["instanceRelationships"] = new JArray(analysis.References.Where(reference => reference.Relation == ReferenceType.InstanceType || reference.Relation == ReferenceType.TypeInstance || IsInstanceAccess(reference.Access)).Select(ReferenceJson));
            result["xmlEvidence"] = ExportAnalysisEvidence(args, analysis, edges.SelectMany(edge => new[] { edge.Value<string>("callerPath"), edge.Value<string>("calleePath"), edge.Value<string>("instancePath") }));
            result["completeness"] = AnalysisCoverage(analysis, "Call edges require native Call, UC or CC access plus a directional Uses/UsedBy relation and resolved block identity.");
            result["completeness"]["unresolvedCallCount"] = unresolved.Count;
            if (unresolved.Count > 0) result["completeness"]["scopeComplete"] = false;
            return result;
        }

        private JObject SymbolTrace(JObject args)
        {
            string symbol = RequiredSymbol(args);
            var analysis = ReadNativeAnalysis(args);
            var matches = analysis.Identities.Values.Where(identity => SymbolMatches(identity, symbol)).ToArray();
            var matching = new HashSet<string>(matches.Select(identity => identity.Key), StringComparer.Ordinal);
            var references = analysis.References.Where(reference => matching.Contains(reference.Source.Key) || matching.Contains(reference.Target.Key)).ToArray();
            var reads = references.Where(reference => reference.Subject != null && matching.Contains(reference.Subject.Key) && IsNativeRead(reference.Access)).ToArray();
            var writes = references.Where(reference => reference.Subject != null && matching.Contains(reference.Subject.Key) && IsNativeWrite(reference.Access)).ToArray();
            var result = AnalysisEnvelope(analysis);
            result["query"] = symbol;
            result["matches"] = new JArray(matches.Select(IdentityJson));
            result["queryResolution"] = matches.Length == 0 ? "unresolvedOrAbsent" : matches.Length == 1 ? "exactNativeMatch" : "multipleNativeMatches";
            result["resolutionNote"] = matches.Length == 0 ? "No matching exposed native identity was found; this does not prove the symbol is unused or resolve indirect access." :
                "Matches retain native owner and instance boundaries. Runtime propagation from an FB declaration to every instance is not inferred.";
            result["readers"] = new JArray(reads.Select(reference => new JObject { ["actor"] = IdentityJson(reference.Actor), ["evidence"] = ReferenceJson(reference) }));
            result["writers"] = new JArray(writes.Select(reference => new JObject { ["actor"] = IdentityJson(reference.Actor), ["evidence"] = ReferenceJson(reference) }));
            result["callers"] = new JArray(references.Where(reference => reference.Subject != null && matching.Contains(reference.Subject.Key) && IsNativeCall(reference.Access))
                .Select(reference => new JObject { ["actor"] = IdentityJson(reference.Actor), ["evidence"] = ReferenceJson(reference) }));
            result["instanceReferences"] = new JArray(references.Where(reference => IsInstanceAccess(reference.Access)).Select(ReferenceJson));
            result["references"] = new JArray(references.Select(ReferenceJson));
            result["unknownOrIndirect"] = new JArray(references.Where(reference => reference.Access == Access.Unknown || reference.Access == Access.Undefined || reference.Actor == null ||
                reference.Access == Access.CreateReference || reference.Access == Access.CreateReferenceAndSymbol || reference.Access == Access.Parameterinstance || reference.Access == Access.ParameterinstanceAndSymbol).Select(ReferenceJson));
            result["hardwareAddressMatches"] = MatchHardwareAddresses(analysis, matches.Select(identity => identity.Address).Concat(new[] { symbol }));
            result["xmlEvidence"] = ExportAnalysisEvidence(args, analysis, references.SelectMany(reference => new[] { reference.Source.BlockPath, reference.Target.BlockPath }));
            result["completeness"] = AnalysisCoverage(analysis, "Exact native name/address/path or canonical block-path match. Readers/writers use native access and Uses/UsedBy direction; ambiguous names retain all matches.");
            return result;
        }

        private JObject ChangeImpact(JObject args)
        {
            string symbol = RequiredSymbol(args);
            var analysis = ReadNativeAnalysis(args);
            var seeds = analysis.Identities.Values.Where(identity => SymbolMatches(identity, symbol)).ToArray();
            var visited = new Dictionary<string, int>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            foreach (var seed in seeds) { visited[seed.Key] = 0; queue.Enqueue(seed.Key); }
            var impacted = new JArray();
            var evidencePaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var seed in seeds) if (seed.BlockPath != null) evidencePaths.Add(seed.BlockPath);
            int maximumDepth = args.Value<int?>("maxDepth") ?? 32;
            if (maximumDepth < 1 || maximumDepth > 64) throw new BridgeException("InvalidRequest", "maxDepth must be between 1 and 64.");
            bool depthBound = false;
            while (queue.Count > 0)
            {
                string changed = queue.Dequeue();
                int depth = visited[changed];
                var incoming = analysis.References.Where(reference => reference.Actor != null && reference.Subject != null && reference.Subject.Key == changed).ToArray();
                if (depth >= maximumDepth) { if (incoming.Any(reference => !visited.ContainsKey(reference.Actor.Key))) depthBound = true; continue; }
                foreach (var reference in incoming)
                {
                    if (visited.ContainsKey(reference.Actor.Key)) continue;
                    if (impacted.Count >= analysis.MaxItems) { analysis.Budget.Truncated = true; break; }
                    visited[reference.Actor.Key] = depth + 1;
                    queue.Enqueue(reference.Actor.Key);
                    impacted.Add(new JObject { ["object"] = IdentityJson(reference.Actor), ["distance"] = depth + 1,
                        ["dependsOn"] = changed, ["evidence"] = ReferenceJson(reference), ["interpretation"] = "Static native dependency; runtime effect and execution are not proven." });
                    if (reference.Actor.BlockPath != null) evidencePaths.Add(reference.Actor.BlockPath);
                }
                if (impacted.Count >= analysis.MaxItems) break;
            }
            var result = AnalysisEnvelope(analysis);
            result["query"] = symbol;
            result["seeds"] = new JArray(seeds.Select(IdentityJson));
            result["potentiallyAffected"] = impacted;
            result["affectedObPaths"] = new JArray(impacted.Select(row => row["object"].Value<string>("blockPath")).Where(path => path != null && analysis.Blocks.ContainsKey(path) && analysis.Blocks[path].Item is OB).Distinct());
            result["maxDepth"] = maximumDepth;
            result["depthBoundReached"] = depthBound;
            result["xmlEvidence"] = ExportAnalysisEvidence(args, analysis, evidencePaths);
            result["completeness"] = AnalysisCoverage(analysis, "Reverse closure of native Uses/UsedBy and instance/type dependencies, with one evidence chain per reached object.");
            if (depthBound) result["completeness"]["scopeComplete"] = false;
            return result;
        }

        private NativeAnalysis ReadNativeAnalysis(JObject args)
        {
            var plc = SelectInspectionPlc(args, "blockPath");
            int max = Limit(args, 500);
            var analysis = new NativeAnalysis { Plc = plc, MaxItems = max, Budget = new ScanBudget(max) };
            var indexing = new ScanBudget(Math.Min(20000, Math.Max(1000, max * 4)));
            foreach (var block in AllBlocks(plc, indexing)) { analysis.Blocks[block.Path] = block; analysis.BlockIdentities[block.Item] = block; }
            if (indexing.Truncated) analysis.Warnings.Add("Block identity index reached its item bound; unresolved endpoints remain explicit.");
            analysis.IndexTruncated = indexing.Truncated;
            CrossReferenceService service;
            if (!string.IsNullOrWhiteSpace(args.Value<string>("blockPath")) || !string.IsNullOrWhiteSpace(args.Value<string>("blockName")))
            {
                var block = SelectBlock(args);
                service = block.Block.Item.GetService<CrossReferenceService>();
                analysis.Scope = block.Block.Path;
            }
            else
            {
                service = plc.Software.GetService<CrossReferenceService>();
                analysis.Scope = plc.Path;
            }
            if (service != null)
            {
                analysis.QueryStrategy = "Native service on selected target";
                var native = service.GetCrossReferences(CrossReferenceFilter.AllObjects);
                AddAnalysisSources(native.Sources, analysis, 0);
            }
            else if (analysis.Scope == plc.Path)
            {
                // V21 exposes this service on program blocks for CPU configurations
                // where PlcSoftware itself has no CrossReferenceService.
                analysis.QueryStrategy = "Native per-block services; one shared traversal bound and canonical evidence deduplication";
                foreach (var block in analysis.Blocks.Values)
                {
                    if (!analysis.Budget.Take()) break;
                    try
                    {
                        var blockService = block.Item.GetService<CrossReferenceService>();
                        if (blockService == null)
                        {
                            analysis.Warnings.Add(new JObject { ["blockPath"] = block.Path, ["code"] = "UnsupportedCapability", ["message"] = "No native cross-reference service on this block." });
                            continue;
                        }
                        var native = blockService.GetCrossReferences(CrossReferenceFilter.AllObjects);
                        analysis.QueriedBlocks++;
                        AddAnalysisSources(native.Sources, analysis, 0);
                    }
                    catch (Exception exception) { analysis.Warnings.Add(InspectionWarning(block.Path, exception)); }
                    if (analysis.Budget.Truncated) break;
                }
            }
            else throw new BridgeException("UnsupportedCapability", "Native CrossReferenceService is unavailable for this target.");
            return analysis;
        }

        private static void AddAnalysisSources(IEnumerable<SourceObject> sources, NativeAnalysis analysis, int depth)
        {
            if (depth > 64) { analysis.Budget.Truncated = true; return; }
            foreach (var source in sources)
            {
                if (!analysis.Budget.Take()) return;
                var from = NativeIdentity(source.Name, source.Path, source.Address, source.Device, source.TypeName, source.UnderlyingObject, analysis);
                foreach (var target in source.References)
                {
                    if (!analysis.Budget.Take()) return;
                    var to = NativeIdentity(target.Name, target.Path, target.Address, target.Device, target.TypeName, target.UnderlyingObject, analysis);
                    foreach (var location in target.Locations)
                    {
                        if (!analysis.Budget.Take()) return;
                        var reference = new NativeReference { Id = "xref-" + analysis.References.Count.ToString(CultureInfo.InvariantCulture),
                            Source = from, Target = to, Access = location.Access, Relation = location.ReferenceType,
                            Location = location.ReferenceLocation, LocationName = location.Name, LocationAddress = location.Address,
                            ReferencedAsName = location.ReferencedAsName, HasReferencedAsObject = location.ReferencedAs != null };
                        int direction = OfflineEvidence.DependencyDirection(reference.Relation.ToString());
                        if (direction > 0)
                        { reference.Actor = from; reference.Subject = to; }
                        else if (direction < 0)
                        { reference.Actor = to; reference.Subject = from; }
                        string evidenceKey = (reference.Actor == null ? from.Key + "\n" + to.Key + "\n" + reference.Relation : reference.Actor.Key + "\n" + reference.Subject.Key) +
                            "\n" + reference.Access + "\n" + reference.Location + "\n" + reference.LocationAddress + "\n" + reference.ReferencedAsName;
                        if (analysis.ReferenceKeys.Add(evidenceKey)) analysis.References.Add(reference);
                    }
                }
                AddAnalysisSources(source.Children, analysis, depth + 1);
                if (analysis.Budget.Truncated) return;
            }
        }

        private static AnalysisIdentity NativeIdentity(string name, string path, string address, string device, string typeName, IEngineeringObject underlying, NativeAnalysis analysis)
        {
            var owner = OwnerBlock(underlying);
            BlockEntry block = null;
            if (owner != null) analysis.BlockIdentities.TryGetValue(owner, out block);
            string key = underlying is PlcBlock && block != null ? block.Path :
                OfflineEvidence.NativeMemberKey(block?.Path, device, path, name, address, typeName);
            AnalysisIdentity existing;
            if (analysis.Identities.TryGetValue(key, out existing)) return existing;
            var identity = new AnalysisIdentity { Key = key, Name = name, NativePath = path, Address = address, Device = device,
                TypeName = typeName, BlockPath = block?.Path, HasEngineeringObject = underlying != null, EngineeringObject = underlying };
            analysis.Identities.Add(key, identity);
            return identity;
        }

        private static PlcBlock OwnerBlock(IEngineeringObject item)
        {
            IEngineeringInstance current = item;
            for (int depth = 0; current != null && depth < 64; depth++)
            {
                if (current is PlcBlock block) return block;
                current = current.Parent;
            }
            return null;
        }

        private JObject AnalysisEnvelope(NativeAnalysis analysis)
        {
            var result = Envelope();
            result["project"] = ProjectInfo(analysis.Plc.Project);
            result["plcPath"] = analysis.Plc.Path;
            result["targetPath"] = analysis.Scope;
            result["queryStrategy"] = analysis.QueryStrategy;
            result["queriedBlocks"] = analysis.QueriedBlocks;
            result["warnings"] = analysis.Warnings;
            return result;
        }

        private static JObject AnalysisCoverage(NativeAnalysis analysis, string scope) => new JObject
        {
            ["complete"] = false, ["scopeComplete"] = !analysis.Budget.Truncated && !analysis.IndexTruncated && analysis.Warnings.Count == 0,
            ["truncated"] = analysis.Budget.Truncated, ["indexTruncated"] = analysis.IndexTruncated,
            ["maxItems"] = analysis.MaxItems, ["visitedNativeItems"] = analysis.Budget.Used, ["scope"] = scope,
            ["gaps"] = new JArray("Indirect calls, ANY/pointer targets and runtime-selected DB/array instances are not resolved", "No dataflow/interlock evaluation or runtime execution proof",
                "No transactional snapshot; TIA edits can invalidate this observation", "Native cross references can depend on compilation and object consistency", "Partial XML evidence has its own per-block export status")
        };

        private static JObject IdentityJson(AnalysisIdentity identity) => identity == null ? null : new JObject
        { ["key"] = identity.Key, ["name"] = identity.Name, ["nativePath"] = identity.NativePath, ["address"] = identity.Address,
            ["device"] = identity.Device, ["typeName"] = identity.TypeName, ["blockPath"] = identity.BlockPath, ["hasEngineeringObject"] = identity.HasEngineeringObject };

        private static JObject ReferenceJson(NativeReference reference) => new JObject
        { ["id"] = reference.Id, ["source"] = IdentityJson(reference.Source), ["target"] = IdentityJson(reference.Target),
            ["access"] = reference.Access.ToString(), ["referenceType"] = reference.Relation.ToString(),
            ["referenceLocation"] = reference.Location, ["locationName"] = reference.LocationName, ["locationAddress"] = reference.LocationAddress,
            ["referencedAsName"] = reference.ReferencedAsName, ["hasReferencedAsObject"] = reference.HasReferencedAsObject,
            ["directionResolved"] = reference.Actor != null };

        private static bool IsNativeCall(Access access) => OfflineEvidence.IsCallAccess(access.ToString());
        private static bool IsNativeRead(Access access) => OfflineEvidence.IsReadAccess(access.ToString());
        private static bool IsNativeWrite(Access access) => OfflineEvidence.IsWriteAccess(access.ToString());
        private static bool IsInstanceAccess(Access access) => access == Access.InstanceDB || access == Access.Multiinstance || access == Access.InstanceAndSymbol || access == Access.MultiinstanceAndSymbol;

        private static string RequiredSymbol(JObject args)
        {
            string symbol = args.Value<string>("symbol");
            if (string.IsNullOrWhiteSpace(symbol) || symbol.Length > 2048) throw new BridgeException("InvalidRequest", "symbol must be a native name/address/path or canonical block path of 1–2048 characters.");
            return symbol;
        }

        private static bool SymbolMatches(AnalysisIdentity identity, string symbol) =>
            OfflineEvidence.NativeSymbolMatches(symbol, identity.Key, identity.Name, identity.Address, identity.NativePath, identity.BlockPath);

        private static JArray ReachableCalls(string root, JArray edges, int maximum)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { root };
            var queue = new Queue<string>();
            queue.Enqueue(root);
            while (queue.Count > 0 && visited.Count <= maximum)
            {
                string current = queue.Dequeue();
                foreach (var edge in edges.Where(edge => edge.Value<string>("callerPath") == current))
                {
                    string callee = edge.Value<string>("calleePath");
                    if (visited.Add(callee)) queue.Enqueue(callee);
                }
            }
            return new JArray(visited.Where(path => path != root));
        }

        private JArray ExportAnalysisEvidence(JObject args, NativeAnalysis analysis, IEnumerable<string> blockPaths)
        {
            var result = new JArray();
            if (args.Value<bool?>("includeXmlEvidence") != true) return result;
            int maximum = args.Value<int?>("maxXmlBlocks") ?? 10;
            if (maximum < 1 || maximum > 50) throw new BridgeException("InvalidRequest", "maxXmlBlocks must be between 1 and 50.");
            var paths = blockPaths.Where(path => path != null).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var path in paths.Take(maximum))
            {
                var entry = new JObject { ["blockPath"] = path };
                result.Add(entry);
                BlockEntry block;
                if (!analysis.Blocks.TryGetValue(path, out block)) { entry["status"] = "unresolved"; continue; }
                if (block.Item.IsKnowHowProtected) { entry["status"] = "protected"; continue; }
                try
                {
                    var file = NewInspectionArtifact(args, "analysis-evidence");
                    block.Item.Export(file, ExportOptions.WithDefaults);
                    entry["status"] = "exported";
                    entry["artifact"] = FileArtifact(file);
                    entry["evidenceKind"] = "Unchanged native XML; graph edges come from native cross references, never textual call guesses.";
                }
                catch (Exception exception) { entry["status"] = "unavailable"; entry["error"] = exception.Message; analysis.Warnings.Add(InspectionWarning(path, exception)); }
            }
            if (paths.Length > maximum) result.Add(new JObject { ["status"] = "bounded", ["omittedBlocks"] = paths.Length - maximum, ["maxXmlBlocks"] = maximum });
            return result;
        }

        private JArray MatchHardwareAddresses(NativeAnalysis analysis, IEnumerable<string> addresses, string hardwarePath = null)
        {
            var requested = addresses.Where(address => !string.IsNullOrWhiteSpace(address)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(OfflineEvidence.ParseIoAddress).Where(address => address != null).ToArray();
            var result = new JArray();
            if (requested.Length == 0) return result;
            var budget = new ScanBudget(analysis.MaxItems);
            foreach (var entry in DeviceItems(analysis.Plc.Project, budget))
            {
                if (hardwarePath != null && entry.Path != hardwarePath) continue;
                try
                {
                    foreach (var range in entry.Item.Addresses)
                    {
                        if (!budget.Take()) break;
                        foreach (var address in requested)
                        {
                            string io = range.IoType.ToString();
                            if (io.IndexOf(address.Direction, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (!OfflineEvidence.IoAddressOverlapsNativeRange(address, range.StartAddress, range.Length)) continue;
                            string ownership = OfflineEvidence.IoOwnershipResolution(range.AddressControllers
                                .Select(controller => ControllerMatchesSelectedPlc(controller, analysis.Plc.Software)));
                            if (ownership == "otherPlc") continue;
                            bool ownerVerified = ownership == "verified";
                            if (result.Count >= analysis.MaxItems) { budget.Truncated = true; break; }
                            result.Add(new JObject { ["queryAddress"] = address.Original, ["hardwarePath"] = entry.Path, ["hardwareName"] = entry.Item.Name,
                                ["ioType"] = io, ["startAddress"] = range.StartAddress, ["length"] = range.Length,
                                ["lengthUnit"] = "bits", ["lengthBytes"] = OfflineEvidence.IoBitLengthToByteLength(range.Length),
                                ["plcOwnershipVerified"] = ownerVerified, ["ownershipResolution"] = ownership,
                                ["match"] = ownerVerified ? "Configured address range overlap in the selected PLC; individual channel and electrical terminal are not inferred." :
                                    "Potential configured address overlap with unresolved PLC ownership; association to the selected PLC is not established." });
                            if (!ownerVerified) analysis.Warnings.Add(new JObject { ["hardwarePath"] = entry.Path, ["queryAddress"] = address.Original,
                                ["code"] = "UnresolvedIoOwnership", ["message"] = "Address overlap was retained as a candidate because its controller ownership could not be resolved." });
                        }
                        if (budget.Truncated) break;
                    }
                }
                catch (Exception exception) { analysis.Warnings.Add(InspectionWarning(entry.Path + "/addresses", exception)); }
                if (budget.Truncated) break;
            }
            if (budget.Truncated) analysis.Warnings.Add("Hardware address matching reached its traversal limit; additional matches may exist.");
            analysis.HardwareVisited += budget.Used;
            return result;
        }

        private sealed class NativeAnalysis
        {
            internal PlcSelection Plc;
            internal int MaxItems;
            internal ScanBudget Budget;
            internal bool IndexTruncated;
            internal string Scope, QueryStrategy;
            internal int QueriedBlocks;
            internal int HardwareVisited;
            internal readonly Dictionary<string, BlockEntry> Blocks = new Dictionary<string, BlockEntry>(StringComparer.Ordinal);
            internal readonly Dictionary<PlcBlock, BlockEntry> BlockIdentities = new Dictionary<PlcBlock, BlockEntry>();
            internal readonly Dictionary<string, AnalysisIdentity> Identities = new Dictionary<string, AnalysisIdentity>(StringComparer.Ordinal);
            internal readonly List<NativeReference> References = new List<NativeReference>();
            internal readonly HashSet<string> ReferenceKeys = new HashSet<string>(StringComparer.Ordinal);
            internal readonly JArray Warnings = new JArray();
        }
        private sealed class AnalysisIdentity { internal string Key, Name, NativePath, Address, Device, TypeName, BlockPath; internal bool HasEngineeringObject; internal IEngineeringObject EngineeringObject; }
        private sealed class NativeReference
        {
            internal string Id, Location, LocationName, LocationAddress, ReferencedAsName;
            internal bool HasReferencedAsObject;
            internal AnalysisIdentity Source, Target, Actor, Subject;
            internal Access Access;
            internal ReferenceType Relation;
        }
    }
}
