using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Simatic.Simulation.Runtime;
using TiaMcp.Simulation;

namespace TiaMcp.Simulator.V8
{
    internal sealed class SimulatorSession : IDisposable
    {
        private readonly Dictionary<int, Selection> _instances = new Dictionary<int, Selection>();
        private bool _initialized;

        internal JObject Execute(string operation, JObject args)
        {
            // This starts Siemens Runtime Manager on first use; it never starts a CPU
            // or calls license activation, network discovery, or adapter configuration.
            uint version = SimulationRuntimeManager.Version;
            _initialized = true;
            switch (operation)
            {
                case "bridge_info":
                case "sim_status": return Status();
                case "sim_create": return Create(args);
                case "sim_creation_status": return CreationStatus(args);
                case "sim_select": return Select(args);
                case "sim_state": return State(args);
                case "sim_tags": return Tags(args);
                case "sim_read": return Read(args);
                case "sim_write": return Write(args);
                case "sim_test": return Test(args);
                case "sim_release": return Release(args);
                case "sim_capture_begin": return CaptureBegin(args);
                case "sim_capture_sample": return CaptureSample(args);
                case "sim_capture_end": return CaptureEnd(args);
                case "sim_identity": return Identity(args);
                case "sim_write_owned": return WriteOwned(args);
                default: throw new SimulatorException("UnknownOperation", "Unknown simulator operation: " + operation);
            }
        }

        private JObject Status()
        {
            var result = Envelope();
            result["available"] = SimulationRuntimeManager.IsRuntimeManagerAvailable;
            result["runtimeVersion"] = SimulationRuntimeManager.Version;
            result["networkMode"] = SimulationRuntimeManager.NetworkMode.ToString();
            result["instances"] = new JArray(SimulationRuntimeManager.RegisteredInstanceInfo.Select(info => new JObject {
                ["id"] = info.ID, ["name"] = info.Name, ["selected"] = _instances.ContainsKey(info.ID),
                ["owned"] = _instances.ContainsKey(info.ID) && _instances[info.ID].Owned }));
            result["selections"] = new JArray(_instances.Values.Select(Info));
            result["licensePolicy"] = "No activation API or licensing-dialog confirmation. Explicit bounded PowerOn supplies the licensing result; trial and Test Suite warnings are rejected. LicenseStatus reports current use, not available entitlement.";
            result["downloadRoute"] = "Owned fresh virtual memory card, verified through a CurrentUser DPAPI ticket and independent local runtime identity check.";
            result["timing"] = "Wall-clock polling only; no scan-accurate or synchronized simulation claim.";
            return result;
        }

        private JObject Create(JObject args)
        {
            string root = DirectoryArgument(args);
            string cpuName = args.Value<string>("cpuType") ?? "CPU1511";
            ECPUType cpu;
            if (!Enum.TryParse(cpuName, false, out cpu) || !Enum.IsDefined(typeof(ECPUType), cpu))
                throw new SimulatorException("InvalidRequest", "cpuType must be an exact V8 ECPUType enum name, for example CPU1511.");
            string name = "TiaMcp_" + Guid.NewGuid().ToString("N");
            string storage = Path.Combine(root, name);
            if (storage.Length > 180) throw new SimulatorException("InvalidRequest", "Use a shorter artifact directory; instance storage must remain below 180 characters.");
            Directory.CreateDirectory(storage);
            string creationId = SimulationCreationJournal.ValidateId(args.Value<string>("creationId") ?? Guid.NewGuid().ToString("N"));
            var receipt = SimulationCreationJournal.Begin(root, creationId, name, storage, cpu.ToString());
            IInstance instance = null;
            try
            {
                instance = SimulationRuntimeManager.RegisterInstance(cpu, name);
                receipt["instanceId"] = instance.ID;
                receipt["stage"] = "registered";
                SimulationCreationJournal.Save(root, receipt);
                instance.StoragePath = storage;
                var selected = new Selection { Instance = instance, Id = instance.ID, Name = name, Owned = true, Storage = storage,
                    CreationRoot = root, CreationReceipt = receipt };
                var marker = SimulationTicket.Create(selected.Id, name, storage);
                receipt["markerSha256"] = SimulationCreationJournal.MarkerHash(storage);
                receipt["stage"] = "ready";
                SimulationCreationJournal.Save(root, receipt);
                var result = Envelope(selected);
                result["instance"] = Info(selected);
                result["downloadTicket"] = SimulationTicket.Encode(marker);
                result["downloadTicketExpiresAtUtc"] = marker["expiresAtUtc"];
                result["poweredOn"] = false;
                result["creationId"] = creationId;
                result["creationReceiptPath"] = SimulationCreationJournal.PathFor(root, creationId);
                _instances.Add(selected.Id, selected);
                return result;
            }
            catch (Exception error)
            {
                receipt["stage"] = "outcomeUnknown";
                receipt["failureType"] = error.GetType().FullName;
                if (instance != null && !_instances.Values.Any(s => ReferenceEquals(s.Instance, instance)))
                {
                    try { instance.UnregisterInstance(); receipt["stage"] = "unregisteredAfterFailure"; }
                    catch (Exception cleanup) { receipt["cleanupFailureType"] = cleanup.GetType().FullName; }
                    try { instance.Dispose(); } catch (Exception cleanup) { receipt["disposeFailureType"] = cleanup.GetType().FullName; }
                }
                try { SimulationCreationJournal.Save(root, receipt); } catch { /* Keep the last durably recorded phase. */ }
                throw new SimulatorException("SimulationCreationFailed", error.Message, new JObject { ["creationId"] = creationId,
                    ["receiptPath"] = SimulationCreationJournal.PathFor(root, creationId), ["stage"] = receipt["stage"],
                    ["exceptionType"] = error.GetType().FullName, ["retrySafe"] = false });
            }
        }

