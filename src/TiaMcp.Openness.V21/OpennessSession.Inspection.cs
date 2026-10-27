using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using TiaMcp.Model;
using TiaMcp.Analysis;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private JObject SoftwareInspect(JObject args)
        {
            var plc = SelectInspectionPlc(args, "scopePath");
            var budget = new ScanBudget(Limit(args, 500));
            var warnings = new JArray();
            var technologyEntries = TechnologyObjects(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", budget).ToList();
            var technologyMembership = new TechnologyBlockMembership();
            foreach (var technologyEntry in technologyEntries) technologyMembership.AddTechnology(technologyEntry.Item, technologyEntry.Path);
            var scopes = SoftwareScopes(plc, budget, warnings).Where(scope => string.IsNullOrWhiteSpace(args.Value<string>("scopePath")) || scope.Path == args.Value<string>("scopePath")).ToList();
            var result = Envelope();
            var units = new JArray();
            var blocks = new JArray();
            var tables = new JArray();
            var types = new JArray();
            foreach (var scope in scopes)
            {
                var scopeInfo = new JObject { ["path"] = scope.Path, ["name"] = scope.Name, ["kind"] = scope.Kind,
                    ["identity"] = IdentityInfo(plc.Project, plc.Path, scope.Path, scope.Path, scope.Kind, scope.Name) };
                units.Add(scopeInfo);
                scopeInfo["namespacePreset"] = scope.Unit?.NamespacePreset;
                var groups = new JArray(); scopeInfo["groups"] = groups;
                foreach (string groupPath in EngineeringGroupPaths(scope.Blocks, scope.Path + "/blocks", 0)
                    .Concat(EngineeringGroupPaths(scope.Types, scope.Path + "/types", 0))
                    .Concat(EngineeringGroupPaths(scope.Tags, scope.Path + "/tags", 0))
                    .Concat(scope.Path == plc.Path ? EngineeringGroupPaths(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", 0) : Enumerable.Empty<string>()))
                {
                    if (!budget.Take()) break;
                    groups.Add(groupPath);
                }
                if (scope.Unit != null)
                {
                    var relations = new JArray();
                    scopeInfo["relations"] = relations;
                    foreach (var relation in scope.Unit.Relations)
                    {
                        if (!budget.Take()) break;
                        relations.Add(new JObject { ["relationType"] = relation.RelationType.ToString(), ["relatedObject"] = InspectionValue(relation.RelatedObject) });
                    }
                }
                foreach (var block in ScopeBlocks(scope, budget))
                {
                    var row = BlockInfo(block, plc.Path);
                    row["scopePath"] = scope.Path;
                    row["technologyPath"] = technologyMembership.AddBlock(block.Item, block.Path);
                    row["identity"] = IdentityInfo(plc.Project, plc.Path, scope.Path, block.Path, "block", block.Item.Name);
                    blocks.Add(row);
                }
                foreach (var table in TagTables(scope.Tags, scope.Path + "/tags", budget))
                {
                    var row = new JObject { ["path"] = table.Path, ["name"] = table.Item.Name, ["scopePath"] = scope.Path };
                    var tags = new JArray();
                    var constants = new JArray();
                    row["tags"] = tags;
                    row["constants"] = constants;
                    tables.Add(row);
                    foreach (var tag in table.Item.Tags)
                    {
                        if (!budget.Take()) break;
                        tags.Add(new JObject { ["path"] = table.Path + "/tags/" + Segment(tag.Name), ["name"] = tag.Name,
                            ["dataType"] = tag.DataTypeName, ["logicalAddress"] = tag.LogicalAddress, ["isSafety"] = tag.IsSafety,
                            ["externalAccessible"] = tag.ExternalAccessible, ["externalVisible"] = tag.ExternalVisible,
                            ["externalWritable"] = tag.ExternalWritable });
                    }
                    foreach (var constant in table.Item.UserConstants)
                    {
                        if (!budget.Take()) break;
                        constants.Add(new JObject { ["path"] = table.Path + "/constants/" + Segment(constant.Name),
                            ["name"] = constant.Name, ["dataType"] = constant.DataTypeName, ["value"] = InspectionValue(constant.Value), ["kind"] = "user" });
                    }
                    foreach (var constant in table.Item.SystemConstants)
                    {
                        if (!budget.Take()) break;
                        constants.Add(new JObject { ["path"] = table.Path + "/systemConstants/" + Segment(constant.Name),
                            ["name"] = constant.Name, ["dataType"] = constant.DataTypeName, ["value"] = InspectionValue(constant.Value), ["kind"] = "system" });
                    }
                }
                foreach (var type in ScopeTypes(scope, budget))
                    types.Add(new JObject { ["path"] = type.Path, ["name"] = type.Item.Name, ["scopePath"] = scope.Path,
                        ["isConsistent"] = type.Item.IsConsistent, ["isKnowHowProtected"] = type.Item.IsKnowHowProtected });
            }
            var technology = new JArray();
            foreach (var entry in technologyEntries)
                technology.Add(TechnologyInfo(entry));
            result["project"] = ProjectInfo(plc.Project);
            result["plcPath"] = plc.Path;
            result["softwareScopes"] = units;
            result["blocks"] = blocks;
            result["tagTables"] = tables;
            result["types"] = types;
            result["technologyObjects"] = technology;
            result["technologyBlockMembership"] = technologyMembership.Coverage(budget.Truncated);
            result["groupCoverage"] = "Complete bounded user-group paths, including empty groups; native system-group objects are included in their object inventories.";
            result["warnings"] = warnings;
            result["completeness"] = InspectionCoverage(budget, warnings, "Program and generated system blocks, software/safety-unit contents, tag tables, constants, PLC types and technology-object identities.",
                "Block bodies and type members require native XML reads", "Technology parameters require technology_read", "No online values", "No transactional project snapshot");
            return result;
        }

        private JObject BlockInterface(JObject args)
        {
            var block = SelectBlock(args);
            if (block.Block.Item.IsKnowHowProtected) throw new BridgeException("ProtectedObject", "The selected block is know-how protected.");
            var file = NewInspectionArtifact(args, "interface");
            block.Block.Item.Export(file, ExportOptions.WithDefaults);
            var result = ExportedInspection(file, args);
            result["project"] = ProjectInfo(block.Project);
            result["block"] = BlockInfo(block.Block, block.PlcPath);
            return result;
        }

        private JObject TypeRead(JObject args)
        {
            if (args.Value<string>("format") != null) return FormatRead(args, "type", "typePath", "typeName");
            var plc = SelectInspectionPlc(args, "typePath");
            string path = args.Value<string>("typePath");
            string name = args.Value<string>("typeName");
            RequireInspectionSelector(path, name, "typePath", "typeName");
            var lookup = new ScanBudget(50000);
            var matches = SoftwareScopes(plc, lookup).SelectMany(scope => ScopeTypes(scope, lookup))
                .Where(entry => ObjectIdentity.Matches(entry.Path, entry.Item.Name, ObjectIdentity.ScopePath(plc.Path, entry.Path), path, name, args.Value<string>("scopePath"))).ToList();
            RequireOne(matches.Count, lookup, "PLC type");
            var selected = matches[0];
            if (selected.Item.IsKnowHowProtected) throw new BridgeException("ProtectedObject", "The selected PLC type is know-how protected.");
            var file = NewInspectionArtifact(args, "type");
            selected.Item.Export(file, ExportOptions.WithDefaults);
            var result = ExportedInspection(file, args);
            result["project"] = ProjectInfo(plc.Project);
            result["plcPath"] = plc.Path;
            result["type"] = new JObject { ["path"] = selected.Path, ["name"] = selected.Item.Name, ["isConsistent"] = selected.Item.IsConsistent };
            result["identity"] = IdentityInfo(plc.Project, plc.Path, ObjectIdentity.ScopePath(plc.Path, selected.Path), selected.Path, "type", selected.Item.Name);
            return result;
        }

        private JObject TagTableRead(JObject args)
        {
            var plc = SelectInspectionPlc(args, "tagTablePath");
            string path = args.Value<string>("tagTablePath");
            string name = args.Value<string>("tagTableName");
            RequireInspectionSelector(path, name, "tagTablePath", "tagTableName");
            var budget = new ScanBudget(50000);
            var matches = SoftwareScopes(plc, budget).SelectMany(scope => TagTables(scope.Tags, scope.Path + "/tags", budget))
                .Where(entry => ObjectIdentity.Matches(entry.Path, entry.Item.Name, ObjectIdentity.ScopePath(plc.Path, entry.Path), path, name, args.Value<string>("scopePath"))).ToList();
            RequireOne(matches.Count, budget, "tag table");
            var selected = matches[0];
            var file = NewInspectionArtifact(args, "tag-table");
            selected.Item.Export(file, ExportOptions.WithDefaults);
            var result = Envelope();
            result["project"] = ProjectInfo(plc.Project);
            result["plcPath"] = plc.Path;
            result["tagTable"] = new JObject { ["path"] = selected.Path, ["name"] = selected.Item.Name };
            result["identity"] = IdentityInfo(plc.Project, plc.Path, ObjectIdentity.ScopePath(plc.Path, selected.Path), selected.Path, "tagTable", selected.Item.Name);
            result["artifact"] = FileArtifact(file);
            if (file.Length <= 512 * 1024) result["xml"] = File.ReadAllText(file.FullName);
            result["completeness"] = new JObject { ["complete"] = true, ["scope"] = "Unchanged native tag-table XML export, including constants and language-specific comments.", ["xmlIncluded"] = file.Length <= 512 * 1024 };
            return result;
        }

        private JObject HardwareInspect(JObject args)
        {
            var project = SelectProject(args);
            string wanted = args.Value<string>("hardwarePath");
            var lookup = new ScanBudget(50000);
            var budget = new ScanBudget(Limit(args, 500));
            var warnings = new JArray();
            var items = new JArray();
            var hardware = DeviceItems(project, lookup).ToArray();
            foreach (var entry in hardware)
            {
                if (!string.IsNullOrWhiteSpace(wanted) && !string.Equals(entry.Path, wanted, StringComparison.Ordinal)) continue;
                if (!budget.Take()) break;
                var row = new JObject { ["path"] = entry.Path, ["name"] = entry.Item.Name,
                    ["positionNumber"] = entry.Item.PositionNumber, ["classification"] = entry.Item.Classification.ToString(),
                    ["typeIdentifier"] = entry.Item.TypeIdentifier,
                    ["isBuiltIn"] = entry.Item.IsBuiltIn, ["isPlugged"] = entry.Item.IsPlugged,
                    ["networkObservation"] = "notRead" };
                items.Add(row);
                var addresses = new JArray();
                var channels = new JArray();
                row["addresses"] = addresses;
                row["channels"] = channels;
                try
                {
                    foreach (var address in entry.Item.Addresses)
                    {
                        if (!budget.Take()) break;
                        addresses.Add(new JObject { ["ioType"] = address.IoType.ToString(), ["startAddress"] = address.StartAddress,
                            ["length"] = address.Length, ["lengthUnit"] = "bits", ["byteSpan"] = TiaMcp.Analysis.OfflineEvidence.IoBitLengthToByteLength(address.Length),
                            ["attributes"] = InspectionAttributes(address, budget, warnings, entry.Path + "/address") });
                    }
                    foreach (var channel in entry.Item.Channels)
                    {
                        if (!budget.Take()) break;
                        channels.Add(new JObject { ["number"] = channel.Number, ["ioType"] = channel.IoType.ToString(), ["type"] = channel.Type.ToString(),
                            ["attributes"] = InspectChannelEngineeringAttributes(channel, budget, warnings, entry.Path + "/channel/" + channel.Number) });
                    }
                    var network = entry.Item.GetService<NetworkInterface>();
                    row["networkObservation"] = network == null ? "notExposed" : "observed";
                    if (network != null)
                        row["network"] = InspectConfiguredNetwork(project, entry, network, hardware, budget, warnings);
                    row["gsd"] = GsdInfo(entry.Item.GetService<GsdDeviceItem>());
                    if (entry.Item.Parent is Device device && device.IsGsd) row["gsdDevice"] = GsdInfo(device.GetService<GsdDevice>());
                    row["gsdExportAvailable"] = entry.Item.GetService<GsdExportProvider>() != null;
                    row["attributes"] = InspectionAttributes(entry.Item, budget, warnings, entry.Path);
                }
                catch (Exception exception) { warnings.Add(InspectionWarning(entry.Path, exception)); }
                if (budget.Truncated) break;
            }
            if (lookup.Truncated) warnings.Add("Hardware lookup reached its 50000-item traversal bound.");
            if (items.Count == 0 && !string.IsNullOrWhiteSpace(wanted) && !lookup.Truncated)
                throw new BridgeException("HardwareNotFound", "No hardware item matches hardwarePath.");
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["items"] = items;
            result["warnings"] = warnings;
            result["completeness"] = InspectionCoverage(budget, warnings, "Configured hardware attributes, address ranges, channels, subnet memberships, I/O-controller/connector associations, port interconnections and GSD identities exposed by V21 Openness.",
                "Channel terminal/signal mappings only when exposed as native attributes; no guessed byte-per-channel allocation", "No electrical wiring or runtime diagnostics", "Attribute values limited to 4096 characters");
            return result;
        }

        private JObject TechnologyRead(JObject args)
        {
            var plc = SelectInspectionPlc(args, "technologyPath");
            string path = args.Value<string>("technologyPath");
            string name = args.Value<string>("technologyName");
            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(name))
            {
                var listBudget = new ScanBudget(Limit(args, 500));
                var list = new JArray(TechnologyObjects(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", listBudget).Select(TechnologyInfo));
                var inventory = Envelope();
                inventory["project"] = ProjectInfo(plc.Project);
                inventory["plcPath"] = plc.Path;
                inventory["technologyObjects"] = list;
                inventory["parametersCoverage"] = "Select technologyPath or technologyName to read parameters and native hardware connections. Inventory includes PLC groups and OutputCamMeasuringInputContainer.OutputCams/MeasuringInputs; other service compositions and associations are not expanded.";
                inventory["completeness"] = InspectionCoverage(listBudget, new JArray(), "Native technology-object identities and type/version metadata.",
                    "Parameters and connections require an explicit technology-object selector", "Other service compositions and associations are not expanded", "No live axis state or motion commands");
                return inventory;
            }
            var lookup = new ScanBudget(50000);
            var matches = TechnologyObjects(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", lookup)
                .Where(entry => InspectionMatches(entry.Path, entry.Item.Name, path, name)).Take(2).ToList();
            RequireOne(matches.Count, lookup, "technology object");
            var selected = matches[0];
            var budget = new ScanBudget(Limit(args, 500));
            var warnings = new JArray();
            var connections = TechnologyConnections(selected.Item, budget, warnings, selected.Path, out var connectionCoverage, new TechnologyReferenceReader(plc.Project, plc.Path));
            string prefix = args.Value<string>("parameterPrefix");
            var parameters = CaptureTechnologyParameters(selected.Item.Parameters, prefix, budget.Take, InspectionValue, out bool scanTruncated, out int parameterScanVisited);
            if (scanTruncated) budget.Truncated = true;
            foreach (var warning in TechnologyParameterWarnings(parameters)) warnings.Add(warning.DeepClone());
            var result = Envelope();
            result["project"] = ProjectInfo(plc.Project);
            result["plcPath"] = plc.Path;
            result["technologyObject"] = TechnologyInfo(selected);
            result["hardwareConnections"] = connections;
            result["hardwareConnectionCoverage"] = connectionCoverage;
            result["parameters"] = parameters;
            result["parameterPrefix"] = prefix;
            result["parameterScanVisited"] = parameterScanVisited;
            result["parametersComplete"] = !budget.Truncated && TechnologyParametersComplete(parameters);
            result["sensitiveAttributesExcluded"] = true;
            result["warnings"] = warnings;
            if (args.Value<bool?>("exportXml") == true)
            {
                var file = NewInspectionArtifact(args, "technology");
                selected.Item.Export(file, ExportOptions.WithDefaults);
                result["artifact"] = FileArtifact(file);
            }
            result["completeness"] = InspectionCoverage(budget, warnings, "Native configured technology-object parameter names and values, optionally filtered by exact prefix.",
                "No live axis state or motion commands", "Connection values are native parameter/attribute evidence, not inferred topology", "Other service compositions and associations are not expanded", "Values limited to 4096 characters; excluded, failed and display-only parameter values are incomplete", "Parameter traversal is limited to 50000 entries including prefix exclusions");
            return result;
        }

        private JObject SafetyInspect(JObject args, bool validation = false)
        {
            var plc = SelectPlc(args);
            var administration = SafetyAdministrationFor(plc.Software, out var cpu);
            if (administration == null) throw new BridgeException("UnsupportedCapability", "SafetyAdministration is not available for this PLC/software configuration.");
            var budget = new ScanBudget(Limit(args, 500));
            var warnings = new JArray();
            var result = Envelope();
            result["project"] = ProjectInfo(plc.Project);
            result["plcPath"] = plc.Path;
            result["isLoggedOn"] = administration.IsLoggedOnToSafetyOfflineProgram;
            result["isPasswordSet"] = administration.IsSafetyOfflineProgramPasswordSet;
            var groups = new JArray();
            var signatures = new JArray();
            result["runtimeGroups"] = groups;
            result["programSignatures"] = signatures;
            foreach (var group in administration.RuntimeGroups)
            {
                if (!budget.Take()) break;
                groups.Add(new JObject { ["name"] = group.Name, ["mainSafetyBlockName"] = group.MainSafetyBlockName,
                    ["mainSafetyBlockInstanceDbName"] = group.MainSafetyBlockIDbName, ["fObName"] = group.FOBName,
                    ["fObEventClass"] = group.FOBEventClass.ToString(), ["infoDbName"] = group.InfoDbName,
                    ["preProcessingName"] = group.PreProcessingName, ["postProcessingName"] = group.PostProcessingName,
                    ["maximumCycleTime"] = InspectionValue(group.MaximumCycleTime), ["warnCycleTime"] = InspectionValue(group.WarnCycleTime) });
            }
            foreach (var signature in administration.ProgramSignatures.Signatures)
            {
                if (!budget.Take()) break;
                signatures.Add(new JObject { ["type"] = signature.Type.ToString(), ["value"] = InspectionValue(signature.Value) });
            }
            result["settings"] = InspectionAttributes(administration.Settings, budget, warnings, plc.Path + "/safety/settings");
            if (!string.IsNullOrWhiteSpace(args.Value<string>("blockName")) || !string.IsNullOrWhiteSpace(args.Value<string>("blockPath")))
            {
                var block = SelectBlock(args);
                var provider = block.Block.Item.GetService<SafetySignatureProvider>();
                var blockSignatures = new JArray();
                if (provider != null)
                    foreach (var signature in provider.Signatures)
                    {
                        if (!budget.Take()) break;
                        blockSignatures.Add(new JObject { ["type"] = signature.Type.ToString(), ["value"] = InspectionValue(signature.Value) });
                    }
                result["block"] = BlockInfo(block.Block, block.PlcPath);
                result["blockSignatures"] = blockSignatures;
                result["blockSignatureServiceAvailable"] = provider != null;
            }
            if (validation) InspectSafetyValidation(plc, cpu, args, budget, warnings, result);
            result["warnings"] = warnings;
            result["completeness"] = validation
                ? InspectionCoverage(budget, warnings, "Offline native safety runtime-group configuration, settings, program/block signatures, Failsafe_* parameters of the PLC and its F-I/O, and Safety Validation Assistant activation tests.",
                    "Reading signatures, F-parameters or test validity does not validate or approve a safety program", "Activation tests are project-level; evaluation-device correlation is by name only",
                    "No safety login, compile, download, activation-test execution, result reset, acceptance or runtime operation performed")
                : InspectionCoverage(budget, warnings, "Offline native safety runtime-group configuration, settings and program/block signatures.",
                    "Reading signatures does not validate or approve a safety program", "No safety login, compile, download, acceptance or runtime operation performed");
            return result;
        }

        private static SafetyAdministration SafetyAdministrationFor(PlcSoftware software) => SafetyAdministrationFor(software, out _);

        private static SafetyAdministration SafetyAdministrationFor(PlcSoftware software, out DeviceItem cpu)
        {
            // V21 exposes this service on the PLC DeviceItem, not PlcSoftware.
            // Follow native ownership rather than selecting another CPU by name.
            IEngineeringInstance owner = software;
            for (int depth = 0; owner != null && depth < 64; depth++, owner = owner.Parent)
            {
                if (!(owner is DeviceItem device)) continue;
                var administration = device.GetService<SafetyAdministration>();
                if (administration != null) { cpu = device; return administration; }
            }
            cpu = null;
            return null;
        }

        private static List<SoftwareScope> SoftwareScopes(PlcSelection plc, ScanBudget budget, JArray warnings = null)
        {
            var scopes = new List<SoftwareScope> { new SoftwareScope { Name = plc.Software.Name, Kind = "plc", Path = plc.Path,
                Blocks = plc.Software.BlockGroup, Tags = plc.Software.TagTableGroup, Types = plc.Software.TypeGroup } };
            try
            {
                var provider = plc.Software.GetService<PlcUnitProvider>();
                if (provider == null) return scopes;
                foreach (var unit in provider.UnitGroup.Units)
                {
                    if (!budget.Take()) break;
                    scopes.Add(UnitScope(unit, plc.Path + "/units/" + Segment(unit.Name), "softwareUnit"));
                }
                foreach (var unit in provider.UnitGroup.SafetyUnits)
                {
                    if (!budget.Take()) break;
                    scopes.Add(UnitScope(unit, plc.Path + "/safetyUnits/" + Segment(unit.Name), "safetyUnit"));
                }
            }
            catch (Exception exception)
            {
                if (warnings == null) throw new BridgeException("TraversalIncomplete", "Software-unit traversal failed: " + exception.Message);
                warnings.Add(InspectionWarning(plc.Path + "/units", exception));
            }
            return scopes;
        }

        private static SoftwareScope UnitScope(PlcUnitBase unit, string path, string kind) => new SoftwareScope
        { Name = unit.Name, Kind = kind, Path = path, Unit = unit, Blocks = unit.BlockGroup, Tags = unit.TagTableGroup, Types = unit.TypeGroup };

        private static IEnumerable<BlockEntry> AllBlocks(PlcSelection plc, ScanBudget budget)
        {
            foreach (var scope in SoftwareScopes(plc, budget))
                foreach (var block in ScopeBlocks(scope, budget)) yield return block;
        }

        private static IEnumerable<BlockEntry> ScopeBlocks(SoftwareScope scope, ScanBudget budget)
        {
            foreach (var block in Blocks(scope.Blocks, scope.Path + "/blocks", budget)) yield return block;
            var system = scope.Blocks as PlcBlockSystemGroup;
            if (system != null)
                foreach (var group in system.SystemBlockGroups)
                    foreach (var block in SystemBlocks(group, scope.Path + "/blocks/systemGroups/" + Segment(group.Name), budget, 0)) yield return block;
        }

        private static IEnumerable<BlockEntry> SystemBlocks(PlcSystemBlockGroup group, string path, ScanBudget budget, int depth)
        {
            if (depth > 64 || !budget.Take()) { budget.Truncated = true; yield break; }
            foreach (var block in group.Blocks)
            {
                if (!budget.Take()) yield break;
                yield return new BlockEntry { Item = block, Path = BlockObjectPath(path, block), NativeSystemGroup = true };
            }
            foreach (var child in group.Groups)
                foreach (var block in SystemBlocks(child, path + "/groups/" + Segment(child.Name), budget, depth + 1)) yield return block;
        }

        private static IEnumerable<TypeEntry> ScopeTypes(SoftwareScope scope, ScanBudget budget)
        {
            foreach (var entry in Types(scope.Types, scope.Path + "/types", budget, 0)) yield return entry;
            var system = scope.Types as PlcTypeSystemGroup;
            if (system != null)
                foreach (var group in system.SystemTypeGroups)
                {
                    if (!budget.Take()) yield break;
                    foreach (var type in group.Types)
                    {
                        if (!budget.Take()) yield break;
                        yield return new TypeEntry { Item = type, Path = ObjectIdentity.ObjectPath(scope.Path + "/types/systemGroups/" + Segment(group.Name), type.Name, type.Namespace) };
                    }
                }
        }

        private static IEnumerable<TypeEntry> Types(PlcTypeGroup group, string path, ScanBudget budget, int depth)
        {
            if (depth > 64) { budget.Truncated = true; yield break; }
            foreach (var type in group.Types)
            {
                if (!budget.Take()) yield break;
                yield return new TypeEntry { Item = type, Path = ObjectIdentity.ObjectPath(path, type.Name, type.Namespace) };
            }
            foreach (var child in group.Groups)
            {
                if (!budget.Take()) yield break;
                foreach (var type in Types(child, path + "/groups/" + Segment(child.Name), budget, depth + 1)) yield return type;
            }
        }

        private static IEnumerable<TagTableEntry> TagTables(PlcTagTableGroup group, string path, ScanBudget budget, int depth = 0)
        {
            if (depth > 64) { budget.Truncated = true; yield break; }
            foreach (var table in group.TagTables)
            {
                if (!budget.Take()) yield break;
                yield return new TagTableEntry { Item = table, Path = path + "/" + Segment(table.Name) };
            }
            foreach (var child in group.Groups)
            {
                if (!budget.Take()) yield break;
                foreach (var table in TagTables(child, path + "/groups/" + Segment(child.Name), budget, depth + 1)) yield return table;
            }
        }

        private static JObject TechnologyInfo(TechnologyEntry entry) => new JObject { ["path"] = entry.Path,
            ["ownerPath"] = entry.OwnerPath, ["composition"] = entry.Composition,
            ["name"] = entry.Item.Name, ["systemLibraryType"] = entry.Item.OfSystemLibElement,
            ["systemLibraryVersion"] = InspectionValue(entry.Item.OfSystemLibVersion) };

        private static JObject InspectionWarning(string path, Exception exception) => new JObject { ["path"] = path, ["exceptionType"] = exception.GetType().Name, ["message"] = exception.Message };
        private static JObject InspectionCoverage(ScanBudget budget, JArray warnings, string scope, params string[] gaps) => new JObject
        { ["complete"] = false, ["scopeComplete"] = !budget.Truncated && warnings.Count == 0, ["truncated"] = budget.Truncated,
            ["visitedItems"] = budget.Used, ["scope"] = scope, ["gaps"] = new JArray(gaps) };

        private PlcSelection SelectInspectionPlc(JObject args, string pathKey)
        {
            var effective = (JObject)args.DeepClone();
            string path = args.Value<string>(pathKey) ?? args.Value<string>("scopePath");
            if (string.IsNullOrWhiteSpace(args.Value<string>("plcPath")) && !string.IsNullOrWhiteSpace(path))
            {
                effective["plcPath"] = ObjectIdentity.PlcPath(path);
            }
            return SelectPlc(effective);
        }

        private static bool InspectionMatches(string path, string name, string wantedPath, string wantedName) =>
            (string.IsNullOrWhiteSpace(wantedPath) || string.Equals(path, wantedPath, StringComparison.Ordinal)) &&
            (string.IsNullOrWhiteSpace(wantedName) || string.Equals(name, wantedName, StringComparison.Ordinal));
        private static void RequireInspectionSelector(string path, string name, string pathKey, string nameKey)
        { if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(name)) throw new BridgeException("InvalidRequest", "Specify " + pathKey + " or " + nameKey + "."); }
        private static void RequireOne(int count, ScanBudget budget, string kind)
        {
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Lookup reached its traversal bound; target uniqueness is not established.");
            if (count == 0) throw new BridgeException("ObjectNotFound", "No matching " + kind + " was found.");
            if (count != 1) throw new BridgeException("AmbiguousTarget", "More than one " + kind + " matches; specify its exact path.");
        }

        private static FileInfo NewInspectionArtifact(JObject args, string prefix) => new FileInfo(Path.Combine(ArtifactDirectory(args), prefix + "-" + Guid.NewGuid().ToString("N") + ".xml"));

        private JObject ExportedInspection(FileInfo file, JObject args)
        {
            var result = Envelope();
            result["artifact"] = FileArtifact(file);
            var budget = new ScanBudget(Limit(args, 500));
            var warnings = new JArray();
            try
            {
                using (var reader = XmlReader.Create(file.FullName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64L * 1024 * 1024 }))
                {
                    var xml = XDocument.Load(reader, LoadOptions.SetLineInfo);
                    result["schema"] = new JObject { ["root"] = xml.Root?.Name.ToString(),
                        ["engineeringVersion"] = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Engineering")?.Attribute("version")?.Value,
                        ["namespaces"] = new JArray(xml.Descendants().Select(e => e.Name.NamespaceName).Where(v => v.Length != 0).Distinct()) };
                    var normalized = OfflineEvidence.NormalizeInterface(xml, budget.Take, () => budget.Truncated = true);
                    result["sections"] = new JArray(normalized.Sections.Select(section => new JObject { ["name"] = section.Name,
                        ["ownerName"] = section.OwnerName, ["ownerKind"] = section.OwnerKind,
                        ["members"] = InspectionMembers(section.Members), ["xmlLine"] = section.XmlLine }));
                    result["declarations"] = new JArray(normalized.Declarations.Select(declaration => new JObject {
                        ["name"] = declaration.Name, ["kind"] = declaration.Kind, ["namespace"] = declaration.Namespace,
                        ["instanceOfName"] = declaration.InstanceOfName, ["instanceOfNumber"] = declaration.InstanceOfNumber,
                        ["instanceTargetResolved"] = false, ["xmlLine"] = declaration.XmlLine }));
                    result["objectKinds"] = new JArray(xml.Root.Elements().Where(e => e.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal)).Select(e => e.Name.LocalName));
                }
            }
            catch (Exception exception) { warnings.Add(InspectionWarning(file.FullName, exception)); }
            result["warnings"] = warnings;
            result["completeness"] = InspectionCoverage(budget, warnings, "Interface/DB/type members parsed from native XML; original export retained unchanged with SHA-256.",
                "Sparse array element values are declared overrides, not a complete initialized array; expressions are never evaluated",
                "Named types and instance declarations retain their native spelling; exact type identity requires native relations",
                "No absolute offset inference, transactional snapshot or byte-layout guarantee; unknown XML remains in original artifact");
            return result;
        }

        private static JArray InspectionMembers(IEnumerable<OfflineEvidence.InterfaceMember> members)
        {
            var rows = new JArray();
            foreach (var member in members)
            {
                var row = new JObject { ["name"] = member.Name, ["dataType"] = member.DataType, ["section"] = member.Section,
                    ["pathComponents"] = new JArray(member.Path), ["declarationKind"] = member.DeclarationKind,
                    ["declaredType"] = member.DeclaredType, ["startValue"] = InspectionValue(member.StartValue), ["defaultValue"] = InspectionValue(member.DefaultValue),
                    ["xmlAttributes"] = JObject.FromObject(member.XmlAttributes), ["xmlLine"] = member.XmlLine,
                    ["sections"] = new JArray(member.Sections), ["members"] = InspectionMembers(member.Members) };
                foreach (var field in member.FieldXml)
                    row[field.Key.Substring(0, 1).ToLowerInvariant() + field.Key.Substring(1) + "Xml"] = InspectionValue(field.Value);
                if (member.Array != null) row["array"] = InspectionArray(member.Array);
                row["elementValues"] = new JArray(member.ElementValues.Select(value => new JObject {
                    ["nativePath"] = value.NativePath, ["indices"] = value.Indices == null ? null : new JArray(value.Indices),
                    ["pathResolved"] = value.UnresolvedReason == null, ["unresolvedReason"] = value.UnresolvedReason,
                    ["startValue"] = InspectionValue(value.StartValue), ["defaultValue"] = InspectionValue(value.DefaultValue),
                    ["xml"] = InspectionValue(value.Xml), ["xmlLine"] = value.XmlLine }));
                rows.Add(row);
            }
            return rows;
        }

        private static JObject InspectionArray(OfflineEvidence.ArrayDeclaration array) => new JObject {
            ["elementType"] = array.ElementType, ["boundsResolved"] = array.BoundsResolved,
            ["elementArray"] = array.ElementArray == null ? null : InspectionArray(array.ElementArray),
            ["valuesAreSparse"] = true, ["dimensions"] = new JArray(array.Dimensions.Select(dimension => new JObject {
                ["declaration"] = dimension.Declaration, ["lower"] = dimension.Lower, ["upper"] = dimension.Upper })) };

        private sealed class SoftwareScope { internal string Path, Name, Kind; internal PlcUnitBase Unit; internal PlcBlockGroup Blocks; internal PlcTagTableGroup Tags; internal PlcTypeGroup Types; }
        private sealed class TypeEntry { internal PlcType Item; internal string Path; }
        private sealed class TagTableEntry { internal PlcTagTable Item; internal string Path; }
    }
}
