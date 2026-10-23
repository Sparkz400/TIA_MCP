"""Independent, offline AC-01 contract tests. Never loads or executes vendor code."""
from __future__ import annotations

from copy import deepcopy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET


SPEC = importlib.util.spec_from_file_location("api_coverage_under_test", Path(__file__).with_name("api_coverage.py"))
coverage = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(coverage)

EXPORT = "M:Siemens.Engineering.ProjectBase.ExportProjectTexts(System.IO.FileInfo,Siemens.Engineering.Language,System.Collections.Generic.IEnumerable{Siemens.Engineering.Language},System.Collections.Generic.IEnumerable{Siemens.Engineering.TextCategory},System.Collections.Generic.IEnumerable{Siemens.Engineering.IEngineeringObject})"
VCI = "M:Siemens.Engineering.VersionControl.Workspace.MappedObjects(Siemens.Engineering.IEngineeringObject)"
INTERFACE = "M:Siemens.Engineering.IEngineeringObject.GetAttribute(System.String)"


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def seal(snapshot):
    snapshot["semanticSha256"] = coverage.digest_bytes(coverage.canonical(snapshot["payload"]))
    return snapshot


class Fixture:
    """Tiny independent repository and public SDK; fake DLL bytes must stay opaque."""

    def __init__(self, directory):
        self.root = Path(directory)
        self.api = self.root / "api"
        self.repo = self.root / "repo"
        self.dll = self.api / "net48/Test.Api.dll"
        self.xml = self.dll.with_suffix(".xml")
        self.xsd = self.api / "Schemas/root.xsd"
        self.registry_path = self.root / "registry.json"
        self.metadata_path = self.root / "metadata.json"
        self.catalog_path = self.repo / "contracts/feature-catalog.v1.json"
        self.host = self.repo / "src/TiaMcp.Host/ProjectTextTools.cs"
        self.worker = self.repo / "src/TiaMcp.Openness.V21/ProjectTexts.cs"
        for p in (self.dll, self.xsd, self.host, self.worker):
            p.parent.mkdir(parents=True, exist_ok=True)
        self.dll.write_bytes(b"NOT AN EXECUTABLE: offline metadata fixture\x00\x01")
        self.xml.write_text('<doc><assembly><name>Test.Api</name></assembly><members>'
                            '<member name="T:Siemens.Engineering.ProjectBase" />'
                            f'<member name="{EXPORT}"><summary>Fixture export</summary></member>'
                            f'<member name="{INTERFACE}" />'
                            '</members></doc>', encoding="utf-8")
        self.xsd.write_text('<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:fixture">'
                            '<xs:include schemaLocation="child.xsd"/>'
                            '<xs:element name="Text" type="xs:string"/></xs:schema>', encoding="utf-8")
        (self.xsd.parent / "child.xsd").write_text(
            '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"/>', encoding="utf-8")
        self.host.write_text('[McpServerTool(Name = "project_text_export")]\n'
                             'public object Export() { return CallAsync("fake.read"); }\n', encoding="utf-8")
        self.worker.write_text('switch (name) { case "fake.read": return 1; case "fake_import": return 2; }', encoding="utf-8")
        self.registry = {
            "domains": [{"id": f"D{i:02}", "releaseObligation": True, "releaseAccepted": False} for i in range(1, 18)] + [{"id": f"X{i:02}", "releaseObligation": True, "releaseAccepted": False} for i in range(1, 14)],
            "manualResiduals": [{"domain": f"D{i:02}"} for i in range(1, 18)] + [{"domain": f"X{i:02}"} for i in range(1, 14)],
            "featureObligations": [{"featureId": "project_text_export", "obligations": ["D03"]},
                                   {"featureId": "change.fake_import", "obligations": ["D03", "F03"]}],
            "operations": [{"id": "D03.project-text-export", "domain": "D03", "symbols": [EXPORT],
                            "releaseObligation": True, "releaseAccepted": False,
                            "implementation": "not_implemented", "qualification": "not_run", "availability": "unobserved"}],
        }
        self.catalog = {
            "features": [
                {"featureId": "project_text_export", "mcpTools": ["project_text_export"], "changeKind": None,
                 "implementation": "partial", "qualification": {"status": "verified", "evidenceIds": ["historical.fixture"]}},
                {"featureId": "change.fake_import", "mcpTools": [], "changeKind": "fake_import",
                 "qualification": {"status": "unverified", "evidenceIds": []}},
            ],
            "evidence": [{"id": "historical.fixture", "scope": "Synthetic retained fixture only"}],
        }
        self.metadata = {
            "inspectionMode": "metadataOnly", "nativeCodeExecuted": False, "targetProbed": False,
            "assemblies": [{"name": "Test.Api", "version": "21.0.0.0",
                            "sha256": coverage.digest_bytes(self.dll.read_bytes()),
                            "types": [{"name": "Siemens.Engineering.ProjectBase", "members": [
                                {"id": EXPORT, "kind": "method", "visibility": "public"}]}], "unresolved": []}],
        }
        self.save()

    def save(self):
        for path, value in ((self.registry_path, self.registry), (self.metadata_path, self.metadata), (self.catalog_path, self.catalog)):
            write_json(path, value)

    def collect(self):
        return coverage.collect(self.api, self.repo, self.registry_path, self.metadata_path)

    def api_files(self):
        return {"net48/Test.Api.dll": {"sha256": coverage.digest_bytes(self.dll.read_bytes())}}


