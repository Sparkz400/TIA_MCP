"""Offline independent export-reader regressions; no Host or Siemens process."""
import csv
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile

from qualify_device_inventory import csv_literal, decode_cell, verify_exports


class InventoryExportChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def exports(self, csv_values, encoded_values):
        csv_path, xlsx_path = self.root / "rows.csv", self.root / "rows.xlsx"
        with csv_path.open("w", encoding="utf-8-sig", newline="") as stream:
            writer = csv.writer(stream)
            writer.writerow(["Name"])
            writer.writerows([[value] for value in csv_values])
        ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
        sheet = ET.Element("{" + ns + "}worksheet")
        data = ET.SubElement(sheet, "{" + ns + "}sheetData")
        for value in ["Name", *encoded_values]:
            row = ET.SubElement(data, "{" + ns + "}row")
            cell = ET.SubElement(row, "{" + ns + "}c", {"t": "inlineStr"})
            inline = ET.SubElement(cell, "{" + ns + "}is")
            ET.SubElement(inline, "{" + ns + "}t").text = value
        with zipfile.ZipFile(xlsx_path, "w") as archive:
            archive.writestr("xl/worksheets/sheet1.xml", ET.tostring(sheet))
        return csv_path, xlsx_path

    def test_escaped_text_is_decoded_once(self):
        pairs = [("_x000D_", "_x005F_x000D_"),
                 ("_x005F_x000D_", "_x005F_x005F_x005F_x000D_"),
                 ("a\rb\nc\r\nd", "a_x000D_b\nc_x000D_\nd"),
                 ("NUL\0end", "NUL_x0000_end"), ("tab\tend", "tab\tend"),
                 ("日本語 🐝", "日本語 🐝"), ("_X000D_", "_X000D_")]
        for literal, encoded in pairs:
            self.assertEqual(decode_cell(encoded), literal)
        verify_exports(*self.exports([p[0] for p in pairs], [p[1] for p in pairs]), len(pairs))

    def test_missing_escape_is_a_real_mismatch(self):
        with self.assertRaises(AssertionError):
            verify_exports(*self.exports(["_x000D_"], ["_x000D_"]), 1)

    def test_formula_policy_accepts_only_required_apostrophe(self):
        values = ["=1", "  +2", "-3", "@name", "\tname", "\rname", "\nname", "\u2003=1", "\x1c=literal", "plain", "'original"]
        encoded = [value.replace("\r", "_x000D_").replace("\x1c", "_x001C_") for value in values]
        verify_exports(*self.exports([csv_literal(v) for v in values], encoded), len(values))
        with self.assertRaises(AssertionError):
            verify_exports(*self.exports(["'plain"], ["plain"]), 1)
        with self.assertRaises(AssertionError):
            verify_exports(*self.exports(["=1"], ["=1"]), 1)

    def test_row_count_remains_exact(self):
        with self.assertRaises(AssertionError):
            verify_exports(*self.exports(["one"], ["one"]), 2)


if __name__ == "__main__":
    unittest.main()
