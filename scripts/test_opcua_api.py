"""File-only metadata audit tests. No Siemens assembly execution or target attachment."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from probe_opcua_api import inspect_api, xml_read


class OpcUaApiTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.api = self.root / "net48"
        self.api.mkdir()
        for name, member in [("Siemens.Engineering.Base", "T:Siemens.Engineering.HW.OpcUaSecurityPolicies"),
                             ("Siemens.Engineering.Step7", "T:Siemens.Engineering.SW.OpcUa.OpcUaProvider")]:
            (self.api / (name + ".dll")).write_bytes(b"not executable test metadata")
            (self.api / (name + ".xml")).write_text(
                '<doc><members><member name="' + member + '"><summary>Test</summary></member></members></doc>', encoding="utf-8")

    def test_optional_missing_does_not_mean_target_absent(self):
        report = inspect_api(self.root)
        self.assertFalse(report["targetProbed"])
        self.assertFalse(report["nativeQualified"])
        self.assertFalse(report["nativeCodeExecuted"])
        self.assertEqual(1, len(report["assemblies"]["Siemens.Engineering.Step7"]["members"]))
        self.assertEqual(2, len(report["diagnostics"]))

    def test_accessors_are_bound_to_installed_bytes(self):
        evidence = {"schemaVersion": 1, "inspectionMode": "metadataOnly", "nativeCodeExecuted": False, "targetProbed": False, "assemblies": [
            {"name": "Siemens.Engineering.Step7", "sha256": hashlib.sha256(b"not executable test metadata").hexdigest()}]}
        self.assertEqual(evidence, inspect_api(self.api, evidence)["accessorEvidence"])
        evidence["assemblies"][0]["sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "does not match"):
            inspect_api(self.api, evidence)

    def test_empty_or_execution_report_is_rejected(self):
        for report in [{"schemaVersion": 1, "inspectionMode": "metadataOnly", "assemblies": []},
                       {"schemaVersion": 1, "inspectionMode": "execution"}]:
            with self.assertRaises(ValueError): inspect_api(self.api, report)

    def test_dtd_utf8_and_utf16_rejected(self):
        for encoding in ["utf-8", "utf-16"]:
            path = self.root / "unsafe.xml"
            path.write_text('<!DOCTYPE doc [<!ENTITY x "expanded">]><doc>&x;</doc>', encoding=encoding)
            with self.assertRaisesRegex(ValueError, "DTD/entity"): xml_read(path)

    def test_metadata_capacity(self):
        path = self.root / "large.xml"
        path.write_text("<doc />", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "limit"): xml_read(path, maximum=2)

    def test_malformed_document_is_explicit(self):
        (self.api / "Siemens.Engineering.Step7.xml").write_text("<doc>", encoding="utf-8")
        self.assertTrue(any(d["code"] == "DocumentationInvalid" for d in inspect_api(self.api)["diagnostics"]))

    def test_schema_inventory_does_not_imply_ua_support(self):
        directory = self.root / "Schemas"
        directory.mkdir()
        (directory / "Other.xsd").write_text('<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:other"><xs:import schemaLocation="missing.xsd"/></xs:schema>', encoding="utf-8")
        report = inspect_api(self.api)
        self.assertEqual(["missing.xsd"], report["schemas"][0]["dependencies"])
        self.assertEqual("urn:other", report["schemas"][0]["targetNamespace"])

    def test_candidate_profiles_fail_closed(self):
        root = Path(__file__).resolve().parents[1]
        profiles = json.loads((root / "contracts/opc-ua-profiles.v1.json").read_text(encoding="utf-8"))
        self.assertFalse(profiles["nativeQualified"])
        for profile in profiles["profiles"]:
            self.assertFalse(profile["nativeQualified"])
            self.assertIsNone(profile["targetAvailable"])
            self.assertIsNone(profile["formatProfile"])
            self.assertTrue(profile["blockers"])
        for attribute in profiles["documentedAttributeCandidates"]:
            self.assertFalse(attribute["writeEnabled"])
        schema = profiles["offlineSchema"]
        self.assertEqual(schema["sha256"], hashlib.sha256((root / schema["path"]).read_bytes()).hexdigest())
        self.assertFalse(schema["tiaImportAccepted"])

    def test_consumer_fixture_matches_exact_source(self):
        root = Path(__file__).resolve().parents[1] / "fixtures/opcua"
        consumers = json.loads((root / "consumers-v1.json").read_text(encoding="utf-8"))
        self.assertEqual(consumers["modelSha256"], hashlib.sha256((root / "machine-v1.xml").read_bytes()).hexdigest())
        self.assertEqual({"Historian-A", "Dashboard-B", "MES-C"}, {item["id"] for item in consumers["consumers"]})


if __name__ == "__main__":
    unittest.main()
