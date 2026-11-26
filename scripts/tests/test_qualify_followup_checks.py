"""Offline checks of qualify_followup_checks.py: its pure helpers, the raw-frame client and the stage bookkeeping; nothing
here starts TIA, PLCSIM or a Host."""
import json
import os
import queue
import tempfile
import time
import unittest
from pathlib import Path

import qualify_followup_checks as q


class Helpers(unittest.TestCase):
    def test_stage_plan_skips_by_selection_and_flags(self):
        plan = q.stage_plan(None, {"running_instance": False, "seeded_report": False})
        self.assertEqual(list(plan), list(q.STAGES))
        self.assertIn("--running-instance", plan["sim_write_signals_both_spellings"])
        for name in q.SEEDED_STAGES:
            self.assertIn("--seeded-report", plan[name])
        self.assertIsNone(plan["tia_capabilities"])
        plan = q.stage_plan({"queue_timeout_during_change_apply"}, {"running_instance": True, "seeded_report": True})
        self.assertEqual([n for n, reason in plan.items() if reason is None], ["queue_timeout_during_change_apply"])
        self.assertEqual(plan["tia_capabilities"], "not selected (--stages)")

    def test_summarize_needs_every_run_stage_passed(self):
        stages = [{"name": "a", "status": "passed"}, {"name": "b", "status": "skipped", "reason": "flag"}]
        self.assertTrue(q.summarize(stages)["completed"])
        self.assertEqual(q.summarize(stages)["skipped"], [{"name": "b", "reason": "flag"}])
        self.assertFalse(q.summarize(stages + [{"name": "c", "status": "failed"}])["completed"])
        self.assertFalse(q.summarize([{"name": "b", "status": "skipped", "reason": "x"}])["completed"], "nothing ran")
        self.assertFalse(q.summarize([])["completed"])

    def test_repeated_member_object_keeps_both_members(self):
        text = q.repeated_member_object([("symbol", "A"), ("symbol", "B"), ("type", "bool"), ("value", True)])
        pairs = json.loads(text, object_pairs_hook=lambda items: items)
        self.assertEqual(pairs, [("symbol", "A"), ("symbol", "B"), ("type", "bool"), ("value", True)])
        self.assertEqual(json.loads(text)["symbol"], "B")

    def test_tools_call_frame_carries_raw_arguments(self):
        arguments = '{"kind":"k","payload":' + q.repeated_member_object([("enabled", True), ("enabled", False)]) + "}"
        frame = q.tools_call_frame(7, "change_prepare", arguments)
        self.assertNotIn("\n", frame)
        message = json.loads(frame)
        self.assertEqual((message["id"], message["method"], message["params"]["name"]), (7, "tools/call", "change_prepare"))
        self.assertEqual(frame.count('"enabled"'), 2)

    def test_spellings(self):
        self.assertEqual(q.spellings("TiaMcp_TestIO.Enable"), ("TiaMcp_TestIO.Enable", '"TiaMcp_TestIO".Enable'))
        self.assertEqual(q.spellings('"TiaMcp_TestIO".Enable'), ("TiaMcp_TestIO.Enable", '"TiaMcp_TestIO".Enable'))
        with self.assertRaises(AssertionError):
            q.spellings("NoMember")

    def test_product_version_reads_the_props(self):
        self.assertEqual(q.product_version("<Project><PropertyGroup>\n  <Version>0.9.0</Version>\n</PropertyGroup></Project>"), "0.9.0")
        self.assertIsNone(q.product_version("<Project/>"))
        props = Path(q.ROOT / "Directory.Build.props").read_text(encoding="utf-8")
        self.assertRegex(q.product_version(props), r"^\d+\.\d+\.\d+")

    def test_catalog_cpu_takes_the_newest_exact_type(self):
        entries = [{"typeName": "CPU 1518-4 PN/DP", "version": "V2.9", "typeIdentifier": "OrderNumber:X/V2.9"},
                   {"typeName": "CPU 1518-4 PN/DP", "version": "V3.1", "typeIdentifier": "OrderNumber:X/V3.1"},
                   {"typeName": "CPU 1518F-4 PN/DP", "version": "V4.0", "typeIdentifier": "OrderNumber:F/V4.0"},
                   {"typeName": "CPU 1518-4 PN/DP", "version": "V3.2"}]
        self.assertEqual(q.catalog_cpu(entries, q.CPU_1518)["version"], "V3.1")
        self.assertIsNone(q.catalog_cpu(entries[2:3], q.CPU_1518))
        self.assertIsNone(q.catalog_cpu(None, q.CPU_1518))

    def test_sessions_and_portals(self):
        processes = [{"processId": 10, "attachedSessions": [{"processId": 77, "processRunning": False}]}, {"processId": 11}]
        sessions = q.portal_sessions(processes, 10)
        self.assertEqual(q.session_of(sessions, 77)["processRunning"], False)
        self.assertIsNone(q.session_of(sessions, 78))
        self.assertEqual(q.portal_sessions(processes, 11), [])
        self.assertIsNone(q.portal_sessions(processes, 12))
        self.assertEqual(q.portal_row([{"processId": 3, "thisWorker": True}], 3), {"processId": 3, "thisWorker": True})
        self.assertIsNone(q.portal_row(None, 3))

    def test_same_path(self):
        self.assertTrue(q.same_path(r"C:\A\b.ap21", "C:/A/b.ap21") if os.name == "nt" else q.same_path("/a/b", "/a/./b"))
        self.assertFalse(q.same_path(None, "x"))

    def test_change_journal_stays_in_the_changes_folder(self):
        with tempfile.TemporaryDirectory() as folder:
            change = "0123456789abcdef0123456789abcdef"
            path = q.change_journal(folder, change)
            self.assertEqual(path, Path(folder) / ("change-" + change) / "journal.json")
            for bad in ("../x", "0123", "0123456789ABCDEF0123456789ABCDEF", None):
                with self.assertRaises(AssertionError):
                    q.change_journal(folder, bad)

    def test_pruned_ids_and_verdict(self):
        log = json.dumps({"changeId": "old"}) + "\n\n" + json.dumps({"changeId": "other"}) + "\n"
        self.assertEqual(q.pruned_ids(log), ["old", "other"])
        self.assertEqual(q.pruned_ids(""), [])
        self.assertEqual(q.sweep_verdict(["old"], ["young", "held"], ["young", "held"], ["old"]), [])
        problems = q.sweep_verdict(["old"], ["young", "held"], ["old", "young"], ["held"])
        self.assertEqual(len(problems), 4, problems)  # old kept, old not logged, held removed, held logged unexpectedly.

    def test_foreign_images(self):
        allowed = [r"C:\py\python.exe", r"C:\b\host\TiaMcp.Host.exe", None]
        self.assertEqual(q.foreign_images([r"C:\py\python.exe", r"c:\b\host\tiamcp.host.exe"] if os.name == "nt" else [r"C:\py\python.exe"], allowed), [])
        self.assertEqual(q.foreign_images([r"C:\Siemens\Portal.exe", None], allowed), [r"C:\Siemens\Portal.exe", None])

    def test_seeded_archive(self):
        row = {"profile": "unified-pc", "archiveRetrievalVerified": True, "archive": {"path": "x.zap21", "sha256": "ab"}}
        self.assertEqual(q.seeded_archive({"completed": True, "profiles": [row]}, "unified-pc"), row["archive"])
        self.assertIsNone(q.seeded_archive({"completed": False, "profiles": [row]}, "unified-pc"))
        self.assertIsNone(q.seeded_archive({"completed": True, "profiles": [{**row, "archiveRetrievalVerified": False}]}, "unified-pc"))
        self.assertIsNone(q.seeded_archive({"completed": True, "profiles": [row, row]}, "unified-pc"))
        self.assertIsNone(q.seeded_archive({"completed": True, "profiles": [row]}, "classic-basic"))

    def test_key_order_and_duration(self):
        self.assertTrue(q.key_order({"section": 1, "page": 2, "items": 3}, "page", "items"))
        self.assertFalse(q.key_order({"items": 3, "page": 2}, "page", "items"))
        self.assertFalse(q.key_order({"page": 2}, "page", "items"))
        self.assertEqual(q.duration_ms({"_meta": {q.DURATION_META: 5012}}), 5012)
        self.assertIsNone(q.duration_ms({}))

    def test_error_helpers(self):
        value = {"error": {"code": "X", "details": {"ticketState": "spent"}}}
        self.assertEqual(q.error_of(value)["code"], "X")
        self.assertEqual(q.details_of(value)["ticketState"], "spent")
        self.assertEqual(q.details_of({}), {})
        self.assertEqual(q.error_of(None), {})


