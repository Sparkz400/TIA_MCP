"""Kill the worker during real library transactions, then reconcile from disk in a fresh host, refuse replay and recover every resource."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import shutil
import threading
import time

import psutil

from library_native_harness import Harness, require
from smoke_mcp import Client

ROOT = Path(__file__).resolve().parents[1]


def restart_host(h):
    """A fresh host process: nothing survives but the durable journals under the same artifact root."""
    calls = h.client.tool_calls
    h.client.close()
    h.report.setdefault("hostExitCodes", []).append(h.client.process.returncode)
    h.client = Client(h.args.host.resolve(), h.args.timeout, ["--worker", str(h.args.worker.resolve()),
                      "--artifacts", str(h.directory), "--sim-worker", str(h.directory / "sim-not-used.exe")])
    h.client.tool_calls = calls
    h.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                     "clientInfo": {"name": "native-library-interruption", "version": "1"}})
    h.client.notify("notifications/initialized")


def owned_child(h, executable):
    matches = [p for p in psutil.Process(h.client.process.pid).children(recursive=True)
               if Path(p.exe()).resolve() == executable.resolve()]
    require(len(matches) == 1, "Expected exactly one worker child of this host")
    return matches[0]


def last_event(journal):
    try:
        events = json.loads(journal.read_text(encoding="utf-8")).get("events", [])
    except (OSError, ValueError):
        return None  # Mid-replacement; poll again.
    return events[-1] if events else None


def interrupted_apply(h, kind, payload, stage):
    prepared = h.prepare(kind, payload)
    journal = h.directory / "changes" / ("change-" + prepared["changeId"]) / "journal.json"
    portal = psutil.Process(h.tool("tia_session_status")["processId"])
    worker = owned_child(h, h.args.worker)
    dispatch = {"kind": kind, "changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                "state": "dispatching", "interruptAt": stage}
    h.report["mutations"].append(dispatch)
    h.persist()
    response = {}

    def call():
        try:
            response["result"] = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
        except Exception as error:  # A dead worker may also end the host.
            response["exception"] = str(error)

    def kill():
        thread = threading.Thread(target=call, daemon=True)
        thread.start()
        deadline = time.monotonic() + h.args.timeout
        while time.monotonic() < deadline and thread.is_alive():
            event = last_event(journal)
            if event and event.get("stage") == stage and event.get("state") == "dispatching":
                worker.kill()
                dispatch["killedAtEvent"] = event
                break
            time.sleep(0.02)
        require("killedAtEvent" in dispatch, "The transaction ended before stage %s dispatched: %s" % (stage, response))
        thread.join(120)
        require(not thread.is_alive(), "change_apply did not return after the worker was killed")
        require(response.get("exception") or response["result"].get("isError") is True, "Interrupted apply reported success")
        require("Portal" in portal.name(), "Owned portal process identity changed")
        portal.kill()
        portal.wait(60)
        dispatch.update(state="interrupted", response=response, portalKilled=portal.pid)
        return {"killedAtEvent": dispatch["killedAtEvent"], "response": response}
    h.stage("kill worker during %s %s" % (kind, stage), kill)
    return prepared


def reconcile(h, prepared):
    restart_host(h)

    def status():
        result = h.tool("change_status", {"changeId": prepared["changeId"]})
        require(result.get("recoveredFromDisk") is True and result["state"] == "outcome_unknown"
                and result.get("automaticRetryAllowed") is False, "Fresh host did not reconcile an unknown outcome from disk")
        return result
    h.stage("fresh host reconciles unknown outcome", status)

    def no_replay():
        error = h.call_error("change_apply", h.apply_args(prepared))
        # Shared admission refuses first (ResourceBusy); the library journal refuses otherwise (OutcomeUnknown).
        require(error.get("code") in ("ResourceBusy", "OutcomeUnknown"), "Replay was not refused: %s" % error)
        return error
    h.stage("fresh host refuses replay", no_replay)


def reconcile_and_recover(h, prepared, verify):
    reconcile(h, prepared)
    h.owned_portal("recovery " + prepared["changeId"][-6:])
    h.reconcile_admission("reconcile the interrupted admission in a fresh session")
    restored = h.stage("restore every checkpointed resource", lambda: h.tool("change_restore", {
        "changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
    require(restored["state"] == "restored_to_new_locations" and restored.get("originalsPreserved") is True, "Restore incomplete")
    require(all(r.get("state") == "completed" and r.get("retrieved") is True for r in restored["resources"]), "A resource was not retrieved")
    h.stage("verify recovered pre-change state", lambda: verify(restored["resources"]))
    again = h.stage("repeat restore is read back, not replayed", lambda: h.tool("change_restore", {
        "changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
    require(again.get("idempotentReadback") is True, "Repeated restore was not an idempotent readback")
    h.stage("close recovery session", lambda: h.tool("tia_detach"))
    return restored


def body(h):
    documents = h.directory / "documents"
    shutil.copytree(ROOT / "fixtures/libraries/type-lifecycle", documents)

    # Multi-resource: a project checkpoint plus a writable global library, killed while compiling.
    plc = h.owned_scratch("multi-resource")
    blocks = {b["name"]: b["path"] for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"] if b["plcPath"] == plc}
    library = h.apply("library_create", {"schemaVersion": 1, "name": "InterruptQualification"})["journal"]["result"]["library"]["libraryId"]
    h.apply("library_create_master_copy", {"schemaVersion": 1, "destinationLibraryId": library, "target": {"plcPath": plc},
            "object": {"kind": "block", "objectPath": blocks["TiaMcp_IsEnabled"]}})
    first = interrupted_apply(h, "library_create_master_copy", {"schemaVersion": 1, "destinationLibraryId": library,
                              "target": {"plcPath": plc}, "object": {"kind": "block", "objectPath": blocks["TiaMcp_EnableGate"]}}, "compile")

    def verify_multi(resources):
        require(sorted(r["kind"] for r in resources) == ["library", "project"], "Expected one project and one library resource")
        recovered = next(r for r in resources if r["kind"] == "library")["library"]["libraryId"]
        names = [x["name"] for x in h.query(recovered)["items"] if x.get("kind") == "masterCopy"]
        require(names == ["TiaMcp_IsEnabled"], "Recovered library is not the pre-change state: %s" % names)
        inventory = {b["name"] for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"]}
        require({"Main", "TiaMcp_IsEnabled", "TiaMcp_EnableGate"} <= inventory, "Recovered project lacks its blocks")
        return {"masterCopies": names, "blocks": sorted(inventory)}
    reconcile_and_recover(h, first, verify_multi)

    # Project-only: type authoring killed while archiving its checkpoint, before any native mutation.
    plc = h.owned_scratch("checkpoint")
    scratch_path = h.tool("tia_session_status")["projects"][0]["path"]
    project_library = h.project_library()
    second = interrupted_apply(h, "library_create_type", {"schemaVersion": 1, "destinationLibraryId": project_library,
                               "documentDirectory": str(documents), "documentName": "TiaMcpLibraryState", "targetEnvironment": "selectedPlc",
                               "destinationComposition": "types", "target": {"plcPath": plc}}, "checkpoint_project_archive")

    reconcile(h, second)

    def no_checkpoint():
        error = h.call_error("change_restore", {"changeId": second["changeId"], "previewHash": second["previewHash"]})
        require(error.get("code") == "CheckpointInvalid", "Restore without a checkpoint was not refused: %s" % error)
        journal = error.get("details") or h.tool("change_status", {"changeId": second["changeId"]})["journal"]
        stages = [e["stage"] for e in journal["events"]]
        require("native_mutation" not in stages and not journal["checkpoints"], "Journal does not prove the mutation was never dispatched")
        return {"error": error, "stages": stages}
    h.owned_portal("checkpoint recovery")
    h.reconcile_admission("reconcile the interrupted admission in a fresh session")
    h.stage("refuse restore without a completed checkpoint", no_checkpoint)

    def original_unchanged():
        h.tool("project_open", {"path": scratch_path})
        types = [t["name"] for t in h.tool("project_inventory", {"maxItems": 1000}).get("types", [])]
        require("TiaMcpLibraryState" not in types, "The saved original contains the never-dispatched type")
        library_types = [x["name"] for x in h.query(h.project_library())["items"] if x.get("kind") == "type"]
        require(not library_types, "The saved original project library is not empty: %s" % library_types)
        return {"plcTypes": types, "libraryTypes": library_types}
    h.stage("reopen the saved original after the killed portal", original_unchanged)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "iq_", "V21 library transaction interruption: worker kill, fresh-host reconciliation, no replay, multi-resource recovery", [
        "Two interruption points: compile after a multi-resource mutation, and checkpoint archive before any mutation (no checkpoint exists, so the saved original is reopened). Other stages are not exercised.",
        "After the compile-stage kill the original project and library are left for inspection; only recovered copies are verified.",
        "Host-side shared admission behaviour is observed, not changed (lane E owns it)."], []).run(body)
