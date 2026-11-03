#if TESTSUITE_QUALIFICATION
using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using TiaMcp.TestSuite.V21;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static readonly string[] TsSystemProbes = { "testsuite.product", "testsuite.license", "testsuite.systemService", "testsuite.scope", "simulator.ownedInstance" };

        private JObject TestSuiteQualificationSystemPrepare(JObject args, string root, bool pass)
        {
            string address = args.Value<string>("serverAddress"), ticket = args.Value<string>("simulatorTicket");
            if (!SystemQualificationFixture.Endpoint(address)) throw new BridgeException("InvalidRequest", "TS-Q03 needs a canonical opc.tcp://IPv4:4840/ endpoint.");
            var simulator = TestSuiteOwnedEndpoint(ticket, address);
            string name = pass ? SystemQualificationFixture.Pass : SystemQualificationFixture.Fail;
            string directory = TsDirectory(root, "system-prepare");
            var selector = TsSelector(_scratchProject.Path.FullName, directory);
            using (_portal.ExclusiveAccess("TIA MCP: prepare owned Test Suite system qualification fixture"))
            {
                if (!_testSuiteFixtureAttempted)
                {
                    _testSuiteFixtureAttempted = true; // A partial import is never repeated in this worker.
                    TestSuiteOwnedEndpoint(ticket, address);
                    SystemQualificationFixture.Prepare(_scratchProject, SelectPlc(selector).Software, address, directory,
                        () => TestSuiteOwnedEndpoint(ticket, address));
                    var compile = ChangeCompilation(SelectPlc(selector).Software.GetService<ICompilable>().Compile(), SelectPlc(selector).Path);
                    TsSave(directory, "initial-compile.json", compile);
                    if (!CompilationEvidenceRules.IsSuccessfulTarget(compile)) throw new BridgeException("CompileFailed", "Initial system fixture compilation failed.");
                    _scratchProject.Save();
                }
                var manifest = TsCapture(_scratchProject, selector, directory, system: true);
                var target = TsTarget(_scratchProject, selector, manifest, SystemQualificationFixture.Profile);
                var definition = TsSystemDefinition(_scratchProject, selector, name, address, manifest, target, directory);
                var probes = TsProbes(root, directory, target, "project:" + _scratchProject.Path.FullName.ToUpperInvariant(), TsSystemProbes);
                return new JObject { ["target"] = target, ["definition"] = definition, ["probeEvidence"] = probes,
                    ["executionProfile"] = SystemQualificationFixture.Profile, ["processId"] = _processId,
                    ["simulator"] = simulator, ["nativeExecuted"] = false, ["resultProfileQualified"] = false };
            }
        }

        private JObject TsSystemDefinition(Project project, JObject selector, string name, string address, JObject manifest, JObject target, string directory)
        {
            var plc = SelectPlc(selector); string source = target["reference"]["snapshot"].Value<string>("sourceSha256");
            string file = Path.Combine(directory, "selected-definition.tst");
            SystemQualificationFixture.Export(project, name, file);
            if (ChangeFileHash(file) != manifest["definitions"].Value<string>("system/" + name)) throw new BridgeException("StaleContent", "System definition changed during capture.");
            var scope = SystemQualificationFixture.Scope(project, name);
            TsSave(directory, "scope-readback.json", new JObject { ["case"] = name, ["scope"] = scope,
                ["exportSha256"] = ChangeFileHash(file), ["licenseObservation"] = "SystemTestCase.SaveToFile returned in this session; native Run acceptance remains unqualified" });
            if (!SystemQualificationFixture.ScopeMatches(scope, File.ReadAllText(file), address))
                throw new BridgeException("TestSuiteScopeIncomplete", "Native scope and exported definition must name the exact owned StandardSIMATIC endpoint.");
            var required = new JArray(TsReference(project, plc.Path, plc.Path, "plcSoftware", source, source),
                TsReference(project, plc.Path, plc.Path + "/opcua/" + Segment(address), "opcUaEndpoint", source, ChangeHash(address + "|StandardSIMATIC")));
            return new JObject { ["id"] = name, ["name"] = name,
                ["reference"] = TsReference(project, plc.Path, plc.Path + "/testsuite/system/" + name, "systemTest", source, ChangeFileHash(file)),
                ["requiredScope"] = required, ["originalArtifact"] = TsArtifact(_testSuiteQualificationRoot, file) };
        }
    }
}
#endif
