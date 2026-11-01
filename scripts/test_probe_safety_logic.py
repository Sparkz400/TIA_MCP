"""Offline checks of the changed F-logic and the behavior classification in probe_safety_logic.py; nothing here starts TIA."""
import re
import unittest
import xml.etree.ElementTree as ElementTree

import probe_safety_logic as p

# The generated main safety block as V21 exports it (local.safety_import.device-compile.20261028), trimmed to its structure.
GENERATED = """<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V21" />
  <SW.Blocks.FB ID="0">
    <AttributeList>
      <Name>Main_Safety_RTG1</Name>
      <Number>1</Number>
      <ProgrammingLanguage>F_FBD</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <MultilingualText ID="1" CompositionName="Comment"><ObjectList><MultilingualTextItem ID="2" CompositionName="Items"><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
      <SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits">
        <AttributeList>
          <NetworkSource />
          <ProgrammingLanguage>F_FBD</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID="4" CompositionName="Comment"><ObjectList><MultilingualTextItem ID="5" CompositionName="Items"><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
          <MultilingualText ID="6" CompositionName="Title"><ObjectList><MultilingualTextItem ID="7" CompositionName="Items"><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>
      <MultilingualText ID="8" CompositionName="Title"><ObjectList><MultilingualTextItem ID="9" CompositionName="Items"><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
    </ObjectList>
  </SW.Blocks.FB>
</Document>"""
FLG = "{%s}" % p.FLG


class ChangedLogicTests(unittest.TestCase):
    def test_two_networks_with_unique_ids(self):
        changed = p.changed_main(GENERATED, 16, "SafeOut", "SafeAlive")
        ids = re.findall(r' ID="([0-9A-F]+)"', changed)
        self.assertEqual(len(ids), len(set(ids)))
        root = ElementTree.fromstring(changed.encode("utf-8"))
        units = root.findall(".//SW.Blocks.CompileUnit")
        self.assertEqual(2, len(units))
        networks = [unit.find("AttributeList/NetworkSource/" + FLG + "FlgNet") for unit in units]
        symbols = [[c.get("Name") for c in n.iter(FLG + "Component")] for n in networks]
        self.assertEqual([["SafeOut"], ["SafeAlive"]], symbols)
        address = networks[0].find(".//" + FLG + "Address")
        self.assertEqual({"Area": "Input", "Type": "Bool", "BitOffset": "16"}, address.attrib)
        self.assertEqual("TRUE", networks[1].find(".//" + FLG + "ConstantValue").text)
        for net in networks:
            self.assertEqual(["Coil"], [part.get("Name") for part in net.iter(FLG + "Part")])
            self.assertEqual({"in", "operand"}, {c.get("Name") for c in net.iter(FLG + "NameCon")})
        self.assertEqual(["F_FBD", "F_FBD"], [u.find("AttributeList/ProgrammingLanguage").text for u in units])

    def test_only_an_empty_generated_network_is_changed(self):
        changed = p.changed_main(GENERATED, 0, "SafeOut", "SafeAlive")
        with self.assertRaises(AssertionError):
            p.changed_main(changed, "FdiCh0", "SafeOut", "SafeAlive")
        with self.assertRaises(AssertionError):
            p.changed_main(GENERATED.replace("F_FBD", "FBD"), "FdiCh0", "SafeOut", "SafeAlive")


class ClassifyTests(unittest.TestCase):
    def steps(self, outs, qbad=None):
        return [{"written": w, "safeOut": o, "qbad": qbad} for w, o in zip((True, False, True), outs)]

    def test_outcomes(self):
        self.assertEqual("behaviorQualified", p.classify({"alive": True, "steps": self.steps((True, False, True), False)}))
        self.assertEqual("fIoPassivated", p.classify({"alive": True, "steps": self.steps((False, False, False), True)}))
        self.assertEqual("inputInjectionIneffective", p.classify({"alive": True, "steps": self.steps((False, False, False), None)}))
        self.assertEqual("changedLogicNotObserved", p.classify({"alive": False, "steps": self.steps((True, False, True))}))
        self.assertEqual("changedLogicNotObserved", p.classify({"alive": {"quality": "bad"}, "steps": []}))

    def test_input_start_reads_the_module_subtree_once(self):
        rows = [{"path": "/m", "addresses": []}, {"path": "/m/items/m", "addresses": [{"ioType": "Input", "startAddress": 4}, {"ioType": "Output", "startAddress": 9}]},
                {"path": "/m/items/m", "addresses": [{"ioType": "Input", "startAddress": 4}]}, {"path": "/other", "addresses": [{"ioType": "Input", "startAddress": 0}]}]
        self.assertEqual(4, p.input_start(rows, "/m"))
        with self.assertRaises(AssertionError):
            p.input_start(rows + [{"path": "/m/items/x", "addresses": [{"ioType": "Input", "startAddress": 8}]}], "/m")



class SimulationCompileTests(unittest.TestCase):
    receipt = {"errorCount": 0, "targetIdentityPolicy": "device-path-v1",
               "targets": [{"targetPath": "/projects/P/devices/ScratchStation"}, {"targetPath": "/projects/P/devices/TiaMcpFStation"}]}

    def test_every_device_compiled(self):
        self.assertEqual(["/projects/P/devices/ScratchStation", "/projects/P/devices/TiaMcpFStation"],
                         p.simulation_compiled(self.receipt, ["/projects/P/devices/ScratchStation", "/projects/P/devices/TiaMcpFStation"]))

    def test_a_plc_compile_or_an_error_fails(self):
        for receipt in ({**self.receipt, "targets": self.receipt["targets"][1:]}, {**self.receipt, "errorCount": 1},
                        {"errorCount": 0, "targets": [{"target": "/projects/P/devices/ScratchStation/items/ScratchPLC/plc/ScratchPLC"}]}):
            with self.assertRaises(AssertionError):
                p.simulation_compiled(receipt, ["/projects/P/devices/ScratchStation", "/projects/P/devices/TiaMcpFStation"])


if __name__ == "__main__":
    unittest.main()
