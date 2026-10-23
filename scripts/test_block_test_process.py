"""Injected Win32 handle outcomes; never opens or waits on a real process."""
import unittest

from block_test_process import OwnedTiaProcessWatcher, ProcessExitNotVerified, SYNCHRONIZE, QUERY_LIMITED_INFORMATION, WAIT_OBJECT_0, WAIT_TIMEOUT, WAIT_FAILED


class Api:
    def __init__(self, results=None, *, handle=71, close_ok=True):
        self.results = list(results or [])
        self.handle, self.close_ok = handle, close_ok
        self.now = 0.0
        self.calls = []

    def open_process(self, access, inherit, pid):
        self.calls.append(("open", access, inherit, pid))
        return self.handle

    def wait(self, handle, timeout):
        self.calls.append(("wait", handle, timeout))
        result = self.results.pop(0) if self.results else WAIT_TIMEOUT
        if isinstance(result, Exception):
            raise result
        if result == WAIT_TIMEOUT:
            self.now += timeout / 1000
        return result

    def close(self, handle):
        self.calls.append(("close", handle))
        return self.close_ok

    def last_error(self):
        return 5


class ProcessWatcherTests(unittest.TestCase):
    def test_exit_code_is_read_from_the_same_retained_handle_before_close(self):
        api = Api([WAIT_OBJECT_0])
        def exit_code(handle):
            api.calls.append(("exit_code", handle))
            return 73
        api.exit_code = exit_code
        watcher = OwnedTiaProcessWatcher(1234, api=api, clock=lambda: api.now, query_exit_code=True)
        evidence = watcher.wait_for_exit()
        self.assertEqual(("open", SYNCHRONIZE | QUERY_LIMITED_INFORMATION, False, 1234), api.calls[0])
        self.assertEqual([("exit_code", 71), ("close", 71)], api.calls[-2:])
        self.assertEqual(73, evidence["exitCode"])
        self.assertTrue(evidence["completed"])

    def test_exit_code_query_failure_revokes_completion_and_closes_handle(self):
        api = Api([WAIT_OBJECT_0])
        def exit_code(handle): raise OSError("query failed")
        api.exit_code = exit_code
        watcher = OwnedTiaProcessWatcher(1234, api=api, clock=lambda: api.now, query_exit_code=True)
        with self.assertRaises(ProcessExitNotVerified) as caught:
            watcher.wait_for_exit()
        self.assertFalse(caught.exception.evidence["completed"])
        self.assertTrue(caught.exception.evidence["handleClosed"])

    def watcher(self, api):
        return OwnedTiaProcessWatcher(1234, api=api, clock=lambda: api.now)

    def test_signaled_exact_handle_records_exit_without_claiming_project_unlock(self):
        api = Api([WAIT_OBJECT_0])
        evidence = self.watcher(api).wait_for_exit()
        self.assertEqual(("open", SYNCHRONIZE, False, 1234), api.calls[0])
        self.assertEqual([("wait", 71, 1000), ("close", 71)], api.calls[1:])
        self.assertTrue(evidence["completed"])
        self.assertTrue(evidence["processExitVerified"])
        self.assertTrue(evidence["handleClosed"])
        self.assertFalse(evidence["projectLockReleaseVerified"])

    def test_timeout_uses_bounded_chunks_and_monotonic_deadline_then_closes(self):
        api = Api()
        with self.assertRaises(ProcessExitNotVerified) as caught:
            self.watcher(api).wait_for_exit(2500)
        evidence = caught.exception.evidence
        self.assertEqual([1000, 1000, 500], [row[2] for row in api.calls if row[0] == "wait"])
        self.assertEqual(2500, evidence["elapsedMs"])
        self.assertEqual("timeout", evidence["failure"])
        self.assertFalse(evidence["processExitVerified"])
        self.assertTrue(evidence["handleClosed"])
        self.assertEqual(("close", 71), api.calls[-1])

    def test_pid_reuse_never_changes_captured_handle_or_reopens_by_pid(self):
        api = Api([WAIT_TIMEOUT, WAIT_OBJECT_0])
        watcher = self.watcher(api)
        api.handle = 99
        watcher.wait_for_exit()
        self.assertEqual(1, sum(row[0] == "open" for row in api.calls))
        self.assertEqual({71}, {row[1] for row in api.calls if row[0] in ("wait", "close")})

    def test_open_failure_fails_closed_without_invalid_wait_or_close(self):
        api = Api(handle=0)
        with self.assertRaises(ProcessExitNotVerified) as caught:
            self.watcher(api)
        self.assertEqual("OpenProcess", caught.exception.evidence["failure"])
        self.assertEqual(5, caught.exception.evidence["win32Error"])
        self.assertEqual([("open", SYNCHRONIZE, False, 1234)], api.calls)

    def test_failed_unexpected_and_throwing_waits_always_close(self):
        for result in (WAIT_FAILED, 128, OSError("injected")):
            with self.subTest(result=result):
                api = Api([result])
                with self.assertRaises(ProcessExitNotVerified) as caught:
                    self.watcher(api).wait_for_exit()
                self.assertFalse(caught.exception.evidence["completed"])
                self.assertFalse(caught.exception.evidence["processExitVerified"])
                self.assertTrue(caught.exception.evidence["handleClosed"])
                self.assertEqual(("close", 71), api.calls[-1])

    def test_close_failure_revokes_completion_even_after_signaled_exit(self):
        api = Api([WAIT_OBJECT_0], close_ok=False)
        with self.assertRaises(ProcessExitNotVerified) as caught:
            self.watcher(api).wait_for_exit()
        self.assertTrue(caught.exception.evidence["processExitVerified"])
        self.assertFalse(caught.exception.evidence["completed"])
        self.assertFalse(caught.exception.evidence["handleClosed"])
        self.assertEqual(5, caught.exception.evidence["closeWin32Error"])

    def test_manual_disposal_and_completed_wait_cannot_double_close_or_wait(self):
        for wait in (False, True):
            with self.subTest(wait=wait):
                api = Api([WAIT_OBJECT_0])
                watcher = self.watcher(api)
                if wait:
                    watcher.wait_for_exit()
                self.assertTrue(watcher.close())
                self.assertTrue(watcher.close())
                with self.assertRaises(ProcessExitNotVerified):
                    watcher.wait_for_exit()
                self.assertEqual(1, sum(row[0] == "close" for row in api.calls))

    def test_invalid_wait_timeout_still_disposes_captured_handle(self):
        for timeout in (0, True, 300001):
            with self.subTest(timeout=timeout):
                api = Api()
                with self.assertRaises(ProcessExitNotVerified):
                    self.watcher(api).wait_for_exit(timeout)
                self.assertEqual([("open", SYNCHRONIZE, False, 1234), ("close", 71)], api.calls)

    def test_invalid_pid_and_chunk_bounds_do_not_call_win32(self):
        api = Api()
        for pid in (0, -1, True, 0x100000000):
            with self.subTest(pid=pid), self.assertRaises(ValueError):
                OwnedTiaProcessWatcher(pid, api=api)
        for chunk in (0, True, 1001):
            with self.subTest(chunk=chunk), self.assertRaises(ValueError):
                OwnedTiaProcessWatcher(1234, api=api, wait_chunk_ms=chunk)
        self.assertFalse(api.calls)


if __name__ == "__main__":
    unittest.main()
