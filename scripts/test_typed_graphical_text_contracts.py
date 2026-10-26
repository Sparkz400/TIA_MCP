"""Whole-string typed identities in the public schemas; no native calls or generated oracles."""
import copy
import json
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET
from jsonschema import Draft202012Validator
from referencing import Registry, Resource

ROOT = Path(__file__).resolve().parents[1]


def schemas():
    return {p.name: json.loads(p.read_text(encoding="utf-8")) for p in (ROOT / "contracts").glob("typed-graphical*.schema.json")}


class TypedGraphicalTextContracts(unittest.TestCase):
    def test_restricted_fields_consume_every_character(self):
        docs=schemas(); model=docs["typed-graphical-model.schema.json"]["$defs"]
        instance=docs["typed-graphical-change.schema.json"]["$defs"]["instance"]["properties"]
        fields=[("logical key",model["key"],"rung_1.inner-out","a"*128),
                ("native ID",model["nativeId"],"A09f","1"*10),
                ("native UId",model["node"]["properties"]["nativeUid"],"21","1"*10),
                ("connection node",model["endpoint"]["properties"]["node"],"$input","$"+"a"*128),
                ("block name",model["creation"]["properties"]["name"],"NewBlock","A"*80),
                ("interface name",model["declaration"]["properties"]["name"],"State","A"*128),
                ("instance key",instance["key"],"state.member","A"*128),
                ("instance name",instance["name"],"State","A"*80)]
        for label,schema,valid,maximum in fields:
            check=Draft202012Validator(schema);check.validate(valid);check.validate(maximum)
            self.assertFalse(check.is_valid(maximum+"A"),label)
            for suffix in ("\n","\r","\r\n","\u2028","\u2029","\0"," "):
                with self.subTest(field=label,suffix=repr(suffix)):
                    self.assertFalse(check.is_valid(valid+suffix))
                    self.assertFalse(check.is_valid(suffix+valid))
                    self.assertFalse(check.is_valid(valid[:1]+suffix+valid[1:]))

    def test_complete_independently_authored_corpus_stays_admissible(self):
        docs=schemas(); resources=[]
        for name,schema in docs.items():
            Draft202012Validator.check_schema(schema)
            resource=Resource.from_contents(schema)
            resources.extend(((name,resource),(schema["$id"],resource)))
        check=Draft202012Validator(docs["typed-graphical-change.schema.json"],registry=Registry().with_resources(resources))
        count=0
        for name in ("cases","extended-cases","numeric-cases","route-cases"):
            fixtures=json.loads((ROOT / f"fixtures/typed-graphical/{name}.v1.json").read_text(encoding="utf-8"))
            for case in fixtures["cases"]:
                value=copy.deepcopy(case["payload"])
                if value["model"]["operation"] != "createBlock":value["blockPath"]="Devices/PLC/Program blocks/Independent"
                # Qualification specifications contain placeholders until a native
                # capture supplies IDs. Use the existing independent XML fixture
                # here to check the resulting wire grammar, without native access.
                document=ET.parse(ROOT / case["referenceXml"])
                for field in ("networkId","insertAfterNetworkId"):
                    if value["model"].get(field)=="$first":
                        value["model"][field]=next(e.attrib["ID"] for e in document.iter() if e.tag=="SW.Blocks.CompileUnit")
                for node in value["model"]["nodes"]:
                    if node.get("nativeUid","").startswith("$part:"):
                        _,kind,ordinal=node["nativeUid"].split(":")
                        nodes=[e for e in document.iter() if e.tag.rsplit("}",1)[-1]=="Part" and e.get("Name")==kind]
                        node["nativeUid"]=nodes[int(ordinal)].attrib["UId"]
                with self.subTest(fixture=case["fixtureId"]):check.validate(value)
                count+=1
        self.assertEqual(90,count)  # + typed-insert-first-* and typed-insert-create*-ton-*

    def test_free_text_and_symbol_components_are_not_normalized_by_this_contract(self):
        model=schemas()["typed-graphical-model.schema.json"]["$defs"]
        # The restricted-key repair must not impose identifier grammar on quoted
        # symbolic components or on general paths. Their native resolution is separate.
        component=model["operand"]["properties"]["components"]["items"]
        for text in ("Mötör 1", "quoted.member", "Line\nName"):
            Draft202012Validator(component).validate(text)
        Draft202012Validator(model["path"]).validate("Devices/PLC/Program blocks/Named block")


if __name__ == "__main__":unittest.main()
