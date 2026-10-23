"""Fault injection for the prerequisite-only fixture coordinator; no TIA launch."""
import json
import os
import tempfile
import threading
import unittest
from contextlib import ExitStack
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

import prepare_hmi_fixture_ui as runner
import qualify_force_capture as process_tracking


class HeldForReconciliation(BaseException):
    pass


class PreparationPacketTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.archive = self.root / 'baseline.zap21'
        self.archive.write_bytes(b'original test archive')
        self.helper = self.root / 'helper.exe'
        self.helper.write_bytes(b'file-only helper hash')
        self.source = self.root / 'source.json'
        self.recovery = self.root / 'recovery.json'
        self.source_data = dict(completed=True, profiles=[dict(profile='classic-comfort',
            fixtureMode='positive', deliberateInvalidReferences=False, archiveRetrievalVerified=True,
            projectPath=str(self.root / 'TiaMcpHmi_test.ap21'), archive=runner.reference(self.archive),
            nativeBefore=[dict(name='HmiA'), dict(name='HmiB')])])
        self.recovery_data = dict(completed=True,
            preservation=dict(checkpointRecovery=True, recoveredFreshReopen=True),
            sessions=[dict(cleanup=dict(completed=True))])
        self.args = SimpleNamespace(helper=self.helper, source_report=self.source,
            recovery_report=self.recovery, output=self.root / 'prepared', profile='classic-comfort',
            prepare_only=True, known_store=[])
        self.save()

    def save(self):
        self.source.write_text(json.dumps(self.source_data), encoding='utf-8')
        self.recovery.write_text(json.dumps(self.recovery_data), encoding='utf-8')

    def test_preparation_retains_exact_inputs_without_native_or_locks(self):
        original = {p: p.read_bytes() for p in [self.archive, self.helper, self.source, self.recovery]}
        with patch.object(runner, 'StoreExclusiveWindow') as window, patch.object(runner, 'execute_helper') as execute:
            self.assertEqual(runner.run(self.args), 0)
        window.assert_not_called()
        execute.assert_not_called()
        packet = runner.read(self.args.output / 'preparation.json')
        self.assertEqual(packet['inputs']['archive'], runner.reference(self.archive))
        self.assertEqual(packet['owners'], ['HmiA', 'HmiB'])
        self.assertFalse(packet['admissionGranted'])
        self.assertFalse(packet['bridgeRecoveryQualified'])
        self.assertFalse((self.args.output / 'admission.json').exists())
        for path, data in original.items(): self.assertEqual(path.read_bytes(), data)

    def test_unified_preparation_selects_only_transfer_prerequisite(self):
        self.args.profile = 'unified-comfort'
        self.source_data['profiles'][0]['profile'] = self.args.profile
        self.save()
        self.assertEqual(runner.run(self.args), 0)
        packet = runner.read(self.args.output / 'preparation.json')
        self.assertEqual(packet['allowedEdit'], 'configure-encrypted-transfer-password-and-confirmation')
        self.assertFalse(packet['disableSecurityAllowed'])

    def test_duplicate_profile_refuses_before_output(self):
        self.source_data['profiles'] *= 2
        self.save()
        with self.assertRaisesRegex(ValueError, 'Exactly one'): runner.run(self.args)
        self.assertFalse(self.args.output.exists())

    def test_empty_cleanup_cannot_prove_recovery(self):
        self.recovery_data['sessions'] = []
        self.save()
        with self.assertRaisesRegex(ValueError, 'Recovery foundation'): runner.run(self.args)
        self.assertFalse(self.args.output.exists())

    def test_changed_archive_refuses_before_output(self):
        self.archive.write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'Original archive changed'): runner.run(self.args)
        self.assertFalse(self.args.output.exists())

    def test_failed_reopen_refuses_even_with_completed_summary(self):
        self.recovery_data['preservation']['recoveredFreshReopen'] = False
        self.save()
        with self.assertRaisesRegex(ValueError, 'Recovery foundation'): runner.run(self.args)

    def test_unrelated_project_name_refuses(self):
        self.source_data['profiles'][0]['projectPath'] = str(self.root / 'customer.ap21')
        self.save()
        with self.assertRaisesRegex(ValueError, 'positive owned fixture'): runner.run(self.args)


