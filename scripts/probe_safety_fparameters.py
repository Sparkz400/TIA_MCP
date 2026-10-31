"""Discover and round-trip every F-parameter V21 declares writable on F-I/O modules of an owned scratch F-CPU (safety.hardware).

The Host runs with --safety-fparameter-discovery, so safety_fparameter_set admits declared-writable F-parameters outside the
qualified allowlist; F-source addresses, signatures, SIL/CRC/version and F-I/O DB assignment stay refused by the worker.
The fixture is probe_safety_kinds' F-CPU 1511F-1 PN (failsafeRuntimeAccess) with an ET 200SP station carrying two F-DI 8x24VDC
HF, an F-DQ, an F-AI and an F-PM-E (server module moved to slot 8), plus ET 200MP F-DI, F-DQ and F-AI modules in the F-CPU's
central rack; a module type the catalog lacks or the rack refuses is recorded and skipped.

Discovery: safety_validation_inspect lists every F-I/O with its Failsafe_* attributes and native access mode. Per F-I/O module
type, every ReadWrite F-parameter gets a different candidate value (Boolean negated, F-destination address moved to a free one,
an integer one step toward 0 or up from 0, a real +1; other types are skipped) and is prepared alone, which records the
worker's refusals (never-written classes, expected value, address conflicts). The prepared names are then written in one
change (checkpoint, device compile with the safety program, configured-hardware preservation of every written name, save),
read back through safety_validation_inspect and restored from the checkpoint into a new directory, after which every F-I/O's
F-parameters must equal the baseline. A failing batch (native refusal, preservation failure, readback difference, or a
change of another module's F-parameters or of an unwritten writable one) is restored and split in halves until each failing
name stands alone. Read-only attributes of the written module that change with a passing batch (for example the DIP switch
setting of a moved F-destination address) are recorded as derived. scripts/safety_fparameter_allowlist.py turns the passed
names into allowlist rows. No safety login, password, acceptance or download.
"""
from __future__ import annotations

import argparse
from pathlib import Path

from library_native_harness import Harness, require
from probe_safety_kinds import DEST, STATION, catalog, change, free_destination, plug_extra, topology
from qualify_drives import outcome

ET200SP = (("F-DQ", "F-DQ", "6ES7 136-6D", "TiaMcpFdq", 3), ("F-AI", "F-AI", "6ES7 136-6A", "TiaMcpFai", 4),
           ("F-PM-E", "F-PM-E", "6ES7 136-6P", "TiaMcpFpm", 5))
CENTRAL = (("F-DI", "F-DI", "6ES7 526-1", "TiaMcpMpFdi", 2), ("F-DQ", "F-DQ", "6ES7 526-2", "TiaMcpMpFdq", 3),
           ("F-AI", "F-AI", "6ES7 536-", "TiaMcpMpFai", 4))
