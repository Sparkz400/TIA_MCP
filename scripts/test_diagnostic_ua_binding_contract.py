"""Independent wire boundaries for caller-selected pre-acquisition UA intent."""
import copy
import json
from pathlib import Path
import unittest

from jsonschema import Draft202012Validator
from referencing import Registry, Resource

ROOT = Path(__file__).resolve().parents[1]


def validator(fragment=""):
    schemas = [json.loads(p.read_text(encoding="utf-8")) for p in (ROOT / "contracts").glob("*.json")]
    registry = Registry().with_resources((s["$id"], Resource.from_contents(s)) for s in schemas if "$id" in s and "$schema" in s)
    return Draft202012Validator({"$ref": "https://tia-mcp.local/contracts/diagnostic-ua-binding.v1.schema.json" + fragment}, registry=registry)


class UaBindingContractTests(unittest.TestCase):
    def setUp(self):
        self.request = dict(attemptId="a" * 32, reason="Selected observation",
                            mappings=[dict(signalId="enable", unit="bool", reason="Enable observation",
                                           node=dict(namespaceUri="urn:fixture", identifierType="string", identifier="bool"))])

    def test_explicit_required_selection(self):
        check = validator("#/$defs/input")
        value = dict(experimentId="b" * 64, request=self.request)
        check.validate(value)
        for field in self.request:
            changed = copy.deepcopy(value)
            del changed["request"][field]
            self.assertFalse(check.is_valid(changed), field)
        self.assertFalse(check.is_valid(dict(experimentId="b"*64+"\n",request=self.request)))
        changed=copy.deepcopy(value);changed["request"]["attemptId"]+="\n"
        self.assertFalse(check.is_valid(changed))
        for field in self.request["mappings"][0]:
            changed = copy.deepcopy(value)
            del changed["request"]["mappings"][0][field]
            self.assertFalse(check.is_valid(changed), field)

    def test_no_implicit_selection_or_authority(self):
        check = validator("#/$defs/request")
        for key in ("programMatchVerified", "addressMatchVerified", "assertionsAdmitted", "reconnect"):
            self.assertFalse(check.is_valid({**self.request, key: True}))
        for mappings in ([], [self.request["mappings"][0]] * 65):
            self.assertFalse(check.is_valid({**self.request, "mappings": mappings}))
        changed = copy.deepcopy(self.request)
        changed["mappings"][0]["node"]["namespaceIndex"] = 2
        self.assertFalse(check.is_valid(changed))

    def test_additive_capture_request_reference(self):
        for name, defs in (("capture", ("request",)), ("subscription", ("request", "normalizedRequest"))):
            schema = json.loads((ROOT / f"contracts/opcua-runtime-{name}.v1.schema.json").read_text(encoding="utf-8"))
            Draft202012Validator.check_schema(schema)
            for definition in defs:
                field = schema["$defs"][definition]["properties"]["diagnosticBindingId"]
                check = Draft202012Validator(field)
                check.validate("a" * 64)
                check.validate(None)
                for bad in ("", "../record", "A" * 64, "a" * 63, True):
                    self.assertFalse(check.is_valid(bad))
                self.assertNotIn("diagnosticBindingId", schema["$defs"][definition]["required"])


if __name__ == "__main__":
    unittest.main()
