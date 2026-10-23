"""Fresh engineering session lifecycle tests; no TIA or PLCSIM execution."""
import copy
import pathlib
import json
import sys
import types
import unittest
from unittest import mock

from block_test_executor import execute_suite
from block_test_program import generate_program, inspect_interface
from test_block_test_runtime import Simulator, temporary_directory
from test_block_test_stages import write_binaries
from block_test_process import ProcessExitNotVerified
import run_block_tests as runner
from smoke_mcp import Client


class CaseClient:
    def __init__(self, program, project, ordinal, events, fault=None):
        self.simulator = Simulator(program)
        self.project, self.ordinal, self.events, self.fault = project, ordinal, events, fault
        self.process = types.SimpleNamespace(returncode=7 if fault == "exit" else 0)

    def notify(self, *args):
        pass

    def request(self, method, params=None):
        if method == "initialize":
            return {}
        if method == "tools/list":
            return {"tools": [{"name": name} for name in runner.CaseSessionAudit.ALLOWED_TOOLS | runner.CaseSessionAudit.OPTIONAL_TOOLS]}
        tool, arguments = params["name"], params["arguments"]
        self.events.append((self.ordinal, tool))
        if tool == self.fault:
            return {"isError": True, "structuredContent": {"error": {"code": "InjectedFailure"}}}
        if tool == "simulation_provision_scratch_credential" and self.fault in ("helper_restore", "helper_isolation"):
            return {"isError": True, "structuredContent": {"error": {
                "code": "SimulationProvisionCardRestoreFailed" if self.fault == "helper_restore" else "SimulationProvisionIsolationRequired",
                "details": {"originalProgramCardRestored": False, "cleanup": []}}}}
        if tool == "sim_state" and arguments.get("action") == "powerOn" and self.fault in ("poweron_unknown", "poweron_native_timeout"):
            return {"isError": True, "structuredContent": {"error": {
                "code": "TimeoutOutcomeUnknown" if self.fault == "poweron_unknown" else "Timeout",
                "details": {"exceptionType": "Siemens.Simatic.Simulation.Runtime.SimulationRuntimeException"}}}}
        if tool == "tia_start":
            result = {"attached": True, "ownsPortalProcess": True, "processId": self.ordinal + 100}
        elif tool == "project_open":
            if arguments != {"path": self.project}:
                raise AssertionError("Wrong project")
            result = {"opened": True, "saved": False, "project": {"path": self.project}}
        elif tool == "tia_detach":
            result = {"attached": False, "disposedOwnedPortalConnection": True}
        else:
            result = self.simulator(tool, arguments)
        return {"structuredContent": result}

    def close(self):
        self.events.append((self.ordinal, "host_close"))
        if self.fault == "host_close":
            raise RuntimeError("Injected close failure")
        return {"hostExitCode": self.process.returncode, "forcedHostStop": self.fault == "forced_host_stop",
                "workerExits": [{"processExitVerified": self.fault != "worker_unknown",
                                 "exitCode": 9 if self.fault == "worker_exit" else 0}]}


