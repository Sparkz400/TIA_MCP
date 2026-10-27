using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Analysis;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Static storage/call/binding evidence for one exact FB or FC. Every row names its source
        // (native xref, exported interface or exported network code); runtime call context is never proven.
        private JObject InstanceLookup(JObject args)
        {
            if (string.IsNullOrWhiteSpace(args.Value<string>("blockPath")) && string.IsNullOrWhiteSpace(args.Value<string>("blockName")))
                throw new BridgeException("InvalidRequest", "An exact FB or FC blockPath or blockName is required.");
            int max = Limit(args, 500), maxDepth = args.Value<int?>("maxDepth") ?? 8, maxXml = args.Value<int?>("maxXmlBlocks") ?? 20;
            if (maxDepth < 1 || maxDepth > 8) throw new BridgeException("InvalidRequest", "maxDepth must be between 1 and 8.");
            if (maxXml < 1 || maxXml > 50) throw new BridgeException("InvalidRequest", "maxXmlBlocks must be between 1 and 50.");
            var query = (JObject)args.DeepClone(); query["maxItems"] = 2000; // the shared reader's admitted native scan budget
            var analysis = ReadNativeAnalysis(query);
            if (!analysis.Blocks.TryGetValue(analysis.Scope, out var target))
                throw new BridgeException("TraversalLimit", "The selected block is outside the bounded block identity index.");
            bool isFb = target.Item is FB;
            if (!isFb && !(target.Item is FC)) throw new BridgeException("InvalidRequest", "instance_lookup requires an exact FB or FC.");
            string plcPath = analysis.Plc.Path, targetName = QualifiedName(target.Item);
            var rows = new ScanBudget(max); var parse = new ScanBudget(50000);
            var instances = new JArray(); var declarations = new JArray(); var callSites = new JArray(); var operands = new JArray(); var unresolved = new JArray(); var exports = new JArray();
            bool AddRow(JArray array, JObject row) { if (!rows.Take()) return false; array.Add(row); return true; }

            var queried = new HashSet<string>(StringComparer.Ordinal) { target.Path };
            void Xref(BlockEntry block)
            {
                if (!queried.Add(block.Path) || analysis.Budget.Truncated) return;
                try
                {
                    var service = block.Item.GetService<CrossReferenceService>();
                    if (service == null) { analysis.Warnings.Add(new JObject { ["blockPath"] = block.Path, ["code"] = "UnsupportedCapability", ["message"] = "No native cross-reference service on this block." }); return; }
                    AddAnalysisSources(service.GetCrossReferences(CrossReferenceFilter.AllObjects).Sources, analysis, 0);
                    analysis.QueriedBlocks++;
                }
                catch (Exception exception) { analysis.Warnings.Add(InspectionWarning(block.Path, exception)); }
            }
            var documents = new Dictionary<string, XDocument>(StringComparer.Ordinal); int exported = 0, omitted = 0;
            XDocument Export(BlockEntry block)
            {
                if (documents.TryGetValue(block.Path, out var cached)) return cached;
                documents[block.Path] = null;
                if (block.Item.IsKnowHowProtected) { exports.Add(new JObject { ["blockPath"] = block.Path, ["status"] = "protected" }); return null; }
                if (exported >= maxXml) { omitted++; return null; }
                try
                {
                    var file = NewInspectionArtifact(args, "instance-lookup");
                    block.Item.Export(file, ExportOptions.WithDefaults);
                    exported++;
                    XDocument document;
                    using (var reader = XmlReader.Create(file.FullName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64L * 1024 * 1024 }))
                        document = XDocument.Load(reader, LoadOptions.SetLineInfo);
                    exports.Add(new JObject { ["blockPath"] = block.Path, ["status"] = "exported", ["programmingLanguage"] = block.Item.ProgrammingLanguage.ToString(), ["artifact"] = FileArtifact(file) });
                    return documents[block.Path] = document;
                }
                catch (Exception exception)
                {
                    exports.Add(new JObject { ["blockPath"] = block.Path, ["status"] = "unavailable", ["error"] = exception.Message });
                    analysis.Warnings.Add(InspectionWarning(block.Path, exception)); return null;
                }
            }

            // Storage: walk from the FB outwards through enclosing FB static declarations to root instance DBs.
            var fbs = analysis.Blocks.Values.Where(b => b.Item is FB).Select(b => new OfflineEvidence.InstanceTarget { Path = b.Path, Name = b.Item.Name, Namespace = b.Item.Namespace }).ToArray();
            var roots = new List<Tuple<BlockEntry, string[]>>(); var targetDbs = new HashSet<string>(StringComparer.Ordinal);
            bool depthBound = false;
            var queue = new Queue<LookupLevel>();
            if (isFb) queue.Enqueue(new LookupLevel { Type = target, Suffix = new string[0], Chain = new JArray() });
            while (queue.Count > 0 && !rows.Truncated)
            {
                var level = queue.Dequeue();
                Xref(level.Type);
                string typeName = QualifiedName(level.Type.Item);
                int found = 0;
                var typed = new HashSet<string>(analysis.References.Where(r => (r.Relation == ReferenceType.InstanceType || r.Relation == ReferenceType.TypeInstance)
                    && r.Subject?.BlockPath == level.Type.Path && r.Actor != null).Select(r => r.Actor.Key), StringComparer.Ordinal);
                foreach (var db in analysis.Blocks.Values.Where(b => b.Item is InstanceDB).ToArray())
                {
                    var idb = (InstanceDB)db.Item;
                    if (!typed.Contains(db.Path) && idb.InstanceOfName != level.Type.Item.Name && idb.InstanceOfName != typeName) continue;
                    Xref(db);
                    var native = analysis.References.Where(r => (r.Relation == ReferenceType.InstanceType || r.Relation == ReferenceType.TypeInstance) && r.Actor?.Key == db.Path
                        && r.Subject?.BlockPath != null && r.Subject.Key == r.Subject.BlockPath).Select(r => r.Subject.BlockPath);
                    var resolved = OfflineEvidence.ResolveInstanceTarget(plcPath, db.Path, idb.Namespace, idb.InstanceOfName, analysis.IndexTruncated, fbs, native);
                    if (resolved.Path != level.Type.Path)
                    {
                        AddRow(unresolved, new JObject { ["dbPath"] = db.Path, ["typePath"] = level.Type.Path, ["memberPath"] = new JArray(level.Suffix), ["reason"] = resolved.Reason });
                        continue;
                    }
                    found++; roots.Add(Tuple.Create(db, level.Suffix));
                    if (level.Depth == 0) targetDbs.Add(db.Path);
                    bool array = level.Chain.Any(link => link["arrayBounds"]?.Type == JTokenType.Array);
                    AddRow(instances, new JObject { ["kind"] = level.Depth == 0 ? "singleInstanceDb" : array ? "multiInstanceArray" : "multiInstance",
                        ["dbPath"] = db.Path, ["memberPath"] = new JArray(level.Suffix), ["typeChain"] = level.Chain.DeepClone(),
                        ["source"] = resolved.NativeRelation ? "nativeXref" : "interface", ["resolution"] = resolved.Reason,
                        // Same shape as diagnostic_plan's instance selector; the planner re-resolves it against its saved review.
                        ["diagnosticInstanceSelector"] = array ? null : new JObject { ["dbPath"] = db.Path, ["memberPath"] = new JArray(level.Suffix) },
                        ["runtimeCallContextVerified"] = false });
                }
                var owners = analysis.References.Where(r => r.Subject?.BlockPath == level.Type.Path && r.Actor?.BlockPath != null && r.Actor.BlockPath != level.Type.Path)
                    .Select(r => r.Actor.BlockPath).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)
                    .Select(p => analysis.Blocks.TryGetValue(p, out var b) ? b : null).Where(b => b?.Item is FB).ToArray();
                foreach (var owner in owners)
                {
                    if (level.Chain.Any(link => link.Value<string>("ownerPath") == owner.Path)) continue;
                    var document = Export(owner);
                    if (document == null)
                    {
                        AddRow(unresolved, new JObject { ["ownerPath"] = owner.Path, ["typePath"] = level.Type.Path, ["reason"] = "Candidate owner is protected, unavailable or beyond maxXmlBlocks; its static declarations were not read." });
                        continue;
                    }
                    bool sameScope = ObjectIdentity.ScopePath(plcPath, owner.Path) == ObjectIdentity.ScopePath(plcPath, level.Type.Path);
                    foreach (var member in OfflineEvidence.NormalizeInterface(document, parse.Take, () => parse.Truncated = true).Sections.Where(s => s.Name == "Static").SelectMany(s => s.Members))
                    {
                        if ((member.Array?.ElementType ?? member.DataType ?? "").Trim().Trim('"') != typeName) continue;
                        var bounds = member.Array == null ? null : new JArray(member.Array.Dimensions.Select(d => new JObject { ["declaration"] = d.Declaration, ["lower"] = d.Lower, ["upper"] = d.Upper }));
                        AddRow(declarations, new JObject { ["ownerPath"] = owner.Path, ["member"] = member.Name, ["dataType"] = member.DataType, ["typePath"] = level.Type.Path,
                            ["arrayBounds"] = bounds, ["boundsResolved"] = member.Array?.BoundsResolved, ["xmlLine"] = member.XmlLine, ["source"] = "interface",
                            ["typeResolution"] = sameScope ? "exactQualifiedNameSameScope" : "unresolvedOtherScope" });
                        if (!sameScope) continue;
                        if (level.Depth + 1 > maxDepth) { depthBound = true; continue; }
                        var chain = new JArray(new JObject { ["ownerPath"] = owner.Path, ["member"] = member.Name, ["typePath"] = level.Type.Path, ["arrayBounds"] = bounds?.DeepClone() });
                        foreach (var link in level.Chain) chain.Add(link.DeepClone());
                        queue.Enqueue(new LookupLevel { Type = owner, Suffix = new[] { member.Name }.Concat(level.Suffix).ToArray(), Chain = chain, Depth = level.Depth + 1 });
                        found++;
                    }
                }
                if (level.Depth > 0 && found == 0)
                    AddRow(unresolved, new JObject { ["typeChain"] = level.Chain.DeepClone(), ["reason"] = "No resolved instance DB or enclosing multi-instance holds this declaration chain." });
            }

            // Call sites: native xref first, then exported caller code for network, instance and actual parameters.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in analysis.References.Where(r => IsNativeCall(r.Access) && r.Actor?.BlockPath != null && r.Subject?.BlockPath != null && (r.Subject.BlockPath == target.Path || targetDbs.Contains(r.Subject.BlockPath))))
            {
                if (!seen.Add(r.Actor.BlockPath + "\n" + r.Subject.BlockPath + "\n" + r.Location + "\n" + r.Access)) continue;
                if (!AddRow(callSites, new JObject { ["source"] = "nativeXref", ["callerPath"] = r.Actor.BlockPath, ["calleePath"] = target.Path,
                    ["instanceDbPath"] = targetDbs.Contains(r.Subject.BlockPath) ? r.Subject.BlockPath : null, ["access"] = r.Access.ToString(),
                    ["referenceLocation"] = r.Location, ["locationName"] = r.LocationName, ["evidenceId"] = r.Id })) break;
            }
            foreach (var r in analysis.References.Where(r => r.Subject?.BlockPath != null && r.Actor?.BlockPath != null && r.Actor.BlockPath != r.Subject.BlockPath
                && roots.Any(root => root.Item1.Path == r.Subject.BlockPath) && !IsNativeCall(r.Access) && r.Relation != ReferenceType.InstanceType && r.Relation != ReferenceType.TypeInstance))
                if (!AddRow(operands, new JObject { ["source"] = "nativeXref", ["blockPath"] = r.Actor.BlockPath, ["dbPath"] = r.Subject.BlockPath, ["name"] = r.Subject.Name,
                    ["nativePath"] = r.Subject.NativePath, ["address"] = r.Subject.Address, ["access"] = r.Access.ToString(), ["languageHint"] = LookupLanguageHint(r.Access),
                    ["referenceLocation"] = r.Location, ["locationName"] = r.LocationName, ["evidenceId"] = r.Id })) break;

            var directDeclarations = declarations.OfType<JObject>().Where(d => d.Value<string>("typePath") == target.Path && d.Value<string>("typeResolution") == "exactQualifiedNameSameScope").ToArray();
            var targetDbNames = targetDbs.Select(p => roots.First(root => root.Item1.Path == p).Item1).GroupBy(b => QualifiedName(b.Item), StringComparer.Ordinal)
                .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Path, StringComparer.Ordinal);
            var languages = new Dictionary<string, int>(StringComparer.Ordinal) { ["SCL"] = 0, ["LAD"] = 0, ["FBD"] = 0, ["STL"] = 0, ["GRAPH"] = 0 };
            var scan = callSites.Select(c => c.Value<string>("callerPath")).Concat(operands.Select(o => o.Value<string>("blockPath")))
                .Concat(directDeclarations.Select(d => d.Value<string>("ownerPath"))).Where(p => p != target.Path)
                .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => analysis.Blocks.TryGetValue(p, out var b) ? b : null).Where(b => b != null && !(b.Item is DataBlock)).ToArray();
            bool sameTargetScope(BlockEntry block) => ObjectIdentity.ScopePath(plcPath, block.Path) == ObjectIdentity.ScopePath(plcPath, target.Path);
            foreach (var block in scan)
            {
                if (rows.Truncated) break;
                var document = Export(block); if (document == null) continue;
                try
                {
                    var units = document.Descendants("SW.Blocks.CompileUnit").ToArray();
                    string blockLanguage = block.Item.ProgrammingLanguage.ToString();
                    // Unverified export shapes (e.g. GRAPH) may hold code outside compile units; never let that pass as scanned.
                    if (!units.Any(u => (u.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value ?? blockLanguage) == blockLanguage))
                        analysis.Warnings.Add(new JObject { ["blockPath"] = block.Path, ["code"] = "UnscannedContent", ["message"] = "No compile unit carries the block language " + blockLanguage + "; content outside compile units was not searched." });
                    foreach (var call in EngineeringSources.ReadNativeCalls(document))
                    {
                        if (call.BlockType != (isFb ? "FB" : "FC") || (call.Name != target.Item.Name && call.Name != targetName)) continue;
                        string language = call.Language ?? blockLanguage, support = LookupSupport(language);
                        if (support == "unsupported") continue;
                        var components = call.InstanceComponents ?? new string[0];
                        string dbPath = call.InstanceScope == "GlobalVariable" && targetDbNames.TryGetValue(string.Join(".", components), out var path) ? path : null;
                        bool local = call.InstanceScope == "LocalVariable" && components.Length == 1 && directDeclarations.Any(d => d.Value<string>("ownerPath") == block.Path && d.Value<string>("member") == components[0]);
                        if (!AddRow(callSites, new JObject { ["source"] = "simaticMlExport", ["callerPath"] = block.Path, ["calleePath"] = target.Path,
                            ["calleeResolution"] = sameTargetScope(block) ? "exportedNameSameScope" : "exportedNameOtherScope", ["language"] = language, ["languageSupport"] = support,
                            ["networkId"] = call.NetworkId, ["networkOrdinal"] = Array.FindIndex(units, u => (string)u.Attribute("ID") == call.NetworkId) + 1, ["callUid"] = call.CallUid,
                            ["instanceScope"] = call.InstanceScope, ["instanceComponents"] = new JArray(components), ["instanceKind"] = call.InstanceKind,
                            ["instanceDbPath"] = dbPath, ["instanceDeclaration"] = local ? new JObject { ["ownerPath"] = block.Path, ["member"] = components[0] } : null,
                            ["parameterBindings"] = new JArray(call.Parameters.Select(p => new JObject { ["parameter"] = p.Name, ["section"] = p.Section, ["type"] = p.Type,
                                ["direction"] = p.Direction, ["resolution"] = support == "parsed" ? p.Resolution : "unsupported", ["scope"] = p.Scope,
                                ["actualComponents"] = new JArray(p.Components ?? new string[0]), ["reason"] = p.Reason })) })) break;
                    }
                    for (int index = 0; index < units.Length && !rows.Truncated; index++)
                    {
                        var unit = units[index];
                        string language = unit.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value ?? blockLanguage, support = LookupSupport(language);
                        languages[language] = (languages.TryGetValue(language, out int count) ? count : 0) + 1;
                        if (support == "unsupported") continue;
                        foreach (var access in unit.Descendants().Where(e => e.Name.LocalName == "Access"))
                        {
                            if (!parse.Take()) break;
                            var symbols = access.Elements().Where(e => e.Name.LocalName == "Symbol").ToArray();
                            if (symbols.Length != 1) continue;
                            var parts = symbols[0].Elements().Where(e => e.Name.LocalName == "Component").ToArray();
                            var names = parts.Select(e => (string)e.Attribute("Name") ?? "").ToArray();
                            string scope = (string)access.Attribute("Scope");
                            var root = scope != "GlobalVariable" ? null : roots.FirstOrDefault(candidate => names.Length > candidate.Item2.Length && names[0] == QualifiedName(candidate.Item1.Item)
                                && names.Skip(1).Take(candidate.Item2.Length).SequenceEqual(candidate.Item2));
                            bool local = scope == "LocalVariable" && names.Length > 0 && directDeclarations.Any(d => d.Value<string>("ownerPath") == block.Path && d.Value<string>("member") == names[0]);
                            if (root == null && !local) continue;
                            if (!AddRow(operands, new JObject { ["source"] = "simaticMlExport", ["blockPath"] = block.Path, ["language"] = language, ["languageSupport"] = support,
                                ["networkId"] = (string)unit.Attribute("ID"), ["networkOrdinal"] = index + 1, ["scope"] = scope, ["components"] = new JArray(names),
                                ["dbPath"] = root?.Item1.Path, ["instanceMemberPath"] = root == null ? new JArray(names[0]) : new JArray(root.Item2),
                                ["memberPath"] = new JArray(names.Skip(root == null ? 1 : 1 + root.Item2.Length)),
                                ["ownerPath"] = local ? block.Path : null, ["indexed"] = parts.Any(p => p.HasElements), ["xmlLine"] = ((IXmlLineInfo)access).LineNumber })) break;
                        }
                    }
                }
                catch (Exception exception) { analysis.Warnings.Add(InspectionWarning(block.Path, exception)); }
            }

            var languageSupport = new JObject();
            foreach (var pair in languages.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string status = LookupSupport(pair.Key);
                languageSupport[pair.Key] = new JObject { ["status"] = status, ["compileUnitsScanned"] = pair.Value, ["calls"] = status,
                    ["bindings"] = status == "parsed" ? "simpleSymbolicOnly" : "unsupported", ["operandReferences"] = status,
                    ["gap"] = pair.Key == "STL" ? "Exported CALL and symbolic Access are read per the V21 STL_v5 schema without native qualification. OPN/DI register-relative, absolute and indirect addressing appear only as native xref rows (access Open)."
                        : pair.Key == "GRAPH" ? "Exported Graph_v6 symbolic Access is read without native qualification; step, transition and interlock semantics are not interpreted. Native xref Actions/Transition/Interlock/Supervision rows are retained."
                        : status == "parsed" ? "Single symbolic operands only; expressions, slices and indirect operands stay unresolved."
                        : "No qualified reader for this language; its compile units were skipped, not searched." };
            }
            var completeness = AnalysisCoverage(analysis, "Exact FB/FC; native xref of the block, its candidate instance DBs and enclosing FBs; exported interfaces of candidate owners; exported code of xref callers, readers and owners.");
            bool truncated = analysis.Budget.Truncated || analysis.IndexTruncated || rows.Truncated || parse.Truncated || omitted > 0 || depthBound;
            completeness["truncated"] = truncated; completeness["maxItems"] = max; completeness["rowsTruncated"] = rows.Truncated;
            completeness["scanBudget"] = analysis.MaxItems; completeness["scanTruncated"] = analysis.Budget.Truncated; completeness["xmlScanTruncated"] = parse.Truncated;
            completeness["maxXmlBlocks"] = maxXml; completeness["omittedXmlBlocks"] = omitted; completeness["maxDepth"] = maxDepth; completeness["depthBoundReached"] = depthBound;
            completeness["unresolvedCount"] = unresolved.Count;
            if (truncated || unresolved.Count > 0 || exports.Any(e => e.Value<string>("status") != "exported")) completeness["scopeComplete"] = false;
            var gaps = (JArray)completeness["gaps"];
            gaps.Add("Owners are FBs that native xref reports as users of the type; a declaration the xref omits is not found.");
            gaps.Add("Network operand rows match exact DB symbol components or direct local multi-instance members; namespaced symbol spellings and deeper local chains are not attributed.");
            gaps.Add("Native xref operand rows retain the DB member name/path unparsed; it belongs to a multi-instance only if it starts with that instance memberPath.");
            var result = AnalysisEnvelope(analysis);
            result["block"] = BlockInfo(target, plcPath);
            result["instanceApplicability"] = isFb ? "fbInstances" : "notApplicableForFc";
            result["instances"] = instances; result["multiInstanceDeclarations"] = declarations; result["callSites"] = callSites;
            result["operandReferences"] = operands; result["languageSupport"] = languageSupport; result["unresolved"] = unresolved; result["exports"] = exports;
            result["diagnosticHandoff"] = "Pass an instance row's diagnosticInstanceSelector as diagnostic_plan instance. The planner re-resolves it against its saved review and refuses a mismatch; array instances need an element selector it does not support.";
            result["completeness"] = completeness;
            return result;
        }

        private static string LookupSupport(string language) =>
            language == "SCL" || language == "LAD" || language == "FBD" ? "parsed" : language == "STL" || language == "GRAPH" ? "unqualifiedScan" : "unsupported";

        private static string LookupLanguageHint(Access access) =>
            access == Access.Open ? "STL" : access == Access.Actions || access == Access.Transition || access == Access.Interlock || access == Access.Supervision ? "GRAPH" : null;

        private sealed class LookupLevel { internal BlockEntry Type; internal string[] Suffix; internal JArray Chain; internal int Depth; }
    }
}
