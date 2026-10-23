"""Bounded block-test sequences and evidence, independent of Siemens or MCP.

A backend must create a fresh owned instance in start(), return good-quality
snapshots from read()/call(instance), and release it in close(). Application call
control is deliberately distinct from PLC scan synchronization.
"""
import copy
import datetime
import decimal
import hashlib
import json
import math
import re
import time


class ExecutionBlocked(RuntimeError):
    """Required evidence/capabilities are unavailable; never a passing test."""


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2, allow_nan=False) + "\n"


def revision_hash(value):
    return hashlib.sha256(canonical(value).encode("utf-8")).hexdigest()


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def integer(value, low, high, name):
    if type(value) is not int or not low <= value <= high:
        raise ValueError(f"{name} must be an integer in {low}..{high}.")
    return value


def number(value, name):
    if type(value) not in (int, float) or (isinstance(value, float) and not math.isfinite(value)):
        raise ValueError(name + " must be a finite number, not a Boolean.")
    return value


def flatten_bindings(program):
    return {instance + "." + path: binding for instance, members in program["bindings"].items()
            for path, binding in members.items()}


def check_value(value, binding, name):
    kind = binding["type"].lower()
    if kind == "bool":
        if type(value) is not bool:
            raise ValueError(name + " requires a Boolean.")
    elif re.fullmatch(r"u?int(8|16|32|64)", kind):
        bits = int(re.search(r"\d+", kind)[0])
        low, high = (0, 2**bits - 1) if kind.startswith("u") else (-2**(bits-1), 2**(bits-1)-1)
        integer(value, low, high, name)
    elif kind in ("float", "double"):
        number(value, name)
        if abs(value) > (3.4028234663852886e38 if kind == "float" else 1.7976931348623157e308):
            raise ValueError(name + " exceeds the supported floating-point range.")
    elif kind in ("string", "wstring"):
        if not isinstance(value, str):
            raise ValueError(name + " requires a string.")
        length = len(value.encode("utf-16-le")) // 2 if kind == "wstring" else len(value)
        if "capacity" in binding and length > binding["capacity"]:
            raise ValueError(name + " exceeds its declared string capacity.")
    else:
        raise ValueError("Unsupported runtime value type: " + kind)


def strict_equal(actual, expected):
    if isinstance(actual, bool) or isinstance(expected, bool):
        return type(actual) is type(expected) and actual == expected
    return type(actual) is type(expected) and actual == expected if isinstance(actual, str) or isinstance(expected, str) else actual == expected


