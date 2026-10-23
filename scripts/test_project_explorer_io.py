"""The I/O qualifier must reject overlap-only, reversed, or wrong-object evidence."""
import copy
import unittest

from qualify_project_explorer_io import validate_io_graph


class ConfiguredIoOracleTests(unittest.TestCase):
    def setUp(self):
        self.native_range = {"startAddress": 256, "length": 32}
        self.graph = {"edges": [{"kind": "configuredIo", "source": {"objectPath": "/tag"},
            "target": {"objectPath": "/hardware"}, "unresolved": None, "evidence": {
                "provider": "nativeConfiguredAddress", "origin": "nativeConfirmed", "resolution": "resolved",
                "sourcePath": "/hardware", "capturedAtUtc": "2026-10-08T00:00:00Z",
                "contentRevision": {"status": "unavailable", "scope": "configuredAddressRange"},
                "native": {"plcOwnershipVerified": True, "ownershipResolution": "verified", "hardwarePath": "/hardware",
                           "queryAddress": "%IB256", "ioType": "Input", "startAddress": 256, "length": 32, "lengthUnit": "bits"}}}]}

    def validate(self, graph):
        return validate_io_graph(graph, "/tag", "/hardware", "%IB256", self.native_range)

    def test_exact_positive(self):
        self.assertEqual(self.validate(self.graph)["kind"], "configuredIo")

    def test_unverified_overlap_rejected(self):
        graph = copy.deepcopy(self.graph)
        graph["edges"][0]["evidence"]["native"]["plcOwnershipVerified"] = False
        with self.assertRaises(AssertionError):
            self.validate(graph)

    def test_reverse_semantic_direction_rejected(self):
        graph = copy.deepcopy(self.graph)
        edge = graph["edges"][0]
        edge["source"], edge["target"] = edge["target"], edge["source"]
        with self.assertRaises(AssertionError):
            self.validate(graph)

    def test_same_named_hardware_substitution_rejected(self):
        graph = copy.deepcopy(self.graph)
        graph["edges"][0]["target"]["objectPath"] = "/otherPlc/hardware"
        with self.assertRaises(AssertionError):
            self.validate(graph)

    def test_native_address_range_units_must_match(self):
        graph = copy.deepcopy(self.graph)
        graph["edges"][0]["evidence"]["native"]["lengthUnit"] = "bytes"
        with self.assertRaises(AssertionError):
            self.validate(graph)

    def test_metadata_hash_cannot_substitute_content_revision(self):
        graph = copy.deepcopy(self.graph)
        graph["edges"][0]["evidence"]["contentRevision"] = {"status": "available", "value": "metadata-hash"}
        with self.assertRaises(AssertionError):
            self.validate(graph)


if __name__ == "__main__":
    unittest.main()
