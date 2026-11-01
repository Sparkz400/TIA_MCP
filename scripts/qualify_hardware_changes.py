"""Qualify the X10 hardware change kinds natively in one owned scratch CPU 1511-1 PN.

Phase A adds a second CPU, puts both PROFINET nodes on one subnet, plugs two DI modules, and applies every covered kind with
checkpoint, device compile and save: attributes, type replacement, port connect and disconnect, and module deletion. Each
preview is asserted against the request, and a complete state (hardware_inspect of every item with its complete attribute
values, ports and subnet membership, device_inventory rows, and the program exports of every PLC) must differ from the
previous one in exactly the intended keys. TIA-coupled side effects are tolerated only where named, and are recorded. A fresh
Portal then reopens the saved project, which must show the identical state. Phase B applies one more change per kind from
that state and restores its checkpoint in the applying Portal; the recovered copy must equal the pre-change state. A second
CPU is added and restored already in phase A, because generated CPU security is restricted to the worker's own new scratch
(a reopened project refuses it with ScratchIsolationRequired), and so is a port connection, because a second link between the
two CPUs would form a ring. Typed refusals must change nothing.

Two more families pass the same gates: a CPU 1214C on the subnet with SM 1221 modules, replaced by their successor article and
linked to the scratch CPU's free port, and an ET 200MP station (IM 155-5 PN ST) assigned to the second CPU, with DI modules
replaced between firmware versions and linked to the second CPU's free port. An IO device fails the device compile until it
is assigned, so the station's add and node change apply without compile and save, and the assignment compiles and saves all
three. ET 200SP (IM 155-6 PN ST V6.4) joins the second CPU's IO system with its generated server module, which moves to slot 4;
catalog-selected DI 8x24VDC ST modules then pass the same family gates.

The topology gates follow: the ET 200SP station becomes a shared device (its connector 1 joins a new IO system of the scratch
CPU, sharedDevice), one of its modules is handed outside the project (hardware_shared_device_access), and the line of port
links becomes a ring with media redundancy: the single-port CPU 1214C leaves the line, an MRP domain takes the scratch CPU as
manager and the second CPU and the ET 200MP head as clients (network_mrp_set), and the head's free Port_2 closes the ring on
the scratch CPU's Port_1. When an operator has installed the project's own exported I-device GSD in TIA (V21 Openness cannot
install one), that GSD device is added, assigned to the second CPU and compiled; otherwise the run records gsdNotInstalled. Phase B
restores one change of each new kind and a tag-referenced module replacement that changes its addresses (acceptAddressChange with
acknowledgedTags).

--discover runs the same sequence but records every unexpected state difference, preview and refusal under discovered
instead of failing: a key-discovery pass that fixes the expectations, never evidence.

--slice restores runs a short sequence instead: the two stations on the second CPU, the capability-profile gates (the catalog
search names a profile; a write the profile does not declare is refused) and a checkpoint restore of each topology change the
full run applied without one (the shared-device assignment, OutsideOfProject, the createDomain manager, the IM 155-5 client).
--slice gsd runs only the installed I-device GSD device and what it needs (see gsd_slice).
--slice refusals runs the native refusals and paths the full run lacks (see refusal_slice): the MRP port-link rule both ways, a
PLC-to-PLC shared-device access, channel and versionless capability-profile refusals, the sharedDevice disconnect, an address change
that moves the start address, and a second owned scratch whose CPU takes the module the first project set OutsideOfProject.
"""
from __future__ import annotations

import argparse
from collections import namedtuple
import json
from pathlib import Path
import re

import hardware_profiles
from library_native_harness import Harness, require
from qualification_owned_scratch import snapshot
from qualify_access_changes import Run as StateRun
from qualify_plc_object_mutations import coverage_gaps, diff, view
from qualify_remote_io import SERVER_MODULE_NAMES
from urllib.parse import quote

KINDS = ("hardware_add", "hardware_attributes", "hardware_replace", "hardware_port_connect", "hardware_port_disconnect", "hardware_delete",
         "network_mrp_set", "hardware_shared_device_access")
CPU = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"
# Two firmware versions of DI 16x24VDC HF; the change between them keeps the module's addresses.
MODULE, MODULE_V2 = "OrderNumber:6ES7 521-1BH00-0AB0/V1.0", "OrderNumber:6ES7 521-1BH00-0AB0/V2.0"
S7_1200, IM = "OrderNumber:6ES7 214-1AG40-0XB0/V4.7", "OrderNumber:6ES7 155-5AA01-0AB0/V4.1"
# SM 1221 DI16x24VDC V1.0 and its successor article V2.0, which keeps the module's addresses.
SM, SM_NEXT = "OrderNumber:6ES7 221-1BH30-0XB0/V1.0", "OrderNumber:6ES7 221-1BH32-0XB0/V2.0"
CONFIGURED = ("hardware_replace", "hardware_port_connect", "hardware_port_disconnect", "network_configure", "hardware_plug", "hardware_move",
              "hardware_transfer_area_create", "hardware_transfer_area_delete", "network_io_assign", "network_mrp_set", "hardware_shared_device_access")
# A rack, the PLC whose default tag table holds its modules' system constants (named prefix + module name), and an IO device's interface.
Station = namedtuple("Station", "device rack plc_device plc prefix interface")
SCRATCH = Station("ScratchStation", "Rail_0", "ScratchStation", "ScratchPLC", "Local~", None)
S1200 = Station("Hw1200", "Rack_0", "Hw1200", "Hw1200Cpu", "Local~", None)
MP = Station("HwMp", "Rail_0", "HwPeer", "HwPeerCpu", "HwMpHead~", ("HwMpHead", "items", "PROFINET%20interface"))
IO_SYSTEM = "HwPeerIo"
# ET 200SP: the IM with the captured generated-server contract; its rack is read after hardware_add, its DI modules are catalog-selected.
IM_SP, SERVER = "OrderNumber:6ES7 155-6AU02-0BN0/V6.4", "OrderNumber:6ES7 193-6PA00-0AA0/V1.2"
SP = Station("HwSp", None, "HwPeer", "HwPeerCpu", "HwSpHead~", ("HwSpHead", "items", "PROFINET%20interface"))
SP_DI, WIDE_DI = ("DI 8x24VDC ST", "6ES7 131-6BF"), ("DI 32x24VDC HF", "6ES7 521-1BL")
SHARED_IO, RING = "HwScratchIo", "HwRing"
# The same ET 200SP station in a second project, assigned to that project's own scratch CPU (refusal slice).
SP2, OTHER_IO = SP._replace(plc_device="ScratchStation", plc="ScratchPLC"), "HwOtherIo"
GSD_DESIGNATION = "TiaMcpIDevice"  # The I-device designation scripts/qualify_gsd_export.py exports; an operator installs that GSD in TIA.
DEVICES = (CPU, S7_1200, IM, IM_SP)  # Types the run creates with hardware_add; every other profiled type is plugged or generated.
# An IO controller's cyclic IO bandwidth is computed at the device compile and not saved (a fresh open reads 0), so it is not compared.
DERIVED = ("CalculatedBandwidthForCyclicIoData",)
# Per family: its rack, the module plugged at slots 2 and 3, and the replacement of the first one.
FAMILIES = ((S1200, ("HwSm", "HwSm2"), SM, SM_NEXT), (MP, ("MpDi", "MpDi2"), MODULE, MODULE_V2))
CRITERIA = {
    "AC-identity": "refused: typed refusals of duplicate, unknown, built-in, software-bearing, unchanged, unconnected or already connected targets, each leaving the complete state unchanged.",
    "AC-preview": "Every preview is asserted against the request before apply; a wrong previewHash refuses at apply.",
    "AC-preserve": "applied: hardware_inspect items (complete attribute values, addresses, channels, ports, subnet membership), device_inventory rows and every PLC's program exports differ in exactly the intended keys; named TIA-coupled side effects are recorded under sideEffects.",
    "AC-native": "applied + persisted: checkpoint, device compile and save stages (configured_hardware_preservation_verified for replace and ports), change_status and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the pre-change state.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json, limitations and sideEffects.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1, an added CPU 1511-1 PN V4.1 and an added CPU 1214C DC/DC/DC V4.7 (both configureScratchSecurity) on one subnet, one "
    "ET 200MP IM 155-5 PN ST V4.1 station and one ET 200SP IM 155-6 PN ST V6.4 station assigned to the added CPU 1511-1 PN; HMI and drive devices are not added, "
    "and a GSD device only when an operator has installed the I-device GSD that scripts/qualify_gsd_export.py exports (designation TiaMcpIDevice); "
    "V21 Openness has no GSD installation API, so otherwise the run records gsdNotInstalled.",
    "Shared device: the ET 200SP interface's connector 1 joins a new IO system of the scratch CPU while connector 0 stays with the added CPU 1511-1 PN, in one "
    "project; one ET 200SP DI module is set to SharedDeviceAccessType OutsideOfProject. A second project's controller is not configured.",
    "Media redundancy: one MRP domain on the subnet with the scratch CPU as manager and the added CPU 1511-1 PN and the IM 155-5 PN ST as clients; the "
    "single-port CPU 1214C leaves the line first. Ring ports stay the TIA defaults. The ring is configured offline only; no MRP telegrams are observed.",
    "Address-changing replacement: one DI 16x24VDC HF replaced by a catalog-selected DI 32x24VDC HF with acceptAddressChange and one acknowledged tag, "
    "then restored; the tag's absolute address is not rewritten.",
    "The ET 200MP station's hardware_add and network_configure apply with checkpoint but without compile and save, because an unassigned IO device fails the "
    "device compile; network_io_assign then compiles and saves them.",
    "Modules: DI 16x24VDC HF (6ES7 521-1BH00-0AB0) in the S7-1500 and ET 200MP racks, replaced between its firmware versions V1.0 and V2.0; SM 1221 DI16x24VDC "
    "(6ES7 221-1BH30-0XB0 V1.0) in the S7-1200 rack, replaced by its successor article 6ES7 221-1BH32-0XB0 V2.0 and back; two catalog-selected DI 8x24VDC ST "
    "entries in the ET 200SP rack. Each keeps its addresses; acceptLoss and replacements that lose child items are not exercised.",
    "hardware_attributes writes Comment and Author of a module; TIA mirrors Comment to the module's built-in submodule (recorded under sideEffects). Channel attributes are qualified by the remote-I/O run.",
    "Port interconnections between the two S7-1500 CPUs' X1 ports, the CPU 1214C's and the scratch CPU's, and the IM 155-5's and the added S7-1500 CPU's; TIA sets "
    "the ports' cable defaults (SignalDelaySelection, CableLength) on connect and clears them on disconnect (recorded under sideEffects).",
    "hardware_delete removes the last DI module of each rack (an S7-1500 rack with a gap fails compile); CPUs, HMIs and built-in items are refused by design.",
    "Engineering only; nothing is downloaded and no physical topology is observed.",
]
SLICE_LIMITATIONS = [
    "--slice restores: one owned scratch CPU 1511-1 PN V4.1, an added CPU 1511-1 PN V4.1 (configureScratchSecurity) on one subnet with one ET 200MP IM 155-5 PN ST V4.1 "
    "and one ET 200SP IM 155-6 PN ST V6.4 station assigned to it, three catalog-selected DI 8x24VDC ST in the ET 200SP rack; no S7-1200, no port links, no reopen.",
    "Capability profiles: the worker's bundled contract; one hardware_catalog_search entry naming its profile and one refused undeclared write (InformationText).",
    "Checkpoint restores of the shared-device assignment (SpDi3 to the scratch CPU), SpDi4 OutsideOfProject with one external IO controller, the MRP manager "
    "creating domain HwRing and the IM 155-5 PN ST joining it as client; the members' ports stay unlinked, so no ring is closed.",
    "Engineering only; nothing is downloaded and no physical topology is observed.",
]
GSD_SLICE_LIMITATIONS = [
    "--slice gsd: one owned scratch CPU 1511-1 PN V4.1 and an added CPU 1511-1 PN V4.1 (configureScratchSecurity) on HwNet with an ET 200MP IM 155-5 PN ST V4.1 "
    "station, whose assignment creates the IO system HwPeerIo; the scratch CPU stays off HwNet; no S7-1200, ET 200SP, port links or reopen.",
    "GSD device: the I-device GSD that scripts/qualify_gsd_export.py exports (designation TiaMcpIDevice), installed by an operator in TIA (V21 Openness has no GSD "
    "installation API); added and put on HwNet with checkpoint but without compile (an unassigned IO device fails the device compile), then assigned to HwPeerIo "
    "with compile and save, that assignment restored once and applied again. No modules are plugged into the GSD device; nothing is downloaded.",
]
REFUSAL_LIMITATIONS = [
    "--slice refusals: one owned scratch CPU 1511-1 PN V4.1 and an added CPU 1511-1 PN V4.1 (configureScratchSecurity) on HwNet, two DI 16x24VDC HF in the scratch "
    "rack, one ET 200SP IM 155-6 PN ST V6.4 station whose assignment creates HwPeerIo, with three catalog-selected DI 8x24VDC ST, and the operator-installed "
    "I-device GSD device on HwPeerIo; no S7-1200, ET 200MP or reopen.",
    "Refusals: the MRP port-link rule both ways (Manager on the scratch CPU while its Port_2 links HwPeerCpu's non-ring Port_2; NotInRing on HwPeerCpu while that "
    "link joins two ring ports), a direct PLC-to-PLC shared-device access (SpDi from HwPeerCpu to the scratch CPU), a channel write the DI 8x24VDC ST profile "
    "declares read-only (InputDelay) and a write the GSD head's versionless profile, keyed by its FirmwareVersion, does not declare (InformationText). A firmware "
    "matching no versionless profile stays offline: the installed GSD has one DAP with one firmware.",
    "Applied and restored: the sharedDevice disconnect of the scratch CPU's connector, and HwDi replaced by a catalog-selected DI 32x24VDC HF whose wider input "
    "range moves its start past HwDi2 (acceptAddressChange with one acknowledged tag, whose absolute address is not rewritten).",
    "Second project: a second owned scratch whose CPU 1511-1 PN V4.1 assigns the same ET 200SP station (same device name and address) with the same DI modules; "
    "SpDi4, outside the first project, is its own, SpDi3 and SpDi are OutsideOfProject with two external IO controllers. Head, interface, ports and server "
    "module stay assigned in both projects; V21 checks nothing across projects, so the runner compares the two stations.",
    "Engineering only; nothing is downloaded and no physical topology is observed.",
]
PROFILE_LINK = "contracts/hardware-catalog-profiles.v1.json#"  # hardware_catalog_search's profile reference: the contract path, #, the typeIdentifier.


