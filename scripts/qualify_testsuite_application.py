"""TS-Q02: run Test Suite application tests on one owned PLCSIM Advanced V8 instance (Softbus) from an owned scratch.

Needs a TestSuiteQualification=true build and a Host started with --testsuite-qualification. The scratch CPU 1511-1 PN runs
Main -> "TiaMcp_EnableGate_DB"(Enable := TRUE). Its program is downloaded to a fresh owned instance, which is powered on and
set to RUN. qualification_testsuite_prepare then imports the two fixture cases (TiaMcp_Q02_Pass asserts Active = TRUE,
TiaMcp_Q02_Fail deliberately asserts FALSE), scoped externally managed on that instance, and reviews one of them. One case
per Host session, like TS-Q01:

  pass      the passing case succeeds; refusals first: a tampered and a released instance's ticket, a project outside the
            owned scratch, a missing ticket, the production gate and a system-managed review of the same case
  fail      the failing case is an assertion failure (recoveryRequired), then checkpoint recovery and reconciliation
  afterRun  the passing case with an injected lost Run reply (outcomeUnknown), then recovery and reconciliation
  released  the instance is released between prepare and execute; observation refuses before checkpoint or Run

Nothing outside the worker-created scratch and the instances this run creates is touched. Every instance is released.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import uuid

from block_test_runtime import save_generated_scratch_project, verify_credential_provisioning, verify_download, verify_owned_instance
from library_native_harness import Harness, require

PROFILE = "v21.owned-plcsim.application.qualification-v1"
LIMITATIONS = [
    "One owned PLCSIM Advanced V8 CPU 1511 instance on Softbus per case, externally managed; one single-step case per run. "
    "System-managed mode, case lists, sets, whole-group runs and system tests stay unqualified.",
    "The license is observed through this session's TestCase.SaveToFile (documented to throw LicenseNotFoundException); "
    "execution acceptance is the returned Run itself.",
    "The simulator ticket expires 15 minutes after sim_create, so download, prepare and execute run back to back.",
]


def body(h, case):
    plc = h.owned_scratch("testsuite application " + case)
    project = h.scratch["project"]["path"]
    h.apply("project_simulation_enable", {"enabled": True})
    h.report["case"] = case

    def refuse(label, name, arguments, codes):
        def check():
            error = h.call_error(name, arguments)
            require(error.get("code") in codes, "Expected %s, observed %s: %s" % (codes, error.get("code"), error.get("message")))
            return error
        return h.stage("refuse " + label, check)

    def create(label):
        creation = uuid.uuid4().hex
        created = h.stage("sim_create " + label, lambda: h.tool("sim_create", {"cpuType": "CPU1511", "creationId": creation}))
        instance = verify_owned_instance(created["instance"])
        require(instance.get("operatingState") == "Off", "A fresh owned OFF instance is required")
        return created, instance

    def release(instance, powered=True):
        for action in ("stop", "powerOff") if powered else ():
            try:
                h.stage("sim_state " + action, lambda: h.tool("sim_state", {"instanceId": instance["id"], "action": action, "timeoutMs": 60000}))
            except Exception:
                pass  # The release below still decides; its failure fails the run.
        result = h.stage("sim_release", lambda: h.tool("sim_release", {"instanceId": instance["id"], "deleteOwned": True}))
        require(result.get("unregistered") is True, "The owned instance was not released")

    if case == "pass":
        # A released owned instance is no longer registered: its ticket refuses before any fixture import.
        gone_created, gone = create("(released before use)")
        release(gone, powered=False)
        h.report["releasedRefusal"] = refuse("a released instance's ticket", "qualification_testsuite_prepare",
            {"projectPath": project, "fixture": "applicationPass", "idempotencyKey": uuid.uuid4().hex,
             "simulatorTicket": gone_created["downloadTicket"]}, ["SimulationTargetMismatch"])

    created, instance = create("(owned target)")
    released = False
    try:
        cpus = h.scratch["scratchSecurity"]["cpus"]
        require(len(cpus) == 1, "Exactly one generated scratch credential is required")
        ticket = {"downloadTicket": created["downloadTicket"], "credentialMetadataPath": cpus[0]["credentialMetadataPath"]}
        download = h.stage("simulation_download", lambda: h.tool("simulation_download", ticket))
        verify_download(download, instance)
        h.report["generatedProjectSave"] = save_generated_scratch_project(h.tool, download)
        provisioned = h.stage("simulation_provision_scratch_credential",
                              lambda: h.tool("simulation_provision_scratch_credential", {**ticket, "trustOwnedEmptyCpuCertificate": True}))
        h.report["credentialProvisioning"] = verify_credential_provisioning(provisioned, download, trust_owned_empty_cpu_certificate=True)
        h.stage("power on", lambda: h.tool("sim_state", {"instanceId": instance["id"], "action": "powerOn", "timeoutMs": 60000}))
        running = h.stage("run", lambda: h.tool("sim_state", {"instanceId": instance["id"], "action": "run", "timeoutMs": 30000}))
        require(running["instance"]["operatingState"] == "Run", "The instance did not enter RUN")
        h.report["instance"] = {"id": instance["id"], "name": instance["name"]}

        prepare = {"projectPath": project, "fixture": "applicationFail" if case == "fail" else "applicationPass",
                   "idempotencyKey": h.directory.name + "-" + case, "simulatorTicket": created["downloadTicket"]}
        if case == "pass":
            tampered = created["downloadTicket"][:-8] + ("A" * 8 if not created["downloadTicket"].endswith("A" * 8) else "B" * 8)
            refuse("a tampered ticket", "qualification_testsuite_prepare", {**prepare, "simulatorTicket": tampered}, ["InvalidSimulationTicket"])
            refuse("an application fixture without a ticket", "qualification_testsuite_prepare",
                   {key: value for key, value in prepare.items() if key != "simulatorTicket"}, ["InvalidRequest"])
            refuse("a project outside the owned scratch", "qualification_testsuite_prepare",
                   {**prepare, "projectPath": str(h.directory / "foreign" / "Foreign.ap21")}, ["ProjectIsolationRequired"])
        prepared = h.stage("qualification_testsuite_prepare", lambda: h.tool("qualification_testsuite_prepare", prepare))
        status = prepared["status"]
        job, review = status["job"]["id"], status["job"]["reviewSha256"]
        op = status["operations"][0]["id"]
        require(prepared["observation"]["executionProfile"] == PROFILE and prepared["observation"]["nativeExecuted"] is False, "Prepare must not execute")
        h.report["prepare"] = {"jobId": job, "review": prepared["review"], "definition": prepared["observation"]["definition"]["name"]}

        def action(current):
            return {"jobId": job, "reviewedSha256": review, "operationId": op,
                    "expectedRevision": next(o for o in current["operations"] if o["id"] == op)["revision"]}

        def state():
            return h.tool("testsuite_style_job_status", {"jobId": job})["operations"][0]

        if case == "pass":
            refuse("the production gate", "testsuite_style_job_execute_next", action(status), ["TestSuiteProfileUnqualified"])
            require(state()["state"] == "prepared" and state()["revision"] == 0, "The production refusal changed the operation")
            # The same reviewed case in system-managed mode, where Test Suite would create its own unowned instance.
            reviewed = json.loads((h.directory / prepared["review"]["relativePath"]).read_text(encoding="utf-8"))
            manifest = reviewed["runs"][0]["run"]["definitions"][0]
            system = h.stage("system-managed review", lambda: h.tool("testsuite_application_plan_prepare", {"request": {
                "target": reviewed["runs"][0]["operation"]["target"], "executionProfile": PROFILE, "executionMode": "systemManaged",
                "simulatorTicket": created["downloadTicket"], "wholeGroup": False,
                "definitions": [{"manifest": manifest, "originalArtifact": prepared["observation"]["definition"]["originalArtifact"]}],
                "runs": [{"selectedDefinitionIds": [manifest["id"]], "excludedDefinitionIds": []}]}}))
            system_job = h.tool("testsuite_style_job_prepare", {"relativePath": system["review"]["relativePath"], "sha256": system["review"]["sha256"],
                                                                  "reviewedSha256": system["review"]["sha256"], "idempotencyKey": uuid.uuid4().hex})
            system_op = system_job["operations"][0]
            refuse("a system-managed (Test Suite owned) instance", "qualification_testsuite_execute",
                   {"jobId": system_job["job"]["id"], "reviewedSha256": system_job["job"]["reviewSha256"], "operationId": system_op["id"],
                    "expectedRevision": system_op["revision"]}, ["TestSuiteProfileUnqualified"])
        if case == "released":
            release(instance)
            released = True
            refuse("an instance released after prepare", "qualification_testsuite_execute", action(status), ["SimulationTargetMismatch"])
            current = state()
            require(current["state"] == "outcomeUnknown" and not current["checkpoints"], "The refusal must precede any checkpoint")
            h.report["finalOperation"] = current
            return

        execute = action(status)
        if case == "afterRun":
            execute["fault"] = "afterRun"
            refuse("the injected lost Run reply", "qualification_testsuite_execute", execute, ["QualificationResponseLost"])
        else:
            h.stage("qualification_testsuite_execute", lambda: h.tool("qualification_testsuite_execute", execute))
        status = h.tool("testsuite_style_job_status", {"jobId": job})
        current = status["operations"][0]["state"]
        expected = {"pass": "succeeded", "fail": "recoveryRequired", "afterRun": "outcomeUnknown"}[case]
        require(current == expected, "Expected %s, observed %s" % (expected, current))
        results = h.stage("testsuite_style_job_results", lambda: h.tool("testsuite_style_job_results", {"jobId": job, "operationId": op}))
        if results.get("raw"):
            h.report["rawResults"] = json.loads((h.directory / results["raw"]["relativePath"]).read_text(encoding="utf-8"))
        if case != "afterRun":
            classification = results["result"]["classification"]
            h.report["classification"] = classification
            require(classification == ("passed" if case == "pass" else "assertionFailure"), "Classification mismatch: " + classification)
        if case in ("fail", "afterRun"):
            recovered = h.stage("qualification_testsuite_recover", lambda: h.tool("qualification_testsuite_recover",
                                {**action(status), "recoveryLocation": str(h.directory / "rc")}))
            reconciled = h.stage("qualification_testsuite_reconcile", lambda: h.tool("qualification_testsuite_reconcile", action(recovered)))
            final = reconciled["operations"][0]
            require(final["state"] == "reconciled" and final["recoveryResolutions"][0]["disposition"] == "verifiedRestored",
                    "Independent original/destination reconciliation incomplete")
        h.report["finalOperation"] = state()
    finally:
        if not released:
            release(instance)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--sim-worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--case", choices=["pass", "fail", "afterRun", "released"], required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    args.host_args = ["--testsuite-qualification", "--timeout-seconds", "900"]
    Harness(args, "tq_", "TS-Q02 Test Suite application test on one owned PLCSIM Advanced V8 instance (case %s)" % args.case,
            LIMITATIONS, []).run(lambda h: body(h, args.case))
