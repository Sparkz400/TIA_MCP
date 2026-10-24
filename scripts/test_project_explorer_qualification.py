"""Pure negative checks for the native qualification oracle; never starts TIA."""
import copy
import unittest

from qualify_project_explorer import (SEED_MEMBERS, SEED_RESOLVED, exact_reference, validate_fixture_relationships,
                                      validate_relationship_scope, validate_typed_preview)


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


class RelationshipScopeOracle(unittest.TestCase):
    plc = "/projects/Owned/devices/Station/items/CPU/plc/CPU"

    def graphs(self):
        block = lambda name: {"objectPath": self.plc + "/blocks/" + name}
        edges = [{"edgeId": "r%d" % i, "kind": kind, "source": block(source), "target": block(target)}
                 for i, (kind, source, target) in enumerate(sorted(SEED_RESOLVED))]
        edges += [{"edgeId": "m%d" % i, "kind": kind, "source": block("TiaMcp_EnableGate"), "target": None,
                   "unresolved": {"target": {"name": name, "nativeIdentity": {"nativePath": "CPU\Program blocks\TiaMcp_EnableGate"}}}}
                  for i, (kind, name) in enumerate(sorted(SEED_MEMBERS))]
        def graph(rows, status, stops, max_edges):
            return {"edges": copy.deepcopy(rows), "truncated": bool(stops), "limits": {"maxEdges": max_edges},
                    "nativeCoverage": {"scopeComplete": True, "truncated": False},
                    "relationshipScope": {"status": status, "stopReasons": stops, "retainedEdges": len(edges), "returnedEdges": len(rows),
                                          "projectCompleteness": "notEstablished"}}
        return graph(edges, "complete", [], 200), graph(edges, "complete", [], 1000), graph(edges[:3], "truncated", ["edges"], 3)

    def test_exact_authored_scope_passes(self):
        summary = validate_relationship_scope(*self.graphs(), self.plc)
        self.assertEqual((15, 3), (summary["retainedEdges"], summary["truncatedReturnedEdges"]))

    def test_invented_or_missing_resolved_edge_rejected(self):
        seed, full, cut = self.graphs()
        seed["edges"][0]["kind"] = "writes"
        with self.assertRaisesRegex(AssertionError, "authored source"): validate_relationship_scope(seed, full, cut, self.plc)
        seed, full, cut = self.graphs()
        del seed["edges"][0]
        with self.assertRaisesRegex(AssertionError, "authored source"): validate_relationship_scope(seed, full, cut, self.plc)

    def test_foreign_endpoint_rejected(self):
        seed, full, cut = self.graphs()
        full["edges"][0]["target"] = {"objectPath": self.plc + "/blocks/Other"}
        with self.assertRaisesRegex(AssertionError, "outside the authored fixture"): validate_relationship_scope(seed, full, cut, self.plc)
        seed, full, cut = self.graphs()
        full["edges"][-1]["unresolved"]["target"] = {"name": "Foreign", "nativeIdentity": {"nativePath": "CPU\Program blocks\Other"}}
        with self.assertRaisesRegex(AssertionError, "not owned"): validate_relationship_scope(seed, full, cut, self.plc)

    def test_member_substitution_rejected(self):
        seed, full, cut = self.graphs()
        seed["edges"][-1]["unresolved"]["target"]["name"] = "Enable2"
        with self.assertRaisesRegex(AssertionError, "FB interface"): validate_relationship_scope(seed, full, cut, self.plc)

    def test_unreturned_retained_edges_or_hidden_truncation_rejected(self):
        seed, full, cut = self.graphs()
        full["relationshipScope"]["retainedEdges"] += 1
        with self.assertRaisesRegex(AssertionError, "every retained edge"): validate_relationship_scope(seed, full, cut, self.plc)
        seed, full, cut = self.graphs()
        cut["relationshipScope"]["stopReasons"] = []
        with self.assertRaisesRegex(AssertionError, "not visible"): validate_relationship_scope(seed, full, cut, self.plc)


if __name__ == "__main__":
    unittest.main()
