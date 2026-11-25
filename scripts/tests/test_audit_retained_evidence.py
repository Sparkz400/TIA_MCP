"""Recovered receipts establish byte identity only, never renewed qualification."""
import copy
import hashlib
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import audit_retained_evidence as audit


class RetainedEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.digest = hashlib.sha256(b'original').hexdigest()
        self.binding = dict(schemaVersion='api-coverage-bindings.v1', nativeExecuted=False, releaseAccepted=False,
                            tools=[], mutations=[], counts=dict(tools=0, mutations=0, features=1), sourceInputs=[],
                            evidenceInputs=[dict(path='artifacts/proof.json', state='observed', sha256=self.digest)],
                            evidenceProfiles=[dict(id='receipt', profileSha256='a' * 64)],
                            features=[dict(featureId='fixture', tools=[], changeKind=None, sourceDependencies=[],
                                           schemaBindings=[], schemaReview='unresolved', reviewState='partial',
                                           releaseAccepted=False, qualificationPromoted=False, gaps=['fixture'],
                                           evidenceRefs=[dict(id='receipt', profileSha256='a' * 64,
                                                              artifacts=['artifacts/proof.json'])])])

    def put(self, data):
        path = self.root / 'artifacts/proof.json'
        path.parent.mkdir(exist_ok=True)
        path.write_bytes(data)

    def report(self):
        return audit.audit(self.binding, self.root, 'b' * 64)

    def test_missing_manifest_keeps_expected_hash_and_dependents(self):
        before = copy.deepcopy(self.binding)
        report = self.report()
        self.assertEqual({'missing': 1}, report['counts'])
        self.assertFalse(report['allRetainedInputsMatched'])
        row = report['inputs'][0]
        self.assertEqual(self.digest, row['expectedSha256'])
        self.assertEqual(['receipt'], row['evidenceIds'])
        self.assertEqual(['fixture'], row['featureIds'])
        self.assertEqual(before, self.binding)

    def test_recovery_matches_bytes_without_promoting_native_claims(self):
        self.put(b'original')
        report = self.report()
        self.assertTrue(report['allRetainedInputsMatched'])
        self.assertEqual({'matched': 1}, report['counts'])
        self.assertTrue(all(report[name] is False for name in ('nativeExecuted', 'qualificationPromoted', 'releaseAccepted')))
        self.assertEqual(b'original', (self.root / 'artifacts/proof.json').read_bytes())
        self.put(b'changed')
        self.assertEqual({'mismatch': 1}, self.report()['counts'])

    def test_unbound_bytes_and_directories_never_match(self):
        self.put(b'original')
        self.binding['evidenceInputs'][0]['sha256'] = None
        self.assertEqual({'unbound': 1}, self.report()['counts'])
        (self.root / 'artifacts/proof.json').unlink()
        (self.root / 'artifacts/proof.json').mkdir()
        self.assertEqual({'not-a-file': 1}, self.report()['counts'])

    def test_traversal_and_noncanonical_paths_are_not_read(self):
        for relative in ('../proof.json', 'artifacts/../proof.json', '/proof.json', 'C:/proof.json', 'artifacts//proof.json', 'artifacts\\proof.json'):
            self.binding['evidenceInputs'][0]['path'] = relative
            self.assertEqual({'unreadable-or-unsafe': 1}, self.report()['counts'])

    def test_junction_or_symlink_is_not_followed(self):
        target = self.root / 'outside'
        target.mkdir()
        (target / 'proof.json').write_bytes(b'original')
        link = self.root / 'artifacts'
        if sys.platform == 'win32':
            subprocess.run(['cmd', '/c', 'mklink', '/J', str(link), str(target)], check=True, capture_output=True)
            self.addCleanup(lambda: link.rmdir())
        else:
            link.symlink_to(target, target_is_directory=True)
            self.addCleanup(link.unlink)
        self.assertEqual({'unreadable-or-unsafe': 1}, self.report()['counts'])

    def test_file_and_total_read_limits(self):
        self.put(b'original')
        with patch.object(audit.inventory, 'MAX_FILE', 4):
            self.assertEqual({'over-budget': 1}, self.report()['counts'])
        with patch.object(audit.inventory, 'MAX_TOTAL', 4):
            self.assertEqual({'over-budget': 1}, self.report()['counts'])

    def test_duplicate_entries_and_malformed_digests_fail(self):
        self.binding['evidenceInputs'].append(copy.deepcopy(self.binding['evidenceInputs'][0]))
        with self.assertRaises(ValueError): self.report()
        self.binding['evidenceInputs'].pop()
        self.binding['evidenceInputs'][0]['sha256'] = 'not-a-hash'
        with self.assertRaises(ValueError): self.report()

    def test_cli_strict_missing_and_immutable_output(self):
        binding = self.root / 'binding.json'
        audit.inventory.write_new(binding, self.binding)
        output = self.root / 'report.json'
        command = [sys.executable, '-B', str(Path(audit.__file__)), '--binding', str(binding),
                   '--evidence-root', str(self.root), '--out', str(output), '--require-complete']
        result = subprocess.run(command, capture_output=True, timeout=30)
        self.assertEqual(1, result.returncode)
        saved = output.read_bytes()
        self.put(b'original')
        self.assertNotEqual(0, subprocess.run(command, capture_output=True, timeout=30).returncode)
        self.assertEqual(saved, output.read_bytes())
        output.unlink()
        self.assertEqual(0, subprocess.run(command, capture_output=True, timeout=30).returncode)
        for changed in (b'changed', None):
            output.unlink()
            if changed is not None:
                self.put(changed)
            else:
                (self.root / 'artifacts/proof.json').unlink()
                (self.root / 'artifacts/proof.json').mkdir()
            self.assertEqual(1, subprocess.run(command[:-1], capture_output=True, timeout=30).returncode)


if __name__ == '__main__':
    unittest.main()
