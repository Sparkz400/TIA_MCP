import copy
import json
import unittest

import validate_library_observations as validation


class LibraryInspectionObservations(unittest.TestCase):
    def setUp(self):
        identity = json.loads((validation.ROOT / 'fixtures/libraries/observation-contracts/identity.json').read_bytes())
        self.value = {name: identity[name] for name in ('origin', 'observedAtUtc', 'processId', 'libraryId', 'revision')}
        self.value.update(query='versions', items=[], totalCaptured=0, nextOffset=None,
                          completeness={'scopeComplete': True, 'truncated': False, 'scope': 'Synthetic fixture'})
        version = json.loads((validation.ROOT / 'fixtures/libraries/observation-contracts/lock.json').read_bytes())['dependencies'][0]['versions'][0]
        self.version = copy.deepcopy(version)
        del self.version['requiredVersionGuids']

    def validate(self, value):
        return validation.validate_bytes(json.dumps(value).encode(), 'inspection')

    def reject(self, value):
        with self.assertRaises(ValueError): self.validate(value)

    def test_empty_queries_and_no_authority(self):
        for query in ('elements', 'versions', 'dependencies', 'uses'):
            self.value['query'] = query
            self.assertFalse(self.validate(self.value)['qualificationGranted'])

    def test_page_counts_and_offsets(self):
        self.value.update(items=[self.version], totalCaptured=3, nextOffset=1)
        self.validate(self.value)
        for field, bad in (('totalCaptured', 0), ('nextOffset', 0), ('nextOffset', 3), ('items', [])):
            value = copy.deepcopy(self.value); value[field] = bad; self.reject(value)
        self.value.update(items=[], totalCaptured=3, nextOffset=None)
        self.validate(self.value)  # offset beyond captured rows is a valid empty final page

    def test_query_specific_rows(self):
        self.value.update(items=[self.version], totalCaptured=1)
        self.validate(self.value)
        for query in ('elements', 'dependencies', 'uses'):
            value = copy.deepcopy(self.value); value['query'] = query; self.reject(value)
        self.value['query'] = 'dependencies'
        self.value['items'][0]['requiredVersionGuids'] = []
        self.validate(self.value)
        self.value['totalCaptured'] = 1001; self.reject(self.value)

    def test_completeness_and_version_sentinel(self):
        for complete, truncated in ((True, True), (False, False)):
            value = copy.deepcopy(self.value)
            value['completeness'].update(scopeComplete=complete, truncated=truncated)
            self.reject(value)
        self.value['totalCaptured'] = 10001
        self.reject(self.value)
        self.value['completeness'].update(scopeComplete=False, truncated=True)
        self.validate(self.value)

    def test_bounds_and_exact_identity(self):
        for field, bad in (('libraryId', self.value['libraryId'] + '\n'), ('revision', 'a' * 64 + '\n'),
                           ('processId', True), ('query', 'export'), ('items', [self.version] * 201), ('totalCaptured', 10002)):
            value = copy.deepcopy(self.value); value[field] = bad; self.reject(value)

    def test_element_rows_preserve_each_native_shape(self):
        rows = [
            {'kind': kind, 'path': '/' + kind, 'name': 'Synthetic'}
            for kind in ('typeFolder', 'masterCopyFolder')
        ]
        rows += [{**self.version, 'path': '/types/Synthetic/versions/' + self.version['versionGuid']},
                 {'kind': 'masterCopy', 'masterCopyPath': '/masterCopies/Synthetic', 'name': 'Synthetic',
                  'author': None, 'creationDate': '2026-10-23T00:00:00',
                  'contents': [{'name': 'Block', 'type': 'SyntheticBlock'}]},
                 {'kind': 'type', 'path': '/types/Synthetic', 'name': 'Synthetic',
                  'typeGuid': self.version['typeGuid'], 'engineeringType': 'SyntheticType',
                  'doNotUse': False, 'setForUpdate': False, 'status': 'Synthetic',
                  'documentExportAvailable': True, 'supportedExportFormats': ['XML'],
                  'documentExportLimitation': None}]
        self.value.update(query='elements', items=rows, totalCaptured=len(rows))
        self.validate(self.value)
        for index, row in enumerate(rows):
            for field in row:
                value = copy.deepcopy(self.value); del value['items'][index][field]
                with self.subTest(kind=row['kind'], field=field): self.reject(value)

    def test_export_availability_cannot_contradict_native_scope(self):
        row = {'kind': 'type', 'path': '/types/Synthetic', 'name': 'Synthetic',
               'typeGuid': self.version['typeGuid'], 'engineeringType': 'SyntheticType',
               'doNotUse': False, 'setForUpdate': False, 'status': 'Synthetic',
               'documentExportAvailable': False, 'supportedExportFormats': None,
               'documentExportLimitation': 'No global library document export'}
        self.value.update(query='elements', items=[row], totalCaptured=1)
        self.validate(self.value)
        for field, bad in (('documentExportAvailable', True), ('supportedExportFormats', []),
                           ('documentExportLimitation', None)):
            value = copy.deepcopy(self.value); value['items'][0][field] = bad; self.reject(value)

    def test_use_rows_preserve_unavailable_attributes_without_defaults(self):
        self.value.update(query='uses', totalCaptured=1, items=[
            {'version': self.version, 'nativeInstance': {'engineeringType': None},
             'softwarePath': '/Synthetic/PLC', 'projectPath': None}])
        self.validate(self.value)
        self.value['items'][0]['nativeInstance'].update(Name='Instance', Number=42, Namespace='Unit')
        self.validate(self.value)
        self.value['items'][0]['nativeInstance']['inventedDefault'] = 0
        self.reject(self.value)

    def test_master_contents_sentinel_does_not_claim_complete_contents(self):
        row = {'kind': 'masterCopy', 'masterCopyPath': '/masterCopies/Synthetic', 'name': 'Synthetic',
               'author': None, 'creationDate': '2026-10-23T00:00:00',
               'contents': [{'name': str(i), 'type': 'Synthetic'} for i in range(201)]}
        self.value.update(query='elements', items=[row], totalCaptured=1)
        self.validate(self.value)
        row['contents'].append({'name': 'extra', 'type': 'Synthetic'}); self.reject(self.value)


if __name__ == '__main__':
    unittest.main()
