"""Synthetic admission controls; never dispatch TIA."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import native_dispatch_guard as guard
from qualify_translation_category_probe import StoreReadWindow, StoreExclusiveWindow
from test_qualify_translation_category_probe import FakeLocks


class GuardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.registry = self.root / "registry"; self.registry.mkdir()
        self.source = self.root / "source.json"; self.source.write_text("{}")
        self.store = self.root / "store"; self.store.mkdir()
        self.project = self.root / "owned.ap21"; self.project.write_bytes(b"synthetic")
        self.ticket = guard.begin(self.registry, job_name="Local\\TiaMcp-Owned-synthetic", source=self.source)
        self.proof = self.root / "job.json"
        self.evidence = dict(schemaVersion="owned-job-terminal.v1", jobName="Local\\TiaMcp-Owned-synthetic",
            createdAtomicallyInJob=True, helperHandleSignaled=True, totalProcesses=5,
            activeProcesses=0, limitFlags=0, automaticTermination=False)

    def finish(self):
        guard.write(self.proof, self.evidence)
        guard.complete(self.ticket, self.proof)

    def test_unresolved_ticket_blocks_unrelated_store_before_body(self):
        locks = FakeLocks()
        with patch.object(guard, "DEFAULT_ROOT", self.registry):
            with self.assertRaisesRegex(ValueError, "Unresolved native dispatch quarantine"):
                with StoreReadWindow([self.store], self.project, self.root / "window", _api=locks):
                    self.fail("Unrelated store must not bypass unresolved cleanup")
        self.assertEqual(locks.closed, [1])
        self.assertFalse((self.root / "window-intent.json").exists())

    def test_exact_job_terminal_allows_next_window(self):
        self.finish()
        self.assertEqual(len(guard.scan(self.registry)), 1)

    def test_live_job_refuses_despite_terminal_helper(self):
        self.evidence["activeProcesses"] = 1
        self.finish()
        with self.assertRaisesRegex(ValueError, "exact retained job"): guard.scan(self.registry)

    def test_unsignaled_helper_refuses_despite_zero_active_count(self):
        self.evidence["helperHandleSignaled"] = False
        self.finish()
        with self.assertRaisesRegex(ValueError, "exact retained job"): guard.scan(self.registry)

    def test_breakaway_or_kill_flags_refuse(self):
        self.evidence["limitFlags"] = 0x800
        self.finish()
        with self.assertRaisesRegex(ValueError, "exact retained job"): guard.scan(self.registry)

    def test_changed_proof_refuses(self):
        self.finish()
        self.proof.write_text(json.dumps({**self.evidence, "totalProcesses": 6}))
        with self.assertRaisesRegex(ValueError, "Invalid native terminal"): guard.scan(self.registry)

    def test_unrelated_job_cannot_release_ticket(self):
        self.evidence["jobName"] += "-other"
        self.finish()
        with self.assertRaisesRegex(ValueError, "exact retained job"): guard.scan(self.registry)

    def test_legacy_missing_lifetime_remains_blocked(self):
        intent = self.ticket / "intent.json"
        data = guard.read(intent)
        data.update(captureIncomplete=True, nativeQualified=False, completeOwnedTreeExitVerified=False)
        intent.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, "quarantine"): guard.scan(self.registry)

    def test_failed_atomic_launch_with_empty_job_can_release(self):
        self.evidence.update(createdAtomicallyInJob=False, helperHandleSignaled=False,
                             totalProcesses=0, launchNeverCreated=True)
        self.finish()
        self.assertEqual(len(guard.scan(self.registry)), 1)

    def test_empty_unlaunched_job_needs_explicit_no_launch_evidence(self):
        self.evidence.update(createdAtomicallyInJob=False, helperHandleSignaled=False, totalProcesses=0)
        self.finish()
        with self.assertRaisesRegex(ValueError, "exact retained job"): guard.scan(self.registry)

    def test_same_name_new_job_proof_cannot_reconcile_legacy_gap(self):
        intent = self.ticket / "intent.json"
        data = guard.read(intent)
        data.update(containmentRequiredFromCreation=False, captureIncomplete=True, completeOwnedTreeExitVerified=False)
        intent.write_text(json.dumps(data))
        guard.write(self.proof, self.evidence)
        with self.assertRaisesRegex(ValueError, "Legacy uncontained"):
            guard.complete(self.ticket, self.proof)
        guard.write(self.ticket / "terminal.json", dict(
            schemaVersion="native-dispatch-terminal.v1", intent=guard.reference(intent),
            jobEvidence=guard.reference(self.proof), originalAttemptPromoted=False))
        with self.assertRaisesRegex(ValueError, "Legacy uncontained"):
            guard.scan(self.registry)

    def test_explicit_batch_shares_still_held_guard_without_reopening(self):
        self.finish()
        locks = FakeLocks()
        resources = [dict(kind="project", path=str(self.project), effect="synthetic")]
        other = self.root / "other"; other.mkdir()
        options = dict(mode="readOnlyProbe", resources=resources, effects="synthetic",
                       _api=locks, _guard_root=self.registry)
        with StoreExclusiveWindow([self.store], self.project, self.root / "first", **options) as owner:
            with StoreExclusiveWindow([other], self.project, self.root / "next", _guard_owner=owner, **options) as batch:
                self.assertTrue(batch.evidence["nativeDispatchGuard"]["sameRegistryLockHeldThroughDispatch"])
                self.assertEqual(sum(Path(p).name == "dispatch.lock" for p in locks.opened), 1)
                with self.assertRaisesRegex(ValueError, "batched window is held"):
                    owner._release()
                self.assertTrue(owner.entered)
        with self.assertRaisesRegex(ValueError, "still-held original"):
            with StoreExclusiveWindow([other], self.project, self.root / "closed-owner", _guard_owner=owner, **options):
                self.fail("Closed owner must not grant admission")


if __name__ == "__main__":
    unittest.main()
