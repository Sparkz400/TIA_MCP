"""Admission ledger per contract from synthetic packets; nothing is installed and no native evidence is created."""
import json
from pathlib import Path
import tempfile
import unittest

import build_translation_admission as builder
from verify_watch_workflow import canonical_sha


class LedgerTests(unittest.TestCase):
    def packet(self, folder, contract, populated=True, kept=(5, 1, 0)):
        root = Path(folder)
        key = {'contract': contract, 'scope': {'categoryIds': ['BlockCommentCategoryData'] if contract == builder.RESTRICTED_CONTRACT else ['All']}}
        files = {'forward-instance-key.json': {'key': key, 'sha256': canonical_sha(key)},
                 'fixture-identity.json': {'projectPath': 'C:/scratch/Fixture.ap21', 'plcPath': 'PLC'},
                 'result.json': {}, 'admission.json': {}, 'exact-close.json': {},
                 'category-verification.json': {'populatedMainControl': populated, 'restricted': {'captures': [
                     {'category': c, 'keptRows': k, 'expectedRows': e} for (c, e), k in zip(builder_expected(), kept)]}},
                 'verification.json': {'completed': True, 'nativeExecuted': True, 'sameInstance': True, 'forwardKeySha256': canonical_sha(key),
                                       'pendingResources': 0, 'checks': [{'case': c} for c in ('before-dispatch', 'source-drift', 'import-reply',
                                                                                              'compile-reply', 'save-reply', 'reopen-reply', 'restore-reply')]},
                 'portable.json': {'failed': 0, 'passed': 181}, 'manifest.json': {}}
        for name, value in files.items(): (root / name).write_text(json.dumps(value), encoding='utf-8')
        return builder.build(root, root / 'verification.json', root / 'portable.json', root / 'manifest.json', 'synthetic-profile')

    def cases(self, registry): return [p['case'] for p in registry['entries'][0]['profile']['proofs'] if p['mode'] == 'native']

    def test_whole_project_ledger_is_unchanged(self):
        with tempfile.TemporaryDirectory() as folder:
            registry, _ = self.packet(folder, builder.CONTRACT)
            self.assertEqual(self.cases(registry), builder.NATIVE_CASES)

    def test_restricted_ledger_adds_the_category_filter_case(self):
        with tempfile.TemporaryDirectory() as folder:
            registry, files = self.packet(folder, builder.RESTRICTED_CONTRACT)
            self.assertEqual(self.cases(registry), builder.NATIVE_CASES + ['restricted-category-filter'])
            native = next(json.loads(data) for data in files.values() if json.loads(data)['kind'] == 'translation-admission-native-evidence')
            self.assertEqual(native['contract'], builder.RESTRICTED_CONTRACT)
            self.assertIn('categoryControls', native)

    def test_restricted_ledger_needs_conclusive_controls_and_exact_captures(self):
        for populated, kept in ((False, (5, 1, 0)), (True, (6, 1, 0))):
            with tempfile.TemporaryDirectory() as folder, self.assertRaisesRegex(ValueError, 'conclusive Main control'):
                self.packet(folder, builder.RESTRICTED_CONTRACT, populated, kept)


def builder_expected():
    return [('BlockCommentCategoryData', 5), ('HwcnCommentTextCategoryData', 1), ('AlarmClassDisplayNameCategory', 0)]


if __name__ == '__main__': unittest.main()
