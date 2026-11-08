"""Qualify drive_encoder_configure natively on a SINAMICS G220 in one owned scratch CPU 1511-1 PN.

The newest installed G220 is added and compiled; drive_observe finds the drive object with a HardwareProjection and
reads encoder 0, which has no configuration yet. Typed refusals (encoder and entries together, a DRIVE-CLiQ encoder,
entries on an encoder without a configuration, an unknown enum value) change nothing. Each candidate encoder in
ENCODERS is then set until Startdrive accepts one; a refused one is restored from its checkpoint and recorded. With an
accepted encoder, drive_observe reads its entries, a second change sets the pulses per revolution with its expected
value, is read back and restored, and the encoder change is restored so encoder 0 reads no configuration again.
"""
from __future__ import annotations

import argparse
from pathlib import Path

from library_native_harness import Harness, require
from qualify_drive_motor import add_g220
from qualify_drives import observe

ENCODERS = [{"interface": "HTL", "type": "HTL", "absoluteIncremental": "Incremental", "rotaryLinear": "Rotary"},
            {"interface": "Terminal", "type": "HTLTTL", "absoluteIncremental": "Incremental", "rotaryLinear": "Rotary"},
            {"interface": "DSub", "type": "HTLTTL", "absoluteIncremental": "Incremental", "rotaryLinear": "Rotary"},
            {"interface": "SSI", "type": "SSIProtocoll", "absoluteIncremental": "Absolute", "rotaryLinear": "Rotary"}]
PULSES = "p408"  # rotary encoder pulse number


def encoder(h):
    """(drive object reference, encoder 0 entries or None) for the one drive object whose motor data set 0 reads."""
    drives = observe(h, encoderNumbers=[0], motorDataSets=[0], parameterNumbers=[1])["drives"]
    found = [(d, o) for d in drives for o in d["driveObjects"]
             if any(c["status"] == "observed" for c in (o.get("motorConfigurations") or o["snapshot"].get("motorConfigurations") or []))]
    require(len(found) == 1, "Expected exactly one drive object with a readable HardwareProjection")
    drive, obj = found[0]
    ref = {"hardwarePath": drive["hardwarePath"], **({"driveObjectIndex": obj["driveObjectIndex"]} if "driveObjectIndex" in obj else {"driveObjectNumber": obj["driveObjectNumber"]})}
    config = next(c for c in (obj.get("encoderConfigurations") or obj["snapshot"].get("encoderConfigurations") or []) if c["number"] == 0)
    return ref, ({e["name"]: e["value"] for e in config["entries"]} if config["status"] == "observed" else None)


def attempt(h, payload):
    prepared = h.tool("change_prepare", {"kind": "drive_encoder_configure", "payload": payload})
    result = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
    if not result.get("isError"):
        return prepared, {"applied": True}
    error = (result.get("structuredContent") or {}).get("error") or {}
    restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
    return None, {"applied": False, "error": {k: error.get(k) for k in ("code", "message")}, "restored": restored["state"]}


def body(h):
    h.owned_scratch("drive encoder")
    add_g220(h)
    h.apply("project_compile", {})
    ref, before = h.stage("read encoder 0", lambda: encoder(h))
    h.report["before"] = before
    require(before is None, "Encoder 0 of the fresh G220 already has a configuration: %s" % before)
    base = {**ref, "encoderNumber": 0}
    h.refuse("refuse encoder and entries together", "drive_encoder_configure",
             {**base, "encoder": ENCODERS[0], "entries": [{"name": PULSES, "expected": "0", "value": "1"}]}, "InvalidRequest")
    h.refuse("refuse a DRIVE-CLiQ encoder", "drive_encoder_configure",
             {**base, "encoder": {**ENCODERS[0], "interface": "DriveCliQ", "type": "DriveCliQ"}}, "UnsupportedCapability")
    h.refuse("refuse an unknown encoder type", "drive_encoder_configure", {**base, "encoder": {**ENCODERS[0], "type": "Optical"}}, "InvalidRequest")
    h.refuse("refuse entries on an encoder without a configuration", "drive_encoder_configure",
             {**base, "entries": [{"name": PULSES, "expected": "0", "value": "1"}]}, "UnsupportedCapability")

    attempts, accepted = [], None
    for choice in ENCODERS:
        prepared, outcome = h.stage("set encoder %s/%s" % (choice["interface"], choice["type"]), lambda c=choice: attempt(h, {**base, "encoder": c}))
        attempts.append({"encoder": choice, **outcome})
        if prepared:
            accepted = (choice, prepared)
            break
        require(h.stage("encoder 0 unchanged after the restore", lambda: encoder(h))[1] is None, "A restored encoder attempt left a configuration")
    h.report["encoderAttempts"] = attempts
    if accepted is None:
        h.report["boundary"] = "Startdrive refused every candidate encoder on this G220; recorded, no encoder projected."
        return
    choice, prepared = accepted
    entries = h.stage("drive_observe reads the new encoder", lambda: encoder(h))[1]
    require(entries, "The accepted encoder reads no configuration")
    h.report["encoderEntries"] = entries
    if PULSES in entries:
        value = "2048" if entries[PULSES] != "2048" else "1024"
        second = h.prepare("drive_encoder_configure", {**base, "entries": [{"name": PULSES, "expected": entries[PULSES], "value": value}]})
        applied = h.apply_prepared("drive_encoder_configure", second)
        h.report["entryChange"] = next(r for r in applied["readbacks"] if r["role"] == "drive-encoder-readback")["encoder"]
        require(float(h.stage("drive_observe shows the pulse number", lambda: encoder(h))[1][PULSES]) == float(value), "Pulse number not projected")
        h.stage("restore the entry change", lambda: h.tool("change_restore", {"changeId": second["changeId"], "previewHash": second["previewHash"]}))
        require(h.stage("encoder entries after the entry restore", lambda: encoder(h))[1] == entries, "The entry restore differs")
    else:
        h.report["entryChange"] = "skipped: the encoder has no %s entry" % PULSES
    restored = h.stage("restore the encoder change", lambda: h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
    h.report["restored"] = restored["state"]
    require(h.stage("encoder 0 has no configuration after restore", lambda: encoder(h))[1] is None, "The restore left an encoder")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "de_", "D10 drive_encoder_configure on a SINAMICS G220 in one owned scratch CPU 1511-1 PN", [
        "One G220 (the newest installed catalog entry); encoder 0 only; the first accepted encoder of four candidates.",
        "Engineering only; no download or online access."], []).run(body)
