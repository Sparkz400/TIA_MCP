using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Simatic.Simulation.Runtime;
using TiaMcp.Simulation;

namespace TiaMcp.Simulator.V8
{
    // Virtual time, event injection, memory reset and storage archive/retrieve. All writes are limited to an instance this
    // worker created or adopted (owned) in its admitted network mode; reads work on any explicitly selected instance.
    internal sealed partial class SimulatorSession
    {
        private const string VirtualTimingBasis = "Virtual time: TimespanSynchronized_C, each wait is StartProcessing of the requested span followed by the next sync point; elapsedMs is the instance's virtual SystemTime since the scenario started. Samples are read while the CPU is frozen.";

        private JObject Time(JObject args)
        {
            var selected = Get(args);
            double? factor = !Given(args, "scaleFactor") ? (double?)null : Control(() => SimulationRuntimeControl.ScaleFactor(args["scaleFactor"]));
            DateTime? time = !Given(args, "systemTimeUtc") ? (DateTime?)null : Control(() => SimulationRuntimeControl.SystemTimeUtc(args["systemTimeUtc"]));
            var result = Envelope(selected);
            result["before"] = TimeState(selected);
            if (factor != null || time != null)
            {
                RequireOwned(selected);
                EnsureAdmitted(selected);
                RequireNoCapture(selected);
                if (factor != null) selected.Instance.ScaleFactor = factor.Value;
                if (time != null) selected.Instance.SystemTime = time.Value;
            }
            var after = TimeState(selected);
            result["after"] = after;
            if (factor != null && Math.Abs(selected.Instance.ScaleFactor - factor.Value) > 1e-9)
                throw new SimulatorException("SimulationTimeNotApplied", "The scale factor read back differs from the request.", result);
            if (time != null)
            {
                // The virtual clock keeps running after the write; it may only have advanced by the elapsed call time at the set scale.
                double drift = (selected.Instance.SystemTime - time.Value).TotalMilliseconds;
                if (drift < -1000 || drift > 5000 * Math.Max(1, selected.Instance.ScaleFactor))
                    throw new SimulatorException("SimulationTimeNotApplied", "The virtual system time read back is not the requested instant.", result);
            }
            result["note"] = "ScaleFactor speeds up (above 1) or slows down (below 1) the virtual clock in the default operating mode from the next cycle control point. Operating-mode changes are made only by a virtual-time sim_test, which restores the previous mode.";
            return result;
        }

        private static JObject TimeState(Selection selected)
        {
            var time = selected.Instance.SystemTime;
            return new JObject { ["scaleFactor"] = selected.Instance.ScaleFactor, ["systemTime"] = time.ToString("O"), ["systemTimeKind"] = time.Kind.ToString(),
                ["operatingMode"] = selected.Instance.OperatingMode.ToString(), ["operatingState"] = selected.Instance.OperatingState.ToString() };
        }

