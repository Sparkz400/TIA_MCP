"""Offline stopped-prefix finalizer guards; no native process or project API."""
import copy
import json
import pathlib
import tempfile
import types
import unittest
from unittest import mock

import finalize_typed_capture_prefix as runner


class PrefixTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.patch = mock.patch.object(runner, "ROOT", self.root)
        self.patch.start()
        self.addCleanup(self.patch.stop)
        self.run = self.root / "artifacts" / "old"
        self.project = self.run / "recovery" / "owned" / "owned.ap21"
        self.project.parent.mkdir(parents=True)
        self.project.write_bytes(b"closed saved project")
        (self.project.parent / "companion.info").write_bytes(b"every companion is retained")
        self.xml = b'<Document><!--peer--><?native keep?><Payload ID="7" xmlns:q="urn:kept"> text </Payload></Document>'
        self.expected = self.run / "source-00" / "after" / "manifest.json"
        self.expected.parent.mkdir(parents=True)
        self.write_snapshot(self.expected.parent, self.project, 11)
        self.capture = {"status": "stopped_by_request", "completed": False, "builderUsed": False,
            "runtimeVerified": False, "physicalPlcAccess": False,
            "fixtures": [{"engineeringVerified": True, "unrelatedPreserved": True}], "remainingSources": ["not-admitted"],
            "sessions": [{"processId": 11, "cleanup": {"completed": True, "errors": [], "binaryHashesVerified": True,
                "tiaProcessExit": {"processId": 11, "completed": True, "processExitVerified": True, "handleClosed": True}}}],
            "retrieved": {"project": {"path": str(self.project)}}}
        self.report_path = self.run / "capture.json"
        self.audit_path = self.root / "artifacts" / "audit.json"
        self.args = types.SimpleNamespace(capture_report=self.report_path, prefix_audit=self.audit_path,
            output=self.root / "artifacts" / "new", host=self.root / "host.exe", worker=self.root / "worker.exe", timeout=30)
        self.audit = {"passed": True, "verified": {key: True for key in
            ("nativeImportCompileSave", "exactAddedBlocks", "allPeerXml", "exactOwnedCleanup")},
            "inputs": 1, "remainingInputs": 1, "ownedProject": str(self.project),
            "expectedSnapshot": {"path": str(self.expected), "sha256": runner.hash_file(self.expected)}}
        self.bind()
        self.calls = []

    def bind(self):
        self.report_path.write_text(json.dumps(self.capture), encoding="utf-8")
        self.args.capture_sha256 = runner.hash_file(self.report_path)
        self.audit["sourceReport"] = {"path": str(self.report_path), "sha256": self.args.capture_sha256}
        self.audit_path.write_text(json.dumps(self.audit), encoding="utf-8")
        self.args.audit_sha256 = runner.hash_file(self.audit_path)

    def write_snapshot(self, folder, project, pid, raw=None):
        folder.mkdir(parents=True, exist_ok=True)
        file = folder / "0000.xml"
        file.write_bytes(self.xml if raw is None else raw)
        row = {"name": "Peer", "path": "/plc/Peer"}
        value = {"inventory": {"project": {"path": str(project)}, "processId": pid,
            "completeness": {"scopeComplete": True, "truncated": False}, "blocks": [row], "types": [], "tagTables": []},
            "objects": {"block:/plc/Peer": {**row, "file": str(file), "bytes": file.stat().st_size, "sha256": runner.hash_file(file)}}}
        (folder / "manifest.json").write_text(json.dumps(value), encoding="utf-8")
        return value

    def fake_session(self, host, worker, output, report, persist, **kwargs):
        outer = self
        class Fake:
            known_paths = set()
            def invoke(self, name, arguments=None):
                outer.calls.append(name)
                if name == "tia_start":
                    report["sessions"] = [{"calls": []}]
                    return {"processId": 22}
                if name == "project_open":
                    self.project = pathlib.Path(arguments["path"])
                    outer.assertIn(str(self.project), self.known_paths)
                    outer.assertNotEqual(outer.project, self.project)
                    return {"opened": True, "project": {"path": str(self.project)}}
                raise AssertionError("Unexpected dispatched call: " + name)
            def close(self):
                outer.calls.append("close")
                if getattr(outer, "close_failure", False):
                    raise RuntimeError("injected cleanup failure")
                report["sessions"][0]["cleanup"] = {"completed": True}
        return Fake()

    def fake_snapshot(self, call, folder):
        if getattr(self, "snapshot_failure", False):
            raise RuntimeError("injected readback failure")
        if getattr(self, "alter_original", False):
            self.project.write_bytes(b"external concurrent change")
        raw = self.xml.replace(b'ID="7"', b'ID="8"') if getattr(self, "alter_xml", False) else None
        return self.write_snapshot(folder, call.__self__.project, 22, raw)

    def execute(self):
        with mock.patch.object(runner, "ReadOnlyPrefixSession", side_effect=self.fake_session), \
                mock.patch.object(runner, "snapshot", side_effect=self.fake_snapshot):
            code = runner.finalize(self.args)
        return code, json.loads((self.args.output / "finalization.json").read_bytes())

    def failed_saved_fixture(self):
        self.expected = self.run / "saved" / "manifest.json"
        self.write_snapshot(self.expected.parent, self.project, 11)
        self.capture.update(status="blocked", completedAtUtc="2026-10-13T01:00:00+00:00", remainingSources=[],
            failure={"type": "RuntimeError", "message": "Owned qualification cleanup failed: host_close"},
            binaries={"host": {"path": str(self.args.host)}, "worker": {"path": str(self.args.worker)}})
        cleanup = self.capture["sessions"][0]["cleanup"]
        cleanup.update(completed=False, errors=[{"action": "host_close", "message": "Host shutdown was not clean."}],
            launcherShutdown={"hostExitCode": 1, "forcedHostStop": False,
                "workerExits": [{"processId": 33, "exitCode": None, "processExitVerified": False, "forcedTermination": False}]})
        self.audit["remainingInputs"] = 0
        self.audit["expectedSnapshot"] = {"path": str(self.expected), "sha256": runner.hash_file(self.expected)}
        self.audit["verified"].update(exactOwnedCleanup=False, freshProcessPersistence=False, finalSavedSnapshot=True)
        self.bind()
        self.observation_path = self.root / "artifacts" / "observation.json"
        self.observation = {"observedAtUtc": "2026-10-13T01:01:00+00:00", "originalOwnedProcessIds": [11, 33],
            "originalOwnedIdsStillPresent": [], "selectedBundleProcessesStillPresent": [], "terminationAttempted": False, "inventory": []}
        self.observation_path.write_text(json.dumps(self.observation), encoding="utf-8")
        self.supplement = {"report": {"path": str(self.report_path), "sha256": self.args.capture_sha256},
            "nativeProcessesRemainObserved": False, "cleanCleanupAccepted": False, "freshPersistenceEstablished": False,
            "cleanup": copy.deepcopy(cleanup), "processObservation": {"path": str(self.observation_path),
                "sha256": runner.hash_file(self.observation_path)}}
        self.args.cleanup_failure_supplement = self.root / "artifacts" / "supplement.json"
        self.bind_supplement()

    def bind_supplement(self):
        self.args.cleanup_failure_supplement.write_text(json.dumps(self.supplement), encoding="utf-8")
        self.args.supplement_sha256 = runner.hash_file(self.args.cleanup_failure_supplement)

    def test_explicit_failed_cleanup_copy_proves_only_new_readback(self):
        self.failed_saved_fixture()
        original = self.report_path.read_bytes()
        code, result = self.execute()
        self.assertEqual(0, code)
        self.assertEqual(["tia_start", "project_open", "close"], self.calls)
        self.assertTrue(result["persistenceVerified"])
        self.assertFalse(result["originalCleanCleanupAccepted"])
        self.assertEqual("blocked", result["originalOutcome"])
        self.assertEqual(self.capture["failure"], result["originalFailure"])
        self.assertEqual(original, self.report_path.read_bytes())
        self.assertEqual(str(self.expected), result["expectedSnapshot"]["path"])

    def test_failed_cleanup_is_not_admitted_by_default_or_unpaired_options(self):
        self.failed_saved_fixture()
        for path, digest in [(None, None), (self.args.cleanup_failure_supplement, None), (None, self.args.supplement_sha256)]:
            with self.subTest(path=path, digest=digest):
                self.args.cleanup_failure_supplement, self.args.supplement_sha256 = path, digest
                with self.assertRaises(RuntimeError):
                    runner.preflight(self.args)
        self.assertFalse(self.calls)

    def test_failed_cleanup_requires_exact_audited_boundary(self):
        baseline_capture, baseline_audit = copy.deepcopy(self.capture), copy.deepcopy(self.audit)
        for fault in ("other-failure", "remaining-source", "compile-incomplete", "cleanup-promoted", "forced-host", "unverified-tia", "wrong-snapshot"):
            with self.subTest(fault=fault):
                self.capture, self.audit = copy.deepcopy(baseline_capture), copy.deepcopy(baseline_audit)
                self.failed_saved_fixture()
                if fault == "other-failure": self.capture["failure"]["message"] = "uncertain mutation"
                if fault == "remaining-source": self.capture["remainingSources"] = ["not admitted"]
                if fault == "compile-incomplete": self.audit["verified"]["nativeImportCompileSave"] = False
                if fault == "cleanup-promoted": self.audit["verified"]["exactOwnedCleanup"] = True
                if fault == "forced-host": self.capture["sessions"][0]["cleanup"]["launcherShutdown"]["forcedHostStop"] = True
                if fault == "unverified-tia": self.capture["sessions"][0]["cleanup"]["tiaProcessExit"]["processExitVerified"] = False
                if fault == "wrong-snapshot": self.audit["expectedSnapshot"]["path"] = str(self.run / "source-00" / "after" / "manifest.json")
                self.bind()
                self.supplement["report"]["sha256"] = self.args.capture_sha256
                self.supplement["cleanup"] = copy.deepcopy(self.capture["sessions"][0]["cleanup"])
                self.bind_supplement()
                with self.assertRaises(RuntimeError):
                    runner.preflight(self.args)
        self.assertFalse(self.calls)

    def test_failed_cleanup_exit_observation_cannot_hide_live_or_stale_processes(self):
        self.failed_saved_fixture()
        original = copy.deepcopy(self.observation)
        for fault in ("live-id", "live-binary", "contradictory-ids", "stale", "termination", "wrong-ids", "unhashed"):
            with self.subTest(fault=fault):
                value = copy.deepcopy(original)
                if fault == "live-id": value["inventory"] = [{"ProcessId": 33, "ExecutablePath": "worker"}]
                if fault == "live-binary": value["inventory"] = [{"ProcessId": 99, "ExecutablePath": str(self.args.worker)}]
                if fault == "contradictory-ids": value["originalOwnedIdsStillPresent"] = [{"ProcessId": 33}]
                if fault == "stale": value["observedAtUtc"] = "2026-10-13T00:59:00+00:00"
                if fault == "termination": value["terminationAttempted"] = True
                if fault == "wrong-ids": value["originalOwnedProcessIds"] = [22, 44]
                self.observation_path.write_text(json.dumps(value) + (" " if fault == "unhashed" else ""), encoding="utf-8")
                self.supplement["processObservation"]["sha256"] = "changed" if fault == "unhashed" else runner.hash_file(self.observation_path)
                self.bind_supplement()
                with self.assertRaises(RuntimeError):
                    runner.preflight(self.args)
        self.assertFalse(self.calls)

    def test_failed_cleanup_supplement_cannot_rebind_or_promote_original(self):
        self.failed_saved_fixture()
        original = copy.deepcopy(self.supplement)
        for fault in ("report-hash", "report-path", "cleanup", "claimed-clean", "claimed-persistence", "claimed-live"):
            with self.subTest(fault=fault):
                self.supplement = copy.deepcopy(original)
                if fault == "report-hash": self.supplement["report"]["sha256"] = "wrong"
                if fault == "report-path": self.supplement["report"]["path"] = str(self.root / "different.json")
                if fault == "cleanup": self.supplement["cleanup"]["completed"] = True
                if fault == "claimed-clean": self.supplement["cleanCleanupAccepted"] = True
                if fault == "claimed-persistence": self.supplement["freshPersistenceEstablished"] = True
                if fault == "claimed-live": self.supplement["nativeProcessesRemainObserved"] = True
                self.bind_supplement()
                with self.assertRaises(RuntimeError):
                    runner.preflight(self.args)
        self.assertFalse(self.calls)

    def test_success_opens_only_exact_copy_and_keeps_every_original_byte(self):
        before = runner.file_inventory(self.project.parent)
        code, result = self.execute()
        self.assertEqual(0, code)
        self.assertEqual(["tia_start", "project_open", "close"], self.calls)
        self.assertTrue(result["completed"])
        self.assertTrue(result["persistenceVerified"])
        self.assertTrue(result["copyVerification"]["preserved"])
        self.assertTrue(result["originalPreservation"]["preserved"])
        self.assertEqual(2, len(result["copiedFilesBeforeOpen"]["files"]))
        self.assertEqual(before["files"], runner.file_inventory(self.project.parent)["files"])
        self.assertEqual("stopped_by_request", json.loads(self.report_path.read_bytes())["status"])

    def test_altered_report_is_rejected_before_native_start(self):
        self.report_path.write_bytes(self.report_path.read_bytes() + b" ")
        with self.assertRaisesRegex(RuntimeError, "Pinned input hash"):
            self.execute()
        self.assertFalse(self.calls)

    def test_audit_must_bind_same_report(self):
        self.audit["sourceReport"]["sha256"] = "different"
        self.audit_path.write_text(json.dumps(self.audit), encoding="utf-8")
        self.args.audit_sha256 = runner.hash_file(self.audit_path)
        with self.assertRaisesRegex(RuntimeError, "does not bind"):
            self.execute()
        self.assertFalse(self.calls)

    def test_failed_or_uncertain_original_lifetime_is_not_admitted(self):
        for fault in ("failure", "cleanup", "exit", "empty-fixtures", "incomplete-fixture"):
            with self.subTest(fault=fault):
                original = copy.deepcopy(self.capture)
                if fault == "failure": self.capture["failure"] = {"message": "unknown outcome"}
                if fault == "cleanup": self.capture["sessions"][0]["cleanup"]["completed"] = False
                if fault == "exit": self.capture["sessions"][0]["cleanup"]["tiaProcessExit"]["processExitVerified"] = False
                if fault == "empty-fixtures": self.capture["fixtures"] = []
                if fault == "incomplete-fixture": self.capture["fixtures"][0]["engineeringVerified"] = False
                self.bind()
                with self.assertRaises(RuntimeError):
                    self.execute()
                self.assertFalse(self.calls)
                self.capture = original

    def test_incomplete_audit_does_not_admit_native(self):
        self.audit["verified"]["allPeerXml"] = False
        self.bind()
        with self.assertRaisesRegex(RuntimeError, "audit is incomplete"):
            self.execute()
        self.assertFalse(self.calls)

    def test_expected_export_hash_is_checked_before_native(self):
        (self.expected.parent / "0000.xml").write_bytes(b"altered")
        with self.assertRaisesRegex(RuntimeError, "export changed"):
            self.execute()
        self.assertFalse(self.calls)

    def test_original_path_cannot_escape_owned_capture_retrieval(self):
        self.capture["retrieved"]["project"]["path"] = str(self.root / "unrelated.ap21")
        self.bind()
        with self.assertRaisesRegex(RuntimeError, "outside the recorded"):
            self.execute()
        self.assertFalse(self.calls)

    def test_output_cannot_modify_historical_capture(self):
        self.args.output = self.run / "new-output"
        with self.assertRaisesRegex(RuntimeError, "outside the historical"):
            self.execute()
        self.assertFalse(self.calls)

    def test_existing_output_is_never_replayed(self):
        self.args.output.mkdir()
        with self.assertRaises(FileExistsError):
            self.execute()
        self.assertFalse(self.calls)

    def test_copy_mismatch_blocks_before_native_start(self):
        import shutil
        real_copy = shutil.copytree
        def corrupt_copy(source, target, **kwargs):
            value = real_copy(source, target, **kwargs)
            (target / "companion.info").write_bytes(b"corrupted copy")
            return value
        with mock.patch.object(runner.shutil, "copytree", side_effect=corrupt_copy):
            code, result = self.execute()
        self.assertEqual(2, code)
        self.assertFalse(self.calls)
        self.assertFalse(result["copyVerification"]["preserved"])
        self.assertTrue(result["originalPreservation"]["preserved"])

    def test_readback_failure_always_closes_owned_lifetime_without_replay(self):
        self.snapshot_failure = True
        code, result = self.execute()
        self.assertEqual(2, code)
        self.assertFalse(result["completed"])
        self.assertFalse(result["persistenceVerified"])
        self.assertEqual(["tia_start", "project_open", "close"], self.calls)

    def test_changed_native_id_blocks_persistence(self):
        self.alter_xml = True
        code, result = self.execute()
        self.assertEqual(2, code)
        self.assertFalse(result["persistenceVerified"])
        self.assertEqual(["block:/plc/Peer"], result["strictComparison"]["changed"])
        self.assertEqual("close", self.calls[-1])

    def test_original_change_blocks_overall_acceptance(self):
        self.alter_original = True
        code, result = self.execute()
        self.assertEqual(2, code)
        self.assertFalse(result["completed"])
        self.assertFalse(result["originalPreservation"]["preserved"])

    def test_cleanup_failure_is_not_success(self):
        self.close_failure = True
        code, result = self.execute()
        self.assertEqual(2, code)
        self.assertFalse(result["completed"])
        self.assertIn("cleanupFailure", result)

    def test_read_only_adapter_rejects_all_mutation_and_retrieve_names(self):
        adapter = runner.ReadOnlyPrefixSession.__new__(runner.ReadOnlyPrefixSession)
        for name in ("change_prepare", "change_apply", "change_restore", "project_retrieve", "project_save", "project_compile", "simulation_start"):
            with self.subTest(tool=name), self.assertRaisesRegex(RuntimeError, "only owned open and read"):
                adapter.invoke(name)

    def test_strict_comparison_preserves_comments_ids_whitespace_cdata_and_namespaces(self):
        original = runner.strict_payload(self.xml)
        for altered in (self.xml.replace(b"peer", b"changed"), self.xml.replace(b"keep", b"different"),
                self.xml.replace(b'ID="7"', b'ID="8"'), self.xml.replace(b" text ", b"text"),
                self.xml.replace(b" text ", b"<![CDATA[ text ]]>"), self.xml.replace(b"urn:kept", b"urn:other")):
            self.assertNotEqual(original, runner.strict_payload(altered))
        self.assertNotEqual(original, runner.strict_payload(self.xml.replace(b"<Document>", b"<Document><DocumentInfo>provenance</DocumentInfo>")))

    def test_only_simple_created_timestamp_text_is_normalized(self):
        first = b'<Document><DocumentInfo><Created>2026-10-12T12:00:00.1234567Z</Created><ExportSetting>WithDefaults</ExportSetting><InstalledProducts><Product>V21</Product></InstalledProducts></DocumentInfo><Payload /></Document>'
        original = runner.strict_payload(first)
        self.assertEqual(original, runner.strict_payload(first.replace(b"12:00:00", b"12:01:00")))
        for changed in (first.replace(b"WithDefaults", b"Other"), first.replace(b"V21", b"V22"),
                first.replace(b"<DocumentInfo>", b'<DocumentInfo Unknown="1">'),
                first.replace(b"</DocumentInfo>", b"<!--unknown--></DocumentInfo>"),
                first.replace(b"</DocumentInfo>", b"<Unknown>retained</Unknown></DocumentInfo>"),
                first.replace(b"<Created>", b'<Created Unknown="1">'),
                first.replace(b"<Created>", b"<Created><![CDATA[").replace(b"</Created>", b"]]></Created>"),
                first.replace(b"<Created>", b"<Created><!--retained-->"),
                first.replace(b"<Created>", b"<Created><Nested>").replace(b"</Created>", b"</Nested></Created>")):
            with self.subTest(xml=changed):
                self.assertNotEqual(original, runner.strict_payload(changed))

    def test_created_with_extra_payload_is_compared_in_full(self):
        for created in (b'<Created Unknown="1">2026-10-12T12:00:00Z</Created>',
                b'<Created><!--keep-->2026-10-12T12:00:00Z</Created>',
                b'<Created><![CDATA[2026-10-12T12:00:00Z]]></Created>',
                b'<Created><Nested>2026-10-12T12:00:00Z</Nested></Created>',
                b'<Created>2026-10-12T12:00:00Z</Created><Created>other</Created>'):
            first = b'<Document><DocumentInfo>' + created + b'</DocumentInfo></Document>'
            with self.subTest(xml=first):
                self.assertNotEqual(runner.strict_payload(first), runner.strict_payload(first.replace(b"12:00:00", b"12:01:00")))


if __name__ == "__main__":
    unittest.main()
