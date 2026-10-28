using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.Alarm.Exceptions;
using Siemens.Engineering.SW.Supervision;

namespace TiaMcp.Openness.V21
{
    // D11 PLC alarm and ProDiag imports: a supplied hash-bound XLSX/DAT goes through the native provider of one
    // PLC/software unit, or of the project, with a fresh-export preimage, retained native result/log and a fresh-export readback.
    internal sealed partial class OpennessSession
    {
        private static readonly string[] PlcAlarmProjectKinds = { "plc_alarm_class_import", "plc_supervision_settings_import" };
        private static readonly string[] PlcAlarmOwnerKinds = { "plc_alarm_text_list_import", "plc_alarm_instance_text_import", "plc_supervision_import" };
        private static bool IsPlcAlarmKind(string kind) => PlcAlarmProjectKinds.Contains(kind) || PlcAlarmOwnerKinds.Contains(kind);
        private static string PlcAlarmExtension(string kind) => PlcAlarmProjectKinds.Contains(kind) ? ".dat" : ".xlsx";

        private void PreparePlcAlarmImport(EngineeringChange change, Project project, JObject preview)
        {
            var payload = change.Payload;
            string source = ChangeText(payload, "artifactPath", 4096), hash = ChangeText(payload, "expectedSha256", 64).ToLowerInvariant(), extension = PlcAlarmExtension(change.Kind);
            if (!ProjectPathIsAbsolute(source) || !File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 || !string.Equals(Path.GetExtension(source), extension, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("InvalidArtifact", "artifactPath must be an existing absolute, non-linked " + extension + " file.");
            // Apply imports this reviewed copy, so later edits to the supplied file cannot reach the project.
            change.InputPath = Path.Combine(change.Directory, "requested" + extension);
            File.Copy(source, change.InputPath);
            change.InputHash = ChangeFileHash(change.InputPath);
            if (change.InputHash != hash) throw new BridgeException("ArtifactChanged", "artifactPath does not match expectedSha256.");
            bool option = change.Kind == "plc_alarm_text_list_import" || change.Kind == "plc_supervision_import";
            if (option && ChangeText(payload, "importOption", 16) != "None" && payload.Value<string>("importOption") != "Override")
                throw new BridgeException("InvalidRequest", "importOption must be exactly None or Override.");
            if (!option && payload["importOption"] != null) throw new BridgeException("InvalidRequest", "This native provider has no import option.");
            var owner = PlcAlarmOwner(change, project);
            string before = Path.Combine(change.Directory, "before" + extension);
            change.Backups.Add(new JObject { ["role"] = "native-preimage", ["state"] = PlcAlarmExport(change.Kind, owner, before), ["artifact"] = File.Exists(before) ? ChangeArtifact(before) : null });
            var effect = new JObject { ["action"] = change.Kind, ["owner"] = change.ScopePath ?? "project", ["input"] = ChangeArtifact(change.InputPath), ["importOption"] = payload["importOption"]?.DeepClone() };
            var warnings = (JArray)preview["warnings"];
            if (change.Kind == "plc_alarm_text_list_import")
            {
                change.ObjectNames = ChangeNames(payload, "expectedTextListNames");
                var existing = PlcAlarmUserTextLists(change).Select(row => row.Value<string>("name")).ToArray();
                var present = change.ObjectNames.Where(name => existing.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (present.Length > 0 && payload.Value<string>("importOption") == "None")
                    throw new BridgeException("ObjectExists", "importOption None refuses existing text lists; use Override to replace their entry sets: " + string.Join(", ", present));
                effect["replaceTextLists"] = new JArray(present);
                effect["createTextLists"] = new JArray(change.ObjectNames.Except(present, StringComparer.OrdinalIgnoreCase));
                warnings.Add("Override replaces each supplied list's complete entry set rather than merging; unmentioned lists remain. Never import partial translation rows as a replacement list.");
            }
            else if (change.Kind == "plc_alarm_instance_text_import") warnings.Add("Updates texts of existing alarm instances in all workbook languages; instances are not created and inactive languages are not activated.");
            else if (change.Kind == "plc_supervision_import") warnings.Add("ProDiag supervisions only; supervision settings XLSX import is a separate native route that this kind does not call.");
            else warnings.Add("Project-wide: every PLC and HMI consumer of these settings is affected, and apply compiles every device in the project.");
            warnings.Add("Native Warning results are accepted but can describe a partial import; review the retained native result, log and fresh-export readback.");
            ((JArray)preview["effects"]).Add(effect);
        }

        private void ApplyPlcAlarmImport(EngineeringChange change, Project project)
        {
            var owner = PlcAlarmOwner(change, project);
            var input = new FileInfo(change.InputPath);
            var option = change.Payload.Value<string>("importOption") == "Override" ? ImportOptions.Override : ImportOptions.None;
            JObject result; FileInfo log = null; bool failed;
            switch (change.Kind)
            {
                case "plc_alarm_text_list_import":
                { var r = PlcAlarmService<PlcAlarmTextListProvider>(owner).ImportFromXlsx(input, option); log = r.LogFilePath; failed = r.State == TextListXlsxResultState.Error; result = new JObject { ["state"] = r.State.ToString() }; break; }
                case "plc_alarm_instance_text_import":
                { var r = PlcAlarmService<PlcAlarmTextProvider>(owner).ImportInstanceTextsFromXlsx(input, null); log = r.LogFilePath; failed = r.State == PlcAlarmTextXlsxResultState.Error; result = new JObject { ["state"] = r.State.ToString() }; break; }
                case "plc_supervision_import":
                { var r = PlcAlarmService<SupervisionProvider>(owner).ImportSupervisionsFromXlsx(input, option); log = r.LogFilePath; failed = r.State != SupervisionXlsxResultState.Success; result = new JObject { ["state"] = r.State.ToString() }; break; }
                case "plc_alarm_class_import":
                {
                    var r = PlcAlarmService<AlarmClassDataProvider>(owner).Import(input);
                    failed = r.State == AlarmClassExportImportResultState.Error;
                    result = new JObject { ["state"] = r.State.ToString(), ["errorCount"] = r.ErrorCount, ["warningCount"] = r.WarningCount,
                        ["messages"] = new JArray(r.Messages.Take(500).Select(m => new JObject { ["state"] = m.State.ToString(), ["message"] = m.Message })) };
                    break;
                }
                default:
                {
                    var r = PlcAlarmService<SupervisionSettingsProvider>(owner).Import(input);
                    failed = r.State != SupervisionSettingsExportImportResultState.Success;
                    result = new JObject { ["state"] = r.State.ToString(), ["errorCount"] = r.ErrorCount, ["warningCount"] = r.WarningCount,
                        ["messages"] = new JArray(r.Messages.Take(500).Select(m => m.Message)) };
                    break;
                }
            }
            if (log != null && File.Exists(log.FullName))
            {
                string copy = Path.Combine(change.Directory, "import-log" + log.Extension);
                File.Copy(log.FullName, copy, true);
                result["log"] = ChangeArtifact(copy);
            }
            change.Readbacks.Add(new JObject { ["role"] = "native-import-result", ["result"] = result });
            change.Stages.Add("native_import_returned");
            if (failed) throw new BridgeException("ImportFailed", "Native " + change.Kind + " returned " + result.Value<string>("state") + "; the native result and log are retained.");
            string after = Path.Combine(change.Directory, "after" + PlcAlarmExtension(change.Kind));
            change.Readbacks.Add(new JObject { ["role"] = "native-export-readback", ["state"] = PlcAlarmExport(change.Kind, owner, after), ["artifact"] = File.Exists(after) ? ChangeArtifact(after) : null });
            if (change.Kind != "plc_alarm_text_list_import") return;
            var lists = PlcAlarmUserTextLists(change);
            change.Readbacks.Add(new JObject { ["role"] = "user-text-lists", ["textLists"] = lists });
            var missing = change.ObjectNames.Where(name => !lists.Any(row => string.Equals(row.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (missing.Length > 0) throw new BridgeException("MutationVerificationFailed", "Expected text lists are missing after import: " + string.Join(", ", missing));
        }

        private IEngineeringServiceProvider PlcAlarmOwner(EngineeringChange change, Project project)
        {
            string payloadScope = change.Payload.Value<string>("scopePath"), selectorScope = change.Selector.Value<string>("scopePath");
            if (PlcAlarmProjectKinds.Contains(change.Kind))
            {
                if (payloadScope != null || selectorScope != null) throw new BridgeException("InvalidRequest", "Project-wide alarm class and supervision settings imports take no scopePath.");
                return project;
            }
            if (payloadScope != null && selectorScope != null && payloadScope != selectorScope) throw new BridgeException("ScopeMismatch", "Top-level and payload scopePath selectors disagree.");
            var plc = SelectPlc(change.Selector);
            var scope = ResolveSoftwareScope(plc, change.ScopePath ?? payloadScope ?? selectorScope ?? plc.Path);
            if (scope.Kind == "safetyUnit") throw new BridgeException("SafetyBoundary", "Safety units are excluded from PLC alarm and ProDiag imports.");
            change.ScopePath = scope.Path;
            return (IEngineeringServiceProvider)scope.Unit ?? plc.Software;
        }

        private JArray PlcAlarmUserTextLists(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            var scope = ResolveSoftwareScope(plc, change.ScopePath);
            var lists = (scope.Unit?.PlcAlarmTextlistGroup ?? plc.Software.PlcAlarmTextlistGroup).PlcAlarmUserTextlists.Take(10001)
                .Select(list => new JObject { ["name"] = list.Name, ["id"] = list.ID, ["listRange"] = list.ListRange.ToString() }).ToArray();
            if (lists.Length > 10000) throw new BridgeException("TraversalLimit", "More than 10000 user text lists; the inventory is incomplete.");
            return new JArray(lists);
        }

        private static T PlcAlarmService<T>(IEngineeringServiceProvider owner) where T : class, IEngineeringService
            => owner.GetService<T>() ?? throw new BridgeException("UnsupportedCapability", "The selected owner exposes no " + typeof(T).Name + "; the alarm/ProDiag product or license may be unavailable.");

        // Exports the artifact type the kind imports and returns the native result state.
        private static string PlcAlarmExport(string kind, IEngineeringServiceProvider owner, string file)
        {
            var target = new FileInfo(file);
            try
            {
                switch (kind)
                {
                    case "plc_alarm_text_list_import": return PlcAlarmService<PlcAlarmTextListProvider>(owner).ExportToXlsx(target).State.ToString();
                    case "plc_alarm_instance_text_import": return PlcAlarmService<PlcAlarmTextProvider>(owner).ExportInstanceTextsToXlsx(target, null, PlcAlarmTextXlsxExportOption.All).State.ToString();
                    case "plc_supervision_import": return PlcAlarmService<SupervisionProvider>(owner).ExportSupervisionsToXlsx(target).State.ToString();
                    case "plc_alarm_class_import": return PlcAlarmService<AlarmClassDataProvider>(owner).Export(target).State.ToString();
                    default: return PlcAlarmService<SupervisionSettingsProvider>(owner).Export(target).State.ToString();
                }
            }
            catch (TextListNotFoundException) { return "noTextLists"; }
            catch (Exception error) when (error is LicenseNotFoundException || error is MissingProductsException)
            { throw new BridgeException("LicenseRequired", "Native " + kind + " provider refused: " + error.Message); }
        }

        // Content hash of a fresh native export of the kind's artifact type for every PLC/unit owner (or the project),
        // so prepare/apply/restore and a fresh reconciliation session compare outcomes without in-memory targets.
        private static JObject PlcAlarmFingerprint(string kind, Project project)
        {
            var rows = new JObject();
            string directory = Path.Combine(Path.GetTempPath(), "tiamcp-alarm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                if (PlcAlarmProjectKinds.Contains(kind)) { rows["project"] = PlcAlarmSnapshot(kind, project, directory); return rows; }
                var budget = new ScanBudget(50000);
                foreach (var entry in DeviceItems(project, budget))
                {
                    var plc = entry.Item.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                    if (plc == null) continue;
                    foreach (var scope in SoftwareScopes(new PlcSelection { Project = project, Software = plc, Path = entry.Path + "/plc/" + Segment(plc.Name) }, budget))
                        if (scope.Kind != "safetyUnit") rows[scope.Path] = PlcAlarmSnapshot(kind, (IEngineeringServiceProvider)scope.Unit ?? plc, directory);
                }
                if (budget.Truncated) throw new BridgeException("TraversalLimit", "Cannot establish a complete alarm export fingerprint for this project size.");
                return rows;
            }
            finally { try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        private static JObject PlcAlarmSnapshot(string kind, IEngineeringServiceProvider owner, string directory)
        {
            string file = Path.Combine(directory, Guid.NewGuid().ToString("N") + PlcAlarmExtension(kind));
            try { return new JObject { ["state"] = PlcAlarmExport(kind, owner, file), ["contentSha256"] = File.Exists(file) ? PlcAlarmContentHash(file) : null }; }
            // Type names only: native messages can carry paths or times and would make the fingerprint unstable.
            catch (BridgeException error) { return new JObject { ["unavailable"] = error.Code }; }
            catch (EngineeringException error) { return new JObject { ["exportFailed"] = error.GetType().FullName }; }
        }

        // Native evidence (28 October 2026): every export carries save times in docProps and zip headers, a DAT package names
        // its single entry after the export file and stamps its XML with CreationDate, and each XLSX export draws fresh random
        // relationship IDs. Hash only the decompressed parts, DAT entries by position without CreationDate and XLSX
        // relationship IDs renumbered by first use.
        private static string PlcAlarmContentHash(string file)
        {
            bool xlsx = file.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
            ZipArchive archive;
            try { archive = ZipFile.OpenRead(file); }
            catch (InvalidDataException) { return ChangeFileHash(file); }
            var parts = new JObject();
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            using (archive)
            {
                var entries = xlsx ? archive.Entries.Where(e => !e.FullName.StartsWith("docProps/", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName, StringComparer.Ordinal).ToArray() : archive.Entries.ToArray();
                for (int index = 0; index < entries.Length; index++)
                    using (var stream = entries[index].Open()) using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        byte[] bytes = memory.ToArray();
                        if (xlsx) bytes = Encoding.UTF8.GetBytes(RelationshipIds.Replace(Encoding.UTF8.GetString(bytes),
                            match => match.Groups[1].Value + (ids.TryGetValue(match.Groups[2].Value, out var id) ? id : ids[match.Groups[2].Value] = "R" + ids.Count) + "\""));
                        else bytes = Encoding.UTF8.GetBytes(CreationDate.Replace(Encoding.UTF8.GetString(bytes), ""));
                        using (var sha = SHA256.Create())
                            parts[xlsx ? entries[index].FullName : "#" + index] = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    }
            }
            return ChangeHash(parts.ToString(Formatting.None));
        }

        private static readonly Regex RelationshipIds = new Regex("((?:\\br:id|\\bId)=\")(R[0-9a-f]{16})\"", RegexOptions.CultureInvariant);
        private static readonly Regex CreationDate = new Regex("\\sCreationDate=\"[^\"]*\"", RegexOptions.CultureInvariant);
    }
}
