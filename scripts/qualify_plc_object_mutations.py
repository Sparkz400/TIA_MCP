"""Qualify the X11 PLC object mutations natively in one owned scratch CPU 1511-1 PN.

Phase A seeds tags, constants, types, blocks and groups through the prepared-change kinds themselves and applies every
covered kind with checkpoint, compile and save. Each preview must name the exact targets and effects, and a complete
native snapshot (every block, type and tag-table XML, every tag/constant row and group path) must differ from the
previous one in exactly the intended keys. A fresh Portal then reopens the saved project, which must show the identical
state. Phase B applies one more change per kind from that state and restores its checkpoint; the recovered copy must
equal the pre-change state. Typed refusals must leave the PLC software unchanged.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import re

from compilation_evidence import require_compile_only, require_plc_compile
from library_native_harness import Harness, require
from qualification_owned_scratch import complete_inventory, snapshot
from qualify_typed_resolution import empty_tag_template, tag_fixture

KINDS = ("tag_set", "tag_create", "tag_delete", "constant_set", "constant_create", "constant_delete", "tag_table_import_xml",
         "type_source_upsert", "type_rename", "type_delete", "plc_type_import_xml",
         "source_upsert", "block_import_xml", "block_rename", "block_delete", "block_number_set", "block_attributes_set",
         "group_create", "group_rename", "group_delete", "plc_compile", "instance_db_create")
_CREDENTIAL = "Apply refuses with CredentialRouteUnavailable before the checkpoint: no route hands a Host credential to the worker."
_GRAPHICAL = "Qualified by scripts/qualify_graphical_changes.py on native LAD/FBD fixtures; a plain scratch holds SCL blocks only."
# X11-owned change kinds a plain scratch cannot qualify; scripts/test_qualify_plc_object_mutations.py keeps
# KINDS plus these equal to the catalog's X11-owned change kinds.
EXCLUDED = {
    "block_protect": _CREDENTIAL, "block_unprotect": _CREDENTIAL, "block_write_protection_set": _CREDENTIAL,
    "graphical_operand_set": _GRAPHICAL, "graphical_network_patch": _GRAPHICAL, "graphical_network_insert": _GRAPHICAL,
    "call_connection_set": "Unavailable on V21: TIA refuses an Access without a wire or on two wires; prepare refuses it (qualify_graphical_changes.py).",
    "call_parameter_set": _GRAPHICAL,
    "graphical_typed_edit": "Qualified by its own typed LAD/FBD program, which also carries AC-completeness and AC-runtime.",
    **{kind: "Qualified by the engineering-gap runner (qualify_engineering_gaps.py)." for kind in
       ("tag_table_create", "tag_table_rename", "tag_table_delete", "device_compile", "project_compile")},
}
NEEDS = ("applied", "persisted", "refused", "restored")
STAGES = ("prechange_project_saved", "recovery_archive_created", "mutation_applied", "compile_finished", "changed_project_saved")
POLICY = {"dependencyPolicy": "rejectReferenced"}
NO_REFERENCES = {"policy": "rejectReferenced", "references": [], "cascade": False}
BLOCK_INFO = ("path", "plcPath", "name", "number", "kind", "programmingLanguage", "isConsistent", "isKnowHowProtected")
TABLE = "X11Tags"
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN at PLC-root scope: no software units, namespaces, safety, know-how protection or "
    "generated system-group objects, so AmbiguousTarget, namespace, ProtectedObject and GeneratedObject-block refusals are not exercised.",
    "SCL blocks and one global DB only; LAD/FBD/STL targets, OB attributes (PriorityNumber, CyclicTime, PhaseOffset, "
    "ProcessImagePartNumber), HeaderName and HeaderVersion are not written.",
    "Renamed and deleted groups were empty or held only a subgroup; groups holding blocks, types or tables are not renamed or moved.",
    "Not covered here: " + ", ".join(sorted(EXCLUDED)) + ".",
    "Engineering only; runtime behavior is not observed.",
    "A change whose compile fails leaves an inconsistent user block; change_restore then refuses (FingerprintUnavailable) by design and the retained checkpoint is the recovery input.",
]

DATA = '''DATA_BLOCK "X11Data"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
NON_RETAIN
   VAR
      Item : "X11Used";
      Count : Int := 4711;
   END_VAR

BEGIN

END_DATA_BLOCK

'''
EDIT = '''FUNCTION_BLOCK "X11Edit"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      Run : Bool;
   END_VAR
   VAR_OUTPUT
      Done : Bool;
   END_VAR

BEGIN
\t#Done := #Run;
END_FUNCTION_BLOCK

'''
STL_EDIT = 'FUNCTION_BLOCK "X11Edit"\nVERSION : 0.1\nBEGIN\nNETWORK\nTITLE =\n      NOP 0;\nEND_FUNCTION_BLOCK\n'


def function(name, statement, temp=""):
    return 'FUNCTION "%s" : Void\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\n%sBEGIN\n\t%s\nEND_FUNCTION\n\n' % (name, temp, statement)


def drop(value):
    return function("X11Drop", "#Count := %d;" % value, "   VAR_TEMP\n      Count : Int;\n   END_VAR\n")


def udt(name, members="Value : Int;"):
    return 'TYPE "%s"\nVERSION : 0.1\n   STRUCT\n      %s\n   END_STRUCT;\n\nEND_TYPE\n\n' % (name, members)


def view(inventory, objects=None):
    """Comparable PLC software state from software_inspect: block/type/table rows (plus their native XML payload hashes
    when exported), every tag and constant row, and every group path. Identities naming the project path are left out,
    so a recovered copy compares equal to its original."""
    state = {}
    for prefix, collection in (("block:", "blocks"), ("type:", "types"), ("tagTable:", "tagTables")):
        for row in inventory[collection]:
            state[prefix + row["path"]] = {k: v for k, v in row.items() if k not in ("identity", "tags", "constants")}
            for member in row.get("tags", []) + row.get("constants", []):
                state["member:" + member["path"]] = member
    for scope in inventory["softwareScopes"]:
        state.update(("group:" + path, True) for path in scope["groups"])
    for key, row in (objects or {}).items():
        state[key] = {**state[key], "payloadSha256": row["payloadSha256"]}
    return state


def diff(before, after):
    return {"added": sorted(after.keys() - before.keys()), "removed": sorted(before.keys() - after.keys()),
            "changed": sorted(key for key in before.keys() & after.keys() if before[key] != after[key])}


def matches(expected, actual):
    """Every expected key matches recursively; lists match element-wise at equal length; booleans never match numbers."""
    if isinstance(expected, dict):
        return isinstance(actual, dict) and all(key in actual and matches(value, actual[key]) for key, value in expected.items())
    if isinstance(expected, list):
        return isinstance(actual, list) and len(expected) == len(actual) and all(map(matches, expected, actual))
    return expected == actual and isinstance(expected, bool) == isinstance(actual, bool)


def in_order(stages, required):
    return [stage for stage in stages if stage in required] == list(required)


def replace_once(text, old, new):
    require(text.count(old) == 1, "Expected exactly one %r in the native export" % old)
    return text.replace(old, new)


def target_group(kind, plc, payload):
    """The native group a prepared change must name: the exact group for group kinds, else its family's root group."""
    if kind == "plc_compile":
        return None
    if kind.startswith("group_"):
        return payload["groupPath"]
    return plc + ("/types" if "type" in kind else "/tags" if kind.startswith(("tag_", "constant_")) else "/blocks")


