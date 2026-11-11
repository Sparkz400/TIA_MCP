"""Qualify payload.items natively in one owned scratch CPU: every batched kind, and a tag_set batch on tags a block uses.

Each batch must apply under one checkpoint archive, one mutation stage, one compile and one save, list every item in its
preview and leave exactly the intended names. Covered: tag_set (tags referenced by a compiled SCL block, whose source must
name the new tags afterwards), tag_create, constant_create, constant_set, constant_delete, tag_delete, tag_table_create,
tag_table_rename, block_rename, block_attributes_set and type_rename. Refused batches (one object in two items, one name
from two items, a name that exists, an unknown object in a later item, items on a kind that does not batch, fields beside
items) must name the item and leave the software unchanged.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from library_native_harness import Harness, require

TABLE = "BatchTags"
LIMITATIONS = ["One owned scratch standard CPU at PLC-root scope; software units and namespaces are not exercised.",
               "The safety-tag and F-I/O refusals of a batch need an F-CPU fixture (qualify_fio_tag_routes.py covers the F-I/O address refusal).",
               "A batch that fails after its first write (recovery_required) is not provoked."]
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
        return {"tables": tables, "blocks": sorted(b["name"] for b in view["blocks"]), "types": sorted(t["name"] for t in view["types"])}

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
