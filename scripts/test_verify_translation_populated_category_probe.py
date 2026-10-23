"""Synthetic adversarial requests and file-only controls; no native evidence is created."""
import copy
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import xml.etree.ElementTree as ET
from zipfile import BadZipFile, ZIP_DEFLATED, ZIP_STORED, ZipFile, ZipInfo

import verify_translation_populated_category_probe as verifier

from verify_translation_populated_category_probe import (ALARMS, COMMENTS, HARDWARE, LABELS, MODE, MODES,
                                                         compare, populated_control, validate_requests)


class PopulatedCategoryEvidenceTests(unittest.TestCase):
    def setUp(self):
        fixture = Path(__file__).resolve().parents[1] / "fixtures/translations/native-category-request-20261015"
        self.native = json.loads((fixture / "result.json").read_text(encoding="utf-8-sig"))
        self.admission = json.loads((fixture / "admission.json").read_text(encoding="utf-8-sig"))
        retained = self.native["translations"]
        self.native["mode"] = self.admission["mode"] = MODE
        self.progress = {"kind": "translation-populated-category-probe", "completed": True, "readOnly": True, "automaticReplay": False,
                         "nativeImportExecuted": False, "nativeImportAllowed": False, "nativeQualified": False, "sourceCulture": "de-DE", "targetCulture": "en-GB",
                         "projectName": "TiaMcpScratch_20261015_071342_e395a300", "plcName": self.native["plcName"], "hardwareOwnerName": self.native["hardwareOwner"]["ownerName"],
                         "selectedBlock": {"name": "Main", "number": 1, "runtimeType": "Siemens.Engineering.SW.Blocks.OB", "selectionPath": "plc:ScratchPLC/BlockGroup/Blocks/Main",
                                           "parentName": "Programmbausteine", "parentRuntimeType": "Siemens.Engineering.SW.Blocks.PlcBlockSystemGroup"},
                         "categories": retained["categories"], "maximumExportCalls": 7, "populatedMainControlPassed": True,
                         "mainBaselineUnchanged": True, "plcBaselineUnchanged": True, "outcome": "exports-completed-awaiting-independent-comparison", "exports": []}
        for index, mode in enumerate(MODES):
            request = copy.deepcopy(retained["exports"][index])
            request.update(mode=mode, categoryArgument="explicit", categoryIds={"main-comments": [COMMENTS], "main-hardware": [HARDWARE], "main-alarms": [ALARMS]}.get(mode, [c["identifier"] for c in retained["categories"]]),
                           objectArgument="explicit", objectCount=1, objectSelection="plc:ScratchPLC" if mode.startswith("plc-") else "block:Main:OB:1",
                           objectRuntimeTypes=["Siemens.Engineering.SW.PlcSoftware" if mode.startswith("plc-") else "Siemens.Engineering.SW.Blocks.OB"], beforeModified=False, state="returned")
            self.progress["exports"].append(request)
        prefix = self.progress["projectName"] + "\\ScratchPLC\\Programmbausteine\\Main\\"
        self.main = {"a": {"nativeKey": "a", "category": LABELS[COMMENTS], "viewPath": prefix + "Remanence", "source": "=True, if remanent data are available", "target": "", "sheet": "User Texts", "part": "xl/worksheets/sheet1.xml", "substitutions": ""},
                     "b": {"nativeKey": "b", "category": LABELS[COMMENTS], "viewPath": prefix + "Initial_Call", "source": "Initial call of this OB", "target": "", "sheet": "User Texts", "part": "xl/worksheets/sheet1.xml", "substitutions": ""}}
        self.plc = copy.deepcopy(self.main)
        self.plc["peer"] = {"nativeKey": "peer", "category": LABELS[HARDWARE], "viewPath": "", "source": "", "target": "", "sheet": "User Texts", "part": "xl/worksheets/sheet1.xml", "substitutions": ""}

    def verify(self):
        self.native["translations"] = copy.deepcopy(self.progress)
        return validate_requests(self.native, self.admission, self.progress)

    def gate(self):
        return populated_control(self.plc, self.main, self.progress["projectName"], self.progress["plcName"])

    def test_synthetic_complete_request_control(self): self.assertTrue(self.verify())

    def test_synthetic_empty_control_stops_at_two(self):
        self.progress.update(populatedMainControlPassed=False, restrictedExportsSkipped=True, outcome="inconclusive-main-control-not-populated-or-not-preserved")
        self.progress["exports"] = self.progress["exports"][:2]
        self.assertFalse(self.verify())

    def test_empty_control_cannot_issue_restricted_exports(self):
        self.progress.update(populatedMainControlPassed=False, restrictedExportsSkipped=True, outcome="inconclusive-main-control-not-populated-or-not-preserved")
        with self.assertRaises(ValueError): self.verify()

    def test_category_omission_in_all_baseline(self):
        self.progress["exports"][0]["categoryIds"].pop()
        with self.assertRaises(ValueError): self.verify()

    def test_restricted_category_is_exact(self):
        self.progress["exports"][3]["categoryIds"] = [COMMENTS]
        with self.assertRaises(ValueError): self.verify()

    def test_block_number_mismatch(self):
        self.progress["selectedBlock"]["number"] = 2
        with self.assertRaises(ValueError): self.verify()

    def test_block_path_mismatch(self):
        self.progress["selectedBlock"]["selectionPath"] = "plc:ScratchPLC/BlockGroup/Groups/Other/Blocks/Main"
        with self.assertRaises(ValueError): self.verify()

    def test_nested_parent_not_root(self):
        self.progress["selectedBlock"]["parentRuntimeType"] = "Siemens.Engineering.SW.Blocks.PlcBlockUserGroup"
        with self.assertRaises(ValueError): self.verify()

    def test_plc_is_not_main_selection(self):
        self.progress["exports"][3]["objectSelection"] = "plc:ScratchPLC"
        with self.assertRaises(ValueError): self.verify()

    def test_object_count_mismatch(self):
        self.progress["exports"][3]["objectCount"] = 2
        with self.assertRaises(ValueError): self.verify()

    def test_block_runtime_type_mismatch(self):
        self.progress["exports"][3]["objectRuntimeTypes"] = ["Siemens.Engineering.SW.Blocks.FC"]
        with self.assertRaises(ValueError): self.verify()

    def test_different_admitted_process(self):
        self.admission["processId"] += 1
        with self.assertRaises(ValueError): self.verify()

    def test_project_name_mismatch(self):
        self.progress["projectName"] = "another-project"
        with self.assertRaises(ValueError): self.verify()

    def test_out_of_order_calls(self):
        self.progress["exports"][2:4] = reversed(self.progress["exports"][2:4])
        with self.assertRaises(ValueError): self.verify()

    def test_overlapping_call_times(self):
        self.progress["exports"][1]["startedAtUtc"] = self.progress["exports"][0]["startedAtUtc"]
        with self.assertRaises(ValueError): self.verify()

    def test_timezone_required(self):
        self.progress["exports"][0]["startedAtUtc"] = "2026-10-15T07:16:16"
        with self.assertRaises(ValueError): self.verify()

    def test_native_import_enablement_refused(self):
        self.progress["nativeImportAllowed"] = True
        with self.assertRaises(ValueError): self.verify()

    def test_native_qualification_refused(self):
        self.progress["nativeQualified"] = True
        with self.assertRaises(ValueError): self.verify()

    def test_modified_project_refused(self):
        self.progress["exports"][2]["afterModified"] = True
        with self.assertRaises(ValueError): self.verify()

    def test_populated_synthetic_main_control(self): self.assertTrue(self.gate())

    def test_empty_synthetic_main_control(self):
        self.main = {}
        self.assertFalse(self.gate())

    def test_foreign_main_control(self):
        self.progress["projectName"] = "different"
        self.assertFalse(self.gate())

    def test_leaking_peer_in_main_control(self):
        self.main = self.plc
        self.assertFalse(self.gate())

    def test_main_source_changed_from_plc_control(self):
        self.main["a"]["source"] += "changed"
        self.assertFalse(self.gate())

    def test_row_moved_to_system_texts_refused(self):
        self.main["a"]["sheet"] = "System Texts"
        self.assertFalse(self.gate())

    def test_main_disjoint_exclusion_reported_without_import_permission(self):
        report = compare(self.main, {}, HARDWARE)
        self.assertTrue(report["matchesIntersection"] and report["nonemptyBaselineExcludedByDisjointCategory"])
        self.assertFalse(report["restrictiveFilteringDemonstrated"] or report["nativeImportAllowed"] or report["nativeQualified"])

    def test_main_disjoint_leakage_detected(self):
        report = compare(self.main, self.main, HARDWARE)
        self.assertFalse(report["matchesIntersection"])
        self.assertEqual(report["outOfCategoryRows"], 2)

    def test_same_category_all_rows_not_restrictive(self):
        report = compare(self.main, self.main, COMMENTS)
        self.assertTrue(report["matchesIntersection"])
        self.assertFalse(report["restrictiveFilteringDemonstrated"])

    def test_changed_source_not_equal_intersection(self):
        selected = copy.deepcopy(self.main)
        selected["a"]["source"] += "changed"
        report = compare(self.main, selected, COMMENTS)
        self.assertFalse(report["matchesIntersection"])
        self.assertEqual(report["changedKeys"], ["a"])

    def test_checks_remain_active_under_optimization(self):
        run = subprocess.run([sys.executable, "-O", "-c", "from verify_translation_populated_category_probe import require; require(False, 'optimization control')"],
                             cwd=Path(__file__).resolve().parent, capture_output=True, text=True, check=False)
        self.assertNotEqual(run.returncode, 0)
        self.assertIn("ValueError: optimization control", run.stderr)


