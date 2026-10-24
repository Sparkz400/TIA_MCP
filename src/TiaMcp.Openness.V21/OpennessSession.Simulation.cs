using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Simatic.Simulation.Runtime;
using TiaMcp.Simulation;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // This intentionally uses ONLY the offline folder overload. It cannot select
        // a NIC, IP address or online CPU. The target is an adapter-owned, Off instance.
        private JObject SimulationDownload(JObject args)
        {
            JObject marker;
            try { marker = SimulationTicket.Decode(args.Value<string>("downloadTicket")); }
            catch (Exception ex) { throw new BridgeException("InvalidSimulationTicket", ex.Message); }
            var plc = SelectPlc(args);
            var simulationSettings = plc.Project.GetService<PlcSimulationSettingsProvider>();
            if (simulationSettings == null || !simulationSettings.IsSimulationDuringBlockCompilationEnabled)
                throw new BridgeException("SimulationCompilationDisabled", "Enable the selected project's IsSimulationDuringBlockCompilationEnabled property through an explicit reviewed engineering change before generating a simulated memory card. The download operation does not silently change project settings.");
            var budget = new ScanBudget(50000);
            var matchingHardware = DeviceItems(plc.Project, budget).Where(entry =>
            {
                var software = entry.Item.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                return software != null && entry.Path + "/plc/" + Segment(software.Name) == plc.Path;
            }).Take(2).ToArray();
            if (budget.Truncated || matchingHardware.Length != 1)
                throw new BridgeException("AmbiguousTarget", "Cannot uniquely resolve the selected PLC's hardware for offline memory-card generation.");
            var provider = matchingHardware[0].Item.GetService<DownloadProvider>();
            if (provider == null) throw new BridgeException("UnsupportedCapability", "The selected CPU does not expose DownloadProvider.");
            var credential = ValidateSimulationCredential(args, plc.Project, matchingHardware[0].Item);

            string storage = marker.Value<string>("storagePath");
            string card = Path.Combine(storage, "SIMATIC_MC");
            string reportPath = Path.Combine(storage, "simulation-download.json");
            string claimPath = Path.Combine(storage, ".download.claim");
            var report = Envelope();
            report["route"] = "Offline DownloadProvider Windows folder / PLCSIM Advanced virtual memory card";
            report["project"] = ProjectInfo(plc.Project);
            report["plcPath"] = plc.Path;
            report["instanceId"] = marker["instanceId"];
            report["instanceName"] = marker["instanceName"];
            report["memoryCardPath"] = card;
            report["reportPath"] = reportPath;
            report["poweredOn"] = false;
            if (credential != null) report["credentialEvidence"] = credential.Evidence;
            var configurations = new JArray();
            report["configurations"] = configurations;
            IInstance instance = null;
            bool claimed = false;
            BridgeException callbackFailure = null;
            try
            {
                RetainSimulationRuntimeUntilWorkerExit();
                uint version = SimulationRuntimeManager.Version;
                if ((version >> 16) != 8) throw new BridgeException("SimulatorVersionMismatch", "PLCSIM Advanced runtime V8 is required.");
                instance = VerifySimulationInstance(marker);
                if (Directory.Exists(card) || File.Exists(card) || File.Exists(reportPath) || File.Exists(claimPath))
                    throw new BridgeException("FreshSimulationStorageRequired", "This instance already contains a memory card or download report. Create a fresh owned instance; existing files are never replaced.");
                // Compile the selected in-memory engineering state. No save, online
                // access, physical download or password/safety acknowledgement occurs.
                var compiler = plc.Software.GetService<ICompilable>();
                if (compiler == null) throw new BridgeException("UnsupportedCapability", "The selected PLC does not expose compilation.");
                var compiled = compiler.Compile();
                var compilationMessages = new JArray();
                var compilationBudget = new ScanBudget(2000);
                AddCompileMessages(compiled.Messages, compilationMessages, compilationBudget, 0);
                report["compilation"] = new JObject { ["state"] = compiled.State.ToString(), ["errors"] = compiled.ErrorCount,
                    ["warnings"] = compiled.WarningCount, ["messages"] = compilationMessages, ["truncated"] = compilationBudget.Truncated };
                if (!CompilationEvidenceRules.IsSuccessfulTarget((JObject)report["compilation"], "errors", "truncated"))
                    throw new BridgeException("CompileFailed", "The selected PLC failed compilation or has incomplete diagnostic evidence; no memory card was generated.", report);
                VerifySimulationIdentity(instance, marker);
                SimulationTicket.VerifyPlainDirectory(storage);
                // CreateNew provides a persistent, one-use claim across worker restarts.
                using (var claim = new StreamWriter(new FileStream(claimPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
                    claim.Write(marker.Value<string>("nonce"));
                claimed = true;
                Directory.CreateDirectory(card);
                var download = provider.Download(new DirectoryInfo(card), configuration =>
                {
                    try
                    {
                    VerifySimulationIdentity(instance, marker);
                    configurations.Add(new JObject { ["type"] = configuration.GetType().Name, ["message"] = configuration.Message });
                    if (configuration is TargetForSoftware target) target.CurrentSelection = TargetForSoftwareSelections.PlcSimulationAdvanced;
                    else if (configuration is OverwriteOnMemoryCard overwrite) overwrite.CurrentSelection = OverwriteOnMemoryCardSelections.Load;
                    else if (configuration is ConsistentBlocksDownload blocks) blocks.CurrentSelection = ConsistentBlocksDownloadSelections.ConsistentDownload;
                    else if (configuration is AlarmTextLibrariesDownload alarms) alarms.CurrentSelection = AlarmTextLibrariesDownloadSelections.ConsistentDownload;
                    else if (configuration is UserManagementDownload users)
                    {
                        // This overload writes a fresh owned offline memory card;
                        // there is no online user database or existing card to reset.
                        users.CurrentSelection = UserManagementPreDownloadSelections.DownloadAllUserManagementDataResetToProject;
                        ((JObject)configurations.Last)["selection"] = users.CurrentSelection.ToString();
                        ((JObject)configurations.Last)["scope"] = "Fresh owned offline memory card only; populate from selected project user data.";
                    }
                    else if (configuration is PlcMasterSecretPassword masterSecret)
                    {
                        if (credential == null) throw new BridgeException("SimulationCredentialRequired", "TIA requires validation of the generated scratch CPU configuration password. Supply its credentialMetadataPath; recovered projects also require credentialLineageJournalPaths. No protection was disabled.");
                        using (var password = OpenSimulationCredential(credential)) masterSecret.SetPassword(password);
                        ((JObject)configurations.Last)["action"] = "Validated generated scratch CPU credential through typed SecureString callback.";
                        ((JObject)configurations.Last)["secretReturned"] = false;
                    }
                    else if (configuration.GetType() != typeof(DownloadConfiguration))
                        throw new BridgeException("UnsupportedDownloadConfiguration", "Offline simulation generation requested an unsupported acknowledgement: " + configuration.GetType().Name + ". No protection or safety acknowledgement is automated.");
                    }
                    catch (Exception configError)
                    {
                        callbackFailure = configError as BridgeException ?? (configuration is PlcMasterSecretPassword
                            ? new BridgeException("SimulationCredentialValidationFailed", "TIA could not validate the generated scratch CPU configuration password. The native credential exception text is withheld from logs and MCP output.")
                            : new BridgeException("SimulationDownloadConfigurationFailed", "The typed " + configuration.GetType().Name + " callback failed: " + configError.Message));
                        report["callbackFailure"] = new JObject { ["code"] = callbackFailure.Code, ["message"] = callbackFailure.Message,
                            ["configurationType"] = configuration.GetType().Name, ["exceptionType"] = configError.GetType().FullName };
                        throw;
                    }
                });
                var messages = new JArray(); var messageBudget = new ScanBudget(2000);
                SimulationDownloadMessages(download.Messages, messages, messageBudget, 0);
                report["download"] = new JObject { ["state"] = download.State.ToString(), ["errors"] = download.ErrorCount,
                    ["warnings"] = download.WarningCount, ["messages"] = messages, ["truncated"] = messageBudget.Truncated };
                if (!CompilationEvidenceRules.IsSuccessfulTarget((JObject)report["download"], "errors", "truncated"))
                    throw new BridgeException("SimulationDownloadFailed", "TIA reported errors or incomplete diagnostic evidence while generating the simulated memory card.", report);
                VerifySimulationIdentity(instance, marker);
                var files = SimulationCardFiles(card);
                if (files.Count == 0) throw new BridgeException("EmptySimulationMemoryCard", "TIA returned without producing memory-card files.");
                report["files"] = files;
                using (var sha = SHA256.Create())
                    report["programRevision"] = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(files.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
                report["programRevisionBasis"] = "SHA-256 of sorted virtual memory-card file paths, lengths and content hashes; not an engineering-source revision or safety signature.";
                report["completed"] = true;
                report["completedAtUtc"] = DateTime.UtcNow.ToString("O");
                File.WriteAllText(reportPath, report.ToString(Formatting.Indented), new UTF8Encoding(false));
                return report;
            }
            catch (Exception ex)
            {
                report["completed"] = false;
                var bridge = callbackFailure ?? ex as BridgeException;
                report["failure"] = new JObject { ["message"] = bridge?.Message ?? ex.Message, ["exceptionType"] = ex.GetType().FullName };
                report["retrySafe"] = !claimed && !File.Exists(claimPath) && !Directory.Exists(card) && !File.Exists(card) && !File.Exists(reportPath);
                report["storageRetained"] = storage;
                if (claimed)
                {
                    try { File.WriteAllText(reportPath, report.ToString(Formatting.Indented), new UTF8Encoding(false)); }
                    catch (Exception writeError) { report["reportWriteError"] = writeError.Message; }
                }
                throw new BridgeException(bridge?.Code ?? "SimulationDownloadFailed", bridge?.Message ?? ex.Message, report);
            }
            finally
            {
                // Cleanup errors must not hide the primary compile/download result.
                try { RetainSimulationInterface(instance); } // Not disposing does not unregister the CPU either.
                catch (Exception ex) { Console.Error.WriteLine("Simulation download interface release: " + ex.Message); }
            }
        }

        // PLCSIM's CInstanceNet Dispose once raised an AccessViolationException right after a completed
        // download, killing the worker (frozen runtime run, 24 Oct 2026). Holding interfaces until worker
        // exit kept the instance's Softbus endpoint visible after unregistration, which blocked the next
        // case's discovery baseline. So interfaces are held (without finalization) only until this worker
        // next powers the instance off, released there before any unregistration, and at worker exit.
        private static readonly List<IInstance> RetainedSimulationInterfaces = new List<IInstance>();

        private static void RetainSimulationInterface(IInstance instance)
        {
            if (instance == null) return;
            lock (RetainedSimulationInterfaces) RetainedSimulationInterfaces.Add(instance);
            GC.SuppressFinalize(instance);
        }

        /// <summary>Disposes every held interface; an access violation is reported, not fatal.</summary>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions, System.Security.SecurityCritical]
        private static JArray ReleaseRetainedSimulationInterfaces()
        {
            var receipts = new JArray();
            IInstance[] held;
            lock (RetainedSimulationInterfaces) { held = RetainedSimulationInterfaces.ToArray(); RetainedSimulationInterfaces.Clear(); }
            foreach (var instance in held)
            {
                try { instance.Dispose(); receipts.Add(new JObject { ["released"] = true }); }
                catch (Exception ex) { receipts.Add(new JObject { ["released"] = false, ["exceptionType"] = ex.GetType().FullName }); }
            }
            return receipts;
        }

        private void RetainSimulationRuntimeUntilWorkerExit()
        {
            _simulationRuntimeLifetime.RegisterShutdown(ShutdownSimulationRuntime);
        }

        // Kept behind simulation-only entry points so PLC-only sessions never
        // load or initialize the optional PLCSIM API while disposing the worker.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ShutdownSimulationRuntime()
        {
            ReleaseRetainedSimulationInterfaces();
            if (SimulationRuntimeManager.IsInitialized) SimulationRuntimeManager.Shutdown();
        }

        private static IInstance VerifySimulationInstance(JObject marker)
        {
            int id = marker.Value<int>("instanceId"); string name = marker.Value<string>("instanceName");
            if (!SimulationRuntimeManager.RegisteredInstanceInfo.Any(info => info.ID == id && info.Name == name))
                throw new BridgeException("SimulationTargetMismatch", "The exact adapter-owned instance is no longer registered locally.");
            var instance = SimulationRuntimeManager.CreateInterface(id);
            try { VerifySimulationIdentity(instance, marker); return instance; }
            catch { RetainSimulationInterface(instance); throw; }
        }

        private static void VerifySimulationIdentity(IInstance instance, JObject marker)
        {
            if (instance.ID != marker.Value<int>("instanceId") || instance.Name != marker.Value<string>("instanceName") ||
                !string.Equals(Path.GetFullPath(instance.StoragePath).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(marker.Value<string>("storagePath")).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("SimulationTargetMismatch", "Simulator identity or storage changed. No substitute target is selected.");
            if (instance.OperatingState != EOperatingState.Off)
                throw new BridgeException("SimulationMustBeOff", "The verified simulated instance must remain powered off during memory-card generation.");
            if (SimulationRuntimeManager.NetworkMode != ENetworkMode.Softbus)
                throw new BridgeException("SoftbusRequired", "Simulation memory-card generation requires existing local Softbus mode.");
        }

        private static void SimulationDownloadMessages(IEnumerable<DownloadResultMessage> source, JArray output, ScanBudget budget, int depth)
        {
            if (depth > 64) { budget.Truncated = true; return; }
            foreach (var message in source)
            {
                if (!budget.Take()) return;
                output.Add(new JObject { ["message"] = message.Message, ["state"] = message.State.ToString(),
                    ["errors"] = message.ErrorCount, ["warnings"] = message.WarningCount, ["depth"] = depth });
                SimulationDownloadMessages(message.Messages, output, budget, depth + 1);
                if (budget.Truncated) return;
            }
        }

        private static JArray SimulationCardFiles(string card)
        {
            var files = new List<FileInfo>(); var pending = new Stack<DirectoryInfo>(); pending.Push(new DirectoryInfo(card));
            int count = 0; long bytes = 0;
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Memory card cannot contain a reparse point.");
                foreach (var item in directory.EnumerateFileSystemInfos())
                {
                    if (++count > 20000) throw new IOException("Memory-card manifest exceeds the 20,000-entry limit.");
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Memory card cannot contain a reparse point.");
                    if (item is DirectoryInfo child) pending.Push(child);
                    else if (item is FileInfo file)
                    {
                        bytes += file.Length;
                        if (bytes > 2L * 1024 * 1024 * 1024) throw new IOException("Memory-card manifest exceeds the 2 GiB hashing limit.");
                        files.Add(file);
                    }
                }
            }
            return new JArray(files.OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).Select(file =>
            {
                using (var stream = file.OpenRead()) using (var sha = SHA256.Create())
                    return new JObject { ["path"] = file.FullName.Substring(card.Length + 1).Replace('\\', '/'), ["bytes"] = file.Length,
                        ["sha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() };
            }));
        }
    }
}
