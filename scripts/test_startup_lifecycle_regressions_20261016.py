"""Independent actual-controller regressions with synthetic OS/process boundaries.

No Host/TIA/worker/helper is launched. Run with --source-dir pointing to a frozen
source directory and --results-dir pointing to a NEW directory. The second review
suite imports the harness here. Real controller methods and the real watcher are
used; only transport, process observations, handle APIs, clocks and receipts are
substituted. Historical PID55160 has NO assigned role in these tests.
"""
import argparse
from collections import deque
from contextlib import ExitStack, contextmanager, redirect_stdout, redirect_stderr
import copy
import ctypes
from datetime import datetime, timezone
import hashlib
import importlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[1]
REVIEW = REPO / "artifacts/startup-lifecycle-review-20261016"
_parser = argparse.ArgumentParser(add_help=False)
_parser.add_argument("--source-dir", type=Path, default=REPO / "scripts")
_parser.add_argument("--results-dir", type=Path)
_parser.add_argument("--test", action="append", default=[])
OPTIONS, _ = _parser.parse_known_args()
SOURCE = OPTIONS.source_dir.resolve()
sys.path.insert(0, str(SOURCE))
import retained_native_lifecycle as life
import exact_qualification_client as exact
import block_test_process as process_module


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


@contextmanager
def no_native_boundaries():
    """A test bug must fail before it touches a real process boundary."""
    def forbidden(*args, **kwargs):
        raise AssertionError("REAL PROCESS / NATIVE BOUNDARY FORBIDDEN")
    with ExitStack() as stack:
        for module, name in ((subprocess, "Popen"), (subprocess, "run"),
                             (os, "system"), (os, "kill"),
                             (life.psutil, "Process"), (life.psutil, "process_iter"),
                             (ctypes, "WinDLL"), (os, "startfile")):
            if hasattr(module, name):
                stack.enter_context(patch.object(module, name, side_effect=forbidden))
        yield


class FakeProcess:
    def __init__(self, pid, parent, name, folder, born=None):
        self.pid = pid
        self.info = dict(pid=pid, parentPid=parent, created=float(born or pid),
                         name=name, exe=str(folder / name), command=[str(folder / name)])
        self.descendants, self.ancestors = [], []
        self.identity_error = None
        self.create_time = Mock(side_effect=lambda: self.info["created"])
        self.name = Mock(side_effect=lambda: self.info["name"])
        self.ppid = Mock(side_effect=lambda: self.info["parentPid"])
        self.exe = Mock(side_effect=lambda: self.info["exe"])
        self.cmdline = Mock(side_effect=lambda: list(self.info["command"]))
        self.parents = Mock(side_effect=lambda: list(self.ancestors))
        self.children = Mock(side_effect=lambda recursive: list(self.descendants))


class FakeApi:
    def __init__(self, pid):
        self.pid, self.handle = pid, object()
        self.terminal = self.closed = False
        self.code, self.opens, self.closes = 0, 0, 0
        self.wait_script, self.code_script = deque(), deque()
        self.violations = []
        self.on_close = None

    def open_process(self, access, inherit, pid):
        assert pid == self.pid and not inherit
        self.opens += 1
        return self.handle

    def wait(self, handle, milliseconds):
        assert handle is self.handle
        if self.closed:
            # Record the illegal access, then let the test finish. This sentinel
            # is NOT a simulated Windows terminal proof. Every such access fails
            # the independent oracle, even if the controller reports success.
            self.violations.append("wait-after-close")
            return 0
        if milliseconds and self.wait_script:
            item = self.wait_script.popleft()
            if isinstance(item, BaseException):
                raise item
            if callable(item):
                return item()
            return item
        return 0 if self.terminal else 258

    def exit_code(self, handle):
        assert handle is self.handle and self.terminal
        if self.closed:
            self.violations.append("exit-code-after-close")
        if self.code_script:
            raise self.code_script.popleft()
        return self.code

    def close(self, handle):
        assert handle is self.handle and self.terminal, "live handle must remain retained"
        self.closes += 1
        if self.closed:
            self.violations.append("double-close")
        self.closed = True
        if self.on_close:
            self.on_close()
        return True

    def last_error(self):
        return 6


