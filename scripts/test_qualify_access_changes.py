"""Offline checks of the pure helpers in qualify_access_changes.py; nothing here starts TIA."""
import json
import pathlib
import tempfile
import types
import unittest
from unittest import mock

import qualify_access_changes as q
import release_matrix

ROOT = pathlib.Path(__file__).resolve().parents[1]
CPU = "/projects/S/devices/St/items/PLC"
DONE = {"truncated": False}


def inspected(der="AAAA", role_rights=(), inherits=("role:R1",), web=False, workspaces=()):
    certificates = {"completeness": DONE, "syslogServers": [], "project": {"path": "D:/a/S.ap21"}, "devices": [{
        "objectPath": CPU, "globalCertificateStoreEnabled": False, "certificateManagement": None,
        "certificates": [{"certificateId": "1", "sha256": "ab", "observation": {"contentBase64": der, "association": {"storeId": CPU + "/certificates"}}}],
        "attributes": [{"name": "PlcCommunicationCertificate", "value": {"valueStatus": "displayOnly"}}]}]}
    access = {"completeness": DONE, "devices": [{"objectPath": CPU, "accessLevel": None}], "projectUmac": {
        "anonymousUserActive": None, "deviceRightScopes": ["/projects/S/devices/St"], "roleNames": {"role:R1": "Runtime", "role:Admin": "Engineering administrator"},
        "snapshot": {"entitiesComplete": True, "entities": [
            {"id": "user:U", "kind": "user", "rights": [], "inherits": list(inherits)},
            {"id": "role:R1", "kind": "role", "rights": list(role_rights), "inherits": []},
            {"id": "role:Admin", "kind": "role", "rights": [], "inherits": []}]}}}
    webs = {"completeness": DONE, "devices": [{"objectPath": CPU, "userDefinedPagesServiceExposed": True, "attributes": [{"name": "WebserverActivate", "value": web}],
            "observation": {"configuration": {"webEnabled": web, "systemPagesEnabled": False, "applicationInventoryComplete": True, "generationProperties": [],
                                              "applications": [{"applicationId": "A", "name": "A", "applicationType": "None", "isDefault": True, "properties": []}]}}}]}
    vci = {"completeness": DONE, "groups": [{"path": "vci/G", "name": "G", "kind": "system"}], "workspaces": list(workspaces)}
    return certificates, access, webs, vci


