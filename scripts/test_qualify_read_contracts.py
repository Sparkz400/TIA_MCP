"""Offline checks of the pure helpers in qualify_read_contracts.py; nothing here starts TIA."""
import hashlib
import json
import pathlib
import tempfile
import unittest

import qualify_read_contracts as q
import release_matrix

ROOT = pathlib.Path(__file__).resolve().parents[1]


class ReadContractTests(unittest.TestCase):
    def test_features_are_read_rows_of_the_catalog(self):
        catalog = json.loads((ROOT / release_matrix.CATALOG).read_text(encoding="utf-8"))
        features = {f["featureId"]: f for f in catalog["features"]}
        for feature, tools in q.FEATURES.items():
            self.assertIn(feature, features)
            self.assertEqual(sorted(tools), sorted(features[feature]["mcpTools"]), feature)
            self.assertLessEqual(set(features[feature]["acceptanceCriteria"]), q.READ_GATES, feature)
        self.assertLessEqual(set(q.NO_SELECTOR), set(q.FEATURES))

    def test_coverage_gaps_name_each_missing_need(self):
        complete = {feature: {need: ["x"] for need in q.NEEDS} for feature in q.FEATURES}
        self.assertEqual({}, q.coverage_gaps(complete))
        partial = {**complete, "web_inspect": {"identity": ["x"]}, "library_list": {"identity": ["x"], "completeness": ["x"]}}
        del partial["plc_compare"]
        self.assertEqual({"web_inspect": ["refusal", "completeness"], "plc_compare": list(q.NEEDS)}, q.coverage_gaps(partial))

    def test_bounded_and_explicit_completeness(self):
        q.bounded({"truncated": True, "scopeComplete": False}, True)
        q.bounded({"truncated": False, "complete": True}, False)
        for completeness, truncated in (({"truncated": True, "scopeComplete": True}, True), ({"truncated": False}, True), ({"truncated": 0}, False)):
            with self.assertRaises(AssertionError):
                q.bounded(completeness, truncated)
        self.assertEqual({"scopeComplete": True, "warnings": 0}, q.explicit({"completeness": {"truncated": False, "scopeComplete": True}}))
        self.assertEqual({"scopeComplete": False, "warnings": 1}, q.explicit({"completeness": {"truncated": False, "scopeComplete": False}, "warnings": [{}]}))
        with self.assertRaises(AssertionError):
            q.explicit({"completeness": {"truncated": False, "scopeComplete": False}, "warnings": []})

    def test_artifacts_must_match_hashes_and_the_whole_directory(self):
        with tempfile.TemporaryDirectory() as folder:
            a, b = pathlib.Path(folder, "a.txt"), pathlib.Path(folder, "b.txt")
            a.write_text("a", encoding="utf-8")
            b.write_text("b", encoding="utf-8")
            row = lambda p: {"path": str(p), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
            self.assertEqual(2, q.verify_artifacts([row(a), row(b)], folder))
            with self.assertRaises(AssertionError):
                q.verify_artifacts([row(a)], folder)
            with self.assertRaises(AssertionError):
                q.verify_artifacts([{**row(a), "sha256": "0" * 64}])
            with self.assertRaises(AssertionError):
                q.verify_artifacts([])

    def test_block_export_rows_must_match_the_expected_blocks_and_their_files(self):
        with tempfile.TemporaryDirectory() as folder:
            xml = pathlib.Path(folder, "block-1.xml")
            xml.write_text('<Document><Engineering version="V21" /><SW.Blocks.FC ID="0"><AttributeList><Name>RcIoWrite</Name></AttributeList></SW.Blocks.FC></Document>', encoding="utf-8")
            pathlib.Path(folder, "block-1.txt").write_text('"RcOut" := TRUE;', encoding="utf-8")
            row = {"name": "RcIoWrite", "kind": "FC", "number": 3, "language": "SCL", "path": "RcIoWrite", "xml": xml.name, "bytes": xml.stat().st_size, "text": "block-1.txt"}
            index = pathlib.Path(folder, "block-export-1.json")
            result = {"directory": folder, "index": str(index), "exported": 1, "skipped": 0, "blocks": [row]}
            index.write_text(json.dumps({"blocks": [row]}), encoding="utf-8")
            expected = [("RcIoWrite", "RcIoWrite", "FC", 3)]
            self.assertEqual(1, q.verify_block_export(result, expected))
            no_text = {k: v for k, v in row.items() if k != "text"}
            for rows, wanted in (([row], expected + [("RcIoRead", "RcIoRead", "FC", 2)]), ([{**row, "bytes": row["bytes"] + 1}], expected),
                                 ([{**row, "name": "Other"}], [("RcIoWrite", "Other", "FC", 3)]), ([no_text], expected)):
                index.write_text(json.dumps({"blocks": rows}), encoding="utf-8")
                with self.assertRaises(AssertionError):
                    q.verify_block_export({**result, "blocks": rows}, wanted)
            index.write_text(json.dumps({"blocks": [{**no_text, "textUnavailable": "no networks"}]}), encoding="utf-8")
            self.assertEqual(1, q.verify_block_export({**result, "blocks": [{**no_text, "textUnavailable": "no networks"}]}, expected))
            with self.assertRaises(AssertionError):  # The index file still lists the row without its text file.
                q.verify_block_export(result, expected)

    def test_io_expectation_places_tags_by_their_first_byte(self):
        items = [{"path": "/d/items/Cpu"}, {"path": "/d/items/DiA", "addresses": [{"ioType": "Input", "startAddress": 0, "byteSpan": 2}]},
                 {"path": "/d/items/DiB", "addresses": [{"ioType": "Input", "startAddress": 2, "byteSpan": 2}, {"ioType": "Output", "startAddress": -1, "byteSpan": 0}]},
                 {"path": "/d/items/Dq", "addresses": [{"ioType": "Output", "startAddress": 0, "byteSpan": 0}, {"ioType": "Diagnosis", "startAddress": 9, "byteSpan": 1}]}]
        tables = [{"name": "RcIo", "tags": [{"name": "In", "logicalAddress": "%I1.7"}, {"name": "Word", "logicalAddress": "%IW0"}, {"name": "Out", "logicalAddress": "%Q0.0"},
                                            {"name": "Far", "logicalAddress": "%Q200.0"}, {"name": "Flag", "logicalAddress": "%M0.0"}, {"name": "None", "logicalAddress": None}]}]
        ranges, unplaced = q.io_expectation(items, tables)
        self.assertEqual({("%I0..1", "/d/items/DiA"): ["%I1.7 In", "%IW0 Word"], ("%I2..3", "/d/items/DiB"): [], ("%Q0..0", "/d/items/Dq"): ["%Q0.0 Out"]}, ranges)
        self.assertEqual(["%Q200.0 Far"], unplaced)


if __name__ == "__main__":
    unittest.main()
