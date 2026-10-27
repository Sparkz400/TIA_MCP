#nullable disable
using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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

            result["safetyValidation"] = SafetyValidationSection(plc, cpu, args, export, budget, warnings);

            if (!export) return;
            try { result["safetyPrintout"] = PrintSafetyPrintout(cpu, ArtifactDirectory(args), SafetyPrintoutFilePrinter.MicrosoftPrintToPdf, "DocuInfo_ISO_A4_Portrait", SafetyPrintoutOption.All); }
            catch (Exception exception) { warnings.Add(InspectionWarning(plc.Path + "/safety/printout", exception)); }
        }

        private static JObject SafetyValidationSection(PlcSelection plc, DeviceItem cpu, JObject args, bool export, ScanBudget budget, JArray warnings)
        {
            JObject validation = null;
            try { validation = SafetyValidationInventory(plc.Project, cpu.Name, args, export, budget, warnings); }
            catch (FileNotFoundException exception) when (exception.FileName?.StartsWith("Siemens.Engineering.SafetyValidation", StringComparison.Ordinal) == true) { }
            if (validation == null) warnings.Add(new JObject { ["path"] = plc.Path + "/safetyValidation", ["message"] = "Safety Validation Assistant is unavailable; activation tests were not captured." });
            return validation ?? new JObject { ["available"] = false, ["code"] = "UnsupportedCapability",
                ["message"] = "The SafetyValidationAssistant project service or its assembly is not available." };
        }

        // Print takes only the two built-in Windows file printers; a missing driver queue refuses rather than falling back to another printer.
        private static JObject PrintSafetyPrintout(DeviceItem cpu, string directory, SafetyPrintoutFilePrinter printer, string layout, SafetyPrintoutOption option)
        {
            var printout = cpu.GetService<SafetyPrintout>() ?? throw new BridgeException("UnsupportedCapability", "SafetyPrintout is not exposed on the PLC DeviceItem.");
            bool pdf = printer == SafetyPrintoutFilePrinter.MicrosoftPrintToPdf;
            string queue = pdf ? "Microsoft Print to PDF" : "Microsoft XPS Document Writer";
            if (!PrinterSettings.InstalledPrinters.Cast<string>().Contains(queue, StringComparer.OrdinalIgnoreCase))
                throw new BridgeException("PrinterUnavailable", "The Windows file printer '" + queue + "' is not installed; nothing was printed.");
            var file = new FileInfo(Path.Combine(directory, "safety-printout-" + Guid.NewGuid().ToString("N") + (pdf ? ".pdf" : ".xps")));
            bool printed = printout.Print(printer, file, layout, option);
            file.Refresh();
            if (!printed || !file.Exists) throw new BridgeException("PrintoutFailed", "SafetyPrintout.Print did not produce a file.");
            var artifact = FileArtifact(file, pdf ? "application/pdf" : "application/vnd.ms-xpsdocument");
            artifact["printer"] = printer.ToString(); artifact["documentLayout"] = layout; artifact["option"] = option.ToString();
            return artifact;
        }

        private JObject SafetyPrintoutCapture(JObject args)
        {
            var plc = SelectPlc(args);
            if (SafetyAdministrationFor(plc.Software, out var cpu) == null) throw new BridgeException("UnsupportedCapability", "SafetyAdministration is not available for this PLC/software configuration.");
            var printer = (SafetyPrintoutFilePrinter)ConvertChangeValue(args["printer"], typeof(SafetyPrintoutFilePrinter));
            var option = (SafetyPrintoutOption)ConvertChangeValue(args["option"], typeof(SafetyPrintoutOption));
            string layout = ChangeText(args, "documentLayout", 128);
            if (!Regex.IsMatch(layout, "^[A-Za-z0-9_]+$")) throw new BridgeException("InvalidRequest", "documentLayout must be a TIA document layout name such as DocuInfo_ISO_A4_Portrait.");
            bool reports = args.Value<bool?>("activationTestReports") == true;
            var budget = new ScanBudget(Limit(args, 500));
            var warnings = new JArray();
            var result = Envelope();
            result["project"] = ProjectInfo(plc.Project);
            result["plcPath"] = plc.Path;
            result["safetyPrintout"] = PrintSafetyPrintout(cpu, ArtifactDirectory(args), printer, layout, option);
            if (reports) result["safetyValidation"] = SafetyValidationSection(plc, cpu, args, true, budget, warnings);
            result["warnings"] = warnings;
            result["completeness"] = InspectionCoverage(budget, warnings, reports ? "Native safety printout of the PLC DeviceItem owning SafetyAdministration, plus the SimaticML export and XLSX report of every Safety Validation Assistant activation test." : "Native safety printout of the PLC DeviceItem owning SafetyAdministration.",
                "PDF/XPS and XLSX files are retained opaquely with SHA-256; their content is not parsed or certified", "A printout or report does not validate, accept or approve the safety program",
                "No safety login, compile, download, activation-test execution or project change performed");
            return result;
        }

        // The reviewed F-I/O write subset: V21 Openness documents both as read/write F-I/O F-parameters. F-source addresses (set by
        // the CPU's central F-source address), F-parameter signatures, SIL/CRC/version and F-I/O DB assignment stay read-only here.
        private static readonly HashSet<string> FParameterWriteAttributes = new HashSet<string>(new[] { "Failsafe_FDestinationAddress", "Failsafe_FMonitoringtime" }, StringComparer.Ordinal);
        // A property: partial-class static initializer order across files is not guaranteed.
        private static HashSet<string> HardwareFParameterPreservedAttributes => new HashSet<string>(HardwareWriteAttributes.Concat(FParameterWriteAttributes), StringComparer.Ordinal);

        // Prepare (write=false) returns the preview rows; apply (write=true) rechecks ownership, expected values and collisions, writes, then reads back.
        private JArray FParameterSet(EngineeringChange change, Project project, bool write)
        {
            var payload = change.Payload;
            // An exact shape keeps anything else, such as a credential, out of the preview and journal.
            if (payload.Count != 2 || !(payload["parameters"] is JArray)) throw new BridgeException("InvalidRequest", "payload must contain exactly safetyAcknowledged and parameters.");
            RequireChangeBool(payload, "safetyAcknowledged");
            var plc = SelectPlc(change.Selector);
            // Refuses a password-protected safety program that is not already logged in; no password is accepted or used.
            EnsureSafetyWritable(plc.Software);
            SafetyAdministrationFor(plc.Software, out var cpu);
            var central = FailsafeValue(cpu, "Failsafe_CentralFSourceAddress");
            if (central?.Type != JTokenType.Integer) throw new BridgeException("UnsupportedCapability", "The safety PLC's Failsafe_CentralFSourceAddress is unreadable; F-I/O ownership cannot be established.");
            var rows = (JArray)payload["parameters"];
            if (rows.Count == 0 || rows.Count > 64) throw new BridgeException("InvalidRequest", "parameters must list 1..64 F-parameter writes.");
            var budget = new ScanBudget(50000);
            var hardware = DeviceItems(project, budget).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "F-I/O lookup is incomplete.");
            // Every F-I/O not provably on another F-source; an unreadable F-source counts, so the collision check stays conservative.
            var fIo = hardware.Where(entry => entry.Item.GetAttributeInfos().Any(info => info.Name == "Failsafe_FDestinationAddress")).Where(entry =>
                { var source = FailsafeValue(entry.Item, "Failsafe_FSourceAddress"); return source?.Type != JTokenType.Integer || JToken.DeepEquals(source, central); }).ToArray();
            var targets = new List<(HardwareEntry Entry, string Name, object Value)>();
            var result = new JArray();
            foreach (var token in rows)
            {
                var row = token as JObject;
                if (row == null || row.Count != 4 || row["expected"] == null || row["value"] == null) throw new BridgeException("InvalidRequest", "Each F-parameter write needs exactly hardwarePath, name, expected and value.");
                string path = ChangeText(row, "hardwarePath", 4096), name = ChangeText(row, "name", 128);
                if (!FParameterWriteAttributes.Contains(name)) throw new BridgeException("UnsupportedAttribute", "F-parameter is outside the reviewed write subset (Failsafe_FDestinationAddress, Failsafe_FMonitoringtime): " + name);
                if (targets.Any(target => target.Entry.Path == path && target.Name == name)) throw new BridgeException("InvalidRequest", "Duplicate F-parameter write: " + path + " " + name);
                var entry = fIo.FirstOrDefault(item => item.Path == path) ?? throw new BridgeException("HardwareNotFound", "hardwarePath does not select an F-I/O device item on this safety PLC's F-source address: " + path);
                if (!JToken.DeepEquals(FailsafeValue(entry.Item, "Failsafe_FSourceAddress"), central)) throw new BridgeException("UnresolvedSafetyOwnership", "The F-source address of the F-I/O is unreadable: " + path);
                var addresses = hardware.Where(item => item.Path == path || item.Path.StartsWith(path + "/", StringComparison.Ordinal)).SelectMany(item => item.Item.Addresses).ToArray();
                if (addresses.Length == 0 || addresses.Any(address => !address.AddressControllers.Any() || address.AddressControllers.Any(controller => ControllerMatchesSelectedPlc(controller, plc.Software) != true)))
                    throw new BridgeException("UnresolvedIoOwnership", "Every I/O address of the F-I/O must be owned by the selected PLC only: " + path);
                var infos = entry.Item.GetAttributeInfos().Where(info => info.Name == name).Take(2).ToArray();
                if (infos.Length != 1 || (infos[0].AccessMode & EngineeringAttributeAccessMode.Read) == 0 || (infos[0].AccessMode & EngineeringAttributeAccessMode.Write) == 0)
                    throw new BridgeException("AttributeNotWritable", "The F-I/O must declare " + name + " once as readable and writable: " + path);
                object before = entry.Item.GetAttribute(name) ?? throw new BridgeException("AttributeTypeUnknown", "F-parameter has no native value: " + name);
                if (!JToken.DeepEquals(ChangeScalar(before), row["expected"]))
                    throw new BridgeException(write ? "PreviewStale" : "ExpectedValueMismatch", "The current value of " + name + " differs from expected; nothing was written.",
                        new JObject { ["hardwarePath"] = path, ["name"] = name, ["current"] = ChangeScalar(before) });
                object value = ConvertChangeValue(row["value"], before.GetType());
                if (Equals(value, before)) throw new BridgeException("InvalidRequest", "The requested value equals the current value: " + path + " " + name);
                // Preservation compares native scalars, not the JSON input spelling.
                row["value"] = ChangeScalar(value);
                targets.Add((entry, name, value));
                result.Add(new JObject { ["hardwarePath"] = path, ["name"] = name, ["type"] = before.GetType().FullName, ["before"] = ChangeScalar(before), ["after"] = row["value"].DeepClone(),
                    ["fParameterSignatures"] = FParameterSignatures(entry.Item) });
            }
            foreach (var target in targets.Where(target => target.Name == "Failsafe_FDestinationAddress"))
            {
                var conflicts = new JArray();
                foreach (var other in fIo.Where(other => other.Path != target.Entry.Path))
                {
                    var current = FailsafeValue(other.Item, "Failsafe_FDestinationAddress");
                    if (current?.Type != JTokenType.Integer) throw new BridgeException("DependencyCoverageIncomplete", "An F-destination address is unreadable; collision absence cannot be established: " + other.Path);
                    // Current and planned addresses both count, so swaps and chained moves refuse rather than pass through a transient duplicate.
                    if (JToken.DeepEquals(current, ChangeScalar(target.Value)) || targets.Any(peer => peer.Entry.Path == other.Path && peer.Name == target.Name && Equals(peer.Value, target.Value)))
                        conflicts.Add(other.Path);
                }
                if (conflicts.Count != 0) throw new BridgeException("AddressConflict", "The F-destination address is used or planned by another F-I/O on this safety PLC's F-source: " + target.Entry.Path, new JObject { ["conflicts"] = conflicts });
            }
            if (!write) return result;
            // The whole batch is preflighted before the first setter; a native failure after dispatch still requires checkpoint recovery.
            foreach (var target in targets) target.Entry.Item.SetAttribute(target.Name, target.Value);
            result = new JArray();
            foreach (var target in targets)
            {
                object actual = target.Entry.Item.GetAttribute(target.Name);
                if (actual?.GetType() != target.Value.GetType() || !Equals(actual, target.Value)) throw new BridgeException("MutationVerificationFailed", "F-parameter readback differs from the request: " + target.Entry.Path + " " + target.Name);
                result.Add(new JObject { ["hardwarePath"] = target.Entry.Path, ["name"] = target.Name, ["value"] = ChangeScalar(actual), ["fParameterSignatures"] = FParameterSignatures(target.Entry.Item) });
            }
            return result;
        }

        // Every readable Failsafe_*Signature* attribute of one F-I/O, for example Failsafe_FParameterSignatureWithAddresses.
        private static JObject FParameterSignatures(DeviceItem item) => new JObject(item.GetAttributeInfos()
            .Where(info => IsFailsafeAttribute(info.Name) && info.Name.IndexOf("Signature", StringComparison.Ordinal) >= 0 && (info.AccessMode & EngineeringAttributeAccessMode.Read) != 0)
            .OrderBy(info => info.Name, StringComparer.Ordinal).Select(info => new JProperty(info.Name, FailsafeValue(item, info.Name))));

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
