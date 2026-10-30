"""Qualify watch_table_monitor (D02 live monitoring) on one owned PLCSIM Advanced V8 instance on Softbus.

The fixture is an owned scratch CPU 1511-1 PN with simulation support whose Main calls "TiaMcp_EnableGate_DB"(Enable := TRUE).
The qualification-only watch fixture seed imports MilestoneWatch with two symbolic rows, "TiaMcp_EnableGate_DB".Active and
"TiaMcp_EnableGate_DB".Enable. The program is downloaded to a fresh owned instance and run. An independent oracle resolves both
members in the instance's tag list (sim_tags) and reads them (sim_read); watch_table_monitor must resolve the same symbols and
report the same values in every sample, page the rows, monitor the PLC's force table, leave the offline table unchanged and keep
working in STOP. Typed refusals cover bad bounds, a foreign creationId and instance name, an unknown table and a released
instance. Nothing is written, modified or forced. The instance is stopped, switched off and released.
"""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import uuid

from block_test_runtime import save_generated_scratch_project, verify_credential_provisioning, verify_download, verify_owned_instance
from library_native_harness import Harness, require

SEED = Path(__file__).resolve().parents[1] / "fixtures/watch-tables/native-owned-v21/MilestoneWatch.before.xml"
MEMBERS = ("Active", "Enable")
CRITERIA = {"watch_table_monitor": {
    "AC-identity": "A foreign creationId refuses as SimulationOwnershipUnverified, a foreign instance name as SimulationTargetMismatch and a released "
                   "instance as SimulationNotSelected; an unknown table refuses as WatchTableNotFound and bad bounds as InvalidRequest; none reads a value.",
    "AC-completeness": "Every row of the page is reported with its stored fields and resolution; paging reports total and nextOffset; the force table is monitored too.",
    "AC-runtime": "Both rows resolve to the tag-list symbols an independent sim_tags lookup finds, and every sample equals an independent sim_read, in RUN and STOP.",
    "AC-evidence": "This report."}}
LIMITATIONS = [
    "One owned PLCSIM Advanced V8 CPU 1511 instance on Softbus; one watch table with two Bool rows of one instance DB, seeded through the qualification-only "
    "fixture seed on a Host started with --component-qualification --watch-qualification none. The PLC's force table has no rows, so no force-table value is read.",
    "Values are sequential PLCSIM API reads per sample, not scan-consistent; both members are TRUE throughout because Main assigns Enable := TRUE every cycle.",
    "Absolute-address rows, comment rows, unsupported types and ambiguous symbols are covered by the offline checks only.",
]


def seed_document(directory):
    """MilestoneWatch with a second row for the Enable member (fresh IDs 6..A)."""
    text = SEED.read_text(encoding="utf-8-sig")
    end = "</SW.WatchAndForceTables.PlcWatchTableEntry>"
    first = text[text.index("<SW.WatchAndForceTables.PlcWatchTableEntry"):text.index(end) + len(end)]
    second = (first.replace('ID="1"', 'ID="6"').replace('ID="2"', 'ID="7"').replace('ID="3"', 'ID="8"')
              .replace('ID="4"', 'ID="9"').replace('ID="5"', 'ID="A"').replace('".Active<', '".Enable<'))
    require(second.count('".Enable<') == 1, "The seed row template changed")
    path = directory / "seed.xml"
    path.write_text(text.replace(first, first + "\n      " + second), encoding="utf-8")
    return path


