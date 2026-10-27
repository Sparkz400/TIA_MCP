using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.HW;

namespace TiaMcp.Openness.V21
{
    // Native AutomationML exchange through CaxProvider. The V19+ TransferResult overloads write no native
    // log file; Siemens leaves storing the result to the caller, so it is retained as JSON beside the AML.
    internal sealed partial class OpennessSession
    {
        private const int CaxRetainedMessages = 50000;
        private const long CaxImportBytes = 64L * 1024 * 1024;

        private JObject CaxExport(JObject args)
        {
            var project = SelectProject(args);
            var provider = project.GetService<CaxProvider>() ?? throw new BridgeException("UnsupportedCapability", "Project has no CaxProvider service.");
            string wanted = args.Value<string>("hardwarePath");
            Device device = string.IsNullOrWhiteSpace(wanted) ? null : EngineeringDeviceEntries(project).SingleOrDefault(entry => entry.Path == wanted)?.Item
                ?? throw new BridgeException("DeviceNotFound", "No device matches the exact hardwarePath (a device path from device_inventory).");
            string stem = Path.Combine(ArtifactDirectory(args), "cax-" + Guid.NewGuid().ToString("N"));
            var aml = new FileInfo(stem + ".aml");
            var log = new FileInfo(stem + ".transfer-result.json");
            bool modifiedBefore = project.IsModified;
            // Siemens requires only an open project; the export reflects unsaved in-memory edits, so nothing is saved.
            var native = device == null ? provider.Export(project, aml) : provider.Export(device, aml);
            File.WriteAllText(log.FullName, CaxTransfer(native, CaxRetainedMessages).ToString(Formatting.Indented));
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["hardwarePath"] = wanted;
            result["transferResult"] = CaxTransfer(native, Limit(args, 500));
            var logArtifact = FileArtifact(log); logArtifact["mediaType"] = "application/json";
            result["log"] = logArtifact;
            aml.Refresh();
            if (aml.Exists) result["artifact"] = FileArtifact(aml);
            result["projectModifiedBefore"] = modifiedBefore;
            result["projectModifiedAfter"] = project.IsModified;
            if (native.State == TransferResultState.Error || native.ErrorCount > 0 || !aml.Exists)
                throw new BridgeException("CaxExportFailed", "CAx export reported errors or wrote no AML file; the transfer result is retained for review.", result);
            return result;
        }

        private void PrepareCaxImport(EngineeringChange change, Project project, JObject preview)
        {
            RequireCaxImportOwner();
            if (project.GetService<CaxProvider>() == null) throw new BridgeException("UnsupportedCapability", "Project has no CaxProvider service.");
            var payload = change.Payload;
            string option = ChangeText(payload, "importOption", 32);
            if (!Enum.GetNames(typeof(CaxImportOptions)).Contains(option, StringComparer.Ordinal))
                throw new BridgeException("InvalidRequest", "importOption must be MoveToParkingLot, RetainTiaDevice or OverwriteTiaDevice.");
            string source = ChangeText(payload, "path", 4096), expected = ChangeText(payload, "expectedSha256", 64);
            if (!Path.IsPathRooted(source) || !File.Exists(source) || new FileInfo(source).Length > CaxImportBytes)
                throw new BridgeException("InvalidRequest", "The AML file must be an existing absolute Host artifact path of at most 64 MiB.");
            change.InputPath = Path.Combine(change.Directory, "requested.aml");
            File.Copy(source, change.InputPath);
            change.InputHash = ChangeFileHash(change.InputPath);
            if (!string.Equals(change.InputHash, expected, StringComparison.Ordinal)) throw new BridgeException("ArtifactChanged", "The AML file does not match expectedSha256 (lowercase hex).");
            ((JArray)preview["effects"]).Add(new JObject { ["importFile"] = ChangeArtifact(change.InputPath), ["amlRef"] = payload["amlRef"]?.DeepClone(), ["importOption"] = option,
                ["nameConflicts"] = option == "OverwriteTiaDevice" ? "Replace name-conflicting project devices with the AML devices." : option == "RetainTiaDevice"
                    ? "Keep name-conflicting project devices and skip those AML devices." : "Keep name-conflicting project devices and import those AML devices into a parking-lot folder." });
            ((JArray)preview["warnings"]).Add("Siemens refuses CAx import inside exclusive access: apply releases this worker's lease only around the native import, so it requires a worker-owned TIA instance. The AML file decides which devices, modules, subnets and addresses are created; Siemens validates them during import and device compilation.");
            if (option == "OverwriteTiaDevice") ((JArray)preview["warnings"]).Add("OverwriteTiaDevice replaces devices as the AML file dictates, so configured-hardware preservation cannot be matched; the before/after difference is only recorded for review.");
        }

