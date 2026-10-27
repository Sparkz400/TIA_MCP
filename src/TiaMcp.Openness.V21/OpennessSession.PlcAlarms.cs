using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.Alarm.TextLists;
using Siemens.Engineering.SW.Supervision;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private JObject PlcAlarmInspect(JObject args)
        {
            var plc = SelectInspectionPlc(args, "scopePath");
            string wanted = args.Value<string>("scopePath");
            string directory = ArtifactDirectory(args);
            var budget = new ScanBudget(Limit(args, 500));
            var warnings = new JArray();
            var scopes = SoftwareScopes(plc, budget, warnings).Where(scope => string.IsNullOrWhiteSpace(wanted) || scope.Path == wanted).ToList();
            if (!string.IsNullOrWhiteSpace(wanted)) RequireOne(scopes.Count, budget, "PLC or software-unit scope");
            var rows = new JArray();
            foreach (var scope in scopes)
            {
                var row = new JObject { ["path"] = scope.Path, ["name"] = scope.Name, ["kind"] = scope.Kind };
                rows.Add(row);
                row["textLists"] = AlarmPart(scope.Path + "/alarmTextLists", warnings, () => AlarmTextLists(AlarmTextListGroup(plc, scope), scope.Path + "/alarmTextLists", budget));
                // V21 exports only user text lists (with entries) to XLSX and throws when a scope has none.
                row["userTextListExport"] = AlarmPart(scope.Path + "/alarmTextLists/export", warnings, () =>
                {
                    var provider = AlarmService<PlcAlarmTextListProvider>(plc, scope);
                    if (provider == null) return AlarmNotExposed();
                    var file = AlarmFile(directory, "alarm-text-lists", ".xlsx");
                    var export = provider.ExportToXlsx(file);
                    return AlarmXlsx(file, export.State.ToString(), export.LogFilePath, budget, warnings);
                });
                row["alarmInstanceTextExport"] = AlarmPart(scope.Path + "/alarmTexts/export", warnings, () =>
                {
                    var provider = AlarmService<PlcAlarmTextProvider>(plc, scope);
                    if (provider == null) return AlarmNotExposed();
                    var file = AlarmFile(directory, "alarm-instance-texts", ".xlsx");
                    var export = provider.ExportInstanceTextsToXlsx(file, null, PlcAlarmTextXlsxExportOption.All);
                    return AlarmXlsx(file, export.State.ToString(), export.LogFilePath, budget, warnings);
                });
                row["supervisionExport"] = AlarmPart(scope.Path + "/supervisions/export", warnings, () =>
                {
                    var provider = AlarmService<SupervisionProvider>(plc, scope);
                    if (provider == null) return AlarmNotExposed();
                    var file = AlarmFile(directory, "supervisions", ".xlsx");
                    var export = provider.ExportSupervisionsToXlsx(file);
                    return AlarmXlsx(file, export.State.ToString(), export.LogFilePath, budget, warnings);
                });
            }
            var result = Envelope();
            result["project"] = ProjectInfo(plc.Project);
            result["plcPath"] = plc.Path;
            result["activeCultures"] = new JArray(plc.Project.LanguageSettings.ActiveLanguages.Select(language => language.Culture.Name));
            result["scopes"] = rows;
            result["alarmClassExport"] = AlarmPart("project/alarmClasses/export", warnings, () =>
            {
                var provider = plc.Project.GetService<AlarmClassDataProvider>();
                if (provider == null) return AlarmNotExposed();
                var file = AlarmFile(directory, "alarm-classes", ".dat");
                var export = provider.Export(file);
                return AlarmDat(file, export.State.ToString(), export.ErrorCount, export.WarningCount, export.Messages.Select(m => new JObject { ["state"] = m.State.ToString(), ["message"] = m.Message }), budget);
            });
            result["supervisionSettingsExport"] = AlarmPart("project/supervisionSettings/export", warnings, () =>
            {
                var provider = plc.Project.GetService<SupervisionSettingsProvider>();
                if (provider == null) return AlarmNotExposed();
                var file = AlarmFile(directory, "supervision-settings", ".dat");
                var export = provider.Export(file);
                return AlarmDat(file, export.State.ToString(), export.ErrorCount, export.WarningCount, export.Messages.Select(m => new JObject { ["message"] = m.Message }), budget);
            });
            result["warnings"] = warnings;
            result["completeness"] = InspectionCoverage(budget, warnings, "Offline PLC/software-unit alarm text-list identities and the native exports of user text lists, alarm instance texts, global ProDiag supervisions, project alarm classes and ProDiag settings.",
                "Worksheet rows are the exported cell text by column; the Siemens column layout is not interpreted and rows are bounded by maxItems",
                "V21 exposes no system text-list entries; only user text lists are exported",
                "Alarm-class and ProDiag-setting .dat exports are retained with SHA-256 but not parsed",
                "Supervisions owned by individual ProDiag FBs are not exported",
                "An unavailable part carries the native message (for example no user text list or a missing ProDiag option)",
                "No runtime alarm state, acknowledgement or text substitution");
            return result;
        }

        private static PlcAlarmTextlistGroup AlarmTextListGroup(PlcSelection plc, SoftwareScope scope) => scope.Unit != null ? scope.Unit.PlcAlarmTextlistGroup : plc.Software.PlcAlarmTextlistGroup;
        private static T AlarmService<T>(PlcSelection plc, SoftwareScope scope) where T : class, IEngineeringService => scope.Unit != null ? scope.Unit.GetService<T>() : plc.Software.GetService<T>();
        private static JObject AlarmNotExposed() => new JObject { ["status"] = "notExposed" };
        private static FileInfo AlarmFile(string directory, string prefix, string extension) => new FileInfo(Path.Combine(directory, prefix + "-" + Guid.NewGuid().ToString("N") + extension));

        // One failing alarm service (missing option, license or empty list) must not hide the others.
        private static JObject AlarmPart(string path, JArray warnings, Func<JObject> read)
        {
            try { return read(); }
            catch (Exception exception)
            {
                warnings.Add(InspectionWarning(path, exception));
                return new JObject { ["status"] = "unavailable", ["exceptionType"] = exception.GetType().Name, ["message"] = exception.Message };
            }
        }

        private static JObject AlarmTextLists(PlcAlarmTextlistGroup group, string path, ScanBudget budget)
        {
            if (group == null) return AlarmNotExposed();
            var items = new JArray();
            foreach (var list in group.PlcAlarmUserTextlists) { if (!budget.Take()) break; items.Add(AlarmTextListRow(list, path + "/user/", "user")); }
            foreach (var list in group.PlcAlarmSystemTextlists) { if (!budget.Take()) break; items.Add(AlarmTextListRow(list, path + "/system/", "system")); }
            return new JObject { ["status"] = "observed", ["items"] = items, ["truncated"] = budget.Truncated };
        }

        private static JObject AlarmTextListRow(PlcAlarmTextlist list, string prefix, string kind)
        {
            var row = new JObject { ["path"] = prefix + Segment(list.Name), ["name"] = list.Name };
            row.Merge(AlarmTextListInfo(list, kind));
            return row;
        }

        private static JObject AlarmTextListInfo(PlcAlarmTextlist list, string kind)
        {
            var comment = new JObject();
            foreach (var item in list.Comment?.Items ?? Enumerable.Empty<MultilingualTextItem>()) comment[item.Language.Culture.Name] = InspectionValue(item.Text);
            return new JObject { ["textListKind"] = kind, ["id"] = list.ID, ["listRange"] = list.ListRange.ToString(), ["comment"] = comment };
        }

        private static JObject AlarmXlsx(FileInfo file, string state, FileInfo log, ScanBudget budget, JArray warnings)
        {
            file.Refresh();
            var part = new JObject { ["status"] = "observed", ["nativeState"] = state, ["logFilePath"] = log?.FullName };
            if (!file.Exists) { part["status"] = "noFile"; return part; }
            var artifact = FileArtifact(file); artifact["mediaType"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            part["artifact"] = artifact;
            try { part["sheets"] = AlarmWorkbook.Read(file.FullName, budget.Take); }
            catch (Exception exception) { part["sheetsStatus"] = "unreadable"; warnings.Add(InspectionWarning(file.FullName, exception)); }
            return part;
        }

        private static JObject AlarmDat(FileInfo file, string state, int errors, int warnings, IEnumerable<JObject> messages, ScanBudget budget)
        {
            file.Refresh();
            var part = new JObject { ["status"] = file.Exists ? "observed" : "noFile", ["nativeState"] = state, ["errorCount"] = errors, ["warningCount"] = warnings,
                ["messages"] = new JArray(messages.TakeWhile(_ => budget.Take())) };
            if (file.Exists) { var artifact = FileArtifact(file); artifact["mediaType"] = "application/octet-stream"; part["artifact"] = artifact; }
            return part;
        }
    }
}
