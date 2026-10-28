"""Offline checks of the pure helpers in qualify_library_preservation.py; nothing here starts TIA."""
import json
import pathlib
import unittest

import qualify_library_preservation as q
import release_matrix

ROOT = pathlib.Path(__file__).resolve().parents[1]


class LibraryPreservationTests(unittest.TestCase):
    def test_compare_requires_every_key_and_every_pattern_to_match(self):
        observed = {"added": ["P|type:A", "P|version:A@0.0.1"], "removed": [], "changed": ["block:PLC/blocks/A"]}
        self.assertEqual({}, q.compare(observed, {"added": ["P|type:A", "P|version:A@*"], "changed": ["block:PLC/blocks/A"]}))
        self.assertEqual({"changed": {"keys": ["block:PLC/blocks/A"], "patterns": []}}, q.compare(observed, {"added": ["P|*"]}))
        self.assertEqual({"removed": {"keys": [], "patterns": ["P|version:A@1.0.0"]}},
                         q.compare(observed, {"added": ["P|*"], "removed": ["P|version:A@1.0.0"], "changed": ["block:*"]}))

    def test_library_rows_key_on_names_and_versions_not_session_ids(self):
        version = {"kind": "version", "typeName": "A", "version": "1.0.0", "typeGuid": "t", "versionGuid": "v", "path": "/types/A/versions/v", "isDefault": True}
        self.assertEqual(("G|version:A@1.0.0", {k: v for k, v in version.items() if k != "kind"}), q.library_row("G|", version))
        self.assertEqual(("P|typeFolder:/types/F", True), q.library_row("P|", {"kind": "typeFolder", "path": "/types/F", "name": "F"}))
        self.assertEqual("P|masterCopy:/masterCopies/M", q.library_row("P|", {"kind": "masterCopy", "masterCopyPath": "/masterCopies/M", "name": "M"})[0])
        with self.assertRaises(AssertionError):
            q.library_row("P|", {"kind": "type", "name": "X", "unavailable": True})

    def test_parent_releases_change_only_the_sum_statement(self):
        original = (q.FIXTURES / (q.PARENT + ".scl")).read_text(encoding="utf-8")
        self.assertEqual(original.replace(q.PARENT_SUM, "#Sum := #Child.Sum + 2;"), q.parent_source(2))
        self.assertIn("Extra : Bool;", q.udt(q.EXTRA, "Value : Int;\n    Extra : Bool;"))

    def test_libint_comparison_drops_only_document_byte_hashes(self):
        text = "﻿DocumentHash:\n  - FileName: component.xml\n    Hash: ZlqJ=\nLibraryMetaFileHash: leBI=\nLibraryVersion:\n  Guid: 1d7c\n"
        self.assertEqual(["﻿DocumentHash:", "  - FileName: component.xml", "LibraryMetaFileHash: leBI=", "LibraryVersion:", "  Guid: 1d7c"],
                         q.libint_lines(text))
        self.assertNotEqual(q.libint_lines(text), q.libint_lines(text.replace("leBI=", "other=")))

    def test_plc_two_references_grow_from_parent_to_every_library_object(self):
        bare, child, every = q.consumer_two(), q.consumer_two(True), q.consumer_two(True, True)
        self.assertNotIn(q.CHILD + "_DB", bare)
        self.assertNotIn("VAR_TEMP", bare)
        self.assertIn('Data : "%s";' % q.OUTER, child)
        self.assertNotIn(q.SPARE, child)
        self.assertIn('Spare : "%s";' % q.SPARE, every)
        self.assertTrue(all('ORGANIZATION_BLOCK "%s"' % q.CYCLE in s for s in (bare, child, every)))

    def test_covered_and_excluded_kinds_are_the_catalog_library_change_kinds(self):
        catalog = json.loads((ROOT / release_matrix.CATALOG).read_text(encoding="utf-8"))
        library = {f["changeKind"] for f in catalog["features"] if (f["changeKind"] or "").startswith("library_")}
        self.assertFalse(set(q.KINDS) & set(q.EXCLUDED))
        self.assertEqual(library, set(q.KINDS) | set(q.EXCLUDED))
        self.assertTrue(set(q.OUTPUT_ONLY) <= set(q.KINDS))


if __name__ == "__main__":
    unittest.main()