        private JObject CreationStatus(JObject args)
        {
            string root = DirectoryArgument(args);
            var receipt = SimulationCreationJournal.Read(root, RequiredString(args, "creationId", 32));
            bool exited = SimulationCreationJournal.WorkerExited(receipt);
            var matches = SimulationRuntimeManager.RegisteredInstanceInfo.Where(i =>
                i.Name == receipt.Value<string>("instanceName") && i.ID == receipt.Value<int?>("instanceId")).ToArray();
            return new JObject { ["receipt"] = receipt, ["workerExitVerified"] = exited,
                ["exactRegisteredIdentityObserved"] = matches.Length == 1,
                ["ownershipAdopted"] = false, ["nativeOutcomeReconciled"] = false,
                ["note"] = "Read-only observation. An intent, absent registration or name match does not resolve an unknown native operation. Use sim_select with the exact receipt creationId only after the original worker exits and a ready receipt exists." };
        }

        private JObject Select(JObject args)
        {
            int id = Integer(args, "instanceId", 0, int.MaxValue);
            string name = RequiredString(args, "expectedName", 128);
            var known = SimulationRuntimeManager.RegisteredInstanceInfo.Where(i => i.ID == id && i.Name == name).ToArray();
            if (known.Length != 1) throw new SimulatorException("SimulationTargetMismatch", "The exact local simulated instance ID and name were not found.");
            Selection selected;
            if (!_instances.TryGetValue(id, out selected))
            {
                var instance = SimulationRuntimeManager.CreateInterface(id);
                try
                {
                    selected = new Selection { Instance = instance, Id = id, Name = name, Owned = false, Storage = instance.StoragePath };
                    if (args.Value<string>("creationId") != null) AdoptSelection(selected, args);
                    _instances.Add(id, selected);
                }
                catch { instance.Dispose(); throw; }
            }
            else if (selected.Name != name)
                throw new SimulatorException("SimulationTargetMismatch", "The instance ID has been reused. Restart the simulator bridge before selecting it.");
            else if (args.Value<string>("creationId") != null)
            {
                if (!selected.Owned) AdoptSelection(selected, args);
                else if (selected.CreationReceipt?.Value<string>("creationId") != SimulationCreationJournal.ValidateId(args.Value<string>("creationId")))
                    throw new SimulatorException("SimulationOwnershipUnverified", "Selection belongs to a different creation receipt.");
            }
            if (selected.Name != name) throw new SimulatorException("SimulationTargetMismatch", "The instance ID has been reused. Restart the simulator bridge before selecting it.");
            return new JObject { ["instance"] = Info(selected), ["origin"] = "PLCSIM Advanced V8/local API" };
        }

