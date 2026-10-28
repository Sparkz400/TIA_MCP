"""Offline checks of qualify_safety_changes.py; nothing here starts TIA."""
import unittest

import qualify_safety_changes as q


class SafetyRunnerTests(unittest.TestCase):
    def test_reopen_ignores_only_compile_derived_hardware_attributes(self):
        before = {"hw:/a": {"name": "X1", "attributes": {"CalculatedBandwidthForCyclicIoData": 7040, "Name": "X1"}}, "block:/b": {"number": 1}}
        after = {"hw:/a": {"name": "X1", "attributes": {"CalculatedBandwidthForCyclicIoData": 0, "Name": "X1"}}, "block:/b": {"number": 1}}
        self.assertEqual(q.comparable(before), q.comparable(after))
        after["hw:/a"]["attributes"]["Name"] = "X2"
        self.assertNotEqual(q.comparable(before), q.comparable(after))


if __name__ == "__main__":
    unittest.main()
