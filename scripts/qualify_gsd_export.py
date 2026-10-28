"""Qualify hardware_gsd_export and the transfer-area kinds natively on an I-device CPU 1511-1 PN in one owned scratch project.

Phase A puts the CPU's X1 node on a subnet, then makes its PROFINET interface an I-device with hardware_attributes
(InterfaceOperatingMode "IoController, IoDevice"). V21 compiles a new I-device only with a transfer area, so that change
applies without compile or save, and the checkpoint of the next change saves it. hardware_transfer_area_create then adds
the controller-device areas TiaMcpArea, TiaMcpSpare and TiaMcpTemp and hardware_transfer_area_delete removes TiaMcpTemp, each
under checkpoint, device compile and save; the complete hardware, device-inventory and program state must differ in
exactly the intended keys. hardware_gsd_export then exports the I-device GSD from the item that reports
gsdExportAvailable: every returned file must exist with its SHA-256, the GSDML must be well-formed XML naming the
designation, and the export must change nothing. Typed refusals cover an invented path, an item without the service, a
blank designation, a missing name, the interface before it is an I-device, an existing or absent area name. A fresh Portal
reopens the saved project, which must show the identical state and export a GSDML of the same name. Phase B creates
TiaMcpTemp, deletes TiaMcpSpare and switches the interface back to IoController (which withdraws the export), restoring
each checkpoint: every recovered copy equals the reopened state, and the last one exports again.
"""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import xml.etree.ElementTree as ET

from library_native_harness import Harness, require
from qualify_hardware_changes import CRITERIA as HARDWARE_CRITERIA, Run
from qualify_plc_object_mutations import STAGES, coverage_gaps, diff, in_order, matches

DESIGNATION = "TiaMcpIDevice"
# V21 refuses IoDevice alone on this CPU interface ("cannot be set to the value 2"): it stays an IO controller as well.
IDEVICE = "IoController, IoDevice"
AREA, SPARE, TEMP = "TiaMcpArea", "TiaMcpSpare", "TiaMcpTemp"
KINDS = ("hardware_transfer_area_create", "hardware_transfer_area_delete")
CRITERIA = {
    **HARDWARE_CRITERIA,
    "AC-identity": "refused: an invented hardware path, an item without GsdExportProvider, a blank designation and the interface before it is an I-device (and after it is switched back) "
                   "are typed export refusals; transfer-area kinds refuse the interface before it is an I-device, an item without a network interface, a missing name, "
                   "an existing name in another case (ObjectExists) and an absent exact name (ObjectNotFound); hardware_attributes refuses an operating-mode value naming an unknown mode. "
                   "Each refusal leaves the complete state unchanged.",
    "AC-completeness": "Every file the export returns exists with its SHA-256 and byte count, the result is not truncated, and the GSDML is well-formed XML naming the designation; "
                       "the fresh reopen and the restored copy export a GSDML of the same name.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 whose PROFINET interface is IO controller and I-device (InterfaceOperatingMode \"IoController, IoDevice\"; V21 refuses IoDevice alone) "
    "on a subnet without a higher-level IO controller; no shared device or controller assignment is configured.",
    "Transfer areas are controller-device (CD) areas with the direction, lengths and local addresses TIA chooses; F-CD, PROFIsafe, shared-device and mapping-rule areas, "
    "direction and length edits are not exercised.",
    "The I-device mode change applies without compile or save, because V21 compiles a new I-device only with a transfer area; the next change's checkpoint saves it.",
    "The interface's CalculatedBandwidthForCyclicIoData (7040 after the device compile) is not saved: a fresh open reads 0 until the next compile, so the reopen comparison "
    "excludes it and records both values under derivedAfterReopen.",
    "The GSDML content is Siemens output: the runner checks that it is well-formed XML naming the designation, not that another engineering tool imports it.",
    "Engineering only; nothing is downloaded and no PROFINET communication is observed.",
]


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def exportable(run):
    """Exact hardware paths whose item exposes GsdExportProvider."""
    inspect = run.h.tool("hardware_inspect", {"maxItems": 2000})
    return sorted(item["path"] for item in inspect["items"] if item.get("gsdExportAvailable"))


