"""Reconcile one failed credential-provisioning admission in a fresh owned TIA session.

The Host releases the reservation only if the project matches its pre-provision
archive, the card manifest matches and any discovery observer project is empty.
"""
import argparse
import json
import pathlib

from smoke_mcp import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--admission-id", required=True)
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=pathlib.Path)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    args = parser.parse_args()
    client = Client(args.host.resolve(), 600, ["--worker", str(args.worker.resolve())] if args.worker else [])
    report = {"admissionId": args.admission_id, "steps": [], "completed": False}
    started = False
    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "provision-reconcile", "version": "1"}})
        client.notify("notifications/initialized")
        report["steps"].append({"name": "tia_start", "result": client.tool("tia_start")})
        started = True
        result = client.tool("simulation_provision_reconcile", {"admissionId": args.admission_id})
        report["steps"].append({"name": "simulation_provision_reconcile", "result": result})
        report["released"] = result["released"]
        # A held reservation is a correct outcome, not a run failure.
        report["completed"] = True
        print(("RELEASED " if result["released"] else "HELD ") + json.dumps(result.get("reasons", [])), flush=True)
    except Exception as error:
        report["failure"] = str(error)
        print("FAIL " + str(error)[:2000], flush=True)
    finally:
        if started:
            try: client.tool("tia_detach")
            except Exception as error: report["detachFailure"] = str(error); report["completed"] = False
        client.close()
        report["hostExitCode"] = client.process.returncode
        report["toolCalls"] = client.tool_calls
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
