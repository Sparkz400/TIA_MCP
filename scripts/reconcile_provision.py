"""Reconcile one failed credential-provisioning admission in a fresh owned TIA session.

The Host releases the reservation only if the project matches its pre-provision archive and
was not saved after it, the card manifest and the rest of the storage match their pre-attempt
evidence, the write-ahead stream never armed the credential, and any discovery observer project
is absent or empty. Each Host lifetime is tracked exactly (ExactClient, no termination); a
second fresh Portal then reopens the original project, which proves the first released its lock.
No simulator tool is called.
"""
import argparse
import json
import pathlib

from exact_qualification_client import ExactClient
from smoke_mcp import ROOT


def initialized(host, worker, artifacts):
    client = ExactClient(host, 600, ["--worker", str(worker), "--artifacts", str(artifacts)])
    client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "provision-reconcile", "version": "2"}})
    client.notify("notifications/initialized")
    return client


def lifetime(report, name, host, worker, artifacts, body):
    """One Host lifetime: tia_start, the body, tia_detach, then the exact process-tree proof."""
    row = report["lifetimes"][name] = {"steps": []}
    client = initialized(host, worker, artifacts)
    try:
        row["steps"].append({"name": "tia_start", "result": client.tool("tia_start")})
        body(client, row)
        row["steps"].append({"name": "tia_detach", "result": client.tool("tia_detach")})
    except Exception as error:
        row["failure"] = str(error)
    finally:
        closed = client.close()
        row["toolCalls"] = client.tool_calls
        row["close"] = {key: value for key, value in closed.items() if key != "tree"}
        row["processTree"] = closed.get("tree")
        row["completed"] = "failure" not in row and closed.get("hostExitCode") == 0 and (closed.get("tree") or {}).get("completed") is True
    return row


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--admission-id", required=True)
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=pathlib.Path, required=True)
    parser.add_argument("--artifacts", type=pathlib.Path, required=True, help="The Host artifact root that holds the admission.")
    parser.add_argument("--report", type=pathlib.Path, required=True)
    args = parser.parse_args()
    host, worker, artifacts = args.host.resolve(), args.worker.resolve(), args.artifacts.resolve()
    record = json.loads((artifacts / "engineering-admissions" / args.admission_id / "00000000.json").read_text(encoding="utf-8-sig"))
    project = next(r["path"] for r in record["scope"]["resources"] if r["effect"] == "simulationCompilationOrCredentialPreparation")
    report = {"admissionId": args.admission_id, "project": project, "lifetimes": {}, "completed": False}

    def reconcile(client, row):
        result = client.tool("simulation_provision_reconcile", {"admissionId": args.admission_id})
        row["steps"].append({"name": "simulation_provision_reconcile", "result": result})
        report["released"], report["reasons"] = result["released"], result.get("reasons", [])
        print(("RELEASED " if result["released"] else "HELD ") + json.dumps(report["reasons"]), flush=True)

    def reopen(client, row):
        row["steps"].append({"name": "project_open", "result": client.tool("project_open", {"path": project})})

    try:
        first = lifetime(report, "reconcile", host, worker, artifacts, reconcile)
        # Only after the reconciling Portal's exact exit: a fresh Portal opens the original project only if its lock was released.
        second = lifetime(report, "lockRelease", host, worker, artifacts, reopen) if first["completed"] else None
        report["projectLockReleaseVerified"] = bool(second and second["completed"])
        report["simulatorToolsCalled"] = sorted({c["name"] for row in report["lifetimes"].values() for c in row["toolCalls"] if c["name"].startswith("sim_")})
        report["toolCalls"] = [c for row in report["lifetimes"].values() for c in row["toolCalls"]]
        # A held reservation is a correct outcome, not a run failure.
        report["completed"] = "released" in report and report["projectLockReleaseVerified"] and not report["simulatorToolsCalled"]
    finally:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print("COMPLETED" if report["completed"] else "INCOMPLETE", flush=True)
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
