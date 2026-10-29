using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compare;
using Siemens.Engineering.Connection;
using Siemens.Engineering.FingerprintData;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW;
using Siemens.Engineering.Upload;
using Siemens.Engineering.Upload.Configurations;
using Siemens.Simatic.Simulation.Runtime;
using TiaMcp.Simulation;

namespace TiaMcp.Openness.V21
{
    // Online compare and station upload reuse the credential-provisioning route to an owned
    // simulator: current ticket and marker, sole registered local Softbus instance, a fresh
    // observer Portal for endpoint identity, PN/IE / PLCSIM (1) only and fail-closed challenges.
    // Unlike provisioning, they never power, address or credential the instance.
    internal sealed partial class OpennessSession
    {
        private const string StationUploadProjectName = "TiaMcpUpload";

        private JObject PlcOnlineCompare(JObject args)
        {
            if (!_ownsPortal)
                throw new BridgeException("ScratchIsolationRequired", "Online compare requires a worker-owned TIA instance started with tia_start; a user-owned Portal is never taken online.");
            var marker = OwnedSimulationMarker(args);
            var plc = SelectPlc(args);
            var hardware = OwnedSimulationHardware(plc);
            EnsureProjectOffline(plc.Project);
            int maxItems = Limit(args, 500);
            string expected = args.Value<string>("expectedFingerprintSha256");
            if (expected != null && !Regex.IsMatch(expected, "^[0-9a-f]{64}$"))
                throw new BridgeException("InvalidRequest", "expectedFingerprintSha256 must be 64 lowercase hexadecimal characters.");
            string observerDirectory = SimulationDiscoveryObserverDirectory(args);
            SimulationDiscoveryObserver.VerifyDestination(observerDirectory);
            var login = OwnedSimulationLogin(args, marker);
            var report = OwnedSimulationReport(marker, "online compare");
            report["project"] = ProjectInfo(plc.Project);
            report["plcPath"] = plc.Path;
            report["projectModifiedBefore"] = plc.Project.IsModified;
            var failure = OwnedSimulationOnline(marker, plc, hardware, observerDirectory, report, "PlcOnlineCompareFailed",
                (address, challenges) => { report["stage"] = "fingerprint"; report["fingerprint"] = OwnedSimulationFingerprint(address, challenges, maxItems, expected); },
                challenges =>
                {
                    report["stage"] = "compareToOnline";
                    var compared = plc.Software.CompareToOnline();
                    if (challenges.Failure != null) throw challenges.Failure;
                    if (compared?.RootElement == null) throw new BridgeException("PlcOnlineCompareFailed", "CompareToOnline returned no result tree.");
                    var budget = new ScanBudget(maxItems);
                    report["compare"] = OnlineCompareElement(compared.RootElement, budget, 0);
                    report["compareTruncated"] = budget.Truncated;
                    report["compareBasis"] = "PlcSoftware.CompareToOnline LeftName/RightName, CompareResultState and DetailedInformation as returned by Siemens; names alone do not establish object identity.";
                }, login);
            if (failure != null) throw new BridgeException(failure.Code, failure.Message, report);
            report["completed"] = true;
            return report;
        }

