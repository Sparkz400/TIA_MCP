"""Verify the captured native dependency-effect evidence stays internally bound."""
import hashlib
import json
import unittest
from xml.etree import ElementTree as ET

from smoke_mcp import ROOT
from qualify_typed_graphical import native_hash


class InstanceEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.evidence = json.loads((ROOT / "fixtures/typed-graphical/instance-effects.v1.json").read_text())

    def read(self, name):
        return ET.fromstring((ROOT / self.evidence["artifacts"][name]["path"]).read_bytes())

    def test_hashes_and_separate_qualification(self):
        values = [*self.evidence["artifacts"].values(), *self.evidence["sources"].values(), self.evidence["referenceCallee"]]
        for entry in values:
            self.assertEqual(hashlib.sha256((ROOT / entry["path"]).read_bytes()).hexdigest(), entry["sha256"])
        self.assertIs(self.evidence["builderUsed"], False)
        self.assertIs(self.evidence["nativeImportCompileVerified"], True)
        for gate in ("engineeringVerified", "preservationVerified", "persistenceVerified", "recoveryVerified", "runtimeVerified"):
            self.assertIs(self.evidence[gate], False)

    def test_appended_child_preserves_complete_existing_db(self):
        before, after = self.read("beforeInstance"), self.read("afterInstance")
        child = next(e for e in after.iter() if e.attrib.get("Name") == "Child")
        next(e for e in after.iter() if child in list(e)).remove(child)
        self.assertEqual(native_hash(ET.tostring(before, encoding="unicode")), native_hash(ET.tostring(after, encoding="unicode")))
        for document in (before, after):
            marker = next(e for e in document.iter() if e.attrib.get("Name") == "RetainedMarker")
            self.assertEqual(next(e.text for e in marker if e.tag.rsplit("}", 1)[-1] == "StartValue"), "42")

    def test_added_caller_member_equals_added_db_member(self):
        caller = next(e for e in self.read("afterCaller").iter() if e.attrib.get("Name") == "Child")
        instance = next(e for e in self.read("afterInstance").iter() if e.attrib.get("Name") == "Child")
        self.assertEqual(ET.tostring(caller), ET.tostring(instance))
        self.assertEqual({e.attrib["Datatype"] for e in caller.iter() if e.tag.rsplit("}", 1)[-1] == "Member" and e is not caller},
            {"Bool", "SInt", "Int", "DInt", "LInt", "USInt", "UInt", "UDInt", "ULInt", "Real", "LReal", "Byte", "Word", "DWord", "LWord", "Time", "LTime"})


if __name__ == "__main__":
    unittest.main()