        private static void AdoptSelection(Selection selected, JObject args)
        {
            string root = DirectoryArgument(args), creationId = SimulationCreationJournal.ValidateId(args.Value<string>("creationId"));
            string leasePath = SimulationCreationJournal.PathFor(root, creationId) + ".lock";
            if (File.Exists(leasePath) && (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0)
                throw new SimulatorException("SimulationOwnershipUnverified", "Creation lease cannot be a reparse point.");
            using (var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var receipt = SimulationCreationJournal.Read(root, creationId);
                SimulationCreationJournal.VerifyAdoption(receipt, selected.Id, selected.Name, selected.Instance.StoragePath,
                    selected.Instance.CPUType.ToString(), SimulationCreationJournal.WorkerExited(receipt));
                receipt["previousWorkerPid"] = receipt["workerPid"];
                receipt["previousWorkerStartedAtUtc"] = receipt["workerStartedAtUtc"];
                using (var process = Process.GetCurrentProcess())
                {
                    receipt["workerPid"] = process.Id;
                    receipt["workerStartedAtUtc"] = process.StartTime.ToUniversalTime().ToString("O");
                }
                receipt["adoptedAtUtc"] = DateTime.UtcNow.ToString("O");
                SimulationCreationJournal.Save(root, receipt);
                selected.Owned = true;
                selected.CreationRoot = root;
                selected.CreationReceipt = receipt;
            }
        }

        private JObject State(JObject args)
        {
            var selected = Get(args);
            string action = RequiredString(args, "action", 16);
            uint timeout = (uint)OptionalInteger(args, "timeoutMs", 30000, 1000, 60000);
            string resultCode = null;
            switch (action)
            {
                case "powerOn":
                    if (SimulationRuntimeManager.NetworkMode != ENetworkMode.Softbus)
                        throw new SimulatorException("SoftbusRequired", "This adapter only powers instances in existing local Softbus mode. It does not reconfigure shared adapters or network mode.");
                    // LicenseStatus documents the currently used license; it cannot
                    // prove available entitlement for an Off CPU. PowerOn is the
                    // documented licensing check, and no ALM dialog is confirmed.
                    ERuntimeErrorCode? currentLicense = null;
                    try { currentLicense = selected.Instance.LicenseStatus; } catch { }
                    if (currentLicense == ERuntimeErrorCode.WarningTrialModeActive || currentLicense == ERuntimeErrorCode.WarningRunningOnTiaPortalTestSuite)
                        throw new SimulatorException("FullLicenseRequired", "The selected instance reports " + currentLicense + ". No trial activation or power-on was attempted.");
                    var status = selected.Instance.PowerOn(timeout);
                    resultCode = status.ToString();
                    if (status != ERuntimeErrorCode.OK && status != ERuntimeErrorCode.WarningAlreadyExists)
                    {
                        // Do not continue after a rejected power-on result, including protection or license warnings.
                        if (selected.Owned) { try { selected.Instance.PowerOff(timeout); } catch { } }
                        throw SimulationPowerOnFailure.Create(resultCode, Info(selected));
                    }
                    break;
                case "powerOff": selected.Instance.PowerOff(timeout); break;
                case "run": EnsureSoftbus(selected); selected.Instance.Run(timeout); break;
                case "stop": selected.Instance.Stop(timeout); break;
                default: throw new SimulatorException("InvalidRequest", "action must be powerOn, powerOff, run, or stop.");
            }
            var observed = selected.Instance.OperatingState;
            bool reached = action == "powerOn" ? new[] { EOperatingState.Run, EOperatingState.Stop, EOperatingState.Freeze, EOperatingState.Hold }.Contains(observed)
                : action == "powerOff" ? observed == EOperatingState.Off : action == "run" ? observed == EOperatingState.Run : observed == EOperatingState.Stop;
            if (!reached) throw new SimulatorException("SimulationStateNotReached", "The runtime call returned but the requested state was not observed.",
                new JObject { ["action"] = action, ["observedState"] = observed.ToString(), ["runtimeCode"] = resultCode, ["retrySafe"] = false, ["instance"] = Info(selected) });
            var result = Envelope(selected);
            result["instance"] = Info(selected);
            result["action"] = action;
            result["runtimeCode"] = resultCode;
            result["completionBasis"] = resultCode == null ? "Managed void API returned without exception; final operating state verified." : "Runtime return code and final operating state verified.";
            return result;
        }

        private JObject Tags(JObject args)
        {
            var selected = Get(args);
            int maximum = OptionalInteger(args, "maxItems", 200, 1, 2000);
            string filter = args.Value<string>("filter") ?? "";
            if (filter.Length > 256) throw new SimulatorException("InvalidRequest", "filter is limited to 256 characters.");
            if (args.Value<bool?>("refresh") ?? true) selected.Instance.UpdateTagList(ETagListDetails.IOMCTDB);
            var matches = selected.Instance.TagInfos.Where(t => t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            var result = Envelope(selected);
            result["tags"] = new JArray(matches.Take(maximum).Select(t => new JObject { ["symbol"] = t.Name,
                ["dataType"] = t.DataType.ToString(), ["primitiveDataType"] = t.PrimitiveDataType.ToString(),
                ["area"] = t.Area.ToString(), ["size"] = t.Size, ["offset"] = t.Offset, ["bit"] = t.Bit,
                ["index"] = t.Index, ["parentIndex"] = t.ParentIndex }));
            result["totalMatches"] = matches.Length;
            result["truncated"] = matches.Length > maximum;
            return result;
        }

        private JObject Read(JObject args)
        {
            var selected = Get(args);
            var signals = Signals(args["signals"], false);
            var result = Envelope(selected);
            result["values"] = Capture(selected, signals);
            return result;
        }

        private JObject Write(JObject args)
        {
            var selected = Get(args);
            EnsureSoftbus(selected);
            var writes = Signals(args["writes"], true);
            var result = Envelope(selected);
            result["writes"] = ApplyWrites(selected, writes);
            result["note"] = "Values are written to the selected local simulated PLC. Program scans may immediately overwrite them.";
            return result;
        }

        // Runtime captures read only an exact owned instance: ID, name and its creation receipt must all match.
        private Selection Owned(JObject args)
        {
            var selected = Get(args);
            if (selected.Name != RequiredString(args, "instanceName", 128))
                throw new SimulatorException("SimulationTargetMismatch", "The exact local simulated instance name does not match. No action was performed.");
            if (!selected.Owned || selected.CreationReceipt?.Value<string>("creationId") != SimulationCreationJournal.ValidateId(RequiredString(args, "creationId", 32)))
                throw new SimulatorException("SimulationOwnershipUnverified", "Runtime capture requires an instance this adapter created or adopted with the supplied creationId.");
            return selected;
        }

        // Runtime target binding: the exact owned instance, the digest of its ownership marker and its controller addresses.
        private JObject Identity(JObject args)
        {
            var selected = Owned(args);
            SimulationTicket.VerifyPlainDirectory(selected.Storage);
            var marker = new FileInfo(Path.Combine(selected.Storage, SimulationTicket.MarkerName));
            if (!marker.Exists || marker.Length > 16384 || (marker.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new SimulatorException("SimulationOwnershipUnverified", "The owned instance's ownership marker is unavailable. No binding was observed.");
            var result = Envelope(selected);
            result["instance"] = Info(selected);
            result["creationId"] = selected.CreationReceipt.Value<string>("creationId");
            using (var sha = System.Security.Cryptography.SHA256.Create())
                result["markerSha256"] = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(marker.FullName))).Replace("-", "").ToLowerInvariant();
            result["controllerAddresses"] = new JArray(selected.Instance.ControllerIP ?? new string[0]);
            return result;
        }

        // Diagnostic plan writes: the exact owned instance only. Nothing is written unless every signal first reads as its expected value;
        // then each write is sent once and read back, stopping at the first failure or mismatch (the scan may overwrite assignments).
        private JObject WriteOwned(JObject args)
        {
            var selected = Owned(args);
            EnsureSoftbus(selected);
            var writes = Signals(args["writes"], true);
            var expected = Signals(new JArray(args["writes"].Select(w => new JObject { ["symbol"] = w["symbol"], ["type"] = w["type"], ["value"] = w["expected"] })), true);
            if (writes.Count > 16 || writes.Select(w => w.Symbol).Distinct(StringComparer.Ordinal).Count() != writes.Count)
                throw new SimulatorException("InvalidRequest", "Provide 1–16 writes with distinct symbols.");
            var result = Envelope(selected);
            var before = Capture(selected, writes);
            result["before"] = before;
            if (Enumerable.Range(0, writes.Count).Any(i => !Matches(before[i], expected[i])))
            { result["state"] = "preconditionMismatch"; result["dispatched"] = 0; return result; }
            var items = new JArray(); bool complete = true;
            for (int i = 0; i < writes.Count && complete; i++)
            {
                var item = new JObject { ["symbol"] = writes[i].Symbol, ["type"] = writes[i].Type, ["value"] = writes[i].Value };
                items.Add(item);
                try { WriteValue(selected.Instance, writes[i]); item["dispatch"] = "accepted"; item["appliedAtUtc"] = DateTime.UtcNow.ToString("O"); }
                catch (Exception ex) { item["dispatch"] = "failed"; item["errorCode"] = ErrorCode(ex); complete = false; break; }
                var readback = Capture(selected, new List<Signal> { writes[i] })[0];
                item["readback"] = readback;
                item["readbackMatches"] = complete = Matches(readback, writes[i]);
            }
            result["state"] = complete ? "written" : "incomplete";
            result["dispatched"] = items.Count;
            result["items"] = items;
            return result;
        }

        private static bool Matches(JToken read, Signal wanted) => read.Value<string>("quality") == "good" && SimulationAssertions.Same(wanted.Type, read["value"], wanted.Value);

        private JObject CaptureBegin(JObject args)
        {
            var selected = Owned(args);
            Signals(args["signals"], false);
            bool synchronous = args.Value<bool?>("cycleSynchronous") ?? false;
            var result = Envelope(selected);
            if (synchronous)
            {
                if (selected.CaptureRestoreMode != null) throw new SimulatorException("SimulationCaptureModeActive", "A cycle-synchronous capture already holds this instance.");
                if (selected.Instance.OperatingState != EOperatingState.Run) throw new SimulatorException("SimulationNotRunning", "Cycle-synchronous capture requires a running simulated CPU.");
                var previous = selected.Instance.OperatingMode;
                selected.Instance.RegisterOnSyncPointReachedEvent();
                selected.CaptureRestoreMode = previous;
                try
                {
                    selected.Instance.OperatingMode = EOperatingMode.SingleStep_C;
                    // The CPU now holds at its next cycle end; confirm that before the first sample steps it.
                    result["syncPoint"] = SyncPoint(selected.Instance.WaitForOnSyncPointReachedEvent(SyncTimeoutMs));
                }
                // Keep the original failure; an unrestored mode stays recorded and blocks the next begin until release retries it.
                catch { try { RestoreCaptureMode(selected); } catch { } throw; }
                result["previousOperatingMode"] = previous.ToString();
            }
            result["cycleSynchronous"] = synchronous;
            return result;
        }

        private JObject CaptureSample(JObject args)
        {
            var selected = Owned(args);
            var signals = Signals(args["signals"], false);
            bool synchronous = args.Value<bool?>("cycleSynchronous") ?? false;
            if (synchronous != (selected.CaptureRestoreMode != null))
                throw new SimulatorException("SimulationCaptureModeInactive", "The requested capture mode does not match this instance's capture state.");
            var result = Envelope(selected);
            if (synchronous) { selected.Instance.RunToNextSyncPoint(); result["syncPoint"] = SyncPoint(selected.Instance.WaitForOnSyncPointReachedEvent(SyncTimeoutMs)); }
            else result["syncPoint"] = null;
            // In single-step mode the CPU holds at the reached sync point while every signal is read.
            result["values"] = Capture(selected, signals);
            return result;
        }

        private JObject CaptureEnd(JObject args)
        {
            var selected = Owned(args);
            var result = Envelope(selected);
            result["restoredOperatingMode"] = selected.CaptureRestoreMode?.ToString();
            RestoreCaptureMode(selected);
            result["operatingMode"] = selected.Instance.OperatingMode.ToString();
            return result;
        }

        private static void RestoreCaptureMode(Selection selected)
        {
            if (selected.CaptureRestoreMode == null) return;
            var previous = selected.CaptureRestoreMode.Value;
            selected.Instance.OperatingMode = previous;
            selected.Instance.UnregisterOnSyncPointReachedEvent();
            if (selected.Instance.OperatingMode != previous)
                throw new SimulatorException("SimulationOperatingModeNotRestored", "The recorded operating mode " + previous + " was not observed after restore.");
            selected.CaptureRestoreMode = null;
        }

        private const uint SyncTimeoutMs = 5000;
        private static JObject SyncPoint(SOnSyncPointReachedResult reached)
        {
            if (reached.ErrorCode != ERuntimeErrorCode.OK)
                throw new SimulatorException("SimulationSyncPointTimeout", "No sync point was confirmed within " + SyncTimeoutMs + " ms: " + reached.ErrorCode);
            return new JObject { ["count"] = reached.SyncPointCount, ["timeSinceSameSyncPointNanoseconds"] = reached.TimeSinceSameSyncPoint_ns,
                ["timeSinceAnySyncPointNanoseconds"] = reached.TimeSinceAnySyncPoint_ns };
        }

        private static string ErrorCode(Exception error) => (error as SimulatorException)?.Code
            ?? (error as SimulationRuntimeException)?.RuntimeErrorCode.ToString() ?? "SimulatorReadFailed";

        private JObject Release(JObject args)
        {
            var selected = Get(args);
            bool remove = args.Value<bool?>("deleteOwned") ?? false;
            if (remove)
            {
                if (!selected.Owned) throw new SimulatorException("OwnedInstanceRequired", "This adapter cannot unregister an instance it did not create.");
                if (selected.Instance.OperatingState != EOperatingState.Off) selected.Instance.PowerOff(30000);
                selected.Instance.UnregisterInstance();
                if (selected.CreationReceipt != null)
                {
                    selected.CreationReceipt["stage"] = "unregistered";
                    SimulationCreationJournal.Save(selected.CreationRoot, selected.CreationReceipt);
                }
            }
            else RestoreCaptureMode(selected);
            selected.Instance.Dispose();
            _instances.Remove(selected.Id);
            return new JObject { ["instanceId"] = selected.Id, ["instanceName"] = selected.Name, ["unregistered"] = remove,
                ["storageRetained"] = selected.Storage, ["note"] = "Storage files are retained; no directory was deleted." };
        }

        private JObject Test(JObject args)
        {
            var selected = Get(args);
            EnsureSoftbus(selected);
            if (selected.Instance.OperatingState != EOperatingState.Run)
                throw new SimulatorException("SimulationNotRunning", "Start the selected simulated CPU before running a scenario.");
            string root = DirectoryArgument(args);
            var signals = Signals(args["signals"], false);
            var steps = args["steps"] as JArray;
            if (steps == null || steps.Count < 1 || steps.Count > 128) throw new SimulatorException("InvalidRequest", "Provide 1–128 scenario steps.");
            int interval = OptionalInteger(args, "intervalMs", 100, 20, 5000);
            long duration = 0;
            int snapshots = 1;
            foreach (var token in steps)
            {
                var step = token as JObject ?? throw new SimulatorException("InvalidRequest", "Each scenario step must be an object.");
                int delay = OptionalInteger(step, "delayMs", 0, 0, 60000);
                duration += delay;
                snapshots += Math.Max(1, (delay + interval - 1) / interval);
                if (step["writes"] != null) Signals(step["writes"], true);
                ValidateAssertions(step["assertions"]);
            }
            if (duration > 60000 || snapshots > 500) throw new SimulatorException("ScenarioLimit", "Scenario is limited to 60 seconds and 500 snapshots. Increase intervalMs or reduce steps.");
            var report = Envelope(selected);
            report["instance"] = Info(selected);
            report["timing"] = "Wall-clock polling, including API overhead. Not scan-accurate; samples are not atomic across symbols.";
            report["scenario"] = args.DeepClone();
            var samples = new JArray(); var actions = new JArray(); var assertions = new JArray();
            report["samples"] = samples; report["actions"] = actions; report["assertions"] = assertions;
            string reportPath = Path.Combine(root, "simulation-test-" + Guid.NewGuid().ToString("N") + ".json");
            var clock = Stopwatch.StartNew();
            bool completed = false;
            try
            {
                samples.Add(Sample(selected, signals, clock));
                foreach (JObject step in steps)
                {
                    if (step["writes"] != null) actions.Add(new JObject { ["elapsedMs"] = clock.ElapsedMilliseconds, ["writes"] = ApplyWrites(selected, Signals(step["writes"], true)) });
                    long until = clock.ElapsedMilliseconds + OptionalInteger(step, "delayMs", 0, 0, 60000);
                    do
                    {
                        int wait = (int)Math.Min(interval, Math.Max(0, until - clock.ElapsedMilliseconds));
                        if (wait > 0) Thread.Sleep(wait);
                        samples.Add(Sample(selected, signals, clock));
                        if (samples.Count > 500 || clock.ElapsedMilliseconds > 90000)
                            throw new SimulatorException("ScenarioLimit", "Actual execution exceeded 90 seconds or 500 samples including API overhead.");
                    } while (clock.ElapsedMilliseconds < until);
                    foreach (JObject assertion in (step["assertions"] as JArray) ?? new JArray())
                        assertions.Add(Evaluate(selected, assertion, clock));
                }
                completed = true;
            }
            catch (Exception ex) { report["failure"] = new JObject { ["message"] = ex.Message, ["exceptionType"] = ex.GetType().FullName }; }
            report["completed"] = completed;
            report["passed"] = completed && assertions.All(a => a.Value<bool>("passed")) && samples.All(s => s["values"].All(v => v.Value<string>("quality") == "good"));
            report["elapsedMs"] = clock.ElapsedMilliseconds;
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented), new UTF8Encoding(false));
            report["reportPath"] = reportPath;
            if (!report.Value<bool>("passed")) throw new SimulatorException("SimulationTestFailed", "Simulation scenario failed or contains bad-quality samples. Inspect the retained report.", report);
            return report;
        }

