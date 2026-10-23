"""Reopen actual prior-producer diagnostic cases; preserve every preexisting source byte.

Uses the selected historical artifact root in place so its absolute original input
references stay valid. Adds only new content-addressed diagnostic artifacts there.
Records the exact prior result receipt and source manifest in a separate output.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path

from smoke_mcp import Client


def run(host, source, output):
    source, output = source.resolve(), output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    original = {p: p.read_bytes() for p in source.rglob("*") if p.is_file()}
    assert source / "result.json" in original, "Select actual retained producer output with its result receipt."
    producer = json.loads(original[source / "result.json"])
    assert producer["status"] == "passed"
    documents = {p.stem: json.loads(data) for p, data in original.items()
                 if p.parent.name == "trace-diagnostics" and p.suffix == ".json"}
    cases = {key: doc for key, doc in documents.items() if doc.get("schemaVersion") == "diagnostic-case.v1"}
    imports = {key: doc for key, doc in documents.items() if doc.get("schemaVersion") == "trace-import.v1"}
    assert cases and imports, "No actual prior diagnostic cases/imports."
    report = dict(status="running", nativeQualification=False, producerResult=producer,
                  producerResultSha256=hashlib.sha256(original[source / "result.json"]).hexdigest(),
                  source=str(source), steps=[], originalFiles={str(p.relative_to(source)): hashlib.sha256(raw).hexdigest()
                                                              for p, raw in original.items()},
                  managedBinaryHashes={name: hashlib.sha256((host.parent / name).read_bytes()).hexdigest()
                                       for name in ("TiaMcp.Host.dll", "TiaMcp.Core.dll")})
    client = Client(host.resolve(), 30, ["--artifacts", str(source), "--worker", str(output / "absent-native.exe"),
                                       "--sim-worker", str(output / "absent-simulator.exe")])
    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                     "clientInfo": {"name": "prior-diagnostic-experiment", "version": "1"}})
        client.notify("notifications/initialized")
        trace = next(d for d in documents.values() if d.get("schemaVersion") == "trace-plan.v1")
        for case_id, case in cases.items():
            external = sorted({row["storagePath"] for row in case["signals"]
                               if row["storagePath"] != case["selectedInstance"]["dbPath"]})
            settings = copy.deepcopy(trace["requestedSettings"])
            if settings["trigger"]["signalId"] not in {s["id"] for s in case["signals"]}:
                settings["trigger"] = dict(signalId=next(s["id"] for s in case["signals"] if s["datatype"] == "Bool"), kind="fallingEdge")
                settings["rationale"] = "Explicit Boolean signal from the retained case; target observability remains unverified."
            request = dict(reason="Reopen actual prior-producer saved evidence without replacing its bytes.",
                           watch=dict(tableName="Prior experiment", tiaVersion=21,
                                      externalStorage=[dict(objectPath=p, reason="Explicit retained external diagnostic role.") for p in external]),
                           trace=dict(backend="manual", family="ordinaryPlc", reviewId=case["reviewId"], settings=settings))
            result = client.tool("diagnostic_experiment_plan", dict(planId=case_id, request=request))
            assert result["experiment"]["diagnosticCase"] == case
            report["steps"].append(dict(caseId=case_id, experimentId=result["id"], outcome="composedOriginalUnchanged"))
            for import_id, imported in imports.items():
                if imported["tracePlanId"] != result["experiment"]["tracePlanId"]:
                    continue
                reviewed = client.tool("diagnostic_experiment_review", dict(experimentId=result["id"], request=dict(
                    afterExperimentId=result["id"], beforeMeasurementId=import_id, afterMeasurementId=import_id,
                    comparison=dict(reason="Reassess actual prior import without resampling.", beforeAnchorSeconds=0,
                                    afterAnchorSeconds=0, windowStartSeconds=-1, windowEndSeconds=1))))
                assert reviewed["review"]["bugFixProven"] is False
                report["steps"].append(dict(importId=import_id, outcome="reviewedOriginalImport"))
        for import_id in imports:
            result = client.tool("trace_analyze", dict(measurementId=import_id))
            assert result["report"]["measurementId"] == import_id and result["report"]["causationProven"] is False
            report["steps"].append(dict(importId=import_id, outcome="existingAnalysisPreserved"))
        assert all(path.read_bytes() == raw for path, raw in original.items()), "Original historical bytes changed."
        report["originalFileCount"] = len(original)
        report["status"] = "passed"
    except Exception as error:
        report["status"], report["error"] = "failed", str(error)
        raise
    finally:
        report["shutdown"] = client.close()
        if report["shutdown"]["hostExitCode"] != 0 or report["shutdown"]["forcedHostStop"]:
            report["status"] = "failed"
        (output / "result.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    assert report["status"] == "passed", report
    print("PASS actual prior diagnostic records:", len(cases), "cases,", len(imports), "imports;",
          len(original), "original files unchanged;", len(report["steps"]), "checks")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    run(args.host, args.source, args.output)
