"""Offline typed-builder contract and independent behavioral fixture checks.

These checks do not import native blocks, start a simulator, or qualify behavior.
"""
import itertools
import hashlib
import json
import pathlib
import tempfile
import unittest

from block_test_executor import validate_suite
from block_test_program import generate_program, inspect_interface
import typed_graphical_runtime_catalog as catalog


ROOT = pathlib.Path(__file__).resolve().parents[1]


def expected(step):
    return {item["path"]: item["expected"] for item in step["assertions"] if item["kind"] == "exact"}


class TypedGraphicalContractTests(unittest.TestCase):
    def setUp(self):
        self.suites = {suite["fixtureId"]: suite for suite in catalog.specifications()}

    def test_all_suites_use_existing_executor_and_inspected_harness_contract(self):
        self.assertEqual(30, len(self.suites))
        for suite in self.suites.values():
            with self.subTest(fixture=suite["fixtureId"]):
                interface = inspect_interface(suite["interface"], suite["typeDefinitions"])
                program = generate_program(interface, suite["instances"], prefix="TypedHarness")
                validate_suite(suite, program)
                self.assertEqual("specified_not_executed", suite["runtimeQualification"])
                self.assertEqual("fresh_simulator_per_case", suite["isolation"])
                self.assertFalse(program["timing"]["scanSynchronized"])
                self.assertNotIn("source", suite)

    def test_languages_have_separate_ids_and_equal_independent_observations(self):
        lad = {item["category"]: item for item in self.suites.values() if item["language"] == "LAD"}
        fbd = {item["category"]: item for item in self.suites.values() if item["language"] == "FBD"}
        self.assertEqual(set(lad), set(fbd))
        for family in lad:
            self.assertEqual(lad[family]["cases"], fbd[family]["cases"])
            self.assertNotEqual(lad[family]["blockName"], fbd[family]["blockName"])

    def test_boolean_table_is_complete_and_independently_correct(self):
        for language in ("lad", "fbd"):
            cases = self.suites["typed-" + language + "-boolean-branches"]["cases"]
            rows = set()
            for case in cases:
                step = case["steps"][0]
                values = tuple(step["set"]["dut." + name] for name in ("A", "B", "Inhibit"))
                rows.add(values)
                self.assertEqual((values[0] or values[1]) and not values[2], expected(step)["dut.Active"])
            self.assertEqual(set(itertools.product((False, True), repeat=3)), rows)

    def test_comparison_boundaries_distinguish_strict_and_inclusive_operations(self):
        operations = {"eq": lambda a, b: a == b, "ne": lambda a, b: a != b,
                      "lt": lambda a, b: a < b, "le": lambda a, b: a <= b,
                      "gt": lambda a, b: a > b, "ge": lambda a, b: a >= b}
        for op, reference in operations.items():
            suite = self.suites["typed-lad-compare-" + op + "-int"]
            inputs = set()
            for case in suite["cases"]:
                step = case["steps"][0]
                pair = step["set"]["dut.Left"], step["set"]["dut.Right"]
                inputs.add(pair)
                self.assertEqual(reference(*pair), expected(step)["dut.Result"])
            self.assertTrue({(-32768, 32767), (32767, -32768), (0, 0), (32767, 32767)} <= inputs)

    def test_parameter_flow_and_instance_independence_have_observable_witnesses(self):
        suite = self.suites["typed-lad-fc-parameter-flow"]
        for case in suite["cases"]:
            step = case["steps"][0]
            self.assertEqual(step["set"]["dut.Value"] + step["set"]["dut.Delta"], expected(step)["dut.Result"])
            self.assertEqual(case["initial"]["dut.Accumulator"] + step["set"]["dut.Value"],
                             expected(step)["dut.Accumulator"])
        fb = self.suites["typed-fbd-fb-instance-independence"]
        self.assertEqual(["left", "right"], fb["instances"])
        steps = {step["id"]: step for step in fb["cases"][0]["steps"]}
        self.assertEqual({"left.Total": 6, "right.Total": 0}, expected(steps["left-two-calls"]))
        self.assertEqual({"right.Total": -2, "left.Total": 6}, expected(steps["right-call"]))
        self.assertEqual({"left.Total": 0, "right.Total": -2}, expected(steps["left-reset"]))

    def test_every_stateful_family_has_distinct_assertions_and_timing_limitations(self):
        for instruction in ("ton", "tof", "tp", "ctu", "ctd", "ctud"):
            family = "timer-" if instruction in ("ton", "tof", "tp") else "counter-"
            for language in ("lad", "fbd"):
                suite = self.suites["typed-" + language + "-" + family + instruction]
                self.assertEqual(instruction.upper(), suite["instruction"])
                assertions = [item for case in suite["cases"] for step in case["steps"] for item in step["assertions"]]
                self.assertGreater(len(assertions), 5)
                if family == "timer-":
                    self.assertGreater(len(suite["timingLimitations"]), 1)
                    self.assertTrue(any(item["kind"] == "window" for item in assertions))
                else:
                    steps = [step for case in suite["cases"] for step in case["steps"]]
                    self.assertTrue(any(step["calls"] > 1 for step in steps))
        tp = {step["id"]: step for step in self.suites["typed-lad-timer-tp"]["cases"][0]["steps"]}
        self.assertTrue(expected(tp["input-falls"])["dut.Done"])
        self.assertFalse(expected(tp["held-high-no-retrigger"])["dut.Done"])
        both = {step["id"]: step for step in self.suites["typed-fbd-counter-ctud"]["cases"][0]["steps"]}
        self.assertEqual(2, expected(both["both-rising"])["dut.CountValue"])
        self.assertEqual(0, expected(both["reset-over-load"])["dut.CountValue"])
        limits = {step["id"]: step for step in self.suites["typed-lad-counter-ctud"]["cases"][1]["steps"]}
        self.assertEqual(32767, expected(limits["maximum-saturates"])["dut.CountValue"])
        self.assertEqual(-32768, expected(limits["minimum-saturates"])["dut.CountValue"])
        self.assertIn("not asserted", self.suites["typed-lad-counter-ctu"]["limitCoverage"])

    def test_specs_are_fresh_deterministic_and_reject_unsafe_fixture_names(self):
        snapshot = json.dumps(catalog.specifications(), sort_keys=True, allow_nan=False)
        mutated = catalog.specifications()
        mutated[0]["cases"][0]["steps"][0]["assertions"][0]["expected"] = "changed"
        self.assertEqual(snapshot, json.dumps(catalog.specifications(), sort_keys=True, allow_nan=False))
        self.assertTrue(all(suite["blockName"].startswith("Other_") for suite in catalog.specifications("Other")))
        for invalid in (None, "", "../bad", "a b", "a'", "x" * 21):
            with self.assertRaises(ValueError):
                catalog.specifications(invalid)

    def test_schemas_are_versioned_self_contained_and_survive_resource_relocation(self):
        # No optional external schema library is required to catch missing local
        # definitions, invalid JSON, machine-specific paths, or deployment omissions.
        for path in sorted((ROOT / "contracts").glob("typed-graphical*.schema.json")):
            content = path.read_bytes()
            schema = json.loads(content)
            self.assertEqual("https://json-schema.org/draft/2020-12/schema", schema["$schema"])
            self.assertTrue(schema["$id"].endswith(":v1"))
            self.assertFalse(schema["additionalProperties"])
            pending = [schema]
            while pending:
                item = pending.pop()
                if isinstance(item, dict):
                    if "$ref" in item:
                        reference = item["$ref"]
                        if reference.startswith("#/$defs/"):
                            self.assertIn(reference.split("/")[-1], schema["$defs"])
                        else:
                            self.assertNotIn("/", reference)
                            self.assertTrue((path.parent / reference).is_file())
                    pending.extend(item.values())
                elif isinstance(item, list):
                    pending.extend(item)
            scratch_root = ROOT / "artifacts/typed-graphical/contract-tests"
            scratch_root.mkdir(parents=True, exist_ok=True)
            with tempfile.TemporaryDirectory(prefix="typed-schema-", dir=scratch_root) as directory:
                relocated = pathlib.Path(directory) / path.name
                relocated.write_bytes(content)
                self.assertEqual(schema, json.loads(relocated.read_bytes()))
        model = json.loads((ROOT / "contracts/typed-graphical-model.schema.json").read_bytes())
        self.assertEqual(1, model["properties"]["schemaVersion"]["const"])
        self.assertEqual({"LAD", "FBD"}, set(model["properties"]["language"]["enum"]))
        self.assertNotIn("profiles", model["properties"])

    def test_deployed_profiles_bind_native_sources_and_keep_qualification_independent(self):
        path = ROOT / "fixtures/typed-graphical/profiles.v1.json"
        data = json.loads(path.read_bytes())
        schema = json.loads((ROOT / "contracts/typed-graphical-profiles.schema.json").read_bytes())
        self.assertEqual(1, data["schemaVersion"])
        self.assertFalse(set(data) - set(schema["properties"]))
        self.assertFalse(set(schema["required"]) - set(data))
        sources = {}
        for source in data["sources"]:
            source_path = ROOT / source["path"]
            self.assertTrue(source_path.resolve().is_relative_to(ROOT))
            self.assertEqual(source["sha256"], hashlib.sha256(source_path.read_bytes()).hexdigest())
            self.assertFalse(source["builderUsed"])
            authored = ROOT / source["authoredSourcePath"]
            self.assertEqual(source["authoredSourceSha256"], hashlib.sha256(authored.read_bytes()).hexdigest())
            sources[source["sha256"]] = source
        ids = set()
        for profile in data["profiles"]:
            with self.subTest(profile=profile["id"]):
                self.assertNotIn(profile["id"], ids)
                ids.add(profile["id"])
                self.assertFalse(set(profile) - set(schema["$defs"]["profile"]["properties"]))
                self.assertFalse(set(schema["$defs"]["profile"]["required"]) - set(profile))
                self.assertIn(profile["sourceSha256"], sources)
                self.assertTrue(profile["nativeFormatValidated"])
                self.assertIn(profile["language"], ("LAD", "FBD"))
                self.assertTrue(profile["cpuFamily"].startswith("OrderNumber:"))
                for evidence in ("engineeringVerified", "preservationVerified", "persistenceVerified", "recoveryVerified", "runtimeVerified"):
                    self.assertIs(type(profile[evidence]), bool)
        for template in data.get("creationTemplates", []) + data.get("globalInstanceTemplates", []) + data.get("multiInstanceTemplates", []):
            template_path = path.parent / template["path"]
            self.assertTrue(template_path.resolve().is_relative_to(path.parent))
            self.assertEqual(template["sha256"], hashlib.sha256(template_path.read_bytes()).hexdigest())
            if "referenceCalleePath" in template:
                reference = path.parent / template["referenceCalleePath"]
                self.assertTrue(reference.resolve().is_relative_to(path.parent))
                self.assertEqual(template["referenceCalleeSha256"], hashlib.sha256(reference.read_bytes()).hexdigest())


if __name__ == "__main__":
    unittest.main()
