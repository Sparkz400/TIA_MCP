"""Qualify the readback of the tags a master copy brings natively (1 November audit fix) in owned scratch CPU 1511-1 PN projects.

TIA creates a master-copy tag whose name the software scope already uses as <name>(<n>); ThrowIfExists decides on the table name
only (run fr_eeb8be6f: McProbe became McProbe(1) and library_instantiate reported applied). The readback now compares the created
tags with the scope's names before the create and fails the change with MutationVerificationFailed.

Scratch 1: the table McTags with McProbe (%M60.0) and McSpare (%M61.0) becomes a master copy in the project library.
- Without a collision: after both tags and McTags were deleted, the master copy instantiates with checkpoint, compile and save,
  and McTags reads back with both tags under their own names.
- With a collision: McTags is renamed McTagsOld, so both tags stay in the PLC, and change_apply of the instantiation must fail
  with MutationVerificationFailed naming McProbe(1) and McSpare(1); change_status shows recovery_required. A fresh empty owned
  Portal retrieves the checkpoint to a new location, whose tag tables must equal those before the apply.
Scratch 2: a unit CollUnit whose default tag table holds UnitTag (%M70.0) becomes a master copy, and software_unit_create_from
with onNameConflict=rename copies it into the same PLC as CollUnit2. UnitTag then exists in CollUnit; the step records whether
TIA kept the copy's UnitTag (it applies) or renamed it (the readback fails it with MutationVerificationFailed and the checkpoint
is restored).
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from library_native_harness import Harness, require
from probe_safety_kinds import change
from probe_wave_kinds import error_of
from qualify_fio_tag_routes import table_path

MC_TABLE, OLD_TABLE, UNIT, UNIT_TAG = "McTags", "McTagsOld", "CollUnit", "UnitTag"
TAGS = (("McProbe", "%M60.0"), ("McSpare", "%M61.0"))
LIMITATIONS = [
    "Owned scratch CPU 1511-1 PN V4.1 projects; master copies in the project library only, Bool tags at bit-memory addresses, collisions within one software scope.",
    "TIA's rename is recognized by its <name>(<n>) pattern, conservatively: a master-copy tag that is itself named <existing>(<n>) also fails the readback.",
    "The unit copy holds one tag and is copied within its own PLC with onNameConflict=rename; unit copies into other PLCs and user constants are not exercised.",
]


def by_name(h):
    """Every tag table of the one PLC by name with its tags as (name, type, address, isSafety), independent of the project path."""
    plcs = [p["path"] for p in h.tool("project_inventory", {"maxItems": 2000})["plcs"]]
    require(len(plcs) == 1, "Expected one PLC: %s" % plcs)
    return {t["path"].split(plcs[0], 1)[1]: sorted([g["name"], g.get("dataType"), g.get("logicalAddress"), g.get("isSafety")] for g in t.get("tags", []))
            for t in h.tool("software_inspect", {"plcPath": plcs[0], "maxItems": 2000})["tagTables"]}


def master_copy(h, plc, kind, path, name):
    library = h.project_library()
    change(h, "library_create_master_copy", {"schemaVersion": 1, "destinationLibraryId": library, "target": {"plcPath": plc},
                                             "object": {"kind": kind, "objectPath": path}}, {})
    masters = [x for x in h.query(library)["items"] if x.get("kind") == "masterCopy" and x.get("name") == name]
    require(len(masters) == 1, "Expected one master copy %s: %s" % (name, masters))
    return library, masters[0]["masterCopyPath"]


def failed_apply(h, label, prepared, check):
    """change_apply must fail with MutationVerificationFailed; check(error) asserts its details. Returns the error."""
    def run():
        error = h.call_error("change_apply", h.apply_args(prepared))
        require(error.get("code") == "MutationVerificationFailed", "Expected MutationVerificationFailed, observed %s: %s" % (error.get("code"), error.get("message")))
        check(error)
        return {"code": error["code"], "message": error.get("message")}
    result = h.stage(label, run)
    if h.unresolved_admissions():
        h.reconcile_admission("release the admission of: " + label, prepared.get("admissionId"))
    return result


def library_collision(h):
    plc = h.owned_scratch("tag collision")
    s = {"plcPath": plc}
    change(h, "tag_table_create", {"tagTableName": MC_TABLE}, s)
    source = table_path(h, plc, MC_TABLE)
    for name, address in TAGS:
        change(h, "tag_create", {"tagTablePath": source, "name": name, "dataTypeName": "Bool", "logicalAddress": address}, s)
    library, path = master_copy(h, plc, "tagTable", source, MC_TABLE)
    instantiate = {"schemaVersion": 1, "source": h.source_ref(library), "selection": [{"masterCopyPath": path}],
                   "destinationComposition": "tagTables", "target": {"plcPath": plc}}
    expected = sorted([name, "Bool", address, False] for name, address in TAGS)

    for name, _ in TAGS:
        change(h, "tag_delete", {"tagTablePath": source, "memberName": name, "dependencyPolicy": "rejectReferenced"}, s)
    change(h, "tag_table_delete", {"tagTablePath": source}, s)
    change(h, "library_instantiate", {**instantiate, "source": h.source_ref(library)}, {})
    def unrenamed():
        rows = [v for k, v in by_name(h).items() if k.endswith("/" + MC_TABLE)]
        require(rows == [expected], "%s reads back %s, expected %s" % (MC_TABLE, rows, expected))
        return rows[0]
    h.stage("without a collision, %s reads back with both tags under their own names" % MC_TABLE, unrenamed)

    change(h, "tag_table_rename", {"tagTablePath": table_path(h, plc, MC_TABLE), "newName": OLD_TABLE}, s)
    before = h.stage("tag tables before the colliding instantiation", lambda: by_name(h))
    require([v for k, v in before.items() if k.endswith("/" + OLD_TABLE)] == [expected] and not [k for k in before if k.endswith("/" + MC_TABLE)],
            "Unexpected tables before the collision: %s" % before)
    prepared = h.stage("prepare library_instantiate of %s beside %s" % (MC_TABLE, OLD_TABLE),
                       lambda: h.tool("change_prepare", {"kind": "library_instantiate", "payload": {**instantiate, "source": h.source_ref(library)}}))
    renamed = sorted(name + "(1)" for name, _ in TAGS)
    def named(error):
        text = json.dumps(error)
        require(all(name in text for name in renamed), "The failure does not name %s: %s" % (renamed, text[:2000]))
    h.report["collision"] = failed_apply(h, "change_apply fails with MutationVerificationFailed naming %s" % ", ".join(renamed), prepared, named)
    def status():
        result = h.tool("change_status", {"changeId": prepared["changeId"]})
        failure = (result.get("journal") or {}).get("failure") or {}
        require(result["state"] == "recovery_required" and failure.get("code") == "MutationVerificationFailed"
                and sorted((failure.get("details") or {}).get("renamedTags") or []) == renamed, "Unexpected status: %s" % {k: result.get(k) for k in ("state",)})
        return {"state": result["state"], "failure": failure}
    h.report["collisionStatus"] = h.stage("change_status shows recovery_required with the renamed tags", status)
    h.stage("close the failed session", lambda: h.tool("tia_detach"))
    h.owned_portal("recovery")
    def restore():
        restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        rows = restored.get("resources") or []
        require(restored.get("state") == "restored_to_new_locations" and [r.get("kind") for r in rows] == ["project"] and rows[0].get("state") == "completed",
                "Checkpoint recovery differs: %s" % restored)
        return restored
    h.report["restored"] = h.stage("change_restore retrieves the checkpoint to a new location", restore)
    def recovered():
        after = by_name(h)
        require(after == before, "The recovered tag tables differ: %s != %s" % (after, before))
        return after
    h.stage("the recovered project holds %s with both tags and no %s" % (OLD_TABLE, MC_TABLE), recovered)
    h.stage("close the recovery session", lambda: h.tool("tia_detach"))


def unit_copy(h):
    plc = h.owned_scratch("unit tag copy")
    s = {"plcPath": plc}
    unit = plc + "/units/" + UNIT
    change(h, "software_unit_create", {"name": UNIT}, s)
    tables = [t["path"] for t in h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["tagTables"] if t["path"].startswith(unit + "/")]
    require(len(tables) == 1, "Expected one tag table in %s: %s" % (UNIT, tables))
    change(h, "tag_create", {"tagTablePath": tables[0], "name": UNIT_TAG, "dataTypeName": "Bool", "logicalAddress": "%M70.0"}, s)
    library, path = master_copy(h, plc, "softwareUnit", unit, UNIT)
    before = h.stage("tag tables before the unit copy", lambda: by_name(h))
    copy_unit = plc + "/units/" + UNIT + "2"
    prepared = h.stage("prepare software_unit_create_from of %s as %s2" % (UNIT, UNIT), lambda: h.tool("change_prepare", {
        "kind": "software_unit_create_from", "payload": {"libraryId": library, "masterCopyPath": path, "name": UNIT + "2", "onNameConflict": "rename"}, **s}))
    def run():
        result = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
        if not result.get("isError"):
            applied = result["structuredContent"]
            members = [r for r in applied.get("readbacks") or [] if r.get("role") == "createdUnitMembers"]
            copied = {k: v for k, v in by_name(h).items() if k.startswith(copy_unit.split(plc, 1)[1] + "/")}
            require(applied["state"] == "applied" and [[UNIT_TAG, "Bool", "%M70.0", False]] in copied.values(), "The copy's %s differs: %s" % (UNIT_TAG, copied))
            return {"outcome": "applied", "createdUnitMembers": members, "copiedTables": copied}
        error = error_of(result)
        require(error.get("code") == "MutationVerificationFailed" and UNIT_TAG + "(" in (error.get("message") or ""),
                "Expected an applied copy or MutationVerificationFailed naming a renamed %s: %s" % (UNIT_TAG, error))
        if h.unresolved_admissions():
            h.reconcile_admission("release the admission of the renamed unit copy", prepared.get("admissionId"))
        restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        require(by_name(h) == before, "The restored unit copy left tag tables changed")
        return {"outcome": "failedAtApply", "error": {k: error.get(k) for k in ("code", "message")}, "restore": restored.get("state")}
    h.report["unitCopy"] = h.stage("software_unit_create_from with %s in the template unit: applied unrenamed, or refused and restored" % UNIT_TAG, run)


def body(h):
    library_collision(h)
    unit_copy(h)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "tc_", "Master-copy tag readback of library_instantiate and software_unit_create_from in owned scratch CPU 1511-1 PN projects",
            LIMITATIONS, []).run(body)
