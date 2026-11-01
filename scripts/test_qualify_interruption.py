"""Offline checks of the change_apply interruption matrix in qualify_interruption.py."""
import json
import pathlib
import queue
import re
import shutil
import tempfile
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
        self.assertEqual({"project", "hardware", "target", "library"}, set(routes.values()))
        # Every engineering route has a native representative; library transactions use qualify_library_interruption.py.
        represented = {q.reconciliation_class(kind) for kind, *_ in q.KINDS.values()}
        self.assertEqual({"project", "hardware", "target"}, represented)

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

    def test_text_list_points_split_import_and_readback(self):
        journals = {
            "ckpt": {"state": "checkpointing", "stages": []},
            "mut": {"state": "applying", "stages": ["prechange_project_saved", "recovery_archive_created"]},
            "imp": {"state": "applying", "stages": ["recovery_archive_created", "hmi_baseline_compiled", "hmi_text_list_import_started"]},
            "read": {"state": "applying", "stages": ["hmi_baseline_compiled", "hmi_text_list_import_started", "hmi_text_list_import_returned"]},
            "comp": {"state": "applying", "stages": ["hmi_text_list_import_returned", "hmi_text_lists_read_back_exactly", "mutation_applied"]},
            "save": {"state": "applying", "stages": ["mutation_applied", "compile_finished", "hmi_unrelated_content_preserved", "changed_project_saved"]},
        }
        host = {"ckpt": ["hckp"], "imp": ["himp"], "read": ["hred"], "save": ["hsav"], "mut": [], "comp": []}
        for name, journal in journals.items():
            for mode in ("worker", "host"):
                hits = [point for point, (trigger, m) in q.TEXT_LIST_POINTS.items() if m == mode and trigger(journal)]
                self.assertEqual([name] if mode == "worker" else host[name], hits, (name, mode))
        # The pre-import export between the baseline compile and the import start is no boundary of its own.
        between = {"state": "applying", "stages": ["recovery_archive_created", "hmi_baseline_compiled"]}
        self.assertEqual([], [point for point, (trigger, _) in q.TEXT_LIST_POINTS.items() if trigger(between)])
        for terminal in ({"state": "applied", "stages": ["changed_project_saved"]}, {"state": "prepared"}, {}):
            self.assertFalse(any(trigger(terminal) for trigger, _ in q.TEXT_LIST_POINTS.values()), terminal)

    def test_text_list_applied_stages_pass_every_boundary_once(self):
        stages = q.TEXT_LIST_APPLIED_STAGES
        hit = set()
        for n in range(len(stages) + 1):
            journal = {"state": "applying", "stages": stages[:n]}
            hit |= {p for p, (trigger, m) in q.TEXT_LIST_POINTS.items() if m == "worker" and trigger(journal)}
        self.assertEqual({"mut", "imp", "read", "comp", "save"}, hit)
        self.assertFalse(any(trigger({"state": "applied", "stages": stages}) for trigger, _ in q.TEXT_LIST_POINTS.values()))

    def test_text_list_scenarios_run_on_the_qualification_host(self):
        self.assertEqual(["--hmi-text-list-qualification", "unified-comfort"], q.text_list_host_arguments())

    def test_expected_outcomes(self):
        for kind in ("source_upsert", "hardware_plug", "to_create", "tag_set"):
            self.assertEqual({"released": False, "effectPersisted": False}, q.expected_outcome(kind, "ckpt"))
            for point in ("mut", "comp"):
                self.assertEqual({"released": True, "project": "unchanged", "effectPersisted": False}, q.expected_outcome(kind, point))
            self.assertEqual({"released": False, "effectPersisted": True}, q.expected_outcome(kind, "save"))
        self.assertEqual({"released": True, "project": "verifiedApplied", "effectPersisted": True}, q.expected_outcome("source_upsert", "host"))
        self.assertEqual({"released": True, "project": "verifiedApplied", "effectPersisted": True}, q.expected_outcome("hardware_plug", "host"))
        self.assertEqual({"released": False, "effectPersisted": True}, q.expected_outcome("to_create", "host"))
        # The text-list import is a target kind: Host loss at any boundary leaves the worker to finish and save; held for review.
        for point in ("mut", "imp", "read"):
            self.assertEqual({"released": True, "project": "unchanged", "effectPersisted": False}, q.expected_outcome("hmi_text_list_import", point))
        for point in ("hckp", "himp", "hred", "hsav"):
            self.assertEqual({"released": False, "effectPersisted": True}, q.expected_outcome("hmi_text_list_import", point))

    def test_scenario_names_stay_short(self):
        # The reconciliation retrieve root (base/scenario/ar_<12>) must stay within the worker's 70-character limit.
        self.assertEqual(25, len(q.KIND_SCENARIOS))
        self.assertTrue(all(len(name) <= 7 for name in q.KIND_SCENARIOS))
        self.assertFalse(set(q.KIND_SCENARIOS) & set(q.SCENARIOS))
        self.assertEqual(16, len(q.UPGRADE_SCENARIOS))
        self.assertTrue(all(len(name) <= 7 for name in q.UPGRADE_SCENARIOS))
        self.assertFalse(set(q.UPGRADE_SCENARIOS) & (set(q.SCENARIOS) | set(q.KIND_SCENARIOS)))


