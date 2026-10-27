using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using TiaMcp.TestSuite.V21;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Read-only: lists the project's system test group and optionally exports each listed case (.tst).
        private JObject TestSuiteSystemInventory(JObject args)
        {
            var project = SelectProject(args);
            string export = null;
            if (args.Value<bool?>("export") == true)
            {
                export = Path.Combine(ArtifactDirectory(args), "testsuite-system-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(export);
            }
            var result = Envelope();
            try { result["inventory"] = SystemTestDefinitions.Inventory(project, Limit(args, 200), export); }
            catch (Exception error) when (error is FileNotFoundException || error is FileLoadException || error is TypeLoadException)
            { result["inventory"] = new JObject { ["optionalAdapterAvailable"] = false, ["failureType"] = error.GetType().FullName, ["failure"] = error.Message }; }
            result["exportDirectory"] = export;
            result["nativeExecutionAuthorized"] = false;
            return result;
        }

        // A system test writes through OPC UA, so its endpoint must be a controller address of the ticket-owned PLCSIM instance.
        private JObject TestSuiteOwnedEndpoint(string ticket, string serverAddress)
        {
            var simulator = TestSuiteOwnedSimulator(ticket);
            if (!Uri.TryCreate(serverAddress, UriKind.Absolute, out var uri) || uri.Scheme != "opc.tcp" ||
                !simulator["controllerAddresses"].Values<string>().Contains(uri.Host, StringComparer.Ordinal))
                throw new BridgeException("RuntimeTargetNotOwnedPlcsim", "serverAddress is not a controller address of the ticket-owned PLCSIM instance.");
            return simulator;
        }

        // Strict import: the supplied file is copied byte-exact into the change directory; its grammar is not parsed.
        private void PrepareTestSuiteSystemImport(EngineeringChange change, Project project, JObject preview)
        {
            var payload = change.Payload;
            string source = ChangeText(payload, "sourcePath", 4096);
            var file = new FileInfo(ProjectPathIsAbsolute(source) ? source : throw new BridgeException("InvalidRequest", "sourcePath must be absolute."));
            if (Path.GetExtension(source).ToLowerInvariant() != ".tst" || !file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length < 1 || file.Length > 16 * 1024 * 1024)
                throw new BridgeException("InvalidRequest", "sourcePath must be an existing plain .tst file of at most 16 MiB.");
            string[] cases = ChangeNames(payload, "expectedTestCaseNames");
            var existing = SystemTestDefinitions.Existing(project, cases);
            if (existing.Length != 0) throw new BridgeException("ObjectExists", "Strict import refuses existing system test cases: " + string.Join(", ", existing));
            change.ObjectNames = cases;
            change.InputPath = Path.Combine(change.Directory, "requested.tst");
            File.Copy(file.FullName, change.InputPath);
            change.InputHash = ChangeFileHash(change.InputPath);
            ((JArray)preview["effects"]).Add(new JObject { ["importSystemTestCases"] = new JArray(cases),
                ["sourceSha256"] = change.InputHash, ["importOptions"] = "None", ["loadOptions"] = "None" });
            ((JArray)preview["warnings"]).Add("Strict native import (ImportOptions.None, no IgnoreInvalidObject): collisions and invalid objects refuse. The OPC UA scope is not set and nothing executes. Imported names are verified by readback.");
        }

        private void ApplyTestSuiteSystemImport(EngineeringChange change, Project project)
        {
            string[] cases = change.Preview["effects"][0]["importSystemTestCases"].Values<string>().ToArray();
            if (SystemTestDefinitions.Existing(project, cases).Length != 0) throw new BridgeException("ObjectExists", "A reviewed system test case name appeared after preparation.");
            var imported = SystemTestDefinitions.Import(project, change.InputPath);
            change.Readbacks.Add(new JObject { ["role"] = "testsuite-system-import", ["returned"] = imported });
            change.Stages.Add("testsuite_system_imported");
            if (!imported["testCases"].Values<string>().OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(cases.OrderBy(n => n, StringComparer.Ordinal)) ||
                ((JArray)imported["unexpected"]).Count != 0 || SystemTestDefinitions.Existing(project, cases).Length != cases.Length)
                throw new BridgeException("MutationVerificationFailed", "Imported system test cases differ from the reviewed names.");
        }
    }
}
