"""Offline checks of qualify_drives.py's drive object handles and owned-TIA cleanup; nothing here starts TIA."""
from pathlib import Path
import tempfile
import types
import unittest
from unittest import mock

import library_native_harness as harness
import qualify_drives as q

PATH = "/projects/S/devices/TiaMcpDrive/items/Drive"
INDEXED = {"driveObjectNumber": None, "driveObjectIndex": 0, "snapshot": {"parameters": [
    {"name": "p1120[0]", "number": 1120, "value": {"kind": "scalar", "type": "System.Single", "text": "10"}}],
    "telegramsComplete": True, "telegrams": [{"type": "MainTelegram", "number": 1}]}}
NUMBERED = {**INDEXED, "driveObjectNumber": 2}
del NUMBERED["driveObjectIndex"]


def run(step, drive_object):
    """Run one change step against a drive_observe listing, recording the prepare payload; prepare refuses so the step records a boundary."""
    sent, boundaries = [], {}
    def outcome(h, name, arguments):
        sent.append(arguments["payload"])
        return {"code": "Refused"}, {}
    with mock.patch.object(q, "observe", lambda h, path, **_: {"drives": [{"driveObjects": [drive_object]}]}), mock.patch.object(q, "outcome", outcome):
        step(types.SimpleNamespace(stage=lambda name, operation: operation(), report={}), PATH, q.handle(drive_object), boundaries)
    return sent, boundaries


class DriveHandleTests(unittest.TestCase):
    def test_handle_is_the_one_drive_observe_reports(self):
        self.assertEqual({"driveObjectIndex": 0}, q.handle(INDEXED))
        self.assertEqual({"driveObjectNumber": 2}, q.handle(NUMBERED))
        self.assertEqual({"driveObjectNumber": 1}, q.other_handle({"driveObjectIndex": 0}))
        self.assertEqual({"driveObjectIndex": 0}, q.other_handle({"driveObjectNumber": 2}))

    def test_only_the_number_warning_is_expected(self):
        number = {"path": PATH + "#DO@0/driveObjectNumber", "exceptionType": "EngineeringTargetInvocationException", "message": "Drive object number could not be retrieved"}
        telegrams = {"path": PATH + "#DO@0/telegrams", "exceptionType": "X", "message": "y"}
        self.assertEqual([], q.unexpected_warnings([number]))
        self.assertEqual([telegrams], q.unexpected_warnings([number, telegrams]))

    def test_change_payloads_use_the_reported_handle(self):
        for drive_object, ref in ((INDEXED, {"driveObjectIndex": 0}), (NUMBERED, {"driveObjectNumber": 2})):
            for step, kind in ((q.parameters, "drive_parameter_set"), (q.telegrams, "drive_telegram_set")):
                sent, boundaries = run(step, drive_object)
                self.assertEqual(1, len(sent))
                self.assertEqual({"hardwarePath": PATH, **ref}, {k: v for k, v in sent[0].items() if k in ("hardwarePath", "driveObjectNumber", "driveObjectIndex")})
                self.assertEqual({kind: {"code": "Refused"}}, boundaries)


class AppliedStatusTests(unittest.TestCase):
    def test_change_status_must_match_the_apply_result(self):
        stages = ["prechange_project_saved", "mutation_applied"]
        for status, ok in (({"state": "applied", "stages": stages}, True), ({"state": "applied", "stages": stages[:1]}, False), ({"state": "recovery_required", "stages": stages}, False)):
            h = types.SimpleNamespace(tool=lambda name, arguments, status=status: status)
            if ok:
                self.assertEqual({"state": "applied", "stages": stages}, q.applied_status(h, {"changeId": "c"}, {"stages": stages}))
            else:
                with self.assertRaises(AssertionError):
                    q.applied_status(h, {"changeId": "c"}, {"stages": stages})


class OwnedTiaCleanupTests(unittest.TestCase):
    def test_body_watches_the_owned_tia_before_any_change(self):
        calls = []
        h = types.SimpleNamespace(owned_scratch=lambda label: calls.append("scratch"), watch_owned_tia=lambda: calls.append("watch"), report={})
        with mock.patch.object(q, "add_drive", side_effect=KeyboardInterrupt), self.assertRaises(KeyboardInterrupt):
            q.body(h)
        self.assertEqual(["scratch", "watch"], calls)

    def run_watched(self, exited):
        """Harness.run with a fake Host and process watcher; returns the call order and the report."""
        order = []
        class Client:
            def __init__(self, *arguments):
                self.tool_calls, self.errors, self.process = [], [], types.SimpleNamespace(returncode=0)
            def request(self, method, params=None): return {}
            def notify(self, method): pass
            def tool(self, name, arguments=None):
                order.append(name)
                return {"processId": 4242} if name == "tia_session_status" else {"disposedOwnedPortalConnection": True, "attached": False}
            def close(self): order.append("close"); return {"hostExitCode": 0, "toolCalls": []}
        class Watcher:
            def __init__(self, process_id): self.evidence = {"processId": process_id, "handleCaptured": True}
            def wait_for_exit(self, timeout_ms):
                order.append("wait")
                return {"completed": exited, "processExitVerified": exited, "handleClosed": True}
            def close(self): return True
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(harness, "Client", Client), mock.patch.object(harness, "OwnedTiaProcessWatcher", Watcher):
            runner = Path(folder) / "runner.py"; runner.write_text("runner")
            args = types.SimpleNamespace(run_native=True, artifacts=Path(folder), host=runner, worker=runner, runner=runner, timeout=1)
            h = harness.Harness(args, "dr_", "test", [], [], scratch=False)
            try:
                h.run(lambda h: h.watch_owned_tia())
                error = None
            except AssertionError as failure:
                error = failure
            return order, h.report, error

    def test_run_waits_for_the_owned_tia_after_detach_and_host_close(self):
        order, report, error = self.run_watched(True)
        self.assertIsNone(error)
        self.assertEqual(["tia_session_status", "tia_detach", "close", "wait"], order)
        self.assertTrue(report["completed"])
        self.assertTrue(report["tiaProcessExit"]["processExitVerified"])

    def test_a_lingering_owned_tia_fails_the_run(self):
        order, report, error = self.run_watched(False)
        self.assertIsNotNone(error)
        self.assertFalse(report["completed"])
        self.assertIn("did not exit", report["failure"])
        self.assertIn("tiaExitFailure", report)


if __name__ == "__main__":
    unittest.main()