class FixtureRunnerTests(unittest.TestCase):
    def setUp(self):
        self.stack = ExitStack()
        self.addCleanup(self.stack.close)
        self.root = Path(self.stack.enter_context(tempfile.TemporaryDirectory())).resolve()
        self.output = self.root / 'native'
        self.output.mkdir()
        self.process = Mock(pid=42)
        self.process.poll.return_value = 0
        self.process.wait.return_value = 0
        self.tracker = Mock()
        self.tracker.finish.return_value = {'completed': True, 'jobProof': {'path': 'synthetic-job-proof.json'}}
        self.launch = self.stack.enter_context(patch.object(runner, 'OwnedProcessJob', return_value=self.process))
        self.stack.enter_context(patch.object(runner.native_dispatch_guard, 'begin', return_value=self.root / 'ticket'))
        self.complete = self.stack.enter_context(patch.object(runner.native_dispatch_guard, 'complete'))
        self.stack.enter_context(patch.object(runner.subprocess, 'CREATE_NO_WINDOW', 0, create=True))
        self.stack.enter_context(patch.object(runner.psutil, 'Process', return_value=object()))
        self.identity = self.stack.enter_context(patch.object(runner, 'identity', return_value={'pid': 42}))
        self.capture = self.stack.enter_context(patch.object(runner, 'FixtureProcesses', return_value=self.tracker))
        self.hold = self.stack.enter_context(patch.object(runner, 'hold_missing', side_effect=HeldForReconciliation))
        self.result = {}

    def execute(self):
        runner.execute_helper(self.root / 'helper.exe', {'path': str(self.root / 'admission.json')},
                              self.root, self.output, self.result)

    def test_identity_failure_enters_hold_before_returning_to_store_owner(self):
        self.identity.side_effect = RuntimeError('identity unavailable after launch')
        with self.assertRaises(HeldForReconciliation): self.execute()
        self.hold.assert_called_once()
        self.capture.assert_not_called()
        self.process.wait.assert_not_called()

    def test_capture_constructor_failure_enters_hold(self):
        self.capture.side_effect = RuntimeError('capture failed')
        with self.assertRaises(HeldForReconciliation): self.execute()
        self.hold.assert_called_once()
        self.assertNotIn('processProof', self.result)

    def test_failed_launch_has_no_process_to_reconcile(self):
        self.launch.side_effect = OSError('launch refused')
        with self.assertRaises(OSError): self.execute()
        self.hold.assert_not_called()
        self.capture.assert_not_called()

    def test_terminal_cleanup_does_not_assume_one_native_process(self):
        self.execute()
        self.tracker.await_terminal.assert_called_once_with('Wait for exact fixture helper and every retained owned descendant')
        self.hold.assert_not_called()

    def test_dispose_is_never_inferred_from_expected_helper_behavior(self):
        self.execute()
        self.tracker.await_terminal.assert_called_once()
        self.tracker.finish.assert_called_once_with(explicit_native_dispose=False)
        self.assertEqual(self.result['helperExitCode'], 0)

    def test_observed_explicit_dispose_is_retained(self):
        (self.output / 'result.json').write_text(json.dumps({'explicitOwnedSdkDispose': True}), encoding='utf-8')
        self.execute()
        self.tracker.finish.assert_called_once_with(explicit_native_dispose=True)
        self.complete.assert_called_once()
        self.process.close.assert_called_once()

    def test_wrong_project_ready_receipt_reconciles_before_error_escapes(self):
        self.process.poll.return_value = None
        self.tracker.native.return_value = {'identity': {'parentPid': 42}}
        (self.output / 'ui-ready.json').write_text(json.dumps({
            'processId': 7, 'projectPath': str(self.root / 'unrelated.ap21')}), encoding='utf-8')
        with self.assertRaisesRegex(Exception, 'Unexpected editor project'): self.execute()
        self.tracker.await_terminal.assert_called_once()
        self.tracker.finish.assert_called_once()
        self.assertFalse((self.root / 'editor-ready.json').exists())


