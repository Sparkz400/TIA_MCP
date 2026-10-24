"""Protocol, isolation, evidence and dry-run tests; never starts TIA/PLCSIM."""
import copy
import hashlib
import json
import pathlib
import tempfile
import types
import unittest
from unittest import mock

from block_test_program import generate_program, inspect_interface
from block_test_runtime import McpRuntimeBackend, NativeAudit, NativeToolError, RuntimeCleanupUncertain, redact, verify_memory_card, verify_credential_provisioning
import run_block_tests
import validate_simulation


def temporary_directory():
    root = (run_block_tests.ROOT / "artifacts/block-tests/test-temp").resolve()
    root.mkdir(parents=True, exist_ok=True)
    return tempfile.TemporaryDirectory(dir=root)


def trusted_empty_cpu_evidence():
    pin = "b" * 64
    return {"trustOwnedEmptyCpuCertificate": True, "tlsPinnedCertificateSha256": pin,
        "tlsCertificatePinScope": "currentProvisioningAttemptOnly",
        "tlsVerification": {"attemptPinnedCertificateSha256": pin, "observedCertificateSha256": pin,
            "certificateSha256Matched": True, "certificateValidityVerified": True,
            "ownedTargetRevalidated": True, "trustGranted": True, "certificateIdentityIndependentlyVerified": False,
            "callerTrustAuthorization": True, "authorizationRequested": True,
            "pinPolicyDecision": "allowOwnedEmptyCpuFirstUse", "appliedDecision": "Trusted",
            "certificatesTruncated": False, "certificates": [{"sha256": pin}]}}


class Clock:
    def __init__(self):
        self.now = 0.0

    def clock(self):
        return self.now

    def sleep(self, duration):
        self.now += duration


class Simulator:
    def __init__(self, program):
        self.program = program
        self.values = {item["symbol"]: False if item["type"] == "bool" else 0
                       for bindings in program["bindings"].values() for item in bindings.values()}
        self.values.update({item["symbol"]: 0 for item in program["signals"].values()})
        self.controls = {name: item["symbol"] for name, item in program["signals"].items()}
        self.calls = []
        self.acknowledge = True
        self.bad_quality = False
        self.running_revision = "a" * 64
        self.download_id = 0
        self.download_errors = 0
        self.counter_increment = 1
        self.provision_overrides = {}
        self.storage = (run_block_tests.ROOT / "artifacts/block-tests/fake-instance").resolve()

    def __call__(self, tool, arguments):
        self.calls.append((tool, copy.deepcopy(arguments)))
        if tool == "simulation_discovery_baseline":
            return {"baseline": {"classification": "placeholder", "registeredInstanceCount": 0}}
        if tool == "sim_create":
            return {"instance": {"id": 0, "name": "owned", "owned": True, "operatingState": "Off"}, "downloadTicket": "TOP-SECRET-TICKET"}
        if tool == "simulation_download":
            return {"completed": True, "download": {"state": "Success", "errors": self.download_errors, "truncated": False, "messages": []},
                    "compilation": {"state": "Success", "errors": 0, "truncated": False, "messages": []}, "instanceId": self.download_id,
                    "instanceName": "owned", "programRevision": "a" * 64, "files": [],
                    "reportPath": str(self.storage / "simulation-download.json"), "memoryCardPath": str(self.storage / "SIMATIC_MC")}
        if tool == "simulation_provision_scratch_credential":
            return {"completed": True, "credentialProvisioned": True, "instanceId": 0, "instanceName": "owned",
                    "originalProgramRevision": "a" * 64, "operatingState": "Off", "protectionDisabled": False,
                    "emptyStartupVerified": True, "originalProgramCardRestored": True, "provisioningStrategy": "owned empty CPU then restore original card",
                    "reportPath": str(self.storage / "provision.json"), **self.provision_overrides}
        if tool == "sim_state":
            return {"instance": {"operatingState": "Run", "programEvidence": {"programRevision": self.running_revision}}}
        if tool == "sim_tags":
            return {"tags": [{"symbol": '"' + symbol.split(".", 1)[0] + '".' + symbol.split(".", 1)[1]} for symbol in self.values]}
        if tool == "sim_read":
            return {"instanceId": 0, "values": [{**signal, "value": self.values[signal["symbol"].replace('"', '')],
                    "observedAtUtc": "2026-10-08T00:00:00Z", "quality": "bad" if self.bad_quality else "good"} for signal in arguments["signals"]]}
        if tool == "sim_write":
            for item in arguments["writes"]:
                symbol = item["symbol"].replace('"', '')
                self.values[symbol] = item["value"]
                if symbol == self.controls["requestId"] and self.acknowledge:
                    self.values[self.controls["ackId"]] = item["value"]
                    self.values[self.controls["callCount"]] += self.counter_increment
                    self.values["BlockTest_Data.dut.Active"] = ((self.values["BlockTest_Data.dut.A"] or self.values["BlockTest_Data.dut.B"])
                                                               and not self.values["BlockTest_Data.dut.Inhibit"])
            return {"instanceId": 0, "writes": arguments["writes"]}
        if tool == "sim_release":
            return {"instanceId": 0, "instanceName": "owned", "unregistered": True}
        raise AssertionError(tool)


