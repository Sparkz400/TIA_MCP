"""Qualify the D13 technology-object change kinds and technology_read natively in one owned scratch CPU 1511-1 PN.

Phase A creates PID_Compact objects and a virtual positioning axis with output cams, and applies every covered kind with
checkpoint, PLC compile and save: creation, child creation, parameters, output-cam connection to a PLC tag and its
disconnection, and deletion. Each preview is asserted against the request, and a complete state (every block, type and
tag-table export, every member and group path, and per technology object its identity, native hardware connections and
native XML export) must differ from the previous one in exactly the intended keys. A fresh Portal then reopens the saved
project, which must show the identical state. Phase B applies one more change per kind from that state and restores its
checkpoint in the applying Portal; the recovered copy must equal the pre-change state. Typed refusals must change nothing.
technology_read is checked for exact selection, refusal of a missing path, bounded inventories and prefixed parameters.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re

from engineering_preservation import native_payload_hash
from library_native_harness import Harness, require
from qualification_owned_scratch import complete_inventory, snapshot
from qualify_access_changes import Run as StateRun
from qualify_plc_object_mutations import coverage_gaps, diff, view
from typed_graphical_transport import artifact_bytes

KINDS = ("to_create", "to_child_create", "to_parameters", "to_connect", "to_disconnect", "to_delete")
PID = ("PID_Compact", "3.0")
AXIS = ("TO_PositioningAxis", "10.0")
CAM = ("TO_OutputCam", "10.0")
LIMIT = "Config.OutputUpperLimit"
CRITERIA = {
    "AC-identity": "refused: typed refusals of duplicate names, invalid versions and compositions, owners without a cam container, missing objects and tags, roles a target lacks, ambiguous connection targets and unconnected slots, each leaving the complete state unchanged; technology_read refuses a missing path.",
    "AC-preview": "Every preview is asserted against the request before apply; a wrong previewHash refuses at apply.",
    "AC-preserve": "applied: every program export and member, and each technology object's identity, connections and native XML differ in exactly the intended keys.",
    "AC-native": "applied + persisted: checkpoint, PLC compile and save stages, change_status, technology_read parameter and connection readback and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the pre-change state.",
    "AC-completeness": "technology_read inventories report truncation at their bound and complete scope otherwise; prefixed parameter reads return only that prefix.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json and limitations.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1: PID_Compact 3.0, TO_PositioningAxis 10.0 in virtual mode (VirtualAxis.Mode = 1) and TO_OutputCam 10.0 children. "
    "Other TO types and versions, measuring inputs and real drives or encoders are not exercised; an unconnected TO_ExternalEncoder fails compile by design.",
    "to_connect and to_disconnect use the output-cam role with a PLC output tag; the actor, sensor, torque, encoder and measuring-input roles and hardware or "
    "channel targets are only refused here.",
    "to_parameters writes one scalar PID_Compact parameter; the readback covers that parameter, the native XML export covers the object.",
    "TIA adds the instructions and data types a technology object needs to the system groups and generates know-how protected motion OBs for an axis "
    "(recorded under sideEffects); they are compared by identity, because protected objects cannot be exported.",
    "Engineering only; nothing is downloaded and no motion or control behavior is observed.",
]


class Run(StateRun):
    """The access runner's apply/refuse/restore gates over the PLC program and its technology objects."""
    plc_compiled = KINDS + ("tag_create",)

    def __init__(self, h):
        super().__init__(h)
        for key in ("certificateIdentity", "notQualified"):
            h.report.pop(key)
        h.report.update(criteria=CRITERIA, excluded={})

    def args(self, kind, payload, **top):
        return {"kind": kind, "payload": payload, "plcPath": self.plc, **top}

    def to(self, *names):
        return self.plc + "/technology/" + "/".join(names)

    def read(self, path, **extra):
        return self.h.tool("technology_read", {"plcPath": self.plc, "technologyPath": path, "maxItems": 2000, **extra})

    def technology(self, destination=None):
        """Each technology object's identity, native hardware connections and native XML payload hash."""
        inventory = self.h.tool("technology_read", {"plcPath": self.plc, "maxItems": 500})
        require(inventory["completeness"]["scopeComplete"] is True and inventory["completeness"]["truncated"] is False, "technology_read inventory is incomplete")
        state, files = {}, {}
        for row in inventory["technologyObjects"]:
            detail = self.read(row["path"], exportXml=True)
            require(detail["technologyObject"] == row, "technology_read selected another object: %s" % detail["technologyObject"])
            raw = artifact_bytes(self.h.tool, detail["artifact"])
            if destination is not None:
                files["to:" + row["path"]] = destination / ("to-%03d.xml" % len(files))
                files["to:" + row["path"]].write_bytes(raw)
            state["to:" + row["path"]] = {**row, "connections": detail["hardwareConnections"], "payloadSha256": native_payload_hash(raw.decode("utf-8-sig"))}
        return state, files

    def state(self):
        return {**view(complete_inventory(lambda name, arguments=None: self.h.tool(name, {**(arguments or {}), "plcPath": self.plc}))), **self.technology()[0]}

    def snap(self, label):
        self.count += 1
        destination = self.h.directory / "snapshots" / ("%03d-%s" % (self.count, re.sub(r"[^A-Za-z0-9]+", "-", label)[:40]))
        taken = {}
        def take():
            taken.update(snapshot(lambda name, arguments=None: self.h.tool(name, {**(arguments or {}), "plcPath": self.plc} if name == "software_inspect" else arguments), destination))
            taken["technology"], taken["files"] = self.technology(destination)
            return {"objects": len(taken["objects"]), "technologyObjects": len(taken["technology"])}
        self.h.stage("snapshot " + label, take)
        state = {**view(taken["inventory"], taken["objects"]), **taken["technology"]}
        (destination / "state.json").write_text(json.dumps(state, indent=1), encoding="utf-8")
        return {"view": state, "files": taken["files"]}

    def apply(self, label, kind, payload, preview=None, shown=None, **expected):
        prepared = self.prepare(label, kind, payload, preview, shown)
        before = self.current["view"]
        def generated(category, key):
            """TIA adds a technology object's instructions and types to the system groups (PID_Compact) and generates
            know-how protected motion OBs (MC_Interpolator, MC_Servo) for an axis."""
            row = (self.current["view"] if category == "added" else before).get(key) or {}
            return category in ("added", "removed") and key.split(":", 1)[0] in ("block", "type") and ("/systemGroups/" in key or row.get("isKnowHowProtected") is True)
        self.finish(label, kind, prepared, tolerated=generated, **expected)
        return prepared


