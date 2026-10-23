"""Offline candidate extraction stays separate from the deployment catalog."""
import hashlib
import json
import pathlib
import tempfile
import unittest
from unittest import mock

import extract_typed_graphical_profiles as extractor


class CandidateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name).resolve()
        patch = mock.patch.object(extractor, "ROOT", self.root)
        patch.start()
        self.addCleanup(patch.stop)
        self.deploy = self.root / "fixtures/typed-graphical"
        (self.deploy / "native").mkdir(parents=True)
        self.catalog = self.deploy / "profiles.v1.json"
        self.catalog.write_bytes(b"unchanged deployment catalog")
        self.deployed_native = self.deploy / "native/Tgp_TON_LAD.xml"
        self.deployed_native.write_bytes(b"unchanged deployment reference")
        self.stage = self.root / "artifacts/candidates"
        captured = self.root / "artifacts/capture"
        captured.mkdir(parents=True)
        self.source = self.deploy / "reference.s7dcl"
        self.source.write_text("independent authored source\n", encoding="utf-8")
        self.native = captured / "Tgp_TON_LAD.xml"
        self.native.write_text('<Document><SW.Blocks.FB><AttributeList><Namespace>Unit</Namespace>'
            '<Interface><Member Name="Storage" Datatype="IEC_TIMER" /></Interface></AttributeList>'
            '<SW.Blocks.CompileUnit><NetworkSource><Part Name="TON" UId="1" /></NetworkSource></SW.Blocks.CompileUnit>'
            '</SW.Blocks.FB></Document>', encoding="utf-8")
        self.report_path = captured / "capture.json"
        self.report = {"origin": "independently_authored_native_references", "builderUsed": False,
            "scratch": {"selectedCpu": {"typeIdentifier": "CpuFixture"}},
            "fixtures": [{"engineeringVerified": True, "sourcePath": str(self.source), "changeId": "fixture",
                "sha256": hashlib.sha256(self.source.read_text().encode()).hexdigest(),
                "exports": [{"name": "Tgp_TON_LAD"}]}],
            "calls": [{"tool": "block_read", "status": "passed", "result": {"block": {"name": "Tgp_TON_LAD"},
                "artifact": {"path": str(self.native), "sha256": hashlib.sha256(self.native.read_bytes()).hexdigest()}}}]}
        block = {"name": "Tgp_TON_LAD", "path": "/plc/Tgp_TON_LAD", "scopePath": "/plc", "identity": {"namespace": ""}}
        self.report["calls"][0]["result"]["block"] = block
        self.report["fixtures"][0].update(scopePath="/plc", namespace="")
        self.report["fixtures"][0]["exports"][0].update(block=block,
            sha256=self.report["calls"][0]["result"]["artifact"]["sha256"])
        self.write_report()

    def write_report(self):
        self.report_path.write_text(json.dumps(self.report), encoding="utf-8")

    def unchanged(self):
        self.assertEqual(b"unchanged deployment catalog", self.catalog.read_bytes())
        self.assertEqual(b"unchanged deployment reference", self.deployed_native.read_bytes())

    def test_candidates_copy_native_bytes_without_enabling_profile_or_overwriting_deployment(self):
        result, output = extractor.extract_catalog([self.report_path], stage_output=self.stage)
        self.assertTrue(result["candidateOnly"])
        self.assertEqual(self.stage / "profiles.candidate.v1.json", output)
        self.assertEqual(1, len(result["profiles"]))
        profile = result["profiles"][0]
        for key in ("engineeringVerified", "preservationVerified", "persistenceVerified", "recoveryVerified", "runtimeVerified"):
            self.assertFalse(profile[key])
        self.assertTrue(profile["provenance"].startswith("artifacts/candidates/"))
        self.assertEqual(self.native.read_bytes(), (self.stage / "native/Tgp_TON_LAD.xml").read_bytes())
        self.unchanged()

    def test_staging_cannot_append_to_deployment_catalog(self):
        with self.assertRaisesRegex(ValueError, "cannot append"):
            extractor.extract_catalog([self.report_path], stage_output=self.stage, append=True)
        self.assertFalse(self.stage.exists())
        self.unchanged()

    def test_existing_candidate_output_is_immutable(self):
        self.stage.mkdir(parents=True)
        with self.assertRaises(FileExistsError):
            extractor.extract_catalog([self.report_path], stage_output=self.stage)
        self.unchanged()

    def test_candidate_output_cannot_escape_artifacts(self):
        for destination in (self.deploy, self.root / "elsewhere", self.root / "artifacts/../outside"):
            with self.subTest(destination=destination), self.assertRaisesRegex(ValueError, "under repository artifacts"):
                extractor.extract_catalog([self.report_path], stage_output=destination)
        self.unchanged()

    def test_failed_native_hash_check_cannot_change_deployment(self):
        self.native.write_bytes(self.native.read_bytes() + b" ")
        with self.assertRaises(AssertionError):
            extractor.extract_catalog([self.report_path], stage_output=self.stage)
        self.unchanged()

    def test_missing_template_dependency_is_recorded_and_not_invented(self):
        self.report["fixtures"][0]["route"] = "namespace"
        self.report["fixtures"][0]["exports"][0]["name"] = "TgpNamespace_GlobalTemplateFB_LAD"
        self.report["calls"][0]["result"]["block"]["name"] = "TgpNamespace_GlobalTemplateFB_LAD"
        self.write_report()
        result, _ = extractor.extract_catalog([self.report_path], [self.report_path], stage_output=self.stage)
        self.assertEqual([], result["globalInstanceTemplates"])
        self.assertEqual([{"block": "Tgp_GlobalTemplateFB_LAD", "missingReference": "Tgp_TemplateFB_LAD", "route": "namespace"}], result["unresolvedTemplates"])
        self.unchanged()

    def test_duplicate_native_filename_cannot_overwrite_first_candidate(self):
        with self.assertRaisesRegex(ValueError, "filename collision"):
            extractor.extract_catalog([self.report_path, self.report_path], stage_output=self.stage)
        self.assertEqual(self.native.read_bytes(), (self.stage / "native/Tgp_TON_LAD.xml").read_bytes())
        self.unchanged()

    def test_wrong_scope_identity_is_rejected_before_writing_native_candidate(self):
        self.report["fixtures"][0]["namespace"] = "other"
        self.write_report()
        with self.assertRaisesRegex(ValueError, "scope/namespace identity"):
            extractor.extract_catalog([self.report_path], stage_output=self.stage)
        self.assertFalse((self.stage / "native/Tgp_TON_LAD.xml").exists())
        self.unchanged()

    def test_missing_scope_or_namespace_is_not_an_exact_candidate_identity(self):
        for field in ("scopePath", "namespace"):
            with self.subTest(field=field):
                entry = self.report["fixtures"][0]
                entry[field] = None
                identity = entry["exports"][0]["block"]
                if field == "scopePath": identity[field] = None
                else: identity["identity"][field] = None
                self.write_report()
                with self.assertRaisesRegex(ValueError, "scope/namespace identity"):
                    extractor.extract_catalog([self.report_path], stage_output=self.stage / field)
        self.unchanged()

    def test_report_native_name_cannot_escape_staging_or_use_alternate_stream(self):
        for ordinal, name in enumerate(("../escape", "nested/name", "nested\\name", "name:stream")):
            with self.subTest(name=name):
                self.report["fixtures"][0]["exports"][0]["name"] = name
                self.report["calls"][0]["result"]["block"]["name"] = name
                self.write_report()
                with self.assertRaisesRegex(ValueError, "directly inside staging/native"):
                    extractor.extract_catalog([self.report_path], stage_output=self.stage / str(ordinal))
        self.unchanged()
        self.assertFalse((self.root / "escape.xml").exists())


if __name__ == "__main__":
    unittest.main()