class BoundedWorkbookDecodeTests(unittest.TestCase):
    """Authored narrow-layout controls, never labelled genuine/native evidence."""

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "synthetic.xlsx"
        ns = verifier.S[1:-1]
        headers = ["Category", "ViewPath", "Internal ID", "Substitutions", "de-DE*", "en-GB"] + [f'ref="{i}"' for i in range(16)]
        strings = ET.Element(verifier.S + "sst")
        for text in headers:
            ET.SubElement(ET.SubElement(strings, verifier.S + "si"), verifier.S + "t").text = text
        self.parts = {name: b"<unused/>" for name in verifier.PARTS}
        self.parts["xl/sharedStrings.xml"] = ET.tostring(strings, encoding="utf-8", xml_declaration=True)
        self.parts["xl/workbook.xml"] = (f'<workbook xmlns="{ns}" xmlns:r="{verifier.R[1:-1]}"><sheets>'
            '<sheet name="User Texts" sheetId="1" r:id="r1"/><sheet name="System Texts" sheetId="2" r:id="r2"/></sheets></workbook>').encode()
        self.parts["xl/_rels/workbook.xml.rels"] = (f'<Relationships xmlns="{verifier.P[1:-1]}">' + ''.join(
            f'<Relationship Id="r{i}" Type="{verifier.R[1:-1]}/worksheet" Target="/xl/worksheets/sheet{i}.xml"/>' for i in (1, 2)) + '</Relationships>').encode()
        sheet = (f'<worksheet xmlns="{ns}"><sheetData><row r="1">' + ''.join(
            f'<c r="{chr(65 + i)}1" t="s"><v>{i}</v></c>' for i in range(22)) + '</row></sheetData></worksheet>').encode()
        for i in (1, 2): self.parts[f"xl/worksheets/sheet{i}.xml"] = sheet

    def write(self):
        with ZipFile(self.path, "w", compression=ZIP_DEFLATED) as archive:
            for name, value in self.parts.items(): archive.writestr(name, value)
        return self.path

    def decode(self): return verifier.decode(self.write())

    def test_authored_empty_control_is_readable_without_native_qualification(self):
        rows, profiles, sheets = self.decode()
        self.assertEqual(rows, {})
        self.assertEqual(profiles, [])
        self.assertEqual([s["rows"] for s in sheets], [0, 0])

    def test_utf16_without_dtd_remains_readable(self):
        self.parts = {name: ET.tostring(ET.fromstring(raw), encoding="utf-16", xml_declaration=True) for name, raw in self.parts.items()}
        self.assertEqual(self.decode()[0], {})

    def test_dtd_is_rejected_for_utf8_and_both_utf16_byte_orders(self):
        # The expansion is tiny; the regression is rejection before any DTD use.
        xml = ('<?xml version="1.0" encoding="ENCODING"?>'
               '<!DOCTYPE unused [<!ENTITY synthetic "not a native value">]><unused>&synthetic;</unused>')
        for encoding in ("utf-8", "utf-16-le", "utf-16-be"):
            with self.subTest(encoding=encoding):
                declaration = "utf-8" if encoding == "utf-8" else "utf-16"
                bom = {"utf-8": b"", "utf-16-le": b"\xff\xfe", "utf-16-be": b"\xfe\xff"}[encoding]
                self.parts["docProps/custom.xml"] = bom + xml.replace("ENCODING", declaration).encode(encoding)
                with self.assertRaisesRegex(ValueError, "DTD|entities"):
                    self.decode()

    def test_external_dtd_is_rejected_without_loading_it(self):
        self.parts["docProps/custom.xml"] = b'<!DOCTYPE unused SYSTEM "https://invalid.test/no-network"><unused/>'
        with self.assertRaisesRegex(ValueError, "DTD|entities"):
            self.decode()

    def test_nested_shared_text_cannot_be_silently_flattened(self):
        root = ET.fromstring(self.parts["xl/sharedStrings.xml"])
        ET.SubElement(root[0][0], verifier.S + "hidden").text = "concealed"
        self.parts["xl/sharedStrings.xml"] = ET.tostring(root)
        with self.assertRaisesRegex(ValueError, "shared string"):
            self.decode()

    def test_unmodelled_shared_string_text_cannot_be_ignored(self):
        original = self.parts["xl/sharedStrings.xml"]
        for field in ("prefix", "suffix"):
            with self.subTest(field=field):
                root = ET.fromstring(original)
                if field == "prefix": root[0].text = "concealed"
                else: root[0][0].tail = "concealed"
                self.parts["xl/sharedStrings.xml"] = ET.tostring(root)
                with self.assertRaisesRegex(ValueError, "shared string"):
                    self.decode()

    def test_xml_depth_budget_is_enforced_on_opaque_parts(self):
        self.parts["docProps/custom.xml"] = b"<n>" * 66 + b"</n>" * 66
        with self.assertRaisesRegex(ValueError, "depth"):
            self.decode()

    def test_xml_node_budget_is_enforced_before_full_tree_build(self):
        self.parts["docProps/custom.xml"] = b"<n>" + b"<v/>" * 150 + b"</n>"
        with mock.patch.object(verifier, "MAX_XML_NODES", 100, create=True):
            with self.assertRaisesRegex(ValueError, "node"):
                self.decode()

    def test_xml_byte_budget_is_enforced_per_part(self):
        self.parts["docProps/custom.xml"] = b"<n>" + b"x" * 4096 + b"</n>"
        with mock.patch.object(verifier, "MAX_XML_BYTES", 4000, create=True):
            with self.assertRaisesRegex(ValueError, "XML part"):
                self.decode()

    def test_expanded_budget_is_checked_before_any_payload_read(self):
        self.write()
        archive = mock.MagicMock()
        archive.__enter__.return_value = archive
        entries = [ZipInfo(name) for name in verifier.PARTS]
        for entry in entries: entry.file_size = 4 * 1024 * 1024
        archive.namelist.return_value = [e.filename for e in entries]
        archive.infolist.return_value = entries
        archive.testzip.side_effect = AssertionError("Unbounded decompression preceded budget validation")
        archive.open.side_effect = AssertionError("Payload read preceded budget validation")
        archive.read.side_effect = AssertionError("Payload read preceded budget validation")
        with mock.patch.object(verifier, "ZipFile", return_value=archive):
            with self.assertRaisesRegex(ValueError, "Expanded|expanded"):
                verifier.decode(self.path)
        archive.open.assert_not_called()
        archive.read.assert_not_called()
        archive.testzip.assert_not_called()

    def test_stream_bytes_cannot_exceed_budget_even_if_directory_understates_size(self):
        self.write()
        archive = mock.MagicMock()
        archive.__enter__.return_value = archive
        entries = [ZipInfo(name) for name in verifier.PARTS]
        for entry in entries: entry.file_size = 1
        archive.infolist.return_value = entries
        archive.open.return_value = io.BytesIO(b"x" * 65)
        with mock.patch.object(verifier, "ZipFile", return_value=archive), mock.patch.object(verifier, "MAX_XML_BYTES", 64):
            with self.assertRaisesRegex(ValueError, "XML part"):
                verifier.decode(self.path)

    def test_stream_size_must_match_directory_even_inside_budget(self):
        self.write()
        archive = mock.MagicMock()
        archive.__enter__.return_value = archive
        entries = [ZipInfo(name) for name in verifier.PARTS]
        for entry in entries: entry.file_size = 1
        archive.infolist.return_value = entries
        archive.open.return_value = io.BytesIO(b"<unused/>")
        with mock.patch.object(verifier, "ZipFile", return_value=archive):
            with self.assertRaisesRegex(ValueError, "size mismatch"):
                verifier.decode(self.path)

    def test_crc_is_still_checked_without_testzip_prepass(self):
        with ZipFile(self.path, "w", compression=ZIP_STORED) as archive:
            for name, value in self.parts.items(): archive.writestr(name, value)
        data = self.path.read_bytes()
        self.assertIn(b"<unused/>", data)
        self.path.write_bytes(data.replace(b"<unused/>", b"<Unused/>", 1))
        with self.assertRaisesRegex(BadZipFile, "CRC"):
            verifier.decode(self.path)


if __name__ == "__main__": unittest.main()
