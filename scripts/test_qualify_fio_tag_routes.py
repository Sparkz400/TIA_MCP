"""Offline checks of the VCI package edit in qualify_fio_tag_routes.py; nothing here starts TIA."""
from pathlib import Path
import tempfile
import unittest

import qualify_fio_tag_routes as r


class EditPackageTests(unittest.TestCase):
    def test_edits_the_one_package_file_holding_the_address(self):
        with tempfile.TemporaryDirectory() as root:
            (Path(root) / "FioTags.xml").write_bytes(b"\xef\xbb\xbf<LogicalAddress>%M40.0</LogicalAddress>")
            (Path(root) / "Other.xml").write_bytes(b"<LogicalAddress>%M40.0</LogicalAddress>")
            self.assertEqual("FioTags.xml", r.edit_package(root, "FioTags", "%M40.0", "%I0.0"))
            self.assertEqual(b"\xef\xbb\xbf<LogicalAddress>%I0.0</LogicalAddress>", (Path(root) / "FioTags.xml").read_bytes())
            self.assertEqual(b"<LogicalAddress>%M40.0</LogicalAddress>", (Path(root) / "Other.xml").read_bytes())

    def test_refuses_an_absent_or_repeated_address(self):
        with tempfile.TemporaryDirectory() as root:
            (Path(root) / "FioTags.xml").write_bytes(b"%M40.0 %M40.0")
            for old in ("%M40.0", "%M41.0"):
                with self.assertRaises(AssertionError):
                    r.edit_package(root, "FioTags", old, "%I0.0")
            self.assertEqual(b"%M40.0 %M40.0", (Path(root) / "FioTags.xml").read_bytes())


if __name__ == "__main__":
    unittest.main()
