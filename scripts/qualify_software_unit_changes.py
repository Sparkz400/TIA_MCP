"""Qualify the D07 software-unit change kinds natively in one owned scratch CPU 1511-1 PN.

Phase A creates units, puts an FB into one, and applies every covered kind with checkpoint, PLC compile and save: unit
creation, publication of the FB, relations added and removed, deletion of an empty unit, a unit created from a master copy
of another unit (library_create_master_copy into the project library, then PlcUnitComposition.CreateFrom: the copy route,
since V21 has no unit copy or move API) and a rename. Each preview is asserted
against the request, and a complete state (every block, type and tag-table export, every tag, constant and group path, and
each software scope's name, kind, namespace preset and relations) must differ from the previous one in exactly the intended
keys. A fresh Portal then reopens the saved project, which must show the identical state. Phase B applies one more change
per kind from that state and restores its checkpoint in the applying Portal; the recovered copy must equal the pre-change
state. Typed refusals must change nothing. Last, a master copy of the unit holding the FB is instantiated in the same PLC
with onNameConflict=rename; whether V21 accepts it or the member check refuses it is recorded under boundaries.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re

from library_native_harness import Harness, require
from qualification_owned_scratch import complete_inventory, snapshot
from qualify_access_changes import Run as StateRun
from qualify_hardware_changes import under
from qualify_plc_object_mutations import coverage_gaps, diff, matches, view
from qualify_drives import outcome

KINDS = ("software_unit_create", "software_unit_publication_set", "software_unit_relation_set", "software_unit_delete",
         "software_unit_create_from", "software_unit_rename")
FB = '''FUNCTION_BLOCK "UnitFb"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      Run : Bool;
   END_VAR

BEGIN
END_FUNCTION_BLOCK

'''
CRITERIA = {
    "AC-identity": "refused: typed refusals of duplicate names, relation cycles, duplicate or missing relations, unsupported relation types, non-unit targets, non-empty or referenced units, "
                   "renames of referenced units or to the current name, master copies without exactly one unit and template-name conflicts, each leaving the complete state unchanged.",
    "AC-preview": "Every preview is asserted against the request and the current state before apply; a wrong previewHash refuses at apply.",
    "AC-preserve": "applied: every block, type and tag-table export, every member and group path, and each software scope's name, kind, namespace preset and relations differ in exactly the intended keys.",
    "AC-native": "applied + persisted: checkpoint, PLC compile and save stages, change_status, the native export (publication as <Access>) and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the pre-change state.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json and limitations.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 without safety units; SafetyBoundary refusals are not exercised. V21 has no unit copy or move API "
    "(PlcUnit exposes only Delete; PlcUnitComposition only Create, CreateFrom(MasterCopy[, MasterCopyMode]) and Find; PlcUnitSystemGroup has no unit groups).",
    "Copy is the composed route: library_create_master_copy of an empty unit into the project library, then software_unit_create_from in the same PLC; "
    "master copies in global libraries and other PLCs or projects are not exercised. Rename covers an unreferenced unit; a referenced unit is refused by design.",
    "Publication is set on one SCL FB inside a unit; PLC data types, other block kinds and consumers in related units that unpublishing would break are not exercised.",
    "Relations are SoftwareUnit relations between standard units; NonUnitDB and TODB relations are refused by design.",
    "Unit deletion covers empty units, whose generated default tag table goes with them; deleting a unit with members is refused by design.",
    "Engineering only; nothing is downloaded and runtime behavior is not observed.",
]


def scopes(inventory):
    return {"scope:" + row["path"]: {key: row.get(key) for key in ("name", "kind", "namespacePreset", "relations")} for row in inventory["softwareScopes"]}


class Run(StateRun):
    """The access runner's apply/refuse/restore gates over the PLC program and its software scopes."""
    plc_compiled = KINDS + ("source_upsert",)

    def __init__(self, h):
        super().__init__(h)
        for key in ("certificateIdentity", "notQualified"):
            h.report.pop(key)
        h.report.update(criteria=CRITERIA, excluded={})

    def args(self, kind, payload, **top):
        return {"kind": kind, "payload": payload, "plcPath": self.plc, **top}

    def unit(self, name):
        return self.plc + "/units/" + name

    def state(self):
        inventory = complete_inventory(lambda name, arguments=None: self.h.tool(name, {**(arguments or {}), "plcPath": self.plc}))
        return {**view(inventory), **scopes(inventory)}

    def snap(self, label):
        self.count += 1
        destination = self.h.directory / "snapshots" / ("%03d-%s" % (self.count, re.sub(r"[^A-Za-z0-9]+", "-", label)[:40]))
        taken = {}
        def take():
            taken.update(snapshot(lambda name, arguments=None: self.h.tool(name, {**(arguments or {}), "plcPath": self.plc} if name == "software_inspect" else arguments), destination))
            return {"objects": len(taken["objects"])}
        self.h.stage("snapshot " + label, take)
        state = {**view(taken["inventory"], taken["objects"]), **scopes(taken["inventory"])}
        (destination / "state.json").write_text(json.dumps(state, indent=1), encoding="utf-8")
        return {"view": state, "files": {key: row.get("file") for key, row in taken["objects"].items()}}

    def apply(self, label, kind, payload, preview=None, top=None, **expected):
        prepared = self.h.stage("prepare " + label, lambda: self.h.tool("change_prepare", self.args(kind, payload, **(top or {}))))
        actual = prepared["preview"]
        wanted = {"changeId": prepared["changeId"], "kind": kind, "project": {"path": self.project}, **(preview or {})}
        require(prepared["state"] == "prepared" and actual["payload"] == payload and matches(wanted, actual),
                "Preview of %s differs: %s" % (label, {key: actual.get(key) for key in [*wanted, "payload"]}))
        self.finish(label, kind, prepared, **expected)
        return prepared


