"""The relationship qualifier's oracles must reject unverified, reversed or name-substituted evidence."""
import copy
import unittest

from qualify_project_explorer_relationships import (ENCODER, ENCODER_MODULE_NAME, UNIT_A, UNIT_B, expected_network_edges,
                                                    expected_technology_targets, require_complete_coverage, validate_hardware_graph,
                                                    validate_seeded_providers, validate_unit_edges)

PLC_IF = "/projects/Fx/devices/PLC/items/PLC/items/PN"
ET_IF = "/projects/Fx/devices/ET/items/ET/items/PN"
SUBNET = "/projects/Fx/subnets/PN%2FIE%201"
SYSTEM = SUBNET + "/ioSystems/IO%20System"


def system(owner=PLC_IF):
    return {"state": "assigned", "name": "IO System", "controllerOwner": {"resolution": "resolved", "ownerHardwarePath": owner},
            "subnet": {"state": "connected", "name": "PN/IE 1", "resolution": "resolved"}}


ROWS = [
    {"path": PLC_IF, "network": {"nodes": [{"subnet": {"state": "connected", "name": "PN/IE 1", "resolution": "resolved"}}],
                                 "ioControllers": [{"ioSystem": system()}], "ioConnectors": []}},
    {"path": ET_IF, "network": {"nodes": [{"subnet": {"state": "notConnected"}}], "ioControllers": [],
                                "ioConnectors": [{"connectedToIoSystem": system()}]}},
    {"path": "/projects/Fx/devices/HMI/items/HMI/items/PN", "network": {"nodes": [{"subnet": {"state": "connected", "name": "x", "resolution": "unresolved"}}],
                                                                       "ioControllers": [{"ioSystem": system()}], "ioConnectors": []}},
]


def edge(kind, source, target, resolution="resolved"):
    return {"kind": kind, "source": {"objectPath": source}, "target": {"objectPath": target}, "unresolved": None if resolution == "resolved" else {"reason": "x"},
            "evidence": {"resolution": resolution, "capturedAtUtc": "2026-10-26T00:00:00Z", "contentRevision": {"status": "unavailable", "scope": "configuredNetwork"}}}


class NetworkOracleTests(unittest.TestCase):
    def test_oracle_derives_escaped_paths_and_rejects_foreign_controller(self):
        self.assertEqual(expected_network_edges(ROWS, "Fx"), {
            ("connectedSubnet", PLC_IF, SUBNET), ("ioSystemController", PLC_IF, SYSTEM), ("ioSystemDevice", ET_IF, SYSTEM)})

    def test_exact_graph_passes_and_substitution_fails(self):
        expected = expected_network_edges(ROWS, "Fx")
        graph = {"providerScope": {"providers": ["configuredIo", "configuredNetwork"]},
                 "edges": [edge("connectedSubnet", PLC_IF, SUBNET), edge("ioSystemController", PLC_IF, SYSTEM), edge("calls", PLC_IF, SUBNET)]}
        self.assertEqual(len(validate_hardware_graph(graph, PLC_IF, expected)), 2)
        renamed = copy.deepcopy(graph)
        renamed["edges"][0]["target"]["objectPath"] = "/projects/Fx/subnets/PN%2FIE%202"
        with self.assertRaises(AssertionError):
            validate_hardware_graph(renamed, PLC_IF, expected)
        missing = copy.deepcopy(graph)
        missing["edges"][1]["evidence"]["resolution"] = "unresolved"
        with self.assertRaises(AssertionError):
            validate_hardware_graph(missing, PLC_IF, expected)

    def test_resolved_edge_cannot_hide_unresolved_reason(self):
        graph = {"providerScope": {"providers": ["configuredNetwork"]}, "edges": [edge("connectedSubnet", PLC_IF, SUBNET)]}
        graph["edges"][0]["unresolved"] = {"reason": "x"}
        with self.assertRaises(AssertionError):
            validate_hardware_graph(graph, PLC_IF, {("connectedSubnet", PLC_IF, SUBNET)})


