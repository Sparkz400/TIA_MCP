#nullable disable
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SafetyValidation;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static bool IsFailsafeAttribute(string name) => name.StartsWith("Failsafe_", StringComparison.Ordinal);

        // Adds F-parameters and Safety Validation Assistant data to a safety_inspect result, keeping its field names for safety_evidence_review.
        private static void InspectSafetyValidation(PlcSelection plc, DeviceItem cpu, JObject args, ScanBudget budget, JArray warnings, JObject result)
        {
            bool export = args.Value<bool?>("exportArtifacts") == true;
            result["plcFParameters"] = InspectionAttributes(cpu, budget, warnings, plc.Path + "/safety/fParameters", include: IsFailsafeAttribute);
            var central = FailsafeValue(cpu, "Failsafe_CentralFSourceAddress");
            var modules = new JArray();
            int otherSource = 0;
            var lookup = new ScanBudget(50000);
            foreach (var entry in DeviceItems(plc.Project, lookup))
            {
                bool fIo;
                try { fIo = entry.Item.GetAttributeInfos().Any(info => info.Name == "Failsafe_FDestinationAddress"); }
                catch (Exception) { warnings.Add(new JObject { ["path"] = entry.Path, ["message"] = "Native attribute metadata could not be captured." }); continue; }
                if (!fIo) continue;
                var source = FailsafeValue(entry.Item, "Failsafe_FSourceAddress");
                bool? matches = central?.Type == JTokenType.Integer && source?.Type == JTokenType.Integer ? JToken.DeepEquals(central, source) : (bool?)null;
                if (matches == false) { otherSource++; continue; }
                if (!budget.Take()) break;
                modules.Add(new JObject { ["path"] = entry.Path, ["name"] = entry.Item.Name, ["typeIdentifier"] = entry.Item.TypeIdentifier,
                    ["fSourceAddressMatchesPlc"] = matches, ["fParameters"] = InspectionAttributes(entry.Item, budget, warnings, entry.Path, include: IsFailsafeAttribute) });
            }
            if (lookup.Truncated) warnings.Add("F-I/O lookup reached its 50000-item traversal bound.");
            result["fIoModules"] = modules;
            result["fIoModulesWithOtherFSource"] = otherSource;

            JObject validation = null;
            try { validation = SafetyValidationInventory(plc.Project, cpu.Name, args, export, budget, warnings); }
            catch (FileNotFoundException exception) when (exception.FileName?.StartsWith("Siemens.Engineering.SafetyValidation", StringComparison.Ordinal) == true) { }
            if (validation == null) warnings.Add(new JObject { ["path"] = plc.Path + "/safetyValidation", ["message"] = "Safety Validation Assistant is unavailable; activation tests were not captured." });
            result["safetyValidation"] = validation ?? new JObject { ["available"] = false, ["code"] = "UnsupportedCapability",
                ["message"] = "The SafetyValidationAssistant project service or its assembly is not available." };

            if (!export) return;
            var printout = cpu.GetService<SafetyPrintout>();
            if (printout == null) { warnings.Add(new JObject { ["path"] = plc.Path + "/safety/printout", ["message"] = "SafetyPrintout is not exposed on the PLC DeviceItem." }); return; }
            var pdf = new FileInfo(Path.Combine(ArtifactDirectory(args), "safety-printout-" + Guid.NewGuid().ToString("N") + ".pdf"));
            try
            {
                bool printed = printout.Print(SafetyPrintoutFilePrinter.MicrosoftPrintToPdf, pdf, "DocuInfo_ISO_A4_Portrait", SafetyPrintoutOption.All);
                pdf.Refresh();
                if (!printed || !pdf.Exists) warnings.Add(new JObject { ["path"] = plc.Path + "/safety/printout", ["message"] = "SafetyPrintout.Print did not produce a file." });
                else { var artifact = FileArtifact(pdf); artifact["mediaType"] = "application/pdf"; result["safetyPrintout"] = artifact; }
            }
            catch (Exception exception) { warnings.Add(InspectionWarning(plc.Path + "/safety/printout", exception)); }
        }

        private static JToken FailsafeValue(DeviceItem item, string name)
        {
            try { return InspectionValue(item.GetAttribute(name)); }
            catch (Exception) { return null; }
        }

        // Not inlined: Siemens.Engineering.SafetyValidation.dll is loaded only when this method is JIT-compiled.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static JObject SafetyValidationInventory(Project project, string plcName, JObject args, bool export, ScanBudget budget, JArray warnings)
        {
            var assistant = project.GetService<SafetyValidationAssistant>();
            if (assistant == null) return null;
            var groups = new JArray();
            var tests = new JArray();
            Visit(assistant.ActivationTests, assistant.ActivationTestGroups, "/projects/" + Segment(project.Name) + "/activationTests", 0);
            return new JObject { ["available"] = true, ["activationTestGroups"] = groups, ["activationTests"] = tests };

            void Visit(ActivationTestComposition activationTests, ActivationTestUserGroupComposition userGroups, string groupPath, int depth)
            {
                if (depth > 64) { budget.Truncated = true; return; }
                foreach (var test in activationTests)
                {
                    if (!budget.Take()) return;
                    string path = groupPath + "/" + Segment(test.Name);
                    var row = new JObject { ["path"] = path, ["groupPath"] = groupPath, ["name"] = test.Name };
                    tests.Add(row);
                    try
                    {
                        row["author"] = InspectionValue(test.Author);
                        row["evaluationDeviceName"] = test.EvaluationDeviceName;
                        row["evaluationDeviceNameMatchesPlc"] = string.Equals(test.EvaluationDeviceName, plcName, StringComparison.Ordinal);
                        row["overallState"] = InspectionValue(test.OverallState);
                        row["validity"] = Validity(test);
                        var functions = new JArray();
                        row["safetyFunctions"] = functions;
                        foreach (var function in test.SafetyFunctions)
                        {
                            if (!budget.Take()) break;
                            var conditions = new JArray();
                            foreach (var condition in function.Conditions)
                            {
                                if (!budget.Take()) break;
                                conditions.Add(new JObject { ["signalUsage"] = InspectionValue(condition.SignalUsage), ["deviceName"] = condition.DeviceName,
                                    ["signalName"] = condition.SignalName, ["comment"] = InspectionValue(condition.Comment),
                                    ["initialInput"] = InspectionValue(condition.InitialInput), ["executedInput"] = InspectionValue(condition.ExecutedInput),
                                    ["response"] = InspectionValue(condition.Response), ["validity"] = Validity(condition) });
                            }
                            var trace = function.TraceConfiguration;
                            functions.Add(new JObject { ["name"] = function.Name, ["testName"] = function.TestName, ["description"] = InspectionValue(function.Description),
                                ["testState"] = InspectionValue(function.TestState), ["isTraced"] = trace?.IsTraced, ["traceValidity"] = Validity(trace),
                                ["validity"] = Validity(function), ["conditions"] = conditions });
                        }
                        if (export)
                        {
                            var file = NewInspectionArtifact(args, "activation-test");
                            test.Export(file, ExportOptions.WithDefaults);
                            row["artifact"] = FileArtifact(file);
                            var printout = test.GetService<ActivationTestPrintout>();
                            if (printout == null) warnings.Add(new JObject { ["path"] = path, ["message"] = "ActivationTestPrintout is not exposed." });
                            else
                            {
                                var report = new FileInfo(Path.ChangeExtension(file.FullName, ".xlsx"));
                                printout.Generate(report);
                                row["printout"] = FileArtifact(report);
                                row["printout"]["mediaType"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                            }
                        }
                    }
                    catch (Exception exception) { warnings.Add(InspectionWarning(path, exception)); }
                }
                foreach (var group in userGroups)
                {
                    if (!budget.Take()) return;
                    string child = groupPath + "/groups/" + Segment(group.Name);
                    groups.Add(child);
                    Visit(group.ActivationTests, group.Groups, child, depth + 1);
                }
            }

            JObject Validity(IEngineeringServiceProvider item)
            {
                var check = item?.GetService<TestValidity>()?.CheckValidity();
                return check == null ? null : new JObject { ["state"] = InspectionValue(check.State), ["errorCount"] = check.ErrorCount, ["messages"] = InspectionValue(check.Messages) };
            }
        }
    }
}