INTEGERS = ("System.Byte", "System.SByte", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32", "System.Int64", "System.UInt64")
LIMITATIONS = [
    "One F-CPU 1511F-1 PN with the ET 200SP and ET 200MP F-modules the installed catalog offers; one F-I/O per module type is "
    "round-tripped. Other F-I/O families, F-PM-E variants, firmware and shared/I-device F-I/O are not exercised.",
    "One candidate value per F-parameter; a passed round trip shows V21 writes, preserves, compiles and restores that value, "
    "not that every value of its range is valid. A parameter that becomes writable only after another one changes (for "
    "example a manually assigned F-monitoring time) is recorded through the access modes after the batch, not chased.",
    "The F-CPU's generated scratch user holds Full access including fail-safe (failsafeRuntimeAccess, scratch only). Nothing "
    "validates, accepts or approves the safety program; no login, password or download.",
]


def writable(row):
    return "Write" in (row.get("accessMode") or "") and row.get("valueCaptured") is True


def candidate(row, values):
    """A value different from the current one, or None for a type this probe does not write."""
    value, kind = row.get("value"), row.get("type")
    if row["name"] == DEST and type(value) is int:
        return free_destination(value, values)
    if kind == "System.Boolean" and type(value) is bool:
        return not value
    if kind in INTEGERS and type(value) is int:
        return value - 1 if value > 0 else value + 1
    if kind in ("System.Single", "System.Double") and type(value) in (int, float):
        return value + 1
    return None


def bisect(rows, attempt):
    """attempt(rows) -> (passed, detail). A failing batch splits in halves until each failing row stands alone; returns {name: outcome}."""
    passed, detail = attempt(rows)
    if passed:
        return {row["name"]: {"outcome": "passed", "batch": len(rows), **detail} for row in rows}
    if len(rows) == 1:
        return {rows[0]["name"]: {"outcome": "refused", **detail}}
    half = len(rows) // 2
    return {**bisect(rows[:half], attempt), **bisect(rows[half:], attempt)}


def unexpected(before, after, path, written):
    """Differences a passing batch may not have: another F-I/O's F-parameters, or an unwritten writable one of this F-I/O.
    Returns (unexpected, derived) as {path or name: [before, after]}; derived are read-only attributes of this F-I/O."""
    bad, derived = {}, {}
    for module in sorted(set(before) | set(after)):
        old, new = before.get(module, {}), after.get(module, {})
        for name in sorted(set(old) | set(new)):
            if name in written and module == path or old.get(name, {}).get("value") == new.get(name, {}).get("value"):
                continue
            pair = [old.get(name, {}).get("value"), new.get(name, {}).get("value")]
            if module == path and "Signature" in name:
                continue
            if module == path and not writable(old.get(name, {})):
                derived[name] = pair
            else:
                bad["%s %s" % (module, name)] = pair
    return bad, derived


class Run:
    def __init__(self, h, plc):
        self.h, self.plc, self.modules = h, plc, []
        h.report["modules"] = self.modules

    def inspect(self):
        """{F-I/O path: {name: F-parameter row}} from safety_validation_inspect."""
        inspected = self.h.tool("safety_validation_inspect", {"plcPath": self.plc, "maxItems": 5000})
        require(inspected["completeness"]["truncated"] is False, "safety_validation_inspect was truncated")
        return {m["path"]: {row["name"]: row for row in m["fParameters"]} for m in inspected["fIoModules"]}

    def values(self, state):
        return {path: {name: row.get("value") for name, row in rows.items()} for path, rows in state.items()}

    def write(self, rows):
        return {"safetyAcknowledged": True, "parameters": [{k: row[k] for k in ("hardwarePath", "name", "expected", "value")} for row in rows]}

    def attempt(self, path, rows, label):
        """One batch with checkpoint, device compile and save, read back, then restored; (passed, detail)."""
        h, before = self.h, self.baseline
        def run():
            error, prepared = outcome(h, "change_prepare", {"kind": "safety_fparameter_set", "payload": self.write(rows), "plcPath": self.plc})
            if error:
                return False, {"stage": "prepare", "error": error}
            error, applied = outcome(h, "change_apply", h.apply_args(prepared))
            detail = {}
            if error:
                if h.unresolved_admissions():
                    h.reconcile_admission("release the failed " + label)
                status = h.tool("change_status", {"changeId": prepared["changeId"]})
                detail = {"stage": "apply", "error": {k: error.get(k) for k in ("code", "message")},
                          "native": [r for r in status.get("readbacks") or [] if r.get("role") in ("native-exception", "failure-details")]}
            else:
                after = self.inspect()
                differs = {row["name"]: after[path][row["name"]].get("value") for row in rows if after[path][row["name"]].get("value") != row["value"]}
                bad, derived = unexpected(before, after, path, {row["name"] for row in rows})
                access = {name: [before[path][name].get("accessMode"), after[path][name].get("accessMode")] for name in after[path]
                          if name in before[path] and before[path][name].get("accessMode") != after[path][name].get("accessMode")}
                detail = {"stage": "readback", "stages": applied["stages"], "derived": derived, "accessModeChanges": access}
                if differs or bad:
                    detail.update(readbackDiffers=differs, unexpected=bad)
            restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
            require(restored["state"] == "restored", "Checkpoint recovery of %s failed: %s" % (label, restored))
            require(self.values(self.inspect()) == self.values(before), "The restored F-parameters differ from the baseline after " + label)
            return detail.get("stage") == "readback" and not detail.get("readbackDiffers") and not detail.get("unexpected"), detail
        return h.stage("round trip %s (%d)" % (label, len(rows)), run)

    def module(self, path, seen):
        """Prepare every declared-writable F-parameter of one F-I/O alone, then round-trip the prepared ones unless an earlier
        F-I/O of the same module type (seen) already did."""
        h = self.h
        rows = self.baseline[path]
        record = {"path": path, "declaredWritable": sorted(n for n, r in rows.items() if writable(r)), "skipped": {}, "refusedAtPrepare": {}}
        self.modules.append(record)
        candidates = []
        for name in record["declaredWritable"]:
            value = candidate(rows[name], self.values(self.baseline))
            if value is None:
                record["skipped"][name] = "type %s" % rows[name].get("type")
                continue
            row = {"hardwarePath": path, "name": name, "expected": rows[name]["value"], "value": value}
            error, prepared = outcome(h, "change_prepare", {"kind": "safety_fparameter_set", "payload": self.write([row]), "plcPath": self.plc})
            if error:
                record["refusedAtPrepare"][name] = {k: error.get(k) for k in ("code", "message")}
                continue
            change_row = prepared["preview"]["parameterChanges"][0]
            record["moduleTypeIdentifier"] = change_row["moduleTypeIdentifier"]
            candidates.append({**row, "value": change_row["after"]})
        if record.get("moduleTypeIdentifier") in seen:
            record["roundTrips"] = {}
            record["sameTypeAs"] = seen[record["moduleTypeIdentifier"]]
            return record
        if "moduleTypeIdentifier" in record:
            seen[record["moduleTypeIdentifier"]] = path
        record["roundTrips"] = bisect(candidates, lambda batch: self.attempt(path, batch, path.rsplit("/", 1)[-1])) if candidates else {}
        return record


def body(h):
    h.owned_scratch("safety F-parameter discovery")
    fcpu = h.report["fCpu"] = catalog(h, "1511F-1 PN", "1511F-1 PN", "6ES7 ")[0]
    change(h, "hardware_add", {"typeIdentifier": fcpu["typeIdentifier"], "deviceName": STATION, "itemName": "TiaMcpFPlc",
                               "configureScratchSecurity": True, "failsafeRuntimeAccess": True}, {})
    plc = next(p["path"] for p in h.tool("project_inventory", {"maxItems": 2000})["plcs"] if STATION in p["path"])
    scope = {"plcPath": plc}
    topology(h, scope, extra=ET200SP, server_slot=8)
    rack = [r for r in h.tool("project_inventory", {"maxItems": 2000})["hardware"]
            if STATION in r["path"] and r["classification"] == "None" and r["positionNumber"] == 0 and r["path"].count("/items/") == 1]
    require(len(rack) == 1, "The F-CPU station must expose exactly one central rack.")
    plug_extra(h, rack[0]["path"], CENTRAL, scope)
    run = Run(h, plc)
    run.baseline = h.stage("discover every F-I/O's F-parameters", run.inspect)
    h.report["discovery"] = {path: {name: {k: row.get(k) for k in ("accessMode", "type", "value")} for name, row in rows.items()} for path, rows in run.baseline.items()}
    seen = {}  # One F-I/O per module type: the second F-DI 8x24VDC HF repeats the first.
    for path in sorted(run.baseline):
        run.module(path, seen)
    h.report["summary"] = {m["path"]: {"moduleTypeIdentifier": m.get("moduleTypeIdentifier"), "passed": sorted(n for n, r in m["roundTrips"].items() if r["outcome"] == "passed")}
                           for m in run.modules}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    args.host_args = ("--safety-fparameter-discovery",)
    Harness(args, "fp_", "safety.hardware F-parameter discovery and round trips on an owned scratch F-CPU 1511F-1 PN", LIMITATIONS, []).run(body)
