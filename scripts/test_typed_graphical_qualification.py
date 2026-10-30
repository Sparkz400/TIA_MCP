"""Tests for independent native expectation checks; these are not PLC runtime evidence."""
import hashlib
import pathlib
import json
import re
import struct
import tempfile
import unittest
from xml.etree import ElementTree as ET

from typed_graphical_native_expectations import topology, adapt_reference_bindings, adapt_reference_culture, adapt_reference_import, project_culture
from qualify_typed_graphical import block_path, selected_blocks, snapshot, read_graph, independent_behavior
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

    def test_reference_culture_moves_only_empty_text(self):
        baseline = self.xml("Tgp_Boolean_LAD")
        adapted, count = adapt_reference_culture(baseline, "en-US")
        self.assertEqual((4, "en-US"), (count, project_culture(adapted)))
        self.assertEqual(topology(baseline), topology(adapted))
        with self.assertRaises(ValueError):
            adapt_reference_culture(baseline.replace("<Text />", "<Text>Kommentar</Text>", 1), "en-US")

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

    def test_crossscope_cases_bind_their_captured_callers_and_the_callee_outside_the_route(self):
        cases = json.loads((ROOT / "fixtures/typed-graphical/crossscope-cases.v1.json").read_bytes())["cases"]
        self.assertEqual(["typed-crossnamespace-numeric-fc-lad", "typed-crossnamespace-numeric-fc-fbd",
                          "typed-crossunit-numeric-fc-lad", "typed-crossunit-numeric-fc-fbd"], [c["fixtureId"] for c in cases])
        net = "{http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5}"
        callee = "/plc/units/TiaMcp_UnitA/blocks/namespaces/TiaMcp_UnitA/TgpUnit_NumericFC"
        callees = (ROOT / "fixtures/typed-graphical/sources/route-Unit-advanced-callees.scl").read_text(encoding="utf-8")
        for case in cases:
            with self.subTest(case=case["fixtureId"]):
                document = ET.fromstring((ROOT / case["referenceXml"]).read_text(encoding="utf-8-sig"))
                self.assertEqual(case["namespace"], next(document.iter("Namespace")).text)
                self.assertEqual(["TiaMcp_UnitA.TgpUnit_NumericFC"], [info.get("Name") for info in document.iter(net + "CallInfo")])
                self.assertTrue(case["engineeringOnly"] and (ROOT / case["referenceXml"]).name == case["blockName"] + ".xml")
                cross_unit = case["unitName"] != "TiaMcp_UnitA"
                # The cross-unit archive already holds the callees; the cross-namespace case admits them as its capture did.
                self.assertEqual([] if cross_unit else [{"language": "SCL", "source": callees, "namespace": "TiaMcp_UnitA",
                                                         "expectedBlockNames": ["TgpUnit_NumericFC", "TgpUnit_NumericFB"]}],
                                 case.get("dependencySources", []))
                [call] = [node for node in case["payload"]["model"]["nodes"] if node["kind"] == "call"]
                before = {case["blockName"]: {"block": {"path": "/plc/units/" + case["unitName"] + "/blocks/" + case["blockName"]}},
                          "@unselected:" + callee: {"block": {"path": callee}}}
                self.assertEqual(callee, block_path(before, call["calleePath"]))
                self.assertEqual(before[case["blockName"]]["block"]["path"], block_path(before, "$block:" + case["blockName"]))
                for blocks in ({case["blockName"]: before[case["blockName"]]}, {**before, "@unselected:/other" + callee: {"block": {"path": "/other" + callee}}}):
                    with self.assertRaises(AssertionError):
                        block_path(blocks, call["calleePath"])

    def test_wave9_cases_preflight_against_their_authored_sources(self):
        packet = json.loads((ROOT / "fixtures/typed-graphical/wave9-capture-packet.v1.json").read_bytes())
        self.assertEqual("authored-not-executed", packet["status"])
        sources = {}
        for row in packet["sources"]:
            path = ROOT / row["path"]
            self.assertEqual(row["sha256"], hashlib.sha256(path.read_bytes()).hexdigest())
            sources[row["names"][0]] = path.read_text(encoding="utf-8")
        cases = json.loads((ROOT / "fixtures/typed-graphical/wave9-cases.v1.json").read_bytes())["cases"]
        self.assertEqual(84, len(cases))
        for case in cases:
            with self.subTest(case=case["fixtureId"]):
                self.assertEqual("fixtures/typed-graphical/native/" + case["blockName"] + ".xml", case["referenceXml"])
                sections = {"Input": [], "Output": [], "InOut": [], "Static": [], "Temp": [], "Constant": []}
                for keyword, body in re.findall(r"^(VAR_INPUT|VAR_OUTPUT|VAR)\n(.*?)^END_VAR", sources[case["blockName"]], re.M | re.S):
                    sections[{"VAR_INPUT": "Input", "VAR_OUTPUT": "Output", "VAR": "Static"}[keyword]] += [
                        {"name": name, "dataType": data_type, "members": []} for name, data_type in re.findall(r"^  (\w+) : (\w+);$", body, re.M)]
                declared = {member["name"] for members in sections.values() for member in members}
                for operand in case["payload"]["model"]["operands"]:
                    if operand["scope"] == "local":
                        self.assertIn(operand["components"][0], declared)
                interface = {"block": {"name": case["blockName"], "kind": "FB"}, "objectKinds": ["SW.Blocks.FB"],
                             "sections": [{"name": name, "members": members} for name, members in sections.items()]}
                program = generate_program(inspect_interface(interface, {}), ["dut"], prefix="Wave9Harness")
                suite = {"fixtureId": case["fixtureId"], "instances": ["dut"], "cases": independent_behavior(case["family"])}
                validate_suite(suite, program)
                self.assertEqual(1, len(suite["cases"]))  # live qualification admits one authored case

    def test_wave9_arithmetic_and_real_expectations_are_exact_in_their_plc_types(self):
        limits = {"int": 2 ** 15, "dint": 2 ** 31}
        operations = {"add": lambda x, y: x + y, "sub": lambda x, y: x - y, "mul": lambda x, y: x * y, "div": lambda x, y: x / y}
        for numeric in ("int", "dint", "real"):
            families = ["move-" + numeric] + [op + "-" + numeric for op in operations] + ([c + "-" + numeric for c in ("eq", "lt")] if numeric != "int" else [])
            for family in families:
                for step in independent_behavior(family)[0]["steps"]:
                    values = [*step["set"].values(), *(a["expected"] for a in step["assertions"] if not isinstance(a["expected"], bool))]
                    for value in values:
                        if numeric == "real":
                            self.assertEqual(value, struct.unpack("<f", struct.pack("<f", value))[0], family)
                        else:
                            self.assertTrue(isinstance(value, int) and -limits[numeric] <= value < limits[numeric], family)
                    operation = family.partition("-")[0]
                    if operation in operations:
                        left, right = step["set"]["dut.Left"], step["set"]["dut.Right"]
                        self.assertEqual(operations[operation](left, right), step["assertions"][0]["expected"], family)

    def test_all_authored_case_constants_use_typed_request_grammar(self):
        count = 0
        for path in (ROOT / "fixtures/typed-graphical").glob("*cases.v1.json"):
            for case in json.loads(path.read_bytes())["cases"]:
                for operand in case["payload"]["model"].get("operands", []):
                    if operand["scope"] == "constant" and operand["dataType"] == "Bool":
                        self.assertIn(operand["value"], ("true", "false"), (path.name, case["fixtureId"]))
                        count += 1
        self.assertGreater(count, 0)



