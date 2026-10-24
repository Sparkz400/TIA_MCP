using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW;
using Siemens.Simatic.Simulation.Runtime;
using TiaMcp.Simulation;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // This is deliberately separate from offline card generation. No caller
        // supplied address, adapter, password, reset, or user login is accepted.
        private JObject SimulationProvision(JObject args)
        {
            var discoveryBaseline = TakeSimulationDiscoveryBaseline();
            var trustArgument = args["trustOwnedEmptyCpuCertificate"];
            if (trustArgument != null && trustArgument.Type != JTokenType.Boolean)
                throw new BridgeException("InvalidSimulationTlsTrustPolicy", "The optional owned empty CPU certificate trust policy must be an explicit boolean.");
            if (args["expectedTlsCertificateSha256"] != null)
                throw new BridgeException("InvalidSimulationTlsTrustPolicy", "An external certificate pin cannot authorize first-use trust. Use only the explicit reviewed owned-empty-CPU trust policy.");
            bool trustOwnedEmptyCpuCertificate = trustArgument?.Value<bool>() == true;
            string attemptTlsPin = null;
            if (!_ownsPortal)
                throw new BridgeException("ScratchIsolationRequired", "Runtime credential provisioning requires a worker-owned TIA connection to the validated scratch project.");
            JObject marker;
            try { marker = SimulationTicket.Decode(args.Value<string>("downloadTicket")); }
            catch { throw new BridgeException("InvalidSimulationTicket", "A current owned simulator ticket is required for scratch credential provisioning."); }
            var plc = SelectPlc(args);
            var budget = new ScanBudget(50000);
            var hardware = DeviceItems(plc.Project, budget).Where(entry =>
            {
                var software = entry.Item.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                return software != null && entry.Path + "/plc/" + Segment(software.Name) == plc.Path;
            }).Take(2).ToArray();
            if (budget.Truncated || hardware.Length != 1)
                throw new BridgeException("AmbiguousTarget", "Cannot uniquely resolve the scratch PLC hardware for credential provisioning.");
            discoveryBaseline = UsableSimulationDiscoveryBaseline(discoveryBaseline, plc);
            var credential = ValidateSimulationCredential(args, plc.Project, hardware[0].Item);
            if (credential == null)
                throw new BridgeException("SimulationCredentialRequired", "Supply the exact generated scratch CPU credential metadata and any required recovery lineage.");
            var protection = hardware[0].Item.GetService<PlcMasterSecretConfigurator>();
            string protectionBefore = protection?.MasterSecretConfiguration.ToString();
            if (protectionBefore != "WithPassword" && protectionBefore != "WithPasswordAllDataProtection")
                throw new BridgeException("SimulationProtectionRequired", "The selected scratch CPU must retain its existing password-protected configuration.");

            string storage = marker.Value<string>("storagePath");
            string card = Path.Combine(storage, "SIMATIC_MC");
            string provisionClaimPath = Path.Combine(storage, ".provision.claim");
            if (File.Exists(provisionClaimPath) || Directory.Exists(provisionClaimPath))
                throw new BridgeException("SimulationProvisionAlreadyAttempted", "This owned instance already has a credential-provisioning claim. A fresh owned instance and generated card are required; automatic credential retries are not allowed.");
            var downloaded = ReadSimulationCredentialJson(Path.Combine(storage, "simulation-download.json"), 16 * 1024 * 1024);
            if (!SimulationDownloadEvidenceRules.IsSuccessfulForInstance(downloaded, marker) ||
                downloaded.Value<string>("plcPath") != plc.Path ||
                downloaded["project"]?.Value<string>("name") != plc.Project.Name ||
                !SameSimulationPath(downloaded["project"]?.Value<string>("path"), plc.Project.Path.FullName) ||
                !SameSimulationPath(downloaded.Value<string>("memoryCardPath"), card) ||
                downloaded["credentialEvidence"]?.Value<string>("credentialId") != credential.Evidence.Value<string>("credentialId") ||
                !SameSimulationPath(downloaded["credentialEvidence"]?.Value<string>("credentialMetadataPath"), credential.Evidence.Value<string>("credentialMetadataPath")))
                throw new BridgeException("SimulationProvisionTargetMismatch", "The completed generated-card report does not match the exact selected scratch PLC, credential, and owned simulator.");
            var claim = SimulationCredentialFile(Path.Combine(storage, ".download.claim"), 16384);
            if (File.ReadAllText(claim.FullName) != marker.Value<string>("nonce"))
                throw new BridgeException("SimulationProvisionTargetMismatch", "The generated card does not carry this simulator ticket's download claim.");
            var expectedFiles = downloaded["files"] as JArray;
            string revision = downloaded.Value<string>("programRevision");
            if (expectedFiles == null || expectedFiles.Count == 0 || !JToken.DeepEquals(expectedFiles, SimulationCardFiles(card)) ||
                revision != SimulationProvisionManifestHash(expectedFiles))
                throw new BridgeException("SimulationProvisionCardChanged", "The generated memory card no longer matches its original file manifest and program revision. No credential was submitted.");

            string attemptId = Guid.NewGuid().ToString("N");
            string reportPath = Path.Combine(storage, "simulation-provision-" + attemptId + ".json");
            string checkpointPath = Path.Combine(storage, "simulation-provision-" + attemptId + ".checkpoints.jsonl");
            string stagedCard = Path.Combine(storage, "simulation-protected-card-" + attemptId);
            string emptyCardBackup = Path.Combine(storage, "simulation-empty-card-" + attemptId);
            VerifySimulationProvisionChild(storage, card, true);
            VerifySimulationProvisionChild(storage, stagedCard, false);
            VerifySimulationProvisionChild(storage, emptyCardBackup, false);
            var recoveryBaseline = new JObject {
                ["schemaVersion"] = 1, ["project"] = ProjectInfo(plc.Project),
                ["projectArchivePath"] = SimulationProvisionCheckpointPath(storage, plc.Project.IsModified),
                ["projectArchive"] = null, ["nativeScope"] = _sharedMutationScope.DeepClone(),
                ["markerSha256"] = SharedMutationHash(marker),
                ["generatedCardReportSha256"] = SharedMutationHash(downloaded),
                ["generatedCardReportHashPolicy"] = "SHA-256 of ordinal-key-sorted compact Newtonsoft JSON; array order preserved",
                ["storageMetadata"] = SimulationProvisionTreeMetadata(storage),
                ["originalCardManifest"] = expectedFiles.DeepClone(),
                ["opaqueStorageContentsRead"] = false, ["fullStorageBytePreservationVerified"] = false,
                ["independentAfterStateVerified"] = false };
            var report = Envelope();
            report["recoveryBaseline"] = recoveryBaseline;
            report["origin"] = "TIA Portal V21 Openness / owned PLCSIM Advanced V8 credential provisioning";
            report["route"] = "OnlineProvider.SetPlcMasterSecret via PN/IE / PLCSIM (1), local Softbus only";
            report["project"] = ProjectInfo(plc.Project);
            report["plcPath"] = plc.Path;
            report["instanceId"] = marker["instanceId"].DeepClone();
            report["instanceName"] = marker["instanceName"].DeepClone();
            report["reportPath"] = reportPath;
            report["checkpointPath"] = checkpointPath;
            report["provisioningStrategy"] = "Provision the same owned empty virtual CPU before restoring its unchanged protected program card.";
            report["preRegistrationDiscoveryBaseline"] = discoveryBaseline?.Evidence.DeepClone();
            report["stagedProtectedCardPath"] = stagedCard;
            report["emptyCardBackupPath"] = emptyCardBackup;
            report["originalProgramCardRestored"] = false;
            report["emptyStartupVerified"] = false;
            report["protectedProgramLoadedDuringProvisioning"] = false;
            report["provisionClaimPath"] = provisionClaimPath;
            report["provisionClaimRecorded"] = false;
            report["credentialEvidence"] = credential.Evidence;
            report["programRevision"] = revision;
            report["originalProgramRevision"] = revision;
            report["generatedCardVerified"] = true;
            report["protectionConfiguration"] = protectionBefore;
            report["protectionDisabled"] = false;
            report["credentialProvisioned"] = false;
            report["protectionProvisioned"] = false;
            report["completed"] = false;
            report["runtimeReadbackVerified"] = false;
            report["runtimeApiLifetime"] = "worker";
            report["runtimeApiShutdownDeferred"] = true;
            report["runVerified"] = false;
            report["secretReturned"] = false;
            report["stage"] = "runtimePreflight";
            report["routeApplied"] = false;
            report["trustOwnedEmptyCpuCertificate"] = trustOwnedEmptyCpuCertificate;
            report["tlsPinnedCertificateSha256"] = null;
            report["tlsCertificatePinScope"] = "currentProvisioningAttemptOnly";
            report["goOnlineAttempted"] = false;
            report["goOnlineCallReturned"] = false;
            report["credentialSubmissionAttempted"] = false;
            report["credentialSubmissionArmed"] = false;
            report["credentialNativeCallReturned"] = false;
            // This flag closes diagnostic text access before even attempting to
            // open the SecureString, including when credential opening fails.
            report["credentialOpened"] = false;
            report["cardMoves"] = new JArray();
            report["cardMoveRetryPolicy"] = "Filesystem-only: Windows access/sharing/lock denial, at most 61 move calls and a 15000 ms retry-admission budget from the first failure. Guard/property/file calls may exceed that budget; no native or credential call is retried.";
            var cleanup = new JArray();
            report["cleanup"] = cleanup;
            IInstance instance = null;
            OnlineProvider provider = null;
            ConnectionConfiguration provisionConfiguration = null;
            OnlineConfigurationDelegate rejectAuthentication = null;
            BridgeException failure = null;
            BridgeException callbackFailure = null;
            bool poweredByOperation = false;
            bool explicitRouteApplied = false;
            bool stagingAttempted = false;
            bool ownedOffVerified = false;
            int tlsCallbackCount = 0;
            SimulationDiscoveryObserver observer = null;
            string observerDirectory = SimulationDiscoveryObserverDirectory(args);
            report["discoveryObserverDirectory"] = observerDirectory;
            report["discoveryObserver"] = null;
            // CreateNew prevents accidentally replacing earlier evidence, including
            // when a previous attempt failed after native runtime changes.
            using (var checkpointStream = new FileStream(checkpointPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            using (var checkpointWriter = new StreamWriter(checkpointStream, new UTF8Encoding(false)))
            using (var evidence = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
            {
                Action<string> checkpoint = phase =>
                {
                    checkpointWriter.WriteLine(new JObject { ["phase"] = phase, ["atUtc"] = DateTime.UtcNow.ToString("O"),
                        ["instanceId"] = marker["instanceId"].DeepClone(), ["instanceName"] = marker["instanceName"].DeepClone(),
                        ["storagePath"] = storage, ["originalCardPath"] = card, ["stagedProtectedCardPath"] = stagedCard,
                        ["emptyCardBackupPath"] = emptyCardBackup, ["originalProgramRevision"] = revision,
                        ["originalCardManifest"] = phase == "beforeStageProtectedCard" ? expectedFiles.DeepClone() : null,
                        ["recoveryBaseline"] = phase == "attemptPrepared" || phase == "beforeProjectArchive" || phase == "afterProjectArchive" ? recoveryBaseline.DeepClone() : null,
                        ["cardMove"] = phase == "cardMove" ? report["cardMoves"]?.Last?.DeepClone() : null,
                        ["tlsVerification"] = phase.StartsWith("tlsVerification", StringComparison.Ordinal) ? report["tlsVerification"]?.DeepClone() : null,
                        ["credentialOpened"] = report["credentialOpened"].DeepClone(),
                        ["credentialSubmissionAttempted"] = report["credentialSubmissionAttempted"].DeepClone(),
                        ["credentialSubmissionMayHaveOccurred"] = report["credentialSubmissionArmed"].DeepClone(),
                        ["credentialNativeCallReturned"] = report["credentialNativeCallReturned"].DeepClone(),
                        ["authenticationRejectionLatched"] = callbackFailure != null,
                        ["credentialSubmissionOutcome"] = report.Value<bool>("credentialNativeCallReturned") ? "nativeCallReturned" :
                            report.Value<bool>("credentialSubmissionArmed") ? "unknownWithoutMatchingReturnCheckpoint" : "notArmed" }.ToString(Formatting.None));
                    checkpointWriter.Flush();
                    checkpointStream.Flush(true);
                };
                // A checkpoint failure must abort new work, but it must never
                // prevent independent shutdown or restoration during cleanup.
                Action<string> cleanupCheckpoint = phase =>
                {
                    try { checkpoint(phase); }
                    catch (Exception ex)
                    {
                        cleanup.Add(new JObject { ["action"] = "checkpoint", ["phase"] = phase, ["completed"] = false, ["exceptionType"] = ex.GetType().FullName });
                        if (failure == null) failure = new BridgeException("SimulationProvisionCheckpointFailed", "A durable provisioning cleanup checkpoint could not be written.");
                    }
                };
                Action<JObject> moveCheckpoint = item =>
                {
                    ((JArray)report["cardMoves"]).Add(item);
                    // A new move requires durable intent, including in cleanup.
                    // Failure leaves the retained paths for explicit recovery.
                    checkpoint("cardMove");
                };
                try
                {
                    checkpoint("attemptPrepared");
                    RetainSimulationRuntimeUntilWorkerExit();
                    if ((SimulationRuntimeManager.Version >> 16) != 8)
                        throw new BridgeException("SimulatorVersionMismatch", "PLCSIM Advanced runtime V8 is required.");
                    VerifySimulationProvisionRegistration(marker);
                    instance = VerifySimulationInstance(marker); // Requires exact identity and OFF.
                    VerifySimulationProvisionIdentity(instance, marker, true);
                    if (instance.CommunicationInterface != ECommunicationInterface.Softbus)
                        throw new BridgeException("SoftbusRequired", "The owned simulator must already use local Softbus; shared network settings are not changed.");
                    provider = hardware[0].Item.GetService<OnlineProvider>();
                    if (provider == null)
                        throw new BridgeException("UnsupportedCapability", "The selected scratch CPU has no OnlineProvider service.");
                    if (provider.State != OnlineState.Offline)
                        throw new BridgeException("OfflineRequired", "The selected scratch CPU must be offline before configuring the explicit PLCSIM connection.");
                    report["stage"] = "captureRecoveryBaseline";
                    SimulationProvisionProjectCheckpoint(plc.Project, recoveryBaseline, checkpoint);
                    VerifySimulationProvisionIdentity(instance, marker, true);
                    // Establish absence before this exact owned CPU is started.
                    // PLCSIM can share Softbus with other simulator products; the
                    // Advanced registration list alone does not exclude them.
                    var discoveryObservations = new JArray();
                    report["preStartupDiscoveryObservations"] = discoveryObservations;
                    report["preStartupDiscoveryPolicy"] = new JObject {
                        ["budgetMs"] = SimulationDiscoveryWait.BudgetMs,
                        ["maximumQueries"] = SimulationDiscoveryWait.MaximumQueries,
                        ["requiredConsecutiveEmptySnapshots"] = SimulationDiscoveryWait.RequiredEmptySnapshots,
                        ["scope"] = "Read-only explicit PLCSIM discovery. Every nonempty snapshot remains significant, including unnamed or zero-address entries. Native reads may exceed the wait budget; late snapshots do not authorize startup." };
                    JObject discoverySnapshot = null;
                    var preStartupDiscoveryClock = Stopwatch.StartNew();
                    bool absentBeforeStartup = SimulationDiscoveryWait.ObserveEmpty(
                        () => {
                            VerifySimulationProvisionIdentity(instance, marker, true);
                            if (provider.State != OnlineState.Offline)
                                throw new BridgeException("OfflineRequired", "The scratch CPU must remain offline during pre-start discovery.");
                        },
                        () => {
                            var beforePc = provider.Configuration.Modes.Find("PN/IE")?.PcInterfaces.Find("PLCSIM", 1);
                            var beforeDevices = beforePc == null ? null : beforePc.GetAccessibleDevices().Take(65).ToArray();
                            var unexplained = beforeDevices == null ? null : WithoutSimulationPlaceholder(beforeDevices, AcceptedSimulationPlaceholder(discoveryBaseline), beforeDevices.Length > 64);
                            bool empty = beforeDevices == null || unexplained.Length == 0;
                            discoverySnapshot = new JObject { ["observedAtUtc"] = DateTime.UtcNow.ToString("O"),
                                ["status"] = beforePc == null ? "routeAbsent" : beforeDevices.Length == 0 ? "queriedEmpty" : empty ? "baselinePlaceholderOnly" : "preexistingDevices",
                                ["baselinePlaceholderExcluded"] = beforeDevices == null ? 0 : beforeDevices.Length - unexplained.Length,
                                ["mode"] = "PN/IE", ["interfaceName"] = beforePc?.Name, ["interfaceNumber"] = beforePc?.Number,
                                ["providerState"] = provider.State.ToString(), ["queryPerformed"] = beforePc != null, ["truncated"] = beforeDevices != null && beforeDevices.Length > 64,
                                ["basis"] = beforePc == null ? "Siemens documents PLCSIM absent from PcInterfaces when not running." : "Successful snapshot on explicit PN/IE / PLCSIM (1); only zero observed devices establishes absence.",
                                ["accessibleDevices"] = beforeDevices == null ? null : new JArray(beforeDevices.Take(64).Select(device => new JObject {
                                    ["name"] = device.Name, ["address"] = device.Address, ["macAddress"] = device.MACAddress, ["deviceSeries"] = device.DeviceSeries })) };
                            return empty;
                        },
                        (attempt, empty, consecutive, elapsed) => {
                            discoverySnapshot["attempt"] = attempt; discoverySnapshot["empty"] = empty;
                            discoverySnapshot["consecutiveEmptySnapshots"] = consecutive; discoverySnapshot["elapsedMs"] = elapsed;
                            report["preStartupDiscovery"] = discoverySnapshot;
                            discoveryObservations.Add(discoverySnapshot.DeepClone());
                            checkpoint("preStartupDiscoveryObserved");
                        }, () => preStartupDiscoveryClock.ElapsedMilliseconds, delay => Thread.Sleep(delay));
                    report["preStartupIsolationVerified"] = absentBeforeStartup;
                    if (!absentBeforeStartup)
                        throw new BridgeException("SimulationProvisionIsolationRequired", "Repeated empty PLCSIM discovery snapshots were not obtained within the bounded wait before owned startup. No endpoint was ignored, selected or changed.");
                    VerifySimulationProvisionIdentity(instance, marker, true);
                    ERuntimeErrorCode? license = null;
                    try { license = instance.LicenseStatus; } catch { }
                    if (license == ERuntimeErrorCode.WarningTrialModeActive || license == ERuntimeErrorCode.WarningRunningOnTiaPortalTestSuite)
                        throw new BridgeException("FullLicenseRequired", "The selected simulator reports a trial or test-suite license state; provisioning did not power it on.");
                    // Rehash immediately before the first state mutation.
                    if (!JToken.DeepEquals(expectedFiles, SimulationCardFiles(card)))
                        throw new BridgeException("SimulationProvisionCardChanged", "The generated memory card changed before provisioning startup.");
                    VerifySimulationProvisionIdentity(instance, marker, true);
                    // This deterministic CreateNew claim persists across failures
                    // and worker restarts. Never retry this credential on the same
                    // instance, even if a previous native call had an unknown result.
                    try
                    {
                        using (var provisionClaim = new StreamWriter(new FileStream(provisionClaimPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
                            provisionClaim.Write(marker.Value<string>("nonce"));
                        report["provisionClaimRecorded"] = true;
                    }
                    catch (IOException)
                    {
                        throw new BridgeException("SimulationProvisionClaimFailed", "The one-use provisioning claim could not be created. No runtime mutation or credential submission was attempted.");
                    }
                    report["stage"] = "stageProtectedCardWhileOff";
                    VerifySimulationProvisionIdentity(instance, marker, true);
                    VerifySimulationProvisionChild(storage, card, true);
                    VerifySimulationProvisionChild(storage, stagedCard, false);
                    stagingAttempted = true;
                    checkpoint("beforeStageProtectedCard");
                    SimulationProvisionMoveCard(storage, card, stagedCard, expectedFiles, card, true,
                        () => VerifySimulationProvisionIdentity(instance, marker, true), moveCheckpoint);
                    checkpoint("afterStageProtectedCard");
                    if (!JToken.DeepEquals(expectedFiles, SimulationCardFiles(stagedCard)) || Directory.Exists(card) || File.Exists(card))
                        throw new BridgeException("SimulationProvisionCardChanged", "The protected card could not be verified unchanged in owned staging before empty startup.");
                    VerifySimulationProvisionIdentity(instance, marker, true);
                    poweredByOperation = true; // Also clean up after a throwing PowerOn.
                    report["stage"] = "powerOn";
                    checkpoint("beforeEmptyPowerOn");
                    var powerOn = instance.PowerOn(60000);
                    checkpoint("afterEmptyPowerOn");
                    report["stage"] = "verifyRecoveryStartup";
                    report["powerOnRuntimeCode"] = powerOn.ToString();
                    report["powerOnPurpose"] = "Start the owned empty virtual CPU while the unchanged protected card is retained in owned staging; no protected application is loaded.";
                    if (powerOn != ERuntimeErrorCode.OK)
                        throw new BridgeException("SimulationProvisionPowerOnRejected", "Empty credential-provisioning startup must return OK; no startup warning is accepted.");
                    VerifySimulationProvisionIdentity(instance, marker, false);
                    report["recoveryOperatingState"] = instance.OperatingState.ToString();
                    if (instance.OperatingState != EOperatingState.Stop)
                        throw new BridgeException("SimulationProvisionStopRequired", "Credential recovery requires the owned virtual CPU to be in STOP.");
                    report["emptyStartupVerified"] = true;
                    // This Portal's PLCSIM discovery stays stale after owned startup; identity
                    // reads use a fresh observer Portal while route and connection stay here.
                    report["stage"] = "startDiscoveryObserver";
                    checkpoint("beforeDiscoveryObserverStart");
                    observer = SimulationDiscoveryObserver.Start(observerDirectory, _processId);
                    report["discoveryObserver"] = observer.Evidence.DeepClone();
                    checkpoint("afterDiscoveryObserverStart");
                    VerifySimulationProvisionIdentity(instance, marker, false);
                    if (instance.OperatingState != EOperatingState.Stop)
                        throw new BridgeException("SimulationProvisionStopRequired", "The owned virtual CPU left STOP while starting the discovery observer.");
                    // Siemens V21 "Supporting secure S7 communication TLS" says
                    // PLCSIM is absent from PcInterfaces while it is not running.
                    // Acquire the connection inventory only after this exact owned
                    // instance has powered on; never substitute an Ethernet route.
                    if (provider.State != OnlineState.Offline)
                        throw new BridgeException("OfflineRequired", "The scratch CPU's engineering connection changed before configuring the explicit PLCSIM route.");
                    provisionConfiguration = provider.Configuration;
                    var configuration = provisionConfiguration;
                    var mode = configuration.Modes.Find("PN/IE");
                    var pc = mode?.PcInterfaces.Find("PLCSIM", 1);
                    report["routeLookupOperatingState"] = instance.OperatingState.ToString();
                    if (pc == null)
                    {
                        report["connectionInventory"] = SimulationProvisionConnectionInventory(configuration);
                        throw new BridgeException("SimulationProvisionRouteUnavailable", "The explicit PN/IE / PLCSIM (1) connection is unavailable after owned simulator startup. No alternate adapter or address was selected.");
                    }
                    var originalAddresses = instance.ControllerIP ?? new string[0];
                    var ips = originalAddresses.Where(SimulationProvisionUsableIPv4).Distinct(StringComparer.Ordinal).ToArray();
                    report["originalControllerAddresses"] = new JArray(originalAddresses);
                    report["controllerName"] = instance.ControllerName;
                    if (ips.Length == 0)
                    {
                        // SetIPSuite addresses the already-verified IInstance,
                        // never an online address or a Windows network adapter.
                        // Siemens documents this operation for Softbus; it ignores
                        // the subnet/gateway and remanence flag in that mode.
                        const string documentedStartupAddress = "192.168.0.1";
                        var observedBefore = observer.Read(65);
                        var before = WithoutSimulationPlaceholder(observedBefore, AcceptedSimulationPlaceholder(discoveryBaseline), observedBefore.Length > 64);
                        report["postStartupDiscovery"] = new JObject { ["observedAtUtc"] = DateTime.UtcNow.ToString("O"),
                            ["accessibleDevices"] = new JArray(observedBefore.Take(64).Select(device => new JObject {
                                ["name"] = device.Name, ["address"] = device.Address, ["macAddress"] = device.MACAddress, ["deviceSeries"] = device.DeviceSeries })),
                            ["baselinePlaceholderExcluded"] = observedBefore.Length - before.Length,
                            ["truncated"] = observedBefore.Length > 64, ["causalTargetVerified"] = false };
                        if (before.Length != 1 || before[0].Address != documentedStartupAddress ||
                            (before[0].DeviceSeries != "S7-1500" && before[0].DeviceSeries != "S7-1500 CPU"))
                            throw new BridgeException("SimulationProvisionTargetAmbiguous", "Owned startup did not produce exactly one new S7-1500 endpoint at the documented PLCSIM startup address. No address or credential was submitted.");
                        string bootstrapAddress = before[0].Address;
                        report["postStartupDiscovery"]["causalTargetVerified"] = true;
                        report["targetIdentityBasis"] = "PLCSIM route absent or successfully queried empty (apart from only the exact pre-registration Runtime Manager placeholder, if recorded) before sole owned OFF-to-STOP startup; one new S7-1500 default endpoint observed by a fresh owned discovery-only Portal; exact IInstance address assignment and readback; fresh exact discovery match.";
                        var originalSuites = instance.ControllerIPSuite4 ?? new SIPSuite4[0];
                        report["addressBootstrap"] = new JObject {
                            ["purpose"] = "Align the exact owned virtual CPU with its newly observed default Softbus endpoint after verified absent-to-present startup; no physical or shared adapter configuration.",
                            ["addressSource"] = "Unique newly observed S7-1500 endpoint, constrained to documented PLCSIM startup default; no alternative address attempted.",
                            ["interfaceId"] = (uint)EPLCInterface.IE1,
                            ["originalSuites"] = new JArray(originalSuites.Select(suite => new JObject {
                                ["address"] = suite.IPAddress.IPString, ["subnetMask"] = suite.SubnetMask.IPString,
                                ["defaultGateway"] = suite.DefaultGateway.IPString })),
                            ["assignedAddress"] = bootstrapAddress, ["requestedSubnetMask"] = "255.255.255.0",
                            ["requestedDefaultGateway"] = "0.0.0.0", ["requestedRemanent"] = false,
                            ["softbusIgnoresSubnetGatewayAndRemanence"] = true, ["readbackVerified"] = false,
                            ["scope"] = "Owned simulator runtime only; owned instance is powered off on every exit and its storage is retained." };
                        VerifySimulationProvisionIdentity(instance, marker, false);
                        if (instance.OperatingState != EOperatingState.Stop || (instance.ControllerIP ?? new string[0]).Any(SimulationProvisionUsableIPv4))
                            throw new BridgeException("SimulationProvisionTargetMismatch", "The owned simulator state or initially empty address changed before bootstrap assignment.");
                        report["stage"] = "assignOwnedBootstrapAddress";
                        checkpoint("beforeSetOwnedIpSuite");
                        instance.SetIPSuite((uint)EPLCInterface.IE1, new SIPSuite4 {
                            IPAddress = new SIP { IPString = bootstrapAddress },
                            SubnetMask = new SIP { IPString = "255.255.255.0" },
                            DefaultGateway = new SIP { IPString = "0.0.0.0" } }, false);
                        checkpoint("afterSetOwnedIpSuite");
                        VerifySimulationProvisionIdentity(instance, marker, false);
                        ips = (instance.ControllerIP ?? new string[0]).Where(SimulationProvisionUsableIPv4).Distinct(StringComparer.Ordinal).ToArray();
                        report["addressBootstrap"]["observedAddresses"] = new JArray(ips);
                        if (instance.OperatingState != EOperatingState.Stop || ips.Length != 1 || ips[0] != bootstrapAddress)
                            throw new BridgeException("SimulationProvisionAddressUnavailable", "The exact owned simulator did not read back the requested bootstrap address in STOP. No credential was submitted.");
                        report["addressBootstrap"]["readbackVerified"] = true;
                    }
                    report["stage"] = "discoverExactRuntimeAddress";
                    report["controllerAddresses"] = new JArray(ips);
                    // GetAccessibleDevices returns a point-in-time snapshot. After
                    // an instance-local address assignment, reacquire configuration
                    // and collect bounded fresh snapshots; never relax matching or
                    // repeat the address/password mutation to force convergence.
                    var discoverySamples = new JArray();
                    report["addressDiscoverySamples"] = discoverySamples;
                    // A newly powered-on empty CPU can already report its IP
                    // before TIA discovery becomes visible. Bound read-only polling
                    // for both existing and newly assigned addresses.
                    int discoveryBudgetMs = 10000;
                    report["addressDiscoveryBudgetMs"] = discoveryBudgetMs;
                    var discoveryClock = Stopwatch.StartNew();
                    ConfigurationAccessibleDevice[] accessible;
                    while (true)
                    {
                        if (discoverySamples.Count > 0 && discoveryClock.ElapsedMilliseconds >= discoveryBudgetMs)
                            throw new BridgeException("SimulationProvisionTargetAmbiguous", "The bounded read-only PLCSIM discovery interval expired without an exact runtime address match. No credential was submitted.");
                        VerifySimulationProvisionIdentity(instance, marker, false);
                        var currentIps = (instance.ControllerIP ?? new string[0]).Where(SimulationProvisionUsableIPv4).Distinct(StringComparer.Ordinal).ToArray();
                        if (instance.OperatingState != EOperatingState.Stop || provider.State != OnlineState.Offline ||
                            currentIps.Length != ips.Length || currentIps.Any(ip => !ips.Contains(ip, StringComparer.Ordinal)))
                            throw new BridgeException("SimulationProvisionTargetMismatch", "The owned CPU identity, state, or runtime addresses changed during read-only discovery.");
                        provisionConfiguration = provider.Configuration;
                        configuration = provisionConfiguration;
                        mode = configuration.Modes.Find("PN/IE");
                        pc = mode?.PcInterfaces.Find("PLCSIM", 1);
                        if (pc == null)
                        {
                            report["connectionInventory"] = SimulationProvisionConnectionInventory(configuration);
                            throw new BridgeException("SimulationProvisionRouteUnavailable", "The exact PLCSIM (1) route disappeared during read-only address discovery.");
                        }
                        var observedAccessible = observer.Read(65);
                        accessible = WithoutSimulationPlaceholder(observedAccessible, AcceptedSimulationPlaceholder(discoveryBaseline), observedAccessible.Length > 64);
                        var visible = new JArray(observedAccessible.Take(64).Select(device => new JObject {
                            ["name"] = device.Name, ["address"] = device.Address, ["macAddress"] = device.MACAddress, ["deviceSeries"] = device.DeviceSeries }));
                        report["accessibleDevices"] = visible;
                        report["accessibleDevicesTruncated"] = observedAccessible.Length > 64;
                        discoverySamples.Add(new JObject { ["observedAtUtc"] = DateTime.UtcNow.ToString("O"),
                            ["elapsedMs"] = discoveryClock.ElapsedMilliseconds, ["runtimeAddresses"] = new JArray(currentIps),
                            ["accessibleDevices"] = visible.DeepClone(), ["baselinePlaceholderExcluded"] = observedAccessible.Length - accessible.Length });
                        if (accessible.Length == 1 && ips.Contains(accessible[0].Address, StringComparer.Ordinal)) break;
                        int discoveryDelay = SimulationProvisionDiscoveryDelayMilliseconds(accessible.Length, discoveryClock.ElapsedMilliseconds);
                        if (discoveryDelay == 0)
                            throw new BridgeException("SimulationProvisionTargetAmbiguous", "Fresh PLCSIM discovery did not establish one unique device whose address matches the owned runtime CPU within the bounded read-only check. No credential was submitted.");
                        Thread.Sleep(discoveryDelay);
                    }
                    string address = accessible[0].Address;
                    report["stage"] = "configureOnlineAuthenticationHandler";
                    report["routeSelection"] = new JObject { ["mode"] = mode.Name, ["pcInterface"] = pc.Name,
                        ["pcInterfaceNumber"] = pc.Number, ["address"] = address,
                        ["identityBasis"] = report["targetIdentityBasis"]?.DeepClone() ?? new JValue("Only registered local instance, exact owned ticket/storage, Softbus, pre-start absence, unique accessible device matching existing runtime ControllerIP") };
                    rejectAuthentication = request =>
                    {
                        string type = request?.GetType().Name ?? "unknown";
                        report["onlineChallengeType"] = type;
                        var tls = request as TlsVerificationConfiguration;
                        if (tls != null)
                        {
                            // Siemens documents NonTrusted as the explicit reject
                            // decision. Returning it avoids throwing our exception
                            // through the native callback. Only explicit reviewed
                            // first-use authorization can establish an attempt pin.
                            tlsCallbackCount++;
                            var tlsEvidence = new JObject { ["trustGranted"] = false,
                                ["certificateIdentityIndependentlyVerified"] = false,
                                ["attemptPinnedCertificateSha256"] = attemptTlsPin,
                                ["callerTrustAuthorization"] = trustOwnedEmptyCpuCertificate,
                                ["authorizationRequested"] = trustOwnedEmptyCpuCertificate,
                                ["certificateSha256Matched"] = false, ["certificateValidityVerified"] = false,
                                ["ownedTargetRevalidated"] = false, ["callbackNumber"] = tlsCallbackCount,
                                ["plannedDecision"] = "NonTrusted", ["publicMetadataOnly"] = true,
                                ["routeBinding"] = "Observed during the exact owned empty CPU connection; this is transport context, not independent certificate authentication.",
                                ["trustBasis"] = trustOwnedEmptyCpuCertificate ?
                                    "Explicit caller authorization for first use of the exact owned empty local Softbus CPU; the observed certificate hash is pinned only within this provisioning attempt." :
                                    "No first-use certificate trust authorization was supplied; the default decision is NonTrusted." };
                            report["tlsVerification"] = tlsEvidence;
                            cleanupCheckpoint("tlsVerificationObserved");
                            try { tlsEvidence["initialSelection"] = tls.CurrentSelection.ToString(); }
                            catch (Exception ex) { tlsEvidence["initialSelectionExceptionType"] = ex.GetType().FullName; }
                            tls.CurrentSelection = TlsVerificationConfigurationSelection.NonTrusted;
                            tlsEvidence["appliedDecision"] = "NonTrusted";
                            cleanupCheckpoint("tlsVerificationRejected");
                            int certificateCount = 0;
                            string observedSha256 = null;
                            DateTime certificateNotBefore = DateTime.MinValue;
                            DateTime certificateNotAfter = DateTime.MinValue;
                            bool metadataComplete = false;
                            try
                            {
                                string plcName = tls.PlcName;
                                string verificationInfo = tls.VerificationInfo;
                                tlsEvidence["plcName"] = SimulationProvisionFilterPreCredentialText(plcName ?? "", 512);
                                tlsEvidence["verificationInfo"] = SimulationProvisionFilterPreCredentialText(verificationInfo ?? "", 2048);
                                var certificates = tls.Certificates.Take(9).ToArray();
                                certificateCount = certificates.Length;
                                tlsEvidence["certificatesTruncated"] = certificates.Length > 8;
                                var certificateEvidence = new JArray();
                                tlsEvidence["certificates"] = certificateEvidence;
                                foreach (var certificate in certificates.Take(8))
                                    certificateEvidence.Add(SimulationProvisionPublicCertificate(certificate.Certificate));
                                if (certificates.Length == 1)
                                {
                                    observedSha256 = certificateEvidence[0].Value<string>("sha256");
                                    certificateNotBefore = DateTime.Parse(certificateEvidence[0].Value<string>("notBeforeUtc"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                                    certificateNotAfter = DateTime.Parse(certificateEvidence[0].Value<string>("notAfterUtc"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                                }
                                metadataComplete = true;
                            }
                            catch (Exception ex)
                            {
                                tlsEvidence["metadataExceptionType"] = ex.GetType().FullName;
                            }
                            bool ownedTargetVerified = false;
                            if (trustOwnedEmptyCpuCertificate && metadataComplete && certificateCount == 1 && callbackFailure == null && failure == null)
                            {
                                try
                                {
                                    VerifySimulationProvisionIdentity(instance, marker, false);
                                    VerifySimulationProvisionChild(storage, stagedCard, true);
                                    var callbackIps = (instance.ControllerIP ?? new string[0]).Where(SimulationProvisionUsableIPv4).Distinct(StringComparer.Ordinal).ToArray();
                                    ownedTargetVerified = explicitRouteApplied && report.Value<string>("stage") == "goOnline" &&
                                        report.Value<bool>("emptyStartupVerified") && !report.Value<bool>("originalProgramCardRestored") &&
                                        instance.OperatingState == EOperatingState.Stop && callbackIps.Length == 1 && callbackIps[0] == address;
                                }
                                catch (Exception ex) { tlsEvidence["targetRevalidationExceptionType"] = ex.GetType().FullName; }
                            }
                            DateTime policyTime = DateTime.UtcNow;
                            string decision = SimulationProvisionTlsPinDecision(trustOwnedEmptyCpuCertificate, attemptTlsPin, observedSha256, certificateCount,
                                certificateNotBefore, certificateNotAfter, policyTime, metadataComplete, ownedTargetVerified,
                                report.Value<bool>("credentialOpened") || report.Value<bool>("credentialSubmissionArmed"),
                                callbackFailure != null || failure != null || tlsCallbackCount > 8);
                            tlsEvidence["observedCertificateSha256"] = observedSha256;
                            tlsEvidence["certificateValidityVerified"] = metadataComplete && certificateCount == 1 && certificateNotBefore <= policyTime && policyTime <= certificateNotAfter;
                            tlsEvidence["certificateValidityBasis"] = "Public certificate UTC validity interval; independent CA or device identity authentication is not claimed.";
                            tlsEvidence["ownedTargetRevalidated"] = ownedTargetVerified;
                            tlsEvidence["pinPolicyDecision"] = decision;
                            cleanupCheckpoint("tlsVerificationMetadata");
                            if ((decision == "allowOwnedEmptyCpuFirstUse" || decision == "allowSameAttemptPinnedCertificate") && failure == null)
                            {
                                bool firstUse = attemptTlsPin == null;
                                if (firstUse) attemptTlsPin = observedSha256;
                                report["tlsPinnedCertificateSha256"] = attemptTlsPin;
                                tlsEvidence["attemptPinnedCertificateSha256"] = attemptTlsPin;
                                tlsEvidence["firstUsePinEstablished"] = firstUse;
                                tlsEvidence["certificateSha256Matched"] = observedSha256 == attemptTlsPin;
                                tlsEvidence["plannedDecision"] = "Trusted";
                                tlsEvidence["trustMayHaveBeenGranted"] = true;
                                cleanupCheckpoint("tlsVerificationTrustIntent");
                                if (failure == null)
                                {
                                    tls.CurrentSelection = TlsVerificationConfigurationSelection.Trusted;
                                    tlsEvidence["appliedDecision"] = "Trusted";
                                    tlsEvidence["trustGranted"] = true;
                                    cleanupCheckpoint("tlsVerificationTrusted");
                                    if (failure == null) return;
                                }
                            }
                            callbackFailure = callbackFailure ?? new BridgeException("SimulationProvisionAuthenticationRequired", "The isolated TLS challenge was rejected because the explicit owned-empty-CPU trust policy or an attempt certificate, validity, or target check was not satisfied. No user credentials were supplied.");
                            return;
                        }
                        // An unrelated authentication challenge is never satisfied
                        // and remains latched even if a later TLS challenge matches.
                        callbackFailure = callbackFailure ?? new BridgeException("SimulationProvisionAuthenticationRequired", "The isolated connection requested unsupported online authentication. No user credentials were supplied.");
                        return;
                    };
                    configuration.OnlineLegitimation += rejectAuthentication;
                    // Siemens V21's PLCSIM example applies the target-interface
                    // overload. These are connection choices exposed by this exact
                    // scratch CPU's OnlineProvider, not newly discovered devices.
                    // Never guess a slot name or select the first of several ports.
                    report["stage"] = "enumeratePlcsimTargetInterfaces";
                    var targets = pc.TargetInterfaces.Take(17).ToArray();
                    var targetInventory = new JArray();
                    report["targetInterfaceInventory"] = targetInventory;
                    report["targetInterfaceInventoryTruncated"] = targets.Length > 16;
                    foreach (var candidate in targets.Take(16))
                    {
                        var addresses = candidate.Addresses.Take(33).ToArray();
                        targetInventory.Add(new JObject { ["name"] = candidate.Name,
                            ["addresses"] = new JArray(addresses.Take(32).Select(item => new JObject {
                                ["name"] = item.Name, ["address"] = item.Address })),
                            ["addressesTruncated"] = addresses.Length > 32 });
                    }
                    if (targets.Length != 1 || string.IsNullOrWhiteSpace(targets[0].Name))
                        throw new BridgeException("SimulationProvisionTargetInterfaceAmbiguous", "The exact scratch CPU's PLCSIM (1) connection must expose one uniquely named target interface. No slot is guessed and no alternate route is applied.");
                    var target = targets[0];
                    report["routeSelection"]["targetInterface"] = target.Name;
                    report["routeSelection"]["configurationKind"] = "soleEnumeratedConfigurationTargetInterface";
                    report["routeSelection"]["targetInterfaceScope"] = "Exact validated scratch CPU OnlineProvider.Configuration / PN/IE / PLCSIM (1); live endpoint identity separately revalidated.";
                    VerifySimulationProvisionIdentity(instance, marker, false);
                    if (instance.OperatingState != EOperatingState.Stop || provider.State != OnlineState.Offline ||
                        !(instance.ControllerIP ?? new string[0]).Contains(address, StringComparer.Ordinal))
                        throw new BridgeException("SimulationProvisionTargetMismatch", "The owned runtime or scratch engineering state changed before applying its sole PLCSIM target interface.");
                    report["stage"] = "applyTargetInterfaceConfiguration";
                    checkpoint("beforeApplyTargetInterfaceConfiguration");
                    if (!configuration.ApplyConfiguration(target))
                        throw new BridgeException("SimulationProvisionRouteUnavailable", "The sole enumerated PLCSIM target interface could not be applied to the scratch CPU connection.");
                    explicitRouteApplied = true;
                    report["routeApplied"] = true;
                    checkpoint("afterApplyTargetInterfaceConfiguration");
                    report["stage"] = "revalidateBeforeCredentialSubmission";
                    VerifySimulationProvisionIdentity(instance, marker, false);
                    if (instance.OperatingState != EOperatingState.Stop || !(instance.ControllerIP ?? new string[0]).Contains(address, StringComparer.Ordinal))
                        throw new BridgeException("SimulationProvisionTargetMismatch", "The owned simulator state or controller address changed before credential submission.");
                    var read = observer.Read(3);
                    var rechecked = WithoutSimulationPlaceholder(read, AcceptedSimulationPlaceholder(discoveryBaseline), read.Length > 2);
                    if (rechecked.Length != 1 || rechecked[0].Address != address)
                        throw new BridgeException("SimulationProvisionTargetAmbiguous", "The exact PLCSIM accessible target changed before credential submission.");
                    // ApplyConfiguration chooses a session route; it does not
                    // establish the connection. Exercise the documented explicit
                    // GoOnline step before opening the secret, with the same
                    // rejecting TLS/authentication handler and exact target guards.
                    report["stage"] = "goOnline";
                    report["onlineStateBeforeConnection"] = provider.State.ToString();
                    if (provider.State != OnlineState.Offline)
                        throw new BridgeException("OfflineRequired", "The worker-owned scratch connection changed before its explicit GoOnline call.");
                    report["goOnlineAttempted"] = true;
                    checkpoint("beforeGoOnline");
                    var online = provider.GoOnline();
                    report["goOnlineCallReturned"] = true;
                    checkpoint("afterGoOnline");
                    report["goOnlineReturnedState"] = online.ToString();
                    report["onlineStateAfterConnection"] = provider.State.ToString();
                    if (callbackFailure != null) throw callbackFailure;
                    if (trustOwnedEmptyCpuCertificate && (attemptTlsPin == null || report["tlsVerification"]?.Value<bool>("trustGranted") != true))
                        throw new BridgeException("SimulationProvisionTlsPinNotVerified", "The authorized first-use TLS certificate was not bound to this provisioning attempt. No credential was opened or submitted.");
                    if (online != OnlineState.Online || provider.State != OnlineState.Online)
                        throw new BridgeException("SimulationProvisionOnlineRequired", "The explicit owned PLCSIM connection did not reach Online. No credential was opened or submitted.");
                    report["stage"] = "revalidateAfterGoOnline";
                    VerifySimulationProvisionIdentity(instance, marker, false);
                    if (instance.OperatingState != EOperatingState.Stop || !(instance.ControllerIP ?? new string[0]).Contains(address, StringComparer.Ordinal))
                        throw new BridgeException("SimulationProvisionTargetMismatch", "The owned simulator state or controller address changed while establishing the scratch connection.");
                    read = observer.Read(3);
                    rechecked = WithoutSimulationPlaceholder(read, AcceptedSimulationPlaceholder(discoveryBaseline), read.Length > 2);
                    if (rechecked.Length != 1 || rechecked[0].Address != address)
                        throw new BridgeException("SimulationProvisionTargetAmbiguous", "The exact unique PLCSIM endpoint changed while establishing the scratch connection.");
                    if (callbackFailure != null) throw callbackFailure;
                    if (failure != null) throw failure;
                    report["stage"] = "openValidatedScratchCredential";
                    report["credentialOpened"] = true;
                    using (var password = OpenSimulationCredential(credential))
                    {
                        if (password == null || password.Length == 0)
                            throw new BridgeException("SimulationCredentialUnavailable", "The generated scratch credential is empty; no password was submitted.");
                        VerifySimulationProvisionIdentity(instance, marker, false);
                        if (provider.State != OnlineState.Online || instance.OperatingState != EOperatingState.Stop ||
                            !(instance.ControllerIP ?? new string[0]).Contains(address, StringComparer.Ordinal))
                            throw new BridgeException("SimulationProvisionTargetMismatch", "The owned online connection or simulator state changed immediately before credential submission.");
                        if (callbackFailure != null) throw callbackFailure;
                        if (failure != null) throw failure;
                        report["stage"] = "setPlcMasterSecret";
                        // A crash after this durable intent has an unknown secret
                        // submission outcome until a matching return checkpoint.
                        report["credentialSubmissionArmed"] = true;
                        checkpoint("beforeSetPlcMasterSecret");
                        // Entering a native setter does not prove whether its
                        // credential was accepted if that call subsequently throws.
                        report["credentialSubmissionAttempted"] = true;
                        provider.SetPlcMasterSecret(password);
                        report["credentialNativeCallReturned"] = true;
                        checkpoint("afterSetPlcMasterSecret");
                        if (callbackFailure != null) throw callbackFailure;
                        if (failure != null) throw failure;
                    }
                    report["credentialProvisioned"] = true;
                    report["protectionProvisioned"] = true;
                    report["submissionBasis"] = "Typed SetPlcMasterSecret returned without exception using the existing validated scratch SecureString.";
                    report["stage"] = "verifyProtectionUnchanged";
                    if (protection.MasterSecretConfiguration.ToString() != protectionBefore)
                        throw new BridgeException("SimulationProtectionChanged", "The scratch project protection state unexpectedly changed during runtime credential provisioning.");
                    if (callbackFailure != null) throw callbackFailure;
                    if (failure != null) throw failure;
                }
                catch (Exception ex)
                {
                    // Native exception messages can contain credential material.
                    // Only the narrowly gated pre-credential connection failure
                    // permits filtered typed messages; raw native text stays private.
                    failure = callbackFailure ?? ex as BridgeException ?? new BridgeException("SimulationCredentialProvisionFailed", "Native scratch credential provisioning failed; unfiltered native exception text is withheld.");
                    bool allowPreCredentialText = report.Value<string>("stage") == "goOnline" &&
                        report.Value<bool>("goOnlineAttempted") && !report.Value<bool>("goOnlineCallReturned") &&
                        !report.Value<bool>("credentialOpened") && !report.Value<bool>("credentialSubmissionAttempted") &&
                        callbackFailure == null && report["onlineChallengeType"] == null;
                    report["failure"] = new JObject { ["code"] = failure.Code, ["message"] = failure.Message, ["exceptionType"] = ex.GetType().FullName,
                        ["stage"] = report["stage"].DeepClone(), ["diagnostics"] = SimulationProvisionExceptionDiagnostics(ex, allowPreCredentialText) };
                }
                finally
                {
                    if (observer != null)
                    {
                        try
                        {
                            var stopped = observer.Stop();
                            cleanup.Add(stopped);
                            report["discoveryObserver"]["stopped"] = stopped.DeepClone();
                            if (stopped.Value<bool>("completed") != true && failure == null)
                                failure = new BridgeException("SimulationProvisionCleanupFailed", "The discovery observer Portal did not close its project and exit.");
                        }
                        catch (Exception ex)
                        {
                            cleanup.Add(new JObject { ["action"] = "stopDiscoveryObserver", ["completed"] = false, ["exceptionType"] = ex.GetType().FullName });
                            if (failure == null) failure = new BridgeException("SimulationProvisionCleanupFailed", "The discovery observer Portal could not be stopped.");
                        }
                        observer = null;
                    }
                    if (provider != null && explicitRouteApplied)
                    {
                        try
                        {
                            if (provider.State != OnlineState.Offline)
                            {
                                cleanupCheckpoint("beforeGoOffline");
                                provider.GoOffline();
                                cleanupCheckpoint("afterGoOffline");
                            }
                            var finalOnlineState = provider.State;
                            if (finalOnlineState != OnlineState.Offline)
                                throw new BridgeException("SimulationProvisionCleanupFailed", "The worker-owned scratch connection did not reach Offline after credential recovery.");
                            cleanup.Add(new JObject { ["action"] = "disconnectOwnedTiaConnection", ["completed"] = true,
                                ["onlineState"] = finalOnlineState.ToString() });
                        }
                        catch (Exception ex)
                        {
                            cleanup.Add(new JObject { ["action"] = "disconnectOwnedTiaConnection", ["completed"] = false, ["exceptionType"] = ex.GetType().FullName });
                            if (failure == null) failure = new BridgeException("SimulationProvisionCleanupFailed", "The worker-owned TIA online connection could not be released after credential recovery.");
                        }
                    }
                    if (provisionConfiguration != null && rejectAuthentication != null)
                    {
                        try { provisionConfiguration.OnlineLegitimation -= rejectAuthentication; }
                        catch (Exception ex)
                        {
                            cleanup.Add(new JObject { ["action"] = "removeAuthenticationHandler", ["completed"] = false, ["exceptionType"] = ex.GetType().FullName });
                            if (failure == null) failure = new BridgeException("SimulationProvisionCleanupFailed", "The scratch authentication handler could not be removed after credential recovery.");
                        }
                    }
                    if (instance != null)
                    {
                        try
                        {
                            VerifySimulationProvisionIdentity(instance, marker, false, false);
                            if (poweredByOperation && instance.OperatingState != EOperatingState.Off)
                            {
                                cleanupCheckpoint("beforePowerOff");
                                instance.PowerOff(60000);
                                cleanupCheckpoint("afterPowerOff");
                            }
                            bool off = instance.OperatingState == EOperatingState.Off;
                            ownedOffVerified = off;
                            report["operatingState"] = instance.OperatingState.ToString();
                            report["instance"] = new JObject { ["id"] = instance.ID, ["name"] = instance.Name,
                                ["storagePath"] = instance.StoragePath, ["operatingState"] = instance.OperatingState.ToString(),
                                ["communicationInterface"] = instance.CommunicationInterface.ToString() };
                            cleanup.Add(new JObject { ["action"] = "powerOff", ["completed"] = off });
                            if (!off) throw new BridgeException("SimulationProvisionCleanupFailed", "The owned simulator did not reach OFF after credential recovery.");
                        }
                        catch (Exception ex)
                        {
                            cleanup.Add(new JObject { ["action"] = "powerOff", ["completed"] = false, ["exceptionType"] = ex.GetType().FullName });
                            if (failure == null) failure = new BridgeException("SimulationProvisionCleanupFailed", "The exact owned simulator could not be verified powered OFF after credential recovery.");
                        }
                        if (stagingAttempted)
                        {
                            try
                            {
                                if (!ownedOffVerified)
                                {
                                    report["restorationDeferredUntilOwnedOff"] = true;
                                    throw new BridgeException("SimulationProvisionCardRestoreDeferred", "The protected program remains retained in owned staging because the exact owned CPU could not be verified OFF. No card is moved while startup state is uncertain.");
                                }
                                VerifySimulationProvisionIdentity(instance, marker, true, false);
                                cleanupCheckpoint("beforeRestoreProtectedCard");
                                // Inspect actual paths, not a post-move flag: a
                                // filesystem move may have completed before an
                                // exception or process interruption was observed.
                                if (Directory.Exists(stagedCard))
                                {
                                    VerifySimulationProvisionChild(storage, stagedCard, true);
                                    if (!JToken.DeepEquals(expectedFiles, SimulationCardFiles(stagedCard)))
                                        throw new BridgeException("SimulationProvisionCardChanged", "The staged protected card no longer matches the original manifest. Both runtime and staged files are retained for inspection.");
                                    if (Directory.Exists(card))
                                    {
                                        VerifySimulationProvisionChild(storage, card, true);
                                        VerifySimulationProvisionTree(card);
                                        VerifySimulationProvisionChild(storage, emptyCardBackup, false);
                                        cleanupCheckpoint("beforeRetainEmptyCard");
                                        SimulationProvisionMoveCard(storage, card, emptyCardBackup, expectedFiles, stagedCard, false,
                                            () => VerifySimulationProvisionIdentity(instance, marker, true, false), moveCheckpoint);
                                        report["emptyCardRetained"] = true;
                                        cleanupCheckpoint("afterRetainEmptyCard");
                                    }
                                    VerifySimulationProvisionChild(storage, card, false);
                                    VerifySimulationProvisionIdentity(instance, marker, true, false);
                                    cleanupCheckpoint("beforeRestoreOriginalCardMove");
                                    SimulationProvisionMoveCard(storage, stagedCard, card, expectedFiles, stagedCard, true,
                                        () => VerifySimulationProvisionIdentity(instance, marker, true, false), moveCheckpoint);
                                    cleanupCheckpoint("afterRestoreOriginalCardMove");
                                }
                                VerifySimulationProvisionChild(storage, card, true);
                                if (!JToken.DeepEquals(expectedFiles, SimulationCardFiles(card)))
                                    throw new BridgeException("SimulationProvisionCardChanged", "The restored protected card did not match its exact original manifest. Retained storage must be inspected before any startup.");
                                report["originalProgramCardRestored"] = true;
                                cleanup.Add(new JObject { ["action"] = "restoreOriginalProtectedCard", ["completed"] = true, ["programRevision"] = revision });
                                cleanupCheckpoint("afterRestoreProtectedCard");
                            }
                            catch (Exception ex)
                            {
                                cleanup.Add(new JObject { ["action"] = "restoreOriginalProtectedCard", ["completed"] = false,
                                    ["exceptionType"] = ex.GetType().FullName, ["hresult"] = ex.HResult });
                                report["originalProgramCardRestored"] = false;
                                if (failure == null) failure = ex as BridgeException ?? new BridgeException("SimulationProvisionCardRestoreFailed", "The original protected card could not be verified restored after owned OFF. All staged and runtime-created files are retained; no files were deleted or overwritten.");
                            }
                        }
                        try
                        {
                            cleanupCheckpoint("beforeReleaseInterface");
                            // Off now: release this and the download's interface before anyone unregisters the instance.
                            RetainSimulationInterface(instance);
                            var released = ReleaseRetainedSimulationInterfaces();
                            bool all = released.All(r => r.Value<bool>("released"));
                            cleanup.Add(new JObject { ["action"] = "releaseInterface", ["completed"] = all, ["interfaces"] = released });
                            if (!all && failure == null) failure = new BridgeException("SimulationProvisionCleanupFailed", "An owned simulator interface could not be released after credential recovery.");
                            cleanupCheckpoint("afterReleaseInterface");
                        }
                        catch (Exception ex)
                        {
                            cleanup.Add(new JObject { ["action"] = "releaseInterface", ["completed"] = false, ["exceptionType"] = ex.GetType().FullName });
                            if (failure == null) failure = new BridgeException("SimulationProvisionCleanupFailed", "The owned simulator interface could not be released after credential recovery.");
                        }
                    }
                    if (callbackFailure != null && failure == null) failure = callbackFailure;
                    cleanupCheckpoint("attemptFinalized");
                    report["completed"] = failure == null && report.Value<bool>("credentialProvisioned") &&
                        report.Value<bool>("emptyStartupVerified") && report.Value<bool>("originalProgramCardRestored") && report.Value<string>("operatingState") == "Off";
                    if (failure != null && report["failure"] == null)
                        report["failure"] = new JObject { ["code"] = failure.Code, ["message"] = failure.Message };
                    report["runtimeProtectionStorage"] = new JObject { ["retainFileExists"] = File.Exists(Path.Combine(storage, "retain.pms")),
                        ["contentsRead"] = false, ["includedInOriginalProgramRevision"] = false };
                    report["completedAtUtc"] = DateTime.UtcNow.ToString("O");
                    evidence.Write(report.ToString(Formatting.Indented));
                    evidence.Flush();
                }
            }
            if (failure != null) throw new BridgeException(failure.Code, failure.Message, report);
            return report;
        }

        private static void SimulationProvisionMoveCard(string storage, string source, string destination,
            JArray originalManifest, string originalPath, bool sourceIsOriginal, Action verifyOwnedOff, Action<JObject> checkpoint)
        {
            if (SameSimulationPath(source, destination))
                throw new BridgeException("SimulationProvisionStorageCollision", "A card move must have distinct owned source and destination paths.");
            // Runtime-created card files can include opaque protection storage.
            // Metadata comparison never opens their contents or certifies bytes.
            JArray runtimeMetadata = null;
            Action<string> verifyOriginal = path =>
            {
                VerifySimulationProvisionChild(storage, path, true);
                if (!JToken.DeepEquals(originalManifest, SimulationCardFiles(path)))
                    throw new BridgeException("SimulationProvisionCardChanged", "The retained original card no longer matches its authorized manifest. No move was retried.");
            };
            Action<string> verifySourceContents = path =>
            {
                if (sourceIsOriginal) verifyOriginal(path);
                else
                {
                    verifyOriginal(originalPath);
                    var observed = SimulationProvisionTreeMetadata(path);
                    if (runtimeMetadata == null) runtimeMetadata = observed;
                    else if (!JToken.DeepEquals(runtimeMetadata, observed))
                        throw new BridgeException("SimulationProvisionCardChanged", "The runtime-created card metadata changed during restoration. Opaque contents were not read and all paths are retained.");
                }
            };
            Action validateBefore = () =>
            {
                verifyOwnedOff();
                VerifySimulationProvisionChild(storage, source, true);
                VerifySimulationProvisionChild(storage, destination, false);
                verifySourceContents(source);
                // Recheck state and collision after bounded but potentially slow hashing.
                verifyOwnedOff();
                VerifySimulationProvisionChild(storage, source, true);
                VerifySimulationProvisionChild(storage, destination, false);
            };
            Action validateAfter = () =>
            {
                verifyOwnedOff();
                VerifySimulationProvisionChild(storage, source, false);
                VerifySimulationProvisionChild(storage, destination, true);
                verifySourceContents(destination);
                verifyOwnedOff();
            };
            Func<bool> reconcile = () =>
            {
                verifyOwnedOff();
                bool sourcePresent = Directory.Exists(source), destinationPresent = Directory.Exists(destination);
                // These guards reject files/reparse points even when Exists returned false.
                VerifySimulationProvisionChild(storage, source, sourcePresent);
                VerifySimulationProvisionChild(storage, destination, destinationPresent);
                if (sourcePresent && !destinationPresent)
                {
                    verifySourceContents(source);
                    return false;
                }
                if (!sourcePresent && destinationPresent && sourceIsOriginal)
                {
                    validateAfter();
                    return true;
                }
                throw new BridgeException("SimulationProvisionMoveOutcomeUnknown", "A card move has an ambiguous outcome. All paths are retained; opaque runtime-card bytes are never read to reconcile a throwing move.");
            };
            var timer = Stopwatch.StartNew();
            SimulationCardMoveRetry.Run(validateBefore, () => Directory.Move(source, destination), validateAfter, reconcile,
                (outcome, attempt, elapsed, exception) => checkpoint(new JObject {
                    ["source"] = source, ["destination"] = destination,
                    ["sourceKind"] = sourceIsOriginal ? "originalProtectedCard" : "opaqueRuntimeCard",
                    ["outcome"] = outcome, ["attempt"] = attempt, ["retryElapsedMs"] = elapsed,
                    ["retryAdmissionBudgetMs"] = SimulationCardMoveRetry.RetryAdmissionBudgetMs, ["maxMoveCalls"] = SimulationCardMoveRetry.MaximumMoveCalls,
                    ["exceptionType"] = exception?.GetType().FullName,
                    ["hresult"] = exception == null ? (int?)null : exception.HResult,
                    ["opaqueContentsRead"] = false }), () => timer.ElapsedMilliseconds, Thread.Sleep);
        }

        private static JArray SimulationProvisionTreeMetadata(string path)
        {
            VerifySimulationProvisionTree(path);
            var entries = new System.Collections.Generic.List<JObject>();
            var pending = new System.Collections.Generic.Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(path));
            int count = 0;
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new BridgeException("SimulationProvisionStorageScopeInvalid", "An opaque card directory became a reparse point.");
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    if (++count > 20000 || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new BridgeException("SimulationProvisionStorageScopeInvalid", "An opaque card tree changed scope or exceeded its metadata bound.");
                    bool isDirectory = entry is DirectoryInfo;
                    entries.Add(new JObject { ["path"] = entry.FullName.Substring(path.Length + 1).Replace('\\', '/'),
                        ["directory"] = isDirectory, ["bytes"] = isDirectory ? (long?)null : ((FileInfo)entry).Length,
                        ["lastWriteUtcTicks"] = entry.LastWriteTimeUtc.Ticks, ["attributes"] = (int)entry.Attributes });
                    if (entry is DirectoryInfo child) pending.Push(child);
                }
            }
            return new JArray(entries.OrderBy(entry => entry.Value<string>("path"), StringComparer.OrdinalIgnoreCase));
        }

        private static JObject SimulationProvisionExceptionDiagnostics(Exception exception, bool allowPreCredentialText)
        {
            // EngineeringException has textual MessageData/DetailMessageData,
            // not a dedicated diagnostic code. Never serialize those properties raw,
            // exception Data, stack traces, or arbitrary native ToString values.
            // These fixed labels are only phrase-match hints, not diagnoses.
            try
            {
                var chain = new JArray();
                var categories = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                var filteredMessages = new JArray();
                int filteredCharacters = 0;
                Action<string, string> capturePreCredentialText = (source, text) =>
                {
                    if (!allowPreCredentialText || string.IsNullOrEmpty(text) || filteredMessages.Count >= 16 || filteredCharacters >= 8192) return;
                    string filtered = SimulationProvisionFilterPreCredentialText(text, Math.Min(2048, 8192 - filteredCharacters));
                    filteredCharacters += filtered.Length;
                    filteredMessages.Add(new JObject { ["source"] = source, ["text"] = filtered });
                };
                Action<string> classify = text =>
                {
                    if (string.IsNullOrEmpty(text)) return;
                    if (text.Length > 32768) text = text.Substring(0, 32768);
                    Func<string[], bool> contains = phrases => phrases.Any(phrase => text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (contains(new[] { "must be online", "not online", "online connection", "online-verbindung", "online verbunden", "must be offline", "muss offline", "muss online" })) categories.Add("onlineOfflineRequirementOrConnection");
                    if (contains(new[] { "offline" })) categories.Add("mentionsOfflineState");
                    if (contains(new[] { "online" })) categories.Add("mentionsOnlineState");
                    if (contains(new[] { "not supported", "does not support", "nicht unterstützt", "nicht unterstuetzt" })) categories.Add("operationNotSupported");
                    if (contains(new[] { "access denied", "insufficient rights", "not authorized", "not authorised", "permission denied", "zugriff verweigert", "keine berechtigung", "nicht berechtigt" })) categories.Add("accessOrRightsDenied");
                    if (contains(new[] { "authentication", "authenticate", "legitimation", "authentifizierung", "anmeldung" })) categories.Add("authenticationOrLogin");
                    if (contains(new[] { "already set", "already configured", "bereits gesetzt", "bereits festgelegt", "bereits eingerichtet" })) categories.Add("alreadyConfigured");
                    if (contains(new[] { "incorrect password", "wrong password", "invalid password", "falsches passwort", "passwort ist falsch", "ungültiges passwort" })) categories.Add("passwordRejected");
                    if (contains(new[] { "password protection", "password-protected", "protected program", "master secret", "mastersecret", "passwortgeschützt", "passwortschutz", "schutz vertraulicher" })) categories.Add("passwordProtectionOrMasterSecret");
                    if (contains(new[] { "timeout", "timed out", "unreachable", "cannot connect", "could not connect", "nicht erreichbar", "zeitüberschreitung", "verbindung konnte nicht" })) categories.Add("connectionOrTimeout");
                    if (contains(new[] { "certificate", "zertifikat", "tls verification" })) categories.Add("certificateOrTls");
                };
                var current = exception;
                for (int depth = 0; current != null && depth < 8; depth++)
                {
                    chain.Add(new JObject { ["type"] = current.GetType().FullName, ["hresult"] = current.HResult });
                    classify(current.Message);
                    var engineering = current as EngineeringException;
                    if (engineering != null)
                    {
                        var message = engineering.MessageData;
                        string messageText = message.Text;
                        string messageDetailText = message.DetailText;
                        classify(messageText); classify(messageDetailText);
                        capturePreCredentialText("MessageData.Text", messageText);
                        capturePreCredentialText("MessageData.DetailText", messageDetailText);
                        var details = engineering.DetailMessageData;
                        if (details != null)
                            foreach (var detail in details.Take(16))
                                {
                                    string detailText = detail.Text;
                                    string detailDetailText = detail.DetailText;
                                    classify(detailText); classify(detailDetailText);
                                    capturePreCredentialText("DetailMessageData.Text", detailText);
                                    capturePreCredentialText("DetailMessageData.DetailText", detailDetailText);
                                }
                    }
                    current = current.InnerException;
                }
                return new JObject { ["exceptionChain"] = chain, ["chainTruncated"] = current != null,
                    ["phraseMatchHints"] = new JArray(categories.OrderBy(value => value, StringComparer.Ordinal)),
                    ["nativeTextWithheld"] = !allowPreCredentialText, ["hresultIsVendorDiagnosticCode"] = false,
                    ["preCredentialFilteredMessages"] = filteredMessages,
                    ["preCredentialTextScope"] = allowPreCredentialText ? "Parameterless GoOnline threw before credential open or submission and without an online-authentication callback; typed message fields only, credential-related lines and quoted values withheld." : null };
            }
            catch
            {
                // Metadata access is best effort and must never replace the
                // original failure or prevent owned cleanup/report persistence.
                return new JObject { ["diagnosticMetadataUnavailable"] = true, ["nativeTextWithheld"] = true };
            }
        }

        private static string SimulationProvisionFilterPreCredentialText(string text, int budget)
        {
            // No unfiltered message is returned. Bound processing before splitting;
            // an incomplete quoted span stays redacted through the line ending.
            if (text.Length > 8192) text = text.Substring(0, 8192);
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            // Redact quoted spans before splitting lines, so a multiline quoted
            // argument remains withheld even when its label is on another line.
            var unquoted = new StringBuilder();
            char quote = '\0';
            bool escaped = false;
            foreach (char value in text)
            {
                if (value == '\n') { unquoted.Append('\n'); continue; }
                if (quote != '\0')
                {
                    if (escaped) { escaped = false; continue; }
                    if (value == '\\') { escaped = true; continue; }
                    if (value == quote || (quote == '”' && value == '“')) quote = '\0';
                    continue;
                }
                if (value == '"' || value == '\'' || value == '`' || value == '“' || value == '”' || value == '„' || value == '‘' || value == '’' || value == '«' || value == '»')
                {
                    quote = value == '“' || value == '”' || value == '„' ? '”' :
                        value == '‘' || value == '’' ? '’' : value == '«' || value == '»' ? '»' : value;
                    unquoted.Append("[quoted value withheld]");
                }
                else if (!char.IsControl(value) || value == '\t') unquoted.Append(value);
            }
            var rawLines = text.Split('\n');
            var safeLines = unquoted.ToString().Split('\n');
            var output = new StringBuilder();
            for (int index = 0; index < Math.Min(rawLines.Length, 64); index++)
            {
                if (output.Length >= budget) break;
                string line = rawLines[index];
                string filtered;
                // Both owned generated credential formats have this fixed prefix;
                // suppress an unquoted echo without ever opening either secret.
                if (new[] { "password", "passwort", "credential", "secret", "kennwort", "token", "Tm9!" }
                    .Any(word => line.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                    filtered = "[credential-related line withheld]";
                else
                    filtered = safeLines[index];
                if (output.Length > 0 && output.Length < budget) output.Append('\n');
                output.Append(filtered.Substring(0, Math.Min(filtered.Length, budget - output.Length)));
            }
            return output.ToString();
        }

        private static string SimulationProvisionTlsPinDecision(bool firstUseAuthorized, string attemptPin, string observedSha256, int certificateCount,
            DateTime notBeforeUtc, DateTime notAfterUtc, DateTime nowUtc, bool metadataComplete, bool ownedTargetVerified,
            bool credentialOpened, bool rejectionLatched)
        {
            if (!firstUseAuthorized) return "firstUseTrustNotAuthorized";
            if (rejectionLatched) return "priorRejectionLatched";
            if (credentialOpened) return "credentialAlreadyOpened";
            if (!ownedTargetVerified) return "ownedTargetNotVerified";
            if (!metadataComplete) return "certificateMetadataIncomplete";
            if (certificateCount != 1) return "certificateCountNotOne";
            if (!SimulationProvisionValidTlsPin(observedSha256)) return "invalidObservedCertificateHash";
            if (notBeforeUtc.Kind != DateTimeKind.Utc || notAfterUtc.Kind != DateTimeKind.Utc || nowUtc.Kind != DateTimeKind.Utc ||
                notBeforeUtc > nowUtc || nowUtc > notAfterUtc) return "certificateOutsideValidityInterval";
            if (attemptPin == null) return "allowOwnedEmptyCpuFirstUse";
            if (!SimulationProvisionValidTlsPin(attemptPin) || !string.Equals(attemptPin, observedSha256, StringComparison.Ordinal))
                return "attemptCertificateHashChanged";
            return "allowSameAttemptPinnedCertificate";
        }

        private static bool SimulationProvisionValidTlsPin(string value)
        {
            return value != null && value.Length == 64 && value.All(character =>
                (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'));
        }

        private static JObject SimulationProvisionPublicCertificate(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
        {
            if (certificate == null)
                throw new BridgeException("SimulationProvisionTlsMetadataUnavailable", "A TLS verification certificate was unavailable.");
            // RawData is the public DER certificate, never a private-key export.
            // Clone only that public value; do not dispose the Siemens-owned object.
            byte[] publicDer = certificate.RawData;
            if (publicDer == null || publicDer.Length == 0 || publicDer.Length > 65536)
                throw new BridgeException("SimulationProvisionTlsMetadataUnavailable", "A TLS public certificate exceeded the bounded metadata limit.");
            using (var publicCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(publicDer))
            using (var sha = SHA256.Create())
                return new JObject { ["sha256"] = BitConverter.ToString(sha.ComputeHash(publicDer)).Replace("-", "").ToLowerInvariant(),
                    ["subject"] = SimulationProvisionFilterPreCredentialText(publicCertificate.Subject, 1024),
                    ["issuer"] = SimulationProvisionFilterPreCredentialText(publicCertificate.Issuer, 1024),
                    ["serialNumber"] = publicCertificate.SerialNumber,
                    ["notBeforeUtc"] = publicCertificate.NotBefore.ToUniversalTime().ToString("O"),
                    ["notAfterUtc"] = publicCertificate.NotAfter.ToUniversalTime().ToString("O"),
                    ["publicDerBytes"] = publicDer.Length };
        }

        private static void VerifySimulationProvisionTree(string path)
        {
            var pending = new System.Collections.Generic.Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(path));
            int count = 0;
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new BridgeException("SimulationProvisionStorageScopeInvalid", "An owned card directory contains a reparse point and is retained in place.");
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    if (++count > 20000 || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new BridgeException("SimulationProvisionStorageScopeInvalid", "An owned card tree is too large or contains a reparse point and is retained in place.");
                    if (entry is DirectoryInfo child) pending.Push(child);
                }
            }
        }

        private static void VerifySimulationProvisionRegistration(JObject marker)
        {
            var registered = SimulationRuntimeManager.RegisteredInstanceInfo.Take(2).ToArray();
            if (registered.Length != 1 || registered[0].ID != marker.Value<int>("instanceId") || registered[0].Name != marker.Value<string>("instanceName"))
                throw new BridgeException("SimulationProvisionIsolationRequired", "Credential provisioning requires the exact owned simulator to be the only registered local instance. Other instances are never stopped or selected.");
            if (SimulationRuntimeManager.NetworkMode != ENetworkMode.Softbus)
                throw new BridgeException("SoftbusRequired", "Credential provisioning requires existing local Softbus mode.");
        }

        private static void VerifySimulationProvisionIdentity(IInstance instance, JObject marker, bool requireOff, bool requireExclusive = true)
        {
            if (requireExclusive) VerifySimulationProvisionRegistration(marker);
            if (!SimulationRuntimeManager.RegisteredInstanceInfo.Any(info => info.ID == marker.Value<int>("instanceId") && info.Name == marker.Value<string>("instanceName")) ||
                instance.ID != marker.Value<int>("instanceId") || instance.Name != marker.Value<string>("instanceName") ||
                !SameSimulationPath(instance.StoragePath, marker.Value<string>("storagePath")))
                throw new BridgeException("SimulationProvisionTargetMismatch", "The exact owned simulator identity or storage changed. No substitute target is selected.");
            SimulationTicket.VerifyPlainDirectory(instance.StoragePath);
            var markerFile = SimulationCredentialFile(Path.Combine(instance.StoragePath, SimulationTicket.MarkerName), 16384);
            if (!JToken.DeepEquals(marker, JObject.Parse(File.ReadAllText(markerFile.FullName))))
                throw new BridgeException("SimulationProvisionTargetMismatch", "The simulator ownership marker changed.");
            if (requireExclusive && (SimulationRuntimeManager.NetworkMode != ENetworkMode.Softbus || instance.CommunicationInterface != ECommunicationInterface.Softbus))
                throw new BridgeException("SoftbusRequired", "The exact owned simulator is no longer isolated through local Softbus.");
            if (requireOff && instance.OperatingState != EOperatingState.Off)
                throw new BridgeException("SimulationMustBeOff", "The owned simulator must be OFF before credential recovery starts.");
        }

        private static string SimulationProvisionManifestHash(JArray files)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(files.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
        }

        private static int SimulationProvisionDiscoveryDelayMilliseconds(int accessibleCount, long elapsedMilliseconds)
        {
            if (accessibleCount < 0 || accessibleCount > 1 || elapsedMilliseconds < 0 || elapsedMilliseconds >= 10000) return 0;
            return (int)Math.Min(250L, 10000L - elapsedMilliseconds);
        }

        private static bool SimulationProvisionUsableIPv4(string value)
        {
            IPAddress address;
            return IPAddress.TryParse(value, out address) && address.AddressFamily == AddressFamily.InterNetwork &&
                address.ToString() == value && !IPAddress.Any.Equals(address) && !IPAddress.Broadcast.Equals(address) && !IPAddress.IsLoopback(address);
        }

        private static JArray SimulationProvisionConnectionInventory(ConnectionConfiguration configuration)
        {
            var result = new JArray();
            foreach (var mode in configuration.Modes.Take(16))
                result.Add(new JObject { ["mode"] = mode.Name, ["pcInterfaces"] = new JArray(mode.PcInterfaces.Take(32).Select(pc =>
                    new JObject { ["name"] = pc.Name, ["number"] = pc.Number })) });
            return result;
        }
    }
}
