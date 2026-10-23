"""Qualify the native PLC master-copy library path in newly owned scratch projects.

Explicit host/worker paths and --run-native are mandatory. Never attaches to a
pre-existing TIA process or writes a supplied user project. No physical or simulated
PLC download is performed. This profile does not qualify type versioning or HMI.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path
import uuid
import xml.etree.ElementTree as ET
import zipfile

from smoke_mcp import Client


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def run(args):
    require(args.run_native, "Explicit --run-native is required")
    directory = args.artifacts.resolve() / ("nl_" + uuid.uuid4().hex[:8])
    # TIA's project container has a tighter bound than normal Windows paths.
    # ScratchCreate uses the project name as both outer and inner directory.
    scratch_name = "TiaMcpScratch_20000101_000000_12345678"
    require(len(str(directory / "scratch" / scratch_name / scratch_name)) <= 143,
            "Use a shorter --artifacts path (for example artifacts/lq); the TIA scratch project path would exceed 143 characters")
    directory.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "profile": "V21 standard PLC single FC master-copy lifecycle",
              "completed": False, "runtimeVerified": False, "steps": [], "mutations": [],
              "limitations": ["Does not qualify typed FB/UDT version lifecycle, vendor packages, upgrades, HMI or runtime execution.",
                              "Recovery is intentional checkpoint retrieval after restart, not a crash injected during a Siemens call."]}
    for name, path in (("host", args.host), ("worker", args.worker)):
        report[name] = {"path": str(path.resolve()), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
    client = None

    def connect():
        value = Client(args.host.resolve(), args.timeout, ["--worker", str(args.worker.resolve()),
                       "--artifacts", str(directory), "--sim-worker", str(directory / "sim-not-used.exe")])
        value.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                      "clientInfo": {"name": "native-library-qualification", "version": "1"}})
        value.notify("notifications/initialized")
        return value

    def persist():
        (directory / "qualification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")

    def step(name, result):
        report["steps"].append({"name": name, "status": "passed", "result": result})
        persist()
        print("PASS " + name, flush=True)
        return result

    def apply(kind, payload):
        prepared = client.tool("change_prepare", {"kind": kind, "payload": payload})
        dispatch = {"kind": kind, "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "state": "dispatching"}
        report["mutations"].append(dispatch)
        persist()
        result = client.tool("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                             "idempotencyKey": "qualify-" + uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
        require(result["state"] == "applied", "Native change did not reach applied")
        dispatch["state"] = "applied"
        return step(kind, result)

    def start_empty():
        client.tool("tia_start")
        status = client.tool("tia_session_status")
        require(status.get("ownsPortalProcess") is True and not status.get("projects"), "Native instance is not owned and empty")
        return step("owned empty session", status)

    def scratch():
        result = client.tool("tia_create_scratch")
        require(result.get("completed") is True and result.get("compile", {}).get("errorCount") == 0, "Scratch compile failed")
        inventory = client.tool("project_inventory", {"maxItems": 1000})
        require(len(inventory.get("plcs", [])) == 1, "Expected exactly one scratch PLC")
        step("new scratch project", result)
        return inventory

    def source_ref(identifier):
        values = client.tool("library_list")["libraries"]
        match = [x for x in values if x["libraryId"] == identifier]
        require(len(match) == 1, "Library identity changed")
        return {"libraryId": identifier, "revision": match[0]["revision"]}

    def fixture_block(inventory, name):
        blocks = [x for x in inventory.get("blocks", []) if x.get("name") == name]
        require(len(blocks) == 1, "Expected exactly one native fixture block")
        return blocks[0]

    try:
        client = connect()
        start_empty()
        first = scratch()
        name = "LibraryProbe_" + uuid.uuid4().hex[:8]
        source = f'FUNCTION "{name}" : Bool\nVERSION : 0.1\nVAR_INPUT\n Enable : Bool;\nEND_VAR\nBEGIN\n #{name} := #Enable;\nEND_FUNCTION\n'
        apply("source_upsert", {"language": "SCL", "source": source, "expectedBlockNames": [name]})
        original = fixture_block(client.tool("project_inventory", {"maxItems": 1000}), name)
        created = apply("library_create", {"schemaVersion": 1, "name": "QualificationLibrary"})
        library_id = created["journal"]["result"]["library"]["libraryId"]
        apply("library_create_master_copy", {"schemaVersion": 1, "destinationLibraryId": library_id,
              "target": {"plcPath": first["plcs"][0]["path"]}, "object": {"kind": "block", "objectPath": original["path"]}})
        query = client.tool("library_query", {"request": {"libraryId": library_id}})
        masters = [x for x in query["items"] if x.get("kind") == "masterCopy" and x.get("name") == name]
        require(len(masters) == 1, "Created native master copy missing")
        master_path = masters[0]["masterCopyPath"]
        archived = apply("library_archive", {"schemaVersion": 1, "source": source_ref(library_id)})
        archive = archived["journal"]["result"]["archive"]
        require(hashlib.sha256(Path(archive["path"]).read_bytes()).hexdigest() == archive["sha256"], "Archive hash mismatch")
        client.tool("library_close", {"libraryId": library_id})
        client.tool("tia_detach")
        start_empty()
        consumer = scratch()
        # Exercise the user's ZIP-upload path with a real Siemens-generated
        # archive. The surrounding ZIP is a synthetic fixture, not vendor content.
        package_path = directory / "qualification-library.zip"
        with zipfile.ZipFile(package_path, "w", compression=zipfile.ZIP_DEFLATED) as bundled:
            bundled.write(archive["path"], "Library/Qualification.zal21")
            bundled.writestr("README.txt", "Owned TIA MCP qualification fixture; not a Siemens download.\n")
        package_bytes = package_path.read_bytes()
        upload = client.tool("artifact_upload_begin", {"fileName": package_path.name,
            "expectedBytes": len(package_bytes), "sha256": hashlib.sha256(package_bytes).hexdigest()})
        for offset in range(0, len(package_bytes), 16384):
            client.tool("artifact_upload_chunk", {"uploadId": upload["uploadId"], "offset": offset,
                "data": base64.b64encode(package_bytes[offset:offset + 16384]).decode("ascii")})
        committed = client.tool("artifact_upload_commit", {"uploadId": upload["uploadId"]})
        package = client.tool("library_package_stage", {"artifactRef": committed["artifactRef"]})
        step("ZIP upload and staging", {"package": package, "zipSha256": hashlib.sha256(package_bytes).hexdigest()})
        candidates = client.tool("library_package_inspect", {"packageId": package["packageId"]})["items"]
        require(len(candidates) == 1 and candidates[0]["kind"] == "globalLibraryArchive", "Native archive classification mismatch")
        opened = step("staged native archive open", client.tool("library_open", {"request": {
            "packageId": package["packageId"], "candidateId": candidates[0]["candidateId"]}}))
        require(opened.get("sourceSnapshotOwned") is True, "Native open did not establish an independent source snapshot")
        client.tool("artifact_upload_release", {"uploadId": upload["uploadId"]})
        step("release intake after independent native snapshot", client.tool("library_package_release", {"packageId": package["packageId"]}))
        applied = apply("library_instantiate", {"schemaVersion": 1, "source": source_ref(opened["libraryId"]),
                        "selection": [{"masterCopyPath": master_path}], "destinationComposition": "blocks",
                        "target": {"plcPath": consumer["plcs"][0]["path"]}})
        require(applied["journal"]["compile"]["errorCount"] == 0, "Consumer did not compile")
        installed = fixture_block(client.tool("project_inventory", {"maxItems": 1000}), name)
        require(installed["programmingLanguage"] == "SCL", "Master-copy language changed")
        exported = client.tool("block_read", {"blockPath": installed["path"]})
        data = Path(exported["artifact"]["path"]).read_bytes()
        require(hashlib.sha256(data).hexdigest() == exported["artifact"]["sha256"], "Consumer export changed")
        parsed = ET.fromstring(data)
        require(any(node.text == name for node in parsed.iter() if node.tag.rsplit('}', 1)[-1] == "Name"), "Consumer block name absent from native export")
        step("consumer compile and native readback", exported)
        consumer_path = client.tool("tia_session_status")["projects"][0]["path"]
        client.tool("tia_detach")
        # A fresh host must reconcile journal state without live native handles.
        client.close()
        client = connect()
        status = client.tool("change_status", {"changeId": applied["changeId"]})
        require(status.get("recoveredFromDisk") is True and status["state"] == "applied", "Library journal did not survive host restart")
        step("journal recovery after host restart", status)
        start_empty()
        client.tool("project_open", {"path": consumer_path})
        fixture_block(client.tool("project_inventory", {"maxItems": 1000}), name)
        step("saved consumer reopen", {"path": consumer_path, "fixtureBlock": name})
        client.tool("tia_detach")
        start_empty()
        restored = client.tool("change_restore", {"changeId": applied["changeId"], "previewHash": applied["previewHash"]})
        require(restored.get("originalsPreserved") is True, "Recovery did not retain originals")
        inventory = client.tool("project_inventory", {"maxItems": 1000})
        require(inventory.get("completeness", {}).get("scopeComplete") is True, "Recovered block coverage incomplete")
        require(not any(x.get("name") == name for x in inventory.get("blocks", [])), "Checkpoint unexpectedly includes imported fixture")
        step("checkpoint recovery to separate project", restored)
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        if client is not None:
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
