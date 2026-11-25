"""The reference stdio client fails fast, with the Host's stderr, when the Host exits before or while answering."""
import sys
import time
import unittest

from smoke_mcp import Client, HostExited


def fake_host(code):
    return [sys.executable, "-I", "-c", code]


class ReferenceClientExitTests(unittest.TestCase):
    def test_host_that_exits_before_initialize_fails_fast_with_stderr(self):
        client = Client(None, 600, command=fake_host("import sys; sys.stderr.write('Unknown option: --bogus\\n'); sys.exit(2)"))
        client.process.wait(timeout=30)
        started = time.monotonic()
        with self.assertRaises(HostExited) as raised:
            client.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "test", "version": "1"}})
        self.assertLess(time.monotonic() - started, 10, "The client waited for the timeout instead of failing fast.")
        message = str(raised.exception)
        self.assertTrue(message.startswith("MCP host exited"), message)
        self.assertIn("Unknown option: --bogus", message)
        self.assertIn("exit code 2", message)
        # Every later request fails at once too.
        with self.assertRaises(HostExited):
            client.request("tools/list")

    def test_host_that_exits_while_a_request_is_pending_fails_fast(self):
        # Reads one request, then exits without answering.
        client = Client(None, 600, command=fake_host("import sys; sys.stdin.readline(); sys.stderr.write('worker missing\\n'); sys.exit(3)"))
        started = time.monotonic()
        with self.assertRaises(HostExited) as raised:
            client.request("initialize", {})
        self.assertLess(time.monotonic() - started, 10)
        self.assertIn("worker missing", str(raised.exception))
        self.assertIn("before answering initialize", str(raised.exception))
        client.process.wait(timeout=30)


if __name__ == "__main__":
    unittest.main()
