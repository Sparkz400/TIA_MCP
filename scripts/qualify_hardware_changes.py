"""Qualify the X10 hardware change kinds natively in one owned scratch CPU 1511-1 PN.

Phase A adds a second CPU, puts both PROFINET nodes on one subnet, plugs two DI modules, and applies every covered kind with
checkpoint, device compile and save: attributes, type replacement, port connect and disconnect, and module deletion. Each
preview is asserted against the request, and a complete state (hardware_inspect of every item with its complete attribute
values, ports and subnet membership, device_inventory rows, and the program exports of every PLC) must differ from the
previous one in exactly the intended keys. TIA-coupled side effects are tolerated only where named, and are recorded. A fresh
Portal then reopens the saved project, which must show the identical state. Phase B applies one more change per kind from
that state and restores its checkpoint in the applying Portal; the recovered copy must equal the pre-change state. Typed
refusals must change nothing.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re

from library_native_harness import Harness, require
from qualification_owned_scratch import snapshot
from qualify_access_changes import Run as StateRun
from qualify_plc_object_mutations import coverage_gaps, diff, view

KINDS = ("hardware_add", "hardware_attributes", "hardware_replace", "hardware_port_connect", "hardware_port_disconnect", "hardware_delete")
CPU = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"
# Two firmware versions of DI 16x24VDC HF; the change between them keeps the module's addresses.
MODULE, MODULE_V2 = "OrderNumber:6ES7 521-1BH00-0AB0/V1.0", "OrderNumber:6ES7 521-1BH00-0AB0/V2.0"
CONFIGURED = ("hardware_replace", "hardware_port_connect", "hardware_port_disconnect", "network_configure", "hardware_plug")
CRITERIA = {
    "AC-identity": "refused: typed refusals of duplicate, unknown, built-in, software-bearing, unchanged, unconnected or already connected targets, each leaving the complete state unchanged.",
    "AC-preview": "Every preview is asserted against the request before apply; a wrong previewHash refuses at apply.",
    "AC-preserve": "applied: hardware_inspect items (complete attribute values, addresses, channels, ports, subnet membership), device_inventory rows and every PLC's program exports differ in exactly the intended keys; named TIA-coupled side effects are recorded under sideEffects.",
    "AC-native": "applied + persisted: checkpoint, device compile and save stages (configured_hardware_preservation_verified for replace and ports), change_status and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the pre-change state.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json, limitations and sideEffects.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 plus one added CPU 1511-1 PN V4.1 (configureScratchSecurity) on one subnet; ET 200SP, ET 200MP, HMI, drive and GSD devices are not added, "
    "because an unassigned IO device does not compile on its own.",
    "Modules: DI 16x24VDC HF (6ES7 521-1BH00-0AB0) in the CPU rack; replacement only between its firmware versions V1.0 and V2.0, which keep the addresses. "
    "acceptLoss, address-changing replacements and replacements that lose child items are not exercised.",
    "hardware_attributes writes Comment and Author of a module; TIA mirrors Comment to the module's built-in submodule (recorded under sideEffects). Channel attributes are qualified by the remote-I/O run.",
    "Port interconnections between the two CPUs' X1 ports only; TIA sets the ports' cable defaults (SignalDelaySelection, CableLength) on connect and clears them on disconnect (recorded under sideEffects).",
    "hardware_delete removes a pluggable module; CPUs, HMIs and built-in items are refused by design.",
    "Engineering only; nothing is downloaded and no physical topology is observed.",
]


def hardware(inspect):
    """Comparable configured hardware from hardware_inspect, keyed by exact item path; only complete captured attribute
    values are compared, so display-only values (timestamps, associations) never count as changes."""
    require(inspect["completeness"]["truncated"] is False, "hardware_inspect truncated its scope: %s" % inspect["completeness"])
    state = {}
    for item in inspect["items"]:
        row = {key: item.get(key) for key in ("name", "typeIdentifier", "positionNumber", "isBuiltIn", "isPlugged", "classification", "addresses", "channels", "gsd", "network")}
        row["attributes"] = {a["name"]: a.get("value") for a in item["attributes"] if a.get("valueComplete") and a.get("valueStatus") == "captured"}
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

    def __init__(self, h):
        super().__init__(h)
        for key in ("certificateIdentity", "notQualified"):
            h.report.pop(key)
        h.report.update(criteria=CRITERIA, excluded={})
        self.base = None

    def args(self, kind, payload, **top):
        return {"kind": kind, "payload": payload, **top}

    def plcs(self):
        return [p["path"] for p in self.h.tool("project_inventory", {"maxItems": 1000})["plcs"]]

    def domains(self):
        return {**hardware(self.h.tool("hardware_inspect", {"maxItems": 2000})), **devices(self.h.tool)}

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
        return {"view": taken["view"]}

    def path(self, *parts):
        return self.base + "/".join(parts)

    def hw(self, *parts):
        return "hw:" + self.path(*parts)

    def apply(self, label, kind, payload, preview=None, **expected):
        prepared = self.prepare(label, kind, payload, preview)
        applied = self.finish(label, kind, prepared, **expected)
        require(("configured_hardware_preservation_verified" in applied["stages"]) == (kind in CONFIGURED),
                "Configured-hardware preservation stage differs for %s: %s" % (kind, applied["stages"]))
        return prepared


def coupled_comment(module):
    """TIA mirrors a module's Comment to its built-in submodule."""
    return lambda category, key: category == "changed" and key == module + "/items/" + module.rsplit("/", 1)[1]


