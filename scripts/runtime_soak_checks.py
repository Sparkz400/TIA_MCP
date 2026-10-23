"""Independent original-byte oracle for real-clock runtime capture soak/recovery.

No production reader, device access, native launch, or time simulation. The reused
chain reader verifies original plan/segment bytes directly with Python SHA-256.
"""
import argparse
import copy
from decimal import Decimal
from datetime import datetime
import hashlib
import json
from pathlib import Path

from runtime_pressure_checks import chain, decode, require


def read(path):
    return decode(Path(path).read_bytes())


def ledger(path):
    records = [decode(line) for line in Path(path).read_bytes().splitlines()]
    records.sort(key=lambda item: item["number"])
    require([row["number"] for row in records] == list(range(1, len(records) + 1)), "one original outcome per read number")
    return records


def rows_match(plan, rows, observations, *, prefix=False, frequency=None):
    ids = plan["request"]["selectionIds"]
    selected = {row["selectionId"]: row["selection"] for row in plan["signals"]}
    observations = [o for o in observations if o["selectionId"] in ids]
    reads = [r for r in rows if r["kind"] == "read"]
    require(len(reads) <= len(observations) if prefix else len(reads) == len(observations), "committed read/ledger length")
    last_cycle, signal_index, elapsed = None, 0, Decimal(0)
    accounted_cycle = -1
    last_completion = 0
    cursor = 0
    for sequence, row in enumerate(rows):
        require(row["sequence"] == sequence, "no duplicated/dropped/reordered committed row")
        require(row["elapsedMilliseconds"] >= elapsed, "nondecreasing monotonic row time")
        elapsed = row["elapsedMilliseconds"]
        require(row["scheduledMilliseconds"] == row["cycle"] * plan["request"]["intervalMilliseconds"], "exact scheduled slot")
        require(row["elapsedMilliseconds"] + 2 >= row["scheduledMilliseconds"], "no acquisition before its scheduled slot")
        if row["kind"] == "gap":
            require(last_cycle is not None and row["cycle"] == last_cycle and row["selectionId"] is None and
                    row["readResultJson"] is None and row["errorCode"] is None and row["skippedPolls"] > 0,
                    "explicit valueless gap attached to its acquired cycle")
            accounted_cycle += row["skippedPolls"]
            require(accounted_cycle * plan["request"]["intervalMilliseconds"] <= row["elapsedMilliseconds"], "no future gap")
            continue
        require(row["kind"] == "read" and row["skippedPolls"] == 0, "read/gap separation")
        if last_cycle != row["cycle"]:
            if last_cycle is None:
                require(row["cycle"] == 0, "first acquisition starts at cycle zero")
            else:
                require(signal_index == len(ids) and row["cycle"] == accounted_cycle + 1,
                        "cycle progression includes every skipped slot without catch-up")
            last_cycle = row["cycle"]
            accounted_cycle = last_cycle
            signal_index = 0
        require(signal_index < len(ids) and row["selectionId"] == ids[signal_index], "exact per-cycle signal order")
        signal_index += 1
        observed = observations[cursor]
        cursor += 1
        require(observed["selectionId"] == row["selectionId"] and observed["started"] >= last_completion and
                observed["completed"] >= observed["started"], "original acquisition order and measured duration")
        last_completion = observed["completed"]
        number = observed["number"]
        expected_error = "RuntimeTimeout" if number % 17 == 0 else "RuntimeTransportFailure" if number % 29 == 0 else None
        if observed["error"] == "RuntimeCancelled":
            expected_error = "RuntimeCancelled"
            require(cursor == len(observations) and not prefix, "cancellation only terminates an observed capture")
        require(observed["error"] == expected_error and row["errorCode"] == expected_error, "independent error schedule and committed quality")
        if frequency and expected_error != "RuntimeCancelled":
            measured_ms = Decimal(observed["completed"] - observed["started"]) * 1000 / frequency
            # Windows timer coalescing does not promise an exact lower bound for
            # every 2 ms request. The slow reads must actually exceed one poll slot.
            require(measured_ms > (100 if number % 37 == 0 else 0), "real acquisition delay measured with OS timestamps")
        if expected_error:
            require(row["readResultJson"] is None and observed["responseMember"] is None, "failed read never substitutes a value")
            continue
        value = decode(row["readResultJson"])
        require(value["selection"] == selected[row["selectionId"]], "original selection/context/metadata")
        for flag in ("atomicAcrossSignals", "scanSynchronized", "programMatchVerified", "deviceIdentityVerified", "nativeQualified"):
            require(value[flag] is False, "test source never promotes native evidence")
        if number % 31 == 0:
            require(value["status"] == "invalidAddress" and value["providerErrorCode"] == 201 and value["value"] is None,
                    "provider failure remains distinct and valueless")
            require("not retained" not in row["readResultJson"], "provider error message is omitted")
        else:
            expected = {0: 18446744073709551615, 1: -9223372036854775808,
                        2: Decimal("1.234567890123456789e-100"), 3: {"nested": [True, None, "text", 42]},
                        4: "x" * 1024, 5: False}[number % 6]
            require(value["status"] == "read" and value["providerErrorCode"] is None and value["value"] == expected,
                    "exact independent value including large integers")
            if number % 6 == 2:
                require("1.234567890123456789e-100" in row["readResultJson"], "source numeric lexeme preserved")
    return {"reads": len(reads), "gaps": sum(r["kind"] == "gap" for r in rows),
            "skippedPolls": sum(r["skippedPolls"] for r in rows)}


