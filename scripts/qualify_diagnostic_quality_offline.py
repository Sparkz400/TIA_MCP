"""Exercise v2 original quality/timing through the actual offline Host and an independent oracle."""
import argparse
import base64
import copy
import hashlib
import json
from pathlib import Path

from jsonschema import Draft202012Validator
from referencing import Registry, Resource
from smoke_mcp import Client, ROOT


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def validators():
    schemas = [s for p in (ROOT / "contracts").glob("*.json")
               if isinstance(s := json.loads(p.read_text(encoding="utf-8")), dict) and "$schema" in s and "$id" in s]
    registry = Registry().with_resources((s["$id"], Resource.from_contents(s)) for s in schemas)
    return {s["$id"].split("/")[-1].removesuffix(".schema.json"): Draft202012Validator(s, registry=registry)
            for s in schemas}


def original_oracle(document, expected_raw):
    """Decode independent original bytes, never trust normalized counts or rehashed derivatives."""
    raw_report = base64.b64decode(document["sourceReportBase64"], validate=True)
    assert sha(raw_report) == document["sourceReportId"]
    assert json.loads(raw_report) == document["sourceReport"]
    for item in document["measurements"]:
        raw = base64.b64decode(item["original"]["base64"], validate=True)
        assert sha(raw) == item["original"]["sha256"] == item["document"]["inputArtifact"]["sha256"]
        assert len(raw) == item["original"]["bytes"]
        assert raw == expected_raw[item["id"]]
        original = json.loads(raw)
        assert original == item["document"]["measurement"]
        assert len(original["samples"]) == 12
        assert [s["readings"].get("final", {}).get("status") for s in original["samples"]] == [
            "good", "good", "bad", "uncertain", "good", "missing", "good", "good", None, "good", "good", "good"]
        actual_times = [next((t["value"] for t in s["readings"].get("final", {}).get("timestamps", [])
                             if t["clockId"] == "source"), "absent") for s in original["samples"]]
        assert actual_times == [-10, 0, 10, 30, 20, 20, 0, 10, "absent", 30, None, 50]
        assert original["samples"][11]["readings"]["final"]["error"] == "transport unavailable"
        assert original["capture"]["state"] == "truncated" and original["capture"]["reportedDroppedSamples"] is None
        series = next(s for s in item["document"]["qualityAssessment"]["series"]
                      if s["signalId"] == "final" and s["clockId"] == "source")
        for key, value in dict(usableValueReadings=7, unknownValueReadings=5, trueReadings=3, falseReadings=4,
                               clockResets=1, duplicateTimes=1, epochChanges=1, intervalGaps=1,
                               adjacentValueChangeCount=2).items():
            assert series[key] == value, key
        assert [(c["afterTime"], c["byTime"]) for c in series["adjacentValueChanges"]] == [(-10, 0), (0, 10)]
    for run in document["runs"]:
        for side in ("before", "after"):
            analysis = run[side + "Report"]
            h = analysis["hypotheses"][0]
            assert (h["matchedSamples"], h["unknownSamples"], h["verdict"]) == (4, 5, "unresolved")
            window = run["comparison"][side + "Window"]
            assert (window["clockId"], window["epoch"]) == ("source", "boot-0")
            assert (window["hypotheses"][0]["matchedSamples"], window["hypotheses"][0]["unknownSamples"]) == (2, 3)
            assert window["verdict"] == "unresolved" and not window["completenessVerified"]
    for flag in ("sourceAuthenticityVerified", "clockSynchronizationVerified", "atomicityVerified",
                 "behavioralAuthority", "causationProven", "bugFixProven", "nativeQualification", "nativeAccessAttempted"):
        assert document[flag] is False and document["sourceReport"][flag] is False


