using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // #216: after a compiled source_upsert, compare each existing block's native export before and after, so a refresh
        // from unchanged source shows whether number, interface or body changed. An observation, not a verdict: compiler
        // findings are compared by compile.baselineComparison, and no cause is inferred.
        private void CompareSourceRefresh(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            var blocks = new JArray();
            foreach (var backup in change.Backups.OfType<JObject>().Where(b => b["blockName"] != null))
            {
                string name = backup.Value<string>("blockName");
                var block = FindBlockByName(plc, name, false, change);
                if (block == null) { blocks.Add(new JObject { ["blockName"] = name, ["present"] = false }); continue; }
                string after = Path.Combine(change.Directory, "after-refresh-" + Guid.NewGuid().ToString("N") + ".xml");
                block.Item.Export(new FileInfo(after), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                var old = SourceRefreshRules.Parts(File.ReadAllText(backup["artifact"].Value<string>("path")));
                var now = SourceRefreshRules.Parts(File.ReadAllText(after));
                blocks.Add(new JObject { ["blockName"] = name, ["present"] = true,
                    ["numberBefore"] = old.Number, ["numberAfter"] = now.Number,
                    ["interfaceUnchanged"] = old.Interface == now.Interface, ["bodyUnchanged"] = old.Body == now.Body, ["layoutUnchanged"] = old.Formatted == now.Formatted,
                    ["before"] = backup["artifact"].DeepClone(), ["after"] = ChangeArtifact(after) });
            }
            var present = blocks.OfType<JObject>().Where(b => b.Value<bool>("present")).ToList();
            change.Readbacks.Add(new JObject { ["role"] = "source-refresh-comparison", ["blocks"] = blocks,
                ["logicChanged"] = present.Any(b => !b.Value<bool>("bodyUnchanged")),
                ["interfaceChanged"] = present.Any(b => !b.Value<bool>("interfaceUnchanged")),
                ["numbersPreserved"] = present.All(b => b.Value<string>("numberBefore") == b.Value<string>("numberAfter")),
                ["note"] = "Native exports before and after, compared without serialization IDs and token UIds; body ignores NewLine/Blank layout tokens, which layoutUnchanged reports. Compiler findings that disappeared or remained are in compile.baselineComparison; neither proves a cause." });
            change.Stages.Add("source_refresh_compared");
        }
    }
}
