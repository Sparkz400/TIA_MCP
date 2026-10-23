import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import validate_library_observations as validation


class LibraryExportObservations(unittest.TestCase):
    def setUp(self):
        root = validation.ROOT / 'fixtures/libraries/observation-contracts'
        identity = json.loads((root / 'identity.json').read_bytes())
        version = json.loads((root / 'lock.json').read_bytes())['dependencies'][0]['versions'][0]
        del version['requiredVersionGuids']
        self.file = {'path': 'D:/Synthetic/Export/component.xml', 'relativePath': 'component.xml',
                     'bytes': 42, 'sha256': 'a' * 64, 'mediaType': 'application/xml'}
        self.export = {'schemaVersion': 1, 'libraryId': identity['libraryId'], 'revision': identity['revision'],
                       'directory': 'D:/Synthetic/Export', 'documentDirectory': 'D:/Synthetic/Export',
                       'documentName': 'component', 'sha256': 'b' * 64,
                       'manifest': {'path': 'D:/Synthetic/Export/export-manifest.v1.json', 'bytes': 300,
                                    'sha256': 'c' * 64, 'mediaType': 'application/json'},
                       'transferState': 'SyntheticUnknown', 'files': [self.file], 'filesTruncated': False}
        self.manifest = {'schemaVersion': 1, 'version': version, 'format': 'SyntheticXML',
                         'documentsSha256': 'd' * 64, 'files': [copy.deepcopy(self.file)]}

    def validate(self, value, kind='export'):
        return validation.validate_bytes(json.dumps(value).encode(), kind)

    def reject(self, value, kind='export'):
        with self.assertRaises(ValueError):
            self.validate(value, kind)

    def test_synthetic_export_and_manifest_never_grant_authority(self):
        for value, kind in ((self.export, 'export'), (self.manifest, 'export-manifest')):
            result = self.validate(value, kind)
            for flag in ('qualificationGranted', 'nativeExecuted', 'referencedFilesRead', 'previewHashRecomputed'):
                self.assertIs(result[flag], False)
            value['runtimeVerified'] = True
            self.reject(value, kind)

    def test_required_fields_and_unknown_fields(self):
        for original, kind in ((self.export, 'export'), (self.manifest, 'export-manifest')):
            for key in original:
                value = copy.deepcopy(original); del value[key]
                with self.subTest(kind=kind, key=key): self.reject(value, kind)
            value = copy.deepcopy(original); value['files'][0]['extra'] = True
            self.reject(value, kind)

    def test_file_bounds_pins_and_exact_types(self):
        for field, bad in (('bytes', -1), ('bytes', True), ('bytes', 2**63), ('sha256', 'a' * 64 + '\n'),
                           ('path', ''), ('mediaType', 'application/json')):
            value = copy.deepcopy(self.export); value['files'][0][field] = bad
            with self.subTest(field=field, bad=bad): self.reject(value)
        self.export['files'][0]['bytes'] = 2**63 - 1
        self.validate(self.export)

    def test_relative_paths_and_duplicate_files(self):
        for relative in ('../x.xml', './x.xml', '/x.xml', 'a//x.xml', 'a/../x.xml', 'a\\x.xml',
                         'C:/x.xml', 'x.xml\0', 'x/'):
            value = copy.deepcopy(self.export); value['files'][0]['relativePath'] = relative
            with self.subTest(relative=relative): self.reject(value)
        for original, kind in ((self.export, 'export'), (self.manifest, 'export-manifest')):
            for changed_field in (None, 'path', 'relativePath'):
                value = copy.deepcopy(original); duplicate = copy.deepcopy(value['files'][0])
                if changed_field: duplicate[changed_field] = 'other.xml'
                value['files'].append(duplicate)
                self.reject(value, kind)

    def test_directory_alias_and_manifest_exclusion(self):
        value = copy.deepcopy(self.export); value['directory'] += '/changed'; self.reject(value)
        value = copy.deepcopy(self.export); value['manifest']['path'] = self.file['path']; self.reject(value)
        value = copy.deepcopy(self.export); value['manifest']['relativePath'] = 'manifest.json'; self.reject(value)
        # Both hash scopes are retained independently; they are not interchangeable.
        self.assertNotEqual(self.export['sha256'], self.manifest['documentsSha256'])
        self.validate(self.export); self.validate(self.manifest, 'export-manifest')

    def test_truncation_requires_full_response_prefix_but_manifest_is_not_capped(self):
        files = [{**self.file, 'path': f'D:/Synthetic/{i}.xml', 'relativePath': f'{i}.xml'} for i in range(201)]
        self.export.update(files=files[:200], filesTruncated=True)
        self.validate(self.export)
        self.export['filesTruncated'] = False
        self.validate(self.export)  # exactly 200 can also be the complete set
        self.export['files'] = files; self.reject(self.export)
        self.export.update(files=files[:199], filesTruncated=True); self.reject(self.export)
        self.manifest['files'] = files; self.validate(self.manifest, 'export-manifest')

    def test_binary_unicode_and_case_preserving_file_observations(self):
        self.file.update(relativePath='Unterordner/Grüße.XML', path='D:/Synthetic/Unterordner/Grüße.XML')
        self.validate(self.export)
        self.file.update(relativePath='component.dat', mediaType='application/octet-stream')
        self.validate(self.export)
        self.file['mediaType'] = 'application/xml'; self.reject(self.export)

    def test_unknown_and_failed_transfer_states_are_observations(self):
        for state in ('Failed', 'Cancelled', 'SomeFutureVendorState'):
            self.export['transferState'] = state
            self.assertFalse(self.validate(self.export)['qualificationGranted'])

    def test_cli_preserves_original_bytes_and_rejects_changed_pin(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'export.json'
            raw = b'\xef\xbb\xbf' + json.dumps(self.export, ensure_ascii=False).encode()
            path.write_bytes(raw)
            command = [sys.executable, str(validation.ROOT / 'scripts/validate_library_observations.py'),
                       '--kind', 'export', '--path', str(path), '--expected-sha256', hashlib.sha256(raw).hexdigest()]
            run = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(0, run.returncode, run.stdout + run.stderr)
            self.assertEqual(raw, path.read_bytes())
            path.write_bytes(raw + b' ')
            self.assertEqual(1, subprocess.run(command, capture_output=True).returncode)


if __name__ == '__main__':
    unittest.main()
