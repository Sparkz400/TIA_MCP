"""Fault injection for ownership, preservation, and timeout admission boundaries."""
import copy
import unittest

from block_test_runtime import NativeToolError, RuntimeCleanupUncertain
import test_block_test_runtime as fixtures


def failure(tool, code="Timeout", details=None):
    return NativeToolError(tool, {"error": {"code": code, "details": details or {
        "exceptionType": "Siemens.Simatic.Simulation.Runtime.SimulationRuntimeException"}}})


class RuntimeUncertaintyTests(unittest.TestCase):
    def setUp(self):
        fixtures.BackendTests.setUp(self)

    def inject(self, tool, error, *, action=None, result=None):
        previous = self.backend.native
        def native(name, arguments):
            if name == tool and (action is None or arguments.get("action") == action):
                self.native.calls.append((name, copy.deepcopy(arguments)))
                if error:
                    raise error
                return copy.deepcopy(result)
            return previous(name, arguments)
        self.backend.native = native

    def test_unknown_creation_never_claims_no_instance_or_attempts_unguarded_release(self):
        original = failure("sim_create")
        self.inject("sim_create", original)
        with self.assertRaises(NativeToolError) as started:
            self.backend.start()
        self.assertIs(original, started.exception)
        with self.assertRaises(RuntimeCleanupUncertain) as closed:
            self.backend.close()
        cleanup = closed.exception.cleanup
        self.assertNotIn("instanceCreated", cleanup)
        self.assertTrue(cleanup["instanceCreationAttempted"])
        self.assertFalse(cleanup["instanceIdentityVerified"])
        self.assertEqual("unknown", cleanup["instanceCreationOutcome"])
        self.assertEqual("stopped_due_to_uncertain_runtime", cleanup["admission"])
        self.assertEqual("Timeout", cleanup["runtimeOutcomes"][0]["nativeErrorCode"])
        self.assertEqual(["sim_create"], [name for name, _ in self.native.calls])

    def test_malformed_created_identity_is_not_used_for_cleanup(self):
        for instance in ({"id": False, "name": "owned", "owned": True}, {"id": 0, "name": "", "owned": True},
                         {"id": 0, "name": "other", "owned": False}):
            with self.subTest(instance=instance):
                self.setUp()
                self.inject("sim_create", None, result={"instance": instance})
                with self.assertRaises(RuntimeError):
                    self.backend.start()
                with self.assertRaises(RuntimeCleanupUncertain):
                    self.backend.close()
                self.assertFalse(any(name == "sim_release" for name, _ in self.native.calls))

    def test_restore_and_isolation_uncertainty_survive_successful_own_release(self):
        for code, details in (
            ("SimulationProvisionCardRestoreFailed", {"originalProgramCardRestored": False, "credentialSubmissionAttempted": True,
                "cleanup": [{"action": "powerOff", "completed": True}, {"action": "restoreOriginalProtectedCard", "completed": False}]}),
            ("SimulationProvisionIsolationRequired", {"originalProgramCardRestored": False, "credentialSubmissionAttempted": False, "cleanup": []}),
            ("SimulationProvisionCleanupFailed", {"originalProgramCardRestored": True, "cleanup": [{"action": "releaseInterface", "completed": False}]})):
            with self.subTest(code=code):
                self.setUp()
                self.backend.provision_runtime_credential = True
                error = failure("simulation_provision_scratch_credential", code, details)
                self.inject("simulation_provision_scratch_credential", error)
                with self.assertRaises(NativeToolError):
                    self.backend.start()
                with self.assertRaises(RuntimeCleanupUncertain) as closed:
                    self.backend.close()
                cleanup = closed.exception.cleanup
                self.assertTrue(cleanup["release"]["unregistered"])
                self.assertTrue(cleanup["instanceUnregistrationVerified"])
                self.assertTrue(cleanup["runtimeStateUncertain"])
                self.assertFalse(cleanup["completed"])
                self.assertFalse(cleanup["runtimeOutcomes"][0]["reconciled"])
                self.assertEqual(code, cleanup["runtimeOutcomes"][0]["nativeErrorCode"])
                self.assertFalse(any(name == "sim_state" for name, _ in self.native.calls))

    def test_terminal_native_power_on_timeout_reconciles_only_exact_owned_unregistration(self):
        self.inject("sim_state", failure("sim_state"), action="powerOn")
        with self.assertRaises(NativeToolError):
            self.backend.start()
        cleanup = self.backend.close()
        self.assertTrue(cleanup["completed"])
        self.assertFalse(cleanup["runtimeStateUncertain"])
        self.assertTrue(cleanup["runtimeOutcomes"][0]["reconciled"])
        self.assertEqual("Timeout", cleanup["runtimeOutcomes"][0]["nativeErrorCode"])
        self.assertEqual(1, sum(name == "sim_state" for name, _ in self.native.calls))

    def test_transport_unknown_is_not_reconciled_by_a_later_release(self):
        for code in ("TimeoutOutcomeUnknown", "CancelledOutcomeUnknown", "WorkerDisconnected", "BridgeFaulted", "Empty", "TimeoutError"):
            with self.subTest(code=code):
                self.setUp()
                self.inject("sim_state", failure("sim_state", code), action="powerOn")
                with self.assertRaises(NativeToolError):
                    self.backend.start()
                with self.assertRaises(RuntimeCleanupUncertain) as closed:
                    self.backend.close()
                self.assertTrue(closed.exception.cleanup["release"]["unregistered"])
                self.assertFalse(closed.exception.cleanup["runtimeOutcomes"][0]["reconciled"])

    def test_unknown_download_cannot_be_reconciled_by_owned_simulator_release(self):
        for code in ("TimeoutOutcomeUnknown", "CancelledOutcomeUnknown", "WorkerDisconnected", "Empty"):
            with self.subTest(code=code):
                self.setUp()
                self.inject("simulation_download", failure("simulation_download", code))
                with self.assertRaises(NativeToolError):
                    self.backend.start()
                with self.assertRaises(RuntimeCleanupUncertain) as closed:
                    self.backend.close()
                self.assertTrue(closed.exception.cleanup["release"]["unregistered"])
                self.assertFalse(closed.exception.cleanup["runtimeOutcomes"][0]["reconciled"])
                self.assertEqual("simulation_download", closed.exception.cleanup["runtimeOutcomes"][0]["tool"])

    def test_unknown_read_write_or_tags_never_disappears_after_release(self):
        for tool in ("sim_tags", "sim_read", "sim_write"):
            for code in ("WorkerDisconnected", "CancelledOutcomeUnknown", "Empty"):
                with self.subTest(tool=tool, code=code):
                    self.setUp()
                    if tool != "sim_tags":
                        self.backend.start()
                    self.inject(tool, failure(tool, code))
                    with self.assertRaises(NativeToolError):
                        if tool == "sim_tags":
                            self.backend.start()
                        elif tool == "sim_read":
                            self.backend.call("dut")
                        else:
                            self.backend.write({"dut.A": True})
                    with self.assertRaises(RuntimeCleanupUncertain) as closed:
                        self.backend.close()
                    self.assertTrue(closed.exception.cleanup["release"]["unregistered"])
                    self.assertEqual(tool, closed.exception.cleanup["runtimeOutcomes"][0]["tool"])
                    self.assertFalse(closed.exception.cleanup["runtimeOutcomes"][0]["reconciled"])

    def test_helper_failure_requires_complete_terminal_exact_preservation_evidence(self):
        details = {"completed": False, "completedAtUtc": "2026-10-08T00:00:00Z", "instanceId": 0,
            "instanceName": "owned", "originalProgramRevision": "a" * 64, "operatingState": "Off", "protectionDisabled": False,
            "originalProgramCardRestored": True, "goOnlineAttempted": False,
            "cleanup": [{"action": "powerOff", "completed": True}, {"action": "restoreOriginalProtectedCard", "completed": True}]}
        for field, value in ((None, None), ("completedAtUtc", None), ("instanceName", "other"), ("originalProgramRevision", "b" * 64),
                             ("operatingState", "Stop"), ("cleanup", []), ("goOnlineAttempted", True)):
            with self.subTest(field=field):
                self.setUp()
                self.backend.provision_runtime_credential = True
                changed = copy.deepcopy(details)
                if field:
                    changed[field] = value
                self.inject("simulation_provision_scratch_credential",
                    failure("simulation_provision_scratch_credential", "SimulationCredentialProvisionFailed", changed))
                with self.assertRaises(NativeToolError):
                    self.backend.start()
                if field:
                    with self.assertRaises(RuntimeCleanupUncertain):
                        self.backend.close()
                else:
                    self.assertTrue(self.backend.close()["completed"])

    def test_wrong_release_identity_or_failed_release_keeps_timeout_unresolved(self):
        for result in ({"instanceId": 1, "instanceName": "owned", "unregistered": True},
                       {"instanceId": 0, "instanceName": "other", "unregistered": True},
                       {"instanceId": False, "instanceName": "owned", "unregistered": True},
                       {"instanceId": 0, "instanceName": "owned", "unregistered": False}):
            with self.subTest(result=result):
                self.setUp()
                self.inject("sim_state", failure("sim_state"), action="powerOn")
                self.inject("sim_release", None, result=result)
                with self.assertRaises(NativeToolError):
                    self.backend.start()
                with self.assertRaises(RuntimeCleanupUncertain) as closed:
                    self.backend.close()
                self.assertFalse(closed.exception.cleanup["runtimeOutcomes"][0]["reconciled"])
                self.assertIn("releaseFailure", closed.exception.cleanup)

    def test_no_creation_claim_is_allowed_only_before_creation_was_attempted(self):
        cleanup = self.backend.close()
        self.assertTrue(cleanup["completed"])
        self.assertFalse(cleanup["instanceCreated"])
        self.assertFalse(cleanup["instanceCreationAttempted"])

    def test_malformed_native_errors_cannot_bypass_state_uncertainty_latch(self):
        for payload in (None, [], {"error": None}, {"error": []}, {"error": {"code": "Timeout", "details": None}},
                        {"error": {"code": "Timeout", "details": []}}):
            with self.subTest(payload=payload):
                self.setUp()
                original = NativeToolError("sim_state", payload)
                self.inject("sim_state", original, action="powerOn")
                with self.assertRaises(NativeToolError) as started:
                    self.backend.start()
                self.assertIs(original, started.exception)
                with self.assertRaises(RuntimeCleanupUncertain) as closed:
                    self.backend.close()
                self.assertTrue(closed.exception.cleanup["runtimeStateUncertain"])
                self.assertFalse(closed.exception.cleanup["runtimeOutcomes"][0]["reconciled"])

    def test_creation_admission_is_independent_of_diagnostic_record_success(self):
        self.inject("sim_create", NativeToolError("sim_create", {"error": None}))
        with self.assertRaises(NativeToolError):
            self.backend.start()
        self.backend.runtime_outcomes.clear()
        with self.assertRaises(RuntimeCleanupUncertain) as closed:
            self.backend.close()
        self.assertTrue(closed.exception.cleanup["runtimeStateUncertain"])
        self.assertNotIn("instanceCreated", closed.exception.cleanup)


if __name__ == "__main__":
    unittest.main()
