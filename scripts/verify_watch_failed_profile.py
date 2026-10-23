"""Verify recovery after a refused native edit without promoting the failed profile."""
import argparse
from datetime import datetime
import json
from pathlib import Path

import verify_watch_expansion as oracle


def verify(root):
    root = Path(root).resolve()
    admission = oracle.load(root / "admission.json")
    oracle.need(admission["mode"] in {"comment-de", "comment-en", "comment-nl", "clone", "reorder", "delete"}, "Exact failed edit profile required")
    oracle.need(not (root / "interruption-marker.json").exists() and not (root / "applied.json").exists(), "Use the separate interruption/accepted-apply oracle")
    oracle.need(oracle.load(root / "failure.json")["automaticReplay"] is False, "Failed attempt receipt missing")
    reviewed = oracle.reviewed_candidate(root)
    outer, operation = oracle.load(root / "review.json"), oracle.load(root / "operation.json")
    records = oracle.journal(root, operation["id"])
    oracle.need(any(r["state"] == "dispatchIntent" for r in records) and any(r["state"] in {"outcomeUnknown", "recoveryRequired"} for r in records), "Failed native dispatch must retain recovery ownership until independent reconciliation")
    recovery = oracle.recovery_check(root, records, outer["review"], operation,
        Path(reviewed["baseline"]["path"]).read_bytes(), Path(reviewed["candidate"]["path"]).read_bytes(), True)
    oracle.base.replay_refusal_check(root, operation)
    competitor = oracle.load(root / "competing-owner-refusal.json")
    oracle.need(competitor["code"] == "ResourceBusy" and competitor["operationId"] == operation["id"] and competitor["competitorId"] != operation["id"], "Competing owner refusal missing")
    contenders = oracle.journal(root, competitor["competitorId"])
    oracle.need(all(r["state"] == "prepared" for r in contenders) and contenders[0]["plan"]["resources"] == records[0]["plan"]["resources"], "Competitor resources/result differ")
    oracle.need({p.name for p in root.glob("host-*-start.json")} == {"host-01-start.json", "host-02-start.json"}, "Exactly two Host lifetimes required")
    identities, closes, starts, native_ids = set(), [], [], set()
    for ordinal in (1, 2):
        started = oracle.load(root / f"host-{ordinal:02}-start.json")
        closed = oracle.load(root / f"host-{ordinal:02}-close.json")
        host, close = started["identity"], closed["close"]
        oracle.need(started["point"] == "none" and closed["expectedLoss"] is False and closed["identity"] == host and closed["cleanupError"] is None, "Failure recovery requires clean actual Host lifetimes")
        oracle.need(Path(host["exe"]).resolve() == Path(admission["host"]).resolve() and close["hostExitCode"] == 0 and close["forcedHostStop"] is False and close["stderrEofObserved"] is True, "Exact unforced Host close missing")
        exits = closed["exits"]
        oracle.need(exits and oracle.load(root / f"host-{ordinal:02}-processes.json") == [e["identity"] for e in exits], "Complete retained process inventory required")
        workers = [e for e in exits if Path(e["identity"]["exe"]).resolve() == Path(admission["worker"]).resolve()]
        oracle.need(len(workers) == len(close["workerExits"]) == 1, "One exact worker and receipt required")
        worker, receipt = workers[0]["identity"], close["workerExits"][0]
        oracle.need(worker["parentPid"] == host["pid"] and receipt["processId"] == worker["pid"] and receipt["processExitVerified"] is True and receipt["exitCode"] == 0 and receipt["forcedTermination"] is False and abs(datetime.fromisoformat(receipt["startedAtUtc"].replace("Z", "+00:00")).timestamp() - worker["created"]) < .01, "Worker receipt/ancestry/birth differs")
        count = 0
        for entry in exits:
            value, proof = entry["identity"], entry["proof"]
            identity = value["pid"], value["created"]
            oracle.need(identity not in identities and host["created"] <= value["created"] <= closed["closedAt"], "Process reused or outside admitted lifetime")
            identities.add(identity)
            oracle.need(proof["processId"] == value["pid"] and proof["handleCaptured"] is True and proof["handleClosed"] is True and proof["processExitVerified"] is True and proof["completed"] is True and proof["exitCode"] == 0, "Exact retained handle exit proof missing")
            if entry is not workers[0]:
                oracle.need(value["name"] == "Siemens.Automation.Portal.exe" and value["parentPid"] == worker["pid"] and value["created"] >= worker["created"], "Unrelated native process in cleanup")
                count += 1; native_ids.add(value["pid"])
        oracle.need(count > 0, "Owned native process proof missing")
        starts.append(host); closes.append(closed["closedAt"])
    oracle.need(starts[0]["pid"] == admission["hostProcessId"] and starts[0]["pid"] != starts[1]["pid"] and closes[0] <= starts[1]["created"] and competitor["hostProcessId"] == starts[1]["pid"], "Fresh Host order/competition identity differs")
    for key in ("host", "worker"):
        oracle.need(oracle.sha(Path(admission[key]).read_bytes()) == admission[key + "Sha256"], "Frozen binary changed")
    for proof in records[-1]["evidence"]:
        if proof["gate"] in {"recovery.original", "recovery.restored"}:
            oracle.need(oracle.load(oracle.artifact(root, proof["artifact"]))["processId"] in native_ids, "Recovery snapshot lacks retained process cleanup")
    oracle.need(outer["review"]["before"]["processId"] in native_ids, "Baseline lacks exact native cleanup")
    return {"completed": True, "nativeInvoked": False, "profileQualificationPassed": False, "failingProfileRemainsDisabled": True,
        "recovery": recovery, "exactNormalCleanupVerified": True, "hostRestartVerified": True, "replayAndCompetingOwnerRefused": True}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path); parser.add_argument("output", type=Path)
    args = parser.parse_args(); result = verify(args.root)
    with args.output.open("x", encoding="utf-8") as stream: json.dump(result, stream, indent=2)
    print(json.dumps(result))
