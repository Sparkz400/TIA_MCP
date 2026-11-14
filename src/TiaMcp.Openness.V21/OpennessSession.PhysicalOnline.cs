using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.Upload;
using Siemens.Engineering.Upload.Configurations;

namespace TiaMcp.Openness.V21
{
    // Online access to a real (physical) PLC: route inventory, online compare, station upload and download. Off unless the
    // Host was started with --physical-plc-online, which passes the same flag to this worker. Every route is explicit: the
    // caller names the PG/PC interface, the target interface and the PLC's expected IPv4 address, which must be one the
    // project configures for the selected CPU. PLCSIM interfaces are refused (the simulation tools own them). Logins and
    // passwords are never answered; a TLS certificate is trusted only when its SHA-256 equals one the user supplied after
    // checking it out of band. Downloads answer TIA's questions only from explicit acknowledgements; anything else stops them.
    // Not qualified: no physical PLC was available when this was written. The catalog marks these features unverified.
    internal sealed partial class OpennessSession
    {
        internal static bool PhysicalOnlineEnabled => Program.PhysicalPlcOnline;

        private void RequirePhysicalOnline()
        {
            if (!PhysicalOnlineEnabled) throw new BridgeException("PhysicalOnlineDisabled", "Online access to real PLCs is off. The Host must be started with --physical-plc-online.");
            EnsureAttached();
            if (!_ownsPortal) throw new BridgeException("OwnedSessionRequired", "Real-PLC online access runs only in a worker-owned instance started with tia_start; a user-owned Portal is never taken online.");
        }

        // Read-only: what the selected CPU's online service offers (modes, PG/PC interfaces, target interfaces) and which
        // addresses the project configures. Nothing goes online.
        private JObject PhysicalTargets(JObject args)
        {
            RequirePhysicalOnline();
            var plc = SelectPlc(args);
            var hardware = PhysicalCpu(plc);
            var configuration = (hardware.GetService<OnlineProvider>() ?? throw new BridgeException("UnsupportedCapability", "The selected PLC has no OnlineProvider service.")).Configuration;
            var modes = new JArray();
            foreach (var mode in configuration.Modes.Take(16))
                modes.Add(new JObject { ["mode"] = mode.Name, ["pcInterfaces"] = new JArray(mode.PcInterfaces.Take(32).Select(pc => new JObject {
                    ["name"] = pc.Name, ["number"] = pc.Number, ["isPlcsim"] = IsPlcsimInterface(pc.Name),
                    ["targetInterfaces"] = new JArray(pc.TargetInterfaces.Take(16).Select(t => t.Name)) })) });
            var result = Envelope();
            result["plcPath"] = plc.Path;
            result["plcName"] = plc.Software.Name;
            result["configuredAddresses"] = new JArray(PhysicalConfiguredAddresses(hardware));
            result["modes"] = modes;
            result["wentOnline"] = false;
            result["note"] = "Pick mode, pcInterface (name and number) and targetInterface from this list and pass the PLC's expectedAddress; it must be one of configuredAddresses.";
            return result;
        }

