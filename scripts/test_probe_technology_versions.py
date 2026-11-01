"""Offline checks of the technology version probe planner and the profile merge; nothing here starts TIA."""
import json
from pathlib import Path
import tempfile
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


class ApplyFailureTests(unittest.TestCase):
    error = {"code": "EngineeringError", "message": "Error when calling method 'Create'."}
    native = {"role": "native-exception", "chain": [{"type": "Siemens.Engineering.EngineeringTargetInvocationException", "message": "Error when calling method 'Create'.",
                                                     "text": "Error when calling method 'Create'.", "detail": None,
                                                     "details": [{"text": "The type or version of the library element \"TO_Cam.9.0\" does not exist.", "detail": None}]}]}

    def status(self, stages, *readbacks):
        return {"stages": stages, "readbacks": list(readbacks)}

    def test_only_tia_refusing_the_create_is_a_refused_cell(self):
        refused = p.apply_failure(self.error, self.status(["prechange_project_saved", "recovery_archive_created"], self.native))
        self.assertEqual("refused", refused["outcome"])
        self.assertEqual({"code": "EngineeringError", "message": "Error when calling method 'Create'. The type or version of the library element \"TO_Cam.9.0\" does not exist."},
                         refused["nativeRefusal"])

    def test_other_apply_failures_are_failed_at_apply(self):
        worker = {"role": "native-exception", "chain": [{"type": "TiaMcp.Openness.V21.BridgeException", "message": "Technology object already exists."}]}
        for status in (self.status(["prechange_project_saved"], self.native),
                       self.status(["prechange_project_saved", "recovery_archive_created", "mutation_applied"], self.native),
                       self.status(["prechange_project_saved", "recovery_archive_created"], worker),
                       self.status(["prechange_project_saved", "recovery_archive_created"], {"role": "failure-details", "details": {}}),
                       self.status(["prechange_project_saved", "recovery_archive_created"])):
            self.assertEqual("failedAtApply", p.apply_failure(self.error, status)["outcome"], status)


class RecoveryCopyTests(unittest.TestCase):
    def test_only_copies_under_the_run_recovery_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "recovery"
            project = root / "Recovery_1" / "TiaMcpScratch" / "TiaMcpScratch.ap21"
            self.assertEqual((root / "Recovery_1").resolve(), p.recovery_copy(root, project))
            self.assertIsNone(p.recovery_copy(root, Path(directory) / "scratch" / "TiaMcpScratch" / "TiaMcpScratch.ap21"))
            self.assertIsNone(p.recovery_copy(root, root / "loose.ap21"))
            project.parent.mkdir(parents=True)
            project.write_bytes(b"ap21")
            receipt = p.copy_receipt(root / "Recovery_1", project)
            self.assertEqual((1, 4), (receipt["files"], receipt["bytes"]))


class MergeTests(unittest.TestCase):
    def report(self, *rows):
        return {"completed": True, "cells": [dict(cpuTypeIdentifier="cpu", cpuTypeName="CPU", **row) for row in rows]}

    def test_native_observations_become_cells(self):
        report = self.report({"typeIdentifier": "PID_Compact", "version": "3.0", "outcome": "accepted"},
                             {"typeIdentifier": "TO_Cam", "version": "10.0", "outcome": "refused", "nativeRefusal": {"code": "EngineeringError", "message": "No."}},
                             {"typeIdentifier": "TO_Cam", "version": "8.0", "outcome": "failedAtApply", "error": {"code": "CheckpointFailed", "message": "x"}},
                             {"typeIdentifier": "TO_Cam", "version": "9.0", "outcome": "substituted", "observed": {"typeIdentifier": "TO_Cam", "version": "10.0"}},
                             {"typeIdentifier": "PID_Temp", "version": "1.2", "outcome": "refusedAtPrepare", "error": {"code": "InvalidRequest"}})
        merged = t.merge({"cpus": []}, t.observed_cells(report, "local.x"))
        cells = merged["cpus"][0]["cells"]
        self.assertEqual([("PID_Compact", "3.0", "accepted"), ("TO_Cam", "9.0", "refused"), ("TO_Cam", "10.0", "refused")],
                         [(c["typeIdentifier"], c["version"], c["outcome"]) for c in cells])
        self.assertEqual("VersionSubstituted", cells[1]["nativeRefusal"]["code"])
        self.assertEqual({"code": "EngineeringError", "message": "No."}, cells[2]["nativeRefusal"])

    def test_the_merge_takes_tias_reason_from_the_journaled_chain(self):
        chain = [[{"type": "Siemens.Engineering.EngineeringTargetInvocationException", "message": "m", "text": "Error when calling method 'Create'.",
                   "details": [{"text": "The type or version of the library element \"PID_3Step.3.0\" does not exist or is not a valid technology object."}]}]]
        report = self.report({"typeIdentifier": "PID_3Step", "version": "3.0", "outcome": "refused", "native": chain,
                              "nativeRefusal": {"code": "EngineeringError", "message": "Error when calling method 'Create'."}})
        cell = t.observed_cells(report, "local.x")[0][2]
        self.assertEqual("Error when calling method 'Create'. The type or version of the library element \"PID_3Step.3.0\" does not exist or is not a valid technology object.",
                         cell["nativeRefusal"]["message"])

    def test_a_later_run_replaces_the_same_cell(self):
        first = t.merge({"cpus": []}, t.observed_cells(self.report({"typeIdentifier": "PID_Compact", "version": "3.0", "outcome": "accepted"}), "local.a"))
        second = t.merge(first, t.observed_cells(self.report({"typeIdentifier": "PID_Compact", "version": "3.00", "outcome": "refused",
                                                               "nativeRefusal": {"code": "E", "message": "m"}}), "local.b"))
        self.assertEqual([("3.00", "local.b")], [(c["version"], c["evidenceId"]) for c in second["cpus"][0]["cells"]])

    def test_an_incomplete_run_is_not_merged(self):
        with self.assertRaises(ValueError):
            t.observed_cells({"completed": False, "cells": []}, "local.x")

    def test_shipped_contract_cells_are_sorted_and_unique(self):
        document = json.loads(t.CONTRACT.read_text(encoding="utf-8"))
        self.assertEqual(document, t.merge(document, []))


if __name__ == "__main__":
    unittest.main()
