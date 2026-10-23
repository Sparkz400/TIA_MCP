"""Independent retained-byte oracle for the managed runtime pressure qualification.

Reads files directly, never calls the production store/reader or a target endpoint.
Uses Decimal to preserve scientific numeric values and explicit expected mixed results.
"""
import argparse
import copy
from decimal import Decimal
import hashlib
import json
from pathlib import Path


def decode(raw):
    return json.loads(raw, parse_float=Decimal)


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def chain(root, capture_id, reference=None):
    directory = Path(root) / "runtime-captures" / capture_id
    head = json.loads((directory / "head.json").read_bytes())
    reference = reference or head
    kind, identity, count, expected = reference.split(":")
    require(kind == "capture" and identity == capture_id, "capture reference identity")
    plan_bytes = (directory / "plan.json").read_bytes()
    plan = decode(plan_bytes)
    require(plan["captureId"] == capture_id, "plan capture identity")
    segments = []
    total_bytes = len(plan_bytes)
    for number in range(int(count), 0, -1):
        raw = (directory / f"{number:06d}.segment").read_bytes()
        total_bytes += len(raw)
        require(hashlib.sha256(raw).hexdigest() == expected, "segment hash")
        segment = decode(raw)
        require(segment["index"] == number and segment["version"] == 1, "segment index/version")
        expected = segment["previousSha256"]
        segments.append(segment)
    require(hashlib.sha256(plan_bytes).hexdigest() == expected, "original plan hash")
    require(total_bytes <= plan["request"]["maximumBytes"], "capture retained byte budget")
    rows = []
    attempts = skipped = 0
    for segment in reversed(segments):
        for row in segment["rows"]:
            require(row["sequence"] == len(rows), "contiguous sequence")
            rows.append(row)
            attempts += row["kind"] == "read"
            skipped += row["skippedPolls"]
        progress = segment["progress"]
        require(progress["rows"] == len(rows) and progress["readAttempts"] == attempts and progress["skippedPolls"] == skipped,
                "segment cumulative accounting")
    return plan, rows, segments[0]["progress"] if segments else None


def mixed(plan, rows, observations):
    request = plan["request"]
    require(len(observations) == request["maximumSamples"], "observation count")
    require(len(request["selectionIds"]) == 64, "64 signals")
    cursor = cycle = timestamp = 0
    for index, observation in enumerate(observations):
        number = index + 1
        if index % 64 == 0:
            timestamp = max(timestamp, cycle * 100)
        started = timestamp
        timestamp += 351 if number % 997 == 0 else 1
        error = "RuntimeTimeout" if number % 17 == 0 else "RuntimeTransportFailure" if number % 29 == 0 else None
        require(observation["number"] == number and observation["started"] == started and
                observation["completed"] == timestamp and observation["error"] == error, "independent acquisition ledger timing/error")
        row = rows[cursor]
        cursor += 1
        require(row["kind"] == "read" and row["selectionId"] == request["selectionIds"][index % 64], "exact signal acquisition order")
        require(row["cycle"] == cycle and row["scheduledMilliseconds"] == cycle * 100 and row["elapsedMilliseconds"] == timestamp,
                "independent monotonic schedule")
        require(row["errorCode"] == error and row["skippedPolls"] == 0, "explicit read quality")
        if error:
            require(row["readResultJson"] is None and observation["responseMember"] is None, "failed read has no substituted value")
        else:
            result = decode(row["readResultJson"])
            require(result["selection"] == plan["signals"][index % 64]["selection"], "retained signal metadata/context")
            require(result["elapsedMilliseconds"] == timestamp - started, "read elapsed duration")
            for flag in ["atomicAcrossSignals", "scanSynchronized", "programMatchVerified", "deviceIdentityVerified", "nativeQualified"]:
                require(result[flag] is False, "pressure does not promote evidence")
            if number % 31 == 0:
                require(result["status"] == "invalidAddress" and result["providerErrorCode"] == 201 and result["value"] is None,
                        "provider failure remains valueless")
                require("not retained" not in row["readResultJson"], "provider text omitted")
            else:
                expected = {0: 18446744073709551615, 1: -9223372036854775808,
                            2: Decimal("1.234567890123456789e-100"), 3: {"nested": [True, None, "text", 42]},
                            4: "x" * 1024, 5: False}[number % 6]
                require(result["status"] == "read" and result["providerErrorCode"] is None and result["value"] == expected,
                        "exact independent mixed value")
                if number % 6 == 2:
                    require("1.234567890123456789e-100" in row["readResultJson"], "decimal source lexeme")
        if number % 64 == 0 or number == len(observations):
            missed = timestamp // 100 - cycle
            if missed > 0:
                gap = rows[cursor]
                cursor += 1
                require(gap["kind"] == "gap" and gap["cycle"] == cycle and gap["skippedPolls"] == missed and
                        gap["elapsedMilliseconds"] == timestamp and gap["selectionId"] is None and
                        gap["readResultJson"] is None and gap["errorCode"] is None, "independent skipped-slot accounting")
            cycle = timestamp // 100 + 1
    require(cursor == len(rows), "no replay or extra rows")


