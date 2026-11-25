"""Offline checks of the process image partition fixture and its native runner; nothing here talks to PLCSIM or TIA Portal."""
import json
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "fixtures" / "plcsim-process-image"


class ProcessImageFixture(unittest.TestCase):
    def setUp(self):
        self.definition = json.loads((FIXTURE / "fixture.json").read_text(encoding="utf-8"))

    def test_every_step_names_an_existing_file_and_its_blocks(self):
        for step in self.definition["steps"]:
            if "file" in step:
                text = (FIXTURE / step["file"]).read_text(encoding="utf-8")
                for block in step["blocks"]:
                    self.assertIn(block, text)

    def test_ob30_is_a_cyclic_interrupt_and_joins_partition_1(self):
        root = ET.parse(FIXTURE / "PipCyclic.xml").getroot()
        attributes = root.find("SW.Blocks.OB/AttributeList")
        self.assertEqual("30", attributes.findtext("Number"))
        self.assertEqual("CyclicInterrupt", attributes.findtext("SecondaryType"))
        assignment = [s for s in self.definition["steps"] if s["kind"] == "block_attributes_set"]
        self.assertEqual(1, len(assignment))
        self.assertEqual("PipCyclic", assignment[0]["blockName"])
        self.assertEqual(1, assignment[0]["attributes"]["ProcessImagePartNumber"])

    def test_signals_are_members_of_the_data_block_and_both_programs_count_them(self):
        db = (FIXTURE / "PipDb.scl").read_text(encoding="utf-8")
        counters = {"Ob1Count": FIXTURE / "Main.scl", "Ob30Count": FIXTURE / "PipCyclic.xml"}
        self.assertEqual(sorted(counters), sorted(s["member"] for s in self.definition["signals"]))
        for member, writer in counters.items():
            self.assertIn(member + " : DInt", db)
            self.assertIn(member, writer.read_text(encoding="utf-8"))

    def test_runner_covers_both_partition_modes_and_stays_off_bus(self):
        source = (ROOT / "scripts" / "qualify_plcsim_process_image.py").read_text(encoding="utf-8")
        compile(source, "qualify_plcsim_process_image.py", "exec")
        for mode in ("SingleStep_P", "TimespanSynchronized_P"):
            self.assertIn('"%s"' % mode, source)
        self.assertNotIn("SingleStep_Bus", source.split("LIMITATIONS")[0])


if __name__ == "__main__":
    unittest.main()