def export_matches(receipt_path, expected_rows, snapshot):
    receipt = read(receipt_path)
    require(receipt["snapshotRef"] == snapshot and receipt["nativeQualified"] is False, "export reference")
    require(len(receipt["artifacts"]) == 1, "one JSON export")
    artifact = receipt["artifacts"][0]
    raw = Path(artifact["path"]).read_bytes()
    require(hashlib.sha256(raw).hexdigest() == artifact["sha256"], "original export digest")
    exported = decode(raw)
    require(exported["rows"] == expected_rows and exported["capture"]["snapshotRef"] == snapshot, "export equals independently read committed rows")


def resources_match(samples, summary):
    baseline, loaded, released = samples[0], samples[1], samples[-1]
    require([baseline["phase"], loaded["phase"], released["phase"]] == ["baseline", "loadHeld", "resourcesReleased"], "resource measurement phases")
    during = [sample for sample in samples if sample["phase"] == "during"]
    require(len(during) >= summary["seconds"] and summary["activeSeconds"] >= summary["seconds"] >= 30, "actual sustained observation count and elapsed time")
    require(during[-1]["elapsedSeconds"] - during[0]["elapsedSeconds"] >= summary["seconds"] - 1, "resource samples span the loaded wall-clock interval")
    require(all(a["elapsedSeconds"] < b["elapsedSeconds"] and a["processorSeconds"] <= b["processorSeconds"]
                for a, b in zip(samples, samples[1:])), "monotonic resource sampling")
    require(loaded["managedBytes"] - baseline["managedBytes"] >= 60 << 20 and
            loaded["handleCount"] - baseline["handleCount"] >= 256, "actual bounded memory and OS handle pressure")
    require(loaded["handleCount"] - released["handleCount"] >= 200 and
            released["handleCount"] <= baseline["handleCount"] + 64, "post-disposal handle bound")
    require(released["managedBytes"] < loaded["managedBytes"] - (32 << 20), "owned allocation reclaimed")
    require(max(s["privateBytes"] for s in samples) < baseline["privateBytes"] + (384 << 20), "measured private-memory bound")
    require(summary["cpuHashIterations"] >= 100 and samples[-1]["processorSeconds"] > samples[0]["processorSeconds"], "actual bounded CPU work")
    require(0 <= summary["cancellationMilliseconds"] < 10000, "bounded actual cancellation latency")
    require(summary["clockBoundary"] == "system" and summary["sessionBoundary"] == "testDouble" and
            summary["systemResourcesExhausted"] is False and summary["actualDiskFull"] is False and summary["nativeExecuted"] is False,
            "honest qualification boundaries")