class BackendTests(unittest.TestCase):
    def setUp(self):
        self.suite = run_block_tests.load_suites(["runtime-boolean"])[0]
        self.program = generate_program(inspect_interface(self.suite["interface"]))
        self.native = Simulator(self.program)
        self.clock = Clock()
        self.backend = McpRuntimeBackend(self.native, self.program, {"sourceRevision": "known"}, "credential.json",
            clock=self.clock.clock, sleep=self.clock.sleep, timeout_ms=100, poll_ms=20,
            card_verifier=lambda result: {"programRevision": result["programRevision"]})

    def test_start_and_one_acknowledged_call_publish_request_last(self):
        evidence = self.backend.start()
        self.assertEqual("fresh_owned_instance", evidence["isolation"])
        self.assertFalse(evidence["capabilities"]["scanSynchronized"])
        self.backend.write({"dut.A": True, "dut.B": False, "dut.Inhibit": False})
        snapshot = self.backend.call("dut")
        self.assertIs(True, snapshot["values"]["dut.Active"])
        self.assertEqual(1, snapshot["controls"]["callCount"])
        writes = [arguments["writes"] for tool, arguments in self.native.calls if tool == "sim_write"]
        self.assertTrue(writes[-2][0]["symbol"].endswith("InstanceIndex"))
        self.assertTrue(writes[-1][0]["symbol"].endswith("RequestId"))
        self.assertEqual(1, writes[-1][0]["value"])
        self.assertTrue(self.backend.close()["completed"])
        self.assertEqual(("sim_release", {"instanceId": 0, "deleteOwned": True}), self.native.calls[-1])
        self.assertNotIn("simulation_provision_scratch_credential", [tool for tool, _ in self.native.calls])
        self.assertNotIn("simulation_discovery_baseline", [tool for tool, _ in self.native.calls])

    def test_discovery_baseline_precedes_registration_and_failure_creates_nothing(self):
        self.backend.provision_runtime_credential = True
        self.backend.card_verifier = mock.Mock(return_value={"programRevision": "a" * 64, "files": []})
        evidence = self.backend.start()
        names = [tool for tool, _ in self.native.calls]
        self.assertEqual(["simulation_discovery_baseline", "sim_create"], names[:2])
        self.assertEqual("placeholder", evidence["discoveryBaseline"]["classification"])
        native = Simulator(self.program)
        def refuse(tool, arguments):
            native.calls.append((tool, copy.deepcopy(arguments)))
            if tool == "simulation_discovery_baseline":
                raise NativeToolError(tool, {"error": {"code": "SimulationDiscoveryBaselineUnavailable"}})
            raise AssertionError(tool)
        backend = McpRuntimeBackend(refuse, self.program, {"sourceRevision": "known"}, "credential.json",
                                    provision_runtime_credential=True)
        with self.assertRaises(NativeToolError):
            backend.start()
        self.assertFalse(backend.identity_verified)
        self.assertEqual(["simulation_discovery_baseline"], [tool for tool, _ in native.calls])
        cleanup = backend.close()  # nothing was created, so cleanup is certain, not "outcome unknown"
        self.assertFalse(cleanup["instanceCreationAttempted"])
        self.assertNotEqual("unknown", cleanup.get("instanceCreationOutcome"))
        self.assertFalse(cleanup["runtimeStateUncertain"])
        self.assertEqual(["simulation_discovery_baseline"], [tool for tool, _ in native.calls])

    def test_opt_in_provisions_after_card_verification_before_normal_power_on(self):
        self.backend.provision_runtime_credential = True
        original_manifest = {"programRevision": "a" * 64, "files": [{"path": "program.bin"}]}
        verification_phases = []
        def verify(download):
            verification_phases.append(self.native.calls[-1][0])
            return copy.deepcopy(original_manifest)
        self.backend.card_verifier = mock.Mock(side_effect=verify)
        evidence = self.backend.start()
        names = [tool for tool, _ in self.native.calls]
        download_index = names.index("simulation_download")
        provision_index = names.index("simulation_provision_scratch_credential")
        self.assertEqual(download_index + 1, provision_index)
        self.assertEqual(self.native.calls[download_index][1], self.native.calls[provision_index][1])
        self.assertEqual("powerOn", self.native.calls[provision_index + 1][1]["action"])
        self.assertEqual(["simulation_download", "simulation_provision_scratch_credential"], verification_phases)
        self.assertEqual(2, self.backend.card_verifier.call_count)
        self.assertEqual(original_manifest, evidence["memoryCard"])
        self.assertEqual(original_manifest, evidence["restoredMemoryCard"])
        self.assertEqual("a" * 64, evidence["credentialProvisioning"]["originalProgramRevision"])
        self.backend.close()

    def test_invalid_provisioning_evidence_stops_power_on_and_preserves_cleanup(self):
        self.backend.provision_runtime_credential = True
        self.native.provision_overrides = {"originalProgramRevision": "b" * 64}
        with self.assertRaisesRegex(RuntimeError, "original generated-card revision"):
            self.backend.start()
        self.assertFalse(any(tool == "sim_state" for tool, _ in self.native.calls))
        with self.assertRaises(RuntimeCleanupUncertain):
            self.backend.close()
        self.assertEqual(("sim_release", {"instanceId": 0, "deleteOwned": True}), self.native.calls[-1])

    def test_tls_trust_is_opt_in_and_only_passed_to_provisioning(self):
        self.backend = McpRuntimeBackend(self.native, self.program, {}, "credential.json",
            provision_runtime_credential=True, trust_owned_empty_cpu_certificate=True,
            card_verifier=lambda result: {"programRevision": result["programRevision"]})
        self.native.provision_overrides = trusted_empty_cpu_evidence()
        evidence = self.backend.start()
        calls = dict(self.native.calls)
        self.assertEqual({**calls["simulation_download"], "trustOwnedEmptyCpuCertificate": True},
                         calls["simulation_provision_scratch_credential"])
        self.assertTrue(evidence["trustOwnedEmptyCpuCertificate"])
        self.assertFalse(evidence["credentialProvisioning"]["tlsVerification"]["certificateIdentityIndependentlyVerified"])
        self.backend.close()

    def test_unrequested_tls_trust_blocks_protected_power_on(self):
        self.backend.provision_runtime_credential = True
        self.native.provision_overrides = trusted_empty_cpu_evidence()
        with self.assertRaisesRegex(RuntimeError, "without explicit"):
            self.backend.start()
        self.assertFalse(any(tool == "sim_state" for tool, _ in self.native.calls))
        with self.assertRaises(RuntimeCleanupUncertain):
            self.backend.close()

    def test_tls_trust_constructor_requires_explicit_provisioning_and_boolean(self):
        for value in (True, "true", 1):
            with self.subTest(value=value), self.assertRaises(ValueError):
                McpRuntimeBackend(self.native, self.program, {}, "credential.json", trust_owned_empty_cpu_certificate=value)

    def test_restore_claim_does_not_bypass_independent_original_card_hash_check(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            card = root / "SIMATIC_MC"
            card.mkdir()
            binary = card / "program.bin"
            binary.write_bytes(b"original")
            manifest = [{"path": "program.bin", "bytes": 8, "sha256": hashlib.sha256(b"original").hexdigest()}]
            revision = hashlib.sha256(json.dumps(manifest, separators=(",", ":")).encode()).hexdigest()
            self.native.storage = root
            self.native.provision_overrides = {"originalProgramRevision": revision}
            original_call = self.backend.native
            def native(tool, arguments):
                result = original_call(tool, arguments)
                if tool == "simulation_download":
                    result.update(programRevision=revision, files=manifest)
                elif tool == "simulation_provision_scratch_credential":
                    binary.write_bytes(b"modified")
                return result
            self.backend.native = native
            self.backend.card_verifier = verify_memory_card
            self.backend.provision_runtime_credential = True
            with self.assertRaisesRegex(ValueError, "differs from the download manifest"):
                self.backend.start()
            self.assertEqual(revision, self.backend.evidence["memoryCard"]["programRevision"])
            self.assertNotIn("restoredMemoryCard", self.backend.evidence)
            self.assertFalse(any(tool == "sim_state" for tool, _ in self.native.calls))
            with self.assertRaises(RuntimeCleanupUncertain):
                self.backend.close()
            self.assertEqual(("sim_release", {"instanceId": 0, "deleteOwned": True}), self.native.calls[-1])

    def test_failed_restoration_claim_prevents_normal_power_on(self):
        self.backend.provision_runtime_credential = True
        self.native.provision_overrides = {"originalProgramCardRestored": False}
        with self.assertRaisesRegex(RuntimeError, "restoration of the original program card"):
            self.backend.start()
        self.assertFalse(any(tool == "sim_state" for tool, _ in self.native.calls))
        with self.assertRaises(RuntimeCleanupUncertain) as caught:
            self.backend.close()
        self.assertTrue(caught.exception.cleanup["release"]["unregistered"])
        self.assertTrue(caught.exception.cleanup["runtimeStateUncertain"])

    def test_timeout_does_not_retry_or_let_partial_case_continue(self):
        self.backend.start()
        self.native.acknowledge = False
        with self.assertRaises(TimeoutError):
            self.backend.call("dut")
        with self.assertRaises(RuntimeError):
            self.backend.write({"dut.A": True})
        self.assertEqual(1, sum(item["symbol"].endswith("RequestId") for tool, args in self.native.calls
                                if tool == "sim_write" for item in args["writes"]))
        self.backend.close()

    def test_extra_application_call_is_detected(self):
        self.backend.start()
        self.native.counter_increment = 2
        with self.assertRaisesRegex(RuntimeError, "exact call count"):
            self.backend.call("dut")
        self.backend.close()

    def test_fresh_counters_prove_initial_state(self):
        self.native.values[self.native.controls["callCount"]] = 2
        with self.assertRaisesRegex(RuntimeError, "initial state"):
            self.backend.start()
        self.backend.close()
        self.assertEqual("sim_release", self.native.calls[-1][0])

    def test_revision_mismatch_is_blocked_before_stimuli(self):
        self.native.running_revision = "b" * 64
        with self.assertRaisesRegex(RuntimeError, "does not match"):
            self.backend.start()
        self.backend.close()
        self.assertFalse(any(tool == "sim_write" for tool, _ in self.native.calls))

    def test_download_identity_mismatch_releases_id_zero(self):
        for invalid in (1, False, 0.0, "0", None):
            with self.subTest(invalid=invalid):
                self.setUp()
                self.native.download_id = invalid
                with self.assertRaisesRegex(RuntimeError, "different simulator"):
                    self.backend.start()
                with self.assertRaises(RuntimeCleanupUncertain):
                    self.backend.close()
                self.assertEqual(0, self.native.calls[-1][1]["instanceId"])
                self.assertFalse(any(tool == "sim_state" for tool, _ in self.native.calls))

    def test_download_error_count_requires_integer_zero(self):
        for invalid in (False, 0.0, "0", None, -1, 1):
            with self.subTest(invalid=invalid):
                self.setUp()
                self.native.download_errors = invalid
                with self.assertRaisesRegex(RuntimeError, "did not complete"):
                    self.backend.start()
                with self.assertRaises(RuntimeCleanupUncertain):
                    self.backend.close()
                self.assertFalse(any(tool == "sim_state" for tool, _ in self.native.calls))

    def test_bad_quality_is_not_a_value(self):
        self.backend.start()
        self.native.bad_quality = True
        with self.assertRaisesRegex(RuntimeError, "bad-quality"):
            self.backend.read()
        self.backend.close()

    def test_only_input_and_inout_bindings_are_writable(self):
        self.backend.start()
        with self.assertRaisesRegex(ValueError, "Not a writable"):
            self.backend.write({"dut.Active": True})
        self.assertFalse(any(tool == "sim_write" for tool, _ in self.native.calls))
        self.backend.close()

    def test_used_backend_cannot_be_restarted(self):
        self.backend.start()
        self.backend.close()
        with self.assertRaisesRegex(RuntimeError, "reused"):
            self.backend.start()


class EvidenceTests(unittest.TestCase):
    def test_authorized_tls_trust_requires_exact_attempt_pin_and_owned_target_evidence(self):
        native = Simulator({"bindings": {}, "signals": {}})
        download = native("simulation_download", {})
        result = {**native("simulation_provision_scratch_credential", {}), **trusted_empty_cpu_evidence()}
        self.assertEqual(result, verify_credential_provisioning(result, download, trust_owned_empty_cpu_certificate=True))
        for policy in ("allowOwnedEmptyCpuFirstUse", "allowSameAttemptPinnedCertificate"):
            candidate = copy.deepcopy(result)
            candidate["tlsVerification"]["pinPolicyDecision"] = policy
            verify_credential_provisioning(candidate, download, trust_owned_empty_cpu_certificate=True)
        for key, value in (("attemptPinnedCertificateSha256", "c" * 64), ("observedCertificateSha256", "c" * 64),
                ("certificateSha256Matched", False), ("certificateValidityVerified", False), ("ownedTargetRevalidated", False),
                ("trustGranted", False), ("callerTrustAuthorization", False), ("authorizationRequested", False),
                ("certificateIdentityIndependentlyVerified", True), ("pinPolicyDecision", "allowAny"),
                ("appliedDecision", "NonTrusted"), ("certificatesTruncated", True),
                ("certificates", []), ("certificates", [{"sha256": "c" * 64}]), ("certificates", [None])):
            candidate = copy.deepcopy(result)
            candidate["tlsVerification"][key] = value
            with self.subTest(key=key, value=value), self.assertRaisesRegex(RuntimeError, "attempt pin"):
                verify_credential_provisioning(candidate, download, trust_owned_empty_cpu_certificate=True)
        for key, value in (("trustOwnedEmptyCpuCertificate", False), ("tlsPinnedCertificateSha256", "x" * 64),
                ("tlsCertificatePinScope", "permanent")):
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                verify_credential_provisioning({**result, key: value}, download, trust_owned_empty_cpu_certificate=True)

    def test_provisioning_requires_exact_identity_revision_protection_and_completion(self):
        storage = (run_block_tests.ROOT / "artifacts/block-tests/fake-instance").resolve()
        download = {"instanceId": 0, "instanceName": "owned", "programRevision": "a" * 64,
                    "reportPath": str(storage / "simulation-download.json"), "memoryCardPath": str(storage / "SIMATIC_MC")}
        result = {"completed": True, "credentialProvisioned": True, "instanceId": 0, "instanceName": "owned",
                  "originalProgramRevision": "a" * 64, "operatingState": "Off", "protectionDisabled": False,
                  "emptyStartupVerified": True, "originalProgramCardRestored": True, "provisioningStrategy": "owned empty CPU then restore original card",
                  "reportPath": str(storage / "provision.json")}
        self.assertEqual(result, verify_credential_provisioning(result, download))
        for invalid_id in (False, 0.0):
            with self.subTest(download_id=invalid_id), self.assertRaises(RuntimeError):
                verify_credential_provisioning(result, {**download, "instanceId": invalid_id})
        for invalid in ({"completed": False}, {"credentialProvisioned": 1}, {"instanceId": False},
                        {"instanceId": 1}, {"instanceName": "other"}, {"originalProgramRevision": "b" * 64},
                        {"operatingState": "Stop"}, {"protectionDisabled": True}, {"protectionDisabled": 0},
                        {"emptyStartupVerified": False}, {"emptyStartupVerified": 1}, {"originalProgramCardRestored": False},
                        {"originalProgramCardRestored": 1}, {"provisioningStrategy": ""},
                        {"reportPath": ""}, {"reportPath": "provision.json"},
                        {"reportPath": str(storage.parent / "other-instance/provision.json")},
                        {"reportPath": str(storage / "SIMATIC_MC/provision.json")}):
            with self.subTest(invalid=invalid), self.assertRaises(RuntimeError):
                verify_credential_provisioning({**result, **invalid}, download)
        with self.assertRaisesRegex(RuntimeError, "exact downloaded instance storage"):
            verify_credential_provisioning(result, {**download, "memoryCardPath": str(storage.parent / "other-instance/SIMATIC_MC")})

    def test_provisioning_tool_is_allowed_without_becoming_a_default_required_tool(self):
        name = "simulation_provision_scratch_credential"
        self.assertNotIn(name, NativeAudit.ALLOWED_TOOLS)
        client = mock.Mock()
        client.request.return_value = {"structuredContent": {"completed": True}}
        self.assertEqual({"completed": True}, NativeAudit(client, [], lambda: None)(name))

    def test_ticket_and_credentials_do_not_enter_audit(self):
        records = []
        ticket = "private-value-to-redact"
        class Client:
            def request(self, method, params):
                if params["name"] == "sim_create":
                    return {"structuredContent": {"downloadTicket": ticket, "password": "hidden"}}
                raise RuntimeError("failure containing " + ticket)
        audit = NativeAudit(Client(), records, lambda: None)
        result = audit("sim_create")
        self.assertEqual(ticket, result["downloadTicket"])
        with self.assertRaises(NativeToolError) as error:
            audit("simulation_download", {"downloadTicket": ticket})
        self.assertNotIn(ticket, json.dumps(records))
        self.assertNotIn(ticket, str(error.exception))
        self.assertNotIn(ticket, json.dumps(error.exception.result))
        self.assertEqual("<redacted>", records[0]["result"]["password"])

    def test_plain_mcp_argument_error_is_retained_in_audit(self):
        client = mock.Mock()
        message = "An error occurred invoking 'block_read': maxItems must be between 1 and 2000."
        client.request.return_value = {"content": [{"type": "text", "text": message}], "isError": True}
        records = []
        with self.assertRaises(NativeToolError) as error:
            NativeAudit(client, records, lambda: None)("block_read", {"maxItems": 10000})
        self.assertEqual("McpToolError", error.exception.result["error"]["code"])
        self.assertEqual(message, records[0]["result"]["error"]["message"])
        self.assertEqual("failed", records[0]["status"])
        self.assertIn("completedAtUtc", records[0])

    def test_scope_rejects_existing_project_or_physical_route(self):
        client = mock.Mock()
        audit = NativeAudit(client, [], lambda: None)
        for name in ("project_open", "tia_attach", "download", "sim_select"):
            with self.assertRaises(ValueError):
                audit(name)
        client.request.assert_not_called()

    def test_card_mutation_or_extra_file_invalidates_revision(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            (root / "program.bin").write_bytes(b"image")
            entries = [{"path": "program.bin", "bytes": 5, "sha256": hashlib.sha256(b"image").hexdigest()}]
            revision = hashlib.sha256(json.dumps(entries, separators=(",", ":")).encode()).hexdigest()
            download = {"memoryCardPath": str(root), "programRevision": revision, "files": entries}
            verified = verify_memory_card(download)
            self.assertTrue(verified["verifiedBeforePowerOn"])
            self.assertFalse(verified["runtimeReadbackVerified"])
            with self.assertRaisesRegex(ValueError, "revision does not match"):
                verify_memory_card({**download, "programRevision": "a" * 64})
            (root / "other.bin").write_bytes(b"retained")
            with self.assertRaisesRegex(ValueError, "differs"):
                verify_memory_card(download)
            (root / "other.bin").unlink()
            (root / "program.bin").write_bytes(b"wrong")
            with self.assertRaisesRegex(ValueError, "differs"):
                verify_memory_card(download)

    def test_manifest_traversal_is_rejected(self):
        with temporary_directory() as directory:
            with self.assertRaisesRegex(ValueError, "manifest path"):
                verify_memory_card({"memoryCardPath": directory, "programRevision": "a" * 64,
                                    "files": [{"path": "../outside", "bytes": 0, "sha256": "b" * 64}]})

    def test_manifest_byte_count_requires_nonnegative_integer(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            for data in (b"", b"x"):
                (root / "program.bin").write_bytes(data)
                valid = [{"path": "program.bin", "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}]
                valid_revision = hashlib.sha256(json.dumps(valid, separators=(",", ":")).encode()).hexdigest()
                self.assertTrue(verify_memory_card({"memoryCardPath": str(root), "programRevision": valid_revision,
                                                   "files": valid})["verifiedBeforePowerOn"])
                for count in (bool(len(data)), float(len(data)), str(len(data)), None, -1):
                    with self.subTest(data=data, count=count):
                        entries = [{"path": "program.bin", "bytes": count, "sha256": hashlib.sha256(data).hexdigest()}]
                        revision = hashlib.sha256(json.dumps(entries, separators=(",", ":")).encode()).hexdigest()
                        with self.assertRaisesRegex(ValueError, "byte count"):
                            verify_memory_card({"memoryCardPath": str(root), "programRevision": revision, "files": entries})


class CliTests(unittest.TestCase):
    def test_authored_signal_names_do_not_change_archived_revision(self):
        from run_block_tests import save_json
        with temporary_directory() as directory:
            path = pathlib.Path(directory) / "specification.json"
            specification = {"initial": {"dut.Token": 7, "dut.PasswordLength": 4}}
            save_json(path, specification)
            self.assertEqual(specification, json.loads(path.read_text(encoding="utf-8")))
            self.assertEqual(run_block_tests.digest(specification), hashlib.sha256(path.read_bytes()).hexdigest())

    def test_default_prepares_all_families_without_client_or_tools(self):
        with temporary_directory() as directory, mock.patch.object(run_block_tests, "Client") as client, mock.patch.object(run_block_tests, "qualify_suite") as live:
            self.assertEqual(0, run_block_tests.main(["--report-directory", directory]))
            index = json.loads((pathlib.Path(directory) / "index.json").read_text())
            self.assertFalse(index["runtimeVerified"])
            self.assertEqual(9, len(index["results"]))
            client.assert_not_called()
            live.assert_not_called()
            for entry in index["results"]:
                root = pathlib.Path(directory) / entry["fixtureId"]
                revision = json.loads((root / "revision.json").read_text())
                self.assertEqual(revision["suiteSha256"], hashlib.sha256((root / "suite.json").read_bytes()).hexdigest())
                for source in revision["sources"]:
                    self.assertEqual(source["sha256"], hashlib.sha256((root / source["path"]).read_bytes()).hexdigest())

    def test_custom_suite_uses_same_validation_and_archiving(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            source = root / "custom.json"
            suite = run_block_tests.load_suites(["runtime-boolean"])[0]
            suite["fixtureId"] = "custom-boolean"
            source.write_text(json.dumps(suite), encoding="utf-8")
            self.assertEqual(0, run_block_tests.main(["--suite", str(source), "--report-directory", str(root / "output")]))
            index = json.loads((root / "output/index.json").read_text())
            self.assertEqual(["custom-boolean"], [item["fixtureId"] for item in index["results"]])

    def test_existing_evidence_is_not_overwritten(self):
        with temporary_directory() as directory:
            path = pathlib.Path(directory) / "index.json"
            path.write_text("retained")
            with self.assertRaisesRegex(ValueError, "never overwritten"):
                run_block_tests.main(["--fixture", "runtime-boolean", "--report-directory", directory])
            self.assertEqual("retained", path.read_text())

class BaselineCliTests(unittest.TestCase):
    def run_baseline(self, root, provision=False, provision_overrides=None, restored_card_error=False,
                     trust=False, expected_card_verifications=None, download_overrides=None, created_overrides=None):
        project = root / "TiaMcpScratch_Test.ap21"
        metadata, journal = root / "credential.json", root / "journal.json"
        for path in (project, metadata, journal):
            path.write_text("fixture", encoding="utf-8")
        report_path = root / "report.json"
        events, requests = [], []
        download = {"completed": True, "download": {"state": "Success", "errors": 0, "truncated": False, "messages": []},
                    "compilation": {"state": "Success", "errors": 0, "truncated": False, "messages": []}, "instanceId": 0,
                    "instanceName": "owned", "programRevision": "a" * 64, "files": [],
                    "reportPath": str(root / "simulation-download.json"), "memoryCardPath": str(root / "SIMATIC_MC"),
                    **(download_overrides or {})}
        class Client:
            process = types.SimpleNamespace(returncode=0)
            def notify(self, *args):
                pass
            def close(self):
                pass
            def request(self, method, params=None):
                if method == "initialize":
                    return {}
                tool, arguments = params["name"], params["arguments"]
                events.append(tool)
                requests.append((tool, copy.deepcopy(arguments)))
                if tool == "simulation_discovery_baseline":
                    result = {"baseline": {"classification": "placeholder", "registeredInstanceCount": 0}}
                elif tool == "sim_create":
                    result = {"instance": {"id": 0, "name": "owned", "owned": True, "operatingState": "Off", **(created_overrides or {})},
                              "downloadTicket": "PRIVATE-TICKET"}
                elif tool == "simulation_download":
                    result = download
                elif tool == "simulation_provision_scratch_credential":
                    result = {"completed": True, "credentialProvisioned": True, "instanceId": 0, "instanceName": "owned",
                              "originalProgramRevision": "a" * 64, "operatingState": "Off", "protectionDisabled": False,
                              "emptyStartupVerified": True, "originalProgramCardRestored": True, "provisioningStrategy": "owned empty CPU then restore original card",
                              "reportPath": str(root / "provision.json"), **(provision_overrides or {})}
                elif tool == "sim_state":
                    result = {"instance": {"operatingState": "Run"}}
                elif tool == "sim_tags":
                    result = {"tags": [{"symbol": "TiaMcp_TestIO." + member}
                                       for member in ("Enable", "Active", "AnalogIn", "AnalogOut")]}
                elif tool == "sim_read":
                    result = {"values": [{**signal, "value": False if signal["type"] == "bool" else 0,
                                          "quality": "good"} for signal in arguments["signals"]]}
                elif tool == "sim_test":
                    result = {"passed": True, "completed": True, "assertions": [{"passed": True}] * 8,
                              "reportPath": "scenario.json"}
                elif tool == "sim_release":
                    result = {"unregistered": True}
                else:
                    result = {"ok": True}
                return {"structuredContent": result}
        def verify_card(value):
            events.append("verify_memory_card")
            self.assertEqual(download, value)
            if restored_card_error and events.count("verify_memory_card") == 2:
                raise ValueError("Generated memory-card content differs from the download manifest.")
            return {"programRevision": value["programRevision"], "verifiedBeforePowerOn": True}
        args = ["--project", str(project), "--credential-metadata", str(metadata), "--credential-journal", str(journal),
                "--report", str(report_path), "--host", str(root / "host.exe"),
                "--worker", str(root / "engineering.exe"), "--sim-worker", str(root / "simulator.exe")]
        if provision:
            args.append("--provision-runtime-credential")
        if trust:
            args.append("--trust-owned-empty-cpu-certificate")
        with mock.patch.object(validate_simulation, "Client", return_value=Client()) as factory, \
                mock.patch.object(validate_simulation, "verify_memory_card", side_effect=verify_card) as verifier:
            exit_code = validate_simulation.main(args)
            factory.assert_called_once_with((root / "host.exe").resolve(), 300,
                ["--worker", str((root / "engineering.exe").resolve()), "--sim-worker", str((root / "simulator.exe").resolve())])
            expected_count = ((1 if provision_overrides else 2) if provision else 0) if expected_card_verifications is None else expected_card_verifications
            self.assertEqual(expected_count, verifier.call_count)
        report = json.loads(report_path.read_text(encoding="utf-8"))
        self.assertNotIn("PRIVATE-TICKET", json.dumps(report))
        return exit_code, report, events, requests

    def test_provisioning_opt_in_carries_exact_credential_lineage_and_preserves_baseline(self):
        with temporary_directory() as directory:
            exit_code, report, events, requests = self.run_baseline(pathlib.Path(directory), provision=True)
            self.assertEqual(0, exit_code)
            self.assertTrue(report["simulationVerified"])
            self.assertTrue(report["provisionRuntimeCredential"])
            self.assertEqual(events.index("simulation_discovery_baseline") + 1, events.index("sim_create"))
            index = events.index("simulation_download")
            self.assertEqual(["simulation_download", "verify_memory_card", "simulation_provision_scratch_credential", "verify_memory_card", "sim_state"], events[index:index + 5])
            self.assertEqual(report["memoryCard"], report["restoredMemoryCard"])
            by_tool = dict(requests)
            self.assertEqual(by_tool["simulation_download"], by_tool["simulation_provision_scratch_credential"])
            self.assertEqual(1, len(by_tool["simulation_provision_scratch_credential"]["credentialLineageJournalPaths"]))

    def test_default_uses_normal_startup_without_provisioning(self):
        with temporary_directory() as directory:
            exit_code, report, events, _ = self.run_baseline(pathlib.Path(directory))
            self.assertEqual(0, exit_code)
            self.assertFalse(report["provisionRuntimeCredential"])
            self.assertFalse(report["trustOwnedEmptyCpuCertificate"])
            self.assertNotIn("simulation_provision_scratch_credential", events)

    def test_bad_download_evidence_stops_both_modes_before_power_on(self):
        for provision in (False, True):
            for invalid in ({"instanceId": False}, {"instanceId": 0.0}, {"instanceName": "other"},
                            {"download": {"errors": False}}, {"download": {"errors": 0.0}}, {"completed": 1}):
                with self.subTest(provision=provision, invalid=invalid), temporary_directory() as directory:
                    code, report, events, _ = self.run_baseline(pathlib.Path(directory), provision=provision,
                        download_overrides=invalid, expected_card_verifications=0)
                    self.assertEqual(1, code)
                    self.assertFalse(report["completed"])
                    self.assertNotIn("sim_state", events)
                    self.assertNotIn("simulation_provision_scratch_credential", events)
                    self.assertIn("sim_release", events)

    def test_bad_created_identity_is_not_used_for_download_or_cleanup(self):
        for invalid in ({"id": False}, {"id": 0.0}, {"id": -1}, {"owned": 1}, {"name": ""}):
            with self.subTest(invalid=invalid), temporary_directory() as directory:
                code, report, events, _ = self.run_baseline(pathlib.Path(directory), created_overrides=invalid)
                self.assertEqual(1, code)
                self.assertFalse(report["completed"])
                for tool in ("simulation_download", "sim_state", "sim_release"):
                    self.assertNotIn(tool, events)

    def test_incomplete_download_diagnostics_stop_both_modes_before_power_on(self):
        clean = {"state": "Success", "errors": 0, "truncated": False, "messages": []}
        defects = [None, {}, {"errors": 0}, {**clean, "state": "Error"}, {**clean, "state": "Unknown"},
                   {**clean, "truncated": True}, {**clean, "truncated": "false"}, {**clean, "messages": None},
                   {**clean, "messages": [{"state": "Error"}]},
                   {**clean, "messages": [{"state": "Information", "children": [{"state": "Error"}]}]}]
        for provision in (False, True):
            for field in ("compilation", "download"):
                for value in defects:
                    with self.subTest(provision=provision, field=field, value=value), temporary_directory() as directory:
                        code, report, events, _ = self.run_baseline(pathlib.Path(directory), provision=provision,
                            download_overrides={field: value}, expected_card_verifications=0)
                        self.assertEqual(1, code)
                        self.assertFalse(report["completed"])
                        self.assertNotIn("sim_state", events)
                        self.assertNotIn("simulation_provision_scratch_credential", events)

    def test_complete_download_diagnostics_preserve_success_information_and_warning(self):
        for state in ("Success", "Information", "Warning"):
            with self.subTest(state=state), temporary_directory() as directory:
                result = {"state": state, "errors": 0, "truncated": False, "messages": [{"state": state}]}
                code, report, _, _ = self.run_baseline(pathlib.Path(directory),
                    download_overrides={"download": result, "compilation": result})
                self.assertEqual(0, code)
                self.assertTrue(report["completed"])

    def test_tls_first_use_flag_is_recorded_and_passed_only_to_provisioning(self):
        with temporary_directory() as directory:
            exit_code, report, events, requests = self.run_baseline(pathlib.Path(directory), provision=True, trust=True,
                provision_overrides=trusted_empty_cpu_evidence(), expected_card_verifications=2)
            self.assertEqual(0, exit_code)
            self.assertTrue(report["trustOwnedEmptyCpuCertificate"])
            calls = dict(requests)
            self.assertNotIn("trustOwnedEmptyCpuCertificate", calls["simulation_download"])
            self.assertEqual({**calls["simulation_download"], "trustOwnedEmptyCpuCertificate": True},
                             calls["simulation_provision_scratch_credential"])
            self.assertEqual(2, events.count("verify_memory_card"))

    def test_tls_first_use_requires_provisioning_before_native_work(self):
        with mock.patch.object(validate_simulation, "Client") as native:
            with self.assertRaises(SystemExit) as error:
                validate_simulation.main(["--project", "unused", "--credential-metadata", "unused",
                    "--trust-owned-empty-cpu-certificate"])
            self.assertEqual(2, error.exception.code)
            native.assert_not_called()

    def test_trust_without_valid_evidence_stops_before_normal_power_on(self):
        with temporary_directory() as directory:
            exit_code, report, events, requests = self.run_baseline(pathlib.Path(directory), provision=True,
                trust=True, expected_card_verifications=1)
            self.assertEqual(1, exit_code)
            self.assertFalse(report["simulationVerified"])
            self.assertNotIn("sim_state", events)
            self.assertEqual(("sim_release", {"instanceId": 0, "deleteOwned": True}), requests[-2])

    def test_invalid_provisioning_fails_before_normal_power_on_and_cleans_up_id_zero(self):
        with temporary_directory() as directory:
            exit_code, report, events, requests = self.run_baseline(pathlib.Path(directory), provision=True,
                provision_overrides={"protectionDisabled": True})
            self.assertEqual(1, exit_code)
            self.assertFalse(report["simulationVerified"])
            self.assertNotIn("sim_state", events)
            self.assertEqual(1, events.count("simulation_provision_scratch_credential"))
            self.assertEqual(("sim_release", {"instanceId": 0, "deleteOwned": True}), requests[-2])
            self.assertEqual("tia_detach", requests[-1][0])

    def test_baseline_rejects_restored_card_hash_failure_before_power_on(self):
        with temporary_directory() as directory:
            exit_code, report, events, requests = self.run_baseline(pathlib.Path(directory), provision=True,
                restored_card_error=True)
            self.assertEqual(1, exit_code)
            self.assertFalse(report["simulationVerified"])
            self.assertNotIn("sim_state", events)
            self.assertIn("credentialProvisioning", report)
            self.assertNotIn("restoredMemoryCard", report)
            self.assertEqual(2, events.count("verify_memory_card"))
            self.assertEqual(("sim_release", {"instanceId": 0, "deleteOwned": True}), requests[-2])


if __name__ == "__main__":
    unittest.main()
