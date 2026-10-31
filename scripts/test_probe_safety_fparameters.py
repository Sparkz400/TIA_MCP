"""Offline checks of the F-parameter discovery planning and the allowlist derivation; nothing here starts TIA."""
import json
import unittest

import probe_safety_fparameters as p
import safety_fparameter_allowlist as a
from probe_safety_kinds import DEST


def row(name, value, kind, access="ReadWrite"):
    return {"name": name, "value": value, "type": kind, "accessMode": access, "valueCaptured": True}


class CandidateTests(unittest.TestCase):
    def test_values_differ_from_the_current_one(self):
        values = {"a": {DEST: 100}, "b": {DEST: 99}}
        self.assertEqual(98, p.candidate(row(DEST, 100, "System.UInt64"), values))
        self.assertIs(False, p.candidate(row("Failsafe_ShortCircuitTest_0", True, "System.Boolean"), values))
        self.assertEqual(1, p.candidate(row("Failsafe_ManualAssignmentFMonitoringtime", 0, "System.UInt64"), values))
        self.assertEqual(1, p.candidate(row("Failsafe_ReintegrationAfterChannelFault", 2, "System.UInt64"), values))
        self.assertEqual(2.5, p.candidate(row("Failsafe_X", 1.5, "System.Double"), values))
        self.assertIsNone(p.candidate(row("Failsafe_DIPSwitchSetting", "0110", "System.String"), values))

    def test_only_captured_readwrite_rows_are_writable(self):
        self.assertTrue(p.writable(row("Failsafe_X", 1, "System.UInt64")))
        self.assertFalse(p.writable(row("Failsafe_X", 1, "System.UInt64", access="Read")))
        self.assertFalse(p.writable({**row("Failsafe_X", 1, "System.UInt64"), "valueCaptured": False}))


class BisectTests(unittest.TestCase):
    def test_failing_names_are_isolated(self):
        calls = []
        def attempt(batch):
            calls.append([r["name"] for r in batch])
            bad = [r["name"] for r in batch if r["name"] in ("c", "f")]
            return (not bad, {"error": bad} if bad else {})
        result = p.bisect([{"name": n} for n in "abcdefgh"], attempt)
        self.assertEqual({"c", "f"}, {n for n, r in result.items() if r["outcome"] == "refused"})
        self.assertEqual(set("abdegh"), {n for n, r in result.items() if r["outcome"] == "passed"})
        self.assertLessEqual(len(calls), 11)
        self.assertEqual(list("abcdefgh"), calls[0])

    def test_a_passing_batch_is_one_attempt(self):
        result = p.bisect([{"name": "a"}, {"name": "b"}], lambda batch: (True, {"stages": ["x"]}))
        self.assertEqual({"a": {"outcome": "passed", "batch": 2, "stages": ["x"]}, "b": {"outcome": "passed", "batch": 2, "stages": ["x"]}}, result)


class UnexpectedTests(unittest.TestCase):
    def test_classifies_differences_of_a_passing_batch(self):
        before = {"m": {DEST: row(DEST, 100, "System.UInt64"), "Failsafe_DIPSwitchSetting": row("Failsafe_DIPSwitchSetting", "1", "System.String", "Read"),
                        "Failsafe_FParameterSignatureWithAddresses": row("Failsafe_FParameterSignatureWithAddresses", 1, "System.UInt64", "Read"),
                        "Failsafe_ShortCircuitTest_0": row("Failsafe_ShortCircuitTest_0", True, "System.Boolean")},
                  "n": {DEST: row(DEST, 101, "System.UInt64")}}
        after = json.loads(json.dumps(before))
        after["m"][DEST]["value"] = 98
        after["m"]["Failsafe_DIPSwitchSetting"]["value"] = "2"
        after["m"]["Failsafe_FParameterSignatureWithAddresses"]["value"] = 2
        self.assertEqual(({}, {"Failsafe_DIPSwitchSetting": ["1", "2"]}), p.unexpected(before, after, "m", {DEST}))
        after["m"]["Failsafe_ShortCircuitTest_0"]["value"] = False
        after["n"][DEST]["value"] = 7
        bad, _ = p.unexpected(before, after, "m", {DEST})
        self.assertEqual({"m Failsafe_ShortCircuitTest_0", "n " + DEST}, set(bad))


class AllowlistTests(unittest.TestCase):
    report = {"completed": True, "fModule": {"typeIdentifier": "T:DI", "typeName": "F-DI"}, "fModules": {"TiaMcpFdq": {"typeIdentifier": "T:DQ", "typeName": "F-DQ"}, "TiaMcpFai": {"skipped": "notInCatalog"}},
              "modules": [{"moduleTypeIdentifier": "T:DI", "roundTrips": {DEST: {"outcome": "passed"}, "Failsafe_B": {"outcome": "refused"}}},
                          {"moduleTypeIdentifier": "T:DQ", "roundTrips": {"Failsafe_A": {"outcome": "passed"}}},
                          {"roundTrips": {}}]}

    def test_only_passed_round_trips_become_rows(self):
        merged = a.merge({"modules": []}, a.passed_rows(self.report, "local.x"))
        self.assertEqual([("T:DI", "F-DI", [DEST]), ("T:DQ", "F-DQ", ["Failsafe_A"])],
                         [(m["moduleTypeIdentifier"], m["moduleTypeName"], [r["name"] for r in m["parameters"]]) for m in merged["modules"]])

    def test_a_later_run_replaces_the_row(self):
        first = a.merge({"modules": []}, a.passed_rows(self.report, "local.a"))
        second = a.merge(first, a.passed_rows(self.report, "local.b"))
        self.assertEqual({"local.b"}, {r["evidenceId"] for m in second["modules"] for r in m["parameters"]})

    def test_an_incomplete_run_is_not_merged(self):
        with self.assertRaises(ValueError):
            a.passed_rows({**self.report, "completed": False}, "local.x")

    def test_shipped_allowlist_is_sorted(self):
        document = json.loads(a.CONTRACT.read_text(encoding="utf-8"))
        self.assertEqual(document, a.merge(document, []))


if __name__ == "__main__":
    unittest.main()