def negative_controls(plan, rows, observations, samples, summary):
    tests = []

    def reject(name, action):
        try:
            action()
        except (AssertionError, KeyError, ValueError, IndexError):
            tests.append(name)
            return
        raise AssertionError("Mutation accepted: " + name)

    altered = copy.deepcopy(rows)
    altered[0]["selectionId"] = "0" * 32
    reject("wrong-signal", lambda: rows_match(plan, altered, observations))
    altered = copy.deepcopy(rows)
    next(r for r in altered if r["errorCode"] == "RuntimeTimeout")["errorCode"] = None
    reject("timeout-to-success", lambda: rows_match(plan, altered, observations))
    altered = copy.deepcopy(rows)
    target = next(r for r in altered if r["readResultJson"] and "18446744073709551615" in r["readResultJson"])
    target["readResultJson"] = target["readResultJson"].replace("18446744073709551615", "18446744073709551614")
    reject("large-integer-change", lambda: rows_match(plan, altered, observations))
    altered = copy.deepcopy(rows)
    altered.pop(next(i for i, r in enumerate(altered) if r["kind"] == "gap"))
    for sequence, row in enumerate(altered):
        row["sequence"] = sequence
    reject("omitted-gap-renumbered", lambda: rows_match(plan, altered, observations))
    altered = copy.deepcopy(rows)
    altered.insert(1, copy.deepcopy(altered[0]))
    for sequence, row in enumerate(altered):
        row["sequence"] = sequence
    reject("replayed-row-renumbered", lambda: rows_match(plan, altered, observations))
    altered = copy.deepcopy(rows)
    altered[0]["scheduledMilliseconds"] += 1
    reject("wrong-scheduled-time", lambda: rows_match(plan, altered, observations))
    altered_summary = copy.deepcopy(summary)
    altered_summary["activeSeconds"] = 0
    reject("simulated-elapsed-time", lambda: resources_match(samples, altered_summary))
    altered_samples = copy.deepcopy(samples)
    altered_samples[1]["handleCount"] = altered_samples[0]["handleCount"]
    reject("unobserved-handle-pressure", lambda: resources_match(altered_samples, summary))
    return tests


def audit_steady(steady):
    steady = Path(steady)
    summary, samples = read(steady / "soak-checks.json"), read(steady / "resources.json")
    resources_match(samples, summary)
    observations = ledger(steady / "acquisition-ledger.jsonl")
    require(len(observations) == summary["readCount"], "all steady outcomes persisted")
    captures = read(steady / "scenario.json")["captures"]
    require(len(captures) == 4 and all(len(c["request"]["selectionIds"]) == 16 for c in captures), "four real-clock 16-signal workers")
    active_ids = {selection for capture in captures for selection in capture["request"]["selectionIds"]}
    active_observations = [o for o in observations if o["selectionId"] in active_ids]
    require(Decimal(max(o["completed"] for o in active_observations) - min(o["started"] for o in active_observations)) /
            summary["timestampFrequency"] >= summary["seconds"], "original OS read timestamps span the real acquisition interval")
    statuses = read(steady / "statuses.json")
    cases = []
    for capture, status in zip(captures, statuses, strict=True):
        plan, rows, progress = chain(steady / "store", capture["captureId"])
        require(plan["request"] == capture["request"] and progress["state"] == "ended" and
                progress["terminalReason"] == "stopped" and progress["droppedReadResults"] == 0, "stopped full committed capture")
        case = rows_match(plan, rows, observations, frequency=summary["timestampFrequency"])
        require(case["reads"] > 256 and case["gaps"] > 0, "multiple real-time buffers and gaps")
        export_matches(steady / ("export-" + capture["captureId"] + ".json"), rows, status["snapshotRef"])
        cases.append(case)
    rejected = negative_controls(plan, rows, observations, samples, summary)
    healthy = read(steady / "healthy.json")
    plan, rows, progress = chain(steady / "store", healthy["captureId"])
    rows_match(plan, rows, observations)
    require(progress["terminalReason"] == "sampleLimit" and any(r["readResultJson"] and decode(r["readResultJson"])["status"] == "read" for r in rows),
            "healthy acquisition after pressure")
    export_matches(steady / "export-healthy.json", rows, read(steady / "healthy-status.json")["snapshotRef"])
    require(not (steady / "store/runtime-captures" / summary["refusedId"]).exists(), "capacity refusal has no intent")
    return summary, samples, cases, rejected