        private JObject Event(JObject args)
        {
            var selected = Get(args);
            RequireOwned(selected);
            EnsureAdmitted(selected);
            JObject plan = !Given(args, "event") ? null : Control(() => SimulationRuntimeControl.Event(args["event"]));
            var result = Envelope(selected);
            result["configuredProcessEvents"] = new JArray(selected.Instance.GetConfiguredProcessEvents().Take(256).Select(item => new JObject {
                ["hardwareId"] = item.HardwareIdentifier, ["channel"] = item.Channel, ["processEventType"] = item.ProcessEventType.ToString(), ["name"] = item.Name }));
            if (plan == null) return result;
            if (selected.Instance.OperatingState != EOperatingState.Run)
                throw new SimulatorException("SimulationNotRunning", "Interrupt OBs run only in RUN; start the owned instance first.");
            ushort hardware = (ushort)plan.Value<int>("hardwareId"), sequence = 0;
            bool sequenced = true;
            switch (plan.Value<string>("type"))
            {
                case "hardwareInterrupt":
                    selected.Instance.ProcessEvent(hardware, (ushort)plan.Value<int>("channel"), (EProcessEventType)plan.Value<int>("code"), out sequence); break;
                case "pullOrPlug":
                    selected.Instance.PullOrPlugEvent(hardware, (EPullOrPlugEventType)plan.Value<int>("code"), out sequence); break;
                case "rackOrStationFault":
                    selected.Instance.RackOrStationFaultEvent(hardware, (ERackOrStationFaultType)plan.Value<int>("code")); sequenced = false; break;
                case "statusEvent":
                    selected.Instance.StatusEvent(hardware, (ushort)plan.Value<int>("specifier")); sequenced = false; break;
                case "diagnosticAlarm":
                {
                    var items = ((JArray)plan["channels"]).Select(channel => new SDiagExtChannelDescription {
                        ChannelNumber = (ushort)channel.Value<int>("channel"), ErrorType = (ushort)channel.Value<int>("errorType"), ExtErrorType = (ushort)channel.Value<int>("extErrorType"),
                        Severity = (EDiagSeverity)channel.Value<int>("severityCode"), Direction = (EDiagProperty)channel.Value<int>("directionCode") }).ToArray();
                    selected.Instance.AlarmNotification(hardware, (ushort)plan.Value<int>("moduleStateCode"), (ushort)items.Length, items, out sequence); break;
                }
            }
            result["event"] = plan;
            result["sequenceNumber"] = sequenced ? (JToken)sequence : null;
            result["triggeredAtUtc"] = DateTime.UtcNow.ToString("O");
            result["expectedOb"] = SimulationRuntimeControl.ExpectedOb(plan.Value<string>("type"));
            result["basis"] = "The runtime accepted the event. Whether its OB ran is shown only by program-visible values, for example an OB counter read with sim_read.";
            return result;
        }