def create(run, name, preset=None, restore=False):
    unit = run.unit(name)
    run.apply("create " + name, "software_unit_create", {"name": name, **({"namespacePreset": preset} if preset else {})},
              {"effects": [{"createSoftwareUnit": name, "unitPath": unit, "namespacePreset": preset}]},
              added=under(unit), rows={"scope:" + unit: {"name": name, "kind": "softwareUnit", "namespacePreset": preset or "", "relations": []}}, restore=restore)


def publish(run, value, restore=False):
    block = run.unit("UnitA") + "/blocks/UnitFb"
    before = "Unpublished" if value == "Published" else "Published"
    def exported(applied, state):
        text = Path(run.current["files"]["block:" + block]).read_text(encoding="utf-8-sig")
        require("<Access>%s</Access>" % value in text, "The native export of UnitFb lacks <Access>%s</Access>" % value)
    run.apply("set UnitFb %s" % value, "software_unit_publication_set", {"objectPath": block, "attributes": {"Access": value}},
              {"effects": [{"objectPath": block, "kind": "block"}], "attributeChanges": [{"name": "Access", "before": before, "after": value}],
               "targetScopePath": run.unit("UnitA")},
              changed=["block:" + block], check=exported, restore=restore)


def relate(run, owner, related, action, restore=False):
    unit, other = run.unit(owner), run.unit(related)
    relations = [r for r in run.current["view"]["scope:" + unit]["relations"] if r["relatedObject"] != related]
    if action == "add":
        relations.append({"relationType": "SoftwareUnit", "relatedObject": related})
    run.apply("%s relation %s -> %s" % (action, owner, related), "software_unit_relation_set",
              {"unitPath": unit, "relatedUnitPath": other, "relationType": "SoftwareUnit", "action": action},
              {"effects": [{"unitPath": unit, "relatedUnitPath": other, "relatedObject": related, "relationType": "SoftwareUnit", "action": action}],
               "targetScopePath": unit},
              changed=["scope:" + unit], rows={"scope:" + unit: {"relations": relations}}, restore=restore)


def delete(run, name, restore=False):
    unit = run.unit(name)
    removed = under(unit)(None, run.current["view"])
    tables = sorted(key.split(":", 1)[1] for key in removed if key.startswith("tagTable:"))
    outgoing = run.current["view"]["scope:" + unit]["relations"]
    run.apply("delete " + name, "software_unit_delete", {"unitPath": unit, "dependencyPolicy": "rejectReferenced"},
              {"effects": [{"deleteSoftwareUnit": unit, "deletedEmptyTagTables": tables, "deletedOutgoingRelations": outgoing}],
               "dependencies": {"policy": "rejectReferenced", "references": [], "cascade": False}},
              removed=removed, restore=restore)


def master(run, kind, path):
    """library_create_master_copy of one unit or block into the project library: the program state must not change."""
    h, before, name = run.h, run.current, path.rsplit("/", 1)[1]
    library = h.project_library()
    h.apply("library_create_master_copy", {"schemaVersion": 1, "destinationLibraryId": library, "target": {"plcPath": run.plc}, "object": {"kind": kind, "objectPath": path}})
    run.current = run.snap("master copy " + name)
    h.stage("master copy of %s leaves the program unchanged" % name,
            lambda: require(run.current["view"] == before["view"], "Master copy changed the program: %s" % diff(before["view"], run.current["view"])) or name)
    rows = [x for x in h.query(library)["items"] if x.get("kind") == "masterCopy" and x.get("name") == name]
    require(len(rows) == 1, "Expected one master copy %s: %s" % (name, rows))
    return library, rows[0]["masterCopyPath"]


