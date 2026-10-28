"""Qualify drive_observe (D10) natively in one owned scratch CPU 1511-1 PN with an added SINAMICS G120C PN.

The drive is added without a compile: Startdrive drives expose no device compile service, which the worker's device
compile requires for every device, so no compiled change can run in a project that holds a drive. drive_observe must list
exactly that drive by an exact hardware path with drive objects DO<n>, return the same objects for that path, filter
parameters by number, report truncation at a one-item bound, leave the project unchanged and refuse a non-drive item, an
unknown path and an invalid offset.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import re

from library_native_harness import Harness, require
from qualify_hardware_changes import Run as HardwareRun

DEVICE = "TiaMcpDrive"
CRITERIA = {
    "AC-identity": "drive_observe lists exactly the added drive by its exact hardware path with drive objects DO<n>; a targeted read returns the same objects; a non-drive item, an unknown path and an invalid offset are refused.",
    "AC-completeness": "Complete scope without warnings for the drive, a one-item bound reports truncation, and a parameter-number filter returns only those numbers.",
    "AC-evidence": "This report: binaries, tool calls, step results, the observed drive objects and limitations.",
}
LIMITATIONS = [
    "One SINAMICS G120C PN added to an owned scratch without a compile; motor and encoder data sets, BICO sources and telegram addresses are recorded, not checked against Startdrive's UI.",
    "drive_parameter_set and drive_telegram_set cannot apply in a project holding a drive: their device compile requires a compile service on every device, and Startdrive drives expose none.",
    "Offline engineering values only; no online values, upload, download or RAM-to-ROM.",
]


def body(h):
    run = HardwareRun(h)
    h.report["criteria"] = CRITERIA
    h.owned_scratch("drive observe")
    status = h.tool("tia_session_status")["projects"][0]
    run.project, run.base = status["path"], "/projects/%s/devices/" % status["name"]
    entries = h.tool("hardware_catalog_search", {"filter": "G120C PN", "maxItems": 200})["entries"]
    rows = sorted((e for e in entries if " PN" in (e.get("typeName") or "")),
                  key=lambda e: [int(x) for x in e["typeIdentifier"].rsplit("/", 1)[1].split(".") if x.isdigit()])
    require(rows, "No SINAMICS G120C PN in the installed catalog")
    payload = {"typeIdentifier": rows[-1]["typeIdentifier"], "deviceName": DEVICE, "itemName": DEVICE + "Item"}
    prepared = h.stage("prepare hardware_add " + DEVICE, lambda: h.tool("change_prepare", {"kind": "hardware_add", "payload": payload}))
    # hardware_add may defer compile and save, which a project holding a drive needs (see LIMITATIONS).
    applied = h.stage("apply hardware_add without compile", lambda: h.tool("change_apply", {**h.apply_args(prepared), "compile": False, "save": False}))
    require(applied["state"] == "applied", "The drive was not added")
    h.report["drive"] = rows[-1]
    before = run.state()
    def listing():
        result = h.tool("drive_observe", {"maxItems": 2000})
        drives = [d for d in result["drives"] if d["hardwarePath"].startswith(run.path(DEVICE) + "/")]
        require(len(drives) == 1 and drives[0]["driveObjects"], "Expected one listed drive with drive objects: %s" % [d["hardwarePath"] for d in result["drives"]])
        require(all(re.fullmatch(r"DO\d+", o["snapshot"]["driveId"] if "snapshot" in o else o.get("driveId", "")) for o in drives[0]["driveObjects"]),
                "Drive objects are not identified as DO<n>")
        require(result["completeness"]["scopeComplete"] is True and result["completeness"]["truncated"] is False, "The drive listing is incomplete: %s" % result["completeness"])
        return drives[0]
    drive = h.stage("drive_observe of every drive", listing)
    path = drive["hardwarePath"]
    ids = lambda d: sorted((o["snapshot"]["driveId"] if "snapshot" in o else o.get("driveId")) for o in d["driveObjects"])
    def targeted():
        result = h.tool("drive_observe", {"hardwarePath": path, "maxItems": 2000})
        require([d["hardwarePath"] for d in result["drives"]] == [path] and ids(result["drives"][0]) == ids(drive), "The targeted read returned another drive")
        return ids(result["drives"][0])
    h.stage("drive_observe of the exact drive", targeted)
    def filtered():
        result = h.tool("drive_observe", {"hardwarePath": path, "maxItems": 2000, "parameterNumbers": [1082, 1120]})
        numbers = {p.get("number") for o in result["drives"][0]["driveObjects"] for p in (o.get("snapshot", o).get("parameters") or [])}
        require(numbers and numbers <= {1082, 1120}, "The parameter filter returned other numbers: %s" % sorted(numbers)[:20])
        return sorted(numbers)
    h.stage("drive_observe filtered by parameter number", filtered)
    def bounded():
        result = h.tool("drive_observe", {"hardwarePath": path, "maxItems": 1})
        require(result["completeness"]["truncated"] is True, "A one-item bound must report truncation: %s" % result["completeness"])
        return result["completeness"]
    h.stage("drive_observe at a one-item bound", bounded)
    for label, arguments, code in (("a CPU item", {"hardwarePath": run.path("ScratchStation", "items", "ScratchPLC")}, "UnsupportedCapability"),
                                   ("an unknown path", {"hardwarePath": path + "Missing"}, "HardwareNotFound"),
                                   ("a negative parameter offset", {"hardwarePath": path, "parameterOffset": -1}, "InvalidRequest")):
        def refused(arguments=arguments, code=code):
            error = h.call_error("drive_observe", arguments)
            require(error.get("code") == code, "Expected %s: %s" % (code, error))
            return error
        h.stage("refuse drive_observe: " + label, refused)
    h.stage("drive_observe left the project unchanged", lambda: require(run.state() == before, "drive_observe changed the project") or 1)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "dr_", "D10 drive_observe in one owned scratch CPU 1511-1 PN with a SINAMICS G120C PN", LIMITATIONS, []).run(body)
