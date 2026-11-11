"""Qualify payload.items natively in one owned scratch CPU: every batched kind, and a tag_set batch on tags a block uses.

Each batch must apply under one checkpoint archive, one mutation stage, one compile and one save, list every item in its
preview and leave exactly the intended names. Covered: tag_set (tags referenced by a compiled SCL block, whose source must
name the new tags afterwards), tag_create, constant_create, constant_set, constant_delete, tag_delete, tag_table_create,
tag_table_rename, block_rename, block_attributes_set, type_rename, block_number_set, group_create and group_rename, the tag
kinds again inside a software unit with scopePath beside items, and the block and type kinds in a unit that holds one name
in a namespace and in none. A batch whose second item TIA refuses natively, after the first was written, must end recovery_required, block
further changes, and restore to the state before the batch. Refused batches (one object in two items, one name or block
number from two items, a name that exists, an unknown object in a later item, a group inside a group another item renames,
a name two namespaces hold given without namespace, items on a kind that does not batch, fields beside items) must name the
item and leave the software unchanged.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from library_native_harness import Harness, require

TABLE = "BatchTags"
NS = "BatchNs"
LIMITATIONS = ["One owned scratch standard CPU: PLC-root scope, one software unit (tag kinds only) and one unit with one namespace (block and type kinds).",
               "The safety-tag and F-I/O refusals of a batch need an F-CPU fixture (qualify_fio_tag_routes.py covers the F-I/O address refusal).",
               "The batch that fails after its first write is whichever candidate TIA refuses natively at its second item (a name with a double quote); see midwayFailureCandidates."]
STAGES = ("recovery_archive_created", "baseline_compile_finished", "mutation_applied", "compile_finished", "changed_project_saved")


def scl_fc(name, body=";"):
    return "\n".join(['FUNCTION "%s" : Void' % name, "{ S7_Optimized_Access := 'TRUE' }", "VERSION : 0.1", "BEGIN", "\t" + body, "END_FUNCTION", ""])


def scl_type(name):
    return "\n".join(['TYPE "%s"' % name, "VERSION : 0.1", "   STRUCT", "      a : Int;", "   END_STRUCT;", "", "END_TYPE", ""])


def body(h):
    plc = h.owned_scratch("change batch")
    p = {"plcPath": plc}

    def software():
        view = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})
        tables = {t["name"]: {"tags": sorted(g["name"] for g in t.get("tags", [])),
                              "constants": sorted(c["name"] for c in t.get("constants", []) if c.get("kind") == "user")} for t in view["tagTables"]}
        return {"tables": tables, "blocks": sorted(b["name"] for b in view["blocks"]), "types": sorted(t["name"] for t in view["types"]),
                "numbers": {b["path"]: b.get("number") for b in view["blocks"]} | {b["name"]: b.get("number") for b in view["blocks"]},
                "groups": sorted(path for scope in view["softwareScopes"] for path in scope["groups"])}

    def apply(label, kind, payload, count=None):
        prepared = h.stage("prepare " + label, lambda: h.tool("change_prepare", {"kind": kind, "payload": payload, **p}))
        if count is not None:
            rows = prepared["preview"]["items"]
            require([row["index"] for row in rows] == list(range(count)), "The preview of %s does not list %d items in order" % (label, count))
        applied = h.apply_prepared(kind, prepared)
        for stage in STAGES:
            # A tag-table import names its baseline compile tag_table_baseline_compile_finished.
            require(sum(1 for done in applied["stages"] if done in (stage, "tag_table_" + stage)) == 1,
                    "Stage %s did not run exactly once for %s: %s" % (stage, label, applied["stages"]))
        require(applied["recoveryArchiveSha256"] and applied["progress"]["projectEditComplete"] is True, "%s is not applied and saved with a checkpoint" % label)
        if count is not None:
            require(any(stage.endswith("_verified") or stage.endswith("_verified_after_save") for stage in applied["stages"]), "No readback stage for " + label)
        return applied

    def refuse(label, kind, payload, code, starts=None):
        def check():
            before = software()
            error = h.call_error("change_prepare", {"kind": kind, "payload": payload, **p})
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            require(starts is None or error.get("message", "").startswith(starts), "The refusal does not name its item: %s" % error.get("message"))
            require(software() == before, "A refused batch changed the PLC software")
            return error.get("message")
        h.stage("refuse %s batch: %s" % (kind, label), check)

    def expect(label, check):
        def verify():
            now = software()
            require(check(now), "%s reads back differently: %s" % (label, json.dumps(now)[:900]))
        h.stage("readback " + label, verify)

    tags = [{"name": "Bt%d" % i, "dataType": "Bool", "logicalAddress": "%%M220.%d" % i} for i in range(6)]
    apply("seed tags", "tag_table_import_xml", {"tagTableName": TABLE, "tags": tags})
    apply("seed a block that uses two tags", "source_upsert", {"language": "SCL", "source": scl_fc("UseTags", '"Bt0" := "Bt1";'), "expectedBlockNames": ["UseTags"]})

    def item(member, **attributes):
        return {"tagTableName": TABLE, "memberName": member, "attributes": attributes}

    # tag_set on referenced tags: the native rename must carry the block's references.
    apply("rename two referenced tags", "tag_set", {"items": [item("Bt0", Name="Bt0_R"), item("Bt1", Name="Bt1_R")]}, 2)
    expect("renamed referenced tags", lambda s: {"Bt0_R", "Bt1_R"} <= set(s["tables"][TABLE]["tags"]) and not {"Bt0", "Bt1"} & set(s["tables"][TABLE]["tags"]))

    def references():
        block = next(b for b in h.tool("software_inspect", {"plcPath": plc, "include": "blocks", "maxItems": 500})["blocks"] if b["name"] == "UseTags")
        require(block["isConsistent"] is True, "The block using the renamed tags is not consistent after the batch")
        text = json.dumps(h.tool("block_read", {"blockPath": block["path"], "render": "text"}))
        require("Bt0_R" in text and "Bt1_R" in text, "The block does not name the renamed tags")
        return "UseTags names Bt0_R and Bt1_R"
    h.stage("the block follows the renamed tags", references)

    # Tags and constants.
    create = lambda name, address: {"tagTableName": TABLE, "name": name, "dataTypeName": "Bool", "logicalAddress": address}
    apply("create three tags", "tag_create", {"items": [create("Bn0", "%M221.0"), create("Bn1", "%M221.1"), create("Bn2", "%M221.2")]}, 3)
    constant = lambda name, value: {"tagTableName": TABLE, "name": name, "dataTypeName": "Int", "value": value}
    apply("create two constants", "constant_create", {"items": [constant("Bc0", "1"), constant("Bc1", "2")]}, 2)
    apply("edit two constants", "constant_set", {"items": [item("Bc0", Name="Bc0_R"), item("Bc1", Value="5")]}, 2)
    expect("tags and constants", lambda s: {"Bn0", "Bn1", "Bn2"} <= set(s["tables"][TABLE]["tags"]) and s["tables"][TABLE]["constants"] == ["Bc0_R", "Bc1"])

    # Refusals: nothing may change.
    refuse("one tag in two items", "constant_set", {"items": [item("Bc1", Value="6"), item("Bc1", Name="Bc1_R")]}, "InvalidRequest", "items[0] and items[1] ")
    refuse("one name from two items", "tag_create", {"items": [create("Dup", "%M222.0"), create("dup", "%M222.1")]}, "ObjectExists", "items[0] and items[1] ")
    refuse("a name that exists", "tag_create", {"items": [create("Fresh", "%M222.0"), create("Bn0", "%M222.1")]}, "ObjectExists", "items[1]: ")
    refuse("an unknown constant in a later item", "constant_set", {"items": [item("Bc1", Value="7"), item("Missing", Value="1")]}, "ObjectNotFound", "items[1]: ")
    refuse("a kind that does not batch", "block_delete", {"items": [{"blockName": "UseTags"}]}, "InvalidRequest")
    refuse("fields beside items", "tag_create", {"items": [create("Fresh", "%M222.0")], "tagTableName": TABLE}, "InvalidRequest")

    delete = lambda member: {"tagTableName": TABLE, "memberName": member, "dependencyPolicy": "rejectReferenced"}
    apply("delete two constants", "constant_delete", {"items": [delete("Bc0_R"), delete("Bc1")]}, 2)
    apply("delete two unused tags", "tag_delete", {"items": [delete("Bn1"), delete("Bn2")]}, 2)
    expect("deleted members", lambda s: s["tables"][TABLE]["constants"] == [] and not {"Bn1", "Bn2"} & set(s["tables"][TABLE]["tags"]) and "Bn0" in s["tables"][TABLE]["tags"])

    # Tag tables, blocks, types.
    apply("create two tag tables", "tag_table_create", {"items": [{"tagTableName": "BtA"}, {"tagTableName": "BtB"}]}, 2)
    apply("rename two tag tables", "tag_table_rename", {"items": [{"tagTableName": "BtA", "newName": "BtA_R"}, {"tagTableName": "BtB", "newName": "BtB_R"}]}, 2)
    expect("tag tables", lambda s: {"BtA_R", "BtB_R"} <= set(s["tables"]) and not {"BtA", "BtB"} & set(s["tables"]))
    apply("seed two blocks", "source_upsert", {"language": "SCL", "source": scl_fc("BbA") + scl_fc("BbB"), "expectedBlockNames": ["BbA", "BbB"]})
    apply("rename two blocks", "block_rename", {"items": [{"blockName": "BbA", "newName": "BbA_R"}, {"blockName": "BbB", "newName": "BbB_R"}]}, 2)
    apply("set two block headers", "block_attributes_set", {"items": [{"blockName": "BbA_R", "attributes": {"HeaderAuthor": "batch"}},
                                                                      {"blockName": "BbB_R", "attributes": {"HeaderFamily": "batch"}}]}, 2)
    expect("blocks", lambda s: {"BbA_R", "BbB_R"} <= set(s["blocks"]) and not {"BbA", "BbB"} & set(s["blocks"]))
    apply("seed two data types", "type_source_upsert", {"source": scl_type("ByA") + scl_type("ByB"), "expectedTypeNames": ["ByA", "ByB"]})
    apply("rename two data types", "type_rename", {"items": [{"typeName": "ByA", "newName": "ByA_R"}, {"typeName": "ByB", "newName": "ByB_R"}]}, 2)
    expect("data types", lambda s: {"ByA_R", "ByB_R"} <= set(s["types"]) and not {"ByA", "ByB"} & set(s["types"]))

    # Block numbers: no two items may take one number of one block kind, and a number another item frees is still taken.
    number = lambda name, value: {"blockName": name, "number": value}
    apply("number two blocks", "block_number_set", {"items": [number("BbA_R", 741), number("BbB_R", 742)]}, 2)
    expect("block numbers", lambda s: s["numbers"]["BbA_R"] == 741 and s["numbers"]["BbB_R"] == 742)
    refuse("one number for two blocks of one kind", "block_number_set", {"items": [number("BbA_R", 750), number("BbB_R", 750)]}, "BlockNumberCollision", "items[0] and items[1] ")
    refuse("a number another item frees", "block_number_set", {"items": [number("BbA_R", 742), number("BbB_R", 743)]}, "BlockNumberCollision", "items[0]: ")
    refuse("one block in two items", "block_number_set", {"items": [number("BbA_R", 760), number("BbA_R", 761)]}, "InvalidRequest", "items[0] and items[1] ")

    # Groups: created and renamed per parent; a renamed group may not contain another item's group.
    for root in ("blocks", "types", "tags"):
        base, title = plc + "/" + root, "Bg" + root.capitalize()
        group = lambda path, name, root=root: {"groupKind": root, "groupPath": path, "name": name}
        a, b = base + "/groups/" + title + "A", base + "/groups/" + title + "B"
        apply("create two %s groups" % root, "group_create", {"items": [group(base, title + "A"), group(base, title + "B")]}, 2)
        apply("create a nested %s group of one name in each" % root, "group_create", {"items": [group(a, "Inner"), group(b, "Inner")]}, 2)
        expect("created %s groups" % root, lambda s, a=a, b=b: {a, b, a + "/groups/Inner", b + "/groups/Inner"} <= set(s["groups"]))
        if root == "blocks":
            refuse("one group name twice under one parent", "group_create", {"items": [group(a, "Dup"), group(a, "dup")]}, "ObjectExists", "items[0] and items[1] ")
            refuse("a group under a group the same batch creates", "group_create",
                   {"items": [group(base, title + "C"), group(base + "/groups/" + title + "C", "Inner")]}, "GroupNotFound", "items[1]: ")
            refuse("a group inside a group another item renames", "group_rename",
                   {"items": [group(a + "/groups/Inner", "Inner_R"), group(a, title + "A_R")]}, "InvalidRequest", "items[0] renames a group inside the group that items[1] renames")
            refuse("two sibling groups renamed to one name", "group_rename", {"items": [group(a, title + "X"), group(b, title + "x")]}, "ObjectExists", "items[0] and items[1] ")
            refuse("one group in two items", "group_rename", {"items": [group(a, title + "X"), group(a, title + "Y")]}, "InvalidRequest", "items[0] and items[1] ")
        apply("rename two %s groups" % root, "group_rename", {"items": [group(a, title + "A_R"), group(b, title + "B_R")]}, 2)
        apply("rename the nested %s groups to one name under two parents" % root, "group_rename",
              {"items": [group(a + "_R/groups/Inner", "Inner_R"), group(b + "_R/groups/Inner", "Inner_R")]}, 2)
        expect("renamed %s groups" % root, lambda s, a=a, b=b: {a + "_R", b + "_R", a + "_R/groups/Inner_R", b + "_R/groups/Inner_R"} <= set(s["groups"])
               and not {a, b, a + "_R/groups/Inner", b + "_R/groups/Inner"} & set(s["groups"]))

    # A software unit: the same batches with scopePath beside items.
    apply("create a software unit", "software_unit_create", {"name": "BatchUnit"})
    scopes = [u["path"] for u in h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["softwareScopes"] if u["name"] == "BatchUnit"]
    require(len(scopes) == 1, "Expected one software unit BatchUnit: %s" % scopes)
    unit = scopes[0]
    apply("unit: create a tag table", "tag_table_create", {"tagTableName": "UnitTags", "scopePath": unit})
    unit_tag = lambda name, address: {"tagTableName": "UnitTags", "name": name, "dataTypeName": "Bool", "logicalAddress": address}
    apply("unit: create three tags", "tag_create", {"scopePath": unit, "items": [unit_tag("Ut0", "%M240.0"), unit_tag("Ut1", "%M240.1"), unit_tag("Ut2", "%M240.2")]}, 3)
    unit_item = lambda member, **attributes: {"tagTableName": "UnitTags", "memberName": member, "attributes": attributes}
    apply("unit: rename two tags", "tag_set", {"scopePath": unit, "items": [unit_item("Ut0", Name="Ut0_R"), unit_item("Ut1", Name="Ut1_R")]}, 2)
    apply("unit: create two constants", "constant_create", {"scopePath": unit, "items": [{"tagTableName": "UnitTags", "name": "Uc0", "dataTypeName": "Int", "value": "1"},
                                                                                         {"tagTableName": "UnitTags", "name": "Uc1", "dataTypeName": "Int", "value": "2"}]}, 2)
    apply("unit: delete one tag and keep the rest", "tag_delete", {"scopePath": unit, "items": [{"tagTableName": "UnitTags", "memberName": "Ut2", "dependencyPolicy": "rejectReferenced"}]}, 1)
    other = dict(unit_tag("UtX", "%M240.5"), scopePath=plc)
    refuse("an item naming another scope than the batch", "tag_create", {"scopePath": unit, "items": [unit_tag("UtY", "%M240.4"), other]}, "ScopeMismatch", "items[1] ")
    expect("the unit's table", lambda s: s["tables"].get("UnitTags") == {"tags": ["Ut0_R", "Ut1_R"], "constants": ["Uc0", "Uc1"]})

    # Namespaces: a unit that holds one block name and one type name in a namespace and in none. Blocks and types are
    # named per namespace, so an item selects its object with namespace beside the name, and two namespaces may take one name.
    apply("create a unit for namespaces", "software_unit_create", {"name": "BatchNsUnit"})
    scopes = [u["path"] for u in h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["softwareScopes"] if u["name"] == "BatchNsUnit"]
    require(len(scopes) == 1, "Expected one software unit BatchNsUnit: %s" % scopes)
    ns_unit = scopes[0]
    in_ns = lambda text: "NAMESPACE %s\n%sEND_NAMESPACE\n" % (NS, text)
    apply("namespace: seed a block outside the namespace", "source_upsert", {"scopePath": ns_unit, "language": "SCL", "source": scl_fc("NbSame"), "expectedBlockNames": ["NbSame"]})
    apply("namespace: seed three blocks in the namespace", "source_upsert",
          {"scopePath": ns_unit, "language": "SCL", "source": in_ns(scl_fc("NbA") + scl_fc("NbB") + scl_fc("NbSame")), "expectedBlockNames": ["NbA", "NbB", "NbSame"]})
    apply("namespace: seed a data type outside the namespace", "type_source_upsert", {"scopePath": ns_unit, "source": scl_type("NySame"), "expectedTypeNames": ["NySame"]})
    apply("namespace: seed two data types in the namespace", "type_source_upsert",
          {"scopePath": ns_unit, "source": in_ns(scl_type("NyA") + scl_type("NySame")), "expectedTypeNames": ["NyA", "NySame"]})

    def placed(collection):
        """Per object name in the namespace unit: whether each object of that name sits in the namespace (sorted)."""
        rows = {}
        for row in h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})[collection]:
            if row.get("scopePath") == ns_unit:
                rows.setdefault(row["name"], []).append("/namespaces/%s/" % NS in row["path"])
        return {name: sorted(flags) for name, flags in rows.items()}

    def placement(label, collection, wanted):
        def check():
            found = placed(collection)
            require(found == wanted, "%s are placed differently: %s" % (label, found))
            return found
        h.report.setdefault("namespacePlacement", {})[label] = h.stage("placement of " + label, check)

    placement("seeded blocks", "blocks", {"NbA": [True], "NbB": [True], "NbSame": [False, True]})
    placement("seeded data types", "types", {"NyA": [True], "NySame": [False, True]})
    block = lambda name, namespace, **more: {"blockName": name, "namespace": namespace, **more}
    data_type = lambda name, namespace, **more: {"typeName": name, "namespace": namespace, **more}
    in_unit = lambda *items: {"scopePath": ns_unit, "items": list(items)}
    apply("namespace: rename two blocks", "block_rename", in_unit(block("NbA", NS, newName="NbA_R"), block("NbB", NS, newName="NbB_R")), 2)
    refuse("a block name two namespaces hold, without namespace", "block_rename",
           in_unit(block("NbA_R", NS, newName="NbA_S"), {"blockName": "NbSame", "newName": "NbSame_X"}), "AmbiguousTarget", "items[1]: ")
    refuse("one namespaced block in two items", "block_attributes_set",
           in_unit(block("NbSame", NS, attributes={"HeaderAuthor": "one"}), block("NbSame", NS, attributes={"HeaderFamily": "two"})), "InvalidRequest", "items[0] and items[1] ")
    refuse("one new name twice in one namespace", "block_rename", in_unit(block("NbA_R", NS, newName="NbDup"), block("NbB_R", NS, newName="nbdup")), "ObjectExists", "items[0] and items[1] ")
    refuse("a name the namespace already holds", "block_rename", in_unit(block("NbA_R", NS, newName="NbA_S"), block("NbB_R", NS, newName="NbSame")), "ObjectExists", "items[1]: ")
    apply("namespace: set headers of one block name in two namespaces", "block_attributes_set",
          in_unit(block("NbSame", NS, attributes={"HeaderAuthor": "inside"}), block("NbSame", "", attributes={"HeaderAuthor": "outside"})), 2)

    # TIA V21 numbers a software unit's blocks itself: AutoNumber is not writable there (native run of 11 November), so
    # block_number_set refuses such a block at preparation, in a batch as in a single change.
    def unit_numbers():
        outcomes = {}
        for label, namespace, value in (("inNamespace", NS, 771), ("outsideNamespace", "", 772)):
            before = software()
            error = h.call_error("change_prepare", {"kind": "block_number_set", "payload": in_unit(block("NbSame", namespace, number=value)), **p})
            require(error.get("code") == "AttributeNotWritable" and error.get("message", "").startswith("items[0]: "),
                    "Expected AttributeNotWritable naming items[0] for a unit block %s: %s" % (label, error))
            require(software() == before, "A refused block_number_set batch changed the PLC software")
            outcomes[label] = error.get("message")
        return outcomes
    h.report["unitBlockNumbers"] = h.stage("refuse block_number_set batch: a software unit's block, in the namespace and outside it", unit_numbers)

    apply("namespace: rename one block name in two namespaces to one new name", "block_rename",
          in_unit(block("NbSame", NS, newName="NbSame_R"), block("NbSame", "", newName="NbSame_R")), 2)
    placement("renamed blocks", "blocks", {"NbA_R": [True], "NbB_R": [True], "NbSame_R": [False, True]})
    apply("namespace: rename one type name in two namespaces", "type_rename",
          in_unit(data_type("NySame", NS, newName="NySame_R"), data_type("NySame", "", newName="NySame_R"), data_type("NyA", NS, newName="NyA_R")), 3)
    placement("renamed data types", "types", {"NyA_R": [True], "NySame_R": [False, True]})

    # A batch that fails after its first write. Preparation and the preflight pass; TIA itself refuses the second item's
    # name when it is written. Which names TIA refuses is its own rule, so candidates are tried until one fails natively;
    # a candidate TIA accepts is simply an applied batch.
    bad = 'Bf"Bad'
    # Tag tables first: a block (or type) renamed by the first item stays inconsistent after the failure, and change_restore
    # then refuses by design with ScopedRecoveryRequired, leaving the retained archive as the recovery input.
    candidates = [
        ("tag_table_rename", {"items": [{"tagTableName": "BtA_R", "newName": "BfTableOk"}, {"tagTableName": "BtB_R", "newName": bad}]}),
        ("tag_create", {"items": [create("BfTagOk", "%M250.0"), create(bad, "%M250.1")]}),
        ("type_rename", {"items": [{"typeName": "ByA_R", "newName": "BfTypeOk"}, {"typeName": "ByB_R", "newName": bad}]}),
        ("block_rename", {"items": [{"blockName": "BbA_R", "newName": "BfBlockOk"}, {"blockName": "BbB_R", "newName": bad}]}),
    ]
    outcomes = h.report["midwayFailureCandidates"] = []
    failed = None
    for kind, payload in candidates:
        before = software()
        prepared = h.client.request("tools/call", {"name": "change_prepare", "arguments": {"kind": kind, "payload": payload, **p}})
        if prepared.get("isError"):
            outcomes.append({"kind": kind, "outcome": "refused at preparation", "code": ((prepared.get("structuredContent") or {}).get("error") or {}).get("code")})
            continue
        prepared = prepared["structuredContent"]
        row = {"kind": kind, "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "state": "dispatching"}
        h.report["mutations"].append(row)
        result = h.client.request("tools/call", {"name": "change_apply", "arguments": Harness.apply_args(prepared)})
        status = h.tool("change_status", {"changeId": prepared["changeId"], "includeDetails": True})
        row["state"] = status["state"]
        error = (result.get("structuredContent") or {}).get("error") or {}
        outcomes.append({"kind": kind, "outcome": status["state"], "code": error.get("code"), "message": (error.get("message") or "")[:300], "stages": status["stages"]})
        h.persist()
        if status["state"] == "recovery_required":
            failed = (kind, prepared, status, error, before)
            break
        require(status["state"] == "applied", "Unexpected outcome of a %s candidate: %s" % (kind, status["state"]))
    require(failed is not None, "No candidate batch failed natively at its second item: %s" % outcomes)
    kind, failing, status, error, before = failed

    def midway():
        require(error.get("message", "").startswith("items[1]: ") and "Items 0..0 were already written" in error.get("message", ""),
                "The failure does not name its item and what was written: %s" % error.get("message"))
        require("batch_items_preflighted" in status["stages"] and "mutation_applied" not in status["stages"], "Unexpected stages: %s" % status["stages"])
        require(status["recoveryArchiveSha256"], "The failed batch has no recovery archive")
        require(software() != before, "The first item was not written before the failure")
        blocked = h.call_error("change_prepare", {"kind": "tag_create", "payload": create("BfLater", "%M250.2"), **p})
        require(blocked.get("code") == "RecoveryRequired", "Another change was prepared while the batch needs recovery: %s" % blocked.get("code"))
        return {"kind": kind, "code": error.get("code"), "message": error.get("message")[:300]}
    h.stage("a batch fails at its second item after the first was written", midway)

    def restored():
        recovered = h.tool("change_restore", {"changeId": failing["changeId"], "previewHash": failing["previewHash"]})
        require(recovered["state"] == "restored" and "checkpoint_retrieved_to_new_directory" in recovered["stages"], "Checkpoint recovery was not verified")
        require(software() == before, "The recovered copy differs from the state before the failed batch")
        return recovered["recoveredProject"]["path"]
    h.stage("restore returns the state before the failed batch", restored)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "cb_", "payload.items on tag_set and the simple single-object kinds in one owned scratch CPU", LIMITATIONS, []).run(body)
