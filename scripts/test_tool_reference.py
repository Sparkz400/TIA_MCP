"""Keep the generated MCP tool reference in step with the feature catalog."""
import unittest

from tool_reference import PAGE, ROOT, build, group_of, group_rules, named_flags, render


def feature(feature_id, tools, domain="session", prerequisites=()):
    return {"featureId": feature_id, "domain": domain, "title": feature_id, "mcpTools": tools,
            "prerequisites": list(prerequisites), "api": {}, "implementation": {"status": "implemented"},
            "qualification": {"status": "verified"}, "availability": {"status": "conditional"}}


class ToolReferenceTests(unittest.TestCase):
    def test_committed_page_is_current(self):
        self.assertEqual((ROOT / PAGE).read_text(encoding="utf-8"), build(),
                         "Run python -B scripts/tool_reference.py after changing the catalog or Host options.")

    def test_flags_match_whole_options_only(self):
        flags = ["--addin-handoff", "--addin-handoff-extra"]
        self.assertEqual(named_flags(feature("a", ["a"], prerequisites=["Host started with --addin-handoff."]), flags),
                         ["--addin-handoff"])

    def test_every_tool_needs_its_own_feature(self):
        with self.assertRaises(ValueError):
            render({"features": [feature("change.x", ["change_apply"])]}, [], [("change_", "core")])

    def test_shared_tools_list_their_other_features(self):
        page = render({"features": [feature("a", ["a"]), feature("b", ["b", "a"], "hardware")]}, [], [("", "core")])
        self.assertIn("| `a` | a | shipped | core |  | `b` |", page)
        self.assertLess(page.index("## hardware"), page.index("## session"))

    def test_groups_follow_the_host_prefix_table(self):
        rules = group_rules()
        self.assertEqual(group_of("plc_station_upload", rules), "simulation")
        self.assertEqual(group_of("plc_compare", rules), "core")
        self.assertEqual(group_of("feature_catalog", rules), "base")
        self.assertEqual(group_of("qualification_component_window", rules), "qualification")
        with self.assertRaises(ValueError):
            group_of("unknown_tool", rules)


if __name__ == "__main__":
    unittest.main()
