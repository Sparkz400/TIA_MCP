"""Qualify library compatibility cases: unsaved target edits, an inherited inconsistent (uncompiled) PLC data type, a native library
older than the supported upgrade range, and library types instantiated into a different CPU family."""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import shutil
import uuid

from library_native_harness import Harness, require
from qualify_cpu_memory_bytes import add_cpu

ROOT = Path(__file__).resolve().parents[1]
FB, WIDE, UNSAVED, BROKEN_TYPE = "TiaMcpLibraryGate", "TiaMcpWideGate", "TiaMcpUnsavedEdit", "TiaMcpBrokenType"
S7_1200 = "CPU 1214C DC/DC/DC"
# Shipped with every V21 installation: a real native V13 library, below the supported V14..V20 upgrade range.
V13_LIBRARY = Path(r"D:\Arbeit\Siemens\Portal V21\Data\Hmi\Styles\Predefined-Styles\Predefined-Styles.al13")
UNSAVED_SOURCE = ('FUNCTION "%s" : Void\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\nVAR_TEMP\n    Scratch : Bool;\nEND_VAR\n'
                  'BEGIN\n    #Scratch := TRUE;\nEND_FUNCTION\n') % UNSAVED
BROKEN_TYPE_SOURCE = 'TYPE "%s"\nVERSION : 0.1\n   STRUCT\n      Value : "TiaMcpMissingType";\n   END_STRUCT;\nEND_TYPE\n' % BROKEN_TYPE
UNCOMPILED_TYPE = "TiaMcpUncompiledType"
UNCOMPILED_TYPE_SOURCE = 'TYPE "%s"\nVERSION : 0.1\n   STRUCT\n      Value : Bool;\n   END_STRUCT;\nEND_TYPE\n' % UNCOMPILED_TYPE
# Typed outcomes for a library type in a CPU family it was not authored for; anything else fails the run.
TARGET_REFUSALS = ("TargetIncompatible", "LibraryConflict")
TARGET_FAILURES = ("CompileFailed", "NativeLibraryError", "TargetIncompatible")


def tree_hash(folder):
    digest = hashlib.sha256()
    for path in sorted(p for p in folder.rglob("*") if p.is_file()):
        digest.update(path.relative_to(folder).as_posix().encode() + b"\0" + hashlib.sha256(path.read_bytes()).digest())
    return digest.hexdigest()


