"""Offline checks of the VCI package edit in qualify_fio_tag_routes.py; nothing here starts TIA."""
from pathlib import Path
import tempfile
import unittest

import qualify_fio_tag_routes as r


class EditPackageTests(unittest.TestCase):
    def test_absolute_operand_keeps_native_namespace_spelling_and_other_accesses(self):
        original = '<Document><StructuredText xmlns="urn:native"><Access Scope="LocalVariable" UId="1"><Symbol Name="Return" /></Access><Token Text=":=" /><Access Scope="GlobalVariable" UId="2"><Symbol Name="Tag_0" UId="3" /></Access></StructuredText></Document>'
        expected = original.replace('<Access Scope="GlobalVariable" UId="2"><Symbol Name="Tag_0" UId="3" /></Access>', '<Access Scope="Address" UId="2"><Address Area="Input" Type="Bool" BitOffset="19" UId="3" /></Access>')
        self.assertEqual(expected, r.absolute_xml(original, "%I2.3"))
        with self.assertRaises(AssertionError):
            r.absolute_xml(original.replace('GlobalVariable', 'LocalVariable'), "%I2.3")

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