def instantiate(run, library, path, template, name, restore=False):
    """software_unit_create_from with onNameConflict=rename; the copy's preset and relations are recorded against the template's."""
    unit, source = run.unit(name), run.current["view"]["scope:" + run.unit(template)]
    def record(applied, state):
        members = [row for row in applied.get("readbacks") or [] if row.get("role") == "createdUnitMembers"]
        require(len(members) == 1 and not [m for m in members[0]["renamedByMasterCopyMode"] if m["kind"] != "tagTable"], "Unexpected member readback: %s" % members)
        run.h.report.setdefault("createFrom", {})[name] = {"template": source, "copy": state["scope:" + unit], "members": members[0]}
    run.apply("create %s from the master copy of %s" % (name, template), "software_unit_create_from",
              {"libraryId": library, "masterCopyPath": path, "name": name, "onNameConflict": "rename"},
              {"effects": [{"createSoftwareUnitFrom": {"masterCopyPath": path}, "templateUnit": template, "masterCopyMode": "Rename", "name": name, "unitPath": unit}]},
              added=under(unit), rows={"scope:" + unit: {"name": name, "kind": "softwareUnit"}}, check=record, restore=restore)


def rename(run, old, new, restore=False):
    unit, renamed = run.unit(old), run.unit(new)
    removed = under(unit)(None, run.current["view"])
    run.apply("rename %s to %s" % (old, new), "software_unit_rename", {"unitPath": unit, "name": new},
              {"effects": [{"renameSoftwareUnit": unit, "name": new, "unitPath": renamed}], "dependencies": {"references": []}, "targetScopePath": unit},
              removed=removed, added=under(renamed), rows={"scope:" + renamed: {**run.current["view"]["scope:" + unit], "name": new}}, restore=restore)


def copies(run):
    """Copy through a master copy, the template-name conflict and rename refusals, then a rename."""
    create(run, "UnitT", "Ns.T")
    relate(run, "UnitT", "UnitA", "add")
    run.library, run.template = master(run, "softwareUnit", run.unit("UnitT"))
    root = next(key.split(":", 1)[1] for key in run.current["view"] if key.startswith("block:") and key.endswith("/blocks/TiaMcp_IsEnabled"))
    _, block = master(run, "block", root)
    template = {"libraryId": run.library, "masterCopyPath": run.template, "name": "UnitT2"}
    run.refuse("software_unit_create_from", "the template unit's name already in the PLC", template, "ObjectExists")
    run.refuse("software_unit_create_from", "a name already in the PLC", {**template, "name": "UnitA", "onNameConflict": "rename"}, "ObjectExists")
    run.refuse("software_unit_create_from", "a block master copy", {**template, "masterCopyPath": block, "onNameConflict": "rename"}, "TargetIncompatible")
    run.refuse("software_unit_create_from", "an unknown conflict policy", {**template, "onNameConflict": "replace"}, "InvalidRequest")
    instantiate(run, run.library, run.template, "UnitT", "UnitT2")
    rename(run, "UnitT2", "UnitR")
    run.refuse("software_unit_rename", "a unit other units relate to", {"unitPath": run.unit("UnitA"), "name": "UnitZ"}, "DependencyExists")
    run.refuse("software_unit_rename", "a name in use", {"unitPath": run.unit("UnitR"), "name": "unitb"}, "ObjectExists")
    run.refuse("software_unit_rename", "the current name", {"unitPath": run.unit("UnitR"), "name": "UnitR"}, "InvalidRequest")


def nonempty_copy(run):
    """Boundary probe: instantiate a master copy of UnitA (holding UnitFb) in the same PLC with onNameConflict=rename."""
    h = run.h
    library, path = master(run, "softwareUnit", run.unit("UnitA"))
    def probe():
        payload = {"libraryId": library, "masterCopyPath": path, "name": "UnitA2", "onNameConflict": "rename"}
        error, prepared = outcome(h, "change_prepare", run.args("software_unit_create_from", payload))
        if error:
            return {"outcome": "refusedAtPrepare", "error": error}
        error, applied = outcome(h, "change_apply", h.apply_args(prepared))
        if not error:
            return {"outcome": "applied", "stages": applied["stages"], "units": sorted(k for k in run.state() if k.startswith("scope:"))}
        if h.unresolved_admissions():
            h.reconcile_admission("release the failed non-empty copy")
        restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        return {"outcome": "failedAtApply", "error": {k: error.get(k) for k in ("code", "message")}, "restore": restored["state"]}
    h.report.setdefault("boundaries", {})["software_unit_create_from of a unit holding a block in its own PLC"] = h.stage("probe a copy of UnitA (with UnitFb) in the same PLC", probe)


