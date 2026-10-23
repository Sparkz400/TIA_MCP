import copy
import unittest
import tempfile
from pathlib import Path
from library_hmi_evidence import assess
from verify_library_hmi_screen import compare


class HmiEvidenceTests(unittest.TestCase):
    def fixture(self):
        names = ["start-owned-tia", "create-owned-project", "catalog", "create-explicit-hmi-devices",
                 "save-project", "close-owned-project", "reopen-owned-project", "verify-reopened-fixture",
                 "archive-owned-project", "dispose-owned-portal-connection", "create-unified-screen-and-tag", "compile-unified"]
        return {"profile": {"family": "unified"}, "stages": [{"name": n, "status": "passed"} for n in names],
                "compile": {"errors": 0}, "completed": True, "persistenceVerified": True}

    def test_complete_foundation_does_not_claim_full_feature(self):
        result = assess(self.fixture())
        self.assertTrue(result["foundationVerified"])
        self.assertFalse(result["fullLibraryHmiAccepted"])
        self.assertFalse(result["faceplateVerified"])

    def test_every_native_stage_is_required(self):
        fixture = self.fixture()
        for stage in fixture["stages"]:
            value = copy.deepcopy(fixture)
            value["stages"] = [s for s in value["stages"] if s["name"] != stage["name"]]
            self.assertFalse(assess(value)["foundationVerified"], stage["name"])

    def test_reopen_without_readback_is_not_persistence(self):
        value = self.fixture()
        value.pop("persistenceVerified")
        self.assertFalse(assess(value)["foundationVerified"])

    def test_failed_compile_does_not_pass(self):
        value = self.fixture()
        value["compile"]["errors"] = 1
        self.assertFalse(assess(value)["foundationVerified"])

    def test_inflight_stage_is_not_pass(self):
        value = self.fixture()
        value["stages"][0]["status"] = "invoking"
        self.assertFalse(assess(value)["foundationVerified"])

    def test_catalog_only_does_not_qualify_family(self):
        self.assertFalse(assess({"completed": True, "stages": [{"name": "catalog", "status": "passed"}]})["foundationVerified"])

    def test_unbacked_runtime_claims_are_ignored(self):
        value = self.fixture()
        value.update(runtimeVerified=True, visualVerified=True, fullLibraryHmiAccepted=True)
        result = assess(value)
        self.assertFalse(result["runtimeVerified"])
        self.assertFalse(result["visualVerified"])
        self.assertFalse(result["fullLibraryHmiAccepted"])

    def compare_xml(self, other):
        original = '<Document><Engineering version="V21"/><DocumentInfo><Created>one</Created></DocumentInfo><Hmi.Screen.Screen ID="0"><AttributeList><Name>Screen</Name><Binding>Motor</Binding><Script>  return value; </Script></AttributeList></Hmi.Screen.Screen></Document>'
        root = Path(__file__).resolve().parents[1]/"artifacts"/"hmi-evidence-tests"
        root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=root) as folder:
            assert Path(folder).resolve().is_relative_to(root.resolve())
            first, second = Path(folder)/"a.xml", Path(folder)/"b.xml"
            first.write_text(original, encoding="utf-8")
            second.write_text(other(original), encoding="utf-8")
            return compare([first, second])["exportedScreenPreserved"]

    def test_export_metadata_and_indentation_are_excluded(self):
        self.assertTrue(self.compare_xml(lambda xml: xml.replace("one", "two").replace("<AttributeList>", "\n<AttributeList>\n")))

    def test_changed_binding_is_detected(self):
        self.assertFalse(self.compare_xml(lambda xml: xml.replace("Motor", "OtherMotor")))

    def test_native_object_id_change_is_detected(self):
        self.assertFalse(self.compare_xml(lambda xml: xml.replace('ID="0"', 'ID="1"')))

    def test_script_whitespace_is_not_globally_stripped(self):
        self.assertFalse(self.compare_xml(lambda xml: xml.replace("  return value; ", "return value;")))

    def test_unexpected_root_is_rejected(self):
        with self.assertRaises(ValueError):
            self.compare_xml(lambda xml: xml.replace("</Document>", "<UnexpectedPayload/></Document>"))

    def test_entity_declaration_is_rejected(self):
        with self.assertRaises(ValueError):
            self.compare_xml(lambda xml: '<!DOCTYPE Document [<!ENTITY x "xx">]>'+xml)


if __name__ == "__main__":
    unittest.main()
