"""contracts/change-payloads.v1.schema.json agrees with the worker's payload parsers on payloads whose verdict the code fixes.

Each case is (kind, payload, worker accepts). The expectations follow the cited worker rules; they are not native evidence."""
import json
import pathlib
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


if __name__ == "__main__":
    unittest.main()