def phase_a(run):
    create(run, "UnitA", "Ns.A")
    create(run, "UnitB")
    run.refuse("software_unit_create", "a name differing only in case", {"name": "unita"}, "ObjectExists")
    block = run.unit("UnitA") + "/blocks/UnitFb"
    run.apply("create UnitFb in UnitA", "source_upsert", {"language": "SCL", "source": FB, "expectedBlockNames": ["UnitFb"]},
              {"effects": [{"action": "create", "blockName": "UnitFb"}], "targetScopePath": run.unit("UnitA")}, top={"scopePath": run.unit("UnitA")},
              added=["block:" + block])
    root = next(key.split(":", 1)[1] for key in run.current["view"] if key.startswith("block:") and key.endswith("/blocks/TiaMcp_IsEnabled"))
    run.refuse("software_unit_publication_set", "a PLC-root block", {"objectPath": root, "attributes": {"Access": "Published"}}, "InvalidRequest")
    run.refuse("software_unit_publication_set", "an attribute other than Access", {"objectPath": block, "attributes": {"Comment": "x"}}, "UnsupportedAttribute")
    stale = run.h.stage("prepare publish UnitFb (made stale below)", lambda: run.h.tool("change_prepare", run.args(
        "software_unit_publication_set", {"objectPath": block, "attributes": {"Access": "Published"}})))
    run.refuse_apply("software_unit_publication_set", "a wrong previewHash", {**stale, "previewHash": "0" * 64}, "PreviewMismatch")
    publish(run, "Published")
    publish(run, "Unpublished")
    relate(run, "UnitB", "UnitA", "add")
    relation = lambda owner, related, action="add", kind="SoftwareUnit": {"unitPath": run.unit(owner), "relatedUnitPath": run.unit(related), "relationType": kind, "action": action}
    run.refuse("software_unit_relation_set", "a relation closing a cycle", relation("UnitA", "UnitB"), "RelationCycle")
    run.refuse("software_unit_relation_set", "a self-relation", relation("UnitA", "UnitA"), "RelationCycle")
    run.refuse("software_unit_relation_set", "an existing relation", relation("UnitB", "UnitA"), "ObjectExists")
    run.refuse("software_unit_relation_set", "a missing relation", relation("UnitA", "UnitB", "remove"), "ObjectNotFound")
    run.refuse("software_unit_relation_set", "a NonUnitDB relation", relation("UnitA", "UnitB", kind="NonUnitDB"), "UnsupportedRelationType")
    run.refuse("software_unit_relation_set", "a missing unit", relation("UnitA", "UnitZ"), "ScopeNotFound")
    create(run, "UnitC")
    run.refuse_apply("software_unit_publication_set", "a preview made stale by later changes", stale, "StaleRevision")
    relate(run, "UnitB", "UnitC", "add")
    run.refuse("software_unit_delete", "a unit another unit relates to", {"unitPath": run.unit("UnitC"), "dependencyPolicy": "rejectReferenced"}, "DependencyExists")
    run.refuse("software_unit_delete", "a unit holding a block", {"unitPath": run.unit("UnitA"), "dependencyPolicy": "rejectReferenced"}, "UnitNotEmpty")
    run.refuse("software_unit_delete", "the PLC root scope", {"unitPath": run.plc, "dependencyPolicy": "rejectReferenced"}, "InvalidRequest")
    relate(run, "UnitB", "UnitC", "remove")
    delete(run, "UnitC")
    copies(run)


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
    create(run, "UnitD", "Ns.D", restore=True)
    publish(run, "Published", restore=True)
    relate(run, "UnitB", "UnitA", "remove", restore=True)
    delete(run, "UnitB", restore=True)
    library = run.h.project_library()  # Library IDs do not survive the reopen; the master copy path does.
    instantiate(run, library, run.template, "UnitT", "UnitT3", restore=True)
    rename(run, "UnitR", "UnitR2", restore=True)


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("software unit changes")
    run.project = h.tool("tia_session_status")["projects"][0]["path"]
    run.current = run.snap("baseline")
    phase_a(run)
    reopen(run)
    phase_b(run)
    gaps = coverage_gaps(run.coverage, KINDS)
    require(not gaps, "Kinds without applied, persisted, refused and restored evidence: %s" % gaps)
    nonempty_copy(run)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "su_", "D07 software-unit changes in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
