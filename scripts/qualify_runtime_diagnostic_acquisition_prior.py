"""Actual previous-producer captures and experiment/repeat bytes through the current offline Host."""
import argparse
import json
from pathlib import Path

from smoke_mcp import Client
from qualify_runtime_diagnostic_acquisition_offline import original_chain, sha, validators


def run(host, runtime_source, experiment_source, output):
    runtime_source, experiment_source, output = runtime_source.resolve(), experiment_source.resolve(), output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    runtime_root = runtime_source / "store"
    runtime_producer = json.loads((runtime_source / "verification.json").read_text(encoding="utf-8"))
    experiment_producer = json.loads((experiment_source / "result.json").read_text(encoding="utf-8"))
    assert runtime_producer["completed"] and experiment_producer["status"] == "passed"
    current_hashes = {name: sha((host.parent / name).read_bytes()) for name in ("TiaMcp.Host.dll", "TiaMcp.Core.dll")}
    assert current_hashes["TiaMcp.Core.dll"] != runtime_producer["binaryHashes"]["TiaMcp.Core.dll"]
    assert current_hashes["TiaMcp.Core.dll"] != experiment_producer["managedBinaryHashes"]["TiaMcp.Core.dll"]
    roots = [runtime_root, experiment_source]
    original = {p: p.read_bytes() for root in roots for p in root.rglob("*") if p.is_file()}
    report = dict(status="running", nativeQualification=False, sourceRoots=[str(r) for r in roots],
                  producerReceipts={str(p): sha(p.read_bytes()) for p in (runtime_source / "verification.json", experiment_source / "result.json")},
                  producerBinaryHashes=[runtime_producer["binaryHashes"], experiment_producer["managedBinaryHashes"]],
                  managedBinaryHashes=current_hashes, steps=[], hostExits=[],
                  originalFiles={str(p): sha(raw) for p, raw in original.items()})
    schema = validators()

    def check(ok, label):
        assert ok, label
        report["steps"].append(label)

    def start(root):
        client = Client(host.resolve(), 60, ["--artifacts", str(root), "--worker", str(output / "absent-native.exe"),
                                            "--sim-worker", str(output / "absent-simulator.exe")])
        client.request("initialize", dict(protocolVersion="2025-11-25", capabilities={},
                       clientInfo=dict(name="prior-runtime-acquisition", version="1")))
        client.notify("notifications/initialized")
        return client

    def close(client):
        result = client.close()
        report["hostExits"].append(result)
        check(result["hostExitCode"] == 0 and not result["forcedHostStop"]
              and not any(row.get("started") for row in result["workerExits"]), "Prior producer closes cleanly without native workers")

    try:
        runtime_documents = {p.stem: json.loads(raw) for p, raw in original.items()
                             if p.parent == runtime_root / "trace-diagnostics" and p.suffix == ".json"}
        eligible = {key: d for key, d in runtime_documents.items()
                    if d.get("schemaVersion") == "runtime-capture-analysis.v1"
                    and d["caseId"] in runtime_documents and (runtime_root / "project-reviews" / (d["reviewId"] + ".json")).is_file()}
        assert eligible, "Need actual prior producer with original saved declaration evidence still retained."
        acquisitions, exports = {}, {}
        for restart in range(2):
            client = start(runtime_root)
            try:
                for report_id, old in eligible.items():
                    current = client.tool("runtime_capture_analyze", dict(snapshotRef=old["snapshotRef"], units=old["units"]))
                    check(current["id"] == report_id and current["report"] == old, "Actual previous runtime analysis rederived identically after restart " + str(restart))
                    case = runtime_documents[old["caseId"]]
                    external = sorted({s["storagePath"] for s in case["signals"] if s["storagePath"] != case["selectedInstance"]["dbPath"]})
                    trigger = next(s["id"] for s in case["signals"] if s["datatype"] == "Bool")
                    request = dict(reason="Attach an actual prior retained polling capture; no behavioral admission.",
                                   watch=dict(tableName="Prior runtime acquisition", tiaVersion=21,
                                              externalStorage=[dict(objectPath=p, reason="Exact prior external diagnostic signal.") for p in external]),
                                   trace=dict(backend="manual", family="ordinaryPlc", reviewId=case["reviewId"],
                                              settings=dict(task="OB1", sampleEvery=1, preSamples=20, postSamples=100,
                                                            rationale="Manual specification remains separate from the already recorded polling capture.",
                                                            trigger=dict(signalId=trigger, kind="risingEdge"))))
                    experiment = client.tool("diagnostic_experiment_plan", dict(planId=old["caseId"], request=request))
                    acquired = client.tool("diagnostic_experiment_acquire_runtime",
                                           dict(experimentId=experiment["id"], snapshotRef=old["snapshotRef"],
                                                request=dict(reason="Original previous-producer capture with retained source evidence.", units=old["units"])))
                    schema["receipt"].validate(acquired)
                    check(acquired["acquisition"]["analysisId"] == report_id, "New acquisition retains exact actual old analysis identity")
                    original_chain(acquired["acquisition"], runtime_root)
                    check(True, "Independent old producer plan/segments decode to complete retained rows")
                    if restart:
                        check(acquisitions[report_id] == acquired["id"], "Prior acquisition is deterministic across Host restart")
                    acquisitions[report_id] = acquired["id"]
                    for fmt in ("json", "markdown", "html"):
                        receipt = client.tool("diagnostic_runtime_acquisition_export", dict(acquisitionId=acquired["id"], format=fmt))
                        schema["export"].validate(receipt)
                        if restart:
                            check(exports[(report_id, fmt)] == receipt["id"], "Prior acquisition export stable after restart: " + fmt)
                        exports[(report_id, fmt)] = receipt["id"]
            finally:
                close(client)
        # Reuse exact old single/repeated requests and every old exported format: no regenerated prior fixtures.
        old_documents = {p.stem: json.loads(raw) for p, raw in original.items()
                         if p.parent == experiment_source / "trace-diagnostics" and p.suffix == ".json"}
        retained_ids = experiment_producer["artifactIds"]
        old_exports = experiment_producer["exports"]
        stable_exports = {}
        for restart in range(2):
            client = start(experiment_source)
            try:
                for name, report_id in retained_ids.items():
                    old = old_documents[report_id]
                    if old["schemaVersion"] == "diagnostic-experiment-repeat.v1":
                        result = client.tool("diagnostic_experiment_compare_runs", dict(request=old["request"]))
                        check(result["id"] == report_id, "Actual old repeated report remains byte-identical: " + name)
                    else:
                        result = client.tool("diagnostic_experiment_review",
                                             dict(experimentId=old["beforeExperimentId"], request=old["request"]))
                        check(result["id"] == report_id, "Actual old single review remains byte-identical")
                    for fmt in ("json", "markdown", "html"):
                        exported = client.tool("diagnostic_experiment_report_export", dict(reportId=report_id, format=fmt))
                        key = (name, fmt)
                        if restart:
                            check(stable_exports[key] == exported["id"], "Old experiment export stable across restart: " + name + "/" + fmt)
                        if name == "repeated":
                            check(exported["id"] == old_exports[fmt]["id"], "Actual prior combined " + fmt + " bytes remain unchanged")
                        stable_exports[key] = exported["id"]
            finally:
                close(client)
        check(all(p.read_bytes() == raw for p, raw in original.items()), "Every original prior-producer file remains byte-for-byte unchanged")
        report.update(status="passed", originalFileCount=len(original), priorRuntimeAnalyses=len(eligible),
                      acquisitions=acquisitions, oldExperimentReports=len(retained_ids))
    except Exception as error:
        report.update(status="failed", error=str(error))
        raise
    finally:
        (output / "result.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("PASS actual prior runtime/experiment producers:", len(report["steps"]), "checks;", len(original), "original files unchanged")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=Path)
    parser.add_argument("--runtime-source", required=True, type=Path)
    parser.add_argument("--experiment-source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    run(args.host, args.runtime_source, args.experiment_source, args.output)
