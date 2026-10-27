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

    def test_run_reads_host_lifetime_receipts_and_checks(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory, "verification.json")
            path.write_text(json.dumps({"completed": True, "checks": ["inspect", "reopen"], "hostExits": [
                {"hostExitCode": 0, "toolCalls": [{"name": "vci_prepare", "isError": False}]},
                {"hostExitCode": 0, "toolCalls": [{"name": "vci_commit", "isError": True}]}]}), encoding="utf-8")
            summary = run(path)
        self.assertEqual(["inspect", "reopen"], summary["steps"])
        self.assertEqual((["vci_prepare"], ["vci_commit"]), (summary["toolsPassed"], summary["toolsRejected"]))

    def test_run_accepts_passed_status_and_string_steps(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory, "result.json")
            path.write_text(json.dumps({"status": "passed", "steps": ["tool refusal kept"], "hostExits": [
                {"toolCalls": [{"name": "trace_analyze", "isError": False}]}]}), encoding="utf-8")
            summary = run(path)
            path.write_text(json.dumps({"status": "failed", "steps": []}), encoding="utf-8")
            failed = run(path)
        self.assertEqual((True, ["tool refusal kept"], ["trace_analyze"]),
                         (summary["completed"], summary["steps"], summary["toolsPassed"]))
        self.assertFalse(failed["completed"])

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