def coverage_gaps(coverage, kinds):
    gaps = {kind: [need for need in NEEDS if not coverage.get(kind, {}).get(need)] for kind in kinds}
    return {kind: missing for kind, missing in gaps.items() if missing}


def member_effect(name, *changes):
    return {"memberName": name, "attributes": [{"name": n, "before": b, "after": a} for n, b, a in changes]}


class Run:
    """The owned scratch's current native snapshot, the reopened pre-change state and per-kind criterion coverage."""

    def __init__(self, h):
        self.h, self.count, self.coverage = h, 0, {}
        self.plc = self.project = self.current = self.b0 = None
        h.report["coverage"] = self.coverage
        h.report["excluded"] = EXCLUDED

    # Keys of view(); fixture names are plain identifiers, so their path segments need no escaping.
    def blk(self, name): return "block:%s/blocks/%s" % (self.plc, name)
    def typ(self, name): return "type:%s/types/%s" % (self.plc, name)
    def tbl(self): return "tagTable:%s/tags/%s" % (self.plc, TABLE)
    def tag(self, name): return "member:%s/tags/%s/tags/%s" % (self.plc, TABLE, name)
    def const(self, name): return "member:%s/tags/%s/constants/%s" % (self.plc, TABLE, name)

    @property
    def table(self):
        return {"tagTablePath": "%s/tags/%s" % (self.plc, TABLE)}

    def info(self, name):
        row = self.current["view"][self.blk(name)]
        return {key: row[key] for key in BLOCK_INFO}

    def xml(self, key):
        return Path(self.current["files"][key]).read_text(encoding="utf-8-sig")

    def mark(self, kind, need, label):
        self.coverage.setdefault(kind, {}).setdefault(need, []).append(label)

    def snap(self, label):
        self.count += 1
        destination = self.h.directory / "snapshots" / ("%03d-%s" % (self.count, re.sub(r"[^A-Za-z0-9]+", "-", label)[:40]))
        taken = {}
        def take():
            taken.update(snapshot(self.h.tool, destination))
            return {"objects": len(taken["objects"]), "manifest": str(destination / "manifest.json")}
        self.h.stage("snapshot " + label, take)
        return {"view": view(taken["inventory"], taken["objects"]), "files": {k: v.get("file") for k, v in taken["objects"].items()},
                "inventory": taken["inventory"]}

    def state(self):
        return view(complete_inventory(self.h.tool))

    def prepare(self, label, kind, payload, preview=None):
        arguments = {"kind": kind, "payload": payload, "plcPath": self.plc}
        prepared = self.h.stage("prepare " + label, lambda: self.h.tool("change_prepare", arguments))
        shown = prepared["preview"]
        expected = {"changeId": prepared["changeId"], "kind": kind, "project": {"path": self.project},
                    "targetScopePath": None if kind == "plc_compile" else self.plc,
                    "targetGroupPath": target_group(kind, self.plc, payload), **(preview or {})}
        require(prepared["state"] == "prepared" and shown["payload"] == payload and matches(expected, shown),
                "Preview of %s differs: %s" % (label, {key: shown.get(key) for key in expected}))
        return prepared

    def finish(self, label, kind, prepared, added=(), removed=(), changed=(), rows=None, xml=None, restore=False):
        """Apply with checkpoint, compile and save; a complete native snapshot must differ in exactly the listed keys."""
        before = self.current
        applied = self.h.apply_prepared(kind, prepared)
        require(applied["recoveryArchiveSha256"] and applied["preview"] == prepared["preview"], "Applied change lost its checkpoint or preview")
        if kind == "plc_compile":
            require_compile_only(prepared, applied, self.plc)
        else:
            require(in_order(applied["stages"], STAGES), "Checkpoint, apply, compile and save stages differ: %s" % applied["stages"])
            require_plc_compile(applied["compile"], self.plc)
        self.current = self.snap(label)
        def verify():
            observed = diff(before["view"], self.current["view"])
            wanted = {"added": sorted(added), "removed": sorted(removed), "changed": sorted(changed)}
            require(observed == wanted, "Native effect of %s differs: expected %s, observed %s" % (label, wanted, observed))
            for key, row in (rows or {}).items():
                require(matches(row, self.current["view"].get(key)), "Readback of %s differs: %s" % (key, self.current["view"].get(key)))
            for key, texts in (xml or {}).items():
                for text in [texts] if isinstance(texts, str) else texts:
                    require(text in self.xml(key), "Native export of %s lacks %s" % (key, text))
            return observed
        self.h.stage("verify " + label, verify)
        self.mark(kind, "applied", label)
        if restore:
            self.restore(label, kind, prepared)
        return prepared, applied

    def mutate(self, label, kind, payload, preview=None, **expected):
        return self.finish(label, kind, self.prepare(label, kind, payload, preview), **expected)

    def group(self, label, kind, root, path, name=None, added=(), removed=(), restore=False):
        payload = {"groupKind": root, "groupPath": path, **({"name": name} if kind != "group_delete" else POLICY)}
        return self.mutate(label, kind, payload, {"effects": [{"action": kind, "groupKind": root, "groupPath": path, "name": name}]},
                           added=["group:" + p for p in added], removed=["group:" + p for p in removed], restore=restore)

    def refuse(self, kind, label, payload, code, **top):
        """A refused preparation changes no block, type, table, tag, constant or group."""
        def check():
            before = self.state()
            error = self.h.call_error("change_prepare", {"kind": kind, "payload": payload, "plcPath": self.plc, **top})
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            require(self.state() == before, "Refused preparation changed the PLC software")
            return error
        self.h.stage("refuse %s: %s" % (kind, label), check)
        self.mark(kind, "refused", code)

    def refuse_apply(self, kind, label, prepared, code, **overrides):
        """A refused apply leaves the PLC software unchanged and the change prepared, without stages or checkpoint."""
        def check():
            before = self.state()
            error = self.h.call_error("change_apply", {**self.h.apply_args(prepared), **overrides})
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            status = self.h.tool("change_status", {"changeId": prepared["changeId"]})
            require(status["state"] == "prepared" and not status["stages"] and not status["recoveryArchive"] and self.state() == before,
                    "Refused apply changed the change state or the PLC software")
            return {"error": error, "state": status["state"]}
        self.h.stage("refuse apply %s: %s" % (kind, label), check)
        self.mark(kind, "refused", code)

    def restore(self, label, kind, prepared):
        recovered = self.h.stage("restore " + label, lambda: self.h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
        require(recovered["state"] == "restored" and "checkpoint_retrieved_to_new_directory" in recovered["stages"], "Checkpoint recovery of %s was not verified" % label)
        path = recovered["recoveredProject"]["path"]
        require(Path(path).resolve() != Path(self.project).resolve(), "Recovery reused the changed project's directory")
        self.project = path
        self.current = self.snap("restored " + label)
        def equal():
            require([p["path"] for p in self.h.tool("tia_session_status")["projects"]] == [path], "The recovered copy is not the only open project")
            require(self.current["view"] == self.b0["view"], "Recovered state differs: %s" % diff(self.b0["view"], self.current["view"]))
            return len(self.b0["view"])
        self.h.stage("restored " + label + " equals the pre-change state", equal)
        self.mark(kind, "restored", label)


def tags(run, default, empty_xml):
    t = run.table
    run.mutate("create table " + TABLE, "tag_table_import_xml", {"xml": tag_fixture(empty_xml, TABLE, "X11Seed", "%M30.0"), "expectedTableNames": [TABLE]},
               {"effects": [{"importTagTables": [TABLE]}], "backups": []},
               added=[run.tbl(), run.tag("X11Seed")], rows={run.tag("X11Seed"): {"dataType": "Bool", "logicalAddress": "%M30.0"}})
    for kind, name, dtype, detail in (("tag_create", "X11Ref", "Bool", "%M30.1"), ("tag_create", "X11Free", "Int", "%MW32"), ("tag_create", "X11Gone", "Bool", "%M30.2"),
                                      ("constant_create", "X11LimitC", "Int", "10"), ("constant_create", "X11FreeC", "Int", "5"), ("constant_create", "X11GoneC", "Int", "1")):
        create_member(run, kind, name, dtype, detail)
    system = [row for row in default["constants"] if row.get("kind") == "system"]
    require(system, "The default tag table exposes no system constant")
    run.refuse("tag_create", "a name used in another table", {"tagTablePath": default["path"], "name": "X11Ref", "dataTypeName": "Bool", "logicalAddress": "%M31.0"}, "ObjectExists")
    run.refuse("constant_create", "a name differing only in case from a tag", {**t, "name": "x11ref", "dataTypeName": "Int", "value": "1"}, "ObjectExists")
    run.refuse("tag_create", "a missing tag table", {"tagTableName": "X11Missing", "name": "X11Other", "dataTypeName": "Bool", "logicalAddress": "%M31.0"}, "ObjectNotFound")
    run.refuse("constant_set", "a system constant", {"tagTablePath": default["path"], "memberName": system[0]["name"], "attributes": {"Value": "1"}}, "ObjectNotFound")


def create_member(run, kind, name, dtype, detail, restore=False):
    field, key = ("value", run.const(name)) if kind == "constant_create" else ("logicalAddress", run.tag(name))
    run.mutate("create " + name, kind, {**run.table, "name": name, "dataTypeName": dtype, field: detail},
               {"effects": [{"action": kind, "tableName": TABLE, "name": name, "dataTypeName": dtype, field: detail}], "backups": [{"tableName": TABLE}]},
               added=[key], changed=[run.tbl()], rows={key: {"name": name, "dataType": dtype, field: detail}}, restore=restore)


def delete_member(run, kind, name, restore=False):
    key = run.const(name) if kind == "constant_delete" else run.tag(name)
    run.mutate("delete " + name, kind, {**run.table, "memberName": name, **POLICY},
               {"effects": [], "dependencies": NO_REFERENCES, "backups": [{"tableName": TABLE}]}, removed=[key], changed=[run.tbl()], restore=restore)


def seed_types_and_blocks(run):
    names = ["X11Used", "X11Rename", "X11Spare"]
    run.mutate("create types", "type_source_upsert", {"source": "".join(map(udt, names)), "expectedTypeNames": names},
               {"effects": [{"dataType": n, "action": "create"} for n in names]}, added=[run.typ(n) for n in names])
    run.mutate("add a member to X11Rename", "type_source_upsert", {"source": udt("X11Rename", "Value : Int;\n      Extra : Bool;"), "expectedTypeNames": ["X11Rename"]},
               {"effects": [{"dataType": "X11Rename", "action": "update"}]}, changed=[run.typ("X11Rename")], xml={run.typ("X11Rename"): 'Name="Extra"'})
    run.refuse("type_source_upsert", "a declaration outside expectedTypeNames", {"source": udt("X11Other"), "expectedTypeNames": ["X11Spare"]}, "SourceScopeMismatch")
    names = ["X11Data", "X11Reader", "X11Edit", "X11Drop"]
    run.mutate("create blocks", "source_upsert", {"language": "SCL", "source": DATA + function("X11Reader", '"X11Ref" := "X11LimitC" > 0;') + EDIT + drop(1), "expectedBlockNames": names},
               {"effects": [{"action": "create", "blockName": n} for n in names], "language": "SCL"},
               added=[run.blk(n) for n in names], rows={run.blk(n): {"isConsistent": True, "isKnowHowProtected": False} for n in names})
    # X11Reader reads X11Ref and X11LimitC, X11Data declares X11Used: the conservative dependency scan must refuse these deletions.
    run.refuse("tag_delete", "a tag X11Reader reads", {**run.table, "memberName": "X11Ref", **POLICY}, "DependencyExists")
    run.refuse("constant_delete", "a constant X11Reader reads", {**run.table, "memberName": "X11LimitC", **POLICY}, "DependencyExists")
    run.refuse("tag_delete", "no dependency policy", {**run.table, "memberName": "X11Gone"}, "DependencyPolicyRequired")
    run.refuse("type_delete", "a type X11Data declares", {"typeName": "X11Used", **POLICY}, "DependencyExists")
    run.refuse("type_delete", "no dependency policy", {"typeName": "X11Spare"}, "DependencyPolicyRequired")


def edit_tags(run, empty_xml):
    t = run.table
    stale = run.prepare("readdress X11Free (made stale below)", "tag_set", {**t, "memberName": "X11Free", "attributes": {"LogicalAddress": "%MW40"}},
                        {"effects": [member_effect("X11Free", ("LogicalAddress", "%MW32", "%MW40"))]})
    label = "retype X11Free as X11Word"
    prepared = run.prepare(label, "tag_set", {**t, "memberName": "X11Free", "attributes": {"Name": "X11Word", "DataTypeName": "Word", "LogicalAddress": "%MW36"}},
                           {"effects": [member_effect("X11Free", ("Name", "X11Free", "X11Word"), ("DataTypeName", "Int", "Word"), ("LogicalAddress", "%MW32", "%MW36"))],
                            "backups": [{"tableName": TABLE}]})
    run.refuse_apply("tag_set", "a wrong previewHash", prepared, "PreviewMismatch", previewHash="0" * 64)
    run.refuse_apply("tag_set", "compile=false", prepared, "InvalidRequest", compile=False, save=False)
    run.finish(label, "tag_set", prepared, added=[run.tag("X11Word")], removed=[run.tag("X11Free")], changed=[run.tbl()],
               rows={run.tag("X11Word"): {"name": "X11Word", "dataType": "Word", "logicalAddress": "%MW36"}})
    run.refuse_apply("tag_set", "a preview made stale by a later change", stale, "StaleRevision")
    run.refuse("tag_set", "a name a constant uses", {**t, "memberName": "X11Word", "attributes": {"Name": "X11LimitC"}}, "ObjectExists")
    run.refuse("tag_set", "an attribute outside the allowlist", {**t, "memberName": "X11Word", "attributes": {"ExternalWritable": False}}, "UnsupportedAttribute")
    run.mutate("rename X11FreeC with value 6", "constant_set", {**t, "memberName": "X11FreeC", "attributes": {"Name": "X11RenamedC", "Value": "6"}},
               {"effects": [member_effect("X11FreeC", ("Name", "X11FreeC", "X11RenamedC"), ("Value", "5", "6"))]},
               added=[run.const("X11RenamedC")], removed=[run.const("X11FreeC")], changed=[run.tbl()], rows={run.const("X11RenamedC"): {"dataType": "Int", "value": "6", "kind": "user"}})
    delete_member(run, "tag_delete", "X11Gone")
    delete_member(run, "constant_delete", "X11GoneC")
    moved = replace_once(run.xml(run.tbl()), "<LogicalAddress>%M30.0</LogicalAddress>", "<LogicalAddress>%M34.0</LogicalAddress>")
    run.mutate("readdress X11Seed by table import", "tag_table_import_xml", {"xml": moved, "expectedTableNames": [TABLE]},
               {"effects": [{"importTagTables": [TABLE]}], "backups": [{"tableName": TABLE}]},
               changed=[run.tbl(), run.tag("X11Seed")], rows={run.tag("X11Seed"): {"logicalAddress": "%M34.0"}})
    run.refuse("tag_table_import_xml", "a new table whose tag X11Ref collides", {"xml": tag_fixture(empty_xml, "X11Clash", "X11Ref", "%M50.0"), "expectedTableNames": ["X11Clash"]}, "TagScopeMismatch")
    run.refuse("tag_table_import_xml", "a table outside expectedTableNames", {"xml": moved, "expectedTableNames": ["X11Other"]}, "XmlScopeMismatch")


def edit_types(run):
    run.refuse("type_rename", "a name differing only in case from X11Used", {"typeName": "X11Rename", "newName": "x11used"}, "ObjectExists")
    run.refuse("type_rename", "a missing type", {"typeName": "X11Missing", "newName": "X11Other"}, "ObjectNotFound")
    run.mutate("rename X11Rename", "type_rename", {"typeName": "X11Rename", "newName": "X11Renamed"},
               {"effects": [{"dataType": "X11Rename", "action": "update"}, {"renameTypeTo": "X11Renamed"}]},
               added=[run.typ("X11Renamed")], removed=[run.typ("X11Rename")], xml={run.typ("X11Renamed"): 'Name="Extra"'})
    spare = run.xml(run.typ("X11Spare"))
    update = {"effects": [{"dataType": "X11Spare", "action": "update"}]}
    run.mutate("reimport X11Spare unchanged", "plc_type_import_xml", {"xml": spare, "expectedTypeNames": ["X11Spare"]}, update)
    run.refuse("plc_type_import_xml", "a type outside expectedTypeNames", {"xml": spare, "expectedTypeNames": ["X11Other"]}, "XmlScopeMismatch")
    run.mutate("widen X11Spare by type import", "plc_type_import_xml", {"xml": replace_once(spare, 'Datatype="Int"', 'Datatype="DInt"'), "expectedTypeNames": ["X11Spare"]},
               update, changed=[run.typ("X11Spare")], xml={run.typ("X11Spare"): 'Datatype="DInt"'})
    run.mutate("delete X11Spare", "type_delete", {"typeName": "X11Spare", **POLICY}, {**update, "dependencies": NO_REFERENCES}, removed=[run.typ("X11Spare")])


def edit_blocks(run, gate):
    run.refuse("source_upsert", "a declaration outside expectedBlockNames", {"language": "SCL", "source": drop(2), "expectedBlockNames": ["X11Other"]}, "SourceScopeMismatch")
    run.refuse("source_upsert", "STL source for the SCL block X11Edit", {"language": "STL", "source": STL_EDIT, "expectedBlockNames": ["X11Edit"]}, "LanguageConversionNotSupported")
    run.mutate("rewrite X11Drop", "source_upsert", {"language": "SCL", "source": drop(2), "expectedBlockNames": ["X11Drop"]},
               {"effects": [{"action": "source_upsert", "existingBlock": run.info("X11Drop")}]}, changed=[run.blk("X11Drop")])
    data, existing = run.xml(run.blk("X11Data")), {"effects": [{"action": "block_import_xml", "existingBlock": run.info("X11Data")}]}
    run.mutate("reimport X11Data unchanged", "block_import_xml", {"xml": data, "expectedBlockNames": ["X11Data"]}, existing)
    run.refuse("block_import_xml", "a copy keeping X11Data's number", {"xml": replace_once(data, "<Name>X11Data</Name>", "<Name>X11Copy</Name>"), "expectedBlockNames": ["X11Copy"]}, "BlockNumberCollision")
    run.refuse("block_import_xml", "a block outside expectedBlockNames", {"xml": data, "expectedBlockNames": ["X11Other"]}, "XmlScopeMismatch")
    run.mutate("raise X11Data.Count's start value", "block_import_xml", {"xml": replace_once(data, "<StartValue>4711</StartValue>", "<StartValue>4712</StartValue>"), "expectedBlockNames": ["X11Data"]},
               existing, changed=[run.blk("X11Data")], rows={run.blk("X11Data"): {"number": run.info("X11Data")["number"]}}, xml={run.blk("X11Data"): "<StartValue>4712</StartValue>"})
    run.refuse("block_attributes_set", "an OB attribute on an FB", {"blockName": "X11Edit", "attributes": {"PriorityNumber": 2}}, "UnsupportedAttribute")
    run.refuse("block_attributes_set", "no attributes", {"blockName": "X11Edit", "attributes": {}}, "InvalidRequest")
    run.mutate("set X11Edit's header", "block_attributes_set", {"blockPath": run.plc + "/blocks/X11Edit", "attributes": {"HeaderAuthor": "X11Auth", "HeaderFamily": "X11Fam"}},
               {"effects": [{"action": "block_attributes_set", "existingBlock": run.info("X11Edit")},
                            {"attributes": [{"name": "HeaderAuthor", "after": "X11Auth"}, {"name": "HeaderFamily", "after": "X11Fam"}]}]},
               changed=[run.blk("X11Edit")], xml={run.blk("X11Edit"): ["<HeaderAuthor>X11Auth</HeaderAuthor>", "<HeaderFamily>X11Fam</HeaderFamily>"]})
    run.refuse("block_number_set", "the number of FB TiaMcp_EnableGate", {"blockName": "X11Edit", "number": gate["number"]}, "BlockNumberCollision")
    run.refuse("block_number_set", "number 0", {"blockName": "X11Edit", "number": 0}, "InvalidRequest")
    edit = run.info("X11Edit")
    run.mutate("renumber X11Edit to 77", "block_number_set", {"blockName": "X11Edit", "number": 77},
               {"effects": [{"action": "block_number_set", "existingBlock": edit},
                            {"attributes": [{"name": "AutoNumber", "after": False}, {"name": "Number", "before": edit["number"], "after": 77}]}]},
               changed=[run.blk("X11Edit")], rows={run.blk("X11Edit"): {"number": 77}})
    run.refuse("block_rename", "an existing block name", {"blockName": "X11Edit", "newName": "TiaMcp_IsEnabled"}, "ObjectExists")
    run.refuse("block_rename", "a missing block", {"blockName": "X11Missing", "newName": "X11Other"}, "BlockNotFound")
    run.mutate("rename X11Edit", "block_rename", {"blockName": "X11Edit", "newName": "X11Edited"},
               {"effects": [{"action": "block_rename", "existingBlock": run.info("X11Edit")}, {"renameTo": "X11Edited"}]},
               added=[run.blk("X11Edited")], removed=[run.blk("X11Edit")], rows={run.blk("X11Edited"): {"name": "X11Edited", "number": 77}},
               xml={run.blk("X11Edited"): "<HeaderAuthor>X11Auth</HeaderAuthor>"})
    run.refuse("block_delete", "FB TiaMcp_EnableGate, used by its instance DB and Main", {"blockName": "TiaMcp_EnableGate", **POLICY}, "DependencyExists")
    run.mutate("delete X11Drop", "block_delete", {"blockName": "X11Drop", **POLICY},
               {"effects": [{"action": "block_delete", "existingBlock": run.info("X11Drop")}], "dependencies": NO_REFERENCES}, removed=[run.blk("X11Drop")])
    # Instance DBs of the scratch's own FB, which no later step renames or deletes.
    run.refuse("instance_db_create", "an existing block name", {"name": "X11Data", "instanceOfName": "TiaMcp_EnableGate"}, "ObjectExists")
    run.refuse("instance_db_create", "a missing FB", {"name": "X11GateDB", "instanceOfName": "X11Missing"}, "BlockNotFound")
    run.mutate("create an instance DB of TiaMcp_EnableGate", "instance_db_create", {"name": "X11GateDB", "instanceOfName": "TiaMcp_EnableGate"},
               {"effects": [{"action": "create", "blockType": "InstanceDB", "blockName": "X11GateDB", "instanceOfName": "TiaMcp_EnableGate"}]},
               added=[run.blk("X11GateDB")], rows={run.blk("X11GateDB"): {"kind": "InstanceDB"}})


def edit_groups(run):
    for root in ("blocks", "types", "tags"):
        base = run.plc + "/" + root
        title = "X11" + root.capitalize()
        outer, moved = base + "/groups/" + title + "Outer", base + "/groups/" + title + "Moved"
        run.group("create " + title + "Outer", "group_create", root, base, title + "Outer", added=[outer])
        run.group("create X11Inner in " + title + "Outer", "group_create", root, outer, "X11Inner", added=[outer + "/groups/X11Inner"])
        if root == "blocks":
            run.refuse("group_delete", "a user group holding a subgroup", {"groupKind": root, "groupPath": outer, **POLICY}, "GroupNotEmpty")
            run.refuse("group_delete", "no dependency policy", {"groupKind": root, "groupPath": outer + "/groups/X11Inner"}, "DependencyPolicyRequired")
            run.refuse("group_create", "a sibling name differing only in case", {"groupKind": root, "groupPath": base, "name": (title + "Outer").lower()}, "ObjectExists")
            run.refuse("group_create", "an unknown group kind", {"groupKind": "hardware", "groupPath": base, "name": "X11Other"}, "InvalidRequest")
            run.refuse("group_rename", "the root block group", {"groupKind": root, "groupPath": base, "name": "X11Root"}, "GeneratedObject")
            run.refuse("group_rename", "an invented group path", {"groupKind": root, "groupPath": base + "/groups/X11Missing", "name": "X11Other"}, "GroupNotFound")
        run.group("rename " + title + "Outer", "group_rename", root, outer, title + "Moved",
                  added=[moved, moved + "/groups/X11Inner"], removed=[outer, outer + "/groups/X11Inner"])
        run.group("delete X11Inner in " + title + "Moved", "group_delete", root, moved + "/groups/X11Inner", removed=[moved + "/groups/X11Inner"])


def compile_plc(run):
    run.refuse("plc_compile", "a non-empty payload", {"scopePath": run.plc}, "InvalidRequest")
    run.refuse("plc_compile", "a software scope", {}, "InvalidRequest", scopePath=run.plc)
    run.refuse("plc_compile", "no exact plcPath", {}, "InvalidRequest", plcPath=None)
    prepared = run.prepare("compile the PLC", "plc_compile", {}, {"effects": [{"action": "compile", "plcPath": run.plc}]})
    run.refuse_apply("plc_compile", "compile=false", prepared, "InvalidRequest", compile=False, save=False)
    run.finish("compile the PLC", "plc_compile", prepared)


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


def recover_each_kind(run):
    """From the reopened state, apply one change per kind and restore its checkpoint into a new directory."""
    t, h = run.table, run.h
    run.mutate("readdress X11Seed", "tag_set", {**t, "memberName": "X11Seed", "attributes": {"LogicalAddress": "%M38.0"}},
               {"effects": [member_effect("X11Seed", ("LogicalAddress", "%M34.0", "%M38.0"))]},
               changed=[run.tbl(), run.tag("X11Seed")], rows={run.tag("X11Seed"): {"logicalAddress": "%M38.0"}}, restore=True)
    create_member(run, "tag_create", "X11Again", "Bool", "%M38.1", restore=True)
    delete_member(run, "tag_delete", "X11Word", restore=True)
    run.mutate("set X11RenamedC to 7", "constant_set", {**t, "memberName": "X11RenamedC", "attributes": {"Value": "7"}},
               {"effects": [member_effect("X11RenamedC", ("Value", "6", "7"))]},
               changed=[run.tbl(), run.const("X11RenamedC")], rows={run.const("X11RenamedC"): {"value": "7"}}, restore=True)
    create_member(run, "constant_create", "X11AgainC", "Int", "3", restore=True)
    delete_member(run, "constant_delete", "X11RenamedC", restore=True)
    run.mutate("readdress X11Seed by table import", "tag_table_import_xml",
               {"xml": replace_once(run.xml(run.tbl()), "<LogicalAddress>%M34.0</LogicalAddress>", "<LogicalAddress>%M39.0</LogicalAddress>"), "expectedTableNames": [TABLE]},
               {"effects": [{"importTagTables": [TABLE]}], "backups": [{"tableName": TABLE}]},
               changed=[run.tbl(), run.tag("X11Seed")], rows={run.tag("X11Seed"): {"logicalAddress": "%M39.0"}}, restore=True)
    run.mutate("create type X11TypeNew", "type_source_upsert", {"source": udt("X11TypeNew"), "expectedTypeNames": ["X11TypeNew"]},
               {"effects": [{"dataType": "X11TypeNew", "action": "create"}]}, added=[run.typ("X11TypeNew")], restore=True)
    renamed = {"effects": [{"dataType": "X11Renamed", "action": "update"}]}
    run.mutate("rename X11Renamed", "type_rename", {"typeName": "X11Renamed", "newName": "X11TypeAgain"},
               {"effects": renamed["effects"] + [{"renameTypeTo": "X11TypeAgain"}]}, added=[run.typ("X11TypeAgain")], removed=[run.typ("X11Renamed")], restore=True)
    run.mutate("widen X11Renamed by type import", "plc_type_import_xml",
               {"xml": replace_once(run.xml(run.typ("X11Renamed")), 'Datatype="Int"', 'Datatype="DInt"'), "expectedTypeNames": ["X11Renamed"]},
               renamed, changed=[run.typ("X11Renamed")], xml={run.typ("X11Renamed"): 'Datatype="DInt"'}, restore=True)
    run.mutate("delete X11Renamed", "type_delete", {"typeName": "X11Renamed", **POLICY}, {**renamed, "dependencies": NO_REFERENCES},
               removed=[run.typ("X11Renamed")], restore=True)
    run.mutate("rewrite X11Reader", "source_upsert", {"language": "SCL", "source": function("X11Reader", '"X11Ref" := "X11LimitC" >= 0;'), "expectedBlockNames": ["X11Reader"]},
               {"effects": [{"action": "source_upsert", "existingBlock": run.info("X11Reader")}]}, changed=[run.blk("X11Reader")], restore=True)
    run.mutate("raise X11Data.Count's start value again", "block_import_xml",
               {"xml": replace_once(run.xml(run.blk("X11Data")), "<StartValue>4712</StartValue>", "<StartValue>4713</StartValue>"), "expectedBlockNames": ["X11Data"]},
               {"effects": [{"action": "block_import_xml", "existingBlock": run.info("X11Data")}]},
               changed=[run.blk("X11Data")], xml={run.blk("X11Data"): "<StartValue>4713</StartValue>"}, restore=True)
    edited = run.info("X11Edited")
    run.mutate("set X11Edited's family", "block_attributes_set", {"blockName": "X11Edited", "attributes": {"HeaderFamily": "X11B"}},
               {"effects": [{"action": "block_attributes_set", "existingBlock": edited}, {"attributes": [{"name": "HeaderFamily", "before": "X11Fam", "after": "X11B"}]}]},
               changed=[run.blk("X11Edited")], xml={run.blk("X11Edited"): "<HeaderFamily>X11B</HeaderFamily>"}, restore=True)
    run.mutate("renumber X11Edited to 78", "block_number_set", {"blockName": "X11Edited", "number": 78},
               {"effects": [{"action": "block_number_set", "existingBlock": edited}, {"attributes": [{"name": "AutoNumber"}, {"name": "Number", "before": 77, "after": 78}]}]},
               changed=[run.blk("X11Edited")], rows={run.blk("X11Edited"): {"number": 78}}, restore=True)
    run.mutate("rename X11Edited", "block_rename", {"blockName": "X11Edited", "newName": "X11BlockAgain"},
               {"effects": [{"action": "block_rename", "existingBlock": edited}, {"renameTo": "X11BlockAgain"}]},
               added=[run.blk("X11BlockAgain")], removed=[run.blk("X11Edited")], rows={run.blk("X11BlockAgain"): {"number": 77}}, restore=True)
    run.mutate("delete X11Edited", "block_delete", {"blockName": "X11Edited", **POLICY},
               {"effects": [{"action": "block_delete", "existingBlock": edited}], "dependencies": NO_REFERENCES}, removed=[run.blk("X11Edited")], restore=True)
    run.group("create X11GroupAgain", "group_create", "blocks", run.plc + "/blocks", "X11GroupAgain", added=[run.plc + "/blocks/groups/X11GroupAgain"], restore=True)
    groups = run.plc + "/tags/groups/"
    run.group("rename X11TagsMoved", "group_rename", "tags", groups + "X11TagsMoved", "X11TagsAgain",
              added=[groups + "X11TagsAgain"], removed=[groups + "X11TagsMoved"], restore=True)
    run.group("delete X11TypesMoved", "group_delete", "types", run.plc + "/types/groups/X11TypesMoved", removed=[run.plc + "/types/groups/X11TypesMoved"], restore=True)
    run.mutate("create another instance DB of TiaMcp_EnableGate", "instance_db_create", {"name": "X11GateAgainDB", "instanceOfName": "TiaMcp_EnableGate"},
               {"effects": [{"action": "create", "blockType": "InstanceDB", "blockName": "X11GateAgainDB", "instanceOfName": "TiaMcp_EnableGate"}]},
               added=[run.blk("X11GateAgainDB")], rows={run.blk("X11GateAgainDB"): {"kind": "InstanceDB"}}, restore=True)
    run.mutate("compile the PLC again", "plc_compile", {}, {"effects": [{"action": "compile", "plcPath": run.plc}]}, restore=True)

    # A partial failure: X11Ref beyond the CPU's bit memory; the outcome is recorded and recovered, never replayed.
    label = "readdress X11Ref beyond the bit memory"
    prepared = run.prepare(label, "tag_set", {**t, "memberName": "X11Ref", "attributes": {"LogicalAddress": "%M20000.0"}},
                           {"effects": [member_effect("X11Ref", ("LogicalAddress", "%M30.1", "%M20000.0"))]})
    def dispatch():
        row = {"kind": "tag_set", "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "state": "dispatching"}
        h.report["mutations"].append(row)
        h.persist()
        outcome = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
        status = h.tool("change_status", {"changeId": prepared["changeId"]})
        row["state"] = status["state"]
        failed = outcome.get("isError") is True
        require(status["state"] == ("recovery_required" if failed else "applied") and status["recoveryArchiveSha256"],
                "The apply outcome must retain its checkpoint: %s" % status["state"])
        return {"isError": failed, "error": (outcome.get("structuredContent") or {}).get("error"), "state": status["state"],
                "stages": status["stages"], "compile": status["compile"], "failure": status["failure"]}
    h.report["nativeBound"] = h.stage("apply " + label, dispatch)
    if not h.report["nativeBound"]["isError"]:
        run.restore(label, "tag_set", prepared)
        return
    # A failed compile leaves an inconsistent user block, which the recovery guard deliberately cannot fingerprint;
    # change_restore then refuses rather than accept a weaker guard. The checkpoint stays retained (checked above).
    def refused():
        error = h.call_error("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        require(error.get("code") == "FingerprintUnavailable", "Expected the inconsistent-block recovery refusal: %s" % error)
        return error
    h.report["nativeBound"]["restoreRefusal"] = h.stage("restore " + label + " is refused on the inconsistent block", refused)


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("plc object mutations")
    run.project = h.tool("tia_session_status")["projects"][0]["path"]
    run.current = run.snap("baseline")
    default = empty_tag_template(run.current["inventory"], run.plc)
    empty_xml = run.xml("tagTable:" + default["path"])
    gate = next(row for key, row in run.current["view"].items() if key.startswith("block:") and row["name"] == "TiaMcp_EnableGate")
    tags(run, default, empty_xml)
    seed_types_and_blocks(run)
    edit_tags(run, empty_xml)
    edit_types(run)
    edit_blocks(run, gate)
    edit_groups(run)
    compile_plc(run)
    reopen(run)
    recover_each_kind(run)
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
    Harness(args, "x11_", "X11 PLC object mutations in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
