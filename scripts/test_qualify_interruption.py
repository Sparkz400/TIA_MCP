"""Offline checks of the change_apply interruption matrix in qualify_interruption.py."""
import json
import pathlib
import re
import unittest

import qualify_interruption as q

ROOT = pathlib.Path(__file__).resolve().parents[1]


def csharp_set(path, pattern):
    text = (ROOT / path).read_text(encoding="utf-8")
    return set(re.findall(r'"([a-z_0-9]+)"', re.search(pattern, text, re.S).group(1)))


class InterruptionMatrixTests(unittest.TestCase):
    def test_every_catalog_change_kind_has_a_reconciliation_route(self):
        catalog = json.loads((ROOT / "contracts/feature-catalog.v1.json").read_text(encoding="utf-8"))
        kinds = {f["changeKind"] for f in catalog["features"] if f["changeKind"]}
        self.assertGreater(len(kinds), 50)
        routes = {kind: q.reconciliation_class(kind) for kind in kinds}
        self.assertEqual({"software", "hardware", "target", "library"}, set(routes.values()))
        # Every engineering route has a native representative; library transactions use qualify_library_interruption.py.
        represented = {q.reconciliation_class(kind) for kind, _, _ in q.KINDS.values()}
        self.assertEqual({"software", "hardware", "target"}, represented)

    def test_kind_sets_match_the_worker(self):
        self.assertEqual(q.TARGET_KINDS, csharp_set("src/TiaMcp.Openness.V21/AdmissionReconcileRules.cs", r"TargetKinds = new HashSet<string>\(new\[\] \{(.*?)\}"))
        self.assertEqual(q.HARDWARE_RULE_KINDS, csharp_set("src/TiaMcp.Openness.V21/HardwareEngineeringRules.cs", r"bool Applies\(string kind\) => new\[\] \{(.*?)\}"))

    def test_points_split_the_journal_without_overlap(self):
        journals = {
            "ckpt": {"state": "checkpointing", "stages": []},
            "mut": {"state": "applying", "stages": ["prechange_project_saved", "recovery_archive_created"]},
            "comp": {"state": "applying", "stages": ["recovery_archive_created", "mutation_applied"]},
            "save": {"state": "applying", "stages": ["mutation_applied", "compile_finished", "changed_project_saved"]},
        }
        for name, journal in journals.items():
            hits = [point for point, (trigger, mode) in q.POINTS.items() if mode == "worker" and trigger(journal)]
            self.assertEqual([name], hits, name)
        for terminal in ({"state": "applied", "stages": ["changed_project_saved"]}, {"state": "prepared"}, {}):
            self.assertFalse(any(trigger(terminal) for trigger, _ in q.POINTS.values()), terminal)
        self.assertEqual("host", q.POINTS["host"][1])

    def test_expected_outcomes(self):
        for kind in ("source_upsert", "hardware_plug", "to_create", "tag_set"):
            self.assertEqual({"released": False, "effectPersisted": False}, q.expected_outcome(kind, "ckpt"))
            for point in ("mut", "comp"):
                self.assertEqual({"released": True, "project": "unchanged", "effectPersisted": False}, q.expected_outcome(kind, point))
            self.assertEqual({"released": False, "effectPersisted": True}, q.expected_outcome(kind, "save"))
        self.assertEqual({"released": True, "project": "verifiedApplied", "effectPersisted": True}, q.expected_outcome("source_upsert", "host"))
        self.assertEqual({"released": True, "project": "verifiedApplied", "effectPersisted": True}, q.expected_outcome("hardware_plug", "host"))
        self.assertEqual({"released": False, "effectPersisted": True}, q.expected_outcome("to_create", "host"))

    def test_scenario_names_stay_short(self):
        # The reconciliation retrieve root (base/scenario/ar_<12>) must stay within the worker's 70-character limit.
        self.assertEqual(15, len(q.KIND_SCENARIOS))
        self.assertTrue(all(len(name) <= 7 for name in q.KIND_SCENARIOS))
        self.assertFalse(set(q.KIND_SCENARIOS) & set(q.SCENARIOS))


if __name__ == "__main__":
    unittest.main()
