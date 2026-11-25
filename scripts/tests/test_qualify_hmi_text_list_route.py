import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from qualify_hmi_text_list_route import RETAINED, install_retained, summary


class TextListRouteRunnerTests(unittest.TestCase):
    def test_install_retained_copies_every_record_byte_exact(self):
        with tempfile.TemporaryDirectory() as directory:
            records = install_retained(Path(directory))
            self.assertEqual({"author": 5, "set": 2, "refused": 4}, {k: len(v) for k, v in records.items()})
            for root in RETAINED.values():
                for row in json.loads((root / "fixture.json").read_text(encoding="utf-8"))["files"]:
                    self.assertEqual(row["sha256"], hashlib.sha256((Path(directory) / row["path"]).read_bytes()).hexdigest())
            for record in records["refused"]:
                self.assertTrue((Path(directory) / "hmi-component-reviews" / (record["recordId"] + ".author.json")).exists())
            self.assertEqual({"HmiTextListValueOutOfRange", "HmiTextListNameUnsupported", "HmiTextListTextUnsupported", "HmiTextListDefaultEntryValue"},
                             {r["expectedCode"] for r in records["refused"]})

    def test_install_retained_refuses_a_changed_fixture_byte(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "fixture"
            (root / "store").mkdir(parents=True)
            (root / "store" / "a.json").write_bytes(b"changed")
            (root / "fixture.json").write_text(json.dumps({"files": [{"path": "a.json", "sha256": "0" * 64}], "records": []}), encoding="utf-8")
            original = dict(RETAINED)
            RETAINED.clear(); RETAINED["x"] = root
            try:
                with self.assertRaises(AssertionError):
                    install_retained(Path(directory) / "store")
            finally:
                RETAINED.clear(); RETAINED.update(original)

    def test_summary_keeps_the_gated_fields_of_change_views_only(self):
        view = {"changeId": "c", "state": "applied", "stages": ["changed_project_saved"], "journalPath": "j", "preview": {"effects": [{"created": ["A"]}], "payload": {"big": 1}},
                "hmiEvidence": {"gate": {"available": True}, "importReadback": {"exact": True}, "baseline": {"artifacts": {"x": 1}}}}
        trimmed = summary(view)
        self.assertEqual({"available": True}, trimmed["gate"])
        self.assertEqual([{"created": ["A"]}], trimmed["effects"])
        self.assertNotIn("artifacts", json.dumps(trimmed))
        self.assertEqual({"items": []}, summary({"items": []}))


if __name__ == "__main__":
    unittest.main()
