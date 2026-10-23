"""Real closed file trees and synthetic process orchestration; no Siemens execution."""
import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

import encoder_copy_admission as admission
import test_encoder_lifecycle as lifecycle
import qualify_encoder_hardware as qualifier
import audit_encoder_preservation as auditor


class CopyAdmissionTests(unittest.TestCase):
    def setUp(self):
        self.harness = lifecycle.EncoderLifecycleTests('test_success_requires_all_three_exact_session_cleanups')
        self.harness.setUp()
        self.addCleanup(self.harness.doCleanups)

    def source(self):
        self.source_report = self.harness.run_qualifier()
        self.path = Path(self.source_report['projectPath'])
        self.sidecar = self.path.parent/'database.bin'
        self.digest = hashlib.sha256(self.harness.report_path.read_bytes()).hexdigest()
        return self.source_report

    def load(self):
        return admission.load_source(self.harness.report_path, self.digest)

    def test_complete_sources_are_snapshotted_after_exit_and_inspected_through_fresh_copies(self):
        source = self.source(); self.assertTrue(source['cleanupVerified'])
        self.assertEqual(3, source['closedCopies']['terminalSessionCount'])
        companion = self.harness.run_companion()
        self.harness.audit_companion(companion)
        self.assertEqual(2, len(companion['openAdmissions']))
        for label, row in companion['copies'].items():
            original = Path(row['sourceProjectPath']); copied = Path(row['projectPath'])
            self.assertNotEqual(original, copied)
            self.assertEqual(original.read_bytes(), copied.read_bytes())
            self.assertEqual(source['closedCopies']['copies'][label]['tree'], admission.snapshot(original)['tree'])
            self.assertTrue(companion['inspectionCopies'][label]['sourcePreserved'])
        self.assertFalse(companion['copyAdmission']['sourceAuthenticityVerified'])
        result = {}; auditor.audit(source, result)
        self.assertTrue(result['closedSourceSnapshotsVerified'])

    def test_changed_added_missing_files_and_empty_directories_refuse_before_startup(self):
        self.source(); baseline = self.sidecar.read_bytes()
        for defect in ('bytes', 'missing', 'extra', 'emptyDirectory'):
            extra = self.path.parent/'extra.bin'
            if defect == 'bytes': self.sidecar.write_bytes(b'changed')
            elif defect == 'missing': self.sidecar.unlink()
            elif defect == 'extra': extra.write_bytes(b'new')
            else: (self.path.parent/'empty').rmdir()
            count = len(self.harness.trees)
            with self.subTest(defect=defect), self.assertRaises(AssertionError): self.harness.run_companion()
            self.assertEqual(count, len(self.harness.trees))
            with self.assertRaises(AssertionError): auditor.audit(self.source_report, {})
            self.sidecar.write_bytes(baseline)
            if extra.exists(): extra.unlink()
            (self.path.parent/'empty').mkdir(exist_ok=True)
        self.load()

    def test_source_hash_completion_terminal_snapshot_and_checkpoint_are_required(self):
        source = self.source(); raw = self.harness.report_path.read_bytes()
        with self.assertRaisesRegex(AssertionError, 'hash differs'):
            admission.load_source(self.harness.report_path, '0'*64)
        for defect in ('incomplete', 'cleanup', 'missingSnapshot', 'snapshotBytes', 'archiveReceipt', 'overlap'):
            changed = copy.deepcopy(source)
            if defect == 'incomplete': changed['completed'] = False
            elif defect == 'cleanup': changed['cleanupVerified'] = False
            elif defect == 'missingSnapshot': changed.pop('closedCopies')
            elif defect == 'snapshotBytes': changed['closedCopies']['copies']['changed']['tree']['files'][0]['sha256'] = '0'*64
            elif defect == 'archiveReceipt': changed['edit']['applied']['recoveryArchiveSha256'] = '0'*64
            else: changed['recoveredProjectPath'] = changed['projectPath']
            self.harness.report_path.write_text(json.dumps(changed), encoding='utf-8')
            with self.subTest(defect=defect), self.assertRaises((AssertionError, RuntimeError)): self.harness.run_companion()
        self.harness.report_path.write_bytes(raw)
        archive = Path(source['edit']['applied']['recoveryArchive']); archive.write_bytes(b'replaced checkpoint')
        with self.assertRaisesRegex(AssertionError, 'checkpoint bytes changed'): self.harness.run_companion()

    def test_report_cannot_be_rehashed_after_change_to_bypass_expected_digest(self):
        self.source(); self.harness.report_path.write_bytes(b'{"changed":true}')
        with self.assertRaisesRegex(AssertionError, 'hash differs'): self.load()

    def test_changes_after_startup_and_before_retry_never_dispatch_an_unadmitted_open(self):
        self.source(); real = admission.before_open
        for target in ('source', 'copy', 'retry'):
            original = self.sidecar.read_bytes(); seen = []
            self.harness.fault = 'knownLock' if target == 'retry' else None
            start = len(self.harness.calls)
            def changed(packet, label, receipt, session):
                seen.append(label)
                if target != 'retry' or len(seen) == 2:
                    path = self.sidecar if target == 'source' else Path(receipt['copyProject'])
                    path.write_bytes(b'drift after startup')
                return real(packet, label, receipt, session)
            with patch.object(admission, 'before_open', side_effect=changed), self.assertRaises(AssertionError): self.harness.run_companion()
            opens = [name for _, name, _ in self.harness.calls[start:] if name == 'project_open']
            self.assertEqual(1 if target == 'retry' else 0, len(opens))
            report_path = self.harness.root/'companion.json'
            report = json.loads(report_path.read_text()); self.assertFalse(report['completed'])
            self.assertTrue(report['cleanupVerified'])
            self.sidecar.write_bytes(original); report_path.unlink()

    def test_copying_drift_refuses_before_host_start(self):
        self.source()
        from hmi_fixture_copy import shutil
        original = shutil.copyfile
        def changed(source, target):
            result = original(source, target); self.sidecar.write_bytes(b'changed during copying'); return result
        count = len(self.harness.trees)
        with patch('hmi_fixture_copy.shutil.copyfile', side_effect=changed), self.assertRaises(AssertionError): self.harness.run_companion()
        self.assertEqual(count, len(self.harness.trees))

    def test_native_copy_metadata_does_not_modify_retained_originals(self):
        self.source(); self.harness.fault = 'inspectionWrites'
        companion = self.harness.run_companion(); self.harness.audit_companion(companion)
        for row in companion['copies'].values():
            self.assertTrue((Path(row['projectPath']).parent/'runtime.tmp').is_file())
            self.assertFalse((Path(row['sourceProjectPath']).parent/'runtime.tmp').exists())

    def test_foreign_open_process_stops_before_native_readback(self):
        self.source(); self.harness.fault = 'openForeignProcess'; start = len(self.harness.calls)
        with self.assertRaisesRegex(AssertionError, 'another process'): self.harness.run_companion()
        self.assertNotIn('technology_read', [name for _, name, _ in self.harness.calls[start:]])

    def test_post_cleanup_snapshot_failure_cannot_publish_success(self):
        with patch.object(qualifier, 'capture_closed', side_effect=AssertionError('snapshot unavailable')):
            with self.assertRaisesRegex(AssertionError, 'snapshot unavailable'): self.harness.run_qualifier()
        report = json.loads(self.harness.report_path.read_text())
        self.assertFalse(report['completed']); self.assertTrue(report['cleanupVerified'])
        self.assertIn('postCleanupFailure', report); self.assertNotIn('closedCopies', report)

    def test_post_cleanup_source_drift_overrides_companion_success(self):
        self.source(); real = admission.finish_copies
        def changed(*args):
            self.sidecar.write_bytes(b'changed during cleanup'); return real(*args)
        with patch.object(admission, 'finish_copies', side_effect=changed), self.assertRaises(AssertionError): self.harness.run_companion()
        report = json.loads((self.harness.root/'companion.json').read_text())
        self.assertFalse(report['completed']); self.assertTrue(report['cleanupVerified']); self.assertIn('postCleanupFailure', report)

    def test_independent_audit_binds_file_receipts_to_each_original_open(self):
        self.source(); companion = self.harness.run_companion()
        for defect in ('missing', 'duplicate', 'callIndex', 'path', 'tree', 'source', 'preservation', 'reportHash'):
            changed = copy.deepcopy(companion)
            if defect == 'missing': changed['openAdmissions'].pop()
            elif defect == 'duplicate': changed['openAdmissions'].append(copy.deepcopy(changed['openAdmissions'][0]))
            elif defect == 'callIndex': changed['openAdmissions'][0]['callIndex'] += 1
            elif defect == 'path': changed['copies']['changed']['projectPath'] = changed['copies']['changed']['sourceProjectPath']
            elif defect == 'tree': changed['inspectionCopies']['changed']['copyBeforeOpen']['sha256'] = '0'*64
            elif defect == 'source': changed['copies']['changed']['sourceProjectPath'] = changed['copies']['recovered']['sourceProjectPath']
            elif defect == 'preservation': changed['inspectionCopies']['changed']['sourcePreserved'] = False
            else: changed['copyAdmission']['sourceReport']['sha256'] = '0'*64
            with self.subTest(defect=defect), self.assertRaises((AssertionError, RuntimeError)): self.harness.audit_companion(changed)

    def test_project_junction_is_rejected_without_touching_target(self):
        self.source(); target = self.harness.root/'junction-target'; target.mkdir()
        marker = target/'keep'; marker.write_bytes(b'keep')
        link = self.path.parent/'alias'
        if os.name == 'nt': subprocess.run(['cmd', '/c', 'mklink', '/J', str(link), str(target)], capture_output=True, check=True)
        else: link.symlink_to(target, target_is_directory=True)
        try:
            with self.assertRaisesRegex(AssertionError, 'reparse'): self.harness.run_companion()
        finally:
            if os.name == 'nt': link.rmdir()
            else: link.unlink()
        self.assertEqual(b'keep', marker.read_bytes())


if __name__ == '__main__':
    unittest.main()
