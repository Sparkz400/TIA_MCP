"""Qualify implementation-changing and breaking-interface FB releases through MCP in owned scratch projects only."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET  # Native exports from this run's owned scratch only.

from engineering_preservation import native_payload_hash
from library_native_harness import Harness, require

ROOT = Path(__file__).resolve().parents[1]
FB = "TiaMcpLibraryGate"
UDT = "TiaMcpLibraryState"


def local(tag):
    return tag.rsplit("}", 1)[-1]


def fb_shape(raw):
    """Independent reading of a native FB export: output member names and SCL body tokens."""
    root = ET.fromstring(raw)
    outputs = [m.get("Name") for s in root.iter() if local(s.tag) == "Section" and s.get("Name") == "Output"
               for m in s if local(m.tag) == "Member"]
    tokens = [e.get("Text") for e in root.iter() if local(e.tag) == "Token"]
    return {"outputs": outputs, "negated": "NOT" in tokens}


# Expected shapes are authored from the fixture documents, not read back from the implementation.
EXPECTED = {"1.0.0": {"outputs": ["Active"], "negated": False},
            "1.1.0": {"outputs": ["Active"], "negated": True},
            "2.0.0": {"outputs": ["Ready"], "negated": True}}


def compile_errors(compile_result):
    return [m for t in compile_result.get("targets", []) for m in t.get("messages", []) if m.get("state") == "Error"]


def body(h):
    documents = h.directory / "documents"
    shutil.copytree(ROOT / "fixtures/libraries/type-lifecycle", documents)
    changes = ROOT / "fixtures/libraries/release-changes"

    def exported_version_shape(library, version):
        result = h.export_documents(library, version)
        shapes = [fb_shape(Path(a["path"]).read_bytes()) for a in result["files"] if a["path"].lower().endswith(".xml")]
        require(len(shapes) == 1, "Expected exactly one native XML FB document")
        return {"files": result["files"], "shape": shapes[0]}

    def block_shape(name):
        exported = h.tool("block_read", {"blockName": name})
        raw = Path(exported["artifact"]["path"]).read_bytes()
        require(hashlib.sha256(raw).hexdigest() == exported["artifact"]["sha256"], "Native block export hash differs")
        return {"artifact": exported["artifact"], "shape": fb_shape(raw)}

    def require_shape(observed, number, where):
        require(observed["shape"] == EXPECTED[number], "%s does not match independent %s expectation: %s" % (where, number, observed["shape"]))
        return observed

    def edit_and_release(library, plc, previous, source_name, number):
        released = h.release(library, h.edit_source(library, plc, previous, FB, (changes / source_name).read_text()), number)
        h.stage("export released " + number, lambda: require_shape(exported_version_shape(library, released), number, "Released export"))
        return released

    def publish_generation(project_library, global_library, version):
        h.apply("library_publish", h.selected(project_library, [version], destinationLibraryId=global_library))
        h.apply("library_set_default", h.selected(global_library, [version]))
        return h.archive(global_library)

    def unrelated_snapshot(plc):
        inventory = h.tool("project_inventory", {"maxItems": 1000})
        names = {"TiaMcp_IsEnabled", "TiaMcp_EnableGate", "TiaMcp_EnableGate_DB", "TiaMcpLibraryState_DB"}
        blocks = sorted((x for x in inventory["blocks"] if x["name"] in names and x["plcPath"] == plc), key=lambda x: x["path"])
        require(len(blocks) == len(names), "Unrelated fixture blocks are missing or ambiguous")
        exported = [(b["path"], h.tool("block_read", {"blockPath": b["path"]})) for b in blocks]
        exported.append(("type:" + UDT, h.tool("type_read", {"plcPath": plc, "typeName": UDT})))
        rows = []
        for path, result in exported:
            raw = Path(result["artifact"]["path"]).read_bytes()
            require(hashlib.sha256(raw).hexdigest() == result["artifact"]["sha256"], "Unrelated native export hash differs")
            rows.append({"path": path, "nativeDocumentWithoutDocumentInfoSha256": native_payload_hash(raw.decode("utf-8-sig"))})
        return rows

    def expect_breaking_compile_failure(payload):
        prepared = h.prepare("library_update_project", payload)
        dispatch = {"kind": "library_update_project", "changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                    "state": "dispatching", "expectedOutcome": "CompileFailed"}
        h.report["mutations"].append(dispatch)
        h.persist()
        error = h.call_error("change_apply", h.apply_args(prepared))
        details = error.get("details") or {}
        dispatch.update(state=details.get("state"), errorCode=error.get("code"))
        h.report["breakingUpdate"] = {"error": error}
        require(error.get("code") == "CompileFailed" and details.get("state") == "recovery_required", "Breaking update did not fail compilation for recovery")
        events = details["journal"].get("events", [])
        require(any(e.get("stage") == "native_mutation" and e.get("state") == "completed" for e in events), "Native update did not complete before compilation")
        require(not any(e.get("stage", "").startswith("save") for e in events), "Failed breaking update was saved")
        errors = compile_errors(details["journal"]["compile"])
        h.report["breakingUpdate"]["errors"] = errors
        text = json.dumps(errors)
        require(errors and "Main" in text, "Compiler errors do not identify the stale caller Main")
        require(not any(n in text for n in ("TiaMcp_EnableGate", "TiaMcp_IsEnabled")), "Compiler errors reach unrelated fixture blocks")
        return prepared

    # Authoring: three FB generations of one native type identity.
    plc = h.owned_scratch("authoring")
    project_library = h.project_library()
    initial = {name: h.release(project_library, h.create_type(project_library, documents, name, composition, plc), "1.0.0")
               for name, composition in ((UDT, "types"), (FB, "blocks"))}
    h.stage("export released 1.0.0", lambda: require_shape(exported_version_shape(project_library, initial[FB]), "1.0.0", "Released export"))
    implementation = edit_and_release(project_library, plc, initial[FB], "TiaMcpLibraryGate.implementation.scl", "1.1.0")
    h.apply("library_set_default", h.selected(project_library, [implementation]))
    global_library = h.apply("library_create", {"schemaVersion": 1, "name": "ReleaseQualification"})["journal"]["result"]["library"]["libraryId"]
    h.apply("library_publish", h.selected(project_library, [initial[UDT]], destinationLibraryId=global_library))
    generation = {"1.1.0": publish_generation(project_library, global_library, implementation)}
    breaking = edit_and_release(project_library, plc, implementation, "TiaMcpLibraryGate.breaking.scl", "2.0.0")
    h.apply("library_set_default", h.selected(project_library, [breaking]))
    generation["2.0.0"] = publish_generation(project_library, global_library, breaking)
    fb_versions = {"1.0.0": initial[FB], "1.1.0": implementation, "2.0.0": breaking}
    require({v["typeGuid"] for v in fb_versions.values()} == {initial[FB]["typeGuid"]}, "A release changed the FB type identity")
    require(len({v["versionGuid"] for v in fb_versions.values()}) == 3, "Release version GUIDs are not distinct")
    h.report["nativeVersions"] = {"udt": initial[UDT], "fb": fb_versions}
    h.tool("library_close", {"libraryId": global_library})
    h.stage("close owned authoring session", lambda: h.tool("tia_detach"))

    # Consumer: the 1.1.0 generation (which also carries 1.0.0) is consumed at 1.0.0, then updated.
    consumer_plc = h.owned_scratch("consumer")
    source = h.open_archive(generation["1.1.0"])
    for version in (initial[UDT], initial[FB]):
        h.apply("library_instantiate", h.selected(source, [version], target={"plcPath": consumer_plc}))
    consumer_library = h.project_library()
    h.apply("source_upsert", {"scopePath": consumer_plc, "language": "SCL", "source": (documents / "ConsumerReferences.scl").read_text(),
            "expectedBlockNames": ["TiaMcpLibraryGate_DB", "TiaMcpLibraryState_DB", "Main"]})
    h.stage("consumer FB at 1.0.0", lambda: h.verify_use(consumer_library, initial[FB], 1))
    h.stage("consumer FB body 1.0.0", lambda: require_shape(block_shape(FB), "1.0.0", "Consumer FB"))
    before = h.stage("capture unrelated content before implementation update", lambda: unrelated_snapshot(consumer_plc))
    applied = h.apply("library_update_project", h.selected(source, [implementation], updateScope="entireSelectedPlc", target={"plcPath": consumer_plc}))
    require(applied["journal"]["compile"]["errorCount"] == 0, "Implementation update compile failed")
    h.tool("library_close", {"libraryId": source})
    h.stage("consumer FB at 1.1.0", lambda: h.verify_use(consumer_library, implementation, 1))
    h.stage("consumer FB has no 1.0.0 uses", lambda: h.verify_use(consumer_library, initial[FB], 0))
    h.stage("consumer UDT unaffected", lambda: h.verify_use(consumer_library, initial[UDT], 1))
    h.stage("consumer FB body 1.1.0", lambda: require_shape(block_shape(FB), "1.1.0", "Updated consumer FB"))
    after = h.stage("capture unrelated content after implementation update", lambda: unrelated_snapshot(consumer_plc))
    require(before == after, "Implementation update changed unrelated native content")
    h.report["implementationUpdatePreservedUnrelated"] = True

    source = h.open_archive(generation["2.0.0"])
    failed = h.stage("breaking update fails compile at stale caller", lambda: expect_breaking_compile_failure(
        h.selected(source, [breaking], updateScope="entireSelectedPlc", target={"plcPath": consumer_plc})))
    consumer_path = h.tool("tia_session_status")["projects"][0]["path"]
    h.stage("discard failed consumer session", lambda: h.tool("tia_detach"))

    # Recovery: the checkpoint and the unsaved original both keep the exact 1.1.0 link.
    h.owned_portal("recovery")
    restored = h.stage("restore breaking-update checkpoint", lambda: h.tool("change_restore", {"changeId": failed["changeId"], "previewHash": failed["previewHash"]}))
    require(restored["state"] == "restored_to_new_locations" and restored.get("originalsPreserved") is True, "Checkpoint was not restored")
    recovered = h.project_library()
    h.stage("recovered FB at 1.1.0", lambda: h.verify_use(recovered, implementation, 1))
    h.stage("recovered FB body 1.1.0", lambda: require_shape(block_shape(FB), "1.1.0", "Recovered FB"))
    h.stage("close recovery session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen original consumer", lambda: h.tool("project_open", {"path": consumer_path}))
    reopened = h.project_library()
    h.stage("reopened FB at 1.1.0", lambda: h.verify_use(reopened, implementation, 1))
    h.stage("reopened UDT at 1.0.0", lambda: h.verify_use(reopened, initial[UDT], 1))
    h.stage("reopened FB body 1.1.0", lambda: require_shape(block_shape(FB), "1.1.0", "Reopened FB"))
    require(h.stage("capture unrelated content after reopen", lambda: unrelated_snapshot(consumer_plc)) == before,
            "Reopened original changed unrelated native content")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    inputs = sorted(p for folder in (ROOT / "fixtures/libraries/type-lifecycle", ROOT / "fixtures/libraries/release-changes")
                    for p in folder.iterdir() if p.suffix in (".scl", ".udt"))
    Harness(args, "rc_", "V21 standard FB implementation-changing and breaking-interface releases", [
        "Behavior is asserted from native exports (body and interface), not executed on a CPU or PLCSIM.",
        "New versions are created from documents: TIA refuses replacing an edited version's test instance by source generation.",
        "The breaking update is qualified as an expected compile failure followed by checkpoint recovery; caller migration in the same transaction is not offered.",
        "One PLC, standard groups, no software units, protected/F types or crash injection."], inputs).run(body)
