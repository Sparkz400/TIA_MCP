"""Test native PLCSIM startup on one fresh empty owned virtual CPU, without TIA."""
import argparse
import json

from smoke_mcp import Client, ROOT
from validate_simulation import artifact_path, redact


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", default=ROOT / "artifacts/simulator-startup-validation.json", help="JSON report path inside this repository's artifacts directory.")
    args = parser.parse_args()
    report_path = artifact_path(args.report, ".json")
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report = {"scope": "Fresh empty adapter-owned local Softbus CPU; no TIA, project download or trial activation.", "steps": [], "completed": False}
    client = Client(ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe", 120)
    instance_id = None

    def call(name, arguments=None):
        raw = client.request("tools/call", {"name": name, "arguments": arguments or {}})
        result = raw.get("structuredContent") or json.loads(raw["content"][0]["text"])
        report["steps"].append({"name": name, "success": not bool(raw.get("isError")), "result": redact(result)})
        print(("FAIL " if raw.get("isError") else "PASS ") + name, flush=True)
        return result, not bool(raw.get("isError"))

    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "tia-simulator-startup-validation", "version": "0.2.0"}})
        client.notify("notifications/initialized")
        call("sim_status")
        created, ok = call("sim_create", {"cpuType": "CPU1511"})
        if not ok:
            return 1
        instance_id = created["instance"]["id"]
        _, ok = call("sim_state", {"instanceId": instance_id, "action": "powerOn", "timeoutMs": 60000})
        report["completed"] = ok
        call("sim_status")
    finally:
        if instance_id is not None:
            _, released = call("sim_release", {"instanceId": instance_id, "deleteOwned": True})
            report["cleanupSucceeded"] = released
            report["completed"] = report["completed"] and released
        client.close()
        report["hostExitCode"] = client.process.returncode
        report_path.write_text(json.dumps(redact(report), indent=2), encoding="utf-8")
        print(str(report_path), flush=True)
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