        private static JObject Sample(Selection selected, List<Signal> signals, Stopwatch clock) =>
            new JObject { ["elapsedMs"] = clock.ElapsedMilliseconds, ["values"] = Capture(selected, signals) };

        private static JArray Capture(Selection selected, List<Signal> signals)
        {
            var values = new JArray();
            foreach (var signal in signals)
            {
                var value = new JObject { ["symbol"] = signal.Symbol, ["type"] = signal.Type, ["observedAtUtc"] = DateTime.UtcNow.ToString("O") };
                try
                {
                    var observed = ReadValue(selected.Instance, signal);
                    if ((signal.Type == "float" || signal.Type == "double") && (double.IsNaN(observed.Value<double>()) || double.IsInfinity(observed.Value<double>())))
                        throw new SimulatorException("NonFiniteValue", "PLC real value is NaN or infinite; JSON capture cannot represent it as a finite number.");
                    value["value"] = observed; value["quality"] = "good";
                }
                catch (Exception ex) { value["quality"] = "bad"; value["error"] = ex.Message; value["errorCode"] = ErrorCode(ex); }
                value["completedAtUtc"] = DateTime.UtcNow.ToString("O");
                values.Add(value);
            }
            return values;
        }

        private static JArray ApplyWrites(Selection selected, List<Signal> signals)
        {
            var completed = new JArray();
            foreach (var signal in signals)
            {
                try
                {
                    WriteValue(selected.Instance, signal);
                    completed.Add(new JObject { ["symbol"] = signal.Symbol, ["type"] = signal.Type, ["value"] = signal.Value, ["appliedAtUtc"] = DateTime.UtcNow.ToString("O") });
                }
                catch (Exception ex) { throw new SimulatorException("PartialSimulationWrite", "Write failed at " + signal.Symbol + ": " + ex.Message, new JObject { ["completedWrites"] = completed, ["failedSymbol"] = signal.Symbol, ["retrySafe"] = false }); }
            }
            return completed;
        }

