"""Pure negative checks for the native qualification oracle; never starts TIA."""
import copy
import unittest

from qualify_project_explorer import exact_reference, validate_fixture_relationships, validate_typed_preview


class ExplorerQualificationOracle(unittest.TestCase):
    def setUp(self):
        self.plc = "/projects/Owned/devices/Station/items/CPU/plc/CPU"
        self.path = lambda name: self.plc + "/blocks/" + name
        self.reference = {"schemaVersion": "project-model.v1", "snapshotId": "snapshot", "sessionGeneration": "session",
                          "projectPath": "D:/owned/Owned.ap21", "nodeId": "lad", "objectPath": self.path("Preview")}

    def graph(self):
        def edge(kind, source, target):
            return {"kind": kind, "source": {"objectPath": self.path(source)}, "target": {"objectPath": self.path(target)} if target else None,
                    "evidence": {"sourcePath": self.path(source), "location": "native line 1"}}
        rows = [edge("calls", "Main", "TiaMcp_EnableGate"), edge("calls", "TiaMcp_EnableGate", "TiaMcp_IsEnabled"),
                edge("instanceOf", "TiaMcp_EnableGate_DB", "TiaMcp_EnableGate")]
        for kind, member in (("reads", "Enable"), ("writes", "Active")):
            row = edge(kind, "TiaMcp_EnableGate", None)
            row["unresolved"] = {"target": {"name": member}}
            rows.append(row)
        return {"edges": rows}

    def preview(self):
        revision = {"status": "available", "algorithm": "sha256", "value": "a" * 64, "scope": "exportedNativeBlockXml"}
        return {"previews": [{"provider": "typedLadFbd.v21", "status": "partial", "reference": self.reference,
            "contentRevision": revision, "sourceArtifact": {"sha256": "a" * 64}, "runtimeVerified": False,
            "attachments": [{"kind": "ladFbdNetwork", "source": {"objectPath": self.reference["objectPath"], "contentRevision": revision, "capturedAtUtc": "2026-10-08T12:00:00Z"},
                             "content": {"language": "LAD", "nodes": [{"nativeName": "Contact"}, {"nativeName": "Coil"}]}, "coverage": {"status": "partial"}}]}]}

    def test_independent_authored_direction_and_members(self):
        validate_fixture_relationships(self.graph(), self.plc)

    def test_reversed_call_rejected(self):
        graph = self.graph(); graph["edges"][0]["source"], graph["edges"][0]["target"] = graph["edges"][0]["target"], graph["edges"][0]["source"]
        with self.assertRaises(AssertionError): validate_fixture_relationships(graph, self.plc)

    def test_reversed_instance_rejected(self):
        graph = self.graph(); graph["edges"][2]["source"], graph["edges"][2]["target"] = graph["edges"][2]["target"], graph["edges"][2]["source"]
        with self.assertRaises(AssertionError): validate_fixture_relationships(graph, self.plc)

    def test_member_name_substitution_rejected(self):
        graph = self.graph(); graph["edges"][3]["unresolved"] = None
        with self.assertRaises(AssertionError): validate_fixture_relationships(graph, self.plc)

    def test_native_preview_source_contract(self):
        validate_typed_preview(self.preview(), self.reference, "LAD")

    def test_wrong_source_hash_rejected(self):
        preview = self.preview(); preview["previews"][0]["sourceArtifact"]["sha256"] = "b" * 64
        with self.assertRaises(AssertionError): validate_typed_preview(preview, self.reference, "LAD")

    def test_cross_object_preview_rejected(self):
        preview = self.preview(); preview["previews"][0]["reference"] = {**self.reference, "objectPath": self.path("Other")}
        with self.assertRaises(AssertionError): validate_typed_preview(preview, self.reference, "LAD")

    def test_raw_xml_preview_rejected(self):
        preview = self.preview(); preview["previews"][0]["attachments"][0]["content"]["nativeXml"] = "<script/>"
        with self.assertRaises(AssertionError): validate_typed_preview(preview, self.reference, "LAD")

    def test_virtual_nodes_never_become_references(self):
        with self.assertRaises(AssertionError): exact_reference({}, {"origin": "virtual", "name": "SameName"})


if __name__ == "__main__":
    unittest.main()
