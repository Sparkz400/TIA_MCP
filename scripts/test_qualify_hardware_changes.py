"""Offline checks of the pure helpers in qualify_hardware_changes.py, qualify_software_unit_changes.py, qualify_project_settings.py and qualify_technology_changes.py; nothing here starts TIA."""
import unittest

import io
import zipfile

import qualify_alarm_imports as al
import qualify_hardware_changes as q
import qualify_project_settings as ps
import qualify_software_unit_changes as u
import qualify_technology_changes as tc

D = "/projects/S/devices/St/items/"


def item(path, comment="", ports=None):
    attributes = [{"name": "Comment", "value": comment, "valueComplete": True, "valueStatus": "captured"},
                  {"name": "InstallationDate", "value": {"text": "now"}, "valueComplete": False, "valueStatus": "displayOnly"}]
    return {"path": path, "name": path.rsplit("/", 1)[1], "attributes": attributes, **({"network": {"ports": ports}} if ports is not None else {})}


class HardwareChangeTests(unittest.TestCase):
    def test_hardware_compares_complete_values_only_and_refuses_truncation(self):
        state = q.hardware({"completeness": {"truncated": False}, "items": [item(D + "Di", "x")]})
        self.assertEqual({"Comment": "x"}, state["hw:" + D + "Di"]["attributes"])
        with self.assertRaises(AssertionError):
            q.hardware({"completeness": {"truncated": True}, "items": []})

    def test_under_matches_exact_paths_and_their_children_only(self):
        state = {"hw:" + D + "Di": 1, "hw:" + D + "Di/items/Di": 1, "hw:" + D + "Di2": 1, "inv:" + D + "Di": 1, "member:/x": 1}
        self.assertEqual(["hw:" + D + "Di", "hw:" + D + "Di/items/Di", "inv:" + D + "Di", "member:/c"], q.under(D + "Di", "member:/c")(None, state))

    def test_coupled_side_effects_are_exact(self):
        self.assertTrue(q.coupled_comment("hw:" + D + "Di")("changed", "hw:" + D + "Di/items/Di"))
        self.assertFalse(q.coupled_comment("hw:" + D + "Di")("added", "hw:" + D + "Di/items/Di"))
        self.assertFalse(q.port_defaults(["hw:a"])("changed", "hw:b"))

    def test_linked_reads_both_directions(self):
        local, partner = D + "C/items/I/items/P1", D + "Q/items/I/items/P1"
        row = lambda port, other: {"port": {"ownerHardwarePath": port}, "connectedPorts": [{"ownerHardwarePath": other}] if other else []}
        state = {"hw:" + D + "C/items/I": {"network": {"ports": [row(local, partner)]}}, "hw:" + D + "Q/items/I": {"network": {"ports": [row(partner, local)]}}}
        q.linked(state, local, partner, True)
        with self.assertRaises(AssertionError):
            q.linked(state, local, partner, False)

    def test_unit_scopes_keep_preset_and_relations(self):
        rows = u.scopes({"softwareScopes": [{"path": "/p/units/A", "name": "A", "kind": "softwareUnit", "namespacePreset": "N", "relations": [], "groups": ["g"], "identity": {}}]})
        self.assertEqual({"scope:/p/units/A": {"name": "A", "kind": "softwareUnit", "namespacePreset": "N", "relations": []}}, rows)

    def test_project_state_drops_point_in_time_values(self):
        state = ps.project({"completeness": {"complete": True}, "properties": {"author": "a", "lastModified": "t", "sizeKb": 1, "isModified": False, "isPrimary": True},
                            "languageSettings": {"active": ["en-US"]}, "attributeAccess": {}, "textCategories": [], "usedProducts": []})
        self.assertEqual({"author": "a", "isPrimary": True}, state["project:properties"])
        with self.assertRaises(AssertionError):
            ps.project({"completeness": {"complete": False}})

    def test_technology_selection_binds_path_and_name(self):
        self.assertEqual({"role": "outputCam", "technologyPath": "/p/technology/A/children/OutputCams/C", "name": "C"},
                         tc.selected(None, "/p/technology/A/children/OutputCams/C", {"role": "outputCam"}))

    def test_alarm_files_are_edited_in_one_place_only(self):
        dat = io.BytesIO()
        with zipfile.ZipFile(dat, "w") as package:
            package.writestr("x.dat", '﻿<?xml version="1.0"?><S CreationDate="2026"><DisplayName>A</DisplayName></S>'.encode("utf-8"))
        edited = al.edit_dat(dat.getvalue(), "<DisplayName>A</DisplayName>", "<DisplayName>AQ</DisplayName>")
        self.assertEqual('<?xml version="1.0"?><S><DisplayName>AQ</DisplayName></S>', al.dat_text(edited))
        book = io.BytesIO()
        with zipfile.ZipFile(book, "w") as package:
            package.writestr("xl/workbook.xml", '<x:workbook xmlns:x="m"><x:sheets><x:sheet name="T" sheetId="1" r:id="R1" /></x:sheets></x:workbook>')
            package.writestr("xl/_rels/workbook.xml.rels", '<Relationships><Relationship Type="w" Target="/xl/worksheets/sheet1.xml" Id="R1" /></Relationships>')
            package.writestr("xl/worksheets/sheet1.xml", '<x:worksheet xmlns:x="m"><x:sheetData><x:row r="2"><x:c r="A2" t="s"><x:v>0</x:v></x:c><x:c r="D2" t="s"><x:v>1</x:v></x:c></x:row></x:sheetData></x:worksheet>')
        sheet = zipfile.ZipFile(io.BytesIO(al.edit_xlsx(book.getvalue(), "T", "C2", "New"))).read("xl/worksheets/sheet1.xml").decode()
        self.assertIn('<x:c r="A2" t="s"><x:v>0</x:v></x:c><x:c r="C2" t="inlineStr"><x:is><x:t>New</x:t></x:is></x:c><x:c r="D2"', sheet)
        self.assertEqual(("S", 2), al.text_column({"rows": [{"row": 1, "cells": {"A": "Name", "S": "Alarm text [en-US]"}}, {"row": 2, "cells": {}}]}))


if __name__ == "__main__":
    unittest.main()