def audit_crash(root):
    root = Path(root)
    crash = root / "crash"
    failure, recovery = read(crash / "failure-intent.json"), read(crash / "recovery.json")
    require(failure["expectedExitCode"] == 73 and failure["disposalInvoked"] is False and
            failure["processId"] != recovery["processId"], "distinct process restart after abrupt owned exit")
    for relative, digest in failure["originalFiles"].items():
        require(hashlib.sha256((crash / "store" / relative).read_bytes()).hexdigest() == digest,
                "every pre-crash original plan/head/segment byte remains unchanged")
    original = recovery["original"]
    plan, rows, progress = chain(crash / "store", original["captureId"], failure["snapshotRef"])
    crashed_observations = ledger(crash / "acquisition-ledger.jsonl")
    require(len(crashed_observations) == failure["completedReadOutcomes"] >= 176 and
            failure["pendingReadNumber"] == failure["completedReadOutcomes"] + 1, "controlled pending read failure boundary")
    prefix_case = rows_match(plan, rows, crashed_observations, prefix=True)
    require(128 <= prefix_case["reads"] < failure["completedReadOutcomes"] and progress["state"] == "running" and progress["terminalReason"] is None,
            "only the committed prefix survives; pending outcomes are not adopted")
    interrupted = recovery["interrupted"]
    require(interrupted["state"] == "interrupted" and interrupted["uncommittedDataUnknown"] is True and
            interrupted["snapshotRef"] == failure["snapshotRef"] and recovery["repeatedIntentReads"] == 0 and
            recovery["acquisitionResumed"] is False and recovery["uncommittedRowsRecovered"] is False, "honest interrupted no-replay recovery")
    export_matches(crash / "export-recovered-prefix.json", rows, failure["snapshotRef"])
    plan, rows, progress = chain(crash / "store", recovery["healthy"]["captureId"])
    rows_match(plan, rows, ledger(crash / "recovery-ledger.jsonl"))
    require(progress["terminalReason"] == "sampleLimit" and progress["readAttempts"] == recovery["reads"] == 16,
            "fresh process releases capacity and acquires exactly 16 healthy reads")
    export_matches(crash / "export-recovery-healthy.json", rows, recovery["healthyStatus"]["snapshotRef"])
    execution = read(root / "execution.json")
    require([phase["exitCode"] for phase in execution["phases"]] == [0, 73, 0] and
            all(phase["originalOwnedProcessWaited"] and not phase["forcedTermination"] for phase in execution["phases"]),
            "actual child exit/restart observations")
    return prefix_case


