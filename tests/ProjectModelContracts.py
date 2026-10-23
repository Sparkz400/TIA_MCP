"""Executable Draft 2020-12 contract checks. Requires jsonschema>=4.18.

Run: python tests/ProjectModelContracts.py
The workspace-local artifact dependency directory is an optional test-only install.
"""
import copy
from datetime import datetime
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "artifacts/project-model-schema-deps"))
from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource


def read(path):
    return json.loads((ROOT / path).read_text(encoding="utf-8"))


MODEL = read("contracts/project-model.v1.schema.json")
PROVIDER = read("contracts/project-model-provider.v1.schema.json")
REGISTRY = Registry().with_resources([(s["$id"], Resource.from_contents(s)) for s in [MODEL, PROVIDER]])
FORMATS = FormatChecker()


@FORMATS.checks("date-time", raises=(ValueError, TypeError))
def date_time(value):
    if not isinstance(value, str):
        return True
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    return "T" in value and parsed.tzinfo is not None


class ProjectModelContracts(unittest.TestCase):
    def validate(self, value, schema=MODEL):
        Draft202012Validator(schema, registry=REGISTRY, format_checker=FORMATS).validate(value)

    def invalid(self, value, schema=MODEL):
        self.assertTrue(list(Draft202012Validator(schema, registry=REGISTRY, format_checker=FORMATS).iter_errors(value)))

    def test_schemas_and_references_resolve(self):
        for schema in [MODEL, PROVIDER]:
            Draft202012Validator.check_schema(schema)
        self.validate(read("fixtures/project-explorer/representative.project-model.json"))
        self.validate(read("fixtures/project-explorer/expired.project-model.json"))
        self.validate(read("fixtures/project-explorer/unsupported-provider.json"), PROVIDER)

    def test_relationship_scope_never_claims_project_completeness(self):
        fixture = read("fixtures/project-explorer/representative.project-model.json")
        record = fixture["coverage"][0]
        record["relationshipScope"] = {"scope": "retainedSnapshotEdges", "status": "truncated", "projectCompleteness": "notEstablished", "retainedEdges": 5, "returnedEdges": 2, "direction": "outgoing", "kinds": ["calls"], "stopReasons": ["depth"]}
        self.validate(fixture)
        for field, value in [("projectCompleteness", "complete"), ("stopReasons", ["unknown"]), ("retainedEdges", -1)]:
            invalid = copy.deepcopy(fixture)
            invalid["coverage"][0]["relationshipScope"][field] = value
            self.invalid(invalid)

    def test_native_and_virtual_origins_are_not_interchangeable(self):
        fixture = read("fixtures/project-explorer/representative.project-model.json")
        fixture["nodes"][1]["objectPath"] = "/forged/native/path"
        self.invalid(fixture)
        fixture = read("fixtures/project-explorer/representative.project-model.json")
        del fixture["nodes"][0]["objectPath"]
        self.invalid(fixture)

    def test_metadata_cannot_certify_content(self):
        fixture = read("fixtures/project-explorer/representative.project-model.json")
        fixture["metadataRevision"]["includesContent"] = True
        self.invalid(fixture)
        response = read("fixtures/project-explorer/unsupported-provider.json")
        response["contentRevision"]["scope"] = "exposedMetadata"
        self.invalid(response, PROVIDER)

    def test_unknown_endpoints_require_unresolved_evidence(self):
        fixture = read("fixtures/project-explorer/representative.project-model.json")
        edge = next(e for e in fixture["edges"] if e["target"] is None)
        self.assertEqual("unresolved", edge["evidence"]["resolution"])
        edge["unresolved"] = None
        self.invalid(fixture)

    def test_provider_requires_exact_snapshot_reference(self):
        response = read("fixtures/project-explorer/unsupported-provider.json")
        for field in ["snapshotId", "sessionGeneration", "projectPath", "nodeId", "objectPath"]:
            invalid = copy.deepcopy(response)
            del invalid["reference"][field]
            self.invalid(invalid, PROVIDER)

    def test_unavailable_provider_cannot_attach_content(self):
        response = read("fixtures/project-explorer/unsupported-provider.json")
        response["attachments"] = [{}]
        self.invalid(response, PROVIDER)

    def test_empty_groups_duplicates_and_coverage_are_explicit(self):
        fixture = read("fixtures/project-explorer/representative.project-model.json")
        nodes = {n["nodeId"]: n for n in fixture["nodes"]}
        self.assertEqual("native", nodes["empty-a"]["origin"])
        self.assertEqual("blocks-a", nodes["empty-a"]["parentId"])
        self.assertFalse(any(n["parentId"] == "empty-a" for n in nodes.values()))
        starts = [n["objectPath"] for n in nodes.values() if n["name"] == "Start"]
        self.assertEqual(4, len(set(starts)))
        self.assertNotIn("plcPath", nodes["hmi"])
        self.assertEqual("unsupported", next(c for c in fixture["coverage"] if c["provider"] == "hmi")["status"])
        self.assertEqual("protected", nodes["protected"]["access"]["state"])
        self.assertEqual("unsupported", nodes["cpu-a"]["metadata"]["physicalTopology"])

    def test_timestamp_and_budget_boundaries(self):
        fixture = read("fixtures/project-explorer/representative.project-model.json")
        fixture["captureStartedAtUtc"] = "not-a-date"
        self.invalid(fixture)
        fixture = read("fixtures/project-explorer/representative.project-model.json")
        fixture["limits"]["maxVisited"] = 50001
        self.invalid(fixture)


if __name__ == "__main__":
    unittest.main(verbosity=2)