class ObservedRLock:
    """Actual RLock; signal contention so thread tests need no scheduling sleeps."""
    def __init__(self, contested):
        self.lock, self.contested = threading.RLock(), contested

    def acquire(self, *args, **kwargs):
        if self.lock.acquire(False):
            return True
        self.contested.set()
        return self.lock.acquire(*args, **kwargs)

    def release(self):
        self.lock.release()

    def __enter__(self):
        self.acquire()
        return self

    def __exit__(self, *args):
        self.release()


class World:
    HOST, WORKER, NATIVE = 56132, 60808, 6372

    def __init__(self, case, lazy=False):
        self.case = case
        REVIEW.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="mock-", dir=REVIEW)
        case.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        self.processes, self.watchers, self.apis = {}, {}, {}
        self.receipts = []
        self.host = self.make(self.HOST, 19996, "TiaMcp.Host.exe")
        self.worker = self.make(self.WORKER, self.HOST, "TiaMcp.Openness.V21.exe")
        self.worker.ancestors = [self.host]
        if not lazy:
            self.host.descendants = [self.worker]
        self.stack = ExitStack()
        case.addCleanup(self.stack.close)
        self.stack.enter_context(patch.object(life.psutil, "Process", side_effect=self.lookup))
        self.stack.enter_context(patch.object(life, "identity", side_effect=self.identity))
        self.stack.enter_context(patch.object(life, "emit", side_effect=self.receipt))
        self.tree = life.RetainedNativeTree(SimpleNamespace(pid=self.HOST),
            self.folder / "TiaMcp.Host.exe", self.folder / "TiaMcp.Openness.V21.exe",
            self.folder / "tree", watcher_factory=self.factory, binder=self.binder,
            start_thread=False)

    def make(self, pid, parent, name="Siemens.Automation.Portal.exe", born=None):
        process = FakeProcess(pid, parent, name, self.folder, born)
        self.processes[pid] = process
        return process

    def lookup(self, pid):
        if pid not in self.processes:
            raise life.psutil.NoSuchProcess(pid)
        return self.processes[pid]

    def identity(self, process):
        if process.identity_error:
            raise process.identity_error
        return copy.deepcopy(process.info)

    def receipt(self, path, value):
        self.receipts.append((str(path), copy.deepcopy(value)))

    def factory(self, pid, **kwargs):
        api = FakeApi(pid)
        watcher = process_module.OwnedTiaProcessWatcher(pid, api=api, **kwargs)
        self.apis[pid], self.watchers[pid] = api, watcher
        return watcher

    def binder(self, watcher, info):
        watcher.evidence.update(birthAndImageVerified=True,
            processStartedAtUtc="2026-10-16T00:00:00.0000000Z",
            originalFileTimeTicks=int(info["created"] * 10000000),
            imagePath=info["exe"], sameOsHandleRetained=True)

    def add_native(self, pid=NATIVE, parent=None):
        native = self.make(pid, self.WORKER if parent is None else parent)
        native.ancestors = [self.worker, self.host] if parent is None else [self.host]
        if self.worker not in self.host.descendants:
            self.host.descendants.insert(0, self.worker)
        self.host.descendants.append(native)
        self.worker.descendants.append(native)
        return native

    def start(self, pid=NATIVE):
        self.tree.begin_start()
        native = self.add_native(pid)
        return native, self.tree.accept_start(pid)

    def all_terminal(self):
        for api in self.apis.values():
            api.terminal = True

    def assert_handles(self, closed=None):
        for api in self.apis.values():
            self.case.assertEqual(api.opens, 1)
            self.case.assertEqual(api.violations, [], (api.pid, api.violations))
            if closed is not None:
                self.case.assertEqual(api.closes, int(closed), api.pid)

    def client(self):
        client = exact.ExactClient.__new__(exact.ExactClient)
        client.exact_tree = self.tree
        client.process = SimpleNamespace(pid=self.HOST, stdin=Mock(), stdout=Mock(),
            stderr=Mock(), returncode=0, poll=Mock(return_value=0),
            kill=Mock(side_effect=AssertionError("kill forbidden")))
        client.errors = []
        client.output_reader, client.error_reader = Mock(), Mock()
        for reader in (client.output_reader, client.error_reader):
            reader.is_alive.return_value = False
        return client


