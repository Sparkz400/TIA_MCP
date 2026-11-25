"""contracts/change-payloads.v1.schema.json agrees with the worker's payload parsers on payloads whose verdict the code fixes.

Each case is (kind, payload, worker accepts). The expectations follow the cited worker rules; they are not native evidence.

Every inline kind also carries `examples`: payloads with synthetic names whose shapes and literal values come from the
qualification harnesses (scripts/qualify_*.py) and the accepted cases below. They show a valid shape, not a payload that
applies to any project: paths, names, hashes and credential references stand for what inventory returns."""
import json
import pathlib
import re
import unittest

from jsonschema import Draft202012Validator

ROOT = pathlib.Path(__file__).resolve().parents[1]
DOCUMENT = json.loads((ROOT / "contracts/change-payloads.v1.schema.json").read_text(encoding="utf-8"))
HMI_DEVICE = {"projectCopyId": "a", "sessionGeneration": "b", "hmiPath": "/h", "objectPath": "/h", "family": "unified", "kind": "hmiDevice"}

CASES = [
    # Batching (ChangeBatchRules.cs): only batch kinds take items; never nested or beside a single edit.
    ("hardware_delete", {"hardwarePath": "x"}, True),
    ("hardware_delete", {"hardwarePath": "x", "items": [{}]}, False),
    ("block_rename", {"blockName": "A", "items": [{}]}, False),
    ("block_rename", {"items": [{"blockName": "A", "newName": "B", "items": None}]}, False),
    ("block_rename", {"items": [{"blockName": "A", "newName": "B"}], "scopePath": None}, True),
    ("tag_table_create", {"tagTableName": "T"}, True),
    ("tag_table_create", {"items": [{"tagTableName": "T"}]}, True),
    # Namespaces (ObjectModel.cs): "" is the root namespace; tag tables have none.
    ("type_rename", {"typeName": "T", "newName": "U", "namespace": ""}, True),
    ("tag_delete", {"tagTableName": "T", "memberName": "a", "dependencyPolicy": "rejectReferenced"}, True),
    ("tag_delete", {"tagTableName": "T", "memberName": "a"}, False),
    ("tag_delete", {"tagTableName": "T", "memberName": "a", "dependencyPolicy": "rejectReferenced", "namespace": "N"}, False),
    # Technology objects (TechnologyEditing.cs, TechnologyConnections.cs).
    ("to_connect", {"name": "Axis_1", "role": "actor", "inputAddress": 256, "outputAddress": 256}, True),
    ("to_connect", {"name": "Axis_1", "role": "sensor", "hardwarePath": "x"}, False),
    ("to_connect", {"name": "Axis_1", "role": "sensor", "sensorIndex": 0, "hardwarePath": "x", "telegramType": "MainTelegram"}, True),
    ("to_connect", {"name": "Axis_1", "role": "actor", "hardwarePath": "x", "telegramType": "Main"}, False),
    # Hardware and connections.
    ("hardware_shared_device_access", {"hardwarePath": "x", "accessType": "OutsideOfProject", "externalIoControllers": 2}, True),
    ("connection_delete", {"hardwarePath": "x", "connectionName": "c", "connectionId": "5"}, True),
    ("network_configure", {"hardwarePath": "x", "subnetName": "PN", "disconnect": False}, True),
    ("network_configure", {"hardwarePath": "x", "subnetName": "PN", "disconnect": True}, False),
    ("network_mrp_set", {"hardwarePath": "x", "role": "Client", "createDomain": False}, True),
    # Graphical edits (EngineeringSources.cs).
    ("graphical_network_insert", {"blockName": "A", "compileUnitXml": "<x/>"}, False),
    ("graphical_network_patch", {"blockName": "A", "networkId": "1"}, False),
    # Tags (TagBatchRules.cs, SafetyTagRenameRules.cs, TagTableRowRules.cs).
    ("tag_set", {"items": [{"tagTableName": "T", "memberName": "a", "attributes": {"Name": "b"}}], "scopePath": "x"}, True),
    ("tag_set", {"items": [{"tagTableName": "T", "memberName": "a", "attributes": {"Name": "b"}, "scopePath": "s"}]}, False),
    ("tag_set", {"tagTableName": "T", "memberName": "a", "attributes": {"Name": "b", "DataTypeName": "Int"}, "safetyAcknowledged": True}, False),
    ("tag_table_import_xml", {"tagTableName": "T", "tags": [{"name": "x", "dataType": "Bool", "logicalAddress": "%E0.0"}]}, True),
    ("tag_table_import_xml", {"tagTableName": "T", "tags": [{"name": "x", "dataType": "Bool", "logicalAddress": "%I0.9"}]}, False),
    # Sources, VCI, OPC UA, HMI and Test Suite.
    ("source_upsert", {"language": "scl", "source": "x", "expectedBlockNames": ["A"]}, True),
    ("source_upsert", {"language": "SIMATIC_SD", "source": "x", "expectedBlockNames": ["A", "B"]}, False),
    ("vci_synchronize", {"workspacePath": "w", "direction": "ProjectToWorkspace", "objectPath": "o", "allMappedObjects": False}, True),
    ("vci_synchronize", {"workspacePath": "w", "direction": "ProjectToWorkspace"}, False),
    ("opcua_server_settings_set", {"guestAccess": {"expected": False, "value": True}}, False),
    ("opcua_server_settings_set", {"port": {"expected": 4840, "value": 4841}}, True),
    ("hmi_text_list_import", {"schemaVersion": 1, "target": HMI_DEVICE, "expectedContentHash": "a" * 64, "recordId": "b" * 64}, True),
    ("hmi_text_list_import", {"schemaVersion": 1, "target": HMI_DEVICE, "expectedContentHash": "a" * 64, "recordId": "rec-1"}, False),
    ("testsuite_application_import", {"sourcePath": "C:/a.tat", "expectedTestCaseNames": ["x"]}, True),
    ("testsuite_application_import", {"sourcePath": "C:/a.tat", "expectedTestSetNames": ["x"]}, False),
    ("plc_compile", {}, True),
    ("plc_compile", {"a": 1}, False),
]

