using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private void HmiExportInspectionSource(HmiDiscoveredTarget target, string section, JObject args, HmiCapture capture)
        {
            string path = args.Value<string>("objectPath") ?? (args["target"] as JObject ?? args["identity"] as JObject)?.Value<string>("objectPath");
            if (string.IsNullOrEmpty(path)) throw new BridgeException("InvalidRequest", "includeSource requires an exact objectPath and artifactDirectory; unbounded whole-target exports are not inspection previews.");
            var selected = capture.Entries.SingleOrDefault(e => e.Identity.Value<string>("objectPath") == path);
            if (selected == null) throw new BridgeException("ObjectNotFound", "The exact source export object was not found within the bounded inspection.");
            string directory = Path.Combine(ArtifactDirectory(args), "hmi-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            if (target.Family == "classic") HmiExportClassicSource(selected.Native, directory);
            else HmiExportUnifiedSource(target, selected, directory);
            var sourceCoverage = HmiInspectionRules.SourceCoverage(target.Family, selected.Native.GetType().FullName, target.Path, path);
            var sources = new JArray(); int total = 0;
            foreach (string filename in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Take(33))
            {
                if (sources.Count >= 32) { capture.Status = "truncated"; break; }
                var file = new FileInfo(filename); var artifact = FileArtifact(file); capture.Artifacts.Add(artifact.DeepClone());
                var source = new JObject { ["artifact"] = artifact, ["executed"] = false, ["parsedForBindings"] = false };
                string extension = file.Extension.ToLowerInvariant();
                if (new[] { ".xml", ".json", ".js", ".txt", ".vbs", ".yml", ".yaml" }.Contains(extension))
                {
                    if (file.Length <= 65536 && total + file.Length <= 196608) { source["text"] = File.ReadAllText(filename); total += (int)file.Length; }
                    else { source["textIncluded"] = false; source["reason"] = "Text omitted by bounded inspection; retained native artifact is available."; }
                }
                sources.Add(source);
            }
            HmiAttachMetadata(selected, "nativeSources", sources, capture);
            sourceCoverage["retainedFileCount"] = sources.Count;
            HmiAttachMetadata(selected, "nativeSourceCoverage", sourceCoverage, capture);
            if (!sourceCoverage.Value<bool>("selectedObjectOnly")) capture.Diagnostics.Add(new JObject { ["code"] = "HmiCompositionSourceExport", ["sourceCoverage"] = sourceCoverage.DeepClone() });
            if (sources.Count == 0) { capture.Status = "unavailable"; capture.Diagnostics.Add(new JObject { ["code"] = "HmiExportEmpty", ["reason"] = "The native selected-object export produced no top-level files." }); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void HmiExportClassicSource(IEngineeringObject native, string directory)
        {
            var file = new FileInfo(Path.Combine(directory, "native.xml"));
            if (native is Siemens.Engineering.Hmi.RuntimeScripting.VBScript script) script.Export(file, ExportOptions.WithDefaults);
            else if (native is Siemens.Engineering.Hmi.TextGraphicList.TextList text) text.Export(file, ExportOptions.WithDefaults);
            else if (native is Siemens.Engineering.Hmi.TextGraphicList.GraphicList graphic) graphic.Export(file, ExportOptions.WithDefaults);
            else if (native is Siemens.Engineering.Hmi.Communication.Connection connection) connection.Export(file, ExportOptions.WithDefaults);
            else if (native is Siemens.Engineering.Hmi.Tag.TagTable table) table.Export(file, ExportOptions.WithDefaults);
            else if (native is Siemens.Engineering.Hmi.Tag.Tag tag) tag.Export(file, ExportOptions.WithDefaults);
            else if (native is Siemens.Engineering.Hmi.Screen.Screen screen) screen.Export(file, ExportOptions.WithDefaults);
            else throw new BridgeException("HmiExportUnsupported", "No explicit native export route is implemented for this Classic object.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void HmiExportUnifiedSource(HmiDiscoveredTarget target, HmiNativeEntry selected, string directory)
        {
            var hmi = (Siemens.Engineering.HmiUnified.HmiSoftware)target.Software;
            var output = new DirectoryInfo(directory);
            if (selected.Native is Siemens.Engineering.HmiUnified.Scripts.HmiScriptModule script) script.Export(output);
            else if (selected.Native is Siemens.Engineering.HmiUnified.TextGraphicList.HmiTextList) hmi.HmiTextLists.Export(output, "native-text-lists");
            else if (selected.Native is Siemens.Engineering.HmiUnified.TextGraphicList.HmiGraphicList) hmi.HmiGraphicLists.Export(output, "native-graphic-lists");
            else throw new BridgeException("HmiExportUnsupported", "Only selected Unified script modules and text/graphic lists have an explicit inspection export adapter. No screen XML export is fabricated.");
        }
    }
}