def port_defaults(ports):
    """TIA sets (connect) or clears (disconnect) the cable defaults of both connected ports."""
    return lambda category, key: category == "changed" and key in ports


def add_cpu(run, label, name, restore=False):
    payload = {"typeIdentifier": CPU, "deviceName": name, "itemName": name + "Cpu", "configureScratchSecurity": True}
    # The second effect is the worker's description of configureScratchSecurity.
    run.apply(label, "hardware_add", payload, {"effects": [{"createDevice": name, "typeIdentifier": CPU}, {}]},
              added=under(run.path(name)), rows={run.hw(name, "items", name + "Cpu"): {"typeIdentifier": CPU, "name": name + "Cpu"}}, restore=restore)


def plug(run, name, slot):
    rail = run.path("ScratchStation", "items", "Rail_0")
    module = run.hw("ScratchStation", "items", name)
    constant = "member:" + run.path("ScratchStation", "items", "ScratchPLC", "plc", "ScratchPLC", "tags", "Default%20tag%20table", "systemConstants", "Local~" + name)
    run.apply("plug " + name, "hardware_plug", {"hardwarePath": rail, "typeIdentifier": MODULE, "name": name, "positionNumber": slot},
              {"effects": [{"parentPath": rail, "createItem": name, "positionNumber": slot, "typeIdentifier": MODULE}]},
              added=under(module[3:], constant), rows={module: {"typeIdentifier": MODULE, "positionNumber": slot}})


def network(run):
    for device, cpu, extra in (("HwPeer", "HwPeerCpu", {"attributes": {"Address": "192.168.0.2"}}), ("ScratchStation", "ScratchPLC", {})):
        interface = run.path(device, "items", cpu, "items", "PROFINET%20interface_1")
        # device_inventory reports the node address on the device, CPU and interface rows, so only the readdressed peer changes there.
        rows = [run.path(device), run.path(device, "items", cpu), interface] if extra else []
        run.apply("put %s on HwNet" % cpu, "network_configure", {"hardwarePath": interface, "nodeName": "X1", "subnetName": "HwNet", **extra},
                  changed=["hw:" + interface] + ["inv:" + row for row in rows])


def attributes(run, name, values, restore=False):
    module = run.hw("ScratchStation", "items", name)
    run.apply("set %s's %s" % (name, " and ".join(values)), "hardware_attributes", {"hardwarePath": module[3:], "attributes": values},
              {"effects": [{"hardwarePath": module[3:]}], "attributeChanges": [{"name": key, "after": value} for key, value in values.items()]},
              changed=[module], rows={module: {"attributes": values}}, tolerated=coupled_comment(module), restore=restore)


def replace(run, name, before, after, restore=False):
    module = run.hw("ScratchStation", "items", name)
    run.apply("replace %s with %s" % (name, after), "hardware_replace", {"hardwarePath": module[3:], "typeIdentifier": after},
              {"effects": [{"hardwarePath": module[3:], "typeIdentifierBefore": before, "typeIdentifierAfter": after, "acceptLoss": False}]},
              changed=[module, "inv:" + module[3:]], rows={module: {"typeIdentifier": after}}, restore=restore)


def ports(run, number):
    return [run.path(device, "items", cpu, "items", "PROFINET%20interface_1", "items", "Port_%d" % number)
            for device, cpu in (("ScratchStation", "ScratchPLC"), ("HwPeer", "HwPeerCpu"))]


def link(run, kind, number, restore=False):
    local, partner = ports(run, number)
    interfaces = ["hw:" + port.rsplit("/items/", 1)[0] for port in (local, partner)]
    connected = kind == "hardware_port_connect"
    run.apply("%s Port_%d" % ("connect" if connected else "disconnect", number), kind, {"hardwarePath": local, "partnerHardwarePath": partner},
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


def delete(run, name, restore=False):
    module = run.hw("ScratchStation", "items", name)
    constant = "member:" + run.path("ScratchStation", "items", "ScratchPLC", "plc", "ScratchPLC", "tags", "Default%20tag%20table", "systemConstants", "Local~" + name)
    run.apply("delete " + name, "hardware_delete", {"hardwarePath": module[3:]},
              {"effects": [{"deleteHardwarePath": module[3:], "positionNumber": int(run.current["view"][module]["positionNumber"])}]},
              removed=under(module[3:], constant)(None, run.current["view"]), restore=restore)


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
    network(run)
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
    link(run, "hardware_port_connect", 2)
    delete(run, "HwDi")


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
    add_cpu(run, "add CPU HwOther", "HwOther", restore=True)
    attributes(run, "HwDi2", {"Comment": "HwAgain"}, restore=True)
    replace(run, "HwDi2", MODULE, MODULE_V2, restore=True)
    link(run, "hardware_port_connect", 1, restore=True)
    link(run, "hardware_port_disconnect", 2, restore=True)
    delete(run, "HwDi2", restore=True)


def body(h):
    run = Run(h)
    h.owned_scratch("hardware changes")
    status = h.tool("tia_session_status")["projects"][0]
    run.project, run.base = status["path"], "/projects/%s/devices/" % status["name"]
    run.current = run.snap("baseline")
    phase_a(run)
    reopen(run)
    phase_b(run)
    gaps = coverage_gaps(run.coverage, KINDS)
    require(not gaps, "Kinds without applied, persisted, refused and restored evidence: %s" % gaps)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "hw_", "X10 hardware changes in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
