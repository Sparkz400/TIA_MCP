using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.HW;
using Siemens.Engineering.Online;
using Siemens.Simatic.Simulation.Runtime;
using TiaMcp.Simulation;

namespace TiaMcp.Openness.V21
{
    // simulation_download_online: compile the PLC that produced an owned instance's memory card and download its software
    // online to that same powered instance, through DownloadProvider on PN/IE / PLCSIM (1) only. The owned-instance checks
    // of the online compare route apply: current ticket and marker, the sole registered local Softbus simulator and one
    // addressable endpoint at its own address; every TIA download configuration is answered from the reviewed options or
    // refused. Never a physical PLC, never a hardware download.
    internal sealed partial class OpennessSession
    {
        private void PrepareSimulationDownloadOnline(EngineeringChange change, JObject preview)
        {
            if (!_ownsPortal) throw new BridgeException("ScratchIsolationRequired", "Online simulator downloads require a worker-owned TIA instance started with tia_start.");
            if (string.IsNullOrWhiteSpace(change.Payload.Value<string>("downloadTicket")))
                throw new BridgeException("PhysicalPlcRefused", "Online downloads are limited to a ticket-bound owned PLCSIM Advanced instance (downloadTicket from sim_create); physical PLCs are refused.");
            var options = SimulationOnlineDownloadOptions(change.Payload);
            var marker = OwnedSimulationMarker(change.Payload);
            var plc = SelectPlc(change.Selector);
            var hardware = OwnedSimulationHardware(plc);
            if (hardware.GetService<DownloadProvider>() == null) throw new BridgeException("UnsupportedCapability", "The selected CPU does not expose DownloadProvider.");
            if (plc.Software.GetService<ICompilable>() == null) throw new BridgeException("UnsupportedCapability", "The selected PLC does not expose compilation.");
            RequireOwnedSimulationProgramTarget(marker, plc);
            var login = OwnedSimulationLogin(change.Payload, marker);
            var credential = ValidateSimulationCredential(change.Payload, plc.Project, hardware);
            if (login != null) preview["login"] = login.Evidence;
            if (credential != null) preview["credential"] = credential.Evidence;
            ((JArray)preview["effects"]).Add(new JObject { ["action"] = "downloadSoftwareToOwnedSimulation", ["instanceId"] = marker["instanceId"].DeepClone(),
                ["instanceName"] = marker["instanceName"].DeepClone(), ["plcPath"] = plc.Path, ["options"] = options });
            ((JArray)preview["warnings"]).Add("Compiles the PLC software, then downloads it online to the ticket-bound owned PLCSIM instance. This is a CPU write: the project checkpoint cannot undo it. " +
                (options.Value<bool>("allowStop") ? "TIA may stop the CPU for the download; a CPU found in RUN is restarted afterwards." : "A download that needs the CPU stopped is refused."));
        }

