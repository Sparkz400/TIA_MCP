"""Qualify repeated diagnostic windows and combined exports through the actual offline Host."""
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
    names = {"repeat": "diagnostic-experiment-repeat.v1.schema.json",
             "input": "diagnostic-experiment-repeat.v1.schema.json#/$defs/input",
             "receipt": "diagnostic-experiment-repeat.v1.schema.json#/$defs/receipt",
             "combined": "diagnostic-experiment-report.v1.schema.json",
             "export": "diagnostic-experiment-report-export.v1.schema.json",
             "exportInput": "diagnostic-experiment-report-export.v1.schema.json#/$defs/input"}
    return {key: Draft202012Validator({"$ref": "https://tia-mcp.local/contracts/" + target}, registry=registry)
            for key, target in names.items()}


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
        client = Client(host.resolve(), 60, ["--artifacts", str(output), "--worker", str(output / "absent-native.exe"),
                                            "--sim-worker", str(output / "absent-simulator.exe")])
        client.request("initialize", dict(protocolVersion="2025-11-25", capabilities={},
                       clientInfo=dict(name="repeated-diagnostic-qualification", version="1")))
        client.notify("notifications/initialized")

    def close():
        nonlocal client
        if client is None:
            return
        result = client.close()
        client = None
        report["hostExits"].append(result)
        check(result["hostExitCode"] == 0 and not result["forcedHostStop"]
              and not any(row.get("started") for row in result["workerExits"]), "Clean Host exit without native workers")

    def upload(raw):
        begin = client.tool("artifact_upload_begin", dict(fileName="input.json", expectedBytes=len(raw),
                            sha256=hashlib.sha256(raw).hexdigest()))
        for offset in range(0, len(raw), begin["maxChunkBytes"]):
            client.tool("artifact_upload_chunk", dict(uploadId=begin["uploadId"], offset=offset,
                        data=base64.b64encode(raw[offset:offset + begin["maxChunkBytes"]]).decode()))
        return client.tool("artifact_upload_commit", dict(uploadId=begin["uploadId"]))

    def uploaded_call(name, args, raw):
        receipt = upload(raw)
        result = client.tool(name, dict(args, uploadRef=receipt["artifactRef"]))
        client.tool("artifact_upload_release", dict(uploadId=receipt["uploadId"]))
        return result

    def reject(name, args, code):
        result = client.request("tools/call", dict(name=name, arguments=args))
        body = result.get("structuredContent") or json.loads("\n".join(c["text"] for c in result["content"] if c["type"] == "text"))
        check(result.get("isError") and body["error"]["code"] == code, name + " refuses " + code)

    def download(receipt):
        artifact, data = receipt["artifact"], bytearray()
        while len(data) < artifact["bytes"]:
            chunk = client.tool("artifact_read", dict(path=artifact["path"], offset=len(data)))
            part = base64.b64decode(chunk["data"], validate=True)
            assert part, "Empty artifact chunk"
            data.extend(part)
        check(len(data) == artifact["bytes"] and hashlib.sha256(data).hexdigest() == receipt["id"],
              "Downloaded exact content-addressed artifact")
        return bytes(data)

    def rehash(document):
        raw = json.dumps(document, ensure_ascii=False).encode("utf-8")
        digest = hashlib.sha256(raw).hexdigest()
        (output / "trace-diagnostics" / (digest + ".json")).write_bytes(raw)
        return digest

    def compare(request):
        args = dict(request=request)
        schema["input"].validate(args)
        receipt = client.tool("diagnostic_experiment_compare_runs", args)
        schema["receipt"].validate(receipt)
        return receipt

    def export(report_id, fmt):
        receipt = client.tool("diagnostic_experiment_report_export", dict(reportId=report_id, format=fmt))
        schema["export"].validate(receipt)
        raw = download(receipt)
        if fmt == "json":
            schema["combined"].validate(json.loads(raw))
        return receipt, raw

    try:
        start()
        tools = {row["name"]: row for row in client.request("tools/list")["tools"]}
        for name in ("diagnostic_experiment_compare_runs", "diagnostic_experiment_report_export"):
            check(tools[name]["annotations"]["readOnlyHint"] is True, name + " is offline read-only")
        review = uploaded_call("project_snapshot_import", {}, base64.b64decode(samples["alarmBindingReviewArtifactBase64"], validate=True))
        finding = uploaded_call("testsuite_result_import", dict(reviewId=review["reviewId"]),
                                json.dumps(samples["alarmBindingTestSuitePacket"]).encode())
        request = copy.deepcopy(samples["alarmBindingRequest"])
        request["testSuiteFinding"] = dict(finding["receipt"], index=0)
        request["report"] += " </td><script>alert('offline')</script> [link](https://invalid.example) ```"
        case = client.tool("diagnostic_plan", dict(request=request))
        prepare = copy.deepcopy(samples["experimentRequest"])
        prepare["trace"]["reviewId"] = review["reviewId"]
        experiment = client.tool("diagnostic_experiment_plan", dict(planId=case["id"], request=prepare))
        trace_id = experiment["experiment"]["tracePlanId"]

        def measurement(label, mutate=None, target_trace=trace_id):
            capture = copy.deepcopy(samples["trace-measurement.v1"])
            capture.update(tracePlanId=target_trace, reviewId=review["reviewId"], clock="Authored fixture run " + label)
            if mutate:
                mutate(capture)
            return uploaded_call("trace_read", dict(tracePlanId=target_trace), json.dumps(capture).encode())

        def no_condition(c):
            for row in c["samples"]:
                row["values"]["final"] = True

        def contradiction(c):
            for index in (2, 3, 4):
                c["samples"][index]["values"]["run"] = False

        measurements = [measurement("before 1"), measurement("after 1", no_condition),
                        measurement("before 2", contradiction),
                        measurement("after 2", lambda c: c["samples"][0]["values"].update(feedback=None))]
        repeat_request = dict(reason="Repeated separately authored observations; no fix claim.", pairs=[
            dict(id=f"pair-{i + 1}", beforeExperimentId=experiment["id"], afterExperimentId=experiment["id"],
                 beforeMeasurementId=measurements[i * 2]["id"], afterMeasurementId=measurements[i * 2 + 1]["id"],
                 comparison=copy.deepcopy(samples["comparisonOptions"])) for i in range(2)])
        repeated = compare(repeat_request)
        check(json.loads(download(repeated)) == repeated["report"], "Retained repeat equals returned report")
        counts = repeated["report"]["hypotheses"][0]
        check(counts["before"] == dict(runs=2, supportedPredictionRuns=1, contradictedPredictionRuns=1,
                                      unresolvedRuns=0, conditionObservedRuns=2), "Independent before run-count oracle")
        check(counts["after"] == dict(runs=2, supportedPredictionRuns=0, contradictedPredictionRuns=0,
                                     unresolvedRuns=2, conditionObservedRuns=1), "Missing and absent-condition runs remain unresolved")
        check(repeated["report"]["runs"][1]["afterWindow"]["hypotheses"][0]["matchedSamples"] == 3,
              "Poor-quality samples retain three observed matches without a verdict")
        exports = {}
        for fmt in ("json", "markdown", "html"):
            receipt, raw = export(repeated["id"], fmt)
            exports[fmt] = receipt
            check(receipt["sourceArtifact"]["sha256"] == repeated["id"], "Export keeps exact original report reference")
            if fmt == "json":
                combined = json.loads(raw)
                check(combined["sourceReport"] == repeated["report"], "Combined export embeds original report unchanged")
                check(combined["experiments"][0]["document"]["diagnosticCase"] == case["plan"], "Combined source retains exact alarm and Test Suite case")
            elif fmt == "html":
                text = raw.decode()
                check("<script" not in text.lower() and "<a " not in text.lower()
                      and "&lt;script&gt;" in text, "Self-contained HTML escapes hostile saved report")
            else:
                text = raw.decode().split("Complete combined JSON", 1)[0]
                check("[link](https://invalid.example)" not in text and "\\[link\\]" in text, "Markdown escapes saved links outside JSON fence")
        for flag in ("independentRunsVerified", "sourceAuthenticityVerified", "clockSynchronizationVerified",
                     "causationProven", "bugFixProven", "nativeQualification", "nativeAccessAttempted"):
            check(repeated["report"][flag] is False, "No promotion of " + flag)
            forged = copy.deepcopy(repeated["report"])
            forged[flag] = True
            try:
                schema["repeat"].validate(forged)
            except ValidationError:
                check(True, "Schema refuses " + flag)
            else:
                raise AssertionError("Schema accepted " + flag)
        original_files = {p: p.read_bytes() for p in output.rglob("*") if p.is_file() and p.name != "result.json"}
        close()
        start()
        check(compare(repeat_request)["id"] == repeated["id"], "Repeated comparison stable after Host restart and upload release")
        for fmt in exports:
            check(export(repeated["id"], fmt)[0]["id"] == exports[fmt]["id"], "Combined " + fmt + " stable after Host restart")
        check(all(p.read_bytes() == raw for p, raw in original_files.items()), "All original bytes unchanged after rederivation")
        defects = {
            "non-atomic": lambda c: c["quality"].update(atomicAcrossSignals=False),
            "unknown dropouts": lambda c: c["quality"].update(dropoutsKnown=False),
            "unknown program": lambda c: c["quality"].update(programMatchVerified=False),
            "reported dropout": lambda c: c["quality"].update(reportedDroppedSamples=1),
            "missing interval": lambda c: c["quality"].pop("expectedInterval"),
            "sampling gap": lambda c: c["samples"].pop(1),
            "partial window": lambda c: c["samples"].pop(0)}
        for label, mutate in defects.items():
            changed = copy.deepcopy(repeat_request)
            changed["pairs"][1]["afterMeasurementId"] = measurement(label, mutate)["id"]
            window = compare(changed)["report"]["runs"][1]["afterWindow"]
            check(not window["qualityGatePassed"] and window["qualityGateReasons"]
                  and window["hypotheses"][0]["verdict"] == "unresolved", label + " never becomes a supported verdict")
        outside = copy.deepcopy(repeat_request)
        for pair in outside["pairs"]:
            pair["comparison"].update(beforeAnchorSeconds=0, afterAnchorSeconds=0, windowStartSeconds=0, windowEndSeconds=.01)
        window = compare(outside)["report"]["runs"][0]["beforeWindow"]
        check(window["samplesInWindow"] == 2 and window["hypotheses"][0]["matchedSamples"] == 0
              and window["hypotheses"][0]["verdict"] == "unresolved", "Whole-capture event outside window does not count")
        for pair in outside["pairs"]:
            pair["comparison"].update(windowStartSeconds=1, windowEndSeconds=2)
        check(compare(outside)["report"]["runs"][0]["beforeWindow"]["coverage"] == "noRecordedSamples", "Empty window explicit")
        invalids = [
            ("InvalidRequest", lambda r: r.update(ignored=True)),
            ("InvalidRequest", lambda r: r["pairs"].pop()),
            ("InvalidRequest", lambda r: r["pairs"][1].update(id="pair-1")),
            ("ExperimentRepeatedInput", lambda r: r["pairs"][1].update(beforeMeasurementId=measurements[0]["id"])),
            ("ExperimentWindowMismatch", lambda r: r["pairs"][1]["comparison"].update(windowEndSeconds=.04)),
            ("MeasurementLineageMismatch", lambda r: r["pairs"][1]["comparison"].update(signalMap=[dict(before="final", after="final")]))]
        for code, mutate in invalids:
            changed = copy.deepcopy(repeat_request)
            mutate(changed)
            before_paths = set((output / "trace-diagnostics").glob("*"))
            reject("diagnostic_experiment_compare_runs", dict(request=changed), code)
            check(before_paths == set((output / "trace-diagnostics").glob("*")), "Invalid repeat creates no child artifacts")
        for mutate in (lambda r: r["hypotheses"][0]["after"].update(supportedPredictionRuns=2),
                       lambda r: r["runs"][0]["beforeWindow"].update(samplesInWindow=999),
                       lambda r: r.update(bugFixProven=True)):
            forged = copy.deepcopy(repeated["report"])
            mutate(forged)
            digest = rehash(forged)
            for fmt in exports:
                reject("diagnostic_experiment_report_export", dict(reportId=digest, format=fmt), "CaptureMismatch")
        before_import = json.loads(Path(measurements[0]["artifact"]["path"]).read_bytes())
        source_path = Path(before_import["inputArtifact"]["path"])
        original = source_path.read_bytes()
        try:
            source_path.write_bytes(original + b" ")
            reject("diagnostic_experiment_report_export", dict(reportId=repeated["id"], format="html"), "ArtifactChanged")
            source_path.unlink()
            reject("diagnostic_experiment_report_export", dict(reportId=repeated["id"], format="json"), "ArtifactUnavailable")
        finally:
            source_path.write_bytes(original)
        # Explicit instance mapping is required on every run, even when IDs and datatypes match.
        next_case = client.tool("diagnostic_replan", dict(planId=case["id"], request=dict(reviewId=review["reviewId"],
            blockPath=case["plan"]["blockPath"], instance=dict(dbPath=oracle["selectedDb"], memberPath=["Conv1"]),
            reason="Explicit corresponding instance; no fix claim.", signalMappings=[
                dict(id=s["id"], storagePath=s["storagePath"], memberPath=["Conv1" if p == "Conv2" else p for p in s["memberPath"]],
                     reason="Explicit corresponding role.") for s in case["plan"]["signals"]])))
        next_experiment = client.tool("diagnostic_experiment_plan", dict(planId=next_case["id"], request=prepare))
        mapped = copy.deepcopy(repeat_request)
        for i, pair in enumerate(mapped["pairs"]):
            pair.update(afterExperimentId=next_experiment["id"],
                        afterMeasurementId=measurement("mapped " + str(i), target_trace=next_experiment["experiment"]["tracePlanId"])["id"])
        reject("diagnostic_experiment_compare_runs", dict(request=mapped), "MeasurementLineageMismatch")
        for pair in mapped["pairs"]:
            pair["comparison"]["signalMap"] = [dict(before=s["id"], after=s["id"]) for s in oracle["signals"]]
        mapped_result = compare(mapped)
        mapped_combined = json.loads(export(mapped_result["id"], "json")[1])
        check(len(mapped_combined["experiments"]) == 2 and all(r["review"]["lineage"]["kind"] == "explicitDirectReplan"
              for r in mapped_combined["runs"]), "Combined report retains explicit second-instance lineage for every pair")
        single_id = repeated["report"]["runs"][0]["reviewId"]
        single = json.loads(export(single_id, "json")[1])
        check(single["sourceSchemaVersion"] == "diagnostic-experiment-review.v1" and len(single["runs"]) == 1, "Single-pair v1 report supported")
        report["artifactIds"] = dict(repeated=repeated["id"], mappedRepeated=mapped_result["id"], singleReview=single_id)
        report["exports"] = exports
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
    print("PASS repeated diagnostic experiments:", len(report["steps"]), "MCP checks; no native qualification")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--samples", required=True, type=Path)
    args = parser.parse_args()
    run(args.host, args.output, args.samples)
