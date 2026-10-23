"""Offline tests: every process/Win32 boundary is synthetic, never native dispatch."""
import io
import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import Mock, patch

import retained_native_lifecycle as life


class Watcher:
    def __init__(self, pid, **_):
        self.pid = pid; self._handle = object(); self.live = True; self.closes = 0
        self.evidence = {"processId": pid, "handleCaptured": True, "handleClosed": False}
        self.api = SimpleNamespace(wait=lambda *_: 258 if self.live else 0, exit_code=lambda _: 0)
    def close(self):
        assert not self.live
        self.closes += 1; self.evidence["handleClosed"] = True; return True


class RetainedTests(unittest.TestCase):
    def test_only_direct_owned_chain_is_collected_and_receipt_keeps_full_ancestry(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp).resolve()
            processes, watchers, make, host, worker, hp, wp, factory, binder = self.fixture(folder)
            native = make(3, 2, "Siemens.Automation.Portal.exe")
            native.ancestors = [worker, host]
            vanished_helper = make(4, 3, "UnknownTransient.exe")
            vanished_helper.name = Mock(side_effect=life.psutil.NoSuchProcess(4))
            host.children = Mock(side_effect=lambda recursive: [worker, native, vanished_helper] if recursive else [worker])
            worker.children = Mock(side_effect=lambda recursive: [native, vanished_helper] if recursive else [native])
            with patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), patch.object(life, "identity", side_effect=lambda p: dict(p.info)):
                tree = life.RetainedNativeTree(SimpleNamespace(pid=1), hp, wp, folder / "tree", watcher_factory=factory, binder=binder, start_thread=False)
                tree.collect()
                self.assertEqual(set(watchers), {1, 2, 3})
                vanished_helper.name.assert_not_called()
                host.children.assert_called_once_with(recursive=False)
                worker.children.assert_called_once_with(recursive=False)
                receipt = json.loads(next((folder / "tree").glob('process-3-*-start.json')).read_text())
                self.assertEqual(receipt['ancestry'], [worker.info, host.info])
                self.assertFalse(tree.unknown)

    def test_timeout_and_cancellations_never_close_live_handle(self):
        watcher = Watcher(7); events = []; calls = 0
        def wait(handle, milliseconds):
            nonlocal calls
            self.assertIs(handle, watcher._handle); self.assertEqual(watcher.closes, 0); calls += 1
            if calls == 1: return 258
            if calls == 2: raise KeyboardInterrupt()
            if calls == 3: raise SystemExit()
            watcher.live = False; return 0
        watcher.api.wait = wait
        with patch("sys.stdout", new_callable=io.StringIO):
            proof = life.wait_retained_terminal(watcher, timeout_seconds=0, clock=lambda: 1, report=events.append)
        self.assertEqual(watcher.closes, 1); self.assertTrue(proof["completed"])
        self.assertTrue(proof["deadlineOrCancellationObserved"])
        self.assertEqual(len(events), 3); self.assertFalse(proof["forcedTermination"])

    def fixture(self, directory):
        processes = {}; watchers = {}
        host_path, worker_path = directory / "Host.exe", directory / "TiaMcp.Openness.V21.exe"
        def process(pid, parent, name):
            info = {"pid": pid, "parentPid": parent, "created": float(pid), "name": name, "exe": str(directory / name)}
            obj = SimpleNamespace(pid=pid, info=info, descendants=[], ancestors=[])
            obj.children = lambda recursive: obj.descendants
            obj.create_time = lambda: info["created"]
            obj.parents = lambda: obj.ancestors
            obj.name = lambda: name
            processes[pid] = obj; return obj
        host = process(1, 99, "Host.exe"); worker = process(2, 1, "TiaMcp.Openness.V21.exe")
        host.descendants = [worker]; worker.ancestors = [host]
        def factory(pid, **kw):
            watcher = Watcher(pid); watchers[pid] = watcher; return watcher
        def binder(watcher, info):
            watcher.evidence.update(birthAndImageVerified=True, originalFileTimeTicks=info["pid"],
                processStartedAtUtc="2026-10-15T00:00:00.1234567Z", sameOsHandleRetained=True)
        return processes, watchers, process, host, worker, host_path, worker_path, factory, binder

    def test_descendant_handles_are_captured_before_start_reply(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp).resolve(); processes, watchers, make, host, worker, hp, wp, factory, binder = self.fixture(folder)
            with patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), patch.object(life, "identity", side_effect=lambda p: dict(p.info)):
                tree = life.RetainedNativeTree(SimpleNamespace(pid=1), hp, wp, folder / "tree", watcher_factory=factory, binder=binder, start_thread=False)
                tree.one("worker"); tree.begin_start()
                native = make(3, 2, "Siemens.Automation.Portal.exe"); native.ancestors = [worker, host]
                host.descendants.append(native); worker.descendants = [native]
                tree.collect()  # reply has not arrived
                self.assertIn(3, watchers); self.assertIsNotNone(tree.pending_start)
                row = tree.accept_start(3); self.assertIs(row["watcher"], watchers[3]); self.assertIsNone(tree.pending_start)
                for watcher in watchers.values(): watcher.live = False
                proof = tree.close_all()
            self.assertTrue(proof["completed"]); self.assertEqual(len(proof["exits"]), 3)
            self.assertTrue(all(w.closes == 1 for w in watchers.values()))

    def test_unknown_start_holds_through_cancellation_until_captured_tree_terminal(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp).resolve(); processes, watchers, make, host, worker, hp, wp, factory, binder = self.fixture(folder)
            with patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), patch.object(life, "identity", side_effect=lambda p: dict(p.info)):
                tree = life.RetainedNativeTree(SimpleNamespace(pid=1), hp, wp, folder / "tree", watcher_factory=factory, binder=binder, start_thread=False)
                tree.one("worker"); tree.begin_start(); calls = 0
                def advance(_):
                    nonlocal calls
                    self.assertTrue(all(w.closes == 0 for w in watchers.values())); calls += 1
                    if calls == 1: raise KeyboardInterrupt()
                    if calls == 2:
                        native = make(3, 2, "Siemens.Automation.Portal.exe"); native.ancestors = [worker, host]
                        host.descendants.append(native); worker.descendants = [native]
                        tree.collect(); raise SystemExit()
                    for watcher in watchers.values(): watcher.live = False
                with patch.object(life.time, "sleep", side_effect=advance), patch("sys.stdout", new_callable=io.StringIO):
                    proof = tree.close_all(timeout_seconds=0)
            self.assertFalse(proof["completed"]); self.assertTrue(proof["startupOutcomeUnknown"])
            self.assertEqual(len(proof["exits"]), 3); self.assertTrue(all(w.closes == 1 for w in watchers.values()))

    def test_host_close_uses_eof_and_tree_without_client_autokill(self):
        trace = []
        process = SimpleNamespace(stdin=Mock(), stdout=Mock(), stderr=Mock(), returncode=0, poll=Mock(return_value=0), kill=Mock(side_effect=AssertionError("kill")))
        process.stdin.close.side_effect = lambda: trace.append("eof")
        client = SimpleNamespace(process=process, output_reader=Mock(), error_reader=Mock(), errors=[], close=Mock(side_effect=AssertionError("Client.close")))
        client.output_reader.is_alive.return_value = False; client.error_reader.is_alive.return_value = False
        tree = SimpleNamespace(await_capture_complete=lambda: trace.append("capture-complete"),
            close_all=lambda: trace.append("exact-tree-terminal") or {"completed": True, "exits": []})
        proof = life.close_host_without_kill(client, tree)
        self.assertEqual(trace, ["capture-complete", "eof", "exact-tree-terminal"]); self.assertFalse(proof["forcedHostStop"])
        process.kill.assert_not_called(); client.close.assert_not_called()

    def test_tree_never_converts_a_prior_terminal_timeout_into_clean_completion(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp).resolve(); processes, watchers, make, host, worker, hp, wp, factory, binder = self.fixture(folder)
            with patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), patch.object(life, "identity", side_effect=lambda p: dict(p.info)):
                tree = life.RetainedNativeTree(SimpleNamespace(pid=1), hp, wp, folder / "tree", watcher_factory=factory, binder=binder, start_thread=False)
                row = tree.one("worker"); calls = 0
                def wait(handle, milliseconds):
                    nonlocal calls
                    if milliseconds == 0: return 258 if watchers[2].live else 0
                    calls += 1
                    if calls == 1: return 258
                    watchers[2].live = False; return 0
                watchers[2].api.wait = wait
                with patch("sys.stdout", new_callable=io.StringIO): tree.close_record(row, timeout_seconds=0)
                watchers[1].live = False
                proof = tree.close_all()
            self.assertFalse(proof["completed"])
            self.assertTrue(row["proof"]["completed"] and row["proof"]["deadlineOrCancellationObserved"])

    def test_terminal_host_without_worker_keeps_eof_and_handles_held_through_cancellation(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp).resolve(); processes, watchers, make, host, worker, hp, wp, factory, binder = self.fixture(folder)
            host.descendants = []
            with patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), patch.object(life, "identity", side_effect=lambda p: dict(p.info)):
                tree = life.RetainedNativeTree(SimpleNamespace(pid=1), hp, wp, folder / "tree", watcher_factory=factory, binder=binder, start_thread=False)
                watchers[1].live = False
                self.assertFalse(tree.worker_captured())
                self.assertIsNone(tree.pending_start)  # No native dispatch was attempted.
                process = SimpleNamespace(stdin=Mock(), stdout=Mock(), stderr=Mock(), returncode=0, poll=Mock(return_value=0), kill=Mock())
                client = SimpleNamespace(process=process, output_reader=Mock(), error_reader=Mock(), errors=[], close=Mock())
                client.output_reader.is_alive.return_value = False; client.error_reader.is_alive.return_value = False
                calls = 0
                def advance(_):
                    nonlocal calls
                    calls += 1; process.stdin.close.assert_not_called(); client.close.assert_not_called(); process.kill.assert_not_called()
                    self.assertTrue(all(w.closes == 0 for w in watchers.values()))
                    if calls == 1: raise KeyboardInterrupt()
                    if calls == 2: raise SystemExit()
                    # Model a pending capture finishing, not a disappearance or
                    # no-dispatch exemption. Until this exact evidence arrives,
                    # a terminal ordinary Host remains unknown indefinitely.
                    tree.retain(worker, "worker", [dict(host.info)]); watchers[2].live = False
                with patch.object(life.time, "sleep", side_effect=advance), patch("sys.stdout", new_callable=io.StringIO):
                    proof = life.close_host_without_kill(client, tree)
            self.assertEqual(calls, 3); self.assertFalse(proof["tree"]["completed"])
            self.assertTrue(proof["tree"]["workerStartupCaptured"])
            self.assertTrue(proof["tree"]["startupInventoryUnknownReasons"])
            self.assertTrue(all(w.closes == 1 for w in watchers.values()))

    def test_two_fresh_native_identities_cannot_accept_one_matching_reply(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp).resolve(); processes, watchers, make, host, worker, hp, wp, factory, binder = self.fixture(folder)
            with patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), patch.object(life, "identity", side_effect=lambda p: dict(p.info)):
                tree = life.RetainedNativeTree(SimpleNamespace(pid=1), hp, wp, folder / "tree", watcher_factory=factory, binder=binder, start_thread=False)
                tree.begin_start()
                for pid in (3, 4):
                    native = make(pid, 2, "Siemens.Automation.Portal.exe"); native.ancestors = [worker, host]
                    host.descendants.append(native); worker.descendants.append(native)
                with self.assertRaisesRegex(RuntimeError, "Exactly one fresh"): tree.accept_start(3)
                self.assertIsNotNone(tree.pending_start); self.assertFalse(tree.native_inventory_complete())
                self.assertEqual(tree.accepted_native_keys, set()); self.assertTrue(all(w.closes == 0 for w in watchers.values()))

    def test_unadmitted_native_identity_cannot_be_closed_as_complete_inventory(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp).resolve(); processes, watchers, make, host, worker, hp, wp, factory, binder = self.fixture(folder)
            with patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), patch.object(life, "identity", side_effect=lambda p: dict(p.info)):
                tree = life.RetainedNativeTree(SimpleNamespace(pid=1), hp, wp, folder / "tree", watcher_factory=factory, binder=binder, start_thread=False)
                native = make(3, 2, "Siemens.Automation.Portal.exe"); native.ancestors = [worker, host]
                host.descendants.append(native); worker.descendants = [native]
                tree.collect(); self.assertFalse(tree.native_inventory_complete())
                with self.assertRaisesRegex(RuntimeError, "Earlier or unexpected"): tree.begin_start()
                self.assertTrue(all(w.closes == 0 for w in watchers.values()))

    def test_native_reply_with_wrong_exact_worker_ancestry_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp).resolve(); processes, watchers, make, host, worker, hp, wp, factory, binder = self.fixture(folder)
            with patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), patch.object(life, "identity", side_effect=lambda p: dict(p.info)):
                tree = life.RetainedNativeTree(SimpleNamespace(pid=1), hp, wp, folder / "tree", watcher_factory=factory, binder=binder, start_thread=False)
                tree.begin_start()
                native = make(3, 1, "Siemens.Automation.Portal.exe"); native.ancestors = [host]; host.descendants.append(native)
                with self.assertRaisesRegex(RuntimeError, "exact captured worker ancestry"): tree.accept_start(3)
                self.assertFalse(tree.native_inventory_complete()); self.assertTrue(all(w.closes == 0 for w in watchers.values()))

    def test_lazy_worker_can_be_admitted_but_must_be_captured_before_reply_acceptance(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp).resolve(); processes, watchers, make, host, worker, hp, wp, factory, binder = self.fixture(folder)
            host.descendants = []
            with patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), patch.object(life, "identity", side_effect=lambda p: dict(p.info)):
                tree = life.RetainedNativeTree(SimpleNamespace(pid=1), hp, wp, folder / "tree", watcher_factory=factory, binder=binder, start_thread=False)
                tree.begin_start(); self.assertEqual(tree.pending_start, set()); self.assertFalse(tree.worker_captured())
                with self.assertRaisesRegex(RuntimeError, "startup capture is incomplete"): tree.accept_start(3)
                native = make(3, 2, "Siemens.Automation.Portal.exe"); native.ancestors = [worker, host]
                host.descendants = [worker, native]; worker.descendants = [native]
                tree.accept_start(3); self.assertTrue(tree.worker_captured()); self.assertTrue(tree.native_inventory_complete())

    def test_captured_native_skips_expired_host_ancestry_and_keeps_original_chain(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp).resolve(); processes, watchers, make, host, worker, hp, wp, factory, binder = self.fixture(folder)
            with patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), patch.object(life, "identity", side_effect=lambda p: dict(p.info)):
                tree = life.RetainedNativeTree(SimpleNamespace(pid=1), hp, wp, folder / "tree", watcher_factory=factory, binder=binder, start_thread=False)
                tree.begin_start(); native = make(3, 2, "Siemens.Automation.Portal.exe"); native.ancestors = [worker, host]
                host.descendants.append(native); worker.descendants = [native]; row = tree.accept_start(3)
                chain = list(row["ancestry"]); watchers[1].live = False
                native.parents = Mock(side_effect=life.psutil.NoSuchProcess(1))
                tree.collect()
                native.parents.assert_not_called(); self.assertEqual(row["ancestry"], chain); self.assertFalse(tree.unknown)

    def test_captured_exact_birth_disappearance_requires_signaled_original_handle(self):
        for terminal in (True, False):
            with self.subTest(terminal=terminal), tempfile.TemporaryDirectory() as temp:
                folder = Path(temp).resolve(); processes, watchers, make, host, worker, hp, wp, factory, binder = self.fixture(folder)
                with patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), patch.object(life, "identity", side_effect=lambda p: dict(p.info)):
                    tree = life.RetainedNativeTree(SimpleNamespace(pid=1), hp, wp, folder / "tree", watcher_factory=factory, binder=binder, start_thread=False)
                    tree.begin_start(); native = make(3, 2, "Siemens.Automation.Portal.exe"); native.ancestors = [worker, host]
                    host.descendants.append(native); worker.descendants = [native]; tree.accept_start(3)
                    watchers[3].live = not terminal
                    def identity(p):
                        if p.pid == 3: raise life.psutil.NoSuchProcess(3)
                        return dict(p.info)
                    with patch.object(life, "identity", side_effect=identity):
                        if terminal: tree.collect(); self.assertFalse(tree.unknown)
                        else:
                            with self.assertRaisesRegex(RuntimeError, "original handle remained live"): tree.collect()
                            self.assertTrue(tree.unknown)
                    self.assertTrue(all(w.closes == 0 for w in watchers.values()))

    def test_captured_pid_new_birth_is_unknown_and_never_adopted(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp).resolve(); processes, watchers, make, host, worker, hp, wp, factory, binder = self.fixture(folder)
            with patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), patch.object(life, "identity", side_effect=lambda p: dict(p.info)):
                tree = life.RetainedNativeTree(SimpleNamespace(pid=1), hp, wp, folder / "tree", watcher_factory=factory, binder=binder, start_thread=False)
                tree.begin_start(); native = make(3, 2, "Siemens.Automation.Portal.exe"); native.ancestors = [worker, host]
                host.descendants.append(native); worker.descendants = [native]; tree.accept_start(3)
                native.info["created"] += 1; native.parents = Mock(side_effect=AssertionError("new ancestry must not be adopted"))
                with self.assertRaisesRegex(RuntimeError, "different birth; no adoption"): tree.collect()
                self.assertTrue(tree.unknown); native.parents.assert_not_called(); self.assertEqual(len(tree.entries), 3)


if __name__ == "__main__": unittest.main()
