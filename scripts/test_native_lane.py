import tempfile
import unittest
from pathlib import Path

import os
import subprocess
import sys

from native_lane import acquire, release, run


class NativeLaneTests(unittest.TestCase):
    def test_one_holder_at_a_time_and_only_the_holder_releases(self):
        with tempfile.TemporaryDirectory() as directory:
            lock = Path(directory, "native-lane.lock")
            token = acquire("lane-a", lock=lock)
            with self.assertRaises(TimeoutError):
                acquire("lane-b", poll_seconds=0, timeout_seconds=0, lock=lock)
            with self.assertRaises(PermissionError):
                release("not-the-token", lock=lock)
            release(token, lock=lock)
            release(acquire("lane-b", lock=lock), lock=lock)
            self.assertFalse(lock.exists())


    def test_run_holds_the_lock_only_for_the_command_and_returns_its_code(self):
        with tempfile.TemporaryDirectory() as directory:
            lock = Path(directory, "native-lane.lock")
            code = run("lane-a", [sys.executable, "-c", f"import pathlib,sys; sys.exit(0 if pathlib.Path(r'{lock}').exists() else 9)"], lock=lock)
            self.assertEqual(0, code)
            self.assertFalse(lock.exists())
            self.assertEqual(3, run("lane-a", [sys.executable, "-c", "import sys; sys.exit(3)"], lock=lock))
            self.assertFalse(lock.exists())


    def test_cli_run_executes_the_command(self):
        with tempfile.TemporaryDirectory() as directory:
            lock = Path(directory, "native-lane.lock")
            script = Path(__file__).with_name("native_lane.py")
            done = subprocess.run([sys.executable, str(script), "run", "--owner", "probe", "--", sys.executable, "-c", "print('ran')"],
                                  capture_output=True, text=True, env={**os.environ, "TIA_MCP_NATIVE_LANE_LOCK": str(lock)})
            self.assertEqual(0, done.returncode, done.stderr)
            self.assertIn("ran", done.stdout)
            self.assertFalse(lock.exists())


if __name__ == "__main__":
    unittest.main()