        private static List<Signal> Signals(JToken input, bool writing)
        {
            var array = input as JArray;
            if (array == null || array.Count < 1 || array.Count > 64) throw new SimulatorException("InvalidRequest", "Provide 1–64 signal objects.");
            return array.Select(token =>
            {
                var item = token as JObject ?? throw new SimulatorException("InvalidRequest", "Each signal must be an object.");
                var signal = new Signal { Symbol = RequiredString(item, "symbol", 512), Type = RequiredString(item, "type", 16).ToLowerInvariant(), Value = item["value"] };
                ValidateValue(signal, writing);
                return signal;
            }).ToList();
        }

        private static void ValidateValue(Signal signal, bool writing)
        {
            string type = signal.Type;
            if (!new[] { "bool", "int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64", "float", "double", "string", "wstring" }.Contains(type))
                throw new SimulatorException("UnsupportedSignalType", "Unsupported type: " + type);
            if (!writing) return;
            var value = signal.Value;
            if (value == null || value.Type == JTokenType.Null) throw new SimulatorException("InvalidRequest", "A non-null value is required for " + signal.Symbol);
            if (type == "bool") { if (value.Type != JTokenType.Boolean) throw new SimulatorException("InvalidRequest", "Boolean JSON value required."); return; }
            if (type == "string" || type == "wstring")
            {
                if (value.Type != JTokenType.String || value.Value<string>().Length > 4096) throw new SimulatorException("InvalidRequest", "String JSON value of at most 4096 characters required.");
                return;
            }
            if (type == "float" || type == "double")
            {
                if (value.Type != JTokenType.Float && value.Type != JTokenType.Integer) throw new SimulatorException("InvalidRequest", "Numeric JSON value required.");
                double number = (double)value;
                if (double.IsNaN(number) || double.IsInfinity(number) || type == "float" && (number > float.MaxValue || number < -float.MaxValue)) throw new SimulatorException("InvalidRequest", "Finite numeric value within the selected type range required.");
                return;
            }
            if (value.Type != JTokenType.Integer) throw new SimulatorException("InvalidRequest", "Integer JSON value required.");
            // Checked conversions validate the entire write batch before the first write.
            switch (type)
            {
                case "int8": checked { var v = (sbyte)value.Value<long>(); } break;
                case "uint8": checked { var v = (byte)(ulong)value; } break;
                case "int16": checked { var v = (short)value.Value<long>(); } break;
                case "uint16": checked { var v = (ushort)(ulong)value; } break;
                case "int32": checked { var v = (int)value.Value<long>(); } break;
                case "uint32": checked { var v = (uint)(ulong)value; } break;
                case "int64": value.Value<long>(); break;
                case "uint64": checked { var v = (ulong)value; } break;
            }
        }