        private JObject PhysicalOnlineCompare(JObject args)
        {
            RequirePhysicalOnline();
            var plc = SelectPlc(args);
            var hardware = PhysicalCpu(plc);
            EnsureProjectOffline(plc.Project);
            int maxItems = Limit(args, 500);
            var report = PhysicalReport(plc, args, "online compare");
            var provider = hardware.GetService<OnlineProvider>() ?? throw new BridgeException("UnsupportedCapability", "The selected PLC has no OnlineProvider service.");
            if (provider.State != OnlineState.Offline) throw new BridgeException("OfflineRequired", "The selected PLC must be offline before a route is configured.");
            var challenges = new PhysicalChallenges(args.Value<string>("trustedCertificateSha256"));
            report["onlineChallenges"] = challenges.Observed;
            var configuration = provider.Configuration;
            BridgeException failure = null; bool connectAttempted = false;
            try
            {
                var target = PhysicalRoute(configuration, args, hardware, report);
                configuration.OnlineLegitimation += challenges.Answer;
                if (!configuration.ApplyConfiguration(target)) throw new BridgeException("PhysicalRouteUnavailable", "TIA did not accept the selected target interface for this connection.");
                report["stage"] = "goOnline";
                connectAttempted = true;
                var state = provider.GoOnline();
                if (challenges.Failure != null) throw challenges.Failure;
                if (state != OnlineState.Online) throw new BridgeException("PhysicalOnlineFailed", "The PLC connection did not reach Online (state " + state + ").");
                report["stage"] = "compareToOnline";
                var compared = plc.Software.CompareToOnline();
                if (challenges.Failure != null) throw challenges.Failure;
                if (compared?.RootElement == null) throw new BridgeException("PlcOnlineCompareFailed", "CompareToOnline returned no result tree.");
                var budget = new ScanBudget(maxItems);
                report["compare"] = OnlineCompareElement(compared.RootElement, budget, 0);
                report["compareTruncated"] = budget.Truncated;
            }
            catch (Exception ex) { failure = PhysicalFailure(report, challenges, ex, "PlcOnlineCompareFailed"); }
            finally
            {
                if (connectAttempted) failure = PhysicalGoOffline(provider, report, failure);
                configuration.OnlineLegitimation -= challenges.Answer;
                report["projectModifiedAfter"] = plc.Project.IsModified;
            }
            if (failure != null) throw new BridgeException(failure.Code, failure.Message, report);
            report["completed"] = true;
            return report;
        }

        // Uploads the station at expectedAddress into a new project in an empty owned instance; never into an open project.
        private JObject PhysicalStationUpload(JObject args)
        {
            RequirePhysicalOnline();
            if (_portal.Projects.Count != 0) throw new BridgeException("ProjectIsolationRequired", "Station upload needs an empty worker-owned instance; project_close the open project or tia_start a new one.");
            string address = PhysicalAddress(args);
            string destination = Path.Combine(ArtifactDirectory(args), "pu_" + Guid.NewGuid().ToString("N").Substring(0, 12));
            if (Path.Combine(destination, StationUploadProjectName).Length > 143) throw new BridgeException("ProjectPathTooLong", "The upload project path exceeds TIA's 143-character limit; configure a shorter artifact directory.");
            var report = new JObject { ["purpose"] = "station upload", ["target"] = "physical", ["expectedAddress"] = address, ["destination"] = destination, ["cleanup"] = new JArray() };
            var challenges = new PhysicalChallenges(args.Value<string>("trustedCertificateSha256"));
            report["onlineChallenges"] = challenges.Observed;
            var decisions = new JArray(); report["uploadConfigurations"] = decisions;
            ConnectionConfiguration configuration = null; Project project = null;
            BridgeException failure = null, callbackFailure = null;
            try
            {
                Directory.CreateDirectory(destination);
                project = _portal.Projects.Create(new DirectoryInfo(destination), StationUploadProjectName);
                report["project"] = ProjectInfo(project);
                var provider = project.GetService<StationUploadProvider>() ?? throw new BridgeException("UnsupportedCapability", "The new project has no StationUploadProvider service.");
                configuration = provider.Configuration;
                var pc = PhysicalPcInterface(configuration, args, report);
                configuration.OnlineLegitimation += challenges.Answer;
                var target = pc.Addresses.Create(address);
                report["stage"] = "stationUpload";
                var upload = provider.StationUpload(target, request =>
                {
                    string type = request?.GetType().Name ?? "unknown";
                    var decision = new JObject { ["type"] = type, ["message"] = request?.Message };
                    if (decisions.Count < 64) decisions.Add(decision);
                    if (request is UploadMissingProducts missing)
                    {
                        missing.CurrentSelection = UploadMissingProductsSelections.NoAction; decision["selection"] = "NoAction";
                        callbackFailure = callbackFailure ?? new BridgeException("StationUploadMissingProducts", "The station needs products missing from this installation; they were not uploaded, so the result is partial.");
                    }
                    else callbackFailure = callbackFailure ?? new BridgeException("UnsupportedUploadConfiguration", "Station upload requested " + type + "; no password, credential or acknowledgement is supplied.");
                });
                var messages = new JArray(); var budget = new ScanBudget(10000);
                StationUploadMessages(upload.Messages, messages, budget, 0);
                report["upload"] = new JObject { ["state"] = upload.State.ToString(), ["errors"] = upload.ErrorCount, ["warnings"] = upload.WarningCount, ["messages"] = messages, ["truncated"] = budget.Truncated };
                var station = upload.UploadedStation;
                report["uploadedStation"] = station == null ? null : new JObject { ["name"] = station.Name, ["typeIdentifier"] = station.TypeIdentifier };
                if (challenges.Failure != null) throw challenges.Failure;
                if (callbackFailure != null) throw callbackFailure;
                if (station == null || !CompilationEvidenceRules.IsSuccessfulTarget((JObject)report["upload"], "errors", "truncated"))
                    throw new BridgeException("StationUploadFailed", "TIA reported upload errors, incomplete messages or no uploaded station; the partial destination project is retained.");
            }
            catch (Exception ex) { failure = PhysicalFailure(report, challenges, ex, "StationUploadFailed"); }
            finally
            {
                if (configuration != null) configuration.OnlineLegitimation -= challenges.Answer;
                if (project != null)
                {
                    try
                    {
                        foreach (var entry in DeviceItems(project, new ScanBudget(50000)))
                        {
                            var online = entry.Item.GetService<OnlineProvider>();
                            if (online != null && online.State != OnlineState.Offline) online.GoOffline();
                        }
                        project.Save();
                        report["projectSaved"] = true;
                    }
                    catch (Exception ex) { report["projectSaved"] = false; report["saveFailure"] = ex.Message; }
                }
            }
            if (failure != null) throw new BridgeException(failure.Code, failure.Message, report);
            report["completed"] = true;
            return report;
        }

