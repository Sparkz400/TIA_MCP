"""Pure guards for remote ownership, immutable transfer and journal reconciliation."""
import base64
import hashlib
import unittest

from qualify_ssh_recovery import checkpoint, fault_source, fetch, journal_identity, owned_status, remote_path, semantic_xml, verify_fixture_absent, transient_journal_read, verify_launcher_shutdown
from qualify_deployment import ToolBlocked


ROOT = r"C:\TiaMcpArtifacts"
PROJECT = ROOT + r"\scratch\TiaMcpScratch_test\TiaMcpScratch_test.ap21"


class ArtifactClient:
    def __init__(self, pieces):
        self.pieces = iter(pieces)
        self.calls = []

    def tool(self, name, arguments):
        self.calls.append((name, arguments))
        return next(self.pieces)


def piece(data, total, offset=0, signature=None, complete=True):
    return {"encoding": "base64", "offset": offset, "data": base64.b64encode(data).decode(), "bytes": total,
            "count": len(data), "nextOffset": offset + len(data), "sha256": signature or hashlib.sha256(data).hexdigest(), "complete": complete}


class RemoteRecoveryGuards(unittest.TestCase):
    def test_launcher_shutdown_requires_clean_exit_except_explicit_interruption(self):
        verify_launcher_shutdown({"exitCode": 0, "forcedLauncherStop": False})
        verify_launcher_shutdown({"exitCode": 1, "forcedLauncherStop": False}, interrupted=True)
        for record, interrupted in (({"exitCode": 1, "forcedLauncherStop": False}, False),
                                    ({"exitCode": 0, "forcedLauncherStop": True}, False),
                                    ({"exitCode": 1, "forcedLauncherStop": True}, True), ({}, False)):
            with self.assertRaises(ValueError):
                verify_launcher_shutdown(record, interrupted)

    def test_only_explicitly_unavailable_journal_reads_are_transient(self):
        self.assertTrue(transient_journal_read(ToolBlocked("artifact_read", {"error": {"code": "ArtifactUnavailable"}})))
        for code in ("ArtifactPathDenied", "ArtifactReadFailed", "EngineeringError"):
            self.assertFalse(transient_journal_read(ToolBlocked("artifact_read", {"error": {"code": code}})))
        self.assertFalse(transient_journal_read(ValueError("ArtifactUnavailable")))

    def test_remote_paths_use_windows_semantics(self):
        self.assertEqual(str(remote_path(ROOT.lower() + r"\changes\one.json", ROOT)).lower(), (ROOT + r"\changes\one.json").lower())

    def test_remote_path_escape_rejected(self):
        for path in (r"C:\Other\file", ROOT, ROOT + r"\..\outside", r"relative\file", r"D:\TiaMcpArtifacts\file"):
            with self.assertRaises(ValueError):
                remote_path(path, ROOT)

    def test_drive_root_is_not_an_artifact_scope(self):
        with self.assertRaises(ValueError):
            remote_path(r"C:\one.txt", "C:\\")

    def test_owned_remote_identity_is_exact(self):
        status = {"attached": True, "processId": 123, "ownsPortalProcess": True, "projects": [{"path": PROJECT}]}
        owned_status(status, 123, PROJECT, True)
        for pid, project in ((124, PROJECT), (123, ROOT + r"\other.ap21")):
            with self.assertRaises(ValueError):
                owned_status(status, pid, project)

    def test_new_remote_session_must_be_owned_and_empty(self):
        status = {"attached": True, "processId": 123, "ownsPortalProcess": False, "projects": []}
        with self.assertRaises(ValueError):
            owned_status(status, 123, owner=True)
        status["ownsPortalProcess"] = True
        owned_status(status, 123, owner=True)
        status["projects"] = [{"path": PROJECT}]
        with self.assertRaises(ValueError):
            owned_status(status, 123, owner=True)

    def test_transfer_verifies_final_hash_and_expected_backup(self):
        data = b"immutable checkpoint"
        signature = hashlib.sha256(data).hexdigest()
        client = ArtifactClient([piece(data[:4], len(data), signature=signature, complete=False), piece(data[4:], len(data), 4, signature)])
        actual, metadata = fetch(client, ROOT + r"\before.zap21", ROOT, signature)
        self.assertEqual(data, actual)
        self.assertEqual(signature, metadata["sha256"])
        self.assertTrue(all(name == "artifact_read" for name, _ in client.calls))

    def test_changed_artifact_between_chunks_rejected(self):
        client = ArtifactClient([piece(b"a", 2, signature="a" * 64, complete=False), piece(b"b", 2, 1, "b" * 64)])
        with self.assertRaisesRegex(ValueError, "changed between read chunks"):
            fetch(client, ROOT + r"\journal.json", ROOT)

    def test_transfer_size_and_offsets_are_bounded(self):
        for record in (piece(b"a", 500), piece(b"a", 1, 5)):
            with self.assertRaises(ValueError):
                fetch(ArtifactClient([record]), ROOT + r"\file", ROOT, maximum=100)

    def test_wrong_expected_backup_hash_rejected(self):
        with self.assertRaisesRegex(ValueError, "immutable checkpoint"):
            fetch(ArtifactClient([piece(b"a", 1)]), ROOT + r"\file", ROOT, "0" * 64)

    def test_journal_cannot_reconcile_another_mutation(self):
        journal = {"changeId": "one", "projectPath": PROJECT, "idempotencyKey": "key"}
        journal_identity(journal, "one", PROJECT, "key")
        for change, project, key in (("two", PROJECT, "key"), ("one", PROJECT + "different", "key"), ("one", PROJECT, "other")):
            with self.assertRaises(ValueError):
                journal_identity(journal, change, project, key)

    def test_checkpoint_requires_durable_creation_stage(self):
        journal = {"recoveryArchive": ROOT + r"\before.zap21", "recoveryArchiveSha256": "a" * 64, "stages": []}
        with self.assertRaises(ValueError):
            checkpoint(journal, ROOT)
        journal["stages"] = ["recovery_archive_created"]
        self.assertEqual((ROOT + r"\before.zap21", "a" * 64), checkpoint(journal, ROOT))

    def test_fault_source_is_bounded_and_identified(self):
        names, source = fault_source("abcdef12", 100)
        self.assertEqual(100, len(set(names)))
        self.assertEqual(100, source.count('FUNCTION "'))
        for token, count in (("../bad", 10), ("abcdef12", 101), ("abcdef12", 0)):
            with self.assertRaises(ValueError):
                fault_source(token, count)

    def test_xml_metadata_ignored_but_program_change_preserved(self):
        first = b'<Document><DocumentInfo Created="1"/><Code Value="a"/></Document>'
        self.assertEqual(semantic_xml(first), semantic_xml(first.replace(b'Created="1"', b'Created="2"')))
        self.assertNotEqual(semantic_xml(first), semantic_xml(first.replace(b'Value="a"', b'Value="b"')))

    def inventory(self):
        return {"project": {"path": PROJECT}, "blocks": [{"name": "TiaMcp_IsEnabled"}], "warnings": [],
                "completeness": {"complete": False, "scopeComplete": True, "truncated": False,
                                 "scope": "Device items, configured address ranges and exposed program blocks including units/system groups; no online data."}}

    def test_block_absence_accepts_complete_scope_without_claiming_whole_project(self):
        inventory = self.inventory()
        result = verify_fixture_absent(inventory, ["TiaMcp_SshFault_one"], PROJECT)
        self.assertTrue(result["scopeComplete"])
        self.assertFalse(result["wholeProjectComplete"])
        self.assertFalse(inventory["completeness"]["complete"])

    def test_block_absence_rejects_partial_or_wrong_coverage(self):
        for key, value in (("scopeComplete", False), ("truncated", True), ("scope", "Hardware only")):
            inventory = self.inventory()
            inventory["completeness"][key] = value
            inventory["completeness"]["complete"] = True
            with self.assertRaises(ValueError):
                verify_fixture_absent(inventory, ["TiaMcp_SshFault_one"], PROJECT)
        inventory = self.inventory()
        inventory["warnings"] = ["One software scope was unavailable"]
        with self.assertRaises(ValueError):
            verify_fixture_absent(inventory, ["TiaMcp_SshFault_one"], PROJECT)

    def test_block_absence_rejects_wrong_project_missing_baseline_or_present_fixture(self):
        for blocks, project in (([{"name": "TiaMcp_IsEnabled"}], PROJECT + "other"),
                                ([], PROJECT),
                                ([{"name": "TiaMcp_IsEnabled"}, {"name": "TiaMcp_SshFault_one"}], PROJECT)):
            inventory = self.inventory()
            inventory["blocks"] = blocks
            with self.assertRaises(ValueError):
                verify_fixture_absent(inventory, ["TiaMcp_SshFault_one"], project)


if __name__ == "__main__":
    unittest.main()
