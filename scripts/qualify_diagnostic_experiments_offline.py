"""Exercise retained experiment composition through the real MCP Host, without native workers."""
import argparse
import base64
import copy
import hashlib
import json
from pathlib import Path

from jsonschema import Draft202012Validator, ValidationError
from referencing import Registry, Resource
from smoke_mcp import Client, ROOT


def validators():
    schemas = [s for p in (ROOT / "contracts").glob("*.json")
               if isinstance(s := json.loads(p.read_text(encoding="utf-8")), dict) and "$schema" in s and "$id" in s]
    registry = Registry().with_resources((s["$id"], Resource.from_contents(s)) for s in schemas)
    return {name: Draft202012Validator({"$ref": "https://tia-mcp.local/contracts/" + target}, registry=registry)
            for name, target in {
                "plan": "diagnostic-experiment.v1.schema.json",
                "planReceipt": "diagnostic-experiment.v1.schema.json#/$defs/receipt",
                "planInput": "diagnostic-experiment.v1.schema.json#/$defs/input",
                "review": "diagnostic-experiment-review.v1.schema.json",
                "reviewReceipt": "diagnostic-experiment-review.v1.schema.json#/$defs/receipt",
                "reviewInput": "diagnostic-experiment-review.v1.schema.json#/$defs/input"}.items()}


