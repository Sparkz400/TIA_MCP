"""Check archive recovery and reopen of the completed isolated unit qualification fixture."""
import argparse
import json
import pathlib
import uuid

from smoke_mcp import Client, ROOT
from validate_object_model import semantic_xml


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture-report", type=pathlib.Path, required=True)
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/object-model-unit-recovery.json")
    args = parser.parse_args()
    fixture = json.loads(args.fixture_report.read_text(encoding="utf-8"))
    assert fixture["completed"], "Recovery qualification requires a completed isolated fixture report"
    project = pathlib.Path(fixture["projectPath"]).resolve()
    assert project.is_relative_to(ROOT / "artifacts") and project.name.startswith("TiaMcpScratch_") and project.suffix == ".ap21"
    client = Client(ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe", 300)
    report = {"schemaVersion": 1, "completed": False, "steps": [], "sourceProjectPath": str(project)}

    def persist():
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    def call(name, arguments=None):
        result = client.tool(name, arguments)
        report["steps"].append({"name": name, "result": result})
        persist()
        print("PASS " + name, flush=True)
        return result

    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "unit-recovery", "version": "1"}})
        client.notify("notifications/initialized")
        call("tia_start")
        call("project_open", {"path": str(project)})
        inventory = call("software_inspect", {"maxItems": 2000})
        blocks = [block for block in inventory["blocks"] if "/units/" in block["path"]]
        before = {block["path"]: semantic_xml(call("block_read", {"blockPath": block["path"]})["xml"]) for block in blocks}
        selected = next(block for block in blocks if block["name"] == "TiaMcp_IsEnabled_AChanged")
        prepared = call("change_prepare", {"kind": "block_rename", "payload": {"blockPath": selected["path"], "newName": "TiaMcp_RecoveryTemporary"}})
        applied = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "idempotencyKey": uuid.uuid4().hex,
                                       "checkpointSave": True, "compile": True, "save": True})
        assert applied["compile"]["errorCount"] == 0, applied
        restored = call("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        assert restored["state"] == "restored", restored
        recovered_path = restored["recoveredProject"]["path"]
        report["recoveredProjectPath"] = recovered_path
        assert pathlib.Path(recovered_path) != project
        stale = client.request("tools/call", {"name": "object_resolve", "arguments": {"selectors": {"objectPath": selected["path"], "projectPath": str(project)}}})
        value = stale.get("structuredContent") or json.loads(stale["content"][0]["text"])
        assert stale.get("isError") and value["error"]["code"] == "ProjectMismatch", value
        report["steps"].append({"name": "actual old-copy identity rejected", "result": value})
        for path, expected in before.items():
            assert semantic_xml(call("block_read", {"blockPath": path})["xml"]) == expected, "Recovered unit payload differs: " + path
        call("tia_detach")
        call("tia_start")
        call("project_open", {"path": recovered_path})
        for path, expected in before.items():
            assert semantic_xml(call("block_read", {"blockPath": path})["xml"]) == expected, "Reopened unit payload differs: " + path
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        try:
            call("tia_detach")
        finally:
            client.close()
            report["hostLogs"] = client.errors
            report["hostExitCode"] = client.process.returncode
            persist()


if __name__ == "__main__":
    main()
