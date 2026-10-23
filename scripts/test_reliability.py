"""Pure guard tests for reliability qualification; no subprocess or Siemens calls."""
import json
import hashlib
import pathlib
import tempfile
import unittest
from unittest.mock import patch

import qualify_reliability as reliability


class ReliabilityGuards(unittest.TestCase):
    def events(self):
        return [{"kind": "started", "token": "owner"},
                {"kind": "request", "operation": "change_apply", "id": "write1", "token": "owner"},
                {"kind": "committed", "operation": "change_apply", "id": "write1", "token": "owner"}]

    def test_complete_lines_only(self):
        with tempfile.TemporaryDirectory(dir=reliability.ROOT / "artifacts") as temporary:
            path = pathlib.Path(temporary) / "events.jsonl"
            self.assertEqual([], reliability.read_events(path))
            path.write_text('{"kind":"started"}\n{"kind":', encoding="utf-8")
            self.assertEqual([{"kind": "started"}], reliability.read_events(path))

    def test_single_write_is_accepted(self):
        reliability.verify_no_replay(self.events(), "owner")

    def test_repeated_write_rejected(self):
        events = self.events() + [{"kind": "request", "operation": "change_apply", "id": "write2", "token": "owner"}]
        with self.assertRaisesRegex(AssertionError, "replayed"):
            reliability.verify_no_replay(events, "owner")

    def test_hidden_restart_rejected(self):
        with self.assertRaisesRegex(AssertionError, "restart"):
            reliability.verify_no_replay(self.events() + [{"kind": "started", "token": "owner"}], "owner")

    def test_foreign_token_rejected(self):
        with self.assertRaisesRegex(AssertionError, "Foreign"):
            reliability.verify_no_replay(self.events(), "different-owner")

    def test_duplicate_request_identity_rejected(self):
        events = self.events() + [{"kind": "request", "operation": "change_status", "id": "write1", "token": "owner"}]
        with self.assertRaisesRegex(AssertionError, "Duplicate"):
            reliability.verify_no_replay(events, "owner")

    def test_error_flag_and_code_both_required(self):
        result = {"isError": True, "structuredContent": {"error": {"code": "BridgeFaulted"}}}
        reliability.require_error(result, "BridgeFaulted")
        with self.assertRaises(AssertionError):
            reliability.require_error(result, "WorkerDisconnected")
        result["isError"] = False
        with self.assertRaises(AssertionError):
            reliability.require_error(result, "BridgeFaulted")

    def test_error_text_fallback(self):
        result = {"isError": True, "content": [{"type": "text", "text": json.dumps({"error": {"code": "WorkerExited"}})}]}
        self.assertEqual("WorkerExited", reliability.require_error(result, "WorkerExited")["error"]["code"])

    def test_semantic_xml_keeps_program_changes(self):
        first = '<Document><DocumentInfo><Created>1</Created></DocumentInfo><Code Value="a"/></Document>'
        timestamp = first.replace('>1<', '>2<')
        changed = first.replace('Value="a"', 'Value="b"')
        self.assertEqual(reliability.semantic_xml(first), reliability.semantic_xml(timestamp))
        self.assertNotEqual(reliability.semantic_xml(first), reliability.semantic_xml(changed))

    def test_no_process_termination_on_other_platform(self):
        with patch.object(reliability.os, "name", "posix"):
            with self.assertRaisesRegex(RuntimeError, "requires Windows"):
                reliability.terminate_owned_test_worker(1, pathlib.Path("worker.exe"))

    def checkpoint(self, temporary):
        directory = pathlib.Path(temporary) / "changes" / ("change-" + "a" * 32)
        directory.mkdir(parents=True)
        archive = directory / "before.zap21"
        archive.write_bytes(b"fixture archive")
        journal = {"changeId": "a" * 32, "projectPath": str(pathlib.Path(temporary) / "TiaMcpScratch_test.ap21"),
                   "recoveryArchive": str(archive), "recoveryArchiveSha256": hashlib.sha256(archive.read_bytes()).hexdigest()}
        path = directory / "journal.json"
        path.write_text(json.dumps(journal), encoding="utf-8")
        return path, archive, journal

    def test_recorded_scratch_hash_is_validated(self):
        with tempfile.TemporaryDirectory(dir=reliability.ROOT / "artifacts") as temporary:
            path, archive, journal = self.checkpoint(temporary)
            result, actual, artifacts = reliability.validated_checkpoint(path)
            self.assertEqual(journal, result)
            self.assertEqual(archive.resolve(), actual)
            self.assertEqual(pathlib.Path(temporary).resolve(), artifacts)

    def test_modified_archive_rejected(self):
        with tempfile.TemporaryDirectory(dir=reliability.ROOT / "artifacts") as temporary:
            path, archive, _ = self.checkpoint(temporary)
            archive.write_bytes(b"modified")
            with self.assertRaisesRegex(AssertionError, "hash mismatch"):
                reliability.validated_checkpoint(path)

    def test_customer_project_journal_rejected(self):
        with tempfile.TemporaryDirectory(dir=reliability.ROOT / "artifacts") as temporary:
            path, _, journal = self.checkpoint(temporary)
            journal["projectPath"] = str(pathlib.Path(temporary) / "Customer.ap21")
            path.write_text(json.dumps(journal), encoding="utf-8")
            with self.assertRaisesRegex(AssertionError, "scratch projects only"):
                reliability.validated_checkpoint(path)

    def test_journal_identity_mismatch_rejected(self):
        with tempfile.TemporaryDirectory(dir=reliability.ROOT / "artifacts") as temporary:
            path, _, journal = self.checkpoint(temporary)
            journal["changeId"] = "b" * 32
            path.write_text(json.dumps(journal), encoding="utf-8")
            with self.assertRaisesRegex(AssertionError, "identity"):
                reliability.validated_checkpoint(path)

    def test_non_archive_checkpoint_rejected(self):
        with tempfile.TemporaryDirectory(dir=reliability.ROOT / "artifacts") as temporary:
            path, _, journal = self.checkpoint(temporary)
            journal["recoveryArchive"] = str(path)
            path.write_text(json.dumps(journal), encoding="utf-8")
            with self.assertRaisesRegex(AssertionError, "checkpoint archive"):
                reliability.validated_checkpoint(path)


if __name__ == "__main__":
    unittest.main()
