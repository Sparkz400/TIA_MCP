"""Qualify change_apply's first-motion-object rule (change.to_create, 1 November audit fix) in one owned scratch CPU 1511-1 PN V4.1.

The scratch PLC holds no technology object. to_create of TO_PositioningAxis 10.0 must preview firstMotionObject with its
warning, and change_apply with compile=false must refuse it with CompileRequired before the checkpoint: the change stays
prepared with no stage and no recovery archive, no admission stays open, and technology_read and the program inventory
are unchanged. The same axis with parameters {"VirtualAxis.Mode": 1} then applies with checkpoint, compile and save. A
following TO_ExternalEncoder 10.0 is no longer the first motion object: its preview has neither, it applies with
compile=false and save=false, and change_restore into a new directory brings back the project with the axis and without
the encoder. In that recovered project to_delete of the axis must refuse compile=false (InvalidRequest, the delete and
rename rule of the 31 October audit) before its checkpoint, with the axis kept.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import re

from library_native_harness import Harness, require

CPU = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"
AXIS, ENCODER = ("FmAxis", "TO_PositioningAxis", "10.0"), ("FmEncoder", "TO_ExternalEncoder", "10.0")
WARNING = "first motion technology object"
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1: TO_PositioningAxis 10.0 (refused uncompiled, then created virtual with VirtualAxis.Mode 1) "
    "and TO_ExternalEncoder 10.0. The rule's other motion families and other CPUs are checked offline only.",
    "Engineering only: nothing is downloaded, no axis moves and the encoder stays unconnected.",
    "to_delete's compile=false refusal is checked on the restored axis; the delete itself is not applied here.",
]


def created(name, family, version, **extra):
    return {"name": name, "typeIdentifier": family, "version": version, **extra}


def body(h):
    plc = h.owned_scratch("first motion")
    require(h.scratch_cpu == CPU, "The scratch CPU differs from %s: %s" % (CPU, h.scratch_cpu))

    def state():
        objects = h.tool("technology_read", {"plcPath": plc, "maxItems": 500})["technologyObjects"]
        # Versions as dotted digits, so 10.0 compares equal however technology_read spells it.
        return {"technology": sorted([o["name"], o.get("systemLibraryType"), ".".join(re.findall(r"\d+", str(o.get("systemLibraryVersion"))))] for o in objects),
                "program": h.inventory()}

    def prepare(label, payload, first):
        def run():
            prepared = h.tool("change_prepare", {"kind": "to_create", "payload": payload, "plcPath": plc})
            preview = prepared["preview"]
            warned = [w for w in preview.get("warnings", []) if WARNING in w]
            if first:
                require(preview.get("firstMotionObject") is True and len(warned) == 1, "The preview does not flag the first motion object: %s" % preview)
            else:
                require("firstMotionObject" not in preview and not warned, "A later motion object is flagged as the first: %s" % preview)
            return prepared
        return h.stage("prepare " + label, run)

    baseline = h.stage("baseline: no technology object", state)
    require(baseline["technology"] == [], "The scratch PLC already holds technology objects: %s" % baseline["technology"])
    uncompiled = prepare("the first motion object TO_PositioningAxis 10.0", created(*AXIS), True)

    def refused():
        error = h.call_error("change_apply", {**h.apply_args(uncompiled), "compile": False, "save": False})
        require(error.get("code") == "CompileRequired", "Expected CompileRequired, observed %s: %s" % (error.get("code"), error.get("message")))
        status = h.tool("change_status", {"changeId": uncompiled["changeId"]})
        require(status.get("state") == "prepared" and not status.get("stages"), "The refused change left the prepared state: %s" % {k: status.get(k) for k in ("state", "stages")})
        archives = [str(p) for p in h.directory.glob("**/change-%s/before.zap21" % uncompiled["changeId"])]
        require(not archives and not h.unresolved_admissions(), "The refusal left a checkpoint or an open admission: %s %s" % (archives, h.unresolved_admissions()))
        require(state() == baseline, "The refused change altered the technology objects or the program")
        return {"error": error, "status": {k: status.get(k) for k in ("state", "stages")}}
    h.report["refusal"] = h.stage("refuse change_apply compile=false: CompileRequired before the checkpoint", refused)

    axis = prepare("the first motion object as a virtual axis", created(*AXIS, parameters={"VirtualAxis.Mode": 1}), True)
    h.apply_prepared("to_create", axis)
    with_axis = h.stage("technology_read shows the compiled axis", state)
    require(with_axis["technology"] == [list(AXIS)], "Unexpected technology objects after the axis: %s" % with_axis["technology"])

    encoder = prepare("a later motion object TO_ExternalEncoder 10.0", created(*ENCODER), False)
    applied = h.stage("apply TO_ExternalEncoder without compile", lambda: h.tool("change_apply", {**h.apply_args(encoder), "compile": False, "save": False}))
    require(applied["state"] == "applied", "The encoder did not apply: %s" % applied.get("state"))
    both = h.stage("technology_read shows the axis and the encoder", state)
    require(both["technology"] == sorted([list(AXIS), list(ENCODER)]), "Unexpected technology objects after the encoder: %s" % both["technology"])

    def restored():
        result = h.tool("change_restore", {"changeId": encoder["changeId"], "previewHash": encoder["previewHash"]})
        require(result["state"] == "restored" and "checkpoint_retrieved_to_new_directory" in result["stages"], "The encoder change was not restored: %s" % result)
        require(state() == with_axis, "The restored project differs from the project with the axis only")
        return {k: result.get(k) for k in ("state", "stages", "recoveredProject")}
    h.report["restore"] = h.stage("change_restore of the encoder into a new directory", restored)
    path = plc + "/technology/" + AXIS[0]
    delete = h.stage("prepare to_delete of the axis", lambda: h.tool("change_prepare", {"kind": "to_delete", "payload": {"technologyPath": path}, "plcPath": plc}))

    def delete_refused():
        error = h.call_error("change_apply", {**h.apply_args(delete), "compile": False, "save": False})
        require(error.get("code") == "InvalidRequest" and "compile=true" in (error.get("message") or ""),
                "Expected the compile=true refusal, observed %s: %s" % (error.get("code"), error.get("message")))
        status = h.tool("change_status", {"changeId": delete["changeId"]})
        archives = [str(p) for p in h.directory.glob("**/change-%s/before.zap21" % delete["changeId"])]
        require(status.get("state") == "prepared" and not status.get("stages") and not archives, "The refused delete left the prepared state or wrote a checkpoint")
        require(state() == with_axis, "The refused delete changed the technology objects or the program")
        return {"error": error, "status": {k: status.get(k) for k in ("state", "stages")}}
    h.report["deleteRefusal"] = h.stage("refuse to_delete with compile=false before the checkpoint", delete_refused)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "fm_", "change.to_create first motion object: CompileRequired without compile, then a compiled virtual axis and a later encoder", LIMITATIONS, []).run(body)
