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


if __name__ == "__main__":
    unittest.main()
