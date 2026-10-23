"""Independent prior-boot admission negatives. Never starts TIA or clears tickets."""
import copy
from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
import time
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import native_dispatch_guard as guard
import native_dispatch_isolation as isolation
from qualify_translation_category_probe import StoreExclusiveWindow
from test_qualify_translation_category_probe import FakeLocks

REAL_OBSERVE_BOOT = isolation.observe_boot


def utc(value):
    return datetime.fromtimestamp(value, timezone.utc).isoformat()


class BootEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.boot_epoch = time.time() + 1000
        self.boot = dict(cimBootUtc=utc(self.boot_epoch), eventBootUtc=utc(self.boot_epoch),
            observedEpoch=self.boot_epoch + 500, observedStartEpoch=self.boot_epoch + 499,
            uptimeMilliseconds=500000, computer="LOCAL-PC", eventComputer="LOCAL-PC.example.test",
            eventProvider="Microsoft-Windows-Kernel-General", eventId=12, eventRecordId=100,
            bootType=0, bootTypeRecordId=101, bootTypeComputer="LOCAL-PC.example.test", bootTypeAtUtc=utc(self.boot_epoch + 1))

    def test_three_clocks_and_full_boot_agree(self):
        self.assertAlmostEqual(isolation.validate_boot(self.boot), self.boot_epoch, places=4)

    def test_every_required_field_missing_refuses(self):
        for key in self.boot:
            with self.subTest(key=key):
                altered = {k: v for k, v in self.boot.items() if k != key}
                with self.assertRaises(ValueError): isolation.validate_boot(altered)

    def test_clock_rollback_forward_or_hibernation_uptime_disagreement_refuses(self):
        for key, value in (("cimBootUtc", utc(self.boot_epoch - 100)),
                           ("eventBootUtc", utc(self.boot_epoch + 100)),
                           ("uptimeMilliseconds", 99999999), ("observedStartEpoch", self.boot_epoch + 600),
                           ("observedEpoch", float("nan")), ("uptimeMilliseconds", True)):
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                isolation.validate_boot({**self.boot, key: value})

    def test_missing_wrong_host_hybrid_boot_or_unrelated_events_refuse(self):
        for key, value in (("eventComputer", "REMOTE"), ("bootTypeComputer", "REMOTE"),
                           ("eventId", 13), ("eventProvider", "other"), ("bootType", 1),
                           ("bootType", 2), ("bootType", False), ("bootTypeRecordId", 99),
                           ("bootTypeAtUtc", utc(self.boot_epoch - 100)), ("cimBootUtc", "2026-10-21T03:00:00")):
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                isolation.validate_boot({**self.boot, key: value})


