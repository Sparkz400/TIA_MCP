"""Qualify tag_set with payload.items natively in one owned scratch CPU and time it against single tag_set changes.

A tag-table import seeds 2*count+2 Bool tags on bit memory. The first `count` are renamed by one tag_set change each;
the next `count` by one tag_set change with items. Refused batches (a taken name, a swap, one tag twice, an unknown
tag, items beside the single-tag fields) must leave the table unchanged. The applied batch must list every item in its
preview, read every item back after the writes and after save, keep one recovery archive, and make a batch prepared
before it stale. change_restore of the batch must return every renamed tag to its old name in the recovered copy.

The report's `timings` hold wall time and journal phase timings (fingerprints, checkpoint archive, saves) per change.
A scratch project is small, so the absolute times say little about a large project; the count of fingerprint runs,
archives, compiles and saves per renamed tag is what the comparison shows.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import time

from library_native_harness import Harness, require

TABLE = "TagBatch"
LIMITATIONS = ["One owned scratch standard CPU at PLC-root scope; Bool tags on bit memory in one tag table.",
               "No block references the renamed tags, software units are not exercised, and the F-I/O and safety-tag refusals need an F-CPU fixture.",
               "Timings are from a small scratch project and one run."]


def names(h, plc):
    software = h.tool("software_inspect", {"plcPath": plc, "include": "tags", "compact": True, "namePrefix": TABLE, "maxItems": 2000})
    tables = [t for t in software["tagTables"] if t["name"] == TABLE]
    require(len(tables) == 1, "Expected exactly one %s table" % TABLE)
    return {tag["name"]: tag["logicalAddress"] for tag in tables[0]["tags"]}


def timed_change(h, plc, label, payload, timings):
    started = time.monotonic()
    prepared = h.stage("prepare " + label, lambda: h.tool("change_prepare", {"kind": "tag_set", "payload": payload, "plcPath": plc}))
    prepared_at = time.monotonic()
    applied = h.apply_prepared("tag_set", prepared)
    finished = time.monotonic()
    full = h.tool("change_status", {"changeId": prepared["changeId"], "includeDetails": True})
    phases = {row["phase"]: row.get("elapsedMilliseconds") for row in full["phaseTimings"]}
    for phase in ("prepare-fingerprint", "stale-revision-fingerprint", "checkpoint-save", "checkpoint-archive", "changed-project-save", "after-fingerprint"):
        require(isinstance(phases.get(phase), int), "Phase %s was not timed for %s" % (phase, label))
    timings.append({"label": label, "tags": len(payload.get("items", [0])), "prepareSeconds": round(prepared_at - started, 2),
                    "applySeconds": round(finished - prepared_at, 2), "totalSeconds": round(finished - started, 2), "phaseMilliseconds": phases})
    return prepared, applied, full


def body(h):
    count = h.args.count
    plc = h.owned_scratch("tag batch")
    single = ["TbSingle%d" % i for i in range(count)]
    batch = ["TbBatch%d" % i for i in range(count)]
    seed = single + batch + ["TbKeep", "TbTaken"]
    rows = [{"name": name, "dataType": "Bool", "logicalAddress": "%%M%d.%d" % (200 + i // 8, i % 8)} for i, name in enumerate(seed)]
    h.apply_prepared("tag_table_import_xml", h.stage("prepare seed", lambda: h.tool("change_prepare", {
        "kind": "tag_table_import_xml", "payload": {"tagTableName": TABLE, "tags": rows}, "plcPath": plc})))
    seeded = names(h, plc)
    require(sorted(seeded) == sorted(seed), "Seeded tags differ: %s" % sorted(seeded))
    timings = h.report["timings"] = []

    # Before: one change per renamed tag.
    for name in single:
        timed_change(h, plc, "single " + name, {"tagTableName": TABLE, "memberName": name, "attributes": {"Name": name + "_R"}}, timings)
    expected = {(name + "_R" if name in single else name): address for name, address in seeded.items()}
    require(names(h, plc) == expected, "Single renames read back differently")

    def item(member, **attributes):
        return {"tagTableName": TABLE, "memberName": member, "attributes": attributes}

    def refuse(label, payload, code):
        def check():
            error = h.call_error("change_prepare", {"kind": "tag_set", "payload": payload, "plcPath": plc})
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            require(names(h, plc) == expected, "A refused batch changed the table")
            return error
        h.stage("refuse batch: " + label, check)

    good = item(batch[0], Name=batch[0] + "_R")
    refuse("a name another tag holds", {"items": [good, item(batch[1], Name="TbTaken")]}, "ObjectExists")
    refuse("a swap", {"items": [item(batch[0], Name=batch[1]), item(batch[1], Name=batch[0])]}, "ObjectExists")
    refuse("two items with one new name", {"items": [good, item(batch[1], Name=batch[0] + "_r")]}, "ObjectExists")
    refuse("one tag twice", {"items": [good, item(batch[0], LogicalAddress="%M300.0")]}, "InvalidRequest")
    refuse("an unknown tag", {"items": [good, item("TbMissing", Name="TbMissing_R")]}, "ObjectNotFound")
    refuse("an unknown table", {"items": [good, {"tagTableName": "TbNoTable", "memberName": "X", "attributes": {"Name": "Y"}}]}, "ObjectNotFound")
    refuse("an attribute outside the allowlist", {"items": [good, item(batch[1], ExternalWritable="false")]}, "UnsupportedAttribute")
    refuse("items beside the single-tag fields", {"items": [good], "tagTableName": TABLE, "memberName": batch[1], "attributes": {"Name": "Z"}}, "InvalidRequest")
    h.stage("refuse items on constant_set", lambda: require(
        h.call_error("change_prepare", {"kind": "constant_set", "payload": {"items": [good]}, "plcPath": plc}).get("code") == "InvalidRequest", "constant_set accepted items"))

    # After: all renames in one change; the last item also moves its tag.
    moved = "%M399.7"
    items = [item(name, Name=name + "_R") for name in batch]
    items[-1]["attributes"]["LogicalAddress"] = moved
    stale = h.stage("prepare a batch made stale below", lambda: h.tool("change_prepare", {
        "kind": "tag_set", "payload": {"items": [item("TbKeep", Name="TbKeep_R")]}, "plcPath": plc}))
    prepared, applied, full = timed_change(h, plc, "batch of %d" % count, {"items": items}, timings)

    def verify():
        shown = prepared["preview"]["items"]
        require([(row["index"], row["memberName"]) for row in shown] == list(enumerate(batch)), "The summary preview does not list every item in order")
        require(all(row["attributes"][0]["name"] == "Name" and row["attributes"][0]["before"] == row["memberName"]
                    and row["attributes"][0]["after"] == row["memberName"] + "_R" for row in shown), "Preview rows lack before and after names")
        require(len(prepared["preview"]["backups"]) == 1, "Expected one table backup")
        for stage in ("recovery_archive_created", "baseline_compile_finished", "mutation_applied", "tag_batch_readback_verified",
                      "compile_finished", "changed_project_saved", "tag_batch_verified_after_save"):
            require(applied["stages"].count(stage) == 1, "Stage %s did not run exactly once: %s" % (stage, applied["stages"]))
        readbacks = [row for row in full["readbacks"] if row.get("role") == "tag-batch-readback"]
        require([row["stage"] for row in readbacks] == ["after-mutation", "after-save"] and all(row["verifiedItems"] == count for row in readbacks),
                "Every item must be read back after the writes and after save")
        require(applied["recoveryArchiveSha256"] and applied["progress"]["projectEditComplete"] is True, "The batch is not applied and saved with a checkpoint")
        for name in batch:
            expected[name + "_R"] = expected.pop(name)
        expected[batch[-1] + "_R"] = moved
        require(names(h, plc) == expected, "The batch reads back differently: %s" % names(h, plc))
        return {"items": count, "readbacks": len(readbacks)}
    h.stage("batch preview, stages and readback", verify)

    def stale_refused():
        error = h.call_error("change_apply", Harness.apply_args(stale))
        require(error.get("code") == "StaleRevision", "Expected StaleRevision, observed %s" % error.get("code"))
        require(names(h, plc) == expected, "A stale batch changed the table")
    h.stage("refuse apply of the stale batch", stale_refused)

    singles = [row for row in timings if row["tags"] == 1]
    h.report["timingSummary"] = {
        "tagsPerRun": count, "singleChangesTotalSeconds": round(sum(row["totalSeconds"] for row in singles), 2),
        "singleChangeMeanSeconds": round(sum(row["totalSeconds"] for row in singles) / len(singles), 2),
        "batchTotalSeconds": timings[-1]["totalSeconds"]}

    def restored():
        recovered = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        require(recovered["state"] == "restored" and "checkpoint_retrieved_to_new_directory" in recovered["stages"], "Checkpoint recovery was not verified")
        before = {(name + "_R" if name in single else name): address for name, address in seeded.items()}
        require(names(h, plc) == before, "The recovered copy does not hold the tags as before the batch: %s" % names(h, plc))
        return recovered["recoveredProject"]["path"]
    h.stage("restore the batch checkpoint", restored)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--count", type=int, default=5, help="tags renamed singly, and again as one batch")
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    require(1 <= args.count <= 100, "--count must be 1..100")
    args.runner = __file__
    Harness(args, "tb_", "tag_set items (batch tag edits) in one owned scratch CPU", LIMITATIONS, []).run(body)