def validate_suite(suite, program):
    """Preflight the entire sequence before a backend can cause any mutation."""
    bindings = flatten_bindings(program)
    instances = list(program["bindings"])
    if not instances or not bindings:
        raise ValueError("A generated program with bindings is required.")
    if not re.fullmatch(r"[a-z0-9][a-z0-9_.-]*", suite.get("fixtureId", "")):
        raise ValueError("Invalid fixtureId.")
    if suite.get("timingMode", "application_call_controlled") not in ("application_call_controlled", "scan_controlled", "timed_polling"):
        raise ValueError("Unknown timingMode.")
    if "requireScanSynchronization" in suite and type(suite["requireScanSynchronization"]) is not bool:
        raise ValueError("requireScanSynchronization must be Boolean.")
    cases = suite.get("cases")
    if not isinstance(cases, list) or not 1 <= len(cases) <= 256:
        raise ValueError("Provide 1..256 test cases.")
    seen = set()
    total_calls = 0
    total_operations = 0
    total_instances = 0

    def values(mapping):
        if not isinstance(mapping, dict):
            raise ValueError("Stimuli must map leaf paths to values.")
        for path, value in mapping.items():
            if path not in bindings or not bindings[path].get("writable"):
                raise ValueError("Unknown or non-writable stimulus: " + path)
            check_value(value, bindings[path], path)
            if bindings[path]["type"].lower() in ("string", "wstring") and len(value.encode("utf-16-le")) // 2 > 4096:
                raise ValueError(path + " exceeds the simulator adapter's 4096-character write limit.")

    def assertion(item):
        if not isinstance(item, dict) or item.get("path") not in bindings:
            raise ValueError("Assertion must identify a known binding.")
        kind, path = item.get("kind"), item["path"]
        fields = {"exact": {"expected"}, "approx": {"expected", "absoluteTolerance", "relativeTolerance"},
                  "transition": {"from", "to"}, "window": {"expected", "minMs", "maxMs", "pollMs"},
                  "elapsed": {"sinceStep", "capMs", "resolutionMs", "maxUncertaintyMs"}}
        if kind not in fields or set(item) - ({"kind", "path"} | fields[kind]):
            raise ValueError("Unknown assertion kind or field.")
        for field in ("from", "to") if kind == "transition" else () if kind == "elapsed" else ("expected",):
            if field not in item:
                raise ValueError("Missing assertion " + field)
            check_value(item[field], bindings[path], path)
        if kind == "elapsed":
            if bindings[path]["type"] not in ("int32", "int64") or bindings[path].get("unit", "ms") != "ms":
                raise ValueError("Elapsed assertions require a signed millisecond value.")
            if not isinstance(item.get("sinceStep"), str) or not item["sinceStep"]:
                raise ValueError("Elapsed assertions require an earlier named single-call step.")
            integer(item.get("capMs"), 1, 60000, "capMs")
            integer(item.get("resolutionMs"), 0, 10, "resolutionMs")
            integer(item.get("maxUncertaintyMs"), 1, 60000, "maxUncertaintyMs")
        if kind == "approx":
            number(item["expected"], "expected")
            if not {"absoluteTolerance", "relativeTolerance"} & set(item):
                raise ValueError("An explicit numerical tolerance is required.")
            for field in ("absoluteTolerance", "relativeTolerance"):
                if number(item.get(field, 0), field) < 0:
                    raise ValueError("Tolerance cannot be negative.")
        if kind == "window":
            integer(item.get("minMs", 0), 0, 60000, "minMs")
            integer(item.get("maxMs"), 1, 60000, "maxMs")
            integer(item.get("pollMs", 20), 1, 5000, "pollMs")
            if item.get("minMs", 0) >= item["maxMs"]:
                raise ValueError("Timing window must have positive width.")

    for case in cases:
        if not isinstance(case, dict) or not isinstance(case.get("id"), str) or not case["id"] or case["id"] in seen:
            raise ValueError("Case IDs must be unique and nonempty.")
        if set(case) - {"id", "initial", "steps", "repeat", "maxDurationMs", "description"}:
            raise ValueError("Unknown case field.")
        seen.add(case["id"])
        repeat = integer(case.get("repeat", 1), 1, 100, "repeat")
        total_instances += repeat
        integer(case.get("maxDurationMs", 120000), 1, 600000, "maxDurationMs")
        values(case.get("initial", {}))
        if not isinstance(case.get("steps"), list) or not 1 <= len(case["steps"]) <= 2048:
            raise ValueError("Provide 1..2048 steps per case.")
        case_calls, case_operations, checks = 0, 1, 0  # Initial capture, even for read-only cases.
        prior_steps = {}
        for step_index, step in enumerate(case["steps"]):
            if not isinstance(step, dict) or set(step) - {"id", "target", "set", "calls", "waitMs", "pulse", "assertions", "description"}:
                raise ValueError("Unknown step field.")
            if step.get("target", instances[0]) not in instances:
                raise ValueError("Unknown call target.")
            values(step.get("set", {}))
            calls = integer(step.get("calls", 1), 0, 1024, "calls")
            case_calls += calls
            case_operations += max(1, calls) + bool(step.get("set"))
            integer(step.get("waitMs", 0), 0, 60000, "waitMs")
            if "pulse" in step:
                pulse = step["pulse"]
                if not isinstance(pulse, dict) or set(pulse) - {"path", "high", "low", "highCalls", "lowCalls"}:
                    raise ValueError("Unknown pulse field.")
                values({pulse.get("path", ""): pulse.get("high", True)})
                values({pulse.get("path", ""): pulse.get("low", False)})
                if not pulse["path"].startswith(step.get("target", instances[0]) + "."):
                    raise ValueError("Pulse path must belong to the call target.")
                pulse_calls = integer(pulse.get("highCalls", 1), 1, 1024, "highCalls")
                pulse_calls += integer(pulse.get("lowCalls", 1), 1, 1024, "lowCalls")
                case_calls += pulse_calls
                case_operations += pulse_calls + 2
            if not isinstance(step.get("assertions", []), list):
                raise ValueError("Assertions must be a list.")
            windows = [item for item in step.get("assertions", []) if item.get("kind") == "window"]
            if len(windows) > 1:
                raise ValueError("Use at most one timing window per step to preserve a common origin.")
            for item in step.get("assertions", []):
                assertion(item)
                if item["kind"] == "elapsed":
                    origin_step = prior_steps.get(item["sinceStep"])
                    if (origin_step is None or any(s.get("calls", 1) != 1 or "pulse" in s for s in (origin_step, step))
                            or origin_step.get("target", instances[0]) != step.get("target", instances[0])):
                        raise ValueError("Elapsed origin/current must be single calls to the same instance with an earlier unique step ID.")
                checks += 1
                if item["kind"] == "window":
                    window_calls = item["maxMs"] // item.get("pollMs", 20) + 2
                    case_calls += window_calls
                    case_operations += window_calls
            step_id = step.get("id", str(step_index + 1))
            if not isinstance(step_id, str) or not step_id or step_id in prior_steps:
                raise ValueError("Step IDs must be nonempty and unique within a case.")
            prior_steps[step_id] = step
        if not checks:
            raise ValueError("Every case must contain an assertion.")
        total_calls += case_calls * repeat
        total_operations += case_operations * repeat
    if total_calls > 50000:
        raise ValueError("Suite exceeds 50000 bounded calls.")
    if total_operations > 50000 or total_instances > 1024:
        raise ValueError("Suite exceeds 50000 operations or 1024 fresh deployments, including read-only steps/repeats.")
    return suite