class FakeStdin:
    def __init__(self):
        self.frames = []

    def write(self, text):
        self.frames.append(text)

    def flush(self):
        pass


class FakeClient:
    def __init__(self):
        self.sequence = 4
        self.lines = queue.Queue()
        self.errors = []
        self.tool_calls = []
        self.checked = []
        self.process = type("P", (), {"stdin": FakeStdin()})()

    def check_output(self, name, result, arguments=None):
        self.checked.append(name)


class RawCallsTests(unittest.TestCase):
    def test_overlapping_replies_are_matched_by_id(self):
        client = FakeClient()
        raw = q.RawCalls(client)
        first = raw.send("change_apply", "{}")
        second = raw.send("project_inventory", '{"includeBlocks":false}')
        self.assertEqual((first, second), (5, 6))
        self.assertEqual(json.loads(client.process.stdin.frames[1])["params"]["arguments"], {"includeBlocks": False})
        client.lines.put(json.dumps({"jsonrpc": "2.0", "method": "notifications/message", "params": {}}) + "\n")
        client.lines.put(json.dumps({"jsonrpc": "2.0", "id": 6, "result": {"isError": True, "structuredContent": {"error": {"code": "QueueTimeout"}}}}))
        client.lines.put(json.dumps({"jsonrpc": "2.0", "id": 5, "result": {"structuredContent": {"state": "applied"}}}))
        replies = raw.collect([first, second], {first: "change_apply", second: "project_inventory"}, 5)
        self.assertEqual(replies[second]["message"]["result"]["structuredContent"]["error"]["code"], "QueueTimeout")
        self.assertLess(replies[second]["receivedMonotonic"], replies[first]["receivedMonotonic"] + 1)
        self.assertEqual(client.tool_calls, [{"name": "change_apply", "isError": False}, {"name": "project_inventory", "isError": True}])
        self.assertEqual(sorted(client.checked), ["change_apply", "project_inventory"])

    def test_rpc_error_and_host_exit(self):
        client = FakeClient()
        raw = q.RawCalls(client)
        sequence = raw.send("sim_write", "{}")
        client.lines.put(json.dumps({"jsonrpc": "2.0", "id": sequence, "error": {"code": -32602, "message": "bad"}}))
        reply = raw.collect([sequence], {sequence: "sim_write"}, 5)[sequence]["message"]
        self.assertEqual(reply["error"]["code"], -32602)
        self.assertEqual(client.tool_calls, [])
        sequence = raw.send("sim_write", "{}")
        client.lines.put(None)
        with self.assertRaises(q.HostExited):
            raw.collect([sequence], {sequence: "sim_write"}, 5)

    def test_timeout(self):
        raw = q.RawCalls(FakeClient())
        sequence = raw.send("x", "{}")
        started = time.monotonic()
        with self.assertRaises(TimeoutError):
            raw.collect([sequence], {sequence: "x"}, 0.2)
        self.assertLess(time.monotonic() - started, 5)