class UpgradeInterruptionTests(unittest.TestCase):
    def setUp(self):
        self.root = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.root)

    def stage(self, receipt=None, projects=(), admitted=True, destination="u_12345678"):
        if admitted:
            folder = self.root / "engineering-admissions" / ("a" * 32)
            folder.mkdir(parents=True, exist_ok=True)
            (folder / "00000000.json").write_text(json.dumps({"state": "dispatchIntent", "scope": {"operation": "project_upgrade"}}), encoding="utf-8")
        if destination:
            (self.root / "upgrades" / destination).mkdir(parents=True, exist_ok=True)
        if receipt:
            (self.root / "upgrades" / destination / "upgrade-receipt.json").write_text(json.dumps({"state": receipt}), encoding="utf-8")
        for project in projects:
            path = self.root / "upgrades" / destination / project
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("x", encoding="utf-8")

    def fired(self, mode):
        return {p for p, (trigger, m, _) in q.UPGRADE_POINTS.items() if trigger and (m == mode) and trigger(self.root)}

    def test_triggers_follow_the_durable_receipt(self):
        self.assertEqual(set(), self.fired("worker") | self.fired("host"))
        self.stage(destination=None)
        self.assertEqual(({"int"}, set()), (self.fired("worker"), self.fired("host")))
        self.assertEqual("absent", q.upgrade_state(self.root))
        self.stage()
        self.assertEqual("unreceipted", q.upgrade_state(self.root))
        for receipt, worker, host in (("copying", {"int", "cpy"}, {"hcpy"}), ("upgrading", {"int", "upg"}, {"hupg"})):
            self.stage(receipt)
            self.assertEqual((receipt, worker, host), (q.upgrade_state(self.root), self.fired("worker"), self.fired("host")))
        # TIA's project at the observed layout, before and after the completed receipt.
        self.stage("upgrading", ["Demo/Demo.ap15", "Demo/Logs/x.ap21", "Demo_V21/Demo_V21.ap21"])
        self.assertEqual({"int", "upg", "prj"}, self.fired("worker"))
        self.assertEqual(["Demo_V21.ap21"], [p.name for p in q.upgraded_projects(self.root)])
        self.stage("upgraded")
        self.assertEqual(("upgraded", {"int", "prj"}), (q.upgrade_state(self.root), self.fired("worker")))

    def test_archive_layout_and_a_second_destination(self):
        self.stage("upgraded", ["s/Demo_1.zap14", "w/Demo/Demo.ap14", "w/Demo_V21/Demo_V21.ap21"])
        self.assertEqual(["w/Demo_V21/Demo_V21.ap21"], [p.relative_to(self.root / "upgrades" / "u_12345678").as_posix() for p in q.upgraded_projects(self.root)])
        self.stage(destination="u_87654321", admitted=False)
        with self.assertRaises(AssertionError):
            q.upgrade_state(self.root)

    def test_outcomes_follow_the_state(self):
        self.assertEqual({"released": True, "destination": "unchanged"}, q.upgrade_outcome("absent"))
        self.assertEqual({"released": True, "destination": "verifiedApplied"}, q.upgrade_outcome("upgraded"))
        for state in ("unreceipted", "copying", "upgrading", None):
            self.assertEqual({"released": False, "destination": None}, q.upgrade_outcome(state))
        # Each boundary's accepted states: Host loss always ends in the worker's completed upgrade, the intent point in nothing.
        outcomes = {p: {q.upgrade_outcome(s)["destination"] for s in states} for p, (_, _, states) in q.UPGRADE_POINTS.items()}
        self.assertEqual({"int": {"unchanged", None}, "cpy": {None}, "upg": {None}, "prj": {None, "verifiedApplied"}, "hint": {"unchanged"},
                          "hcpy": {"verifiedApplied"}, "hupg": {"verifiedApplied"}, "hret": {"verifiedApplied"}}, outcomes)

    def test_host_point_wait_fails_fast_on_an_answer(self):
        class Client:
            def __init__(self, lines, code):
                self.lines, self.process = queue.Queue(), type("P", (), {"poll": lambda _: code})()
                for line in lines:
                    self.lines.put(line)
        self.assertFalse(q.host_stopped(Client(['{"jsonrpc":"2.0","method":"notifications/message"}'], None), 7))
        self.assertTrue(q.host_stopped(Client(['{"jsonrpc":"2.0","id":6,"result":{}}', None], 74), 7))
        with self.assertRaises(RuntimeError):
            q.host_stopped(Client(['{"jsonrpc":"2.0","id":7,"result":{"isError":true}}'], None), 7)

    def test_host_points_exist_and_examples_are_pinned(self):
        text = (ROOT / "src/TiaMcp.Host/HostOptions.cs").read_text(encoding="utf-8")
        accepted = set(re.findall(r'"([a-z_.A-Z]+)"', re.search(r"watchQualificationPoint is not \((.*?)\)\)", text, re.S).group(1)))
        modes = {mode for _, mode, _ in q.UPGRADE_POINTS.values()}
        self.assertEqual({"worker", "host", "host.project_upgrade", "host.project_upgrade.beforeDispatch"}, modes)
        self.assertLessEqual(modes - {"worker", "host"}, accepted)
        for name in q.UPGRADE_EXAMPLES.values():
            self.assertEqual({"version", "format", "project", "inventory"}, set(q.UPGRADE_PINS[name]))
        # base/<scenario>/ar_<12> stays within the reconciler's 70 characters and the upgrade within TIA's 143 for a short base.
        base = pathlib.Path(r"D:\Entwicklung\TIA_MCP-public\artifacts\pi")
        for name, (prefix, _) in q.UPGRADE_SCENARIOS.items():
            self.assertLessEqual(len(str(base / name / ("ar_" + "0" * 12))), 70)
            self.assertLessEqual(q.paths_fit(base / name, [q.UPGRADE_EXAMPLES[prefix]]), 143)


if __name__ == "__main__":
    unittest.main()