def mutation_controls(plan, rows, observations):
    controls = []
    def refuses(name, mutate):
        changed_rows = copy.deepcopy(rows)
        changed_observations = copy.deepcopy(observations)
        mutate(changed_rows, changed_observations)
        try:
            mixed(plan, changed_rows, changed_observations)
        except (AssertionError, IndexError, KeyError):
            controls.append(name)
            return
        raise AssertionError("Oracle accepted mutation: " + name)
    refuses("wrong-signal", lambda r, _: r[0].update(selectionId=plan["request"]["selectionIds"][1]))
    refuses("wrong-cycle", lambda r, _: r[0].update(cycle=1))
    refuses("missing-gap", lambda r, _: next(x for x in r if x["kind"] == "gap").update(skippedPolls=0))
    refuses("timeout-as-good", lambda r, _: next(x for x in r if x["errorCode"] == "RuntimeTimeout").update(errorCode=None))
    def corrupt_value(r, _):
        row = next(x for x in r if x.get("readResultJson") and "18446744073709551615" in x["readResultJson"])
        row["readResultJson"] = row["readResultJson"].replace("18446744073709551615", "18446744073709551614")
    refuses("integer-rounded", corrupt_value)
    refuses("replayed-row", lambda r, _: r.append(copy.deepcopy(r[0])))
    refuses("ledger-time-shift", lambda _, o: o[0].update(completed=o[0]["completed"] + 1))
    return controls


def audit(root, controls=True):
    root = Path(root)
    receipt = json.loads((root / "pressure-checks.json").read_bytes())
    require(receipt["productionPoller"] and not receipt["actualDiskFull"] and not receipt["nativeExecuted"], "pressure evidence scope")
    checked = []
    mutations = []
    for case in receipt["cases"]:
        if case["kind"] == "mixed":
            plan, rows, progress = chain(case["captureRoot"], case["captureId"], case["snapshotRef"])
            require(progress["state"] == "ended" and progress["terminalReason"] == "sampleLimit" and progress["droppedReadResults"] == 0,
                    "mixed terminal accounting")
            observations = decode(Path(case["ledgerPath"]).read_bytes())
            mixed(plan, rows, observations)
            exported = decode(Path(case["exportPath"]).read_bytes())
            require(exported["rows"] == rows, "export equals exact committed chain")
            require(exported["capture"]["snapshotRef"] == case["snapshotRef"], "export exact snapshot identity")
            checked.append({"kind": "mixed", "rows": len(rows), "attempts": len(observations), "skippedPolls": progress["skippedPolls"]})
            if controls:
                mutations = mutation_controls(plan, rows, observations)
        elif case["kind"] == "workerChurn":
            for capture_id in case["captureIds"]:
                _, rows, progress = chain(case["captureRoot"], capture_id)
                require(progress["state"] == "ended" and progress["terminalReason"] == "stopped" and len(rows) == 1,
                        "churn cancellation accounting")
                require(rows[0]["errorCode"] == "RuntimeCancelled" and rows[0]["readResultJson"] is None, "churn cancellation quality")
            _, healthy, progress = chain(case["captureRoot"], case["healthyCaptureId"])
            require(progress["terminalReason"] == "sampleLimit" and len(healthy) == 1 and healthy[0]["errorCode"] is None, "churn recovery")
            checked.append({"kind": "workerChurn", "captures": len(case["captureIds"]), "healthy": True})
        elif case["kind"] == "reservationCapacity":
            _, rows, progress = chain(case["captureRoot"], case["recoveredCaptureId"])
            require(not rows and progress["state"] == "ended", "reservation recovery")
            checked.append({"kind": "reservationCapacity", "recovered": True})
        else:
            raise AssertionError("Unknown pressure case")
    result = {"schemaVersion": "runtime-pressure-oracle.v1", "cases": checked, "rejectedMutations": mutations,
              "independentRawChainReader": True, "actualDiskFull": False, "nativeExecuted": False}
    (root / "independent-oracle.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    arguments = parser.parse_args()
    print(json.dumps(audit(arguments.root), indent=2))
