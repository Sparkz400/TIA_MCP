"""Probe the D10 drive kinds natively in one owned scratch CPU 1511-1 PN with an added SINAMICS G120C PN.

The drive is added without compile or save, and drive_observe records what Startdrive reads from the new drive (on
28 October 2026 it could not read DriveObject.DriveObjectNumber). A project_compile with save then compiles the PLC
station and lists the drive under notCompiled (Startdrive drives expose no device compile service), and drive_observe
runs again. When the drive objects read, drive_parameter_set and drive_telegram_set each apply with checkpoint, device
compile (drive listed as not compiled) and save, are read back through drive_observe, refuse typed and are restored from
their checkpoints. Whatever V21 refuses is recorded under boundaries with its exact error.
"""
from __future__ import annotations

import argparse
from pathlib import Path

from compilation_evidence import require_clean_compile
from library_native_harness import Harness, require

DEVICE = "TiaMcpDrive"
LIMITATIONS = [
    "One SINAMICS G120C PN added to an owned scratch CPU 1511-1 PN V4.1, not connected to the CPU's IO system; motor, encoder, "
    "BICO and telegram addresses are recorded, not checked against Startdrive's UI.",
    "One scalar parameter (p1120 ramp-up time, or the first scalar row of p1082) and the main telegram number are changed; "
    "safety, commissioning, BICO, supplementary and additional telegrams are refused or not exercised.",
    "The drive itself is never compiled: Startdrive exposes no device compile service, so every device compile lists it under notCompiled.",
    "Offline engineering values only; no online values, upload, download or RAM-to-ROM.",
]


def outcome(h, name, arguments):
    """A tool call whose refusal is recorded, not raised."""
    result = h.client.request("tools/call", {"name": name, "arguments": arguments})
    content = result.get("structuredContent") or {}
    return (None, content) if not result.get("isError") else ((content.get("error") or {"text": result.get("content")}), content)


def observe(h, path=None, **arguments):
    error, result = outcome(h, "drive_observe", {"maxItems": 2000, **({"hardwarePath": path} if path else {}), **arguments})
    require(error is None, "drive_observe failed: %s" % error)
    drives = [d for d in result["drives"] if "/devices/%s/" % DEVICE in d["hardwarePath"] + "/"]
    return {"drives": drives, "warnings": result["warnings"], "completeness": result["completeness"]}


def drive_receipt(applied):
    """The device compile compiled the PLC station and listed exactly the drive under notCompiled."""
    receipt = applied["compile"]
    skipped = receipt.get("notCompiled") or []
    require(len(skipped) == 1 and skipped[0]["target"] == DEVICE, "Expected only the drive under notCompiled: %s" % skipped)
    require_clean_compile(receipt, not_compiled=[skipped[0]["targetPath"]])
    return {"targets": [t["targetPath"] for t in receipt["targets"]], "notCompiled": skipped}


def scalar(rows):
    return next((p for p in rows if (p.get("value") or {}).get("kind") == "scalar" and "Single" in (p["value"].get("type") or "")), None)


def add_drive(h):
    """The newest installed SINAMICS G120C PN as device DEVICE, added without compile or save."""
    entries = h.tool("hardware_catalog_search", {"filter": "G120C PN", "maxItems": 200})["entries"]
    rows = sorted((e for e in entries if " PN" in (e.get("typeName") or "")),
                  key=lambda e: [int(x) for x in e["typeIdentifier"].rsplit("/", 1)[1].split(".") if x.isdigit()])
    require(rows, "No SINAMICS G120C PN in the installed catalog")
    h.report["drive"] = rows[-1]
    prepared = h.prepare("hardware_add", {"typeIdentifier": rows[-1]["typeIdentifier"], "deviceName": DEVICE, "itemName": DEVICE + "Item"})
    applied = h.stage("apply hardware_add without compile or save", lambda: h.tool("change_apply", {**h.apply_args(prepared), "compile": False, "save": False}))
    require(applied["state"] == "applied", "The drive was not added")


def body(h):
    h.owned_scratch("drives")
    boundaries = h.report["boundaries"] = {}
    add_drive(h)
    h.report["unsavedObserve"] = h.stage("drive_observe of the new unsaved drive", lambda: observe(h))

    compiled = h.apply("project_compile", {})
    h.report["projectCompile"] = h.stage("project_compile lists the drive as not compiled", lambda: drive_receipt(compiled))
    observed = h.report["savedObserve"] = h.stage("drive_observe after compile and save", lambda: observe(h))
    require(len(observed["drives"]) == 1, "Expected one listed drive: %s" % observed["drives"])
    drive = observed["drives"][0]
    path = drive["hardwarePath"]
    if not drive["driveObjects"]:
        boundaries["drive_observe"] = {"warnings": observed["warnings"], "completeness": observed["completeness"]}
        # Record what the change kinds answer without readable drive objects.
        for kind, payload in (("drive_parameter_set", {"hardwarePath": path, "driveObjectNumber": 1, "parameters": [{"name": "p1120", "expected": {"kind": "scalar"}, "value": 12.5}]}),
                              ("drive_telegram_set", {"hardwarePath": path, "driveObjectNumber": 1, "operation": "change", "telegramType": "MainTelegram", "telegramNumber": 20, "expectedTelegrams": []})):
            boundaries[kind] = h.stage("record the %s answer without readable drive objects" % kind, lambda kind=kind, payload=payload: outcome(h, "change_prepare", {"kind": kind, "payload": payload})[0])
        return
    require(observed["completeness"]["scopeComplete"] is True, "The drive listing is incomplete: %s" % observed["completeness"])
    number = drive["driveObjects"][0]["driveObjectNumber"]
    h.report["driveObject"] = {"hardwarePath": path, "driveObjectNumber": number}
    parameters(h, path, number, boundaries)
    telegrams(h, path, number, boundaries)


