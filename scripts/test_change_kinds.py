import json
import unittest

import change_kinds


class ChangeKindsTests(unittest.TestCase):
    def test_committed_index_is_current(self):
        self.assertEqual((change_kinds.ROOT / change_kinds.OUTPUT).read_text(encoding="utf-8"), change_kinds.render(change_kinds.build()),
                         "Run python -B scripts/change_kinds.py after changing a change kind or its documentation row.")

    def test_every_catalog_kind_is_listed_and_fields_come_from_the_docs(self):
        document = change_kinds.build()
        catalog = json.loads((change_kinds.ROOT / "contracts/feature-catalog.v1.json").read_text(encoding="utf-8"))
        kinds = {f["changeKind"] for f in catalog["features"] if f.get("changeKind")}
        self.assertEqual(kinds, set(document["kinds"]))
        call = document["kinds"]["call_parameter_set"]
        self.assertTrue(call["documented"])
        for name in ("networkId", "callUid", "parameterName", "components"):
            self.assertIn(name, call["fields"])
        self.assertEqual("hmi-engineering.v1.schema.json", document["kinds"]["hmi_screen_create"]["schema"])
        undocumented = [k for k, v in document["kinds"].items() if not v["documented"]]
        self.assertTrue(all("fields" not in document["kinds"][k] for k in undocumented))


if __name__ == "__main__":
    unittest.main()
