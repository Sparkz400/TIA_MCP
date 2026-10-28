"""Finish read-only native save/reopen checks from a retained encoder run.

Never replay a mutation. Verify the prior owned process exited, bind the exact
source report and its native exports by SHA-256, then open each retained saved
copy through a byte-verified inspection copy in a fresh worker-owned TIA instance. TIA stale-lock errors may be retried
only while that new session remains empty; lock files are never removed.
"""
import argparse
import hashlib
import json
import os
import pathlib
import uuid

from smoke_mcp import ROOT
from compilation_evidence import fixture_plc_selection
from encoder_endpoints import capture_endpoint, endpoint, same
import encoder_copy_admission as copy_admission
import encoder_software_preservation as software_peers
import encoder_hardware_preservation as hardware_peers
from encoder_lifecycle import new_session, sync_steps, finish_session
from block_test_runtime import NativeToolError
from run_block_tests import open_qualified_project, same_user_project_lock
from engineering_preservation import require, native_payload, native_payload_hash

RUNS = ROOT / "artifacts"
from qualify_encoder_hardware import NAME, assert_parameter_edit, decode_native_artifact, parameter_map


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence", type=pathlib.Path, required=True)
    parser.add_argument("--evidence-sha256", required=True, help="SHA-256 of the completed producer report, pinned before this invocation.")
    parser.add_argument("--host", type=pathlib.Path, required=True)
    parser.add_argument("--worker", type=pathlib.Path, required=True)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    args = parser.parse_args()
    require(not args.report.exists(), "Choose a new report path; prior evidence is immutable")
    source, admission = copy_admission.load_source(args.evidence, args.evidence_sha256)
    profile = source['profile']
    output = copy_admission.safe_path(args.report.absolute())
    require(all(not output.is_relative_to(pathlib.Path(row['projectPath']).parent)
                for row in admission['closedCopies']['copies'].values()), 'Companion report cannot overwrite an original project tree')
    baseline = parameter_map(next(step["result"] for step in source["steps"] if step["name"] == "engineering_read"))
    edited = source["edit"]["readback"]
    assert_parameter_edit(baseline, edited, profile["parameterEdit"])
    native_before = source["nativeBefore"]
    native_after = source["edit"]["nativeAfter"]
    require(native_payload(native_before["xml"]) == native_payload(source["recovery"]["nativeRecovered"]["xml"]), 'Native evidence requirement failed: native_payload(native_before["xml"]) == native_payload(source["recovery"]["nativeRecovered"]["xml"])')
    owned_pid = next(step["result"]["processId"] for step in source["steps"] if step["name"] == "tia_start")
    # Prior exit is proved by the retained original handles, never a new PID lookup.
    report = {"completed": False, "readOnly": True, "steps": [], "sourceReport": {"path": str(args.evidence.resolve()),
        "sha256": admission['sourceReport']['sha256'], "completed": source["completed"], "failure": source.get("failure")},
        "copyAdmission": admission, "inspectionCopies": {}, "openAdmissions": [],
        "priorOwnedProcess": {"processId": owned_pid, "exited": True, "basis": "hashBoundExactSourceLifetimes"}, "copies": {}, "sourcePrerequisitesVerified": True,
        "lockOwner": {"user": os.environ.get("USERNAME"), "computer": os.environ.get("COMPUTERNAME")},
        "binaries": {key: {"path": str(path.resolve()), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                     for key, path in (("host", args.host), ("worker", args.worker))},
        "verifierSha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest()}
    run_dir = RUNS / ("encoder-verifier-" + uuid.uuid4().hex[:8])
    run_dir.mkdir(parents=True)
    report["runDirectory"] = str(run_dir)

    def persist():
        sync_steps(report)
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    copy_admission.recheck(admission)
    session = new_session(args.host.resolve(), args.worker.resolve(), run_dir, report, persist, run_dir / "lifetimes")
    persist()

    def call(name, arguments=None):
        try:
            if name == 'project_open':
                require(arguments == {'path': str(project_path)}, 'Only the currently admitted inspection copy may open')
                report['openAdmissions'].append(copy_admission.before_open(admission, label, receipt, session.session))
                persist()
            return session.call(name, arguments)
        except NativeToolError as error:
            if name == "project_open" and same_user_project_lock(error, arguments["path"]):
                status = session.call("tia_session_status")
                require(status.get("attached") is True and status.get("ownsPortalProcess") is True and
                        status.get("processId") == session.session["processId"] and status.get("projects") == [],
                        "Partial or changed owned session prevents a safe project-open retry")
            raise

    try:
        for label, path, expected, expected_parameters in (
                ("recovered", source["recoveredProjectPath"], native_before, baseline),
                ("changed", source["projectPath"], native_after, edited)):
            receipt = report['inspectionCopies'][label] = {}
            project_path = copy_admission.prepare_copy(admission, label, run_dir, receipt)
            persist()
            copy_admission.recheck(admission)
            session.known_paths.add(str(project_path))
            started_result = session.start()
            open_evidence = {}
            report.setdefault("openEvidence", {})[label] = open_evidence
            opened = open_qualified_project(call, str(project_path), open_evidence, persist)
            require(type(opened.get("processId")) is int and opened["processId"] == started_result["processId"], "Inspection copy opened in another process")
            attempts = len(open_evidence["projectOpenRetry"]["attempts"])
            inventory = call("project_inventory", {"maxItems": 2000})
            selector = fixture_plc_selection(inventory, str(project_path), started_result["processId"])
            actual = call("technology_read", {"technologyName": NAME, "maxItems": 2000, "exportXml": True, **selector})
            require(actual.get("project", {}).get("path") == str(project_path) and actual.get("processId") == started_result["processId"]
                    and actual.get("plcPath") == selector["plcPath"], "Reopened encoder read has another owner")
            require(actual["completeness"]["scopeComplete"] and not actual["warnings"], 'Native evidence requirement failed: actual["completeness"]["scopeComplete"] and not actual["warnings"]')
            actual["xml"] = decode_native_artifact(actual, call("artifact_read", {"path": actual["artifact"]["path"]}))
            capture_endpoint(call, actual, inventory, profile)
            software_peers.capture(call, actual, selector)
            software_peers.compare(expected, actual)
            hardware_peers.capture(call, actual)
            hardware_peers.compare(expected, actual)
            parameter_read = call("engineering_read", {"selectors": {"scope": "technology", "name": NAME, "maxItems": 2000, **selector}})
            require(parameter_read.get("project", {}).get("path") == str(project_path) and type(parameter_read.get("processId")) is int and
                    parameter_read["processId"] == started_result["processId"] and all(isinstance(row.get("path"), str) and
                    row["path"].startswith(selector["plcPath"] + "/technology/") for row in parameter_read.get("technologyObjects", [])),
                    "Reopened parameter read identifies another owner")
            observed_parameters = parameter_map(parameter_read)
            require(json.dumps(observed_parameters, sort_keys=True) == json.dumps(expected_parameters, sort_keys=True), 'Native evidence requirement failed: json.dumps(observed_parameters, sort_keys=True) == json.dumps(expected_parameters, sort_keys=True)')
            require(same(endpoint(actual, profile), endpoint(expected, profile)), 'Native evidence requirement failed: connection_attributes(actual, profile) == connection_attributes(expected, profile)')
            require(native_payload(actual["xml"]) == native_payload(expected["xml"]), 'Native evidence requirement failed: native_payload(actual["xml"]) == native_payload(expected["xml"])')
            hardware = actual['endpointEvidence']['hardware']
            module_path = hardware[0]['items'][0]['path']
            report["copies"][label] = {"passed": True, "sourceProjectPath": path, "projectPath": str(project_path), "processId": started_result["processId"],
                "openAttempts": attempts, "nativeInventory": inventory, "selector": selector, "nativeReadback": actual, "parameterCount": len(observed_parameters), "exactParameters": True,
                "expectedPayloadSha256": native_payload_hash(expected["xml"]), "actualPayloadSha256": native_payload_hash(actual["xml"]),
                "exactHardwareConnections": True, "hardwarePath": module_path, "hardwareInspection": hardware}
            session.close()
        report["qualificationChecksPassed"] = True
    except BaseException as error:
        report["failure"] = str(error)
        raise
    finally:
        finish_session(session, report, persist, after_cleanup=lambda: copy_admission.finish_copies(admission, report['inspectionCopies']))
        print("Report: " + str(args.report), flush=True)


if __name__ == "__main__":
    main()
