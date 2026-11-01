"""Offline checks of qualify_master_copy_tag_collision.py's tag-table view; nothing here starts TIA."""
import unittest

import qualify_master_copy_tag_collision as r


class FakeHarness:
    def __init__(self, plc, tables):
        self.plc, self.tables = plc, tables

    def tool(self, name, arguments=None):
        if name == "project_inventory":
            return {"plcs": [{"path": self.plc}]}
        assert name == "software_inspect" and arguments["plcPath"] == self.plc
        return {"tagTables": self.tables}


class ByNameTests(unittest.TestCase):
    def test_keys_tables_relative_to_the_plc_so_a_recovered_copy_compares_equal(self):
        tables = lambda plc: [{"path": plc + "/tagTables/McTagsOld", "tags": [
            {"name": "McSpare", "dataType": "Bool", "logicalAddress": "%M61.0", "isSafety": False},
            {"name": "McProbe", "dataType": "Bool", "logicalAddress": "%M60.0", "isSafety": False}]}]
        original = r.by_name(FakeHarness("/projects/A/devices/PLC_1/plc/PLC_1", tables("/projects/A/devices/PLC_1/plc/PLC_1")))
        recovered = r.by_name(FakeHarness("/projects/A_1/devices/PLC_1/plc/PLC_1", tables("/projects/A_1/devices/PLC_1/plc/PLC_1")))
        self.assertEqual(original, recovered)
        self.assertEqual({"/tagTables/McTagsOld": [["McProbe", "Bool", "%M60.0", False], ["McSpare", "Bool", "%M61.0", False]]}, original)

    def test_a_renamed_tag_differs(self):
        plc = "/projects/A/devices/PLC_1/plc/PLC_1"
        row = lambda name: [{"path": plc + "/tagTables/McTags", "tags": [{"name": name, "dataType": "Bool", "logicalAddress": "%M60.0", "isSafety": False}]}]
        self.assertNotEqual(r.by_name(FakeHarness(plc, row("McProbe"))), r.by_name(FakeHarness(plc, row("McProbe(1)"))))


if __name__ == "__main__":
    unittest.main()
