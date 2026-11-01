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

    def gsd_slice(self, entries, coverage, device=True, strict=False):
        """gsd_slice with stubbed steps: the calls in order and the report. strict: expect raises on failure, as in a qualification run."""
        self.calls = calls = []  # Kept on the test, so a raising run still shows what it did.
        run = q.Run.__new__(q.Run)
        run.base, run.current, run.coverage = "/projects/P/devices/", {"view": {}}, coverage
        run.h = type("H", (), {"report": {}, "args": None})()
        def expect(ok, label, message, detail=None):
            calls.append(("expect", bool(ok)))
            if strict and not ok:
                raise AssertionError(message)
        run.expect = expect
        def gsd_device(run, rows=None, restore=False):
            calls.append(("gsd_device", rows, restore))
            if device:
                run.h.report["gsdDevice"] = {}
        def entries_of(run):
            run.h.report["gsdCatalog"] = entries
            return entries
        stubs = {"gsd_entries": entries_of, "add_cpu": lambda run, label, name, **k: calls.append(("add_cpu", name)),
                 "network": lambda run, scratch=True: calls.append(("network", scratch)), "station": lambda run: calls.append(("station",)), "gsd_device": gsd_device}
        saved = {name: getattr(q, name) for name in stubs}
        try:
            for name, stub in stubs.items():
                setattr(q, name, stub)
            q.gsd_slice(run)
        finally:
            for name, original in saved.items():
                setattr(q, name, original)
        return calls, run.h.report

    def test_gsd_slice_runs_only_what_the_gsd_device_needs(self):
        entry, restored = [{"typeIdentifier": "GSD:X/D/DAP1"}], {"network_io_assign": {"restored": ["assign HwGsd to HwPeerCpu, restored"]}}
        calls, report = self.gsd_slice(entry, restored)
        # The CPU, HwNet without the scratch CPU, HwPeerIo (ET 200MP), then the GSD device with its assignment restored.
        self.assertEqual([("expect", True), ("add_cpu", "HwPeer"), ("network", False), ("station",), ("gsd_device", entry, True), ("expect", True)], calls)
        self.assertNotIn("gsdNotInstalled", report)
        # The final gate needs both the assignment's restore and the recorded device.
        self.assertEqual(("expect", False), self.gsd_slice(entry, {}, device=False)[0][-1])
        self.assertEqual(("expect", False), self.gsd_slice(entry, {})[0][-1])
        self.assertEqual(("expect", False), self.gsd_slice(entry, restored, device=False)[0][-1])
        # Without an installed GSD nothing changes the project: gsdNotInstalled is recorded and the gate fails.
        calls, report = self.gsd_slice([], {})
        self.assertEqual([("expect", False)], calls)
        self.assertEqual({"gsdCatalog": [], "gsdNotInstalled": True}, report)
        # Two entries fail before any mutation.
        with self.assertRaises(AssertionError):
            self.gsd_slice(entry * 2, restored, strict=True)
        self.assertEqual([("expect", False)], self.calls)

    def test_body_runs_the_selected_slice_with_its_limitations(self):
        args = type("A", (), {"slice": "gsd", "discover": False})()
        ran, h = [], type("H", (), {"report": {}, "args": args, "owned_scratch": lambda self, name: None,
                                    "tool": lambda self, name, arguments=None: {"projects": [{"path": "P.ap21", "name": "P"}]}})()
        saved = q.gsd_slice, q.Run.snap
        try:
            q.gsd_slice, q.Run.snap = (lambda run: ran.append(run.base)), (lambda self, label: {"view": {}})
            q.body(h)
        finally:
            q.gsd_slice, q.Run.snap = saved
        self.assertEqual(["/projects/P/devices/"], ran)
        title, limitations = q.heading(args)
        self.assertIs(q.GSD_SLICE_LIMITATIONS, limitations)
        self.assertIn("GSD device", title)
        self.assertIs(q.SLICE_LIMITATIONS, q.heading(type("A", (), {"slice": "restores", "discover": True})())[1])
        self.assertIs(q.LIMITATIONS, q.heading(type("A", (), {"slice": None, "discover": False})())[1])

    def test_gsd_device_restores_its_assignment_once_before_applying_it(self):
        run = q.Run.__new__(q.Run)
        run.h, run.base, calls = type("H", (), {"report": {}})(), "/projects/P/devices/", []
        interface = run.base + "HwGsd/items/HwGsdHead/items/PROFINET%20interface"
        def apply(label, kind, payload, preview=None, shown=None, plc=None, **expected):
            calls.append((label, expected.get("restore", False), expected.get("deferred", False)))
            if kind == "hardware_add":
                run.current = {"view": {"hw:" + interface: {"network": {"ioConnectors": [{}], "nodes": [{"name": "X1"}]}}}}
        run.apply, run.current = apply, {"view": {}}
        q.gsd_device(run, [{"typeIdentifier": "GSD:X/D/DAP1"}], restore=True)
        self.assertEqual([("add GSD device HwGsd", False, True), ("put HwGsd on HwNet", False, True),
                          ("assign HwGsd to HwPeerCpu, restored", True, False), ("assign HwGsd to HwPeerCpu", False, False)], calls)
        self.assertEqual({}, run.h.report.get("gsdCatalog", {}))  # Given rows are not searched again.

    def test_network_can_leave_the_scratch_cpu_off_hwnet(self):
        run, labels = q.Run.__new__(q.Run), []
        run.base, run.apply = "/projects/P/devices/", lambda label, *a, **k: labels.append(label)
        q.network(run, scratch=False)
        q.network(run)
        self.assertEqual(["put HwPeerCpu on HwNet", "put HwPeerCpu on HwNet", "put ScratchPLC on HwNet"], labels)

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

    def slice_run(self, stubs, coverage=None, report=None):
        """refusal_slice with stubbed steps: the recorded calls (name, string args, restore) and the final gate."""
        calls, run = [], q.Run.__new__(q.Run)
        run.base, run.current, run.coverage = "/projects/P/devices/", {"view": {}}, coverage if coverage is not None else {}
        run.h = type("H", (), {"report": dict(report or {}), "args": None})()
        run.expect = lambda ok, label, message, detail=None: calls.append(("expect", bool(ok)))
        def record(name, result=None):
            return lambda *args, **kwargs: calls.append((name, *[a for a in args[1:] if isinstance(a, str)], kwargs.get("restore", False))) or result
        defaults = {"add_cpu": record("add_cpu"), "network": record("network"), "plug": record("plug"), "address_change": record("address_change"),
                    "sp_station": record("sp_station", q.SP), "sp_modules": record("sp_modules", ("old", "new")), "channel_gate": record("channel_gate"),
                    "gsd_entries": lambda run: [{"typeIdentifier": "GSD:X/DAP/DAP1"}], "gsd_device": lambda run, rows=None, restore=False: run.h.report.update(gsdDevice={}),
                    "versionless_gate": record("versionless_gate"), "profiles": lambda run, only=None: calls.append(("profiles", sorted(only))),
                    "shared_device": record("shared", True), "shared_refusal": record("shared_refusal"), "shared_access": record("access", True),
                    "shared_disconnect": record("disconnect", True), "link_refusals": record("link_refusals"), "second_project": record("second_project")}
        stubs = {**defaults, **stubs}
        saved = {name: getattr(q, name) for name in stubs}
        try:
            for name, stub in stubs.items():
                setattr(q, name, stub)
            q.refusal_slice(run)
        finally:
            for name, original in saved.items():
                setattr(q, name, original)
        return calls

    def test_refusal_slice_moves_an_address_then_shares_refuses_and_opens_a_second_project(self):
        complete = {kind: {need: ["x", "y"]} for kind, need in (("network_mrp_set", "refused"), ("hardware_shared_device_access", "refused"), ("hardware_channel_attributes", "refused"),
                                                               ("hardware_attributes", "refused"), ("network_io_assign", "restored"), ("hardware_replace", "restored"))}
        complete["hardware_shared_device_access"]["applied"] = ["SpDi4 outside"]
        opened = []
        second = lambda run, module, first: run.h.report.update(secondProject={}) or opened.append((module, first[0]))
        calls = self.slice_run({"second_project": second}, complete)
        names = [call[0] for call in calls]
        # HwDi and HwDi2 come first, so the address change moves HwDi's start before the scratch CPU takes SpDi3.
        self.assertEqual(["add_cpu", "network", "plug", "plug", "address_change", "sp_station", "sp_modules", "plug", "plug", "plug", "channel_gate", "versionless_gate",
                          "profiles", "shared", "shared_refusal", "access", "disconnect", "link_refusals", "expect"], names)
        self.assertEqual(("profiles", ["GSD:X/DAP/DAP1"]), calls[names.index("profiles")])
        self.assertEqual([("old", "/projects/P/devices/")], opened)
        self.assertEqual(("expect", True), calls[-1])
        # Without the shared device no second project opens and the gate fails; without the GSD device its gates are skipped.
        calls = self.slice_run({"shared_device": lambda run, restore=False: False, "gsd_device": lambda run, rows=None, restore=False: None,
                                "second_project": second}, {})
        self.assertFalse({"versionless_gate", "shared_refusal", "disconnect"} & {call[0] for call in calls})
        self.assertEqual(1, len(opened))
        self.assertEqual(("expect", False), calls[-1])

    def test_sp_station_assigns_to_its_own_plc_and_creates_the_io_system(self):
        run, calls = q.Run.__new__(q.Run), []
        run.base = "/projects/P/devices/"
        head, interface = run.hw("HwSp", "items", "HwSpHead"), run.hw("HwSp", "items", *q.SP.interface)
        run.current = {"view": {interface: {"name": "PROFINET interface", "network": {"nodes": [{"name": "X1"}]}}, head: {"attributes": {"Author": "a"}},
                                "hw:/projects/P/devices/HwSp/items/Rack_0": {"positionNumber": 0, "classification": "None"}}}
        run.apply = lambda label, kind, payload, preview=None, shown=None, plc=None, **expected: calls.append((label, kind, payload, preview, shown, plc, expected))
        q.sp_station(run, q.SP2, q.OTHER_IO)
        label, kind, payload, preview, shown, plc, expected = calls[2]
        owner = run.path("ScratchStation", "items", "ScratchPLC")
        self.assertEqual(("assign HwSp to ScratchPLC", "network_io_assign"), (label, kind))
        self.assertEqual((owner + "/items/PROFINET%20interface_1", q.OTHER_IO), (payload["controllerHardwarePath"], payload["createIoSystemName"]))
        self.assertEqual((owner, q.scratch_plc(run), q.OTHER_IO), (shown["addressControllerOwnerPath"], plc, preview["effects"][0]["afterIoSystem"]))
        own = [q.constant(run, q.SP2, "Local~" + q.OTHER_IO), q.constant(run, q.SP2, "HwSpHead~HwSpHead")]
        state = dict.fromkeys(own + [q.constant(run, q.SP, "HwSpHead~HwSpHead")], 1)
        self.assertEqual(sorted(own), sorted(expected["added"](None, state)))  # The scratch CPU's constants, not HwPeerCpu's.
        calls.clear()
        q.sp_station(run)
        self.assertNotIn("createIoSystemName", calls[2][2])
        self.assertEqual(q.IO_SYSTEM, calls[2][3]["effects"][0]["afterIoSystem"])

    def test_link_refusals_refuse_both_ways_around_the_ring_roles(self):
        run, calls = q.Run.__new__(q.Run), []
        run.base, run.h = "/projects/P/devices/", type("H", (), {"report": {}, "args": None})()
        texts = {"a ring role on a port linked to a non-ring port": "A/P2 would be a ring port while linked to B/P2, which is not a ring port; ...",
                 "NotInRing on a port linked to a ring port": "B/P2 would leave the ring while linked to A/P2, a ring port; ..."}
        run.refuse = lambda kind, label, payload, code: calls.append(("refuse", payload["role"], code)) or {"message": texts[label]}
        run.expect = lambda ok, label, message, detail=None: calls.append(("expect", bool(ok)))
        saved = q.link, q.mrp
        try:
            q.link = lambda run_, kind, number, restore=False, pair=None, label=None: calls.append((kind, number))
            q.mrp = lambda run_, interface, role, domain=None, create=False, restore=False: calls.append(("mrp", role, domain, create)) or True
            q.link_refusals(run)
        finally:
            q.link, q.mrp = saved
        self.assertEqual([("hardware_port_connect", 2), ("refuse", "Manager", "NetworkPrerequisite"), ("expect", True), ("hardware_port_disconnect", 2),
                          ("mrp", "Manager", q.RING, True), ("mrp", "Client", q.RING, False), ("hardware_port_connect", 2),
                          ("refuse", "NotInRing", "NetworkPrerequisite"), ("expect", True)], calls)
        calls.clear()
        q.message(run, "x", {"message": "B/P2 would be a ring port"}, "would leave the ring")
        self.assertEqual([("expect", False)], calls)

    def test_shared_disconnect_names_the_shared_connector_and_both_owners(self):
        run, calls = q.Run.__new__(q.Run), []
        run.base, run.current = "/projects/P/devices/", {"view": {"k": 1}}
        run.apply = lambda label, kind, payload, preview=None, shown=None, plc=None, **expected: calls.append((payload, preview, shown, plc, expected)) or {"changeId": "c"}
        self.assertTrue(q.shared_disconnect(run))
        payload, preview, shown, plc, expected = calls[0]
        self.assertEqual({"hardwarePath": run.path("HwSp", "items", *q.SP.interface), "connectorIndex": 1, "disconnect": True, "sharedDevice": True}, payload)
        self.assertEqual([run.path("HwPeer", "items", "HwPeerCpu")], shown["sharedAddressControllerOwnerPaths"])
        self.assertEqual((q.scratch_plc(run), True, True), (plc, expected["restore"], expected["boundary"]))
        self.assertIs(run.b0, run.current)

    def test_second_project_stations_must_be_complementary(self):
        def station(base, accesses, external):
            prefix = "hw:" + base + "HwSp/items/"
            view = {prefix + name: {"typeIdentifier": "T", "positionNumber": slot, "attributes": {"SharedDeviceAccessType": access}}
                    for (name, slot), access in zip((("SpDi3", 1), ("SpDi", 2), ("SpDi4", 3)), accesses)}
            view[prefix + "HwSpHead/items/PROFINET%20interface"] = {"attributes": {"NumberOfExternalIoControllers": external}}
            return view
        first = ("/projects/A/devices/", station("/projects/A/devices/", ("PLC", "PLC", "OutsideOfProject"), 1))
        for accesses, external, ok in ((("OutsideOfProject", "OutsideOfProject", "PLC"), 2, True), (("OutsideOfProject", "PLC", "PLC"), 2, False),
                                       (("OutsideOfProject", "OutsideOfProject", "PLC"), 1, False)):
            run = q.Run.__new__(q.Run)
            run.base, run.h = "/projects/B/devices/", type("H", (), {"report": {}, "stage": lambda self, name, operation: operation()})()
            run.current = {"view": station(run.base, accesses, external)}
            if ok:
                q.complementary(run, first)
                self.assertEqual([1, 2], run.h.report["secondProject"]["externalIoControllers"])
            else:
                with self.assertRaises(AssertionError):
                    q.complementary(run, first)

    def test_address_change_can_require_a_moved_start(self):
        for moved, after, ok in ((True, 4, True), (True, 0, False), (False, 0, True)):
            run, checks = q.Run.__new__(q.Run), []
            run.base, run.h = "/projects/P/devices/", type("H", (), {"report": {}, "stage": lambda self, name, operation: operation(),
                                                                     "tool": lambda self, name, arguments: {"entries": [{"typeIdentifier": "W", "articleNumber": "6ES7 521-1BL00", "typeName": "DI 32x24VDC HF"}]}})()
            module = run.path("ScratchStation", "items", "HwDi")
            run.current = {"view": {"hw:" + module: {"addresses": [{"ioType": "Input", "startAddress": 0, "length": 16}]}}}
            tag = q.scratch_plc(run) + "/tags/Default%20tag%20table/tags/HwInput"
            run.refuse = lambda *a, **k: {"details": {"referencedTags": [{"path": tag}]}}
            run.expect = lambda ok_, *a, **k: None
            run.apply = lambda label, kind, payload, preview=None, shown=None, plc=None, **expected: checks.append(expected.get("check"))
            q.address_change(run, moved)
            applied = {"readbacks": [{"role": "hardware-replace-inventory", "addressMap": [{"changed": True}],
                                      "changedReferencedTags": [{"path": tag, "startAddress": 0, "rangeAfter": {"startAddress": after}}]}]}
            if ok:
                checks[-1](applied, {})
                self.assertEqual({"before": 0, "after": after}, run.h.report["selected"]["addressStart"])
            else:
                with self.assertRaises(AssertionError):
                    checks[-1](applied, {})

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
