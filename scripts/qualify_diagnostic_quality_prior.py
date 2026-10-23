"""Replay actual prior v1 measurement, single, repeated and acquisition producers without rewriting originals."""
import argparse
import json
from pathlib import Path

from smoke_mcp import Client
from qualify_diagnostic_quality_offline import sha
from qualify_runtime_diagnostic_acquisition_offline import original_chain


def run(host, source, output):
    source, output = source.resolve(), output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    names = ("diagnostic-mcp", "diagnostic-experiment-mcp", "diagnostic-repeat-mcp", "runtime-diagnostic-acquisition-mcp")
    roots = [source / name for name in names]
    originals = {p: sha(p.read_bytes()) for root in roots for p in root.rglob("*") if p.is_file()}
    current = {name: sha((host.parent / name).read_bytes()) for name in ("TiaMcp.Host.dll", "TiaMcp.Core.dll")}
    report = dict(status="running", nativeQualification=False, steps=[], hostExits=[], source=str(source),
                  managedBinaryHashes=current, harnessSha256=sha(Path(__file__).read_bytes()),
                  originalFiles={str(p): digest for p, digest in originals.items()}, producerReceipts={}, exports={})
    stable = {}

    def check(ok, label):
        assert ok, label
        report["steps"].append(label)

    def exported(client, root, report_id, kind, prior=None):
        for fmt in ("json", "markdown", "html"):
            args = dict(format=fmt)
            args["acquisitionId" if kind == "diagnostic_runtime_acquisition_export" else "reportId"] = report_id
            receipt = client.tool(kind, args)
            raw = Path(receipt["artifact"]["path"]).read_bytes()
            check(sha(raw) == receipt["id"], "Export exact bytes " + root.name + "/" + fmt)
            key = root.name + "/" + report_id + "/" + fmt
            if key in stable:
                check(stable[key] == receipt["id"], "Export unchanged after current Host restart " + key)
            stable[key] = receipt["id"]
            if prior is not None:
                check(receipt["id"] == prior[fmt]["id"], "Actual prior " + fmt + " export unchanged")

    try:
        for root in roots:
            producer_path = root / "result.json"
            producer = json.loads(producer_path.read_bytes())
            check(producer["status"] == "passed", "Actual previous producer passed: " + root.name)
            check(producer["managedBinaryHashes"]["TiaMcp.Core.dll"] != current["TiaMcp.Core.dll"],
                  "Producer Core differs from current Core: " + root.name)
            report["producerReceipts"][str(producer_path)] = dict(sha256=sha(producer_path.read_bytes()),
                                                                binaryHashes=producer["managedBinaryHashes"])
            def read(record_id):
                return json.loads((root / "trace-diagnostics" / (record_id + ".json")).read_bytes())
            for restart in range(2):
                client = Client(host.resolve(), 60, ["--artifacts", str(root), "--worker", str(output / "absent-native.exe"),
                                                    "--sim-worker", str(output / "absent-simulator.exe")])
                try:
                    client.request("initialize", dict(protocolVersion="2025-11-25", capabilities={},
                                   clientInfo=dict(name="prior-quality-compatibility", version="2")))
                    client.notify("notifications/initialized")
                    if root.name == "diagnostic-mcp":
                        for name, formats in producer["reportExports"].items():
                            record_id = formats["json"]["sourceReportId"]
                            old = read(record_id)
                            if old["schemaVersion"] == "diagnostic-report.v1":
                                new = client.tool("trace_analyze", dict(measurementId=old["measurementId"]))
                                check(new["id"] == record_id and new["report"] == old, "Original v1 analysis rederives exactly")
                            elif old["schemaVersion"] == "trace-comparison.v1":
                                new = client.tool("trace_compare", dict(beforeMeasurementId=old["beforeMeasurementId"],
                                                  afterMeasurementId=old["afterMeasurementId"], options=old["alignment"]))
                                check(new["id"] == record_id and new["comparison"] == old, "Original v1 comparison rederives exactly")
                            exported(client, root, record_id, "diagnostic_report_export", formats)
                    elif root.name in ("diagnostic-experiment-mcp", "diagnostic-repeat-mcp"):
                        ids = {"review": producer["artifactIds"]["review"]} if root.name == "diagnostic-experiment-mcp" else producer["artifactIds"]
                        for name, record_id in ids.items():
                            old = read(record_id)
                            if old["schemaVersion"] == "diagnostic-experiment-repeat.v1":
                                new = client.tool("diagnostic_experiment_compare_runs", dict(request=old["request"]))
                                check(new["id"] == record_id and new["report"] == old, "Original repeated v1 report rederives exactly")
                            else:
                                new = client.tool("diagnostic_experiment_review", dict(experimentId=old["beforeExperimentId"], request=old["request"]))
                                check(new["id"] == record_id and new["review"] == old, "Original single v1 report rederives exactly")
                            exported(client, root, record_id, "diagnostic_experiment_report_export",
                                     producer["exports"] if name == "repeated" else None)
                    else:
                        ids = {producer["acquisitionId"], producer["artifactIds"]["acquisition"], producer["artifactIds"]["prefix"]}
                        for record_id in sorted(ids):
                            old = read(record_id)
                            original_chain(old, root)
                            check(True, "Independent actual prior raw plan/segment chain and rows verified")
                            new = client.tool("diagnostic_experiment_acquire_runtime",
                                              dict(experimentId=old["experimentId"], snapshotRef=old["snapshotRef"], request=old["request"]))
                            check(new["id"] == record_id and new["acquisition"] == old, "Original acquisition v1 rederives exactly")
                            exported(client, root, record_id, "diagnostic_runtime_acquisition_export",
                                     producer["exports"] if record_id == producer["acquisitionId"] else None)
                finally:
                    result = client.close()
                    report["hostExits"].append(result)
                    check(result["hostExitCode"] == 0 and not result["forcedHostStop"] and
                          not any(row.get("started") for row in result["workerExits"]), "Prior replay Host clean exit without native workers")
        report.update(status="passed", exports=stable)
    except Exception as error:
        report.update(status="failed", error=repr(error))
        raise
    finally:
        mismatches = [str(p) for p, digest in originals.items() if not p.is_file() or sha(p.read_bytes()) != digest]
        additions = {str(p): sha(p.read_bytes()) for root in roots for p in root.rglob("*") if p.is_file() and p not in originals}
        integrity = dict(status="passed" if not mismatches else "failed", fileCount=len(originals), mismatches=mismatches,
                         originalFiles={str(p): digest for p, digest in originals.items()}, additions=additions)
        (output / "prior-original-integrity.json").write_text(json.dumps(integrity, indent=2) + "\n", encoding="utf-8")
        report.update(originalFileCount=len(originals), originalMismatches=mismatches, addedFileCount=len(additions), checks=len(report["steps"]))
        if mismatches:
            report["status"] = "failed"
        (output / "result.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    assert report["status"] == "passed"
    print("PASS actual prior diagnostic quality compatibility:", report["checks"], "checks;", len(originals), "original files unchanged")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=Path)
    parser.add_argument("--source", required=True, type=Path, help="Actual previous All/package output containing four diagnostic producer roots.")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    run(args.host, args.source, args.output)
