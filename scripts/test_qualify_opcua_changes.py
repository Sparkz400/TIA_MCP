"""Offline checks of qualify_opcua_changes.py; nothing here starts TIA."""
import json
import pathlib
import tempfile
import unittest

import qualify_opcua_changes as q
import release_matrix

ROOT = pathlib.Path(__file__).resolve().parents[1]


class OpcUaChangeTests(unittest.TestCase):
    def test_kinds_and_exclusions_cover_every_catalog_opcua_kind_and_criterion(self):
        catalog = json.loads((ROOT / release_matrix.CATALOG).read_text(encoding="utf-8"))
        kinds = {f["changeKind"] for f in catalog["features"] if (f["changeKind"] or "").startswith("opcua_")}
        self.assertFalse(set(q.KINDS) & set(q.EXCLUDED))
        self.assertEqual(kinds, set(q.KINDS) | set(q.EXCLUDED))
        criteria = {c for f in catalog["features"] if f["changeKind"] in q.KINDS for c in f["acceptanceCriteria"]}
        self.assertLessEqual(criteria, set(q.CRITERIA))

    def test_export_hash_ignores_only_the_export_time(self):
        with tempfile.TemporaryDirectory() as directory:
            a, b = pathlib.Path(directory, "a.xml"), pathlib.Path(directory, "b.xml")
            a.write_text('<UANodeSet LastModified="2026-10-29T07:48:08Z" x="1"/>', encoding="utf-8")
            b.write_text('<UANodeSet LastModified="2026-10-30T01:00:00Z" x="1"/>', encoding="utf-8")
            self.assertEqual(q.normalized_hash(a), q.normalized_hash(b))
            b.write_text('<UANodeSet LastModified="2026-10-30T01:00:00Z" x="2"/>', encoding="utf-8")
            self.assertNotEqual(q.normalized_hash(a), q.normalized_hash(b))

    def test_fixtures_share_a_namespace_and_v2_adds_an_object(self):
        v1, v2 = q.V1.read_text(encoding="utf-8"), q.V2.read_text(encoding="utf-8")
        self.assertIn("urn:tiamcp:interface:1", v1)
        self.assertIn("urn:tiamcp:interface:1", v2)
        self.assertEqual((v1.count("<UAObject "), v2.count("<UAObject ")), (1, 2))


if __name__ == "__main__":
    unittest.main()
