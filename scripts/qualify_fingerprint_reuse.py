"""Qualify the save-state marker behind the reused change-fingerprint content, natively in one owned scratch CPU.

The worker reuses its last project-wide fingerprint read while TIA reports the project as saved (IsModified false) with the
same LastModified. This run checks what a script can check:

- every edit the worker makes through Openness and leaves unsaved (a tag, a block source, a hardware attribute) turns
  IsModified true, and no fingerprint content is reused while it is;
- every save turns it false and changes LastModified;
- a fingerprint built from reused content equals the one a full read produces for the same saved state;
- a change reads the whole project once where it read it five times (prepare, two admission checks, the stale-revision
  check, the outcome), and the next change of the same kind starts from the previous outcome's read;
- a preview made stale by another applied change is still refused (StaleRevision) with reuse active.

It cannot check edits made by hand in the TIA user interface: the owned instance has no user interface. That check is the
manual procedure in docs/guides/engineering-workflows.md (fingerprint reuse).
"""
from __future__ import annotations

import argparse
from pathlib import Path
import time
import uuid

from library_native_harness import Harness, require

TABLE = "ReuseTags"
LIMITATIONS = ["One owned scratch standard CPU, headless TIA: edits made in the TIA user interface or by another Openness client are not exercised.",
               "Unsaved edits are a PLC tag rename, an SCL FC source and a hardware Comment written by the worker itself.",
               "Timings are from a small scratch project and one run."]
SOURCE = 'FUNCTION "ReuseProbe" : Void\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\nBEGIN\n\t;\nEND_FUNCTION\n'


