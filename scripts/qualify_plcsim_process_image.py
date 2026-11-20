"""Qualify SingleStep_P and TimespanSynchronized_P on an owned PLCSIM Advanced V8 instance whose program has a process image partition.

NOT YET RUN natively. The fixture is fixtures/plcsim-process-image: an owned scratch CPU 1511-1 PN with OB1 and a cyclic
interrupt OB30 assigned to process image partition 1, each counting its starts in PipDb. The program is downloaded to a fresh
owned instance, provisioned and started like the D09 runner. Each partition mode (and the C and CP modes as controls) captures
both counters for 2 s; a mode passes when the capture ends durationLimit, the counter of OB30 differs between successive rows
(decoded independently from the retained segments) and the previous operating mode is back. The instance is released.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import time
import uuid

from block_test_runtime import save_generated_scratch_project, verify_credential_provisioning, verify_download, verify_memory_card, verify_owned_instance
from library_native_harness import Harness, require

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = ROOT / "fixtures" / "plcsim-process-image"
MODES = ("SingleStep_P", "TimespanSynchronized_P", "SingleStep_C", "SingleStep_CP", "TimespanSynchronized_C", "TimespanSynchronized_CP")
SPAN_MS = 10
LIMITATIONS = [
    "One owned PLCSIM Advanced V8 CPU 1511 instance on Softbus; OB30 is assigned to process image partition 1, no I/O module is, and no isochronous OB exists.",
    "SingleStep_Bus and the I/O module assignment of a partition are not exercised; the capture rows are read one signal after the other, not from one scan.",
]


def plant(h, plc, definition):
    for step in definition["steps"]:
        if step["kind"] == "block_attributes_set":
            h.apply("block_attributes_set", {"blockPath": plc + "/blocks/" + step["blockName"], "attributes": step["attributes"]})
            continue
        text = (FIXTURE / step["file"]).read_text(encoding="utf-8")
        if step["kind"] == "block_import_xml":
            h.apply("block_import_xml", {"scopePath": plc, "xml": text, "expectedBlockNames": step["blocks"]})
        else:
            h.apply("source_upsert", {"scopePath": plc, "language": step["language"], "source": text, "expectedBlockNames": step["blocks"]})


def series(directory, capture_id, selection):
    """The values of one selection in capture order, decoded from the retained segment files."""
    values = []
    for segment in sorted((directory / "runtime-captures" / capture_id).glob("*.segment")):
        for row in json.loads(segment.read_text(encoding="utf-8"))["rows"]:
            if row["kind"] == "read" and row["selectionId"] == selection and not row["errorCode"]:
                result = json.loads(row["readResultJson"])
                if result.get("status") == "read":
                    values.append(result["value"])
    return values


def body(h):
    definition = json.loads((FIXTURE / "fixture.json").read_text(encoding="utf-8"))
    plc = h.owned_scratch("plcsim process image")
    cpus = h.scratch["scratchSecurity"]["cpus"]
    require(len(cpus) == 1, "Exactly one generated scratch credential is required")
    h.apply("project_simulation_enable", {"enabled": True})
    plant(h, plc, definition)
    h.stage("discovery baseline", lambda: h.tool("simulation_discovery_baseline", {})["baseline"])
    creation = uuid.uuid4().hex
    created = h.stage("sim_create", lambda: h.tool("sim_create", {"cpuType": "CPU1511", "creationId": creation}))
    instance = verify_owned_instance(created["instance"])
    try:
        ticket = {"downloadTicket": created["downloadTicket"], "credentialMetadataPath": cpus[0]["credentialMetadataPath"]}
        download = h.stage("simulation_download", lambda: h.tool("simulation_download", ticket))
        verify_download(download, instance)
        h.report["memoryCard"] = verify_memory_card(download)
        h.report["generatedProjectSave"] = save_generated_scratch_project(h.tool, download)
        provisioned = h.stage("simulation_provision_scratch_credential",
                              lambda: h.tool("simulation_provision_scratch_credential", {**ticket, "trustOwnedEmptyCpuCertificate": True}))
        h.report["credentialProvisioning"] = verify_credential_provisioning(provisioned, download, trust_owned_empty_cpu_certificate=True)
        h.stage("power on", lambda: h.tool("sim_state", {"instanceId": instance["id"], "action": "powerOn", "timeoutMs": 60000}))
        running = h.stage("run", lambda: h.tool("sim_state", {"instanceId": instance["id"], "action": "run", "timeoutMs": 30000}))
        require(running["instance"]["operatingState"] == "Run", "The instance did not enter RUN")

        tags = h.tool("sim_tags", {"instanceId": instance["id"], "filter": "PipDb", "maxItems": 2000})["tags"]
        signals = []
        for s in definition["signals"]:
            found = [t["symbol"] for t in tags if t["symbol"].replace('"', "") == "PipDb." + s["member"]]
            require(len(found) == 1, "PLCSIM symbol PipDb.%s is missing or ambiguous: %s" % (s["member"], [t["symbol"] for t in tags]))
            signals.append({"symbol": found[0], "type": s["type"], "datatype": s["datatype"]})

        def mode_of():
            return h.tool("sim_observe", {"instanceId": instance["id"]})["after"]["operatingMode"]
        initial = h.stage("operating mode before the captures", mode_of)
        h.report["captureModes"] = {"initialOperatingMode": initial}
        for mode in MODES:
            selections = [uuid.uuid4().hex for _ in signals]
            capture_id = uuid.uuid4().hex
            span = {"spanMilliseconds": SPAN_MS} if mode.startswith("TimespanSynchronized_") else {}
            request = {"attemptId": creation, "selectionIds": selections, "intervalMilliseconds": 100, "durationSeconds": 2, "source": "plcsim",
                       "simulator": {"instanceId": instance["id"], "instanceName": instance["name"], "cycleSynchronous": True, "syncMode": mode, **span,
                                     "signals": signals}}

            def capture():
                status = h.tool("runtime_capture_start", {"captureId": capture_id, "request": request})
                deadline = time.monotonic() + 62
                while status["state"] == "running" and time.monotonic() < deadline:
                    time.sleep(0.5)
                    status = h.tool("runtime_capture_status", {"captureId": capture_id})
                if status["state"] == "running":
                    status = h.tool("runtime_capture_stop", {"captureId": capture_id})
                return status
            status = h.stage("capture " + mode, capture)
            progress = status["recordedProgress"]
            ob30 = series(h.directory, capture_id, selections[1])
            changes = sum(1 for a, b in zip(ob30, ob30[1:]) if a != b)
            restored = h.stage("operating mode after the %s capture" % mode, mode_of)
            # Recorded before the check, so a mode that stalls still leaves its reason in the report.
            h.report["captureModes"][mode] = {"rows": progress["rows"], "terminalReason": progress["terminalReason"], "ob30Values": len(ob30),
                                              "ob30Changes": changes, "restoredOperatingMode": restored}
            require(progress["terminalReason"] == "durationLimit" and not status["localFailure"] and changes > 0 and restored == initial,
                    "%s did not step on OB30: %s" % (mode, h.report["captureModes"][mode]))
    finally:
        released = h.stage("sim_release", lambda: h.tool("sim_release", {"instanceId": instance["id"], "deleteOwned": True}))
        require(released.get("unregistered") is True, "The owned instance was not released")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--sim-worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "pi_", "SingleStep_P and TimespanSynchronized_P captures on a program with a process image partition", LIMITATIONS, []).run(body)