class ReferenceImportAdaptationTests(unittest.TestCase):
    def test_only_sub_and_div_lose_their_card_value(self):
        root = pathlib.Path(__file__).resolve().parents[1] / "fixtures/typed-graphical/native"
        for name, expected in (("Tgp_SubInt_LAD.xml", 1), ("Tgp_DivReal_FBD.xml", 1), ("Tgp_AddInt_LAD.xml", 0), ("Tgp_MulDInt_FBD.xml", 0)):
            xml = (root / name).read_text(encoding="utf-8-sig")
            adapted, removed = adapt_reference_import(xml)
            self.assertEqual(expected, removed, name)
            self.assertEqual(xml.count('Name="Card"') - expected, adapted.count('Name="Card"'), name)
            self.assertEqual(xml.count('Name="SrcType"'), adapted.count('Name="SrcType"'), name)

    def test_wave9_sub_div_topology_oracle_is_the_card_less_import_form(self):
        # Native w9q typed-sub-int-lad: TIA re-exports the adapted import without Card, and typed replacement emits none,
        # so the topology oracle must be the adapted reference (no topologyXml bypasses it); the raw one would differ.
        cases = json.loads((ROOT / "fixtures/typed-graphical/wave9-cases.v1.json").read_bytes())["cases"]
        selected = [c for c in cases if any(n.get("instruction") in ("SUB", "DIV") for n in c["payload"]["model"]["nodes"])]
        self.assertEqual(12, len(selected))
        for case in selected:
            with self.subTest(case=case["fixtureId"]):
                self.assertNotIn("topologyXml", case)
                xml = (ROOT / case["referenceXml"]).read_text(encoding="utf-8-sig")
                adapted, removed = adapt_reference_import(xml)
                self.assertEqual(1, removed)
                self.assertEqual(re.sub(r'<TemplateValue Name="Card" Type="Cardinality">2</TemplateValue>\s*', "", xml), adapted)
                self.assertNotEqual(topology(xml), topology(adapted))

if __name__ == "__main__":
    unittest.main()
