"""Actual offline Host acquisition/export with independent original-chain and refusal controls."""
import argparse
import base64
import copy
import hashlib
import json
from decimal import Decimal
from pathlib import Path

from jsonschema import Draft202012Validator, ValidationError
from referencing import Registry, Resource
from smoke_mcp import Client, ROOT


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def validators():
    schemas = [s for p in (ROOT / "contracts").glob("*.json")
               if isinstance(s := json.loads(p.read_text(encoding="utf-8")), dict) and "$schema" in s and "$id" in s]
    registry = Registry().with_resources((s["$id"], Resource.from_contents(s)) for s in schemas)
    return {key: Draft202012Validator({"$ref": "https://tia-mcp.local/contracts/" + target}, registry=registry)
            for key, target in {
                "acquisition": "diagnostic-runtime-acquisition.v1.schema.json",
                "input": "diagnostic-runtime-acquisition.v1.schema.json#/$defs/input",
                "receipt": "diagnostic-runtime-acquisition.v1.schema.json#/$defs/receipt",
                "export": "diagnostic-runtime-acquisition-export.v1.schema.json",
                "exportInput": "diagnostic-runtime-acquisition-export.v1.schema.json#/$defs/input"}.items()}


def original_chain(document, root):
    """Independently decode exact raw files; never trust producer's decoded summary."""
    source = document["captureSource"]
    _, capture_id, segments, expected = document["snapshotRef"].split(":")
    originals = source["originalFiles"]
    assert len(originals) == int(segments) + 1
    decoded, raw_rows, total = [], [], 0
    previous = None
    for index, original in enumerate(originals):
        raw = base64.b64decode(original["base64"], validate=True)
        relative = f"runtime-captures/{capture_id}/" + ("plan.json" if index == 0 else f"{index:06}.segment")
        assert original["relativePath"] == relative
        assert raw == (root / relative).read_bytes()
        assert sha(raw) == original["sha256"] and len(raw) == original["bytes"]
        total += len(raw)
        parsed = json.loads(raw)
        if index:
            assert parsed["index"] == index and parsed["previousSha256"] == previous
            raw_rows.extend(parsed["rows"])
        decoded.append(parsed)
        previous = sha(raw)
    assert previous == expected == source["chainSha256"]
    assert originals[0]["sha256"] == source["planSha256"]
    assert total == source["originalByteCount"] <= 3 * 1024 * 1024
    assert decoded[0] == source["plan"] and raw_rows == source["rows"]
    if int(segments):
        assert decoded[-1]["progress"] == source["recordedProgress"]
    assert source["rowsTruncated"] is False and source["currentHeadConsulted"] is False
    assert len(raw_rows) == source["recordedProgress"]["rows"]
    for sequence, row in enumerate(raw_rows):
        assert row["sequence"] == sequence
        assert row["scheduledMilliseconds"] == row["cycle"] * source["plan"]["request"]["intervalMilliseconds"]
    return decoded[0], raw_rows


def fixture_observations(document, root):
    plan, rows = original_chain(document, root)
    assert len(rows) == 15 and sum(r["kind"] == "gap" for r in rows) == 1
    mapping = {s["selectionId"]: s["signalId"] for s in plan["association"]["signals"]}
    assert set(mapping.values()) == {"enable", "step"}
    by_cycle, host_errors, provider_errors, unsupported = {}, 0, 0, 0
    for row in rows:
        values = by_cycle.setdefault(row["cycle"], {})
        if row["kind"] == "gap":
            assert row["skippedPolls"] == 2
            continue
        signal = mapping[row["selectionId"]]
        value = None
        if row["errorCode"]:
            host_errors += 1
        else:
            result = json.loads(row["readResultJson"], parse_float=Decimal)
            assert "requestStartedUtc" in result and "responseReceivedUtc" in result
            assert result["selection"] == next(s["selection"] for s in plan["signals"] if s["selectionId"] == row["selectionId"])
            if result["status"] != "read":
                provider_errors += 1
            else:
                candidate = result["value"]
                valid = type(candidate) is bool if signal == "enable" else (
                    type(candidate) in (int, Decimal) and 0 <= candidate <= 65535 and candidate == int(candidate))
                if valid:
                    value = candidate
                else:
                    unsupported += 1
        values[signal] = value
    matched = contradicted = unknown = 0
    for values in by_cycle.values():
        if values.get("enable") is False:
            continue
        if values.get("enable") is None or values.get("step") is None:
            unknown += 1
        elif values["step"] == 65535:
            matched += 1
        else:
            contradicted += 1
    assert (matched, contradicted, unknown) == (1, 1, 4)
    assert (host_errors, provider_errors, unsupported) == (1, 1, 3)
    analysis = document["analysis"]
    pattern = next(h for h in analysis["hypotheses"] if h["id"] == "pattern")
    assert (pattern["matchedCycles"], pattern["contradictedCycles"], pattern["unknownCycles"]) == (matched, contradicted, unknown)
    quality = analysis["qualityAssessment"]
    assert quality["hostReadErrors"] == host_errors and quality["providerFailures"] == provider_errors
    assert quality["unsupportedValues"] == unsupported and quality["missingValues"] == 68
    assert quality["skippedPolls"] == 2 and quality["droppedReadResults"] == 1
    assert source_progress(document)["terminalReason"] == "byteLimit"
    assert json.loads(rows[3]["readResultJson"])["hostClockWentBackwards"] is True
    assert all(h["verdict"] == "unresolved" and h["causationProven"] is False for h in analysis["hypotheses"])
    return dict(rawRows=len(rows), cycles=len(by_cycle), matched=matched, contradicted=contradicted,
                unknown=unknown, hostErrors=host_errors, providerErrors=provider_errors, unsupported=unsupported)


