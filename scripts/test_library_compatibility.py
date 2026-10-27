"""Offline checks for the library compatibility qualifier."""
from pathlib import Path
import tempfile
import unittest

from library_native_harness import Harness
from qualify_library_compatibility import BROKEN_TYPE_SOURCE, UNSAVED_SOURCE, instantiate_case, tree_hash

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
        self.assertIn('"TiaMcpMissingType"', BROKEN_TYPE_SOURCE)


if __name__ == "__main__":
    unittest.main()