        // Owned-PLCSIM online session shared by online compare and DB snapshot changes: identify the sole endpoint, run
        // the optional pre-route step, apply the PLC's sole PN/IE / PLCSIM (1) target interface, go online, run the online
        // step, then always go offline, detach and release. Returns the first failure; the report records every stage.
        private BridgeException OwnedSimulationOnline(JObject marker, PlcSelection plc, DeviceItem hardware, string observerDirectory, JObject report, string failureCode,
            Action<string, OwnedSimulationChallenges> identified, Action<OwnedSimulationChallenges> online, SimulationCredential login = null)
        {
            var challenges = new OwnedSimulationChallenges(login);
            if (login != null) report["login"] = login.Evidence;
            report["onlineChallenges"] = challenges.Observed;
            var cleanup = (JArray)report["cleanup"];
            IInstance instance = null; SimulationDiscoveryObserver observer = null;
            OnlineProvider provider = null; ConnectionConfiguration configuration = null;
            BridgeException failure = null; bool connectAttempted = false;
            try
            {
                RetainSimulationRuntimeUntilWorkerExit();
                instance = OwnedPoweredSimulation(marker);
                var ips = VerifyOwnedPoweredSimulation(instance, marker, null);
                report["operatingStateBefore"] = instance.OperatingState.ToString();
                report["stage"] = "identifyEndpoint";
                observer = SimulationDiscoveryObserver.Start(observerDirectory, _processId);
                report["discoveryObserver"] = observer.Evidence.DeepClone();
                string address = OwnedSimulationEndpoint(ips, observer, report);
                challenges.Reverify = () => VerifyOwnedPoweredSimulation(instance, marker, address);
                provider = hardware.GetService<OnlineProvider>() ?? throw new BridgeException("UnsupportedCapability", "The selected PLC has no OnlineProvider service.");
                if (provider.State != OnlineState.Offline)
                    throw new BridgeException("OfflineRequired", "The selected PLC must be offline before the owned PLCSIM route is configured.");
                identified?.Invoke(address, challenges);
                report["stage"] = "configureRoute";
                configuration = provider.Configuration;
                var pc = configuration.Modes.Find("PN/IE")?.PcInterfaces.Find("PLCSIM", 1);
                if (pc == null)
                {
                    report["connectionInventory"] = SimulationProvisionConnectionInventory(configuration);
                    throw new BridgeException("SimulationProvisionRouteUnavailable", "The selected PLC has no PN/IE / PLCSIM (1) connection. No alternate adapter or address was selected.");
                }
                configuration.OnlineLegitimation += challenges.Reject;
                var targets = pc.TargetInterfaces.Take(17).ToArray();
                report["targetInterfaces"] = new JArray(targets.Take(16).Select(target => target.Name));
                if (targets.Length != 1 || string.IsNullOrWhiteSpace(targets[0].Name))
                    throw new BridgeException("SimulationProvisionTargetInterfaceAmbiguous", "The PLC's PLCSIM (1) connection must expose one uniquely named target interface. No slot is guessed.");
                report["routeSelection"] = new JObject { ["mode"] = "PN/IE", ["pcInterface"] = pc.Name, ["pcInterfaceNumber"] = pc.Number,
                    ["targetInterface"] = targets[0].Name, ["address"] = address };
                VerifyOwnedPoweredSimulation(instance, marker, address);
                if (!configuration.ApplyConfiguration(targets[0]))
                    throw new BridgeException("SimulationProvisionRouteUnavailable", "The sole PLCSIM target interface could not be applied to the PLC connection.");
                report["stage"] = "goOnline";
                connectAttempted = true;
                var state = provider.GoOnline();
                if (challenges.Failure != null) throw challenges.Failure;
                if (state != OnlineState.Online || provider.State != OnlineState.Online)
                    throw new BridgeException("SimulationOnlineRequired", "The owned PLCSIM connection did not reach Online.");
                VerifyOwnedPoweredSimulation(instance, marker, address);
                if (OwnedSimulationEndpoint(ips, observer, report) != address)
                    throw new BridgeException("SimulationProvisionTargetAmbiguous", "The owned PLCSIM endpoint changed while connecting.");
                online(challenges);
            }
            catch (Exception ex) { failure = OwnedSimulationFailure(report, challenges, ex, failureCode); }
            finally
            {
                if (connectAttempted)
                {
                    try
                    {
                        if (provider.State != OnlineState.Offline) provider.GoOffline();
                        bool offline = provider.State == OnlineState.Offline;
                        cleanup.Add(new JObject { ["action"] = "goOffline", ["completed"] = offline, ["onlineState"] = provider.State.ToString() });
                        if (!offline && failure == null) failure = new BridgeException("SimulationOnlineCleanupFailed", "The PLC connection did not reach Offline.");
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

        private JObject PlcStationUpload(JObject args)
        {
            EnsureAttached();
            if (!_ownsPortal || _portal.Projects.Count != 0)
                throw new BridgeException("ProjectIsolationRequired", "Station upload requires an empty worker-owned TIA instance started with tia_start; it never uploads into an open project.");
            var marker = OwnedSimulationMarker(args);
            string destination = StationUploadDestination(args);
            if (Path.Combine(destination, StationUploadProjectName).Length > 143)
                throw new BridgeException("ProjectPathTooLong", "The upload project path exceeds TIA's 143-character limit; configure a shorter artifact directory.");
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new BridgeException("ArtifactExists", "The upload destination already exists; nothing is overwritten.");
            string observerDirectory = SimulationDiscoveryObserverDirectory(args);
            SimulationDiscoveryObserver.VerifyDestination(observerDirectory);
            var login = OwnedSimulationLogin(args, marker);
            var report = OwnedSimulationReport(marker, "station upload");
            if (login != null) report["login"] = login.Evidence;
            report["destination"] = destination;
            report["reportPath"] = Path.Combine(destination, "station-upload.json");
            var challenges = new OwnedSimulationChallenges(login);
            report["onlineChallenges"] = challenges.Observed;
            var decisions = new JArray();
            report["uploadConfigurations"] = decisions;
            var cleanup = (JArray)report["cleanup"];
            IInstance instance = null; SimulationDiscoveryObserver observer = null;
            Project project = null; ConnectionConfiguration configuration = null;
            BridgeException failure = null, callbackFailure = null;
            try
            {
                RetainSimulationRuntimeUntilWorkerExit();
                instance = OwnedPoweredSimulation(marker);
                var ips = VerifyOwnedPoweredSimulation(instance, marker, null);
                report["operatingStateBefore"] = instance.OperatingState.ToString();
                report["stage"] = "identifyEndpoint";
                observer = SimulationDiscoveryObserver.Start(observerDirectory, _processId);
                report["discoveryObserver"] = observer.Evidence.DeepClone();
                string address = OwnedSimulationEndpoint(ips, observer, report);
                challenges.Reverify = () => VerifyOwnedPoweredSimulation(instance, marker, address);
                report["stage"] = "createDestinationProject";
                Directory.CreateDirectory(destination);
                project = _portal.Projects.Create(new DirectoryInfo(destination), StationUploadProjectName);
                report["project"] = ProjectInfo(project);
                var provider = project.GetService<StationUploadProvider>() ?? throw new BridgeException("UnsupportedCapability", "The new project has no StationUploadProvider service.");
                configuration = provider.Configuration;
                var pc = configuration.Modes.Find("PN/IE")?.PcInterfaces.Find("PLCSIM", 1);
                if (pc == null)
                {
                    report["connectionInventory"] = SimulationProvisionConnectionInventory(configuration);
                    throw new BridgeException("SimulationProvisionRouteUnavailable", "The upload service has no PN/IE / PLCSIM (1) connection. No alternate adapter or address was selected.");
                }
                configuration.OnlineLegitimation += challenges.Reject;
                var target = pc.Addresses.Create(address);
                report["routeSelection"] = new JObject { ["mode"] = "PN/IE", ["pcInterface"] = pc.Name, ["pcInterfaceNumber"] = pc.Number, ["address"] = address };
                VerifyOwnedPoweredSimulation(instance, marker, address);
                report["stage"] = "stationUpload";
                report["uploadAttempted"] = true;
                var upload = provider.StationUpload(target, request =>
                {
                    string type = request?.GetType().Name ?? "unknown";
                    var decision = new JObject { ["type"] = type };
                    if (decisions.Count < 64) decisions.Add(decision);
                    try
                    {
                        decision["message"] = request?.Message;
                        // Never install missing products; every other request (passwords included) is left unanswered and fails the upload.
                        if (request is UploadMissingProducts missing)
                        {
                            missing.CurrentSelection = UploadMissingProductsSelections.NoAction;
                            decision["selection"] = "NoAction";
                            callbackFailure = callbackFailure ?? new BridgeException("StationUploadMissingProducts", "The station needs products missing from this installation; they were not uploaded, so the result is partial.");
                        }
                        else callbackFailure = callbackFailure ?? new BridgeException("UnsupportedUploadConfiguration", "Station upload requested " + type + "; no password, credential or acknowledgement is supplied.");
                    }
                    catch (Exception ex)
                    {
                        decision["exceptionType"] = ex.GetType().FullName;
                        callbackFailure = callbackFailure ?? new BridgeException("StationUploadConfigurationFailed", "The typed " + type + " upload callback failed: " + ex.Message);
                    }
                });
                var messages = new JArray(); var messageBudget = new ScanBudget(10000);
                StationUploadMessages(upload.Messages, messages, messageBudget, 0);
                report["upload"] = new JObject { ["state"] = upload.State.ToString(), ["errors"] = upload.ErrorCount,
                    ["warnings"] = upload.WarningCount, ["messages"] = messages, ["truncated"] = messageBudget.Truncated };
                var station = upload.UploadedStation;
                report["uploadedStation"] = station == null ? null : new JObject { ["name"] = station.Name, ["typeIdentifier"] = station.TypeIdentifier };
                report["devices"] = project.Devices.Count;
                if (challenges.Failure != null) throw challenges.Failure;
                if (callbackFailure != null) throw callbackFailure;
                if (station == null || !CompilationEvidenceRules.IsSuccessfulTarget((JObject)report["upload"], "errors", "truncated"))
                    throw new BridgeException("StationUploadFailed", "TIA reported upload errors, incomplete messages or no uploaded station; the partial destination project is retained.");
                VerifyOwnedPoweredSimulation(instance, marker, address);
            }
            catch (Exception ex) { failure = OwnedSimulationFailure(report, challenges, ex, "StationUploadFailed"); }
            finally
            {
                if (configuration != null) failure = OwnedSimulationDetach(configuration, challenges, cleanup, failure);
                if (project != null)
                {
                    try
                    {
                        foreach (var entry in DeviceItems(project, new ScanBudget(50000)))
                        {
                            var online = entry.Item.GetService<OnlineProvider>();
                            if (online != null && online.State != OnlineState.Offline) online.GoOffline();
                        }
                        EnsureProjectOffline(project);
                        cleanup.Add(new JObject { ["action"] = "uploadedProjectOffline", ["completed"] = true });
                    }
                    catch (Exception ex)
                    {
                        cleanup.Add(new JObject { ["action"] = "uploadedProjectOffline", ["completed"] = false, ["exceptionType"] = ex.GetType().FullName });
                        if (failure == null) failure = new BridgeException("SimulationOnlineCleanupFailed", "The uploaded project could not be verified offline.");
                    }
                    // A partial result is saved too, so every uploaded product stays inspectable.
                    try { project.Save(); report["saved"] = true; }
                    catch (Exception ex)
                    {
                        report["saved"] = false;
                        cleanup.Add(new JObject { ["action"] = "saveUploadedProject", ["completed"] = false, ["exceptionType"] = ex.GetType().FullName });
                        if (failure == null) failure = new BridgeException("StationUploadSaveFailed", "The uploaded project could not be saved: " + ex.Message);
                    }
                }
                failure = OwnedSimulationRelease(observer, instance, report, failure);
                report["completed"] = failure == null;
                if (Directory.Exists(destination))
                {
                    try { File.WriteAllText(report.Value<string>("reportPath"), report.ToString(Formatting.Indented), new UTF8Encoding(false)); }
                    catch (Exception ex) { report["reportWriteError"] = ex.Message; }
                }
            }
            if (failure != null) throw new BridgeException(failure.Code, failure.Message, report);
            return report;
        }

        private string StationUploadDestination(JObject args) => SharedMutationPlannedDestination("stationUpload",
            () => Path.Combine(SharedMutationArtifactDirectory(args), "u_" + Guid.NewGuid().ToString("N").Substring(0, 12)));

        private static JObject OwnedSimulationMarker(JObject args)
        {
            try { return SimulationTicket.Decode(args.Value<string>("downloadTicket")); }
            catch { throw new BridgeException("InvalidSimulationTicket", "A current owned simulator ticket from sim_create is required; no other target is accepted."); }
        }

        private JObject OwnedSimulationReport(JObject marker, string purpose)
        {
            var report = Envelope();
            report["origin"] = "TIA Portal V21 Openness / owned PLCSIM Advanced V8 " + purpose;
            report["route"] = "PN/IE / PLCSIM (1), local Softbus only; never a physical PLC";
            report["instanceId"] = marker["instanceId"].DeepClone();
            report["instanceName"] = marker["instanceName"].DeepClone();
            report["stage"] = "preflight";
            report["completed"] = false;
            report["poweredByOperation"] = false;
            report["cleanup"] = new JArray();
            return report;
        }

        private static DeviceItem OwnedSimulationHardware(PlcSelection plc)
        {
            var budget = new ScanBudget(50000);
            var hardware = DeviceItems(plc.Project, budget).Where(entry =>
            {
                var software = entry.Item.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                return software != null && entry.Path + "/plc/" + Segment(software.Name) == plc.Path;
            }).Take(2).ToArray();
            if (budget.Truncated || hardware.Length != 1)
                throw new BridgeException("AmbiguousTarget", "Cannot uniquely resolve the selected PLC hardware.");
            return hardware[0].Item;
        }

        private static IInstance OwnedPoweredSimulation(JObject marker)
        {
            if ((SimulationRuntimeManager.Version >> 16) != 8)
                throw new BridgeException("SimulatorVersionMismatch", "PLCSIM Advanced runtime V8 is required.");
            VerifySimulationProvisionRegistration(marker);
            var instance = SimulationRuntimeManager.CreateInterface(marker.Value<int>("instanceId"));
            try { VerifyOwnedPoweredSimulation(instance, marker, null); return instance; }
            catch { RetainSimulationInterface(instance); throw; }
        }

        // Exact ticket-bound instance, still the sole registered Softbus simulator, already powered by the
        // caller (never by this operation) and, once identified, still at the same address.
        private static string[] VerifyOwnedPoweredSimulation(IInstance instance, JObject marker, string address)
        {
            VerifySimulationProvisionIdentity(instance, marker, false);
            if (instance.OperatingState != EOperatingState.Stop && instance.OperatingState != EOperatingState.Run)
                throw new BridgeException("SimulationPowerRequired", "The owned simulator must already be powered in STOP or RUN; this operation never powers, starts or stops it.");
            var ips = (instance.ControllerIP ?? new string[0]).Where(SimulationProvisionUsableIPv4).Distinct(StringComparer.Ordinal).ToArray();
            if (ips.Length == 0 || address != null && !ips.Contains(address, StringComparer.Ordinal))
                throw new BridgeException("SimulationProvisionTargetMismatch", "The owned simulator has no usable controller address or its address changed. No substitute target is selected.");
            return ips;
        }

        // Exactly one addressable endpoint may be visible and it must be the owned instance's own address.
        // Zero-address entries such as the Runtime Manager placeholder cannot be addressed; they are only recorded.
        private static string OwnedSimulationEndpoint(string[] ips, SimulationDiscoveryObserver observer, JObject report)
        {
            var observed = observer.Read(65);
            report["accessibleDevices"] = new JArray(observed.Take(64).Select(device => new JObject {
                ["name"] = device.Name, ["address"] = device.Address, ["macAddress"] = device.MACAddress, ["deviceSeries"] = device.DeviceSeries }));
            report["controllerAddresses"] = new JArray(ips);
            var addressable = observed.Where(device => SimulationProvisionUsableIPv4(device.Address)).ToArray();
            if (observed.Length > 64 || addressable.Length != 1 || !ips.Contains(addressable[0].Address, StringComparer.Ordinal))
                throw new BridgeException("SimulationProvisionTargetAmbiguous", "Fresh PLCSIM discovery must show exactly one addressable endpoint, at the owned instance's own controller address. No other device or address is selected.");
            return addressable[0].Address;
        }

        private JObject OwnedSimulationFingerprint(string address, OwnedSimulationChallenges challenges, int maxItems, string expected)
        {
            var provider = _portal.GetService<FingerprintDataProvider>();
            if (provider == null) return new JObject { ["available"] = false, ["reason"] = "This TIA Portal exposes no FingerprintDataProvider service." };
            try
            {
                var pc = provider.Configuration.Modes.Find("PN/IE")?.PcInterfaces.Find("PLCSIM", 1)
                    ?? throw new BridgeException("SimulationProvisionRouteUnavailable", "The fingerprint service has no PN/IE / PLCSIM (1) connection.");
                var data = provider.GetFingerprintData(pc.Addresses.Find(address) ?? pc.Addresses.Create(address), challenges.Reject);
                if (challenges.Failure != null) throw challenges.Failure;
                var items = new JArray(); var budget = new ScanBudget(maxItems);
                foreach (var item in data.FingerprintDataItems)
                {
                    if (!budget.Take()) break;
                    object value = item.FingerprintDataValue;
                    items.Add(new JObject { ["identifier"] = item.FingerprintDataIdentifier.ToString(),
                        ["value"] = value is byte[] bytes ? BitConverter.ToString(bytes).Replace("-", "") : value?.ToString() });
                }
                string sha256 = budget.Truncated ? null : SharedMutationHash(items);
                return new JObject { ["available"] = true, ["address"] = address, ["items"] = items, ["truncated"] = budget.Truncated, ["sha256"] = sha256,
                    ["sha256Basis"] = "SHA-256 of the ordered identifier/value list as returned; null when truncated. Equal hashes show an unchanged station fingerprint, not program identity.",
                    ["matchesExpected"] = expected == null || sha256 == null ? null : (JToken)(sha256 == expected) };
            }
            catch (Exception ex) when (challenges.Failure == null && !(ex is BridgeException))
            {
                // Siemens leaves unsupported targets unqualified; record it and still run the full compare.
                return new JObject { ["available"] = false, ["exceptionType"] = ex.GetType().FullName, ["reason"] = ex.Message };
            }
        }

        private static JObject OnlineCompareElement(CompareResultElement element, ScanBudget budget, int depth)
        {
            var children = new JArray();
            var node = new JObject { ["left"] = element.LeftName, ["right"] = element.RightName,
                ["state"] = element.ComparisonResult.ToString(), ["detail"] = element.DetailedInformation, ["children"] = children };
            if (depth >= 64) { budget.Truncated = true; return node; }
            foreach (var child in element.Elements)
            {
                if (!budget.Take()) break;
                children.Add(OnlineCompareElement(child, budget, depth + 1));
                if (budget.Truncated) break;
            }
            return node;
        }

        private static void StationUploadMessages(IEnumerable<UploadResultMessage> source, JArray output, ScanBudget budget, int depth)
        {
            if (depth > 64) { budget.Truncated = true; return; }
            foreach (var message in source)
            {
                if (!budget.Take()) return;
                output.Add(new JObject { ["message"] = message.Message, ["state"] = message.State.ToString(), ["errors"] = message.ErrorCount,
                    ["warnings"] = message.WarningCount, ["dateTime"] = message.DateTime, ["depth"] = depth });
                StationUploadMessages(message.Messages, output, budget, depth + 1);
                if (budget.Truncated) return;
            }
        }

        private static BridgeException OwnedSimulationFailure(JObject report, OwnedSimulationChallenges challenges, Exception ex, string code)
        {
            var failure = challenges.Failure ?? ex as BridgeException ?? new BridgeException(code, ex.Message);
            report["failure"] = new JObject { ["code"] = failure.Code, ["message"] = failure.Message,
                ["exceptionType"] = ex.GetType().FullName, ["stage"] = report["stage"].DeepClone() };
            return failure;
        }

        private static BridgeException OwnedSimulationDetach(ConnectionConfiguration configuration, OwnedSimulationChallenges challenges, JArray cleanup, BridgeException failure)
        {
            try { configuration.OnlineLegitimation -= challenges.Reject; }
            catch (Exception ex)
            {
                cleanup.Add(new JObject { ["action"] = "removeAuthenticationHandler", ["completed"] = false, ["exceptionType"] = ex.GetType().FullName });
                if (failure == null) failure = new BridgeException("SimulationOnlineCleanupFailed", "The rejecting authentication handler could not be removed.");
            }
            return failure;
        }

        // Stops the observer Portal and keeps (does not dispose) the instance interface like simulation_download;
        // the instance's power state is left as found and reported.
        private static BridgeException OwnedSimulationRelease(SimulationDiscoveryObserver observer, IInstance instance, JObject report, BridgeException failure)
        {
            var cleanup = (JArray)report["cleanup"];
            if (observer != null)
            {
                try
                {
                    var stopped = observer.Stop();
                    cleanup.Add(stopped);
                    if (stopped.Value<bool>("completed") != true && failure == null)
                        failure = new BridgeException("SimulationOnlineCleanupFailed", "The discovery observer Portal did not close its project and exit.");
                }
                catch (Exception ex)
                {
                    cleanup.Add(new JObject { ["action"] = "stopDiscoveryObserver", ["completed"] = false, ["exceptionType"] = ex.GetType().FullName });
                    if (failure == null) failure = new BridgeException("SimulationOnlineCleanupFailed", "The discovery observer Portal could not be stopped.");
                }
            }
            if (instance != null)
            {
                try { report["operatingStateAfter"] = instance.OperatingState.ToString(); }
                catch (Exception ex) { report["operatingStateAfterExceptionType"] = ex.GetType().FullName; }
                RetainSimulationInterface(instance);
            }
            return failure;
        }

        // Fails closed like credential provisioning without its first-use trust: TLS is answered NonTrusted, passwords are
        // never answered, and the first unanswered challenge is latched for the caller to throw. With a login, the one
        // project-user login challenge is answered with the instance's generated runtime user (SimulationOnlineLogin).
        private sealed class OwnedSimulationChallenges
        {
            internal BridgeException Failure;
            internal readonly JArray Observed = new JArray();
            private readonly SimulationCredential _login;
            private bool _answered;
            internal Action Reverify; // Re-checks the owned powered instance and endpoint before the login is sent.

            internal OwnedSimulationChallenges(SimulationCredential login = null) { _login = login; }

            internal void Reject(OnlineConfiguration request)
            {
                string type = request?.GetType().Name ?? "unknown";
                var entry = new JObject { ["type"] = type, ["decision"] = "unanswered" };
                if (Observed.Count < 16) Observed.Add(entry);
                try
                {
                    var authentication = request as OnlineAuthenticationConfiguration;
                    var offered = authentication?.GetSupportedAuthenticationTypes().Take(8).Select(item => item.CurrentUserType).ToArray() ?? new UserType[0];
                    if (authentication != null) entry["offered"] = new JArray(offered.Select(item => item.ToString()));
                    if (SimulationOnlineLogin.Decide(type, _login != null, _answered, offered.Contains(UserType.ProjectUser)) == SimulationOnlineLogin.ProjectUser)
                    {
                        _answered = true;
                        (Reverify ?? throw new BridgeException("SimulationOnlineRequired", "The owned instance was not verified before the login."))();
                        authentication.OnlineCredentials.Type = UserType.ProjectUser;
                        authentication.OnlineCredentials.Name = _login.Username;
                        using (var password = OpenSimulationCredential(_login)) authentication.OnlineCredentials.SetPassword(password);
                        entry["decision"] = SimulationOnlineLogin.ProjectUser;
                        entry["username"] = _login.Username;
                        return;
                    }
                    if (request is TlsVerificationConfiguration tls)
                    {
                        tls.CurrentSelection = TlsVerificationConfigurationSelection.NonTrusted;
                        entry["decision"] = "NonTrusted";
                        entry["certificates"] = new JArray(tls.Certificates.Take(8).Select(certificate => SimulationProvisionPublicCertificate(certificate.Certificate)));
                    }
                }
                catch (Exception ex) { entry["exceptionType"] = ex.GetType().FullName; }
                Failure = Failure ?? new BridgeException("SimulationOnlineAuthenticationRequired", "The owned PLCSIM connection requested " + type +
                    "; TLS trust is never granted and no login, password or credential is supplied.");
            }
        }
    }
}