def run(host, output, samples_path):
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    samples = json.loads(samples_path.read_text(encoding="utf-8"))
    oracle = json.loads((ROOT / "fixtures/trace-diagnostics/expected-mappings.json").read_text(encoding="utf-8"))
    schema = validators()
    report = dict(status="running", nativeQualification=False, steps=[], hostExits=[],
                  harnessSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  managedBinaryHashes={name: hashlib.sha256((host.parent / name).read_bytes()).hexdigest()
                                       for name in ("TiaMcp.Host.dll", "TiaMcp.Core.dll")})
    client = None

    def check(value, label):
        assert value, label
        report["steps"].append(label)

    def start():
        nonlocal client
        client = Client(host.resolve(), 30, ["--artifacts", str(output), "--worker", str(output / "absent-native.exe"),
                                            "--sim-worker", str(output / "absent-simulator.exe")])
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                      "clientInfo": {"name": "diagnostic-experiment-qualification", "version": "1"}})
        client.notify("notifications/initialized")

    def close():
        nonlocal client
        if client is None:
            return
        result = client.close()
        client = None
        report["hostExits"].append(result)
        check(result["hostExitCode"] == 0 and not result["forcedHostStop"]
              and not any(row.get("started") for row in result["workerExits"]), "Host exited without native worker access")

    def upload(raw):
        begin = client.tool("artifact_upload_begin", dict(fileName="input.json", expectedBytes=len(raw),
                            sha256=hashlib.sha256(raw).hexdigest()))
        for offset in range(0, len(raw), begin["maxChunkBytes"]):
            client.tool("artifact_upload_chunk", dict(uploadId=begin["uploadId"], offset=offset,
                        data=base64.b64encode(raw[offset:offset + begin["maxChunkBytes"]]).decode()))
        return client.tool("artifact_upload_commit", dict(uploadId=begin["uploadId"]))

    def release(receipt):
        client.tool("artifact_upload_release", dict(uploadId=receipt["uploadId"]))

    def reject(name, args, code):
        result = client.request("tools/call", {"name": name, "arguments": args})
        body = result.get("structuredContent") or json.loads("\n".join(c["text"] for c in result["content"] if c["type"] == "text"))
        check(result.get("isError") and body["error"]["code"] == code, name + " refuses " + code)

    def download(receipt):
        artifact = receipt["artifact"]
        data = bytearray()
        while len(data) < artifact["bytes"]:
            chunk = client.tool("artifact_read", dict(path=artifact["path"], offset=len(data)))
            data.extend(base64.b64decode(chunk["data"], validate=True))
        check(hashlib.sha256(data).hexdigest() == receipt["id"], "MCP download retains exact hash")
        return json.loads(data)

    def import_measurement(measurement, trace_id):
        committed = upload(json.dumps(measurement, ensure_ascii=False).encode("utf-8"))
        result = client.tool("trace_read", dict(tracePlanId=trace_id, uploadRef=committed["artifactRef"]))
        release(committed)
        return result

    def rehash(document):
        raw = json.dumps(document, ensure_ascii=False).encode("utf-8")
        digest = hashlib.sha256(raw).hexdigest()
        (output / "trace-diagnostics" / (digest + ".json")).write_bytes(raw)
        return digest

    try:
        start()
        tools = {row["name"]: row for row in client.request("tools/list")["tools"]}
        for name in ("diagnostic_experiment_plan", "diagnostic_experiment_review"):
            check(tools[name]["annotations"]["readOnlyHint"] is True, name + " is offline read-only")
        raw = base64.b64decode(samples["alarmBindingReviewArtifactBase64"], validate=True)
        uploaded = upload(raw)
        review = client.tool("project_snapshot_import", dict(uploadRef=uploaded["artifactRef"]))
        release(uploaded)
        review_id = review["reviewId"]
        uploaded = upload(json.dumps(samples["alarmBindingTestSuitePacket"], ensure_ascii=False).encode("utf-8"))
        finding = client.tool("testsuite_result_import", dict(reviewId=review_id, uploadRef=uploaded["artifactRef"]))
        release(uploaded)
        request = copy.deepcopy(samples["alarmBindingRequest"])
        request["testSuiteFinding"] = dict(finding["receipt"], index=0)
        case = client.tool("diagnostic_plan", dict(request=request))
        prepare = copy.deepcopy(samples["experimentRequest"])
        prepare["trace"]["reviewId"] = review_id
        plan_args = dict(planId=case["id"], request=prepare)
        schema["planInput"].validate(plan_args)
        experiment = client.tool("diagnostic_experiment_plan", plan_args)
        schema["planReceipt"].validate(experiment)
        document = download(experiment)
        check(document == experiment["experiment"], "Experiment response matches retained document")
        check(document["diagnosticCase"] == case["plan"], "Composite preserves exact bug, source, FB, call and instance evidence")
        check(document["compositionGaps"] == [], "Selected alarm binding and Test Suite evidence have no composition omissions")
        check(len(document["diagnosticCase"]["alarmBindings"]["bindings"]) == 3
              and document["diagnosticCase"]["testSuiteEvidence"]["finding"]["finding"]["warningCount"] == 2,
              "Independent alarm and warning-count oracle")
        trace_id = document["tracePlanId"]
        trace = json.loads((output / "trace-diagnostics" / (trace_id + ".json")).read_text(encoding="utf-8"))
        by_id = {row["id"]: row for row in trace["signals"]}
        for signal in oracle["signals"]:
            actual = by_id[signal["id"]]
            check(actual["datatype"] == signal["type"] and actual["memberPath"] == signal["memberPath"]
                  and actual["storagePath"] == "/projects/Fixture/plc/CPU/blocks/" + signal["db"],
                  "Independent declaration and instance oracle: " + signal["id"])
        baseline = copy.deepcopy(samples["trace-measurement.v1"])
        baseline["tracePlanId"] = trace_id
        baseline["reviewId"] = review_id
        after = copy.deepcopy(baseline)
        for row in after["samples"]:
            row["values"]["final"], row["values"]["override"] = True, False
        before_import = import_measurement(baseline, trace_id)
        after_import = import_measurement(after, trace_id)
        comparison = copy.deepcopy(samples["comparisonOptions"])
        review_args = dict(experimentId=experiment["id"], request=dict(afterExperimentId=experiment["id"],
                          beforeMeasurementId=before_import["id"], afterMeasurementId=after_import["id"], comparison=comparison))
        schema["reviewInput"].validate(review_args)
        result = client.tool("diagnostic_experiment_review", review_args)
        schema["reviewReceipt"].validate(result)
        assessment = download(result)
        check(assessment["beforeHypotheses"][0]["matchedSamples"] == 3
              and assessment["beforeHypotheses"][0]["verdict"] == "supportedPrediction", "Independent baseline prediction count is three")
        check(assessment["afterHypotheses"][0]["matchedSamples"] == 0
              and assessment["afterHypotheses"][0]["verdict"] == "unresolved", "Unreproduced fault remains unresolved")
        for flag in ("bugFixProven", "causationProven", "nativeQualification", "sourceAuthenticityVerified",
                     "clockSynchronizationVerified", "nativeAccessAttempted"):
            check(assessment[flag] is False, "No promotion of " + flag)
            changed = copy.deepcopy(assessment)
            changed[flag] = True
            try:
                schema["review"].validate(changed)
            except ValidationError:
                report["steps"].append("Contract refuses " + flag)
            else:
                raise AssertionError("Contract accepted " + flag)
        # Prior individual APIs produce the exact same children, and remain available.
        check(client.tool("trace_prepare", dict(planId=case["id"], request=prepare["trace"]))["id"] == trace_id,
              "Existing trace preparation identity preserved")
        check(client.tool("diagnostic_watch_plan", dict(planId=case["id"], request=prepare["watch"]))["id"] == document["watchPlanId"],
              "Existing watch planning identity preserved")
        for key in ("beforeReportId", "afterReportId", "comparisonId"):
            exported = client.tool("diagnostic_report_export", dict(reportId=assessment[key], format="json"))
            check(exported["sourceReportId"] == assessment[key], "Existing report export supports child " + key)
        originals = {p: p.read_bytes() for p in (output / "trace-diagnostics").glob("*") if p.is_file()}
        close()
        start()
        check(client.tool("diagnostic_experiment_plan", plan_args)["id"] == experiment["id"], "Composition stable after actual Host restart")
        check(client.tool("diagnostic_experiment_review", review_args)["id"] == result["id"], "Review stable after actual Host restart and upload release")
        check(all(p.read_bytes() == data for p, data in originals.items()), "All prior diagnostic bytes unchanged after restart")
        invalid = copy.deepcopy(plan_args)
        invalid["request"]["trace"]["settings"]["trigger"]["signalId"] = "missing"
        before_paths = set((output / "trace-diagnostics").glob("*"))
        reject("diagnostic_experiment_plan", invalid, "InvalidRequest")
        invalid = copy.deepcopy(review_args)
        invalid["request"]["comparison"]["signalMaps"] = []
        reject("diagnostic_experiment_review", invalid, "InvalidRequest")
        check(before_paths == set((output / "trace-diagnostics").glob("*")), "Invalid composition/review publishes no child artifacts")
        for field, value in (("nativeExecutable", True), ("tracePlanId", "f" * 64)):
            changed = copy.deepcopy(document)
            changed[field] = value
            reject("diagnostic_experiment_review", dict(review_args, experimentId=rehash(changed)), "CaptureMismatch")
        changed = json.loads(Path(before_import["artifact"]["path"]).read_text(encoding="utf-8"))
        changed["measurement"]["samples"][2]["values"]["run"] = False
        invalid = copy.deepcopy(review_args)
        invalid["request"]["beforeMeasurementId"] = rehash(changed)
        reject("diagnostic_experiment_review", invalid, "CaptureMismatch")
        old_bytes = Path(review["artifact"]["path"]).read_bytes()
        Path(review["artifact"]["path"]).write_bytes(old_bytes + b" ")
        reject("diagnostic_experiment_review", review_args, "ArtifactChanged")
        Path(review["artifact"]["path"]).write_bytes(old_bytes)
        original_import = json.loads(Path(before_import["artifact"]["path"]).read_text(encoding="utf-8"))
        original_path = Path(original_import["inputArtifact"]["path"])
        original_bytes = original_path.read_bytes()
        original_path.unlink()
        try:
            reject("diagnostic_experiment_review", review_args, "ArtifactUnavailable")
        finally:
            original_path.write_bytes(original_bytes)
        changed_program = copy.deepcopy(after)
        changed_program["programIdentity"] = "another explicitly declared program"
        imported_program = import_measurement(changed_program, trace_id)
        invalid = copy.deepcopy(review_args)
        invalid["request"]["afterMeasurementId"] = imported_program["id"]
        reject("diagnostic_experiment_review", invalid, "ProgramRevisionMismatch")
        invalid["request"]["comparison"]["allowChangedSource"] = True
        check(client.tool("diagnostic_experiment_review", invalid)["review"]["programIdentityChanged"],
              "Explicit changed-program admission preserves both identities")
        # Explicitly move to the other captured instance; signal IDs alone cannot grant identity.
        replan_request = dict(reviewId=review_id, blockPath=case["plan"]["blockPath"],
                              instance=dict(dbPath=oracle["selectedDb"], memberPath=["Conv1"]),
                              reason="Compare a separately selected conveyor, without a fix claim.",
                              signalMappings=[dict(id=s["id"], storagePath=s["storagePath"],
                                  memberPath=["Conv1" if p == "Conv2" else p for p in s["memberPath"]],
                                  reason="Explicit corresponding role on Conveyor 1.") for s in case["plan"]["signals"]])
        next_case = client.tool("diagnostic_replan", dict(planId=case["id"], request=replan_request))
        next_experiment = client.tool("diagnostic_experiment_plan", dict(planId=next_case["id"], request=prepare))
        next_measurement = copy.deepcopy(after)
        next_measurement["tracePlanId"] = next_experiment["experiment"]["tracePlanId"]
        next_import = import_measurement(next_measurement, next_measurement["tracePlanId"])
        next_args = copy.deepcopy(review_args)
        next_args["request"].update(afterExperimentId=next_experiment["id"], afterMeasurementId=next_import["id"])
        reject("diagnostic_experiment_review", next_args, "MeasurementLineageMismatch")
        next_args["request"]["comparison"]["signalMap"] = [dict(before=s["id"], after=s["id"]) for s in oracle["signals"]]
        next_result = client.tool("diagnostic_experiment_review", next_args)
        schema["reviewReceipt"].validate(next_result)
        check(next_result["review"]["lineage"]["kind"] == "explicitDirectReplan", "Exact second-instance replan retained")
        check("alarmEvidence" not in next_experiment["experiment"]["diagnosticCase"]
              and "testSuiteEvidence" not in next_experiment["experiment"]["diagnosticCase"],
              "Replan does not inherit evidence from prior target")
        unrelated_request = copy.deepcopy(request)
        unrelated_request["report"] = "Similar report without an explicit replan."
        unrelated = client.tool("diagnostic_plan", dict(request=unrelated_request))
        unrelated_experiment = client.tool("diagnostic_experiment_plan", dict(planId=unrelated["id"], request=prepare))
        invalid = copy.deepcopy(review_args)
        invalid["request"]["afterExperimentId"] = unrelated_experiment["id"]
        reject("diagnostic_experiment_review", invalid, "ExperimentLineageMismatch")
        report["artifactIds"] = dict(case=case["id"], experiment=experiment["id"], before=before_import["id"],
                                    after=after_import["id"], review=result["id"], replannedExperiment=next_experiment["id"])
        report["status"] = "passed"
    except Exception as error:
        report["status"], report["error"] = "failed", str(error)
        raise
    finally:
        try:
            close()
        except Exception as error:
            report["status"], report["shutdownError"] = "failed", str(error)
        (output / "result.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    assert report["status"] == "passed", report
    print("PASS diagnostic experiments:", len(report["steps"]), "MCP checks; no native qualification")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--samples", required=True, type=Path)
    args = parser.parse_args()
    run(args.host, args.output, args.samples)