        private static JToken ReadValue(IInstance instance, Signal signal)
        {
            string s = signal.Symbol;
            switch (signal.Type)
            {
                case "bool": return new JValue(instance.ReadBool(s));
                case "int8": return new JValue(instance.ReadInt8(s));
                case "uint8": return new JValue(instance.ReadUInt8(s));
                case "int16": return new JValue(instance.ReadInt16(s));
                case "uint16": return new JValue(instance.ReadUInt16(s));
                case "int32": return new JValue(instance.ReadInt32(s));
                case "uint32": return new JValue(instance.ReadUInt32(s));
                case "int64": return new JValue(instance.ReadInt64(s));
                case "uint64": return new JValue(instance.ReadUInt64(s));
                case "float": return new JValue(instance.ReadFloat(s));
                case "double": return new JValue(instance.ReadDouble(s));
                case "string": return new JValue(instance.ReadString(s));
                case "wstring": return new JValue(instance.ReadWString(s));
                default: throw new SimulatorException("UnsupportedSignalType", signal.Type);
            }
        }

        private static void WriteValue(IInstance instance, Signal signal)
        {
            string s = signal.Symbol; var v = signal.Value;
            switch (signal.Type)
            {
                case "bool": instance.WriteBool(s, v.Value<bool>()); break;
                case "int8": instance.WriteInt8(s, v.Value<sbyte>()); break;
                case "uint8": instance.WriteUInt8(s, v.Value<byte>()); break;
                case "int16": instance.WriteInt16(s, v.Value<short>()); break;
                case "uint16": instance.WriteUInt16(s, v.Value<ushort>()); break;
                case "int32": instance.WriteInt32(s, v.Value<int>()); break;
                case "uint32": instance.WriteUInt32(s, v.Value<uint>()); break;
                case "int64": instance.WriteInt64(s, v.Value<long>()); break;
                case "uint64": instance.WriteUInt64(s, (ulong)v); break;
                case "float": instance.WriteFloat(s, (float)v); break;
                case "double": instance.WriteDouble(s, (double)v); break;
                case "string": instance.WriteString(s, v.Value<string>()); break;
                case "wstring": instance.WriteWString(s, v.Value<string>()); break;
            }
        }

