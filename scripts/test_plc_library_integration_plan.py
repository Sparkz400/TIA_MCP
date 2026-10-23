"""Offline contract checks for the explicitly unexecuted native plan harness."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from qualify_plc_library_integration_plan import qualify_plan


class FakeClient:
    def __init__(self, root):
        self.calls = []
        self.owner = True
        self.complete = True
        self.claimed = False
        self.guard = True
        self.path = root / "caller.xml"
        self.path.write_text('<Document><SW.Blocks.OB ID="0"><AttributeList><Name>Main</Name></AttributeList></SW.Blocks.OB></Document>')

    def tool(self, name, arguments=None):
        self.calls.append(name)
        if name == "tia_session_status":
            return {"ownsPortalProcess": self.owner, "projects": [{"name": "TiaMcpScratch_plan"}]}
        if name == "software_inspect":
            assert 1 <= arguments["maxItems"] <= 2000
            return {"completeness": {"scopeComplete": self.complete}, "blocks": [], "types": [], "tagTables": []}
        if name == "block_read":
            return {"artifact": {"path": str(self.path), "sha256": hashlib.sha256(self.path.read_bytes()).hexdigest()}}
        if name == "change_prepare":
            return {"changeId": "lib-fixture", "previewHash": "hash", "preview": {"integrationPlan": {
                "applySupported": False, "nativeMutationDispatched": False, "versions": [],
                "steps": [{"kind": "library_instantiate"}, {"kind": "graphical_typed_edit"}]}}}
        if name == "change_status":
            return {"state": "prepared", "journal": {"checkpoints": [], "idempotencyKeyHash": "claimed" if self.claimed else None}}
        raise AssertionError("Unexpected tool " + name)

    def request(self, method, arguments):
        self.calls.append(arguments["name"])
        return {"isError": self.guard, "error": "PlcIntegrationNotQualified" if self.guard else "missing guard"}


class Tests(unittest.TestCase):
    def run_case(self, mutate=lambda c: None, run_native=True):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            client = FakeClient(root)
            mutate(client)
            result = qualify_plan(client, {"target": {"projectName": "TiaMcpScratch_plan", "plcPath": "/plc"},
                                          "integration": {"caller": {"blockPath": "/plc/blocks/Main"}}},
                                  root / "reports", run_native=run_native)
            self.assertTrue(result["completed"])
            self.assertFalse(result["installationVerified"])
            self.assertEqual(1, client.calls.count("change_apply"))
            persisted = json.loads(next((root / "reports").glob("*/qualification.json")).read_text())
            self.assertTrue(persisted["completed"])

    def test_mock_protocol_and_report(self): self.run_case()
    def test_explicit_native_intent_required(self):
        with self.assertRaisesRegex(AssertionError, "Explicit"): self.run_case(run_native=False)
    def test_ownership_required(self):
        with self.assertRaisesRegex(AssertionError, "owned Portal"): self.run_case(lambda c: setattr(c, "owner", False))
    def test_complete_inspection_required(self):
        with self.assertRaisesRegex(AssertionError, "complete"): self.run_case(lambda c: setattr(c, "complete", False))
    def test_missing_guard_stops_without_retry(self):
        with self.assertRaisesRegex(AssertionError, "not refused"): self.run_case(lambda c: setattr(c, "guard", False))
    def test_claim_created_by_refusal_fails(self):
        with self.assertRaisesRegex(AssertionError, "claim/checkpoint"): self.run_case(lambda c: setattr(c, "claimed", True))


if __name__ == "__main__": unittest.main()