def body(h):
    plc = h.owned_scratch("fingerprint reuse")
    states = h.report["saveStates"] = []
    rows = h.report["changes"] = []

    def state(label):
        projects = h.tool("tia_session_status")["projects"]
        require(len(projects) == 1, "Expected one open project")
        row = {"label": label, "isModified": projects[0]["isModified"], "lastModified": projects[0]["lastModified"]}
        states.append(row)
        return row

    def prepare(label, kind, payload):
        started = time.monotonic()
        prepared = h.stage("prepare " + label, lambda: h.tool("change_prepare", {"kind": kind, "payload": payload, "plcPath": plc}))
        prepared["_seconds"] = round(time.monotonic() - started, 2)
        return prepared

    def content(prepared):
        return h.tool("change_status", {"changeId": prepared["changeId"], "includeDetails": True})["fingerprintContent"]

    def apply(label, kind, prepared, save=True):
        started = time.monotonic()
        arguments = {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "idempotencyKey": "reuse-" + uuid.uuid4().hex,
                     "checkpointSave": True, "compile": True, "save": save}
        dispatch = {"kind": kind, "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "state": "dispatching"}
        h.report["mutations"].append(dispatch)
        applied = h.stage("apply " + label, lambda: h.tool("change_apply", arguments))
        require(applied["state"] == "applied", "%s did not reach applied" % label)
        dispatch["state"] = "applied"
        used = content(prepared)
        rows.append({"label": label, "kind": kind, "saved": save, "prepareSeconds": prepared["_seconds"], "applySeconds": round(time.monotonic() - started, 2), **used})
        return used

    def rename(member, name):
        return {"tagTableName": TABLE, "memberName": member, "attributes": {"Name": name}}

    opened = state("scratch created")
    require(opened["isModified"] is False, "A freshly created and saved scratch reports unsaved edits")
    seed = [{"name": "Reuse%d" % i, "dataType": "Bool", "logicalAddress": "%%M210.%d" % i} for i in range(8)]
    apply("seed tags", "tag_table_import_xml", prepare("seed tags", "tag_table_import_xml", {"tagTableName": TABLE, "tags": seed}))
    seeded = state("after seed (saved)")
    require(seeded["isModified"] is False and seeded["lastModified"] != opened["lastModified"], "A save must clear IsModified and change LastModified")

    # A reused read gives the fingerprint a full read gives.
    first = prepare("rename 0 (first of its kind: full read)", "tag_set", rename("Reuse0", "Reuse0_R"))
    require(content(first) == {"fullReads": 1, "reusedForSavedState": 0}, "The first tag_set after an import must read in full: %s" % content(first))
    second = prepare("rename 0 again (reused)", "tag_set", rename("Reuse0", "Reuse0_R"))
    require(content(second) == {"fullReads": 0, "reusedForSavedState": 1}, "An identical preparation on the same saved state must reuse: %s" % content(second))
    require(second["preview"]["baseFingerprint"] == first["preview"]["baseFingerprint"], "Reused content changed the fingerprint")
    h.tool("software_inspect", {"plcPath": plc, "include": "tags", "compact": True, "maxItems": 50})  # any other operation drops the kept content
    third = prepare("rename 0 after another operation (full read)", "tag_set", rename("Reuse0", "Reuse0_R"))
    require(content(third) == {"fullReads": 1, "reusedForSavedState": 0}, "Another worker operation must drop the kept content: %s" % content(third))
    require(third["preview"]["baseFingerprint"] == first["preview"]["baseFingerprint"], "A full read after reuse gives another fingerprint")

    # One change: prepare (read above), two admission checks and the stale-revision check reuse it, the outcome reads in full.
    used = apply("rename 0", "tag_set", third)
    require(used == {"fullReads": 2, "reusedForSavedState": 3}, "Expected 2 full reads and 3 reuses for a change whose prepare read in full: %s" % used)
    after = state("after rename 0 (saved)")
    require(after["isModified"] is False and after["lastModified"] != seeded["lastModified"], "A save must clear IsModified and change LastModified")
    # The next change of the same kind starts from the previous outcome's read.
    used = apply("rename 1", "tag_set", prepare("rename 1 (reused from the previous outcome)", "tag_set", rename("Reuse1", "Reuse1_R")))
    require(used == {"fullReads": 1, "reusedForSavedState": 4}, "Expected 1 full read and 4 reuses for a follow-up change: %s" % used)

    # Unsaved worker edits: IsModified must be true and nothing may be reused until the next save.
    def unsaved(label, kind, payload):
        saved_before = state("before " + label)
        require(saved_before["isModified"] is False, "The project must be saved before " + label)
        apply(label + " (not saved)", kind, prepare(label, kind, payload), save=False)
        dirty = state("after " + label + " (not saved)")
        require(dirty["isModified"] is True, "An unsaved %s left IsModified false: the marker does not hold" % kind)
        probes = [prepare("probe %d while unsaved after %s" % (n, label), "tag_set", rename("Reuse7", "Reuse7_R")) for n in (1, 2)]
        for probe in probes:
            require(probe["preview"]["isModified"] is True and content(probe) == {"fullReads": 1, "reusedForSavedState": 0},
                    "Content was reused while the project had unsaved edits: %s" % content(probe))
        # Applying the probe saves the pending edit with its checkpoint; rename it back so the next round starts equal.
        apply("save after " + label, "tag_set", probes[1])
        apply("undo probe after " + label, "tag_set", prepare("undo probe after " + label, "tag_set", rename("Reuse7_R", "Reuse7")))
        clean = state("after " + label + " saved")
        require(clean["isModified"] is False and clean["lastModified"] != saved_before["lastModified"], "A save must clear IsModified and change LastModified")

    unsaved("tag rename", "tag_set", rename("Reuse2", "Reuse2_R"))
    unsaved("block source", "source_upsert", {"language": "SCL", "source": SOURCE, "expectedBlockNames": ["ReuseProbe"]})
    hardware = h.tool("project_inventory", {"maxItems": 1000})["plcs"][0]["hardwarePath"]
    unsaved("hardware comment", "hardware_attributes", {"hardwarePath": hardware, "attributes": {"Comment": "fingerprint reuse probe"}})

    # Stale-revision detection with reuse active.
    stale = prepare("rename 3 (made stale below)", "tag_set", rename("Reuse3", "Reuse3_R"))
    apply("rename 4", "tag_set", prepare("rename 4", "tag_set", rename("Reuse4", "Reuse4_R")))

    def refused():
        error = h.call_error("change_apply", {"changeId": stale["changeId"], "previewHash": stale["previewHash"], "idempotencyKey": "reuse-" + uuid.uuid4().hex,
                                                "checkpointSave": True, "compile": True, "save": True})
        require(error.get("code") == "StaleRevision", "Expected StaleRevision, observed %s: %s" % (error.get("code"), error.get("message")))
    h.stage("refuse apply of the stale preview", refused)

    saves = [row["lastModified"] for row in states if not row["isModified"]]
    h.report["lastModified"] = {"distinctSavedValues": len(set(saves)), "savedObservations": len(saves),
                                "subSecond": any("." in value and value.split(".")[1][:7].strip("0Z+-:") for value in saves), "sample": saves[-1]}
    singles = [row for row in rows if row["kind"] == "tag_set" and row["saved"] and row["label"].startswith("rename")]
    h.report["summary"] = {"fullReadsPerFollowUpChange": 1, "fullReadsBefore": 5,
                           "renameChanges": [{k: row[k] for k in ("label", "prepareSeconds", "applySeconds", "fullReads", "reusedForSavedState")} for row in singles]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    args.host_args = ["--enable-fingerprint-reuse"]  # off by default
    Harness(args, "fr_", "save-state marker and reused change-fingerprint content in one owned scratch CPU", LIMITATIONS, []).run(body)
