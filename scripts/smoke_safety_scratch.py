"""Explicitly create an isolated scratch project and validate offline F-CPU inspection.

Uses only the built MCP host. Never attaches to user projects or uses an online
download/runtime operation. A new F-station is added through the prepared-change
workflow, with a checkpoint archive, compilation and save.
"""
import json
import pathlib
import uuid

from smoke_mcp import ROOT
import safety_lifecycle
from engineering_preservation import require
from compilation_evidence import require_clean_compile, capture_device_targets


def main(destination=None):
    destination = pathlib.Path(destination) if destination is not None else ROOT / "artifacts" / "safety-scratch-smoke.json"
    host = ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe"
    worker = ROOT / "src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe"
    require(not destination.exists(), "Choose a new report path; prior safety evidence is immutable")
    report = {"steps": [], "completed": False, "scope": "New worker-owned offline scratch project only"}

    def persist():
        safety_lifecycle.sync_steps(report)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    session = safety_lifecycle.new_session(host, worker, ROOT / "artifacts", report, persist,
        ROOT / "artifacts" / ("safety-lifetimes-" + uuid.uuid4().hex))

    def call(name, arguments=None):
        return safety_lifecycle.call(session, name, arguments)

    try:
        start = session.start()
        report["ownedProcessId"] = start["processId"]
        scratch = call("tia_create_scratch")
        require(scratch.get("completed") is True, 'Safety evidence requirement failed: scratch.get("completed") is True')
        require_clean_compile(scratch.get("compile"))
        project = scratch["project"]["name"]
        catalog = call("hardware_catalog_search", {"filter": "1511F-1 PN", "maxItems": 100})
        candidates = [entry for entry in catalog["entries"] if "1511F-1 PN" in entry.get("typeName", "")]
        require(candidates, "Installed catalog has no 1511F-1 PN entry")
        def version_key(entry):
            try:
                return tuple(int(part) for part in (entry.get("version") or "0").lstrip("Vv").split("."))
            except ValueError:
                return (0,)
        selected = sorted(candidates, key=lambda entry: (version_key(entry), entry["typeIdentifier"]), reverse=True)[0]
        report["selectedFcpu"] = selected
        prepared = call("change_prepare", {"kind": "hardware_add", "projectName": project, "payload": {
            "typeIdentifier": selected["typeIdentifier"], "deviceName": "SafetyScratchStation", "itemName": "SafetyScratchPLC"
        }})
        applied = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
            "idempotencyKey": "safety-scratch-" + uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
        require(applied.get("state") == "applied", "Safety hardware creation was not applied")
        require_clean_compile(applied.get("compile"))
        require({"compile_finished", "changed_project_saved"}.issubset(applied.get("stages", [])), "Safety hardware creation was not compiled and saved")
        require(applied['compile'].get('targetIdentityPolicy') == 'device-path-v1', 'Exact hardware compiler identities are required')
        session.guard()
        report['compilerInventory'] = capture_device_targets(call, scratch['project']['path'])
        session.guard()
        require_clean_compile(applied['compile'], expected_targets=report['compilerInventory']['targets'])
        inventory = call("project_inventory", {"projectName": project, "maxItems": 2000})
        fplcs = [plc for plc in inventory["plcs"] if plc["name"] == "SafetyScratchPLC"]
        require(len(fplcs) == 1, "Cannot identify the exact newly added F-PLC")
        selector = {"projectName": project, "plcPath": fplcs[0]["path"]}
        report["fPlcPath"] = fplcs[0]["path"]
        safety = call("safety_inspect", {**selector, "maxItems": 1000})
        require("runtimeGroups" in safety and "programSignatures" in safety, 'Safety evidence requirement failed: "runtimeGroups" in safety and "programSignatures" in safety')
        software = call("software_inspect", {**selector, "maxItems": 2000})
        report["coverage"] = {"fCpuCatalogVerified": True, "fCpuCreated": True,
            "changeAppliedAndCompiled": applied.get("state") == "applied", "offlineSafetyRead": True,
            "runtimeGroupCount": len(safety["runtimeGroups"]), "programSignatureCount": len(safety["programSignatures"]),
            "fBlockCount": sum(block.get("programmingLanguage", "").startswith("F_") for block in software.get("blocks", [])),
            "safetyImportTested": False, "physicalOperations": False}
        report["qualificationChecksPassed"] = True
    except BaseException as error:
        report["failure"] = str(error)
        raise
    finally:
        safety_lifecycle.finish(session, report, persist)
        print("Report: " + str(destination), flush=True)


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=pathlib.Path, help='New output path; existing evidence is never overwritten')
    main(parser.parse_args().report)