def evaluate_assertion(assertion, previous, current, step_observations=None):
    """Evaluate exact, tolerant or adjacent-observation transitions."""
    path, kind = assertion["path"], assertion["kind"]
    actual = current["values"][path]
    result = {"assertion": copy.deepcopy(assertion), "actual": actual, "status": "failed"}
    if kind == "exact":
        passed = strict_equal(actual, assertion["expected"])
    elif kind == "approx":
        number(actual, "actual")
        expected = assertion["expected"]
        delta = abs(decimal.Decimal(str(actual)) - decimal.Decimal(str(expected)))
        tolerance = max(decimal.Decimal(str(assertion.get("absoluteTolerance", 0))),
                        abs(decimal.Decimal(str(expected))) * decimal.Decimal(str(assertion.get("relativeTolerance", 0))))
        passed = delta <= tolerance
        result.update(delta=str(delta), allowedTolerance=str(tolerance))
    elif kind == "transition":
        prior = previous["values"][path]
        result["previous"] = prior
        passed = strict_equal(prior, assertion["from"]) and strict_equal(actual, assertion["to"])
    elif kind == "elapsed":
        origin = (step_observations or {}).get(assertion["sinceStep"])
        result.update(timingBasis="bounded_host_call_intervals", scanSynchronized=False)
        if origin is None or origin.get("operation") != "call" or current.get("operation") != "call":
            return {**result, "status": "blocked", "reason": "Measured origin and current call intervals are required."}
        a, b = origin["hostIntervalMs"], current["hostIntervalMs"]
        if (len(a) != 2 or len(b) != 2 or not all(isinstance(x, (int, float)) and not isinstance(x, bool) and math.isfinite(x) for x in [*a, *b])
                or not 0 <= a[0] <= a[1] <= b[0] <= b[1]):
            raise ValueError("Elapsed call intervals must be finite, ordered and non-overlapping.")
        elapsed = [max(0, b[0] - a[1]), b[1] - a[0]]
        result.update(originCallIntervalMs=a, currentCallIntervalMs=b, measuredSeparationMs=elapsed,
                      uncertaintyMs=elapsed[1] - elapsed[0])
        if result["uncertaintyMs"] > assertion["maxUncertaintyMs"]:
            return {**result, "status": "blocked", "reason": "Measured call uncertainty exceeds the authored bound."}
        baseline = number(origin["values"][path], "origin elapsed")
        number(actual, "actual elapsed")
        cap, resolution = assertion["capMs"], assertion["resolutionMs"]
        if not 0 <= baseline <= cap:
            raise ValueError("Origin elapsed value is outside the authored timer range.")
        bounds = [max(0, min(cap, baseline + elapsed[0]) - resolution),
                  min(cap, baseline + elapsed[1]) + resolution]
        result["expectedIntervalMs"] = bounds
        passed = bounds[0] <= actual <= bounds[1] and 0 <= actual <= cap
    else:
        raise ValueError("Timing windows require observation by the executor.")
    result["status"] = "passed" if passed else "failed"
    return result


