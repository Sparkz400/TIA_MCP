"""Offline checks for the read-only TIA V19 adapter: the Host's V19 tool map, the worker's operations and the
qualifier's expected surface agree, the worker refuses what it does not implement, and the qualifier's
version-independent content comparison reads SimaticML as intended."""
from pathlib import Path
import re
import unittest

from qualify_tia_v19 import V19_TOOLS, SourceWatch, below_project, content, inventory_view
import tempfile

ROOT = Path(__file__).resolve().parents[1]
SURFACE = (ROOT / "src/TiaMcp.Host/TiaVersionSurface.cs").read_text(encoding="utf-8")
SESSION = (ROOT / "src/TiaMcp.Openness.V19/V19Session.cs").read_text(encoding="utf-8")
PROGRAM = (ROOT / "src/TiaMcp.Openness.V19/Program.cs").read_text(encoding="utf-8")


def section(text, start, end):
    return text[text.index(start):text.index(end, text.index(start))]


def host_map():
    body = section(SURFACE, "V19Tools = new Dictionary", "};")
    return dict(re.findall(r'\["(\w+)"\] = "(\w+)"', body))


def worker_operations():
    body = section(SESSION, "internal static readonly string[] Operations", "};")
    return re.findall(r'"(\w+)"', body)


def dispatched():
    body = section(SESSION, "internal JObject Execute(string operation, JObject args)", "default:")
    return re.findall(r'case "(\w+)":', body)


class Surface(unittest.TestCase):
    def test_host_tools_map_exactly_onto_worker_operations(self):
        tools = host_map()
        self.assertEqual(set(tools), V19_TOOLS)
        self.assertEqual(sorted(tools.values()), sorted(worker_operations()))
        self.assertEqual(sorted(dispatched()), sorted(worker_operations()))

    def test_every_v19_tool_has_its_own_description(self):
        body = section(SURFACE, "V19Descriptions = new Dictionary", "};")
        described = dict(re.findall(r'\["(\w+)"\] = "([^"]+)"', body))
        self.assertEqual(set(described), V19_TOOLS)
        for tool, text in described.items():
            self.assertNotIn(".ap21", text, tool)
            self.assertNotRegex(text, r"\bV21\b(?! Host)", tool)

    def test_worker_refuses_unknown_operations_and_v21_options(self):
        default = SESSION[SESSION.index("default:"):SESSION.index("private JObject BridgeInfo")]
        self.assertIn('"UnsupportedOnTiaVersion"', default)
        self.assertIn("Version.Major != TiaMajor", PROGRAM)
        self.assertIn("internal const int TiaMajor = 19;", PROGRAM)
        self.assertNotIn("--enable-library-integrate-apply", PROGRAM)

    def test_worker_never_saves_compiles_or_archives(self):
        for call in (".Save(", ".SaveAs(", ".Archive(", ".Compile(", ".Import(", ".Delete(", "GenerateBlocksFromSource", "OpenWithUpgrade"):
            self.assertNotIn(call, SESSION, call)


BLOCK = """<?xml version="1.0" encoding="utf-8"?>
<Document><Engineering version="{v}" />
<SW.Blocks.FB ID="0"><AttributeList>
  <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/{v}">
    <Section Name="Input"><Member Name="Start" Datatype="Bool" /><Member Name="Setpoint" Datatype="Int"><StartValue>5</StartValue></Member></Section>
    <Section Name="Output"><Member Name="Motor" Datatype="&quot;TiaMcp_Motor&quot;" /></Section>
  </Sections></Interface>
  <MemoryLayout>Optimized</MemoryLayout><Name>TiaMcp_MotorCtrl</Name><Number>1</Number><ProgrammingLanguage>SCL</ProgrammingLanguage>{extra}
</AttributeList></SW.Blocks.FB></Document>"""