        private static void ValidateAssertions(JToken input)
        {
            if (input == null) return;
            var array = input as JArray;
            if (array == null || array.Count > 64) throw new SimulatorException("InvalidRequest", "assertions must be an array of at most 64 objects.");
            foreach (var token in array)
            {
                var item = token as JObject ?? throw new SimulatorException("InvalidRequest", "Each assertion must be an object.");
                var signal = Signals(new JArray(new JObject { ["symbol"] = item["symbol"], ["type"] = item["type"], ["value"] = item["expected"] }), true)[0];
                string op = item.Value<string>("operator") ?? "eq";
                if (!new[] { "eq", "ne", "gt", "gte", "lt", "lte" }.Contains(op)) throw new SimulatorException("InvalidRequest", "Unknown assertion operator.");
                if ((signal.Type == "bool" || signal.Type == "string" || signal.Type == "wstring") && op != "eq" && op != "ne")
                    throw new SimulatorException("InvalidRequest", "Only eq/ne compare strings and booleans.");
                if (item["tolerance"] != null && item["tolerance"].Type != JTokenType.Float && item["tolerance"].Type != JTokenType.Integer)
                    throw new SimulatorException("InvalidRequest", "Tolerance must be numeric.");
                double tolerance = item.Value<double?>("tolerance") ?? 0;
                if (tolerance < 0 || double.IsNaN(tolerance) || double.IsInfinity(tolerance)) throw new SimulatorException("InvalidRequest", "Tolerance must be finite and nonnegative.");
            }
        }

        private static JObject Evaluate(Selection selected, JObject assertion, Stopwatch clock)
        {
            var signal = Signals(new JArray(new JObject { ["symbol"] = assertion["symbol"], ["type"] = assertion["type"] }), false)[0];
            var result = (JObject)assertion.DeepClone(); result["elapsedMs"] = clock.ElapsedMilliseconds;
            try
            {
                var actual = ReadValue(selected.Instance, signal); result["actual"] = actual;
                string op = assertion.Value<string>("operator") ?? "eq";
                result["passed"] = SimulationAssertions.Compare(signal.Type, actual, assertion["expected"], op, assertion.Value<double?>("tolerance") ?? 0);
            }
            catch (Exception ex) { result["passed"] = false; result["error"] = ex.Message; }
            return result;
        }

        private Selection Get(JObject args)
        {
            int id = Integer(args, "instanceId", 0, int.MaxValue);
            Selection selected;
            if (!_instances.TryGetValue(id, out selected)) throw new SimulatorException("SimulationNotSelected", "Create or explicitly select this local simulated instance first.");
            if (!SimulationRuntimeManager.RegisteredInstanceInfo.Any(i => i.ID == selected.Id && i.Name == selected.Name))
                throw new SimulatorException("SimulationTargetMismatch", "The selected instance disappeared or its ID was reused. No action was performed.");
            if (selected.Owned && !string.Equals(selected.Instance.StoragePath, selected.Storage, StringComparison.OrdinalIgnoreCase))
                throw new SimulatorException("SimulationTargetMismatch", "The owned instance storage changed. No action was performed.");
            return selected;
        }

