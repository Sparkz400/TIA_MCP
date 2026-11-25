"""Offline checks of the import-scale runner's generated components and vendor-sized package; no native evidence."""
import io
from pathlib import Path
import tempfile
import unittest
import zipfile

from qualify_library_import_scale import DECOY, LIBRARY, candidate, component_names, document, vendor_package


class ComponentTests(unittest.TestCase):
    def test_half_blocks_half_types_with_unique_names(self):
        names = component_names(100)
        self.assertEqual(len({n for n, _ in names}), 100)
        self.assertEqual(sum(c == "blocks" for _, c in names), 50)
        with self.assertRaises(AssertionError):
            component_names(101)

    def test_documents_are_self_contained(self):
        fb, suffix = document("Imp_FB_007", "blocks")
        self.assertEqual(suffix, ".scl")
        self.assertTrue(fb.startswith('FUNCTION_BLOCK "Imp_FB_007"') and fb.rstrip().endswith("END_FUNCTION_BLOCK"))
        udt, suffix = document("Imp_UDT_007", "types")
        self.assertEqual(suffix, ".udt")
        self.assertNotIn('"Imp_', udt.split("\n", 1)[1])


class VendorPackageTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(tempfile.mkdtemp())
        self.library = self.directory / "published.zal21"
        self.library.write_bytes(b"native archive bytes" * 1000)

    def test_shape_and_volume_follow_the_reviewed_v17_distribution(self):
        target = 4 << 20
        result = vendor_package(self.directory / "package.zip", self.library, target)
        self.assertLess(abs(result["bytes"] - target), target // 100)  # Random padding does not compress.
        self.assertEqual(result["entries"], 29)
        with zipfile.ZipFile(self.directory / "package.zip") as archive:
            self.assertEqual(archive.read(LIBRARY), self.library.read_bytes())
            native = [n for n in archive.namelist() if n.endswith(".zal21")]
            self.assertEqual(sorted(native), sorted([LIBRARY, DECOY]))
            toolbox = zipfile.ZipFile(io.BytesIO(archive.read("3_Alarm Generator/Scale_Toolbox_v1.0.zip")))
            names = toolbox.namelist()
            self.assertEqual(len(names), 27)
            self.assertEqual(sum(n.endswith((".exe", ".dll")) for n in names), 25)
            self.assertFalse(any(n.endswith((".xml", ".scl", ".udt", ".zal21")) for n in names))

    def test_package_must_exceed_the_archive(self):
        with self.assertRaises(AssertionError):
            vendor_package(self.directory / "small.zip", self.library, 100)

    def test_candidate_is_matched_by_member_suffix_and_kind(self):
        rows = [{"relativePath": "content/000001/" + LIBRARY, "kind": "globalLibraryArchive", "candidateId": "a"},
                {"relativePath": "content/000001/" + DECOY, "kind": "sourceFile", "candidateId": "b"}]
        self.assertEqual(candidate(rows, LIBRARY)["candidateId"], "a")
        with self.assertRaises(AssertionError):
            candidate(rows, DECOY)


if __name__ == "__main__":
    unittest.main()