def export(run, label, path):
    def check():
        before = run.state()
        result = run.h.tool("hardware_gsd_export", {"hardwarePath": path, "designation": DESIGNATION, "description": "TIA MCP qualification"})
        require(result["hardwarePath"] == path and result["designation"] == DESIGNATION and result["truncated"] is False and result["completeness"]["complete"] is True,
                "Export result differs: %s" % {k: result.get(k) for k in ("hardwarePath", "designation", "truncated", "completeness")})
        files = []
        for artifact in result["artifacts"]:
            file = Path(artifact["path"])
            require(file.is_file() and sha256(file) == artifact["sha256"] and file.stat().st_size == artifact["bytes"], "Exported file differs from its receipt: %s" % artifact)
            files.append({"name": file.name, "bytes": artifact["bytes"], "sha256": artifact["sha256"]})
        gsdml = [Path(a["path"]) for a in result["artifacts"] if Path(a["path"]).suffix.lower() == ".xml"]
        require(len(gsdml) == 1, "Expected one GSDML file: %s" % files)
        root = ET.parse(gsdml[0]).getroot()
        require(root.tag.endswith("ISO15745Profile") and DESIGNATION in gsdml[0].read_text(encoding="utf-8-sig"), "The GSDML is not an ISO 15745 profile naming %s" % DESIGNATION)
        require(run.state() == before, "The GSD export changed the project")
        return {"gsdml": gsdml[0].name, "files": files}
    result = run.h.stage("export GSD " + label, check)
    run.h.report.setdefault("gsdExports", {})[label] = result
    return result


def refuse_export(run, label, payload, codes):
    def check():
        before = run.state()
        error = run.h.call_error("hardware_gsd_export", payload)
        require(error.get("code") in codes, "Expected %s, observed %s: %s" % (codes, error.get("code"), error.get("message")))
        require(run.state() == before, "Refused export changed the project")
        return error
    return run.h.stage("refuse hardware_gsd_export: " + label, check)


def coupled(interface):
    """The interface's I-device attributes, device_inventory's rows of the station, CPU and interface, and the CPU's hardware-identifier
    system constants (TIA adds Local~PROFINET_interface_1~IODevice with the I-device) follow the mode and its transfer areas."""
    cpu = interface.rsplit("/items/", 1)[0]
    return lambda category, key: category in ("changed", "added", "removed") and (key.startswith("hw:" + cpu) or key.startswith("inv:" + cpu.rsplit("/items/", 1)[0])
                                                                                    or key.startswith("member:" + cpu + "/plc/") and "/systemConstants/" in key)


