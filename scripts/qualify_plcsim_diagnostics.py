"""Qualify scratch credential provisioning (L03) and the diagnostic PLCSIM rows (D02, D17) on one owned Softbus instance.

The fixture is an owned scratch CPU 1511-1 PN with simulation support and an FB DiagGate (input Enable, output Active,
Active := Enable) whose instance DB Main calls without assigning Enable, each added with checkpoint, compile and save. A saved
review is the source of a diagnostic case on that instance with two hypotheses (Active follows Enable true; Active is released
with Enable false) and of its experiment. The program is downloaded to a fresh owned PLCSIM Advanced V8 instance; after the
generated-state save, simulation_provision_scratch_credential provisions the scratch credential, and the complete project state
(hardware, device inventory, every program export) must equal the state just before it. In RUN, a capture associated with the
case is acquired into the experiment and exported as JSON, Markdown and HTML; independently decoded capture rows give the
expected cycle counts. diagnostic_plcsim_write then sets Enable after its precondition read, a second capture is acquired, the
two acquisitions are compared, and a final write restores Enable. Typed refusals and a precondition mismatch change nothing.
The instance is stopped, switched off and released.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import time
import uuid

from block_test_runtime import save_generated_scratch_project, verify_credential_provisioning, verify_download, verify_memory_card, verify_owned_instance
from library_native_harness import Harness, require
from qualify_hardware_changes import Run
from qualify_plc_object_mutations import diff
from qualify_runtime_diagnostic_acquisition_offline import original_chain, validators

FB = '''FUNCTION_BLOCK "DiagGate"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      Enable : Bool;
   END_VAR
   VAR_OUTPUT
      Active : Bool;
   END_VAR

BEGIN
\t#Active := #Enable;
END_FUNCTION_BLOCK

'''
MAIN = '''ORGANIZATION_BLOCK "Main"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
BEGIN
    "DiagGate_DB"();
END_ORGANIZATION_BLOCK

'''
HYPOTHESES = [
    {"id": "follows", "statement": "Active is true whenever Enable is true.",
     "prediction": {"when": [{"signalId": "enable", "op": "eq", "value": True}], "expect": [{"signalId": "active", "op": "eq", "value": True}]}},
    {"id": "releases", "statement": "Active is false whenever Enable is false.",
     "prediction": {"when": [{"signalId": "enable", "op": "eq", "value": False}], "expect": [{"signalId": "active", "op": "eq", "value": False}]}},
]
UNITS = {"enable": "1", "active": "1"}
CRITERIA = {
    "simulation_provision_scratch_credential": {
        "AC-identity": "A malformed download ticket refuses as InvalidSimulationTicket before any native call and leaves the project state unchanged.",
        "AC-preserve": "The complete project state (hardware, device inventory, every program export) after provisioning equals the state just before it.",
        "AC-simulator-lifecycle": "Fresh owned OFF instance, download, generated-state save, provisioning with the original card restored, power-on, RUN, stop, power-off and owned release.",
        "AC-evidence": "This report."},
    "diagnostic_plcsim_write": {
        "AC-identity": "Writes without acknowledgeWrite, of a signal outside the case, with a datatype that differs from the declaration, and while a capture holds the instance are refused; nothing is written.",
        "AC-preview": "A wrong expected value returns preconditionMismatch with the values read and dispatches nothing; sim_read confirms the unchanged value.",
        "AC-recovery": "The reverse write restores Enable after its own precondition read; sim_read and the PLC's response (Active) confirm both writes.",
        "AC-evidence": "This report."},
    "diagnostic_experiment_acquire_runtime": {
        "AC-identity": "An unknown experiment, a malformed snapshot reference and missing units refuse; each acquisition names its exact experiment and snapshot.",
        "AC-completeness": "Every committed row is retained; an independent decoder of the original plan and segments reproduces the rows and the per-hypothesis cycle counts.",
        "AC-evidence": "This report."},
    "diagnostic_runtime_acquisition_export": {
        "AC-identity": "An unknown acquisition and an unknown format refuse.",
        "AC-completeness": "JSON, Markdown and HTML exports of both acquisitions are schema-valid, hash-exact files; the JSON export is the complete acquisition.",
        "AC-evidence": "This report."},
    "diagnostic_runtime_acquisition_compare": {
        "AC-identity": "Comparing an acquisition with itself refuses as MeasurementLineageMismatch, and a request with fields beyond reason refuses.",
        "AC-completeness": "Per hypothesis, the before and after counts equal the independently decoded cycles and the patterns change as the write predicts.",
        "AC-evidence": "This report."},
}
LIMITATIONS = [
    "One owned PLCSIM Advanced V8 CPU 1511 instance on Softbus with the owned scratch's generated credential (first-use TLS trust under the owned-empty-CPU policy); "
    "one FB instance with two Bool signals and two Bool hypotheses; numeric, cycle-synchronous and multi-instance captures are not exercised.",
    "Sequential polling: Enable and Active of one cycle are separate reads, and the counts depend on capture duration. Verdicts stay unresolved by design.",
    "The provisioning comparison covers the offline project state the read tools expose; online state other than the card restore is not compared.",
]


def symbol(h, instance_id, member):
    tags = h.tool("sim_tags", {"instanceId": instance_id, "filter": "DiagGate_DB", "maxItems": 2000})["tags"]
    found = [t["symbol"] for t in tags if t["symbol"].replace('"', "") == "DiagGate_DB." + member]
    require(len(found) == 1, "PLCSIM symbol DiagGate_DB.%s is missing or ambiguous: %s" % (member, [t["symbol"] for t in tags]))
    return found[0]


def cycles(document, root):
    """Independently decoded per-cycle values from the original capture chain."""
    plan, rows = original_chain(document, root)
    mapping = {s["selectionId"]: s["signalId"] for s in plan["association"]["signals"]}
    by_cycle = {}
    for row in rows:
        if row["kind"] == "gap":
            continue
        result = json.loads(row["readResultJson"]) if not row["errorCode"] else {}
        by_cycle.setdefault(row["cycle"], {})[mapping[row["selectionId"]]] = result.get("value") if result.get("status") == "read" else None
    counts = {}
    for hypothesis in HYPOTHESES:
        when, expect = hypothesis["prediction"]["when"][0], hypothesis["prediction"]["expect"][0]
        matched = contradicted = 0
        for values in by_cycle.values():
            if None not in (values.get(when["signalId"]), values.get(expect["signalId"])) and values[when["signalId"]] == when["value"]:
                matched += values[expect["signalId"]] == expect["value"]
                contradicted += values[expect["signalId"]] != expect["value"]
        counts[hypothesis["id"]] = {"matchedCycles": matched, "contradictedCycles": contradicted}
    return counts, len(rows)


class Lab:
    def __init__(self, h, run):
        self.h, self.run = h, run
        self.schema = validators()
        h.report.update(criteria=CRITERIA, acquisitions={}, writes={})

    def refuse(self, label, name, arguments, codes):
        def check():
            error = self.h.call_error(name, arguments)
            require(error.get("code") in codes, "Expected %s, observed %s: %s" % (codes, error.get("code"), error.get("message")))
            return error
        return self.h.stage("refuse %s: %s" % (name, label), check)

    def read(self, signals):
        values = self.h.tool("sim_read", {"instanceId": self.instance["id"], "signals": [{"symbol": s["symbol"], "type": s["type"]} for s in signals]})["values"]
        require(all(v["quality"] == "good" for v in values), "Bad-quality PLCSIM read: %s" % values)
        return {v["symbol"]: v["value"] for v in values}

    def capture(self, label, seconds=3):
        h, selections = self.h, [uuid.uuid4().hex, uuid.uuid4().hex]
        request = {"attemptId": self.creation, "selectionIds": selections, "intervalMilliseconds": 100, "durationSeconds": seconds, "source": "plcsim",
                   "simulator": {"instanceId": self.instance["id"], "instanceName": self.instance["name"], "cycleSynchronous": False,
                                 "signals": [{k: s[k] for k in ("symbol", "type", "datatype")} for s in self.signals]},
                   "reviewId": self.review,
                   "association": {"caseId": self.case, "mappings": [
                       {"selectionId": selections[0], "signalId": "enable", "reason": "The instance's Enable input, read by its PLCSIM symbol."},
                       {"selectionId": selections[1], "signalId": "active", "reason": "The instance's Active output, read by its PLCSIM symbol."}]}}
        capture_id = uuid.uuid4().hex
        def run():
            started = h.tool("runtime_capture_start", {"captureId": capture_id, "request": request})
            require(started["plan"]["association"]["caseId"] == self.case, "The capture lost its case association")
            deadline = time.monotonic() + seconds + 60
            status = started
            while status["state"] == "running" and time.monotonic() < deadline:
                time.sleep(0.5)
                status = h.tool("runtime_capture_status", {"captureId": capture_id})
            if status["state"] == "running":
                status = h.tool("runtime_capture_stop", {"captureId": capture_id})
            progress = status["recordedProgress"]
            require(progress["terminalReason"] == "durationLimit" and progress["rows"] > 0 and not status["localFailure"], "Capture %s did not complete: %s" % (label, progress))
            return {"captureId": capture_id, "snapshotRef": status["snapshotRef"], "state": status["state"], "progress": progress}
        return h.stage("capture " + label, run)

    def acquire(self, label, capture):
        h = self.h
        def run():
            arguments = {"experimentId": self.experiment, "snapshotRef": capture["snapshotRef"], "request": {"reason": "Acquire the %s capture into the experiment." % label, "units": UNITS}}
            self.schema["input"].validate(arguments)
            receipt = h.tool("diagnostic_experiment_acquire_runtime", arguments)
            self.schema["receipt"].validate(receipt)
            document = receipt["acquisition"]
            require(document["experimentId"] == self.experiment and document["snapshotRef"] == capture["snapshotRef"], "Acquisition names another experiment or snapshot")
            counts, rows = cycles(document, h.directory)
            require(rows == capture["progress"]["rows"], "The acquisition retains %d of %d committed rows" % (rows, capture["progress"]["rows"]))
            analysed = {x["id"]: {"matchedCycles": x["matchedCycles"], "contradictedCycles": x["contradictedCycles"]} for x in document["analysis"]["hypotheses"]}
            require(analysed == counts, "Analysis counts %s differ from the independent decoder %s" % (analysed, counts))
            exports = {}
            for fmt in ("json", "markdown", "html"):
                exported = h.tool("diagnostic_runtime_acquisition_export", {"acquisitionId": receipt["id"], "format": fmt})
                self.schema["export"].validate(exported)
                raw = Path(exported["artifact"]["path"]).read_bytes()
                require(hashlib.sha256(raw).hexdigest() == exported["id"] and len(raw) == exported["artifact"]["bytes"], "The %s export file differs from its receipt" % fmt)
                if fmt == "json":
                    require(json.loads(raw) == document, "The JSON export is not the complete acquisition")
                exports[fmt] = {"bytes": len(raw), "sha256": exported["id"]}
            return {"id": receipt["id"], "snapshotRef": capture["snapshotRef"], "rows": rows, "counts": counts, "exports": exports}
        result = h.stage("acquire and export " + label, run)
        h.report["acquisitions"][label] = result
        return result

    def write(self, label, signal, expected, value, **extra):
        h = self.h
        arguments = {"caseId": self.case, "instanceId": self.instance["id"], "instanceName": self.instance["name"], "creationId": self.creation,
                     "writes": [{"signalId": signal["id"], "symbol": signal["symbol"], "type": signal["type"], "datatype": signal["datatype"],
                                 "expected": expected, "value": value, "reason": label}], "acknowledgeWrite": True}
        arguments.update(extra)
        return arguments

    def written(self, label, arguments, state):
        def run():
            before = self.read(self.signals)
            result = self.h.tool("diagnostic_plcsim_write", arguments)
            require(result["state"] == state and result["caseId"] == self.case and result["physicalPlc"] is False and result["forced"] is False,
                    "Write %s returned %s" % (label, {k: result.get(k) for k in ("state", "dispatched")}))
            if state == "written":
                require(result["dispatched"] == 1 and result["items"][0]["readbackMatches"] is True, "Write %s readback differs" % label)
            else:
                require(result["dispatched"] == 0 and self.read(self.signals) == before, "A precondition mismatch changed a value")
            return {"state": result["state"], "dispatched": result["dispatched"], "before": result.get("before")}
        result = self.h.stage("diagnostic_plcsim_write " + label, run)
        self.h.report["writes"][label] = result
        return result

    def settled(self, enable):
        def check():
            deadline = time.monotonic() + 5
            while True:
                values = self.read(self.signals)
                if [values[s["symbol"]] for s in self.signals] == [enable, enable] or time.monotonic() > deadline:
                    break
                time.sleep(0.2)
            require([values[s["symbol"]] for s in self.signals] == [enable, enable], "Enable and Active do not both read %s: %s" % (enable, values))
            return values
        return self.h.stage("sim_read shows Enable and Active %s" % enable, check)


def fixture(h, plc, kind, payload):
    prepared = h.stage("prepare " + kind, lambda: h.tool("change_prepare", {"kind": kind, "payload": payload, "plcPath": plc}))
    return h.apply_prepared(kind, prepared)


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("PLCSIM diagnostics")
    status = h.tool("tia_session_status")["projects"][0]
    run.project, run.base = status["path"], "/projects/%s/devices/" % status["name"]
    lab = Lab(h, run)
    h.apply("project_simulation_enable", {"enabled": True})
    fixture(h, run.plc, "source_upsert", {"language": "SCL", "source": FB, "expectedBlockNames": ["DiagGate"]})
    fixture(h, run.plc, "instance_db_create", {"name": "DiagGate_DB", "instanceOfName": "DiagGate"})
    fixture(h, run.plc, "source_upsert", {"language": "SCL", "source": MAIN, "expectedBlockNames": ["Main"]})

    lab.review = h.stage("save project review", lambda: h.tool("project_snapshot_capture", {"request": {"maxBlocks": 25}}))["reviewId"]
    fb, db = run.plc + "/blocks/DiagGate", run.plc + "/blocks/DiagGate_DB"
    signals = [{"id": member.lower(), "role": role, "reason": "The instance's %s the hypotheses relate." % member, "hypothesisIds": ["follows", "releases"],
                "storagePath": db, "memberPath": [member]} for member, role in (("Enable", "command"), ("Active", "output"))]
    case = h.stage("diagnostic_plan", lambda: h.tool("diagnostic_plan", {"request": {
        "reviewId": lab.review, "report": "DiagGate's Active output should follow its Enable input.", "blockPath": fb,
        "instance": {"dbPath": db, "memberPath": []}, "signals": signals, "hypotheses": HYPOTHESES}}))
    resolved = case["plan"]["signals"]
    require([(s["id"], s["resolution"], s["datatype"]) for s in resolved] == [("enable", "capturedDeclaration", "Bool"), ("active", "capturedDeclaration", "Bool")],
            "Case signals did not resolve to captured Bool declarations: %s" % [(s["id"], s["resolution"], s["datatype"], s.get("unavailableReason")) for s in resolved])
    lab.case = case["id"]
    lab.experiment = h.stage("diagnostic_experiment_plan", lambda: h.tool("diagnostic_experiment_plan", {"planId": lab.case, "request": {
        "reason": "Observe Enable and Active before and after a PLCSIM write of Enable.",
        "watch": {"tableName": "DiagGate experiment", "tiaVersion": 21},
        "trace": {"backend": "manual", "family": "ordinaryPlc", "reviewId": lab.review, "settings": {
            "task": "Main", "sampleEvery": 1, "preSamples": 10, "postSamples": 10,
            "rationale": "Bound the observation around the enable change.", "trigger": {"signalId": "enable", "kind": "risingEdge"}}}}}))["id"]

    h.stage("discovery baseline", lambda: h.tool("simulation_discovery_baseline", {})["baseline"])
    lab.creation = uuid.uuid4().hex
    created = h.stage("sim_create", lambda: h.tool("sim_create", {"cpuType": "CPU1511", "creationId": lab.creation}))
    lab.instance = verify_owned_instance(created["instance"])
    try:
        require(lab.instance.get("operatingState") == "Off", "A fresh owned OFF instance is required")
        cpus = h.scratch["scratchSecurity"]["cpus"]
        require(len(cpus) == 1, "Exactly one generated scratch credential is required")
        ticket = {"downloadTicket": created["downloadTicket"], "credentialMetadataPath": cpus[0]["credentialMetadataPath"]}
        download = h.stage("simulation_download", lambda: h.tool("simulation_download", ticket))
        verify_download(download, lab.instance)
        h.report["memoryCard"] = verify_memory_card(download)
        h.report["generatedProjectSave"] = save_generated_scratch_project(h.tool, download)
        before = h.stage("project state before provisioning", lambda: run.state())
        lab.refuse("a malformed download ticket", "simulation_provision_scratch_credential", {**ticket, "downloadTicket": "not-a-ticket", "trustOwnedEmptyCpuCertificate": True},
                   ["InvalidSimulationTicket"])
        provisioned = h.stage("simulation_provision_scratch_credential",
                              lambda: h.tool("simulation_provision_scratch_credential", {**ticket, "trustOwnedEmptyCpuCertificate": True}))
        h.report["credentialProvisioning"] = verify_credential_provisioning(provisioned, download, trust_owned_empty_cpu_certificate=True)
        h.report["restoredMemoryCard"] = verify_memory_card(download)
        h.stage("provisioning left the project state unchanged", lambda: require(run.state() == before, "Provisioning changed the project: %s" % diff(before, run.state())) or len(before))
        h.stage("power on", lambda: h.tool("sim_state", {"instanceId": lab.instance["id"], "action": "powerOn", "timeoutMs": 60000}))
        running = h.stage("run", lambda: h.tool("sim_state", {"instanceId": lab.instance["id"], "action": "run", "timeoutMs": 30000}))
        require(running["instance"]["operatingState"] == "Run", "The instance did not enter RUN")

        lab.signals = [{"id": s["id"], "symbol": symbol(h, lab.instance["id"], member), "type": "bool", "datatype": "Bool"} for s, member in zip(resolved, ("Enable", "Active"))]
        enable = lab.signals[0]
        lab.settled(False)
        first = lab.acquire("before the write", lab.capture("before the write"))
        require(first["counts"]["releases"]["matchedCycles"] > 0 and first["counts"]["follows"]["matchedCycles"] == 0, "Unexpected counts before the write: %s" % first["counts"])

        lab.refuse("an unknown experiment", "diagnostic_experiment_acquire_runtime", {"experimentId": "0" * 64, "snapshotRef": first["snapshotRef"], "request": {"reason": "x", "units": UNITS}},
                   ["ArtifactUnavailable"])
        lab.refuse("a malformed snapshot reference", "diagnostic_experiment_acquire_runtime", {"experimentId": lab.experiment, "snapshotRef": "capture:x", "request": {"reason": "x", "units": UNITS}},
                   ["RuntimeCaptureReferenceInvalid"])
        lab.refuse("missing units", "diagnostic_experiment_acquire_runtime", {"experimentId": lab.experiment, "snapshotRef": first["snapshotRef"], "request": {"reason": "x"}}, ["InvalidRequest"])
        lab.refuse("an unknown acquisition", "diagnostic_runtime_acquisition_export", {"acquisitionId": "0" * 64, "format": "json"}, ["ArtifactUnavailable"])
        lab.refuse("an unknown format", "diagnostic_runtime_acquisition_export", {"acquisitionId": first["id"], "format": "pdf"}, ["InvalidRequest"])

        lab.refuse("no acknowledgeWrite", "diagnostic_plcsim_write", lab.write("set Enable", enable, False, True, acknowledgeWrite=False), ["RuntimeAcknowledgementRequired"])
        outside = lab.write("an undeclared signal", enable, False, True)
        outside["writes"][0]["signalId"] = "missing"
        lab.refuse("a signal outside the case", "diagnostic_plcsim_write", outside, ["RuntimeCaptureAssociationMismatch"])
        typed = lab.write("a wrong datatype", enable, False, True)
        typed["writes"][0]["datatype"] = "Int"
        lab.refuse("a datatype that differs from the declaration", "diagnostic_plcsim_write", typed, ["PlcsimCaptureInvalid", "RuntimeCaptureAssociationMismatch"])
        lab.written("with a wrong expected value", lab.write("Expect Enable true while it reads false.", enable, True, False), "preconditionMismatch")
        lab.written("set Enable", lab.write("Raise Enable to test the follows hypothesis.", enable, False, True), "written")
        lab.settled(True)
        second = lab.acquire("after the write", lab.capture("after the write", seconds=4))
        require(second["counts"]["follows"]["matchedCycles"] > 0 and second["counts"]["releases"]["matchedCycles"] == 0, "Unexpected counts after the write: %s" % second["counts"])

        def compare():
            result = h.tool("diagnostic_runtime_acquisition_compare", {"beforeAcquisitionId": first["id"], "afterAcquisitionId": second["id"],
                                                                         "request": {"reason": "Compare the observation before and after the Enable write."}})
            comparison = result["comparison"]
            rows = {x["id"]: x for x in comparison["hypotheses"]}
            for side, acquisition in (("before", first), ("after", second)):
                for hypothesis, counts in acquisition["counts"].items():
                    require({k: rows[hypothesis][side][k] for k in counts} == counts, "Compared %s counts differ for %s" % (side, hypothesis))
            require((rows["follows"]["before"]["pattern"], rows["follows"]["after"]["pattern"]) == ("notApplicable", "matchedOnly")
                    and (rows["releases"]["before"]["pattern"], rows["releases"]["after"]["pattern"]) == ("matchedOnly", "notApplicable")
                    and all(x["patternChanged"] and x["verdict"] == "unresolved" for x in rows.values()) and comparison["sameSignals"] is True,
                    "Comparison patterns differ: %s" % rows)
            return {k: {"before": v["before"]["pattern"], "after": v["after"]["pattern"]} for k, v in rows.items()}
        h.report["comparison"] = h.stage("diagnostic_runtime_acquisition_compare", compare)
        lab.refuse("one acquisition with itself", "diagnostic_runtime_acquisition_compare",
                   {"beforeAcquisitionId": first["id"], "afterAcquisitionId": first["id"], "request": {"reason": "x"}}, ["MeasurementLineageMismatch"])
        lab.refuse("fields beyond reason", "diagnostic_runtime_acquisition_compare",
                   {"beforeAcquisitionId": first["id"], "afterAcquisitionId": second["id"], "request": {"reason": "x", "verdict": "fixed"}}, ["InvalidRequest"])

        capture_id = uuid.uuid4().hex
        hold = {"attemptId": lab.creation, "selectionIds": [uuid.uuid4().hex], "intervalMilliseconds": 100, "durationSeconds": 30, "source": "plcsim",
                "simulator": {"instanceId": lab.instance["id"], "instanceName": lab.instance["name"], "signals": [{k: enable[k] for k in ("symbol", "type", "datatype")}]}}
        h.stage("start a capture that holds the instance", lambda: h.tool("runtime_capture_start", {"captureId": capture_id, "request": hold})["state"])
        try:
            lab.refuse("while a capture holds the instance", "diagnostic_plcsim_write", lab.write("Lower Enable during a capture.", enable, True, False),
                       ["RuntimeCaptureSessionBusy"])
        finally:
            h.stage("stop the holding capture", lambda: h.tool("runtime_capture_stop", {"captureId": capture_id})["state"])
        lab.written("restore Enable", lab.write("Restore Enable to its initial value.", enable, True, False), "written")
        lab.settled(False)
        stopped = h.stage("stop", lambda: h.tool("sim_state", {"instanceId": lab.instance["id"], "action": "stop", "timeoutMs": 30000}))
        require(stopped["instance"]["operatingState"] == "Stop", "The instance did not stop")
        off = h.stage("power off", lambda: h.tool("sim_state", {"instanceId": lab.instance["id"], "action": "powerOff", "timeoutMs": 60000}))
        require(off["instance"]["operatingState"] == "Off", "The instance did not switch off")
    finally:
        released = h.stage("sim_release", lambda: h.tool("sim_release", {"instanceId": lab.instance["id"], "deleteOwned": True}))
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
    Harness(args, "ps_", "L03, D02 and D17 on one owned PLCSIM Advanced V8 instance on Softbus", LIMITATIONS, []).run(body)