def parameter_rows(h, path, number, numbers):
    objects = [o for o in observe(h, path, parameterNumbers=numbers)["drives"][0]["driveObjects"] if o["driveObjectNumber"] == number]
    require(len(objects) == 1, "Drive object %s is not listed once" % number)
    return objects[0]["snapshot"]["parameters"]


def parameters(h, path, number, boundaries):
    row = h.stage("read p1120/p1082", lambda: scalar(parameter_rows(h, path, number, [1120, 1082])))
    require(row, "No scalar floating-point p1120/p1082 row")
    name, before = row["name"], row["value"]
    value = 12.5 if before["text"] != "12.5" else 13.5
    payload = {"hardwarePath": path, "driveObjectNumber": number, "parameters": [{"name": name, "expected": before, "value": value}]}
    kind = "drive_parameter_set"
    error, prepared = outcome(h, "change_prepare", {"kind": kind, "payload": payload})
    if error:
        boundaries[kind] = error
        return
    h.refuse("refuse a stale expected value", kind, {**payload, "parameters": [{"name": name, "expected": {**before, "text": "-1"}, "value": value}]}, "ExpectedValueMismatch")
    h.refuse("refuse an unknown parameter", kind, {**payload, "parameters": [{"name": "p99999", "expected": before, "value": value}]}, "ParameterNotFound")
    protected = [p for p in parameter_rows(h, path, number, [971]) if p["number"] == 971]
    if protected:
        h.refuse("refuse the save parameter p971", kind, {**payload, "parameters": [{"name": protected[0]["name"], "expected": protected[0]["value"], "value": 1}]}, "SafetyBoundary")
    applied = h.apply_prepared(kind, prepared)
    h.report["parameterCompile"] = h.stage("drive_parameter_set compile lists the drive as not compiled", lambda: drive_receipt(applied))
    after = h.stage("read back the parameter", lambda: next(p for p in parameter_rows(h, path, number, [row["number"]]) if p["name"] == name)["value"])
    require(after["text"] == str(value), "Parameter readback %s differs from %s" % (after, value))
    def restore():
        restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        require(restored["state"] == "restored", "Parameter restore: %s" % restored["stages"])
        back = next(p for p in parameter_rows(h, path, number, [row["number"]]) if p["name"] == name)["value"]
        require(back == before, "The restored parameter %s differs from %s" % (back, before))
        return {"stages": restored["stages"], "value": back}
    h.report["parameter"] = {"name": name, "before": before, "after": after, "restore": h.stage("restore the parameter change from its checkpoint", restore)}


def telegram_rows(h, path, number):
    objects = [o for o in observe(h, path, parameterNumbers=[922])["drives"][0]["driveObjects"] if o["driveObjectNumber"] == number]
    require(len(objects) == 1 and objects[0]["snapshot"]["telegramsComplete"], "Telegrams of drive object %s are not listed completely" % number)
    return objects[0]["snapshot"]["telegrams"]


def telegrams(h, path, number, boundaries):
    before = h.stage("read the telegrams", lambda: telegram_rows(h, path, number))
    main = [t for t in before if t["type"] == "MainTelegram"]
    require(len(main) == 1, "Expected one main telegram: %s" % before)
    target = 20 if main[0]["number"] != 20 else 1
    payload = {"hardwarePath": path, "driveObjectNumber": number, "operation": "change", "telegramType": "MainTelegram", "telegramNumber": target, "expectedTelegrams": before}
    kind = "drive_telegram_set"
    error, prepared = outcome(h, "change_prepare", {"kind": kind, "payload": payload})
    if error:
        boundaries[kind] = error
        return
    h.refuse("refuse a safety telegram", kind, {**payload, "telegramType": "SafetyTelegram"}, "SafetyBoundary")
    h.refuse("refuse stale expected telegrams", kind, {**payload, "expectedTelegrams": []}, "ExpectedValueMismatch")
    applied = h.apply_prepared(kind, prepared)
    h.report["telegramCompile"] = h.stage("drive_telegram_set compile lists the drive as not compiled", lambda: drive_receipt(applied))
    after = h.stage("read back the main telegram", lambda: telegram_rows(h, path, number))
    require([t["number"] for t in after if t["type"] == "MainTelegram"] == [target], "Main telegram readback: %s" % after)
    def restore():
        restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        require(restored["state"] == "restored", "Telegram restore: %s" % restored["stages"])
        back = telegram_rows(h, path, number)
        require(back == before, "The restored telegrams differ: %s" % back)
        return {"stages": restored["stages"]}
    h.report["telegram"] = {"before": main[0]["number"], "after": target, "readbacks": applied.get("readbacks"),
                            "restore": h.stage("restore the telegram change from its checkpoint", restore)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "dr_", "D10 drive kinds in one owned scratch CPU 1511-1 PN with a SINAMICS G120C PN", LIMITATIONS, []).run(body)
