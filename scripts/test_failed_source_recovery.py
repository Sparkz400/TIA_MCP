"""Offline checks of the failed-source refusal preconditions; no native evidence."""
import unittest

from qualify_failed_source_recovery import refusal


class RefusalTests(unittest.TestCase):
    def capture(self):
        return {"status": "blocked", "restoreDispatched": True, "failureRecoveryError": "change_restore: FingerprintUnavailable",
                "failureStatus": {"state": "recovery_required", "afterFingerprint": None, "changeId": "c"}}

    def test_uncertain_after_state_with_refused_restore_is_accepted(self):
        self.assertEqual("c", refusal(self.capture())["changeId"])

    def test_known_after_state_completed_or_unrefused_runs_are_not_failed_source_evidence(self):
        for fault in ("known-after", "passed", "no-restore", "restored"):
            capture = self.capture()
            if fault == "known-after": capture["failureStatus"]["afterFingerprint"] = "abc"
            if fault == "passed": capture["status"] = "passed"
            if fault == "no-restore": capture["restoreDispatched"] = False
            if fault == "restored": capture.pop("failureRecoveryError")
            with self.subTest(fault=fault), self.assertRaises(RuntimeError):
                refusal(capture)


if __name__ == "__main__":
    unittest.main()
