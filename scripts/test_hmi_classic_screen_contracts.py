"""Public schemas for synthetic output from the real Classic inventory traversal."""
import copy
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator
from referencing import Registry, Resource

ROOT = Path(__file__).resolve().parents[1]


class ClassicScreenContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.documents = [json.loads((ROOT / 'contracts' / name).read_text(encoding='utf-8'))
                         for name in ('hmi-inspection.v1.schema.json', 'hmi-identity.v2.schema.json')]
        cls.registry = Registry().with_resources((d['$id'], Resource.from_contents(d)) for d in cls.documents)
        cls.sample = json.loads((ROOT / 'fixtures/hmi/classic-screen-inventory/synthetic.json').read_text(encoding='utf-8'))

    def validator(self, definition):
        return Draft202012Validator({'$ref': self.documents[0]['$id'] + '#/$defs/' + definition}, registry=self.registry)

    def test_actual_helper_rows_and_coverage(self):
        self.assertEqual('synthetic', self.sample['authority'])
        self.assertEqual(6, len(self.sample['rows']))
        self.assertEqual(6, len(self.sample['screenScopes']))
        for row in self.sample['rows']:
            self.validator('row').validate(row)
        for scope in self.sample['screenScopes']:
            self.validator('classicScreenScope').validate(scope)

    def test_older_screen_rows_remain_valid_without_role(self):
        row = copy.deepcopy(self.sample['rows'][0])
        del row['screenRole']
        self.validator('row').validate(row)

    def test_unknown_roles_and_invalid_coverage_refuse(self):
        row = copy.deepcopy(self.sample['rows'][0])
        row['screenRole'] = 'invented'
        self.assertFalse(self.validator('row').is_valid(row))
        for field, value in [('status', 'verified'), ('role', 'invented'), ('observed', -1), ('observed', 50001), ('property', 'PrivateDatabase')]:
            with self.subTest(field=field, value=value):
                scope = copy.deepcopy(self.sample['screenScopes'][0]); scope[field] = value
                self.assertFalse(self.validator('classicScreenScope').is_valid(scope))

    def test_absence_failure_and_truncation_are_explicit_states(self):
        for status in ('absent', 'unavailable', 'truncated'):
            scope = copy.deepcopy(self.sample['screenScopes'][0]); scope['status'] = status; scope['observed'] = 0
            self.validator('classicScreenScope').validate(scope)


if __name__ == '__main__':
    unittest.main()
