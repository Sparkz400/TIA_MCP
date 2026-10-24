import tempfile
import unittest
from pathlib import Path

from native_lane import acquire, release


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


if __name__ == "__main__":
    unittest.main()
