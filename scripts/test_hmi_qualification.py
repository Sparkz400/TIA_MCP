"""Offline tests of HMI evidence semantics; never claim native qualification."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from probe_hmi_api import inspect_api
from hmi_fixture_copy import copy_project, project_tree, safe_path, verify_source_preserved
from qualify_hmi import (GATES, blocking_startup_failure, block_pending_profiles, compare_device_profile, compare_explorer_relationships, compare_resource_sources, compare_section, compare_screen_items, gate, import_native_evidence,
                         native_compile_gate, native_payload, native_section_entries, prepare_owned_inspection_copy, qualify_alarm_references, qualify_bridge, qualify_relationships, reassess_captured, semantic_snapshot, summarize_gates, validate_fixture, validate_native_copy_directory, validate_owned_derived_project, verify_copied_project_identity)


class HmiQualificationTests(unittest.TestCase):
    def inspection_copy_fixture(self, root):
        evidence = root / "evidence"; evidence.mkdir()
        project = evidence / "recovered" / "TiaMcpHmi_copy" / "TiaMcpHmi_copy.ap21"
        project.parent.mkdir(parents=True); project.write_bytes(b"independent native fixture")
        (project.parent / "companion").mkdir(); (project.parent / "companion" / "state.info").write_bytes(b"all content")
        (project.parent / "empty").mkdir()
        archive = evidence / "baseline.zap21"; archive.write_bytes(b"independent archive")
        owners = [{"name": "HMI", "type": "Siemens.Engineering.HmiUnified.HmiSoftware"}]
        profile = {"profile": "unified-comfort", "selectedTarget": {"typeIdentifier": "observed"}, "projectPath": str(project),
            "archive": {"path": str(archive), "sha256": hashlib.sha256(archive.read_bytes()).hexdigest()},
            "nativeBefore": owners, "nativeRecovered": copy.deepcopy(owners), "archiveRetrievalVerified": True,
            "recoveredProjectPath": str(project), "compile": [{"name": "HMI", "state": "Error", "errorCount": 2,
                "compileTargetType": "Siemens.Engineering.HmiUnified.HmiSoftware", "messages": []}]}
        native = {"completed": True, "ownsPortalProcess": True, "cleanup": "owned-portal-disposed", "profiles": [profile]}
        report = evidence / "native-report.json"; report.write_text(json.dumps(native), encoding="utf-8")
        return profile, native, report, hashlib.sha256(report.read_bytes()).hexdigest(), project

    def test_owned_copy_preserves_all_source_files_and_keeps_failed_compile(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); profile, native, report, digest, source = self.inspection_copy_fixture(root)
            receipt = {}; copied = prepare_owned_inspection_copy(profile, report, digest, root / "out", receipt)
            self.assertNotEqual(copied, source)
            self.assertEqual(receipt["sourceBefore"], project_tree(copied.parent))
            self.assertTrue(any(row["path"].endswith("state.info") for row in receipt["sourceBefore"]["files"]))
            self.assertIn("empty", receipt["sourceBefore"]["directories"])
            copied.write_bytes(b"native changes confined to disposable copy")
            verify_source_preserved(receipt)
            self.assertTrue(receipt["sourcePreserved"])
            self.assertEqual(native_compile_gate(profile, "compile")["status"], "failed")
            source.write_bytes(b"unexpected source change")
            with self.assertRaisesRegex(AssertionError, "Original HMI fixture project changed"):
                verify_source_preserved(receipt)

    def test_owned_copy_rejects_report_changes_outside_projects_and_evidence_destination(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); profile, native, report, digest, source = self.inspection_copy_fixture(root)
            with self.assertRaisesRegex(AssertionError, "outside original evidence"):
                prepare_owned_inspection_copy(profile, report, digest, report.parent / "out", {})
            profile["recoveredProjectPath"] = str(root / "TiaMcpHmi_other.ap21")
            with self.assertRaisesRegex(AssertionError, "exact recorded"):
                prepare_owned_inspection_copy(profile, report, digest, root / "out", {})
            report.write_text("changed")
            with self.assertRaisesRegex(AssertionError, "reference report changed"):
                prepare_owned_inspection_copy(profile, report, digest, root / "out", {})

    def test_owned_copy_rejects_nonfresh_overlapping_and_reparse_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); profile, native, report, digest, source = self.inspection_copy_fixture(root)
            with self.assertRaisesRegex(AssertionError, "fresh complete project"):
                copy_project(source, source.parent, {})
            with self.assertRaisesRegex(AssertionError, "overlaps source"):
                copy_project(source, source.parent / "nested" / source.parent.name, {})
            with self.assertRaisesRegex(AssertionError, "exact absolute"):
                safe_path(Path("relative"))
            original_lstat = Path.lstat
            def reparse(path):
                value = original_lstat(path)
                return SimpleNamespace(st_file_attributes=0x400, st_mode=value.st_mode) if path == source else value
            with patch.object(Path, "lstat", reparse):
                with self.assertRaisesRegex(AssertionError, "links/reparse"):
                    project_tree(source.parent)

    def test_owned_copy_detects_source_change_while_copying(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); profile, native, report, digest, source = self.inspection_copy_fixture(root)
            import shutil
            actual_copy = shutil.copyfile
            def changed_source(path, target):
                result = actual_copy(path, target)
                if path == source: source.write_bytes(b"changed after read")
                return result
            with patch("hmi_fixture_copy.shutil.copyfile", side_effect=changed_source):
                with self.assertRaisesRegex(AssertionError, "changed during fixture copy"):
                    prepare_owned_inspection_copy(profile, report, digest, root / "out", {})

    def test_copied_native_mode_opens_only_copy_and_never_retrieves_or_saves(self):
        class FakeClient:
            def __init__(self):
                self.calls = []; self.errors = []; self.process = SimpleNamespace(returncode=0)
            def request(self, method, params=None):
                if method == "initialize": return {}
                if method == "tools/list": return {"tools": [{"name": n} for n in ("hmi_inspect", "hmi_references", "hmi_capabilities", "project_open")]}
                self.calls.append(params)
                if params["name"] == "tia_start": return {"structuredContent": {"ownsPortalProcess": True, "processId": 123, "projects": []}}
                if params["name"] == "project_open":
                    path = Path(params["arguments"]["path"]); self.project = {"path": str(path), "name": path.stem}
                    return {"structuredContent": {"opened": True, "saved": False, "processId": 123, "project": self.project}}
                raise AssertionError("Unexpected native dispatch")
            def notify(self, *args): pass
            def tool(self, name):
                self.calls.append({"name": name})
                if name == "tia_detach": return {"detached": True}
                if name == "tia_session_status": return {"attached": True, "ownsPortalProcess": True, "processId": 123, "projects": [self.project]}
                raise AssertionError("Unexpected native dispatch")
            def close(self): return {"hostExitCode": 0, "forcedHostStop": False, "workerExits": [{"processExitVerified": True, "exitCode": 0}]}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); profile, native, source_report, digest, source = self.inspection_copy_fixture(root)
            report = {"nativeReportSha256": digest, "profiles": [{"profile": profile["profile"], "gates": import_native_evidence(profile)}]}
            args = SimpleNamespace(host=Path("host.exe"), worker=Path("worker.exe"), artifacts=Path(root.anchor) / "mock-hmi", timeout=1,
                                   native_report=source_report, inspect_owned_recovered_copy=True)
            client = FakeClient()
            # Pure copying has separate real-filesystem tests. This fake-native
            # test must use a native-eligible path without creating drive-root files.
            def fake_copy(source, destination, receipt):
                receipt.update(sourceRoot=str(source.parent), sourceBefore=project_tree(source.parent))
                return destination / source.name
            with patch("qualify_hmi.copy_project", side_effect=fake_copy), patch("qualify_hmi.Client", return_value=client), patch("qualify_hmi.OwnedTiaProcessWatcher") as watcher, patch("qualify_hmi.inspect_all", side_effect=AssertionError("stop test after open")):
                watcher.return_value.wait_for_exit.return_value = {"completed": True}
                qualify_bridge(args, native, report, lambda: None)
            self.assertEqual([call["name"] for call in client.calls], ["tia_start", "project_open", "tia_session_status", "tia_detach"])
            self.assertNotEqual(client.calls[1]["arguments"]["path"], str(source))
            self.assertTrue(report["originalFixturesPreserved"] and report["cleanupVerified"])
            result = report["profiles"][0]
            self.assertFalse(result["bridgeArchiveRetrieved"])
            self.assertEqual(result["gates"]["archiveRecovery"]["status"], "unverified")
            self.assertEqual(result["gates"]["compile"]["status"], "failed")

    def test_native_copy_directory_limit_rejects_before_client_or_native_start(self):
        directory = Path.cwd().anchor + "x" * (143 - len(Path.cwd().anchor))
        validate_native_copy_directory(Path(directory))
        with self.assertRaisesRegex(AssertionError, "native project directory limit"):
            validate_native_copy_directory(Path(directory + "x"))
        profile = {"archive": {"path": "unused"}, "nativeBefore": [{}],
                   "recoveredProjectPath": str(Path.cwd() / "TiaMcpHmi_copy" / "TiaMcpHmi_copy.ap21")}
        args = SimpleNamespace(artifacts=Path(directory), inspect_owned_recovered_copy=True)
        with patch("qualify_hmi.Client") as client, patch("qualify_hmi.prepare_owned_inspection_copy") as copy_fixture:
            with self.assertRaisesRegex(AssertionError, "native project directory limit"):
                qualify_bridge(args, {"profiles": [profile]}, {}, lambda: None)
            client.assert_not_called()
            copy_fixture.assert_not_called()

    def test_file_only_copy_supports_long_runner_paths_without_native_admission(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); profile, native, report, digest, source = self.inspection_copy_fixture(root)
            padding = "x" * max(1, 145 - len(str(root / source.parent.name)))
            target = root / padding / source.parent.name
            self.assertGreater(len(str(target)), 143)
            receipt = {}; copied = copy_project(source, target, receipt)
            self.assertTrue(receipt["copyVerified"])
            self.assertEqual(source.read_bytes(), copied.read_bytes())
            with self.assertRaisesRegex(AssertionError, "native project directory limit"):
                validate_native_copy_directory(copied.parent)

    def test_copied_open_requires_exact_project_and_owned_session_identity(self):
        copied = Path("TiaMcpHmi_copy.ap21").absolute()
        project = {"name": copied.stem, "path": str(copied)}
        start = {"processId": 123, "ownsPortalProcess": True, "projects": []}
        opened = {"processId": 123, "project": project, "opened": True, "saved": False}
        status = {"processId": 123, "ownsPortalProcess": True, "attached": True, "projects": [project]}
        verify_copied_project_identity(copied, start, opened, status)
        for field, changes in (("start", {"projects": [project]}), ("start", {"processId": True}),
                               ("open", {"processId": 456}), ("open", {"project": {**project, "path": str(copied.parent / "other.ap21")}}),
                               ("status", {"processId": 456}), ("status", {"ownsPortalProcess": False}),
                               ("status", {"projects": [project, project]})):
            candidates = {"start": copy.deepcopy(start), "open": copy.deepcopy(opened), "status": copy.deepcopy(status)}
            candidates[field].update(changes)
            with self.subTest(field=field, changes=changes), self.assertRaises(AssertionError):
                verify_copied_project_identity(copied, candidates["start"], candidates["open"], candidates["status"])

    def test_fallback_requires_unchanged_native_source_and_owned_recovered_path(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root / "native-report.json"; source.write_bytes(b"independent source")
            project = root / "TiaMcpHmi_test.ap21"; project.write_bytes(b"owned fixture")
            profile = {"fixtureMode": "positive", "archiveRetrievalVerified": True, "recoveredProjectPath": str(project),
                       "nativeBefore": [{"name": "HMI", "type": "Siemens.Engineering.HmiUnified.HmiSoftware"}],
                       "compileRecovered": [{"name": "HMI", "errorCount": 0, "state": "Success", "messages": [], "compileTargetType": "Siemens.Engineering.HW.Device"}]}
            native = {"sourceNativeReport": {"path": str(source), "sha256": hashlib.sha256(source.read_bytes()).hexdigest()}}
            self.assertEqual(validate_owned_derived_project(profile, native, root / "derived.json"), project.resolve())
            profile["recoveredProjectPath"] = str(root.parent / "TiaMcpHmi_other.ap21")
            with self.assertRaisesRegex(AssertionError, "exact independent"): validate_owned_derived_project(profile, native, root / "derived.json")
            profile["recoveredProjectPath"] = str(project); source.write_bytes(b"changed")
            with self.assertRaisesRegex(AssertionError, "source report"): validate_owned_derived_project(profile, native, root / "derived.json")

    def test_resource_export_requires_both_unchanged_native_files_and_partial_scope(self):
        with tempfile.TemporaryDirectory() as directory:
            sources = []
            for index, content in enumerate((b"configuration\r\n", b"two-language text\r\n")):
                path = Path(directory) / (str(index) + ".yml"); path.write_bytes(content)
                sources.append({"artifact": {"path": str(path), "sha256": hashlib.sha256(content).hexdigest()}, "text": content.decode(), "executed": False})
            exports = []
            for index, source in enumerate(sources):
                path = Path(directory) / ("independent-" + str(index) + ".yml")
                path.write_bytes(Path(source["artifact"]["path"]).read_bytes())
                exports.append({"path": str(path), "sha256": source["artifact"]["sha256"]})
            native = {"textListEvidence": {"status": "observed", "fixtureTextsPresent": True,
                "files": [{"sha256": row["artifact"]["sha256"], "text": row["text"]} for row in sources], "export": exports}}
            observed = {"items": [{"name": "QualificationStates", "nativeSources": sources, "nativeSourceCoverage": {"complete": False, "nativeComposition": "HmiTextLists"}}]}
            self.assertEqual(compare_resource_sources(native, observed)["fileCount"], 2)
            changed = copy.deepcopy(observed); changed["items"][0]["nativeSources"].pop()
            with self.assertRaisesRegex(AssertionError, "Both native"): compare_resource_sources(native, changed)
            changed = copy.deepcopy(observed); changed["items"][0]["nativeSourceCoverage"]["complete"] = True
            with self.assertRaisesRegex(AssertionError, "incomplete composition"): compare_resource_sources(native, changed)
            changed = copy.deepcopy(observed); changed["items"][0]["nativeSources"][0]["text"] = "changed"
            with self.assertRaisesRegex(AssertionError, "Returned resource text"): compare_resource_sources(native, changed)
            independent_path = Path(exports[0]["path"])
            original_bytes = independent_path.read_bytes(); independent_path.write_bytes(b"changed baseline")
            with self.assertRaisesRegex(AssertionError, "Independent native resource export"): compare_resource_sources(native, observed)
            independent_path.write_bytes(original_bytes)
            changed_native = copy.deepcopy(native); changed_native["textListEvidence"]["files"][0]["text"] = "changed baseline text"
            with self.assertRaisesRegex(AssertionError, "Independent recorded resource text"): compare_resource_sources(changed_native, observed)
            changed = copy.deepcopy(observed); changed["items"][0]["nativeSources"][1] = changed["items"][0]["nativeSources"][0]
            with self.assertRaisesRegex(AssertionError, "distinct native files"): compare_resource_sources(native, changed)
            Path(sources[0]["artifact"]["path"]).write_bytes(b"altered artifact")
            with self.assertRaisesRegex(AssertionError, "incomplete or changed"): compare_resource_sources(native, observed)

    def test_captured_resource_reassessment_cannot_drop_missing_or_failed_sources(self):
        owner = {"name": "HMI", "type": "Siemens.Engineering.HmiUnified.HmiSoftware", "textListEvidence": {"status": "observed"}}
        native = {"profiles": [{"profile": "unified-comfort", "nativeBefore": [owner]}]}
        captured = {"profiles": [{"profile": "unified-comfort", "gates": {name: gate() for name in GATES},
            "inspections": [{"target": {"name": "HMI", "family": "unified", "objectPath": "/HMI"}, "section": "resources", "result": {}}]}]}
        with patch("qualify_hmi.compare_section", return_value={"status": "passed"}), patch("qualify_hmi.qualify_relationships"), patch("qualify_hmi.qualify_alarm_references"):
            observed = reassess_captured(native, captured)[0]
            self.assertEqual(observed["gates"]["inspectionAccuracy"]["status"], "failed")
            self.assertIn("requires one captured", observed["resourceSourceComparisons"][0]["comparison"]["reason"])
            captured["profiles"][0]["resourceSources"] = [{"target": {"name": "HMI"}, "result": {}, "comparison": {"status": "passed"}}]
            with patch("qualify_hmi.compare_resource_sources", side_effect=AssertionError("retained file changed")):
                observed = reassess_captured(native, captured)[0]
            self.assertEqual(observed["gates"]["inspectionAccuracy"]["status"], "failed")
            self.assertEqual(observed["resourceSourceComparisons"][0]["comparison"]["reason"], "retained file changed")

    def test_captured_copy_reassessment_keeps_bridge_recovery_unverified(self):
        owner = {"name": "HMI", "type": "Siemens.Engineering.HmiUnified.HmiSoftware"}
        baseline = {"profile": "unified-comfort", "nativeBefore": [owner], "nativeRecovered": [copy.deepcopy(owner)], "archiveRetrievalVerified": True}
        gates = {name: gate() for name in GATES}
        captured = {"qualificationMode": "owned-recovered-copy-inspection", "profiles": [{"profile": baseline["profile"], "gates": gates,
            "inspectionCopy": {"sourcePreserved": True},
            "inspections": [{"target": {"name": "HMI", "family": "unified", "objectPath": "/HMI"}, "section": "resources", "result": {}}]}]}
        with patch("qualify_hmi.compare_section", return_value={"status": "passed"}), patch("qualify_hmi.qualify_relationships"), patch("qualify_hmi.qualify_alarm_references"):
            result = reassess_captured({"profiles": [baseline]}, captured)[0]
            self.assertEqual(result["gates"]["archiveRecovery"]["status"], "unverified")
            self.assertEqual(result["nativeArchiveRecoveryGate"]["status"], "passed")
            captured["profiles"][0]["inspectionCopy"]["sourcePreserved"] = False
            result = reassess_captured({"profiles": [baseline]}, captured)[0]
            self.assertEqual(result["gates"]["inspectionAccuracy"]["status"], "failed")

    def test_explorer_requires_exact_populated_native_edges(self):
        edge = {"relation": "hmiAlarmTag", "sourcePath": "/hmi/alarms/a", "target": {"objectPath": "/hmi/tags/t"},
                "sourceProperty": "RaisedStateTag", "nativeSpelling": "t", "resolution": "resolved"}
        references = [{"result": {"relationships": [edge]}}]
        observed = {"edges": [{"source": {"nodeId": "alarm-a", "objectPath": edge["sourcePath"], "resolution": "resolved"},
                              "target": {"nodeId": "tag-t", "objectPath": edge["target"]["objectPath"], "resolution": "resolved"},
                              "evidence": {"native": copy.deepcopy(edge), "resolution": "resolved"}, "unresolved": None}],
                    "nodes": [{"nodeId": "alarm-a", "objectPath": edge["sourcePath"]}, {"nodeId": "tag-t", "objectPath": edge["target"]["objectPath"]}],
                    "scriptsExecuted": False}
        for reference in observed["nodes"]:
            self.assertEqual(compare_explorer_relationships(reference, observed, references)["expectedRelationshipCount"], 1)
        for mutate in (lambda row: row.update(edges=[]),
                       lambda row: row["edges"][0]["evidence"]["native"]["target"].update(objectPath="/other/tags/t"),
                       lambda row: row["edges"][0].update(source=None),
                       lambda row: row["edges"][0]["source"].update(nodeId="different-alarm"),
                       lambda row: row["nodes"][0].update(objectPath="/hmi/alarms/different"),
                       lambda row: row["edges"][0]["target"].update(objectPath="/other/tags/t"),
                       lambda row: row["edges"][0]["evidence"].update(resolution="unresolved"),
                       lambda row: row.update(scriptsExecuted=True)):
            changed = copy.deepcopy(observed); mutate(changed)
            with self.assertRaises(AssertionError): compare_explorer_relationships({"nodeId": "alarm-a", "objectPath": "/hmi/alarms/a"}, changed, references)
        with self.assertRaisesRegex(AssertionError, "no independently observed"):
            compare_explorer_relationships({"objectPath": "/unknown"}, observed, references)

    def test_exact_catalog_provenance_keeps_missing_runtime_explicit(self):
        profile = {"profile": "unified-pc", "selectedTarget": {"typeIdentifier": "native/version21", "version": "21"}}
        row = {"profile": "unified-pc", "catalogIdentifier": "native/version21", "runtimeVersion": None,
               "catalogEvidence": {"status": "nativeConfirmed", "scopeComplete": True, "candidates": [{"sourcePath": "/native/item", "catalogIdentifier": "native/version21"}]}}
        self.assertEqual(compare_device_profile(profile, row)["runtimeVersionAvailability"], "unavailable")
        row["runtimeVersion"] = "17"
        with self.assertRaisesRegex(AssertionError, "runtime version"):
            compare_device_profile(profile, row)

    def test_alarm_binding_requires_exact_same_hmi_tag_and_bit(self):
        owner = {"name": "HMI", "family": "unified", "objectPath": "/HMI", "hmiPath": "/HMI"}
        alarm = {"nativeType": "HmiDiscreteAlarm", "attributes": {"Name": "QualificationAlarm", "RaisedStateTag": "SharedValue", "RaisedStateTagBitNumber": 0}}
        profile = {"nativeBefore": [{"name": "HMI", "sections": {"DiscreteAlarms": [alarm], "AnalogAlarms": []}}]}
        inspections = [{"target": owner, "section": "tags", "result": {"items": [{"name": "SharedValue", "identity": {"objectPath": "/HMI/tag"}}]}},
                       {"target": owner, "section": "alarms", "result": {"items": [{"name": "QualificationAlarm", "identity": {"objectPath": "/HMI/alarm"}}]}}]
        edge = {"relation": "hmiAlarmTag", "sourcePath": "/HMI/alarm", "sourceProperty": "RaisedStateTag", "nativeSpelling": "SharedValue", "resolution": "resolved",
                "target": {"objectPath": "/HMI/tag", "hmiPath": "/HMI"}, "bitNumber": 0}
        refs = [{"target": owner, "requestedAlarms": True, "result": {"relationships": [edge]}}]
        gates = {name: gate() for name in GATES}
        qualify_alarm_references(profile, refs, inspections, gates)
        self.assertEqual(gates["alarmTagReferences"]["status"], "passed")
        edge["target"]["hmiPath"] = "/otherHmi"
        with self.assertRaisesRegex(AssertionError, "same-HMI"):
            qualify_alarm_references(profile, refs, inspections, gates)

    def test_optional_modules_absent_is_not_qualified(self):
        with tempfile.TemporaryDirectory() as directory:
            result = inspect_api(Path(directory))
        self.assertEqual([row["apiAvailability"] for row in result["families"]], ["unavailable", "unavailable"])
        self.assertFalse(result["nativeCodeExecuted"])
        self.assertEqual(len(result["profiles"]), 6)

    def test_only_faulted_or_unknown_startup_stops_shared_worker(self):
        self.assertFalse(blocking_startup_failure({"code": "AccessDenied"}))
        self.assertTrue(blocking_startup_failure({"code": "BridgeFaulted"}))
        self.assertTrue(blocking_startup_failure({"code": "TimeoutOutcomeUnknown"}))
        self.assertFalse(blocking_startup_failure({"details": {"exceptionType": "Siemens.Engineering.EngineeringSecurityException"}}))
        self.assertFalse(blocking_startup_failure({"code": "TargetNotFound"}))
        self.assertFalse(blocking_startup_failure({"code": "TargetNotFound", "details": None}))

    def test_startup_fail_fast_preserves_completed_native_gates(self):
        native = [{"profile": name, "archive": {"path": "fixture"}, "nativeBefore": [{"name": "HMI"}]} for name in ("first", "next")]
        report = [{"profile": name, "gates": {key: gate() for key in GATES}} for name in ("first", "next")]
        report[0]["gates"]["deviceDiscovery"] = gate("failed", "original failure")
        report[1]["gates"]["saveReopen"] = gate("passed", "independent native evidence")
        report[1]["gates"]["compile"] = gate("failed", "native compile failed")
        failure = {"profile": "first", "operation": "tia_start", "error": {"code": "BridgeFaulted"}}
        block_pending_profiles(native, report, 0, failure)
        self.assertEqual(report[0]["gates"]["deviceDiscovery"]["status"], "failed")
        self.assertEqual(report[1]["gates"]["deviceDiscovery"]["status"], "blocked")
        self.assertEqual(report[1]["gates"]["saveReopen"]["status"], "passed")
        self.assertEqual(report[1]["gates"]["compile"]["status"], "failed")
        self.assertEqual(report[1]["bridgeBlockedByStartup"], failure)

    def test_faulted_start_dispatches_only_one_native_start(self):
        class FakeClient:
            def __init__(self, *args):
                self.starts = 0; self.errors = []; self.process = SimpleNamespace(returncode=0)
            def request(self, method, params=None):
                if method == "initialize": return {}
                if method == "tools/list": return {"tools": [{"name": name} for name in ("hmi_inspect", "hmi_references", "hmi_capabilities", "project_retrieve")]}
                self.starts += 1
                return {"isError": True, "structuredContent": {"error": {"code": "BridgeFaulted"}}}
            def notify(self, *args): pass
            def close(self): pass
            def tool(self, *args): raise AssertionError("No native call may follow denied startup")
        client = FakeClient()
        profiles = [{"profile": name, "archive": {"path": "fixture"}, "nativeBefore": [{"name": "HMI"}]} for name in ("classic-comfort", "unified-pc")]
        report = {"profiles": [{"profile": p["profile"], "gates": {name: gate() for name in GATES}} for p in profiles]}
        args = SimpleNamespace(host=Path("host.exe"), worker=Path("worker.exe"), artifacts=Path("artifacts"), timeout=1, native_report=Path("report.json"))
        with patch("qualify_hmi.Client", return_value=client), patch("qualify_hmi.validate_fixture", return_value=Path("fixture.zap21")):
            qualify_bridge(args, {"profiles": profiles}, report, lambda: None)
        self.assertEqual(client.starts, 1)
        self.assertEqual(report["profiles"][0]["gates"]["deviceDiscovery"]["status"], "failed")
        self.assertEqual(report["profiles"][1]["gates"]["deviceDiscovery"]["status"], "blocked")

    def test_native_relationships_key_qualifies_exact_endpoint(self):
        owner = {"name": "HMI", "objectPath": "/hmi", "family": "classic"}
        native = {"connectionFixture": {"links": [{"isValid": True, "hmiSoftware": "HMI", "plcSoftware": "PLC_A"}]}}
        edge = {"relation": "hmiConnectionPlc", "resolution": "resolved", "target": {"name": "PLC_A", "objectPath": "/plcA"},
                "nativeConnectionEvidence": [{"localTargetMatchesHmiOwner": True, "isValid": True}]}
        observed = [{"target": owner, "result": {"relationships": [edge]}}]
        gates = {name: gate() for name in GATES}
        qualify_relationships(native, observed, [{"target": owner}], gates)
        self.assertEqual(gates["integratedConnectionMapping"]["status"], "passed")
        observed[0]["result"] = {"edges": [edge]}
        with self.assertRaisesRegex(AssertionError, "relationships array"):
            qualify_relationships(native, observed, [{"target": owner}], gates)

    def test_script_evidence_survives_native_connection_fixture_failure(self):
        owner = {"name": "HMI", "objectPath": "/hmi", "family": "unified"}
        screen = {"name": "Overview", "objectPath": "/hmi/screens/Overview"}
        script = {"ScriptCode": "const x = 1;", "GlobalDefinitionAreaScriptCode": "const y = 2;", "Async": False}
        event = {"attributes": {"EventType": "Loaded"}, "Script": {"attributes": script}}
        native = {"nativeBefore": [{"name": "HMI", "sections": {"Tags": [], "TagTables": [], "Screens": [
            {"nativeType": "HmiScreen", "attributes": {"Name": "Overview"}, "ScreenItems": [], "EventHandlers": [event]}]}}],
            "connectionFixture": {"links": [{"hmiSoftware": "HMI", "plcSoftware": "PLC_A", "error": {"type": "InvalidOperationException"}}]}}
        edge = {"relation": "hmiScriptBehavior", "resolution": "dynamic", "sourcePath": screen["objectPath"], "eventMetadata": {"EventType": "Loaded"},
                "rawScript": {"scriptCode": script["ScriptCode"], "globalDefinitionAreaScriptCode": script["GlobalDefinitionAreaScriptCode"], "async": False,
                              "scriptCodeSha256": hashlib.sha256(script["ScriptCode"].encode()).hexdigest(),
                              "globalDefinitionAreaScriptCodeSha256": hashlib.sha256(script["GlobalDefinitionAreaScriptCode"].encode()).hexdigest(), "scriptsExecuted": False}}
        refs = [{"target": owner, "screen": screen, "result": {"relationships": [edge]}}]
        inspections = [{"target": owner, "screen": screen, "section": "items", "result": {"items": []}}]
        gates = {name: gate() for name in GATES}
        with self.assertRaisesRegex(AssertionError, "valid integrated"):
            qualify_relationships(native, refs, inspections, gates)
        self.assertEqual(gates["scriptBehaviorInspection"]["status"], "passed")
        self.assertEqual(gates["integratedConnectionMapping"]["status"], "failed")
        edge["rawScript"]["scriptCode"] = "const x = 3;"
        with self.assertRaisesRegex(AssertionError, "source/hash"):
            qualify_relationships(native, refs, inspections, {name: gate() for name in GATES})

    def test_expected_broken_getter_keeps_inventory_proof_partial(self):
        failure = {"type": "NativeError", "message": "Missing native tag"}
        native = {"ScreenItems": [{"nativeType": "Rectangle", "attributes": {"Name": "A", "Left": 31},
                   "Dynamizations": [{"attributes": {"Address": {"error": failure}}}]}]}
        observed = {"items": [{"nativeType": "Rectangle", "name": "A", "properties": {"Name": "A", "Left": 31}}],
                    "lastPage": {"coverage": {"status": "unavailable", "truncated": False, "pageComplete": True,
                    "reasons": [{"code": "HmiNativeReadFailed", "operation": "TagDynamization.Address", "nativeException": "NativeError", "reason": "Missing native tag"}]}}}
        comparison = compare_screen_items(native, "unified", observed)
        self.assertEqual(comparison["status"], "partial")
        self.assertEqual(comparison["inventoryAndScalarComparison"], "passed")
        self.assertEqual(comparison["nativeCoverage"]["status"], "unavailable")
        observed["items"][0]["properties"]["Left"] = 32
        with self.assertRaisesRegex(AssertionError, "scalar"):
            compare_screen_items(native, "unified", observed)
        observed["items"][0]["properties"]["Left"] = 31
        observed["lastPage"]["coverage"]["reasons"][0]["reason"] = "Unexpected native failure"
        with self.assertRaisesRegex(AssertionError, "unexpected"):
            compare_screen_items(native, "unified", observed)

    def test_malformed_metadata_recorded_separately(self):
        with tempfile.TemporaryDirectory() as directory:
            Path(directory, "Siemens.Engineering.WinCC.xml").write_text("<bad>")
            result = inspect_api(Path(directory))
        self.assertEqual(result["families"][0]["apiAvailability"], "invalid-metadata")
        self.assertEqual(result["families"][0]["nativeQualification"], "unverified")

    def test_api_metadata_does_not_establish_target(self):
        with tempfile.TemporaryDirectory() as directory:
            Path(directory, "Siemens.Engineering.WinCC.xml").write_text('<doc><members><member name="T:Siemens.Engineering.Hmi.HmiTarget"><summary>Root</summary></member></members></doc>')
            Path(directory, "Siemens.Engineering.WinCC.dll").write_bytes(b"offline mock binary")
            result = inspect_api(Path(directory))
        self.assertEqual(result["families"][0]["apiAvailability"], "available")
        self.assertEqual(result["families"][0]["currentTargetAvailability"], "not-probed")

    def test_blocked_and_unverified_cannot_pass_acceptance(self):
        gates = {name: gate("passed", "test") for name in GATES}
        gates["visualInspection"] = gate("blocked", "no native renderer")
        summary = summarize_gates(gates)
        self.assertFalse(summary["accepted"])
        self.assertEqual(summary["remainingAcceptanceCriteria"], ["visualInspection"])

    def test_missing_gate_cannot_disappear(self):
        with self.assertRaises(AssertionError): summarize_gates({})

    def test_native_block_is_recorded_for_every_required_gate(self):
        result = import_native_evidence({"status": "blocked", "reason": "LicenseNotFoundException"})
        self.assertEqual(set(result), set(GATES))
        self.assertTrue(all(row["status"] == "blocked" for row in result.values()))

    def test_fixture_bug_is_not_license_block(self):
        result = import_native_evidence({"status": "failed", "error": {"type": "FileNotFoundException"}})
        self.assertEqual(result["nativeFixture"]["status"], "failed")

    def test_reopen_requires_equal_native_readback(self):
        result = import_native_evidence({"status": "observed", "reopenVerified": True,
            "nativeBefore": [{"name": "A"}], "nativeReopened": [{"name": "B"}]})
        self.assertEqual(result["saveReopen"]["status"], "failed")

    def test_native_export_timestamp_only_normalization(self):
        before = b'<Document><DocumentInfo><Created>2026-10-08T10:00:00Z</Created></DocumentInfo><Hmi.Screen.Screen ID="0"><AttributeList><Name>A</Name></AttributeList></Hmi.Screen.Screen></Document>'
        after = before.replace(b"10:00:00Z", b"10:00:01Z")
        self.assertEqual(native_payload(before), native_payload(after))
        self.assertNotEqual(native_payload(before), native_payload(after.replace(b'ID="0"', b'ID="1"')))
        self.assertNotEqual(native_payload(before), native_payload(after.replace(b">A<", b">B<")))

    def test_unknown_payload_and_binding_changes_preserved(self):
        before = b'<Document><Hmi.Screen.Screen ID="0"><Unknown Created="today"/><LinkList><Tag TargetID="@OpenLink"><Name>PLC_A</Name></Tag></LinkList></Hmi.Screen.Screen></Document>'
        self.assertNotEqual(native_payload(before), native_payload(before.replace(b"PLC_A", b"PLC_B")))
        self.assertNotEqual(native_payload(before), native_payload(before.replace(b"today", b"tomorrow")))

    def test_preservation_keeps_comments_processing_instructions_and_whitespace(self):
        before = b'<?native evidence?><Document><!--retain--><Unknown> content </Unknown></Document><!--outer-->'
        for old, new in ((b"retain", b"changed"), (b"evidence", b"changed"), (b"outer", b"changed"), (b"> content <", b">content<")):
            with self.subTest(payload=old):
                self.assertNotEqual(native_payload(before), native_payload(before.replace(old, new)))
        self.assertNotEqual(native_payload(b"<Document><Unknown><![CDATA[content]]></Unknown></Document>"),
                            native_payload(b"<Document><Unknown>content</Unknown></Document>"))

    def test_preservation_rejects_unknown_timestamp_payload(self):
        for content in (b"before", b"2026-10-08T10:00:00Z<!--retain-->", b"2026-10-08T10:00:00Z<?retain content?>",
                        b"<![CDATA[2026-10-08T10:00:00Z]]>", b"<Nested>2026-10-08T10:00:00Z</Nested>"):
            with self.subTest(content=content), self.assertRaises(AssertionError):
                native_payload(b"<Document><DocumentInfo><Created>" + content + b"</Created></DocumentInfo></Document>")
        for xml in (b'<Document><DocumentInfo><Created custom="payload">2026-10-08T10:00:00Z</Created></DocumentInfo></Document>',
                    b'<Document><DocumentInfo/><DocumentInfo/></Document>', b'<!DOCTYPE Document [<!ENTITY a "x">]><Document>&a;</Document>'):
            with self.assertRaises(AssertionError): native_payload(xml)

    def test_compile_requires_complete_hmi_owners_and_native_success(self):
        result = {"name": "HMI_A", "errorCount": 0, "state": "Success", "messages": [], "compileTargetType": "Siemens.Engineering.HW.DeviceImpl"}
        profile = {"nativeBefore": [{"name": "HMI_A", "type": "Siemens.Engineering.HmiUnified.HmiSoftware"}], "compile": [result]}
        self.assertEqual(native_compile_gate(profile, "compile")["status"], "passed")
        result["state"] = "Information"
        self.assertEqual(native_compile_gate(profile, "compile")["status"], "passed")
        result["state"] = "Success"
        for changes in ({"name": "PLC_A"}, {"compileTargetType": "Siemens.Engineering.SW.PlcSoftware"}, {"errorCount": False},
                        {"state": "Error"}, {"errorCount": None}, {"error": {"message": "unavailable"}}):
            candidate = copy.deepcopy(profile); candidate["compile"][0].update(changes)
            with self.subTest(changes=changes): self.assertEqual(native_compile_gate(candidate, "compile")["status"], "failed")
        profile["nativeBefore"].append({"name": "HMI_B", "type": "Siemens.Engineering.HmiUnified.HmiSoftware"})
        self.assertEqual(native_compile_gate(profile, "compile")["status"], "failed")

    def test_positive_baseline_compile_and_lifecycle_are_separate(self):
        result = {"name": "HMI", "errorCount": 0, "state": "Success", "messages": [], "compileTargetType": "Siemens.Engineering.Hmi.HmiTarget"}
        profile = {"fixtureMode": "positive", "deliberateInvalidReferences": False,
                   "nativeBefore": [{"name": "HMI", "type": "Siemens.Engineering.Hmi.HmiTarget"}], "compileBeforeTrial": [result], "compile": [result]}
        gates = import_native_evidence(profile)
        self.assertEqual(gates["cleanBaselineCompile"]["status"], "passed")
        self.assertEqual(gates["reopenedCompile"]["status"], "unverified")
        self.assertEqual(gates["recoveredCompile"]["status"], "unverified")
        profile["fixtureMode"] = "negative"
        self.assertEqual(import_native_evidence(profile)["cleanBaselineCompile"]["status"], "unverified")

    def test_compile_rejects_error_messages_under_successful_zero_error_summary(self):
        result = {"name": "HMI", "errorCount": 0, "state": "Success", "compileTargetType": "Siemens.Engineering.HW.DeviceImpl",
                  "messages": [{"state": "Information", "description": "owner", "children": [
                      {"state": "Error", "description": "The entered password confirmation does not match the password.", "children": []}]}]}
        profile = {"nativeBefore": [{"name": "HMI", "type": "Siemens.Engineering.HmiUnified.HmiSoftware"}], "compile": [result]}
        observed = native_compile_gate(profile, "compile")
        self.assertEqual(observed["status"], "failed")
        self.assertFalse(observed["cleanDiagnostics"])
        self.assertEqual(len(observed["diagnostics"][0]["errors"]), 1)
        for messages in (None, {}, [None], [{"state": "Success"}], [{"state": "Unknown", "children": []}]):
            candidate = copy.deepcopy(profile); candidate["compile"][0]["messages"] = messages
            with self.subTest(messages=messages): self.assertEqual(native_compile_gate(candidate, "compile")["status"], "failed")
        result["messages"][0]["children"][0]["state"] = "Warning"
        self.assertEqual(native_compile_gate(profile, "compile")["status"], "passed")

    def test_compile_rejects_incomplete_capture_and_legacy_limit_saturation(self):
        message = {"state": "Information", "description": "native diagnostic", "children": []}
        result = {"name": "HMI", "errorCount": 0, "state": "Success", "messages": [],
                  "compileTargetType": "Siemens.Engineering.HmiUnified.HmiSoftware"}
        profile = {"nativeBefore": [{"name": "HMI", "type": "Siemens.Engineering.HmiUnified.HmiSoftware"}], "compile": [result]}
        for changes in ({"messagesComplete": False}, {"messagesComplete": "true"},
                        {"messages": [{"diagnosticCaptureIncomplete": True, "reason": "budget exhausted"}]},
                        {"messages": [{**message, "diagnosticCaptureIncomplete": True}]},
                        {"messages": [{**message, "truncated": True}]},
                        {"messages": [copy.deepcopy(message) for _ in range(500)]},
                        {"messages": [{**message, "children": [copy.deepcopy(message) for _ in range(500)]}]}):
            candidate = copy.deepcopy(profile); candidate["compile"][0].update(changes)
            with self.subTest(changes=list(changes)):
                self.assertEqual(native_compile_gate(candidate, "compile")["status"], "failed")
        result["messages"] = [copy.deepcopy(message) for _ in range(500)]
        result["messagesComplete"] = True
        self.assertEqual(native_compile_gate(profile, "compile")["status"], "passed")
        result["messages"].append({"diagnosticCaptureIncomplete": True})
        self.assertEqual(native_compile_gate(profile, "compile")["status"], "failed")

    def test_compile_depth_bound_is_not_silently_accepted(self):
        result = {"name": "HMI", "errorCount": 0, "state": "Success", "messages": [], "messagesComplete": True,
                  "compileTargetType": "Siemens.Engineering.HmiUnified.HmiSoftware"}
        profile = {"nativeBefore": [{"name": "HMI", "type": "Siemens.Engineering.HmiUnified.HmiSoftware"}], "compile": [result]}
        children = result["messages"]
        for _ in range(32):
            children.append({"state": "Information", "children": []})
            children = children[0]["children"]
        self.assertEqual(native_compile_gate(profile, "compile")["status"], "passed")
        children.append({"state": "Information", "children": []})
        self.assertEqual(native_compile_gate(profile, "compile")["status"], "failed")

    def test_captured_reassessment_recomputes_compile_without_erasing_original(self):
        result = {"name": "HMI", "errorCount": 0, "state": "Success", "compileTargetType": "Siemens.Engineering.HW.DeviceImpl",
                  "messages": [{"state": "Error", "description": "native password diagnostic", "children": []}]}
        profile = {"profile": "unified-comfort", "fixtureMode": "positive", "deliberateInvalidReferences": False,
                   "nativeBefore": [{"name": "HMI", "type": "Siemens.Engineering.HmiUnified.HmiSoftware"}],
                   **{field: [result] for field in ("compile", "compileBeforeTrial", "compileReopened", "compileRecovered")}}
        captured = {"profiles": [{"profile": "unified-comfort", "gates": {name: gate("passed", "original oracle") for name in GATES},
                                 "inspections": [{"target": {"name": "HMI", "family": "unified", "objectPath": "/hmi"}, "section": "tags", "result": {}}]}]}
        with patch("qualify_hmi.compare_section", return_value={"status": "passed"}), patch("qualify_hmi.qualify_relationships"), patch("qualify_hmi.qualify_alarm_references"):
            actual = reassess_captured({"profiles": [profile]}, captured)[0]
        for name in ("compile", "cleanBaselineCompile", "reopenedCompile", "recoveredCompile"):
            self.assertEqual(actual["gates"][name]["status"], "failed")
            self.assertEqual(actual["originalGates"][name]["status"], "passed")
        self.assertEqual(captured["profiles"][0]["gates"]["compile"]["status"], "passed")

    def test_semantic_snapshot_keeps_errors_and_unknown_content(self):
        original = {"attributes": {"Name": "A"}, "export": {"path": "one"}, "error": "blocked", "unknown": [1]}
        other = copy.deepcopy(original); other["export"] = {"path": "two"}
        self.assertEqual(semantic_snapshot(original), semantic_snapshot(other))
        other["unknown"] = [2]
        self.assertNotEqual(semantic_snapshot(original), semantic_snapshot(other))

    def test_failed_section_is_not_successful_empty_inventory(self):
        native = {"sections": {"Connections": []}}
        with self.assertRaises(AssertionError):
            compare_section(native, "connections", "classic", {"items": [], "lastPage": {"coverage": {"status": "failed"}}})

    def test_duplicate_native_names_retain_counts(self):
        native = {"sections": {"Screens": [{"nativeType": "Screen", "attributes": {"Name": "A"}}, {"nativeType": "Screen", "attributes": {"Name": "A"}}]}}
        observed = {"items": [{"nativeType": "Screen", "name": "A", "properties": {"Name": "A"}}], "lastPage": {"coverage": {"status": "complete"}}}
        with self.assertRaises(AssertionError): compare_section(native, "screens", "unified", observed)

    def test_duplicate_names_cannot_reuse_one_matching_scalar_row(self):
        native = {'sections': {'Screens': [
            {'nativeType': 'HmiScreen', 'attributes': {'Name': 'Same', 'Width': 10}},
            {'nativeType': 'HmiScreen', 'attributes': {'Name': 'Same', 'Width': 10}}]}}
        observed = {'items': [
            {'nativeType': 'HmiScreen', 'name': 'Same', 'properties': {'Name': 'Same', 'Width': 10}},
            {'nativeType': 'HmiScreen', 'name': 'Same', 'properties': {'Name': 'Same', 'Width': 999}}],
            'lastPage': {'coverage': {'status': 'complete'}}}
        with self.assertRaisesRegex(AssertionError, 'scalar'): compare_section(native, 'screens', 'unified', observed)
        observed['items'][1]['properties']['Width'] = 10
        self.assertEqual(compare_section(native, 'screens', 'unified', observed)['status'], 'passed')

    def test_scalar_null_requires_presence_and_boolean_cannot_alias_number(self):
        for expected, actual in [(None, {}), (True, {'Enabled': 1}), (False, {'Enabled': 0})]:
            with self.subTest(expected=expected, actual=actual):
                native = {'sections': {'Screens': [{'nativeType': 'HmiScreen', 'attributes': {'Name': 'A', 'Enabled': expected}}]}}
                observed = {'items': [{'nativeType': 'HmiScreen', 'name': 'A', 'properties': {'Name': 'A', **actual}}],
                            'lastPage': {'coverage': {'status': 'complete'}}}
                with self.assertRaisesRegex(AssertionError, 'scalar'): compare_section(native, 'screens', 'unified', observed)
                observed['items'][0]['properties']['Enabled'] = expected
                self.assertEqual(compare_section(native, 'screens', 'unified', observed)['status'], 'passed')

    def test_duplicate_scalar_matching_reassigns_ambiguous_rows_without_order_dependence(self):
        from qualify_hmi import match_native_scalars
        from itertools import permutations
        originals = [{'attributes': {'Name': 'A'}}, {'attributes': {'Name': 'A', 'Width': 10}},
                     {'attributes': {'Name': 'A', 'Height': 20}}]
        actual = [{'properties': {'Name': 'A', 'Width': 10, 'Height': 20}},
                  {'properties': {'Name': 'A', 'Width': 10, 'Height': 30}},
                  {'properties': {'Name': 'A', 'Width': 99, 'Height': 99}}]
        for before in permutations(originals):
            for after in permutations(actual):
                self.assertTrue(match_native_scalars(before, after))

    def test_unified_flat_view_requires_exact_table_scope_and_attributes(self):
        def tag(table, address=""):
            return {"nativeType": "HmiTag", "attributes": {"Name": "Shared", "TagTableName": table, "Address": address}}
        native = {"sections": {"Tags": [tag("A"), tag("B")], "TagTables": [
            {"nativeType": "HmiTagTable", "attributes": {"Name": "A"}, "Tags": [tag("A")]},
            {"nativeType": "HmiTagTable", "attributes": {"Name": "B"}, "Tags": [tag("B")]}]}}
        self.assertEqual(len(native_section_entries(native, "tags", "unified")), 4)
        native["sections"]["Tags"][0]["attributes"]["Address"] = "different"
        self.assertEqual(len(native_section_entries(native, "tags", "unified")), 5)
        native["sections"]["TagTables"][0]["attributes"]["Name"] = "C"
        self.assertEqual(len(native_section_entries(native, "tags", "unified")), 5)

    def test_classic_screens_span_every_role_and_tolerate_older_reports(self):
        def screen(kind, name="Overview_A"):
            return {"nativeType": "Siemens.Engineering.Hmi.Screen." + kind, "attributes": {"Name": name}}
        native = {"sections": {
            "ScreenFolder": {"nativeType": "Siemens.Engineering.Hmi.Screen.ScreenSystemFolder", "Screens": [screen("Screen")]},
            "ScreenTemplateFolder": {"nativeType": "Siemens.Engineering.Hmi.Screen.ScreenTemplateSystemFolder", "ScreenTemplates": [screen("ScreenTemplate")]},
            "ScreenPopupFolder": {"nativeType": "Siemens.Engineering.Hmi.Screen.ScreenPopupSystemFolder", "ScreenPopups": [screen("ScreenPopup")]},
            "ScreenSlideinFolder": {"nativeType": "Siemens.Engineering.Hmi.Screen.ScreenSlideinSystemFolder", "ScreenSlideins": [screen("ScreenSlidein", "Top")]},
            "ScreenGlobalElements": screen("ScreenGlobalElements", "Global"), "ScreenOverview": None}}
        kinds = sorted(row["nativeType"].rsplit(".", 1)[-1] for row in native_section_entries(native, "screens", "classic"))
        self.assertEqual(kinds, ["Screen", "ScreenGlobalElements", "ScreenPopup", "ScreenSlidein", "ScreenTemplate"])
        for key in ("ScreenGlobalElements", "ScreenOverview"):
            del native["sections"][key]
        self.assertEqual(len(native_section_entries(native, "screens", "classic")), 4)

    def test_item_geometry_must_match_precisely(self):
        native = {"ScreenItems": [{"nativeType": "Rectangle", "attributes": {"Name": "A", "Left": 31, "Width": 120}}]}
        observed = {"items": [{"nativeType": "Rectangle", "name": "A", "properties": {"Name": "A", "Left": 30, "Width": 120}}], "lastPage": {"coverage": {"status": "complete"}}}
        with self.assertRaisesRegex(AssertionError, "scalar"):
            compare_screen_items(native, "unified", observed)

    def test_item_unavailable_cannot_pass_empty_native_inventory(self):
        with self.assertRaisesRegex(AssertionError, "complete"):
            compare_screen_items({"ScreenItems": []}, "unified", {"items": [], "lastPage": {"coverage": {"status": "unavailable"}}})

    def test_alarm_unsupported_does_not_imply_unified_support(self):
        result = compare_section({}, "alarms", "classic", {"items": [], "lastPage": {"coverage": {"status": "unsupported"}}})
        self.assertEqual(result["status"], "unsupported")
        with self.assertRaises(AssertionError): compare_section({}, "alarms", "classic", {"items": [], "lastPage": {"coverage": {"status": "complete"}}})

    def test_archive_escape_rejected_before_any_native_operation(self):
        with tempfile.TemporaryDirectory() as directory:
            profile = {"profile": "classic-comfort", "selectedTarget": {"typeIdentifier": "actual"}, "archive": {"path": str(Path(directory).parent / "outside.zap21")}}
            with self.assertRaisesRegex(AssertionError, "escapes"): validate_fixture(profile, Path(directory) / "report.json")


if __name__ == "__main__":
    unittest.main()
