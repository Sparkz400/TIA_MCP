"""Report-file retry tests; no native runtime or PLC retries."""
import pathlib
import tempfile
import unittest
from unittest import mock

import run_block_tests as runner


def sharing_error(code):
    error = PermissionError("synthetic file sharing denial")
    error.winerror = code
    return error


class PendingReplaceTests(unittest.TestCase):
    def run_replace(self, outcomes, **options):
        with tempfile.TemporaryDirectory(dir=runner.ROOT / "artifacts/block-tests/test-temp") as directory:
            target = pathlib.Path(directory) / "result.json"
            pending = target.with_name(target.name + ".pending")
            target.write_bytes(b"previous report")
            pending.write_bytes(b"new report")
            now = [0.0]
            calls = []
            original = pathlib.Path.replace
            def replace(path, destination):
                calls.append((path, destination, path.read_bytes()))
                outcome = outcomes.pop(0)
                if outcome:
                    raise outcome
                return original(path, destination)
            def sleep(seconds):
                now[0] += seconds
            with mock.patch.object(pathlib.Path, "replace", replace):
                try:
                    result = runner.replace_owned_pending(pending, target, clock=lambda: now[0], sleep=sleep, **options)
                except OSError as error:
                    result = error
            return result, calls, now[0], target.read_bytes(), pending.read_bytes() if pending.exists() else None

    def test_only_sharing_denials_retry_same_pending_bytes_until_success(self):
        result, calls, elapsed, target, pending = self.run_replace([sharing_error(5), sharing_error(32), sharing_error(33), None])
        self.assertEqual({"attempts": 4}, result)
        self.assertEqual(0.075, round(elapsed, 3))
        self.assertTrue(all(row == calls[0] for row in calls))
        self.assertEqual(b"new report", target)
        self.assertIsNone(pending)

    def test_expired_budget_surfaces_error_and_preserves_both_files(self):
        error = sharing_error(32)
        result, calls, elapsed, target, pending = self.run_replace([error] * 4, budget_ms=50, interval_ms=25)
        self.assertIs(error, result)
        self.assertEqual(2, len(calls))
        self.assertEqual(0.05, elapsed)
        self.assertEqual(b"previous report", target)
        self.assertEqual(b"new report", pending)

    def test_other_filesystem_errors_are_never_retried(self):
        for error in (OSError("disk failure"), sharing_error(2), sharing_error(112)):
            with self.subTest(error=error):
                result, calls, elapsed, _, pending = self.run_replace([error])
                self.assertIs(error, result)
                self.assertEqual(1, len(calls))
                self.assertEqual(0, elapsed)
                self.assertEqual(b"new report", pending)

    def test_zero_budget_and_invalid_file_pair_never_sleep(self):
        error = sharing_error(5)
        result, calls, elapsed, _, _ = self.run_replace([error], budget_ms=0)
        self.assertIs(error, result)
        self.assertEqual(1, len(calls))
        self.assertEqual(0, elapsed)
        with mock.patch.object(pathlib.Path, "replace") as replace:
            for pending, target in ((pathlib.Path("a.pending"), pathlib.Path("b")),
                                    (pathlib.Path("other/a.pending"), pathlib.Path("a"))):
                with self.subTest(pending=pending), self.assertRaises(ValueError):
                    runner.replace_owned_pending(pending, target)
            replace.assert_not_called()


if __name__ == "__main__":
    unittest.main()
