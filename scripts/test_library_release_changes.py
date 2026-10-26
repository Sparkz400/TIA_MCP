"""Offline checks for the independent export readings, oracles and diagnostics used by the release-change qualifier."""
from pathlib import Path
import unittest

from qualify_library_release_changes import (BEHAVIOR, EXPECTED, INSTANCE, behavior_steps, compile_errors, fb_shape,
                                             instance_shape, require_breaking_diagnostics)

NS = "http://www.siemens.com/automation/Openness/SW/Interface/v5"
ROOT = Path(__file__).resolve().parents[1]


def document(output, negated):
    body = '<Token Text=":=" /><Blank />' + ('<Token Text="NOT" /><Blank />' if negated else "")
    return f"""<Document><SW.Blocks.FB ID="0"><AttributeList><Interface><Sections xmlns="{NS}">
<Section Name="Input"><Member Name="Enable" Datatype="Bool" /></Section>
<Section Name="Output"><Member Name="{output}" Datatype="Bool" /></Section>
</Sections></Interface></AttributeList><ObjectList><SW.Blocks.CompileUnit ID="1"><AttributeList><NetworkSource>
<StructuredText xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4">{body}</StructuredText>
</NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FB></Document>""".encode()


class FbShapeTests(unittest.TestCase):
    def test_each_release_is_distinguished(self):
        self.assertEqual(fb_shape(document("Active", False)), EXPECTED["1.0.0"])
        self.assertEqual(fb_shape(document("Active", True)), EXPECTED["1.1.0"])
        self.assertEqual(fb_shape(document("Ready", True)), EXPECTED["2.0.0"])
        self.assertNotEqual(fb_shape(document("Ready", False)), EXPECTED["2.0.0"])

    def test_only_error_messages_count(self):
        result = {"targets": [{"messages": [{"state": "Warning", "path": "Main"}, {"state": "Error", "path": "Main"}]}]}
        self.assertEqual(compile_errors(result), [{"state": "Error", "path": "Main"}])


class InstanceShapeTests(unittest.TestCase):
    def test_reads_top_level_sections_of_a_native_instance_db(self):
        raw = (ROOT / "fixtures/libraries/component-review/native05-requested-instance.xml").read_bytes()
        self.assertEqual(instance_shape(raw), {"instanceOf": "TiaMcpComponentGate",
                                               "sections": {"Input": ["Enable"], "Output": ["Active"], "Static": ["State"]}})

    def test_breaking_release_changes_only_the_output_member(self):
        self.assertEqual(INSTANCE["1.0.0"], INSTANCE["1.1.0"])
        self.assertEqual(INSTANCE["2.0.0"]["sections"], {"Input": ["Enable"], "Output": ["Ready"]})


class BehaviorTests(unittest.TestCase):
    def test_implementation_release_inverts_and_breaking_release_keeps_behavior(self):
        self.assertTrue(all(BEHAVIOR["1.1.0"][x] != BEHAVIOR["1.0.0"][x] for x in (True, False)))
        self.assertEqual(BEHAVIOR["2.0.0"], BEHAVIOR["1.1.0"])

    def test_steps_assert_probe_instance_and_main_through_the_release_output(self):
        steps = behavior_steps("2.0.0")
        self.assertEqual([s["writes"][0]["value"] for s in steps], [True, False, True])
        second = {a["symbol"]: a["expected"] for a in steps[1]["assertions"]}
        self.assertEqual(second, {"TiaMcpReleaseProbe.Output": True, "TiaMcpReleaseProbe_Gate.Ready": True,
                                  "TiaMcpLibraryGate_DB.Ready": False, "TiaMcpLibraryState_DB.State.Active": False})
        self.assertIn("TiaMcpLibraryGate_DB.Active", {a["symbol"] for a in behavior_steps("1.0.0")[0]["assertions"]})


class BreakingDiagnosticsTests(unittest.TestCase):
    ERROR = "The formal parameter 'Active' is invalid"

    def test_every_stale_caller_must_fail_on_the_removed_output(self):
        errors = [{"state": "Error", "path": "Main", "description": self.ERROR},
                  {"state": "Error", "path": "TiaMcpReleaseProbe_Cycle", "description": self.ERROR}]
        require_breaking_diagnostics(errors)
        with self.assertRaises(AssertionError):
            require_breaking_diagnostics(errors[:1])
        with self.assertRaises(AssertionError):
            require_breaking_diagnostics([{**e, "description": "other"} for e in errors])
        with self.assertRaises(AssertionError):
            require_breaking_diagnostics(errors + [{"state": "Error", "path": "TiaMcp_EnableGate", "description": "x"}])


if __name__ == "__main__":
    unittest.main()