def selected(run, path, payload):
    """The worker binds an exact technologyPath and its name into the reviewed payload."""
    return {**payload, "technologyPath": path, "name": path.rsplit("/", 1)[1]}


def create(run, name, family, parameters=None, restore=False):
    payload = {"name": name, "typeIdentifier": family[0], "version": family[1], **({"parameters": parameters} if parameters else {})}
    run.apply("create %s %s %s" % (family[0], family[1], name), "to_create", payload,
              {"effects": [{"createTechnologyObject": name, "typeIdentifier": family[0], "version": family[1]}]},
              added=["to:" + run.to(name)], rows={"to:" + run.to(name): {"systemLibraryType": family[0], "systemLibraryVersion": family[1]}}, restore=restore)


def child(run, owner, name, restore=False):
    path = run.to(owner, "children", "OutputCams", name)
    run.apply("create output cam %s under %s" % (name, owner), "to_child_create",
              {"ownerPath": run.to(owner), "composition": "OutputCams", "name": name, "typeIdentifier": CAM[0], "version": CAM[1]},
              {"effects": [{"createTechnologyObject": name, "ownerPath": run.to(owner), "composition": "OutputCams", "typeIdentifier": CAM[0], "version": CAM[1]}]},
              added=["to:" + path], rows={"to:" + path: {"ownerPath": run.to(owner), "composition": "OutputCams", "systemLibraryType": CAM[0]}}, restore=restore)


def parameter(run, name, value, restore=False):
    path = run.to(name)
    def readback(applied, state):
        rows = run.read(path, parameterPrefix=LIMIT)["parameters"]
        require([(row["name"], row.get("value")) for row in rows] == [(LIMIT, value)], "technology_read reports %s" % rows)
    payload = {"technologyPath": path, "parameters": {LIMIT: value}}
    run.apply("set %s %s to %s" % (name, LIMIT, value), "to_parameters", payload,
              {"effects": [{"technologyObject": name, "technologyPath": path, "action": "to_parameters"}], "parameterChanges": [{"name": LIMIT, "after": value}]},
              shown=selected(run, path, payload), changed=["to:" + path], check=readback, restore=restore)


def link(run, cam, tag, kind, restore=False):
    path = run.to("ToAxis", "children", "OutputCams", cam)
    payload = {"technologyPath": path, "role": "outputCam", **({"tagName": tag} if kind == "to_connect" else {})}
    def connections(applied, state):
        rows = state["to:" + path]["connections"]
        require((tag in json.dumps(rows)) == (kind == "to_connect"), "technology_read connections of %s: %s" % (cam, rows))
    run.apply("%s %s %s %s" % ("connect" if kind == "to_connect" else "disconnect", cam, "to" if kind == "to_connect" else "from", tag), kind, payload,
              {"effects": [{"technologyObject": cam, "technologyPath": path, "action": kind}]},
              shown=selected(run, path, payload), changed=["to:" + path], check=connections, restore=restore)


def delete(run, name, restore=False):
    path = run.to(name)
    payload = {"technologyPath": path}
    run.apply("delete " + name, "to_delete", payload, {"effects": [{"technologyObject": name, "technologyPath": path, "action": "to_delete"}]},
              shown=selected(run, path, payload), removed=["to:" + path], restore=restore)


def tag(run, name, address):
    table = run.plc + "/tags/Default%20tag%20table"
    run.apply("create tag " + name, "tag_create", {"tagTablePath": table, "name": name, "dataTypeName": "Bool", "logicalAddress": address},
              added=["member:%s/tags/%s" % (table, name)], changed=["tagTable:" + table])


