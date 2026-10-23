using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW.TechnologicalObjects;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private JObject EngineeringRead(JObject args)
        {
            string scope = args.Value<string>("scope") ?? "hardware";
            if (scope == "graphical") return ReadTypedGraphical(args);
            var project = SelectProject(args);
            int max = Limit(args, 500);
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["scope"] = scope;
            if (scope == "calls")
            {
                var block = SelectBlock(args);
                string path = Path.Combine(ArtifactDirectory(args), "calls-" + Guid.NewGuid().ToString("N") + ".xml");
                block.Block.Item.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                var calls = EngineeringSources.ReadNativeCalls(EngineeringSources.ReadXml(File.ReadAllText(path)));
                result["blockPath"] = block.Block.Path;
                result["calls"] = JArray.FromObject(calls.Take(max), Newtonsoft.Json.JsonSerializer.Create(new Newtonsoft.Json.JsonSerializerSettings
                    { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() }));
                result["truncated"] = calls.Length > max;
                result["nativeXml"] = ChangeArtifact(path);
                result["coverage"] = "Exported SCL and LAD/FBD FC/FB argument connections and declared instance components. Unsupported expressions, indirect wiring and unresolved instance identities remain explicit; no runtime propagation is inferred.";
                return result;
            }
            if (scope == "safety")
            {
                result["safety"] = ReadSafety(SelectPlc(args).Software);
                result["coverage"] = "Offline login state, program signatures and runtime-group names/timing. No passwords, protection changes, validation acceptance or physical safety actions.";
                return result;
            }
            if (scope == "technology")
            {
                var plc = SelectPlc(args);
                string requestedName = args.Value<string>("name");
                var objectBudget = new ScanBudget(50000);
                var objects = TechnologyObjects(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", objectBudget)
                    .Where(entry => requestedName == null || entry.Item.Name == requestedName).Take(max + 1).ToArray();
                var rows = new JArray();
                foreach (var entry in objects.Take(max))
                {
                    var item = entry.Item;
                    var parameterBudget = new ScanBudget(max);
                    var parameters = CaptureTechnologyParameters(item.Parameters, null, parameterBudget.Take, ChangeScalar, out bool scanTruncated, out int visited);
                    bool truncated = parameterBudget.Truncated || scanTruncated;
                    rows.Add(new JObject { ["name"] = item.Name, ["path"] = entry.Path, ["ownerPath"] = entry.OwnerPath,
                        ["composition"] = entry.Composition, ["number"] = item.Number,
                        ["typeIdentifier"] = item.OfSystemLibElement, ["version"] = item.OfSystemLibVersion.ToString(),
                        ["programmingLanguage"] = item.ProgrammingLanguage.ToString(), ["parameters"] = parameters,
                        ["parametersTruncated"] = truncated, ["parameterScanVisited"] = visited,
                        ["parametersComplete"] = !truncated && TechnologyParametersComplete(parameters), ["warnings"] = TechnologyParameterWarnings(parameters) });
                }
                result["technologyObjects"] = rows;
                result["truncated"] = objectBudget.Truncated || objects.Length > max || rows.Any(row => row.Value<bool>("parametersTruncated"));
                result["scopeComplete"] = !result.Value<bool>("truncated") && rows.All(row => row.Value<bool>("parametersComplete"));
                result["sensitiveAttributesExcluded"] = true;
                result["coverage"] = "Bounded native scalar TO parameter observations across PLC groups and OutputCamMeasuringInputContainer.OutputCams/MeasuringInputs, not a transactional snapshot. Other service compositions and associations are not expanded. Excluded, unreadable, failed and display-only values remain incomplete. Supported type/version and writes depend on the actual CPU/TO service.";
                result["creationMetadata"] = new JObject { ["enumerationAvailable"] = false,
                    ["note"] = "The installed public API has no discovered method to enumerate all CPU-supported TO type/version pairs. Existing TOs expose exact system-library names and versions; creation is validated by Siemens.",
                    ["documentedExample"] = new JObject { ["typeIdentifier"] = "PID_Compact", ["version"] = "3.0", ["cpuCompatibilityVerified"] = false,
                        ["note"] = "Native creation, scalar configuration and deletion were qualified on CPU 1511-1 PN 6ES7 511-1AL03-0AB0/V4.1. Other CPU/type/version combinations still require native validation. The older 2.3 documentation example was rejected by that fixture CPU.",
                        ["source"] = "https://docs.tia.siemens.cloud/r/en-us/v21/pid-control/pid-control-s7-1200-s7-1500-s7-1200-g2/using-pid_compact-s7-1200-s7-1500-s7-1200-g2/pid_compact-as-of-v2-s7-1200-s7-1500-s7-1200-g2/configuring-pid_compact-as-of-v2-s7-1200-s7-1500-s7-1200-g2/advanced-settings-as-of-v2-s7-1200-s7-1500-s7-1200-g2/pid-parameters-v3-s7-1200-s7-1500-s7-1200-g2" } };
                return result;
            }
            if (scope != "hardware" && scope != "network") throw new BridgeException("InvalidRequest", "scope must be hardware, network, technology, safety or calls.");
            var budget = new ScanBudget(50000);
            string hardwarePath = args.Value<string>("hardwarePath");
            var items = DeviceItems(project, budget).Where(item => hardwarePath == null || item.Path == hardwarePath).Take(max + 1).ToArray();
            var output = new JArray();
            foreach (var entry in items.Take(max))
            {
                var network = entry.Item.GetService<NetworkInterface>();
                if (scope == "network" && network == null) continue;
                var item = new JObject { ["path"] = entry.Path, ["name"] = entry.Item.Name, ["typeIdentifier"] = entry.Item.TypeIdentifier, ["positionNumber"] = entry.Item.PositionNumber };
                if (scope == "hardware")
                {
                    var attributes = new JArray();
                    foreach (var info in entry.Item.GetAttributeInfos().Where(info => !IsSensitiveAttribute(info.Name)).Take(max))
                    {
                        var attribute = new JObject { ["name"] = info.Name, ["access"] = info.AccessMode.ToString(),
                            ["supportedTypes"] = new JArray(info.SupportedTypes.Select(type => type.FullName)), ["writeSupportedByBridge"] = HardwareWriteAttributes.Contains(info.Name) };
                        if ((info.AccessMode & EngineeringAttributeAccessMode.Read) != 0)
                        {
                            try { attribute["value"] = ChangeScalar(entry.Item.GetAttribute(info.Name)); }
                            catch (Exception ex) { attribute["readError"] = ex.Message; }
                        }
                        attributes.Add(attribute);
                    }
                    item["attributes"] = attributes;
                }
                if (network != null) item["nodes"] = new JArray(network.Nodes.Select(NodeSnapshot));
                output.Add(item);
            }
            result["items"] = output;
            result["subnets"] = new JArray(project.Subnets.Take(max).Select(subnet => new JObject { ["name"] = subnet.Name, ["typeIdentifier"] = subnet.TypeIdentifier }));
            result["truncated"] = budget.Truncated || items.Length > max;
            result["sensitiveAttributesExcluded"] = true;
            result["hardwareWriteAllowlist"] = new JArray(HardwareWriteAttributes.OrderBy(v => v, StringComparer.Ordinal));
            result["networkWriteAllowlist"] = new JArray(NetworkWriteAttributes.OrderBy(v => v, StringComparer.Ordinal));
            return result;
        }

        private JObject FixtureSources(JObject args)
        {
            string language = (args.Value<string>("language") ?? "SCL").ToUpperInvariant();
            if (language == "KOP") language = "LAD";
            if (language == "FUP") language = "FBD";
            if (language == "AWL") language = "STL";
            if (!new[] { "SCL", "STL", "LAD", "FBD" }.Contains(language)) throw new BridgeException("UnsupportedLanguage", "Fixtures are available for SCL, STL/AWL, LAD/KOP and FBD/FUP.");
            string name = args.Value<string>("blockName") ?? "TiaMcp_Fixture_" + language;
            if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]{0,79}$")) throw new BridgeException("InvalidRequest", "Fixture blockName must be a simple identifier of at most 80 characters.");
            string content = language == "SCL" ? EngineeringSources.SclFixture(name) : language == "STL" ? EngineeringSources.StlFixture(name) : EngineeringSources.GraphicalFixture(name, language);
            bool xml = language == "LAD" || language == "FBD";
            if (xml) EngineeringSources.BlockDeclarations(EngineeringSources.ReadXml(content)); else EngineeringSources.DeclaredBlocks(content);
            var payload = new JObject { ["expectedBlockNames"] = new JArray(name) };
            if (xml) payload["xml"] = content;
            else { payload["language"] = language; payload["source"] = content; }
            var result = Envelope();
            result["language"] = language; result["kind"] = xml ? "block_import_xml" : "source_upsert"; result["payload"] = payload;
            result["expectedBehavior"] = "One standard FC with Bool Enable input and Bool Active output; Active follows Enable." + (xml ? " An unused Bool EnableAlternate input permits a compiled symbolic-operand edit fixture." : "");
            result["validation"] = "Import envelope checked; native TIA import/compile and simulation validation must be performed on the selected CPU.";
            if (args["artifactDirectory"] != null)
            {
                string path = Path.Combine(ArtifactDirectory(args), name + "-" + Guid.NewGuid().ToString("N") + (xml ? ".xml" : language == "SCL" ? ".scl" : ".awl"));
                File.WriteAllText(path, content, new UTF8Encoding(true));
                result["artifact"] = ChangeArtifact(path);
            }
            return result;
        }
    }
}
