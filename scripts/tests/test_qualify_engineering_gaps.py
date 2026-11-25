"""Pure checks of the --hmi-fixture mode of qualify_engineering_gaps.py; no Host or TIA is started."""
import copy
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import library_native_harness as harness
import qualify_engineering_gaps as gaps


def compiled(**target):
    row = {"target": "/projects/P/devices/HmiDevice_A/items/HMI_RT_1/hmi/HMI_RT_1", "state": "Success", "errorCount": 0,
           "warningCount": 0, "messages": [{"description": "Compiling finished (errors: 0; warnings: 0)", "state": "Success"}],
           "messagesTruncated": False, **target}
    return {"outcome": "compiled", "stages": ["compile_only_requested", "compile_finished", "changed_project_saved"],
            "compilation": {"errorCount": 0, "warningCount": 0, "targets": [row]}, "changeId": "c", "previewHash": "p"}


def row(path, session="s1", copy_id="c1", **properties):
    identity = {"schemaVersion": 2, "projectPath": "D:/a/" + session + ".ap21", "projectCopyId": copy_id, "sessionGeneration": session,
                "hmiPath": "/projects/P/hmi", "objectPath": path, "exportSha256": session * 32}
    return {"identity": identity, "kind": "hmiScreenItem", "name": path, "properties": properties,
            "source": {"kind": "nativeExport", "exportSha256": session * 32, "exportLocalId": "7"}}


def snapshot(rows, coverage="complete"):
    return {"screens": {"coverage": coverage, "rows": {r["identity"]["objectPath"]: gaps.stable(r) for r in rows}}}


class CleanCompileTests(unittest.TestCase):
    def test_clean_compile_passes(self):
        self.assertEqual(gaps.clean(compiled())["outcome"], "compiled")

    def test_every_unclean_outcome_refuses(self):
        cases = {"warning": compiled(warningCount=1), "error message": compiled(messages=[{"state": "Error", "description": "x"}]),
                 "warning message": compiled(messages=[{"state": "Warning", "description": "x"}]),
                 "truncated diagnostics": compiled(messagesTruncated=True), "missing truncation flag": compiled(messagesTruncated=None)}
        unsaved = compiled(); unsaved["stages"].remove("changed_project_saved"); cases["not saved"] = unsaved
        total = compiled(); total["compilation"]["warningCount"] = 2; cases["total warnings"] = total
        empty = compiled(); empty["compilation"]["targets"] = []; cases["no targets"] = empty
        cases["compile errors"] = {"outcome": "compileErrorsReported", "errors": ["A start screen has not been configured."]}
        for name, result in cases.items():
            with self.subTest(name), self.assertRaisesRegex(AssertionError, "did not compile cleanly"):
                gaps.clean(result)


class HmiEqualityTests(unittest.TestCase):
    def test_session_bound_fields_are_ignored(self):
        before = snapshot([row("/projects/P/hmi/screens/A/exportItems/7", Left="10")])
        after = snapshot([row("/projects/P/hmi/screens/A/exportItems/7", session="s2", copy_id="c2", Left="10")])
        self.assertEqual(gaps.differences(before, after), [])

    def test_content_and_coverage_changes_are_reported(self):
        path = "/projects/P/hmi/screens/A/exportItems/7"
        before = snapshot([row(path, Left="10")])
        self.assertEqual(gaps.differences(before, snapshot([row(path, Left="11")])), ["screens " + path])
        self.assertEqual(gaps.differences(before, snapshot([])), ["screens " + path])
        self.assertEqual(gaps.differences(before, snapshot([row(path, Left="10")], "unavailable")), ["screens"])
        self.assertEqual(gaps.differences(before, {}), ["screens"])

    @staticmethod
    def inspector(requests, project="D:/a/s1.ap21", filled=("screens", "model"), status="complete"):
        def tool(name, arguments):
            request = arguments["request"]
            requests.append((request["section"], request.get("screenPath")))
            items = [row("/projects/P/hmi/" + request["section"] + "/A")] if request["section"] in filled else []
            return {"projectPath": project, "coverage": {"status": status}, "items": items, "metadataRevision": "0" * 64, "nextOffset": None}
        return SimpleNamespace(client=SimpleNamespace(tool=tool))

    def test_snapshot_pages_every_section_and_each_screens_items(self):
        requests = []
        h = self.inspector(requests)
        classic = gaps.hmi_snapshot(h, {"family": "classic", "hmiPath": "/projects/P/hmi"}, "D:/a/s1.ap21")
        self.assertEqual(list(classic), [*gaps.SECTIONS[:3], "items /projects/P/hmi/screens/A", *gaps.SECTIONS[3:]])
        self.assertNotIn("projectPath", classic["screens"]["rows"]["/projects/P/hmi/screens/A"]["identity"])
        requests.clear()
        unified = gaps.hmi_snapshot(h, {"family": "unified", "hmiPath": "/projects/P/hmi"}, "D:/A/S1.ap21")
        self.assertEqual(requests[-1], ("model", None))
        self.assertIn("model", unified)

    def test_snapshot_must_read_the_named_project(self):
        h = self.inspector([])
        with self.assertRaisesRegex(AssertionError, "did not read"):
            gaps.hmi_snapshot(h, {"family": "classic", "hmiPath": "/projects/P/hmi"}, "D:/a/recovered.ap21")
        with self.assertRaisesRegex(AssertionError, "did not read"):
            gaps.hmi_snapshot(self.inspector([], project=None), {"family": "classic", "hmiPath": "/projects/P/hmi"}, "D:/a/s1.ap21")

    def test_empty_screens_or_model_refuse(self):
        with self.assertRaisesRegex(AssertionError, "screens of /projects/P/hmi is empty"):
            gaps.hmi_snapshot(self.inspector([], filled=()), {"family": "classic", "hmiPath": "/projects/P/hmi"}, "D:/a/s1.ap21")
        with self.assertRaisesRegex(AssertionError, "model of /projects/P/hmi is empty"):
            gaps.hmi_snapshot(self.inspector([], filled=("screens",)), {"family": "unified", "hmiPath": "/projects/P/hmi"}, "D:/a/s1.ap21")
        classic = gaps.hmi_snapshot(self.inspector([], filled=("screens",)), {"family": "classic", "hmiPath": "/projects/P/hmi"}, "D:/a/s1.ap21")
        self.assertNotIn("model", classic)

    def test_truncated_inspection_refuses(self):
        with self.assertRaisesRegex(AssertionError, "truncated"):
            gaps.hmi_snapshot(self.inspector([], status="truncated"), {"family": "classic", "hmiPath": "/projects/P/hmi"}, "D:/a/s1.ap21")


class PreparedArchiveTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.root = Path(folder.name).resolve()
        (self.root / "native").mkdir()
        self.archive = self.root / "native" / "configured.zap21"
        self.archive.write_bytes(b"configured archive")
        sha = gaps.hashlib.sha256(b"configured archive").hexdigest()
        self.result = {"completed": True, "nativeResult": {"completed": True, "savedConfiguredArchiveCaptured": True,
                       "profile": "classic-comfort", "archive": {"path": str(self.archive).upper(), "sha256": sha}}}

    def check(self):
        (self.root / "result.json").write_text(gaps.json.dumps(self.result), encoding="utf-8")
        return gaps.prepared_archive(self.archive)

    def test_recorded_archive_is_pinned(self):
        fixture = self.check()
        self.assertEqual(fixture["profile"], "classic-comfort")
        self.assertEqual(fixture["sha256"], self.result["nativeResult"]["archive"]["sha256"])

    def test_changed_archive_refuses(self):
        self.archive.write_bytes(b"edited after preparation")
        with self.assertRaisesRegex(AssertionError, "differs from the SHA-256"):
            self.check()

    def test_incomplete_or_foreign_preparation_refuses(self):
        for change in (lambda r: r.update(completed=False), lambda r: r["nativeResult"].update(savedConfiguredArchiveCaptured=False),
                       lambda r: r["nativeResult"]["archive"].update(path=str(self.root / "other.zap21"))):
            original = copy.deepcopy(self.result)
            change(self.result)
            with self.subTest(self.result), self.assertRaisesRegex(AssertionError, "not the configured archive"):
                self.check()
            self.result = original


class PathAndHostTests(unittest.TestCase):
    def test_retrieve_destination_limit(self):
        self.assertTrue(gaps.retrieve_fits("D:/Entwicklung/TIA_MCP-public/artifacts/hc/eh_12345678"))
        self.assertFalse(gaps.retrieve_fits("D:/Entwicklung/TIA_MCP-public/artifacts/hmi-compile-fixture/eh_12345678"))

    def test_fresh_host_keeps_earlier_tool_calls(self):
        class FakeClient:
            started = []
            def __init__(self, *arguments):
                self.tool_calls, self.errors = [], []
                FakeClient.started.append(self)
            def request(self, method, params=None):
                return {}
            def notify(self, method):
                pass
            def close(self):
                return {"hostExitCode": 0, "toolCalls": self.tool_calls}
        with tempfile.TemporaryDirectory() as folder, patch.object(harness, "Client", FakeClient):
            runner = Path(folder) / "runner.py"; runner.write_text("runner")
            args = SimpleNamespace(run_native=True, artifacts=Path(folder), host=runner, worker=runner, runner=runner, timeout=1)
            h = harness.Harness(args, "eh_", "test", [], [], scratch=False)
            h.connect()
            h.client.tool_calls.append({"name": "tia_start", "isError": False})
            h.connect()
            self.assertEqual(len(FakeClient.started), 2)
            self.assertEqual(h.client.tool_calls, [{"name": "tia_start", "isError": False}])
            self.assertEqual(h.report["closedHosts"], [{"hostExitCode": 0}])


if __name__ == "__main__":
    unittest.main()
