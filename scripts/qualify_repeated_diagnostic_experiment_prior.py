"""Export actual previous-producer v1 experiments without changing any original source byte."""
import argparse
import hashlib
import json
from pathlib import Path

from smoke_mcp import Client
from qualify_repeated_diagnostic_experiments_offline import validators


def run(host, source, output):
    source, output = source.resolve(), output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    original = {p: p.read_bytes() for p in source.rglob("*") if p.is_file()}
    producer_raw = original[source / "result.json"]
    producer = json.loads(producer_raw)
    assert producer["status"] == "passed"
    report_id = producer["artifactIds"]["review"]
    previous_review = json.loads(original[source / "trace-diagnostics" / (report_id + ".json")])
    assert previous_review["schemaVersion"] == "diagnostic-experiment-review.v1"
    binary_hashes = {name: hashlib.sha256((host.parent / name).read_bytes()).hexdigest()
                     for name in ("TiaMcp.Host.dll", "TiaMcp.Core.dll")}
    assert binary_hashes["TiaMcp.Core.dll"] != producer["managedBinaryHashes"]["TiaMcp.Core.dll"], "Select an actual different prior producer."
    report = dict(status="running", source=str(source), nativeQualification=False,
                  producerResultSha256=hashlib.sha256(producer_raw).hexdigest(), producerResult=producer,
                  originalFiles={str(p.relative_to(source)): hashlib.sha256(raw).hexdigest() for p, raw in original.items()},
                  managedBinaryHashes=binary_hashes, steps=[], hostExits=[])
    schema = validators()
    exported = {}
    try:
        for restart in range(2):
            client = Client(host.resolve(), 60, ["--artifacts", str(source), "--worker", str(output / "absent-native.exe"),
                                               "--sim-worker", str(output / "absent-simulator.exe")])
            try:
                client.request("initialize", dict(protocolVersion="2025-11-25", capabilities={},
                               clientInfo=dict(name="prior-combined-experiment", version="1")))
                client.notify("notifications/initialized")
                review_args = dict(experimentId=previous_review["beforeExperimentId"], request=previous_review["request"])
                current = client.tool("diagnostic_experiment_review", review_args)
                assert current["id"] == report_id and current["review"] == previous_review
                report["steps"].append("Actual old v1 review rederived with identical ID after restart " + str(restart))
                for fmt in ("json", "markdown", "html"):
                    receipt = client.tool("diagnostic_experiment_report_export", dict(reportId=report_id, format=fmt))
                    schema["export"].validate(receipt)
                    raw = Path(receipt["artifact"]["path"]).read_bytes()
                    assert hashlib.sha256(raw).hexdigest() == receipt["id"] and receipt["sourceArtifact"]["sha256"] == report_id
                    if fmt == "json":
                        combined = json.loads(raw)
                        schema["combined"].validate(combined)
                        assert combined["sourceReport"] == previous_review
                        assert len(combined["runs"]) == 1 and len(combined["experiments"]) == 1
                        case = combined["experiments"][0]["document"]["diagnosticCase"]
                        assert len(case["alarmBindings"]["bindings"]) == 3
                        assert case["testSuiteEvidence"]["finding"]["finding"]["warningCount"] == 2
                    if restart:
                        assert receipt["id"] == exported[fmt]["id"]
                    else:
                        exported[fmt] = receipt
                    report["steps"].append("Actual old v1 export retains exact source: " + fmt + " restart " + str(restart))
            finally:
                shutdown = client.close()
                report["hostExits"].append(shutdown)
                assert shutdown["hostExitCode"] == 0 and not shutdown["forcedHostStop"]
                assert not any(row.get("started") for row in shutdown["workerExits"])
        assert all(path.read_bytes() == raw for path, raw in original.items())
        report["steps"].append("All actual prior files remain byte-for-byte unchanged")
        report.update(status="passed", originalFileCount=len(original), exports=exported)
    except Exception as error:
        report["status"], report["error"] = "failed", str(error)
        raise
    finally:
        (output / "result.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("PASS prior diagnostic experiment:", len(report["steps"]), "checks;", len(original), "original files unchanged")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=Path)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    run(args.host, args.source, args.output)