class InputSafetyTests(unittest.TestCase):
    def test_rejects_malformed_xml(self):
        with self.assertRaises((ValueError, ET.ParseError)):
            coverage.xml_read(b"<doc><unterminated></doc>")

    def test_rejects_dtd_and_entity_declarations_in_all_encodings(self):
        samples = ('<!DOCTYPE doc [<!ENTITY x "expanded">]><doc>&x;</doc>',
                   '<!DOCTYPE doc SYSTEM "file:///not-to-read"><doc/>',
                   '<!ENTITY x SYSTEM "https://must-not-fetch.invalid/"><doc/>')
        for encoding in ("utf-8", "utf-16", "utf-16-le", "utf-16-be", "utf-32"):
            for sample in samples:
                with self.subTest(encoding=encoding, sample=sample):
                    with self.assertRaisesRegex(ValueError, "DTD/entity"):
                        coverage.xml_read(sample.encode(encoding))

    def test_rejects_xml_depth_limit(self):
        with self.assertRaisesRegex(ValueError, "depth"):
            coverage.xml_read(("<x>" * 130 + "</x>" * 130).encode())

    def test_rejects_duplicate_xml_member_ids(self):
        with self.assertRaisesRegex(ValueError, "duplicate XML member"):
            coverage.xml_symbols(b'<doc><members><member name="T:Example"/><member name="T:Example"/></members></doc>', "fixture.xml")

    def test_rejects_duplicate_json_keys_in_nested_objects(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "input.json"
            p.write_text('{"outer":{"value":false,"value":true}}', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Duplicate JSON key"):
                coverage.json_read(p)

    def test_within_rejects_escapes_and_network_or_absolute_paths(self):
        with tempfile.TemporaryDirectory() as td:
            for value in ("../escape", "nested/../../escape", "/absolute", "C:/outside", "\\\\server\\share", "https://example.invalid/x", "", "x\\y"):
                with self.subTest(value=value), self.assertRaises(ValueError):
                    coverage.within(Path(td), value)

    def test_schema_dependencies_are_recorded_without_fetching_unsafe_locations(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            for value in ("../../escape.xsd", "https://example.invalid/a.xsd", "file:///x.xsd", "C:/x.xsd"):
                data = f'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:include schemaLocation="{value}"/></xs:schema>'.encode()
                row = coverage.schema_inventory(data, "Schemas/test.xsd", root)
                self.assertEqual(row["dependencies"][0]["state"], "blocked_external_or_unsafe")
                self.assertIsNone(row["dependencies"][0]["resolved"])

    def test_schema_records_missing_namespace_only_and_present_dependencies(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "exists.xsd").write_text("<unused/>", encoding="utf-8")
            data = b'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:import namespace="urn:external"/><xs:include schemaLocation="missing.xsd"/><xs:include schemaLocation="exists.xsd"/></xs:schema>'
            rows = coverage.schema_inventory(data, "root.xsd", root)["dependencies"]
            self.assertEqual([r["state"] for r in rows], ["namespace_only", "missing", "present"])

    def test_write_new_refuses_existing_output_without_changing_bytes(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "snapshot.json"
            p.write_bytes(b"immutable prior evidence")
            with self.assertRaises(FileExistsError):
                coverage.write_new(p, {"replacement": True})
            self.assertEqual(p.read_bytes(), b"immutable prior evidence")


class ClassificationTests(unittest.TestCase):
    def test_parameter_interface_does_not_classify_domain_operation_as_infrastructure(self):
        for member in (EXPORT, VCI):
            with self.subTest(member=member):
                self.assertEqual(coverage.classify(member, {})["classification"], "untriaged")
                self.assertEqual(coverage.classify(member, {member: "reviewed.operation"}),
                                 {"classification": "semantic", "operationId": "reviewed.operation"})

    def test_exact_declaring_interface_is_infrastructure(self):
        self.assertEqual(coverage.classify(INTERFACE, {})["classification"], "infrastructure")
        self.assertEqual(coverage.classify("T:Siemens.Engineering.IEngineeringObject", {})["classification"], "infrastructure")
        self.assertEqual(coverage.classify("T:Siemens.Engineering.IEngineeringObjectLike", {})["classification"], "untriaged")


class SnapshotTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.f = Fixture(self.directory.name)

    def test_collect_retains_historical_scope_but_does_not_promote_support(self):
        result = self.f.collect()
        self.assertFalse(result["nativeExecuted"])
        self.assertFalse(result["targetProbed"])
        self.assertFalse(result["releaseAccepted"])
        features = result["payload"]["features"]
        self.assertTrue(all(f["releaseAccepted"] is False for f in features))
        historical = next(f for f in features if f["featureId"] == "project_text_export")
        self.assertEqual(historical["catalogRecord"]["qualification"]["status"], "verified")
        self.assertEqual(historical["mappingReview"], "handler_and_schema_review_required")
        self.assertTrue(result["payload"]["discrepancies"])
        self.assertTrue(result["payload"]["residuals"])

    def test_semantic_payload_is_deterministic_and_ignores_observation_timestamp(self):
        first, second = self.f.collect(), self.f.collect()
        self.assertEqual(first["payload"], second["payload"])
        self.assertEqual(first["semanticSha256"], second["semanticSha256"])
        second["observedAtUtc"] = "2099-01-01T00:00:00Z"
        coverage.validate(second)
        self.assertEqual(coverage.diff(first, second)["profilesToReview"], [])

    def test_semantic_digest_is_independent_of_json_object_key_order(self):
        self.assertEqual(coverage.canonical({"z": 1, "a": {"y": 2, "b": 3}}),
                         coverage.canonical({"a": {"b": 3, "y": 2}, "z": 1}))

    def test_collect_does_not_modify_inputs_or_invoke_subprocess(self):
        inputs = {p: p.read_bytes() for p in self.f.root.rglob("*") if p.is_file()}
        with patch.object(subprocess, "Popen", side_effect=AssertionError("No execution permitted")):
            self.f.collect()
        self.assertEqual(inputs, {p: p.read_bytes() for p in self.f.root.rglob("*") if p.is_file()})

    def test_build_outputs_do_not_become_source_bindings(self):
        p = self.f.repo / "src/TiaMcp.Host/obj/duplicate.cs"
        p.parent.mkdir(parents=True)
        p.write_text(self.f.host.read_text(encoding="utf-8"), encoding="utf-8")
        result = self.f.collect()
        self.assertFalse(any("/obj/" in row["path"] for row in result["payload"]["sourceInputs"]))

    def test_metadata_requires_nonexecuting_mode_and_explicit_false_flags(self):
        for field, value in (("inspectionMode", "reflectionLoad"), ("nativeCodeExecuted", True),
                             ("nativeCodeExecuted", None), ("targetProbed", True), ("targetProbed", None)):
            with self.subTest(field=field, value=value):
                report = deepcopy(self.f.metadata)
                report[field] = value
                with self.assertRaisesRegex(ValueError, "non-executing"):
                    coverage.metadata_members(report, self.f.api_files())

    def test_metadata_hash_must_match_configured_dll(self):
        self.f.metadata["assemblies"][0]["sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "Stale/ambiguous"):
            coverage.metadata_members(self.f.metadata, self.f.api_files())

    def test_metadata_must_cover_all_configured_dlls(self):
        files = self.f.api_files()
        files["net48/Uninspected.dll"] = {"sha256": "1" * 64}
        with self.assertRaisesRegex(ValueError, "configured DLL"):
            coverage.metadata_members(self.f.metadata, files)

    def test_metadata_ambiguous_basename_and_digest_is_rejected(self):
        files = self.f.api_files()
        files["duplicate/Test.Api.dll"] = deepcopy(files["net48/Test.Api.dll"])
        with self.assertRaisesRegex(ValueError, "Stale/ambiguous"):
            coverage.metadata_members(self.f.metadata, files)

    def test_duplicate_metadata_member_rejected(self):
        typ = self.f.metadata["assemblies"][0]["types"][0]
        typ["members"].append(deepcopy(typ["members"][0]))
        with self.assertRaisesRegex(ValueError, "Duplicate metadata"):
            coverage.metadata_members(self.f.metadata, self.f.api_files())

    def test_duplicate_metadata_type_rejected_even_without_members(self):
        asm = self.f.metadata["assemblies"][0]
        asm["types"].append({"name": asm["types"][0]["name"], "members": []})
        with self.assertRaises(ValueError):
            coverage.metadata_members(self.f.metadata, self.f.api_files())

    def test_duplicate_metadata_assembly_rejected(self):
        self.f.metadata["assemblies"].append(deepcopy(self.f.metadata["assemblies"][0]))
        with self.assertRaises(ValueError):
            coverage.metadata_members(self.f.metadata, self.f.api_files())

    def test_metadata_unresolved_items_are_retained_not_passed(self):
        self.f.metadata["assemblies"][0]["unresolved"] = [{"kind": "missing-dependency", "name": "Optional.Api"}]
        self.f.save()
        result = self.f.collect()
        self.assertEqual(result["payload"]["metadataUnresolved"][0]["items"][0]["name"], "Optional.Api")
        self.assertFalse(result["releaseAccepted"])

    def test_catalog_host_tool_mismatch_is_rejected(self):
        self.f.catalog["features"][0]["mcpTools"] = ["absent_tool"]
        self.f.save()
        with self.assertRaisesRegex(ValueError, "Catalog/Host"):
            self.f.collect()

    def test_duplicate_catalog_feature_is_rejected(self):
        self.f.catalog["features"].append(deepcopy(self.f.catalog["features"][0]))
        self.f.save()
        with self.assertRaisesRegex(ValueError, "Duplicate catalog"):
            self.f.collect()

    def test_unresolved_catalog_evidence_is_rejected(self):
        self.f.catalog["features"][0]["qualification"]["evidenceIds"] = ["not-present"]
        self.f.save()
        with self.assertRaisesRegex(ValueError, "evidence reference"):
            self.f.collect()

    def test_seed_operation_cannot_be_promoted_by_metadata(self):
        for field, value in (("implementation", "implemented"), ("qualification", "verified"),
                             ("availability", "available"), ("releaseAccepted", True), ("releaseObligation", False)):
            with self.subTest(field=field):
                registry = deepcopy(self.f.registry)
                registry["operations"][0][field] = value
                with self.assertRaises(ValueError):
                    coverage.decision_index(registry)

    def test_seed_registry_requires_exact_thirty_unique_domains(self):
        for replacement in ("D00", "D02"):
            registry = deepcopy(self.f.registry)
            registry["domains"][0]["id"] = replacement
            with self.subTest(replacement=replacement), self.assertRaises(ValueError):
                coverage.decision_index(registry)

    def test_reviewed_symbol_must_exist_in_collected_surface(self):
        self.f.registry["operations"][0]["symbols"] = ["M:Does.Not.Exist"]
        self.f.save()
        with self.assertRaisesRegex(ValueError, "symbols absent"):
            self.f.collect()

    def test_hash_tamper_is_rejected(self):
        result = self.f.collect()
        result["payload"]["files"][0]["bytes"] += 1
        with self.assertRaisesRegex(ValueError, "hash mismatch"):
            coverage.validate(result)

    def test_rehashed_snapshot_cannot_claim_execution_or_feature_acceptance(self):
        result = self.f.collect()
        for field in ("nativeExecuted", "targetProbed", "releaseAccepted"):
            bad = deepcopy(result)
            bad[field] = True
            with self.subTest(field=field), self.assertRaises(ValueError):
                coverage.validate(seal(bad))
        bad = deepcopy(result)
        bad["payload"]["features"][0]["releaseAccepted"] = True
        with self.assertRaises(ValueError):
            coverage.validate(seal(bad))

    def test_rehashed_classification_tamper_is_rejected(self):
        result = self.f.collect()
        row = next(r for r in result["payload"]["symbols"] if r["id"] == EXPORT)
        row["classification"] = "infrastructure"
        with self.assertRaisesRegex(ValueError, "classification"):
            coverage.validate(seal(result))

    def test_rehashed_duplicate_symbols_are_rejected(self):
        result = self.f.collect()
        result["payload"]["symbols"].append(deepcopy(result["payload"]["symbols"][0]))
        with self.assertRaisesRegex(ValueError, "Duplicate symbols"):
            coverage.validate(seal(result))

    def test_rehashed_missing_reviewed_symbols_are_rejected(self):
        result = self.f.collect()
        result["payload"]["symbols"] = [r for r in result["payload"]["symbols"] if r["id"] != EXPORT]
        with self.assertRaises(ValueError):
            coverage.validate(seal(result))

    def test_rehashed_unsafe_manifest_paths_and_bad_hashes_are_rejected(self):
        result = self.f.collect()
        for field, value in (("path", "../escape.dll"), ("path", "C:/outside.dll"),
                             ("sha256", "not-a-hash"), ("bytes", -1)):
            bad = deepcopy(result)
            bad["payload"]["files"][0][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                coverage.validate(seal(bad))

    def test_schema_only_change_selects_profiles_and_features_for_review(self):
        before = self.f.collect()
        self.f.xsd.write_text(self.f.xsd.read_text(encoding="utf-8").replace('name="Text"', 'name="ChangedText"'), encoding="utf-8")
        change = coverage.diff(before, self.f.collect())
        self.assertIn("Schemas/root.xsd", change["schemas"]["changed"])
        self.assertIn("Schemas/root.xsd", change["apiInputs"]["changed"])
        self.assertEqual(change["profilesToReview"], ["historical.fixture"])
        self.assertEqual(change["affectedFeatureIds"], ["change.fake_import", "project_text_export"])
        self.assertFalse(change["qualificationPromoted"])
        self.assertFalse(change["nativeExecuted"])

    def test_api_binary_only_change_selects_profiles_without_member_delta(self):
        before = self.f.collect()
        self.f.dll.write_bytes(b"DIFFERENT OPAQUE FIXTURE DLL BYTES; NEVER EXECUTE")
        self.f.metadata["assemblies"][0]["sha256"] = coverage.digest_bytes(self.f.dll.read_bytes())
        self.f.save()
        change = coverage.diff(before, self.f.collect())
        self.assertIn("net48/Test.Api.dll", change["apiInputs"]["changed"])
        self.assertEqual(change["symbols"], {"added": [], "removed": [], "changed": []})
        self.assertEqual(change["profilesToReview"], ["historical.fixture"])

    def test_source_only_change_selects_conservative_review(self):
        before = self.f.collect()
        self.f.worker.write_text(self.f.worker.read_text(encoding="utf-8") + "\n// changed implementation\n", encoding="utf-8")
        change = coverage.diff(before, self.f.collect())
        self.assertIn("src/TiaMcp.Openness.V21/ProjectTexts.cs", change["sourceInputs"]["changed"])
        self.assertTrue(change["profilesToReview"])
        self.assertTrue(change["proposedOnly"])

    def test_removed_feature_and_profile_still_require_impact_review(self):
        before = self.f.collect()
        self.f.catalog["features"] = [self.f.catalog["features"][0]]
        self.f.registry["featureObligations"] = [self.f.registry["featureObligations"][0]]
        self.f.catalog["features"][0]["qualification"]["evidenceIds"] = []
        self.f.catalog["evidence"] = []
        self.f.save()
        change = coverage.diff(before, self.f.collect())
        self.assertIn("change.fake_import", change["affectedFeatureIds"])
        self.assertIn("historical.fixture", change["profilesToReview"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