class IsolationTests(BootEvidenceTests):
    def setUp(self):
        super().setUp()
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.registry = self.root / "registry"; self.registry.mkdir()
        self.old = self.root / "old"; self.old.mkdir()
        self.archive = self.old / "configured.zap21"; self.archive.write_bytes(b"original configured archive")
        self.project = self.old / "p" / "old.ap21"
        self.report = self.old / "coordinator-reconciliation-final.json"
        self.references = []
        for name, value in {
            "failed-holder-release.json": dict(completed=True, originalAttemptPassed=False,
                uncapturedLifetimeGap=True, coordinatorForcedExit=True, coordinator=dict(created=time.time()-100)),
            "fixture-tree-hold.json": dict(anchor=dict(created=time.time()-99)),
            "result.json": dict(projectPath=str(self.project), archive=guard.reference(self.archive))}.items():
            self.references.append(guard.write(self.old / name, value))
        guard.write(self.report, dict(failedCoordinatorReleased=True, originalAttemptPassed=False,
            allCapturedOriginalTargetsExitedBeforeRelease=True, completeOwnedTreeExitVerified=False, nativeQualified=False,
            uncapturedObservedProcesses=[dict(pid=38560, name="Siemens.Automation.Diagnostics.Report.exe",
                originalHandleCaptured=False, originalLifetimeTerminalVerified=False)], sources=self.references))
        self.ticket = guard.begin(self.registry, job_name="Local\\TiaMcp-Owned-fixture", source=self.report, legacy_uncontained=True)
        self.pin = guard.reference(self.ticket / "intent.json")["sha256"]
        self.report_pin = guard.reference(self.report)["sha256"]
        for name, value in (("LEGACY_INTENT_SHA256", self.pin), ("LEGACY_REPORT_SHA256", self.report_pin)):
            patcher = patch.object(isolation, name, value); patcher.start(); self.addCleanup(patcher.stop)
        patcher = patch.object(isolation, "observe_boot", side_effect=lambda: copy.deepcopy(self.boot))
        self.observer = patcher.start(); self.addCleanup(patcher.stop)
        self.workspace = self.root / "new-owned" / "native"
        self.workspace.parent.mkdir()
        self.policy = isolation.RebootIsolation(self.workspace)

    def admit(self):
        return guard.scan(self.registry, isolation=self.policy)

    def test_exact_prior_boot_separation_leaves_original_unknown_and_bytes_unchanged(self):
        paths = [self.ticket / "intent.json", self.report, self.archive, *(Path(r["path"]) for r in self.references)]
        original = {path: path.read_bytes() for path in paths}
        receipt, = self.admit()
        self.assertFalse(receipt["historicalCleanupVerified"])
        self.assertFalse(receipt["originalAttemptPromoted"])
        self.assertFalse(receipt["legacyTicketModified"])
        self.assertTrue(receipt["requiresAtomicOwnedJob"])
        self.assertFalse((self.ticket / "terminal.json").exists())
        self.assertEqual(original, {path: path.read_bytes() for path in paths})
        with self.assertRaisesRegex(ValueError, "quarantine"): guard.scan(self.registry)

    def test_missing_any_original_source_refuses(self):
        for ref in self.references:
            path = Path(ref["path"]); saved = path.read_bytes(); path.unlink()
            try:
                with self.assertRaises((ValueError, OSError)): self.admit()
            finally: path.write_bytes(saved)

    def test_changed_exact_source_refuses_even_if_new_content_is_valid_json(self):
        Path(self.references[0]["path"]).write_text('{}')
        with self.assertRaisesRegex(ValueError, "Historical source"): self.admit()

    def test_honestly_rehashed_report_and_intent_cannot_replace_reviewed_fingerprints(self):
        value = guard.read(self.report); value["unrelated"] = True
        self.report.write_text(json.dumps(value))
        intent = guard.read(self.ticket / "intent.json"); intent["source"] = guard.reference(self.report)
        (self.ticket / "intent.json").write_text(json.dumps(intent))
        with self.assertRaisesRegex(ValueError, "exact reviewed"): self.admit()

    def test_current_or_unknown_ticket_blocks_even_after_old_ticket_is_separated(self):
        guard.begin(self.registry, job_name="Local\\TiaMcp-Owned-current", source=self.report)
        with self.assertRaisesRegex(ValueError, "exact reviewed"): self.admit()

    def test_existing_workspace_or_parent_overlap_refuses(self):
        self.workspace.mkdir()
        with self.assertRaisesRegex(ValueError, "must be new"): self.admit()
        self.workspace.rmdir()
        self.policy = isolation.RebootIsolation(self.old / "new")
        with self.assertRaisesRegex(ValueError, "overlaps"): self.admit()

    def test_changed_archive_refuses(self):
        self.archive.write_bytes(b"different")
        with self.assertRaisesRegex(ValueError, "archive changed"): self.admit()

    def test_postboot_original_evidence_cannot_be_disposed_by_clock_claim(self):
        self.boot_epoch = time.time() - 1000
        self.boot.update(cimBootUtc=utc(self.boot_epoch), eventBootUtc=utc(self.boot_epoch),
            observedEpoch=self.boot_epoch + 500, observedStartEpoch=self.boot_epoch + 499,
            bootTypeAtUtc=utc(self.boot_epoch + 1))
        with self.assertRaisesRegex(ValueError, "strictly before"): self.admit()

    def test_boot_rechecked_and_boot_change_refuses_before_another_dispatch(self):
        self.admit()
        self.boot["eventRecordId"] = 102; self.boot["bootTypeRecordId"] = 103
        with self.assertRaisesRegex(ValueError, "boot changed"): self.admit()
        self.assertEqual(self.observer.call_count, 2)

    def test_duck_typed_policy_refuses(self):
        class Arbitrary:
            def admit(self, path): return {"admitted": True}
        with self.assertRaisesRegex(ValueError, "Exact fixture"): guard.scan(self.registry, isolation=Arbitrary())

    def test_original_guard_and_store_ownership_are_still_required(self):
        locks = FakeLocks()
        store = self.root / "store"; store.mkdir()
        resources = [dict(kind="project", path=str(self.project), effect="Read provenance"),
                     dict(kind="workspace", path=str(self.workspace), effect="Fresh owned copy")]
        options = dict(mode="isolatedQualificationMutation", resources=resources, effects="Fixture only",
                       isolation=self.policy, _api=locks, _guard_root=self.registry)
        with StoreExclusiveWindow([store], self.project, self.root / "window", **options) as window:
            self.assertTrue(window.entered)
            self.assertEqual(len(locks.opened), 2)
            self.assertFalse(locks.closed)
        self.assertEqual(len(locks.closed), 2)

    def test_unrelated_workspace_or_read_only_window_cannot_use_exception(self):
        store = self.root / "store"; store.mkdir()
        resources = [dict(kind="project", path=str(self.project), effect="Read provenance")]
        with self.assertRaisesRegex(ValueError, "explicit fresh fixture"):
            StoreExclusiveWindow([store], self.project, self.root / "window", mode="readOnlyProbe",
                resources=resources, effects="Read", isolation=self.policy)

    def test_reparse_ancestor_refuses_even_without_junction_helper(self):
        original = Path.lstat
        def attributes(path):
            return SimpleNamespace(st_file_attributes=0x400) if path == self.workspace.parent else original(path)
        # Neither is_symlink nor is_junction is needed for the attribute check.
        with patch.object(Path, 'is_symlink', return_value=False), patch.object(Path, 'lstat', attributes):
            with self.assertRaisesRegex(ValueError, 'reparse ancestry'): self.policy.validate_workspace()

    def test_network_and_unc_workspace_refuse_before_path_inspection(self):
        from pathlib import PureWindowsPath
        with patch.object(isolation.os, 'name', 'nt'), patch.object(isolation, 'local_volume_type', return_value=4):
            for path in [PureWindowsPath('Z:/new/native'), PureWindowsPath('//server/share/new/native')]:
                with self.subTest(path=path), self.assertRaisesRegex(ValueError, 'local fixed volume'):
                    isolation.require_local_workspace(path)

    def test_collector_cannot_coerce_missing_boottype_to_zero(self):
        import inspect
        source = inspect.getsource(REAL_OBSERVE_BOOT)
        self.assertIn("$bootTypes.Count -ne 1", source)
        self.assertIn("-cnotmatch '^[0-9]+$'", source)
        self.assertLess(source.index("Missing or ambiguous exact BootType"), source.index("bootType=[int]$bootType"))


if __name__ == "__main__":
    unittest.main()
