using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json;
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
        private JObject WatchCaptureInstance(JObject args)
        {
            EnsureAttached();
            using (_portal.ExclusiveAccess("TIA MCP: exact watch instance capture")) return CaptureWatchInstance(args);
        }

        // Reuses the analysis owner's exact InstanceDB -> FB resolver and declaration reader.
        // This first capture profile covers a direct root instance; no alias/offset inference.
        private JObject CaptureWatchInstance(JObject args)
        {
            string projectPath = ChangeText(args, "projectPath", 8192), plcPath = ChangeText(args, "plcPath", 8192);
            string blockPath = ChangeText(args, "blockPath", 8192), instancePath = ChangeText(args, "instanceDbPath", 8192);
            var plc = SelectPlc(args); EnsureProjectOffline(plc.Project);
            if (!string.Equals(plc.Project.Path.FullName, projectPath, StringComparison.OrdinalIgnoreCase) || plc.Path != plcPath)
                throw new BridgeException("IdentityMismatch", "Exact physical project and PLC required for watch generation.");
            if (plc.Project.IsModified) throw new BridgeException("UnsavedProject", "Save the source before generating watch rows.");
            var query = WatchInstanceAnalysisQuery(plc.Project.Name, plcPath, instancePath);
            var analysis = ReadNativeAnalysis(query);
            BlockEntry source, storage;
            if (analysis.IndexTruncated || !analysis.Blocks.TryGetValue(blockPath, out source) || !(source.Item is FB) ||
                !analysis.Blocks.TryGetValue(instancePath, out storage) || !(storage.Item is InstanceDB))
                throw new BridgeException("InstanceResolutionIncomplete", "Exact FB and direct InstanceDB in a complete block index required.");
            var instance = (InstanceDB)storage.Item;
            var nativeTypes = analysis.References.Where(r => (r.Relation == ReferenceType.InstanceType || r.Relation == ReferenceType.TypeInstance) &&
                r.Actor?.Key == instancePath && r.Subject?.Key == r.Subject?.BlockPath).Select(r => r.Subject?.BlockPath).Where(p => p != null);
            var resolved = OfflineEvidence.ResolveInstanceTarget(plcPath, instancePath, instance.Namespace, instance.InstanceOfName, analysis.IndexTruncated,
                analysis.Blocks.Values.Where(b => b.Item is FB).Select(b => new OfflineEvidence.InstanceTarget { Path = b.Path, Name = b.Item.Name, Namespace = b.Item.Namespace }), nativeTypes);
            if (resolved.Path != blockPath) throw new BridgeException("InstanceTypeMismatch", "Selected instance does not resolve to the exact FB: " + resolved.Reason);
            if (source.Item.IsKnowHowProtected || storage.Item.IsKnowHowProtected)
                throw new BridgeException("ProtectedObject", "Watch source declarations must be exportable.");
            if (ObjectIdentity.ScopePath(plcPath, instancePath) != plcPath || !string.IsNullOrEmpty(instance.Namespace))
                throw new BridgeException("UnsupportedProfile", "Unit and namespaced watch selectors require a separately verified capture profile.");
            string directory = Path.Combine(ArtifactDirectory(args), "instance-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string fbFile = Path.Combine(directory, "fb.xml"), dbFile = Path.Combine(directory, "instance.xml");
            source.Item.Export(new FileInfo(fbFile), ExportOptions.WithDefaults); storage.Item.Export(new FileInfo(dbFile), ExportOptions.WithDefaults);
            string fbHash = WatchXmlHash(fbFile), dbHash = WatchXmlHash(dbFile);
            var binding = new JObject { ["projectPath"] = projectPath, ["plcPath"] = plcPath, ["blockPath"] = blockPath, ["instanceDbPath"] = instancePath,
                ["blockSha256"] = fbHash, ["instanceSha256"] = dbHash, ["profile"] = "v21-direct-instance-declarations-1" };
            string manifest = ChangeHash(binding.ToString(Formatting.None)), captureId = Guid.NewGuid().ToString("N");
            Func<string, string, JObject> reference = (path, hash) => new JObject {
                ["object"] = new JObject { ["projectPath"] = projectPath, ["plcPath"] = plcPath, ["scopePath"] = ObjectIdentity.ScopePath(plcPath, path), ["objectPath"] = path, ["kind"] = "block" },
                ["snapshot"] = new JObject { ["observationId"] = captureId, ["sourceSha256"] = manifest, ["tiaVersion"] = 21 }, ["contentSha256"] = hash };
            int count = 0; bool truncated = false;
            var declarations = OfflineEvidence.NormalizeInterface(XDocument.Load(dbFile, LoadOptions.SetLineInfo), () => ++count <= 10000, () => truncated = true);
            if (truncated) throw new BridgeException("TraversalLimit", "Instance declarations exceed the capture bound.");
            var candidates = new JArray(); var selectors = new JObject();
            Action<IEnumerable<OfflineEvidence.InterfaceMember>, bool> visit = null;
            visit = (members, blocked) => {
                foreach (var member in members)
                {
                    bool indirect = blocked || member.Section == "InOut" || member.Section == "Temp" || member.Array != null || member.DeclarationKind == "multiInstance";
                    if (member.Members.Count > 0 && !indirect) { visit(member.Members, false); continue; }
                    bool simple = member.Path.Length <= 32 && member.Path.All(p => p != null && Regex.IsMatch(p, @"^[A-Za-z_][A-Za-z0-9_]*$")) &&
                        Regex.IsMatch(instance.Name, @"^[A-Za-z_][A-Za-z0-9_]*$");
                    string id = "member" + candidates.Count;
                    var evidence = new JObject { ["observationId"] = captureId, ["sourceSha256"] = manifest, ["objectPath"] = instancePath,
                        ["declaration"] = new JObject { ["storagePath"] = instancePath, ["memberPath"] = new JArray(member.Path), ["datatype"] = member.DataType, ["contentSha256"] = dbHash },
                        ["xmlLine"] = member.XmlLine, ["origin"] = "nativeInstanceExport", ["instanceResolution"] = resolved.Reason };
                    candidates.Add(new JObject { ["id"] = id, ["sourceKind"] = "instanceDb", ["sourceObject"] = reference(instancePath, dbHash), ["storage"] = reference(instancePath, dbHash),
                        ["memberPath"] = new JArray(member.Path), ["datatype"] = member.DataType ?? "unknown", ["role"] = member.Section ?? "instanceState",
                        ["reason"] = "Captured " + member.Section + " state of the selected FB instance: " + string.Join(".", member.Path),
                        ["resolution"] = !indirect && simple ? "capturedDeclaration" : "unavailable", ["storageClass"] = indirect ? "indirect" : "instanceMember",
                        ["unavailableReason"] = !simple ? "Selector grammar requires a separate native profile." : indirect ? "Array, multi-instance or alias is not expanded by this capture profile." : null,
                        ["symbolComponents"] = new JArray(new[] { instance.Name }.Concat(member.Path)), ["evidence"] = evidence });
                    if (!indirect && simple) selectors[id] = "\"" + instance.Name + "\"." + string.Join(".", member.Path);
                }
            };
            foreach (var section in declarations.Sections) visit(section.Members, section.Name == "InOut" || section.Name == "Temp");
            return new JObject { ["binding"] = binding, ["selectors"] = selectors,
                ["artifacts"] = new JArray(FileArtifact(new FileInfo(fbFile)), FileArtifact(new FileInfo(dbFile))),
                ["request"] = new JObject { ["expectedSource"] = reference(blockPath, fbHash), ["capturedSource"] = reference(blockPath, fbHash),
                    ["instance"] = new JObject { ["storage"] = reference(instancePath, dbHash), ["memberPath"] = new JArray() },
                    ["tableName"] = ChangeText(args, "tableName", 128), ["candidates"] = candidates, ["maxRows"] = args.Value<int?>("maxRows") ?? 64 } };
        }

        private void VerifyWatchSource(JObject args)
        {
            var expected = args["sourceBinding"] as JObject; if (expected == null) return;
            var request = (JObject)expected.DeepClone(); request["projectName"] = args["projectName"]?.DeepClone();
            request["artifactDirectory"] = ArtifactDirectory(args); request["tableName"] = args["tableName"]?.DeepClone();
            var capture = CaptureWatchInstance(request);
            if (!JToken.DeepEquals(expected, capture["binding"])) throw new BridgeException("StaleSource", "The exact FB/instance source changed; generate and review again.");
        }

        // Use the shared analysis reader's admitted traversal bound. Its block
        // index has its own budget and an incomplete index remains a refusal.
        private static JObject WatchInstanceAnalysisQuery(string projectName, string plcPath, string instancePath)
            => new JObject { ["projectName"] = projectName, ["plcPath"] = plcPath,
                ["blockPath"] = instancePath, ["maxItems"] = 2000 };
    }
}
