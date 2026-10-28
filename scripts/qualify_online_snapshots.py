"""Qualify the D09 online engineering rows natively against one owned PLCSIM Advanced instance: plc_online_compare,
db_snapshot_create, db_snapshot_capture, db_snapshot_load_actual and plc_station_upload.

An owned scratch CPU 1511-1 PN gets a global DB, a saved project snapshot review and a memory-card download into a fresh
owned PLCSIM Advanced instance (provisioned credential, powered on, RUN). plc_online_compare must report the downloaded
program identical and an offline edit as a difference. The CPU value of the DB is then written through the simulator;
db_snapshot_create stores it as the DB's offline snapshot, db_snapshot_capture exports and reviews that snapshot, and
db_snapshot_load_actual writes it back after the CPU value was changed again, read back through the simulator. Each change
is prepared with its preview asserted, applied with checkpoint, compile and save, and restored from its checkpoint into a
new directory, which must equal the pre-change program state; the project reopens in a fresh Portal before the restores.
Finally plc_station_upload uploads the station into a new project in an empty owned Portal. Refusals of a missing ticket
change nothing. The simulator instance is released at the end.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import uuid

from block_test_runtime import (save_generated_scratch_project, verify_credential_provisioning, verify_download,
                                verify_memory_card, verify_owned_instance)
from library_native_harness import Harness, require
from qualification_owned_scratch import complete_inventory, snapshot
from qualify_access_changes import Run as StateRun
from qualify_plc_object_mutations import coverage_gaps, diff, view

KINDS = ("db_snapshot_create", "db_snapshot_load_actual")
DB = "OnDb"
SOURCE = '''DATA_BLOCK "OnDb"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
NON_RETAIN
   VAR
      Count : Int := %d;
   END_VAR

BEGIN

END_DATA_BLOCK

'''
SYMBOL = '"OnDb".Count'
CRITERIA = {
    "AC-identity": "Online operations bind the exact ticket-bound owned PLCSIM instance and DB; a missing or foreign ticket is refused before anything goes online.",
    "AC-completeness": "plc_online_compare returns its bounded compare tree with explicit truncation; db_snapshot_capture retains the complete exported snapshot.",
    "AC-preview": "Every change preview is asserted against the request before apply.",
    "AC-preserve": "Each change alters exactly the intended program state; capture, compare and upload leave the source project unchanged.",
    "AC-native": "Native CompareToOnline, InterfaceSnapshot and ValueService calls against an owned PLCSIM Advanced instance, read back through the simulator and a fresh owned Portal reopen.",
    "AC-recovery": "change_restore into a new directory equals the pre-change program state.",
    "AC-evidence": "This report: binaries, tool calls, step results, simulator reads, snapshots with state.json and limitations.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 and one owned PLCSIM Advanced V8 instance on Softbus; no physical PLC, network adapter or TCP/IP mode.",
    "One global DB with one Int; arrays, structures, retain values and instance DBs are not exercised.",
    "change_restore restores the offline project; values written to the simulated CPU are not part of a restore.",
]


class Run(StateRun):
    plc_compiled = KINDS + ("source_upsert", "project_simulation_enable")

    def __init__(self, h):
        super().__init__(h)
        for key in ("certificateIdentity", "notQualified"):
            h.report.pop(key)
        h.report.update(criteria=CRITERIA, excluded={}, online={})
        self.ticket = self.instance = self.symbol = None

    def args(self, kind, payload, **top):
        return {"kind": kind, "payload": payload, "plcPath": self.plc, **top}

    def domains(self):
        return {}

    def state(self):
        return view(complete_inventory(self.h.tool))

    def snap(self, label):
        self.count += 1
        destination = self.h.directory / "snapshots" / ("%03d-%s" % (self.count, re.sub(r"[^A-Za-z0-9]+", "-", label)[:40]))
        taken = {}
        def take():
            taken.update(snapshot(self.h.tool, destination))
            return {"objects": len(taken["objects"])}
        self.h.stage("snapshot " + label, take)
        state = view(taken["inventory"], taken["objects"])
        (destination / "state.json").write_text(json.dumps(state, indent=1), encoding="utf-8")
        return {"view": state}

    def block(self):
        return self.plc + "/blocks/" + DB

    def read_count(self):
        row = self.h.tool("sim_read", {"instanceId": self.instance["id"], "signals": [{"symbol": self.symbol, "type": "int16"}]})["values"][0]
        require(row.get("quality") == "good", "The simulator cannot read %s: %s" % (self.symbol, row))
        return row["value"]

    def write_count(self, value):
        self.h.tool("sim_write", {"instanceId": self.instance["id"], "writes": [{"symbol": self.symbol, "type": "int16", "value": value}]})
        require(self.read_count() == value, "The simulator does not read back %s = %d" % (SYMBOL, value))


def simulate(run):
    """Fresh owned PLCSIM Advanced instance with the scratch program downloaded, a provisioned credential and RUN."""
    h = run.h
    def start():
        row = h.report["online"]
        row["discoveryBaseline"] = h.tool("simulation_discovery_baseline", {})["baseline"]
        created = h.tool("sim_create", {"cpuType": "CPU1511", "creationId": uuid.uuid4().hex})
        run.instance = verify_owned_instance(created["instance"])
        require(run.instance.get("operatingState") == "Off", "A fresh owned OFF instance is required")
        cpus = h.scratch["scratchSecurity"]["cpus"]
        require(len(cpus) == 1, "Exactly one generated scratch credential is required")
        run.ticket = {"downloadTicket": created["downloadTicket"], "credentialMetadataPath": cpus[0]["credentialMetadataPath"]}
        download = h.tool("simulation_download", run.ticket)
        verify_download(download, run.instance)
        row["memoryCard"] = verify_memory_card(download)
        row["generatedProjectSave"] = save_generated_scratch_project(h.tool, download)
        provisioned = h.tool("simulation_provision_scratch_credential", {**run.ticket, "trustOwnedEmptyCpuCertificate": True})
        verify_credential_provisioning(provisioned, download, trust_owned_empty_cpu_certificate=True)
        h.tool("sim_state", {"instanceId": run.instance["id"], "action": "powerOn", "timeoutMs": 60000})
        running = h.tool("sim_state", {"instanceId": run.instance["id"], "action": "run", "timeoutMs": 30000})
        require(running.get("instance", {}).get("operatingState") == "Run", "Simulator did not enter RUN")
        # sim_tags loads the instance tag list; the symbol must resolve to exactly one native tag.
        tags = [t["symbol"] for t in h.tool("sim_tags", {"instanceId": run.instance["id"], "maxItems": 2000})["tags"]]
        found = [t for t in tags if t.replace('"', "") == SYMBOL.replace('"', "")]
        require(len(found) == 1, "%s is missing or ambiguous in the simulator: %s" % (SYMBOL, [t for t in tags if "OnDb" in t]))
        run.symbol = found[0]
        return {"instance": run.instance["id"], "symbol": run.symbol, "count": run.read_count()}
    return h.stage("download into an owned PLCSIM Advanced instance", start)


def compare(run, label, expect_identical):
    h = run.h
    def check():
        before = run.state()
        result = h.tool("plc_online_compare", {"downloadTicket": run.ticket["downloadTicket"], "plcPath": run.plc, "maxItems": 2000})
        require(run.state() == before, "plc_online_compare changed the project")
        text = json.dumps(result)
        differs = DB in text and any(state in text for state in ("Different", "OnlyOffline", "OnlyOnline", "DifferentOnlineOffline"))
        h.report["online"][label] = result
        require(differs is (not expect_identical), "plc_online_compare of %s: expected %s" % (label, "identical" if expect_identical else "a difference for " + DB))
        return {"truncated": result.get("truncated"), "differs": differs}
    h.stage("plc_online_compare " + label, check)
    run.mark("plc_online_compare", "applied", label)


def snapshot_create(run, restore=False):
    payload = {"blockPath": run.block(), "downloadTicket": run.ticket["downloadTicket"]}
    prepared = run.prepare("store the CPU values of %s as its snapshot" % DB, "db_snapshot_create", payload, {})
    run.finish("store the CPU values of %s as its snapshot" % DB, "db_snapshot_create", prepared, changed=["block:" + run.block()], restore=restore)


def capture(run, review):
    h = run.h
    def check():
        before = run.state()
        result = h.tool("db_snapshot_capture", {"reviewId": review, "objectPath": run.block()})
        require(run.state() == before, "db_snapshot_capture changed the project")
        return result
    result = h.stage("db_snapshot_capture " + DB, check)
    h.report["online"]["capture"] = result
    run.mark("db_snapshot_capture", "applied", DB)
    return result


def load_actual(run, record, restore=False):
    payload = {"snapshotRecordId": record, "acknowledgeActualValueWrite": True, "downloadTicket": run.ticket["downloadTicket"]}
    label = "load the snapshot of %s into the CPU" % DB
    prepared = run.prepare(label, "db_snapshot_load_actual", payload, {})
    run.finish(label, "db_snapshot_load_actual", prepared, restore=restore)


def upload(run):
    h = run.h
    h.stage("close the project before the upload", lambda: h.tool("tia_detach"))
    h.owned_portal("upload")
    def check():
        result = h.tool("plc_station_upload", {"downloadTicket": run.ticket["downloadTicket"]})
        require(result.get("project", {}).get("path") and Path(result["project"]["path"]).is_file(), "The upload did not save a new project: %s" % {k: result.get(k) for k in ("project", "completed")})
        blocks = [b["name"] for b in complete_inventory(h.tool)["blocks"]]
        require(DB in blocks, "The uploaded station lacks %s: %s" % (DB, blocks))
        return {"project": result["project"], "blocks": sorted(blocks), "messages": len(result.get("messages", []))}
    h.report["online"]["upload"] = h.stage("plc_station_upload", check)
    run.mark("plc_station_upload", "applied", "upload")


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("online snapshots")
    run.project = h.tool("tia_session_status")["projects"][0]["path"]
    run.current = run.snap("baseline")
    prepared = run.prepare("create " + DB, "source_upsert", {"language": "SCL", "source": SOURCE % 5, "expectedBlockNames": [DB]}, {})
    run.finish("create " + DB, "source_upsert", prepared, added=["block:" + run.block()])
    # The memory-card download requires simulation support, which only an explicit reviewed change enables.
    prepared = run.prepare("enable simulation support", "project_simulation_enable", {"enabled": True}, {})
    run.finish("enable simulation support", "project_simulation_enable", prepared)
    review = h.stage("project_snapshot_capture", lambda: h.tool("project_snapshot_capture", {"request": {"plcPath": run.plc, "blockPaths": [run.block()]}}))
    review_id = review.get("reviewId") or review.get("review", {}).get("reviewId")
    require(review_id, "project_snapshot_capture returned no reviewId: %s" % list(review))
    try:
        simulate(run)
        run.refuse("db_snapshot_create", "no download ticket", {"blockPath": run.block()}, "PhysicalPlcRefused")
        def refuse_compare():
            error = h.call_error("plc_online_compare", {"downloadTicket": uuid.uuid4().hex, "plcPath": run.plc})
            require(error.get("code"), "Expected a typed refusal of an unknown ticket: %s" % error)
            return error
        h.stage("refuse plc_online_compare: an unknown ticket", refuse_compare)
        run.mark("plc_online_compare", "refused", "unknown ticket")
        compare(run, "after the download", True)
        run.write_count(42)
        snapshot_create(run)
        record = capture(run, review_id)
        record_id = record.get("recordId") or record.get("record", {}).get("recordId")
        require(record_id and "42" in json.dumps(record), "The captured snapshot does not hold the CPU value 42: %s" % json.dumps(record)[:400])
        run.refuse("db_snapshot_load_actual", "no download ticket", {"snapshotRecordId": record_id, "acknowledgeActualValueWrite": True}, "PhysicalPlcRefused")
        run.write_count(7)
        load_actual(run, record_id)
        h.stage("the CPU reads back the loaded snapshot", lambda: require(run.read_count() == 42, "The CPU reads %s after the load" % run.read_count()) or 42)
        prepared = run.prepare("change %s offline" % DB, "source_upsert", {"language": "SCL", "source": SOURCE % 6, "expectedBlockNames": [DB]}, {})
        run.finish("change %s offline" % DB, "source_upsert", prepared, changed=["block:" + run.block()])
        compare(run, "after an offline edit", False)
        h.stage("close the changed session", lambda: h.tool("tia_detach"))
        h.owned_portal("reopen")
        h.stage("reopen the saved project", lambda: h.reopen_project(run.project))
        final = run.current
        run.current = run.b0 = run.snap("reopened")
        h.stage("reopened state equals every verified change", lambda: require(run.b0["view"] == final["view"], "Reopened state differs: %s" % diff(final["view"], run.b0["view"])) or 1)
        for kind in KINDS:
            run.mark(kind, "persisted", "fresh Portal reopen")
        run.write_count(43)
        snapshot_create(run, restore=True)
        again = capture(run, review_id)
        run.write_count(9)
        load_actual(run, again.get("recordId") or again.get("record", {}).get("recordId"), restore=True)
        upload(run)
    finally:
        if run.instance is not None:
            h.report["online"]["release"] = h.tool("sim_release", {"instanceId": run.instance["id"], "deleteOwned": True})
    gaps = coverage_gaps(run.coverage, KINDS)
    require(not gaps, "Kinds without applied, persisted, refused and restored evidence: %s" % gaps)


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
    Harness(args, "on_", "D09 online engineering against an owned PLCSIM Advanced instance", LIMITATIONS, []).run(body)