def hardware(inspect):
    """Comparable configured hardware from hardware_inspect, keyed by exact item path; only complete captured attribute
    values are compared, so display-only values (timestamps, associations) never count as changes."""
    require(inspect["completeness"]["truncated"] is False, "hardware_inspect truncated its scope: %s" % inspect["completeness"])
    state = {}
    for item in inspect["items"]:
        row = {key: item.get(key) for key in ("name", "typeIdentifier", "positionNumber", "isBuiltIn", "isPlugged", "classification", "addresses", "channels", "gsd", "network")}
        row["attributes"] = {a["name"]: a.get("value") for a in item["attributes"]
                             if a.get("valueComplete") and a.get("valueStatus") == "captured" and a["name"] not in DERIVED}
        state["hw:" + item["path"]] = row
    return state


def devices(call):
    """Every device_inventory row by path, without the project path, row id and snapshot that differ in a recovered copy."""
    state, page = {}, call("device_inventory", {"pageSize": 200, "maxVisited": 50000})
    snapshot_id = page["snapshot"]["snapshotId"]
    require(page["snapshot"]["coverage"]["scopeComplete"] is True and page["snapshot"]["coverage"]["truncated"] is False, "device_inventory is incomplete")
    while True:
        for row in page["rows"]:
            state["inv:" + row["path"]] = {k: v for k, v in row.items() if k not in ("projectPath", "rowId")}
        if page.get("nextCursor") is None:
            return state
        page = call("device_inventory", {"snapshotId": snapshot_id, "cursor": page["nextCursor"], "pageSize": 200})


def under(path, *extra):
    """Keys of the compared state (hardware, inventory and program keys) at or below one exact object path, plus extra keys."""
    return lambda applied, state: [key for key in state if key.split(":", 1)[1] == path or key.split(":", 1)[1].startswith(path + "/")] + list(extra)


