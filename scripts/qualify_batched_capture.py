"""Native check that batched block/tag reads leave project_model_capture and project_index unchanged.

Runs the same owned projects through two engineering workers behind one source-built Host: a baseline worker
(built from the commit before the change) and the candidate worker. Each worker gets its own new TIA instance
without UI (tia_start) and opens every project read-only, so no project is saved or changed. For every project
the metadata revisions of both tools must be identical between the workers; capture times are recorded.
"""
import argparse
import datetime
import json
import pathlib
import statistics

from smoke_mcp import Client, ROOT


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def owned_project(path):
    resolved = path.resolve()
    artifacts = (ROOT / "artifacts").resolve()
    require(resolved.suffix == ".ap21" and artifacts in resolved.parents, "Only owned .ap21 fixtures under artifacts/ are opened: " + str(path))
    return resolved


def run_worker(host, worker, label, projects, rounds, timeout, artifacts):
    client = Client(host, timeout, ["--worker", str(worker), "--artifacts", str(artifacts / label)])
    results = []
    try:
        for project in projects:
            started = client.tool("tia_start")
            require(started.get("ownsPortalProcess") is True and started.get("projects") == [], "tia_start did not give an empty owned instance")
            opened = client.tool("project_open", {"path": str(project), "readOnly": True})
            require(opened.get("opened") is True and opened.get("readOnly") is True, "project did not open read-only")
            name = opened["project"]["name"]
            captures = []
            for _ in range(rounds):
                model = client.tool("project_model_capture", {"request": {"projectName": name, "lifetimeSeconds": 60}})
                captures.append({"revision": model["metadataRevision"]["value"], "nodes": model["nodeCount"],
                                 "ms": round(model["usage"]["nativeCaptureMilliseconds"], 1), "truncated": model["usage"]["truncated"],
                                 "warnings": model["warnings"]})
                client.tool("project_model_release", {"request": {"snapshotId": model["snapshotId"]}})
            index = client.tool("project_index", {"request": {"action": "capture", "projectName": name, "maxVisited": 50000, "lifetimeSeconds": 60}})
            client.tool("project_index", {"request": {"action": "release", "indexId": index["indexId"]}})
            client.tool("tia_detach")
            results.append({"project": str(project), "projectName": name, "captures": captures,
                            "index": {"revision": index["metadataRevision"], "rows": index.get("rowCount"),
                                      "complete": index.get("completeness", {}).get("scopeComplete")}})
    finally:
        exit_record = client.close()
    return {"label": label, "worker": str(worker), "projects": results, "exit": exit_record}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--baseline-worker", type=pathlib.Path, required=True)
    parser.add_argument("--candidate-worker", type=pathlib.Path, default=ROOT / "src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe")
    parser.add_argument("--project", type=pathlib.Path, action="append", required=True)
    parser.add_argument("--rounds", type=int, default=4)
    parser.add_argument("--timeout", type=int, default=900)
    args = parser.parse_args()
    projects = [owned_project(path) for path in args.project]
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    run_dir = ROOT / "artifacts" / "batched-capture" / stamp
    run_dir.mkdir(parents=True)
    report = {"schemaVersion": 1, "evidenceId": "local.project-model.batched-reads." + stamp[:8], "startedUtc": stamp,
              "rounds": args.rounds, "projects": [str(p) for p in projects]}
    report["baseline"] = run_worker(args.host, args.baseline_worker, "baseline", projects, args.rounds, args.timeout, run_dir)
    report["candidate"] = run_worker(args.host, args.candidate_worker, "candidate", projects, args.rounds, args.timeout, run_dir)
    comparison = []
    for old, new in zip(report["baseline"]["projects"], report["candidate"]["projects"]):
        old_revisions = {c["revision"] for c in old["captures"]}
        new_revisions = {c["revision"] for c in new["captures"]}
        row = {"project": old["projectName"], "nodes": old["captures"][0]["nodes"],
               "modelRevisionEqual": old_revisions == new_revisions and len(old_revisions) == 1,
               "indexRevisionEqual": old["index"]["revision"] == new["index"]["revision"],
               "baselineColdMs": old["captures"][0]["ms"], "candidateColdMs": new["captures"][0]["ms"],
               "baselineWarmMedianMs": statistics.median(c["ms"] for c in old["captures"][1:]) if len(old["captures"]) > 1 else None,
               "candidateWarmMedianMs": statistics.median(c["ms"] for c in new["captures"][1:]) if len(new["captures"]) > 1 else None}
        if row["baselineWarmMedianMs"] and row["candidateWarmMedianMs"]:
            row["warmSpeedup"] = round(row["baselineWarmMedianMs"] / row["candidateWarmMedianMs"], 2)
        comparison.append(row)
    report["comparison"] = comparison
    report["passed"] = all(r["modelRevisionEqual"] and r["indexRevisionEqual"] for r in comparison) and \
        all(run["exit"]["hostExitCode"] == 0 and not run["exit"]["forcedHostStop"] for run in (report["baseline"], report["candidate"]))
    (run_dir / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(run_dir / "report.json"), "passed": report["passed"], "comparison": comparison}, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