class GuardedCase(unittest.TestCase):
    def setUp(self):
        self.guard = no_native_boundaries()
        self.guard.__enter__()
        self.addCleanup(self.guard.__exit__, None, None, None)


class StartupLifecycleRegressions(GuardedCase):
    def test_observed_6372_clean_exit_does_not_exonerate_unknown_55160(self):
        w = World(self)
        _, row = w.start()
        w.apis[w.NATIVE].terminal = True
        proof = w.tree.close_record(row)
        self.assertTrue(proof["completed"])
        vanished = SimpleNamespace(pid=55160,
            create_time=Mock(side_effect=life.psutil.NoSuchProcess(55160)),
            name=Mock(side_effect=life.psutil.NoSuchProcess(55160)))
        w.host.descendants.append(vanished)
        try:
            w.tree.collect()
        except (RuntimeError, life.psutil.NoSuchProcess):
            pass
        self.assertTrue(w.tree.unknown, "unclassified disappearance must remain unknown")
        self.assertTrue(w.tree.errors)
        self.assertEqual(row["proof"], proof)
        self.assertNotIn(55160, w.apis)
        self.assertEqual(w.apis[w.HOST].closes, 0)
        self.assertEqual(w.apis[w.WORKER].closes, 0)

    def test_synthetic_non_target_name_can_be_filtered_before_birth_lookup(self):
        # Deliberately NOT PID55160: its historical name was never established.
        w = World(self)
        w.start()
        other = w.make(90001, w.HOST, "SyntheticNonTarget.exe")
        other.create_time.side_effect = life.psutil.NoSuchProcess(other.pid)
        w.host.descendants.append(other)
        w.tree.collect()
        self.assertFalse(w.tree.unknown)
        self.assertNotIn(other.pid, w.apis)
        other.parents.assert_not_called()

    def test_captured_terminated_child_uses_exact_birth_and_original_proof(self):
        w = World(self)
        native, row = w.start()
        w.apis[w.NATIVE].terminal = True
        proof = w.tree.close_record(row)
        native.identity_error = life.psutil.NoSuchProcess(native.pid)
        native.parents.side_effect = AssertionError("must not reconstruct terminal ancestry")
        w.tree.collect()
        self.assertEqual(row["proof"], proof)
        self.assertFalse(w.tree.unknown)
        w.assert_handles()

    def test_uncaptured_target_disappearance_remains_unknown(self):
        w = World(self)
        w.tree.begin_start()
        native = w.add_native()
        native.create_time.side_effect = life.psutil.NoSuchProcess(native.pid)
        with self.assertRaises((RuntimeError, life.psutil.NoSuchProcess)):
            w.tree.collect()
        self.assertTrue(w.tree.unknown)
        self.assertIsNotNone(w.tree.pending_start)
        self.assertNotIn(native.pid, w.apis)
        w.assert_handles(False)

    def test_disappeared_captured_child_with_live_handle_remains_unknown(self):
        w = World(self)
        native, _ = w.start()
        native.identity_error = life.psutil.NoSuchProcess(native.pid)
        with self.assertRaises(RuntimeError):
            w.tree.collect()
        self.assertTrue(w.tree.unknown)
        w.assert_handles(False)

    def test_pid_reuse_is_rejected_even_after_original_clean_exit(self):
        w = World(self)
        native, row = w.start()
        w.apis[w.NATIVE].terminal = True
        w.tree.close_record(row)
        native.info["created"] += 1
        native.info["name"] = "SyntheticNonTarget.exe"
        with self.assertRaises(RuntimeError):
            w.tree.collect()
        self.assertTrue(w.tree.unknown)
        self.assertEqual(len(w.tree.entries), 3)
        w.assert_handles()

    def test_changed_identity_at_same_pid_birth_is_rejected(self):
        for field, value in (("exe", "other.exe"), ("parentPid", 999),
                             ("command", ["changed"]), ("name", "Other.exe")):
            with self.subTest(field=field):
                w = World(self)
                native, _ = w.start()
                native.info[field] = value
                with self.assertRaises(RuntimeError):
                    w.tree.collect()
                self.assertTrue(w.tree.unknown)
                w.assert_handles(False)

    def test_owner_pid_reuse_cannot_be_adopted(self):
        w = World(self)
        w.tree.collect()
        w.host.info["created"] += 1
        with self.assertRaises(RuntimeError):
            w.tree.collect()
        self.assertTrue(w.tree.unknown)
        w.assert_handles(False)

    def test_identity_change_during_binding_keeps_handle_but_blocks_admission(self):
        w = World(self)
        w.tree.begin_start()
        native = w.add_native()
        original = w.tree.binder
        def changed(watcher, info):
            original(watcher, info)
            if info["pid"] == native.pid:
                native.info["created"] += 1
        w.tree.binder = changed
        with self.assertRaises(RuntimeError):
            w.tree.accept_start(native.pid)
        self.assertTrue(w.tree.unknown)
        self.assertEqual(len(w.tree.entries), 3)
        w.assert_handles(False)

    def test_missing_extra_and_wrong_native_reply_are_rejected(self):
        for pids, reply in (([], 6372), ([6372, 6373], 6372), ([6372], 6373)):
            with self.subTest(pids=pids, reply=reply):
                w = World(self)
                w.tree.begin_start()
                for pid in pids:
                    w.add_native(pid)
                with self.assertRaises(RuntimeError):
                    w.tree.accept_start(reply)
                self.assertIsNotNone(w.tree.pending_start)
                self.assertEqual(w.tree.accepted_native_keys, set())
                w.assert_handles(False)

    def test_wrong_worker_ancestry_is_rejected(self):
        w = World(self)
        w.tree.begin_start()
        w.add_native(parent=w.HOST)
        with self.assertRaises(RuntimeError):
            w.tree.accept_start(w.NATIVE)
        self.assertFalse(w.tree.native_inventory_complete())
        w.assert_handles(False)

    def test_extra_native_after_accepted_start_blocks_new_dispatch(self):
        w = World(self)
        w.start()
        w.add_native(6373)
        w.tree.collect()
        self.assertFalse(w.tree.native_inventory_complete())
        with self.assertRaises(RuntimeError):
            w.tree.begin_start()
        w.assert_handles(False)

    def test_actual_terminal_deadline_is_sticky_after_eventual_clean_exit(self):
        w = World(self)
        _, row = w.start()
        api = w.apis[w.NATIVE]
        def signal():
            self.assertEqual(api.closes, 0)
            api.terminal = True
            return 0
        api.wait_script.extend([258, signal])
        proof = w.tree.close_record(row, timeout_seconds=0)
        self.assertTrue(proof["completed"])
        self.assertEqual(proof["exitCode"], 0)
        self.assertTrue(proof["deadlineOrCancellationObserved"])
        w.all_terminal()
        final = w.tree.close_all()
        self.assertFalse(final["completed"])
        w.assert_handles(True)

    def test_lazy_worker_and_two_native_lifetimes_use_actual_client_methods(self):
        w = World(self, lazy=True)
        client = w.client()
        self.assertFalse(w.tree.worker_captured())
        for pid in (6372, 6373):
            def reply(method, params):
                if params["name"] == "tia_start":
                    self.assertIsNotNone(w.tree.pending_start)
                    w.add_native(pid)
                    return {"structuredContent": {"processId": pid}}
                w.apis[pid].terminal = True
                return {}
            with patch.object(exact.Client, "request", side_effect=reply):
                client.request("tools/call", {"name": "tia_start"})
                client.request("tools/call", {"name": "tia_detach"})
        w.all_terminal()
        close = client.close()
        self.assertTrue(close["tree"]["completed"])
        self.assertEqual(len(close["tree"]["exits"]), 4)
        self.assertEqual(len(w.tree.accepted_native_keys), 2)
        w.assert_handles(True)

    def test_incomplete_capture_holds_eof_through_cancellation(self):
        w = World(self, lazy=True)
        client = w.client()
        calls = []
        def advance(_):
            calls.append(1)
            client.process.stdin.close.assert_not_called()
            w.assert_handles(False)
            if len(calls) == 1:
                raise KeyboardInterrupt()
            if len(calls) == 2:
                raise SystemExit()
            self.assertEqual(len(calls), 3)
            # Synthetic late capture completion, not an absence-based exemption.
            w.tree.retain(w.worker, "worker", [copy.deepcopy(w.host.info)])
            w.all_terminal()
        with patch.object(life, "time", SimpleNamespace(sleep=advance, monotonic=lambda: 0)):
            close = client.close()
        self.assertFalse(close["tree"]["completed"])
        self.assertTrue(close["tree"]["startupInventoryUnknownReasons"])
        w.assert_handles(True)

    def test_cancelled_start_request_retains_unknown_intent_until_terminal_cleanup(self):
        for interruption in (KeyboardInterrupt, SystemExit):
            with self.subTest(interruption=interruption.__name__):
                w = World(self, lazy=True)
                client = w.client()
                def interrupted(*args):
                    self.assertIsNotNone(w.tree.pending_start)
                    w.add_native()
                    w.tree.collect()
                    raise interruption("request interrupted")
                with patch.object(exact.Client, "request", side_effect=interrupted):
                    with self.assertRaises(interruption):
                        client.request("tools/call", {"name": "tia_start"})
                w.assert_handles(False)
                w.all_terminal()
                close = client.close()
                self.assertFalse(close["tree"]["completed"])
                self.assertTrue(close["tree"]["startupOutcomeUnknown"])
                w.assert_handles(True)

    def test_terminal_wait_cancellation_and_failed_logging_still_retains_until_signal(self):
        w = World(self)
        api, watcher = w.apis[w.HOST], w.watchers[w.HOST]
        def signal():
            self.assertEqual(api.closes, 0)
            api.terminal = True
            return 0
        api.wait_script.extend([258, KeyboardInterrupt(), SystemExit(), signal])
        with patch("builtins.print", side_effect=OSError("stdout unavailable")):
            proof = life.wait_retained_terminal(watcher, timeout_seconds=0,
                clock=lambda: 1, report=Mock(side_effect=OSError("disk unavailable")))
        self.assertTrue(proof["completed"])
        self.assertTrue(proof["deadlineOrCancellationObserved"])
        self.assertFalse(proof["forcedTermination"])
        self.assertEqual(api.closes, 1)
        self.assertEqual(api.violations, [])

    def test_detach_rejects_late_nonzero_and_incomplete_actual_terminal_results(self):
        for mode in ("late", "nonzero", "incomplete"):
            with self.subTest(mode=mode):
                w = World(self)
                _, row = w.start()
                api = w.apis[w.NATIVE]
                api.terminal = True
                if mode == "nonzero":
                    api.code = 7
                elif mode == "late":
                    api.wait_script.append(KeyboardInterrupt())
                else:
                    api.code_script.append(OSError("exit query failed"))
                with patch.object(exact.Client, "request", return_value={}):
                    with self.assertRaises(RuntimeError):
                        w.client().request("tools/call", {"name": "tia_detach"})
                self.assertIsNotNone(row["proof"])
                with self.assertRaises(RuntimeError):
                    w.tree.begin_start()

    def test_start_receipt_failure_keeps_bound_handle_and_unknown_state(self):
        w = World(self)
        w.tree.begin_start()
        w.add_native()
        with patch.object(life, "emit", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                w.tree.accept_start(w.NATIVE)
        self.assertTrue(w.tree.unknown)
        self.assertIn(w.NATIVE, w.watchers)
        w.assert_handles(False)

    def test_exit_receipt_failure_cannot_skip_other_terminal_handle_cleanup(self):
        w = World(self)
        w.start()
        w.all_terminal()
        result, error = None, None
        with patch.object(life, "emit", side_effect=OSError("exit receipt unavailable")):
            try:
                result = w.tree.close_all()
            except BaseException as caught:
                error = caught
        w.assert_handles(True)
        self.assertTrue(error is not None or result["completed"] is False)

    def test_monitor_join_cancellation_cannot_skip_terminal_handle_finalization(self):
        for interruption in (KeyboardInterrupt, SystemExit):
            with self.subTest(interruption=interruption.__name__):
                w = World(self)
                w.start()
                w.all_terminal()
                w.tree.thread = Mock()
                w.tree.thread.join.side_effect = [interruption("monitor join interrupted"), None]
                w.tree.thread.is_alive.return_value = False
                result, error = None, None
                try:
                    result = w.tree.close_all()
                except BaseException as caught:
                    error = caught
                w.assert_handles(True)
                self.assertTrue(error is not None or result["completed"] is False,
                                "cleanup cancellation cannot qualify a normal lifetime")

    def test_exit_code_query_cancellation_cannot_return_with_unfinalized_handle(self):
        for interruption in (KeyboardInterrupt, SystemExit):
            with self.subTest(interruption=interruption.__name__):
                w = World(self)
                w.start()
                w.all_terminal()
                w.apis[w.NATIVE].code_script.append(interruption("terminal query interrupted"))
                result, error = None, None
                try:
                    result = w.tree.close_all()
                except BaseException as caught:
                    error = caught
                # The next API query would succeed. Returning while that exact
                # handle still has no code abandons the retained-cleanup barrier.
                w.assert_handles(True)
                self.assertTrue(error is not None or result["completed"] is False)

    def test_closehandle_cancellation_preserves_unknown_disposal_without_double_close(self):
        for interruption in (KeyboardInterrupt, SystemExit):
            with self.subTest(interruption=interruption.__name__):
                w = World(self)
                _, row = w.start()
                w.all_terminal()
                def interrupted_close():
                    # Simulate CloseHandle succeeding, then Python receiving a
                    # cancellation before the watcher records that success. The
                    # controller cannot assume disposal succeeded or retry it.
                    raise interruption("CloseHandle acknowledgement interrupted")
                w.apis[w.NATIVE].on_close = interrupted_close
                final = w.tree.close_all()
                self.assertFalse(final["completed"])
                self.assertFalse(row["proof"]["completed"])
                self.assertTrue(row["proof"]["processExitVerified"])
                self.assertFalse(row["proof"]["handleClosed"])
                w.tree.close_record(row)
                w.assert_handles(True)

    def test_closed_client_is_idempotent_and_cannot_dispatch_again(self):
        w = World(self)
        w.start()
        w.all_terminal()
        client = w.client()
        first = client.close()
        second = client.close()
        self.assertEqual(first, second)
        client.process.stdin.close.assert_called_once()
        with patch.object(exact.Client, "request", return_value={}) as transport:
            with self.assertRaises(RuntimeError):
                client.request("tools/call", {"name": "tia_session_status"})
            transport.assert_not_called()
        w.assert_handles(True)

    def test_cancelled_request_prohibits_later_transport_dispatch(self):
        for interruption in (KeyboardInterrupt, SystemExit):
            with self.subTest(interruption=interruption.__name__):
                w = World(self)
                w.start()
                client = w.client()
                with patch.object(exact.Client, "request", side_effect=interruption):
                    with self.assertRaises(interruption):
                        client.request("tools/call", {"name": "tia_session_status"})
                with patch.object(exact.Client, "request", return_value={}) as transport:
                    with self.assertRaises(RuntimeError):
                        client.request("tools/call", {"name": "tia_session_status"})
                    transport.assert_not_called()
                w.all_terminal()
                client.close()
                w.assert_handles(True)

    def test_discovery_and_worker_finalization_are_serialized_or_rechecked(self):
        w = World(self)
        w.tree.collect()
        worker_row = w.tree.one("worker")
        w.apis[w.WORKER].terminal = True
        entered, release, settled = threading.Event(), threading.Event(), threading.Event()
        w.tree.mutex = ObservedRLock(settled)
        errors = []
        def children(recursive):
            if threading.current_thread().name == "review-collector":
                entered.set()
                if not release.wait(3):
                    raise AssertionError("collector barrier timed out")
            return list(w.host.descendants)
        w.host.children.side_effect = children
        def collect():
            try:
                w.tree.collect()
            except BaseException as error:
                errors.append(error)
        def finalize():
            try:
                w.tree.close_record(worker_row)
            except BaseException as error:
                errors.append(error)
            finally:
                settled.set()
        collector = threading.Thread(target=collect, name="review-collector", daemon=True)
        closer = threading.Thread(target=finalize, name="review-finalizer", daemon=True)
        collector.start()
        self.assertTrue(entered.wait(3))
        closer.start()
        try:
            self.assertTrue(settled.wait(3), "finalizer neither finished nor serialized")
        finally:
            release.set()
            collector.join(3)
            closer.join(3)
        self.assertFalse(collector.is_alive() or closer.is_alive())
        self.assertEqual(errors, [])
        self.assertFalse(w.tree.unknown)
        w.assert_handles()

    def test_concurrent_close_record_cannot_wait_or_query_a_finalized_handle(self):
        w = World(self)
        _, row = w.start()
        w.apis[w.NATIVE].terminal = True
        entered, release, settled = threading.Event(), threading.Event(), threading.Event()
        w.tree.mutex = ObservedRLock(settled)
        errors, proofs = [], []
        def at_close():
            entered.set()
            if not release.wait(3):
                raise AssertionError("close barrier timed out")
        w.apis[w.NATIVE].on_close = at_close
        def finalize():
            try:
                proofs.append(w.tree.close_record(row))
            except BaseException as error:
                errors.append(error)
            finally:
                if threading.current_thread().name == "review-second-close":
                    settled.set()
        first = threading.Thread(target=finalize, name="review-first-close", daemon=True)
        second = threading.Thread(target=finalize, name="review-second-close", daemon=True)
        first.start()
        self.assertTrue(entered.wait(3))
        second.start()
        try:
            self.assertTrue(settled.wait(3), "second close neither finished nor serialized")
        finally:
            release.set()
            first.join(3)
            second.join(3)
        self.assertFalse(first.is_alive() or second.is_alive())
        self.assertEqual(errors, [])
        self.assertEqual(len(proofs), 2)
        self.assertTrue(all(p["completed"] for p in proofs))
        self.assertEqual(w.apis[w.NATIVE].closes, 1)
        w.assert_handles()


def run_review(module_name):
    """Immutable per-run text/JSON results, hashes before AND after execution."""
    output = OPTIONS.results_dir
    if output is None:
        output = REVIEW / ("run-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ"))
    output = output.resolve()
    if not output.is_relative_to(REVIEW.resolve()):
        raise ValueError("Results must stay within the assigned review directory")
    output.mkdir(parents=True, exist_ok=False)
    sources = sorted(SOURCE.glob("*.py"))
    before = {str(p): digest(p) for p in sources}
    test_paths = {Path(__file__).resolve(), Path(sys.argv[0]).resolve()}
    tests_before = {str(p): digest(p) for p in test_paths}
    loader = unittest.TestLoader()
    suite = (loader.loadTestsFromNames(OPTIONS.test, module=sys.modules[module_name])
             if OPTIONS.test else loader.loadTestsFromModule(sys.modules[module_name]))
    log = io.StringIO()
    with redirect_stdout(log), redirect_stderr(log):
        result = unittest.TextTestRunner(stream=log, verbosity=2).run(suite)
    after = {str(p): digest(p) for p in sources}
    tests_after = {str(p): digest(p) for p in test_paths}
    loaded = {name: str(Path(mod.__file__).resolve()) for name, mod in list(sys.modules.items())
              if getattr(mod, "__file__", None) and
              Path(mod.__file__).resolve().parent == SOURCE}
    payload = dict(mockedEvidenceOnly=True, nativeQualification=False,
        nativeProcessesLaunched=0, processTerminationPerformed=False,
        command=[sys.executable, *sys.argv], sourceDirectory=str(SOURCE),
        sourceHashesBefore=before, sourceHashesAfter=after, sourceStable=before == after,
        loadedControllerModules=loaded,
        testFileHashes=tests_after, testHashesBefore=tests_before,
        testsStable=tests_before == tests_after,
        testsRun=result.testsRun,
        passed=result.wasSuccessful() and before == after and tests_before == tests_after,
        failures=[dict(test=t.id(), traceback=tb) for t, tb in result.failures],
        errors=[dict(test=t.id(), traceback=tb) for t, tb in result.errors],
        skipped=[dict(test=t.id(), reason=reason) for t, reason in result.skipped])
    (output / "unittest.log").write_text(log.getvalue(), encoding="utf-8")
    (output / "result.json").write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(log.getvalue())
    print(json.dumps({k: payload[k] for k in ("testsRun", "passed", "sourceStable", "mockedEvidenceOnly")}))
    print("Results: " + str(output))
    return 0 if payload["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(run_review(__name__))
