"""Exercise versioned FB/UDT library operations through MCP in owned scratch projects only."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import traceback
import uuid

from engineering_preservation import native_payload_hash
from smoke_mcp import Client


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def identity(version):
    for key in ("typeGuid", "versionGuid"):
        require(uuid.UUID(version[key]).int != 0, "Native identity is empty")
    return {key: version[key] for key in ("typeGuid", "versionGuid")}


def run(args):
    require(args.run_native, "Explicit --run-native is required")
    directory = args.artifacts.resolve() / ("nt_" + uuid.uuid4().hex[:8])
    scratch_name = "TiaMcpScratch_20000101_000000_12345678"
    require(len(str(directory / "scratch" / scratch_name / scratch_name)) <= 143,
            "Choose a shorter --artifacts path (for example artifacts/lq)")
    directory.mkdir(parents=True, exist_ok=False)
    documents = directory / "documents"
    shutil.copytree(Path(__file__).resolve().parents[1] / "fixtures/libraries/type-lifecycle", documents)
    report = {"schemaVersion": 1, "profile": "V21 standard independent FB and UDT type lifecycle",
              "completed": False, "runtimeVerified": False, "steps": [], "mutations": [],
              "freshAuthoringVerified": False,
              "limitations": ["The second FB release changes release metadata, not program behavior or interface.",
                              "No dependency graph, protected/F types, runtime execution or crash injection is qualified."]}
    for name, path in (("host", args.host), ("worker", args.worker), ("runner", Path(__file__))):
        report[name] = {"path": str(path.resolve()), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
    report["inputDocuments"] = [{"name": p.name, "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
                                for p in documents.iterdir() if p.suffix in (".scl", ".udt")]
    client = None

    def persist():
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

    def apply(kind, payload):
        prepared = stage("prepare " + kind, lambda: client.tool("change_prepare", {"kind": kind, "payload": payload}))
        dispatch = {"kind": kind, "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "state": "dispatching"}
        report["mutations"].append(dispatch)
        persist()
        result = stage("apply " + kind, lambda: client.tool("change_apply", {
            "changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
            "idempotencyKey": "typequalify-" + uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True}))
        require(result["state"] == "applied", "Native change did not reach applied")
        dispatch["state"] = "applied"
        persist()
        return result

    def owned_scratch():
        stage("start owned TIA", lambda: client.tool("tia_start"))
        status = client.tool("tia_session_status")
        require(status.get("ownsPortalProcess") is True and not status.get("projects"), "Session must be owned and empty")
        created = stage("create owned scratch", lambda: client.tool("tia_create_scratch"))
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

    def release(identifier, version, number):
        applied = apply("library_release_version", selected_payload(identifier, version, versionNumber=number,
                        author="TIA MCP qualification", comment="Owned fixture release " + number))
        # Siemens assigns a committed identity during Release; the temporary InWork GUID is not stable.
        returned = applied["journal"]["result"]["objects"]
        require(len(returned) == 1 and returned[0]["typeGuid"] == version["typeGuid"], "Release changed the owning native type")
        committed_identity = identity(returned[0])
        report.setdefault("releaseIdentityTransitions", []).append({"inWork": identity(version), "committed": committed_identity})
        released = [x for x in versions(identifier, version["typeName"]) if identity(x) == committed_identity]
        require(len(released) == 1 and released[0]["state"] == "Committed" and released[0]["version"] == number,
                "Native release state/version/GUID differs")
        return released[0]

    def export_version(identifier, version):
        rows = query(identifier)["items"]
        matches = [x for x in rows if x.get("kind") == "type" and x.get("typeGuid") == version["typeGuid"]]
        require(len(matches) == 1 and matches[0]["supportedExportFormats"], "No native export format")
        formats = matches[0]["supportedExportFormats"]
        preferred = next((x for x in formats if x.lower() in ("simaticml", "xml", ".xml")), formats[0])
        result = stage("export native " + version["typeName"], lambda: query(identifier, query="export", format=preferred, **identity(version)))
        require(result["files"] and not result["filesTruncated"], "Native export is empty or truncated")
        for artifact in result["files"]:
            require(hashlib.sha256(Path(artifact["path"]).read_bytes()).hexdigest() == artifact["sha256"], "Native export hash differs")

    def archive(identifier):
        result = apply("library_archive", {"schemaVersion": 1, "source": source_ref(identifier)})["journal"]["result"]["archive"]
        require(hashlib.sha256(Path(result["path"]).read_bytes()).hexdigest() == result["sha256"], "Native archive hash differs")
        return result

    def open_archive(artifact):
        staged = client.tool("library_package_stage", {"hostPath": artifact["path"]})
        candidates = client.tool("library_package_inspect", {"packageId": staged["packageId"]})["items"]
        require(len(candidates) == 1 and candidates[0]["kind"] == "globalLibraryArchive", "Archive candidate differs")
        return stage("open staged archive", lambda: client.tool("library_open", {"request": {
            "packageId": staged["packageId"], "candidateId": candidates[0]["candidateId"]}}))["libraryId"]

    def verify_use(identifier, version, expected):
        result = query(identifier, query="uses", **identity(version))
        report.setdefault("instanceReadbacks", []).append({"libraryId": identifier, "expectedVersion": identity(version),
                                                          "expectedCount": expected, "result": result})
        persist()
        require(result["completeness"]["scopeComplete"] and result.get("nextOffset") is None, "Native instance coverage incomplete")
        require(len(result["items"]) == expected,
                "Native exact-version use count differs: expected %d, observed %d" % (expected, len(result["items"])))
        for row in result["items"]:
            require(identity(row["version"]) == identity(version), "Native use GUID differs")
            require(row["nativeInstance"].get("Name") == version["typeName"], "Native test instance name differs")
        return result

    def unrelated_block_snapshot(plc_path):
        inventory = client.tool("project_inventory", {"maxItems": 1000})
        expected = {"Main", "TiaMcp_IsEnabled", "TiaMcp_EnableGate", "TiaMcp_EnableGate_DB",
                    "TiaMcpLibraryState_DB"}
        blocks = [x for x in inventory["blocks"] if x["name"] in expected and x["plcPath"] == plc_path]
        require(len(blocks) == len(expected), "Unrelated fixture blocks are missing or ambiguous")
        rows = []
        exported_objects = [(block["path"], client.tool("block_read", {"blockPath": block["path"]}))
                            for block in sorted(blocks, key=lambda x: x["path"])]
        udt = client.tool("type_read", {"plcPath": plc_path, "typeName": "TiaMcpLibraryState"})
        exported_objects.append((udt["type"]["path"], udt))
        for path, exported in exported_objects:
            artifact = exported["artifact"]
            raw = Path(artifact["path"]).read_bytes()
            require(hashlib.sha256(raw).hexdigest() == artifact["sha256"], "Unrelated native export hash differs")
            rows.append({"path": path, "artifact": artifact,
                         "nativeDocumentWithoutDocumentInfoSha256": native_payload_hash(raw.decode("utf-8-sig"))})
        return rows

    def reject_unreferenced_update(payload):
        before = client.tool("project_inventory", {"maxItems": 1000})
        result = client.request("tools/call", {"name": "change_prepare", "arguments": {
            "kind": "library_update_project", "payload": payload}})
        report["unreferencedUpdateRejection"] = result
        persist()
        require(result.get("isError") is True and "LibraryUpdateCleanupRisk" in json.dumps(result),
                "Unreferenced native cleanup risk was not rejected before mutation")
        after = client.tool("project_inventory", {"maxItems": 1000})
        for key in ("blocks", "types"):
            require(before.get(key) == after.get(key), "Rejected update changed native object inventory")
        return result

    def reopen_owned_authoring_baseline(path):
        path = path.resolve(strict=True)
        require(path.is_relative_to(args.artifacts.resolve()) and path.parent.name.startswith("nt_"),
                "Baseline report must be from this runner inside the explicit artifact root")
        raw = path.read_bytes()
        baseline = json.loads(raw)
        require(baseline.get("profile") == report["profile"] and baseline.get("hostExitCode") == 0
                and not baseline.get("detachFailure"), "Baseline is not a cleanly closed owned type fixture")
        passed = [x for x in baseline["steps"] if x.get("status") == "passed"]
        require(sum(x["name"] == "apply library_create_type" for x in passed) == 2
                and sum(x["name"] == "apply library_release_version" for x in passed) == 2,
                "Baseline must contain exactly two successfully authored and released initial types")
        scratch = [x for x in passed if x["name"] == "create owned scratch"]
        require(len(scratch) == 1 and scratch[0]["result"].get("completed") is True, "Missing owned scratch creation evidence")
        project = Path(scratch[0]["result"]["project"]["path"]).resolve(strict=True)
        require(project.is_relative_to(path.parent / "scratch") and project.suffix.lower() == ".ap21",
                "Baseline project is outside its runner-owned scratch directory")
        expected = []
        for name in ("TiaMcpLibraryState", "TiaMcpLibraryGate"):
            uses = [x for x in passed if x["name"] == "verify authored " + name]
            exports = [x for x in passed if x["name"] == "export native " + name]
            require(len(uses) == len(exports) == 1 and len(uses[0]["result"]["items"]) == 1,
                    "Baseline lacks independently queried native authored use and export evidence")
            version = uses[0]["result"]["items"][0]["version"]
            require(version["state"] == "Committed" and version["version"] == "1.0.0", "Baseline is not initial committed generation")
            for artifact in exports[0]["result"]["files"]:
                exported = Path(artifact["path"]).resolve(strict=True)
                require(exported.is_relative_to(path.parent)
                        and hashlib.sha256(exported.read_bytes()).hexdigest() == artifact["sha256"], "Baseline export evidence changed")
            expected.append(version)
        report["authoringBaseline"] = {"reportPath": str(path), "reportSha256": hashlib.sha256(raw).hexdigest(),
                                      "projectPath": str(project), "scope": "Authoring evidence linked from prior owned run; not newly executed."}
        (directory / "authoring-baseline-report.json").write_bytes(raw)
        stage("start owned baseline portal", lambda: client.tool("tia_start"))
        status = client.tool("tia_session_status")
        require(status.get("ownsPortalProcess") is True and not status.get("projects"), "Baseline portal must be owned and empty")
        stage("reopen owned authoring baseline", lambda: client.tool("project_open", {"path": str(project)}))
        inventory = client.tool("project_inventory", {"maxItems": 1000})
        require(len(inventory.get("plcs", [])) == 1, "Baseline must have exactly one PLC")
        library = client.tool("library_open", {"request": {"kind": "project"}})["libraryId"]
        for version in expected:
            actual = versions(library, version["typeName"])
            require(len(actual) == 1 and identity(actual[0]) == identity(version)
                    and actual[0]["state"] == "Committed" and actual[0]["version"] == "1.0.0",
                    "Baseline native identity/state changed after prior evidence")
            stage("verify reopened baseline " + version["typeName"], lambda v=version: verify_use(library, v, 1))
        return inventory["plcs"][0]["path"], library, expected

    try:
        client = Client(args.host.resolve(), args.timeout, ["--worker", str(args.worker.resolve()),
                        "--artifacts", str(directory), "--sim-worker", str(directory / "sim-not-used.exe")])
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                       "clientInfo": {"name": "native-library-type-qualification", "version": "1"}})
        client.notify("notifications/initialized")
        if args.authoring_baseline_report:
            plc, project_library, released = reopen_owned_authoring_baseline(args.authoring_baseline_report)
        else:
            plc = owned_scratch()
            project_library = client.tool("library_open", {"request": {"kind": "project"}})["libraryId"]
            released = []
            for name, composition in (("TiaMcpLibraryState", "types"), ("TiaMcpLibraryGate", "blocks")):
                apply("library_create_type", {"schemaVersion": 1, "destinationLibraryId": project_library,
                      "documentDirectory": str(documents), "documentName": name, "targetEnvironment": "selectedPlc",
                      "destinationComposition": composition, "target": {"plcPath": plc}})
                created = versions(project_library, name)
                require(len(created) == 1 and created[0]["state"] == "InWork", "New type must expose one native InWork version")
                version = release(project_library, created[0], "1.0.0")
                released.append(version)
                stage("verify authored " + name, lambda v=version: verify_use(project_library, v, 1))
                export_version(project_library, version)
            report["freshAuthoringVerified"] = True
        global_library = apply("library_create", {"schemaVersion": 1, "name": "TypeQualification"})["journal"]["result"]["library"]["libraryId"]
        apply("library_publish", {"schemaVersion": 1, "source": source_ref(project_library),
              "selection": [identity(v) for v in released], "destinationLibraryId": global_library})
        generation_one = archive(global_library)
        previous = released[1]
        apply("library_edit_version", selected_payload(project_library, previous,
              testInstanceName=previous["typeName"], target={"plcPath": plc}))
        in_work = [x for x in versions(project_library, previous["typeName"]) if x["state"] == "InWork"]
        require(len(in_work) == 1 and in_work[0]["versionGuid"] != previous["versionGuid"], "Edit did not create a distinct InWork GUID")
        updated = release(project_library, in_work[0], "1.1.0")
        apply("library_set_default", selected_payload(project_library, updated))
        apply("library_publish", selected_payload(project_library, updated, destinationLibraryId=global_library))
        apply("library_set_default", selected_payload(global_library, updated))
        generation_two = archive(global_library)
        report["nativeVersions"] = {"generationOne": released, "generationTwoFB": updated}
        client.tool("library_close", {"libraryId": global_library})
        stage("close owned authoring session", lambda: client.tool("tia_detach"))
        consumer_plc = owned_scratch()
        source_one = open_archive(generation_one)
        for version in released:
            apply("library_instantiate", selected_payload(source_one, version, target={"plcPath": consumer_plc}))
        consumer_library = client.tool("library_open", {"request": {"kind": "project"}})["libraryId"]
        for version in released:
            stage("verify consumed " + version["typeName"], lambda v=version: verify_use(consumer_library, v, 1))
        client.tool("library_close", {"libraryId": source_one})
        source_two = open_archive(generation_two)
        update_payload = selected_payload(source_two, updated, updateScope="entireSelectedPlc", target={"plcPath": consumer_plc})
        stage("reject unreferenced native cleanup before mutation", lambda: reject_unreferenced_update(update_payload))
        for version in released:
            stage("verify rejected update retained " + version["typeName"], lambda v=version: verify_use(consumer_library, v, 1))
        apply("source_upsert", {"scopePath": consumer_plc, "language": "SCL",
              "source": (documents / "ConsumerReferences.scl").read_text(),
              "expectedBlockNames": ["TiaMcpLibraryGate_DB", "TiaMcpLibraryState_DB", "Main"]})
        stage("consumer native cross references", lambda: client.tool("xref_query", {"plcPath": consumer_plc, "blockName": "Main", "maxItems": 1000}))
        for version in released:
            stage("verify referenced " + version["typeName"], lambda v=version: verify_use(consumer_library, v, 1))
        preserved_before = stage("capture unrelated native blocks before update", lambda: unrelated_block_snapshot(consumer_plc))
        applied_update = apply("library_update_project", selected_payload(source_two, updated,
                               updateScope="entireSelectedPlc", target={"plcPath": consumer_plc}))
        require(applied_update["journal"]["compile"]["errorCount"] == 0, "Updated project compile failed")
        stage("verify updated FB", lambda: verify_use(consumer_library, updated, 1))
        stage("verify previous FB has no uses", lambda: verify_use(consumer_library, previous, 0))
        stage("verify unaffected UDT", lambda: verify_use(consumer_library, released[0], 1))
        preserved_after = stage("capture unrelated native blocks after update", lambda: unrelated_block_snapshot(consumer_plc))
        require([(x["path"], x["nativeDocumentWithoutDocumentInfoSha256"]) for x in preserved_before]
                == [(x["path"], x["nativeDocumentWithoutDocumentInfoSha256"]) for x in preserved_after],
                "Update changed an unrelated native block document")
        report["unrelatedNativeBlocksPreserved"] = True
        consumer_path = client.tool("tia_session_status")["projects"][0]["path"]
        stage("close owned consumer session", lambda: client.tool("tia_detach"))
        stage("start owned reopen session", lambda: client.tool("tia_start"))
        status = client.tool("tia_session_status")
        require(status.get("ownsPortalProcess") is True and not status.get("projects"), "Reopen session must be owned and empty")
        stage("reopen saved consumer", lambda: client.tool("project_open", {"path": consumer_path}))
        reopened = client.tool("library_open", {"request": {"kind": "project"}})["libraryId"]
        stage("reopened FB exact native link", lambda: verify_use(reopened, updated, 1))
        stage("reopened UDT exact native link", lambda: verify_use(reopened, released[0], 1))
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
    parser.add_argument("--authoring-baseline-report", type=Path,
                        help="Reuse only a verified prior runner-owned initial-generation authoring fixture; prior authoring is linked, not rerun.")
    run(parser.parse_args())