def long_rounds_match(root, intent, summary, execution):
    rounds = summary["rounds"]
    phase = execution["phases"][0]
    require(len(rounds) == intent["epochs"] == summary["epochs"] and
            2 <= len(rounds) <= 15 and summary["seconds"] == intent["seconds"] and
            30 <= summary["seconds"] <= 120 and summary["seconds"] * len(rounds) <= 900,
            "bounded original long-run epoch request")
    require(phase["name"] == "long" and phase["pid"] == summary["processId"] == intent["processId"] and
            phase["exitCode"] == 0 and phase["originalOwnedProcessWaited"] and not phase["forcedTermination"],
            "all epochs belong to the original waited process lifetime")
    require(summary["processStartedUtc"] == intent["processStartedUtc"] and
            abs(datetime.fromisoformat(intent["processStartedUtc"]).timestamp() - float(phase["created"])) <= 0.001 and
            datetime.fromisoformat(intent["startedUtc"]) >= datetime.fromisoformat(intent["processStartedUtc"]),
            "managed process birth matches the retained original parent process identity")
    require(intent["crossEpochHandleAllowance"] == 32 and intent["crossEpochManagedAllowance"] == 64 << 20 and
            intent["crossEpochPrivateAllowance"] == 384 << 20 and intent["maximumEvidenceBytes"] == 1 << 30 and
            intent["minimumInitialFreeBytes"] == 3 << 30 and intent["initialFree"] >= 3 << 30,
            "fixed resource and retention admission bounds")
    total = Decimal(0)
    warm = rounds[0]["released"]
    previous = intent["timestamp"]
    for index, row in enumerate(rounds):
        require(row["index"] == index and row["directory"] == f"epoch-{index + 1:02d}" and
                row["processId"] == phase["pid"] and row["started"] >= previous and row["completed"] > row["started"],
                "exact same-process epoch order without overlap")
        require(Decimal(row["completed"] - row["started"]) / intent["timestampFrequency"] >= row["activeSeconds"] >= summary["seconds"],
                "original epoch timestamps cover the actual loaded duration")
        previous = row["completed"]
        total += row["activeSeconds"]
        require(row["priorCaptureCount"] == (index + 1) * 5 and row["repeatedIntentReads"] == 0,
                "every earlier stopped and healthy intent is checked without replay")
        require(len(row["replayProofs"]) == index + 1, "each earlier epoch has a replay observation in every later round")
        for earlier, proof in zip(rounds[:index + 1], row["replayProofs"], strict=True):
            earlier_root = Path(root) / earlier["directory"]
            capture_ids = [c["captureId"] for c in read(earlier_root / "scenario.json")["captures"]]
            capture_ids.append(read(earlier_root / "healthy.json")["captureId"])
            require(proof["directory"] == earlier["directory"] and proof["captureIds"] == capture_ids and
                    proof["readsBefore"] == proof["readsAfter"] == 0 and
                    proof["beforeFiles"] == proof["afterFiles"] == earlier["originalFiles"],
                    "exact earlier intents, zero acquisition and original bytes on both sides of each reopen")
        released = row["released"]
        require(released["handleCount"] <= warm["handleCount"] + 32 and
                released["managedBytes"] <= warm["managedBytes"] + (64 << 20) and
                released["privateBytes"] <= warm["privateBytes"] + (384 << 20),
                "fixed cross-epoch resource-recovery allowances")
        require(row["evidenceBytes"] <= 1 << 30, "bounded long-run evidence")
        epoch = Path(root) / row["directory"]
        for relative, digest in row["originalFiles"].items():
            require(hashlib.sha256((epoch / "store" / relative).read_bytes()).hexdigest() == digest,
                    "original earlier plan/head/segment bytes never change")
    require(abs(summary["totalActiveSeconds"] - total) <= Decimal("0.000001") and total >= summary["epochs"] * summary["seconds"] and
            phase["elapsedSeconds"] >= total and summary["elapsedSeconds"] >= total,
            "actual total acquisition is distinct from process elapsed time")
    require(summary["qualifiesTenMinuteAcquisition"] == (total >= 600) and summary["separateStoresPerEpoch"] is True and
            summary["systemResourcesExhausted"] is False and summary["actualDiskFull"] is False and summary["nativeExecuted"] is False,
            "long-run limits remain explicit")


