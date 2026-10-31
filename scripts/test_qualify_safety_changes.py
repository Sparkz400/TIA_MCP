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


    def test_unlisted_writable_parameters_get_a_different_value(self):
        allowlist = {"modules": [{"moduleTypeIdentifier": "T", "parameters": [{"name": "Failsafe_A", "status": "qualified"}, {"name": "Failsafe_P", "status": "pending"}]}]}
        rows = [{"name": "Failsafe_A", "accessMode": "ReadWrite", "value": True}, {"name": "Failsafe_P", "accessMode": "ReadWrite", "value": True},
                {"name": "Failsafe_N", "accessMode": "ReadWrite", "value": 0}, {"name": "Failsafe_R", "accessMode": "Read", "value": 1},
                {"name": "Failsafe_S", "accessMode": "ReadWrite", "value": "x"}]
        self.assertEqual({"Failsafe_P": (True, False), "Failsafe_N": (0, 1)}, q.unlisted_writable(rows, "T", allowlist))
        self.assertEqual({"Failsafe_A": (True, False), "Failsafe_P": (True, False), "Failsafe_N": (0, 1)}, q.unlisted_writable(rows, "Other", allowlist))


if __name__ == "__main__":
    unittest.main()