def execute_suite(suite, program, backend_factory, revision, persist=None, *, clock=time.monotonic, sleep=time.sleep,
                  case_selection=None, stop_requested=None):
    """Run a suite; backend_factory(case) returns a new backend for EACH repeat.

    persist(report) is called after each observation and outcome, including setup
    and cleanup failures. Native evidence remains the backend's responsibility.
    case_selection is an optional nonempty list/tuple of exact (caseId, repetition)
    tuples, with one-based repetitions. It never alters the suite identity or skips
    validation of unselected cases. Explicit selections cannot qualify the suite.
    stop_requested is observed only before admitting a case. An active case and
    its verified cleanup finish first; failed cleanup always takes precedence.
    """
    validate_suite(suite, program)
    if not revision:
        raise ValueError("An exact program revision is required.")
    expected_pairs = [(case["id"], repetition) for case in suite["cases"]
                      for repetition in range(1, case.get("repeat", 1) + 1)]
    expected_set = set(expected_pairs)
    if case_selection is None:
        selected_pairs = expected_pairs
    else:
        if type(case_selection) not in (list, tuple) or not 1 <= len(case_selection) <= len(expected_pairs):
            raise ValueError("case_selection must be a nonempty list/tuple bounded by the suite's case repetitions.")
        selected_set = set()
        for pair in case_selection:
            if (type(pair) is not tuple or len(pair) != 2 or type(pair[0]) is not str
                    or type(pair[1]) is not int):
                raise ValueError("Each case_selection entry must be an exact (caseId, integer repetition) tuple.")
            if pair not in expected_set:
                raise ValueError("case_selection identifies an unknown case or repetition.")
            if pair in selected_set:
                raise ValueError("case_selection must not contain duplicate case repetitions.")
            selected_set.add(pair)
        selected_pairs = [pair for pair in expected_pairs if pair in selected_set]
    selected_set = set(selected_pairs)

    def pair_rows(pairs):
        return [{"caseId": case_id, "repetition": repetition} for case_id, repetition in pairs]

    bindings = flatten_bindings(program)
    report = {"schemaVersion": 1, "fixtureId": suite["fixtureId"], "status": "blocked", "startedAtUtc": utc_now(),
              "programRevision": copy.deepcopy(revision), "suiteSha256": revision_hash(suite),
              "cases": [], "runtimeQualified": False, "scanQualified": False,
              "qualificationScope": "full_suite" if case_selection is None else "selected_cases",
              "observationScope": "observed values under the recorded execution mode; timing windows use host observations",
              "selectedCasesPassed": False, "fullSuiteCoverage": False,
              "limitations": ["Application call counts do not establish PLC scan duration or scan synchronization.",
                              "Host timestamps and timing windows include transport latency; they do not measure PLC monotonic time."]}
    if case_selection is not None:
        report["limitations"].append("This report executes an explicit case selection; it does not qualify the full suite.")

    def save():
        attempted_pairs = [(case["id"], case["repetition"]) for case in report["cases"]]
        attempted_set = set(attempted_pairs)
        passed_pairs = [(case["id"], case["repetition"]) for case in report["cases"]
                        if case["status"] == "passed" and "completedAtUtc" in case]
        terminal = "completedAtUtc" in report
        report["selectedCasesPassed"] = terminal and passed_pairs == selected_pairs
        report["fullSuiteCoverage"] = terminal and passed_pairs == expected_pairs
        report["caseCoverage"] = {
            "expected": pair_rows(expected_pairs), "selected": pair_rows(selected_pairs),
            "attempted": pair_rows(attempted_pairs), "passed": pair_rows(passed_pairs),
            "unattemptedSelected": pair_rows(pair for pair in selected_pairs if pair not in attempted_set),
            "expectedCount": len(expected_pairs), "selectedCount": len(selected_pairs),
            "attemptedCount": len(attempted_pairs), "passedCount": len(passed_pairs),
            "selectedExecutionComplete": terminal and attempted_pairs == selected_pairs
                                         and all("completedAtUtc" in case for case in report["cases"]),
            "fullSuiteSelected": selected_pairs == expected_pairs,
            "fullSuiteAttempted": attempted_pairs == expected_pairs,
            "fullSuitePassed": report["fullSuiteCoverage"]}
        if persist:
            persist(copy.deepcopy(report))

    save()
    for case in suite["cases"]:
        for repetition in range(case.get("repeat", 1)):
            if (case["id"], repetition + 1) not in selected_set:
                continue
            if stop_requested is not None and stop_requested():
                report["status"] = "failed" if any(row["status"] == "failed" for row in report["cases"]) else "blocked"
                report["admission"] = report["statusReason"] = "stopped_by_request"
                report["stopRequested"] = True
                report["limitations"].append("Stopped before the next case; completed cases retain their results and remaining cases are unattempted.")
                report["completedAtUtc"] = utc_now()
                save()
                return report
            record = {"id": case["id"], "repetition": repetition + 1, "status": "blocked", "startedAtUtc": utc_now(),
                      "programRevision": copy.deepcopy(revision), "initial": copy.deepcopy(case.get("initial", {})),
                      "observations": [], "steps": [], "failures": [], "cleanup": {}}
            report["cases"].append(record)
            backend, started, current_step = None, False, None
            synchronized = False
            inputs, previous = {}, None
            started_clock = clock()
            max_duration = case.get("maxDurationMs", 120000)

            def deadline():
                if (clock() - started_clock) * 1000 > max_duration:
                    raise TimeoutError("Case duration exceeded maxDurationMs.")

            def capture(operation, target=None):
                deadline()
                if len(record["observations"]) >= 10000:
                    raise RuntimeError("Case observation bound exceeded.")
                begin = (clock() - started_clock) * 1000
                snapshot = backend.call(target) if operation == "call" else backend.read()
                end = (clock() - started_clock) * 1000
                deadline()
                if not isinstance(snapshot, dict) or snapshot.get("quality") != "good":
                    raise ExecutionBlocked("Bad or missing snapshot quality.")
                if synchronized and snapshot.get("scanSynchronized") is not True:
                    raise ExecutionBlocked("Observation does not confirm the requested scan synchronization.")
                if not isinstance(snapshot.get("values"), dict) or not set(bindings) <= set(snapshot["values"]):
                    raise ExecutionBlocked("Snapshot omits required input/output/state bindings.")
                for path, binding in bindings.items():
                    check_value(snapshot["values"][path], binding, path)
                snapshot = copy.deepcopy(snapshot)
                snapshot.update(observedAtUtc=utc_now(), hostIntervalMs=[begin, end], operation=operation, target=target)
                record["observations"].append(snapshot)
                save()
                return snapshot

            def write(values):
                deadline()
                if values:
                    backend.write(values)
                    inputs.update(copy.deepcopy(values))
                    deadline()

            def failure(outcome, snapshot):
                detail = {"step": current_step, "inputs": copy.deepcopy(inputs), "outcome": copy.deepcopy(outcome),
                          "snapshot": copy.deepcopy(snapshot), "timestampUtc": utc_now(),
                          "programRevision": copy.deepcopy(revision)}
                record["failures"].append(detail)

            try:
                backend = backend_factory(copy.deepcopy(case))
                capabilities = copy.deepcopy(backend.capabilities)
                record["capabilities"] = capabilities
                mode = suite.get("timingMode", "application_call_controlled")
                needs_scan = suite.get("requireScanSynchronization", False) or mode == "scan_controlled"
                if needs_scan and not capabilities.get("scanSynchronized"):
                    raise ExecutionBlocked("Scan synchronization is required but unavailable; no polling fallback was used.")
                if mode == "application_call_controlled" and not capabilities.get("applicationCallControl"):
                    raise ExecutionBlocked("Single-call control is required but unavailable.")
                if mode == "timed_polling" and not capabilities.get("timedPolling"):
                    raise ExecutionBlocked("Timed polling capability is unavailable.")
                record["timingMode"] = "scan_controlled" if needs_scan else mode
                isolation = backend.start()
                record["isolation"] = copy.deepcopy(isolation)
                if not isinstance(isolation, dict) or isolation.get("isolation") != "fresh_owned_instance":
                    raise ExecutionBlocked("Fresh owned deployment is required to isolate retained state.")
                if isolation.get("revision") != revision:
                    raise ExecutionBlocked("Loaded program revision does not match the requested revision.")
                if needs_scan:
                    synchronization = isolation.get("synchronization", {})
                    if synchronization.get("mode") != "scan_controlled" or synchronization.get("verified") is not True:
                        raise ExecutionBlocked("Scan capability was advertised but activated synchronization was not verified.")
                    synchronized = True
                record["scanSynchronized"] = synchronized
                for limitation in isolation.get("limitations", []):
                    if limitation not in report["limitations"]:
                        report["limitations"].append(limitation)
                started = True
                started_clock = clock()
                write(case.get("initial", {}))
                previous = capture("read")
                step_observations = {}
                record["status"] = "passed"
                for index, step in enumerate(case["steps"]):
                    current_step = step.get("id", str(index + 1))
                    target = step.get("target", next(iter(program["bindings"])))
                    step_record = {"id": current_step, "request": copy.deepcopy(step), "status": "passed", "assertions": []}
                    record["steps"].append(step_record)
                    before = previous
                    write(step.get("set", {}))
                    if step.get("waitMs", 0):
                        sleep(step["waitMs"] / 1000)
                        deadline()
                    first_call = None
                    current = None
                    for _ in range(step.get("calls", 1)):
                        current = capture("call", target)
                        first_call = first_call or current
                    if "pulse" in step:
                        pulse = step["pulse"]
                        for level, default in (("high", True), ("low", False)):
                            write({pulse["path"]: pulse.get(level, default)})
                            for _ in range(pulse.get(level + "Calls", 1)):
                                current = capture("call", target)
                                first_call = first_call or current
                    current = current or capture("read")
                    origin = (first_call or current)["hostIntervalMs"][1]
                    # All immediate assertions use the same final step observation.
                    immediate = current
                    for assertion in step.get("assertions", []):
                        if assertion["kind"] != "window":
                            outcome = evaluate_assertion(assertion, before, immediate, step_observations)
                        else:
                            path, expected = assertion["path"], assertion["expected"]
                            lower, upper = assertion.get("minMs", 0), assertion["maxMs"]
                            last_nonmatching = None
                            outcome = {"assertion": copy.deepcopy(assertion), "status": "failed", "timingBasis": "host_observation",
                                       "originHostMs": origin, "scanSynchronized": synchronized}
                            while True:
                                left, right = [max(0, x - origin) for x in current["hostIntervalMs"]]
                                matched = strict_equal(current["values"][path], expected)
                                outcome.update(actual=current["values"][path], observedIntervalMs=[left, right])
                                if matched:
                                    # A transition can only be bracketed by observations. A
                                    # bracket crossing a bound is inconclusive, never a pass.
                                    bracket = [last_nonmatching if last_nonmatching is not None else 0, right]
                                    outcome["transitionBracketMs"] = bracket
                                    if right < lower or bracket[0] > upper:
                                        outcome["status"] = "failed"
                                    elif bracket[0] >= lower and right <= upper:
                                        outcome["status"] = "passed"
                                    else:
                                        outcome["status"] = "blocked"
                                        outcome["reason"] = "Observation interval overlaps a timing boundary."
                                    break
                                last_nonmatching = left
                                if right >= upper:
                                    outcome["reason"] = "Expected value was not observed within the timing window."
                                    break
                                sleep(min(assertion.get("pollMs", 20), upper - right) / 1000)
                                current = capture("call", target)
                        step_record["assertions"].append(outcome)
                        if outcome["status"] != "passed":
                            failure(outcome, current if assertion["kind"] == "window" else immediate)
                            if step_record["status"] != "failed":
                                step_record["status"] = outcome["status"]
                            if record["status"] != "failed":
                                record["status"] = outcome["status"]
                        save()
                    previous = current
                    step_observations[current_step] = immediate
                    if step_record["status"] != "passed":
                        break
            except Exception as error:
                record["status"] = "blocked" if isinstance(error, ExecutionBlocked) or not started else "failed"
                record["error"] = {"type": type(error).__name__, "message": str(error)}
                failure(record["error"], record["observations"][-1] if record["observations"] else None)
                save()
            finally:
                if backend is not None:
                    try:
                        record["cleanup"] = backend.close()
                        if not isinstance(record["cleanup"], dict) or record["cleanup"].get("completed") is not True:
                            raise RuntimeError("Backend cleanup did not confirm completion.")
                        if record["cleanup"].get("runtimeStateUncertain") is True:
                            raise RuntimeError("Backend cleanup retained uncertain runtime state.")
                    except Exception as error:
                        retained_cleanup = getattr(error, "cleanup", record.get("cleanup"))
                        record["cleanup"] = copy.deepcopy(retained_cleanup) if isinstance(retained_cleanup, dict) else {}
                        record["cleanup"].update(completed=False, error=str(error), type=type(error).__name__)
                        record["status"] = "failed"
                        failure({"cleanupError": str(error)}, previous)
                record["completedAtUtc"] = utc_now()
                save()
            # An uncertain teardown must never overlap another simulator case.
            if record["cleanup"].get("error") or record["cleanup"].get("runtimeStateUncertain") is True:
                if record["cleanup"].get("runtimeStateUncertain") is True:
                    report["statusReason"] = "stopped_due_to_uncertain_runtime"
                    report["admission"] = "stopped_due_to_uncertain_runtime"
                report["limitations"].append("Remaining cases were not started because cleanup failed.")
                report["status"] = "failed"
                report["completedAtUtc"] = utc_now()
                save()
                return report
    statuses = [case["status"] for case in report["cases"]]
    report["status"] = "failed" if "failed" in statuses else "blocked" if "blocked" in statuses else "passed"
    report["runtimeQualified"] = case_selection is None and report["status"] == "passed"
    report["scanQualified"] = report["runtimeQualified"] and all(case.get("scanSynchronized") is True for case in report["cases"])
    report["completedAtUtc"] = utc_now()
    save()
    return report