        private JObject Storage(JObject args)
        {
            var selected = Get(args);
            RequireOwned(selected);
            string root = DirectoryArgument(args), action = RequiredString(args, "action", 16);
            if (action != "archive" && action != "retrieve") throw new SimulatorException("InvalidRequest", "action must be archive or retrieve.");
            if (selected.Instance.OperatingState != EOperatingState.Off)
                throw new SimulatorException("SimulationMustBeOff", "Storage archive and retrieve require the owned instance to be powered off.");
            var result = Envelope(selected);
            result["action"] = action;
            string marker = Path.Combine(selected.Storage, SimulationTicket.MarkerName), markerBefore = FileSha256(marker);
            if (action == "archive")
            {
                string path = Path.Combine(root, selected.Name + "-" + Guid.NewGuid().ToString("N") + ".zip");
                selected.Instance.ArchiveStorage(path);
                if (!File.Exists(path)) throw new SimulatorException("ArchiveStorageNotCreated", "ArchiveStorage returned without writing the archive.");
                var receipt = new JObject { ["schemaVersion"] = 1, ["archivePath"] = path, ["instanceId"] = selected.Id, ["instanceName"] = selected.Name,
                    ["creationId"] = selected.CreationReceipt?["creationId"], ["sha256"] = FileSha256(path), ["bytes"] = new FileInfo(path).Length, ["createdAtUtc"] = DateTime.UtcNow.ToString("O") };
                using (var output = new StreamWriter(new FileStream(path + ".json", FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
                    output.Write(receipt.ToString(Formatting.Indented));
                result["archive"] = receipt;
            }
            else
            {
                string path = Path.GetFullPath(RequiredString(args, "archivePath", 1024));
                var receiptFile = new FileInfo(path + ".json");
                var receipt = receiptFile.Exists && receiptFile.Length <= 65536 && (receiptFile.Attributes & FileAttributes.ReparsePoint) == 0 ? JObject.Parse(File.ReadAllText(receiptFile.FullName)) : null;
                string refusal = SimulationRuntimeControl.ArchiveRefusal(path, root, receipt, selected.Id, selected.Name, File.Exists(path) ? FileSha256(path) : null, RequiredString(args, "expectedSha256", 64));
                if (refusal != null) throw new SimulatorException(refusal, "Retrieve accepts only an unchanged archive this adapter wrote from this owned instance into its storage-archive directory. Nothing was retrieved.");
                selected.Instance.RetrieveStorage(path);
                result["retrieved"] = receipt;
            }
            string markerAfter = FileSha256(marker);
            result["ownershipMarkerUnchanged"] = markerBefore != null && markerBefore == markerAfter;
            if (markerBefore == null || markerBefore != markerAfter || !string.Equals(selected.Instance.StoragePath, selected.Storage, StringComparison.OrdinalIgnoreCase))
                throw new SimulatorException("SimulationOwnershipUnverified", "The owned instance's storage path or ownership marker changed during " + action + ".", result);
            result["operatingState"] = selected.Instance.OperatingState.ToString();
            return result;
        }

        // Holds the instance in TimespanSynchronized_C for one virtual-time scenario; Dispose restores the recorded mode
        // through the capture-mode path, so release and worker shutdown also restore it after a failure.
        private sealed class VirtualClock : IDisposable
        {
            private readonly Selection _selected;
            private readonly DateTime _start;
            internal readonly JObject Evidence;

            private VirtualClock(Selection selected, JObject evidence) { _selected = selected; Evidence = evidence; _start = selected.Instance.SystemTime; }

            internal static VirtualClock Start(Selection selected)
            {
                if (selected.CaptureRestoreMode != null) throw new SimulatorException("SimulationCaptureModeActive", "A cycle-synchronous capture or virtual-time scenario already holds this instance.");
                var previous = selected.Instance.OperatingMode;
                selected.Instance.RegisterOnSyncPointReachedEvent();
                selected.CaptureRestoreMode = previous;
                try
                {
                    selected.Instance.OperatingMode = EOperatingMode.TimespanSynchronized_C;
                    // The CPU freezes at its next cycle control point; if no sync point is reported, one minimal span reaches it.
                    var entered = selected.Instance.WaitForOnSyncPointReachedEvent(SyncTimeoutMs);
                    bool nudged = entered.ErrorCode != ERuntimeErrorCode.OK;
                    if (nudged) { selected.Instance.StartProcessing(1000000); SyncPoint(selected.Instance.WaitForOnSyncPointReachedEvent(SyncTimeoutMs)); }
                    return new VirtualClock(selected, new JObject { ["previousOperatingMode"] = previous.ToString(), ["operatingMode"] = selected.Instance.OperatingMode.ToString(),
                        ["entryRequiredStartProcessing"] = nudged, ["scaleFactor"] = selected.Instance.ScaleFactor, ["advances"] = 0 });
                }
                catch { try { RestoreCaptureMode(selected); } catch { } throw; }
            }

            internal long ElapsedMs => (long)(_selected.Instance.SystemTime - _start).TotalMilliseconds;

            internal void Advance(int milliseconds)
            {
                _selected.Instance.StartProcessing(milliseconds * 1000000L);
                SyncPoint(_selected.Instance.WaitForOnSyncPointReachedEvent(SyncTimeoutMs));
                Evidence["advances"] = Evidence.Value<int>("advances") + 1;
            }

            public void Dispose()
            {
                RestoreCaptureMode(_selected);
                Evidence["restoredOperatingMode"] = _selected.Instance.OperatingMode.ToString();
            }
        }

        // The Host serializes absent optional arguments as JSON null.
        private static bool Given(JObject args, string name) => args[name] != null && args[name].Type != JTokenType.Null;

        private static void RequireOwned(Selection selected)
        {
            if (!selected.Owned) throw new SimulatorException("OwnedInstanceRequired", "Only an instance this worker created or adopted can be changed by this operation.");
        }

        // A held operating mode is restored only by its capture's end or the worker's shutdown, never from under it.
        private static void RequireNoCapture(Selection selected)
        {
            if (selected.CaptureRestoreMode != null) throw new SimulatorException("SimulationCaptureModeActive", "A cycle-synchronous capture or virtual-time scenario holds this instance.");
        }

        private static T Control<T>(Func<T> parse)
        {
            try { return parse(); }
            catch (ArgumentException ex) { throw new SimulatorException("InvalidRequest", ex.Message); }
        }

        private static string FileSha256(string path)
        {
            if (!File.Exists(path)) return null;
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
