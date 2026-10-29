"""Native acceptance of the TIA Add-In companion (addin_companion); needs an operator at the desktop.

Before the run the operator installs the published package (scripts/Publish-AddIn.ps1) in TIA Portal V21, grants its
declared permissions, sets the user environment variable TIAMCP_ARTIFACTS to this run's --artifacts directory, starts TIA
and opens a project. The runner then starts the Host with --addin-handoff --addin-enrollment, attaches the worker to that
TIA process, saves a review with project_snapshot_capture and waits for the operator to choose
"Send selection to TIA MCP" on one to 64 project-tree objects. The completed handoff's record is read with
addin_selection_page and checked: producer hashes equal the enrollment, the binding names this worker session, and every
reference resolves in the saved review. Nothing in the project is changed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import time

from library_native_harness import require
from smoke_mcp import Client

SELECT_TIMEOUT = 900  # Seconds the operator has to send a selection.


def completed_handoffs(artifacts):
    return sorted(p.parent for p in (artifacts / "addin-handoffs").glob("*/completed.json"))


def wait_for_handoff(artifacts, before):
    deadline = time.monotonic() + SELECT_TIMEOUT
    while time.monotonic() < deadline:
        new = [p for p in completed_handoffs(artifacts) if p not in before]
        if new:
            return new[0]
        time.sleep(2)
    raise AssertionError("No Add-In selection arrived within %d s" % SELECT_TIMEOUT)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True, help="The Host artifacts directory; TIAMCP_ARTIFACTS must name it.")
    parser.add_argument("--enrollment", type=Path, required=True, help="addin-enrollment.json from scripts/Publish-AddIn.ps1")
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    args = parser.parse_args()
    require(not args.report.exists(), "Choose a new --report path")
    artifacts = args.artifacts.resolve()
    enrollment = json.loads(args.enrollment.read_text(encoding="utf-8"))
    report = {"schemaVersion": 1, "completed": False, "steps": [], "enrollment": enrollment,
              "runner": {"path": str(Path(__file__).resolve()), "sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()},
              **{name: {"path": str(p.resolve()), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for name, p in (("host", args.host), ("worker", args.worker))}}
    client = Client(args.host.resolve(), args.timeout, ["--worker", str(args.worker.resolve()), "--artifacts", str(artifacts),
                                                       "--addin-handoff", "--addin-enrollment", str(args.enrollment.resolve())])
    def step(name, result):
        report["steps"].append({"name": name, "result": result})
        print("PASS " + name, flush=True)
        return result
    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "addin-companion-qualification", "version": "1"}})
        client.notify("notifications/initialized")
        discovery = artifacts / "addin-handoffs" / "receiver.json"
        deadline = time.monotonic() + 30  # The Host publishes its receiver asynchronously after startup.
        while not discovery.is_file() and time.monotonic() < deadline:
            time.sleep(0.5)
        step("receiver published", json.loads(discovery.read_text(encoding="utf-8")))
        running = [p for p in client.tool("tia_discover")["processes"] if p.get("projectPath") and str(p.get("mode")) == "WithUserInterface"]
        require(len(running) == 1, "Expected exactly one running TIA with its UI and an open project: %s" % running)
        step("attach the operator's TIA", client.tool("tia_attach", {"processId": running[0]["processId"]}))
        review = step("save a review", client.tool("project_snapshot_capture", {"request": {"maxBlocks": 50}}))
        before = completed_handoffs(artifacts)
        print("Select 1 to 64 objects in the TIA project tree and choose TIA MCP > Send selection to TIA MCP.", flush=True)
        folder = step("operator sent a selection", str(wait_for_handoff(artifacts, before)))
        receipt = json.loads((Path(folder) / "receipt.json").read_text(encoding="utf-8"))
        record = receipt.get("selectionRecordId") or receipt.get("SelectionRecordId")
        summary = step("selection summary", client.tool("addin_selection_page", {"recordId": record, "section": "summary"}))
        rows = step("selections", client.tool("addin_selection_page", {"recordId": record, "section": "selections", "count": 64}))
        text = json.dumps(summary)
        require(enrollment["assemblySha256"] in text and enrollment["configurationSha256"] in text, "The record does not carry the enrolled producer hashes")
        require(review["reviewId"] in text, "The record is not bound to the saved review")
        report["selectionRecordId"], report["completed"] = record, True
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        try:
            client.tool("tia_detach")
        except Exception as error:
            report["detachFailure"] = str(error)
        client.close()
        report["hostExitCode"], report["toolCalls"], report["hostLogs"] = client.process.returncode, client.tool_calls, client.errors
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print("Report: " + str(args.report), flush=True)


if __name__ == "__main__":
    main()
