"""Offline checks of the pure helpers in qualify_plc_object_mutations.py; nothing here starts TIA."""
import json
import pathlib
import unittest

import qualify_plc_object_mutations as q
import release_matrix

ROOT = pathlib.Path(__file__).resolve().parents[1]
P = "/projects/S/devices/St/items/PLC/plc/PLC_1"


def inventory(project="D:/a/S.ap21", address="%M0.0", groups=(P + "/blocks",)):
    return {"blocks": [{"path": P + "/blocks/Main", "name": "Main", "number": 1, "identity": {"projectPath": project}}],
            "types": [{"path": P + "/types/U", "name": "U"}],
            "tagTables": [{"path": P + "/tags/T", "name": "T", "identity": {"projectPath": project},
                           "tags": [{"path": P + "/tags/T/tags/A", "name": "A", "logicalAddress": address}],
                           "constants": [{"path": P + "/tags/T/systemConstants/Local~Device", "name": "Local~Device", "kind": "system"}]}],
            "softwareScopes": [{"path": P, "groups": list(groups), "identity": {"projectPath": project}}]}


class PlcObjectMutationTests(unittest.TestCase):
    def test_view_ignores_project_paths_and_diff_names_exact_keys(self):
        self.assertEqual(q.view(inventory()), q.view(inventory(project="D:/recovery/S.ap21")))
        before = q.view(inventory(), {"block:" + P + "/blocks/Main": {"payloadSha256": "a"}})
        after = q.view(inventory(address="%M1.0", groups=(P + "/blocks", P + "/blocks/groups/G")), {"block:" + P + "/blocks/Main": {"payloadSha256": "b"}})
        self.assertEqual({"added": ["group:" + P + "/blocks/groups/G"], "removed": [],
                          "changed": ["block:" + P + "/blocks/Main", "member:" + P + "/tags/T/tags/A"]}, q.diff(before, after))
        self.assertEqual({"added": [], "removed": [], "changed": []}, q.diff(before, before))

    def test_matches_is_a_recursive_subset_with_exact_lists(self):
        self.assertTrue(q.matches({"effects": [{"name": "A"}]}, {"effects": [{"name": "A", "type": "System.String"}], "x": 1}))
        self.assertFalse(q.matches({"effects": []}, {"effects": [{"name": "A"}]}))
        self.assertFalse(q.matches({"name": None}, {}))
        self.assertTrue(q.matches({"name": None}, {"name": None}))
        self.assertFalse(q.matches({"after": False}, {"after": 0}))
        self.assertFalse(q.matches({"number": 1}, {"number": True}))

    def test_stage_order_text_edits_and_target_groups(self):
        self.assertTrue(q.in_order(["a", "x", "b"], ("a", "b")))
        self.assertFalse(q.in_order(["b", "a"], ("a", "b")))
        self.assertEqual("<N>2</N>", q.replace_once("<N>1</N>", "1", "2"))
        for text in ("<N>2</N>", "<N>1</N><N>1</N>"):
            with self.assertRaises(AssertionError):
                q.replace_once(text, "<N>1</N>", "x")
        expected = {"plc_compile": None, "group_rename": P + "/tags/groups/G", "tag_table_import_xml": P + "/tags", "constant_delete": P + "/tags",
                    "plc_type_import_xml": P + "/types", "type_source_upsert": P + "/types", "block_attributes_set": P + "/blocks", "source_upsert": P + "/blocks"}
        self.assertEqual(expected, {kind: q.target_group(kind, P, {"groupPath": P + "/tags/groups/G"}) for kind in expected})

    def test_coverage_gaps_name_each_missing_criterion(self):
        complete = {need: ["x"] for need in q.NEEDS}
        self.assertEqual({}, q.coverage_gaps({"tag_set": complete}, ["tag_set"]))
        self.assertEqual({"tag_set": ["restored"], "tag_create": list(q.NEEDS)},
                         q.coverage_gaps({"tag_set": {**complete, "restored": []}}, ["tag_set", "tag_create"]))

    def test_covered_and_excluded_kinds_are_the_x11_change_kinds(self):
        catalog = json.loads((ROOT / release_matrix.CATALOG).read_text(encoding="utf-8"))
        ledger = json.loads((ROOT / release_matrix.LEDGER).read_text(encoding="utf-8"))
        owners = {row["featureId"]: row["obligations"] for row in ledger["catalogFeatures"]}
        x11 = {f["changeKind"] for f in catalog["features"] if f["changeKind"] and "X11" in release_matrix.owners(f["featureId"], owners)}
        self.assertEqual(len(q.KINDS), len(set(q.KINDS)))
        self.assertFalse(set(q.KINDS) & set(q.EXCLUDED))
        self.assertEqual(x11, set(q.KINDS) | set(q.EXCLUDED))


if __name__ == "__main__":
    unittest.main()
