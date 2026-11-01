"""Offline checks of qualify_drives.py's drive object handles; nothing here starts TIA."""
import types
import unittest
from unittest import mock

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


if __name__ == "__main__":
    unittest.main()