        private void ApplySimulationDownloadOnline(EngineeringChange change)
        {
            var options = SimulationOnlineDownloadOptions(change.Payload);
            var marker = OwnedSimulationMarker(change.Payload);
            var plc = SelectPlc(change.Selector);
            var hardware = OwnedSimulationHardware(plc);
            RequireOwnedSimulationProgramTarget(marker, plc);
            var login = OwnedSimulationLogin(change.Payload, marker);
            var credential = ValidateSimulationCredential(change.Payload, plc.Project, hardware);
            // The download carries the reviewed in-memory state; a failed compilation downloads nothing.
            change.Compilation = PlcCompilation.Compile(plc.Software.GetService<ICompilable>(), plc.Path, stage => { change.Stages.Add(stage); WriteChangeJournal(change); });
            if (!CompilationEvidenceRules.IsSuccessful(change.Compilation))
                throw new BridgeException("CompileFailed", "The PLC failed compilation or its diagnostic evidence is incomplete; nothing was downloaded.");
            change.Stages.Add("compile_finished");
            WriteChangeJournal(change);
            var report = OwnedSimulationReport(marker, "online software download");
            report["plcPath"] = plc.Path;
            report["options"] = options;
            report["projectModifiedBefore"] = plc.Project.IsModified;
            change.Readbacks.Add(new JObject { ["role"] = "owned-simulation-download", ["report"] = report });
            // Planned during shared admission, like the compare route's observer project.
            var failure = OwnedSimulationDownload(marker, plc, hardware, SimulationDiscoveryObserverDirectory(_sharedMutationArguments), report, options, login, credential);
            if (failure != null) throw failure;
            report["completed"] = true;
            report["completedAtUtc"] = DateTime.UtcNow.ToString("O");
            // The simulator worker's programEvidence reports these records beside the generated card.
            string storage = marker.Value<string>("storagePath");
            SimulationTicket.VerifyPlainDirectory(storage);
            string record = Path.Combine(storage, "simulation-download-online-" + change.Id + ".json");
            using (var output = new StreamWriter(new FileStream(record, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
                output.Write(new JObject { ["instanceId"] = marker["instanceId"].DeepClone(), ["instanceName"] = marker["instanceName"].DeepClone(), ["changeId"] = change.Id,
                    ["project"] = ProjectInfo(plc.Project), ["plcPath"] = plc.Path, ["options"] = options, ["download"] = report["download"]?.DeepClone(),
                    ["completed"] = true, ["completedAtUtc"] = report["completedAtUtc"].DeepClone() }.ToString(Formatting.Indented));
            change.Readbacks.Add(new JObject { ["role"] = "owned-simulation-program-record", ["artifact"] = ChangeArtifact(record) });
        }

        private static JObject SimulationOnlineDownloadOptions(JObject payload)
        {
            try { return SimulationOnlineDownloadRules.Options(payload); }
            catch (ArgumentException ex) { throw new BridgeException("InvalidRequest", ex.Message); }
        }

        // The owned instance's generated card came from exactly this project and PLC; another PLC is never downloaded to it.
        private static void RequireOwnedSimulationProgramTarget(JObject marker, PlcSelection plc)
        {
            var downloaded = ReadSimulationCredentialJson(Path.Combine(marker.Value<string>("storagePath"), "simulation-download.json"), 16 * 1024 * 1024);
            if (!SimulationDownloadEvidenceRules.IsSuccessfulForInstance(downloaded, marker) || downloaded.Value<string>("plcPath") != plc.Path ||
                !SameSimulationPath(downloaded["project"]?.Value<string>("path"), ProjectInfo(plc.Project).Value<string>("path")))
                throw new BridgeException("SimulationTargetMismatch", "The owned instance's generated memory card came from another project or PLC; only that PLC is downloaded to it online.");
        }

        private BridgeException OwnedSimulationDownload(JObject marker, PlcSelection plc, DeviceItem hardware, string observerDirectory, JObject report, JObject options,
            SimulationCredential login, SimulationCredential credential)
        {
            var challenges = new OwnedSimulationChallenges(login);
            if (login != null) report["login"] = login.Evidence;
            report["onlineChallenges"] = challenges.Observed;
            var configurations = new JArray();
            report["downloadConfigurations"] = configurations;
            var cleanup = (JArray)report["cleanup"];
            IInstance instance = null; SimulationDiscoveryObserver observer = null; ConnectionConfiguration configuration = null; OnlineProvider online = null;
            BridgeException failure = null, callbackFailure = null;
            try
            {
                RetainSimulationRuntimeUntilWorkerExit();
                instance = OwnedPoweredSimulation(marker);
                var ips = VerifyOwnedPoweredSimulation(instance, marker, null);
                bool wasRunning = instance.OperatingState == EOperatingState.Run;
                report["operatingStateBefore"] = instance.OperatingState.ToString();
                report["stage"] = "identifyEndpoint";
                observer = SimulationDiscoveryObserver.Start(observerDirectory, _processId);
                report["discoveryObserver"] = observer.Evidence.DeepClone();
                string address = OwnedSimulationEndpoint(ips, observer, report);
                challenges.Reverify = () => VerifyOwnedPoweredSimulation(instance, marker, address);
                online = hardware.GetService<OnlineProvider>();
                if (online != null && online.State != OnlineState.Offline) throw new BridgeException("OfflineRequired", "The selected PLC must be offline before the owned PLCSIM download route is configured.");
                var provider = hardware.GetService<DownloadProvider>() ?? throw new BridgeException("UnsupportedCapability", "The selected CPU does not expose DownloadProvider.");
                report["stage"] = "configureRoute";
                configuration = provider.Configuration;
                var pc = configuration.Modes.Find("PN/IE")?.PcInterfaces.Find("PLCSIM", 1);
                if (pc == null)
                {
                    report["connectionInventory"] = SimulationProvisionConnectionInventory(configuration);
                    throw new BridgeException("SimulationProvisionRouteUnavailable", "The download service has no PN/IE / PLCSIM (1) connection. No alternate adapter or address was selected.");
                }
                configuration.OnlineLegitimation += challenges.Reject;
                var targets = pc.TargetInterfaces.Take(17).ToArray();
                report["targetInterfaces"] = new JArray(targets.Take(16).Select(target => target.Name));
                if (targets.Length != 1 || string.IsNullOrWhiteSpace(targets[0].Name))
                    throw new BridgeException("SimulationProvisionTargetInterfaceAmbiguous", "The PLC's PLCSIM (1) connection must expose one uniquely named target interface. No slot is guessed.");
                report["routeSelection"] = new JObject { ["mode"] = "PN/IE", ["pcInterface"] = pc.Name, ["pcInterfaceNumber"] = pc.Number, ["targetInterface"] = targets[0].Name, ["address"] = address };
                VerifyOwnedPoweredSimulation(instance, marker, address);
                DownloadConfigurationDelegate Answer(bool preDownload) => configurationRequest =>
                {
                    string type = configurationRequest?.GetType().Name ?? "unknown";
                    var entry = new JObject { ["phase"] = preDownload ? "pre" : "post", ["type"] = type };
                    if (configurations.Count < 64) configurations.Add(entry);
                    try
                    {
                        entry["message"] = configurationRequest.Message;
                        // Identity only: the CPU may be stopping or starting while TIA asks.
                        VerifySimulationProvisionIdentity(instance, marker, false);
                        if (configurationRequest is PlcMasterSecretPassword secret)
                        {
                            if (credential == null) throw new BridgeException("SimulationCredentialRequired", "TIA requires the generated scratch CPU configuration password; supply its credentialMetadataPath. No protection was disabled.");
                            using (var password = OpenSimulationCredential(credential)) secret.SetPassword(password);
                            entry["selection"] = "generatedScratchCredential"; entry["secretReturned"] = false;
                            return;
                        }
                        string selection = SimulationOnlineDownloadRules.Selection(type, preDownload, options, wasRunning, out string refusal)
                            ?? throw new BridgeException(refusal, "The owned simulator download requested " + type + "; the reviewed options do not answer it: " + configurationRequest.Message);
                        switch (configurationRequest)
                        {
                            case ConsistentBlocksDownload blocks: blocks.CurrentSelection = ConsistentBlocksDownloadSelections.ConsistentDownload; break;
                            case AlarmTextLibrariesDownload alarms: alarms.CurrentSelection = AlarmTextLibrariesDownloadSelections.ConsistentDownload; break;
                            case UserManagementDownload users: users.CurrentSelection = UserManagementPreDownloadSelections.KeepOnlineUserManagementData; break;
                            case DataBlockReinitializationOrKeepActualValues values: values.CurrentSelection = selection == "KeepActualValues"
                                ? DataBlockReinitializationOrKeepActualValuesSelections.KeepActualValues : DataBlockReinitializationOrKeepActualValuesSelections.StopPlcAndReinitialize; break;
                            case DataBlockReinitialization reinitialize: reinitialize.CurrentSelection = DataBlockReinitializationSelections.StopPlcAndReinitialize; break;
                            case StopModules stop: stop.CurrentSelection = StopModulesSelections.StopAll; break;
                            case StartModules start: start.CurrentSelection = selection == "StartModule" ? StartModulesSelections.StartModule : StartModulesSelections.NoAction; break;
                        }
                        entry["selection"] = selection;
                    }
                    catch (Exception ex)
                    {
                        entry["exceptionType"] = ex.GetType().FullName;
                        callbackFailure = callbackFailure ?? (ex as BridgeException ?? (configurationRequest is PlcMasterSecretPassword
                            ? new BridgeException("SimulationCredentialValidationFailed", "TIA could not validate the generated scratch CPU configuration password. The native credential exception text is withheld.")
                            : new BridgeException("SimulationDownloadConfigurationFailed", "The typed " + type + " callback failed: " + ex.Message)));
                        throw;
                    }
                };
                report["stage"] = "download";
                report["downloadAttempted"] = true;
                var result = provider.Download(targets[0], Answer(true), Answer(false), options.Value<string>("softwareScope") == "changes" ? DownloadOptions.SoftwareOnlyChanges : DownloadOptions.Software);
                var messages = new JArray(); var budget = new ScanBudget(2000);
                SimulationDownloadMessages(result.Messages, messages, budget, 0);
                report["download"] = new JObject { ["state"] = result.State.ToString(), ["errors"] = result.ErrorCount, ["warnings"] = result.WarningCount, ["messages"] = messages, ["truncated"] = budget.Truncated };
                if (challenges.Failure != null) throw challenges.Failure;
                if (callbackFailure != null) throw callbackFailure;
                if (!CompilationEvidenceRules.IsSuccessfulTarget((JObject)report["download"], "errors", "truncated"))
                    throw new BridgeException("SimulationDownloadFailed", "TIA reported download errors or incomplete diagnostics; the instance state is reported.");
                VerifySimulationProvisionIdentity(instance, marker, false);
                // A CPU restarted by StartModules passes through its startup; wait briefly for RUN.
                for (int wait = 0; wasRunning && instance.OperatingState != EOperatingState.Run && wait < 100; wait++) Thread.Sleep(100);
                report["operatingStateAfterDownload"] = instance.OperatingState.ToString();
                if (wasRunning && instance.OperatingState != EOperatingState.Run)
                    throw new BridgeException("SimulationRunNotRestored", "The owned instance was in RUN before the download and did not return to RUN.");
            }
            catch (Exception ex) { failure = OwnedSimulationFailure(report, challenges, callbackFailure ?? ex, "SimulationDownloadFailed"); }
            finally
            {
                if (online != null)
                {
                    try
                    {
                        if (online.State != OnlineState.Offline) online.GoOffline();
                        cleanup.Add(new JObject { ["action"] = "goOffline", ["completed"] = online.State == OnlineState.Offline, ["onlineState"] = online.State.ToString() });
                        if (online.State != OnlineState.Offline && failure == null) failure = new BridgeException("SimulationOnlineCleanupFailed", "The PLC connection did not reach Offline.");
                    }
                    catch (Exception ex)
                    {
                        cleanup.Add(new JObject { ["action"] = "goOffline", ["completed"] = false, ["exceptionType"] = ex.GetType().FullName });
                        if (failure == null) failure = new BridgeException("SimulationOnlineCleanupFailed", "The PLC connection could not be taken offline.");
                    }
                }
                if (configuration != null) failure = OwnedSimulationDetach(configuration, challenges, cleanup, failure);
                failure = OwnedSimulationRelease(observer, instance, report, failure);
                report["projectModifiedAfter"] = plc.Project.IsModified;
            }
            return failure;
        }
    }
}
