"""The frozen MCP surface baseline and the error registry: breaking-change rules and generated pages."""
import copy
import unittest

from error_registry import GATE_CODES, PAGE, ROOT, registry, render
from mcp_surface import breaking, frozen


def surface():
    tool = {"name": "t", "annotations": {"readOnlyHint": True, "destructiveHint": False, "openWorldHint": False},
            "inputSchema": {"type": "object", "properties": {"a": {"type": ["string", "null"], "default": None},
                                                             "mode": {"type": "string", "enum": ["x", "y"]}}, "required": ["mode"]},
            "outputSchema": {"type": "object", "properties": {"rows": {"type": "array", "items": {"type": "object", "properties": {"id": {"type": "string"}}}}},
                             "required": ["rows"]}}
    return {"protocolVersion": "2025-11-25", "tools": [tool, {"name": "new_tool", "inputSchema": {}}],
            "prompts": [{"name": "p", "arguments": [{"name": "goal", "required": True}]}],
            "resourceTemplates": [{"uriTemplate": "tia://x/{id}"}], "resources": [{"uri": "tia://feature-catalog"}]}


CATALOG = {"features": [{"featureId": "t", "api": {"status": "available"}, "implementation": {"status": "partial"},
                         "qualification": {"status": "verified"}, "availability": {"status": "conditional"}},
                        {"featureId": "new_tool", "api": {"status": "available"}, "implementation": {"status": "implemented"},
                         "qualification": {"status": "unverified"}, "availability": {"status": "conditional"}}]}


def cut(s):
    return frozen(s, CATALOG, ["OutcomeUnknown"], ["--artifacts", "--component-qualification"], "1.0.0")


class SurfaceTests(unittest.TestCase):
    def changed(self, edit):
        s = surface()
        edit(s)
        return breaking(cut(surface()), cut(s))

    def test_freeze_follows_support_class_and_drops_qualification_options(self):
        base = cut(surface())
        self.assertEqual(list(base["tools"]), ["t"])
        self.assertEqual(base["experimentalTools"], ["new_tool"])
        self.assertEqual(base["hostOptions"], ["--artifacts"])
        self.assertNotIn("openWorldHint", base["tools"]["t"]["annotations"])

    def test_identical_and_additive_surfaces_pass(self):
        self.assertEqual(self.changed(lambda s: None), [])
        def add(s):
            t = s["tools"][0]
            t["inputSchema"]["properties"]["b"] = {"type": "integer"}
            t["inputSchema"]["properties"]["mode"]["enum"].append("z")
            t["inputSchema"]["properties"]["a"]["type"].append("integer")
            t["outputSchema"]["properties"]["extra"] = {"type": "string"}
            t["annotations"]["destructiveHint"] = True
            s["prompts"][0]["arguments"].append({"name": "project"})
        self.assertEqual(self.changed(add), [])

    def test_breaking_changes_are_named(self):
        cases = {
            "parameter removed": lambda s: s["tools"][0]["inputSchema"]["properties"].pop("a"),
            "newly required": lambda s: s["tools"][0]["inputSchema"].__setitem__("required", ["mode", "a"]),
            "narrowed": lambda s: s["tools"][0]["inputSchema"]["properties"]["a"].__setitem__("type", "string"),
            "default": lambda s: s["tools"][0]["inputSchema"]["properties"]["a"].__setitem__("default", "x"),
            "enum values removed": lambda s: s["tools"][0]["inputSchema"]["properties"]["mode"].__setitem__("enum", ["x"]),
            "readOnlyHint": lambda s: s["tools"][0]["annotations"].__setitem__("readOnlyHint", False),
            "output field removed": lambda s: s["tools"][0]["outputSchema"]["properties"]["rows"]["items"]["properties"].pop("id"),
            "output schema removed": lambda s: s["tools"][0].pop("outputSchema"),
            "tool t: removed": lambda s: s["tools"].pop(0),
            "argument goal removed": lambda s: s["prompts"][0].__setitem__("arguments", []),
            "resource template": lambda s: s["resourceTemplates"].clear(),
            "protocol revision": lambda s: s.__setitem__("protocolVersion", "2026-01-01"),
        }
        for expected, edit in cases.items():
            with self.subTest(expected):
                found = self.changed(edit)
                self.assertTrue(any(expected in change for change in found), found)

    def test_a_frozen_tool_cannot_become_experimental(self):
        catalog = copy.deepcopy(CATALOG)
        catalog["features"][0]["qualification"]["status"] = "unverified"
        found = breaking(cut(surface()), frozen(surface(), catalog, ["OutcomeUnknown"], [], "1.0.0"))
        self.assertIn("tool t: removed (now experimental)", found)
        self.assertIn("stable error code OutcomeUnknown: removed", breaking(cut(surface()), frozen(surface(), CATALOG, [], [], "1.0.0")))


class ErrorRegistryTests(unittest.TestCase):
    def test_committed_page_is_current(self):
        self.assertEqual((ROOT / PAGE).read_text(encoding="utf-8"), render(registry()),
                         "Run python -B scripts/error_registry.py after adding an error code or naming one in the docs.")

    def test_gate_codes_are_raised_and_stable(self):
        entries = registry()
        for code in GATE_CODES:
            self.assertEqual(entries[code][0], "stable", code)


if __name__ == "__main__":
    unittest.main()
