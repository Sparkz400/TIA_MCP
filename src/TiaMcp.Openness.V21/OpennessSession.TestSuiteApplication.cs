using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Simatic.Simulation.Runtime;
using TiaMcp.Simulation;
using TiaMcp.TestSuite.V21;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Read-only: lists the project's application test group and optionally exports each listed case (.tat).
        private JObject TestSuiteApplicationInventory(JObject args)
        {
            var project = SelectProject(args);
            string export = null;
            if (args.Value<bool?>("export") == true)
            {
                export = Path.Combine(ArtifactDirectory(args), "testsuite-application-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(export);
            }
            var result = Envelope();
            try { result["inventory"] = ApplicationTestDefinitions.Inventory(project, Limit(args, 200), export); }
            catch (Exception error) when (error is FileNotFoundException || error is FileLoadException || error is TypeLoadException)
            { result["inventory"] = new JObject { ["optionalAdapterAvailable"] = false, ["failureType"] = error.GetType().FullName, ["failure"] = error.Message }; }
            result["exportDirectory"] = export;
            result["nativeExecutionAuthorized"] = false;
            return result;
        }

        // Strict import: the supplied file is copied byte-exact into the change directory; its grammar is not parsed.
        private void PrepareTestSuiteApplicationImport(EngineeringChange change, Project project, JObject preview)
        {
            var payload = change.Payload;
            string source = ChangeText(payload, "sourcePath", 4096), extension = Path.GetExtension(source).ToLowerInvariant();
            var file = new FileInfo(ProjectPathIsAbsolute(source) ? source : throw new BridgeException("InvalidRequest", "sourcePath must be absolute."));
            if (extension != ".tat" && extension != ".ttst" || !file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length < 1 || file.Length > 16 * 1024 * 1024)
                throw new BridgeException("InvalidRequest", "sourcePath must be an existing plain .tat or .ttst file of at most 16 MiB.");
            if (extension == ".tat" && payload["expectedTestSetNames"] != null) throw new BridgeException("InvalidRequest", ".tat files carry test cases only.");
            string[] cases = extension == ".tat" || payload["expectedTestCaseNames"] != null ? ChangeNames(payload, "expectedTestCaseNames") : new string[0];
            string[] sets = extension == ".ttst" ? ChangeNames(payload, "expectedTestSetNames") : new string[0];
            var existing = ApplicationTestDefinitions.Existing(project, cases, sets);
            if (existing.Length != 0) throw new BridgeException("ObjectExists", "Strict import refuses existing test cases/sets: " + string.Join(", ", existing));
            change.ObjectNames = cases.Concat(sets).ToArray();
            change.InputPath = Path.Combine(change.Directory, "requested" + extension);
            File.Copy(file.FullName, change.InputPath);
            change.InputHash = ChangeFileHash(change.InputPath);
            ((JArray)preview["effects"]).Add(new JObject { ["importTestCases"] = new JArray(cases), ["importTestSets"] = new JArray(sets),
                ["sourceSha256"] = change.InputHash, ["importOptions"] = "None", ["loadOptions"] = "None" });
            ((JArray)preview["warnings"]).Add("Strict native import (ImportOptions.None, no IgnoreInvalidObject): collisions and invalid objects refuse. Test case scope is not set and nothing executes. Imported names are verified by readback; .ttst set content has no export route.");
        }

        private void ApplyTestSuiteApplicationImport(EngineeringChange change, Project project)
        {
            var effect = (JObject)change.Preview["effects"][0];
            string[] cases = effect["importTestCases"].Values<string>().ToArray(), sets = effect["importTestSets"].Values<string>().ToArray();
            if (ApplicationTestDefinitions.Existing(project, cases, sets).Length != 0) throw new BridgeException("ObjectExists", "A reviewed test case/set name appeared after preparation.");
            var imported = ApplicationTestDefinitions.Import(project, change.InputPath);
            change.Readbacks.Add(new JObject { ["role"] = "testsuite-application-import", ["returned"] = imported });
            change.Stages.Add("testsuite_application_imported");
            if (!imported["testCases"].Values<string>().OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(cases.OrderBy(n => n, StringComparer.Ordinal)) ||
                !imported["testSets"].Values<string>().OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(sets.OrderBy(n => n, StringComparer.Ordinal)) ||
                ((JArray)imported["unexpected"]).Count != 0 || ApplicationTestDefinitions.Existing(project, cases, sets).Length != cases.Length + sets.Length)
                throw new BridgeException("MutationVerificationFailed", "Imported test cases/sets differ from the reviewed names.");
        }

        // Ticket ownership exactly as for simulation_download, minus OFF: Test Suite drives the CPU state.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject TestSuiteOwnedSimulator(string ticket)
        {
            JObject marker;
            try { marker = SimulationTicket.Decode(ticket); }
            catch (Exception ex) { throw new BridgeException("InvalidSimulationTicket", ex.Message); }
            RetainSimulationRuntimeUntilWorkerExit();
            if ((SimulationRuntimeManager.Version >> 16) != 8) throw new BridgeException("SimulatorVersionMismatch", "PLCSIM Advanced runtime V8 is required.");
            var instance = VerifySimulationInstance(marker, requireOff: false);
            try
            {
                return new JObject { ["instanceId"] = marker["instanceId"], ["instanceName"] = marker["instanceName"],
                    ["storagePath"] = marker["storagePath"], ["operatingState"] = instance.OperatingState.ToString(),
                    ["controllerAddresses"] = new JArray(instance.ControllerIP ?? new string[0]) };
            }
            finally { RetainSimulationInterface(instance); }
        }
    }
}
