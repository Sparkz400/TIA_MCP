"""Offline checks of the typed profile candidate audit; no native evidence."""
import copy
import hashlib
import json
import pathlib
import tempfile
import unittest

from audit_typed_profile_candidates import ROOT, audit

NATIVE = ('<Document><SW.Blocks.FC ID="0"><AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>'
          '<ObjectList><SW.Blocks.CompileUnit ID="1"><AttributeList><NetworkSource><FlgNet xmlns="urn:net"><Parts>'
          '<Part Name="Ne" UId="21"/></Parts></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>')


class CandidateAuditTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(dir=ROOT)
        base = pathlib.Path(self.folder.name)
        (base / "native").mkdir()
        native, authored = base / "native" / "X.xml", base / "x.s7dcl"
        native.write_text(NATIVE, encoding="utf-8"); authored.write_text("authored", encoding="utf-8")
        digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
        rel = lambda p: p.relative_to(ROOT).as_posix()
        self.catalog = {"sources": [{"path": rel(native), "sha256": digest(native), "authoredSourcePath": rel(authored),
                "authoredSourceSha256": digest(authored), "changeId": "c1", "builderUsed": False, "runtimeVerified": False}],
            "profiles": [{"id": "p", "sourceSha256": digest(native), "provenance": rel(native), "route": "softwareUnit", "cpuFamily": "OrderNumber:X",
                "language": "LAD", "nativeFormatValidated": True, "nativeTemplateXml": '<Part xmlns="urn:net" Name="Ne" UId="21"/>',
                "networkTemplateXml": NATIVE[NATIVE.index("<SW.Blocks.CompileUnit"):NATIVE.index("</ObjectList>")],
                **{gate: False for gate in ("engineeringVerified", "preservationVerified", "persistenceVerified", "recoveryVerified", "runtimeVerified")}}]}
        self.capture = {"fixtures": [{"sourcePath": str(authored), "changeId": "c1", "engineeringVerified": True, "route": "softwareUnit",
                "exports": [{"sha256": digest(native)}]}],
            "calls": [{"tool": "block_read", "status": "passed", "result": {"artifact": {"sha256": digest(native)}}}],
            "hardware": {"items": [{"classification": "CPU", "attributes": [{"name": "TypeIdentifier", "value": "OrderNumber:X"}]}]}}
        self.base = base

    def tearDown(self):
        self.folder.cleanup()

    def run_audit(self, catalog, capture):
        (self.base / "catalog.json").write_text(json.dumps(catalog), encoding="utf-8")
        (self.base / "capture.json").write_text(json.dumps(capture), encoding="utf-8")
        return audit(self.base / "catalog.json", self.base / "capture.json")

    def test_exact_bindings_pass_and_each_drift_fails(self):
        self.assertEqual([], self.run_audit(self.catalog, self.capture)["findings"])
        for change in (lambda c, r: c["profiles"][0].update(nativeTemplateXml='<Part xmlns="urn:net" Name="Lt" UId="21"/>'),
                       lambda c, r: c["profiles"][0].update(route="namespace"),
                       lambda c, r: c["profiles"][0].update(runtimeVerified=True),
                       lambda c, r: c["sources"][0].update(authoredSourceSha256="0" * 64),
                       lambda c, r: r["calls"].clear()):
            catalog, capture = copy.deepcopy(self.catalog), copy.deepcopy(self.capture)
            change(catalog, capture)
            self.assertFalse(self.run_audit(catalog, capture)["passed"])

    def test_a_capture_from_another_checkout_binds_only_an_identical_source(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "fixtures") as folder:
            authored = pathlib.Path(folder).resolve() / "x.s7dcl"
            authored.write_text("authored", encoding="utf-8")
            catalog, capture = copy.deepcopy(self.catalog), copy.deepcopy(self.capture)
            catalog["sources"][0].update(authoredSourcePath=authored.relative_to(ROOT).as_posix(),
                                         authoredSourceSha256=hashlib.sha256(authored.read_bytes()).hexdigest())
            entry = capture["fixtures"][0]
            # Not the temp directory: Test-Offline.ps1 points TEMP inside this repository's artifacts.
            entry["sourcePath"] = str(pathlib.Path(ROOT.anchor) / ("other-checkout-" + pathlib.Path(folder).name) / authored.relative_to(ROOT))
            for digest, passed in ((catalog["sources"][0]["authoredSourceSha256"], True), ("0" * 64, False)):
                with self.subTest(passed=passed):
                    entry["fileSha256"] = digest
                    self.assertEqual(passed, self.run_audit(catalog, capture)["passed"])

    def test_prerequisite_number_is_ignored_only_when_auto_numbered(self):
        def db(number, auto):
            return (f'<Document><SW.Blocks.GlobalDB ID="0"><AttributeList><AutoNumber>{auto}</AutoNumber>'
                    f'<Name>D</Name><Number>{number}</Number></AttributeList></SW.Blocks.GlobalDB></Document>')
        (self.base / "catalog.json").write_text(json.dumps(self.catalog), encoding="utf-8")
        (self.base / "capture.json").write_text(json.dumps(self.capture), encoding="utf-8")
        committed = self.base / "committed" / "D.xml"
        committed.parent.mkdir()
        for auto, expected in (("true", True), ("false", False)):
            with self.subTest(auto=auto):
                committed.write_text(db(3, auto), encoding="utf-8")
                (self.base / "native" / "D.xml").write_text(db(4, auto), encoding="utf-8")
                result = audit(self.base / "catalog.json", self.base / "capture.json", [committed])
                self.assertEqual(result["passed"], expected)


if __name__ == "__main__":
    unittest.main()