        private static void EnsureSoftbus(Selection selected)
        {
            if (SimulationRuntimeManager.NetworkMode != ENetworkMode.Softbus || selected.Instance.CommunicationInterface != ECommunicationInterface.Softbus)
                throw new SimulatorException("SoftbusRequired", "Runtime writes and RUN require a local Softbus instance. This adapter never changes shared network configuration.");
        }

        private static JObject Info(Selection selected)
        {
            var result = new JObject { ["id"] = selected.Id, ["name"] = selected.Name, ["owned"] = selected.Owned, ["storagePath"] = selected.Storage };
            result["programEvidence"] = ProgramEvidence(selected);
            try
            {
                result["operatingState"] = selected.Instance.OperatingState.ToString();
                result["communicationInterface"] = selected.Instance.CommunicationInterface.ToString();
                result["cpuType"] = selected.Instance.CPUType.ToString();
                try { result["licenseStatus"] = selected.Instance.LicenseStatus.ToString(); }
                catch (Exception ex) { result["licenseStatus"] = "unverified"; result["licenseReason"] = ex.Message; }
                result["licenseStatusBasis"] = "Current runtime license status only; this property does not prove an available floating-license entitlement for an Off CPU.";
            }
            catch (Exception ex) { result["available"] = false; result["error"] = ex.Message; }
            return result;
        }

        private static JObject ProgramEvidence(Selection selected)
        {
            var result = new JObject { ["status"] = "unknown", ["runtimeReadbackVerified"] = false };
            if (!selected.Owned) return result;
            try
            {
                string path = Path.Combine(selected.Storage, "simulation-download.json");
                var file = new FileInfo(path);
                if (!file.Exists) return result;
                SimulationTicket.VerifyPlainDirectory(selected.Storage);
                if (file.Length > 16 * 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid download report file.");
                var manifest = JObject.Parse(File.ReadAllText(path));
                if (manifest.Value<int>("instanceId") != selected.Id || manifest.Value<string>("instanceName") != selected.Name)
                    throw new IOException("Download report instance mismatch.");
                result["status"] = manifest.Value<bool?>("completed") == true ? "generated-memory-card" : "generation-failed";
                result["programRevision"] = manifest["programRevision"];
                result["project"] = manifest["project"];
                result["plcPath"] = manifest["plcPath"];
                result["generatedAtUtc"] = manifest["completedAtUtc"];
                result["reportPath"] = path;
                result["basis"] = "Retained engineering generation manifest; subsequent external card/program changes are not detected by this evidence.";
            }
            catch (Exception ex) { result["status"] = "unavailable"; result["error"] = ex.Message; }
            return result;
        }

        private static JObject Envelope(Selection selection = null) => new JObject { ["origin"] = "PLCSIM Advanced V8/local API", ["observedAtUtc"] = DateTime.UtcNow.ToString("O"), ["instanceId"] = selection?.Id, ["instanceName"] = selection?.Name };
        private static string RequiredString(JObject args, string name, int maximum)
        {
            if (args[name]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(args.Value<string>(name)) || args.Value<string>(name).Length > maximum)
                throw new SimulatorException("InvalidRequest", name + " must be a nonempty string of at most " + maximum + " characters.");
            return args.Value<string>(name);
        }
        private static int Integer(JObject args, string name, int minimum, int maximum)
        {
            if (args[name]?.Type != JTokenType.Integer) throw new SimulatorException("InvalidRequest", name + " must be an integer.");
            long n = args.Value<long>(name);
            if (n < minimum || n > maximum) throw new SimulatorException("InvalidRequest", name + " is outside the permitted range.");
            return (int)n;
        }
        private static int OptionalInteger(JObject args, string name, int defaultValue, int minimum, int maximum) => args[name] == null ? defaultValue : Integer(args, name, minimum, maximum);
        private static string DirectoryArgument(JObject args)
        {
            string path = RequiredString(args, "artifactDirectory", 1024);
            SimulationTicket.VerifyPlainDirectory(path);
            path = Path.GetFullPath(path); Directory.CreateDirectory(path); return path;
        }

        public void Dispose()
        {
            // Interface disposal does not unregister/power off CPUs. Cleanup is explicit.
            foreach (var selected in _instances.Values)
            {
                try { RestoreCaptureMode(selected); } catch (Exception ex) { Console.Error.WriteLine("Simulator capture mode restore: " + ex.Message); }
                try { selected.Instance.Dispose(); } catch (Exception ex) { Console.Error.WriteLine("Simulator interface release: " + ex.Message); }
            }
            _instances.Clear();
            if (_initialized) { try { SimulationRuntimeManager.Shutdown(); } catch (Exception ex) { Console.Error.WriteLine("Simulator API shutdown: " + ex.Message); } }
        }

        private sealed class Selection { internal IInstance Instance; internal int Id; internal string Name; internal bool Owned; internal string Storage;
            internal string CreationRoot; internal JObject CreationReceipt; internal EOperatingMode? CaptureRestoreMode; }
        private sealed class Signal { internal string Symbol; internal string Type; internal JToken Value; }
    }
}
