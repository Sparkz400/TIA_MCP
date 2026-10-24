"""Offline-only force coordinator tests. All native/process/UI fixtures are synthetic."""
from copy import deepcopy
from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch, Mock

import qualify_force_capture as force


class ForceCoordinatorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="synthetic-force-coordinator-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def metadata(self):
        return {"force": {"name": "Force table", "oracleRows": []}, "peers": {
            "languages": {"active": ["de-DE", "en-GB"], "editing": "de-DE", "reference": "de-DE"},
            "hardware": [{"path": "ScratchStation/ExplorerConfiguredIoFixture/ExplorerConfiguredIoFixture", "addresses": [
                {"ioType": io, "startAddress": 0, "lengthBits": length, "ownerCount": 1, "allOwnersMatchPlc": True}
                for io, length in (("Input", 256), ("Output", 192))]}]}}

    def reviewed(self):
        value = force.load(force.REPO / "fixtures/watch-tables/planner/force-offline-oracle.json")
        value["projectPath"] = str(force.SOURCE_PROJECT)
        value.update(reviewStatus="reviewed", activeLanguages=["de-DE", "en-GB"])
        for row in value["rows"]: row["comments"]["en-GB"] = "Synthetic offline test input"
        return value

    def test_all_actual_cultures_and_ranges_are_reviewed(self):
        result = force.validate_reviewed_oracle(self.reviewed(), self.metadata())
        self.assertTrue(result["reviewed"] and result["noTagsCreated"])

    def test_template_true_intention_or_wrong_stored_value_refused(self):
        for change in ({"forceIntention": True}, {"forceIntention": None}, {"displayFormat": "Undef"}, {"forceValue": "TRUE"}, {"name": "manufactured tag"}):
            value = self.reviewed(); value["rows"][0].update(change)
            with self.subTest(change=change), self.assertRaises(ValueError): force.validate_reviewed_oracle(value, self.metadata())
        value = self.reviewed(); value["reviewStatus"] = "requiresReview"
        with self.assertRaises(ValueError): force.validate_reviewed_oracle(value, self.metadata())

    def test_missing_culture_comment_refused(self):
        value = self.reviewed(); value["rows"][1]["comments"].pop("en-GB")
        with self.assertRaises(ValueError): force.validate_reviewed_oracle(value, self.metadata())
        value = self.reviewed(); value["activeLanguages"] = ["de-DE"]
        with self.assertRaises(ValueError): force.validate_reviewed_oracle(value, self.metadata())

    def test_unowned_or_out_of_range_peripheral_refused(self):
        for change in ({"ownerCount": 0}, {"allOwnersMatchPlc": False}, {"lengthBits": 0}, {"startAddress": 99}):
            metadata = self.metadata(); metadata["peers"]["hardware"][0]["addresses"][1].update(change)
            with self.subTest(change=change), self.assertRaises(ValueError): force.validate_reviewed_oracle(self.reviewed(), metadata)
        value = self.reviewed(); value["signals"][0]["byteOffset"] = 100
        value["signals"][0]["forceAddress"] = value["rows"][0]["address"] = "%I100.0:P"
        with self.assertRaises(ValueError): force.validate_reviewed_oracle(value, self.metadata())

    def test_unreviewed_address_normalization_and_duplicate_binding_refused(self):
        value = self.reviewed(); value["rows"][0]["address"] = value["signals"][0]["forceAddress"] = "I0.0:P"
        with self.assertRaises(ValueError): force.validate_reviewed_oracle(value, self.metadata())
        value = self.reviewed(); value["signals"][1] = deepcopy(value["signals"][0])
        with self.assertRaises(ValueError): force.validate_reviewed_oracle(value, self.metadata())

    def test_complete_file_manifest_excludes_nothing_and_reports_every_delta(self):
        workspace = self.root / "workspace"; workspace.mkdir()
        for name, data in (("project.ap21", b"native"), ("unknown.vendor", b"unknown"), ("log.txt", b"log")):
            (workspace / name).write_bytes(data)
        before = force.workspace_manifest(workspace)
        (workspace / "unknown.vendor").write_bytes(b"changed")
        (workspace / "log.txt").unlink()
        (workspace / "new.bin").write_bytes(b"new")
        delta = force.manifest_delta(before, force.workspace_manifest(workspace))
        self.assertEqual(before["exclusions"], [])
        self.assertEqual(delta["changed"], ["unknown.vendor"])
        self.assertEqual(delta["added"], ["new.bin"])
        self.assertEqual(delta["removed"], ["log.txt"])
        self.assertFalse(delta["unchangedBytes"])
        self.assertTrue(delta["binaryManifestDeltaIsNotAnUnchangedPeerClaim"])

    def observation(self, path, pid):
        return {"projectPath": str(path), "processId": pid, "nativeProcessStartTimeUtc": "synthetic-" + str(pid),
                "snapshot": self.metadata(), "forceTimestampMaskedSha256": "baseline"}

    def test_restored_copy_requires_new_path_fresh_process_and_all_native_peers(self):
        before = self.observation(self.root / "original.ap21", 100)
        restored = self.observation(self.root / "restored.ap21", 101)
        result = force.validate_restoration(before, restored)
        self.assertTrue(result["restoredCheckpointCopyVerified"])
        self.assertFalse(result["originalProjectOverwritten"] or result["forceEditingQualified"] or result["onlineForcingQualified"])
        for change in ({"projectPath": before["projectPath"]}, {"processId": 100, "nativeProcessStartTimeUtc": "synthetic-100"},
                       {"forceTimestampMaskedSha256": "changed"}):
            with self.subTest(change=change), self.assertRaises(ValueError): force.validate_restoration(before, dict(restored, **change))
        bad = deepcopy(restored); bad["snapshot"]["peers"]["languages"]["active"].append("nl-NL")
        with self.assertRaises(ValueError): force.validate_restoration(before, bad)

    def ui_proof(self):
        evidence = self.root / "synthetic-ui-record.txt"; evidence.write_text("Synthetic unit-test receipt, not a native UI capture.")
        live = {"native": {"pid": 100}, "nativeProcessStartTimeUtc": "synthetic-birth", "oracleSha256": "reviewed"}
        proof = {"savedViaUi": True, "allForceSelectionsUnchecked": True, "projectPath": str(force.SOURCE_PROJECT),
                 "processId": 100, "nativeProcessStartTimeUtc": "synthetic-birth", "oracleSha256": "reviewed",
                 "computerUseEvidence": [force.ref(evidence)]}
        return live, proof

    def test_capture_request_binds_ui_pid_birth_oracle_and_hashed_evidence(self):
        live, proof = self.ui_proof(); force.validate_ui_save_evidence(live, proof)
        for change in ({"savedViaUi": False}, {"allForceSelectionsUnchecked": False}, {"processId": 999},
                       {"nativeProcessStartTimeUtc": "other"}, {"oracleSha256": "other"}, {"computerUseEvidence": []},
                       {"projectPath": str(self.root / "foreign.ap21")}):
            with self.subTest(change=change), self.assertRaises(ValueError): force.validate_ui_save_evidence(live, dict(proof, **change))
        Path(proof["computerUseEvidence"][0]["path"]).write_text("changed")
        with self.assertRaises(RuntimeError): force.validate_ui_save_evidence(live, proof)

    def synthetic_processes(self, native_code):
        tracker = object.__new__(force.Processes)
        tracker.folder = self.root; tracker.errors = []; tracker.stop = Mock(); tracker.thread = Mock()
        tracker.forced = False
        tracker.capture_incomplete = False
        tracker.thread.is_alive.return_value = False
        tracker.entries = {}
        for pid, role, code in ((100, "anchor", 0), (101, "native", native_code)):
            name = "ForceFixtureEvidence.exe" if role == "anchor" else "Siemens.Automation.Portal.exe"
            info = {"pid": pid, "created": 1700000000, "exe": str(self.root / name), "name": name, "parentPid": 99 if role == "anchor" else 100}
            proof = {"processId": pid, "completed": True, "handleCaptured": True, "handleClosed": True, "processExitVerified": True,
                     "exitCode": code, "birthAndImageVerified": True, "sameOsHandleRetained": True, "imagePath": info["exe"],
                     "nativeProcessStartTimeUtc": "2023-11-14T22:13:20.0000000Z"}
            watcher = Mock(); watcher.wait_for_exit.return_value = proof
            watcher.evidence = dict(proof, handleClosed=False)
            tracker.entries[pid] = {"role": role, "identity": info, "ancestry": [], "handle": watcher}
        return tracker

    def test_sdk_disposal_retains_terminal_code_but_never_claims_normal_lifecycle(self):
        tracker = self.synthetic_processes(23)
        result = tracker.finish(explicit_native_dispose=True)
        self.assertTrue(result["completed"] and result["explicitNativeTermination"])
        self.assertFalse(result["normalLifecyclePassed"] or result["coordinatorForcedTermination"])
        self.assertEqual(result["exits"][1]["proof"]["exitCode"], 23)

    def test_ui_native_selection_refuses_a_second_captured_native(self):
        tracker = self.synthetic_processes(0); tracker.collect = Mock()
        extra = dict(tracker.entries[101], identity=dict(tracker.entries[101]["identity"], pid=102))
        tracker.entries[102] = extra
        with self.assertRaisesRegex(ValueError, "sole owned native"): tracker.native(101)

    def test_nonzero_native_exit_cannot_pass_normal_host_lifecycle(self):
        result = self.synthetic_processes(23).finish(explicit_native_dispose=False)
        self.assertFalse(result["completed"] or result["normalLifecyclePassed"])

    def test_unresolved_ui_reservation_is_never_cleared_or_stolen(self):
        folder = self.root / "isolated-force-reservations"; folder.mkdir()
        key = force.resource_key("project", self.root / "owned.ap21")
        intent = folder / "sample-intent.json"
        force.save(intent, {"kind": "isolated-force-ui-reservation", "productionMutationAdmission": False,
                            "resources": [{"kind": "project", "path": str(self.root / "owned.ap21")}], "resourceKeys": [key]})
        original = intent.read_bytes()
        with self.assertRaisesRegex(ValueError, "Unresolved force UI reservation"):
            force.scan_force_reservations([self.root], [key])
        self.assertEqual(intent.read_bytes(), original)
        continuing = force.scan_force_reservations([self.root], [key], force.ref(intent))
        self.assertTrue(continuing[0]["sameOwnedWorkflowContinuing"])
        force.save(folder / "sample-outcome.json", {"intentSha256": force.sha(intent), "completed": True})
        with self.assertRaises(KeyError): force.scan_force_reservations([self.root], [key])

    def test_completed_reservation_requires_full_chain_and_raw_reverification(self):
        intent = self.root / "sample-intent.json"; outcome = self.root / "sample-outcome.json"
        force.save(intent, {"planSha256": "synthetic-plan", "runRoot": str(self.root)})
        payload = self.root / "payload"; payload.mkdir(); (payload / "raw.txt").write_text("synthetic only")
        manifest = force.save(self.root / "phase-manifest.json", force.workspace_manifest(payload))
        common = {"completed": True, "planSha256": "synthetic-plan", "evidence": [], "phaseManifest": manifest}
        final = force.save(self.root / "restored.json", dict(common, restorationVerified=True, freshOriginalReadbackVerified=True, fullPeerRestorationVerified=True))
        verified = force.save(self.root / "verified.json", dict(common, populatedForceCaptureVerified=True))
        authored = force.save(self.root / "author.json", dict(common, uiReservation=force.ref(intent)))
        captured = force.save(self.root / "capture.json", common)
        final_store_intent = force.save(self.root / "synthetic-store-intent.json", {"synthetic": True})
        final_store_close = force.save(self.root / "synthetic-store-close.json", {"allClosed": True})
        force.save(outcome, {"intentSha256": force.sha(intent), "completed": True, "restorationResult": final,
                            "populatedVerification": verified, "authoringResult": authored, "freshCaptureResult": captured,
                            "finalStoreIntent": final_store_intent, "finalStoreClose": final_store_close})
        with patch.object(force, "independently_recheck_force_chain") as recheck:
            self.assertTrue(force.completed_force_reservation(intent, outcome)); recheck.assert_called_once()
        with patch.object(force, "independently_recheck_force_chain", side_effect=ValueError("raw mismatch")):
            with self.assertRaisesRegex(ValueError, "raw mismatch"): force.completed_force_reservation(intent, outcome)

    def test_fullpeer_comparison_excludes_only_exact_force_target_and_never_vci_settings(self):
        peer = {"watchForceTables": [{"kind": "force", "group": "root", "name": "Force table", "nativeXml": "old"},
                                   {"kind": "watch", "group": "root", "name": "Watch", "nativeXml": "peer"}], "blocks": ["unchanged"]}
        updated = deepcopy(peer); updated["watchForceTables"][0]["nativeXml"] = "new"
        state = {"projectLanguageImportOptions": "None", "workspaces": []}
        with (patch.object(force, "fixture_snapshot", side_effect=[peer, updated]),
              patch.object(force, "fixture_vci", side_effect=[state, dict(state, projectLanguageImportOptions="Changed")])):
            with self.assertRaisesRegex(ValueError, "VCI workspace/mapping/settings changed"):
                force.verify_fixture_peers(self.root, self.root, "Force table", True)
        updated["blocks"] = ["changed"]
        with patch.object(force, "fixture_snapshot", side_effect=[peer, updated]):
            with self.assertRaises(ValueError): force.verify_fixture_peers(self.root, self.root, "Force table", True)

    def cleanup_tracker(self):
        tracker = self.synthetic_processes(0)
        tracker.anchor = tracker.entries[100]["identity"]
        for item in tracker.entries.values(): item["handle"].api.wait.return_value = 258
        return tracker

    def test_invalid_cleanup_command_keeps_original_handles_until_natural_exit(self):
        tracker = self.cleanup_tracker()
        force.save(self.root / "process-cleanup-command-synthetic.json", {"command": "wrong"})
        with (patch.object(tracker, "all_terminal", side_effect=[False, False, True]),
              patch.object(force.time, "sleep"), patch.object(force, "identity", return_value={"pid": 999})):
            tracker.await_terminal("synthetic timeout")
        self.assertEqual(len(list(self.root.glob("cleanup-refusal-*.json"))), 1)
        self.assertFalse(tracker.forced)
        for entry in tracker.entries.values():
            entry["handle"].close.assert_not_called(); entry["handle"].api.terminate.assert_not_called()

    def test_explicit_cleanup_uses_same_retained_handles_and_records_forced_failure(self):
        tracker = self.cleanup_tracker()
        calls = []
        def terminal():
            calls.append(1)
            if len(calls) == 2:
                force.save(self.root / "process-cleanup-command-synthetic.json", {"command": "terminate-exact-owned-handles",
                    "liveSha256": force.sha(self.root / "cleanup-live.json"), "anchor": tracker.anchor, "reason": "synthetic explicit cleanup"})
            return len(calls) >= 3
        with (patch.object(tracker, "all_terminal", side_effect=terminal), patch.object(force.time, "sleep"),
              patch.object(force, "identity", return_value={"pid": 999})):
            tracker.await_terminal("synthetic timeout")
        self.assertTrue(tracker.forced)
        for entry in tracker.entries.values():
            entry["handle"].api.terminate.assert_called_once_with(entry["handle"]._handle)
            entry["handle"].close.assert_not_called()

    def test_failed_prior_phase_blocks_dependent_work(self):
        coordinator = object.__new__(force.Coordinator); coordinator.root = self.root; coordinator.plan_sha = "plan"
        (self.root / "author-ui").mkdir()
        force.save(self.root / "author-ui/result.json", {"completed": False, "planSha256": "plan", "evidence": []})
        with self.assertRaisesRegex(ValueError, "Prior phase incomplete"):
            coordinator.previous("author-ui")

    def test_raw_phase_file_tampering_refused_before_dependent_native_work(self):
        folder = self.root / "capture"; folder.mkdir()
        raw = folder / "native-raw.xml"; raw.write_text("synthetic exact raw evidence")
        manifest = force.save(folder / "phase-file-manifest.json", force.workspace_manifest(folder))
        force.save(folder / "result.json", {"completed": True, "planSha256": "plan", "evidence": [manifest], "phaseManifest": manifest})
        coordinator = object.__new__(force.Coordinator); coordinator.root = self.root; coordinator.plan_sha = "plan"
        self.assertTrue(coordinator.previous("capture")["completed"])
        raw.write_text("changed")
        with self.assertRaises(RuntimeError): coordinator.previous("capture")

    def test_initial_source_manifest_refuses_drift_before_native_dispatch(self):
        source = self.root / "controller.py"; dependency = self.root / "dependency.py"
        source.write_text("synthetic-controller"); dependency.write_text("synthetic-dependency")
        manifest = self.root / "source-manifest.json"
        force.save(manifest, {"kind": "reviewed-force-controller-sources", "nativeInvoked": False,
                              "files": [force.ref(source), force.ref(dependency)]})
        with patch.object(force, "CONTROLLER_SOURCES", (source, dependency)):
            self.assertEqual(len(force.reviewed_controller_sources(manifest)), 2)
            dependency.write_text("drifted bytes")
            with patch.object(force.subprocess, "Popen", side_effect=AssertionError("native dispatch")):
                with self.assertRaises(RuntimeError): force.bind_package(manifest)

    def test_vci_package_file_mutation_cannot_pass_with_stable_native_mapping(self):
        workspace = self.root / "vci-workspace"; workspace.mkdir()
        (workspace / "fixture.xml").write_text("synthetic-original")
        before = force.workspace_manifest(workspace)
        self.assertTrue(force.unchanged_workspace_payloads([before], [force.workspace_manifest(workspace)])[0]["unchangedBytes"])
        (workspace / "fixture.xml").write_text("synthetic-changed")
        with self.assertRaisesRegex(ValueError, "package files changed"):
            force.unchanged_workspace_payloads([before], [force.workspace_manifest(workspace)])

    def test_pre_host_overlay_refusal_happens_before_client_creation(self):
        owner = Mock(); owner.pre_host_admission.side_effect = ValueError("competing reservation")
        host = force.HostSession(owner, self.root)
        with patch.object(force, "Client", side_effect=AssertionError("Host dispatched")) as client:
            with self.assertRaisesRegex(ValueError, "competing reservation"): host.start()
            client.assert_not_called()

    def test_actual_start_constructor_cancellation_retains_partial_client_before_outer_close(self):
        process = Mock(pid=100)
        class PartialClient:
            def __init__(self, *args):
                self.process = process; self.errors = []
                raise KeyboardInterrupt("synthetic reader thread start interruption")
        owner = Mock(); owner.args.timeout = 30; owner.root = self.root
        host = force.HostSession(owner, self.root)
        tracker = Mock(); tracker.errors = []; tracker.capture_complete.return_value = False
        def hold(retained, folder, partial, reason):
            self.assertIs(retained, process); self.assertIs(host.client.process, process)
            self.assertIs(partial, tracker); tracker.finish.assert_not_called()
            raise RuntimeError("synthetic partial-client hold observed")
        owner.hold_uncaptured_startup.side_effect = hold
        with (patch.object(force, "Client", PartialClient), patch.object(force, "Processes", return_value=tracker),
              patch.object(force.psutil, "Process", return_value=object()), patch.object(force, "identity", return_value={"pid": 100}),
              patch.object(force, "graceful_close") as close):
            with self.assertRaisesRegex(RuntimeError, "partial-client hold observed"):
                try: host.start()
                finally: host.close()
        close.assert_not_called(); owner.hold_uncaptured_startup.assert_called_once()

    def test_host_constructor_without_returned_popen_holds_unknown(self):
        class PartialClient:
            def __init__(self, *args): raise OSError("synthetic constructor outcome unknown")
        owner = Mock(); owner.args.timeout = 30; owner.root = self.root
        owner.hold_uncaptured_startup.side_effect = RuntimeError("synthetic unknown constructor hold")
        host = force.HostSession(owner, self.root)
        with patch.object(force, "Client", PartialClient), patch.object(force, "Processes") as tracker:
            with self.assertRaisesRegex(RuntimeError, "unknown constructor hold"): host.start()
        tracker.assert_not_called()
        self.assertIsNone(owner.hold_uncaptured_startup.call_args.args[0])

    def test_actual_checkpoint_timeout_and_cancellation_hold_store_before_host_close(self):
        """Real checkpoint -> run_process -> await_terminal -> finally orchestration."""
        source = self.root / "source"; source.mkdir()
        project = source / "TiaMcpScratch_synthetic.ap21"; project.write_text("synthetic native bytes")
        ownership = self.root / "ownership.json"; ownership.write_text("{}")
        binary = self.root / "synthetic-helper.exe"; binary.write_text("never executable")
        run_root = self.root / "run"; run_root.mkdir()
        force.save(run_root / "recovery-gate-verified.json", {"synthetic": True})
        folder = run_root / "checkpoint"; folder.mkdir()
        run = object.__new__(force.Coordinator)
        run.root = run_root; run.folder = folder; run.evidence = []; run.result = {}
        run.args = SimpleNamespace(timeout=30, ui_timeout_minutes=1)
        run.package = {"fixture": {"binary": force.ref(binary), "sources": []}}
        run.check_windows = Mock()
        trace = []; state = {"active": False, "hostClosed": False, "terminal": False}
        class Window:
            def __enter__(self):
                state["active"] = True; trace.append("store-held")
                force.save(folder / "store-window-intent.json", {"synthetic": True})
                return SimpleNamespace(evidence={}, keys=[])
            def __exit__(self, *args):
                self_test.assertTrue(state["terminal"])
                state["active"] = False; trace.append("store-released")
        run.window = lambda *args, **kwargs: Window()
        self_test = self
        class Host:
            pid = 7
            native = {"handle": SimpleNamespace(evidence={"nativeProcessStartTimeUtc": "synthetic"})}
            def __init__(self, *args): pass
            def start(self, *args): trace.append("host-start")
            def status(self, *args): return {"synthetic": True}
            def call(self, *args): return {"synthetic": True}
            def close(self):
                self_test.assertFalse(state["active"]); self_test.assertTrue(state["terminal"])
                state["hostClosed"] = True; trace.append("host-close")
                return force.save(folder / "synthetic-host-close.json", {"completed": True})
        process = Mock(pid=100, returncode=None)
        process.poll.side_effect = lambda: process.returncode
        tracker = self.synthetic_processes(0); tracker.folder = folder / "helper"
        tracker.entries.pop(101); tracker.anchor = tracker.entries[100]["identity"]
        tracker.collect = Mock()
        checks = []
        def terminal():
            self.assertTrue(state["active"]); self.assertFalse(state["hostClosed"])
            checks.append(1)
            if len(checks) < 3: return False
            state["terminal"] = True; process.returncode = 0; trace.append("all-original-handles-terminal"); return True
        def interrupt_sleep(seconds):
            self.assertTrue(state["active"]); self.assertFalse(state["hostClosed"])
            trace.append("cancel-" + str(len(checks)))
            if len(checks) == 1: raise KeyboardInterrupt()
            raise SystemExit(2)
        with (patch.object(force, "SOURCE_WORKSPACE", source), patch.object(force, "SOURCE_PROJECT", project),
              patch.object(force, "OWNERSHIP", ownership), patch.object(force, "HOST", binary), patch.object(force, "WORKER", binary),
              patch.object(force, "HostSession", Host), patch.object(force, "inspection_payload", side_effect=lambda x:x),
              patch.object(force.subprocess, "Popen", return_value=process), patch.object(force, "Processes", return_value=tracker),
              patch.object(force, "identity", return_value=tracker.anchor), patch.object(force.psutil, "Process", return_value=object()),
              patch.object(tracker, "all_terminal", side_effect=terminal), patch.object(force.time, "monotonic", side_effect=[0, 999]),
              patch.object(force.time, "sleep", side_effect=interrupt_sleep)):
            with self.assertRaisesRegex(ValueError, "Helper or exact cleanup failed"): run.checkpoint()
        self.assertEqual(trace, ["host-start", "store-held", "cancel-1", "cancel-2", "all-original-handles-terminal", "store-released", "host-close"])
        proof = force.load(folder / "helper/process-exits.json")
        self.assertFalse(proof["completed"])
        self.assertTrue(any("KeyboardInterrupt" in e for e in proof["errors"]))
        self.assertTrue(any("SystemExit" in e for e in proof["errors"]))

    def test_tracker_failure_holds_even_after_parent_terminal(self):
        run = object.__new__(force.Coordinator); run.evidence = []
        run.args = SimpleNamespace(timeout=30, ui_timeout_minutes=1)
        binary = self.root / "synthetic-helper.exe"; binary.write_text("never executable")
        process = Mock(pid=100, returncode=0); process.poll.return_value = 0
        run.hold_uncaptured_startup = Mock(side_effect=RuntimeError("external reconciliation hold reached"))
        with (patch.object(force.subprocess, "Popen", return_value=process),
              patch.object(force, "Processes", side_effect=RuntimeError("lost startup ancestry")),
              patch.object(force, "identity", return_value={"pid": 100}), patch.object(force.psutil, "Process", return_value=object())):
            with self.assertRaisesRegex(RuntimeError, "external reconciliation hold reached"):
                run.run_process(binary, [], self.root / "helper")
        run.hold_uncaptured_startup.assert_called_once_with(process, self.root / "helper")

    def test_actual_run_process_missing_native_holds_existing_handles_before_store_exit(self):
        run = object.__new__(force.Coordinator); run.evidence = []
        run.args = SimpleNamespace(timeout=30, ui_timeout_minutes=1)
        binary = self.root / "synthetic-helper.exe"; binary.write_text("never executable")
        process = Mock(pid=100, returncode=0); process.poll.return_value = 0
        tracker = self.synthetic_processes(0); tracker.entries.pop(101)
        tracker.anchor = tracker.entries[100]["identity"]
        tracker.finish = Mock(); tracker.await_terminal = Mock()
        state = {"storeHeld": False, "hostClosed": False}; observed = []
        class Window:
            def __enter__(inner): state["storeHeld"] = True
            def __exit__(inner, *args): state["storeHeld"] = False
        def hold(retained_process, folder, retained_tracker, reason):
            self.assertTrue(state["storeHeld"]); self.assertFalse(state["hostClosed"])
            self.assertIs(retained_process, process); self.assertIs(retained_tracker, tracker)
            tracker.await_terminal.assert_not_called(); tracker.finish.assert_not_called()
            tracker.entries[100]["handle"].close.assert_not_called()
            observed.append(reason)
            # Only the synthetic hold spy escapes. Production hold catches all
            # cancellation and has no automatic return path.
            raise RuntimeError("synthetic external reconciliation hold observed")
        run.hold_uncaptured_startup = hold
        with (patch.object(force.subprocess, "Popen", return_value=process),
              patch.object(force, "Processes", return_value=tracker),
              patch.object(force, "identity", return_value=tracker.anchor), patch.object(force.psutil, "Process", return_value=object())):
            with self.assertRaisesRegex(RuntimeError, "reconciliation hold observed"):
                try:
                    with Window(): run.run_process(binary, [], self.root / "helper", own_portal=True)
                finally: state["hostClosed"] = True
        self.assertEqual(len(observed), 1)

    def test_actual_host_close_missing_native_blocks_before_detach_eof_or_handle_release(self):
        owner = Mock()
        owner.hold_uncaptured_startup.side_effect = RuntimeError("synthetic external reconciliation hold observed")
        host = force.HostSession(owner, self.root); host.client = Mock()
        host.native_start_attempted = True
        tracker = self.synthetic_processes(0); tracker.entries.pop(101)
        tracker.anchor = tracker.entries[100]["identity"]; tracker.collect = Mock()
        tracker.finish = Mock(); tracker.await_terminal = Mock()
        host.processes = tracker; host.call = Mock()
        with patch.object(force, "graceful_close") as close:
            with self.assertRaisesRegex(RuntimeError, "reconciliation hold observed"): host.close()
        owner.hold_uncaptured_startup.assert_called_once()
        host.call.assert_not_called(); close.assert_not_called()
        tracker.finish.assert_not_called(); tracker.await_terminal.assert_not_called()

    def test_partial_tracker_constructor_retains_opened_handle_when_receipt_fails(self):
        held = Mock()
        def retain(tracker, *args):
            tracker.entries[100] = {"handle": held}
            raise OSError("synthetic startup receipt failure")
        with (patch.object(force.Processes, "retain", retain), patch.object(force.psutil, "Process", return_value=object())):
            tracker = force.Processes({"pid": 100}, self.root)
        self.assertTrue(tracker.capture_incomplete)
        self.assertIs(tracker.entries[100]["handle"], held)
        held.close.assert_not_called()

    def test_host_reported_native_pid_must_be_captured_before_cleanup(self):
        owner = Mock(); owner.hold_uncaptured_startup.side_effect = RuntimeError("synthetic reconciliation hold observed")
        host = force.HostSession(owner, self.root); host.client = Mock(); host.native_start_attempted = True; host.pid = 999
        tracker = self.synthetic_processes(0); tracker.collect = Mock(); tracker.finish = Mock(); tracker.await_terminal = Mock()
        tracker.capture_complete = Mock(return_value=True); host.processes = tracker; host.call = Mock()
        with patch.object(force, "graceful_close") as close:
            with self.assertRaisesRegex(RuntimeError, "reconciliation hold observed"): host.close()
        owner.hold_uncaptured_startup.assert_called_once(); close.assert_not_called(); host.call.assert_not_called()
        tracker.finish.assert_not_called()

    def test_late_native_terminal_hold_is_cleanup_not_acceptance(self):
        tracker = self.cleanup_tracker()
        with (patch.object(tracker, "all_terminal", side_effect=[False, True]), patch.object(force.time, "sleep"),
              patch.object(force, "identity", return_value={"pid": 999})):
            tracker.await_terminal("synthetic close deadline", timeout_seconds=45, clock=Mock(side_effect=[0, 46]))
        self.assertTrue(any("deadline exceeded" in e for e in tracker.errors))
        self.assertFalse(tracker.finish()["completed"])

    def test_file_storage_server_has_its_own_measured_deadline(self):
        for times, portal_alive, late in (([0, 301], False, None), ([0, 361], False, force.FILE_STORAGE_SERVER),
                                          ([0, 46], True, "Siemens.Automation.Portal.exe")):
            with self.subTest(times=times, portal_alive=portal_alive):
                tracker = self.cleanup_tracker()
                tracker.entries[100]["handle"].api.wait.return_value = 0
                tracker.entries[101]["handle"].api.wait.return_value = 258 if portal_alive else 0
                storage = Mock(); storage.api.wait.return_value = 258
                tracker.entries[102] = {"role": "native", "ancestry": [], "handle": storage,
                                        "identity": {"pid": 102, "created": 1700000000, "name": force.FILE_STORAGE_SERVER}}
                with (patch.object(tracker, "all_terminal", side_effect=[False, True]), patch.object(force.time, "sleep"),
                      patch.object(force, "identity", return_value={"pid": 999})):
                    tracker.await_terminal("synthetic natural exit", timeout_seconds=45, clock=Mock(side_effect=times))
                errors = [e for e in tracker.errors if "deadline exceeded" in e]
                self.assertEqual(bool(errors), late is not None)
                if late: self.assertTrue(errors[0].endswith(late))
                self.assertEqual(tracker.entries[102]["terminalDeadlineSeconds"], force.FILE_STORAGE_TERMINAL_SECONDS)
                self.assertEqual(tracker.entries[101]["terminalDeadlineSeconds"], 45)
                self.assertEqual(tracker.entries[102]["observedExitSeconds"], times[-1])

    def test_helper_launch_receipt_mismatch_holds_before_original_handle_release(self):
        run = object.__new__(force.Coordinator); run.evidence = []; run.folder = self.root
        run.args = SimpleNamespace(timeout=30, ui_timeout_minutes=1)
        binary = self.root / "synthetic-helper.exe"; binary.write_text("never executable")
        (self.root / "native").mkdir()
        force.save(self.root / "native/launch-return.json", {"processId": 999, "nativeProcessStartTimeUtc": "uncaptured"})
        process = Mock(pid=100, returncode=0); process.poll.return_value = 0
        tracker = self.synthetic_processes(0); tracker.anchor = tracker.entries[100]["identity"]
        tracker.finish = Mock(); tracker.await_terminal = Mock()
        run.hold_uncaptured_startup = Mock(side_effect=RuntimeError("synthetic reconciliation hold observed"))
        with (patch.object(force.subprocess, "Popen", return_value=process), patch.object(force, "Processes", return_value=tracker),
              patch.object(force, "identity", return_value=tracker.anchor), patch.object(force.psutil, "Process", return_value=object())):
            with self.assertRaisesRegex(RuntimeError, "reconciliation hold observed"):
                run.run_process(binary, [], self.root / "helper", own_portal=True)
        run.hold_uncaptured_startup.assert_called_once()
        tracker.finish.assert_not_called(); tracker.await_terminal.assert_not_called()

    def test_terminal_anchor_does_not_stop_worker_descendant_discovery(self):
        tracker = self.synthetic_processes(0); tracker.entries.pop(101)
        tracker.entries[100]["handle"].api.wait.return_value = 0
        info = dict(tracker.entries[100]["identity"], pid=102, name="TiaMcp.Openness.V21.exe")
        watcher = Mock(); watcher.api.wait.return_value = 258
        tracker.entries[102] = {"role": "worker", "identity": info, "ancestry": [tracker.entries[100]["identity"]], "handle": watcher}
        child = Mock(); child.name.side_effect = force.psutil.NoSuchProcess(103)
        worker = Mock(); worker.children.return_value = [child]
        with (patch.object(force.psutil, "Process", return_value=worker) as lookup,
              patch.object(force, "identity", return_value=info)):
            with self.assertRaises(force.psutil.NoSuchProcess): tracker.collect()
        lookup.assert_called_once_with(102)
        self.assertTrue(tracker.capture_incomplete)

    def test_known_native_keeps_original_ancestry_after_host_exit_but_new_birth_does_not(self):
        for reused in (False, True):
            with self.subTest(reused=reused):
                tracker = self.synthetic_processes(0)
                tracker.entries[100]["handle"].api.wait.return_value = 0
                worker_info = dict(tracker.entries[100]["identity"], pid=102, name="TiaMcp.Openness.V21.exe")
                watcher = Mock(); watcher.api.wait.return_value = 258
                tracker.entries[102] = {"role": "worker", "identity": worker_info,
                    "ancestry": [tracker.entries[100]["identity"]], "handle": watcher}
                native = Mock(); native.name.return_value = "Siemens.Automation.Portal.exe"
                native.parents.side_effect = force.psutil.NoSuchProcess(100)
                worker = Mock(); worker.children.return_value = [native]
                native_record = tracker.entries.pop(101)
                native_info = dict(native_record["identity"])
                tracker.entries[(native_info["pid"], native_info["created"])] = native_record
                if reused: native_info["created"] += 1
                old_ancestry = list(native_record["ancestry"])
                def info(proc): return worker_info if proc is worker else native_info
                with patch.object(force.psutil, "Process", return_value=worker), patch.object(force, "identity", side_effect=info):
                    if reused:
                        with self.assertRaises(force.psutil.NoSuchProcess): tracker.collect()
                    else: tracker.collect()
                self.assertEqual(tracker.capture_incomplete, reused)
                self.assertEqual(native_record["ancestry"], old_ancestry)
                if not reused: native.parents.assert_not_called()
                native_record["handle"].close.assert_not_called()

    def test_prepare_only_never_constructs_coordinator_or_process(self):
        output = self.root / "preflight.json"
        with (patch.object(force, "preflight", return_value={"completed": True, "nativeInvoked": False}),
              patch.object(force, "Coordinator", side_effect=AssertionError("dispatch")) as coordinator,
              patch.object(force.subprocess, "Popen", side_effect=AssertionError("process launch"))):
            code = force.main(["--root", str(self.root / "unused"), "--recovery-gate", str(self.root / "gate.json"),
                               "--prepare-only", "--output", str(output)])
        self.assertEqual(code, 0); self.assertFalse(coordinator.called)
        self.assertFalse((self.root / "unused").exists())

    def test_native_phase_cannot_use_offline_switch(self):
        with self.assertRaisesRegex(ValueError, "explicit native or file-only"):
            force.main(["--root", str(self.root), "--recovery-gate", "unused.json", "--phase", "author-ui", "--offline"])

    def test_offline_phase_cannot_dispatch_native(self):
        with self.assertRaisesRegex(ValueError, "explicit native or file-only"):
            force.main(["--root", str(self.root), "--recovery-gate", "unused.json", "--phase", "request-capture", "--execute-native"])

    def test_wrong_host_tool_is_refused_before_request(self):
        host = object.__new__(force.HostSession)
        with self.assertRaisesRegex(ValueError, "outside force capture scope"):
            host.call("download")


if __name__ == "__main__":
    unittest.main()
