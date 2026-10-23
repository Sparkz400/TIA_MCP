"""Verify persisted journals and explicit archive recovery in a fresh owned TIA instance."""
import argparse
import hashlib
import json
import pathlib

from smoke_mcp import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--change-id", required=True)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    args = parser.parse_args()
    assert len(args.change_id) == 32 and all(c in "0123456789abcdef" for c in args.change_id)
    journal_path = ROOT / "artifacts/changes" / ("change-" + args.change_id) / "journal.json"
    journal = json.loads(journal_path.read_text(encoding="utf-8-sig"))
    archive = pathlib.Path(journal["recoveryArchive"]).resolve()
    assert archive.is_relative_to((ROOT / "artifacts").resolve())
    expected = journal["recoveryArchiveSha256"]
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == expected
    report = {"completed": False, "steps": []}
    client = Client(ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe", 300)

    def call(name, arguments=None):
        result = client.tool(name, arguments)
        report["steps"].append({"name": name, "result": result})
        print("PASS " + name, flush=True)
        return result

    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "tia-recovery", "version": "0.2.0"}})
        client.notify("notifications/initialized")
        persisted = call("change_status", {"changeId": args.change_id})
        assert persisted["journal"]["state"] == journal["state"], persisted
        call("tia_start")
        bad = client.request("tools/call", {"name": "project_retrieve", "arguments": {"archivePath": str(archive), "expectedSha256": "0" * 64}})
        assert bad.get("isError"), bad
        structured = bad.get("structuredContent") or json.loads(bad["content"][0]["text"])
        assert structured["error"]["code"] == "CheckpointInvalid", structured
        report["steps"].append({"name": "reject altered archive hash", "result": structured})
        recovered = call("project_retrieve", {"archivePath": str(archive), "expectedSha256": expected})
        assert recovered["retrieved"], recovered
        assert pathlib.Path(recovered["project"]["path"]).resolve() != pathlib.Path(journal["projectPath"]).resolve()
        call("project_inventory", {"maxItems": 2000})
        call("block_read", {"blockName": "TiaMcp_IsEnabled"})
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        print("FAIL " + str(error)[:2000], flush=True)
    finally:
        try:
            call("tia_detach")
        except Exception as error:
            report["cleanupFailure"] = str(error)
            report["completed"] = False
        client.close()
        report["hostExitCode"] = client.process.returncode
        report["stderrTail"] = client.errors[-20:]
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