# Inline kinds without an example yet, each with the reason. A kind leaves this list by getting an example.
NO_EXAMPLE_YET = {
    # The payload carries a whole exported document; a short real one does not exist.
    "block_import_xml": "inline SimaticML block document",
    "plc_type_import_xml": "inline SimaticML type document",
    "safety_import_xml": "inline SimaticML F-block document",
    "graphical_network_insert": "inline SimaticML compile unit",
    # A required field is an opaque handle another tool issues; no fixture literal shows its form.
    "certificate_import": "uploadRef of a committed upload",
    "graph_import": "graphRef of a reviewed GRAPH document",
    "software_unit_create_from": "libraryId and masterCopyPath of an open library",
    "db_snapshot_create": "downloadTicket of an owned simulation",
    "db_snapshot_load_actual": "downloadTicket and a reviewed snapshot record",
    "simulation_download_online": "downloadTicket of an owned simulation",
    "vci_workspace_create": "VCI group path from the worker's inventory",
    "vci_workspace_delete": "VCI workspace path from the worker's inventory",
    "vci_mapping_add": "VCI workspace path from the worker's inventory",
    "vci_mapping_remove": "VCI workspace path from the worker's inventory",
    "vci_synchronize": "VCI workspace path from the worker's inventory",
    "vci_conflict_resolve": "VCI workspace path, comparison record and package id",
    # Each entry guards on the value observed on the target, in the form the read tool reports it.
    "drive_motor_configure": "expected nameplate values as observed on the drive",
    "drive_parameter_set": "expected parameter values as observed on the drive",
    "drive_telegram_set": "expectedTelegrams rows as observed on the drive",
    "safety_fparameter_set": "F-parameter names and expected values as observed on the module",
    "hardware_channel_attributes": "channel values in the native form the hardware read reports",
    # The replacement must be a catalog identifier compatible with the module it replaces; no harness literal pairs the two.
    "hardware_replace": "a compatible catalog typeIdentifier for the replaced module",
    # Valid for the parser, but V21 declares every allowlisted attribute read-only (qualify_project_settings.py).
    "project_attributes_set": "always refused on V21 with AttributeNotWritable",
}


