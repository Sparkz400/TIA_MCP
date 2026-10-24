"""Qualify library instantiation into a project with pre-existing compiler errors and unsaved content, then recovery."""
from __future__ import annotations

import argparse
from pathlib import Path
import shutil

from library_native_harness import Harness, require

ROOT = Path(__file__).resolve().parents[1]
FB, BROKEN = "TiaMcpLibraryGate", "TiaMcpBroken"
BROKEN_SOURCE = 'FUNCTION "TiaMcpBroken" : Void\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\nBEGIN\n    "TiaMcpMissingDb".Value := TRUE;\nEND_FUNCTION\n'


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

    # An inherited inconsistent block makes the target fingerprint unreliable: refused before any checkpoint or write.
    source = h.open_archive(archive)
    error = h.refuse("refuse instantiate into a project with an inherited compiler error", "library_instantiate",
                     h.selected(source, [fb], target={"plcPath": consumer}), "FingerprintUnavailable", BROKEN)
    names = [b["name"] for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"]]
    require(FB not in names and BROKEN in names, "Refusal changed the inherited project content")
    h.report["inheritedErrorPolicy"] = "An inconsistent user block refuses library preparation; nothing is checkpointed, compiled or saved."
    h.report["refusal"] = error


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "ps_", "V21 library instantiate refused for a project with an inherited compiler error and unsaved content", [
        "The unsaved erroneous block comes from a failed source change; arbitrary unsaved user edits are not reproduced.",
        "Protected (know-how) objects are not exercised: no MCP route applies protection."], []).run(body)
