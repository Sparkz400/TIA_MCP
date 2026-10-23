"""Offline synthetic coordinator admission tests; no process/native dispatch."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import qualify_translation_category_probe as coordinator


class FakeLocks:
    def __init__(self, fail_at=None): self.opened, self.closed, self.fail_at = [], [], fail_at
    def lock(self, path):
        if len(self.opened) == self.fail_at: raise OSError("synthetic lock unavailable")
        self.opened.append(str(path)); return len(self.opened)
    def close(self, handle): self.closed.append(handle); return True


class StoreWindowTests(unittest.TestCase):
    def test_retained_dispatch_result_is_not_a_second_dispatch(self):
        folder = self.stores[0] / 'engineering-operations' / ('c' * 32)
        folder.mkdir(parents=True)
        plan = {'resources': [{'kind': 'project', 'resourceKey': coordinator.resource_key('project', self.root / 'other.ap21')}]}
        previous = None
        for revision, state in enumerate(['prepared', 'acquired', 'checkpointIntent', 'ready', 'dispatchIntent', 'dispatchIntent']):
            record = {'id': folder.name, 'revision': revision, 'state': state, 'plan': plan,
                'planSha256': coordinator.canonical_sha(plan), 'previousSha256': None if previous is None else coordinator.canonical_sha(previous),
                'evidence': [] if revision < 5 else [{'gate': 'dispatch.rawResult', 'artifact': {'relativePath': 'raw', 'sha256': 'a' * 64}}],
                'checkpoints': [], 'recoveryDestinations': []}
            coordinator.save(folder / f'{revision:06d}.json', record); previous = record
        with self.enter() as held:
            self.assertEqual(held.evidence['stores'][1]['observations'][0]['state'], 'dispatchIntent')

    def test_dispatch_self_transition_without_appended_result_is_refused(self):
        folder = self.stores[0] / 'engineering-operations' / ('d' * 32)
        folder.mkdir(parents=True)
        plan = {'resources': [{'kind': 'project', 'resourceKey': coordinator.resource_key('project', self.root / 'other.ap21')}]}
        previous = None
        for revision, state in enumerate(['prepared', 'acquired', 'checkpointIntent', 'ready', 'dispatchIntent', 'dispatchIntent']):
            record = {'id': folder.name, 'revision': revision, 'state': state, 'plan': plan,
                'planSha256': coordinator.canonical_sha(plan), 'previousSha256': None if previous is None else coordinator.canonical_sha(previous),
                'evidence': [], 'checkpoints': [], 'recoveryDestinations': []}
            coordinator.save(folder / f'{revision:06d}.json', record); previous = record
        with self.assertRaisesRegex(ValueError, 'only append one'):
            with self.enter(): self.fail('No native body permitted')

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.project = self.root / "owned.ap21"; self.project.write_bytes(b"synthetic project identity only")
        self.stores = [self.root / "z-store", self.root / "a-store"]
        for root in self.stores: root.mkdir()
        self.prefix = self.root / "window"
        self.guard = self.root / "guard"

    def journal(self, *, state="acquired", resource=None, mutation=False):
        root = self.stores[0]
        folder = root / ("engineering-admissions" if mutation else "engineering-operations") / ("a" * 32)
        folder.mkdir(parents=True)
        resource = resource or {"resourceKey": coordinator.resource_key("project", self.project), "kind": "project"}
        plan = {"resources": [resource]}
        if mutation:
            scope = {"resources": [{"kind": resource["kind"], "path": resource["resourceKey"].split(":", 1)[1], "effect": "synthetic"}]}
            record = {"id": folder.name, "revision": 0, "state": state, "scope": scope, "resourceKeys": [resource["resourceKey"]], "previousSha256": None}
            width = 8
        else:
            record = {"id": folder.name, "revision": 0, "state": state, "plan": plan, "planSha256": coordinator.canonical_sha(plan), "previousSha256": None, "recoveryDestinations": []}
            width = 6
        stages = (["dispatchIntent"] if mutation else ["prepared"])
        if state not in stages:
            if mutation: stages += [state]
            elif state == "reconciled": stages += ["acquired", "checkpointIntent", "reconciled"]
            else: stages += [state]
        previous = None
        for revision, item in enumerate(stages):
            current = copy.deepcopy(record)
            current.update(state=item, revision=revision, previousSha256=None if previous is None else coordinator.canonical_sha(previous))
            coordinator.save(folder / f"{revision:0{width}d}.json", current)
            previous = current
        return folder, previous

    def enter(self, api=None):
        return coordinator.StoreReadWindow(self.stores, self.project, self.prefix, _api=api or FakeLocks(), _guard_root=self.guard)

    def test_sorted_locks_intent_before_body_reverse_release(self):
        api = FakeLocks()
        with self.enter(api) as window:
            self.assertTrue(Path(str(self.prefix) + "-intent.json").is_file())
            self.assertFalse(Path(str(self.prefix) + "-close.json").exists())
            self.assertTrue(window.evidence["readOnly"])
            self.assertEqual(Path(api.opened[0]), self.guard / "dispatch.lock")
            self.assertEqual(api.opened[1:], sorted(api.opened[1:], key=str.upper))
        self.assertEqual(api.closed, [3, 2, 1])
        self.assertTrue(coordinator.load(str(self.prefix) + "-close.json")["allClosed"])

    def test_unavailable_lock_releases_prior_handles_and_refuses_body(self):
        api = FakeLocks(fail_at=1)
        with self.assertRaisesRegex(OSError, "unavailable"):
            with self.enter(api): self.fail("body must not run")
        self.assertEqual(api.closed, [1])

    def test_pending_project_refused_without_modifying_journal(self):
        folder, _ = self.journal(); before = coordinator.sha(folder / "000000.json")
        with self.assertRaisesRegex(ValueError, "Unresolved overlapping"):
            with self.enter(): self.fail("body must not run")
        self.assertEqual(before, coordinator.sha(folder / "000000.json"))

    def test_pending_parent_workspace_refused(self):
        self.journal(resource={"kind": "workspace", "resourceKey": coordinator.resource_key("workspace", self.root)})
        with self.assertRaisesRegex(ValueError, "Unresolved overlapping"):
            with self.enter(): pass

    def test_prepared_operation_does_not_own_resource(self):
        self.journal(state="prepared")
        with self.enter() as window: self.assertFalse(window.evidence["stores"][1]["observations"][0]["pending"])

    def test_terminal_operation_does_not_own_resource(self):
        self.journal(state="reconciled")
        with self.enter(): pass

    def test_pending_unrelated_resource_is_retained_not_cleared(self):
        other = self.root / "other.ap21"; other.write_bytes(b"other")
        folder, _ = self.journal(resource={"kind": "project", "resourceKey": coordinator.resource_key("project", other)})
        with self.enter(): self.assertTrue(folder.exists())

    def test_unknown_state_fails_closed(self):
        self.journal(state="assumedComplete")
        with self.assertRaisesRegex(ValueError, "Invalid operation state"):
            with self.enter(): pass

    def test_changed_hash_chain_fails_closed(self):
        folder, original = self.journal(state="prepared")
        next_record = dict(original, revision=1, state="reconciled", previousSha256="0" * 64)
        coordinator.save(folder / "000001.json", next_record)
        with self.assertRaisesRegex(ValueError, "hash chain"):
            with self.enter(): pass

    def test_unknown_directory_cannot_hide_reservation(self):
        (self.stores[0] / "engineering-operations/not-a-valid-operation").mkdir(parents=True)
        with self.assertRaisesRegex(ValueError, "Unknown store directory"):
            with self.enter(): pass

    def test_pending_mutation_admission_refused(self):
        self.journal(state="outcomeUnknown", mutation=True)
        with self.assertRaisesRegex(ValueError, "Unresolved overlapping"):
            with self.enter(): pass

    def test_returned_mutation_admission_releases_reservation(self):
        self.journal(state="returned", mutation=True)
        with self.enter(): pass

    def test_recovery_destination_is_reserved(self):
        other = self.root / "other.ap21"; other.write_bytes(b"other")
        folder, original = self.journal(state="reconciled", resource={"kind": "project", "resourceKey": coordinator.resource_key("project", other)})
        # Construct a fresh unresolved operation whose admitted recovery destination covers the target.
        files = sorted(folder.glob("*.json"))
        original = coordinator.load(files[1])
        original.update(state="checkpointIntent", revision=2, previousSha256=coordinator.canonical_sha(original))
        (folder / "000002.json").write_text(json.dumps(original), encoding="utf-8")
        ready = dict(original, state="ready", revision=3, previousSha256=coordinator.canonical_sha(original))
        (folder / "000003.json").write_text(json.dumps(ready), encoding="utf-8")
        next_record = copy.deepcopy(original)
        next_record.update(revision=4, previousSha256=coordinator.canonical_sha(ready), state="recoveryIntent",
                           recoveryDestinations=[{"resourceKey": original["plan"]["resources"][0]["resourceKey"], "recoveryLocation": str(self.root)}])
        coordinator.save(folder / "000004.json", next_record)
        with self.assertRaisesRegex(ValueError, "Unresolved overlapping"):
            with self.enter(): pass

    def test_body_failure_keeps_journal_and_releases_handles(self):
        api = FakeLocks()
        with self.assertRaisesRegex(RuntimeError, "synthetic lost response"):
            with self.enter(api): raise RuntimeError("synthetic lost response")
        self.assertEqual(api.closed, [3, 2, 1])
        self.assertIn("lost response", coordinator.load(str(self.prefix) + "-close.json")["bodyFailure"])

    def test_mutation_window_is_honestly_labelled(self):
        resources = [{"kind": "project", "path": str(self.project), "effect": "explicit synthetic qualification mutation"}]
        with coordinator.StoreExclusiveWindow(self.stores, self.project, self.prefix, mode="isolatedQualificationMutation", resources=resources, effects="synthetic mutation scope only", _api=FakeLocks(), _guard_root=self.guard) as window:
            self.assertFalse(window.evidence["readOnly"])
            self.assertFalse(window.evidence["nativeQualified"])


class RecoveryGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve(); self.host = self.root / "host.exe"; self.worker = self.root / "worker.exe"
        self.host.write_bytes(b"synthetic host"); self.worker.write_bytes(b"synthetic worker")
        self.reruns, self.cases = {}, []
        for index, mode in enumerate(sorted(coordinator.RECOVERY_MODES)):
            root = self.root / str(index); root.mkdir()
            admission = {"mode": mode, "actualMcpHost": True, "nativeSequential": True, "automaticReplay": False,
                         "host": str(self.host), "hostSha256": coordinator.sha(self.host), "worker": str(self.worker), "workerSha256": coordinator.sha(self.worker)}
            coordinator.save(root / "admission.json", admission)
            value = {"kind": "independent-watch-expansion-verification", "root": str(root), "nativeInvoked": False, "completed": True,
                     "interruptionMatrixCasePassed": True, "hostRestartVerified": True, "issues": [], "coverageGaps": [],
                     "stages": [{"name": "synthetic verifier control", "passed": True}],
                     "interruption": {"actualHostLifecycle": {"hostCount": 2, "closedBeforeRestart": True, "exactWorkerLifetimes": 2}},
                     "recovery": {"restoredProjectVerified": mode in {"retrieve.after", "host.watch_restore_native"}}}
            coordinator.save(root / "verification.json", value); self.reruns[str(root)] = value
            self.cases.append({"root": str(root), "verificationPath": str(root / "verification.json"), "verificationSha256": coordinator.sha(root / "verification.json")})
        self.gate = self.root / "gate.json"

    def verify(self):
        self.gate.write_text(json.dumps({"cases": self.cases}), encoding="utf-8")
        with patch.object(coordinator.watch_oracle, "verify", side_effect=lambda root: self.reruns[str(root)]):
            return coordinator.verify_recovery_gate(self.gate, self.host, self.worker)

    def test_six_synthetic_raw_verified_controls_pass(self):
        result = self.verify()
        self.assertTrue(result["completed"]); self.assertFalse(result["nativeInvoked"]); self.assertEqual(len(result["cases"]), 6)

    def test_missing_case_refused(self):
        self.cases.pop()
        with self.assertRaisesRegex(ValueError, "exactly six"): self.verify()

    def test_duplicate_case_refused(self):
        self.cases[-1] = self.cases[0]
        with self.assertRaisesRegex(ValueError, "duplicate"): self.verify()

    def test_changed_independent_receipt_refused(self):
        self.cases[0]["verificationSha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "hash differs"): self.verify()

    def test_failed_raw_reverification_refused_despite_passed_receipt(self):
        self.reruns[self.cases[0]["root"]]["completed"] = False
        with self.assertRaisesRegex(ValueError, "no longer verifies"): self.verify()

    def test_same_host_process_restart_claim_refused(self):
        self.reruns[self.cases[0]["root"]]["interruption"]["actualHostLifecycle"]["closedBeforeRestart"] = False
        with self.assertRaisesRegex(ValueError, "restart/cleanup"): self.verify()

    def test_retrieval_requires_genuine_fresh_restored_verification(self):
        case = next(case for case in self.cases if coordinator.load(Path(case["root"]) / "admission.json")["mode"] == "retrieve.after")
        self.reruns[case["root"]]["recovery"]["restoredProjectVerified"] = False
        with self.assertRaisesRegex(ValueError, "fresh restored-project"): self.verify()

    def test_changed_worker_bytes_refused(self):
        self.worker.write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "different Host/worker"): self.verify()

    def test_no_native_process_on_prepare_only(self):
        with patch.object(coordinator, "preflight", return_value={"root": "synthetic", "resourceRoots": []}), patch.object(coordinator, "Client") as client, patch("sys.argv", ["test", "--root", "synthetic", "--recovery-gate", "synthetic"]):
            self.assertEqual(coordinator.main(), 0); client.assert_not_called()

    def test_failed_gate_cannot_start_host_even_with_execute(self):
        with patch.object(coordinator, "preflight", side_effect=ValueError("synthetic incomplete recovery gate")), patch.object(coordinator, "Client") as client, patch("sys.argv", ["test", "--root", "synthetic", "--recovery-gate", "synthetic", "--execute"]):
            self.assertEqual(coordinator.main(), 1); client.assert_not_called()


if __name__ == "__main__": unittest.main()
