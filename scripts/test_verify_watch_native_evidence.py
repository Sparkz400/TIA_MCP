"""Authored in-memory oracle tests; no customer capture becomes a tracked fixture."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

import verify_watch_native_evidence as oracle


def fixture(kind="watch"):
    root = ET.Element("Document")
    ET.SubElement(root, "Engineering", {"version": "V21"})
    info = ET.SubElement(root, "DocumentInfo")
    ET.SubElement(info, "Created").text = "2026-10-15T06:00:00.1234567Z"
    ET.SubElement(info, "ExportSetting").text = "WithDefaults"
    products = ET.SubElement(info, "InstalledProducts")
    product = ET.SubElement(products, "Product")
    ET.SubElement(product, "DisplayName").text = "Authored test product"
    ET.SubElement(product, "DisplayVersion").text = "Authored profile"
    table = ET.SubElement(root, oracle.PREFIX + ("PlcWatchTable" if kind == "watch" else "PlcForceTable"), {"ID": "0"})
    ET.SubElement(ET.SubElement(table, "AttributeList"), "Name").text = "Authored"
    objects = ET.SubElement(table, "ObjectList")
    entries = []
    if kind == "watch":
        rows = [
            ("PlcWatchTableEntry", {"Name": '"Authored".Flag', "Address": "", "DisplayFormat": "Bool", "ModifyIntention": "True", "ModifyTrigger": "Permanent", "ModifyValue": "FALSE", "MonitorTrigger": "Permanent"}, {"Address", "ModifyIntention"}),
            ("PlcTableCommentEntry", {}, set()),
            ("PlcWatchTableEntry", {"Name": "", "Address": "%I0.0", "DisplayFormat": "Bool", "ModifyIntention": "False", "ModifyTrigger": "Permanent", "ModifyValue": "", "MonitorTrigger": "Permanent"}, {"Name", "ModifyIntention"}),
        ]
    else:
        rows = [("PlcForceTableEntry", {"Name": "", "Address": "", "DisplayFormat": "Undef", "ForceIntention": "False", "ForceValue": "16#01", "MonitorTrigger": "Permanent"}, {"DisplayFormat", "ForceIntention"})]
    counter = 1
    for suffix, fields, omitted in rows:
        row = ET.SubElement(objects, oracle.PREFIX + suffix, {"ID": str(counter), "CompositionName": "Entries"})
        counter += 1
        if fields:
            attrs = ET.SubElement(row, "AttributeList")
            for name, value in fields.items():
                if name not in omitted:
                    ET.SubElement(attrs, name).text = value
        multilingual = ET.SubElement(ET.SubElement(row, "ObjectList"), "MultilingualText", {"ID": str(counter), "CompositionName": "Comment"})
        counter += 1
        cultures = ET.SubElement(multilingual, "ObjectList")
        for culture in ("de-DE", "en-GB"):
            item = ET.SubElement(cultures, "MultilingualTextItem", {"ID": str(counter), "CompositionName": "Items"})
            counter += 1
            item_attrs = ET.SubElement(item, "AttributeList")
            ET.SubElement(item_attrs, "Culture").text = culture
            ET.SubElement(item_attrs, "Text").text = ""
        entries.append({"runtimeType": oracle.RUNTIME_PREFIX + suffix, "attributes": [{"name": key, "value": value, "access": "Read"} for key, value in fields.items()], **fields})
    capture = {"kind": kind, "group": "Authored group", "name": "Authored", "isConsistent": True, "exportOptions": "WithDefaults", "entries": entries,
               "attributes": [{"name": "Name", "value": "Authored", "access": "ReadWrite"}, {"name": "IsConsistent", "value": "True", "access": "Read"}]}
    return capture, ET.tostring(root, encoding="utf-8", xml_declaration=True)


class NativeWatchOracleTests(unittest.TestCase):
    def test_ordered_rows_and_absent_empty_are_distinct(self):
        capture, raw = fixture()
        table = oracle.inspect_table(capture, raw)
        self.assertEqual([r["kind"] for r in table["rows"]], ["watch", "comment", "watch"])
        self.assertEqual([r["selectorProfile"] for r in table["rows"]], ["nameOnly", "comment", "addressOnly"])
        first = {p["field"]: p for p in table["rows"][0]["properties"]}
        self.assertEqual(first["Address"]["xmlState"], "absent")
        self.assertFalse(first["ModifyIntention"]["exactlyCompared"])
        last = {p["field"]: p for p in table["rows"][2]["properties"]}
        self.assertEqual(last["ModifyValue"]["xmlState"], "explicitEmpty")

    def test_changed_value_is_not_parse_success(self):
        capture, raw = fixture()
        with self.assertRaisesRegex(oracle.EvidenceMismatch, "Native/XML property mismatch"):
            oracle.inspect_table(capture, raw.replace(b"FALSE", b"TRUE"))

    def test_reordered_mixed_rows_fail(self):
        capture, raw = fixture()
        capture["entries"][0], capture["entries"][1] = capture["entries"][1], capture["entries"][0]
        with self.assertRaisesRegex(oracle.EvidenceMismatch, "row kind mismatch"):
            oracle.inspect_table(capture, raw)

    def test_reordered_same_kind_rows_fail_by_selector(self):
        capture, raw = fixture()
        capture["entries"][0], capture["entries"][2] = capture["entries"][2], capture["entries"][0]
        with self.assertRaisesRegex(oracle.EvidenceMismatch, "property mismatch"):
            oracle.inspect_table(capture, raw)

    def test_dynamic_and_typed_capture_disagreement_fails(self):
        capture, raw = fixture()
        capture["entries"][0]["ModifyValue"] = "TRUE"
        with self.assertRaisesRegex(oracle.EvidenceMismatch, "Typed/dynamic"):
            oracle.inspect_table(capture, raw)

    def test_missing_native_property_is_not_an_empty_property(self):
        capture, raw = fixture()
        del capture["entries"][0]["Address"]
        with self.assertRaisesRegex(oracle.EvidenceMismatch, "property missing"):
            oracle.inspect_table(capture, raw)

    def test_duplicate_xml_properties_fail(self):
        capture, raw = fixture()
        raw = raw.replace(b"<DisplayFormat>Bool</DisplayFormat>", b"<DisplayFormat>Bool</DisplayFormat><DisplayFormat>Bool</DisplayFormat>", 1)
        with self.assertRaisesRegex(oracle.EvidenceMismatch, "Duplicate XML property"):
            oracle.inspect_table(capture, raw)

    def test_duplicate_ids_fail(self):
        capture, raw = fixture()
        with self.assertRaisesRegex(oracle.EvidenceMismatch, "Duplicate native XML object ID"):
            oracle.inspect_table(capture, raw.replace(b'ID="1"', b'ID="0"', 1))

    def test_unknown_xml_fields_are_retained_as_unverified(self):
        capture, raw = fixture()
        raw = raw.replace(b"<DisplayFormat>Bool</DisplayFormat>", b"<DisplayFormat>Bool</DisplayFormat><FutureField>opaque</FutureField>", 1)
        table = oracle.inspect_table(capture, raw)
        self.assertEqual(table["rows"][0]["unknownXmlProperties"], {"FutureField": "opaque"})

    def test_comment_text_without_readback_is_not_claimed_compared(self):
        capture, raw = fixture()
        raw = raw.replace(b"<Text />", b"<Text>Authored note</Text>", 1)
        table = oracle.inspect_table(capture, raw)
        self.assertEqual(table["rows"][0]["comments"][0]["text"], "Authored note")
        self.assertFalse(table["rows"][0]["commentTextReadbackCompared"])

    def test_comment_text_readback_is_compared_when_present(self):
        capture, raw = fixture()
        capture["entries"][0]["Comment"] = [{"culture": "de-DE", "text": "different"}, {"culture": "en-GB", "text": ""}]
        with self.assertRaisesRegex(oracle.EvidenceMismatch, "multilingual comments differ"):
            oracle.inspect_table(capture, raw)

    def test_duplicate_comment_culture_fails(self):
        capture, raw = fixture()
        with self.assertRaisesRegex(oracle.EvidenceMismatch, "Duplicate comment culture"):
            oracle.inspect_table(capture, raw.replace(b"en-GB", b"de-DE"))

    def test_force_value_does_not_create_a_bound_selector_or_true_intention(self):
        capture, raw = fixture("force")
        row = oracle.inspect_table(capture, raw)["rows"][0]
        self.assertEqual(row["selectorProfile"], "blank")
        fields = {item["field"]: item for item in row["properties"]}
        self.assertEqual(fields["ForceValue"]["nativeValue"], "16#01")
        self.assertEqual(fields["ForceIntention"]["nativeValue"], "False")
        self.assertEqual(fields["ForceIntention"]["xmlState"], "absent")
        self.assertEqual(fields["Address"]["xmlState"], "explicitEmpty")

    def test_only_created_text_is_ignored(self):
        _, raw = fixture()
        altered = raw.replace(b"06:00:00.1234567", b"09:00:00.12")
        self.assertEqual(oracle.without_created_text(raw), oracle.without_created_text(altered))
        self.assertNotEqual(oracle.without_created_text(raw), oracle.without_created_text(altered.replace(b"Authored profile", b"Changed profile")))
        self.assertNotEqual(oracle.without_created_text(raw), oracle.without_created_text(altered.replace(b"<Name", b" <Name", 1)))

    def test_created_string_elsewhere_is_not_ignored(self):
        _, raw = fixture()
        altered = raw.replace(b"<Text />", b"<Text>2026-10-15T06:00:00.1234567Z</Text>", 1)
        changed = altered.replace(b"<Text>2026-10-15T06:00:00.1234567Z</Text>", b"<Text>2026-10-16T06:00:00.1234567Z</Text>")
        self.assertNotEqual(oracle.without_created_text(altered), oracle.without_created_text(changed))

    def test_dtd_is_refused(self):
        with self.assertRaisesRegex(oracle.EvidenceMismatch, "DTD/entity"):
            oracle.parse_xml(b'<!DOCTYPE Document [<!ENTITY x "authored">]><Document/>')

    def write_capture(self, directory, raw=None):
        directory.mkdir()
        capture, original = fixture()
        raw = raw or original
        artifact_path = directory / "watch.xml"
        artifact_path.write_bytes(raw)
        capture["artifact"] = {"path": str(artifact_path.resolve()), "sha256": oracle.sha256(raw), "bytes": len(raw)}
        value = {"projectPath": "authored.ap21", "plcName": "Authored PLC", "processId": 123, "api": [], "mode": "watch-read", "completed": True,
                 "readOnly": True, "saved": False, "onlineConnected": False, "beforeModified": True, "afterModified": True,
                 "projectFileBefore": {"hashState": "unavailable-native-file-lock"}, "projectFileAfter": {"hashState": "unavailable-native-file-lock"},
                 "watch": {"exports": [capture], "inventory": [{"kind": "watch", "group": "Authored group", "name": "Authored", "rows": 3}],
                           "requested": ["Authored"], "matched": 1, "allRequestedFound": True}}
        path = directory / "result.json"
        path.write_text(json.dumps(value), encoding="utf-8")
        return path, value

    def test_full_structural_capture_retains_project_preservation_gap(self):
        with tempfile.TemporaryDirectory() as temporary:
            previous, _ = self.write_capture(Path(temporary) / "previous")
            current, _ = self.write_capture(Path(temporary) / "current")
            result = oracle.verify(current, previous)
            self.assertTrue(result["readPreservation"]["selectedSharedExportBodiesPreserved"])
            self.assertFalse(result["readPreservation"]["fullProjectPreservationProved"])
            self.assertFalse(result["nativeLifecycleQualified"])
            self.assertEqual(result["summary"]["orderedRows"], 3)

    def test_artifact_tamper_fails_before_xml_interpretation(self):
        with tempfile.TemporaryDirectory() as temporary:
            path, _ = self.write_capture(Path(temporary) / "capture")
            (path.parent / "watch.xml").write_bytes(b"<Document />")
            with self.assertRaisesRegex(oracle.EvidenceMismatch, "hash/length"):
                oracle.load_capture(path)

    def test_artifact_cannot_escape_capture_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            path, value = self.write_capture(Path(temporary) / "capture")
            value["watch"]["exports"][0]["artifact"]["path"] = str(Path(temporary) / "outside.xml")
            path.write_text(json.dumps(value), encoding="utf-8")
            with self.assertRaisesRegex(oracle.EvidenceMismatch, "beside its supplied capture"):
                oracle.load_capture(path)

    def test_changed_metadata_outside_timestamp_fails_read_preservation(self):
        with tempfile.TemporaryDirectory() as temporary:
            previous, _ = self.write_capture(Path(temporary) / "previous")
            _, raw = fixture()
            current, _ = self.write_capture(Path(temporary) / "current", raw.replace(b"Authored profile", b"Changed profile"))
            with self.assertRaisesRegex(oracle.EvidenceMismatch, "outside DocumentInfo/Created"):
                oracle.verify(current, previous)


if __name__ == "__main__":
    unittest.main()
