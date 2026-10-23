"""Tests for independent native expectation checks; these are not PLC runtime evidence."""
import pathlib
import json
import tempfile
import unittest
from xml.etree import ElementTree as ET

from typed_graphical_native_expectations import topology, adapt_reference_bindings
from qualify_typed_graphical import selected_blocks, snapshot, read_graph, independent_behavior
from block_test_program import generate_program, inspect_interface
from block_test_executor import validate_suite

ROOT = pathlib.Path(__file__).resolve().parents[1]


class IndependentTopologyTests(unittest.TestCase):
    def xml(self, name):
        return (ROOT / "fixtures/typed-graphical/native" / (name + ".xml")).read_text(encoding="utf-8-sig")

    def test_native_uid_renumbering_does_not_change_semantics(self):
        baseline = self.xml("Tgp_Boolean_LAD")
        document = ET.fromstring(baseline)
        for node in document.iter():
            if "UId" in node.attrib:
                node.set("UId", str(int(node.get("UId")) + 1000))
        self.assertEqual(topology(baseline), topology(ET.tostring(document, encoding="unicode")))

    def test_wrong_operand_and_wrong_comparison_do_not_match(self):
        baseline = self.xml("Tgp_Eq_LAD")
        self.assertNotEqual(topology(baseline), topology(baseline.replace('Name="Eq"', 'Name="Ne"')))
        self.assertNotEqual(topology(baseline), topology(baseline.replace('Name="Left" />', 'Name="Right" />')))

    def test_swapped_counter_pin_fails(self):
        baseline = self.xml("Tgp_CTUD_FBD")
        changed = baseline.replace('Name="CU" />', 'Name="TEMP_PIN" />').replace('Name="CD" />', 'Name="CU" />').replace('Name="TEMP_PIN" />', 'Name="CD" />')
        self.assertNotEqual(topology(baseline), topology(changed))

    def test_native_pin_inversion_and_one_input_and_are_equivalent(self):
        one = ROOT / "fixtures/typed-graphical/native/Tgp_NotOne_FBD.xml"
        self.assertTrue(one.exists(), "The independently captured one-input AND fixture is required.")
        self.assertEqual(topology(self.xml("Tgp_Not_FBD")), topology(self.xml("Tgp_NotOne_FBD")))

    def test_explicit_reference_rebinding_requires_exact_coverage(self):
        original = self.xml("Tgp_TemplateFC_FBD")
        bindings = [{"element": "Access", "fromScope": "LocalVariable", "toScope": "LocalVariable",
                     "fromComponents": ["OutputBool"], "toComponents": ["InOutBool"], "expectedCount": 1}]
        self.assertNotEqual(topology(original), topology(adapt_reference_bindings(original, bindings)))
        bindings[0]["expectedCount"] = 2
        with self.assertRaisesRegex(ValueError, "count"):
            adapt_reference_bindings(original, bindings)

    def test_public_inventory_limits_full_peer_coverage_and_graph_envelope(self):
        inventory = {"completeness": {"scopeComplete": True, "truncated": False, "complete": False},
            "softwareScopes": [{"kind": "softwareUnit", "name": "UnitA", "path": "/plc/unit/A"}],
            "blocks": [{"name": "Target", "path": "/plc/unit/A/Target", "scopePath": "/plc/unit/A", "identity": {"namespace": "UnitA"}},
                       {"name": "Target", "path": "/plc/Target", "scopePath": "/plc", "identity": {"namespace": ""}},
                       {"name": "Peer", "path": "/plc/unit/B/Peer", "scopePath": "/plc/unit/B", "identity": {"namespace": "UnitB"}}]}
        records = []
        def call(tool, args):
            records.append((tool, args))
            if tool == "software_inspect":
                self.assertLessEqual(args["maxItems"], 2000)
                return inventory
            self.assertEqual("engineering_read", tool)
            self.assertEqual({"selectors"}, set(args))
            self.assertEqual("graphical", args["selectors"]["scope"])
            self.assertTrue(pathlib.Path(args["selectors"]["artifactDirectory"]).is_absolute())
            return {"readOnly": True}
        blocks, scope = selected_blocks(call, {"unitName": "UnitA", "namespace": "UnitA"})
        self.assertEqual("/plc/unit/A", scope)
        self.assertEqual({"Target", "@unselected:/plc/Target", "@unselected:/plc/unit/B/Peer"}, set(blocks))
        self.assertTrue(read_graph(call, blocks["Target"]["path"], ROOT / "artifacts")["readOnly"])
        inventory["completeness"]["truncated"] = True
        with self.assertRaises(AssertionError):
            selected_blocks(call, {})

    def test_snapshot_retains_native_comment_line_endings_exactly(self):
        xml = '<Document>\r\n<Text>line one\r\nline two</Text>\r\n</Document>'
        def call(tool, args):
            self.assertEqual("block_read", tool)
            return {"xml": xml, "block": {"name": "Target", "path": "/Target"}}
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as temporary:
            path = pathlib.Path(temporary) / "snapshot"
            snapshot(call, ["Target"], path)
            self.assertEqual(xml.encode("utf-8"), (path / "000.xml").read_bytes())

    def test_numeric_cases_preflight_against_independent_native_interfaces(self):
        def interface(path):
            document = ET.parse(path)
            block = next(node for node in document.getroot() if node.tag in ("SW.Blocks.FC", "SW.Blocks.FB"))
            attributes = block.find("AttributeList")
            sections = list(attributes.find("Interface"))[0]
            def member(node):
                return {"name": node.get("Name"), "dataType": node.get("Datatype"), "members": []}
            return {"block": {"name": attributes.findtext("Name"), "kind": block.tag.rsplit(".", 1)[-1]}, "objectKinds": [block.tag],
                    "sections": [{"name": section.get("Name"), "members": [member(node) for node in section]} for section in sections]}
        cases = json.loads((ROOT / "fixtures/typed-graphical/numeric-cases.v1.json").read_bytes())["cases"]
        self.assertEqual(4, len(cases))
        for case in cases:
            definitions = {dep["names"][0]: interface(ROOT / dep["path"]) for dep in case["dependencyXmls"]}
            inspected = inspect_interface(interface(ROOT / case["referenceXml"]), definitions)
            program = generate_program(inspected, case["runtimeInstances"], prefix="ScalarHarness")
            suite = {"fixtureId": case["fixtureId"], "instances": case["runtimeInstances"], "cases": independent_behavior(case["family"])}
            validate_suite(suite, program)
            self.assertTrue(suite["cases"][0]["steps"][-1]["assertions"])

    def test_all_authored_case_constants_use_typed_request_grammar(self):
        count = 0
        for path in (ROOT / "fixtures/typed-graphical").glob("*cases.v1.json"):
            for case in json.loads(path.read_bytes())["cases"]:
                for operand in case["payload"]["model"].get("operands", []):
                    if operand["scope"] == "constant" and operand["dataType"] == "Bool":
                        self.assertIn(operand["value"], ("true", "false"), (path.name, case["fixtureId"]))
                        count += 1
        self.assertGreater(count, 0)


if __name__ == "__main__":
    unittest.main()
