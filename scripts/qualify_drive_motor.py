"""Qualify drive_motor_configure natively on a SINAMICS G220 in one owned scratch CPU 1511-1 PN.

The newest installed G220 is added and compiled with save; drive_observe reads motor data set 0. A change sets the motor
type InductionMotor and a 400 V / 50 Hz induction motor's nameplate (voltage, current, power, frequency, speed, cos phi)
with each entry's expected current value, applied with checkpoint, device compile (the drive listed under notCompiled)
and save, confirmed by change_status and read back through drive_observe. Typed refusals (a stale expected value, an
unknown entry, an unsupported motor type, the other drive object handle) change nothing. The change is then restored from
its checkpoint and drive_observe must show the original entries again.
"""
from __future__ import annotations

import argparse
from pathlib import Path

from library_native_harness import Harness, require
from qualify_drives import DEVICE, observe

NAMEPLATE = {"p304": 400, "p305": 2.6, "p307": 1.1, "p310": 50, "p311": 1440, "p308": 0.82}


def add_g220(h):
    rows = [e for e in h.tool("hardware_catalog_search", {"filter": "G220", "maxItems": 200})["entries"] if e.get("typeIdentifier", "").startswith("OrderNumber:")]
    require(rows, "No SINAMICS G220 in the installed catalog")
    row = sorted(rows, key=lambda e: e["typeIdentifier"])[-1]
    h.report["drive"] = row
    h.apply("hardware_add", {"typeIdentifier": row["typeIdentifier"], "deviceName": DEVICE, "itemName": DEVICE + "Item"})


def motor(h):
    drives = observe(h, motorDataSets=[0], parameterNumbers=[1])["drives"]
    require(len(drives) == 1 and drives[0]["driveObjects"], "Expected one observed G220 with drive objects: %s" % drives)
    objects = [o for o in drives[0]["driveObjects"] if any(c["status"] == "observed" for c in o["snapshot"].get("motorConfigurations", []))]
    require(objects, "No drive object reads motor data set 0")
    obj = objects[0]
    ref = {"hardwarePath": drives[0]["hardwarePath"], **({"driveObjectIndex": obj["driveObjectIndex"]} if "driveObjectIndex" in obj else {"driveObjectNumber": obj["driveObjectNumber"]})}
    config = next(c for c in obj["snapshot"]["motorConfigurations"] if c["number"] == 0)
    return ref, {e["name"]: e["value"] for e in config["entries"]}


def body(h):
    plc = h.owned_scratch("drive motor")
    add_g220(h)
    h.apply("project_compile", {})
    ref, before = h.stage("read motor data set 0", lambda: motor(h))
    h.report["before"] = before
    require(all(name in before for name in NAMEPLATE), "Motor data set 0 lacks a nameplate entry: %s" % sorted(before))
    entries = [{"name": n, "expected": before[n], "value": v} for n, v in NAMEPLATE.items()]
    payload = {**ref, "driveDataSet": 0, "motorType": "InductionMotor", "entries": entries}

    h.refuse("refuse a stale expected value", "drive_motor_configure", {**payload, "entries": [{**entries[0], "expected": "999"}]}, "ExpectedValueMismatch")
    h.refuse("refuse an unknown entry", "drive_motor_configure", {**payload, "entries": [{"name": "p9999", "expected": "0", "value": 1}]}, "ParameterNotFound")
    h.refuse("refuse a Siemens code-number motor type", "drive_motor_configure", {**payload, "motorType": "InductionMotor1LE1"}, "UnsupportedCapability")
    other = {"driveObjectNumber": 1} if "driveObjectIndex" in ref else {"driveObjectIndex": 0}
    h.refuse("refuse the other drive object handle", "drive_motor_configure",
             {**{k: v for k, v in payload.items() if k not in ("driveObjectIndex", "driveObjectNumber")}, **other}, "DriveObjectHandleMismatch")
    require(h.stage("motor data set 0 unchanged by the refusals", lambda: motor(h))[1] == before, "A refusal changed the motor configuration")

    prepared = h.prepare("drive_motor_configure", payload)
    applied = h.apply_prepared("drive_motor_configure", prepared)
    status = h.tool("change_status", {"changeId": prepared["changeId"]})
    require(status["state"] == "applied", "change_status: %s" % status["state"])
    readback = next(r for r in applied["readbacks"] if r["role"] == "drive-motor-readback")["motor"]
    h.report["applied"] = {"readback": readback, "stages": applied["stages"], "notCompiled": [x.get("target") for x in (applied.get("compile") or {}).get("notCompiled") or []]}
    after = h.stage("drive_observe shows the projected motor", lambda: motor(h))[1]
    h.report["after"] = after
    for name, value in NAMEPLATE.items():
        require(float(after[name]) == float(value), "%s reads %s, not %s" % (name, after[name], value))

    restored = h.stage("restore the motor change from its checkpoint", lambda: h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
    h.report["restored"] = {"state": restored["state"], "stages": restored["stages"]}
    h.report["afterRestore"] = h.stage("drive_observe after restore", lambda: motor(h))[1]
    require(h.report["afterRestore"] == before, "The restored motor configuration differs from the original")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "dm_", "D10 drive_motor_configure on a SINAMICS G220 in one owned scratch CPU 1511-1 PN", [
        "One G220 (the newest installed catalog entry), not commissioned beyond this kind; motor data set 0 only.",
        "Engineering only; no motor identification, download or online access."], []).run(body)