        // Download the selected PLC's software (and hardware on request) to the real PLC at expectedAddress. Every question TIA
        // asks is answered from explicit acknowledgements; an unacknowledged stop, reinitialization or password stops it.
        private JObject PhysicalDownload(JObject args)
        {
            RequirePhysicalOnline();
            if (args.Value<bool?>("acknowledgeLiveTarget") != true)
                throw new BridgeException("AcknowledgementRequired", "A download changes a real PLC and can move the machine it controls. Set acknowledgeLiveTarget:true only after confirming the machine is in a safe state.");
            var plc = SelectPlc(args);
            if (args.Value<string>("expectedPlcName") != plc.Software.Name)
                throw new BridgeException("TargetMismatch", "expectedPlcName must equal the selected PLC's name (" + plc.Software.Name + ").");
            var hardware = PhysicalCpu(plc);
            EnsureProjectOffline(plc.Project);
            if (plc.Project.IsModified) throw new BridgeException("UnsavedProject", "Save the project through a change before downloading; the download sends the project's current state.");
            var report = PhysicalReport(plc, args, "download");
            var challenges = new PhysicalChallenges(args.Value<string>("trustedCertificateSha256"));
            report["onlineChallenges"] = challenges.Observed;
            var answers = new JArray(); report["downloadConfigurations"] = answers;
            bool allowStop = args.Value<bool?>("acknowledgeStop") == true, allowReinit = args.Value<bool?>("acknowledgeReinitialize") == true;
            bool start = args.Value<bool?>("startAfterDownload") == true;
            var online = hardware.GetService<OnlineProvider>();
            if (online != null && online.State != OnlineState.Offline) throw new BridgeException("OfflineRequired", "The selected PLC must be offline before the download route is configured.");
            var provider = hardware.GetService<DownloadProvider>() ?? throw new BridgeException("UnsupportedCapability", "The selected CPU does not expose DownloadProvider.");
            var configuration = provider.Configuration;
            BridgeException failure = null, callbackFailure = null;
            try
            {
                var target = PhysicalRoute(configuration, args, hardware, report);
                configuration.OnlineLegitimation += challenges.Answer;
                DownloadConfigurationDelegate Answer(bool pre) => request =>
                {
                    string type = request?.GetType().Name ?? "unknown";
                    var entry = new JObject { ["phase"] = pre ? "pre" : "post", ["type"] = type, ["message"] = request?.Message };
                    if (answers.Count < 64) answers.Add(entry);
                    string selection = PhysicalDownloadRules.Selection(type, allowStop, allowReinit, start, out string refusal);
                    if (selection == null)
                    {
                        callbackFailure = callbackFailure ?? new BridgeException(refusal, "TIA asked " + type + " (" + request?.Message + "); " + PhysicalDownloadRules.Remedy(refusal));
                        entry["selection"] = "refused";
                        throw callbackFailure;
                    }
                    switch (request)
                    {
                        case ConsistentBlocksDownload blocks: blocks.CurrentSelection = ConsistentBlocksDownloadSelections.ConsistentDownload; break;
                        case AlarmTextLibrariesDownload alarms: alarms.CurrentSelection = AlarmTextLibrariesDownloadSelections.ConsistentDownload; break;
                        case UserManagementDownload users: users.CurrentSelection = UserManagementPreDownloadSelections.KeepOnlineUserManagementData; break;
                        case DataBlockReinitializationOrKeepActualValues values: values.CurrentSelection = selection == "KeepActualValues"
                            ? DataBlockReinitializationOrKeepActualValuesSelections.KeepActualValues : DataBlockReinitializationOrKeepActualValuesSelections.StopPlcAndReinitialize; break;
                        case DataBlockReinitialization reinitialize: reinitialize.CurrentSelection = DataBlockReinitializationSelections.StopPlcAndReinitialize; break;
                        case StopModules stop: stop.CurrentSelection = StopModulesSelections.StopAll; break;
                        case StartModules startModules: startModules.CurrentSelection = selection == "StartModule" ? StartModulesSelections.StartModule : StartModulesSelections.NoAction; break;
                    }
                    entry["selection"] = selection;
                };
                report["stage"] = "download";
                report["downloadAttempted"] = true;
                var result = provider.Download(target, Answer(true), Answer(false), args.Value<bool?>("hardware") == true ? DownloadOptions.Hardware | DownloadOptions.Software
                    : args.Value<string>("softwareScope") == "all" ? DownloadOptions.Software : DownloadOptions.SoftwareOnlyChanges);
                var messages = new JArray(); var budget = new ScanBudget(2000);
                SimulationDownloadMessages(result.Messages, messages, budget, 0);
                report["download"] = new JObject { ["state"] = result.State.ToString(), ["errors"] = result.ErrorCount, ["warnings"] = result.WarningCount, ["messages"] = messages, ["truncated"] = budget.Truncated };
                if (challenges.Failure != null) throw challenges.Failure;
                if (callbackFailure != null) throw callbackFailure;
                if (!CompilationEvidenceRules.IsSuccessfulTarget((JObject)report["download"], "errors", "truncated"))
                    throw new BridgeException("PhysicalDownloadFailed", "TIA reported download errors or incomplete diagnostics. The PLC may hold a partial download; check it in the TIA Portal before running the machine.");
            }
            catch (Exception ex) { failure = PhysicalFailure(report, challenges, callbackFailure ?? ex, "PhysicalDownloadFailed"); }
            finally { configuration.OnlineLegitimation -= challenges.Answer; }
            if (failure != null) throw new BridgeException(failure.Code, failure.Message, report);
            report["completed"] = true;
            report["note"] = "Downloaded. Operating state after the download is not observed by this route; check the PLC before running the machine.";
            return report;
        }

