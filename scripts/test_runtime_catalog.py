"""Offline coverage and harness compatibility; these tests never qualify a PLC."""
import importlib.util
import itertools
import json
import pathlib
import unittest

from block_test_executor import validate_suite
from block_test_program import generate_program, inspect_interface


ROOT = pathlib.Path(__file__).resolve().parents[1]
_spec = importlib.util.spec_from_file_location("runtime_catalog", ROOT / "fixtures/runtime_catalog.py")
catalog = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(catalog)


def expected(step):
    return {item["path"]: item["expected"] for item in step["assertions"] if item["kind"] == "exact"}


class RuntimeCatalogTests(unittest.TestCase):
    def setUp(self):
        self.suites = {suite["fixtureId"]: suite for suite in catalog.specifications()}

    def test_every_suite_generates_and_preflights_with_actual_executor(self):
        self.assertEqual({"boolean_logic", "arithmetic_conversions", "if_case", "edge_detection",
                          "sr_rs_latches", "timers", "counters", "fb_instances", "arrays_udts"},
                         {suite["category"] for suite in self.suites.values()})
        for suite in self.suites.values():
            with self.subTest(fixture=suite["fixtureId"]):
                interface = inspect_interface(suite["interface"], suite["typeDefinitions"])
                program = generate_program(interface, suite["instances"], prefix="Harness")
                validate_suite(suite, program)
                self.assertEqual("specified_not_executed", suite["runtimeQualification"])
                self.assertEqual("fresh_simulator_per_case", suite["isolation"])
                self.assertFalse(program["timing"]["scanSynchronized"])
                self.assertEqual("generated_not_compiled_or_runtime_qualified", program["qualification"])

    def test_catalog_is_deterministic_json_and_callers_cannot_mutate_shared_expectations(self):
        first = catalog.specifications()
        original = json.dumps(first, sort_keys=True, allow_nan=False)
        self.assertEqual(original, json.dumps(catalog.specifications(), sort_keys=True, allow_nan=False))
        first[0]["cases"][0]["steps"][0]["assertions"][0]["expected"] = "tampered"
        self.assertEqual(original, json.dumps(catalog.specifications(), sort_keys=True, allow_nan=False))
        alternate = catalog.specifications("Other")
        self.assertTrue(all(suite["blockName"].startswith("Other_") for suite in alternate))
        self.assertNotEqual(first[0]["source"], alternate[0]["source"])
        for prefix in ("", "bad space", "../bad", 'bad"', "x" * 21, None):
            with self.assertRaises(ValueError):
                catalog.specifications(prefix)

    def test_boolean_vectors_exhaust_complete_truth_table(self):
        cases = self.suites["runtime-boolean"]["cases"]
        seen = set()
        for case in cases:
            step = case["steps"][0]
            row = tuple(step["set"]["dut." + name] for name in ("A", "B", "Inhibit"))
            seen.add(row)
            self.assertIs((row[0] or row[1]) and not row[2], expected(step)["dut.Active"])
        self.assertEqual(set(itertools.product((False, True), repeat=3)), seen)
        self.assertEqual(8, len(cases))

    def test_arithmetic_boundaries_include_signed_widening_and_fractional_results(self):
        cases = self.suites["runtime-arithmetic"]["cases"]
        vectors = {(step["set"]["dut.Left"], step["set"]["dut.Right"]): step
                   for case in cases for step in case["steps"]}
        for inputs, result in {(-32768, -32768): -65536, (32767, 32767): 65534,
                               (-32768, 32767): -1, (0, 0): 0}.items():
            self.assertEqual(result, expected(vectors[inputs])["dut.Sum"])
        self.assertEqual(65535, expected(vectors[(32767, -32768)])["dut.Difference"])
        self.assertEqual(-0.5, next(item["expected"] for item in vectors[(-32768, 32767)]["assertions"]
                                    if item["kind"] == "approx"))

    def test_every_branch_and_both_sides_of_each_boundary_have_witnesses(self):
        steps = [case["steps"][0] for case in self.suites["runtime-branches"]["cases"]]
        by_input = {(s["set"]["dut.Value"], s["set"]["dut.Mode"]): expected(s) for s in steps}
        for value, band in ((-11, -1), (-10, 0), (-9, 0), (9, 0), (10, 0), (11, 1)):
            for mode, selected in ((0, 0), (1, value), (2, -999), (255, -999)):
                self.assertEqual({"dut.Band": band, "dut.Selected": selected}, by_input[(value, mode)])

    def test_edges_latches_and_reset_isolation_have_stateful_witnesses(self):
        edge_cases = {c["id"]: c for c in self.suites["runtime-edges"]["cases"]}
        edges = {s["id"]: s for s in edge_cases["both-edges-and-held-levels"]["steps"]}
        self.assertEqual({"dut.Rising": True, "dut.Falling": False}, expected(edges["rising"]))
        self.assertEqual({"dut.Rising": False, "dut.Falling": True}, expected(edges["falling"]))
        self.assertGreater(edges["held-high"]["calls"], 1)
        self.assertGreater(edges["held-low"]["calls"], 1)
        self.assertTrue(expected(edge_cases["fresh-retained-state"]["steps"][0])["dut.Rising"])
        self.assertIn("pulse", edge_cases["explicit-pulse"]["steps"][0])
        latches = {s["id"]: s for s in self.suites["runtime-latches"]["cases"][0]["steps"]}
        self.assertEqual({"dut.SetDominant": True, "dut.ResetDominant": False}, expected(latches["simultaneous"]))
        self.assertEqual(expected(latches["simultaneous"]), expected(latches["retain-priority-result"]))
        self.assertEqual({"dut.SetDominant": False, "dut.ResetDominant": False}, expected(latches["reset"]))

    def test_timer_elapsed_assertion_requires_progress_and_windows_disclose_limits(self):
        suite = self.suites["runtime-timers"]
        self.assertTrue(suite["timingLimitations"])
        assertions = [item for c in suite["cases"] for s in c["steps"] for item in s["assertions"]]
        elapsed = next(item for item in assertions if item["kind"] == "elapsed")
        self.assertEqual("start", elapsed["sinceStep"])
        self.assertEqual(1000, elapsed["capMs"])
        self.assertLess(elapsed["maxUncertaintyMs"], elapsed["capMs"])
        window = next(item for item in assertions if item["kind"] == "window")
        self.assertGreater(window["maxMs"], window["minMs"])
        self.assertGreater(window["minMs"], 0)
        completion = next(case for case in suite["cases"] if case["id"] == "completion-window-and-reset")
        capped = next(step for step in completion["steps"] if step["id"] == "completed-elapsed")
        self.assertEqual(completion["initial"]["dut.Preset"], expected(capped)["dut.Elapsed"])
        self.assertGreater(completion["initial"]["dut.Preset"], elapsed["maxUncertaintyMs"])
        for case in suite["cases"]:
            self.assertEqual(int, type(case["initial"]["dut.Preset"]))
            reset = case["steps"][-1]
            self.assertFalse(reset["set"]["dut.Enable"])
            self.assertEqual({"dut.Done": False, "dut.Elapsed": 0}, expected(reset))

    def test_counter_saturates_storage_after_preset_and_resets(self):
        cases = {c["id"]: c for c in self.suites["runtime-counters"]["cases"]}
        normal = {s["id"]: s for s in cases["edges-threshold-reset"]["steps"]}
        self.assertEqual(expected(normal["edge-one"]), expected(normal["held-high"]))
        self.assertTrue(expected(normal["threshold"])["dut.Reached"])
        self.assertEqual(0, expected(normal["reset-wins"])["dut.CountValue"])
        limit = {s["id"]: s for s in cases["signed-storage-limit"]["steps"]}
        self.assertEqual(126, expected(limit["edge-126"])["dut.CountValue"])
        self.assertEqual(127, expected(limit["edge-127"])["dut.CountValue"])
        self.assertEqual(127, expected(limit["edge-128"])["dut.CountValue"])
        self.assertEqual(0, expected(limit["reset-saturated"])["dut.CountValue"])

    def test_independent_instances_and_nested_initialization_are_observable(self):
        suite = self.suites["runtime-fb-instances"]
        self.assertEqual(["left", "right"], suite["instances"])
        steps = {s["id"]: s for s in suite["cases"][0]["steps"]}
        self.assertEqual(3, expected(steps["left-a-three"])["left.CountA"])
        self.assertEqual(0, expected(steps["left-a-three"])["right.CountA"])
        self.assertEqual(2, expected(steps["retain-right"])["right.CountB"])
        suite = self.suites["runtime-arrays-udts"]
        steps = {s["id"]: s for s in suite["cases"][0]["steps"]}
        self.assertEqual(-17, expected(steps["load-start-values"])["dut.Item.Samples[-2]"])
        self.assertEqual("nested", expected(steps["nested-writes"])["dut.Item.Meta.LabelText"])
        fresh = expected(suite["cases"][1]["steps"][0])
        self.assertEqual(0, fresh["dut.Item.Samples[-2]"])
        self.assertFalse(fresh["dut.Item.Enabled"])


if __name__ == "__main__":
    unittest.main()