def audit_long(root):
    root = Path(root)
    long_root = root / "long"
    intent, summary, execution = read(long_root / "long-intent.json"), read(long_root / "long-checks.json"), read(root / "execution.json")
    long_rounds_match(long_root, intent, summary, execution)
    require(sum(path.stat().st_size for path in long_root.rglob("*") if path.is_file()) <= 1 << 30,
            "actual final long-run files including summaries remain within the evidence budget")
    epochs = []
    for row in summary["rounds"]:
        epoch = long_root / row["directory"]
        require(read(long_root / (row["directory"] + "-receipt.json")) == row, "immutable per-epoch receipt identity")
        steady, samples, cases, rejected = audit_steady(epoch)
        require(steady["activeSeconds"] == row["activeSeconds"], "actual epoch acquisition matches its original summary")
        epochs.append({"directory": row["directory"], "activeSeconds": float(steady["activeSeconds"]),
                       "captures": cases, "rejectedMutations": rejected,
                       "peakPrivateBytes": max(s["privateBytes"] for s in samples),
                       "peakHandles": max(s["handleCount"] for s in samples)})
    rejected = []
    for name, change in [
        ("epoch-reordered", lambda s: s["rounds"].reverse()),
        ("invented-process-lifetime", lambda s: s["rounds"][0].update(processId=0)),
        ("invented-process-birth", lambda s: s.update(processStartedUtc="2000-01-01T00:00:00+00:00")),
        ("replay-read-hidden", lambda s: s["rounds"][0].update(repeatedIntentReads=1)),
        ("earlier-intent-omitted", lambda s: s["rounds"][-1].update(priorCaptureCount=5)),
        ("changed-original-prefix", lambda s: s["rounds"][0]["originalFiles"].update({next(iter(s["rounds"][0]["originalFiles"])): "0" * 64})),
        ("cross-epoch-handle-growth", lambda s: s["rounds"][-1]["released"].update(handleCount=s["rounds"][0]["released"]["handleCount"] + 33)),
        ("invented-long-duration", lambda s: s.update(totalActiveSeconds=0)),
    ]:
        altered = copy.deepcopy(summary)
        altered_intent = copy.deepcopy(intent)
        change(altered)
        if name == "invented-process-birth":
            # Change both managed receipts consistently: rejection must depend on
            # the original parent's separately retained process birth observation.
            altered_intent["processStartedUtc"] = altered["processStartedUtc"]
        try:
            long_rounds_match(long_root, altered_intent, altered, execution)
        except (AssertionError, ValueError, KeyError):
            rejected.append(name)
        else:
            raise AssertionError("Long-run mutation accepted: " + name)
    prefix = audit_crash(root)
    released = [row["released"] for row in summary["rounds"]]
    times = [float(row["elapsedSeconds"]) / 60 for row in released]
    mean_time = sum(times) / len(times)
    denominator = sum((t - mean_time) ** 2 for t in times)
    slopes = {}
    for field in ["handleCount", "managedBytes", "privateBytes"]:
        values = [float(row[field]) for row in released]
        mean_value = sum(values) / len(values)
        slopes[field + "PerMinute"] = sum((t - mean_time) * (v - mean_value) for t, v in zip(times, values)) / denominator
    return {"schemaVersion": "runtime-long-soak-oracle.v1", "epochs": epochs,
            "totalActiveSeconds": float(summary["totalActiveSeconds"]),
            "qualifiesTenMinuteAcquisition": summary["qualifiesTenMinuteAcquisition"],
            "committedCrashPrefix": prefix, "rejectedCrossEpochMutations": rejected,
            "originalProcessLifetimeBound": True, "independentOriginalByteReader": True,
            "releasedResourceSlopesDescriptiveOnly": slopes,
            "sessionBoundary": "testDouble", "separateStoresPerEpoch": True,
            "systemResourcesExhausted": False, "actualDiskFull": False, "nativeExecuted": False}


def audit(root):
    root = Path(root)
    if (root / "long").is_dir():
        return audit_long(root)
    summary, samples, cases, rejected = audit_steady(root / "steady")
    prefix_case = audit_crash(root)
    result = {"schemaVersion": "runtime-real-clock-soak-oracle.v1", "steadyCaptures": cases,
              "committedCrashPrefix": prefix_case, "rejectedMutations": rejected,
              "activeSeconds": float(summary["activeSeconds"]), "cancellationMilliseconds": float(summary["cancellationMilliseconds"]),
              "maxPrivateBytes": max(s["privateBytes"] for s in samples), "maxHandles": max(s["handleCount"] for s in samples),
              "independentOriginalByteReader": True, "sessionBoundary": "testDouble",
              "systemResourcesExhausted": False, "actualDiskFull": False, "nativeExecuted": False}
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("--receipt", type=Path)
    args = parser.parse_args()
    result = audit(args.root)
    text = json.dumps(result, indent=2) + "\n"
    if args.receipt:
        with args.receipt.open("x", encoding="utf-8") as stream:
            stream.write(text)
    print(text)
