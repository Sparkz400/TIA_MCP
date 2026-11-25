"""Offline checks for the protected-object and unit-relation library qualifiers."""
from types import SimpleNamespace
import unittest

from library_native_harness import Harness
from qualify_library_protected import consumer_source
from qualify_library_unit_relations import CONSUMER, PROVIDER, unit_scopes


class FakeClient:
    """change_prepare answers from a queue; inventories come from a list of snapshots."""
    def __init__(self, prepare, inventories):
        self.prepare, self.inventories = prepare, list(inventories)

    def request(self, method, params):
        return self.prepare


def harness(client):
    h = Harness.__new__(Harness)
    h.client, h.report = client, {"steps": [], "mutations": []}
    h.persist = lambda: None
    h.inventory = lambda: client.inventories.pop(0)
    h.apply_prepared = lambda kind, prepared: {"state": "applied", "changeId": prepared["changeId"]}
    return h


def refusal(code):
    return {"isError": True, "structuredContent": {"error": {"code": code, "message": "refused"}}}


class ApplyOrRefuseTests(unittest.TestCase):
    def test_typed_refusal_without_changes_is_recorded(self):
        h = harness(FakeClient(refusal("ProtectedObject"), [{"plc": 1}, {"plc": 1}]))
        result = h.apply_or_refuse("probe", "library_publish", {}, ("ProtectedObject",))
        self.assertEqual(result["outcome"], "refused")
        self.assertEqual(h.report["steps"][-1]["status"], "passed")

    def test_untyped_native_error_fails(self):
        h = harness(FakeClient(refusal("EngineeringError"), [{"plc": 1}, {"plc": 1}]))
        with self.assertRaises(AssertionError):
            h.apply_or_refuse("probe", "library_publish", {}, ("ProtectedObject",))
        self.assertEqual(h.report["steps"][-1]["status"], "failed")

    def test_refusal_that_changed_the_inventory_fails(self):
        h = harness(FakeClient(refusal("ProtectedObject"), [{"plc": 1}, {"plc": 2}]))
        with self.assertRaises(AssertionError):
            h.apply_or_refuse("probe", "library_publish", {}, ("ProtectedObject",))

    def test_prepared_change_is_applied(self):
        h = harness(FakeClient({"structuredContent": {"changeId": "lib-1", "previewHash": "x"}}, [{"plc": 1}]))
        result = h.apply_or_refuse("probe", "library_publish", {}, ("ProtectedObject",))
        self.assertEqual(result["outcome"], "applied")
        self.assertEqual(result["applied"]["changeId"], "lib-1")


class ConsumerSourceTests(unittest.TestCase):
    def test_one_db_and_call_per_callee(self):
        source = consumer_source(["A", "B"])
        self.assertEqual(source.count("DATA_BLOCK"), 4)  # two blocks, each opened and closed
        self.assertIn('"A_DB"(Enable := TRUE);', source)
        self.assertIn('"B_DB"(Enable := TRUE);', source)
        self.assertIn('"TiaMcp_EnableGate_DB"(Enable := TRUE);', source)

    def test_no_callees_keeps_only_main(self):
        self.assertNotIn("DATA_BLOCK", consumer_source([]))


class UnitScopeTests(unittest.TestCase):
    def scopes(self, relations, provider_relations=None):
        return [{"name": "PLC_1", "path": "/p"}, {"name": PROVIDER, "path": "/p/u/1", "relations": provider_relations or []},
                {"name": CONSUMER, "path": "/p/u/2", "relations": relations}]

    def test_single_relation_to_the_provider(self):
        units = unit_scopes(self.scopes([{"relationType": "SoftwareUnit", "relatedObject": PROVIDER}]))
        self.assertEqual(sorted(units), sorted([PROVIDER, CONSUMER]))

    def test_missing_wrong_or_reverse_relations_fail(self):
        for relations, provider in (([], None), ([{"relationType": "NonUnitDB", "relatedObject": PROVIDER}], None),
                                    ([{"relationType": "SoftwareUnit", "relatedObject": PROVIDER}], [{"relationType": "SoftwareUnit", "relatedObject": CONSUMER}])):
            with self.assertRaises(AssertionError):
                unit_scopes(self.scopes(relations, provider))


if __name__ == "__main__":
    unittest.main()