def declared_properties(schema):
    """Property names a kind declares on its payload object, through allOf/oneOf/anyOf, if/then/else and local $ref."""
    names = set(schema.get("properties", {}))
    for word in ("allOf", "oneOf", "anyOf"):
        for part in schema.get(word, []):
            names |= declared_properties(part)
    for word in ("if", "then", "else"):
        if isinstance(schema.get(word), dict):
            names |= declared_properties(schema[word])
    if "$ref" in schema:
        names |= declared_properties(DOCUMENT["$defs"][schema["$ref"].rsplit("/", 1)[-1]])
    return names


class ChangePayloadSchemaTests(unittest.TestCase):
    def test_document_is_valid_and_every_definition_resolves(self):
        Draft202012Validator.check_schema(DOCUMENT)
        for name in DOCUMENT["$defs"]:
            Draft202012Validator({"$ref": "#/$defs/" + name, "$defs": DOCUMENT["$defs"]}).is_valid({})

    def test_schemas_agree_with_the_worker_rules(self):
        for kind, payload, accepted in CASES:
            with self.subTest(kind=kind, payload=payload):
                validator = Draft202012Validator({"$ref": "#/$defs/" + kind, "$defs": DOCUMENT["$defs"]})
                self.assertEqual(accepted, validator.is_valid(payload))

    def test_every_change_kind_is_covered_once(self):
        catalog = json.loads((ROOT / "contracts/feature-catalog.v1.json").read_text(encoding="utf-8"))
        kinds = {feature["changeKind"] for feature in catalog["features"] if feature.get("changeKind")}
        inline = {name for name in DOCUMENT["$defs"] if not name.startswith("common.")}
        external = set(DOCUMENT["x-external-payloads"])
        self.assertEqual(kinds, inline | external)
        self.assertFalse(inline & external)

    def test_every_example_is_a_valid_payload_of_its_kind(self):
        for kind, schema in DOCUMENT["$defs"].items():
            validator = Draft202012Validator({"$ref": "#/$defs/" + kind, "$defs": DOCUMENT["$defs"]})
            for example in schema.get("examples", []):
                with self.subTest(kind=kind, example=example):
                    self.assertEqual([], [error.message for error in validator.iter_errors(example)])
                    # Kinds that ignore unknown fields would accept a misspelled one; an example uses declared fields only.
                    self.assertFalse(set(example) - declared_properties(schema))
                    # A batch item takes the fields of the single edit (the first oneOf branch).
                    for item in example.get("items") or []:
                        self.assertFalse(set(item) - declared_properties(schema["oneOf"][0]))

    def test_every_inline_kind_has_an_example_or_a_reason(self):
        inline = {name for name in DOCUMENT["$defs"] if not name.startswith("common.")}
        with_examples = {name for name in inline if DOCUMENT["$defs"][name].get("examples")}
        self.assertEqual(inline - with_examples, set(NO_EXAMPLE_YET))
        self.assertFalse([name for name in DOCUMENT["$defs"] if name.startswith("common.") and "examples" in DOCUMENT["$defs"][name]])

    def test_examples_use_synthetic_names_only(self):
        text = json.dumps([schema.get("examples", []) for schema in DOCUMENT["$defs"].values()])
        self.assertNotIn("Users", text)
        for value in re.findall(r'"([A-Za-z]:[^"]*)"', text):
            self.assertTrue(value.startswith("C:/TiaMcpFixture/"), value)


if __name__ == "__main__":
    unittest.main()
