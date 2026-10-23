"""Offline packet binding checks. These never make native qualification claims."""
import copy
import json
import unittest

from typed_graphical_resolution_cases import MANIFEST, ROOT, packet


class ResolutionPacketTests(unittest.TestCase):
    def observations(self, language="LAD"):
        caller = "/plc/blocks/Tgp_TemplateFC_" + language
        db = "/plc/blocks/SelectedDB"
        return {"inventory": {"completeness": {"scopeComplete": True, "truncated": False},
                "blocks": [{"path": caller, "name": "Tgp_TemplateFC_" + language, "scopePath": "/plc"}, {"path": db, "name": "SelectedDB", "scopePath": "/plc"}],
                "tagTables": [{"path": "/plc/tags/Signals"}], "types": []},
                "bindings": {"caller": caller, "tags": "/plc/tags/Signals", "dbOne": db},
                "exports": {caller: (ROOT / "fixtures/typed-graphical/native" / ("Tgp_TemplateFC_" + language + ".xml")).read_text(encoding="utf-8-sig")}}

    def manifest(self):
        return json.loads(MANIFEST.read_bytes())

    def test_retained_lad_and_fbd_bind_observed_ids_without_mutation(self):
        for language in ("LAD", "FBD"):
            observations = self.observations(language)
            original = copy.deepcopy(observations)
            result = packet(self.manifest(), observations, ["tag-exact-owner", "db-first-owner", "db-wrong-kind"])
            self.assertEqual(observations, original)
            self.assertEqual("prepared-not-executed", result["status"])
            for field in ("engineeringVerified", "preservationVerified", "persistenceVerified", "recoveryVerified", "runtimeVerified"):
                self.assertIs(result[field], False)
            for case in result["cases"]:
                payload = case["arguments"]["payload"]
                self.assertEqual(language, payload["model"]["language"])
                self.assertEqual(observations["bindings"]["caller"], payload["blockPath"])
                self.assertEqual("3", payload["model"]["networkId"])
                self.assertIs(case["executed"], False)
            expected = result["cases"][0]["expect"]
            self.assertEqual("/plc/tags/Signals::5:Ready", expected["operandIdentity"])
            self.assertEqual("OperandOwnerMismatch", result["cases"][2]["expect"]["code"])

    def test_missing_or_duplicate_observed_identity_never_falls_back(self):
        for mutate in (lambda data: data["inventory"]["blocks"].clear(),
                       lambda data: data["inventory"]["blocks"].append(data["inventory"]["blocks"][0]),
                       lambda data: data["bindings"].update(tags="/plc/tags/Guessed")):
            data = self.observations()
            mutate(data)
            with self.assertRaises(ValueError):
                packet(self.manifest(), data, ["tag-exact-owner"])

    def test_partial_inventory_and_duplicate_cases_fail_closed(self):
        for field, value in (("scopeComplete", False), ("truncated", True)):
            data = self.observations()
            data["inventory"]["completeness"][field] = value
            with self.assertRaises(ValueError):
                packet(self.manifest(), data, ["tag-exact-owner"])
        with self.assertRaises(ValueError):
            packet(self.manifest(), self.observations(), ["tag-exact-owner"] * 2)
        with self.assertRaises(KeyError):
            packet(self.manifest(), self.observations(), ["not-an-authored-case"])

    def test_nested_type_assertions_use_exact_observed_dependencies(self):
        data = self.observations()
        data["bindings"].update(nestedCaller=data["bindings"]["caller"], outerType="/plc/types/B.Outer", innerType="/plc/types/B.Inner")
        data["inventory"]["types"] = [{"path": data["bindings"][role]} for role in ("outerType", "innerType")]
        result = packet(self.manifest(), data, ["nested-udt-namespace"])
        expected = result["cases"][0]["expect"]
        self.assertEqual(["/plc/types/B.Outer", "/plc/types/B.Inner"], expected["typeDependencyPaths"])
        self.assertEqual(data["bindings"]["caller"] + "::5:State5:Child4:Flag", expected["operandIdentity"])
        data["inventory"]["types"].pop()
        with self.assertRaises(ValueError):
            packet(self.manifest(), data, ["nested-udt-namespace"])

    def test_unsafe_or_non_graphical_xml_is_rejected(self):
        for xml in ("<!DOCTYPE Document><Document />", "<Document />", "<Document><SW.Blocks.CompileUnit ID='1'><AttributeList><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit></Document>"):
            data = self.observations()
            data["exports"][data["bindings"]["caller"]] = xml
            with self.assertRaises(ValueError):
                packet(self.manifest(), data, ["tag-exact-owner"])
        data = self.observations()
        data["inventory"]["blocks"][0]["name"] = "DifferentNativeOwner"
        with self.assertRaises(ValueError):
            packet(self.manifest(), data, ["tag-exact-owner"])


if __name__ == "__main__":
    unittest.main()
