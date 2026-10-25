import json
import pathlib
import tempfile
import unittest

from curate_evidence import run, tree


class CurateEvidenceTests(unittest.TestCase):
    def test_run_keeps_completion_steps_and_tool_outcomes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory, "report.json")
            path.write_text(json.dumps({"completed": True, "steps": [{"name": "discovery"}, {"tool": "tia_start"}],
                                        "toolCalls": [{"name": "tia_discover", "isError": False},
                                                      {"name": "change_apply", "isError": True}]}), encoding="utf-8")
            summary = run(path)
        self.assertTrue(summary["completed"])
        self.assertEqual(["discovery", "tia_start"], summary["steps"])
        self.assertEqual((["tia_discover"], ["change_apply"]), (summary["toolsPassed"], summary["toolsRejected"]))

    def test_run_reads_tool_outcomes_from_steps_without_tool_calls(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory, "report.json")
            path.write_text(json.dumps({"completed": True, "steps": [
                {"tool": "change_apply", "isError": False}, {"tool": "change_apply", "isError": True},
                {"tool": "tia_start"}]}), encoding="utf-8")
            summary = run(path)
        self.assertEqual((["change_apply"], ["change_apply"]), (summary["toolsPassed"], summary["toolsRejected"]))

    def test_tree_hash_changes_with_any_binary(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / "a.dll").write_bytes(b"a")
            before = tree(root)
            (root / "a.dll").write_bytes(b"b")
            self.assertNotEqual(before["sha256"], tree(root)["sha256"])
            self.assertEqual(1, before["files"])
            with self.assertRaises(ValueError):
                tree(root / "missing-dir-is-empty")


if __name__ == "__main__":
    unittest.main()
