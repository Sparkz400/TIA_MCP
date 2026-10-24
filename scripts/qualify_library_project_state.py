"""Qualify library instantiation into a project with pre-existing compiler errors and unsaved content, then recovery."""
from __future__ import annotations

import argparse
from pathlib import Path
import shutil

from library_native_harness import Harness, require

ROOT = Path(__file__).resolve().parents[1]
FB, BROKEN = "TiaMcpLibraryGate", "TiaMcpBroken"
BROKEN_SOURCE = 'FUNCTION "TiaMcpBroken" : Void\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\nBEGIN\n    "TiaMcpMissingDb".Value := TRUE;\nEND_FUNCTION\n'


def error_paths(compile_result):
    return sorted({m.get("path") or "" for t in compile_result.get("targets", []) for m in t.get("messages", []) if m.get("state") == "Error"})


def body(h):
    documents = h.directory / "documents"
    shutil.copytree(ROOT / "fixtures/libraries/type-lifecycle", documents)
    plc = h.owned_scratch("authoring")
    library = h.project_library()
    fb = h.release(library, h.create_type(library, documents, FB, "blocks", plc), "1.0.0")
    global_library = h.apply("library_create", {"schemaVersion": 1, "name": "ProjectStateQualification"})["journal"]["result"]["library"]["libraryId"]
    h.apply("library_publish", h.selected(library, [fb], destinationLibraryId=global_library))
    archive = h.archive(global_library)
    h.tool("library_close", {"libraryId": global_library})
    h.stage("close authoring session", lambda: h.tool("tia_detach"))

    consumer = h.owned_scratch("consumer")

    # A failed source change leaves an erroneous block in memory: pre-existing diagnostics and unsaved content.
    def broken_source():
        prepared = h.tool("change_prepare", {"kind": "source_upsert", "payload": {"scopePath": consumer, "language": "SCL",
                          "source": BROKEN_SOURCE, "expectedBlockNames": [BROKEN]}})
        error = h.call_error("change_apply", h.apply_args(prepared))
        names = [b["name"] for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"]]
        require(BROKEN in names, "The failed source change left no erroneous block to inherit: %s" % error.get("code"))
        return {"error": error, "brokenBlockInMemory": True}
    h.stage("create an inherited compiler error", broken_source)

    source = h.open_archive(archive)
    prepared = h.prepare("library_instantiate", h.selected(source, [fb], target={"plcPath": consumer}))

    def refused_success():
        error = h.call_error("change_apply", h.apply_args(prepared))
        details = error.get("details") or {}
        require(error.get("code") == "CompileFailed" and details.get("state") == "recovery_required", "Inherited errors did not block success: %s" % error)
        journal = details["journal"]
        events = [(e["stage"], e["state"]) for e in journal["events"]]
        require(("checkpoint_project_save", "completed") in events and ("native_mutation", "completed") in events,
                "Unsaved content was not checkpointed before the mutation")
        require(not any(stage.startswith("save") for stage, _ in events), "A failed change was saved")
        paths = error_paths(journal["compile"])
        require(paths and all(BROKEN in p for p in paths), "Compiler errors are not confined to the inherited block: %s" % paths)
        return {"errorPaths": paths}
    h.report["mutations"].append({"kind": "library_instantiate", "changeId": prepared["changeId"], "expectedOutcome": "CompileFailed"})
    h.stage("inherited errors block a successful library change", refused_success)
    h.stage("discard failed consumer session", lambda: h.tool("tia_detach"))

    h.owned_portal("recovery")
    restored = h.stage("restore checkpoint", lambda: h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
    require([r["kind"] for r in restored["resources"]] == ["project"], "Expected one recovered project")

    def recovered_state():
        names = [b["name"] for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"]]
        require(BROKEN in names and FB not in names, "Recovered checkpoint is not the unsaved pre-change state: %s" % names)
        types = [x for x in h.query(h.project_library())["items"] if x.get("kind") == "type"]
        require(not types, "Recovered project library already holds the type")
        return {"blocks": sorted(names)}
    h.stage("checkpoint holds the unsaved erroneous content and no library type", recovered_state)
    h.report["inheritedErrorPolicy"] = "Any compiler error, inherited or new, refuses success; the project is not saved."


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "ps_", "V21 library instantiate into a project with inherited compiler errors and unsaved content", [
        "The unsaved erroneous block comes from a failed source change; arbitrary unsaved user edits are not reproduced.",
        "Protected (know-how) objects are not exercised: no MCP route applies protection."], []).run(body)
