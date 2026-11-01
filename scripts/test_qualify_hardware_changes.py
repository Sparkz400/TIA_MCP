"""Offline checks of the pure helpers in qualify_hardware_changes.py, qualify_software_unit_changes.py, qualify_project_settings.py and qualify_technology_changes.py; nothing here starts TIA."""
import unittest

import io
import zipfile

import qualify_alarm_imports as al
import qualify_hardware_changes as q
import qualify_project_settings as ps
import qualify_software_unit_changes as u
import qualify_technology_changes as tc
from qualify_plc_object_mutations import STAGES

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

    def test_family_links_with_the_cpu_link_form_a_line(self):
        run = q.Run.__new__(q.Run)
        run.base = "/projects/P/devices/"
        links = [*q.family_ports(run).values(), tuple(q.ports(run, 2))]
        ends = [port for link in links for port in link]
        self.assertEqual(len(ends), len(set(ends)), "a port is linked twice")
        devices = [tuple(port[len(run.base):].split("/")[0] for port in link) for link in links]
        self.assertEqual({("Hw1200", "ScratchStation"), ("HwMp", "HwPeer"), ("ScratchStation", "HwPeer")}, set(devices))
        self.assertEqual(4, len({d for link in devices for d in link}))  # Four devices joined by three links: a line, no ring.
        self.assertEqual("member:/projects/P/devices/HwPeer/items/HwPeerCpu/plc/HwPeerCpu/tags/Default%20tag%20table/systemConstants/HwMpHead~MpDi",
                         q.constant(run, q.MP, q.MP.prefix + "MpDi"))

    def test_ring_link_closes_the_line_into_one_ring(self):
        run = q.Run.__new__(q.Run)
        run.base = "/projects/P/devices/"
        # The CPU 1214C (one port) leaves the line; the IM 155-5's Port_2 then takes the scratch CPU's freed Port_1.
        links = [q.family_ports(run)["HwMp"], tuple(q.ports(run, 2)), q.ring_ports(run)]
        ends = [port for link in links for port in link]
        self.assertEqual(len(ends), len(set(ends)), "a port is linked twice")
        self.assertIn(q.family_ports(run)["Hw1200"][1], q.ring_ports(run))
        devices = [tuple(port[len(run.base):].split("/")[0] for port in link) for link in links]
        degree = {d: sum(link.count(d) for link in devices) for link in devices for d in link}
        self.assertEqual({"ScratchStation": 2, "HwPeer": 2, "HwMp": 2}, degree)  # Three devices, three links, two each: one ring.
        managers = [path for path in q.interfaces(run) if path.startswith(run.base + "ScratchStation/")]
        self.assertEqual(1, len(managers))
        self.assertEqual({d for link in devices for d in link}, {path[len(run.base):].split("/")[0] for path in q.interfaces(run)})

    def ring(self, refused=()):
        """ring() with mrp/link stubbed; refused names the (device, domain) role changes V21 refuses."""
        run = q.Run.__new__(q.Run)
        run.base, run.current, calls = "/projects/P/devices/", {"view": {}}, []
        run.h = type("H", (), {"report": {}})()
        def mrp(run_, interface, role, domain=None, create=False, restore=False):
            calls.append(("mrp", interface.split("/")[4], role, domain, create, restore))
            return (interface.split("/")[4], domain) not in refused
        saved = q.mrp, q.link
        q.mrp, q.link = mrp, lambda run_, kind, number, restore=False, pair=None, label=None: calls.append((kind, number, restore))
        try:
            closed = q.ring(run)
        finally:
            q.mrp, q.link = saved
        return closed, calls, run.h.report

    def test_boundary_steps_are_recorded_and_recovered_only_in_discovery(self):
        for discover in (True, False):
            run = q.Run.__new__(q.Run)
            run.h = type("H", (), {"report": {}, "args": type("A", (), {"discover": discover})()})()
            run.current, recovered = {"view": {"k": 1}}, []
            def refused(*args, **kwargs):
                raise RuntimeError("change_prepare: UnsupportedCapability")
            def failed(*args, **kwargs):
                raise RuntimeError("change_apply: CompileFailed")
            run.recover = lambda label, prepared, before: recovered.append((label, before))
            run.prepare = refused
            if discover:
                self.assertIsNone(run.apply("step", "network_mrp_set", {}, boundary=True))
                self.assertEqual("prepare", run.h.report["boundaries"]["step"]["stage"])
                run.prepare, run.finish = (lambda *a, **k: {"changeId": "c", "previewHash": "h"}), failed
                self.assertIsNone(run.apply("apply step", "network_mrp_set", {}, boundary=True))
                self.assertEqual(("apply", [("apply step", {"view": {"k": 1}})]), (run.h.report["boundaries"]["apply step"]["stage"], recovered))
                with self.assertRaises(RuntimeError):  # A step not marked as a boundary fails even a discovery pass.
                    run.apply("plain", "hardware_plug", {})
            else:
                with self.assertRaises(RuntimeError):
                    run.apply("step", "network_mrp_set", {}, boundary=True)

    def test_shared_device_reports_whether_it_applied(self):
        run = q.Run.__new__(q.Run)
        run.base, calls = "/projects/P/devices/", []
        for result, expected in (({"changeId": "c"}, True), (None, False)):
            run.apply = lambda *args, result=result, **kwargs: calls.append(kwargs["removed"]) or result
            self.assertIs(expected, q.shared_device(run))
        self.assertEqual([q.constant(run, q.SP, "HwSpHead~SpDi3")], calls[0])  # hw5: SpDi3's HwPeerCpu system constant goes.

    def test_profile_gates_need_the_catalog_link_and_the_profile_refusal(self):
        identifier = "OrderNumber:6ES7 131-6BF00-0BA0/V1.1"
        for link, message, ok in ((q.PROFILE_LINK + identifier, "The capability profile of %s does not declare InformationText writable on the module." % identifier, True),
                                  (None, "", False), (q.PROFILE_LINK + identifier, "The item declares no writable InformationText.", False)):
            run = q.Run.__new__(q.Run)
            run.base, filters, refused = "/projects/P/devices/", [], []
            run.h = type("H", (), {"report": {}, "args": None, "stage": lambda self, name, operation: operation(),
                                   "tool": lambda self, name, arguments: filters.append(arguments["filter"]) or {"entries": [{"typeIdentifier": identifier, "profile": link}, {"typeIdentifier": "x", "profile": None}]}})()
            run.refuse = lambda kind, label, payload, code: refused.append((kind, payload, code)) or {"code": code, "message": message}
            if ok:
                q.profile_gates(run, identifier)
                self.assertEqual(["6ES7 131-6BF00-0BA0"], filters)
                self.assertEqual([("hardware_attributes", {"hardwarePath": "/projects/P/devices/HwSp/items/SpDi3", "attributes": {"InformationText": "HwText"}}, "AttributeNotWritable")], refused)
            else:
                with self.assertRaises(Exception):
                    q.profile_gates(run, identifier)

    def test_restore_slice_restores_each_topology_change_the_full_run_lacks(self):
        calls, run = [], q.Run.__new__(q.Run)
        run.base, run.current, run.coverage = "/projects/P/devices/", {"view": {}}, {}
        run.h = type("H", (), {"report": {}, "args": None})()
        run.expect = lambda ok, label, message, detail=None: calls.append(("expect", bool(ok)))
        def record(name, result=None):
            return lambda *args, **kwargs: calls.append((name, *[a for a in args[1:] if isinstance(a, str)], kwargs.get("restore", False))) or result
        stubs = {"add_cpu": record("add_cpu"), "network": record("network"), "station": record("station"), "sp_station": record("sp_station", q.SP),
                 "sp_modules": record("sp_modules", ("old", "new")), "plug": record("plug"), "profile_gates": record("profile_gates"),
                 "shared_device": record("shared", True), "shared_access": record("access", True), "mrp": record("mrp", True)}
        saved = {name: getattr(q, name) for name in stubs}
        try:
            for name, stub in stubs.items():
                setattr(q, name, stub)
            q.restore_slice(run)
        finally:
            for name, original in saved.items():
                setattr(q, name, original)
        manager, _, head = q.interfaces(run)
        self.assertEqual([("shared", True), ("shared", False), ("access", "SpDi4", "OutsideOfProject", True), ("mrp", manager, "Manager", q.RING, True),
                          ("mrp", manager, "Manager", q.RING, False), ("mrp", head, "Client", q.RING, True)], calls[calls.index(("profile_gates", "old", False)) + 1:-1])
        self.assertEqual(("expect", False), calls[-1])  # The stubs mark no coverage, so the slice's coverage gate fails.

    def test_ring_opens_the_line_sets_roles_then_relinks_and_closes(self):
        closed, calls, _ = self.ring()
        self.assertTrue(closed)
        self.assertEqual([("hardware_port_disconnect", 1, False), ("hardware_port_disconnect", 2, False), ("hardware_port_disconnect", 2, False),
                          ("mrp", "ScratchStation", "Manager", q.RING, True, False), ("mrp", "HwPeer", "Client", q.RING, False, True),
                          ("mrp", "HwPeer", "Client", q.RING, False, False), ("mrp", "HwMp", "Client", q.RING, False, False),
                          ("hardware_port_connect", 2, False), ("hardware_port_connect", 2, False),
                          ("hardware_port_connect", 2, True), ("hardware_port_connect", 2, False)], calls)

    def test_ring_falls_back_to_the_default_domain_and_relinks_without_roles(self):
        closed, calls, _ = self.ring(refused={("ScratchStation", q.RING)})
        self.assertTrue(closed)
        self.assertEqual([("mrp", "ScratchStation", "Manager", None, False, False), ("mrp", "HwPeer", "Client", None, False, True)], [c for c in calls if c[0] == "mrp"][1:3])
        closed, calls, report = self.ring(refused={("HwMp", q.RING)})
        self.assertFalse(closed)  # The IM refused its role: the applied roles go back to NotInRing, the line is relinked, no ring.
        self.assertEqual([("mrp", "ScratchStation", "NotInRing", None, False, False), ("mrp", "HwPeer", "NotInRing", None, False, False)],
                         [c for c in calls if c[0] == "mrp" and c[2] == "NotInRing"])
        self.assertEqual([("hardware_port_connect", 2, False)] * 2, [c for c in calls if c[0] == "hardware_port_connect"])
        self.assertEqual("skipped", report["boundaries"]["ring"]["stage"])

    def test_gsd_device_not_installed_is_recorded_not_failed(self):
        class Fake:
            def __init__(self):
                self.report = {}
            def stage(self, name, operation):
                return operation()
            def tool(self, name, arguments):
                return {"entries": [{"typeIdentifier": "OrderNumber:6ES7 999/V1.0"}]}  # A catalog hit that is no GSD device.
        run = q.Run.__new__(q.Run)
        run.h = Fake()
        run.apply = lambda *a, **k: self.fail("A missing GSD must not change the project")
        q.gsd_device(run)
        self.assertEqual({"gsdCatalog": [], "gsdNotInstalled": True}, run.h.report)

    def test_installed_gsd_device_is_added_put_on_the_subnet_and_assigned(self):
        class Fake:
            def __init__(self):
                self.report = {}
            def stage(self, name, operation):
                return operation()
            def tool(self, name, arguments):
                return {"entries": [{"typeIdentifier": "GSD:GSDML-V2.43-SIEMENS-TIAMCPIDEVICE.XML/D/DAP1", "typeName": "TiaMcpIDevice"}]}
        run = q.Run.__new__(q.Run)
        run.h, run.base, calls = Fake(), "/projects/P/devices/", []
        interface = run.base + "HwGsd/items/HwGsdHead/items/PROFINET%20interface"
        def apply(label, kind, payload, preview=None, shown=None, plc=None, **expected):
            calls.append((kind, payload))
            if kind == "hardware_add":  # The added device shows its head and interface in the next state.
                run.current = {"view": {"hw:" + run.base + "HwGsd/items/HwGsdHead": {"network": None},
                                        "hw:" + interface: {"network": {"ioConnectors": [{}], "nodes": [{"name": "X1"}]}}}}
        run.apply, run.current = apply, {"view": {}}
        q.gsd_device(run)
        self.assertEqual(["hardware_add", "network_configure", "network_io_assign"], [kind for kind, _ in calls])
        self.assertEqual(interface, calls[1][1]["hardwarePath"])
        self.assertEqual({"hardwarePath": interface, "connectorIndex": 0, "controllerIndex": 0,
                          "controllerHardwarePath": run.base + "HwPeer/items/HwPeerCpu/items/PROFINET%20interface_1"}, calls[2][1])
        self.assertNotIn("gsdNotInstalled", run.h.report)
        self.assertEqual(interface, run.h.report["gsdDevice"]["hardwarePath"])

    def test_rack_is_the_one_root_item_beside_the_head(self):
        run = q.Run.__new__(q.Run)
        run.base = "/projects/P/devices/"
        row = lambda slot, kind="None": {"positionNumber": slot, "classification": kind}
        run.current = {"view": {"hw:/projects/P/devices/HwSp/items/Rail_0": row(0), "hw:/projects/P/devices/HwSp/items/HwSpHead": row(0),
                                "hw:/projects/P/devices/HwSp/items/Rail_0/items/X": row(0), "hw:/projects/P/devices/HwSp/items/Srv": row(4)}}
        self.assertEqual("Rail_0", q.rack(run, "HwSp", "HwSpHead"))
        run.current["view"]["hw:/projects/P/devices/HwSp/items/Rail_1"] = row(0)
        with self.assertRaises(AssertionError):
            q.rack(run, "HwSp", "HwSpHead")

    def test_discovery_records_instead_of_failing(self):
        class Fake:
            def __init__(self, discover):
                self.args, self.report = type("Args", (), {"discover": discover})(), {"certificateIdentity": {}, "notQualified": {}}
        for discover in (False, True):
            run = q.Run(Fake(discover))
            self.assertEqual(discover, run.h.report["discovery"])
            if discover:
                run.expect(False, "step", "differs", {"changed": {}})
                self.assertEqual([{"message": "differs", "detail": {"changed": {}}}], run.h.report["discovered"]["step"])
            else:
                with self.assertRaises(AssertionError):
                    run.expect(False, "step", "differs")

    def test_deferred_apply_skips_compile_and_save_only(self):
        class Fake:
            def __init__(self, stages, compile=None):
                self.report, self.result = {"certificateIdentity": {}, "notQualified": {}}, {"stages": stages, "compile": compile, "preview": {}, "recoveryArchiveSha256": "a", "afterFingerprint": "f"}
            def apply_prepared(self, kind, prepared, deferred=False):
                self.deferred = deferred
                return self.result
            def stage(self, name, operation):
                return operation()
            def tool(self, name, arguments):
                return {"state": "applied", **self.result, "recoveryArchive": {}}
        checkpoint = list(STAGES[:3])
        for stages, ok in ((checkpoint, True), (checkpoint + ["compile_finished", "changed_project_saved"], False)):
            h = Fake(stages)
            run = q.Run(h)
            run.current, run.snap = {"view": {}}, lambda label: {"view": {"hw:x": 1}}
            call = lambda: run.finish("add", "hardware_add", {"changeId": "c", "preview": {}}, added=["hw:x"], deferred=True)
            if ok:
                call()
                self.assertTrue(h.deferred)
                self.assertEqual(["add"], run.coverage["hardware_add"]["applied"])
            else:
                with self.assertRaises(AssertionError):
                    call()

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
