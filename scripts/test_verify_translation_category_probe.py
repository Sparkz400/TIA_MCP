"""File-only adversarial checks for retained native category-request evidence."""
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import unittest

from verify_translation_category_probe import validate_request_matrix

CAPTURE = Path(__file__).resolve().parents[1] / "fixtures/translations/native-category-request-20261015"


class CategoryRequestEvidenceTests(unittest.TestCase):
    def setUp(self):
        provenance = json.loads((CAPTURE / "provenance.json").read_text(encoding="utf-8"))
        retained = {}
        self.assertEqual({entry["fixture"] for entry in provenance["files"]}, {"result.json", "admission.json"})
        for entry in provenance["files"]:
            raw = (CAPTURE / entry["fixture"]).read_bytes()
            self.assertEqual(len(raw), entry["bytes"], "Retained native fixture length changed")
            self.assertEqual(hashlib.sha256(raw).hexdigest(), entry["sha256"], "Retained native fixture bytes changed")
            retained[entry["fixture"]] = json.loads(raw.decode("utf-8-sig"))
        self.native = retained["result.json"]
        self.admission = retained["admission.json"]
        self.progress = copy.deepcopy(self.native["translations"])

    def verify(self):
        # Preserve receipt equality while challenging the scope content itself.
        self.native["translations"] = copy.deepcopy(self.progress)
        validate_request_matrix(self.native, self.admission, self.progress)

    def test_genuine_matrix(self):
        self.verify()

    def test_incomplete_all_category_baseline(self):
        self.progress["exports"][0]["categoryIds"] = ["HwcnCommentTextCategoryData"]
        with self.assertRaises(AssertionError): self.verify()

    def test_duplicated_inventory_identifier(self):
        self.progress["categories"].append(self.progress["categories"][0])
        with self.assertRaises(AssertionError): self.verify()

    def test_selected_categories_cannot_be_relabelled_as_all(self):
        self.progress["exports"][1]["categoryIds"] = self.progress["exports"][0]["categoryIds"]
        with self.assertRaises(AssertionError): self.verify()

    def test_different_object_count(self):
        self.progress["exports"][1]["objectCount"] = 2
        with self.assertRaises(AssertionError): self.verify()

    def test_different_object_selection(self):
        self.progress["exports"][1]["objectSelection"] = "plc:another-owner"
        with self.assertRaises(AssertionError): self.verify()

    def test_different_runtime_type(self):
        self.progress["exports"][1]["objectRuntimeTypes"] = ["Siemens.Engineering.HW.DeviceItemImpl"]
        with self.assertRaises(AssertionError): self.verify()

    def test_null_object_argument_is_not_empty(self):
        self.progress["exports"][-1]["objectArgument"] = "empty"
        with self.assertRaises(AssertionError): self.verify()

    def test_request_finish_before_start(self):
        self.progress["exports"][0]["finishedAtUtc"] = "2026-10-14T07:16:15Z"
        with self.assertRaises(AssertionError): self.verify()

    def test_request_outside_capture_interval(self):
        self.native["finishedAtUtc"] = self.native["startedAtUtc"]
        with self.assertRaises(AssertionError): self.verify()

    def test_native_import_flag_is_not_accepted(self):
        self.progress["nativeImportAllowed"] = True
        with self.assertRaises(AssertionError): self.verify()

    def test_mismatched_admitted_plc(self):
        self.admission["plcName"] = "other-plc"
        with self.assertRaises(AssertionError): self.verify()

    def test_python_optimization_cannot_disable_verification(self):
        run = subprocess.run([sys.executable, "-O", "-c", "import verify_translation_category_probe"],
                             cwd=Path(__file__).resolve().parent, capture_output=True, text=True, check=False)
        self.assertNotEqual(run.returncode, 0)
        self.assertIn("without -O or -OO", run.stderr)


if __name__ == "__main__":
    unittest.main()
