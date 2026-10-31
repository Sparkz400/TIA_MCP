using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Loader;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // block_read/type_read with format=simaticSd|generatedSource; omitting format keeps the SimaticML XML export.
        private JObject FormatRead(JObject args, string kind, string pathKey, string nameKey)
        {
            string format = args.Value<string>("format");
            if (format != "simaticSd" && format != "generatedSource") throw new BridgeException("InvalidRequest", "format must be simaticSd or generatedSource; omit it for native SimaticML XML.");
            RequireInspectionSelector(args.Value<string>(pathKey), args.Value<string>(nameKey), pathKey, nameKey);
            var selector = (JObject)args.DeepClone();
            selector["kind"] = kind; selector["objectPath"] = args[pathKey]; selector["name"] = args[nameKey];
            var target = ResolveObjectTarget(selector);
            var block = target.Target as PlcBlock; var type = target.Target as PlcType;
            if (block?.IsKnowHowProtected ?? type.IsKnowHowProtected) throw new BridgeException("ProtectedObject", "The selected " + kind + " is know-how protected.");
            string name = SafeFileName(target.Identity.Value<string>("name"));
            // .NET Framework's CreateDirectory result keeps only the leaf name as its original path, which Openness rejects as relative.
            var directory = new DirectoryInfo(Directory.CreateDirectory(Path.Combine(ArtifactDirectory(args), kind + "-" + format + "-" + Guid.NewGuid().ToString("N"))).FullName);
            var result = Envelope();
            result["project"] = ProjectInfo(target.Plc.Project);
            result["identity"] = target.Identity.DeepClone();
            result["format"] = format;
            if (format == "simaticSd")
                result["export"] = DocumentExport(block != null ? block.ExportAsDocuments(directory, name) : type.ExportAsDocuments(directory, name), directory);
            else
            {
                string language = block?.ProgrammingLanguage.ToString();
                string extension = type != null ? ".udt" : block is DataBlock ? ".db" : language == "SCL" ? ".scl" : language == "STL" ? ".awl" : null;
                if (extension == null) throw new BridgeException("UnsupportedFormat", "Source generation covers SCL/STL blocks, DBs and PLC data types; " + language + " has no external source form.");
                var sources = (target.Scope?.Unit?.ExternalSourceGroup ?? target.Plc.Software.ExternalSourceGroup) as PlcExternalSourceSystemGroup
                    ?? throw new BridgeException("UnsupportedCapability", "The selected scope exposes no external-source system group.");
                var project = target.Plc.Project;
                // Generation can add an external source that the cleanup deletes, so it is guarded like a write.
                EnsureProjectOffline(project);
                using (_portal.ExclusiveAccess("TIA MCP: generate one external source file"))
                {
                    bool modifiedBefore = project.IsModified;
                    var before = new HashSet<string>(sources.ExternalSources.Select(source => source.Name), StringComparer.Ordinal);
                    sources.GenerateSource(new IGenerateSource[] { block != null ? block : type }, new FileInfo(Path.Combine(directory.FullName, name + extension)), GenerateOptions.None);
                    // The API documents a file result only; anything it leaves in the project is removed and the group re-verified.
                    var added = sources.ExternalSources.Where(source => !before.Contains(source.Name)).ToList();
                    var removed = new JArray(added.Select(source => source.Name));
                    if (added.Count != 0) ObserveIndexOperation("generated_source_cleanup");
                    foreach (var source in added) source.Delete();
                    if (sources.ExternalSources.Any(source => !before.Contains(source.Name)))
                        throw new BridgeException("ProjectSideEffect", "Source generation left an external source in the project that could not be removed.", new JObject { ["externalSources"] = removed });
                    result["export"] = new JObject { ["artifacts"] = DirectoryArtifacts(directory, "text/plain"), ["removedExternalSources"] = removed,
                        ["projectModifiedBefore"] = modifiedBefore, ["projectModifiedAfter"] = project.IsModified };
                }
            }
            result["completeness"] = new JObject { ["complete"] = true, ["scope"] = format == "simaticSd" ? "Native SIMATIC SD document export of one " + kind : "Native generated external source of one " + kind + " without dependencies",
                ["note"] = "Every file written into the fresh artifact directory is hashed. No normalized model is returned." };
            return result;
        }

        private JObject PlcDocumentInventory(JObject args)
        {
            var plc = SelectInspectionPlc(args, "scopePath");
            int max = Limit(args, 500);
            var lookup = new ScanBudget(50000);
            string scopePath = args.Value<string>("scopePath");
            var scopes = SoftwareScopes(plc, lookup).Where(scope => string.IsNullOrWhiteSpace(scopePath) || scope.Path == scopePath).ToList();
            if (!string.IsNullOrWhiteSpace(scopePath)) RequireOne(scopes.Count, lookup, "PLC or software-unit scope");
            var documents = scopes.SelectMany(scope => Documents(scope.Types, scope.Path + "/types", lookup, 0)).ToList();
            var selected = RequestPaths(args, "exportPaths").Select(path =>
            {
                var matches = documents.Where(entry => entry.Path == path).ToList();
                RequireOne(matches.Count, lookup, "PLC document");
                return matches[0];
            }).ToList();
            var exports = new JArray();
            foreach (var entry in selected)
            {
                var directory = new DirectoryInfo(Directory.CreateDirectory(Path.Combine(ArtifactDirectory(args), "document-simaticSd-" + Guid.NewGuid().ToString("N"))).FullName);
                var row = DocumentExport(entry.Item.ExportAsDocuments(directory, SafeFileName(entry.Item.Name)), directory);
                row["path"] = entry.Path;
                exports.Add(row);
            }
            var result = Envelope();
            result["project"] = ProjectInfo(plc.Project);
            result["plcPath"] = plc.Path;
            result["documents"] = new JArray(documents.Take(max).Select(entry => new JObject { ["path"] = entry.Path, ["name"] = entry.Item.Name }));
            result["exports"] = exports;
            result["completeness"] = new JObject { ["complete"] = !lookup.Truncated && documents.Count <= max, ["truncated"] = lookup.Truncated || documents.Count > max,
                ["maxItems"] = max, ["documentCount"] = documents.Count, ["visitedItems"] = lookup.Used,
                ["scope"] = "Native PlcDocument objects of the PLC and software/safety-unit PLC data type groups (PlcTypeGroup.Documents).",
                ["gaps"] = new JArray("V21 exposes PlcDocument only below PLC data type groups; system type groups have no document composition", "Document content is observed only through explicit exportPaths") };
            return result;
        }

        private JObject PlcLoadableGenerate(JObject args)
        {
            string targetName = args.Value<string>("target");
            var option = targetName == "plc" ? TargetOption.Plc : targetName == "plcSim" ? TargetOption.PlcSim : throw new BridgeException("InvalidRequest", "target must be plc or plcSim.");
            var blockPaths = RequestPaths(args, "blockPaths"); var unitPaths = RequestPaths(args, "unitPaths");
            if ((blockPaths.Count == 0) == (unitPaths.Count == 0)) throw new BridgeException("InvalidRequest", "Specify exactly one of blockPaths or unitPaths.");
            var effective = (JObject)args.DeepClone();
            if (string.IsNullOrWhiteSpace(args.Value<string>("plcPath"))) effective["plcPath"] = ObjectIdentity.PlcPath(blockPaths.Concat(unitPaths).First());
            var plc = SelectPlc(effective);
            var lookup = new ScanBudget(50000);
            var scopes = SoftwareScopes(plc, lookup);
            var blocks = scopes.Where(scope => scope.Unit == null).SelectMany(scope => ScopeBlocks(scope, lookup)).Where(entry => blockPaths.Contains(entry.Path)).ToList();
            var units = scopes.Where(scope => scope.Kind == "softwareUnit" && unitPaths.Contains(scope.Path)).ToList();
            var inconsistent = blocks.Where(entry => !entry.Item.IsConsistent).Select(entry => entry.Path)
                .Concat(units.SelectMany(scope => ScopeBlocks(scope, lookup)).Where(entry => !entry.Item.IsConsistent).Select(entry => entry.Path))
                .Concat(units.SelectMany(scope => ScopeTypes(scope, lookup)).Where(entry => !entry.Item.IsConsistent).Select(entry => entry.Path)).ToList();
            if (lookup.Truncated) throw new BridgeException("TraversalLimit", "Lookup reached its traversal bound; selection and consistency are not established.");
            if (blocks.Count != blockPaths.Count) throw new BridgeException("ObjectNotFound", "Every blockPath must name an exact block of the PLC's non-unit program.");
            if (units.Count != unitPaths.Count) throw new BridgeException("ObjectNotFound", "Every unitPath must name an exact software unit; safety units are excluded.");
            if (inconsistent.Count != 0) throw new BridgeException("CompileRequired", "Loadable generation requires consistent compiled objects. Compile first (change_prepare kind plc_compile); this tool never compiles.",
                new JObject { ["inconsistent"] = new JArray(inconsistent.Take(100)), ["inconsistentCount"] = inconsistent.Count });
            var provider = plc.Software.GetService<LoadableProvider>() ?? throw new BridgeException("UnsupportedCapability", "The selected PLC software exposes no LoadableProvider service; Siemens documents loadables only for CPU 1518 and its subtypes from firmware V2.8.");
            var directory = Directory.CreateDirectory(Path.Combine(ArtifactDirectory(args), "loadable-" + targetName + "-" + Guid.NewGuid().ToString("N")));
            var file = new FileInfo(Path.Combine(directory.FullName, "loadable"));
            if (blocks.Count != 0) provider.GenerateLoadable(file, blocks.Select(entry => entry.Item), option);
            else provider.GenerateLoadable(file, units.Select(scope => (PlcUnit)scope.Unit), option);
            var artifacts = DirectoryArtifacts(directory, "application/octet-stream");
            if (artifacts.Count == 0) throw new BridgeException("LoadableMissing", "GenerateLoadable returned without writing a file.");
            var result = Envelope();
            result["project"] = ProjectInfo(plc.Project);
            result["plcPath"] = plc.Path;
            result["target"] = targetName;
            result["blockPaths"] = new JArray(blocks.Select(entry => entry.Path));
            result["unitPaths"] = new JArray(units.Select(scope => scope.Path));
            result["artifacts"] = artifacts;
            result["completeness"] = new JObject { ["complete"] = true, ["scope"] = "Native LoadableProvider.GenerateLoadable file for the exact selection after a consistency check.",
                ["note"] = "No compile, download, PLC/PLCSIM connection or runtime change was requested. Native project side effects of loadable generation are not natively qualified." };
            return result;
        }

        private static JObject DocumentExport(DocumentExportResult export, DirectoryInfo directory)
        {
            var row = new JObject { ["state"] = export.State.ToString(), ["messages"] = new JArray(export.Messages.Take(100).Select(message => (string)message.Message)),
                ["messagesTruncated"] = export.Messages.Skip(100).Any(), ["artifacts"] = DirectoryArtifacts(directory, "text/plain") };
            if (export.State == DocumentResultState.Failure) throw new BridgeException("ExportFailed", "Native SIMATIC SD document export failed.", row);
            return row;
        }

        private static JArray DirectoryArtifacts(DirectoryInfo directory, string mediaType) =>
            new JArray(directory.GetFiles("*", SearchOption.AllDirectories).OrderBy(file => file.FullName, StringComparer.Ordinal).Select(file => FileArtifact(file, mediaType)));

        private static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        private static List<string> RequestPaths(JObject args, string key)
        {
            var paths = (args[key] as JArray)?.Select(path => (string)path).Distinct(StringComparer.Ordinal).ToList() ?? new List<string>();
            if (paths.Count > 100 || paths.Any(string.IsNullOrWhiteSpace)) throw new BridgeException("InvalidRequest", key + " accepts at most 100 exact, non-empty paths.");
            return paths;
        }

        private static IEnumerable<DocumentEntry> Documents(PlcTypeGroup group, string path, ScanBudget budget, int depth)
        {
            if (depth > 64) { budget.Truncated = true; yield break; }
            foreach (var document in group.Documents)
            {
                if (!budget.Take()) yield break;
                yield return new DocumentEntry { Item = document, Path = path + "/documents/" + Segment(document.Name) };
            }
            foreach (var child in group.Groups)
            {
                if (!budget.Take()) yield break;
                foreach (var entry in Documents(child, path + "/groups/" + Segment(child.Name), budget, depth + 1)) yield return entry;
            }
        }

        private sealed class DocumentEntry { internal PlcDocument Item; internal string Path; }
    }
}