        private JObject PhysicalReport(PlcSelection plc, JObject args, string purpose) => new JObject {
            ["purpose"] = purpose, ["target"] = "physical", ["project"] = ProjectInfo(plc.Project), ["plcPath"] = plc.Path, ["plcName"] = plc.Software.Name,
            ["expectedAddress"] = args.Value<string>("expectedAddress"), ["cleanup"] = new JArray(), ["qualified"] = false };

        private static DeviceItem PhysicalCpu(PlcSelection plc)
        {
            var container = plc.Software.Parent as SoftwareContainer ?? throw new BridgeException("UnsupportedCapability", "The PLC software has no hardware container.");
            return container.Parent as DeviceItem ?? throw new BridgeException("UnsupportedCapability", "The PLC software is not hosted by a device item.");
        }

        private static bool IsPlcsimInterface(string name) => (name ?? "").IndexOf("PLCSIM", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string PhysicalAddress(JObject args)
        {
            string address = args.Value<string>("expectedAddress");
            if (!System.Net.IPAddress.TryParse(address ?? "", out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || address != ip.ToString())
                throw new BridgeException("InvalidRequest", "expectedAddress must be the PLC's exact IPv4 address, for example 192.168.0.1.");
            return address;
        }

        // IPv4 addresses the project configures on the CPU's device (its PROFINET/Ethernet interface nodes).
        private static List<string> PhysicalConfiguredAddresses(DeviceItem cpu)
        {
            var result = new List<string>();
            var device = cpu.Parent as Device;
            IEnumerable<DeviceItem> Walk(IEnumerable<DeviceItem> items) { foreach (var item in items) { yield return item; foreach (var child in Walk(item.DeviceItems)) yield return child; } }
            foreach (var item in device != null ? Walk(device.DeviceItems) : Walk(new[] { cpu }))
            {
                var network = item.GetService<NetworkInterface>();
                if (network == null) continue;
                foreach (var node in network.Nodes)
                {
                    try { if (node.GetAttribute("Address") is string value && !string.IsNullOrWhiteSpace(value) && !result.Contains(value)) result.Add(value); }
                    catch (EngineeringException) { }
                }
            }
            return result;
        }

        private static ConfigurationPcInterface PhysicalPcInterface(ConnectionConfiguration configuration, JObject args, JObject report)
        {
            string modeName = args.Value<string>("mode") ?? "PN/IE";
            string pcName = args.Value<string>("pcInterface");
            int pcNumber = args.Value<int?>("pcInterfaceNumber") ?? 1;
            if (string.IsNullOrWhiteSpace(pcName)) throw new BridgeException("InvalidRequest", "pcInterface (exact name from plc_physical_targets) is required.");
            if (IsPlcsimInterface(pcName)) throw new BridgeException("PhysicalRouteIsSimulator", "That PG/PC interface is PLCSIM; use the simulation tools for simulated PLCs.");
            var mode = configuration.Modes.Find(modeName) ?? throw new BridgeException("PhysicalRouteUnavailable", "This connection has no mode " + modeName + ".");
            var pc = mode.PcInterfaces.Find(pcName, pcNumber);
            if (pc == null)
            {
                report["connectionInventory"] = SimulationProvisionConnectionInventory(configuration);
                throw new BridgeException("PhysicalRouteUnavailable", "No PG/PC interface " + pcName + " (" + pcNumber + ") in mode " + modeName + "; see connectionInventory.");
            }
            report["routeSelection"] = new JObject { ["mode"] = modeName, ["pcInterface"] = pc.Name, ["pcInterfaceNumber"] = pc.Number };
            return pc;
        }

        // Exact route: the named PG/PC interface, one target interface (named when there are several) and an expectedAddress the
        // project configures for this CPU. TIA connects to the project's configured address; the check keeps a typo or a
        // project for another machine from reaching an unexpected PLC.
        private static ConfigurationTargetInterface PhysicalRoute(ConnectionConfiguration configuration, JObject args, DeviceItem cpu, JObject report)
        {
            string address = PhysicalAddress(args);
            var configured = PhysicalConfiguredAddresses(cpu);
            report["configuredAddresses"] = new JArray(configured);
            if (!configured.Contains(address))
                throw new BridgeException("TargetMismatch", "expectedAddress " + address + " is not an address this project configures for the selected CPU (" + string.Join(", ", configured) + "). Nothing went online.");
            var pc = PhysicalPcInterface(configuration, args, report);
            var targets = pc.TargetInterfaces.Take(17).ToArray();
            string wanted = args.Value<string>("targetInterface");
            report["targetInterfaces"] = new JArray(targets.Take(16).Select(t => t.Name));
            var target = wanted != null ? targets.FirstOrDefault(t => t.Name == wanted) : targets.Length == 1 ? targets[0] : null;
            if (target == null) throw new BridgeException("PhysicalRouteAmbiguous", wanted != null ? "No target interface named " + wanted + "." : "Several target interfaces exist; name one with targetInterface. Nothing went online.");
            report["routeSelection"]["targetInterface"] = target.Name;
            report["routeSelection"]["address"] = address;
            return target;
        }

        private static BridgeException PhysicalGoOffline(OnlineProvider provider, JObject report, BridgeException failure)
        {
            var cleanup = (JArray)report["cleanup"];
            try
            {
                if (provider.State != OnlineState.Offline) provider.GoOffline();
                bool offline = provider.State == OnlineState.Offline;
                cleanup.Add(new JObject { ["action"] = "goOffline", ["completed"] = offline, ["onlineState"] = provider.State.ToString() });
                if (!offline && failure == null) return new BridgeException("PhysicalOnlineCleanupFailed", "The PLC connection did not reach Offline.");
            }
            catch (Exception ex)
            {
                cleanup.Add(new JObject { ["action"] = "goOffline", ["completed"] = false, ["exceptionType"] = ex.GetType().FullName });
                if (failure == null) return new BridgeException("PhysicalOnlineCleanupFailed", "The PLC connection could not be taken offline.");
            }
            return failure;
        }

        private static BridgeException PhysicalFailure(JObject report, PhysicalChallenges challenges, Exception ex, string code)
        {
            var failure = challenges.Failure ?? ex as BridgeException ?? new BridgeException(code, ex.Message);
            report["failure"] = new JObject { ["code"] = failure.Code, ["message"] = failure.Message, ["stage"] = report["stage"]?.DeepClone() };
            if (!(ex is BridgeException)) report["failure"]["nativeException"] = NativeFailureText.Chain(ex);
            return failure;
        }

        // Fails closed. A login or password challenge is never answered (there is no secure input route for a real PLC yet).
        // TLS: Trusted only when exactly one certificate is offered and its SHA-256 equals trustedCertificateSha256, which the
        // user supplies after checking the PLC's certificate out of band; otherwise NonTrusted, with the observed public
        // certificate in the report so it can be checked.
        private sealed class PhysicalChallenges
        {
            internal BridgeException Failure;
            internal readonly JArray Observed = new JArray();
            private readonly string _pin;
            internal PhysicalChallenges(string pin) { _pin = string.IsNullOrWhiteSpace(pin) ? null : pin.Trim().ToLowerInvariant(); }

            internal void Answer(OnlineConfiguration request)
            {
                string type = request?.GetType().Name ?? "unknown";
                var entry = new JObject { ["type"] = type, ["decision"] = "unanswered" };
                if (Observed.Count < 16) Observed.Add(entry);
                try
                {
                    if (request is TlsVerificationConfiguration tls)
                    {
                        tls.CurrentSelection = TlsVerificationConfigurationSelection.NonTrusted;
                        entry["decision"] = "NonTrusted";
                        var certificates = tls.Certificates.Take(9).Select(c => SimulationProvisionPublicCertificate(c.Certificate)).ToArray();
                        entry["certificates"] = new JArray(certificates.Take(8));
                        if (_pin != null && certificates.Length == 1 && certificates[0].Value<string>("sha256") == _pin)
                        {
                            tls.CurrentSelection = TlsVerificationConfigurationSelection.Trusted;
                            entry["decision"] = "Trusted"; entry["tlsAuthentication"] = "userPinnedCertificate";
                            return;
                        }
                        Failure = Failure ?? new BridgeException("PhysicalTlsUntrusted", "The PLC presented a TLS certificate that was not trusted. Check its SHA-256 (onlineChallenges.certificates) against the PLC, then pass it as trustedCertificateSha256.");
                        return;
                    }
                }
                catch (Exception ex) { entry["exceptionType"] = ex.GetType().FullName; }
                Failure = Failure ?? new BridgeException("PhysicalAuthenticationRequired", "The PLC requested " + type + ". Logins and passwords for real PLCs are not supported by this route; nothing was answered.");
            }
        }
    }
}
