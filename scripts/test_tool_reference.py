"""Keep the generated MCP tool reference in step with the feature catalog."""
import unittest

from tool_reference import PAGE, ROOT, build, named_flags, render


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
            render({"features": [feature("change.x", ["change_apply"])]}, [])

    def test_shared_tools_list_their_other_features(self):
        page = render({"features": [feature("a", ["a"]), feature("b", ["b", "a"], "hardware")]}, [])
        self.assertIn("| `a` | a | shipped |  | `b` |", page)
        self.assertLess(page.index("## hardware"), page.index("## session"))


if __name__ == "__main__":
    unittest.main()
