"""Cooperative-stop regressions; fake backends only, no Siemens operations."""
import json
import pathlib
import unittest
from unittest import mock

import run_block_tests as runner
from block_test_executor import execute_suite
from block_test_stop import StopRequest, remaining_selection
from test_block_test_executor import Backend, Clock, PROGRAM, REVISION, suite, exact
from test_block_test_stages import NativeClient, ProcessWatcher, temporary_directory, write_binaries


class StopTests(unittest.TestCase):
    def test_sentinel_latches_without_reading_or_removing_contents(self):
        with temporary_directory() as directory:
            path = pathlib.Path(directory) / "stop.json"
            request = StopRequest(path)
            self.assertFalse(request())
            path.write_bytes(b"not interpreted as instructions or JSON")
            self.assertTrue(request())
            self.assertTrue(path.exists())
            observed = request.evidence()["observedAtUtc"]
            path.unlink()
            self.assertTrue(request())
            self.assertEqual(observed, request.evidence()["observedAtUtc"])

    def test_stop_before_first_case_never_creates_backend(self):
        factory = mock.Mock()
        report = execute_suite(suite([{"calls": 1, "assertions": [exact("dut.Count", 0)]}]), PROGRAM, factory, REVISION, stop_requested=lambda: True)
        factory.assert_not_called()
        self.assertEqual("blocked", report["status"])
        self.assertEqual("stopped_by_request", report["admission"])
        self.assertFalse(report["runtimeQualified"])
        self.assertEqual(1, len(report["caseCoverage"]["unattemptedSelected"]))

    def test_stop_during_case_finishes_all_steps_and_cleanup_before_next_repeat(self):
        clock = Clock()
        requested = False
        backends, snapshots = [], []

        class StopDuringRead(Backend):
            def read(self):
                nonlocal requested
                requested = True
                return super().read()

        def factory(case):
            backend = StopDuringRead(clock)
            backends.append(backend)
            return backend

        report = execute_suite(suite([{"calls": 2}, {"calls": 3, "assertions": [exact("dut.Count", 0)]}], repeat=3), PROGRAM, factory, REVISION,
                               persist=snapshots.append, clock=clock.time, sleep=clock.sleep,
                               stop_requested=lambda: requested)
        self.assertEqual(1, len(backends))
        self.assertEqual(5, backends[0].calls)
        self.assertTrue(backends[0].closed)
        self.assertEqual("passed", report["cases"][0]["status"])
        self.assertEqual("blocked", report["status"])
        self.assertFalse(report["runtimeQualified"])
        self.assertEqual(1, report["caseCoverage"]["passedCount"])
        self.assertEqual([2, 3], [r["repetition"] for r in report["caseCoverage"]["unattemptedSelected"]])
        self.assertEqual(report, snapshots[-1])

    def test_failed_cleanup_takes_precedence_over_stop(self):
        clock = Clock()
        backend = Backend(clock, fail_close=True)
        report = execute_suite(suite([{"calls": 1, "assertions": [exact("dut.Count", 0)]}], repeat=2), PROGRAM, lambda case: backend, REVISION,
                               stop_requested=lambda: backend.started, clock=clock.time, sleep=clock.sleep)
        self.assertEqual("failed", report["status"])
        self.assertNotEqual("stopped_by_request", report.get("admission"))
        self.assertIn("error", report["cases"][0]["cleanup"])
        self.assertFalse(report["runtimeQualified"])

    def test_request_during_final_case_does_not_erase_completed_suite(self):
        clock = Clock()
        backend = Backend(clock)
        report = execute_suite(suite([{"calls": 1, "assertions": [exact("dut.Count", 0)]}]), PROGRAM, lambda case: backend, REVISION,
                               stop_requested=lambda: backend.started, clock=clock.time, sleep=clock.sleep)
        self.assertEqual("passed", report["status"])
        self.assertTrue(report["runtimeQualified"])

    def test_remaining_manifest_exact_hash_selection_failed_and_unattempted(self):
        authored = runner.load_suites(["runtime-boolean"])[0]
        selection = {authored["fixtureId"]: [("truth-0", 1), ("truth-1", 1), ("truth-7", 1)]}
        entries = [{"fixtureId": authored["fixtureId"], "caseCoverage": {
            "passed": [{"caseId": "truth-0", "repetition": 1}],
            "attempted": [{"caseId": "truth-0", "repetition": 1}, {"caseId": "truth-1", "repetition": 1}]}}]
        manifest = remaining_selection([authored], entries, runner.digest, selection)
        with temporary_directory() as directory:
            path = pathlib.Path(directory) / "remaining.json"
            path.write_text(json.dumps(manifest))
            selected, _ = runner.load_case_selection(path, [authored])
        self.assertEqual({"runtime-boolean": [("truth-1", 1), ("truth-7", 1)]}, selected)
        self.assertEqual(runner.digest(authored), manifest["suites"]["runtime-boolean"]["suiteSha256"])

    def test_no_remaining_manifest_when_all_selected_cases_have_passed(self):
        authored = runner.load_suites(["runtime-boolean"])[0]
        selected = {authored["fixtureId"]: [("truth-0", 1)]}
        entry = {"fixtureId": authored["fixtureId"], "caseCoverage": {"passed": [{"caseId": "truth-0", "repetition": 1}]}}
        self.assertIsNone(remaining_selection([authored], [entry], runner.digest, selected))

    def test_preexisting_stop_cli_never_calls_native_and_writes_remaining(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            host, worker, simulator = write_binaries(root)
            stop = root / "stop"
            stop.touch()
            output = root / "result"
            with mock.patch.object(runner, "qualify_suite") as native:
                result = runner.main(["--live", "--fixture", "runtime-boolean", "--host", str(host),
                    "--worker", str(worker), "--sim-worker", str(simulator), "--report-directory", str(output),
                    "--stop-file", str(stop), "--continue-on-failure"])
            native.assert_not_called()
            self.assertEqual(2, result)
            index = json.loads((output / "index.json").read_text())
            self.assertEqual("stopped_by_request", index["admission"])
            self.assertFalse(index["runtimeVerified"])
            self.assertTrue(index["stopRequest"]["requested"])
            selected, _ = runner.load_case_selection(output / "remaining-cases.json", runner.load_suites())
            self.assertEqual(8, len(selected["runtime-boolean"]))

    def test_cli_stop_during_suite_cannot_be_bypassed_by_continue_on_failure(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            host, worker, simulator = write_binaries(root)
            output = root / "result"
            stopped = {"status": "blocked", "admission": "stopped_by_request", "engineeringVerified": True,
                       "runtimeVerified": False, "caseCoverage": {"passed": []}}
            with mock.patch.object(runner, "qualify_suite", return_value=stopped) as native:
                result = runner.main(["--live", "--fixture", "runtime-boolean", "--fixture", "runtime-edges",
                    "--host", str(host), "--worker", str(worker), "--sim-worker", str(simulator),
                    "--report-directory", str(output), "--continue-on-failure"])
            self.assertEqual(1, native.call_count)
            self.assertEqual(2, result)
            index = json.loads((output / "index.json").read_text())
            self.assertEqual("stopped_by_request", index["admission"])
            self.assertEqual(2, len(json.loads((output / "remaining-cases.json").read_text())["suites"]))

    def test_stop_between_suites_keeps_completed_results_and_remaining_scope(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            host, worker, simulator = write_binaries(root)
            output = root / "result"
            def qualify(authored, *args, **kwargs):
                (output / "stop-request.json").touch()
                pairs = [{"caseId": c["id"], "repetition": r}
                         for c in authored["cases"] for r in range(1, c.get("repeat", 1) + 1)]
                return {"status": "passed", "engineeringVerified": True, "runtimeVerified": True,
                        "caseCoverage": {"passed": pairs}}
            with mock.patch.object(runner, "qualify_suite", side_effect=qualify) as native:
                result = runner.main(["--live", "--fixture", "runtime-boolean", "--fixture", "runtime-edges",
                    "--host", str(host), "--worker", str(worker), "--sim-worker", str(simulator),
                    "--report-directory", str(output)])
            self.assertEqual(1, native.call_count)
            self.assertEqual(2, result)
            index = json.loads((output / "index.json").read_text())
            self.assertEqual("passed", index["results"][0]["status"])
            self.assertEqual("stopped_by_request", index["admission"])
            self.assertFalse(index["runtimeVerified"])
            manifest = json.loads((output / "remaining-cases.json").read_text())
            self.assertNotIn(index["results"][0]["fixtureId"], manifest["suites"])
            self.assertEqual(1, len(manifest["suites"]))

    def test_native_qualification_forwards_stop_after_engineering_shutdown(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            authored = runner.load_suites(["runtime-boolean"])[0]
            host, worker, simulator = write_binaries(root)
            native = NativeClient(authored, root / "native", runtime=True)
            output = root / "result"
            runner.prepare_suite(authored, output)
            stop = mock.Mock(return_value=True)
            execute = mock.Mock(return_value={"status": "blocked", "runtimeQualified": False,
                "admission": "stopped_by_request", "cases": [], "caseCoverage": {"passed": []}})
            report = runner.qualify_suite(authored, output, host, 5, worker=worker, sim_worker=simulator,
                client_factory=native.factory, process_watcher_factory=ProcessWatcher, execute=execute,
                stop_requested=stop)
            self.assertIs(stop, execute.call_args.kwargs["stop_requested"])
            self.assertEqual("stopped_by_request", report["admission"])
            self.assertEqual(0, report["hostExitCode"])
            self.assertTrue(report["engineeringVerified"])
            self.assertFalse(report["runtimeVerified"])


if __name__ == "__main__":
    unittest.main()
