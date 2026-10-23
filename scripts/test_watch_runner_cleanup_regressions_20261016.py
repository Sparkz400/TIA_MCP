"""Independent actual watch-main/close-path tests; every process is synthetic."""
import copy
import importlib
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import Mock, patch

from test_startup_lifecycle_regressions_20261016 import (
    FakeApi, GuardedCase, World, exact, life, process_module, run_review)

runner = importlib.import_module("qualify_watch_reindexed_mcp")


class WatchRunnerCleanupRegressions(GuardedCase):
    def make_run(self):
        w = World(self)
        w.start()
        run = runner.Run.__new__(runner.Run)
        run.root = w.folder
        run.args = SimpleNamespace(mode="reorder", worker=w.folder / "TiaMcp.Openness.V21.exe")
        run.client = w.client()
        run.clients = [{"identity": copy.deepcopy(w.host.info)}]
        run.lifetimes, run.native, run.native_cleanup_helper = [], None, None
        monitor_apis, tracked = [], {}
        # Baseline inherited close_host owns an additional legacy monitor. The
        # repaired override uses only ExactClient's tree; do not invent unused
        # handles in the replacement architecture and then call them leaks.
        legacy_monitor = runner.Run.close_host is runner.workflow.Run.close_host
        for process in ((w.worker, w.processes[w.NATIVE]) if legacy_monitor else ()):
            api = FakeApi(process.pid)
            watcher = process_module.OwnedTiaProcessWatcher(process.pid, api=api, query_exit_code=True)
            tracked[(process.pid, process.info["created"])] = (copy.deepcopy(process.info), watcher)
            monitor_apis.append(api)
        run.monitor = Mock(), Mock(), tracked
        run.monitor[1].is_alive.return_value = False
        def eof():
            w.all_terminal()
            for api in monitor_apis:
                api.terminal = True
        run.client.process.stdin.close.side_effect = eof
        run.save = Mock(side_effect=lambda name, value: value)
        run.execute = Mock()
        return w, run, monitor_apis

    def invoke(self, run, print_error=None):
        argv = ["qualify_watch_reindexed_mcp.py", "--host", "SYNTHETIC-HOST.exe",
                "--worker", "SYNTHETIC-WORKER.exe", "--root", str(run.root),
                "--seed", "SYNTHETIC-SEED", "--mode", "reorder"]
        caught = None
        with patch.object(sys, "argv", argv), patch.object(runner, "Run", return_value=run), \
             patch.object(runner.workflow, "oracle", runner.workflow.oracle), \
             patch.object(runner.workflow, "Client", runner.workflow.Client):
            try:
                if print_error:
                    with patch("builtins.print", side_effect=print_error):
                        runner.main()
                else:
                    runner.main()
            except BaseException as error:
                caught = error
        return caught

    def assert_cleanup(self, w, monitor_apis):
        w.assert_handles(True)
        for api in monitor_apis:
            self.assertTrue(api.closed)
            self.assertEqual(api.closes, 1)
            self.assertEqual(api.violations, [])

    def test_main_keyboard_interrupt_runs_actual_host_cleanup(self):
        w, run, apis = self.make_run()
        run.execute.side_effect = KeyboardInterrupt("request boundary")
        error = self.invoke(run)
        self.assertIsInstance(error, KeyboardInterrupt)
        self.assert_cleanup(w, apis)

    def test_main_system_exit_runs_actual_host_cleanup(self):
        w, run, apis = self.make_run()
        run.execute.side_effect = SystemExit(42)
        error = self.invoke(run)
        self.assertIsInstance(error, SystemExit)
        self.assertEqual(error.code, 42)
        self.assert_cleanup(w, apis)

    def test_failure_receipt_oserror_cannot_precede_and_bypass_cleanup(self):
        w, run, apis = self.make_run()
        run.execute.side_effect = RuntimeError("primary request failed")
        def receipt(name, value):
            if name == "runner-failure":
                raise OSError("failure receipt disk full")
            return value
        run.save.side_effect = receipt
        error = self.invoke(run)
        self.assertIsNotNone(error)
        self.assert_cleanup(w, apis)

    def test_failure_receipt_baseexception_cannot_bypass_cleanup(self):
        for interruption in (KeyboardInterrupt, SystemExit):
            with self.subTest(interruption=interruption.__name__):
                w, run, apis = self.make_run()
                run.execute.side_effect = RuntimeError("primary request failed")
                def receipt(name, value):
                    if name == "runner-failure":
                        raise interruption("receipt interrupted")
                    return value
                run.save.side_effect = receipt
                self.assertIsNotNone(self.invoke(run))
                self.assert_cleanup(w, apis)

    def test_summary_logging_interrupt_cannot_skip_cleanup(self):
        for interruption in (KeyboardInterrupt, SystemExit, OSError):
            with self.subTest(interruption=interruption.__name__):
                w, run, apis = self.make_run()
                # A still-present client must be cleaned even on an unexpected
                # execute return. The normal workflow usually closes it itself.
                run.execute.return_value = {"completed": True}
                self.assertIsNotNone(self.invoke(run, interruption("stdout failed")))
                self.assert_cleanup(w, apis)

    def test_success_return_with_retained_client_is_unconditionally_finalized(self):
        w, run, apis = self.make_run()
        run.execute.return_value = {"completed": True}
        error = self.invoke(run)
        self.assertIsNone(error)
        self.assert_cleanup(w, apis)

    def test_already_closed_success_does_not_close_again(self):
        w, run, apis = self.make_run()
        def execute():
            run.close_host()
            return {"completed": True}
        run.execute.side_effect = execute
        self.assertIsNone(self.invoke(run))
        self.assert_cleanup(w, apis)

    def test_request_exception_and_terminal_wait_interrupt_still_finalize(self):
        w, run, apis = self.make_run()
        run.execute.side_effect = RuntimeError("request failed")
        w.apis[w.NATIVE].wait_script.extend([KeyboardInterrupt(), SystemExit()])
        error = self.invoke(run)
        self.assertIsNotNone(error)
        self.assert_cleanup(w, apis)
        row = next(r for r in w.tree.snapshot() if r["role"] == "native")
        self.assertTrue(row["proof"]["deadlineOrCancellationObserved"])

    def test_actual_close_host_rejects_late_tree_proof_as_normal_qualification(self):
        w, run, apis = self.make_run()
        w.apis[w.NATIVE].wait_script.append(KeyboardInterrupt())
        with self.assertRaises((AssertionError, RuntimeError)):
            run.close_host()
        self.assert_cleanup(w, apis)

    def test_actual_close_host_rejects_incomplete_tree_proof_as_normal_qualification(self):
        w, run, apis = self.make_run()
        w.apis[w.NATIVE].code_script.append(OSError("exit code unavailable"))
        with self.assertRaises((AssertionError, RuntimeError)):
            run.close_host()
        self.assertTrue(w.apis[w.HOST].closed)
        self.assertTrue(w.apis[w.WORKER].closed)
        self.assertEqual(w.apis[w.NATIVE].violations, [])

    def test_actual_close_host_rejects_nonzero_tree_native_exit_as_normal_qualification(self):
        w, run, apis = self.make_run()
        w.apis[w.NATIVE].code = 8
        with self.assertRaises((AssertionError, RuntimeError)):
            run.close_host()
        self.assert_cleanup(w, apis)

    def test_actual_close_host_rejects_interrupted_eof_as_normal_qualification(self):
        w, run, apis = self.make_run()
        original_eof = run.client.process.stdin.close.side_effect
        def interrupted_eof():
            original_eof()
            raise KeyboardInterrupt("EOF interrupted after synthetic terminal signal")
        run.client.process.stdin.close.side_effect = interrupted_eof
        with self.assertRaises((AssertionError, RuntimeError)):
            run.close_host()
        self.assert_cleanup(w, apis)

    def test_exit_receipt_failure_during_main_cleanup_cannot_abandon_remaining_handles(self):
        w, run, apis = self.make_run()
        run.execute.side_effect = RuntimeError("request failed")
        with patch.object(life, "emit", side_effect=OSError("terminal receipt unavailable")):
            self.assertIsNotNone(self.invoke(run))
        self.assert_cleanup(w, apis)

    def test_start_host_receipt_failure_retains_client_for_actual_main_cleanup(self):
        w, run, apis = self.make_run()
        client = run.client
        run.client, run.clients = None, []
        run.args.host = w.folder / "TiaMcp.Host.exe"
        run.host_files = [{"path": str(run.args.host), "sha256": "synthetic"}]
        def receipt(name, value):
            if name == "host-01-start":
                raise OSError("host receipt disk full")
            return value
        run.save.side_effect = receipt
        run.execute.side_effect = lambda: run.start_host("none")
        with patch.object(runner, "ExactClient", return_value=client), \
             patch.object(runner.workflow, "digest", return_value="synthetic"):
            error = self.invoke(run)
        self.assertIsInstance(error, OSError)
        self.assertEqual(str(error), "host receipt disk full")
        self.assert_cleanup(w, apis)

    def test_actual_start_and_close_tia_with_two_native_lifetimes(self):
        # New/replaced start_tia and close_tia must exercise their real adapter
        # path, including the original retained row, not a second PID watcher.
        w = World(self, lazy=True)
        run = runner.Run.__new__(runner.Run)
        run.client, run.root = w.client(), w.folder
        run.args = SimpleNamespace(mode="reorder")
        run.lifetimes, run.native = [], None
        run.save = Mock(side_effect=lambda name, value: value)
        legacy_apis = []
        def legacy_factory(pid, **kwargs):
            api = FakeApi(pid)
            legacy_apis.append(api)
            return process_module.OwnedTiaProcessWatcher(pid, api=api, **kwargs)
        for pid in (6372, 6373):
            def reply(method, params):
                if params["name"] == "tia_start":
                    w.add_native(pid)
                    return {"structuredContent": {"processId": pid, "ownsPortalProcess": True, "projects": []}}
                w.apis[pid].terminal = True
                for api in legacy_apis:
                    if api.pid == pid:
                        api.terminal = True
                return {"structuredContent": {"detached": True}}
            with patch.object(exact.Client, "request", side_effect=reply), \
                 patch.object(runner.workflow, "OwnedTiaProcessWatcher", side_effect=legacy_factory):
                run.start_tia()
                run.close_tia()
        self.assertEqual([p["processId"] for p in run.lifetimes], [6372, 6373])
        self.assertTrue(all(p["completed"] for p in run.lifetimes))
        w.all_terminal()
        run.client.close()
        w.assert_handles(True)
        self.assertTrue(all(api.closes == 1 and not api.violations for api in legacy_apis))

    def test_failed_execute_is_preserved_when_cleanup_receipt_also_fails(self):
        w, run, apis = self.make_run()
        primary = KeyboardInterrupt("original cancellation")
        run.execute.side_effect = primary
        run.save.side_effect = OSError("all receipts unavailable")
        error = self.invoke(run)
        self.assertIs(error, primary)
        self.assert_cleanup(w, apis)

    def test_host_close_receipt_failure_still_finalizes_every_original_handle(self):
        for interruption in (KeyboardInterrupt, SystemExit, OSError):
            with self.subTest(interruption=interruption.__name__):
                w, run, apis = self.make_run()
                run.save.side_effect = interruption("close logging interrupted")
                try:
                    run.close_host()
                except BaseException:
                    pass
                self.assert_cleanup(w, apis)

    def test_exit_code_cancellation_during_main_cleanup_cannot_abandon_handle(self):
        for interruption in (KeyboardInterrupt, SystemExit):
            with self.subTest(interruption=interruption.__name__):
                w, run, apis = self.make_run()
                primary = RuntimeError("request failed")
                run.execute.side_effect = primary
                w.apis[w.NATIVE].code_script.append(interruption("terminal code interrupted"))
                self.assertIs(self.invoke(run), primary)
                self.assert_cleanup(w, apis)


if __name__ == "__main__":
    raise SystemExit(run_review(__name__))
