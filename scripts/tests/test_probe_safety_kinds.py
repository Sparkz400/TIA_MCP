"""Offline check of the F-destination address choice in probe_safety_kinds.py."""
import unittest

from probe_safety_kinds import DEST, free_destination


class FreeDestinationTests(unittest.TestCase):
    def test_nearest_unused_below_first_then_above(self):
        modules = {"a": {DEST: 1022}, "b": {DEST: 1021}, "c": {}}
        self.assertEqual(1020, free_destination(1022, modules))
        self.assertEqual(3, free_destination(1, {"a": {DEST: 1}, "b": {DEST: 2}}))


if __name__ == "__main__":
    unittest.main()
