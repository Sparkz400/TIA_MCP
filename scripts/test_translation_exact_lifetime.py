"""No native execution: exercise real controller/helper hold against fake handles and locks."""
import copy
import json
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

import qualify_translation_category_probe_exact as module
from created_process_lifetime import await_created_terminal


class FakeProcess:
    def __init__(self, events, outcomes): self.events, self.outcomes, self.returncode, self.pid = events, list(outcomes), None, 42
    def wait(self, timeout):
        outcome = self.outcomes.pop(0) if self.outcomes else 0
        if isinstance(outcome, BaseException): self.events.append(type(outcome).__name__); raise outcome
        self.events.append("original-handle-terminal"); self.returncode = outcome; return outcome
    def kill(self): raise AssertionError("No termination allowed")


class FakeLifetime:
    def __init__(self, process, executable): self.process = process; self.proof = {"identity": {"pid": 42}}
    def finish(self):
        if self.process.returncode is None: raise AssertionError("Live original handle closed")
        self.process.events.append("original-handle-closed")
        return {"completed": True, "processExitVerified": True, "handleClosed": True, "exitCode": self.process.returncode, "forcedTermination": False}


class Checks(unittest.TestCase):
    def test_source_reservation_is_checked_before_any_host_start(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); prepared = {"root": str(root / "new-run"), "projectBefore": {"path": str(root / "source.ap21")},
                "resourceRoots": [str(root / "existing"), str(root / "new-run")]}
            with mock.patch.object(module.base, "StoreReadWindow") as window, mock.patch.object(module.subprocess, "Popen") as launch:
                window.return_value.__enter__.side_effect = ValueError("Unresolved overlapping reservation")
                with self.assertRaisesRegex(ValueError, "Unresolved overlapping"):
                    module.verify_resources_before_start(prepared, root / "proof")
                self.assertEqual([root / "existing"], window.call_args.args[0])
                launch.assert_not_called()

    def test_resource_preflight_release_is_not_dispatch_admission(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); prepared = {"root": str(root / "new-run"), "projectBefore": {"path": str(root / "source.ap21")}, "resourceRoots": [str(root)]}
            with mock.patch.object(module.base, "StoreReadWindow") as window, mock.patch.object(module, "scan_component_reservations") as scan, mock.patch.object(module, "ref", return_value={"retained": True}):
                result = module.verify_resources_before_start(prepared, root / "proof")
                window.return_value.__exit__.assert_called_once()
                scan.assert_called_once()
                self.assertTrue(result["completed"])
                self.assertFalse(result["nativeInvoked"])
                self.assertFalse(result["admissionHeldForDispatch"])

    def test_preflight_admits_only_reviewed_direct_child_collector_bytes(self):
        scripts = Path(module.__file__).resolve().parent
        module.verify_lifecycle_source(scripts)
        for changed in ("f59b4a487e610717a5ad0e18b36cb31ca2fba1d10c400a11b9742da7cbc699de", "0" * 64):
            with mock.patch.object(module, "sha", return_value=changed):
                with self.assertRaisesRegex(ValueError, "reviewed direct-child"):
                    module.verify_lifecycle_source(scripts)

    def test_timeout_and_cancellation_hold_until_terminal_and_keep_outcome_failed(self):
        events = []; process = FakeProcess(events, [subprocess.TimeoutExpired("helper", 1), KeyboardInterrupt(), 0])
        proof = await_created_terminal(process, FakeLifetime(process, None), 1, lambda row: events.append("locks-retained-report"))
        self.assertFalse(proof["completed"]); self.assertTrue(proof["deadlineOrCancellationObserved"])
        self.assertLess(events.index("original-handle-terminal"), events.index("original-handle-closed"))
        self.assertEqual(2, len(proof["unknownReasons"]))

    def test_receipt_error_during_unknown_does_not_release_terminal_hold(self):
        events = []; process = FakeProcess(events, [KeyboardInterrupt(), 0])
        def report(_): raise OSError("receipt disk failure")
        proof = await_created_terminal(process, FakeLifetime(process, None), 1, report)
        self.assertFalse(proof["completed"]); self.assertEqual(0, process.returncode)

    def helper_controller(self, folder, events, outcomes, capture_failure=False):
        root = Path(folder); template = root / "template.json"; template.write_text("{}")
        native = root / "native"; native.mkdir()
        project = root / "source.ap21"; project.write_text("source")
        (native / "result.json").write_text(json.dumps({"processId": 1, "projectPath": str(project), "beforeModified": False, "afterModified": False, "readOnly": True, "saved": False,
            "completed": True, "onlineConnected": False, "translations": {"outcome": "synthetic-controls-only"}}))
        (root / "preflight.json").write_text("{}")
        coordinator = module.ExactCoordinator.__new__(module.ExactCoordinator)
        coordinator.root, coordinator.args, coordinator.report = root, SimpleNamespace(timeout=30), {}
        coordinator.helper_terminal = False; coordinator.helper_proof = None
        coordinator.prepared = {"hostPackage": [], "workerPackage": [], "helperPackage": [], "template": module.ref(template), "resourceRoots": [],
            "projectBefore": module.ref(project), "sourceControl": {"plcName": "PLC"}, "helper": {"path": "fake-helper.exe"}, "worker": {"path": "fake-worker.exe"}}
        coordinator.native_row = {"identity": {"pid": 1, "created": 0}, "watcher": SimpleNamespace(api=SimpleNamespace(wait=lambda *_: 258), _handle=1, evidence={"processStartedAtUtc": "1970-01-01T00:00:00Z"})}
        coordinator.tree = SimpleNamespace(anchor={"identity": {"pid": 2}})
        class Window:
            def __init__(self, *args): self.keys = []; self.evidence = {}
            def __enter__(self): events.append("store-locks-held"); return self
            def __exit__(self, *args):
                self_test.assertEqual(0, process.returncode, "Store locks released while helper was alive")
                events.append("store-locks-released")
        self_test = self; process = FakeProcess(events, outcomes)
        with mock.patch.object(module.base, "verify_package"), mock.patch.object(module.base, "StoreReadWindow", Window), mock.patch.object(module.subprocess, "Popen", return_value=process), \
                mock.patch.object(module, "CreatedProcess", side_effect=OSError("identity capture failed") if capture_failure else FakeLifetime):
            coordinator.run_helper()
        return coordinator

    def test_actual_helper_method_releases_window_only_after_terminal(self):
        with tempfile.TemporaryDirectory() as folder:
            events = []; coordinator = self.helper_controller(folder, events, [0])
            self.assertTrue(coordinator.helper_terminal)
            self.assertLess(events.index("original-handle-closed"), events.index("store-locks-released"))

    def test_actual_helper_timeout_keeps_window_and_rejects_result(self):
        with tempfile.TemporaryDirectory() as folder:
            events = []
            with self.assertRaisesRegex(ValueError, "unqualified"):
                self.helper_controller(folder, events, [subprocess.TimeoutExpired("helper", 30), KeyboardInterrupt(), 0])
            self.assertLess(events.index("original-handle-closed"), events.index("store-locks-released"))

    def test_actual_helper_missing_identity_still_waits_original_popen_before_releasing(self):
        with tempfile.TemporaryDirectory() as folder:
            events = []
            with self.assertRaisesRegex(ValueError, "unqualified"):
                self.helper_controller(folder, events, [0], True)
            self.assertLess(events.index("original-handle-terminal"), events.index("store-locks-released"))


if __name__ == "__main__": unittest.main()