class Run(StateRun):
    """The access runner's apply/refuse/restore gates over hardware, device inventory and every PLC's program."""
    plc_compiled = StateRun.plc_compiled + ("tag_create",)

    def __init__(self, h):
        super().__init__(h)
        for key in ("certificateIdentity", "notQualified"):
            h.report.pop(key)
        h.report.update(criteria=CRITERIA, excluded={}, discovery=bool(getattr(getattr(h, "args", None), "discover", False)))
        self.base = self.selected = None
        self.raw, self.profiled = {}, {}  # The latest raw hardware_inspect item per path; profile rows per type at its first snapshot.

    def args(self, kind, payload, **top):
        # An IO assignment names the PLC that owns the controller: the second CPU unless apply selects another (plc=); a tag names its PLC.
        plc = {"plcPath": self.selected or self.path(MP.plc_device, "items", MP.plc, "plc", MP.plc)} if kind in ("network_io_assign", "tag_create") else {}
        return {"kind": kind, "payload": payload, **plc, **top}

    def plcs(self):
        return [p["path"] for p in self.h.tool("project_inventory", {"maxItems": 1000})["plcs"]]

    def domains(self):
        inventory, state, self.raw = devices(self.h.tool), {}, {}
        # One hardware_inspect per device item: four devices' attributes exceed its 2000-value budget in one call.
        for path in (key[4:] for key in inventory if "/items/" in key):
            response = self.h.tool("hardware_inspect", {"hardwarePath": path, "maxItems": 2000})
            item = hardware(response)
            require(list(item) == ["hw:" + path], "hardware_inspect of %s returned %s" % (path, list(item)))
            state.update(item)
            self.raw[path] = response["items"][0]
        return {**state, **inventory}

    def modules(self):
        """Exact paths of every module or head: a direct item of a device that is not its root rack (position 0, classified None)."""
        return [path for path, item in self.raw.items() if item.get("typeIdentifier") and len(path[len(self.base):].split("/items/")) == 2
                and "/" not in path[len(self.base):].split("/items/")[1] and not (item["positionNumber"] == 0 and item["classification"] == "None")]

    def collect(self):
        """Profile rows of each module or head type, merged over every snapshot that shows it: an attribute declared only in some
        states (a channel's interrupt OB link while its flag is on) still enters its profile."""
        for path in self.modules():
            identifier, rows = self.raw[path]["typeIdentifier"], hardware_profiles.items(self.raw, path)
            if identifier in self.profiled:
                self.profiled[identifier]["items"] = hardware_profiles.merge(self.profiled[identifier]["items"], rows)
            else:
                firmware = next((a.get("value") for a in self.raw[path].get("attributes") or [] if a["name"] == "FirmwareVersion" and isinstance(a.get("value"), str)), None)
                # A GSD device's head (its DAP) is added with hardware_add, like the IM heads.
                kind = "device" if identifier in DEVICES or identifier.startswith("GSD:") else "module"
                self.profiled[identifier] = {"path": path, "kind": kind, "items": rows, "firmware": firmware}

    def state(self):
        state = self.domains()
        for plc in self.plcs():
            inventory = self.h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})
            require(inventory["completeness"]["scopeComplete"] is True and inventory["completeness"]["truncated"] is False, "software_inspect is incomplete")
            state.update(view(inventory))
        return state

    def snap(self, label):
        self.count += 1
        destination = self.h.directory / "snapshots" / ("%03d-%s" % (self.count, re.sub(r"[^A-Za-z0-9]+", "-", label)[:40]))
        taken = {}
        def take():
            for index, plc in enumerate(self.plcs()):
                call = lambda name, arguments=None, plc=plc: self.h.tool(name, {**(arguments or {}), "plcPath": plc} if name == "software_inspect" else arguments)
                part = snapshot(call, destination / ("plc%d" % index))
                taken.setdefault("view", {}).update(view(part["inventory"], part["objects"]))
            taken["view"].update(self.domains())
            return {"keys": len(taken["view"])}
        self.h.stage("snapshot " + label, take)
        (destination / "state.json").write_text(json.dumps(taken["view"], indent=1), encoding="utf-8")
        try:
            self.collect()
        except ValueError as error:  # A profile-capture gap fails the run, or is recorded by a discovery pass.
            self.expect(False, "profiles", "Profile capture after %s: %s" % (label, error))
        return {"view": taken["view"]}

    def path(self, *parts):
        return self.base + "/".join(parts)

    def hw(self, *parts):
        return "hw:" + self.path(*parts)

    def apply(self, label, kind, payload, preview=None, shown=None, plc=None, boundary=False, **expected):
        """boundary marks a step V21 may refuse. A --discover pass records its refusal under boundaries, restores the checkpoint after a
        failed apply and returns None, so later steps still run; a qualification run fails as for any step."""
        discovering, before = boundary and getattr(getattr(self.h, "args", None), "discover", False), self.current
        self.selected = plc
        try:
            prepared = self.prepare(label, kind, payload, preview, shown)
        except Exception as error:
            if not discovering:
                raise
            self.h.report.setdefault("boundaries", {})[label] = {"stage": "prepare", "error": str(error)[:4000]}
            return None
        finally:
            self.selected = None
        try:
            applied = self.finish(label, kind, prepared, **expected)
            require(("configured_hardware_preservation_verified" in applied["stages"]) == (kind in CONFIGURED),
                    "Configured-hardware preservation stage differs for %s: %s" % (kind, applied["stages"]))
        except Exception as error:
            if not discovering:
                raise
            self.h.report.setdefault("boundaries", {})[label] = {"stage": "apply", "error": str(error)[:4000]}
            self.recover(label, prepared, before)
            return None
        return prepared

    def recover(self, label, prepared, before):
        """A failed boundary change's checkpoint restored into a new directory (the documented recovery); the run goes on from it."""
        h = self.h
        if prepared["changeId"] in self.applied:
            self.applied.remove(prepared["changeId"])
        recovered = h.stage("restore the checkpoint after " + label, lambda: h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
        require(recovered["state"] == "restored", "Checkpoint recovery after %s failed: %s" % (label, recovered.get("state")))
        self.project = recovered["recoveredProject"]["path"]
        self.current = self.snap("recovered " + label)
        self.expect(self.current["view"] == before["view"], label, "State recovered after %s differs: %s" % (label, diff(before["view"], self.current["view"])))


def coupled_comment(module):
    """TIA mirrors a module's Comment to its built-in submodule."""
    return lambda category, key: category == "changed" and key == module + "/items/" + module.rsplit("/", 1)[1]


def port_defaults(ports):
    """TIA sets (connect) or clears (disconnect) the cable defaults of both connected ports."""
    return lambda category, key: category == "changed" and key in ports


def add_cpu(run, label, name, cpu=CPU, restore=False):
    payload = {"typeIdentifier": cpu, "deviceName": name, "itemName": name + "Cpu", "configureScratchSecurity": True}
    # The second effect is the worker's description of configureScratchSecurity.
    run.apply(label, "hardware_add", payload, {"effects": [{"createDevice": name, "typeIdentifier": cpu}, {}]},
              added=under(run.path(name)), rows={run.hw(name, "items", name + "Cpu"): {"typeIdentifier": cpu, "name": name + "Cpu"}}, restore=restore)


def constant(run, station, name):
    """The system constant of one hardware object in the station's PLC default tag table."""
    return "member:" + run.path(station.plc_device, "items", station.plc, "plc", station.plc, "tags", "Default%20tag%20table", "systemConstants", name)


def timing(run, station):
    """An IO device's interface recomputes its connector's update and watchdog times when its first module arrives or its last one goes;
    the ET 200SP family modules join and leave beside SpDi3, its first DI module, which stays."""
    return ["hw:" + run.path(station.device, "items", *station.interface)] if station.interface and station.device != SP.device else []


def plug(run, name, slot, station=SCRATCH, module=MODULE, changed=()):
    rail = run.path(station.device, "items", station.rack)
    item = run.hw(station.device, "items", name)
    run.apply("plug " + name, "hardware_plug", {"hardwarePath": rail, "typeIdentifier": module, "name": name, "positionNumber": slot},
              {"effects": [{"parentPath": rail, "createItem": name, "positionNumber": slot, "typeIdentifier": module}]},
              added=under(item[3:], constant(run, station, station.prefix + name)), changed=changed, rows={item: {"typeIdentifier": module, "positionNumber": slot}})


def node(run, device, interface, address=None, deferred=False):
    """One more interface on HwNet, with its own address when given; device_inventory shows the address on the device, its head and the
    interface rows."""
    path = run.path(device, "items", *interface)
    name = run.current["view"]["hw:" + path]["network"]["nodes"][0]["name"]  # "X1" on S7-1500, "X1 : PN(LAN)" on S7-1200, "IE1" on IM 155-5.
    rows = [run.path(device), run.path(device, "items", interface[0]), path] if address else []
    extra = {"attributes": {"Address": address}} if address else {}
    run.apply("put %s on HwNet" % device, "network_configure", {"hardwarePath": path, "nodeName": name, "subnetName": "HwNet", **extra},
              changed=["hw:" + path] + ["inv:" + row for row in rows], deferred=deferred)


def station(run):
    """The ET 200MP head is added and put on HwNet without compile, then assigned to a new IO system of HwPeerCpu with compile."""
    interface = MP.interface
    run.apply("add ET 200MP station " + MP.device, "hardware_add", {"typeIdentifier": IM, "deviceName": MP.device, "itemName": "HwMpHead"},
              {"effects": [{"createDevice": MP.device, "typeIdentifier": IM}]}, added=under(run.path(MP.device)),
              rows={run.hw(MP.device, "items", "HwMpHead"): {"typeIdentifier": IM}}, deferred=True)
    node(run, MP.device, interface, "192.168.0.4", deferred=True)
    device, controller = run.path(MP.device, "items", *interface), run.path("HwPeer", "items", "HwPeerCpu", "items", "PROFINET%20interface_1")
    # HwPeerCpu gets system constants for the IO system (Local~HwPeerIo) and the station's head, interface and ports (HwMpHead~...).
    generated = lambda applied, state: [key for key in state if key.startswith((constant(run, MP, MP.prefix), constant(run, MP, "Local~" + IO_SYSTEM)))]
    # Every item of the head reads SharedDeviceAccessType PLC instead of Unknown, and the interface gets its IO-device attributes.
    head = [run.hw(MP.device, "items", *parts) for parts in (("HwMpHead",), ("HwMpHead", "items", "HwMpHead"))] + \
           ["hw:" + device] + ["hw:" + device + "/items/Port_%d" % number for number in (1, 2)]
    payload = {"hardwarePath": device, "connectorIndex": 0, "controllerHardwarePath": controller, "controllerIndex": 0, "createIoSystemName": IO_SYSTEM}
    # The worker records the controller's owning CPU item in the reviewed payload.
    run.apply("assign %s to HwPeerCpu" % MP.device, "network_io_assign", payload,
              {"effects": [{"hardwarePath": device, "connectorIndex": 0, "beforeIoSystem": None, "afterIoSystem": IO_SYSTEM, "disconnect": False}]},
              shown={**payload, "addressControllerOwnerPath": run.path("HwPeer", "items", "HwPeerCpu")}, changed=head + ["hw:" + controller], added=generated,
              rows={key: {"attributes": {"SharedDeviceAccessType": "PLC"}} for key in head})


def rack(run, device, head):
    """A station's one root rack: the item below the device at position 0 classified None, beside its head (remote-I/O runner rule)."""
    prefix = run.hw(device, "items", "")
    racks = [key[len(prefix):] for key, row in run.current["view"].items() if key.startswith(prefix) and "/" not in key[len(prefix):]
             and key[len(prefix):] != head and row["positionNumber"] == 0 and row["classification"] == "None"]
    require(len(racks) == 1, "Expected one root rack of %s: %s" % (device, racks))
    return racks[0]


def hw_under(run, path, state):
    """The hardware keys at or below one exact path (a module and its submodule)."""
    return [key for key in under(path)(None, state) if key.startswith("hw:")]


def head_items(run, device, head):
    """Every hardware key of an IO device's head subtree: the assignment sets SharedDeviceAccessType PLC on each."""
    return lambda applied, state: [key for key in under(run.path(device, "items", head))(applied, state) if key.startswith("hw:")]


def sp_station(run, station=SP, io_system=None):
    """The ET 200SP head joins the IO system of its station's PLC (HwPeerIo, or io_system, which the assignment creates) with its
    declared generated server module, which then moves to slot 4 to free the DI slots, as in the remote-I/O runner. Returns the
    station with its rack."""
    SP = station  # The station's device, head and interface names; its PLC owns the controller.
    run.apply("add ET 200SP station " + SP.device, "hardware_add", {"typeIdentifier": IM_SP, "deviceName": SP.device, "itemName": "HwSpHead"},
              {"effects": [{"createDevice": SP.device, "typeIdentifier": IM_SP}]}, added=under(run.path(SP.device)),
              rows={run.hw(SP.device, "items", "HwSpHead"): {"typeIdentifier": IM_SP}}, deferred=True)
    node(run, SP.device, SP.interface, "192.168.0.5", deferred=True)
    station_ = SP._replace(rack=rack(run, SP.device, "HwSpHead"))
    owner = run.path(SP.plc_device, "items", SP.plc)
    device, controller = run.path(SP.device, "items", *SP.interface), owner + "/items/PROFINET%20interface_1"
    view = run.current["view"]
    name = SERVER_MODULE_NAMES.get(view["hw:" + device]["name"])
    require(name, "No captured server-module name for the interface name %r" % view["hw:" + device]["name"])
    rail, server = run.path(SP.device, "items", station_.rack), run.path(SP.device, "items", quote(name, safe=""))
    author = view[run.hw(SP.device, "items", "HwSpHead")]["attributes"]["Author"]
    payload = {"hardwarePath": device, "connectorIndex": 0, "controllerHardwarePath": controller, "controllerIndex": 0, **({"createIoSystemName": io_system} if io_system else {}),
               "expectedServerModule": {"name": name, "typeIdentifier": SERVER, "containerHardwarePath": rail, "positionNumber": 1,
                                        "attributes": {"AdditionalInformation": "", "Author": author, "Comment": "", "LocationIdentifier": "", "Name": name, "PlantDesignation": ""}}}
    # A created IO system adds its system constant (Local~<name>) to the PLC's default tag table, as the ET 200MP assignment does.
    prefixes = (constant(run, SP, SP.prefix),) + ((constant(run, SP, "Local~" + io_system),) if io_system else ())
    generated = lambda applied, state: [key for key in state if key.startswith(prefixes)] + under(server)(applied, state)
    # The ports of the head's interface sit on the BusAdapter beside it (observed hw2); they take SharedDeviceAccessType PLC as well.
    adapter = [key for key in run.current["view"] if key.startswith(run.hw(SP.device, "items", "")) and "/items/Port_" in key and "/HwSpHead/" not in key]
    # The second effect is the declared generated server contract.
    run.apply("assign %s to %s" % (SP.device, SP.plc), "network_io_assign", payload,
              {"effects": [{"hardwarePath": device, "connectorIndex": 0, "beforeIoSystem": None, "afterIoSystem": io_system or IO_SYSTEM, "disconnect": False, "sharedDevice": False}, {}]},
              shown={**payload, "addressControllerOwnerPath": owner}, plc=owner + "/plc/" + SP.plc,
              changed=lambda applied, state: head_items(run, SP.device, "HwSpHead")(applied, state) + adapter + ["hw:" + controller], added=generated)
    run.apply("move %s's server module to slot 4" % SP.device, "hardware_move", {"hardwarePath": server, "positionNumber": 4},
              {"effects": [{"hardwarePath": server, "positionBefore": 1, "positionAfter": 4}]}, changed=["hw:" + server, "inv:" + server],
              rows={"hw:" + server: {"positionNumber": 4}})
    return station_


def sp_modules(run, station_):
    """The two newest DI 8x24VDC ST catalog entries that plug at slot 2 of the ET 200SP rack (prepare only): the older one is plugged,
    the newer one replaces it."""
    def select():
        rail, fitting = run.path(station_.device, "items", station_.rack), []
        entries = run.h.tool("hardware_catalog_search", {"filter": SP_DI[0], "maxItems": 500})["entries"]
        # The exact type name keeps BA variants (DI 8x24VDC BA) out.
        for entry in sorted((e for e in entries if (e.get("articleNumber") or "").startswith(SP_DI[1]) and e.get("typeName") == SP_DI[0]), key=lambda e: e["typeIdentifier"], reverse=True):
            payload = {"hardwarePath": rail, "typeIdentifier": entry["typeIdentifier"], "name": "SpProbe", "positionNumber": 2}
            result = run.h.client.request("tools/call", {"name": "change_prepare", "arguments": run.args("hardware_plug", payload)})
            if not result.get("isError"):
                fitting.append(entry["typeIdentifier"])
            if len(fitting) == 2:
                return fitting
        require(False, "Fewer than two DI 8x24VDC ST catalog entries plug into the ET 200SP rack: %s" % fitting)
    newer, older = run.h.stage("select ET 200SP DI modules", select)
    run.h.report.setdefault("selected", {})["et200spDi"] = {"plugged": older, "replacement": newer}
    return older, newer


def shared_device(run, restore=False):
    """The ET 200SP interface's connector 1 joins a new IO system of the scratch CPU while connector 0 stays with HwPeerCpu, and SpDi3
    is assigned to the scratch CPU in the same change: V21 compiles a further controller only with a submodule of its own (hw3).
    True when applied."""
    device, controller = run.path(SP.device, "items", *SP.interface), run.path("ScratchStation", "items", "ScratchPLC", "items", "PROFINET%20interface_1")
    payload = {"hardwarePath": device, "connectorIndex": 1, "controllerHardwarePath": controller, "controllerIndex": 0, "createIoSystemName": SHARED_IO, "sharedDevice": True,
               "sharedSubmodules": [run.path(SP.device, "items", "SpDi3")]}
    generated = lambda applied, state: [key for key in state if key.startswith((constant(run, SCRATCH, SP.prefix), constant(run, SCRATCH, "Local~" + SHARED_IO)))]
    submodule = run.path(SP.device, "items", "SpDi3", "items", "SpDi3")
    return run.apply("share %s with ScratchPLC%s" % (SP.device, ", restored" if restore else ""), "network_io_assign", payload,
                     {"effects": [{"hardwarePath": device, "connectorIndex": 1, "beforeIoSystem": None, "afterIoSystem": SHARED_IO, "disconnect": False, "sharedDevice": True,
                                   "sharedSubmodules": [{"hardwarePath": payload["sharedSubmodules"][0], "assignmentTargetBefore": run.path("HwPeer", "items", "HwPeerCpu"),
                                                         "assignmentTargetAfter": run.path("ScratchStation", "items", "ScratchPLC")}]}]},
                     shown={**payload, "addressControllerOwnerPath": run.path("ScratchStation", "items", "ScratchPLC"), "sharedAddressControllerOwnerPaths": [run.path("HwPeer", "items", "HwPeerCpu")]},
                     # SpDi3's submodule takes the scratch CPU's address space and its HwPeerCpu system constant goes (hw5). Its start moves
                     # only when that address is taken there (hw4, hw6); with the scratch CPU's space empty it stays (hw7).
                     plc=scratch_plc(run), changed=["hw:" + device, "hw:" + controller], removed=[constant(run, SP, SP.prefix + "SpDi3")],
                     tolerated=lambda category, key: category == "changed" and key == "hw:" + submodule,
                     added=generated, restore=restore, boundary=True) is not None


def shared_access(run, name, access, shared, external=None, target=None, restore=False, station=SP):
    """One ET 200SP module's shared-device access: OutsideOfProject, or PLC with the PLC item target. The worker records the PLCs of the
    station's IO systems (its own PLC, HwPeerCpu, and ScratchPLC while the station is shared) as its address owners. external also sets
    NumberOfExternalIoControllers of the station's interface, which TIA may require once a module is accessed outside the project.
    True when applied."""
    SP = station  # The station's device and interface names; its PLC owns the first IO system.
    module, interface, own = run.path(SP.device, "items", name), run.path(SP.device, "items", *SP.interface), run.path(SP.plc_device, "items", SP.plc)
    payload = {"hardwarePath": module, "accessType": access, **({"assignmentTargetPath": target} if target else {}), **({"externalIoControllers": external} if external else {})}
    owners = sorted([own] + ([run.path("ScratchStation", "items", "ScratchPLC")] if shared else []))
    shown = {**payload, "addressControllerOwnerPaths": owners, **({"ioDeviceInterfacePath": interface} if external else {})}
    return run.apply("set %s's shared-device access %s%s" % (name, access, " with %d external controller(s)" % external if external else ""), "hardware_shared_device_access", payload,
                     {"effects": [{"hardwarePath": module, "accessAfter": access}]}, shown=shown,
                     changed=lambda applied, state: hw_under(run, module, state) + (["hw:" + interface] if external else []), rows={"hw:" + module: {"attributes": {"SharedDeviceAccessType": access}}},
                     removed=[constant(run, SP, SP.prefix + name)] if access == "OutsideOfProject" else (),
                     added=[constant(run, SP, SP.prefix + name)] if access == "PLC" and target == own else (),
                     restore=restore, boundary=True) is not None


def scratch_plc(run):
    return run.path("ScratchStation", "items", "ScratchPLC", "plc", "ScratchPLC")


def interfaces(run):
    """The PROFINET interface of each ring member, scratch CPU first: the MRP manager and its two clients."""
    return [run.path(device, "items", cpu, "items", interface) for device, cpu, interface in (("ScratchStation", "ScratchPLC", "PROFINET%20interface_1"),
            ("HwPeer", "HwPeerCpu", "PROFINET%20interface_1"), (MP.device, "HwMpHead", "PROFINET%20interface"))]


def ring_ports(run):
    """The IM 155-5 PN ST's free Port_2 and the scratch CPU's Port_1, freed from the CPU 1214C: linking them closes the ring."""
    return ports(run, 2, ((MP.device, "HwMpHead"),), "PROFINET%20interface")[0], ports(run, 1)[0]


def mrp(run, interface, role, domain=None, create=False, restore=False):
    """One interface's MRP role (and domain); True when applied."""
    payload = {"hardwarePath": interface, "role": role, **({"domainName": domain} if domain else {}), **({"createDomain": True} if create else {})}
    effect = {"hardwarePath": interface, "roleAfter": role, "createDomain": create, **({"domainAfter": domain} if domain else {})}
    label = "MRP %s %s%s%s" % (interface.split("/devices/", 1)[1].split("/", 1)[0], role, " in " + domain if domain else "", ", restored" if restore else "")
    # A ring role declares RingPort true on the interface's ports and NotInRing withdraws it (hw4): a recorded side effect.
    ring_port = lambda category, key: category == "changed" and key.startswith("hw:" + interface + "/items/Port_")
    return run.apply(label, "network_mrp_set", payload, {"effects": [effect]}, changed=["hw:" + interface], tolerated=ring_port,
                     rows={"hw:" + interface: {"network": {"mrp": [{"role": role, **({"domain": domain} if domain else {})}]}}}, restore=restore, boundary=True) is not None


def ring(run):
    """V21 compiles an MRP role only while every link of the interface's ports goes to another ring port ("Ring port ... is connected
    to port ..., which is not a ring port", discovery hw3), so the line opens first: the CPU 1214C (one port) leaves it and the two links
    between the members go. The scratch CPU becomes manager in a new MRP domain HwRing (the subnet's default domain if V21 refuses it),
    HwPeerCpu and the IM 155-5 PN ST clients (the first client's join restored once), the two segments are linked again, and the scratch
    CPU's Port_1 and the IM's Port_2 close the ring (restored once, then for real). Without all roles the segments are relinked as a
    plain line."""
    segments = (("scratch-peer", tuple(ports(run, 2))), ("peer-HwMp", family_ports(run)["HwMp"]))
    link(run, "hardware_port_disconnect", 1, pair=family_ports(run)["Hw1200"], label="Hw1200 Port_1, which cannot join the ring")
    for name, pair in segments:
        link(run, "hardware_port_disconnect", 2, pair=pair, label=name + " link, opened for the ring roles")
    manager, *clients = interfaces(run)
    # The worker refuses a ring role on an interface with fewer than two ports (a head whose ports sit on a BusAdapter would show it).
    run.h.report["ringMemberPorts"] = {path: len(((run.current["view"].get("hw:" + path) or {}).get("network") or {}).get("ports") or []) for path in (manager, *clients)}
    domain = RING if mrp(run, manager, "Manager", RING, create=True) else None
    applied = [manager] if domain is not None or mrp(run, manager, "Manager") else []
    if applied:
        run.b0 = run.current
        mrp(run, clients[0], "Client", domain, restore=True)
        applied += [interface for interface in clients if mrp(run, interface, "Client", domain)]
    roles = len(applied) == 1 + len(clients)
    if not roles:
        # A member with a ring role cannot link to one without; the line returns without media redundancy.
        for interface in applied:
            mrp(run, interface, "NotInRing")
        run.h.report.setdefault("boundaries", {})["ring"] = {"stage": "skipped", "error": "Not every member took its MRP role; the ring is not closed."}
    for name, pair in segments:
        link(run, "hardware_port_connect", 2, pair=pair, label=name + " link" + (" between ring members" if roles else ", relinked"))
    if not roles:
        return False
    run.b0 = run.current
    label = "the ring (HwMp Port_2 and ScratchPLC Port_1)"
    link(run, "hardware_port_connect", 2, pair=ring_ports(run), label=label + ", restored", restore=True)
    link(run, "hardware_port_connect", 2, pair=ring_ports(run), label=label)
    return True


def topology_refusals(run):
    sp, scratch = run.path(SP.device, "items", *SP.interface), run.path("ScratchStation", "items", "ScratchPLC", "items", "PROFINET%20interface_1")
    peer, module = run.path("HwPeer", "items", "HwPeerCpu", "items", "PROFINET%20interface_1"), run.path("ScratchStation", "items", "HwDi")
    second = {"hardwarePath": sp, "connectorIndex": 2, "controllerHardwarePath": scratch, "controllerIndex": 0, "createIoSystemName": "HwOtherIo"}
    run.refuse("network_io_assign", "a further controller without sharedDevice", second, "UnsupportedCapability", plcPath=scratch_plc(run))
    run.refuse("network_io_assign", "a shared device with the controller it already has",
               {"hardwarePath": sp, "connectorIndex": 2, "controllerHardwarePath": peer, "controllerIndex": 0, "sharedDevice": True}, "InvalidRequest")
    run.refuse("network_mrp_set", "an unknown role", {"hardwarePath": scratch, "role": "Master"}, "InvalidRequest")
    run.refuse("network_mrp_set", "a module without a network interface", {"hardwarePath": module, "role": "Client"}, "UnsupportedCapability")
    run.refuse("network_mrp_set", "an absent domain without createDomain", {"hardwarePath": peer, "role": "Client", "domainName": "HwNoRing"}, "ObjectNotFound")
    run.refuse("network_mrp_set", "a ring role on the single-port CPU 1214C", {"hardwarePath": run.path("Hw1200", "items", "Hw1200Cpu", "items", "PROFINET%20interface_1"), "role": "Client"},
               "UnsupportedCapability")
    run.refuse("hardware_shared_device_access", "the native unassigned access", {"hardwarePath": run.path(SP.device, "items", "SpDi3"), "accessType": "Unknown"}, "InvalidRequest")
    run.refuse("hardware_shared_device_access", "a module of a CPU station", {"hardwarePath": module, "accessType": "OutsideOfProject"}, "UnsupportedCapability")


def address_change(run, moved=False):
    """A tag on HwDi's first input bit, then HwDi replaced by a catalog-selected DI 32x24VDC HF with acceptAddressChange: refused until
    the tag is acknowledged, then applied and restored. The new module's input range is four bytes instead of two, so the tagged
    range changes whether TIA keeps or moves its start; moved requires the start to move (HwDi2 holds the bytes after HwDi's)."""
    module, view = run.path("ScratchStation", "items", "HwDi"), run.current["view"]
    start = min(a["startAddress"] for key, row in view.items() if key == "hw:" + module or key.startswith("hw:" + module + "/")
                for a in row["addresses"] or [] if a["ioType"] == "Input" and a.get("length", 0) > 0)
    table = scratch_plc(run) + "/tags/Default%20tag%20table"
    tag = table + "/tags/HwInput"
    run.apply("tag HwInput on HwDi's input", "tag_create", {"tagTableName": "Default tag table", "name": "HwInput", "dataTypeName": "Bool", "logicalAddress": "%%I%d.0" % start},
              plc=scratch_plc(run), added=["member:" + tag], changed=["tagTable:" + table])
    entries = run.h.stage("select DI 32x24VDC HF", lambda: run.h.tool("hardware_catalog_search", {"filter": WIDE_DI[0], "maxItems": 500}))["entries"]
    wide = max((e["typeIdentifier"] for e in entries if (e.get("articleNumber") or "").startswith(WIDE_DI[1]) and e.get("typeName") == WIDE_DI[0]), default=None)
    require(wide, "No DI 32x24VDC HF in the installed catalog")
    run.h.report.setdefault("selected", {})["addressChange"] = wide
    payload = {"hardwarePath": module, "typeIdentifier": wide, "acceptAddressChange": True}
    error = run.refuse("hardware_replace", "an address change on a tagged module without acknowledgement", payload, "AddressReferenced")
    run.expect([row["path"] for row in error.get("details", {}).get("referencedTags", [])] == [tag], "address change", "Referenced tags differ: %s" % error.get("details"))
    def mapped(applied, state):
        rows = [row for row in applied["readbacks"] if row.get("role") == "hardware-replace-inventory"]
        require(len(rows) == 1 and any(entry["changed"] for entry in rows[0]["addressMap"]) and [t["path"] for t in rows[0]["changedReferencedTags"]] == [tag],
                "The replacement's address map or tag impact differs: %s" % rows)
        tagged = rows[0]["changedReferencedTags"][0]
        after = (tagged.get("rangeAfter") or {}).get("startAddress")
        run.h.report.setdefault("selected", {})["addressStart"] = {"before": tagged["startAddress"], "after": after}
        require(not moved or after not in (None, tagged["startAddress"]), "The replacement did not move the tagged range's start: %s" % tagged)
    run.b0 = run.current
    run.apply("replace HwDi with %s moving its address" % wide, "hardware_replace", {**payload, "acknowledgedTags": [tag]},
              {"effects": [{"hardwarePath": module, "typeIdentifierAfter": wide, "acceptAddressChange": True}], "addressImpact": {"referencedTags": [{"path": tag}]}},
              # The submodule's device_inventory row stays (hw3); the module's row and every hardware row change.
              changed=lambda applied, state: [key for key in under(module)(applied, state) if key in run.b0["view"] and (key.startswith("hw:") or key == "inv:" + module)],
              added=lambda applied, state: [key for key in under(module)(applied, state) if key not in run.b0["view"]], check=mapped, restore=True, boundary=True)


def gsd_entries(run):
    """The installed catalog entries of the project's own I-device GSD (designation GSD_DESIGNATION, a GSD: type), recorded as gsdCatalog."""
    def search():
        rows = [e for e in run.h.tool("hardware_catalog_search", {"filter": GSD_DESIGNATION, "maxItems": 500})["entries"] if (e.get("typeIdentifier") or "").startswith("GSD:")]
        run.h.report["gsdCatalog"] = rows
        return {"entries": len(rows)}
    run.h.stage("search the catalog for the installed I-device GSD", search)
    return run.h.report["gsdCatalog"]


def gsd_device(run, rows=None, restore=False):
    """The project's own exported I-device GSD, once an operator installed it in TIA: added as an IO device, put on HwNet and assigned
    to HwPeerIo with compile. V21 Openness cannot install a GSD file, so without it the run records gsdNotInstalled, which is not a
    failure. rows: the catalog entries already searched. restore: the assignment, which compiles and saves the deferred add and
    subnet change, is restored once before it applies for good."""
    rows = gsd_entries(run) if rows is None else rows
    if not rows:
        run.h.report["gsdNotInstalled"] = True
        return
    run.expect(len(rows) == 1, "gsd", "Expected one installed I-device GSD entry: %s" % rows)
    identifier, device, head = rows[0]["typeIdentifier"], "HwGsd", "HwGsdHead"
    run.apply("add GSD device " + device, "hardware_add", {"typeIdentifier": identifier, "deviceName": device, "itemName": head},
              {"effects": [{"createDevice": device, "typeIdentifier": identifier}]}, added=under(run.path(device)), deferred=True)
    prefix = run.path(device, "items", "")
    interface = next(key[3 + len(prefix):] for key, row in sorted(run.current["view"].items()) if key.startswith("hw:" + prefix) and (row.get("network") or {}).get("ioConnectors"))
    # ("HwGsdHead", "items", "Interface"). The I-device GSD reads IpProtocolSelection OtherPath (its own CPU project sets the address),
    # so its node has no Address attribute.
    node(run, device, tuple(interface.split("/")), deferred=True)
    path, controller = prefix + interface, run.path("HwPeer", "items", "HwPeerCpu", "items", "PROFINET%20interface_1")
    payload = {"hardwarePath": path, "connectorIndex": 0, "controllerHardwarePath": controller, "controllerIndex": 0}
    generated = lambda applied, state: [key for key in state if key.startswith(constant(run, MP, head + "~"))]
    # Unlike the ET 200MP head, the GSD's ports keep their state on assignment (discovery hg4).
    items = lambda applied, state: [key for key in head_items(run, device, head)(applied, state) if "/items/Port%20" not in key]
    def assign(restored):
        return run.apply("assign %s to HwPeerCpu%s" % (device, ", restored" if restored else ""), "network_io_assign", payload,
                         {"effects": [{"hardwarePath": path, "connectorIndex": 0, "beforeIoSystem": None, "afterIoSystem": IO_SYSTEM, "disconnect": False, "sharedDevice": False}]},
                         shown={**payload, "addressControllerOwnerPath": run.path("HwPeer", "items", "HwPeerCpu")},
                         changed=lambda applied, state: items(applied, state) + ["hw:" + controller], added=generated, restore=restored, boundary=True)
    if restore:
        run.b0 = run.current
        assign(True)
    assign(False)
    run.h.report["gsdDevice"] = {"typeIdentifier": identifier, "hardwarePath": path}


def network(run, scratch=True):
    """HwPeerCpu (readdressed; its change creates HwNet) and the scratch CPU on HwNet; scratch=False leaves the scratch CPU off it."""
    for device, cpu, extra in (("HwPeer", "HwPeerCpu", {"attributes": {"Address": "192.168.0.2"}}), ("ScratchStation", "ScratchPLC", {}))[:2 if scratch else 1]:
        interface = run.path(device, "items", cpu, "items", "PROFINET%20interface_1")
        # device_inventory reports the node address on the device, CPU and interface rows, so only the readdressed peer changes there.
        rows = [run.path(device), run.path(device, "items", cpu), interface] if extra else []
        run.apply("put %s on HwNet" % cpu, "network_configure", {"hardwarePath": interface, "nodeName": "X1", "subnetName": "HwNet", **extra},
                  changed=["hw:" + interface] + ["inv:" + row for row in rows])


def attributes(run, name, values, restore=False, station=SCRATCH):
    module = run.hw(station.device, "items", name)
    run.apply("set %s's %s" % (name, " and ".join(values)), "hardware_attributes", {"hardwarePath": module[3:], "attributes": values},
              {"effects": [{"hardwarePath": module[3:]}], "attributeChanges": [{"name": key, "after": value} for key, value in values.items()]},
              changed=[module], rows={module: {"attributes": values}}, tolerated=coupled_comment(module), restore=restore)


def replace(run, name, before, after, restore=False, station=SCRATCH):
    module = run.hw(station.device, "items", name)
    run.apply("replace %s with %s" % (name, after), "hardware_replace", {"hardwarePath": module[3:], "typeIdentifier": after},
              {"effects": [{"hardwarePath": module[3:], "typeIdentifierBefore": before, "typeIdentifierAfter": after, "acceptLoss": False}]},
              changed=[module, "inv:" + module[3:]], rows={module: {"typeIdentifier": after}}, restore=restore)


def ports(run, number, pairs=(("ScratchStation", "ScratchPLC"), ("HwPeer", "HwPeerCpu")), interface="PROFINET%20interface_1"):
    return [run.path(device, "items", cpu, "items", interface, "items", "Port_%d" % number) for device, cpu in pairs]


def family_ports(run):
    """Port_1 of the CPU 1214C to the scratch CPU's, and the IM 155-5's to HwPeerCpu's: with the CPUs' Port_2 link, a line."""
    return {"Hw1200": (ports(run, 1, (("Hw1200", "Hw1200Cpu"),))[0], ports(run, 1)[0]),
            MP.device: (ports(run, 1, ((MP.device, "HwMpHead"),), "PROFINET%20interface")[0], ports(run, 1)[1])}


def link(run, kind, number, restore=False, pair=None, label=None):
    local, partner = pair or ports(run, number)
    interfaces = ["hw:" + port.rsplit("/items/", 1)[0] for port in (local, partner)]
    connected = kind == "hardware_port_connect"
    run.apply("%s %s" % ("connect" if connected else "disconnect", label or "Port_%d" % number), kind, {"hardwarePath": local, "partnerHardwarePath": partner},
              {"effects": [{"hardwarePath": local, "partnerHardwarePath": partner, "action": "connect" if connected else "disconnect"}]},
              changed=interfaces, tolerated=port_defaults(["hw:" + local, "hw:" + partner]), check=lambda applied, state: linked(state, local, partner, connected),
              restore=restore)


def linked(state, local, partner, connected):
    """hardware_inspect lists each interface's ports with their configured partners."""
    for port, other in ((local, partner), (partner, local)):
        rows = state["hw:" + port.rsplit("/items/", 1)[0]]["network"]["ports"]
        row = next(r for r in rows if r["port"]["ownerHardwarePath"] == port)
        partners = [p.get("ownerHardwarePath") for p in row["connectedPorts"]]
        require(partners == ([other] if connected else []), "Port %s lists partners %s" % (port, partners))


def delete(run, name, restore=False, station=SCRATCH, changed=()):
    module = run.hw(station.device, "items", name)
    run.apply("delete " + name, "hardware_delete", {"hardwarePath": module[3:]},
              {"effects": [{"deleteHardwarePath": module[3:], "positionNumber": int(run.current["view"][module]["positionNumber"])}]},
              removed=under(module[3:], constant(run, station, station.prefix + name))(None, run.current["view"]), changed=changed, restore=restore)


def refusals(run):
    cpu, peer = run.path("ScratchStation", "items", "ScratchPLC"), run.path("HwPeer", "items", "HwPeerCpu")
    module = run.path("ScratchStation", "items", "HwDi")
    (p1, q1), (p2, _) = ports(run, 1), ports(run, 2)
    run.refuse("hardware_add", "a device name differing only in case", {"typeIdentifier": CPU, "deviceName": "hwpeer", "itemName": "X"}, "ObjectExists")
    run.refuse("hardware_add", "an identifier missing from the catalog", {"typeIdentifier": "OrderNumber:6ES7 999-9XX99-9XX9/V9.9", "deviceName": "HwBogus", "itemName": "X"}, "CatalogItemNotFound")
    run.refuse("hardware_attributes", "an attribute outside the allowlist", {"hardwarePath": module, "attributes": {"FirmwareVersion": "V2.0"}}, "UnsupportedAttribute")
    run.refuse("hardware_attributes", "an invented hardware path", {"hardwarePath": module + "X", "attributes": {"Comment": "x"}}, "HardwareNotFound")
    run.refuse("hardware_replace", "the module's current identifier", {"hardwarePath": module, "typeIdentifier": run.current["view"]["hw:" + module]["typeIdentifier"]}, "InvalidRequest")
    run.refuse("hardware_replace", "the CPU", {"hardwarePath": peer, "typeIdentifier": CPU.replace("V4.1", "V4.0")}, "SoftwareBearingHardware")
    run.refuse("hardware_replace", "an identifier missing from the catalog", {"hardwarePath": module, "typeIdentifier": "OrderNumber:6ES7 521-1BH00-0AB0/V9.9"}, "CatalogItemNotFound")
    run.refuse("hardware_port_connect", "a port with itself", {"hardwarePath": p1, "partnerHardwarePath": p1}, "InvalidRequest")
    run.refuse("hardware_port_connect", "a CPU instead of a port", {"hardwarePath": cpu, "partnerHardwarePath": q1}, "UnsupportedCapability")
    run.refuse("hardware_port_disconnect", "two unconnected ports", {"hardwarePath": p2, "partnerHardwarePath": q1}, "InvalidRequest")
    run.refuse("hardware_delete", "a CPU", {"hardwarePath": peer}, "SoftwareBearingHardware")
    run.refuse("hardware_delete", "a built-in port", {"hardwarePath": p1}, "BuiltInHardware")


def phase_a(run):
    add_cpu(run, "add CPU HwPeer", "HwPeer")
    # Generated CPU security is restricted to the worker's own new scratch, so hardware_add's restore runs here, not after the reopen.
    run.b0 = run.current
    add_cpu(run, "add CPU HwOther", "HwOther", restore=True)
    network(run)
    add_cpu(run, "add CPU Hw1200", "Hw1200", S7_1200)
    node(run, "Hw1200", ("Hw1200Cpu", "items", "PROFINET%20interface_1"), "192.168.0.3")
    station(run)
    sp = sp_station(run)
    older, newer = sp_modules(run, sp)
    run.families = FAMILIES + ((sp, ("SpDi", "SpDi2"), older, newer),)
    # The first ET 200SP module needs a new potential group (light BaseUnit), which TIA gives a module at slot 1 (hw2), and it
    # sets the IO device's update time; SpDi3 holds that slot, so the family modules at 2 and 3 continue its potential group.
    plug(run, "SpDi3", 1, sp, older, changed=["hw:" + run.path(SP.device, "items", *SP.interface)])
    for station_, (first, second), module, after in run.families:
        plug(run, first, 2, station_, module, changed=timing(run, station_))
        plug(run, second, 3, station_, module)
        attributes(run, first, {"Comment": "HwComment", "Author": "HwAuthor"}, station=station_)
        replace(run, first, module, after, station=station_)
    plug(run, "HwDi", 2)
    module = run.hw("ScratchStation", "items", "HwDi")
    stale = run.prepare("comment HwDi (made stale below)", "hardware_attributes", {"hardwarePath": module[3:], "attributes": {"Comment": "HwStale"}})
    run.refuse_apply("hardware_attributes", "a wrong previewHash", {**stale, "previewHash": "0" * 64}, "PreviewMismatch")
    plug(run, "HwDi2", 3)
    run.refuse_apply("hardware_attributes", "a preview made stale by a later change", stale, "StaleRevision")
    refusals(run)
    attributes(run, "HwDi", {"Comment": "HwComment", "Author": "HwAuthor"})
    replace(run, "HwDi", MODULE, MODULE_V2)
    link(run, "hardware_port_connect", 1)
    run.refuse("hardware_port_connect", "an already connected port", {"hardwarePath": ports(run, 1)[0], "partnerHardwarePath": ports(run, 1)[1]}, "InvalidRequest")
    link(run, "hardware_port_disconnect", 1)
    # A second link between the two CPUs would form a ring, which fails compile without media redundancy, so the connect
    # restore case runs here, while no link exists.
    run.b0 = run.current
    link(run, "hardware_port_connect", 1, restore=True)
    link(run, "hardware_port_connect", 2)
    for device, pair in family_ports(run).items():
        link(run, "hardware_port_connect", 1, pair=pair, label=device + " Port_1")
    # S7-1500 racks refuse gaps at compile, so only the last module is deleted.
    delete(run, "HwDi2")
    for station_, (_, second), _, _ in run.families:
        delete(run, second, station=station_)
    # SpDi3 becomes the scratch CPU's submodule of the shared device; SpDi4 (slot 3, continuing the potential group) goes outside the project.
    plug(run, "SpDi4", 3, sp, older)
    run.shared = shared_device(run)
    # Without an external IO controller TIA warns "The number of IO controllers outside the project ... is 0" (hw4), so one is configured.
    run.access = shared_access(run, "SpDi4", "OutsideOfProject", run.shared, external=1) or shared_access(run, "SpDi4", "OutsideOfProject", run.shared)
    topology_refusals(run)
    run.ring = ring(run)
    gsd_device(run)


def reopen(run):
    h, final = run.h, run.current
    h.stage("close the changed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen the saved project", lambda: h.reopen_project(run.project))
    run.current = run.b0 = run.snap("reopened")
    h.stage("reopened state equals every verified change",
            lambda: require(run.b0["view"] == final["view"], "Reopened state differs: %s" % diff(final["view"], run.b0["view"])) or len(final["view"]))
    for kind, rows in list(run.coverage.items()):
        if rows.get("applied"):
            run.mark(kind, "persisted", "fresh Portal reopen")


def phase_b(run):
    attributes(run, "HwDi", {"Comment": "HwAgain"}, restore=True)
    replace(run, "HwDi", MODULE_V2, MODULE, restore=True)
    link(run, "hardware_port_disconnect", 2, restore=True)
    delete(run, "HwDi", restore=True)
    pairs = {device: pair for device, pair in family_ports(run).items() if device != "Hw1200"}  # The CPU 1214C left the line for the ring.
    for station_, (first, _), module, after in run.families:
        attributes(run, first, {"Comment": "HwAgain"}, restore=True, station=station_)
        replace(run, first, after, module, restore=True, station=station_)
        if station_.device in pairs:
            link(run, "hardware_port_disconnect", 1, restore=True, pair=pairs[station_.device], label=station_.device + " Port_1")
        # With SpDi3 shared to the scratch CPU and SpDi4 outside the project, SpDi is HwPeerCpu's last ET 200SP module: its connector's
        # update time returns to 128 ms (hw5).
        last = station_.device == SP.device and run.shared and run.access
        delete(run, first, restore=True, station=station_, changed=["hw:" + run.path(SP.device, "items", *SP.interface)] if last else timing(run, station_))
    if run.ring:
        mrp(run, interfaces(run)[0], "ManagerAuto", restore=True)
    if run.access:
        shared_access(run, "SpDi4", "PLC", run.shared, target=run.path("HwPeer", "items", "HwPeerCpu"), restore=True)
    # Last: its tag stays in the project, so the pre-change state of the replacement includes it.
    address_change(run)


def profiles(run, only=None):
    """Module capability profiles of every type the run's snapshots showed (only: of these type identifiers): its catalog entry and the
    CanPlugNew outcome at slots 1-4 of one rack per rack type in the final configuration (prepare only, nothing applied), written to
    profiles.json."""
    def capture():
        racks = {}
        for path, item in run.raw.items():
            rest = path[len(run.base):].split("/items/")
            if len(rest) == 2 and "/" not in rest[1] and item["positionNumber"] == 0 and item["classification"] == "None":
                racks.setdefault(item.get("typeIdentifier") or item["name"], path)
        occupied = {(path[len(run.base):].split("/items/")[0], run.raw[path]["positionNumber"]): run.raw[path]["typeIdentifier"] for path in run.modules()}
        rows = []
        for identifier, captured in sorted(item for item in run.profiled.items() if only is None or item[0] in only):
            article = identifier.split(":", 1)[-1].split("/", 1)[0]
            entry = next((e for e in run.h.tool("hardware_catalog_search", {"filter": article, "maxItems": 500})["entries"] if e["typeIdentifier"] == identifier), None)
            rules = []
            for rack_type, rack_path in sorted(racks.items()) if captured["kind"] == "module" else ():
                for slot in range(1, 5):
                    payload = {"hardwarePath": rack_path, "typeIdentifier": identifier, "name": "ProfileProbe", "positionNumber": slot}
                    result = run.h.client.request("tools/call", {"name": "change_prepare", "arguments": run.args("hardware_plug", payload)})
                    code = ((result.get("structuredContent") or {}).get("error") or {}).get("code") if result.get("isError") else None
                    run.expect(code in (None, "IncompatibleHardware"), "profiles", "Plug probe of %s at %s/%d: %s" % (identifier, rack_path, slot, code))
                    rules.append({"rack": rack_type, "positionNumber": slot, "canPlugNew": code is None,
                                  "occupiedBy": occupied.get((rack_path[len(run.base):].split("/items/")[0], slot))})
            if not identifier.startswith("OrderNumber:"):
                # A versionless type's profile key is the firmware the worker compares: the item's own FirmwareVersion attribute value.
                if not captured["firmware"]:
                    run.h.report.setdefault("profilesSkipped", []).append(identifier)
                    continue
                entry = {**(entry or {}), "version": captured["firmware"]}
            rows.append(hardware_profiles.profile(identifier, captured["kind"], captured["items"], entry, rules, run.h.directory.name + "/qualification.json"))
        document = hardware_profiles.contract(rows)
        (run.h.directory / "profiles.json").write_text(json.dumps(document, indent=2), encoding="utf-8")
        run.h.report["profiles"] = {"path": str(run.h.directory / "profiles.json"), "typeIdentifiers": [row["typeIdentifier"] for row in rows]}
        return {"profiles": len(rows), "racks": sorted(racks)}
    run.h.stage("capture module capability profiles", capture)


def profile_gates(run, identifier):
    """With the bundled capability profiles: hardware_catalog_search names the profile of SpDi3's type, and a write that profile does
    not declare (InformationText on the module) is refused by the profile, before any native check."""
    def search():
        entries = run.h.tool("hardware_catalog_search", {"filter": identifier.split(":", 1)[1].split("/", 1)[0], "maxItems": 500})["entries"]
        row = next((e for e in entries if e["typeIdentifier"] == identifier), None)
        require(row and row.get("profile") == PROFILE_LINK + identifier, "hardware_catalog_search does not name the profile of %s: %s" % (identifier, row))
        return {"profile": row["profile"], "unprofiled": sum(1 for e in entries if e.get("profile") is None)}
    run.h.stage("hardware_catalog_search names the capability profile of " + identifier, search)
    error = run.refuse("hardware_attributes", "a write the module's capability profile does not declare",
                       {"hardwarePath": run.path(SP.device, "items", "SpDi3"), "attributes": {"InformationText": "HwText"}}, "AttributeNotWritable")
    run.expect("capability profile of " + identifier in (error.get("message") or ""), "profiles", "The refusal does not come from the capability profile: %s" % error.get("message"))


def restore_slice(run):
    """--slice restores: the ET 200MP and ET 200SP stations on the second CPU with SpDi3, SpDi and SpDi4, the two profile gates, then a
    checkpoint restore of each change local.hardware_changes.topology.20261101 applied without one: the shared-device assignment, SpDi4
    OutsideOfProject, the createDomain manager and the IM 155-5 PN ST client (the members' ports stay unlinked)."""
    add_cpu(run, "add CPU HwPeer", "HwPeer")
    network(run)
    station(run)
    sp = sp_station(run)
    older, _ = sp_modules(run, sp)
    plug(run, "SpDi3", 1, sp, older, changed=["hw:" + run.path(SP.device, "items", *SP.interface)])
    plug(run, "SpDi", 2, sp, older)
    plug(run, "SpDi4", 3, sp, older)
    profile_gates(run, older)
    run.b0 = run.current
    shared_device(run, restore=True)
    shared_device(run)
    run.b0 = run.current
    shared_access(run, "SpDi4", "OutsideOfProject", True, external=1, restore=True)
    manager, _, head = interfaces(run)
    run.b0 = run.current
    mrp(run, manager, "Manager", RING, create=True, restore=True)
    mrp(run, manager, "Manager", RING, create=True)
    run.b0 = run.current
    mrp(run, head, "Client", RING, restore=True)
    missing = [kind for kind in ("network_io_assign", "hardware_shared_device_access", "network_mrp_set") if not run.coverage.get(kind, {}).get("restored")]
    run.expect(not missing and run.coverage.get("hardware_attributes", {}).get("refused"), "coverage", "Slice kinds without a restore or the profile refusal: %s" % missing)


def gsd_slice(run):
    """--slice gsd: the GSD catalog search first; with an installed I-device GSD, only what gsd_device needs (the added CPU HwPeer on
    HwNet and its IO system HwPeerIo, which the ET 200MP assignment creates), then the GSD device added and put on HwNet without
    compile and its assignment, restored once and applied again. It qualifies only with exactly one installed GSD entry; without
    one it records gsdNotInstalled and fails at once."""
    rows = gsd_entries(run)
    if not rows:
        run.h.report["gsdNotInstalled"] = True
    # Before any mutation: gsd_device would find a second entry only after the prerequisites.
    run.expect(len(rows) == 1, "gsd", "The GSD slice needs exactly one installed I-device GSD entry (gsdNotInstalled: %s): %s" % (not rows, rows))
    if rows:
        add_cpu(run, "add CPU HwPeer", "HwPeer")
        network(run, scratch=False)
        station(run)
        gsd_device(run, rows, restore=True)
        run.expect(bool(run.coverage.get("network_io_assign", {}).get("restored")) and "gsdDevice" in run.h.report, "gsd",
                   "The GSD slice qualifies only when the GSD device's assignment applied and restored.")


def message(run, label, error, *parts):
    """The refusal's message names every part (the rule that refused, not another check with the same code)."""
    text = error.get("message") or ""
    run.expect(all(part in text for part in parts), label, "The refusal message lacks %s: %s" % ([part for part in parts if part not in text], text))


def channel_gate(run, identifier):
    """A channel write SpDi3's capability profile declares read-only (InputDelay of a DI 8x24VDC ST channel) is refused by the profile."""
    channel = {"hardwarePath": run.path(SP.device, "items", "SpDi3", "items", "SpDi3"), "channelNumber": 0, "channelType": "Digital", "channelIoType": "Input",
               "attributes": {"InputDelay": 3}}
    error = run.refuse("hardware_channel_attributes", "a channel write the module's capability profile declares read-only", channel, "AttributeNotWritable")
    message(run, "profiles", error, "capability profile of " + identifier, "InputDelay", "this channel")


def versionless_gate(run, identifier):
    """The I-device GSD's bundled profile, keyed by its head's FirmwareVersion (a GSD: identifier names no firmware): the catalog search
    names it, and a write it does not declare (InformationText on the head) is refused by it."""
    entry = (run.h.report.get("gsdCatalog") or [{}])[0]
    run.expect(entry.get("profile") == PROFILE_LINK + identifier, "profiles", "hardware_catalog_search does not name the profile of %s: %s" % (identifier, entry))
    error = run.refuse("hardware_attributes", "a write the GSD head's versionless profile does not declare",
                       {"hardwarePath": run.path("HwGsd", "items", "HwGsdHead"), "attributes": {"InformationText": "HwText"}}, "AttributeNotWritable")
    message(run, "profiles", error, "capability profile of " + identifier)


def shared_refusal(run):
    """A module of HwPeerCpu straight to the shared device's other PLC: the worker covers only the route through OutsideOfProject."""
    payload = {"hardwarePath": run.path(SP.device, "items", "SpDi"), "accessType": "PLC", "assignmentTargetPath": run.path("ScratchStation", "items", "ScratchPLC")}
    error = run.refuse("hardware_shared_device_access", "a direct move between the station's two PLCs", payload, "UnsupportedCapability")
    message(run, "shared access", error, "direct move between two PLCs")


def shared_disconnect(run):
    """The ET 200SP interface's connector 1 leaves the scratch CPU's IO system (sharedDevice disconnect), applied and restored.
    True when applied."""
    device, owner = run.path(SP.device, "items", *SP.interface), run.path("ScratchStation", "items", "ScratchPLC")
    payload = {"hardwarePath": device, "connectorIndex": 1, "disconnect": True, "sharedDevice": True}
    run.b0 = run.current
    return run.apply("disconnect %s's shared connector, restored" % SP.device, "network_io_assign", payload,
                     {"effects": [{"hardwarePath": device, "connectorIndex": 1, "beforeIoSystem": SHARED_IO, "afterIoSystem": None, "disconnect": True, "sharedDevice": True}]},
                     shown={**payload, "addressControllerOwnerPath": owner, "sharedAddressControllerOwnerPaths": [run.path("HwPeer", "items", "HwPeerCpu")]},
                     plc=scratch_plc(run), changed=["hw:" + device], restore=True, boundary=True) is not None


def link_refusals(run):
    """The MRP port-link rule both ways, natively: with the two CPUs' Port_2 linked as plain ports a ring role is refused, and once both
    hold ring roles (set while unlinked) and are linked again, NotInRing on one of them is refused."""
    manager, client = interfaces(run)[:2]
    link(run, "hardware_port_connect", 2)
    error = run.refuse("network_mrp_set", "a ring role on a port linked to a non-ring port", {"hardwarePath": manager, "role": "Manager", "domainName": RING, "createDomain": True},
                       "NetworkPrerequisite")
    message(run, "ring links", error, "would be a ring port while linked to", "which is not a ring port")
    link(run, "hardware_port_disconnect", 2)
    if mrp(run, manager, "Manager", RING, create=True) and mrp(run, client, "Client", RING):
        link(run, "hardware_port_connect", 2, label="Port_2 between ring members")
        error = run.refuse("network_mrp_set", "NotInRing on a port linked to a ring port", {"hardwarePath": client, "role": "NotInRing"}, "NetworkPrerequisite")
        message(run, "ring links", error, "would leave the ring while linked to", ", a ring port")


def complementary(run, first):
    """The two projects' ET 200SP stations: the same DI modules at the same slots, each owned (SharedDeviceAccessType PLC) in exactly
    one project, and each interface counting the other project's IO controllers of the station as external."""
    def rows(base, view):
        prefix = "hw:" + base + SP.device + "/items/"
        return {key[len(prefix):]: row for key, row in view.items() if key.startswith(prefix)}
    def check():
        stations, modules = (rows(*first), rows(run.base, run.current["view"])), {}
        for name in ("SpDi3", "SpDi", "SpDi4"):
            modules[name] = [{"typeIdentifier": row["typeIdentifier"], "positionNumber": row["positionNumber"], "access": row["attributes"].get("SharedDeviceAccessType")}
                             for row in (station_[name] for station_ in stations)]
            first_, second = modules[name]
            require({**first_, "access": None} == {**second, "access": None} and sorted((first_["access"], second["access"])) == ["OutsideOfProject", "PLC"],
                    "The two projects' %s differ or share an owner: %s" % (name, modules[name]))
        interface = "/".join(SP.interface)
        counts = [station_[interface]["attributes"].get("NumberOfExternalIoControllers") for station_ in stations]
        # The first project's two PLCs (HwPeerCpu and the scratch CPU) are outside the second; the second's CPU is outside the first.
        require(counts == [1, 2], "External IO controller counts differ: %s" % counts)
        run.h.report["secondProject"] = {"modules": modules, "externalIoControllers": counts}
        return run.h.report["secondProject"]
    run.h.stage("the two projects share the ET 200SP station complementarily", check)


def second_project(run, module, first):
    """A second owned scratch: its own CPU on HwNet (192.168.0.6) takes the same ET 200SP station into a new IO system HwOtherIo, with
    the same DI modules; SpDi4, which the first project set OutsideOfProject, stays its own, SpDi3 and SpDi go OutsideOfProject with
    the first project's two PLCs as external IO controllers. first: the first project's (base, final view)."""
    h = run.h
    h.stage("close the first project", lambda: h.tool("tia_detach"))
    h.owned_scratch("second project")
    status = h.tool("tia_session_status")["projects"][0]
    run.project, run.base = status["path"], "/projects/%s/devices/" % status["name"]
    run.plc = scratch_plc(run)
    run.current = run.snap("second project baseline")
    node(run, "ScratchStation", ("ScratchPLC", "items", "PROFINET%20interface_1"), "192.168.0.6")
    station_ = sp_station(run, SP2, OTHER_IO)
    plug(run, "SpDi3", 1, station_, module, changed=["hw:" + run.path(SP.device, "items", *SP.interface)])
    for name, slot in (("SpDi", 2), ("SpDi4", 3)):
        plug(run, name, slot, station_, module)
    shared_access(run, "SpDi3", "OutsideOfProject", False, external=2, station=SP2)
    shared_access(run, "SpDi", "OutsideOfProject", False, station=SP2)
    complementary(run, first)


def refusal_slice(run):
    """--slice refusals: the native refusals and paths the full run lacks, in one owned scratch, then a second owned scratch.
    HwDi and HwDi2 fill the scratch rack's first input bytes, so HwDi's wider replacement moves its start; the ET 200SP station's
    assignment creates HwPeerIo, which the GSD device joins; the shared device then gives SpDi3 to the scratch CPU."""
    add_cpu(run, "add CPU HwPeer", "HwPeer")
    network(run)
    plug(run, "HwDi", 2)
    plug(run, "HwDi2", 3)
    address_change(run, moved=True)
    sp = sp_station(run, io_system=IO_SYSTEM)
    older, _ = sp_modules(run, sp)
    plug(run, "SpDi3", 1, sp, older, changed=["hw:" + run.path(SP.device, "items", *SP.interface)])
    plug(run, "SpDi", 2, sp, older)
    plug(run, "SpDi4", 3, sp, older)
    channel_gate(run, older)
    rows = gsd_entries(run)
    gsd_device(run, rows)
    if "gsdDevice" in run.h.report:
        versionless_gate(run, rows[0]["typeIdentifier"])
        profiles(run, only={rows[0]["typeIdentifier"]})
    if shared_device(run):
        shared_refusal(run)
        shared_access(run, "SpDi4", "OutsideOfProject", True, external=1)
        shared_disconnect(run)
    link_refusals(run)
    first = (run.base, run.current["view"])
    if run.coverage.get("hardware_shared_device_access", {}).get("applied"):
        second_project(run, older, first)
    wanted = {"network_mrp_set": "refused", "hardware_shared_device_access": "refused", "hardware_channel_attributes": "refused", "hardware_attributes": "refused",
              "network_io_assign": "restored", "hardware_replace": "restored"}
    missing = [kind for kind, need in wanted.items() if not run.coverage.get(kind, {}).get(need)] + [key for key in ("gsdDevice", "secondProject") if key not in run.h.report]
    run.expect(not missing and len(run.coverage["network_mrp_set"]["refused"]) == 2, "coverage", "Refusal slice gaps: %s" % missing)


def heading(args):
    """The report title and limitations of the full run or the selected slice."""
    title = {"restores": "X10 topology restores and capability-profile gates in one owned scratch CPU 1511-1 PN with ET 200MP and ET 200SP stations",
             "gsd": "X10 GSD device: the project's own I-device GSD as an IO device of an added CPU 1511-1 PN in one owned scratch",
             "refusals": "X10 hardware refusals and topology paths natively: MRP port links, PLC-to-PLC shared access, channel and versionless profiles, the sharedDevice "
                         "disconnect, a start-moving address change and a second project's controller, in two owned scratches",
             None: "X10 hardware changes in one owned scratch CPU 1511-1 PN with S7-1200, ET 200MP and ET 200SP families, a shared device and an MRP ring"}[args.slice]
    return title + (" (key discovery, not evidence)" if args.discover else ""), {"restores": SLICE_LIMITATIONS, "gsd": GSD_SLICE_LIMITATIONS, "refusals": REFUSAL_LIMITATIONS,
                                                                                None: LIMITATIONS}[args.slice]


def body(h):
    run = Run(h)
    h.owned_scratch("hardware changes")
    status = h.tool("tia_session_status")["projects"][0]
    run.project, run.base = status["path"], "/projects/%s/devices/" % status["name"]
    run.plc = scratch_plc(run)  # The PLC compiled by tag_create.
    run.current = run.snap("baseline")
    sliced = getattr(getattr(h, "args", None), "slice", None)
    if sliced:
        {"restores": restore_slice, "gsd": gsd_slice, "refusals": refusal_slice}[sliced](run)
        return
    phase_a(run)
    reopen(run)
    phase_b(run)
    profiles(run)
    gaps = coverage_gaps(run.coverage, KINDS)
    run.expect(not gaps, "coverage", "Kinds without applied, persisted, refused and restored evidence: %s" % gaps)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    parser.add_argument("--discover", action="store_true", help="Record unexpected state differences, previews and refusals instead of failing (not evidence).")
    parser.add_argument("--slice", choices=("restores", "gsd", "refusals"), help="restores: only the profile gates and the topology restores the full run lacks; "
                        "gsd: only the installed I-device GSD device and what it needs; refusals: the native refusals and topology paths the full run lacks, "
                        "with a second owned scratch.")
    args = parser.parse_args()
    args.runner = __file__
    title, limitations = heading(args)
    Harness(args, "hw_", title, limitations, []).run(body)
