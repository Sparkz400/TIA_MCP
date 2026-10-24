import json
import tempfile
import unittest
from pathlib import Path

from component_runtime import installed_inputs, ob_call_source, scenario, verify_ob_call

FIXTURE = json.loads((Path(__file__).resolve().parents[1] / "fixtures/libraries/component-qualification/manifest.json").read_text())


def ob(number="1", calls=(("TiaMcpComponentCaller", "FC"),)):
    infos = "".join(f'<CallInfo Name="{n}" BlockType="{t}" />' for n, t in calls)
    return (f'<Document><SW.Blocks.OB ID="0"><AttributeList><Name>Main</Name><Number>{number}</Number></AttributeList>'
            f'<ObjectList><SW.Blocks.CompileUnit ID="1"><AttributeList><NetworkSource>{infos}</NetworkSource></AttributeList>'
            '</SW.Blocks.CompileUnit></ObjectList></SW.Blocks.OB></Document>')


class ComponentRuntimeTests(unittest.TestCase):
    def report(self, directory, **changes):
        report = {"status": "passed", "preservationVerified": True, "freshReopenVerified": True, "firstCheckpointRecoveryVerified": True,
                  "runtimeVerified": False, "productionApplySupported": False, "fixture": FIXTURE,
                  "consumerScratch": {"project": {"path": "P"}, "scratchSecurity": {"cpus": [{"credentialMetadataPath": "C"}]}}}
        report.update(changes)
        (directory / "reopened-install").mkdir(exist_ok=True)
        (directory / "reopened-install" / "manifest.json").write_text("{}")
        path = directory / "result.json"; path.write_text(json.dumps(report))
        return path

    def test_requires_complete_passed_installation(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            self.assertEqual(installed_inputs(self.report(folder))["credential"], "C")
            for change in ({"status": "failed"}, {"firstCheckpointRecoveryVerified": False}, {"runtimeVerified": True},
                           {"consumerScratch": {"project": {"path": "P"}, "scratchSecurity": {"cpus": []}}}):
                with self.assertRaises(RuntimeError):
                    installed_inputs(self.report(folder, **change))

    def test_ob_call_oracle(self):
        self.assertIn('"TiaMcpComponentCaller"();', ob_call_source(FIXTURE))
        verify_ob_call(ob(), FIXTURE)
        for bad in (ob(number="123"), ob(calls=()), ob(calls=(("Other", "FC"),)), ob(calls=(("TiaMcpComponentCaller", "FB"),)),
                    ob(calls=(("TiaMcpComponentCaller", "FC"),) * 2)):
            with self.assertRaises(RuntimeError):
                verify_ob_call(bad, FIXTURE)

    def test_native_scl_ob_call_oracle(self):
        ns = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4"
        scl = lambda callee, kind: (f'<Document><SW.Blocks.OB ID="0"><AttributeList><Number>1</Number></AttributeList><ObjectList>'
            f'<SW.Blocks.CompileUnit ID="3"><AttributeList><NetworkSource><StructuredText xmlns="{ns}"><Access Scope="Call" UId="23">'
            f'<CallInfo UId="24" BlockType="{kind}"><Instance Scope="GlobalVariable" UId="26"><Component Name="{callee}" UId="25" />'
            '</Instance></CallInfo></Access></StructuredText></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.OB></Document>')
        verify_ob_call(scl("TiaMcpComponentCaller", "FC"), FIXTURE)
        for bad in (scl("Other", "FC"), scl("TiaMcpComponentCaller", "FB")):
            with self.assertRaises(RuntimeError):
                verify_ob_call(bad, FIXTURE)

    def test_scenario_observes_both_edges_on_mapped_tags(self):
        steps, signals = scenario(FIXTURE)
        self.assertEqual([s["symbol"] for s in signals], ["ComponentEnable", "ComponentActive"])
        self.assertEqual([s["writes"][0]["value"] for s in steps], [True, False, True, False])
        self.assertTrue(all(s["assertions"][0]["expected"] == s["writes"][0]["value"] for s in steps))


if __name__ == "__main__":
    unittest.main()
