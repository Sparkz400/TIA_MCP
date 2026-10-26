"""Offline checks of the retained transitive dependent-instance effect; no native evidence."""
import json
import unittest
from xml.etree import ElementTree as ET

from extract_typed_transitive_effects import EFFECT, local, verify
from smoke_mcp import ROOT


class TransitiveEffectTests(unittest.TestCase):
    def setUp(self):
        manifest = json.loads((ROOT / "fixtures/typed-graphical/instance-effects-transitive.v1.json").read_text(encoding="utf-8"))
        self.appended = tuple(m["name"] for m in manifest["nativeObservedEffect"]["appendedMembers"])
        self.documents = {tuple(key.split(":")): ET.parse(ROOT / row["path"]).getroot() for key, row in manifest["artifacts"].items()}

    def nested(self, block, name):
        host = next(m for m in self.documents[("after", block)].iter() if m.get("Datatype") == '"' + EFFECT + '"')
        return next(m for m in host.iter() if local(m) == "Member" and m.get("Name") == name)

    def test_retained_capture_shows_the_rule_at_both_depths_in_fbs_and_dbs(self):
        self.assertEqual({"Tgp_NestParent": 1, "Tgp_NestGrand": 1, "Tgp_NestParent_DB": 1, "Tgp_NestGrand_DB": 1},
                         verify(self.documents, self.appended))

    def test_top_level_attributes_on_a_nested_member_are_refused(self):
        self.nested("Tgp_NestGrand_DB", "Timer_TON").set("Remanence", "NonRetain")
        with self.assertRaises(AssertionError):
            verify(self.documents, self.appended)

    def test_a_changed_start_value_outside_the_appended_members_is_refused(self):
        start = next(e for e in self.documents[("after", "Tgp_NestParent_DB")].iter() if local(e) == "StartValue")
        start.text = "43"
        with self.assertRaises(AssertionError):
            verify(self.documents, self.appended)


if __name__ == "__main__":
    unittest.main()