class TechnologyAndUnitOracleTests(unittest.TestCase):
    def test_technology_targets_skip_nested_and_unresolved_references(self):
        hardware = {"valueSemantics": "engineeringReference", "kind": "hardware", "resolution": "resolved", "hardwarePath": "/dq",
                    "container": {"valueSemantics": "engineeringReference", "kind": "hardware", "resolution": "resolved", "hardwarePath": "/rack"}}
        channel = {"valueSemantics": "engineeringReference", "kind": "channel", "resolution": "resolved",
                   "parent": {"valueSemantics": "engineeringReference", "kind": "hardware", "resolution": "resolved", "hardwarePath": "/ai"}}
        tag = {"valueSemantics": "engineeringReference", "kind": "tag", "resolution": "resolved"}
        lost = {"valueSemantics": "engineeringReference", "kind": "hardware", "resolution": "unresolved", "hardwarePath": "/lost"}
        read = {"hardwareConnections": [{"attributes": [{"value": hardware}]}, {"channel": channel}, {"outputTag": tag}, {"inputModule": lost}]}
        self.assertEqual(expected_technology_targets(read), {"/dq", "/ai"})

    def test_unit_edges_require_every_relation_and_native_target_kind(self):
        nodes = [{"objectPath": "/u/B", "name": "B", "kind": "softwareUnit"}, {"objectPath": "/db/B", "name": "B", "kind": "block"}]
        relations = [{"relationType": "SoftwareUnit", "relatedObject": "B"}]
        good = {"edges": [{"kind": "unitRelation", "source": {"objectPath": "/u/A"}, "target": {"objectPath": "/u/B"},
                           "evidence": {"resolution": "resolved", "native": relations[0]}}]}
        self.assertEqual(validate_unit_edges(good, "/u/A", relations, nodes), [["B", "/u/B"]])
        unresolved = copy.deepcopy(good)
        unresolved["edges"][0]["evidence"]["resolution"] = "unresolved"
        self.assertEqual(validate_unit_edges(unresolved, "/u/A", relations, nodes), [["B", None]])
        wrong_kind = copy.deepcopy(good)
        wrong_kind["edges"][0]["target"]["objectPath"] = "/db/B"
        with self.assertRaises(AssertionError):
            validate_unit_edges(wrong_kind, "/u/A", relations, nodes)
        with self.assertRaises(AssertionError):
            validate_unit_edges({"edges": []}, "/u/A", relations, nodes)


class SeededFixtureTests(unittest.TestCase):
    MODULE = "/projects/Fx/devices/PLC/items/Rail/items/" + ENCODER_MODULE_NAME
    NODES = [{"kind": "softwareUnit", "name": UNIT_A, "objectPath": "/u/A"}, {"kind": "softwareUnit", "name": UNIT_B, "objectPath": "/u/B"},
             {"kind": "hardware", "name": ENCODER_MODULE_NAME, "objectPath": MODULE},
             {"kind": "hardware", "name": ENCODER_MODULE_NAME, "objectPath": MODULE + "/items/" + ENCODER_MODULE_NAME}, {"kind": "technologyObject", "name": ENCODER, "objectPath": "/to/E"}]

    def test_exact_seeded_relations_pass(self):
        validate_seeded_providers(self.NODES, {"/to/E": [self.MODULE + "/items/Channel"]}, {"/u/A": [[UNIT_B, "/u/B"]], "/u/B": []})

    def test_unresolved_reversed_or_foreign_seeded_relations_fail(self):
        good_units, good_technology = {"/u/A": [[UNIT_B, "/u/B"]], "/u/B": []}, {"/to/E": [self.MODULE]}
        for technology, units in (
                (good_technology, {"/u/A": [[UNIT_B, None]], "/u/B": []}),
                (good_technology, {"/u/A": [], "/u/B": [[UNIT_A, "/u/A"]]}),
                ({"/to/E": []}, good_units),
                ({"/to/E": [self.MODULE + "Other"]}, good_units)):
            with self.assertRaises(AssertionError):
                validate_seeded_providers(self.NODES, technology, units)
        foreign = self.NODES + [{"kind": "hardware", "name": ENCODER_MODULE_NAME, "objectPath": "/projects/Fx/devices/Other/items/" + ENCODER_MODULE_NAME + "_x"}]
        with self.assertRaises(AssertionError):
            validate_seeded_providers(foreign, good_technology, good_units)

    def test_partial_or_undeclared_coverage_fails(self):
        graph = {"providerScope": {"providers": ["softwareUnitRelations"]},
                 "providerCoverage": [{"provider": "softwareUnitRelations", "scope": "/u/A", "status": "complete"}]}
        require_complete_coverage(graph, "softwareUnitRelations", "/u/A")
        partial = copy.deepcopy(graph)
        partial["providerCoverage"][0]["status"] = "partial"
        undeclared = copy.deepcopy(graph)
        undeclared["providerScope"]["providers"] = []
        for value, scope in ((partial, "/u/A"), (undeclared, "/u/A"), (graph, "/u/B")):
            with self.assertRaises(AssertionError):
                require_complete_coverage(value, "softwareUnitRelations", scope)


if __name__ == "__main__":
    unittest.main()