class StageBookkeeping(unittest.TestCase):
    def run_view(self, folder):
        run = q.Run.__new__(q.Run)
        run.root = Path(folder)
        run.report = {"stages": [], "skipped": [], "steps": []}
        run.current = None
        return run

    def test_run_stage_records_pass_fail_and_skip(self):
        with tempfile.TemporaryDirectory() as folder:
            run = self.run_view(folder)
            run.run_stage("ok", lambda: {"value": 1})
            run.run_stage("broken", lambda: q.require(False, "precondition failed"))

            def environment():
                raise q.StageSkipped("no CPU 1518 in the catalog")
            run.run_stage("absent", environment)
            run.run_stage("deselected", lambda: None, "not selected (--stages)")
            stages = {s["name"]: s for s in run.report["stages"]}
            self.assertEqual(stages["ok"]["status"], "passed")
            self.assertEqual(stages["ok"]["observations"], {"value": 1})
            self.assertEqual(stages["broken"]["status"], "failed")
            self.assertIn("precondition failed", stages["broken"]["error"])
            self.assertEqual((stages["absent"]["status"], stages["absent"]["reason"]), ("skipped", "no CPU 1518 in the catalog"))
            self.assertEqual(stages["deselected"]["status"], "skipped")
            self.assertEqual([s["name"] for s in run.report["skipped"]], ["absent", "deselected"])
            self.assertFalse(q.summarize(run.report["stages"])["completed"])
            saved = json.loads((Path(folder) / "report.json").read_text(encoding="utf-8"))
            self.assertEqual(len(saved["stages"]), 4)

    def test_an_exited_host_is_replaced(self):
        class Process:
            def __init__(self, code):
                self.code = code

            def poll(self):
                return self.code

        class Host:
            made = []

            def __init__(self, executable, timeout, arguments):
                self.arguments, self.exited, self.errors, self.process, self.closed = arguments, False, [], Process(None), False
                Host.made.append(self)

            def request(self, method, params=None):
                return {}

            def notify(self, method, params=None):
                pass

            def close(self):
                self.closed = True
                return {"hostExitCode": 1}

        with tempfile.TemporaryDirectory() as folder:
            run = self.run_view(folder)
            run.clients, run.host, run.worker, run.sim_worker = {}, "host.exe", "worker.exe", "sim.exe"
            run.tool_calls, run.scratch, run.seeded = [], {"path": "x"}, None
            original = q.Client
            q.Client = Host
            try:
                first = run.client("a")
                self.assertIs(run.client("a"), first, "a live Host is reused")
                first.process = Process(3)
                second = run.client("a")
                self.assertIsNot(second, first)
                self.assertTrue(first.closed)
                self.assertIsNone(run.scratch, "the lost Host's scratch is forgotten")
                second.exited = True
                self.assertIsNot(run.client("a"), second)
                self.assertIs(second.tool_calls, run.tool_calls)
            finally:
                q.Client = original

    def test_main_refuses_artifacts_outside_the_repository(self):
        # Beside the repository, never created: CI's TEMP lies inside the repository's artifacts, so a temp folder would not do.
        outside = q.ROOT.parent / ("outside-" + os.urandom(4).hex())
        with self.assertRaises(SystemExit):
            q.main(["--artifacts", str(outside)])
        self.assertFalse(outside.exists())

    def test_main_refuses_trust_without_running_instance(self):
        with self.assertRaises(SystemExit):
            q.main(["--artifacts", str(q.ROOT / "artifacts" / ("never-created-" + os.urandom(4).hex())), "--trust-owned-empty-cpu-certificate"])


if __name__ == "__main__":
    unittest.main()