class FixtureProcessTreeTests(unittest.TestCase):
    """Real tracker decisions with simulated process discovery and retained handles."""
    def setUp(self):
        self.stack = ExitStack()
        self.addCleanup(self.stack.close)
        self.root = Path(self.stack.enter_context(tempfile.TemporaryDirectory())).resolve()
        self.processes = {}
        self.tracker = runner.FixtureProcesses.__new__(runner.FixtureProcesses)
        self.tracker.discovery_gate = threading.RLock()
        self.tracker.main_identity = None
        self.tracker.native_output = None
        self.tracker.tree_hold_written = False
        self.tracker.folder = self.root
        self.tracker.entries = {}
        self.tracker.errors = []
        self.tracker.capture_incomplete = False
        self.tracker.anchor = self.process(42, None, "HmiSeeder.exe").info
        self.retain(self.processes[42], "anchor", [])
        self.tracker.retain = Mock(side_effect=self.retain)
        self.stack.enter_context(patch.object(runner.psutil, "Process", side_effect=lambda pid: self.processes[pid]))
        self.stack.enter_context(patch.object(runner, "identity", side_effect=lambda p: p.info.copy()))

    def process(self, pid, parent_pid, name="Siemens.Automation.Portal.exe"):
        process = SimpleNamespace(pid=pid, info=dict(pid=pid, parentPid=parent_pid, created=pid / 10,
                                            name=name, exe="C:/owned/" + name, command=[name]), terminal=False)
        process.create_time = lambda: process.info["created"]
        def parents():
            parent = parent_pid
            while parent is not None:
                yield self.processes[parent]
                parent = self.processes[parent].info["parentPid"]
        def children(recursive):
            return [p for p in self.processes.values() if not p.terminal and
                    any(a.info["pid"] == pid for a in p.parents())]
        process.parents = parents
        process.children = children
        self.processes[pid] = process
        return process

    def retain(self, process, role, ancestry):
        watcher = SimpleNamespace(_handle=process,
            api=SimpleNamespace(wait=lambda handle, timeout: 0 if handle.terminal else 258),
            evidence={"birthAndImageVerified": True})
        entry = dict(identity=process.info.copy(), role=role, ancestry=ancestry, handle=watcher)
        self.tracker.entries[(process.info["pid"], process.info["created"])] = entry
        return entry

    def test_main_plus_two_background_children_use_exact_main_identity(self):
        self.process(50, 42)
        self.process(51, 50)
        self.process(52, 50)
        main = self.tracker.native(50)
        self.assertEqual(main["identity"]["pid"], 50)
        self.assertEqual(len(self.tracker.entries), 4)
        self.assertFalse(self.tracker.all_terminal())
        self.processes[42].terminal = self.processes[50].terminal = True
        self.assertFalse(self.tracker.all_terminal(), "Live owned compiler/background children still hold the store")
        self.processes[51].terminal = True
        self.assertFalse(self.tracker.all_terminal(), "Every original child handle must signal")
        self.processes[52].terminal = True
        self.assertTrue(self.tracker.all_terminal())

    def test_reported_background_child_cannot_substitute_for_main(self):
        self.process(50, 42)
        self.process(51, 50)
        with self.assertRaisesRegex(ValueError, "direct owned child"):
            self.tracker.native(51)
        self.assertIsNone(self.tracker.main_identity)
        self.assertEqual(len(self.tracker.entries), 3, "Rejected UI identity retains all captured handles for cleanup")

    def test_foreign_main_pid_is_never_retained_or_admitted(self):
        self.process(50, 42)
        self.process(90, None)
        with self.assertRaisesRegex(ValueError, "unavailable or unqualified"):
            self.tracker.native(90)
        self.assertNotIn((90, 9.0), self.tracker.entries)

    def test_main_identity_cannot_change_to_another_direct_child(self):
        self.process(50, 42)
        self.tracker.native(50)
        self.process(51, 42)
        with self.assertRaisesRegex(ValueError, "Main fixture editor identity changed"):
            self.tracker.native(51)

    def test_late_child_is_captured_after_helper_exit_from_original_editor_root(self):
        self.process(50, 42)
        self.tracker.native(50)
        self.processes[42].terminal = True
        self.process(51, 50, "HmiCompiler.exe")
        self.assertFalse(self.tracker.all_terminal())
        child = self.tracker.entries[(51, 5.1)]
        self.assertEqual([p["pid"] for p in child["ancestry"]], [50, 42])
        self.processes[50].terminal = True
        self.process(52, 51, "LateCompilerChild.exe")
        self.assertFalse(self.tracker.all_terminal())
        self.assertEqual([p["pid"] for p in self.tracker.entries[(52, 5.2)]["ancestry"]], [51, 50, 42])
        self.assertEqual(self.tracker.retain.call_count, 3)

    def test_incomplete_capture_never_returns_terminal_and_has_visible_reconciliation(self):
        self.process(50, 42)
        self.tracker.native(50)
        self.tracker.capture_incomplete = True
        for process in self.processes.values():
            process.terminal = True
        self.assertFalse(self.tracker.all_terminal())
        hold = runner.read(self.root / "fixture-tree-hold.json")
        self.assertTrue(hold["originalHandlesRetained"])
        self.assertTrue(hold["dependentDispatchProhibited"])
        self.assertFalse(hold["automaticTermination"])
        self.assertEqual(hold["commandPattern"], "process-cleanup-command-*.json")

    def test_native_start_intent_cannot_pass_with_only_the_exited_helper_handle(self):
        self.tracker.native_output = self.root
        (self.root / "start-intent.json").write_text("{}")
        (self.root / "start-return.json").write_text(json.dumps({"processId": 50}))
        self.processes[42].terminal = True
        self.assertFalse(self.tracker.all_terminal())
        self.assertTrue(self.tracker.capture_incomplete)
        self.assertTrue((self.root / "fixture-tree-hold.json").exists())

    def test_start_receipt_recovers_main_identity_when_ui_ready_poll_was_missed(self):
        self.tracker.native_output = self.root
        self.process(50, 42)
        self.tracker.collect()
        (self.root / "start-intent.json").write_text("{}")
        (self.root / "start-return.json").write_text(json.dumps({"processId": 50}))
        for process in self.processes.values():
            process.terminal = True
        self.assertTrue(self.tracker.all_terminal())
        self.assertEqual(self.tracker.main_identity["pid"], 50)

    def test_process_bound_latches_incomplete_without_discarding_captured_handles(self):
        self.tracker.maximum_descendants = 2
        self.process(50, 42)
        self.process(51, 50)
        self.tracker.native(50)
        self.process(52, 50)
        self.assertFalse(self.tracker.all_terminal())
        self.assertTrue(self.tracker.capture_incomplete)
        self.assertEqual(len(self.tracker.entries), 3)

    def test_terminal_route_uses_bound_shared_cleanup_without_native_count(self):
        with patch.object(runner.Processes, "await_terminal", return_value="terminal") as shared:
            self.assertEqual(self.tracker.await_terminal("cleanup", expected_native=1, timeout_seconds=5), "terminal")
        shared.assert_called_once_with("cleanup", timeout_seconds=5)

    def test_real_reconciliation_loop_retains_children_and_exposes_bound_command_route(self):
        self.process(50, 42)
        self.process(51, 50)
        self.process(52, 50)
        self.tracker.native(50)
        self.processes[42].terminal = self.processes[50].terminal = True
        self.process(os.getpid(), None, "coordinator.exe")
        def children_exit(_):
            live = runner.read(self.root / "cleanup-live.json")
            self.assertEqual({entry["identity"]["pid"] for entry in live["retained"]}, {42, 50, 51, 52})
            self.assertTrue(live["sameOriginalHandlesStillHeld"])
            self.assertFalse(live["automaticTermination"])
            self.assertEqual(live["commandPattern"], "process-cleanup-command-*.json")
            self.processes[51].terminal = self.processes[52].terminal = True
        with patch.object(process_tracking, "identity", side_effect=lambda p: p.info.copy()), \
                patch.object(process_tracking.time, "sleep", side_effect=children_exit) as slept:
            self.tracker.await_terminal("main already exited; retain two background children")
        slept.assert_called_once()
        self.assertEqual(len(self.tracker.entries), 4)
        self.assertFalse(self.tracker.capture_incomplete)

    def test_identity_change_latches_incomplete_and_preserves_original_handle(self):
        self.process(50, 42)
        original = self.tracker.native(50)["handle"]
        self.processes[50].info["created"] = 999
        self.assertFalse(self.tracker.all_terminal())
        self.assertTrue(self.tracker.capture_incomplete)
        self.assertIs(self.tracker.entries[(50, 5.0)]["handle"], original)

    def test_retained_child_exits_during_duplicate_identity_read(self):
        self.process(50, 42)
        child = self.process(51, 50, "Opns.Firewall.exe")
        self.tracker.native(50)
        original = self.tracker.entries[(51, 5.1)]["handle"]
        def identity_with_exit(process):
            if process is child:
                process.terminal = True
                raise runner.psutil.NoSuchProcess(51)
            return process.info.copy()
        with patch.object(runner, "identity", side_effect=identity_with_exit):
            self.tracker.collect()
        self.assertFalse(self.tracker.capture_incomplete)
        self.assertEqual(self.tracker.errors, [])
        self.assertIs(self.tracker.entries[(51, 5.1)]["handle"], original)
        self.assertEqual(self.tracker.retain.call_count, 2)

    def test_retained_terminal_duplicate_uses_cached_birth_without_identity_requery(self):
        self.process(50, 42)
        child = self.process(51, 50)
        self.tracker.native(50)
        child.terminal = True
        self.processes[42].children = lambda recursive: [child]
        def identity_without_child(process):
            if process is child:
                raise AssertionError("Terminal matched lifetime must not be queried by PID")
            return process.info.copy()
        with patch.object(runner, "identity", side_effect=identity_without_child):
            self.tracker.collect()
        self.assertFalse(self.tracker.capture_incomplete)

    def test_reused_pid_exit_is_not_proven_by_old_terminal_handle(self):
        self.process(50, 42)
        old_child = self.process(51, 50)
        self.tracker.native(50)
        original = self.tracker.entries[(51, 5.1)]["handle"]
        old_child.terminal = True
        reused = self.process(51, 50)
        reused.info["created"] = 999
        def identity_with_reused_exit(process):
            if process is reused:
                process.terminal = True
                raise runner.psutil.NoSuchProcess(51)
            return process.info.copy()
        with patch.object(runner, "identity", side_effect=identity_with_reused_exit):
            with self.assertRaises(runner.psutil.NoSuchProcess):
                self.tracker.collect()
        self.assertTrue(self.tracker.capture_incomplete)
        self.assertIs(self.tracker.entries[(51, 5.1)]["handle"], original)
        self.assertNotIn((51, 999), self.tracker.entries)

    def test_uncaptured_exit_still_latches_incomplete(self):
        self.process(50, 42)
        self.tracker.native(50)
        child = self.process(51, 50)
        def identity_with_uncaptured_exit(process):
            if process is child:
                process.terminal = True
                raise runner.psutil.NoSuchProcess(51)
            return process.info.copy()
        with patch.object(runner, "identity", side_effect=identity_with_uncaptured_exit):
            with self.assertRaises(runner.psutil.NoSuchProcess):
                self.tracker.collect()
        self.assertTrue(self.tracker.capture_incomplete)
        self.assertNotIn((51, 5.1), self.tracker.entries)

    def test_new_contained_attempt_uses_job_drain_for_ephemeral_observation(self):
        self.process(50, 42)
        self.tracker.native(50)
        self.tracker.job = Mock()
        self.tracker.ephemeral_observations = []
        self.tracker.job.snapshot.return_value = dict(totalProcesses=3, activeProcesses=1, helperHandleSignaled=True)
        child = self.process(51, 50, "ShortReporter.exe")
        def disappear(process):
            if process is child:
                process.terminal = True
                raise runner.psutil.NoSuchProcess(51)
            return process.info.copy()
        with patch.object(runner, "identity", side_effect=disappear):
            self.tracker.collect()
        self.assertFalse(self.tracker.capture_incomplete)
        self.assertFalse(self.tracker.ephemeral_observations[0]["individualLifetimeVerified"])
        self.assertNotIn((51, 5.1), self.tracker.entries)
        self.processes[42].terminal = self.processes[50].terminal = True
        self.assertFalse(self.tracker.all_terminal(), "A still-active job holds the window after every captured handle exits")
        self.tracker.job.snapshot.return_value["activeProcesses"] = 0
        self.assertTrue(self.tracker.all_terminal())

    def test_new_job_cannot_resolve_a_preexisting_missing_capture(self):
        self.tracker.capture_incomplete = True
        self.tracker.job = Mock()
        self.tracker.job.snapshot.return_value = dict(totalProcesses=1, activeProcesses=0, helperHandleSignaled=True)
        self.processes[42].terminal = True
        self.assertFalse(self.tracker.all_terminal())

    def test_missing_candidate_birth_cannot_fall_back_to_retained_pid(self):
        self.process(50, 42)
        child = self.process(51, 50)
        self.tracker.native(50)
        child.terminal = True
        self.processes[42].children = lambda recursive: [child]
        child.create_time = Mock(side_effect=runner.psutil.NoSuchProcess(51))
        with self.assertRaises(runner.psutil.NoSuchProcess):
            self.tracker.collect()
        self.assertTrue(self.tracker.capture_incomplete)


if __name__ == '__main__':
    unittest.main()