def read_contract(run):
    """technology_read: bounded inventory, exact selection, missing paths and prefixed parameters."""
    h = run.h
    def check():
        bounded = h.tool("technology_read", {"plcPath": run.plc, "maxItems": 1})
        require(bounded["completeness"]["truncated"] is True and bounded["completeness"]["scopeComplete"] is False and len(bounded["technologyObjects"]) == 1,
                "A one-item inventory must report truncation: %s" % bounded["completeness"])
        error = h.call_error("technology_read", {"plcPath": run.plc, "technologyPath": run.to("ToMissing")})
        require(error.get("code") == "ObjectNotFound", "Expected ObjectNotFound for a missing path: %s" % error)
        rows = run.read(run.to("ToPidKeep"), parameterPrefix="Config.")["parameters"]
        require(rows and all(row["name"].startswith("Config.") for row in rows), "Prefixed parameters leak other names")
        return {"inventoryBound": bounded["completeness"], "prefixedParameters": len(rows)}
    h.stage("technology_read contract", check)
    run.mark("technology_read", "refused", "ObjectNotFound")


def phase_a(run):
    create(run, "ToPid", PID)
    run.refuse("to_create", "a name differing only in case", {"name": "topid", "typeIdentifier": PID[0], "version": PID[1]}, "ObjectExists")
    run.refuse("to_create", "a version that is not dotted numeric", {"name": "ToOther", "typeIdentifier": PID[0], "version": "three"}, "InvalidRequest")
    run.refuse("to_create", "a technologyPath", {"name": "ToOther", "typeIdentifier": PID[0], "version": PID[1], "technologyPath": run.to("ToPid")}, "InvalidRequest")
    create(run, "ToPidKeep", PID)
    create(run, "ToAxis", AXIS, {"VirtualAxis.Mode": 1})
    child(run, "ToAxis", "ToCam")
    child(run, "ToAxis", "ToCamFree")
    cam = {"composition": "OutputCams", "name": "ToCamOther", "typeIdentifier": CAM[0], "version": CAM[1]}
    run.refuse("to_child_create", "an unknown composition", {**cam, "ownerPath": run.to("ToAxis"), "composition": "Cams"}, "InvalidRequest")
    run.refuse("to_child_create", "an owner without a cam container", {**cam, "ownerPath": run.to("ToPid")}, "UnsupportedCapability")
    run.refuse("to_child_create", "an existing name", {**cam, "ownerPath": run.to("ToAxis"), "name": "ToCam"}, "ObjectExists")
    stale = run.prepare("set ToPid's limit (made stale below)", "to_parameters", {"technologyPath": run.to("ToPid"), "parameters": {LIMIT: 70.0}},
                        shown=selected(run, run.to("ToPid"), {"technologyPath": run.to("ToPid"), "parameters": {LIMIT: 70.0}}))
    run.refuse_apply("to_parameters", "a wrong previewHash", {**stale, "previewHash": "0" * 64}, "PreviewMismatch")
    parameter(run, "ToPid", 90.0)
    run.refuse_apply("to_parameters", "a preview made stale by a later change", stale, "StaleRevision")
    run.refuse("to_parameters", "a missing object", {"technologyPath": run.to("ToMissing"), "parameters": {LIMIT: 1.0}}, "TechnologyObjectNotFound")
    tag(run, "ToCamOut", "%Q0.0")
    tag(run, "ToCamFreeOut", "%Q0.1")
    cam_path = run.to("ToAxis", "children", "OutputCams", "ToCam")
    run.refuse("to_connect", "the actor role of an output cam", {"technologyPath": cam_path, "role": "actor", "tagName": "ToCamOut"}, "UnsupportedCapability")
    run.refuse("to_connect", "a missing tag", {"technologyPath": cam_path, "role": "outputCam", "tagName": "ToMissing"}, "TagNotFound")
    run.refuse("to_connect", "a tag and a hardware target at once", {"technologyPath": cam_path, "role": "outputCam", "tagName": "ToCamOut",
                                                                       "hardwarePath": run.plc}, "InvalidRequest")
    run.refuse("to_disconnect", "an unconnected cam", {"technologyPath": cam_path, "role": "outputCam"}, "NotConnected")
    link(run, "ToCam", "ToCamOut", "to_connect")
    link(run, "ToCam", "ToCamOut", "to_disconnect")
    link(run, "ToCam", "ToCamOut", "to_connect")
    run.refuse("to_delete", "a missing object", {"technologyPath": run.to("ToMissing")}, "TechnologyObjectNotFound")
    delete(run, "ToPid")
    read_contract(run)


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
    create(run, "ToPidAgain", PID, restore=True)
    parameter(run, "ToPidKeep", 80.0, restore=True)
    child(run, "ToAxis", "ToCamAgain", restore=True)
    link(run, "ToCamFree", "ToCamFreeOut", "to_connect", restore=True)
    link(run, "ToCam", "ToCamOut", "to_disconnect", restore=True)
    delete(run, "ToPidKeep", restore=True)


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("technology changes")
    run.project = h.tool("tia_session_status")["projects"][0]["path"]
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
    Harness(args, "to_", "D13 technology-object changes in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
