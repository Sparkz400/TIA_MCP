"""Admission ledgers per contract, provisional binding and finalization from synthetic packets; no native evidence is created."""
import json
from pathlib import Path
import tempfile
import unittest

import build_translation_admission as builder
from qualify_translation_category_probe import ref
from verify_watch_workflow import canonical_sha

EXPECTED = [('BlockCommentCategoryData', 5), ('HwcnCommentTextCategoryData', 1), ('AlarmClassDisplayNameCategory', 0)]


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True); path.write_text(json.dumps(value), encoding='utf-8')


class LedgerTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(); self.root = Path(self.folder.name)
        self.deployment = (self.root / 'deploy' / 'host', self.root / 'deploy' / 'worker')

    def tearDown(self): self.folder.cleanup()

    def packet(self, contract, populated=True, kept=(5, 1, 0)):
        root = self.root / 'si'
        key = {'contract': contract, 'scope': {'categoryIds': ['BlockCommentCategoryData'] if contract == builder.RESTRICTED_CONTRACT else ['All']}}
        files = {'forward-instance-key.json': {'key': key, 'sha256': canonical_sha(key)},
                 'fixture-identity.json': {'projectPath': 'C:/scratch/Fixture.ap21', 'plcPath': 'PLC'},
                 'result.json': {}, 'admission.json': {}, 'exact-close.json': {},
                 'category-verification.json': {'populatedMainControl': populated, 'restricted': {'captures': [
                     {'category': c, 'keptRows': k, 'expectedRows': e} for (c, e), k in zip(EXPECTED, kept)]}},
                 'verification.json': {'completed': True, 'nativeExecuted': True, 'sameInstance': True, 'forwardKeySha256': canonical_sha(key),
                                       'pendingResources': 0, 'checks': [{'case': c} for c in ('before-dispatch', 'source-drift', 'import-reply',
                                                                                              'compile-reply', 'save-reply', 'reopen-reply', 'restore-reply')]},
                 'portable.json': {'failed': 0, 'passed': 183}, 'manifest.json': {}}
        for name, value in files.items(): write(root / name, value)
        return builder.build(root, root / 'verification.json', root / 'portable.json', root / 'manifest.json', 'synthetic-profile', self.deployment)

    def provisional(self):
        registry, files = self.packet(builder.RESTRICTED_CONTRACT)
        builder.install(self.deployment[0], registry, files)
        return self.deployment[0] / 'translation-admission' / 'registry.json', registry['entries'][0]

    def default_packet(self, registry, entry, **change):
        """A qualify_translation_native.py result that ran the three interruptions and the clean lifecycle on that registry."""
        root = self.root / 'p1'
        cases = [{'case': c, 'operationId': 'op-' + str(i), 'originalDisposition': 'unchanged', 'replayRefusal': 'RecoveryRequired'}
                 for i, c in enumerate(builder.INTERRUPTIONS)]
        result = {'completed': True, 'qualificationMode': False, 'automaticReplay': False, 'interruptions': cases,
                  'refusals': {name: 'Refused' for name in builder.LIFECYCLE_REFUSALS}, 'operation': {'id': 'op-clean', 'appliedRevision': 12, 'finalRevision': 16}}
        packages = {'host': [{'path': str(self.deployment[0] / 'TiaMcp.Host.exe')}], 'worker': [{'path': str(self.deployment[1] / 'TiaMcp.Openness.V21.exe')}]}
        admission = {'kind': 'translation-native-default-mode', 'qualificationMode': False, 'profileId': entry['id'], 'registry': ref(registry),
                     'contract': builder.RESTRICTED_CONTRACT, 'scope': entry['profile']['key']['scope'], 'packages': packages}
        for name, value in change.items():
            target, field = name.split('__'); {'result': result, 'admission': admission}[target][field] = value
        write(root / 'result.json', result); write(root / 'admission.json', admission)
        write(root / 'exact-close.json', {'hostExitCode': 0, 'closeInterrupted': None, 'tree': {'completed': True}})
        for op in ['op-clean'] + [c['operationId'] for c in cases]: write(root / 'engineering-operations' / op / '0000.json', {'state': 'reconciled'})
        return root

    def cases(self, registry): return [p['case'] for p in registry['entries'][0]['profile']['proofs'] if p['mode'] == 'native']

    def test_whole_project_ledger_is_unchanged(self):
        registry, _ = self.packet(builder.CONTRACT)
        self.assertEqual(self.cases(registry), builder.NATIVE_CASES)
        self.assertNotIn('qualification', registry['entries'][0])

    def test_restricted_entry_from_the_first_packet_is_provisional_and_bound(self):
        registry, files = self.packet(builder.RESTRICTED_CONTRACT)
        entry = registry['entries'][0]
        self.assertEqual(self.cases(registry), builder.NATIVE_CASES + ['restricted-category-filter'])
        self.assertEqual(entry['qualification'], {'hostDirectory': str(self.deployment[0].resolve()), 'workerDirectory': str(self.deployment[1].resolve())})
        native = next(json.loads(data) for data in files.values() if json.loads(data)['kind'] == 'translation-admission-native-evidence')
        self.assertIn('categoryControls', native)

    def test_restricted_ledger_needs_conclusive_controls_and_exact_captures(self):
        for populated, kept in ((False, (5, 1, 0)), (True, (6, 1, 0))):
            with self.assertRaisesRegex(ValueError, 'conclusive Main control'): self.packet(builder.RESTRICTED_CONTRACT, populated, kept)

    def test_finalize_adds_the_default_mode_packet_and_drops_the_binding(self):
        path, entry = self.provisional()
        registry, files = builder.finalize(path, self.default_packet(path, entry))
        final = registry['entries'][0]
        self.assertNotIn('qualification', final)
        self.assertEqual(self.cases(registry), builder.NATIVE_CASES + ['restricted-category-filter'] + builder.DEFAULT_MODE_CASES)
        run2 = {p['evidenceSha256'] for p in final['profile']['proofs'] if p['case'] in builder.DEFAULT_MODE_CASES}
        self.assertEqual(len(run2), 1)
        self.assertEqual(json.loads(files[run2.pop()])['kind'], 'translation-admission-default-mode-evidence')
        self.assertEqual(sorted(e['sha256'] for e in final['evidence']), sorted(files))

    def test_finalize_refuses_a_packet_that_is_not_the_bound_default_mode_run(self):
        path, entry = self.provisional()
        for change in ({'admission__registry': {'sha256': '0' * 64}}, {'admission__qualificationMode': True},
                       {'admission__packages': {'host': [{'path': str(self.root / 'elsewhere' / 'TiaMcp.Host.exe')}], 'worker': []}},
                       {'result__interruptions': []}, {'result__refusals': {}}):
            with self.subTest(change=list(change)), self.assertRaises(ValueError):
                builder.finalize(path, self.default_packet(path, entry, **change))
        bad = self.default_packet(path, entry)
        cases = json.loads((bad / 'result.json').read_text(encoding='utf-8')); cases['interruptions'][0]['originalDisposition'] = 'verifiedApplied'
        write(bad / 'result.json', cases)
        with self.assertRaisesRegex(ValueError, 'Interruption cases'): builder.finalize(path, bad)

    def test_only_a_provisional_restricted_entry_can_be_finalized(self):
        path, entry = self.provisional()
        packet = self.default_packet(path, entry)
        document = json.loads(path.read_text(encoding='utf-8')); del document['entries'][0]['qualification']
        final = self.root / 'final' / 'registry.json'; write(final, document)
        with self.assertRaisesRegex(ValueError, 'Only a provisional'): builder.finalize(final, packet)


if __name__ == '__main__': unittest.main()