def mode_uncompiled(run, interface, value):
    """hardware_attributes without compile or save: a new I-device without a transfer area fails the device compile."""
    h, before, key = run.h, run.current, "hw:" + interface
    label = "set %s's InterfaceOperatingMode to %s, uncompiled" % (interface.rsplit("/", 1)[1], value)
    prepared = run.prepare(label, "hardware_attributes", {"hardwarePath": interface, "attributes": {"InterfaceOperatingMode": value}},
                           {"effects": [{"hardwarePath": interface}], "attributeChanges": [{"name": "InterfaceOperatingMode", "after": value}]})
    dispatch = {"kind": "hardware_attributes", "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "state": "dispatching"}
    h.report["mutations"].append(dispatch)
    applied = h.stage("apply hardware_attributes without compile or save", lambda: h.tool("change_apply", {**h.apply_args(prepared), "compile": False, "save": False}))
    require(applied["state"] == "applied" and in_order(applied["stages"], STAGES[:3]) and not {"compile_finished", "changed_project_saved"} & set(applied["stages"]),
            "Uncompiled apply stages differ: %s %s" % (applied["state"], applied["stages"]))
    dispatch["state"] = "applied"
    run.current = run.snap(label)
    def verify():
        state = run.current["view"]
        observed = diff(before["view"], state)
        unexpected = {c: [k for k in keys if k != key and not coupled(interface)(c, k)] for c, keys in observed.items()}
        require(key in observed["changed"] and not any(unexpected.values()), "Native effect of %s differs: %s" % (label, observed))
        require(matches({"attributes": {"InterfaceOperatingMode": value}, "network": {"transferAreas": []}}, state[key]), "Readback of %s differs: %s" % (key, state[key]))
        side = {c: [k for k in keys if k != key] for c, keys in observed.items() if any(k != key for k in keys)}
        if side:
            h.report["sideEffects"][label] = {c: {k: {"before": before["view"].get(k), "after": state.get(k)} for k in keys} for c, keys in side.items()}
        return {"observed": observed}
    h.stage("verify " + label, verify)
    run.mark("hardware_attributes", "applied", label)


def area(run, kind, interface, name, restore=False):
    """One transfer-area change: the interface's area names become exactly the expected set and the apply readback equals hardware_inspect."""
    create, key = kind == "hardware_transfer_area_create", "hw:" + interface
    names = sorted(a["name"] for a in run.current["view"][key]["network"]["transferAreas"])
    expected = sorted(set(names) | {name}) if create else sorted(set(names) - {name})
    def check(applied, state):
        areas = state[key]["network"]["transferAreas"]
        require([a["name"] for a in areas] == expected, "Transfer areas after %s %s: %s" % (kind, name, areas))
        readback = [r for r in applied["readbacks"] if r.get("role") == "transfer-areas"]
        require(len(readback) == 1 and readback[0]["transferAreas"] == areas, "The apply readback differs from hardware_inspect: %s" % readback)
        if create:
            row = next(a for a in areas if a["name"] == name)
            require(row["type"] == "CD" and row["localAddresses"], "The created area is not a CD area with local addresses: %s" % row)
            run.h.report.setdefault("transferAreas", {})[name] = row
    run.apply("%s transfer area %s" % ("create" if create else "delete", name), kind, {"hardwarePath": interface, "name": name},
              {"effects": [{"hardwarePath": interface, "action": "create" if create else "delete", "name": name, "type": "CD"}]},
              changed=[key], tolerated=coupled(interface), check=check, restore=restore)


def phase_a(run, cpu, interface):
    display = cpu + "/items/CPU%20display_1"
    refuse_export(run, "an invented hardware path", {"hardwarePath": interface + "X", "designation": DESIGNATION}, ["HardwareNotFound"])
    refuse_export(run, "the CPU display, which has no GsdExportProvider", {"hardwarePath": display, "designation": DESIGNATION}, ["UnsupportedCapability"])
    run.h.stage("no item reports gsdExportAvailable before the I-device", lambda: require(exportable(run) == [], "Exportable before the I-device: %s" % exportable(run)) or [])
    refuse_export(run, "the interface before it is an I-device", {"hardwarePath": interface, "designation": DESIGNATION}, ["UnsupportedCapability"])
    run.apply("put ScratchPLC on GsdNet", "network_configure", {"hardwarePath": interface, "nodeName": "X1", "subnetName": "GsdNet"}, changed=["hw:" + interface])
    run.refuse("hardware_attributes", "an unknown name in an operating-mode set", {"hardwarePath": interface, "attributes": {"InterfaceOperatingMode": "IoController, SharedDevice"}}, "InvalidRequest")
    run.refuse("hardware_attributes", "an unknown operating mode", {"hardwarePath": interface, "attributes": {"InterfaceOperatingMode": "SharedDevice"}}, "InvalidRequest")
    run.refuse(KINDS[0], "the interface before it is an I-device", {"hardwarePath": interface, "name": AREA}, "UnsupportedCapability")
    mode_uncompiled(run, interface, IDEVICE)
    run.refuse(KINDS[0], "the CPU display, which has no network interface", {"hardwarePath": display, "name": AREA}, "UnsupportedCapability")
    run.refuse(KINDS[0], "a missing name", {"hardwarePath": interface}, "InvalidRequest")
    run.refuse(KINDS[1], "an area that does not exist", {"hardwarePath": interface, "name": AREA}, "ObjectNotFound")
    for name in (AREA, SPARE, TEMP):
        area(run, KINDS[0], interface, name)
    run.refuse(KINDS[0], "an existing name in another case", {"hardwarePath": interface, "name": AREA.lower()}, "ObjectExists")
    run.refuse(KINDS[1], "an existing name in another case", {"hardwarePath": interface, "name": TEMP.lower()}, "ObjectNotFound")
    area(run, KINDS[1], interface, TEMP)
    paths = exportable(run)
    run.h.report["exportableAfter"] = paths
    require(paths == [interface], "Expected only the I-device interface to report gsdExportAvailable: %s" % paths)
    # The Host binds designation as a required parameter; a blank one reaches the worker's refusal.
    refuse_export(run, "a blank designation", {"hardwarePath": paths[0], "designation": " "}, ["InvalidRequest"])
    return paths[0], export(run, "of the I-device", paths[0])


# TIA computes an interface's cyclic IO bandwidth at the device compile and does not save it: a fresh open reads 0 until the next compile.
DERIVED = ("CalculatedBandwidthForCyclicIoData",)


def saved(view):
    """The state without compile-derived attributes, for comparison across a fresh open."""
    return {key: {**row, "attributes": {n: v for n, v in row["attributes"].items() if n not in DERIVED}} if key.startswith("hw:") else row for key, row in view.items()}


def reopen(run, source, first):
    h, final = run.h, run.current
    h.stage("close the changed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen the saved project", lambda: h.reopen_project(run.project))
    run.current = run.b0 = run.snap("reopened")
    def equal():
        before, after = saved(final["view"]), saved(run.b0["view"])
        require(after == before, "Reopened state differs: %s" % diff(before, after))
        h.report["derivedAfterReopen"] = {key: {name: {"applied": row["attributes"].get(name), "reopened": run.b0["view"][key]["attributes"].get(name)} for name in DERIVED}
                                          for key, row in final["view"].items() if key.startswith("hw:") and any(row["attributes"].get(n) != run.b0["view"][key]["attributes"].get(n) for n in DERIVED)}
        return len(before)
    h.stage("reopened state equals every verified change", equal)
    for kind, rows in list(run.coverage.items()):
        if rows.get("applied"):
            run.mark(kind, "persisted", "fresh Portal reopen")
    again = export(run, "after the fresh reopen", source)
    h.stage("the reopened project exports a GSDML of the same name", lambda: require(again["gsdml"] == first["gsdml"], "GSDML names differ: %s / %s" % (first["gsdml"], again["gsdml"])) or again["gsdml"])


def phase_b(run, interface, source, first):
    area(run, KINDS[0], interface, TEMP, restore=True)
    area(run, KINDS[1], interface, SPARE, restore=True)
    prepared = run.prepare("switch the interface back to IoController", "hardware_attributes", {"hardwarePath": interface, "attributes": {"InterfaceOperatingMode": "IoController"}},
                           {"effects": [{"hardwarePath": interface}], "attributeChanges": [{"name": "InterfaceOperatingMode", "after": "IoController"}]})
    run.finish("switch the interface back to IoController", "hardware_attributes", prepared, changed=["hw:" + interface], tolerated=coupled(interface),
               rows={"hw:" + interface: {"attributes": {"InterfaceOperatingMode": "IoController"}}})
    refuse_export(run, "the interface switched back to IoController", {"hardwarePath": source, "designation": DESIGNATION}, ["UnsupportedCapability"])
    run.restore("switch the interface back to IoController", "hardware_attributes", prepared)
    again = export(run, "from the restored copy", source)
    run.h.stage("the restored copy exports a GSDML of the same name", lambda: require(again["gsdml"] == first["gsdml"], "GSDML names differ") or again["gsdml"])


def body(h):
    run = Run(h)
    h.report["criteria"] = CRITERIA
    h.owned_scratch("GSD export")
    status = h.tool("tia_session_status")["projects"][0]
    run.project, run.base = status["path"], "/projects/%s/devices/" % status["name"]
    cpu = run.path("ScratchStation", "items", "ScratchPLC")
    interface = cpu + "/items/PROFINET%20interface_1"
    run.current = run.snap("baseline")
    source, first = phase_a(run, cpu, interface)
    reopen(run, source, first)
    phase_b(run, interface, source, first)
    gaps = coverage_gaps(run.coverage, ("hardware_attributes",) + KINDS)
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
    Harness(args, "gs_", "X10 hardware_gsd_export and transfer areas on an I-device CPU 1511-1 PN in one owned scratch", LIMITATIONS, []).run(body)
