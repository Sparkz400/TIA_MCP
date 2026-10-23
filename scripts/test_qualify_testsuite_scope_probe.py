"""Offline coordinator checks. All process/session receipts here are synthetic."""
from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import qualify_testsuite_scope_probe as probe


class CoordinatorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="testsuite-coordinator-synthetic-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def stamp(self, seconds):
        return datetime.fromtimestamp(seconds, timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.0000000Z")

    def info(self, pid, parent, created, name):
        return {"pid": pid, "parentPid": parent, "created": created, "name": name,
                "exe": str(self.root / name), "command": [name]}

    def proof(self, info):
        return {"identity": info, "proof": {"processId": info["pid"], "handleCaptured": True,
            "processExitVerified": True, "handleClosed": True, "completed": True, "exitCode": 0,
            "birthAndImageVerified": True, "sameOsHandleRetained": True, "imagePath": info["exe"],
            "nativeProcessStartTimeUtc": self.stamp(info["created"])}}

    def lifecycle(self):
        host = self.info(100, 50, 1700000000, "TiaMcp.Host.exe")
        worker = self.info(101, 100, 1700000001, "TiaMcp.Openness.V21.exe")
        native = self.info(102, 101, 1700000002, "Siemens.Automation.Portal.exe")
        helper = self.info(103, 50, 1700000003, "TestSuiteSystemScopeEvidence.exe")
        return {"host": host, "native": native, "coordinatorPid": 50, "nativeProcessStartTimeUtc": self.stamp(native["created"]),
            "helper": {"identity": helper, "exit": self.proof(helper), "returncode": 0, "closedAt": 1700000010},
            "cleanup": {"errors": [], "hostExit": self.proof(host), "descendantExits": [self.proof(worker), self.proof(native)],
                "requestedAt": 1700000011, "closedAt": 1700000012,
                "hostClose": {"hostExitCode": 0, "forcedHostStop": False, "stderrEofObserved": True,
                    "workerExits": [{"processId": 101, "processExitVerified": True, "exitCode": 0,
                                     "forcedTermination": False, "startedAtUtc": self.stamp(worker["created"])}]}}}

    def verify(self, session):
        return probe.validate_lifecycle(session, self.root / "TiaMcp.Host.exe", self.root / "TiaMcp.Openness.V21.exe",
                                        self.root / "TestSuiteSystemScopeEvidence.exe")

    def test_exact_filetime_preserves_seventh_fractional_digit(self):
        self.assertEqual(probe.filetime_iso(116444736001234567), "1970-01-01T00:00:00.1234567Z")
        self.assertEqual(probe.filetime_iso(116444736000000001), "1970-01-01T00:00:00.0000001Z")
        for invalid in (0, -1, True, 1.5):
            with self.subTest(invalid=invalid), self.assertRaises(RuntimeError):
                probe.filetime_iso(invalid)

    def test_status_requires_owned_positive_pid_and_single_exact_project(self):
        value = {"attached": True, "ownsPortalProcess": True, "processId": 102, "projects": []}
        probe.validate_status(value, 102)
        for change in ({"attached": False}, {"ownsPortalProcess": False}, {"processId": 103}):
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                probe.validate_status(dict(value, **change), 102)
        with self.assertRaises(RuntimeError):
            probe.validate_status(dict(value, processId=None), None)
        project = self.root / "TiaMcpScratch_test.ap21"
        value["projects"] = [{"name": project.stem, "path": str(project)}]
        probe.validate_status(value, 102, project)
        for projects in ([], value["projects"] * 2, [{"name": project.stem, "path": str(self.root / "other.ap21")}]):
            with self.subTest(projects=projects), self.assertRaises(RuntimeError):
                probe.validate_status(dict(value, projects=projects), 102, project)

    def test_complete_exact_lifecycle_is_accepted_without_lock_release_claim(self):
        result = self.verify(self.lifecycle())
        self.assertTrue(result["completed"] and result["normalLifecyclePassed"])
        self.assertFalse(result["forcedTermination"] or result["projectLockReleaseVerified"])
        self.assertEqual(result["retainedHandlesVerified"], 4)

    def test_missing_or_changed_handle_proof_is_rejected(self):
        for key, value in (("processId", 999), ("handleCaptured", False), ("handleClosed", False),
                           ("processExitVerified", False), ("completed", False), ("exitCode", 1),
                           ("birthAndImageVerified", False), ("sameOsHandleRetained", False),
                           ("imagePath", str(self.root / "other.exe")),
                           ("nativeProcessStartTimeUtc", self.stamp(1700000009))):
            receipt = self.lifecycle()
            receipt["cleanup"]["descendantExits"][1]["proof"][key] = value
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                self.verify(receipt)

    def test_exact_worker_host_receipt_cannot_be_replaced_by_pid_absence(self):
        for key, value in (("processExitVerified", False), ("exitCode", None), ("forcedTermination", True),
                           ("processId", 999), ("startedAtUtc", self.stamp(1700000009))):
            receipt = self.lifecycle()
            receipt["cleanup"]["hostClose"]["workerExits"][0][key] = value
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                self.verify(receipt)
        for values in ([], [self.lifecycle()["cleanup"]["hostClose"]["workerExits"][0]] * 2):
            receipt = self.lifecycle()
            receipt["cleanup"]["hostClose"]["workerExits"] = values
            with self.assertRaises(RuntimeError):
                self.verify(receipt)

    def test_wrong_parent_and_unexpected_process_rejected(self):
        for index in (0, 1):
            receipt = self.lifecycle()
            receipt["cleanup"]["descendantExits"][index]["identity"]["parentPid"] = 999
            with self.subTest(index=index), self.assertRaises(RuntimeError):
                self.verify(receipt)
        receipt = self.lifecycle()
        receipt["cleanup"]["descendantExits"].append(self.proof(self.info(500, 100, 1700000005, "other.exe")))
        with self.assertRaises(RuntimeError):
            self.verify(receipt)

    def test_failed_or_late_helper_blocks_clean_lifecycle(self):
        for change in ({"returncode": 1}, {"closedAt": 1700000012}):
            receipt = self.lifecycle()
            receipt["helper"].update(change)
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                self.verify(receipt)

    def test_forced_or_incomplete_host_close_rejected(self):
        for key, value in (("hostExitCode", 1), ("forcedHostStop", True), ("stderrEofObserved", False)):
            receipt = self.lifecycle()
            receipt["cleanup"]["hostClose"][key] = value
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                self.verify(receipt)

    def test_gate_wrapper_requires_verified_restoration(self):
        args = SimpleNamespace(recovery_gate=self.root / "gate.json", host=probe.HOST, worker=probe.WORKER)
        valid = {"kind": "completed-independent-checkpoint-retrieval-gate", "completed": True, "nativeInvoked": False,
                 "cases": [{"rawEvidenceReverification": {"recovery": {"restoredProjectVerified": True}}}]}
        with patch.object(probe, "shared_api", return_value=(None, lambda *args: valid)):
            self.assertEqual(probe.checked_recovery_gate(args), valid)
        for key, value in (("completed", False), ("nativeInvoked", True), ("kind", "marker"), ("cases", [])):
            broken = dict(valid, **{key: value})
            with patch.object(probe, "shared_api", return_value=(None, lambda *args: broken)):
                with self.subTest(key=key), self.assertRaises(RuntimeError):
                    probe.checked_recovery_gate(args)

    def test_actual_shared_gate_rejects_arbitrary_truth_marker_offline(self):
        marker = self.root / "truth-marker.json"
        probe.save(marker, {"completed": True, "checkpointRetrievalGatePassed": True, "actualMcpHost": True})
        args = SimpleNamespace(recovery_gate=marker, host=probe.HOST, worker=probe.WORKER)
        with patch.object(probe, "Client", side_effect=AssertionError("native dispatch")):
            with self.assertRaisesRegex(ValueError, "exactly six"):
                probe.checked_recovery_gate(args)

    def test_prepare_only_can_report_blocked_gate_without_starting_anything(self):
        output = self.root / "prepared.json"
        plan = {"sourceReady": True, "nativeAdmissionReady": False, "nativeInvoked": False, "issues": ["synthetic failed gate"]}
        with (patch.object(probe, "preparation", return_value=plan), patch.object(probe, "Client", side_effect=AssertionError("native dispatch")) as client,
              patch.object(probe, "Run", side_effect=AssertionError("run instantiated")) as run):
            result = probe.main(["--root", str(self.root / "never-created"), "--recovery-gate", str(self.root / "gate.json"),
                                 "--prepare-only", "--output", str(output)])
        self.assertEqual(result, 2)
        self.assertFalse(client.called or run.called)
        self.assertFalse((self.root / "never-created").exists())
        self.assertEqual(json.loads(output.read_text()), plan)

    def test_execute_is_blocked_before_run_construction_when_gate_fails(self):
        with (patch.object(probe, "preparation", return_value={"nativeAdmissionReady": False, "issues": ["failed uq05"]}),
              patch.object(probe, "Run", side_effect=AssertionError("run instantiated")) as run):
            with self.assertRaisesRegex(RuntimeError, "Native dispatch blocked"):
                probe.main(["--root", str(self.root / "never-created"), "--recovery-gate", str(self.root / "gate.json"), "--execute-native"])
        self.assertFalse(run.called)

    def test_failed_capture_or_close_never_admits_a_second_session(self):
        for failure in ("start", "capture", "close"):
            events = []
            class FakeSession:
                def __init__(inner, run, ordinal):
                    events.append(("create", ordinal)); inner.data = {}
                def start(inner):
                    events.append(("start", 1))
                    if failure == "start": raise RuntimeError("failed start")
                def capture(inner):
                    events.append(("capture", 1))
                    if failure == "capture": raise RuntimeError("failed capture")
                def close(inner):
                    events.append(("close", 1))
                    if failure == "close": raise RuntimeError("failed cleanup")
                    return {"completed": True}
            run = object.__new__(probe.Run)
            run.report = {"sessions": []}
            with patch.object(probe, "Session", FakeSession), self.subTest(failure=failure), self.assertRaises(RuntimeError):
                run.execute()
            self.assertEqual([e for e in events if e[0] == "create"], [("create", 1)])
            self.assertIn(("close", 1), events)
            self.assertEqual(len(run.report["sessions"]), 1)

    def test_native_dispatch_requires_explicit_mode(self):
        with self.assertRaises(SystemExit):
            probe.parser().parse_args(["--root", str(self.root), "--recovery-gate", str(self.root / "gate.json")])

    def test_mutation_tool_is_refused_before_mcp_dispatch(self):
        session = object.__new__(probe.Session)
        with self.assertRaisesRegex(RuntimeError, "forbids MCP tool"):
            session.call("project_save")

    def test_store_window_must_hold_exact_handles_for_entire_helper_lifetime(self):
        intent = {"readOnly": True, "mode": "readOnlyProbe", "sameHandlesHeldThroughDispatch": True,
                  "journalsModified": False, "lockFiles": ["a", "b", "c"], "acquiredAtUtc": self.stamp(1700000000)}
        closed = {"allClosed": True, "bodyFailure": None, "journalsModified": False,
                  "locks": [{"path": p, "closed": True} for p in ("c", "b", "a")],
                  "releasedAtUtc": self.stamp(1700000012)}
        resource = probe.save(self.root / "resource-admission.json", intent)
        probe.save(self.root / "store-read-window-intent.json", intent)
        helper = {"requestedAt": 1700000001, "closedAt": 1700000010}
        close_path = self.root / "store-read-window-close.json"
        probe.save(close_path, closed)
        self.assertTrue(probe.validate_store_window(self.root, resource, helper)["completed"])
        for change in ({"allClosed": False}, {"bodyFailure": "failure"}, {"releasedAtUtc": self.stamp(1700000005)},
                       {"locks": closed["locks"][:-1]}, {"locks": list(reversed(closed["locks"]))}):
            close_path.write_text(json.dumps(dict(closed, **change)), encoding="utf-8")
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                probe.validate_store_window(self.root, resource, helper)

    def test_actual_run_capture_timeout_holds_store_through_cancellation_and_helper_exit(self):
        run = object.__new__(probe.Run)
        run.root = self.root / "run"; run.root.mkdir()
        run.args = SimpleNamespace(timeout=30, host=probe.HOST, worker=probe.WORKER)
        run.report = {"sessions": []}; run.check_frozen = Mock()
        binary = self.root / "synthetic-read-only-helper.exe"; binary.write_text("never executable")
        reference = probe.ref(binary)
        run.package = {"binary": reference, "apiSha256": "synthetic", "ownershipEvidence": reference,
                       "reviewedSource": reference, "sourceFiles": []}
        run.gate_ref = reference
        session = probe.Session(run, 1)
        native = self.info(102, 101, 1700000002, "Siemens.Automation.Portal.exe")
        helper = self.info(103, 50, 1700000003, "synthetic-read-only-helper.exe")
        session.data.update(native=native, nativeProcessStartTimeUtc=self.stamp(native["created"]))
        session.open_ref = reference
        trace = []; state = {"held": False, "hostClosed": False}
        class Window:
            def __init__(self, *args): self.evidence = {"synthetic": True}
            def __enter__(self): state["held"] = True; trace.append("store-held"); return self
            def __exit__(self, *args):
                self_test.assertEqual(process.returncode, 0)
                self_test.assertFalse(session.helper_alive)
                state["held"] = False; trace.append("store-released")
        self_test = self
        class Process:
            pid = 103; returncode = None; calls = 0
            def wait(self, timeout):
                self_test.assertTrue(state["held"]); self_test.assertFalse(state["hostClosed"])
                self.calls += 1
                if self.calls == 1:
                    trace.append("deadline"); raise probe.subprocess.TimeoutExpired("synthetic", timeout)
                if self.calls == 2:
                    trace.append("keyboard-interrupt"); raise KeyboardInterrupt()
                if self.calls == 3:
                    trace.append("system-exit"); raise SystemExit(2)
                self.returncode = 0; trace.append("helper-terminal"); return 0
        process = Process()
        watcher = Mock(); watcher.evidence = self.proof(helper)["proof"]
        watcher.evidence.update(handleClosed=False, processExitVerified=False, completed=False)
        watcher.api.wait.side_effect = lambda *args: 0 if process.returncode == 0 else 258
        watcher.api.exit_code.return_value = 0
        def close_handle():
            self.assertTrue(state["held"]); self.assertEqual(process.returncode, 0)
            trace.append("original-handle-closed"); return True
        watcher.close.side_effect = close_handle
        def close_session():
            self.assertFalse(state["held"] or session.helper_alive); self.assertEqual(process.returncode, 0)
            state["hostClosed"] = True; trace.append("host-close"); return {"completed": True}
        with (patch.object(probe, "Session", return_value=session) as factory,
              patch.object(session, "start"), patch.object(session, "close", side_effect=close_session),
              patch.object(probe, "shared_api", return_value=(Window, None)),
              patch.object(probe.subprocess, "Popen", return_value=process),
              patch.object(probe, "OwnedTiaProcessWatcher", return_value=watcher), patch.object(probe, "bind_handle_identity"),
              patch.object(probe.psutil, "Process", side_effect=lambda pid:pid),
              patch.object(probe, "identity", side_effect=lambda pid: native if pid == 102 else helper)):
            with self.assertRaisesRegex(RuntimeError, "Collector did not return successfully"): run.execute()
            factory.assert_called_once_with(run, 1)
        self.assertEqual(trace, ["store-held", "deadline", "keyboard-interrupt", "system-exit", "helper-terminal", "original-handle-closed", "store-released", "host-close"])
        self.assertIsNone(session.data["helper"]["returncode"])
        self.assertEqual(session.data["helper"]["terminalReturncode"], 0)

    def test_terminal_after_native_deadline_is_cleanup_not_acceptance(self):
        packet = self.lifecycle()
        packet["cleanup"]["descendantExits"][1]["proof"]["deadlineOrCancellationObserved"] = True
        with self.assertRaises(RuntimeError): self.verify(packet)

    def test_reviewed_initial_controller_source_drift_is_refused(self):
        source = self.root / "controller.py"; source.write_text("synthetic controller")
        manifest = self.root / "reviewed-controller.json"
        probe.save(manifest, {"kind": "reviewed-testsuite-controller-sources", "nativeInvoked": False, "files": [probe.ref(source)]})
        with patch.object(probe, "CONTROLLER_SOURCES", (source,)):
            self.assertEqual(len(probe.reviewed_controller_sources(manifest)), 1)
            source.write_text("unreviewed changed controller")
            with self.assertRaises(RuntimeError): probe.reviewed_controller_sources(manifest)

    def retained_session(self):
        session = probe.Session(SimpleNamespace(root=self.root, args=SimpleNamespace(
            host=self.root / "TiaMcp.Host.exe", worker=self.root / "TiaMcp.Openness.V21.exe", timeout=30)), 1)
        session.client = Mock(); session.data["host"] = self.info(100, 50, 1700000000, "TiaMcp.Host.exe")
        session.host_watcher = Mock(); session.host_watcher.evidence = {"birthAndImageVerified": True}
        session.host_watcher.api.wait.return_value = 0
        worker = self.info(101, 100, 1700000001, "TiaMcp.Openness.V21.exe")
        watcher = Mock(); watcher.evidence = {"birthAndImageVerified": True}; watcher.api.wait.return_value = 0
        session.tracked[(101, worker["created"])] = worker, watcher
        session.ancestry[(101, worker["created"])] = [session.data["host"]]
        return session

    def test_actual_close_missing_native_holds_before_detach_monitor_stop_or_handle_release(self):
        session = self.retained_session(); session.start_attempted = True
        session.hold_uncaptured_startup = Mock(side_effect=RuntimeError("synthetic reconciliation hold observed"))
        session.call = Mock()
        with (patch.object(probe, "graceful_close") as close, patch.object(probe, "wait_retained_terminal") as wait):
            with self.assertRaisesRegex(RuntimeError, "reconciliation hold observed"): session.close()
        session.hold_uncaptured_startup.assert_called_once()
        session.call.assert_not_called(); close.assert_not_called(); wait.assert_not_called()
        self.assertFalse(session.stop.is_set())
        session.host_watcher.close.assert_not_called()
        for _, watcher in session.tracked.values(): watcher.close.assert_not_called()

    def test_actual_start_constructor_cancellation_keeps_partial_client_and_original_host_handle(self):
        session = self.retained_session(); session.tracked.clear(); session.run.check_frozen = Mock()
        process = Mock(pid=100)
        class PartialClient:
            def __init__(inner, *args):
                inner.process = process; inner.errors = []
                raise KeyboardInterrupt("synthetic reader thread cancellation")
        watcher = session.host_watcher
        def hold():
            self.assertIs(session.client.process, process); self.assertIs(session.host_watcher, watcher)
            watcher.close.assert_not_called()
            raise RuntimeError("synthetic partial-client hold observed")
        session.hold_uncaptured_startup = hold
        with (patch.object(probe, "Client", PartialClient), patch.object(probe, "OwnedTiaProcessWatcher", return_value=watcher),
              patch.object(probe, "bind_handle_identity"), patch.object(probe, "identity", return_value=session.data["host"]),
              patch.object(probe.psutil, "Process", return_value=object()), patch.object(probe.threading, "Thread"),
              patch.object(probe, "graceful_close") as close, patch.object(probe, "wait_retained_terminal") as wait):
            with self.assertRaisesRegex(RuntimeError, "partial-client hold observed"):
                try: session.start()
                finally: session.close()
        close.assert_not_called(); wait.assert_not_called()

    def test_actual_start_foreign_reply_never_captures_pid_or_opens_project(self):
        session = self.retained_session(); session.run.check_frozen = Mock(); session.scan = Mock()
        session.call = Mock(return_value={"attached": True, "ownsPortalProcess": True, "processId": 999, "projects": []})
        class Client:
            def __init__(inner, *args): inner.process = Mock(pid=100); inner.errors = []
            def request(inner, *args): return {}
            def notify(inner, *args): pass
        with (patch.object(probe, "Client", Client), patch.object(probe, "OwnedTiaProcessWatcher", return_value=session.host_watcher),
              patch.object(probe, "bind_handle_identity"), patch.object(probe, "identity", return_value=session.data["host"]),
              patch.object(probe.psutil, "Process", return_value=object()) as lookup, patch.object(probe.threading, "Thread")):
            with self.assertRaisesRegex(RuntimeError, "inventory is incomplete"): session.start()
        lookup.assert_called_once_with(100)
        session.call.assert_called_once_with("tia_start")
        self.assertTrue(session.capture_incomplete)

    def test_native_selection_requires_exact_previously_captured_worker_ancestry(self):
        session = self.retained_session(); session.start_attempted = True; session.scan = Mock()
        worker = next(iter(session.tracked.values()))[0]
        native = self.info(102, 101, 1700000002, "Siemens.Automation.Portal.exe")
        watcher = Mock(); watcher.evidence = {"birthAndImageVerified": True}
        session.tracked[(102, native["created"])] = native, watcher
        session.ancestry[(102, native["created"])] = [worker, session.data["host"]]
        with patch.object(probe.psutil, "Process", side_effect=AssertionError("returned PID reopened")):
            self.assertIs(session.select_native(102)[1], watcher)
            native["parentPid"] = 999
            with self.assertRaisesRegex(RuntimeError, "inventory is incomplete"): session.select_native(102)
        self.assertTrue(session.capture_incomplete)

    def test_actual_close_missing_worker_before_native_start_keeps_host_handle(self):
        session = self.retained_session(); session.tracked.clear()
        session.hold_uncaptured_startup = Mock(side_effect=RuntimeError("synthetic reconciliation hold observed"))
        with (patch.object(probe, "graceful_close") as close, patch.object(probe, "wait_retained_terminal") as wait):
            with self.assertRaisesRegex(RuntimeError, "reconciliation hold observed"): session.close()
        close.assert_not_called(); wait.assert_not_called()
        self.assertFalse(session.stop.is_set())

    def test_failed_openprocess_capture_stays_unknown_and_keeps_prior_handles(self):
        session = self.retained_session()
        native = self.info(102, 101, 1700000002, "Siemens.Automation.Portal.exe")
        with (patch.object(probe, "identity", return_value=native),
              patch.object(probe, "OwnedTiaProcessWatcher", side_effect=OSError("synthetic OpenProcess failure"))):
            with self.assertRaises(OSError): session.retain(object(), [next(iter(session.tracked.values()))[0], session.data["host"]])
        self.assertTrue(session.capture_incomplete); self.assertEqual(len(session.tracked), 1)
        self.assertFalse(session.capture_complete())
        session.host_watcher.close.assert_not_called()

    def test_discovery_continues_from_retained_worker_after_host_terminal(self):
        session = self.retained_session()
        info, watcher = next(iter(session.tracked.values())); watcher.api.wait.return_value = 258
        child = Mock(); child.name.side_effect = probe.psutil.NoSuchProcess(102)
        worker = Mock(); worker.children.return_value = [child]
        with (patch.object(probe.psutil, "Process", return_value=worker) as lookup,
              patch.object(probe, "identity", return_value=info)):
            with self.assertRaises(probe.psutil.NoSuchProcess): session.scan()
        lookup.assert_called_once_with(101)
        self.assertTrue(session.capture_incomplete)

    def test_known_native_preserves_original_ancestry_after_host_exit_and_new_birth_is_not_adopted(self):
        session = self.retained_session()
        worker_info, worker_watcher = next(iter(session.tracked.values())); worker_watcher.api.wait.return_value = 258
        native_info = self.info(102, 101, 1700000002, "Siemens.Automation.Portal.exe")
        watcher = Mock(); watcher.evidence = {"birthAndImageVerified": True}
        key = native_info["pid"], native_info["created"]
        session.tracked[key] = native_info, watcher
        session.ancestry[key] = [worker_info, session.data["host"]]
        child = Mock(); child.name.return_value = "Siemens.Automation.Portal.exe"
        child.parents.side_effect = probe.psutil.NoSuchProcess(100)
        worker = Mock(); worker.children.return_value = [child]
        current = dict(native_info)
        def info(proc): return worker_info if proc is worker else current
        with patch.object(probe.psutil, "Process", return_value=worker), patch.object(probe, "identity", side_effect=info):
            session.scan(); child.parents.assert_not_called()
            self.assertFalse(session.capture_incomplete)
            current["created"] += 1
            with self.assertRaises(probe.psutil.NoSuchProcess): session.scan()
        self.assertTrue(session.capture_incomplete)
        self.assertEqual(session.ancestry[key], [worker_info, session.data["host"]])
        self.assertEqual(session.tracked[key][0], native_info)
        watcher.close.assert_not_called()

    def test_actual_terminal_hold_keeps_original_handles_across_cancellation(self):
        session = self.retained_session(); cleanup = {"errors": []}
        session.require_captured_startup = Mock()
        session.host_watcher.api.wait.side_effect = [258, 258, 0]
        with patch.object(probe.time, "sleep", side_effect=[KeyboardInterrupt(), SystemExit(2)]):
            session.await_all_terminal(cleanup)
        self.assertEqual(len(cleanup["errors"]), 2)
        session.host_watcher.close.assert_not_called()
        for _, watcher in session.tracked.values(): watcher.close.assert_not_called()

    def test_late_native_terminal_hold_and_late_host_exit_cannot_pass(self):
        session = self.retained_session(); cleanup = {"errors": []}; session.require_captured_startup = Mock()
        session.host_watcher.api.wait.side_effect = [258, 0]
        with patch.object(probe.time, "sleep"):
            session.await_all_terminal(cleanup, timeout_seconds=45, clock=Mock(side_effect=[0, 46]))
        self.assertTrue(any("deadline exceeded" in e for e in cleanup["errors"]))
        session.host_watcher.close.assert_not_called()
        packet = self.lifecycle(); packet["cleanup"]["hostClose"]["closeTimedOut"] = True
        with self.assertRaises(RuntimeError): self.verify(packet)


if __name__ == "__main__":
    unittest.main()
