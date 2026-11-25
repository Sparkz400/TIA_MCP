"""Exact startup-failure isolation; no native processes are launched."""
import copy
from pathlib import Path
import tempfile
import time
from unittest.mock import patch

import native_dispatch_guard as guard
import native_dispatch_isolation as isolation
import native_startup_isolation as startup
from test_native_dispatch_isolation import BootEvidenceTests, utc


class StartupIsolationTests(BootEvidenceTests):
    def setUp(self):
        super().setUp()
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.old = self.root / 'old'; self.old.mkdir()
        (self.old / 'native').mkdir()
        self.registry = self.root / 'registry'; self.registry.mkdir()
        self.workspace = self.root / 'new' / 'native'
        self.workspace.parent.mkdir()
        self.refs = []
        for name in ('configured.json', 'terminal.json', 'archive.zap21', 'window.json',
                     'author.json', 'receipt.json', 'candidate.yml', 'helper.exe'):
            path = self.old / name
            path.write_bytes(b'original test input')
            self.refs.append(guard.reference(path))
        helper = self.refs[-1]
        admission = dict(zip(('configuredResult', 'configuredTerminal', 'archive',
                             'storeWindowIntent', 'authorIntent', 'authorReceipt'), self.refs[:6]))
        admission.update(candidateFiles=[self.refs[6]], artifactRoot=str(self.old),
                         outputDirectory=str(self.old / 'native'), helperSha256=helper['sha256'])
        anchor = dict(created=time.time()-100, exe=helper['path'])
        for name, value in {
            'admission.json': admission,
            'native/result.json': dict(completed=False, stages=[], productionMutationEnabled=False,
                failure=dict(type='Siemens.Engineering.EngineeringSecurityException')),
            'fixture-tree-hold.json': dict(anchor=anchor, dependentDispatchProhibited=True),
            'cleanup-live.json': dict(coordinator=dict(created=time.time()-110),
                retained=[dict(identity=anchor)], automaticTermination=False),
            'startup-failure-observation-01.json': dict(terminalProof=False),
        }.items():
            self.refs.append(guard.write(self.old / name, value))
        self.ticket = guard.begin(self.registry, job_name='Local\\TiaMcp-Owned-startup',
                                  source=self.old / 'admission.json')
        self.intent = self.ticket / 'intent.json'
        pins = {Path(ref['path']).relative_to(self.old).as_posix(): ref['sha256'] for ref in self.refs}
        for module, name, value in (
                (startup, 'STARTUP_INTENT_SHA256', guard.reference(self.intent)['sha256']),
                (startup, 'STARTUP_FILES', pins)):
            patcher = patch.object(module, name, value); patcher.start(); self.addCleanup(patcher.stop)
        patcher = patch.object(isolation, 'observe_boot', side_effect=lambda: copy.deepcopy(self.boot))
        self.observer = patcher.start(); self.addCleanup(patcher.stop)
        self.policy = isolation.RebootIsolation(self.workspace)

    def admit(self):
        return guard.scan(self.registry, isolation=self.policy)

    def test_exact_startup_keeps_original_unknown_and_default_quarantine(self):
        original = {Path(r['path']): Path(r['path']).read_bytes() for r in self.refs}
        original[self.intent] = self.intent.read_bytes()
        receipt, = self.admit()
        for key in ('historicalCleanupVerified', 'originalAttemptPromoted', 'legacyTicketModified', 'automaticReplay'):
            self.assertIs(receipt[key], False)
        self.assertIs(receipt['requiresAtomicOwnedJob'], True)
        self.assertFalse((self.ticket / 'terminal.json').exists())
        self.assertFalse(self.workspace.exists())
        self.assertEqual(original, {p: p.read_bytes() for p in original})
        with self.assertRaisesRegex(ValueError, 'quarantine'): guard.scan(self.registry)

    def test_each_missing_or_changed_original_refuses(self):
        for ref in self.refs:
            path = Path(ref['path']); data = path.read_bytes()
            for changed in (False, True):
                with self.subTest(path=path.name, changed=changed):
                    try:
                        if changed: path.write_bytes(data + b' ')
                        else: path.unlink()
                        with self.assertRaises((ValueError, OSError)): self.admit()
                    finally: path.write_bytes(data)

    def test_honestly_rehashed_replacement_is_not_reviewed(self):
        path = self.old / 'admission.json'
        data = guard.read(path); data['replacement'] = True
        path.unlink(); guard.write(path, data)
        intent = guard.read(self.intent); intent['source'] = guard.reference(path)
        self.intent.unlink(); guard.write(self.intent, intent)
        with self.assertRaisesRegex(ValueError, 'exact reviewed'): self.admit()

    def test_unknown_ticket_still_blocks(self):
        guard.begin(self.registry, job_name='Local\\TiaMcp-Owned-new', source=self.old / 'admission.json')
        with self.assertRaisesRegex(ValueError, 'exact reviewed'): self.admit()

    def test_existing_or_overlapping_workspace_refuses(self):
        self.workspace.mkdir()
        with self.assertRaisesRegex(ValueError, 'must be new'): self.admit()
        self.workspace.rmdir()
        self.policy = isolation.RebootIsolation(self.old / 'different')
        with self.assertRaisesRegex(ValueError, 'overlaps'): self.admit()

    def test_postboot_files_refuse(self):
        boot = time.time()-1000
        self.boot.update(cimBootUtc=utc(boot), eventBootUtc=utc(boot), bootTypeAtUtc=utc(boot+1),
                         observedEpoch=boot+500, observedStartEpoch=boot+499)
        with self.assertRaisesRegex(ValueError, 'strictly before'): self.admit()

    def test_missing_hybrid_or_drifting_boot_refuses(self):
        original = copy.deepcopy(self.boot)
        for key in self.boot:
            self.boot = {k: v for k, v in original.items() if k != key}
            with self.subTest(key=key), self.assertRaises(ValueError): self.admit()
        self.boot = dict(original, bootType=1)
        with self.assertRaises(ValueError): self.admit()
        self.boot = original
        self.admit()
        self.boot.update(eventRecordId=102, bootTypeRecordId=103)
        with self.assertRaisesRegex(ValueError, 'boot changed'): self.admit()


if __name__ == '__main__':
    import unittest
    unittest.main()
