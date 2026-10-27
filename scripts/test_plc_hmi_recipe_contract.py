"""Combined PLC/UDT/HMI recipe contract: examples validate, unsafe variants refuse. No native calls."""
import copy
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator
from referencing import Registry, Resource

ROOT = Path(__file__).resolve().parents[1]


class PlcHmiRecipeContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        load = lambda name: json.loads((ROOT / 'contracts' / name).read_text(encoding='utf-8'))
        plc, combined = load('plc-library-integration-recipe.v1.schema.json'), load('plc-hmi-integration-recipe.v1.schema.json')
        Draft202012Validator.check_schema(combined)
        registry = Registry().with_resources((s['$id'], Resource.from_contents(s)) for s in (plc, combined))
        cls.check = Draft202012Validator(combined, registry=registry)
        cls.examples = {p.name: json.loads(p.read_text(encoding='utf-8')) for p in sorted((ROOT / 'fixtures/libraries/plc-hmi').glob('*.example.json'))}

    def test_examples_validate(self):
        self.assertEqual(2, len(self.examples))
        for example in self.examples.values():
            self.check.validate(example)

    def test_unsafe_variants_refuse(self):
        install, update = self.examples['motor-hmi-plan.example.json'], self.examples['motor-status-update-plan.example.json']
        changes = [(install, lambda p: p['hmi'].update(family='classic')), (install, lambda p: p['hmi'].update(profile='classic-comfort')),
                   (install, lambda p: p['expectedCompilerOutcomes']['hmi'].update(errors=1)), (install, lambda p: p.update(planOnly=False)),
                   (install, lambda p: p['hmi']['tags'][0]['source'].update(members=['status.speed'])), (install, lambda p: p['hmi'].update(extra=True)),
                   (install, lambda p: p['hmi']['tags'][0].update(dataType='"MotorStatus"')), (update, lambda p: p['selection'].append(p['selection'][0])),
                   (update, lambda p: p['target'].update(scopePath='/u')), (update, lambda p: p.update(action='plc_component_plan'))]
        for index, (example, change) in enumerate(changes):
            with self.subTest(index=index):
                altered = copy.deepcopy(example); change(altered)
                self.assertFalse(self.check.is_valid(altered))


if __name__ == '__main__':
    unittest.main()
