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
            ((JArray)change.Preview["warnings"]).Add("This declaration-only import does not fingerprint unrelated block payloads. change_restore restores only the imported tables from their retained native exports, and only while every captured table is as the attempt left it; the archive remains available for manual recovery. Scoped evidence cannot authorize whole-project recovery or interrupted-outcome reconciliation.");
        }

        private void VerifyScopedTagImport(EngineeringChange change)
        {
            var before = ((JArray)change.TagTableEvidence["before"]).OfType<JObject>().ToDictionary(r => r.Value<string>("path"), StringComparer.Ordinal);
            var after = CaptureTagImportScope(change, "after-scope");
            change.TagTableEvidence["after"] = after;
            var actual = after.OfType<JObject>().ToDictionary(r => r.Value<string>("path"), StringComparer.Ordinal);
            var requested = RequestedScopedTables(change);
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

        private System.Collections.Generic.Dictionary<string, System.Xml.Linq.XElement> RequestedScopedTables(EngineeringChange change) =>
            TagTableImportRules.Tables(EngineeringSources.ReadXml(File.ReadAllText(change.InputPath)))
                .ToDictionary(t => change.GroupPath + "/" + Segment((string)t.Element("AttributeList").Element("Name")), StringComparer.Ordinal);

        private static System.Xml.Linq.XElement CapturedTable(JObject row) =>
            TagTableImportRules.Tables(EngineeringSources.ReadXml(File.ReadAllText(row["artifact"].Value<string>("path")))).Single();

        private static System.Collections.Generic.Dictionary<string, JObject> ByPath(JArray rows) =>
            rows.OfType<JObject>().ToDictionary(r => r.Value<string>("path"), StringComparer.Ordinal);

        // #218: restore only the requested tables from their retained native exports, in place. Allowed only while every
        // captured table is exactly as the attempt left it, so later edits there refuse; edits outside tag tables are kept.
        private JObject RestoreScopedTagImport(EngineeringChange change, JObject args)
        {
            if (change.State != "applied" && change.State != "recovery_required") throw new BridgeException("InvalidChangeState", "This change has no applied state to recover.");
            if (ChangeText(args, "previewHash", 64) != change.PreviewHash) throw new BridgeException("PreviewMismatch", "Wrong previewHash.");
            if (!change.Stages.Any(s => (string)s == "mutation_applied"))
                throw new BridgeException("ScopedRecoveryRequired", "The import is not recorded as applied, so no scoped state can be proven to restore. Use the retained archive for explicit manual recovery; nothing was changed.", ChangeView(change));
            var project = ProjectForChange(change);
            EnsureProjectOffline(project);
            var before = ByPath((JArray)change.TagTableEvidence["before"]);
            var requested = RequestedScopedTables(change);
            using (SharedMutationExclusive("TIA MCP: scoped tag-table restore"))
            {
                // The retained exports are re-imported, so they must still hash as captured.
                ValidatePreparedFiles(change);
                var current = ByPath(CaptureTagImportScope(change, "pre-restore-scope"));
                try
                {
                    if (!new System.Collections.Generic.HashSet<string>(before.Keys.Concat(requested.Keys), StringComparer.Ordinal).SetEquals(current.Keys))
                        throw new BridgeException("StaleRevision", "The software scope's tag tables differ from the attempt's outcome. Nothing was restored.", ChangeView(change));
                    foreach (var row in before.Where(r => !requested.ContainsKey(r.Key)))
                        if (row.Value.Value<string>("contentSha256") != current[row.Key].Value<string>("contentSha256"))
                            throw new BridgeException("StaleRevision", "An unrequested tag table changed after the attempt: " + row.Key + ". Nothing was restored.", ChangeView(change));
                    foreach (var request in requested)
                        TagTableImportRules.Verify(before.TryGetValue(request.Key, out var prior) ? CapturedTable(prior) : null, request.Value, CapturedTable(current[request.Key]));
                }
                catch (BridgeException ex) when (ex.Code != "StaleRevision")
                {
                    throw new BridgeException("StaleRevision", "A requested tag table differs from the attempt's outcome (" + ex.Message + "). Nothing was restored.", ChangeView(change));
                }
                change.State = "restoring";
                change.Stages.Add("scoped_restore_state_verified");
                WriteChangeJournal(change);
                try
                {
                    var group = MutationTagGroup(change, SelectPlc(change.Selector));
                    foreach (var request in requested)
                    {
                        if (before.TryGetValue(request.Key, out var prior))
                            group.TagTables.Import(new FileInfo(prior["artifact"].Value<string>("path")), ImportOptions.Override);
                        else group.TagTables.Single(t => string.Equals(t.Name, (string)request.Value.Element("AttributeList").Element("Name"), StringComparison.OrdinalIgnoreCase)).Delete();
                    }
                    change.Stages.Add("scoped_tables_restored");
                    WriteChangeJournal(change);
                    var restored = ByPath(CaptureTagImportScope(change, "restored-scope"));
                    if (!new System.Collections.Generic.HashSet<string>(before.Keys, StringComparer.Ordinal).SetEquals(restored.Keys)
                        || before.Any(r => r.Value.Value<string>("contentSha256") != restored[r.Key].Value<string>("contentSha256")))
                        throw new BridgeException("RecoveryVerificationFailed", "Restored tag tables differ from their retained exports.");
                    foreach (var table in MutationTagGroup(change, SelectPlc(change.Selector)).TagTables.Where(t => change.ObjectNames.Contains(t.Name)))
                        if (table.Tags.Any(t => t.IsSafety))
                            throw new BridgeException("SafetyBoundary", "The restored table holds a safety tag; generic tag-table restores do not authorize safety-tag editing.");
                    change.Stages.Add("scoped_restore_content_verified");
                    var compilation = CompileEngineeringChange(change, project);
                    var comparison = change.CompileBaseline != null ? CompileBaselineRules.Compare(change.CompileBaseline, compilation) : null;
                    change.Readbacks.Add(new JObject { ["role"] = "scoped-restore-compile", ["compile"] = compilation, ["baselineComparison"] = comparison });
                    if (comparison != null ? comparison.Value<bool>("verified") != true : !CompilationEvidenceRules.IsCompleted(compilation))
                        throw new BridgeException("CompileFailed", "The restored project does not compile like the baseline before the import.");
                    change.Stages.Add("scoped_restore_compile_verified");
                    project.Save();
                    change.Stages.Add("scoped_restore_saved");
                    change.State = "restored";
                    change.RecoveredProject = ProjectInfo(project);
                    WriteChangeJournal(change);
                    return ChangeView(change);
                }
                catch (Exception ex)
                {
                    change.State = "recovery_required";
                    change.RecoveryFailure = "Scoped restore did not complete: " + ex.Message;
                    WriteChangeJournal(change);
                    throw;
                }
            }
        }
    }
}