class CaseSessions(unittest.TestCase):
    def setUp(self):
        self.temp = temporary_directory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.suite = runner.load_suites(["runtime-boolean"])[0]
        self.program = generate_program(inspect_interface(self.suite["interface"]))
        paths = write_binaries(self.root)
        self.binaries = runner.binary_manifest(*paths)
        self.project = self.root / "owned.ap21"
        self.project.write_bytes(b"qualified scratch")
        self.manifest = {"block:Rtf_Boolean": {"payloadSha256": "b" * 64}}
        self.revision = {"project": {"path": str(self.project)}, "reopenedExports": self.manifest,
                         "binariesSha256": runner.digest(self.binaries)}
        self.events, self.clients, self.audits = [], [], []
        self.fault = None
        self.exporter = mock.patch.object(runner, "export_snapshot", side_effect=self.export)
        self.exporter.start()
        self.addCleanup(self.exporter.stop)

    def export(self, *args):
        self.events.append((len(self.clients), "verify_exports"))
        result = copy.deepcopy(self.manifest)
        if self.fault == "exports":
            result["block:Rtf_Boolean"]["payloadSha256"] = "c" * 64
        return {}, result

    def client_factory(self, executable, timeout, arguments):
        self.assertEqual(pathlib.Path(self.binaries["host"]["path"]), executable)
        self.assertEqual(["--worker", self.binaries["worker"]["path"], "--sim-worker", self.binaries["simWorker"]["path"]], arguments)
        ordinal = len(self.clients) + 1
        self.events.append((ordinal, "host_start"))
        client = CaseClient(self.program, str(self.project), ordinal, self.events, self.fault)
        self.clients.append(client)
        return client

    def backend(self, case=None):
        audit = {"calls": [], "caseId": (case or {}).get("id", "test")}
        self.audits.append(audit)
        return runner.CaseRuntimeBackend(self.program, self.revision, "credential.json", suite=self.suite,
            output=self.root / ("case-" + str(len(self.audits))), audit=audit, persist=lambda: None,
            binaries=self.binaries, timeout=300, client_factory=self.client_factory,
            process_watcher_factory=self.watcher,
            provision_runtime_credential=self.fault in ("helper_restore", "helper_isolation"),
            card_verifier=lambda value: {"programRevision": value["programRevision"]})

    def watcher(self, pid):
        if self.fault == "capture":
            raise RuntimeError("Injected handle capture failure")
        owner = self
        ordinal = len(self.clients)
        owner.events.append((ordinal, "capture_handle"))
        class Watcher:
            evidence = {"processId": pid, "handleCaptured": True, "access": "SYNCHRONIZE", "inheritable": False}
            def wait_for_exit(self, timeout):
                owner.events.append((ordinal, "wait_process_exit"))
                if owner.fault == "process_wait":
                    raise ProcessExitNotVerified("Injected timeout", {"processId": pid, "completed": False, "processExitVerified": False})
                return {**self.evidence, "completed": True, "processExitVerified": True, "handleClosed": True, "waitResult": 0}
            def close(self):
                owner.events.append((ordinal, "close_handle"))
                return True
        return Watcher()

    def test_lazy_start_reopens_and_checks_exports_before_cpu_creation(self):
        backend = self.backend()
        self.assertFalse(self.clients)
        original = copy.deepcopy(self.revision)
        evidence = backend.start()
        tools = [name for _, name in self.events]
        self.assertLess(tools.index("project_open"), tools.index("verify_exports"))
        self.assertLess(tools.index("verify_exports"), tools.index("sim_create"))
        self.assertEqual(original, self.revision)
        self.assertTrue(evidence["engineeringSession"]["exportsVerified"])
        cleanup = backend.close()
        self.assertTrue(cleanup["completed"])
        self.assertTrue(cleanup["release"]["unregistered"])
        self.assertTrue(cleanup["tiaDetach"]["disposedOwnedPortalConnection"])
        self.assertEqual(0, cleanup["hostExitCode"])
        self.assertEqual([{ "processExitVerified": True, "exitCode": 0 }], cleanup["launcherShutdown"]["workerExits"])
        self.assertEqual(["sim_release", "tia_detach", "host_close", "wait_process_exit", "close_handle"], [name for _, name in self.events][-5:])
        self.assertTrue(cleanup["tiaProcessExit"]["processExitVerified"])

    def test_each_repetition_finishes_host_cleanup_before_next_fresh_session(self):
        suite = copy.deepcopy(self.suite)
        suite["cases"] = [suite["cases"][0]]
        suite["cases"][0]["repeat"] = 2
        result = execute_suite(suite, self.program, self.backend, self.revision)
        self.assertEqual("passed", result["status"])
        self.assertEqual(2, len(self.clients))
        self.assertIsNot(self.clients[0], self.clients[1])
        self.assertLess(self.events.index((1, "host_close")), self.events.index((2, "host_start")))
        self.assertLess(self.events.index((1, "close_handle")), self.events.index((2, "host_start")))
        self.assertEqual([101, 102], [case["isolation"]["engineeringSession"]["tiaProcessId"] for case in result["cases"]])

    def test_export_mismatch_blocks_cpu_creation_and_still_detaches_and_closes(self):
        self.fault = "exports"
        backend = self.backend()
        with self.assertRaisesRegex(RuntimeError, "native exports differ"):
            backend.start()
        self.assertTrue(backend.close()["completed"])
        self.assertNotIn((1, "sim_create"), self.events)
        self.assertEqual(["tia_detach", "host_close", "wait_process_exit", "close_handle"], [name for _, name in self.events][-4:])

    def test_partial_start_and_download_failure_attempt_every_available_cleanup(self):
        for fault in ("project_open", "simulation_download"):
            with self.subTest(fault=fault):
                self.fault = fault
                backend = self.backend()
                with self.assertRaises(RuntimeError):
                    backend.start()
                ordinal = len(self.clients)
                if fault == "simulation_download":
                    with self.assertRaises(runner.RuntimeCleanupUncertain) as closed:
                        backend.close()
                    cleanup = closed.exception.cleanup
                    self.assertTrue(cleanup["runtimeStateUncertain"])
                else:
                    cleanup = backend.close()
                    self.assertTrue(cleanup["completed"])
                self.assertIn((ordinal, "tia_detach"), self.events)
                self.assertIn((ordinal, "host_close"), self.events)
                self.assertEqual(fault == "simulation_download", (ordinal, "sim_release") in self.events)

    def test_release_detach_close_or_exit_failure_stops_all_later_cases(self):
        for fault in ("sim_release", "tia_detach", "host_close", "exit", "process_wait", "worker_exit", "worker_unknown", "forced_host_stop"):
            with self.subTest(fault=fault):
                before = len(self.clients)
                self.fault = fault
                suite = copy.deepcopy(self.suite)
                suite["cases"] = suite["cases"][:2]
                result = execute_suite(suite, self.program, self.backend, self.revision)
                self.assertEqual("failed", result["status"])
                self.assertEqual(before + 1, len(self.clients))
                self.assertEqual(1, len(result["cases"]))
                self.assertIn("error", result["cases"][0]["cleanup"])
                ordinal = len(self.clients)
                for step in ("sim_release", "tia_detach", "host_close", "wait_process_exit", "close_handle"):
                    self.assertIn((ordinal, step), self.events)
                self.assertFalse(self.audits[-1]["session"]["cleanup"]["completed"])

    def test_partial_start_or_missing_handle_fails_cleanup_and_prevents_cpu_creation(self):
        for fault in ("tia_start", "capture"):
            with self.subTest(fault=fault):
                self.fault = fault
                backend = self.backend()
                with self.assertRaises(RuntimeError):
                    backend.start()
                ordinal = len(self.clients)
                with self.assertRaisesRegex(RuntimeError, "tia_process_exit"):
                    backend.close()
                self.assertNotIn((ordinal, "sim_create"), self.events)
                self.assertIn((ordinal, "tia_detach"), self.events)
                self.assertIn((ordinal, "host_close"), self.events)

    def test_uncertain_runtime_stops_next_case_after_all_independent_cleanup(self):
        for fault in ("sim_create", "simulation_download", "helper_restore", "helper_isolation", "poweron_unknown"):
            with self.subTest(fault=fault):
                before = len(self.clients)
                self.fault = fault
                suite = copy.deepcopy(self.suite)
                suite["cases"] = suite["cases"][:2]
                result = execute_suite(suite, self.program, self.backend, self.revision)
                self.assertEqual("stopped_due_to_uncertain_runtime", result["statusReason"])
                self.assertEqual("failed", result["status"])
                self.assertEqual(1, len(result["cases"]))
                self.assertEqual(before + 1, len(self.clients))
                cleanup = result["cases"][0]["cleanup"]
                self.assertTrue(cleanup["runtimeStateUncertain"])
                self.assertFalse(cleanup["completed"])
                self.assertEqual(0, cleanup["hostExitCode"])
                self.assertTrue(cleanup["tiaProcessExit"]["processExitVerified"])
                self.assertTrue(cleanup["tiaDetach"]["disposedOwnedPortalConnection"])
                for action in ("tia_detach", "host_close", "wait_process_exit", "close_handle"):
                    self.assertIn((len(self.clients), action), self.events)
                if fault == "sim_create":
                    self.assertNotIn("instanceCreated", cleanup)
                    self.assertNotIn((len(self.clients), "sim_release"), self.events)
                else:
                    self.assertTrue(cleanup["release"]["unregistered"])
                self.assertEqual(cleanup["runtimeOutcomes"], self.audits[-1]["session"]["cleanup"]["runtimeOutcomes"])

    def test_native_poweron_timeout_with_exact_unregistration_does_not_invent_uncertainty(self):
        self.fault = "poweron_native_timeout"
        suite = copy.deepcopy(self.suite)
        suite["cases"] = suite["cases"][:2]
        result = execute_suite(suite, self.program, self.backend, self.revision)
        self.assertEqual(2, len(result["cases"]))
        self.assertEqual(2, len(self.clients))
        self.assertFalse(result.get("admission") == "stopped_due_to_uncertain_runtime")
        for case in result["cases"]:
            self.assertEqual("blocked", case["status"])
            self.assertTrue(case["cleanup"]["completed"])
            self.assertFalse(case["cleanup"]["runtimeStateUncertain"])
            self.assertTrue(case["cleanup"]["runtimeOutcomes"][0]["reconciled"])

    def test_changed_binaries_prevent_start_and_fail_cleanup_verification(self):
        backend = self.backend()
        pathlib.Path(self.binaries["worker"]["path"]).write_bytes(b"different")
        with self.assertRaisesRegex(RuntimeError, "binaries differ"):
            backend.start()
        with self.assertRaisesRegex(RuntimeError, "binary_verification"):
            backend.close()
        self.assertFalse(self.clients)

    def test_case_audit_rejects_other_project_and_engineering_mutation(self):
        client = mock.Mock()
        audit = runner.CaseSessionAudit(client, [], lambda: None, str(self.project))
        with self.assertRaises(ValueError):
            audit("project_open", {"path": str(self.project.with_name("other.ap21"))})
        with self.assertRaises(ValueError):
            audit("change_apply", {"changeId": "arbitrary"})
        client.request.assert_not_called()


class ShutdownReceiptTests(unittest.TestCase):
    def test_subprocess_exit_receipts_survive_stderr_drain_without_raw_diagnostics(self):
        for code in (0, 7):
            with self.subTest(exit_code=code):
                script = "import json,sys; sys.stdin.read(); print('unstructured native diagnostic',file=sys.stderr); print(json.dumps({'bridgeWorkerExit': {'processExitVerified': True,'exitCode': " + str(code) + "}}),file=sys.stderr,flush=True); sys.exit(" + str(code) + ")"
                client = Client(None, 5, command=[sys.executable, '-u', '-c', script])
                receipt = client.close()
                self.assertEqual(code, receipt['hostExitCode'])
                self.assertFalse(receipt['forcedHostStop'])
                self.assertTrue(receipt['stderrEofObserved'])
                self.assertEqual([{'processExitVerified': True, 'exitCode': code}], receipt['workerExits'])
                self.assertNotIn('unstructured native diagnostic', json.dumps(receipt))


if __name__ == "__main__":
    unittest.main()