        private void ApplyCaxImport(EngineeringChange change, Project project)
        {
            RequireCaxImportRoute();
            var provider = project.GetService<CaxProvider>() ?? throw new BridgeException("UnsupportedCapability", "Project has no CaxProvider service.");
            var option = (CaxImportOptions)Enum.Parse(typeof(CaxImportOptions), change.Payload.Value<string>("importOption"));
            TransferResult native;
            _sharedMutationLease.Dispose();
            try { native = provider.Import(new FileInfo(change.InputPath), option); }
            finally { _sharedMutationLease = _portal.ExclusiveAccess("TIA MCP: shared resource admission " + _sharedMutationOperation); }
            change.Stages.Add("cax_import_returned");
            string log = Path.Combine(change.Directory, "cax-import.transfer-result.json");
            var transfer = CaxTransfer(native, CaxRetainedMessages);
            File.WriteAllText(log, transfer.ToString(Formatting.Indented));
            change.Readbacks.Add(new JObject { ["role"] = "cax-import-transfer-result", ["state"] = transfer["state"], ["errorCount"] = transfer["errorCount"],
                ["warningCount"] = transfer["warningCount"], ["messagesTruncated"] = transfer["truncated"], ["artifact"] = ChangeArtifact(log) });
            if (native.State == TransferResultState.Error || native.ErrorCount > 0)
                throw new BridgeException("CaxImportFailed", "Native CAx import reported errors. Its transfer result and the recovery archive are retained.");
        }

        private void RecordCaxHardwareDifference(EngineeringChange change, JArray after)
        {
            var difference = HardwareEngineeringRules.Difference(change.HardwareBeforeManifest, after);
            string path = Path.Combine(change.Directory, "hardware-difference.json");
            File.WriteAllText(path, difference.ToString(Formatting.Indented));
            change.Readbacks.Add(new JObject { ["role"] = "cax-import-hardware-difference", ["added"] = ((JArray)difference["added"]).Count,
                ["removed"] = ((JArray)difference["removed"]).Count, ["changed"] = ((JArray)difference["changed"]).Count, ["artifact"] = ChangeArtifact(path) });
        }

        private void RequireCaxImportOwner()
        {
            if (!_ownsPortal) throw new BridgeException("OwnedSessionRequired", "CAx import runs outside TIA exclusive access, so it is limited to a worker-owned TIA instance where no interactive edit can interleave.");
        }

        // Siemens: "CAx Import APIs cannot be used within exclusive access." Only the admitted route's lease can be released and re-acquired.
        private void RequireCaxImportRoute()
        {
            RequireCaxImportOwner();
            if (_sharedMutationLease == null || _sharedMutationOperation != "change_apply")
                throw new BridgeException("MutationLeaseRequired", "CAx import runs only through the Host's admitted change_apply route.");
        }

        private static JObject CaxTransfer(TransferResult result, int maxMessages)
        {
            var budget = new ScanBudget(maxMessages);
            var messages = CaxMessages(result.Messages, budget, 0);
            return new JObject { ["state"] = result.State.ToString(), ["errorCount"] = result.ErrorCount, ["warningCount"] = result.WarningCount,
                ["messages"] = messages, ["messageCount"] = budget.Used, ["truncated"] = budget.Truncated, ["maxMessages"] = maxMessages };
        }

        private static JArray CaxMessages(TransferResultMessageComposition messages, ScanBudget budget, int depth)
        {
            var rows = new JArray();
            if (depth > 64) { budget.Truncated = true; return rows; }
            foreach (var message in messages)
            {
                if (!budget.Take()) break;
                rows.Add(new JObject { ["dateTime"] = message.DateTime.ToString("O", CultureInfo.InvariantCulture), ["state"] = message.State.ToString(),
                    ["errorCount"] = message.ErrorCount, ["warningCount"] = message.WarningCount, ["message"] = message.Message,
                    ["messages"] = CaxMessages(message.Messages, budget, depth + 1) });
            }
            return rows;
        }
    }
}
