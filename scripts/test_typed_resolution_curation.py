"""Offline checks of typed resolution curation; no native evidence."""
import json
import pathlib
import tempfile
import unittest

from typed_resolution_curation import capture_curation_report, curation_report


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

    def test_capture_requires_every_input_saved_reopen_and_clean_exit(self):
        passed = {"status": "passed", "completed": True, "persistenceVerified": True, "remainingSources": [],
                  "fixtures": [{"sourcePath": "a.scl", "engineeringVerified": True}], "calls": [{"tool": "change_apply", "status": "passed"}]}
        with tempfile.TemporaryDirectory() as folder:
            for change, expected in ((lambda r: None, True), (lambda r: r.update(persistenceVerified=False), False),
                    (lambda r: r.update(remainingSources=["b.scl"]), False), (lambda r: r.update(cleanupFailure="x"), False)):
                report = json.loads(json.dumps(passed)); change(report)
                path = pathlib.Path(folder) / "capture.json"; path.write_text(json.dumps(report), encoding="utf-8")
                self.assertEqual(expected, capture_curation_report(path)["completed"])


if __name__ == "__main__":
    unittest.main()
