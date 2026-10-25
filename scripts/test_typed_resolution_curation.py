"""Offline checks of typed resolution curation; no native evidence."""
import json
import pathlib
import tempfile
import unittest

from typed_resolution_curation import curation_report


class ResolutionCurationTests(unittest.TestCase):
    def write(self, folder, name, report):
        path = pathlib.Path(folder) / name / "result.json"
        path.parent.mkdir()
        path.write_text(json.dumps(report), encoding="utf-8")
        return str(path)

    def test_passes_and_preserved_native_rejections_only(self):
        call = {"tool": "change_apply", "status": "passed"}
        with tempfile.TemporaryDirectory() as folder:
            passed = self.write(folder, "a", {"status": "passed", "cases": [{"id": "x", "status": "passed"}], "sessions": [{"calls": [call]}]})
            rejected = self.write(folder, "b", {"status": "blocked", "nativeDuplicateSetup": {"outcome": "native-rejected",
                "fullOriginalXmlPreserved": True, "result": {"nativeRejection": {"message": "object that already exists"}}}})
            unpreserved = self.write(folder, "c", {"status": "blocked", "namespaceReferenceProbe": {"status": "native-attempt-failed"},
                "failureOriginalXmlVerified": False, "failureRestore": {"failure": "is unknown"}})
            self.assertTrue(curation_report([(passed, "passed"), (rejected, "already exists")])["completed"])
            self.assertFalse(curation_report([(rejected, "is unknown")])["completed"])
            self.assertFalse(curation_report([(unpreserved, "is unknown")])["completed"])
            self.assertFalse(curation_report([(rejected, "passed")])["completed"])
            self.assertEqual([{"name": "change_apply", "isError": False}], curation_report([(passed, "passed")])["toolCalls"])


if __name__ == "__main__":
    unittest.main()
