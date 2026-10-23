"""Public component recipe identities consume the entire input; no native calls."""
import copy
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[1]


class ComponentTextContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.schema = json.loads((ROOT / 'contracts/plc-library-integration-recipe.v1.schema.json').read_text(encoding='utf-8'))
        cls.check = Draft202012Validator(cls.schema)
        cls.fixtures = [json.loads(p.read_text(encoding='utf-8')) for p in
                        sorted((ROOT / 'fixtures/libraries/integration').glob('*.example.json'))]

    def test_existing_examples(self):
        self.assertEqual(2, len(self.fixtures))
        for fixture in self.fixtures:
            self.check.validate(fixture)

    def test_complete_restricted_fields(self):
        for fixture in self.fixtures:
            paths = [('integration', 'instanceDb', 'name'), ('integration', 'mappings', 0, 'parameter'),
                     ('selection', 0, 'typeGuid'), ('selection', 0, 'versionGuid')]
            caller = fixture['integration']['caller']
            paths.append(('integration', 'caller', 'name' if caller.get('mode') == 'createStandaloneFc' else 'insertAfterNetworkId'))
            for path in paths:
                for suffix in ('\n', '\r', '\r\n', '\u2028', '\u2029', '\0', ' '):
                    with self.subTest(path=path, suffix=repr(suffix)):
                        altered = copy.deepcopy(fixture)
                        owner = altered
                        for key in path[:-1]:
                            owner = owner[key]
                        owner[path[-1]] += suffix
                        self.assertFalse(self.check.is_valid(altered))

    def test_zero_guids_and_unrestricted_paths(self):
        for fixture in self.fixtures:
            altered = copy.deepcopy(fixture)
            altered['selection'][0]['typeGuid'] = '00000000-0000-0000-0000-000000000000'
            self.assertFalse(self.check.is_valid(altered))
            altered = copy.deepcopy(fixture)
            altered['integration']['mappings'][0]['tagPath'] = '/plc/tags/Operator supplied path'
            self.check.validate(altered)


if __name__ == '__main__':
    unittest.main()