def run(host, output, samples_path):
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    samples = json.loads(samples_path.read_text(encoding="utf-8"))
    schema = validators()
    report = dict(status="running", nativeQualification=False, steps=[], hostExits=[],
                  harnessSha256=sha(Path(__file__).read_bytes()), samplesSha256=sha(samples_path.read_bytes()),
                  managedBinaryHashes={name: sha((host.parent / name).read_bytes())
                                       for name in ("TiaMcp.Host.dll", "TiaMcp.Core.dll")})
    client = None
    expected_raw = {}

    def check(value, label):
        assert value, label
        report["steps"].append(label)

    def validate(document):
        schema[document["schemaVersion"]].validate(document)
        check(True, document["schemaVersion"] + " validates")

    def start():
        nonlocal client
        client = Client(host.resolve(), 60, ["--artifacts", str(output), "--worker", str(output / "absent-native.exe"),
                                            "--sim-worker", str(output / "absent-simulator.exe")])
        client.request("initialize", dict(protocolVersion="2025-11-25", capabilities={},
                       clientInfo=dict(name="diagnostic-quality-qualification", version="2")))
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
        begin = client.tool("artifact_upload_begin", dict(fileName="input.json", expectedBytes=len(raw), sha256=sha(raw)))
        for offset in range(0, len(raw), begin["maxChunkBytes"]):
            client.tool("artifact_upload_chunk", dict(uploadId=begin["uploadId"], offset=offset,
                        data=base64.b64encode(raw[offset:offset + begin["maxChunkBytes"]]).decode()))
        return client.tool("artifact_upload_commit", dict(uploadId=begin["uploadId"]))

    def uploaded_call(name, args, raw):
        receipt = upload(raw)
        try:
            return client.tool(name, dict(args, uploadRef=receipt["artifactRef"]))
        finally:
            client.tool("artifact_upload_release", dict(uploadId=receipt["uploadId"]))

    def reject(name, args, code):
        result = client.request("tools/call", dict(name=name, arguments=args))
        body = result.get("structuredContent") or json.loads("\n".join(c["text"] for c in result["content"] if c["type"] == "text"))
        check(result.get("isError") and body["error"]["code"] == code, name + " refuses " + code + ": " + str(body))

    def download(receipt):
        artifact, data = receipt["artifact"], bytearray()
        while len(data) < artifact["bytes"]:
            chunk = client.tool("artifact_read", dict(path=artifact["path"], offset=len(data)))
            part = base64.b64decode(chunk["data"], validate=True)
            assert part, "Empty artifact chunk"
            data.extend(part)
        check(len(data) == artifact["bytes"] and sha(data) == receipt["id"], "Downloaded exact content-addressed artifact")
        return bytes(data)

    def rehash(document):
        raw = json.dumps(document, ensure_ascii=False).encode()
        digest = sha(raw)
        (output / "trace-diagnostics" / (digest + ".json")).write_bytes(raw)
        return digest

    def export(report_id, fmt, experiment=True):
        receipt = client.tool("diagnostic_experiment_report_export" if experiment else "diagnostic_report_export",
                              dict(reportId=report_id, format=fmt))
        validate(receipt)
        raw = download(receipt)
        if fmt == "json":
            validate(json.loads(raw))
        return receipt, raw

    try:
        start()
        tools = {row["name"]: row for row in client.request("tools/list")["tools"]}
        for name in ("trace_read", "trace_analyze", "trace_compare", "diagnostic_experiment_review",
                     "diagnostic_experiment_compare_runs", "diagnostic_experiment_report_export", "diagnostic_report_export"):
            check(tools[name]["annotations"]["readOnlyHint"] is True and "v2" in tools[name]["description"],
                  name + " exposes v2 read-only route")
        check(any("trace-measurement.v2" in item for item in client.tool("trace_capabilities", {})["measurementFormats"]),
              "Offline capability discovery lists explicit v2 quality route")
        review = uploaded_call("project_snapshot_import", {}, base64.b64decode(samples["reviewArtifactBase64"], validate=True))
        request = copy.deepcopy(samples["diagnosticRequest"])
        request["report"] += " </td><script>alert('quality')</script> [link](https://invalid.example) ```"
        case = client.tool("diagnostic_plan", dict(request=request))
        prepare = copy.deepcopy(samples["qualityMeasurementFixture"]["experimentRequest"])
        prepare["trace"]["reviewId"] = review["reviewId"]
        experiment = client.tool("diagnostic_experiment_plan", dict(planId=case["id"], request=prepare))
        trace_id = experiment["experiment"]["tracePlanId"]

        def measurement(label, mutate=None, target_trace=trace_id):
            capture = copy.deepcopy(samples["qualityMeasurementFixture"]["measurement"])
            capture.update(tracePlanId=target_trace, reviewId=review["reviewId"])
            capture["capture"]["reason"] = "Original separately authored run " + label
            if mutate:
                mutate(capture)
            raw = (" \r\n" + json.dumps(capture, ensure_ascii=False) + "\r\n").encode()
            receipt = uploaded_call("trace_read", dict(tracePlanId=target_trace), raw)
            imported = json.loads(download(receipt))
            validate(imported)
            check(imported["measurement"] == capture and Path(imported["inputArtifact"]["path"]).read_bytes() == raw,
                  "Upload released; exact independent original JSON retained")
            expected_raw[receipt["id"]] = raw
            return receipt

        measurements = [measurement(str(i)) for i in range(4)]
        analyses = [client.tool("trace_analyze", dict(measurementId=m["id"])) for m in measurements]
        for analysis in analyses:
            validate(analysis["report"])
        comparison_options = copy.deepcopy(samples["qualityMeasurementFixture"]["comparison"])
        comparison = client.tool("trace_compare", dict(beforeMeasurementId=measurements[0]["id"],
                                 afterMeasurementId=measurements[1]["id"], options=comparison_options))
        validate(comparison["comparison"])
        single_request = dict(afterExperimentId=experiment["id"], beforeMeasurementId=measurements[0]["id"],
                              afterMeasurementId=measurements[1]["id"], comparison=comparison_options)
        single = client.tool("diagnostic_experiment_review", dict(experimentId=experiment["id"], request=single_request))
        validate(single["review"])
        repeated_request = dict(reason="Two original observation pairs, no independence or fix claim.", pairs=[
            dict(id=f"pair-{i}", beforeExperimentId=experiment["id"], afterExperimentId=experiment["id"],
                 beforeMeasurementId=measurements[i*2]["id"], afterMeasurementId=measurements[i*2+1]["id"],
                 comparison=copy.deepcopy(comparison_options)) for i in range(2)])
        repeated = client.tool("diagnostic_experiment_compare_runs", dict(request=repeated_request))
        validate(repeated["report"])
        check(repeated["report"]["hypotheses"][0]["before"] == dict(runs=2, supportedPredictionRuns=0,
              contradictedPredictionRuns=0, unresolvedRuns=2, conditionObservedRuns=2), "Independent descriptive repeated count vector")
        exports = {}
        for source, is_experiment in ((analyses[0], False), (comparison, False), (single, True), (repeated, True)):
            for fmt in ("json", "markdown", "html"):
                receipt, raw = export(source["id"], fmt, is_experiment)
                if fmt == "json":
                    original_oracle(json.loads(raw), expected_raw)
                    check(True, "Independent original byte/status/time/quality/window oracle " + source["id"])
                if source is repeated:
                    exports[fmt] = receipt
        combined = json.loads(Path(exports["json"]["artifact"]["path"]).read_bytes())
        for mutate in (
                lambda d: d["measurements"][0]["document"]["measurement"]["samples"][2]["readings"]["final"].update(status="good"),
                lambda d: d["measurements"][0]["document"]["qualityAssessment"]["series"][0].update(usableValueReadings=999),
                lambda d: d["runs"][0]["beforeReport"]["hypotheses"][0].update(matchedSamples=999),
                lambda d: d["sourceReport"].update(behavioralAuthority=True)):
            invalid = copy.deepcopy(combined)
            mutate(invalid)
            # The series negative control must target the independently checked signal.
            if invalid["measurements"][0]["document"]["qualityAssessment"]["series"][0].get("usableValueReadings") == 999:
                next(s for s in invalid["measurements"][0]["document"]["qualityAssessment"]["series"]
                     if s["signalId"] == "final" and s["clockId"] == "source")["usableValueReadings"] = 999
            try:
                original_oracle(invalid, expected_raw)
            except AssertionError:
                check(True, "Independent oracle catches modified original/derived evidence")
            else:
                raise AssertionError("Independent oracle accepted a negative control")
        original_paths = {p: sha(p.read_bytes()) for p in output.rglob("*") if p.is_file()}
        close()
        start()
        for source, is_experiment in ((analyses[0], False), (comparison, False), (single, True), (repeated, True)):
            check(export(source["id"], "json", is_experiment)[0]["id"] ==
                  sha(export(source["id"], "json", is_experiment)[1]), "Fresh Host rederives exact report")
        for fmt in exports:
            check(export(repeated["id"], fmt)[0]["id"] == exports[fmt]["id"], "Restart stable " + fmt + " bytes")
        check(all(p.exists() and sha(p.read_bytes()) == digest for p, digest in original_paths.items()), "All originals unchanged after restart")

        # Explicit representable incompleteness remains usable; malformed/ambiguous input refuses before retention.
        empty = measurement("empty", lambda c: (c.update(samples=[]), c["capture"].update(state="incomplete")))
        empty_report = client.tool("trace_analyze", dict(measurementId=empty["id"]))["report"]
        check(empty_report["qualityAssessment"]["sampleGroups"] == 0, "Empty incomplete capture is representable")
        for mutate in (
                lambda c: c["samples"][0]["readings"]["final"].update(status="invented"),
                lambda c: c["samples"][0]["readings"]["final"]["timestamps"][0].update(clockId="unrecorded"),
                lambda c: c["samples"][1].update(sequence=0),
                lambda c: c["samples"][0]["readings"]["step"].update(value=1.5),
                lambda c: c["clocks"].append(copy.deepcopy(c["clocks"][0]))):
            capture = copy.deepcopy(samples["qualityMeasurementFixture"]["measurement"])
            capture.update(tracePlanId=trace_id, reviewId=review["reviewId"])
            mutate(capture)
            raw = json.dumps(capture).encode()
            uploaded = upload(raw)
            before = set((output / "trace-diagnostics").glob("*"))
            try:
                reject("trace_read", dict(tracePlanId=trace_id, uploadRef=uploaded["artifactRef"]), "MeasurementFormatUnsupported")
                check(before == set((output / "trace-diagnostics").glob("*")), "Malformed v2 creates no diagnostic artifacts")
            finally:
                client.tool("artifact_upload_release", dict(uploadId=uploaded["uploadId"]))
        for token, replacement in (('"schemaVersion": "trace-measurement.v2"', '"schemaVersion": "trace-measurement.v2", "schemaVersion": "trace-measurement.v2"'),
                                   ('"status": "good"', '"status": "good", "status": "bad"')):
            uploaded = upload(expected_raw[measurements[0]["id"]].replace(token.encode(), replacement.encode()))
            try:
                reject("trace_read", dict(tracePlanId=trace_id, uploadRef=uploaded["artifactRef"]), "MeasurementFormatUnsupported")
            finally:
                client.tool("artifact_upload_release", dict(uploadId=uploaded["uploadId"]))
        attested = measurement("attested", lambda c: (c["quality"].update(atomicAcrossSignals=True, scanSynchronized=True,
                              programMatchVerified=True), c["capture"].update(state="complete", reportedDroppedSamples=0)))
        attested_report = client.tool("trace_analyze", dict(measurementId=attested["id"]))["report"]
        check(not attested_report["behavioralAuthority"] and attested_report["hypotheses"][0]["verdict"] == "unresolved",
              "Caller quality attestation never grants verified behavior")
        no_interval = measurement("no interval", lambda c: c.update(expectedIntervals=[]))
        no_interval_report = client.tool("trace_analyze", dict(measurementId=no_interval["id"]))["report"]
        check(all(s["adjacentValueChangeCount"] == 0 for s in no_interval_report["qualityAssessment"]["series"]),
              "No interval evidence means no adjacent change inference")
        precise_raw = expected_raw[measurements[0]["id"]].replace(b'"value": -10', b'"value": 1e-28')
        precise = uploaded_call("trace_read", dict(tracePlanId=trace_id), precise_raw)
        reject("trace_compare", dict(beforeMeasurementId=precise["id"], afterMeasurementId=measurements[1]["id"],
                                    options=comparison_options), "MeasurementFormatUnsupported")
        second = dict(comparison_options, beforeEpoch="boot-1", windowEndSeconds=.05)
        second_window = client.tool("trace_compare", dict(beforeMeasurementId=measurements[0]["id"],
                                   afterMeasurementId=measurements[1]["id"], options=second))["comparison"]["beforeWindow"]
        s = next(s for s in second_window["series"] if s["signalId"] == "final")
        check(s["selectionExclusions"]["absentReading"] == s["selectionExclusions"]["unknownTime"] == 1,
              "Absent and untimed readings stay explicit when they cannot be assigned to a window")
        s = next(s for s in second_window["completeEpochAssessment"] if s["signalId"] == "final")
        check(s["untimedSelectedReadings"] == 1, "Complete epoch assessment retains an unknown timestamp")
        different_clocks = dict(comparison_options, beforeClockId="host", beforeEpoch="session")
        mixed_clock = client.tool("trace_compare", dict(beforeMeasurementId=measurements[0]["id"],
                                  afterMeasurementId=measurements[1]["id"], options=different_clocks))["comparison"]
        check(mixed_clock["beforeWindow"]["clockId"] == "host" and mixed_clock["afterWindow"]["clockId"] == "source"
              and not mixed_clock["clockSynchronizationVerified"], "Explicit different clock domains stay separate and unverified")
        per_pair_clocks = copy.deepcopy(repeated_request)
        per_pair_clocks["pairs"][1]["comparison"] = different_clocks
        clock_repeat = client.tool("diagnostic_experiment_compare_runs", dict(request=per_pair_clocks))["report"]
        check([mapping["beforeClockId"] for mapping in clock_repeat["clockEpochMappings"]] == ["source", "host"],
              "Repeated report retains each pair's explicit clock/epoch mapping")
        legacy_capture = copy.deepcopy(samples["trace-measurement.v1"])
        legacy_capture.update(tracePlanId=trace_id, reviewId=review["reviewId"])
        legacy = uploaded_call("trace_read", dict(tracePlanId=trace_id), json.dumps(legacy_capture).encode())
        reject("trace_compare", dict(beforeMeasurementId=measurements[0]["id"], afterMeasurementId=legacy["id"],
                                    options=comparison_options), "MeasurementVersionMismatch")
        for field in ("beforeClockId", "beforeEpoch"):
            invalid = dict(comparison_options)
            invalid[field] = "unknown"
            reject("trace_compare", dict(beforeMeasurementId=measurements[0]["id"], afterMeasurementId=measurements[1]["id"],
                                        options=invalid), "MeasurementClockMismatch")

        # Rehashing any derived layer cannot conceal disagreement with immutable originals.
        for document, mutate, tool, key in (
                (json.loads(Path(measurements[0]["artifact"]["path"]).read_bytes()),
                 lambda d: d["measurement"]["samples"][0]["readings"]["final"].update(value=False), "trace_analyze", "measurementId"),
                (analyses[0]["report"], lambda d: d["hypotheses"][0].update(matchedSamples=999), "diagnostic_report_export", "reportId"),
                (comparison["comparison"], lambda d: d["beforeWindow"].update(verdict="supportedPrediction"), "diagnostic_report_export", "reportId"),
                (single["review"], lambda d: d.update(atomicityVerified=True), "diagnostic_experiment_report_export", "reportId"),
                (repeated["report"], lambda d: d["hypotheses"][0]["before"].update(supportedPredictionRuns=2),
                 "diagnostic_experiment_report_export", "reportId")):
            forged = copy.deepcopy(document)
            mutate(forged)
            digest = rehash(forged)
            args = {key: digest}
            if key == "reportId":
                args["format"] = "html"
            reject(tool, args, "CaptureMismatch")
        imported = json.loads(Path(measurements[0]["artifact"]["path"]).read_bytes())
        for source_path in (Path(imported["inputArtifact"]["path"]), output / "project-reviews" / (review["reviewId"] + ".json")):
            original = source_path.read_bytes()
            try:
                source_path.write_bytes(original + b" ")
                reject("diagnostic_experiment_report_export", dict(reportId=repeated["id"], format="html"), "ArtifactChanged")
                source_path.unlink()
                reject("diagnostic_experiment_report_export", dict(reportId=repeated["id"], format="json"), "ArtifactUnavailable")
            finally:
                source_path.write_bytes(original)
        invalid = copy.deepcopy(repeated_request)
        invalid["pairs"][1]["beforeMeasurementId"] = measurements[0]["id"]
        before = set((output / "trace-diagnostics").glob("*"))
        reject("diagnostic_experiment_compare_runs", dict(request=invalid), "ExperimentRepeatedInput")
        check(before == set((output / "trace-diagnostics").glob("*")), "Invalid repeat creates no orphan children")
        oracle = json.loads((ROOT / "fixtures/trace-diagnostics/expected-mappings.json").read_text(encoding="utf-8"))
        next_case = client.tool("diagnostic_replan", dict(planId=case["id"], request=dict(reviewId=review["reviewId"],
            blockPath=case["plan"]["blockPath"], instance=dict(dbPath=oracle["selectedDb"], memberPath=["Conv1"]),
            reason="Explicit corresponding instance, unverified behavior.", signalMappings=[
                dict(id=s["id"], storagePath=s["storagePath"], memberPath=["Conv1" if p == "Conv2" else p for p in s["memberPath"]],
                     reason="Explicit corresponding selected role.") for s in case["plan"]["signals"]])))
        next_experiment = client.tool("diagnostic_experiment_plan", dict(planId=next_case["id"], request=prepare))
        mapped = copy.deepcopy(repeated_request)
        for i, pair in enumerate(mapped["pairs"]):
            pair.update(afterExperimentId=next_experiment["id"],
                        afterMeasurementId=measurement("mapped " + str(i), target_trace=next_experiment["experiment"]["tracePlanId"])["id"])
        reject("diagnostic_experiment_compare_runs", dict(request=mapped), "MeasurementLineageMismatch")
        for pair in mapped["pairs"]:
            pair["comparison"]["signalMap"] = [dict(before=s["id"], after=s["id"]) for s in oracle["signals"]]
        mapped_result = client.tool("diagnostic_experiment_compare_runs", dict(request=mapped))
        validate(mapped_result["report"])
        mapped_combined = json.loads(export(mapped_result["id"], "json")[1])
        original_oracle(mapped_combined, expected_raw)
        check(len(mapped_combined["experiments"]) == 2 and all(r["review"]["lineage"]["kind"] == "explicitDirectReplan"
              for r in mapped_combined["runs"]), "Every v2 pair retains explicit replan and instance mappings")
        report.update(exports=exports, artifactIds=dict(repeated=repeated["id"], single=single["id"],
                      analysis=analyses[0]["id"], comparison=comparison["id"], mappedRepeated=mapped_result["id"]),
                      originalInputHashes={key: sha(value) for key, value in expected_raw.items()}, status="passed")
    except Exception as error:
        report["status"], report["error"] = "failed", repr(error)
        raise
    finally:
        try:
            close()
        except Exception as error:
            report["status"], report["shutdownError"] = "failed", repr(error)
        report["checks"] = len(report["steps"])
        (output / "result.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    assert report["status"] == "passed", report
    print("PASS diagnostic quality MCP:", report["checks"], "checks; no native qualification")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=Path)
    parser.add_argument("--samples", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    run(args.host, args.output, args.samples)
