using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private JArray CaptureTagImportScope(EngineeringChange change, string phase)
        {
            var plc = SelectPlc(change.Selector);
            var scope = ResolveSoftwareScope(plc, change.ScopePath);
            var budget = new ScanBudget(50000);
            var entries = TagTables(scope.Tags, scope.Path + "/tags", budget).OrderBy(t => t.Path, StringComparer.Ordinal).ToArray();
            if (budget.Truncated || entries.Length > 500) throw new BridgeException("TraversalLimit", "Tag-table content capture is incomplete or exceeds 500 tables.");
            long total = 0; var result = new JArray();
            foreach (var entry in entries)
            {
                string file = Path.Combine(change.Directory, phase + "-tags-" + Guid.NewGuid().ToString("N") + ".xml");
                entry.Item.Export(new FileInfo(file), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                total += new FileInfo(file).Length;
                if (total > 32 * 1024 * 1024) throw new BridgeException("ResponseTooLarge", "Native tag-table capture exceeds 32 MiB.");
                var native = TagTableImportRules.Tables(EngineeringSources.ReadXml(File.ReadAllText(file))).Single();
                result.Add(new JObject { ["path"] = entry.Path, ["name"] = entry.Item.Name,
                    ["contentSha256"] = ChangeHash(TagTableImportRules.Canonical(native)), ["artifact"] = ChangeArtifact(file) });
            }
            return result;
        }

        private void PrepareScopedTagImport(EngineeringChange change)
        {
            change.TagTableEvidence = new JObject { ["policy"] = TagTableImportRules.Policy, ["before"] = CaptureTagImportScope(change, "before-scope") };
            change.Preview["tagTableImport"] = new JObject { ["policy"] = TagTableImportRules.Policy,
                ["tableCount"] = change.TagTableEvidence["before"].Count(), ["automaticArchiveRecovery"] = false,
                ["compilePolicy"] = "When compile=true, compare complete diagnostics before and after import; existing problems may remain, new problems fail verification." };
            ((JArray)change.Preview["warnings"]).Add("This declaration-only import does not fingerprint unrelated block payloads. Its archive remains available for manual recovery; scoped evidence cannot authorize automatic whole-project recovery or interrupted-outcome reconciliation.");
        }

        private void VerifyScopedTagImport(EngineeringChange change)
        {
            var before = ((JArray)change.TagTableEvidence["before"]).OfType<JObject>().ToDictionary(r => r.Value<string>("path"), StringComparer.Ordinal);
            var after = CaptureTagImportScope(change, "after-scope");
            change.TagTableEvidence["after"] = after;
            var actual = after.OfType<JObject>().ToDictionary(r => r.Value<string>("path"), StringComparer.Ordinal);
            var requested = TagTableImportRules.Tables(EngineeringSources.ReadXml(File.ReadAllText(change.InputPath)))
                .ToDictionary(t => change.GroupPath + "/" + Segment((string)t.Element("AttributeList").Element("Name")), StringComparer.Ordinal);
            if (!new System.Collections.Generic.HashSet<string>(before.Keys.Concat(requested.Keys), StringComparer.Ordinal).SetEquals(actual.Keys))
                throw new BridgeException("NativePreservationMismatch", "Tag-table import changed the software scope's table membership unexpectedly.");
            foreach (var row in before.Where(r => !requested.ContainsKey(r.Key)))
                if (row.Value.Value<string>("contentSha256") != actual[row.Key].Value<string>("contentSha256"))
                    throw new BridgeException("NativePreservationMismatch", "An unrequested tag table changed: " + row.Key);
            foreach (var request in requested)
            {
                var current = TagTableImportRules.Tables(EngineeringSources.ReadXml(File.ReadAllText(actual[request.Key]["artifact"].Value<string>("path")))).Single();
                var previous = before.TryGetValue(request.Key, out var prior)
                    ? TagTableImportRules.Tables(EngineeringSources.ReadXml(File.ReadAllText(prior["artifact"].Value<string>("path")))).Single() : null;
                TagTableImportRules.Verify(previous, request.Value, current);
            }
            var plc = SelectPlc(change.Selector);
            foreach (var table in MutationTagGroup(change, plc).TagTables.Where(t => change.ObjectNames.Contains(t.Name)))
                if (table.Tags.Any(t => t.IsSafety))
                    throw new BridgeException("SafetyBoundary", "Native import produced a safety tag; generic tag-table imports do not authorize safety-tag editing. The checkpoint is retained.");
            change.Stages.Add("tag_table_content_verified");
        }
    }
}
