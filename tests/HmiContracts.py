"""Draft 2020-12 HMI contracts and optional validation of a native bridge report.

Uses the explorer's optional workspace-local jsonschema>=4.18 test dependency.
No native engineering operation runs here.
"""
import argparse
import copy
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "artifacts/project-model-schema-deps"))
from jsonschema import Draft202012Validator
from referencing import Registry, Resource

NAMES = ("hmi-identity.v2", "hmi-inspection.v1", "hmi-references.v1", "hmi-engineering.v1", "hmi-library-handoff.v1", "project-model.v1", "project-model-provider.v1")
SCHEMAS = {name: json.loads((ROOT / "contracts" / (name + ".schema.json")).read_text(encoding="utf-8-sig")) for name in NAMES}
REGISTRY = Registry().with_resources((schema["$id"], Resource.from_contents(schema)) for schema in SCHEMAS.values())


def validator(name, definition=None):
    schema = SCHEMAS[name]
    if definition:
        schema = {"$ref": schema["$id"] + "#/$defs/" + definition}
    return Draft202012Validator(schema, registry=REGISTRY)


class HmiContracts(unittest.TestCase):
    def test_schemas_are_valid(self):
        for schema in SCHEMAS.values():
            with self.subTest(schema=schema["$id"]): Draft202012Validator.check_schema(schema)

    def test_identity_forbids_fabricated_plc_owner(self):
        identity = dict(schemaVersion=2, projectPath="D:/fixture.ap21", projectCopyId="copy", sessionGeneration="session", family="classic", devicePath="/device", hmiPath="/device/hmi", kind="hmiScreen", objectPath="/device/hmi/screens/a")
        validator("hmi-identity.v2").validate(identity)
        identity["plcPath"] = "/invented"
        self.assertFalse(validator("hmi-identity.v2").is_valid(identity))

    def test_identity_and_request_versions_are_distinct(self):
        request = {"schemaVersion": 1, "section": "screens", "target": {"schemaVersion": 2, "hmiPath": "/device/hmi"}}
        validator("hmi-inspection.v1").validate(request)
        request["schemaVersion"] = 2
        self.assertFalse(validator("hmi-inspection.v1").is_valid(request))

    def test_incomplete_reference_keeps_null_endpoint_and_native_spelling(self):
        edge = dict(sourcePath="/hmi/screen/item", relation="hmiScreenItemTag", target=None, targetCandidates=[], resolution="dynamic", evidence="unresolved", sourceProperty="TagName", nativeSpelling="{prefix}+tag", automaticRewiringAllowed=False, candidateScopeComplete=False)
        validator("hmi-references.v1", "edge").validate(edge)
        edge["automaticRewiringAllowed"] = True
        self.assertFalse(validator("hmi-references.v1", "edge").is_valid(edge))


def validate_evidence(path):
    report = json.loads(path.read_text(encoding="utf-8-sig"))
    count = 0
    for profile in report["profiles"]:
        observations = ([profile["devices"]] if "devices" in profile else []) + [item["result"] for item in profile.get("inspections", [])]
        observations += [item["result"] for item in profile.get("resourceSources", []) if "result" in item]
        for observed in observations:
            validator("hmi-inspection.v1", "response").validate(observed["lastPage"])
            count += 1
            for row in observed["items"]:
                validator("hmi-inspection.v1", "row").validate(row)
                count += 1
        for references in profile.get("references", []):
            validator("hmi-references.v1").validate(references.get("result", references))
            count += 1
        details = profile.get("explorer", {}).get("details", {})
        for provider in ([details["details"]] if "details" in details else []) + details.get("previews", []):
            if provider.get("schemaVersion") == "project-model-provider.v1":
                validator("project-model-provider.v1").validate(provider)
                count += 1
    if count == 0: raise AssertionError("Report has no native response evidence")
    print(f"Validated {count} actual response/row/provider contracts from {path}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence", type=Path)
    args, remaining = parser.parse_known_args()
    outcome = unittest.main(argv=[sys.argv[0], *remaining], exit=False)
    if not outcome.result.wasSuccessful(): raise SystemExit(1)
    if args.evidence: validate_evidence(args.evidence)