class AccessChangeTests(unittest.TestCase):
    def test_domains_key_state_by_identity_without_envelopes(self):
        state = q.domains(*inspected())
        self.assertEqual({"syslog", "certStore:" + CPU, "cert:%s/1" % CPU, "certAttr:%s/PlcCommunicationCertificate" % CPU, "umac", "umac:user:U",
                          "umac:role:Runtime", "umac:role:Engineering administrator", "access:" + CPU, "web:" + CPU, "webAttr:%s/WebserverActivate" % CPU,
                          "vciGroup:vci/G"}, set(state))
        self.assertEqual({"certificateId": "1", "sha256": "ab", "der": "AAAA"}, state["cert:%s/1" % CPU])
        self.assertEqual(["Runtime"], state["umac:user:U"]["inherits"])
        self.assertNotIn("generationProperties", state["web:" + CPU])
        self.assertEqual(q.domains(*inspected()), q.domains(*inspected()))
        changed = q.domains(*inspected(role_rights=[{"id": "Full", "scope": "/projects/S/devices/St"}], inherits=()))
        self.assertEqual({"added": [], "removed": [], "changed": ["umac:role:Runtime", "umac:user:U"]}, q.diff(state, changed))
        truncated = inspected()
        truncated[3]["completeness"] = {"truncated": True}
        with self.assertRaises(AssertionError):
            q.domains(*truncated)

    def test_split_separates_tolerated_side_effects_from_unexpected_changes(self):
        observed = {"added": ["cert:x/2", "umac:role:R"], "removed": [], "changed": ["web:x", "webAttr:x/Port"]}
        wanted = {"added": [], "removed": [], "changed": ["web:x"]}
        side, unexpected = q.split(observed, wanted, lambda category, key: key.startswith("webAttr:") or category == "added" and key.startswith("cert:"))
        self.assertEqual({"added": ["cert:x/2"], "changed": ["webAttr:x/Port"]}, side)
        self.assertEqual({"added": ["umac:role:R"]}, unexpected)

    def test_tolerance_predicates_stay_on_the_cpu(self):
        run = q.Run(types.SimpleNamespace(report={}))
        run.cpu = CPU
        self.assertTrue(run.web_side_effects("changed", "webAttr:%s/WebserverPort" % CPU))
        self.assertTrue(run.web_side_effects("changed", "certAttr:%s/WebserverCertificate" % CPU))
        self.assertTrue(run.web_side_effects("added", "cert:%s/2" % CPU))
        self.assertFalse(run.web_side_effects("changed", "cert:%s/1" % CPU))
        self.assertFalse(run.web_side_effects("changed", "certStore:" + CPU))
        self.assertFalse(run.web_side_effects("added", "cert:/projects/S/devices/Other/items/PLC/2"))
        self.assertFalse(run.web_side_effects("changed", "access:" + CPU))
        self.assertTrue(run.page_side_effects("changed", "web:" + CPU))
        self.assertFalse(run.page_side_effects("added", "block:%s/plc/PLC/blocks/WWW" % CPU))
        self.assertEqual("cert:%s/7" % CPU, run.cert(7))
        self.assertEqual({"coverage", "excluded", "skipped", "criteria", "sideEffects", "certificateIdentity", "notQualified"}, set(run.h.report))

    def test_phase_c_failure_leaves_only_user_pages_unqualified(self):
        run = q.Run(types.SimpleNamespace(report={}, persist=lambda: None))
        with mock.patch.object(q, "user_pages", side_effect=AssertionError("GenerateBlocks refused")):
            q.pages(run)
        self.assertEqual({"webserver_user_pages_generate": "Phase C failed: AssertionError: GenerateBlocks refused"}, run.skipped)
        self.assertIs(run.skipped, run.h.report["skipped"])

    def test_umac_delta_names_roles_and_principals(self):
        before = {"roles": [{"identifier": "R1", "name": "Runtime", "system": False, "engineeringRights": [], "deviceRights": []}],
                  "principals": [{"kind": "projectUser", "name": "U", "active": True, "roles": ["R1"]}]}
        after = json.loads(json.dumps(before))
        after["roles"].append({"identifier": None, "name": "New", "system": False, "engineeringRights": [], "deviceRights": []})
        after["principals"][0]["roles"] = []
        self.assertEqual({"added": ["role:New"], "removed": [], "changed": ["projectUser:U"]}, q.diff(q.umac_keyed(before), q.umac_keyed(after)))
        self.assertEqual({"action": "create", "roleName": "New", "userName": None, "engineeringRights": [], "deviceRights": []},
                         q.umac_effect({"action": "create", "roleName": "New"}))

    def test_identity_exact_compares_name_validity_and_alternative_names(self):
        request = {"subjectCommonName": "Q", "validFromUtc": "2026-10-28T10:00:00Z", "validUntilUtc": "2027-10-28T10:00:00Z",
                   "subjectAlternativeNames": [{"type": "Dns", "value": "q.local"}, {"type": "IP", "value": "192.168.0.1"}]}
        row = {"subjectCommonName": "Q", "notBeforeUtc": "2026-10-28T10:00:00Z", "notAfterUtc": "2027-10-28T10:00:00Z",
               "subjectAlternativeNames": [{"type": "IP", "value": "192.168.0.1"}, {"type": "Dns", "value": "q.local"}]}
        self.assertTrue(q.identity_exact(request, row))
        # The wave 10 probe's compile re-issue: a new notBefore and the CPU address as alternative names.
        self.assertFalse(q.identity_exact(request, {**row, "notBeforeUtc": "2026-10-28T10:00:02Z"}))
        self.assertFalse(q.identity_exact(request, {**row, "subjectAlternativeNames": [{"type": "IP", "value": "192.168.0.1"}, {"type": "Dns", "value": "192.168.0.1"}]}))
        self.assertFalse(q.identity_exact(request, {**row, "subjectAlternativeNames": None}))

    def test_web_pages_is_the_worker_preview_shape(self):
        entry = q.domains(*inspected(web=True))["web:" + CPU]
        self.assertEqual({"systemWebPagesEnabled": False, "webserverActivate": True, "applications": [{"name": "A", "applicationType": "None", "isDefault": True}]},
                         q.web_pages(entry))

    def test_files_under_lists_relative_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder) / "root"
            self.assertEqual([], q.files_under(root))
            (root / ".vci").mkdir(parents=True)
            (root / ".vci" / "workspace.vci.config").write_text("x", encoding="utf-8")
            self.assertEqual([".vci/workspace.vci.config"], q.files_under(root))

    def test_covered_and_excluded_kinds_are_the_d12_x03_d01_change_kinds(self):
        catalog = json.loads((ROOT / release_matrix.CATALOG).read_text(encoding="utf-8"))
        ledger = json.loads((ROOT / release_matrix.LEDGER).read_text(encoding="utf-8"))
        owners = {row["featureId"]: row["obligations"] for row in ledger["catalogFeatures"]}
        owned = {f["changeKind"] for f in catalog["features"] if f["changeKind"] and {"D12", "X03", "D01"} & set(release_matrix.owners(f["featureId"], owners))}
        self.assertEqual(len(q.KINDS), len(set(q.KINDS)))
        self.assertFalse(set(q.KINDS) & set(q.EXCLUDED))
        self.assertTrue(set(q.PLC_COMPILED) <= set(q.KINDS))
        self.assertEqual(owned, set(q.KINDS) | set(q.EXCLUDED))
        criteria = {f["changeKind"]: f["acceptanceCriteria"] for f in catalog["features"] if f["changeKind"] in q.KINDS}
        self.assertTrue(all(set(found) <= set(q.CRITERIA) for found in criteria.values()), criteria)


if __name__ == "__main__":
    unittest.main()
