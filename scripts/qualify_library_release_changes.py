"""Qualify implementation-changing and breaking-interface FB releases through MCP in owned scratch projects only."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import traceback
import uuid
import xml.etree.ElementTree as ET  # Native exports from this run's owned scratch only.

from engineering_preservation import native_payload_hash
from smoke_mcp import Client

ROOT = Path(__file__).resolve().parents[1]
FB = "TiaMcpLibraryGate"
UDT = "TiaMcpLibraryState"


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def identity(version):
    for key in ("typeGuid", "versionGuid"):
        require(uuid.UUID(version[key]).int != 0, "Native identity is empty")
    return {key: version[key] for key in ("typeGuid", "versionGuid")}


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


def run(args):
    require(args.run_native, "Explicit --run-native is required")
    directory = args.artifacts.resolve() / ("rc_" + uuid.uuid4().hex[:8])
    scratch_name = "TiaMcpScratch_20000101_000000_12345678"
    require(len(str(directory / "scratch" / scratch_name / scratch_name)) <= 143,
            "Choose a shorter --artifacts path (for example artifacts/lq)")
    directory.mkdir(parents=True, exist_ok=False)
    documents = directory / "documents"
    shutil.copytree(ROOT / "fixtures/libraries/type-lifecycle", documents)
    changes = ROOT / "fixtures/libraries/release-changes"
    report = {"schemaVersion": 1, "profile": "V21 standard FB implementation-changing and breaking-interface releases",
              "completed": False, "runtimeVerified": False, "steps": [], "mutations": [],
              "limitations": ["Behavior is asserted from native exports (body and interface), not executed on a CPU or PLCSIM.",
                              "The breaking update is qualified as an expected compile failure followed by checkpoint recovery; "
                              "caller migration in the same transaction is not offered.",
                              "One PLC, standard groups, no software units, protected/F types or crash injection."]}
    for name, path in (("host", args.host), ("worker", args.worker), ("runner", Path(__file__))):
        report[name] = {"path": str(path.resolve()), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
    report["inputDocuments"] = [{"name": p.name, "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
                                for folder in (documents, changes) for p in sorted(folder.iterdir()) if p.suffix in (".scl", ".udt")]
    client = None

    def persist():
        if client is not None:
            report["toolCalls"] = client.tool_calls
        (directory / "qualification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")

    def stage(name, operation):
        row = {"name": name, "status": "invoking"}
        report["steps"].append(row)
        persist()
        print("START " + name, flush=True)
        try:
            result = operation()
            row.update(status="passed", result=result)
            print("PASS " + name, flush=True)
            return result
        except Exception as error:
            row.update(status="failed", failure=str(error))
            raise
        finally:
            persist()

    def prepare(kind, payload):
        return stage("prepare " + kind, lambda: client.tool("change_prepare", {"kind": kind, "payload": payload}))

    def apply_args(prepared):
        return {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                "idempotencyKey": "releasequalify-" + uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True}

    def apply(kind, payload):
        prepared = prepare(kind, payload)
        dispatch = {"kind": kind, "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "state": "dispatching"}
        report["mutations"].append(dispatch)
        persist()
        result = stage("apply " + kind, lambda: client.tool("change_apply", apply_args(prepared)))
        require(result["state"] == "applied", "Native change did not reach applied")
        dispatch["state"] = "applied"
        persist()
        return result

    def owned_portal(label):
        stage("start owned TIA " + label, lambda: client.tool("tia_start"))
        status = client.tool("tia_session_status")
        require(status.get("ownsPortalProcess") is True and not status.get("projects"), "Session must be owned and empty")

    def owned_scratch(label):
        owned_portal(label)
        created = stage("create owned scratch " + label, lambda: client.tool("tia_create_scratch"))
        require(created.get("completed") is True and created.get("compile", {}).get("errorCount") == 0, "Scratch compile failed")
        inventory = client.tool("project_inventory", {"maxItems": 1000})
        require(len(inventory.get("plcs", [])) == 1, "Expected one owned PLC")
        return inventory["plcs"][0]["path"]

    def source_ref(identifier):
        matches = [x for x in client.tool("library_list")["libraries"] if x["libraryId"] == identifier]
        require(len(matches) == 1, "Library identity no longer resolves")
        return {"libraryId": identifier, "revision": matches[0]["revision"]}

    def query(identifier, **selector):
        return client.tool("library_query", {"request": {"libraryId": identifier, "maxItems": 200, **selector}})

    def versions(identifier, name):
        result = query(identifier)
        require(result["completeness"]["scopeComplete"] and result.get("nextOffset") is None, "Type metadata incomplete")
        return [x for x in result["items"] if x.get("kind") == "version" and x.get("typeName") == name]

    def selected_payload(identifier, version, **extra):
        return {"schemaVersion": 1, "source": source_ref(identifier), "selection": [identity(version)], **extra}

    def verify_use(identifier, version, expected):
        result = query(identifier, query="uses", **identity(version))
        require(result["completeness"]["scopeComplete"] and result.get("nextOffset") is None, "Native instance coverage incomplete")
        require(len(result["items"]) == expected,
                "Native exact-version use count differs: expected %d, observed %d" % (expected, len(result["items"])))
        for row in result["items"]:
            require(identity(row["version"]) == identity(version), "Native use GUID differs")
        return result

    def release(identifier, version, number):
        applied = apply("library_release_version", selected_payload(identifier, version, versionNumber=number,
                        author="TIA MCP qualification", comment="Owned fixture release " + number))
        returned = applied["journal"]["result"]["objects"]
        require(len(returned) == 1 and returned[0]["typeGuid"] == version["typeGuid"], "Release changed the owning native type")
        committed = [x for x in versions(identifier, version["typeName"]) if identity(x) == identity(returned[0])]
        require(len(committed) == 1 and committed[0]["state"] == "Committed" and committed[0]["version"] == number,
                "Native release state/version/GUID differs")
        return committed[0]

    def exported_version_shape(identifier, version):
        rows = [x for x in query(identifier)["items"] if x.get("kind") == "type" and x.get("typeGuid") == version["typeGuid"]]
        require(len(rows) == 1 and rows[0]["supportedExportFormats"], "No native export format")
        formats = rows[0]["supportedExportFormats"]
        preferred = next((x for x in formats if x.lower() in ("simaticml", "xml", ".xml")), formats[0])
        result = query(identifier, query="export", format=preferred, **identity(version))
        require(result["files"] and not result["filesTruncated"], "Native export is empty or truncated")
        shapes = []
        for artifact in result["files"]:
            raw = Path(artifact["path"]).read_bytes()
            require(hashlib.sha256(raw).hexdigest() == artifact["sha256"], "Native export hash differs")
            if artifact["path"].lower().endswith(".xml"):
                shapes.append(fb_shape(raw))
        require(len(shapes) == 1, "Expected exactly one native XML FB document")
        return {"files": result["files"], "shape": shapes[0]}

    def block_shape(name):
        exported = client.tool("block_read", {"blockName": name})
        raw = Path(exported["artifact"]["path"]).read_bytes()
        require(hashlib.sha256(raw).hexdigest() == exported["artifact"]["sha256"], "Native block export hash differs")
        return {"artifact": exported["artifact"], "shape": fb_shape(raw)}

    def require_shape(observed, number, where):
        require(observed["shape"] == EXPECTED[number], "%s does not match independent %s expectation: %s"
                % (where, number, observed["shape"]))
        return observed

    def edit_and_release(identifier, plc, previous, source_name, number):
        apply("library_edit_version", selected_payload(identifier, previous, testInstanceName=FB, target={"plcPath": plc}))
        in_work = [x for x in versions(identifier, FB) if x["state"] == "InWork"]
        require(len(in_work) == 1 and in_work[0]["versionGuid"] != previous["versionGuid"], "Edit did not create a distinct InWork GUID")
        apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": (changes / source_name).read_text(),
                                "expectedBlockNames": [FB]})
        stage("verify edited test instance still linked to InWork " + number, lambda: verify_use(identifier, in_work[0], 1))
        released = release(identifier, in_work[0], number)
        stage("export released " + number, lambda: require_shape(exported_version_shape(identifier, released), number, "Released export"))
        return released

    def archive(identifier):
        result = apply("library_archive", {"schemaVersion": 1, "source": source_ref(identifier)})["journal"]["result"]["archive"]
        require(hashlib.sha256(Path(result["path"]).read_bytes()).hexdigest() == result["sha256"], "Native archive hash differs")
        return result

    def publish_generation(project_library, global_library, version):
        apply("library_publish", selected_payload(project_library, version, destinationLibraryId=global_library))
        apply("library_set_default", selected_payload(global_library, version))
        return archive(global_library)

    def open_archive(artifact):
        staged = client.tool("library_package_stage", {"hostPath": artifact["path"]})
        candidates = client.tool("library_package_inspect", {"packageId": staged["packageId"]})["items"]
        require(len(candidates) == 1 and candidates[0]["kind"] == "globalLibraryArchive", "Archive candidate differs")
        return stage("open staged archive", lambda: client.tool("library_open", {"request": {
            "packageId": staged["packageId"], "candidateId": candidates[0]["candidateId"]}}))["libraryId"]

    def unrelated_snapshot(plc):
        inventory = client.tool("project_inventory", {"maxItems": 1000})
        names = {"TiaMcp_IsEnabled", "TiaMcp_EnableGate", "TiaMcp_EnableGate_DB", "TiaMcpLibraryState_DB"}
        blocks = sorted((x for x in inventory["blocks"] if x["name"] in names and x["plcPath"] == plc), key=lambda x: x["path"])
        require(len(blocks) == len(names), "Unrelated fixture blocks are missing or ambiguous")
        exported = [(b["path"], client.tool("block_read", {"blockPath": b["path"]})) for b in blocks]
        exported.append(("type:" + UDT, client.tool("type_read", {"plcPath": plc, "typeName": UDT})))
        rows = []
        for path, result in exported:
            raw = Path(result["artifact"]["path"]).read_bytes()
            require(hashlib.sha256(raw).hexdigest() == result["artifact"]["sha256"], "Unrelated native export hash differs")
            rows.append({"path": path, "nativeDocumentWithoutDocumentInfoSha256": native_payload_hash(raw.decode("utf-8-sig"))})
        return rows

    def expect_breaking_compile_failure(payload):
        prepared = prepare("library_update_project", payload)
        dispatch = {"kind": "library_update_project", "changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                    "state": "dispatching", "expectedOutcome": "CompileFailed"}
        report["mutations"].append(dispatch)
        persist()
        result = client.request("tools/call", {"name": "change_apply", "arguments": apply_args(prepared)})
        error = (result.get("structuredContent") or {}).get("error") or {}
        details = error.get("details") or {}
        dispatch.update(state=details.get("state"), errorCode=error.get("code"))
        report["breakingUpdate"] = {"isError": result.get("isError"), "error": error}
        persist()
        require(result.get("isError") is True and error.get("code") == "CompileFailed", "Breaking update did not fail compilation")
        require(details.get("state") == "recovery_required", "Failed breaking update is not retained for recovery")
        journal = details["journal"]
        events = journal.get("events", [])
        require(any(e.get("stage") == "native_mutation" and e.get("state") == "completed" for e in events),
                "Native update did not complete before compilation")
        require(not any(e.get("stage", "").startswith("save") for e in events), "Failed breaking update was saved")
        errors = compile_errors(journal["compile"])
        report["breakingUpdate"]["errors"] = errors
        persist()
        require(errors, "No native compiler error message was reported")
        text = json.dumps(errors)
        require("Main" in text, "Compiler errors do not identify the stale caller Main")
        require(not any(n in text for n in ("TiaMcp_EnableGate", "TiaMcp_IsEnabled")),
                "Compiler errors reach unrelated fixture blocks")
        return prepared

    try:
        client = Client(args.host.resolve(), args.timeout, ["--worker", str(args.worker.resolve()),
                        "--artifacts", str(directory), "--sim-worker", str(directory / "sim-not-used.exe")])
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                       "clientInfo": {"name": "native-library-release-qualification", "version": "1"}})
        client.notify("notifications/initialized")

        # Authoring: three FB generations from one native type identity.
        plc = owned_scratch("authoring")
        project_library = client.tool("library_open", {"request": {"kind": "project"}})["libraryId"]
        initial = {}
        for name, composition in ((UDT, "types"), (FB, "blocks")):
            apply("library_create_type", {"schemaVersion": 1, "destinationLibraryId": project_library,
                  "documentDirectory": str(documents), "documentName": name, "targetEnvironment": "selectedPlc",
                  "destinationComposition": composition, "target": {"plcPath": plc}})
            created = versions(project_library, name)
            require(len(created) == 1 and created[0]["state"] == "InWork", "New type must expose one native InWork version")
            initial[name] = release(project_library, created[0], "1.0.0")
        stage("export released 1.0.0", lambda: require_shape(exported_version_shape(project_library, initial[FB]), "1.0.0", "Released export"))
        implementation = edit_and_release(project_library, plc, initial[FB], "TiaMcpLibraryGate.implementation.scl", "1.1.0")
        global_library = apply("library_create", {"schemaVersion": 1, "name": "ReleaseQualification"})["journal"]["result"]["library"]["libraryId"]
        apply("library_publish", {"schemaVersion": 1, "source": source_ref(project_library),
              "selection": [identity(initial[UDT]), identity(initial[FB])], "destinationLibraryId": global_library})
        generation = {"1.0.0": archive(global_library), "1.1.0": publish_generation(project_library, global_library, implementation)}
        breaking = edit_and_release(project_library, plc, implementation, "TiaMcpLibraryGate.breaking.scl", "2.0.0")
        generation["2.0.0"] = publish_generation(project_library, global_library, breaking)
        fb_versions = {"1.0.0": initial[FB], "1.1.0": implementation, "2.0.0": breaking}
        for version in fb_versions.values():
            require(version["typeGuid"] == initial[FB]["typeGuid"], "A release changed the FB type identity")
        require(len({v["versionGuid"] for v in fb_versions.values()}) == 3, "Release version GUIDs are not distinct")
        report["nativeVersions"] = {"udt": initial[UDT], "fb": fb_versions}
        client.tool("library_close", {"libraryId": global_library})
        stage("close owned authoring session", lambda: client.tool("tia_detach"))

        # Consumer: consume 1.0.0, update to the implementation change, then attempt the breaking update.
        consumer_plc = owned_scratch("consumer")
        source = open_archive(generation["1.0.0"])
        for version in (initial[UDT], initial[FB]):
            apply("library_instantiate", selected_payload(source, version, target={"plcPath": consumer_plc}))
        client.tool("library_close", {"libraryId": source})
        consumer_library = client.tool("library_open", {"request": {"kind": "project"}})["libraryId"]
        apply("source_upsert", {"scopePath": consumer_plc, "language": "SCL",
              "source": (documents / "ConsumerReferences.scl").read_text(),
              "expectedBlockNames": ["TiaMcpLibraryGate_DB", "TiaMcpLibraryState_DB", "Main"]})
        stage("consumer FB at 1.0.0", lambda: verify_use(consumer_library, initial[FB], 1))
        stage("consumer FB body 1.0.0", lambda: require_shape(block_shape(FB), "1.0.0", "Consumer FB"))
        before = stage("capture unrelated content before implementation update", lambda: unrelated_snapshot(consumer_plc))

        source = open_archive(generation["1.1.0"])
        applied = apply("library_update_project", selected_payload(source, implementation, updateScope="entireSelectedPlc",
                        target={"plcPath": consumer_plc}))
        require(applied["journal"]["compile"]["errorCount"] == 0, "Implementation update compile failed")
        client.tool("library_close", {"libraryId": source})
        stage("consumer FB at 1.1.0", lambda: verify_use(consumer_library, implementation, 1))
        stage("consumer FB has no 1.0.0 uses", lambda: verify_use(consumer_library, initial[FB], 0))
        stage("consumer UDT unaffected", lambda: verify_use(consumer_library, initial[UDT], 1))
        stage("consumer FB body 1.1.0", lambda: require_shape(block_shape(FB), "1.1.0", "Updated consumer FB"))
        after = stage("capture unrelated content after implementation update", lambda: unrelated_snapshot(consumer_plc))
        require(before == after, "Implementation update changed unrelated native content")
        report["implementationUpdatePreservedUnrelated"] = True

        source = open_archive(generation["2.0.0"])
        failed = stage("breaking update fails compile at stale caller", lambda: expect_breaking_compile_failure(
            selected_payload(source, breaking, updateScope="entireSelectedPlc", target={"plcPath": consumer_plc})))
        consumer_path = client.tool("tia_session_status")["projects"][0]["path"]
        stage("discard failed consumer session", lambda: client.tool("tia_detach"))

        # Recovery: the checkpoint and the unsaved original both keep the exact 1.1.0 link.
        owned_portal("recovery")
        restored = stage("restore breaking-update checkpoint", lambda: client.tool("change_restore", {
            "changeId": failed["changeId"], "previewHash": failed["previewHash"]}))
        require(restored["state"] == "restored_to_new_locations" and restored.get("originalsPreserved") is True, "Checkpoint was not restored")
        recovered = client.tool("library_open", {"request": {"kind": "project"}})["libraryId"]
        stage("recovered FB at 1.1.0", lambda: verify_use(recovered, implementation, 1))
        stage("recovered FB body 1.1.0", lambda: require_shape(block_shape(FB), "1.1.0", "Recovered FB"))
        stage("close recovery session", lambda: client.tool("tia_detach"))
        owned_portal("reopen")
        stage("reopen original consumer", lambda: client.tool("project_open", {"path": consumer_path}))
        reopened = client.tool("library_open", {"request": {"kind": "project"}})["libraryId"]
        stage("reopened FB at 1.1.0", lambda: verify_use(reopened, implementation, 1))
        stage("reopened UDT at 1.0.0", lambda: verify_use(reopened, initial[UDT], 1))
        stage("reopened FB body 1.1.0", lambda: require_shape(block_shape(FB), "1.1.0", "Reopened FB"))
        reopened_unrelated = stage("capture unrelated content after reopen", lambda: unrelated_snapshot(consumer_plc))
        require(reopened_unrelated == before, "Reopened original changed unrelated native content")
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        report["traceback"] = traceback.format_exc()
        raise
    finally:
        if client is not None:
            try:
                client.tool("tia_detach")
            except Exception as error:
                report["detachFailure"] = str(error)
            client.close()
            report["hostExitCode"] = client.process.returncode
            report["hostLogs"] = client.errors
        persist()
        print("Report: " + str(directory / "qualification.json"), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    run(parser.parse_args())
