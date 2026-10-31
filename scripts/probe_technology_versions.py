"""Probe which technology object types and versions V21 creates on each CPU of one owned scratch (technology.version_profiles).

V21 has no CPU-specific technology type/version catalog, so each matrix cell is a create-and-refuse capture: one to_create
of TvProbe with unprofiledAcknowledged, applied with checkpoint but without compile or save (an unconnected axis or
encoder never compiles), read back through technology_read and restored from its own checkpoint. After every restore the
blocks, types, tag tables and technology objects of every PLC must equal the baseline.

CPUs are installed-catalog identities recorded on this PC: the scratch CPU 1511-1 PN V4.1, a CPU 1511C-1 PN V2.6 (an S7-1500
of the V2 firmware line with onboard high-speed counters, local.catalog.20261024) and a CPU 1214C DC/DC/DC V4.7 (S7-1200,
local.cpu_memory_bytes.rerun.20261028), both added with configureScratchSecurity. Families and candidate versions follow
Siemens' V21 overview of technology objects and versions; per CPU and family the versions are tried newest first until one
is accepted. All motion objects of a CPU share one technology version, so after TO_PositioningAxis the other motion families
are tried only at its accepted version (the newest candidate when none was accepted).

Outcomes per cell: accepted (technology_read shows exactly the requested type and version), substituted (another one),
refused (the native create failed; code, message and native exception chain recorded), refusedByProfile (the embedded
profile already records the refusal), refusedAtPrepare (another typed refusal). scripts/technology_version_profiles.py
merges a curated report into contracts/technology-version-profiles.v1.json.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import re

from library_native_harness import Harness, require
from qualify_drives import outcome

NAME = "TvProbe"
# (typeIdentifier, typeName, CPU line); the first is the scratch CPU tia_create_scratch selects.
CPUS = (("OrderNumber:6ES7 511-1AL03-0AB0/V4.1", "CPU 1511-1 PN", "S7-1500"),
        ("OrderNumber:6ES7 511-1CK01-0AB0/V2.6", "CPU 1511C-1 PN", "S7-1500"),
        ("OrderNumber:6ES7 214-1AG40-0XB0/V4.7", "CPU 1214C DC/DC/DC", "S7-1200"))
PID = {"PID_Compact": ("3.0", "2.4", "2.3"), "PID_3Step": ("3.0", "2.3"), "PID_Temp": ("1.2", "1.1")}
COUNTING = {"High_Speed_Counter": ("5.0", "4.1")}
MOTION_VERSIONS = ("10.0", "9.0", "8.0", "7.0", "6.0", "5.0", "4.0")
ANCHOR = "TO_PositioningAxis"
MOTION = {"S7-1500": (ANCHOR, "TO_SpeedAxis", "TO_SynchronousAxis", "TO_ExternalEncoder", "TO_MeasuringInput", "TO_OutputCam", "TO_Cam"),
          "S7-1200": (ANCHOR, "TO_CommandTable", "TO_SpeedAxis")}
LIMITATIONS = [
    "Three CPUs of the installed V21 catalog: CPU 1511-1 PN V4.1, CPU 1511C-1 PN V2.6 and CPU 1214C DC/DC/DC V4.7. Other CPUs, "
    "firmware, F- and T-CPUs and the S7-1200 G2 are not probed; an unprofiled pair still needs unprofiledAcknowledged.",
    "A cell records whether V21 creates exactly that type and version at the PLC root; it does not compile, connect, parameterize "
    "or run the object. Children (output cams, measuring inputs under an axis) use to_child_create and are not probed here.",
    "Versions are tried newest first and the search stops at the first accepted one, so older versions a CPU might also accept "
    "stay unprofiled.",
]


def families(line):
    return (*PID, *MOTION[line], *(COUNTING if line == "S7-1500" else ()))


def candidates(family, outcomes):
    if family in PID:
        return PID[family]
    if family in COUNTING:
        return COUNTING[family]
    if family == ANCHOR:
        return MOTION_VERSIONS
    shared = [v for v in MOTION_VERSIONS if outcomes.get((ANCHOR, v)) == "accepted"]
    return tuple(shared[:1]) or MOTION_VERSIONS[:1]


def next_cell(line, outcomes):
    """The next (family, version) to probe on a CPU of this line given its outcomes so far, or None when its matrix is done."""
    for family in families(line):
        versions = candidates(family, outcomes)
        if any(outcomes.get((family, v)) == "accepted" for v in versions):
            continue
        for version in versions:
            if (family, version) not in outcomes:
                return family, version
    return None


def same_version(first, second):
    """Version equality as System.Version compares two dotted parts or more."""
    parts = [tuple(int(x) for x in re.findall(r"\d+", str(v or ""))) for v in (first, second)]
    return bool(parts[0]) and parts[0] == parts[1]


class Run:
    def __init__(self, h):
        self.h, self.cpus, self.cells, self.baseline = h, [], [], None
        h.report.update(cpus=self.cpus, cells=self.cells)

    def plcs(self):
        return [p["path"] for p in self.h.tool("project_inventory", {"maxItems": 2000})["plcs"]]

    def state(self):
        """Blocks, types and tag tables of every PLC plus each PLC's technology objects."""
        technology = {plc: sorted((o["path"], o.get("systemLibraryType"), str(o.get("systemLibraryVersion")))
                                  for o in self.h.tool("technology_read", {"plcPath": plc, "maxItems": 500})["technologyObjects"]) for plc in self.plcs()}
        return {"program": self.h.inventory(), "technology": technology}

    def restore(self, label, prepared):
        h = self.h
        restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        require(restored["state"] == "restored" and "checkpoint_retrieved_to_new_directory" in restored["stages"], "Checkpoint recovery of %s failed: %s" % (label, restored))
        state = self.state()
        require(state == self.baseline, "The restored project differs from the baseline after %s" % label)
        return restored["state"]

    def add_cpu(self, identity, name, line):
        """An extra CPU from the exact installed catalog entry, added with checkpoint, compile and save; absent or refused CPUs are recorded."""
        h, row = self.h, {"typeIdentifier": identity, "typeName": name, "line": line}
        self.cpus.append(row)
        entries = [e for e in h.tool("hardware_catalog_search", {"filter": name.replace("CPU ", ""), "maxItems": 500})["entries"] if e.get("typeIdentifier") == identity]
        if not entries:
            row["status"] = "notInCatalog"
            return
        station = "Tv" + re.sub(r"\W", "", name)[3:]
        payload = {"typeIdentifier": identity, "deviceName": station, "itemName": station + "Plc", "configureScratchSecurity": True}
        def add():
            error, prepared = outcome(h, "change_prepare", {"kind": "hardware_add", "payload": payload})
            if error:
                return {"status": "refusedAtPrepare", "error": error}
            error, applied = outcome(h, "change_apply", h.apply_args(prepared))
            if error:
                if h.unresolved_admissions():
                    h.reconcile_admission("release the failed add of " + name)
                restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
                return {"status": "failedAtApply", "error": {k: error.get(k) for k in ("code", "message")}, "restored": restored["state"]}
            return {"status": "added", "stages": applied["stages"]}
        row.update(h.stage("add " + name, add))
        if row["status"] == "added":
            row["plcPath"] = next(p for p in self.plcs() if "/devices/%s/" % station in p + "/")

    def cell(self, cpu, family, version):
        h = self.h
        label = "%s %s on %s" % (family, version, cpu["typeName"])
        row = {"cpuTypeIdentifier": cpu["typeIdentifier"], "cpuTypeName": cpu["typeName"], "typeIdentifier": family, "version": version}
        self.cells.append(row)
        def probe():
            payload = {"name": NAME, "typeIdentifier": family, "version": version, "unprofiledAcknowledged": True}
            error, prepared = outcome(h, "change_prepare", {"kind": "to_create", "payload": payload, "plcPath": cpu["plcPath"]})
            if error:
                return {"outcome": "refusedByProfile" if error.get("code") == "TechnologyVersionRefused" else "refusedAtPrepare", "error": error}
            error, applied = outcome(h, "change_apply", {**h.apply_args(prepared), "compile": False, "save": False})
            if error:
                if h.unresolved_admissions():
                    h.reconcile_admission("release the refused " + label)
                status = h.tool("change_status", {"changeId": prepared["changeId"]})
                result = {"outcome": "refused", "error": {k: error.get(k) for k in ("code", "message")},
                          "native": [r for r in status.get("readbacks") or [] if r.get("role") in ("native-exception", "failure-details")]}
            else:
                created = [o for o in h.tool("technology_read", {"plcPath": cpu["plcPath"], "maxItems": 500})["technologyObjects"] if o["name"] == NAME]
                require(len(created) == 1, "Expected one created %s: %s" % (NAME, created))
                observed = {"typeIdentifier": created[0].get("systemLibraryType"), "version": str(created[0].get("systemLibraryVersion"))}
                exact = observed["typeIdentifier"] == family and same_version(observed["version"], version)
                result = {"outcome": "accepted" if exact else "substituted", "observed": observed, "stages": applied["stages"]}
            result["restored"] = self.restore(label, prepared)
            return result
        row.update(h.stage("probe " + label, probe))
        return row["outcome"]


def body(h):
    run = Run(h)
    h.owned_scratch("technology versions")
    require(h.scratch_cpu == CPUS[0][0], "The scratch CPU differs from the recorded catalog identity: %s" % h.scratch_cpu)
    run.cpus.append({"typeIdentifier": CPUS[0][0], "typeName": CPUS[0][1], "line": CPUS[0][2], "status": "scratch", "plcPath": run.plcs()[0]})
    for identity, name, line in CPUS[1:]:
        run.add_cpu(identity, name, line)
    run.baseline = h.stage("baseline program and technology state", run.state)
    for cpu in (c for c in run.cpus if c.get("plcPath")):
        outcomes = {}
        while (cell := next_cell(cpu["line"], outcomes)) is not None:
            outcomes[cell] = run.cell(cpu, *cell)
        cpu["outcomes"] = {"%s %s" % key: value for key, value in outcomes.items()}
    h.report["summary"] = {c["typeName"]: c.get("outcomes", c.get("status")) for c in run.cpus}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "tv_", "technology.version_profiles create-and-refuse matrix in one owned scratch", LIMITATIONS, []).run(body)
