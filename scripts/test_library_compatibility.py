"""Offline checks for the library compatibility qualifier."""
from pathlib import Path
import tempfile
import unittest

from library_native_harness import Harness
from qualify_library_compatibility import UNSAVED_SOURCE, add_oldest_firmware_cpu, instantiate_case, tree_hash

PREPARED = {"structuredContent": {"changeId": "lib-1", "previewHash": "x"}}


def error(code, details=None):
    return {"isError": True, "structuredContent": {"error": {"code": code, "message": "m", "details": details}}}


class FakeClient:
    def __init__(self, *responses):
        self.responses = list(responses)

    def request(self, method, params):
        return self.responses.pop(0)


def harness(client, inventories):
    h = Harness.__new__(Harness)
    h.client, h.report = client, {"steps": [], "mutations": []}
    h.persist = lambda: None
    h.inventory = lambda: inventories.pop(0)
    return h


class InstantiateCaseTests(unittest.TestCase):
    def test_applied(self):
        h = harness(FakeClient(PREPARED, {"structuredContent": {"state": "applied", "journal": {"compile": {"errorCount": 0}}}}), [])
        self.assertEqual(instantiate_case(h, {}, {})["outcome"], "applied")
        self.assertEqual(h.report["mutations"][0]["state"], "applied")

    def test_typed_refusal_without_changes(self):
        h = harness(FakeClient(error("TargetIncompatible")), [{"plc": 1}])
        self.assertEqual(instantiate_case(h, {}, {"plc": 1})["outcome"], "refused")

    def test_refusal_that_changed_inventory_fails(self):
        h = harness(FakeClient(error("TargetIncompatible")), [{"plc": 2}])
        with self.assertRaises(AssertionError):
            instantiate_case(h, {}, {"plc": 1})

    def test_untyped_errors_fail(self):
        with self.assertRaises(AssertionError):
            instantiate_case(harness(FakeClient(error("EngineeringError")), [{}]), {}, {})
        with self.assertRaises(AssertionError):
            instantiate_case(harness(FakeClient(PREPARED, error("OutcomeUnknown")), []), {}, {})

    def test_compile_failure_without_save(self):
        details = {"journal": {"compile": {"errorCount": 2}, "events": [{"stage": "checkpoint_project_save", "state": "completed"}]}}
        h = harness(FakeClient(PREPARED, error("CompileFailed", details)), [])
        result = instantiate_case(h, {}, {})
        self.assertEqual((result["outcome"], result["compile"]["errorCount"]), ("failedUnsaved", 2))
        self.assertEqual(h.report["mutations"][0]["state"], "failed")

    def test_failure_after_a_completed_save_fails(self):
        details = {"journal": {"events": [{"stage": "save_project", "state": "completed"}]}}
        with self.assertRaises(AssertionError):
            instantiate_case(harness(FakeClient(PREPARED, error("NativeLibraryError", details)), []), {}, {})


class OldestFirmwareTests(unittest.TestCase):
    def catalog(self, *versions):
        entries = [{"typeName": "CPU 1511-1 PN", "version": v, "typeIdentifier": "OrderNumber:6ES7 511-1AK0x-0AB0/" + v} for v in versions]
        entries.append({"typeName": "CPU 1511-1 PN/DP", "version": "V1.0", "typeIdentifier": "other"})
        h = harness(None, [])
        h.tool = lambda name, arguments: {"entries": entries}
        return h

    def test_picks_the_oldest_firmware_of_the_exact_type(self):
        h = self.catalog("V4.1", "V2.0", "V2.10")
        payload = add_oldest_firmware_cpu(h, "CPU 1511-1 PN")
        self.assertTrue(payload["typeIdentifier"].endswith("/V2.0"))
        self.assertFalse(payload["configureScratchSecurity"])
        self.assertEqual(h.report["addedCpus"][0]["version"], "V2.0")

    def test_security_only_from_firmware_3_1(self):
        self.assertTrue(add_oldest_firmware_cpu(self.catalog("V3.1", "V4.1"), "CPU 1511-1 PN")["configureScratchSecurity"])


class FixtureTests(unittest.TestCase):
    def test_tree_hash_covers_names_and_bytes(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "a").write_bytes(b"1")
            first = tree_hash(root)
            (root / "a").write_bytes(b"2")
            self.assertNotEqual(first, tree_hash(root))
            (root / "a").rename(root / "b")
            self.assertNotEqual(first, tree_hash(root))

    def test_sources_declare_exactly_one_object(self):
        self.assertEqual(UNSAVED_SOURCE.count('FUNCTION "'), 1)


if __name__ == "__main__":
    unittest.main()