def body(h):
    plc = h.owned_scratch("watch monitor")
    h.apply("project_simulation_enable", {"enabled": True})
    seed = seed_document(h.directory)
    seeded = h.stage("qualification_watch_fixture_seed", lambda: h.tool("qualification_watch_fixture_seed",
                     {"inputPath": str(seed), "inputSha256": hashlib.sha256(seed.read_bytes()).hexdigest()}))
    table = seeded["target"]
    require(table["name"] == "MilestoneWatch" and [r["name"] for r in table["rows"]] == ['"TiaMcp_EnableGate_DB".%s' % m for m in MEMBERS],
            "The seeded table differs: %s" % table["rows"])
    forces = [t for t in seeded["tables"] if t["kind"] == "force"]
    require(len(forces) == 1, "Exactly one force table is expected")
    h.report.update(criteria=CRITERIA, seededTable={"semanticSha256": table["semanticSha256"], "rows": table["rows"]},
                    forceTable={"name": forces[0]["name"], "group": forces[0]["group"], "rows": len(forces[0]["rows"])})

    def refuse(label, arguments, codes):
        def check():
            error = h.call_error("watch_table_monitor", arguments)
            require(error.get("code") in codes, "Expected %s, observed %s: %s" % (codes, error.get("code"), error.get("message")))
            return error["code"]
        return h.stage("refuse watch_table_monitor: " + label, check)

    creation = uuid.uuid4().hex
    created = h.stage("sim_create", lambda: h.tool("sim_create", {"cpuType": "CPU1511", "creationId": creation}))
    instance = verify_owned_instance(created["instance"])
    monitor = {"tableName": "MilestoneWatch", "plcPath": plc, "instanceId": instance["id"], "instanceName": instance["name"], "creationId": creation}
    released = False
    try:
        require(instance.get("operatingState") == "Off", "A fresh owned OFF instance is required")
        cpus = h.scratch["scratchSecurity"]["cpus"]
        require(len(cpus) == 1, "Exactly one generated scratch credential is required")
        ticket = {"downloadTicket": created["downloadTicket"], "credentialMetadataPath": cpus[0]["credentialMetadataPath"]}
        download = h.stage("simulation_download", lambda: h.tool("simulation_download", ticket))
        verify_download(download, instance)
        h.report["generatedProjectSave"] = save_generated_scratch_project(h.tool, download)
        # The scratch CPU's card is configuration-protected; power-on needs its credential provisioned first.
        provisioned = h.stage("simulation_provision_scratch_credential",
                              lambda: h.tool("simulation_provision_scratch_credential", {**ticket, "trustOwnedEmptyCpuCertificate": True}))
        h.report["credentialProvisioning"] = verify_credential_provisioning(provisioned, download, trust_owned_empty_cpu_certificate=True)
        h.stage("power on", lambda: h.tool("sim_state", {"instanceId": instance["id"], "action": "powerOn", "timeoutMs": 60000}))
        running = h.stage("run", lambda: h.tool("sim_state", {"instanceId": instance["id"], "action": "run", "timeoutMs": 30000}))
        require(running["instance"]["operatingState"] == "Run", "The instance did not enter RUN")

        def oracle():
            tags = h.tool("sim_tags", {"instanceId": instance["id"], "filter": "TiaMcp_EnableGate_DB", "maxItems": 2000})["tags"]
            symbols = {}
            for member in MEMBERS:
                found = [t for t in tags if t["symbol"].replace('"', "") == "TiaMcp_EnableGate_DB." + member]
                require(len(found) == 1, "PLCSIM tag for %s is missing or ambiguous: %s" % (member, [t["symbol"] for t in tags]))
                symbols[member] = found[0]
            values = h.tool("sim_read", {"instanceId": instance["id"], "signals": [{"symbol": symbols[m]["symbol"], "type": "bool"} for m in MEMBERS]})["values"]
            require(all(v["quality"] == "good" for v in values), "Bad-quality oracle read: %s" % values)
            return {"tags": {m: symbols[m] for m in MEMBERS}, "values": [v["value"] for v in values]}

        def monitored(label, state):
            def check():
                expected = oracle()
                result = h.tool("watch_table_monitor", {**monitor, "samples": 3, "intervalMs": 200})
                rows = result["rows"]
                require([r["resolution"]["status"] for r in rows] == ["resolved", "resolved"], "Rows did not resolve: %s" % rows)
                for row, member in zip(rows, MEMBERS):
                    tag = expected["tags"][member]
                    require(row["resolution"] == {"status": "resolved", "symbol": tag["symbol"], "type": "bool", "dataType": tag["dataType"]},
                            "%s resolved to %s, the oracle found %s" % (member, row["resolution"], tag))
                samples = [[v["value"] for v in s["values"]] for s in result["samples"]]
                require(len(samples) == 3 and all(s == expected["values"] for s in samples)
                        and all(v["quality"] == "good" and [x["ordinal"] for x in s["values"]] == [0, 1] for s in result["samples"] for v in s["values"]),
                        "Samples %s differ from the oracle %s" % (samples, expected["values"]))
                require(result["table"]["name"] == "MilestoneWatch" and result["table"]["semanticSha256"] == table["semanticSha256"]
                        and result["table"]["rowCount"] == 2 and result["page"] == {"offset": 0, "returned": 2, "total": 2, "nextOffset": None},
                        "Table identity or page differs: %s %s" % (result["table"], result["page"]))
                require(result["readOnly"] is True and result["forced"] is False and result["modified"] is False and result["physicalPlc"] is False,
                        "Claims differ: %s" % {k: result[k] for k in ("readOnly", "forced", "modified", "physicalPlc")})
                require(result["target"]["instanceId"] == instance["id"] and result["target"]["instanceName"] == instance["name"], "Target identity differs")
                return {"operatingState": state, "oracle": expected, "samples": samples, "resolution": [r["resolution"] for r in rows]}
            return h.stage("watch_table_monitor " + label, check)

        h.report["monitorRun"] = monitored("in RUN", "Run")

        def paged():
            first = h.tool("watch_table_monitor", {**monitor, "count": 1})
            second = h.tool("watch_table_monitor", {**monitor, "offset": first["page"]["nextOffset"], "count": 1})
            require(first["page"]["nextOffset"] == 1 and [r["ordinal"] for r in first["rows"]] == [0]
                    and second["page"]["nextOffset"] is None and [r["ordinal"] for r in second["rows"]] == [1], "Paging differs: %s %s" % (first["page"], second["page"]))
            return [first["page"], second["page"]]
        h.report["paging"] = h.stage("watch_table_monitor pages", paged)

        def force_table():
            result = h.tool("watch_table_monitor", {**monitor, "tableName": forces[0]["name"], "group": forces[0]["group"], "forceTable": True})
            require(result["table"]["kind"] == "force" and result["page"]["total"] == len(forces[0]["rows"]) and result["forced"] is False,
                    "Force-table monitor differs: %s" % result["table"])
            return {"table": result["table"], "page": result["page"], "samples": len(result["samples"])}
        h.report["forceTableMonitor"] = h.stage("watch_table_monitor the force table", force_table)

        refuse("count 0", {**monitor, "count": 0}, ["InvalidRequest"])
        refuse("a foreign creationId", {**monitor, "creationId": uuid.uuid4().hex}, ["SimulationOwnershipUnverified"])
        refuse("a foreign instance name", {**monitor, "instanceName": instance["name"] + "x"}, ["SimulationTargetMismatch"])
        refuse("an unknown table", {**monitor, "tableName": "NoSuchTable"}, ["WatchTableNotFound"])

        def unchanged():
            after = h.tool("watch_table_read", {"tableName": "MilestoneWatch", "plcPath": plc})["target"]
            require(after["semanticSha256"] == table["semanticSha256"] and after["rows"] == table["rows"], "Monitoring changed the offline table")
            return after["semanticSha256"]
        h.stage("the offline table is unchanged", unchanged)

        stopped = h.stage("stop", lambda: h.tool("sim_state", {"instanceId": instance["id"], "action": "stop", "timeoutMs": 30000}))
        require(stopped["instance"]["operatingState"] == "Stop", "The instance did not stop")
        h.report["monitorStop"] = monitored("in STOP", "Stop")
        off = h.stage("power off", lambda: h.tool("sim_state", {"instanceId": instance["id"], "action": "powerOff", "timeoutMs": 60000}))
        require(off["instance"]["operatingState"] == "Off", "The instance did not switch off")
        result = h.stage("sim_release", lambda: h.tool("sim_release", {"instanceId": instance["id"], "deleteOwned": True}))
        require(result.get("unregistered") is True, "The owned instance was not released")
        released = True
        refuse("a released instance", monitor, ["SimulationNotSelected", "SimulationOwnershipUnverified"])
    finally:
        if not released:
            h.stage("sim_release", lambda: h.tool("sim_release", {"instanceId": instance["id"], "deleteOwned": True}))


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
    args.host_args = ["--component-qualification", "--watch-qualification", "none"]
    Harness(args, "wm_", "D02 live watch-table monitoring on one owned PLCSIM Advanced V8 instance on Softbus", LIMITATIONS, [SEED]).run(body)