def instantiate_case(h, payload, before):
    """Instantiate and classify: applied, refused before any write, or failed at apply (not saved)."""
    result = h.client.request("tools/call", {"name": "change_prepare", "arguments": {"kind": "library_instantiate", "payload": payload}})
    if result.get("isError"):
        error = (result.get("structuredContent") or {}).get("error") or {}
        require(error.get("code") in TARGET_REFUSALS, "Untyped preparation failure %s: %s" % (error.get("code"), error.get("message")))
        require(before == h.inventory(), "Refused preparation changed the native inventory")
        return {"outcome": "refused", "error": {k: error.get(k) for k in ("code", "message")}}
    prepared = result["structuredContent"]
    dispatch = {"kind": "library_instantiate", "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "state": "dispatching"}
    h.report["mutations"].append(dispatch)
    h.persist()
    result = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
    content = result.get("structuredContent") or {}
    if not result.get("isError"):
        require(content.get("state") == "applied", "Native change did not reach applied")
        dispatch["state"] = "applied"
        return {"outcome": "applied", "compile": content["journal"].get("compile")}
    error = content.get("error") or {}
    require(error.get("code") in TARGET_FAILURES, "Untyped apply failure %s: %s" % (error.get("code"), error.get("message")))
    journal = (error.get("details") or {}).get("journal") or {}
    require(not any(e.get("stage", "").startswith("save_") and e.get("state") == "completed" for e in journal.get("events", [])),
            "A failed target change completed a save")
    dispatch["state"] = "failed"
    return {"outcome": "failedUnsaved", "error": {k: error.get(k) for k in ("code", "message")}, "compile": journal.get("compile")}


def body(h):
    documents = h.directory / "documents"
    shutil.copytree(ROOT / "fixtures/libraries/type-lifecycle", documents)
    shutil.copy2(ROOT / "fixtures/libraries/compatibility" / (WIDE + ".scl"), documents)
    findings = h.report.setdefault("findings", {})

    # Authoring on the scratch CPU 1511-1 PN: a Bool-only FB type and a 64-bit-typed FB type, both released 1.0.0.
    plc = h.owned_scratch("authoring")
    library = h.project_library()
    fb = h.release(library, h.create_type(library, documents, FB, "blocks", plc), "1.0.0")
    wide = h.release(library, h.create_type(library, documents, WIDE, "blocks", plc), "1.0.0")
    global_library = h.apply("library_create", {"schemaVersion": 1, "name": "CompatibilityQualification"})["journal"]["result"]["library"]["libraryId"]
    h.apply("library_publish", h.selected(library, [fb, wide], destinationLibraryId=global_library))
    archive = h.archive(global_library)
    h.tool("library_close", {"libraryId": global_library})
    h.stage("close authoring session", lambda: h.tool("tia_detach"))

    consumer = h.owned_scratch("consumer")
    project_path = h.tool("tia_session_status")["projects"][0]["path"]
    # Scratch security for a new CPU needs the worker's own freshly created scratch, so the S7-1200 is added first.
    h.apply("hardware_add", add_cpu(h, S7_1200))
    small = [p["path"] for p in h.tool("project_inventory", {"maxItems": 1000})["plcs"] if p["path"] != consumer]
    require(len(small) == 1, "Expected one added S7-1200 PLC")

    # A real native V13 library is refused before any native call; its copy stays byte-identical.
    def v13_refusal():
        copy = h.directory / "inputs" / "v13"
        shutil.copytree(V13_LIBRARY.parent, copy)
        path, before = copy / V13_LIBRARY.name, tree_hash(copy)
        error = h.call_error("change_prepare", {"kind": "library_upgrade", "payload": {"schemaVersion": 1, "path": str(path),
                             "expectedSha256": hashlib.sha256(path.read_bytes()).hexdigest()}})
        require(error.get("code") == "LibraryVersionUnsupported", "Expected LibraryVersionUnsupported: %s" % error)
        require(tree_hash(copy) == before, "Refused upgrade changed the V13 copy")
        return {"error": error, "sourceTreeSha256": before}
    findings["v13Upgrade"] = h.stage("refuse a native V13 library upgrade", v13_refusal)

    # Consistent unsaved edit: compiled, not saved. The library change must report it and persist it into the checkpoint.
    def unsaved_edit():
        prepared = h.tool("change_prepare", {"kind": "source_upsert", "payload": {"scopePath": consumer, "language": "SCL",
                          "source": UNSAVED_SOURCE, "expectedBlockNames": [UNSAVED]}})
        applied = h.tool("change_apply", {**h.apply_args(prepared), "save": False})
        require(applied["state"] == "applied", "Unsaved source change did not apply")
        require(UNSAVED in [b["name"] for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"]], "Unsaved block is missing")
        return {"changeId": prepared["changeId"]}
    h.stage("create a consistent unsaved edit", unsaved_edit)
    source = h.open_archive(archive)
    prepared = h.prepare("library_instantiate", h.selected(source, [fb], target={"plcPath": consumer}))
    require(prepared["preview"].get("unsavedTargetEdits") == {"present": True, "disposition": "savedIntoCheckpointBaseline"},
            "Preview does not report the unsaved edit: %s" % prepared["preview"].get("unsavedTargetEdits"))
    applied = h.apply_prepared("library_instantiate", prepared)
    findings["unsavedEdit"] = {"preview": prepared["preview"]["unsavedTargetEdits"], "checkpoints": applied["journal"]["checkpoints"]}
    names = [b["name"] for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"]]
    require(UNSAVED in names and FB in names, "Unsaved edit or library instance missing after apply")

    # Inherited inconsistent PLC data type (from a failed type change): refused before checkpoint or write.
    def broken_type():
        prepared = h.tool("change_prepare", {"kind": "type_source_upsert", "payload": {"scopePath": consumer, "source": BROKEN_TYPE_SOURCE,
                          "expectedTypeNames": [BROKEN_TYPE]}})
        error = h.call_error("change_apply", h.apply_args(prepared))
        types = [t["name"] for t in h.tool("software_inspect", {"plcPath": consumer, "maxItems": 2000})["types"]]
        return {"error": {k: error.get(k) for k in ("code", "message")}, "inconsistentTypeInMemory": BROKEN_TYPE in types}
    broken = findings["inheritedType"] = h.stage("create an inherited inconsistent PLC data type", broken_type)
    if broken["inconsistentTypeInMemory"]:
        broken["refusal"] = h.refuse("refuse instantiate with an inherited inconsistent PLC data type", "library_instantiate",
                                     h.selected(source, [wide], target={"plcPath": consumer}), "InheritedDiagnostics", BROKEN_TYPE)

    # TIA discards a type whose generation fails, so the inherited inconsistency is a valid type generated without compile.
    def uncompiled_type():
        prepared = h.tool("change_prepare", {"kind": "type_source_upsert", "payload": {"scopePath": consumer, "source": UNCOMPILED_TYPE_SOURCE,
                          "expectedTypeNames": [UNCOMPILED_TYPE]}})
        applied = h.tool("change_apply", {**h.apply_args(prepared), "compile": False, "save": False})
        require(applied["state"] == "applied", "Uncompiled type change did not apply")
        types = {t["name"]: t for t in h.tool("software_inspect", {"plcPath": consumer, "maxItems": 2000})["types"]}
        require(UNCOMPILED_TYPE in types, "Uncompiled type is missing")
        return {"changeId": prepared["changeId"], "isConsistent": types[UNCOMPILED_TYPE].get("isConsistent")}
    uncompiled = findings["uncompiledType"] = h.stage("generate a PLC data type without compiling it", uncompiled_type)
    require(uncompiled["isConsistent"] is False, "TIA reports the uncompiled type as consistent; no inherited inconsistency to refuse")
    uncompiled["refusal"] = h.refuse("refuse instantiate with an inherited inconsistent PLC data type", "library_instantiate",
                                     h.selected(source, [wide], target={"plcPath": consumer}), "InheritedDiagnostics", UNCOMPILED_TYPE)
    h.stage("close consumer session (discards the failed and uncompiled type changes)", lambda: h.tool("tia_detach"))

    def reopen(label):
        h.owned_portal(label)
        h.stage("reopen consumer " + label, lambda: h.reopen_project(project_path))
        if h.unresolved_admissions():
            h.reconcile_admission("reconcile the failed change " + label)

    reopen("after unsaved edit")
    def persisted():
        blocks = [b["name"] for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"]]
        types = [t["name"] for t in h.tool("software_inspect", {"plcPath": consumer, "maxItems": 2000})["types"]]
        require(UNSAVED in blocks and FB in blocks and BROKEN_TYPE not in types and UNCOMPILED_TYPE not in types, "Reopened project differs: %s %s" % (blocks, types))
        return {"blocks": sorted(blocks)}
    h.stage("reopened: unsaved edit and instance persisted, failed and uncompiled types absent", persisted)

    # CPU family mismatch: both types were authored on an S7-1500 and are instantiated into the added S7-1200.
    for label, version in (("control", fb), ("wide", wide)):
        before = h.inventory()
        source = h.open_archive(archive)
        outcome = findings["s7_1200_" + label] = h.stage("instantiate %s type into the S7-1200" % label,
                                                         lambda v=version, s=source: instantiate_case(h, h.selected(s, [v], target={"plcPath": small[0]}), before))
        if outcome["outcome"] == "failedUnsaved":
            h.stage("discard the failed %s change" % label, lambda: h.tool("tia_detach"))
            reopen("after the %s target failure" % label)
            h.stage("reopened %s target project is unchanged" % label, lambda b=before: require(h.inventory() == b, "Saved project changed"))
        else:
            h.tool("library_close", {"libraryId": source})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "co_", "V21 library compatibility: unsaved edits, inherited inconsistent types, V13 libraries and S7-1200 targets", [
        "No V14-V20 library exists on this PC; upgrades of those versions are covered by local.library_upgrades.20261024 (V14 SP1-V18) and V19/V20 stay open.",
        "The V13 input is TIA's own shipped HMI style library; it is refused by version before any native call.",
        "The unsaved edit is a compiled, unsaved source change; arbitrary unsaved user edits in an attached TIA UI are not reproduced.",
        "Only CPU family is varied (S7-1500 to S7-1200, newest catalog firmware); firmware-version mismatches are not exercised.",
        "No runtime execution."], [ROOT / "fixtures/libraries/compatibility" / (WIDE + ".scl")]).run(body)