TABLE = """<?xml version="1.0" encoding="utf-8"?>
<Document><Engineering version="{v}" />
<SW.Tags.PlcTagTable ID="0"><AttributeList><Name>TiaMcp_Tags</Name></AttributeList><ObjectList>
  <SW.Tags.PlcTag ID="1"><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%I0.0</LogicalAddress><Name>Start</Name></AttributeList></SW.Tags.PlcTag>
  <SW.Tags.PlcUserConstant ID="2"><AttributeList><DataTypeName>Int</DataTypeName><Name>MaxSpeed</Name><Value>1500</Value></AttributeList></SW.Tags.PlcUserConstant>
</ObjectList></SW.Tags.PlcTagTable></Document>"""


class Content(unittest.TestCase):
    def test_block_content_ignores_the_engineering_version_and_unrelated_attributes(self):
        v19 = content(BLOCK.format(v="V19", extra=""), "block")
        v21 = content(BLOCK.format(v="V21", extra="<SetENOAutomatically>false</SetENOAutomatically>"), "block")
        self.assertEqual((v19[0], v21[0]), (["V19"], ["V21"]))
        self.assertEqual(v19[1], v21[1])
        self.assertEqual(v19[1]["interface"]["Input"][1], {"name": "Setpoint", "datatype": "Int", "startValue": "5", "members": []})
        self.assertEqual((v19[1]["kind"], v19[1]["Number"], v19[1]["MemoryLayout"]), ("SW.Blocks.FB", "1", "Optimized"))

    def test_block_content_detects_an_interface_change(self):
        changed = BLOCK.format(v="V21", extra="").replace('Name="Setpoint" Datatype="Int"', 'Name="Setpoint" Datatype="DInt"')
        self.assertNotEqual(content(BLOCK.format(v="V19", extra=""), "block")[1], content(changed, "block")[1])

    def test_tag_table_content_lists_tags_and_constants(self):
        _, table = content(TABLE.format(v="V19"), "tagTable")
        self.assertEqual(table, {"name": "TiaMcp_Tags", "tags": [["Start", "Bool", "%I0.0"]], "constants": [["MaxSpeed", "Int", "1500"]]})


class Paths(unittest.TestCase):
    def test_paths_compare_below_the_project_segment(self):
        self.assertEqual(below_project("/projects/TiaMcpV19Fixture/devices/S/items/PLC_1/plc/PLC_1"),
                         below_project("/projects/TiaMcpV19Fixture_V21/devices/S/items/PLC_1/plc/PLC_1"))
        with self.assertRaises(AssertionError):
            below_project("/devices/S")

    def test_inventory_view_refuses_truncation_and_warnings(self):
        row = {"path": "/projects/P/devices/S/items/PLC_1/plc/PLC_1/blocks/Main", "name": "Main", "number": 1, "kind": "OB",
               "programmingLanguage": "SCL", "isConsistent": True}
        result = {"completeness": {"truncated": False}, "warnings": [], "hardware": [], "plcs": [], "blocks": [row]}
        self.assertEqual(inventory_view(result)["consistency"], {"/devices/S/items/PLC_1/plc/PLC_1/blocks/Main": True})
        for broken in ({**result, "warnings": ["x"]}, {**result, "completeness": {"truncated": True}}):
            with self.assertRaises(AssertionError):
                inventory_view(broken)


class Sources(unittest.TestCase):
    def test_only_the_search_index_may_change_on_open(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "IM/SearchIndex").mkdir(parents=True)
            (root / "P.ap19").write_bytes(b"project")
            (root / "IM/SearchIndex/segments_1").write_bytes(b"a")
            h = type("H", (), {"report": {}})()
            watch = SourceWatch(h, root)
            (root / "IM/SearchIndex/segments_1").write_bytes(b"b")
            (root / "IM/SearchIndex/segments_2").write_bytes(b"c")
            self.assertEqual(watch.check("primaryOpen"), {"searchIndexFiles": 2})
            (root / "P.ap19").write_bytes(b"changed")
            with self.assertRaises(AssertionError):
                watch.check("secondaryOpen")


if __name__ == "__main__":
    unittest.main()
