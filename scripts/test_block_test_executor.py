"""Executor regressions with a deterministic fake clock/backend; no Siemens."""
import copy
import unittest

from block_test_executor import ExecutionBlocked, evaluate_assertion, execute_suite, revision_hash, validate_suite


PROGRAM = {"bindings": {"dut": {
    "Enable": {"symbol": "DB.dut.Enable", "type": "bool", "writable": True},
    "Reset": {"symbol": "DB.dut.Reset", "type": "bool", "writable": True},
    "Value": {"symbol": "DB.dut.Value", "type": "int16", "writable": True},
    "Count": {"symbol": "DB.dut.Count", "type": "int32", "writable": False},
    "Done": {"symbol": "DB.dut.Done", "type": "bool", "writable": False},
    "state.Previous": {"symbol": "DB.dut.Previous", "type": "bool", "writable": False},
}}}
REVISION = {"sourceSha256": "a" * 64, "programSha256": "b" * 64}


class Clock:
    now = 0

    def time(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


class Backend:
    capabilities = {"applicationCallControl": True, "scanSynchronized": False}

    def __init__(self, clock, *, fail_start=False, fail_close=False, bad_quality=False, drop_value=False, revision=REVISION):
        self.clock, self.fail_start, self.fail_close = clock, fail_start, fail_close
        self.bad_quality, self.drop_value, self.revision = bad_quality, drop_value, revision
        self.started = self.closed = False
        self.calls = 0
        self.edge = False
        self.timer_start = None
        self.values = {"dut.Enable": False, "dut.Reset": False, "dut.Value": 0,
                       "dut.Count": 0, "dut.Done": False, "dut.state.Previous": False}

    def start(self):
        self.started = True
        if self.fail_start:
            raise RuntimeError("Native power-on failed")
        return {"isolation": "fresh_owned_instance", "revision": self.revision, "instanceId": 0}

    def write(self, values):
        self.values.update(values)

    def call(self, target):
        self.calls += 1
        self.clock.sleep(0.001)
        enabled = self.values["dut.Enable"]
        if self.values["dut.Reset"]:
            self.values["dut.Count"] = 0
        elif enabled and not self.edge:
            self.values["dut.Count"] += 1
        if enabled and self.timer_start is None:
            self.timer_start = self.clock.time()
        if not enabled:
            self.timer_start = None
        self.values["dut.Done"] = self.timer_start is not None and self.clock.time() - self.timer_start >= 0.1
        self.edge = enabled
        self.values["dut.state.Previous"] = enabled
        return self.read()

    def read(self):
        values = copy.deepcopy(self.values)
        if self.drop_value:
            values.pop("dut.Count")
        return {"values": values, "quality": "bad" if self.bad_quality else "good", "timestampUtc": "2026-10-08T10:00:00Z"}

    def close(self):
        self.closed = True
        if self.fail_close:
            raise RuntimeError("Cannot unregister")
        return {"completed": True}


def exact(path, value):
    return {"kind": "exact", "path": path, "expected": value}


def suite(steps, **case_options):
    return {"fixtureId": "executor-test", "cases": [{"id": "case", "steps": steps, **case_options}]}


class ExecutorTests(unittest.TestCase):
    def test_elapsed_uses_measured_call_bounds_including_transport_and_persistence(self):
        assertion = {"kind": "elapsed", "path": "dut.Count", "sinceStep": "start",
                     "capMs": 1000, "resolutionMs": 1, "maxUncertaintyMs": 500}
        origin = {"values": {"dut.Count": 0}, "operation": "call", "hostIntervalMs": [0, 150]}
        current = {"values": {"dut.Count": 239}, "operation": "call", "hostIntervalMs": [240, 493]}
        result = evaluate_assertion(assertion, origin, current, {"start": origin})
        self.assertEqual("passed", result["status"])
        self.assertEqual([90, 493], result["measuredSeparationMs"])
        self.assertEqual([89, 494], result["expectedIntervalMs"])
        for bad in (0, 60, 700):
            current["values"]["dut.Count"] = bad
            self.assertEqual("failed", evaluate_assertion(assertion, origin, current, {"start": origin})["status"])

    def test_elapsed_blocks_missing_or_excessively_uncertain_timing(self):
        assertion = {"kind": "elapsed", "path": "dut.Count", "sinceStep": "start",
                     "capMs": 1000, "resolutionMs": 1, "maxUncertaintyMs": 100}
        origin = {"values": {"dut.Count": 0}, "operation": "call", "hostIntervalMs": [0, 150]}
        current = {"values": {"dut.Count": 239}, "operation": "call", "hostIntervalMs": [240, 493]}
        self.assertEqual("blocked", evaluate_assertion(assertion, origin, current, {})["status"])
        self.assertEqual("blocked", evaluate_assertion(assertion, origin, current, {"start": origin})["status"])
        current["hostIntervalMs"] = [100, 200]
        with self.assertRaises(ValueError):
            evaluate_assertion(assertion, origin, current, {"start": origin})

    def test_elapsed_preflight_rejects_wrong_origin_repeated_calls_and_cross_instance(self):
        item = {"kind": "elapsed", "path": "dut.Count", "sinceStep": "start",
                "capMs": 1000, "resolutionMs": 1, "maxUncertaintyMs": 100}
        spec = suite([{"id": "start"}, {"id": "elapsed", "assertions": [item]}])
        validate_suite(spec, PROGRAM)
        nanoseconds = copy.deepcopy(PROGRAM)
        nanoseconds["bindings"]["dut"]["Count"]["unit"] = "ns"
        with self.assertRaises(ValueError): validate_suite(spec, nanoseconds)
        for changed in ("missing", "repeated", "duplicate"):
            bad = copy.deepcopy(spec)
            if changed == "missing": bad["cases"][0]["steps"][1]["assertions"][0]["sinceStep"] = "later"
            if changed == "repeated": bad["cases"][0]["steps"][0]["calls"] = 2
            if changed == "duplicate": bad["cases"][0]["steps"][1]["id"] = "start"
            with self.assertRaises(ValueError): validate_suite(bad, PROGRAM)

    def run_suite(self, specification, *, case_selection=None, **backend_options):
        clock, backends, persisted = Clock(), [], []

        def factory(case):
            backend = Backend(clock, **backend_options)
            backends.append(backend)
            return backend

        result = execute_suite(specification, PROGRAM, factory, REVISION, persisted.append,
                               clock=clock.time, sleep=clock.sleep, case_selection=case_selection)
        self.assertTrue(all(backend.closed for backend in backends))
        self.assertEqual(result, persisted[-1])
        return result, backends

    def test_edges_held_input_pulse_reset_and_repeated_calls(self):
        report, backends = self.run_suite(suite([
            {"set": {"dut.Enable": True}, "assertions": [exact("dut.Count", 1)]},
            {"calls": 5, "assertions": [exact("dut.Count", 1)]},
            {"set": {"dut.Enable": False}, "calls": 1},
            {"calls": 0, "pulse": {"path": "dut.Enable"}, "assertions": [exact("dut.Count", 2)]},
            {"set": {"dut.Reset": True}, "assertions": [exact("dut.Count", 0)]}]))
        self.assertEqual("passed", report["status"])
        self.assertEqual(10, backends[0].calls)
        self.assertFalse(report["scanQualified"])

    def test_each_case_and_repeat_has_fresh_retained_state(self):
        spec = suite([{"set": {"dut.Enable": True}, "assertions": [exact("dut.Count", 1)]}], repeat=3)
        second = copy.deepcopy(spec["cases"][0])
        second.update(id="second", repeat=1)
        spec["cases"].append(second)
        report, backends = self.run_suite(spec)
        self.assertEqual("passed", report["status"])
        self.assertEqual(4, len(backends))
        self.assertEqual(4, len({id(item) for item in backends}))

    def test_selection_preserves_suite_identity_and_catalog_order(self):
        spec = suite([{"set": {"dut.Enable": True}, "assertions": [exact("dut.Count", 1)]}], repeat=3)
        second = copy.deepcopy(spec["cases"][0])
        second.update(id="second", repeat=2)
        spec["cases"].append(second)
        original = copy.deepcopy(spec)
        selection = [("second", 2), ("case", 3), ("case", 1)]
        report, backends = self.run_suite(spec, case_selection=selection)
        expected = [{"caseId": "case", "repetition": 1}, {"caseId": "case", "repetition": 3},
                    {"caseId": "second", "repetition": 2}]
        self.assertEqual("passed", report["status"])
        self.assertEqual("selected_cases", report["qualificationScope"])
        self.assertTrue(report["selectedCasesPassed"])
        self.assertFalse(report["runtimeQualified"])
        self.assertFalse(report["scanQualified"])
        self.assertFalse(report["fullSuiteCoverage"])
        self.assertEqual(expected, report["caseCoverage"]["selected"])
        self.assertEqual(expected, report["caseCoverage"]["attempted"])
        self.assertEqual(expected, report["caseCoverage"]["passed"])
        self.assertEqual(5, report["caseCoverage"]["expectedCount"])
        self.assertEqual(3, report["caseCoverage"]["selectedCount"])
        self.assertEqual([], report["caseCoverage"]["unattemptedSelected"])
        self.assertTrue(report["caseCoverage"]["selectedExecutionComplete"])
        self.assertEqual([(row["caseId"], row["repetition"]) for row in expected],
                         [(row["id"], row["repetition"]) for row in report["cases"]])
        self.assertEqual(3, len({id(backend) for backend in backends}))
        self.assertEqual(original, spec)
        self.assertEqual(revision_hash(original), report["suiteSha256"])
        self.assertEqual(REVISION, report["programRevision"])
        self.assertEqual([("second", 2), ("case", 3), ("case", 1)], selection)

    def test_default_full_suite_retains_qualification_behavior(self):
        spec = suite([{"assertions": [exact("dut.Count", 0)]}], repeat=2)
        report, backends = self.run_suite(spec)
        self.assertEqual("passed", report["status"])
        self.assertEqual("full_suite", report["qualificationScope"])
        self.assertTrue(report["runtimeQualified"])
        self.assertTrue(report["selectedCasesPassed"])
        self.assertTrue(report["fullSuiteCoverage"])
        self.assertTrue(report["caseCoverage"]["fullSuiteSelected"])
        self.assertTrue(report["caseCoverage"]["fullSuiteAttempted"])
        self.assertTrue(report["caseCoverage"]["fullSuitePassed"])
        self.assertEqual(2, len(backends))

    def test_explicit_all_case_selection_still_does_not_claim_suite_qualification(self):
        spec = suite([{"assertions": [exact("dut.Count", 0)]}], repeat=2)
        report, _ = self.run_suite(spec, case_selection=(("case", 2), ("case", 1)))
        self.assertEqual("passed", report["status"])
        self.assertTrue(report["selectedCasesPassed"])
        self.assertTrue(report["fullSuiteCoverage"])
        self.assertEqual("selected_cases", report["qualificationScope"])
        self.assertFalse(report["runtimeQualified"])
        self.assertFalse(report["scanQualified"])

    def test_invalid_selections_fail_before_persist_or_backend(self):
        spec = suite([{"assertions": [exact("dut.Count", 0)]}], repeat=2)
        invalid = [[], (), {}, "case", True, 1, {("case", 1)}, [("case", 1), ("case", 1)],
                   [("unknown", 1)], [("case", 0)], [("case", 3)], [("case", True)], [("case", 1.0)],
                   [("case", "1")], [(1, 1)], [["case", 1]], [{"caseId": "case", "repetition": 1}],
                   ["case"], [("case",)], [("case", 1, 2)], [None], [("case", 1)] * 3]
        for selection in invalid:
            with self.subTest(selection=selection), self.assertRaises(ValueError):
                execute_suite(spec, PROGRAM, lambda case: self.fail("Backend must not start"), REVISION,
                              persist=lambda report: self.fail("Invalid selection must not be persisted"),
                              case_selection=selection)

    def test_unselected_cases_still_receive_full_validation(self):
        spec = suite([{"assertions": [exact("dut.Count", 0)]}])
        spec["cases"].append({"id": "unselected", "steps": [
            {"set": {"dut.Value": 32768}, "assertions": [exact("dut.Count", 0)]}]})
        with self.assertRaises(ValueError):
            execute_suite(spec, PROGRAM, lambda case: self.fail("Backend must not start"), REVISION,
                          case_selection=[("case", 1)])

    def test_selected_cleanup_failure_records_unattempted_coverage(self):
        spec = suite([{"assertions": [exact("dut.Count", 0)]}], repeat=3)
        report, backends = self.run_suite(spec, case_selection=[("case", 2), ("case", 3)], fail_close=True)
        self.assertEqual("failed", report["status"])
        self.assertEqual("selected_cases", report["qualificationScope"])
        self.assertFalse(report["selectedCasesPassed"])
        self.assertFalse(report["fullSuiteCoverage"])
        self.assertFalse(report["runtimeQualified"])
        self.assertEqual(1, len(backends))
        self.assertEqual([{"caseId": "case", "repetition": 2}], report["caseCoverage"]["attempted"])
        self.assertEqual([{"caseId": "case", "repetition": 3}], report["caseCoverage"]["unattemptedSelected"])
        self.assertEqual([], report["caseCoverage"]["passed"])
        self.assertFalse(report["caseCoverage"]["selectedExecutionComplete"])

    def test_selected_failure_and_block_are_never_passing_coverage(self):
        spec = suite([{"assertions": [exact("dut.Count", 1)]}], repeat=2)
        for options, expected_status in (({}, "failed"), ({"fail_start": True}, "blocked")):
            with self.subTest(status=expected_status):
                report, _ = self.run_suite(spec, case_selection=[("case", 2)], **options)
                self.assertEqual(expected_status, report["status"])
                self.assertFalse(report["selectedCasesPassed"])
                self.assertFalse(report["fullSuiteCoverage"])
                self.assertEqual([], report["caseCoverage"]["passed"])
                self.assertTrue(report["caseCoverage"]["selectedExecutionComplete"])

    def test_in_progress_snapshots_do_not_claim_selected_success(self):
        clock, persisted = Clock(), []
        spec = suite([{"assertions": [exact("dut.Count", 0)]}], repeat=2)
        result = execute_suite(spec, PROGRAM, lambda case: Backend(clock), REVISION, persisted.append,
                               clock=clock.time, sleep=clock.sleep, case_selection=[("case", 2)])
        self.assertTrue(result["selectedCasesPassed"])
        self.assertTrue(all(row["qualificationScope"] == "selected_cases" for row in persisted))
        self.assertTrue(all(not row["selectedCasesPassed"] and not row["fullSuiteCoverage"]
                            for row in persisted if "completedAtUtc" not in row))

    def test_uncertain_runtime_exception_preserves_cleanup_and_stops_selection(self):
        clock, backends = Clock(), []
        cleanup = {"completed": False, "runtimeStateUncertain": True,
                   "admission": "stopped_due_to_uncertain_runtime", "release": {"unregistered": True},
                   "runtimeOutcomes": [{"kind": "card_restore_failed", "reconciled": False}]}

        class Uncertain(RuntimeError):
            def __init__(self):
                super().__init__("Unresolved runtime outcome.")
                self.cleanup = cleanup

        class UncertainBackend(Backend):
            def close(self):
                self.closed = True
                raise Uncertain()

        def factory(case):
            backend = UncertainBackend(clock)
            backends.append(backend)
            return backend

        report = execute_suite(suite([{"assertions": [exact("dut.Count", 0)]}], repeat=3), PROGRAM,
                               factory, REVISION, clock=clock.time, sleep=clock.sleep,
                               case_selection=[("case", 2), ("case", 3)])
        self.assertEqual(1, len(backends))
        self.assertTrue(backends[0].closed)
        self.assertEqual("failed", report["status"])
        self.assertEqual("stopped_due_to_uncertain_runtime", report["statusReason"])
        self.assertEqual("stopped_due_to_uncertain_runtime", report["admission"])
        self.assertEqual(cleanup["runtimeOutcomes"], report["cases"][0]["cleanup"]["runtimeOutcomes"])
        self.assertTrue(report["cases"][0]["cleanup"]["release"]["unregistered"])
        self.assertFalse(report["selectedCasesPassed"])
        self.assertEqual([{"caseId": "case", "repetition": 3}], report["caseCoverage"]["unattemptedSelected"])
        self.assertNotIn("error", cleanup)

    def test_returned_uncertain_cleanup_cannot_claim_completion_or_continue(self):
        for completed in (False, True):
            with self.subTest(completed=completed):
                clock, backends = Clock(), []

                class UncertainBackend(Backend):
                    def close(self):
                        self.closed = True
                        return {"completed": completed, "runtimeStateUncertain": True,
                                "runtimeOutcomes": [{"kind": "create_outcome_unknown", "reconciled": False}]}

                def factory(case):
                    backend = UncertainBackend(clock)
                    backends.append(backend)
                    return backend

                report = execute_suite(suite([{"assertions": [exact("dut.Count", 0)]}], repeat=2),
                                       PROGRAM, factory, REVISION, clock=clock.time, sleep=clock.sleep)
                self.assertEqual(1, len(backends))
                self.assertEqual("stopped_due_to_uncertain_runtime", report["statusReason"])
                self.assertTrue(report["cases"][0]["cleanup"]["runtimeStateUncertain"])
                self.assertFalse(report["cases"][0]["cleanup"]["completed"])
                self.assertFalse(report["runtimeQualified"])

    def test_failure_preserves_inputs_expectation_actual_state_revision_and_time(self):
        report, _ = self.run_suite(suite([{"set": {"dut.Enable": True}, "assertions": [exact("dut.Count", 2)]}]))
        self.assertEqual("failed", report["status"])
        evidence = report["cases"][0]["failures"][0]
        self.assertEqual({"dut.Enable": True}, evidence["inputs"])
        self.assertEqual(2, evidence["outcome"]["assertion"]["expected"])
        self.assertEqual(1, evidence["outcome"]["actual"])
        self.assertTrue(evidence["snapshot"]["values"]["dut.state.Previous"])
        self.assertEqual(REVISION, evidence["programRevision"])
        self.assertIn("timestampUtc", evidence)

    def test_transition_requires_both_endpoints(self):
        assertion = {"kind": "transition", "path": "dut.Count", "from": 0, "to": 1}
        report, _ = self.run_suite(suite([{"set": {"dut.Enable": True}, "assertions": [assertion]}]))
        self.assertEqual("passed", report["status"])
        assertion["from"] = 8
        report, _ = self.run_suite(suite([{"set": {"dut.Enable": True}, "assertions": [assertion]}]))
        self.assertEqual("failed", report["status"])

    def test_scan_requirement_blocks_before_start(self):
        spec = suite([{"assertions": [exact("dut.Count", 0)]}])
        spec["requireScanSynchronization"] = True
        report, backends = self.run_suite(spec)
        self.assertEqual("blocked", report["status"])
        self.assertFalse(backends[0].started)
        self.assertFalse(report["runtimeQualified"])

    def test_startup_failure_still_releases_id_zero(self):
        report, backends = self.run_suite(suite([{"assertions": [exact("dut.Count", 0)]}]), fail_start=True)
        self.assertEqual("blocked", report["status"])
        self.assertTrue(backends[0].closed)
        self.assertIn("Native power-on", report["cases"][0]["error"]["message"])

    def test_cleanup_failure_stops_repetitions_and_revokes_pass(self):
        report, backends = self.run_suite(suite([{"assertions": [exact("dut.Count", 0)]}], repeat=3), fail_close=True)
        self.assertEqual("failed", report["status"])
        self.assertEqual(1, len(backends))
        self.assertFalse(report["runtimeQualified"])

    def test_missing_or_bad_values_cannot_pass(self):
        for option in ("bad_quality", "drop_value"):
            with self.subTest(option=option):
                report, _ = self.run_suite(suite([{"assertions": [exact("dut.Count", 0)]}]), **{option: True})
                self.assertEqual("blocked", report["status"])

    def test_loaded_revision_mismatch_blocks(self):
        report, _ = self.run_suite(suite([{"assertions": [exact("dut.Count", 0)]}]), revision={"wrong": "revision"})
        self.assertEqual("blocked", report["status"])

    def test_window_observations_include_bracket_and_limitations(self):
        assertion = {"kind": "window", "path": "dut.Done", "expected": True, "minMs": 50, "maxMs": 200, "pollMs": 20}
        report, _ = self.run_suite(suite([{"set": {"dut.Enable": True}, "assertions": [assertion]}]))
        self.assertEqual("passed", report["status"])
        result = report["cases"][0]["steps"][0]["assertions"][0]
        self.assertEqual("host_observation", result["timingBasis"])
        self.assertGreaterEqual(result["transitionBracketMs"][0], 50)
        self.assertLessEqual(result["transitionBracketMs"][1], 200)
        self.assertFalse(result["scanSynchronized"])

    def test_window_boundary_overlap_is_blocked(self):
        assertion = {"kind": "window", "path": "dut.Done", "expected": True, "minMs": 100, "maxMs": 200, "pollMs": 20}
        report, _ = self.run_suite(suite([{"set": {"dut.Enable": True}, "assertions": [assertion]}]))
        self.assertEqual("blocked", report["status"])

    def test_window_timeout_and_too_early_value_fail(self):
        for bounds in ((0, 50), (150, 200)):
            with self.subTest(bounds=bounds):
                assertion = {"kind": "window", "path": "dut.Done", "expected": True, "minMs": bounds[0], "maxMs": bounds[1]}
                report, _ = self.run_suite(suite([{"set": {"dut.Enable": True}, "assertions": [assertion]}]))
                self.assertEqual("failed", report["status"])

    def test_case_duration_bound(self):
        report, _ = self.run_suite(suite([{"waitMs": 50, "assertions": [exact("dut.Count", 0)]}], maxDurationMs=20))
        self.assertEqual("failed", report["status"])
        self.assertEqual("TimeoutError", report["cases"][0]["error"]["type"])

    def test_all_steps_validated_before_any_backend_created(self):
        for invalid in ({"set": {"dut.Count": 1}}, {"set": {"dut.Value": 32768}}, {"calls": True}, {"target": "missing"}):
            with self.subTest(invalid=invalid):
                spec = suite([{"assertions": [exact("dut.Count", 0)]}, invalid])
                with self.assertRaises(ValueError):
                    execute_suite(spec, PROGRAM, lambda case: self.fail("Backend must not start"), REVISION)

    def test_exact_rejects_boolean_as_integer_and_tolerance_preserves_uint64(self):
        result = evaluate_assertion({"kind": "exact", "path": "X", "expected": 1}, {}, {"values": {"X": True}})
        self.assertEqual("failed", result["status"])
        assertion = {"kind": "approx", "path": "X", "expected": 2**64 - 1, "absoluteTolerance": 0}
        result = evaluate_assertion(assertion, {}, {"values": {"X": 2**64 - 2}})
        self.assertEqual("failed", result["status"])
        self.assertEqual("1", result["delta"])

    def test_negative_nan_and_implicit_tolerances_are_rejected(self):
        for addition in ({}, {"absoluteTolerance": -1}, {"absoluteTolerance": float("nan")}, {"absoluteTolerance": True}):
            with self.subTest(addition=addition):
                assertion = {"kind": "approx", "path": "dut.Count", "expected": 0, **addition}
                with self.assertRaises(ValueError):
                    validate_suite(suite([{"assertions": [assertion]}]), PROGRAM)

    def test_read_only_repeats_are_included_in_global_budget(self):
        spec = suite([{"calls": 0, "assertions": [exact("dut.Count", 0)]}] * 1000, repeat=100)
        with self.assertRaisesRegex(ValueError, "operations"):
            validate_suite(spec, PROGRAM)

    def test_string_capacity_checks_wide_utf16_units_before_backend(self):
        from block_test_executor import check_value
        with self.assertRaisesRegex(ValueError, "capacity"):
            check_value("123", {"type": "string", "capacity": 2}, "text")
        with self.assertRaisesRegex(ValueError, "capacity"):
            check_value("\U0001f600", {"type": "wstring", "capacity": 1}, "text")

    def test_scan_capability_does_not_qualify_timed_polling(self):
        clock = Clock()
        backend = Backend(clock)
        backend.capabilities = {"applicationCallControl": True, "scanSynchronized": True, "timedPolling": True}
        spec = suite([{"assertions": [exact("dut.Count", 0)]}])
        spec["timingMode"] = "timed_polling"
        report = execute_suite(spec, PROGRAM, lambda case: backend, REVISION, clock=clock.time, sleep=clock.sleep)
        self.assertEqual("passed", report["status"])
        self.assertFalse(report["scanQualified"])
        spec["timingMode"] = "scan_controlled"
        backend = Backend(clock)
        backend.capabilities = {"scanSynchronized": True}
        report = execute_suite(spec, PROGRAM, lambda case: backend, REVISION, clock=clock.time, sleep=clock.sleep)
        self.assertEqual("blocked", report["status"])
        self.assertIn("activated synchronization", report["cases"][0]["error"]["message"])

    def test_wide_string_stimuli_respect_adapter_write_limit(self):
        program = copy.deepcopy(PROGRAM)
        program["bindings"]["dut"]["Text"] = {"type": "wstring", "capacity": 8000, "writable": True}
        spec = suite([{"set": {"dut.Text": "x" * 4097}, "assertions": [exact("dut.Count", 0)]}])
        with self.assertRaisesRegex(ValueError, "4096-character"):
            validate_suite(spec, program)


if __name__ == "__main__":
    unittest.main()
