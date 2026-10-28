"""Qualify hardware_gsd_export natively on an I-device CPU 1511-1 PN in one owned scratch project.

Phase A puts the CPU's X1 node on a subnet, then makes its PROFINET interface an I-device with hardware_attributes
(InterfaceOperatingMode IoDevice) under checkpoint, device compile and save; the complete hardware, device-inventory and
program state must differ in exactly the intended keys. hardware_gsd_export then exports the I-device GSD from the item that
reports gsdExportAvailable: every returned file must exist with its SHA-256, the GSDML must be well-formed XML naming the
designation, and the export must change nothing. Typed refusals cover an invented path, an item without the service, a
missing designation and the export before the interface is an I-device. A fresh Portal reopens the saved project, which
must show the identical state and export a GSDML of the same name. Phase B switches the interface back to IoController, which
withdraws the export, and restores its checkpoint: the recovered copy equals the I-device state and exports again.
"""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import xml.etree.ElementTree as ET

from library_native_harness import Harness, require
from qualify_hardware_changes import Run
from qualify_plc_object_mutations import coverage_gaps, diff

DESIGNATION = "TiaMcpIDevice"
CRITERIA = {
    "AC-identity": "refused: an invented hardware path, an item without GsdExportProvider, a missing designation and the interface before it is an I-device (and after it is switched back) "
                   "are typed refusals that leave the complete state unchanged; hardware_attributes refuses an operating-mode value that is not one enum name.",
    "AC-completeness": "Every file the export returns exists with its SHA-256 and byte count, the result is not truncated, and the GSDML is well-formed XML naming the designation; "
                       "the fresh reopen and the restored copy export a GSDML of the same name.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json, the exported files' hashes, sideEffects and limitations.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 whose PROFINET interface is an I-device only (InterfaceOperatingMode IoDevice) on a subnet without an IO controller; "
    "no transfer areas, shared device or higher-level controller assignment are configured, and IoController plus IoDevice at once is not settable through one enum name.",
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


def mode(run, interface, value, restore=False):
    key = "hw:" + interface
    cpu = interface.rsplit("/items/", 1)[0]
    # The interface's I-device attributes and device_inventory's rows of the station, CPU and interface follow the mode.
    coupled = lambda category, key_: category in ("changed", "added", "removed") and (key_.startswith("hw:" + cpu) or key_.startswith("inv:" + cpu.rsplit("/items/", 1)[0]))
    run.apply("set %s's InterfaceOperatingMode to %s" % (interface.rsplit("/", 1)[1], value), "hardware_attributes", {"hardwarePath": interface, "attributes": {"InterfaceOperatingMode": value}},
              {"effects": [{"hardwarePath": interface}], "attributeChanges": [{"name": "InterfaceOperatingMode", "after": value}]},
              changed=[key], rows={key: {"attributes": {"InterfaceOperatingMode": value}}}, tolerated=coupled, restore=restore)


def phase_a(run, cpu, interface):
    refuse_export(run, "an invented hardware path", {"hardwarePath": interface + "X", "designation": DESIGNATION}, ["HardwareNotFound"])
    refuse_export(run, "the CPU display, which has no GsdExportProvider", {"hardwarePath": cpu + "/items/CPU%20display_1", "designation": DESIGNATION}, ["UnsupportedCapability"])
    run.h.report["exportableBefore"] = exportable(run)
    refuse_export(run, "the interface before it is an I-device", {"hardwarePath": interface, "designation": DESIGNATION}, ["UnsupportedCapability", "ExportFailed", "EngineeringError"])
    run.apply("put ScratchPLC on GsdNet", "network_configure", {"hardwarePath": interface, "nodeName": "X1", "subnetName": "GsdNet"}, changed=["hw:" + interface])
    run.refuse("hardware_attributes", "two operating modes in one value", {"hardwarePath": interface, "attributes": {"InterfaceOperatingMode": "IoController, IoDevice"}}, "InvalidRequest")
    run.refuse("hardware_attributes", "an unknown operating mode", {"hardwarePath": interface, "attributes": {"InterfaceOperatingMode": "SharedDevice"}}, "InvalidRequest")
    mode(run, interface, "IoDevice")
    paths = exportable(run)
    run.h.report["exportableAfter"] = paths
    require(len(paths) == 1, "Expected one item with GsdExportProvider on the I-device: %s" % paths)
    refuse_export(run, "a missing designation", {"hardwarePath": paths[0]}, ["InvalidRequest"])
    return paths[0], export(run, "of the I-device", paths[0])


def reopen(run, source, first):
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
    again = export(run, "after the fresh reopen", source)
    h.stage("the reopened project exports a GSDML of the same name", lambda: require(again["gsdml"] == first["gsdml"], "GSDML names differ: %s / %s" % (first["gsdml"], again["gsdml"])) or again["gsdml"])


def phase_b(run, interface, source, first):
    prepared = run.prepare("switch the interface back to IoController", "hardware_attributes", {"hardwarePath": interface, "attributes": {"InterfaceOperatingMode": "IoController"}},
                           {"effects": [{"hardwarePath": interface}], "attributeChanges": [{"name": "InterfaceOperatingMode", "after": "IoController"}]})
    cpu = interface.rsplit("/items/", 1)[0]
    coupled = lambda category, key: key.startswith("hw:" + cpu) or key.startswith("inv:" + cpu.rsplit("/items/", 1)[0])
    run.finish("switch the interface back to IoController", "hardware_attributes", prepared, changed=["hw:" + interface], tolerated=coupled,
               rows={"hw:" + interface: {"attributes": {"InterfaceOperatingMode": "IoController"}}})
    refuse_export(run, "the interface switched back to IoController", {"hardwarePath": source, "designation": DESIGNATION}, ["UnsupportedCapability", "ExportFailed", "EngineeringError"])
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
    gaps = coverage_gaps(run.coverage, ("hardware_attributes",))
    require(not gaps, "hardware_attributes lacks applied, persisted, refused or restored evidence: %s" % gaps)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "gs_", "X10 hardware_gsd_export on an I-device CPU 1511-1 PN in one owned scratch", LIMITATIONS, []).run(body)
