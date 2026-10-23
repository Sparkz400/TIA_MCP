"""Portable JSON Schema checks for the offline OPC UA Host contract."""
import copy
import json
import pathlib
import unittest

from jsonschema import Draft202012Validator


ROOT = pathlib.Path(__file__).resolve().parents[1]
SCHEMA = json.loads((ROOT / "contracts/opc-ua-workflow.v1.schema.json").read_text(encoding="utf-8"))
REFERENCE = "opcua:" + "a" * 32 + ":" + "b" * 64


def validate_definition(name, value):
    schema = {"$ref": "#/$defs/" + name, "$defs": SCHEMA["$defs"]}
    Draft202012Validator(schema).validate(value)


class OpcUaWorkflowContractTests(unittest.TestCase):
    def test_schema_and_all_eight_tool_contracts(self):
        Draft202012Validator.check_schema(SCHEMA)
        self.assertEqual(8, len(SCHEMA["x-tool-contracts"]))
        for contract in SCHEMA["x-tool-contracts"].values():
            for direction in ("input", "output"):
                self.assertIn(contract[direction]["$ref"].split("/")[-1], SCHEMA["$defs"])

    def test_path_handles_rejected(self):
        validate_definition("recordRef", REFERENCE)
        validate_definition("artifactRef", REFERENCE + ":original.xml")
        for value in ("../file.xml", "C:\\x.xml", REFERENCE + ":../../file.xml"):
            for definition in ("recordRef", "artifactRef"):
                with self.assertRaises(Exception):
                    validate_definition(definition, value)

    def test_creation_and_paging_limits(self):
        validate_definition("opcua_dependenciesInput", {"modelRefs": [REFERENCE]})
        for value in ([], [REFERENCE, REFERENCE], [REFERENCE] * 33):
            with self.assertRaises(Exception):
                validate_definition("opcua_dependenciesInput", {"modelRefs": value})
        for name, payload in [("opcua_report_pageInput", {"recordRef": REFERENCE, "section": "nodes", "pageSize": 501}),
                              ("opcua_artifact_readInput", {"artifactRef": REFERENCE + ":report.json", "count": 65537})]:
            with self.assertRaises(Exception):
                validate_definition(name, payload)

    def test_no_write_output_path_or_overwrite_contract(self):
        for name in SCHEMA["x-tool-contracts"]:
            properties = SCHEMA["$defs"][name + "Input"]["properties"]
            self.assertFalse({"path", "outputPath", "overwrite"} & properties.keys())
            self.assertFalse(SCHEMA["$defs"][name + "Input"]["additionalProperties"])

    def test_native_and_completeness_claims_rejected(self):
        page = {"recordRef": REFERENCE, "reportSha256": "c" * 64, "section": "nodes", "offset": 0, "total": 0,
                "rows": [], "nextCursor": None, "completeArtifactRef": REFERENCE + ":report.json",
                "nativeQualified": False, "compatibilityCertified": False, "dependencyClosureComplete": False}
        validate_definition("reportPage", page)
        for flag in ("nativeQualified", "compatibilityCertified", "dependencyClosureComplete"):
            changed = copy.deepcopy(page); changed[flag] = True
            with self.assertRaises(Exception):
                validate_definition("reportPage", changed)

    def test_incomplete_discovery_and_error_shapes(self):
        validate_definition("listRow", {"recordRef": None, "publicationId": "a" * 32,
                                        "state": "incomplete", "errorCode": "OpcUaPublicationIncomplete"})
        validate_definition("error", {"error": {"code": "OpcUaCapacity", "message": "Bound exceeded", "details": None}})


if __name__ == "__main__":
    unittest.main()
