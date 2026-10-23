"""Synthetic adversarial tests for the offline force evidence oracle."""
from copy import deepcopy
import unittest
import xml.etree.ElementTree as ET
import verify_force_capture as verify


class ForceOracleTests(unittest.TestCase):
    def setUp(self):
        self.rows = [
            {"kind": "signal", "name": "", "address": "%I0.0:P", "displayFormat": "Bool", "forceValue": "FALSE", "forceIntention": False, "monitorTrigger": "Permanent", "comments": {"de-DE": "Offline Eingang {0}"}},
            {"kind": "comment", "comments": {"de-DE": "Nur Offline-Erfassung"}},
            {"kind": "signal", "name": "", "address": "%Q0.0:P", "displayFormat": "Bool", "forceValue": "TRUE", "forceIntention": False, "monitorTrigger": "Permanent", "comments": {"de-DE": "Offline Ausgang {0}"}},
        ]
        self.oracle = {"forceTableName": "Force table", "activeLanguages": ["de-DE"], "rows": self.rows}

    def xml(self, rows=None, intention=None):
        root = ET.Element("Document")
        ET.SubElement(ET.SubElement(root, "DocumentInfo"), "Created").text = "synthetic-time-1"
        table = ET.SubElement(root, verify.PREFIX + "PlcForceTable")
        ET.SubElement(ET.SubElement(table, "AttributeList"), "Name").text = "Force table"
        entries = ET.SubElement(table, "ObjectList")
        for row in rows or self.rows:
            entry = ET.SubElement(entries, verify.PREFIX + ("PlcForceTableEntry" if row["kind"] == "signal" else "PlcTableCommentEntry"), CompositionName="Entries")
            attrs = ET.SubElement(entry, "AttributeList")
            if row["kind"] == "signal":
                for key, field in (("name", "Name"), ("address", "Address"), ("displayFormat", "DisplayFormat"), ("forceValue", "ForceValue"), ("monitorTrigger", "MonitorTrigger")):
                    ET.SubElement(attrs, field).text = row[key]
                if intention is not None:
                    ET.SubElement(attrs, "ForceIntention").text = intention
            texts = ET.SubElement(ET.SubElement(ET.SubElement(entry, "ObjectList"), "MultilingualText", CompositionName="Comment"), "ObjectList")
            for culture, text in row["comments"].items():
                text_attrs = ET.SubElement(ET.SubElement(texts, "MultilingualTextItem"), "AttributeList")
                ET.SubElement(text_attrs, "Culture").text = culture
                ET.SubElement(text_attrs, "Text").text = text
        return ET.tostring(root)

    def test_direct_native_field_and_xml_oracle_agree(self):
        self.assertEqual(verify.check_rows(self.oracle, self.rows, self.xml()), [None, None])

    def test_false_selection_is_reported_when_serialized(self):
        self.assertEqual(verify.check_rows(self.oracle, self.rows, self.xml(intention="false")), ["false", "false"])

    def test_true_typed_selection_refused(self):
        self.rows[0]["forceIntention"] = True
        with self.assertRaisesRegex(ValueError, "ForceIntention=true"):
            verify.check_rows(self.oracle, self.rows, self.xml())

    def test_true_serialized_selection_refused(self):
        with self.assertRaisesRegex(ValueError, "Serialized force selection"):
            verify.check_rows(self.oracle, self.rows, self.xml(intention="true"))

    def test_unknown_serialized_selection_refused(self):
        with self.assertRaisesRegex(ValueError, "Serialized force selection"):
            verify.check_rows(self.oracle, self.rows, self.xml(intention="1"))

    def test_blank_row_is_not_population(self):
        self.rows[0]["address"] = ""
        with self.assertRaisesRegex(ValueError, "Blank/Undef"):
            verify.check_rows(self.oracle, self.rows, self.xml())

    def test_undef_row_is_not_population(self):
        self.rows[0]["displayFormat"] = "Undef"
        with self.assertRaisesRegex(ValueError, "Blank/Undef"):
            verify.check_rows(self.oracle, self.rows, self.xml())

    def test_xml_value_mismatch_is_detected(self):
        wrong = deepcopy(self.rows)
        wrong[0]["forceValue"] = "TRUE"
        with self.assertRaisesRegex(ValueError, "Native XML row"):
            verify.check_rows(self.oracle, self.rows, self.xml(wrong))

    def test_missing_culture_is_not_hidden(self):
        self.oracle["activeLanguages"].append("en-GB")
        with self.assertRaisesRegex(ValueError, "cultures"):
            verify.check_rows(self.oracle, self.rows, self.xml())

    def test_reorder_is_detected(self):
        wrong = deepcopy(self.rows)
        wrong[0], wrong[2] = wrong[2], wrong[0]
        with self.assertRaisesRegex(ValueError, "Typed row"):
            verify.check_rows(self.oracle, wrong, self.xml())

    def test_only_timestamp_masked(self):
        first = self.xml()
        second = first.replace(b"synthetic-time-1", b"synthetic-time-2")
        self.assertEqual(verify.mask_timestamp(first), verify.mask_timestamp(second))
        self.assertNotEqual(verify.mask_timestamp(first), verify.mask_timestamp(first.replace(b"FALSE", b"TRUE")))

    def test_changed_unknown_payload_is_not_masked(self):
        first = self.xml()
        changed = first.replace(b"</Document>", b"<Unknown vendor='x'/></Document>")
        self.assertNotEqual(verify.mask_timestamp(first), verify.mask_timestamp(changed))

    def test_bom_and_line_endings_are_not_masked(self):
        first = self.xml()
        self.assertNotEqual(verify.mask_timestamp(first), verify.mask_timestamp(b"\xef\xbb\xbf" + first))
        self.assertNotEqual(verify.mask_timestamp(first), verify.mask_timestamp(first.replace(b"</DocumentInfo>", b"</DocumentInfo>\r\n")))

    def test_duplicate_timestamp_refused(self):
        changed = self.xml().replace(b"</DocumentInfo>", b"<Created>other</Created></DocumentInfo>")
        with self.assertRaisesRegex(ValueError, "Single native"):
            verify.mask_timestamp(changed)

    def observations(self):
        first = {"projectPath": "D:\\synthetic\\owned.ap21", "processId": 1, "nativeProcessStartTimeUtc": "one", "oracleSha256": "a" * 64, "forceTimestampMaskedSha256": "b" * 64, "typedRows": self.rows, "peers": {"x": "y"}}
        second = deepcopy(first)
        second.update(processId=2, nativeProcessStartTimeUtc="two")
        return first, second

    def test_fresh_reopen_does_not_claim_force_qualification_or_restoration(self):
        first, second = self.observations()
        report = verify.compare(first, second)
        self.assertTrue(report["freshReopenVerified"])
        self.assertFalse(report["forceEditingQualified"])
        self.assertFalse(report["onlineForcingQualified"])
        self.assertFalse(report["restorationVerified"])

    def test_same_process_is_not_reopen(self):
        first, _ = self.observations()
        with self.assertRaisesRegex(ValueError, "Distinct native"):
            verify.compare(first, first)

    def test_reopened_peer_change_refused(self):
        first, second = self.observations()
        second["peers"]["x"] = "changed"
        with self.assertRaisesRegex(ValueError, "Peers changed"):
            verify.compare(first, second)

    def test_reopened_unknown_payload_change_refused(self):
        first, second = self.observations()
        second["forceTimestampMaskedSha256"] = "c" * 64
        with self.assertRaisesRegex(ValueError, "Force bytes"):
            verify.compare(first, second)


if __name__ == "__main__":
    unittest.main(verbosity=2)