def source_progress(document):
    return document["captureSource"]["recordedProgress"]


def run(host, output, samples_path):
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    fixture = json.loads(samples_path.read_text(encoding="utf-8"))["runtimeAcquisitionFixture"]
    assert fixture["authority"] == "syntheticManagedProducer" and fixture["nativeQualification"] is False
    # Only saved fixture files are installed; no native or network provider is launched.
    for relative, encoded in fixture["files"].items():
        path = (output / relative).resolve()
        assert path.is_relative_to(output)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(base64.b64decode(encoded, validate=True))
    original_files = {p: p.read_bytes() for p in output.rglob("*") if p.is_file()}
    report = dict(status="running", nativeQualification=False, authority=fixture["authority"],
                  steps=[], hostExits=[], harnessSha256=sha(Path(__file__).read_bytes()),
                  samplesSha256=sha(samples_path.read_bytes()),
                  managedBinaryHashes={name: sha((host.parent / name).read_bytes()) for name in ("TiaMcp.Host.dll", "TiaMcp.Core.dll")})
    schema = validators()
    client = None

    def check(value, label):
        assert value, label
        report["steps"].append(label)

    def start():
        nonlocal client
        client = Client(host.resolve(), 60, ["--artifacts", str(output), "--worker", str(output / "absent-native.exe"),
                                           "--sim-worker", str(output / "absent-simulator.exe")])
        client.request("initialize", dict(protocolVersion="2025-11-25", capabilities={},
                       clientInfo=dict(name="runtime-diagnostic-acquisition", version="1")))
        client.notify("notifications/initialized")

    def close():
        nonlocal client
        if client is not None:
            result = client.close()
            client = None
            report["hostExits"].append(result)
            check(result["hostExitCode"] == 0 and not result["forcedHostStop"]
                  and not any(row.get("started") for row in result["workerExits"]), "Clean Host exit without native workers")

    def reject(name, args, code):
        result = client.request("tools/call", dict(name=name, arguments=args))
        body = result.get("structuredContent") or json.loads("\n".join(c["text"] for c in result["content"] if c["type"] == "text"))
        check(result.get("isError") and body["error"]["code"] == code, name + " refuses " + code)

    def acquire(request=None, snapshot=None):
        args = dict(experimentId=fixture["experimentId"], snapshotRef=snapshot or fixture["snapshotRef"],
                    request=request or fixture["request"])
        schema["input"].validate(args)
        receipt = client.tool("diagnostic_experiment_acquire_runtime", args)
        schema["receipt"].validate(receipt)
        return receipt

    def export(acquisition_id, fmt):
        receipt = client.tool("diagnostic_runtime_acquisition_export", dict(acquisitionId=acquisition_id, format=fmt))
        schema["export"].validate(receipt)
        artifact, raw = receipt["artifact"], bytearray()
        while len(raw) < artifact["bytes"]:
            chunk = client.tool("artifact_read", dict(path=artifact["path"], offset=len(raw)))
            part = base64.b64decode(chunk["data"], validate=True)
            assert part
            raw.extend(part)
        check(sha(raw) == receipt["id"] and len(raw) == artifact["bytes"], "Downloaded exact acquisition " + fmt)
        return receipt, bytes(raw)

    def rehash(document):
        raw = json.dumps(document, ensure_ascii=False).encode()
        digest = sha(raw)
        (output / "trace-diagnostics" / (digest + ".json")).write_bytes(raw)
        return digest

    try:
        start()
        tools = {t["name"]: t for t in client.request("tools/list")["tools"]}
        for name in ("diagnostic_experiment_acquire_runtime", "diagnostic_runtime_acquisition_export"):
            check(tools[name]["annotations"]["readOnlyHint"] is True, name + " is local offline review")
        acquired = acquire()
        check(acquired["id"] == fixture["acquisitionId"], "Managed producer and actual Host derive identical acquisition bytes")
        report["independentObservationOracle"] = fixture_observations(acquired["acquisition"], output)
        check(True, "Independent original plan/segment decoder verifies complete rows, quality and sequential pattern counts")
        prefix = acquire(snapshot=fixture["prefixRef"])
        check(prefix["id"] == fixture["prefixAcquisitionId"] and source_progress(prefix["acquisition"])["state"] == "running",
              "Nonempty historical active prefix remains stable after later append and stop")
        prefix_rows = original_chain(prefix["acquisition"], output)[1]
        check(len(prefix_rows) == 3 and prefix["acquisition"]["captureSource"]["uncommittedDataUnknown"],
              "Incomplete sequential cycle and unknown later data stay explicit")
        hostile = copy.deepcopy(fixture["request"])
        hostile["reason"] = "Acquisition </pre><script>fetch('https://invalid.example/')</script> [link](https://invalid.example) ````\n# hostile"
        hostile_receipt = acquire(request=hostile)
        acquired_id = hostile_receipt["id"]
        exported = {}
        for fmt in ("json", "markdown", "html"):
            exported[fmt], raw = export(acquired_id, fmt)
            check(exported[fmt]["sourceArtifact"]["sha256"] == acquired_id, "Export retains exact original acquisition reference")
            if fmt == "json":
                check(raw == (output / "trace-diagnostics" / (acquired_id + ".json")).read_bytes(), "JSON export preserves exact original bytes")
                schema["acquisition"].validate(json.loads(raw))
                fixture_observations(json.loads(raw), output)
            if fmt == "html":
                check(b"<script>" not in raw and b"<img" not in raw, "Hostile text cannot create executable HTML")
        baseline_count = len(list((output / "trace-diagnostics").iterdir()))
        for field in ("programMatchVerified", "atomicAcrossSignals", "scanSynchronized", "signalMap", "quality"):
            bad = copy.deepcopy(fixture["request"])
            bad[field] = True
            reject("diagnostic_experiment_acquire_runtime", dict(experimentId=fixture["experimentId"],
                   snapshotRef=fixture["snapshotRef"], request=bad), "InvalidRequest")
            check(len(list((output / "trace-diagnostics").iterdir())) == baseline_count, "Invalid request publishes no analysis or acquisition")
        bad = copy.deepcopy(fixture["request"])
        bad["units"]["step"] = "ms"
        reject("diagnostic_experiment_acquire_runtime", dict(experimentId=fixture["experimentId"],
               snapshotRef=fixture["snapshotRef"], request=bad), "RuntimeCaptureAnalysisMismatch")
        case = acquired["acquisition"]["experiment"]["diagnosticCase"]
        replan = client.tool("diagnostic_replan", dict(planId=acquired["acquisition"]["experiment"]["diagnosticPlanId"],
                             request=dict(reviewId=case["reviewId"], blockPath=case["blockPath"],
                                          instance=dict(dbPath=case["selectedInstance"]["dbPath"], memberPath=["Conv1"]),
                                          reason="Explicitly select another saved instance; old runtime capture cannot follow it.",
                                          signalMappings=[dict(id=s["id"], storagePath=s["storagePath"],
                                                               memberPath=["Conv1" if m == "Conv2" else m for m in s["memberPath"]],
                                                               reason="Explicit role on the separately selected instance.") for s in case["signals"]])))
        other = client.tool("diagnostic_experiment_plan", dict(planId=replan["id"], request=acquired["acquisition"]["experiment"]["request"]))
        check(other["experiment"]["diagnosticCase"]["selectedInstance"]["memberPath"] == ["Conv1"],
              "Public replan selects exact different retained instance")
        count_before_refusal = len(list((output / "trace-diagnostics").iterdir()))
        reject("diagnostic_experiment_acquire_runtime", dict(experimentId=other["id"],
               snapshotRef=fixture["snapshotRef"], request=fixture["request"]), "RuntimeAcquisitionLineageMismatch")
        check(len(list((output / "trace-diagnostics").iterdir())) == count_before_refusal,
              "Wrong-instance acquisition publishes no derived child")
        forged = []
        mutations = [
            lambda d: d.update(atomicAcrossSignals=True),
            lambda d: d["analysis"]["hypotheses"][1].update(verdict="supportedPrediction"),
            lambda d: d["analysis"]["hypotheses"][1].update(matchedCycles=999),
            lambda d: d["captureSource"]["rows"][0].update(elapsedMilliseconds=999),
            lambda d: d["captureSource"]["rows"][4].update(errorCode=None),
            lambda d: d["captureSource"]["rows"][6].update(skippedPolls=1),
            lambda d: d["captureSource"]["plan"]["association"]["signals"][0].update(signalId="step"),
            lambda d: d["captureSource"]["originalFiles"][0].update(base64="e30="),
        ]
        for mutate in mutations:
            changed = copy.deepcopy(acquired["acquisition"])
            mutate(changed)
            refused = False
            try:
                schema["acquisition"].validate(changed)
                fixture_observations(changed, output)
            except (AssertionError, ValueError, ValidationError):
                # These are independent schema/byte/value assertions, never the production producer.
                refused = True
            check(refused, "Independent schema/original-chain oracle rejects altered acquisition")
            forged.append(rehash(changed))
        # Rehashed child and parent together still cannot invent observed matches.
        changed = copy.deepcopy(acquired["acquisition"])
        changed["analysis"]["hypotheses"][1]["matchedCycles"] = 999
        changed["analysisId"] = rehash(changed["analysis"])
        forged.append(rehash(changed))
        close()
        start()
        for forged_id in forged:
            reject("diagnostic_runtime_acquisition_export", dict(acquisitionId=forged_id, format="json"), "CaptureMismatch")
        for fmt in ("json", "markdown", "html"):
            again, _ = export(acquired_id, fmt)
            check(again["id"] == exported[fmt]["id"], "Restart preserves exact " + fmt + " export")
        check(acquire()["id"] == fixture["acquisitionId"], "Restart rederives identical acquisition and analysis")
        # Originals remain necessary even though the derived acquisition embeds their bytes.
        doc = acquired["acquisition"]
        segment_path = output / doc["captureSource"]["originalFiles"][-1]["relativePath"]
        raw_segment = segment_path.read_bytes()
        segment_path.write_bytes(raw_segment + b" ")
        reject("diagnostic_runtime_acquisition_export", dict(acquisitionId=acquired_id, format="html"), "RuntimeCaptureIntegrityFailure")
        segment_path.write_bytes(raw_segment)
        segment_path.unlink()
        reject("diagnostic_runtime_acquisition_export", dict(acquisitionId=acquired_id, format="json"), "RuntimeAcquisitionSourceUnavailable")
        segment_path.write_bytes(raw_segment)
        review_path = output / "project-reviews" / (doc["experiment"]["reviewId"] + ".json")
        raw_review = review_path.read_bytes()
        review_path.write_bytes(raw_review + b" ")
        reject("diagnostic_runtime_acquisition_export", dict(acquisitionId=acquired_id, format="json"), "ArtifactChanged")
        review_path.write_bytes(raw_review)
        check(all(path.read_bytes() == raw for path, raw in original_files.items()), "Every supplied original fixture byte remains unchanged")
        close()
        report.update(status="passed", exports=exported, acquisitionId=acquired_id, originalFileCount=len(original_files),
                      originalFiles={str(p.relative_to(output)): sha(raw) for p, raw in original_files.items()},
                      artifactIds=dict(acquisition=fixture["acquisitionId"], analysis=fixture["analysisId"], prefix=prefix["id"]))
    except Exception as error:
        report.update(status="failed", error=str(error))
        raise
    finally:
        close()
        (output / "result.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("PASS runtime diagnostic acquisition:", len(report["steps"]), "MCP/original-chain checks; no native qualification")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--samples", required=True, type=Path)
    args = parser.parse_args()
    run(args.host, args.output, args.samples)
