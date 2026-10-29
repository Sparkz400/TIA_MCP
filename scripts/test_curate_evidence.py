import json
import pathlib
import tempfile
import unittest

from audit_evidence_store import audit, audit_uncurated
from curate_evidence import SCHEMA, retain, run, stored_path, tree


class CurateEvidenceTests(unittest.TestCase):
    def test_retained_reports_are_audited_by_their_summary_hash(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            report = root / "qualification.json"
            report.write_text(json.dumps({"completed": True, "steps": []}), encoding="utf-8")
            summary = {"schema": SCHEMA, "evidenceId": "local.x", "runs": [run(report)]}
            (root / "evidence").mkdir()
            (root / "evidence" / "local.x.json").write_text(json.dumps(summary), encoding="utf-8")
            (root / "evidence" / "raw-reports-lost.json").write_text(json.dumps({"runs": []}), encoding="utf-8")
            store = root / "store"
            retain(summary, [report], store)
            self.assertEqual(["local.x/qualification.json"], audit(root / "evidence", store, set())["retained"])
            stored = stored_path(store, "local.x", summary["runs"][0])
            stored.write_text("{}", encoding="utf-8")
            self.assertEqual(["local.x/qualification.json"], audit(root / "evidence", store, set())["mismatched"])
            stored.unlink()
            self.assertEqual(["local.x/qualification.json"], audit(root / "evidence", store, set())["missing"])
            lost = {("local.x", summary["runs"][0]["sha256"])}
            self.assertEqual(["local.x/qualification.json"], audit(root / "evidence", store, lost)["lost"])
            report.write_text("changed after hashing", encoding="utf-8")
            with self.assertRaises(ValueError):
                retain(summary, [report], store)

    def test_uncurated_catalog_artifacts_must_be_in_place_or_listed_lost(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / "artifacts").mkdir()
            (root / "artifacts/kept.json").write_text("{}", encoding="utf-8")
            catalog = {"evidence": [{"id": "old", "artifacts": ["artifacts/kept.json", "artifacts/gone.json",
                                                                 "artifacts/absent.json", "docs/record.md"]}]}
            result = audit_uncurated(catalog, root, {("old", "artifacts/gone.json")})
        self.assertEqual((["old/artifacts/kept.json"], ["old/artifacts/gone.json"], ["old/artifacts/absent.json"]),
                         (result["inPlace"], result["lost"], result["missing"]))

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

    def test_run_reads_hmi_bridge_gates_and_launcher_calls(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory, "hmi-bridge.json")
            path.write_text(json.dumps({"completed": True, "profiles": [{"profile": "unified-pc", "gates": {"retrieval": {"status": "passed"}, "compile": {"status": "failed"}}}],
                                        "launcherShutdown": {"toolCalls": [{"name": "hmi_inspect", "isError": False}, {"name": "project_retrieve", "isError": True}]}}), encoding="utf-8")
            summary = run(path)
        self.assertEqual(["unified-pc: retrieval=passed", "unified-pc: compile=failed"], summary["steps"])
        self.assertEqual((["hmi_inspect"], ["project_retrieve"]), (summary["toolsPassed"], summary["toolsRejected"]))

    def test_run_reads_shutdown_receipts(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory, "verification.json")
            path.write_text(json.dumps({"completed": True, "checks": ["review"], "shutdowns": [
                {"hostExitCode": 0, "toolCalls": [{"name": "connection_review", "isError": False}]}]}), encoding="utf-8")
            summary = run(path)
        self.assertEqual(["connection_review"], summary["toolsPassed"])

    def test_run_accepts_passed_status_and_string_steps(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory, "result.json")
            path.write_text(json.dumps({"status": "passed", "steps": ["tool refusal kept"], "cleanups": [
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
