"""Offline checks of the technology version probe planner and the profile merge; nothing here starts TIA."""
import json
import unittest

import probe_technology_versions as p
import technology_version_profiles as t


def simulate(line, accepts):
    """Run the planner against a CPU that accepts exactly the given (family, version) pairs."""
    outcomes = {}
    while (cell := p.next_cell(line, outcomes)) is not None:
        outcomes[cell] = "accepted" if cell in accepts else "refused"
    return outcomes


class PlannerTests(unittest.TestCase):
    def test_newest_first_until_accepted(self):
        outcomes = simulate("S7-1500", {("PID_Compact", "2.4")})
        self.assertEqual(["3.0", "2.4"], [v for f, v in outcomes if f == "PID_Compact"])

    def test_motion_families_follow_the_anchor_version(self):
        outcomes = simulate("S7-1500", {(p.ANCHOR, "4.0"), ("TO_SpeedAxis", "4.0")})
        self.assertEqual(list(p.MOTION_VERSIONS), [v for f, v in outcomes if f == p.ANCHOR])
        for family in p.MOTION["S7-1500"][1:]:
            self.assertEqual(["4.0"], [v for f, v in outcomes if f == family])

    def test_without_an_anchor_each_motion_family_gets_one_cell(self):
        outcomes = simulate("S7-1200", set())
        for family in p.MOTION["S7-1200"][1:]:
            self.assertEqual([p.MOTION_VERSIONS[0]], [v for f, v in outcomes if f == family])
        self.assertNotIn("High_Speed_Counter", {f for f, _ in outcomes})

    def test_worst_case_matrix_fits_one_run(self):
        total = sum(len(simulate(line, set())) for _, _, line in p.CPUS)
        self.assertLessEqual(total, 60)

    def test_version_equality_matches_system_version(self):
        self.assertTrue(p.same_version("10.0", "10.00"))
        self.assertFalse(p.same_version("10.0", "10.0.0"))
        self.assertFalse(p.same_version(None, ""))


class MergeTests(unittest.TestCase):
    def report(self, *rows):
        return {"completed": True, "cells": [dict(cpuTypeIdentifier="cpu", cpuTypeName="CPU", **row) for row in rows]}

    def test_native_observations_become_cells(self):
        report = self.report({"typeIdentifier": "PID_Compact", "version": "3.0", "outcome": "accepted"},
                             {"typeIdentifier": "TO_Cam", "version": "10.0", "outcome": "refused", "error": {"code": "EngineeringError", "message": "No."}},
                             {"typeIdentifier": "TO_Cam", "version": "9.0", "outcome": "substituted", "observed": {"typeIdentifier": "TO_Cam", "version": "10.0"}},
                             {"typeIdentifier": "PID_Temp", "version": "1.2", "outcome": "refusedAtPrepare", "error": {"code": "InvalidRequest"}})
        merged = t.merge({"cpus": []}, t.observed_cells(report, "local.x"))
        cells = merged["cpus"][0]["cells"]
        self.assertEqual([("PID_Compact", "3.0", "accepted"), ("TO_Cam", "9.0", "refused"), ("TO_Cam", "10.0", "refused")],
                         [(c["typeIdentifier"], c["version"], c["outcome"]) for c in cells])
        self.assertEqual("VersionSubstituted", cells[1]["nativeRefusal"]["code"])
        self.assertEqual({"code": "EngineeringError", "message": "No."}, cells[2]["nativeRefusal"])

    def test_a_later_run_replaces_the_same_cell(self):
        first = t.merge({"cpus": []}, t.observed_cells(self.report({"typeIdentifier": "PID_Compact", "version": "3.0", "outcome": "accepted"}), "local.a"))
        second = t.merge(first, t.observed_cells(self.report({"typeIdentifier": "PID_Compact", "version": "3.00", "outcome": "refused",
                                                               "error": {"code": "E", "message": "m"}}), "local.b"))
        self.assertEqual([("3.00", "local.b")], [(c["version"], c["evidenceId"]) for c in second["cpus"][0]["cells"]])

    def test_an_incomplete_run_is_not_merged(self):
        with self.assertRaises(ValueError):
            t.observed_cells({"completed": False, "cells": []}, "local.x")

    def test_shipped_contract_cells_are_sorted_and_unique(self):
        document = json.loads(t.CONTRACT.read_text(encoding="utf-8"))
        self.assertEqual(document, t.merge(document, []))


if __name__ == "__main__":
    unittest.main()
