using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private JObject CaptureClassicArea(Project project, JObject args, string directory, JObject manifest)
        {
            var area = args["area"] as JObject ?? throw new BridgeException("InvalidRequest", "area must be an object.");
            if (area.Properties().Any(p => !new[] { "plcPath", "blockFolderPath", "typePaths", "prototypeScreens", "hmiTagRoots" }.Contains(p.Name))) throw new BridgeException("InvalidRequest", "Unknown area field.");
            var plc = SelectPlc(new JObject { ["projectName"] = project.Name, ["plcPath"] = ChangeText(area, "plcPath", 4096) });
            if (!Equals(plc.Project, project)) throw new BridgeException("TargetIdentityMismatch", "Area PLC belongs to a different project.");
            string folder = ChangeText(area, "blockFolderPath", 4096);
            var group = FindBlockGroup(plc.Software.BlockGroup, plc.Path + "/blocks", folder, 0)
                ?? throw new BridgeException("ObjectNotFound", "Select an exact PLC-root user block folder. Software-unit area capture is not supported.");
            var budget = new ScanBudget(65);
            var blocks = Blocks(group, folder, budget).ToArray();
            if (budget.Truncated || blocks.Length > 64) throw new BridgeException("TraversalLimit", "Area capture is bounded to 64 blocks and groups.");
            var rows = new JArray(); var findings = new JArray();
            foreach (var block in blocks)
            {
                var row = BlockInfo(block, plc.Path); rows.Add(row);
                if (block.Item.IsKnowHowProtected || !block.Item.IsConsistent)
                {
                    findings.Add(new JObject { ["objectPath"] = block.Path, ["code"] = block.Item.IsKnowHowProtected ? "ProtectedObject" : "CompileRequired",
                        ["nextStep"] = block.Item.IsKnowHowProtected ? "Source unavailable; no attempt to unlock." : "change_prepare kind=plc_compile, payload={}, exact plcPath; then recapture." });
                    continue;
                }
                string path = Path.Combine(directory, "area-block-" + rows.Count + ".xml");
                block.Item.Export(new FileInfo(path), ExportOptions.WithDefaults);
                row["artifact"] = ChangeArtifact(path); row["declarations"] = ClassicScreenAreaReport.Declarations(HmiPreservationRules.ReadXml(File.ReadAllText(path)));
            }
            var types = new JArray();
            var typeDocuments = new Dictionary<string,System.Xml.Linq.XDocument>(StringComparer.Ordinal);
            foreach (string path in ChangeStringArray(area, "typePaths", 32, 4096, true))
            {
                var read = TypeRead(new JObject { ["projectName"] = project.Name, ["plcPath"] = plc.Path, ["typePath"] = path, ["artifactDirectory"] = directory });
                read.Remove("xml"); types.Add(read);
                string name = read["type"].Value<string>("name");
                if (typeDocuments.ContainsKey(name)) throw new BridgeException("AmbiguousTarget", "Selected PLC types have the same unqualified HMI datatype name.");
                typeDocuments.Add(name, HmiPreservationRules.ReadXml(File.ReadAllText(read["artifact"].Value<string>("path"))));
            }
            var documents = ReadClassicScreenDocuments(directory, manifest);
            var selected = area["prototypeScreens"] == null ? null : new HashSet<string>(ChangeStringArray(area, "prototypeScreens", 16, 4096), StringComparer.Ordinal);
            if (selected != null && selected.Any(p => !documents.ContainsKey(p))) throw new BridgeException("ObjectNotFound", "Prototype screens must be in the captured HMI.");
            var roots = area["hmiTagRoots"] == null ? null : new HashSet<string>(ChangeStringArray(area, "hmiTagRoots", 32, 4096), StringComparer.Ordinal);
            var tags = ReadClassicTagEvidence(directory, manifest["tagArtifacts"] as JArray ?? new JArray());
            var tagDocuments = (manifest["tagArtifacts"] as JArray ?? new JArray()).OfType<JObject>()
                .Select(a => HmiPreservationRules.ReadXml(File.ReadAllText(RequireScreenRecoveryFile(directory,a)))).ToArray();
            foreach(var finding in ClassicScreenAreaReport.BindingFindings(tagDocuments,typeDocuments,roots)) findings.Add(finding);
            if (roots != null)
                foreach (string root in roots.Where(r => !tags.Contains(r) && !tags.Contains("\"" + r + "\""))) findings.Add(new JObject { ["code"] = "HmiTagRootUnavailable", ["root"] = root });
            var report = new JObject { ["schemaVersion"] = 1, ["plcPath"] = plc.Path, ["blockFolderPath"] = folder, ["blocks"] = rows, ["types"] = types,
                ["hmiMembers"] = new JArray(tags.Where(t => roots == null || roots.Any(r => ClassicScreenPreflight.UsesRoot(t, r))).OrderBy(t => t, StringComparer.Ordinal)),
                ["prototypes"] = ClassicScreenAreaReport.Prototypes(documents, selected), ["findings"] = findings,
                ["limitations"] = new JArray("Native declarations, calls and existing HMI references are evidence for review, not inferred control meanings.",
                    "HMI datatype comparison checks captured non-array member presence only; array expansion and datatype conversion compatibility remain unverified.",
                    "Parameter indices, manual IDs and signal meaning must be checked in the retained source; runtime values and indirect dependencies are not evaluated."), ["runtimeVerified"] = false };
            string output = Path.Combine(directory, "area-preparation.json"); File.WriteAllText(output, report.ToString());
            return new JObject { ["artifact"] = ChangeArtifact(output), ["blockCount"] = rows.Count, ["typeCount"] = types.Count,
                ["prototypeCount"] = report["prototypes"]["items"].Count(), ["findings"] = findings.DeepClone(), ["semanticMappingsVerified"] = false };
        }
    }
}
