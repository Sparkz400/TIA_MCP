"""Staged-runner regressions using a file-backed MCP double; never start TIA or PLCSIM."""
import copy
import hashlib
import json
import pathlib
import tempfile
import types
import unittest
from unittest import mock
from xml.sax.saxutils import escape

from block_test_runtime import NativeAudit
import run_block_tests as runner


ENGINEERING_TOOLS = frozenset({"tia_start", "tia_create_scratch", "tia_detach", "project_open",
    "change_prepare", "change_apply", "change_status", "block_interface", "block_read", "type_read"})


class ProcessWatcher:
    def __init__(self, pid):
        self.evidence = {"processId": pid, "handleCaptured": True, "access": "SYNCHRONIZE", "inheritable": False}

    def wait_for_exit(self, timeout_ms):
        return {**self.evidence, "completed": True, "processExitVerified": True, "handleClosed": True,
                "waitResult": 0, "projectLockReleaseVerified": False}

    def close(self):
        return True


def temporary_directory():
    root = (runner.ROOT / "artifacts/block-tests/test-temp").resolve()
    root.mkdir(parents=True, exist_ok=True)
    return tempfile.TemporaryDirectory(dir=root)


def write_binaries(root):
    paths = []
    for role in ("host", "worker", "sim-worker"):
        directory = root / role
        directory.mkdir()
        path = directory / (role + ".exe")
        path.write_bytes((role + "-executable").encode())
        (directory / (role + ".dll")).write_bytes((role + "-assembly").encode())
        (directory / (role + ".exe.config")).write_text("<configuration/>")
        (directory / (role + ".runtimeconfig.json")).write_text("{}")
        (directory / (role + ".pdb")).write_bytes(b"symbols")
        paths.append(path)
    return paths


class NativeClient:
    """Produce native-shaped exports with an independently readable artifact for every call."""
    def __init__(self, suite, root, *, runtime=False, fault=None, exit_code=0, provisioning=False):
        self.suite = suite
        self.root = root
        root.mkdir(parents=True, exist_ok=True)
        self.project_path = root / "owned.ap21"
        self.project_path.write_bytes(b"owned scratch project")
        self.runtime = runtime
        self.provisioning = provisioning
        self.fault = fault
        self.process = types.SimpleNamespace(returncode=exit_code)
        self.calls = []
        self.selections = []
        self.reopened = False
        self.closed = False

    def factory(self, executable, timeout, extra_arguments=None):
        self.selections.append((pathlib.Path(executable), timeout, list(extra_arguments or [])))
        return self

    def notify(self, *args):
        pass

    def close(self):
        self.closed = True
        if self.fault == "binary_changed":
            self.selections[0][0].write_bytes(b"replaced executable")

    def project(self):
        return {"name": "owned", "path": str(self.project_path),
                "isModified": self.reopened and self.fault == "modified_project"}

    def export(self, name, kind):
        changed = self.reopened and self.fault == "changed_" + kind
        xml = ('<?xml version="1.0" encoding="utf-8"?><Document>'
               '<Engineering version="V21"/><DocumentInfo><Created>'
               + str(len(self.calls)) + '</Created></DocumentInfo>'
               '<SW.' + ("Types.PlcStruct" if kind == "type" else "Blocks.FC")
               + ' ID="0"><AttributeList><Name>' + escape(name)
               + '</Name><Body>' + ("changed" if changed else "retained")
               + '</Body></AttributeList></SW.'
               + ("Types.PlcStruct" if kind == "type" else "Blocks.FC") + '></Document>')
        content = xml.encode("utf-8")
        path = self.root / (str(len(self.calls)) + "-" + name + ".xml")
        path.write_bytes(content)
        artifact = {"path": str(path), "bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()}
        if self.fault == "corrupt_artifact":
            path.write_bytes(b"tampered after native response")
        inconsistent = (kind == "block" and self.fault == "inconsistent_block" or
                        kind == "type" and (self.fault == "inconsistent_type" or
                                            self.reopened and self.fault == "reopened_inconsistent_type"))
        return {"xml": xml, "artifact": artifact, "project": self.project(),
                kind: {"name": name, "isConsistent": not inconsistent},
                "completeness": {"complete": True, "scopeComplete": True, "truncated": False}}

    def request(self, method, params=None):
        if method == "initialize":
            return {}
        if method == "tools/list":
            allowed = ENGINEERING_TOOLS | (NativeAudit.ALLOWED_TOOLS if self.runtime else set())
            if self.provisioning:
                allowed |= {"simulation_discovery_baseline", "simulation_provision_scratch_credential"}
            return {"tools": [{"name": name} for name in allowed]}
        if method != "tools/call":
            raise AssertionError(method)
        tool, arguments = params["name"], params["arguments"]
        self.calls.append((tool, copy.deepcopy(arguments)))
        if not self.runtime and (tool.startswith("sim_") or tool == "simulation_download"):
            raise AssertionError("Engineering run attempted PLCSIM: " + tool)
        if tool == "tia_start":
            result = {"attached": True, "ownsPortalProcess": True, "processId": 100 + len(self.calls)}
        elif tool == "tia_create_scratch":
            result = {"completed": True,
                      "compile": {"errorCount": False if self.fault == "scratch_boolean_error_count" else 0},
                      "project": self.project()}
            if self.runtime:
                result["scratchSecurity"] = {"cpus": [{"credentialMetadataPath": "scratch-credentials/test.json"}]}
        elif tool == "tia_detach":
            result = {"attached": False, "disposedOwnedPortalConnection": self.fault != "detach_not_disposed"}
        elif tool == "project_open":
            if self.fault == "open_failure":
                return {"isError": True, "structuredContent": {"error": {"code": "ProjectOpenFailed"}}}
            if arguments != {"path": str(self.project_path)}:
                raise AssertionError("Reopen must target the exact freshly created owned scratch path.")
            self.reopened = True
            result = {"opened": True, "saved": False, "project": self.project()}
        elif tool == "change_prepare":
            if not self.runtime and arguments["kind"] == "project_simulation_enable":
                raise AssertionError("Engineering run requested simulation compilation.")
            result = {"changeId": "preview", "previewHash": "hash"}
        elif tool == "change_apply":
            error_count = False if self.fault == "apply_boolean_error_count" else 1 if self.fault == "compile_error" else 0
            result = {"state": "applied", "compile": {"errorCount": error_count},
                      "stages": [] if self.fault == "missing_save" else ["changed_project_saved"]}
        elif tool == "change_status":
            result = {"state": "failed"}
        elif tool == "block_interface":
            name = arguments["blockName"]
            definition = (self.suite["interface"] if name == self.suite["blockName"]
                          else self.suite.get("typeDefinitions", {}).get(name,
                               {"block": {"name": name, "kind": "DB"}, "objectKinds": ["SW.Blocks.GlobalDB"], "sections": []}))
            result = {**self.export(name, "block"), **copy.deepcopy(definition)}
        elif tool == "type_read":
            name = arguments["typeName"]
            definition = self.suite["typeDefinitions"][name]
            result = {**self.export(name, "type"), "objectKinds": ["SW.Types.PlcStruct"],
                      "sections": [{"name": "None", "members": copy.deepcopy(definition)}]}
        elif tool == "block_read":
            result = self.export(arguments["blockName"], "block")
        else:
            raise AssertionError(tool)
        return {"structuredContent": result}


class EngineeringStageTests(unittest.TestCase):
    def qualify(self, root, suite=None, *, fault=None, exit_code=0, runtime=False, execute=None, provision=False, trust=False,
                watcher_factory=ProcessWatcher):
        suite = suite or runner.load_suites(["runtime-boolean"])[0]
        host, worker, sim_worker = write_binaries(root)
        native = NativeClient(suite, root / "native", runtime=runtime, fault=fault, exit_code=exit_code,
                              provisioning=provision and fault != "missing_provision_operation")
        output = root / "report"
        runner.prepare_suite(suite, output)
        report = runner.qualify_suite(suite, output, host, 1, engineering_only=not runtime,
            worker=worker, sim_worker=sim_worker if runtime else None, client_factory=native.factory, execute=execute,
            provision_runtime_credential=provision, trust_owned_empty_cpu_certificate=trust,
            process_watcher_factory=watcher_factory)
        return report, native, output

    def test_qualification_process_exit_precedes_reopen_and_runtime_cases(self):
        events = []
        request = NativeClient.request
        close = NativeClient.close
        def tracked_request(native, method, params=None):
            if method == "tools/call":
                events.append(params["name"])
            return request(native, method, params)
        def tracked_close(native):
            events.append("host_close")
            return close(native)
        class Watcher(ProcessWatcher):
            def wait_for_exit(self, timeout):
                events.append("process_exit")
                return super().wait_for_exit(timeout)
        def execute(*args, **kwargs):
            events.append("runtime_cases")
            return {"status": "passed", "runtimeQualified": True, "cases": []}
        with temporary_directory() as directory, mock.patch.object(NativeClient, "request", tracked_request), mock.patch.object(NativeClient, "close", tracked_close):
            report, _, _ = self.qualify(pathlib.Path(directory), runtime=True, execute=execute, watcher_factory=Watcher)
        self.assertEqual("passed", report["status"], report.get("failure"))
        starts = [index for index, name in enumerate(events) if name == "tia_start"]
        exits = [index for index, name in enumerate(events) if name == "process_exit"]
        self.assertLess(starts[0], exits[0])
        self.assertLess(exits[0], starts[1])
        self.assertLess(events.index("host_close"), exits[1])
        self.assertLess(exits[1], events.index("runtime_cases"))
        self.assertEqual(2, len(report["tiaProcessLifetimes"]))

    def test_unobserved_qualification_exit_prevents_reopen_and_runtime(self):
        class Watcher(ProcessWatcher):
            def wait_for_exit(self, timeout):
                return {"completed": False, "processExitVerified": False, "handleClosed": True}
        execute = mock.Mock()
        with temporary_directory() as directory:
            report, native, _ = self.qualify(pathlib.Path(directory), runtime=True, execute=execute, watcher_factory=Watcher)
        self.assertEqual("failed", report["status"])
        self.assertIn("processExitFailure", report)
        self.assertEqual(1, sum(tool == "tia_start" for tool, _ in native.calls))
        self.assertFalse(any(tool == "project_open" for tool, _ in native.calls))
        self.assertTrue(native.closed)
        execute.assert_not_called()

    def test_all_nine_families_import_compile_export_and_reopen_without_simulator(self):
        suites = runner.load_suites()
        self.assertEqual(9, len(suites))
        for suite in suites:
            with self.subTest(fixture=suite["fixtureId"]), temporary_directory() as directory:
                execute = mock.Mock(side_effect=AssertionError("Runtime executor must not run"))
                report, native, output = self.qualify(pathlib.Path(directory), suite, execute=execute)
                self.assertEqual("passed", report["status"], report.get("failure"))
                self.assertTrue(report["engineeringVerified"])
                self.assertFalse(report["runtimeVerified"])
                execute.assert_not_called()
                self.assertTrue(native.closed)
                self.assertTrue((output / "engineering-result.json").is_file())
                self.assertFalse((output / "live-result.json").exists())
                self.assertEqual(report, json.loads((output / "engineering-result.json").read_text(encoding="utf-8")))
                operations = [tool for tool, _ in native.calls]
                self.assertFalse(any(tool.startswith("sim_") or tool == "simulation_download" for tool in operations))
                self.assertEqual(2, operations.count("tia_start"))
                self.assertEqual(2, operations.count("tia_detach"))
                self.assertLess(operations.index("tia_detach"), operations.index("project_open"))
                self.assertEqual("tia_detach", operations[-1])
                reopened = operations.index("project_open")
                program = json.loads((output / "native-program.json").read_text(encoding="utf-8"))
                expected_blocks = set(suite["names"]) | set(program["expectedBlockNames"])
                for calls in (native.calls[:reopened], native.calls[reopened:]):
                    self.assertEqual(expected_blocks, {args["blockName"] for tool, args in calls if tool == "block_read"})
                    self.assertEqual(set(suite.get("typeNames", [])), {args["typeName"] for tool, args in calls if tool == "type_read"})
                inspected = {args["blockName"] for tool, args in native.calls if tool == "block_interface"}
                self.assertEqual(set(suite["names"]), inspected)
                changes = [args for tool, args in native.calls if tool == "change_apply"]
                self.assertTrue(all(args["compile"] and args["save"] and args["checkpointSave"] for args in changes))
                self.assertIn("binaries", report)
                self.assertIn("--worker", native.selections[0][2])
                self.assertNotIn("--sim-worker", native.selections[0][2])

    def test_save_compile_reopen_and_export_failures_never_verify_engineering(self):
        for fault in ("missing_save", "compile_error", "open_failure", "changed_block", "corrupt_artifact", "inconsistent_block"):
            with self.subTest(fault=fault), temporary_directory() as directory:
                report, native, output = self.qualify(pathlib.Path(directory), fault=fault)
                self.assertNotEqual("passed", report["status"], report)
                self.assertFalse(report["engineeringVerified"])
                self.assertFalse(report["runtimeVerified"])
                self.assertTrue(native.closed)
                self.assertEqual("tia_detach", native.calls[-1][0])
                self.assertTrue((output / "engineering-result.json").is_file())

    def test_reopened_type_mutation_is_detected(self):
        suite = runner.load_suites(["runtime-arrays-udts"])[0]
        with temporary_directory() as directory:
            report, _, _ = self.qualify(pathlib.Path(directory), suite, fault="changed_type")
            self.assertFalse(report["engineeringVerified"])
            self.assertNotEqual("passed", report["status"], report)

    def test_baseline_and_reopened_inconsistent_types_never_verify_engineering(self):
        suite = runner.load_suites(["runtime-arrays-udts"])[0]
        for fault in ("inconsistent_type", "reopened_inconsistent_type"):
            with self.subTest(fault=fault), temporary_directory() as directory:
                report, native, _ = self.qualify(pathlib.Path(directory), suite, fault=fault)
                self.assertEqual("blocked", report["status"], report)
                self.assertFalse(report["engineeringVerified"])
                self.assertFalse(report["runtimeVerified"])
                self.assertEqual(fault == "reopened_inconsistent_type", native.reopened)
                self.assertEqual("tia_detach", native.calls[-1][0])

    def test_boolean_error_counts_do_not_prove_successful_native_compilation(self):
        for fault in ("scratch_boolean_error_count", "apply_boolean_error_count"):
            with self.subTest(fault=fault), temporary_directory() as directory:
                report, native, _ = self.qualify(pathlib.Path(directory), fault=fault)
                self.assertEqual("blocked", report["status"], report)
                self.assertFalse(report["engineeringVerified"])
                self.assertFalse(report["nativeCompileVerified"])
                self.assertFalse(native.reopened)
                self.assertEqual("tia_detach", native.calls[-1][0])

    def test_owned_portal_disposal_is_required_before_reopen(self):
        with temporary_directory() as directory:
            report, native, _ = self.qualify(pathlib.Path(directory), fault="detach_not_disposed")
            self.assertFalse(report["engineeringVerified"])
            self.assertFalse(any(tool == "project_open" for tool, _ in native.calls))
            self.assertNotEqual("passed", report["status"])

    def test_nonzero_host_exit_revokes_engineering_verification(self):
        with temporary_directory() as directory:
            report, native, _ = self.qualify(pathlib.Path(directory), exit_code=7)
            self.assertEqual("failed", report["status"])
            self.assertEqual(7, report["hostExitCode"])
            self.assertFalse(report["engineeringVerified"])
            self.assertTrue(native.reopened)

    def test_binary_changes_during_run_revoke_engineering_verification(self):
        with temporary_directory() as directory:
            report, native, _ = self.qualify(pathlib.Path(directory), fault="binary_changed")
            self.assertTrue(native.reopened)
            self.assertEqual("failed", report["status"])
            self.assertFalse(report["engineeringVerified"])
            self.assertIn("binaryFailure", report)

    def test_nonzero_qualification_host_exit_prevents_runtime_cases(self):
        execute = mock.Mock(return_value={"status": "passed", "cases": []})
        with temporary_directory() as directory:
            report, _, _ = self.qualify(pathlib.Path(directory), runtime=True, execute=execute, exit_code=7)
            execute.assert_not_called()
            self.assertEqual("failed", report["status"])
            self.assertFalse(report["runtimeVerified"])
            self.assertFalse(report["engineeringVerified"])

    def test_live_stage_keeps_runtime_executor_and_worker_selections(self):
        suite = runner.load_suites(["runtime-fb-instances"])[0]
        execute = mock.Mock(return_value={"status": "passed", "runtimeQualified": True, "cases": [{"cleanup": {"completed": True}}]})
        with temporary_directory() as directory:
            report, native, output = self.qualify(pathlib.Path(directory), suite, runtime=True, execute=execute)
            self.assertEqual("passed", report["status"], report.get("failure"))
            self.assertTrue(report["runtimeVerified"])
            execute.assert_called_once()
            self.assertIn("First.CountValue", json.dumps(execute.call_args.args[1]["bindings"]))
            self.assertTrue(any(tool == "change_prepare" and args["kind"] == "project_simulation_enable" for tool, args in native.calls))
            selections = native.selections[0][2]
            self.assertEqual(str((pathlib.Path(directory) / "worker/worker.exe").resolve()), selections[selections.index("--worker") + 1])
            self.assertEqual(str((pathlib.Path(directory) / "sim-worker/sim-worker.exe").resolve()), selections[selections.index("--sim-worker") + 1])
            self.assertTrue((output / "live-result.json").is_file())

    def test_live_opt_in_reaches_backend_and_is_recorded(self):
        def execute(suite, program, backend_factory, revision, persist):
            backend = backend_factory(suite["cases"][0])
            self.assertTrue(backend.provision_runtime_credential)
            self.assertEqual("scratch-credentials/test.json", backend.credential_metadata)
            return {"status": "passed", "runtimeQualified": True, "cases": []}
        with temporary_directory() as directory:
            report, native, _ = self.qualify(pathlib.Path(directory), runtime=True, provision=True, execute=execute)
            self.assertEqual("passed", report["status"], report.get("failure"))
            self.assertTrue(report["provisionRuntimeCredential"])
            self.assertTrue(native.reopened)
            self.assertTrue(native.closed)
            self.assertEqual("fresh_host_and_owned_tia_per_case", report["caseSessionIsolation"])

    def test_tls_trust_option_reaches_backend_and_is_recorded(self):
        def execute(suite, program, backend_factory, revision, persist):
            backend = backend_factory(suite["cases"][0])
            self.assertTrue(backend.provision_runtime_credential)
            self.assertTrue(backend.trust_owned_empty_cpu_certificate)
            return {"status": "passed", "runtimeQualified": True, "cases": []}
        with temporary_directory() as directory:
            report, native, _ = self.qualify(pathlib.Path(directory), runtime=True, provision=True, trust=True, execute=execute)
            self.assertEqual("passed", report["status"], report.get("failure"))
            self.assertTrue(report["trustOwnedEmptyCpuCertificate"])
            self.assertTrue(native.reopened)

    def test_direct_tls_trust_without_provisioning_does_not_start_native(self):
        with temporary_directory() as directory:
            report, native, _ = self.qualify(pathlib.Path(directory), runtime=True, trust=True)
            self.assertEqual("blocked", report["status"])
            self.assertFalse(native.calls)

    def test_missing_opt_in_operation_blocks_before_native_start(self):
        execute = mock.Mock()
        with temporary_directory() as directory:
            report, native, _ = self.qualify(pathlib.Path(directory), runtime=True, provision=True,
                fault="missing_provision_operation", execute=execute)
            self.assertEqual("blocked", report["status"], report)
            self.assertIn("simulation_provision_scratch_credential", report["failure"]["message"])
            self.assertFalse(native.calls)
            self.assertTrue(native.closed)
            execute.assert_not_called()

    def test_direct_engineering_qualification_rejects_runtime_provisioning(self):
        with temporary_directory() as directory:
            report, native, _ = self.qualify(pathlib.Path(directory), provision=True)
            self.assertEqual("blocked", report["status"], report)
            self.assertFalse(native.selections)
            self.assertFalse(native.calls)


class StagedEvidenceTests(unittest.TestCase):
    def test_binary_manifest_hashes_executables_dependencies_and_configuration(self):
        with temporary_directory() as directory:
            host, worker, sim_worker = write_binaries(pathlib.Path(directory))
            manifest = runner.binary_manifest(host, worker, sim_worker)
            self.assertEqual(3, len(manifest))
            expected_paths = {str(path.resolve()) for binary in (host, worker, sim_worker)
                              for path in binary.parent.iterdir() if path.suffix != ".pdb"}
            records = [entry for role in manifest.values() for entry in role["files"]]
            self.assertEqual(expected_paths, {entry["path"] for entry in records})
            for entry in records:
                content = pathlib.Path(entry["path"]).read_bytes()
                self.assertEqual(len(content), entry["bytes"])
                self.assertEqual(hashlib.sha256(content).hexdigest(), entry["sha256"])
            worker.with_suffix(".dll").write_bytes(b"changed implementation")
            self.assertNotEqual(manifest, runner.binary_manifest(host, worker, sim_worker))

    def test_missing_binary_is_rejected_before_native_start(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            host, worker, _ = write_binaries(root)
            worker.unlink()
            client = mock.Mock()
            suite = runner.load_suites(["runtime-boolean"])[0]
            output = root / "report"
            runner.prepare_suite(suite, output)
            report = runner.qualify_suite(suite, output, host, 1, engineering_only=True,
                                          worker=worker, client_factory=client)
            self.assertFalse(report["engineeringVerified"])
            self.assertEqual("blocked", report["status"])
            client.assert_not_called()

    def test_preflight_mismatch_never_marks_binary_hashes_verified(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            host, worker, _ = write_binaries(root)
            expected = runner.binary_manifest(host, worker)
            worker.write_bytes(b"replaced after run preflight")
            client = mock.Mock()
            suite = runner.load_suites(["runtime-boolean"])[0]
            output = root / "report"
            runner.prepare_suite(suite, output)
            report = runner.qualify_suite(suite, output, host, 1, engineering_only=True,
                worker=worker, client_factory=client, expected_binaries=expected)
            self.assertEqual("blocked", report["status"], report)
            self.assertFalse(report["engineeringVerified"])
            self.assertFalse(report.get("binaryHashesVerified", False))
            client.assert_not_called()

    def test_engineering_audit_opens_only_the_exact_owned_scratch(self):
        suite = runner.load_suites(["runtime-boolean"])[0]
        with temporary_directory() as directory:
            native = NativeClient(suite, pathlib.Path(directory))
            audit = runner.EngineeringAudit(native, [], lambda: None)
            with self.assertRaises(ValueError):
                audit("project_open", {"path": str(native.project_path)})
            audit("tia_start")
            audit("tia_create_scratch")
            for arguments in ({"path": str(native.project_path.with_name("existing.ap21"))},
                              {"path": str(native.project_path), "projectName": "other"}):
                with self.subTest(arguments=arguments), self.assertRaises(ValueError):
                    audit("project_open", arguments)
            for tool in ("tia_attach", "download", "sim_select", "sim_create", "simulation_provision_scratch_credential"):
                with self.subTest(tool=tool), self.assertRaises(ValueError):
                    audit(tool)
            audit("tia_detach")
            audit("tia_start")
            audit("project_open", {"path": str(native.project_path)})
            self.assertTrue(native.reopened)

    def test_default_runtime_audit_still_forbids_project_open(self):
        client = mock.Mock()
        audit = NativeAudit(client, [], lambda: None)
        with self.assertRaises(ValueError):
            audit("project_open", {"path": "owned.ap21"})
        client.request.assert_not_called()


class StagedCliTests(unittest.TestCase):
    def test_uncertain_runtime_stops_later_suites_even_when_continue_requested(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            host, worker, simulator = write_binaries(root)
            output = root / "report"
            result = {"status": "failed", "engineeringVerified": True, "runtimeVerified": False,
                      "caseCleanupFailed": False, "runtimeStateUncertain": True,
                      "admission": "stopped_due_to_uncertain_runtime"}
            with mock.patch.object(runner, "qualify_suite", return_value=result) as qualify:
                self.assertEqual(1, runner.main(["--live", "--host", str(host), "--worker", str(worker),
                    "--sim-worker", str(simulator), "--fixture", "runtime-boolean", "--fixture", "runtime-arithmetic",
                    "--report-directory", str(output), "--continue-on-failure"]))
            qualify.assert_called_once()
            index = json.loads((output / "index.json").read_text())
            self.assertEqual("stopped_due_to_uncertain_runtime", index["admission"])
            self.assertEqual("stopped_due_to_uncertain_runtime", index["statusReason"])
            self.assertTrue(index["results"][0]["runtimeStateUncertain"])
            self.assertEqual("prepared", index["results"][1]["status"])
            self.assertFalse(index["runtimeVerified"])

    def test_tls_trust_requires_provisioning_before_artifact_or_native_work(self):
        with mock.patch.object(runner, "prepare_suite") as prepare, mock.patch.object(runner, "qualify_suite") as native:
            with self.assertRaises(SystemExit) as error:
                runner.main(["--live", "--trust-owned-empty-cpu-certificate"])
            self.assertEqual(2, error.exception.code)
            prepare.assert_not_called()
            native.assert_not_called()

    def test_provisioning_flag_requires_live_before_artifact_or_native_work(self):
        for mode in ([], ["--engineering-only"]):
            with self.subTest(mode=mode), mock.patch.object(runner, "prepare_suite") as prepare:
                with self.assertRaises(SystemExit) as error:
                    runner.main([*mode, "--provision-runtime-credential"])
                self.assertEqual(2, error.exception.code)
                prepare.assert_not_called()

    def test_live_provisioning_option_is_recorded_and_forwarded(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            host, worker, simulator = write_binaries(root)
            output = root / "report"
            result = {"status": "passed", "engineeringVerified": True, "runtimeVerified": True}
            with mock.patch.object(runner, "qualify_suite", return_value=result) as qualify:
                self.assertEqual(0, runner.main(["--live", "--host", str(host), "--worker", str(worker),
                    "--sim-worker", str(simulator), "--fixture", "runtime-boolean", "--report-directory", str(output),
                    "--provision-runtime-credential", "--trust-owned-empty-cpu-certificate", "--artifacts", str(root / "native")]))
            index = json.loads((output / "index.json").read_text())
            self.assertTrue(index["provisionRuntimeCredential"])
            self.assertTrue(index["runtimeVerified"])
            self.assertTrue(qualify.call_args.kwargs["provision_runtime_credential"])
            self.assertTrue(index["trustOwnedEmptyCpuCertificate"])
            self.assertTrue(qualify.call_args.kwargs["trust_owned_empty_cpu_certificate"])
            self.assertEqual(root / "native", qualify.call_args.kwargs["artifacts_directory"])

    def test_offline_has_no_binary_or_native_prerequisites(self):
        with temporary_directory() as directory, mock.patch.object(runner, "binary_manifest") as manifest, mock.patch.object(runner, "qualify_suite") as native:
            self.assertEqual(0, runner.main(["--host", "missing.exe", "--report-directory", directory]))
            index = json.loads((pathlib.Path(directory) / "index.json").read_text())
            self.assertEqual("offline", index["mode"])
            self.assertFalse(index["engineeringVerified"])
            self.assertFalse(index["runtimeVerified"])
            self.assertFalse(index["trustOwnedEmptyCpuCertificate"])
            self.assertEqual(9, len(index["results"]))
            native.assert_not_called()
            manifest.assert_not_called()

    def test_cli_requires_explicit_worker_for_either_native_stage(self):
        for mode in ("--engineering-only", "--live"):
            with self.subTest(mode=mode), mock.patch.object(runner, "qualify_suite") as native:
                with self.assertRaises(SystemExit) as error:
                    runner.main([mode])
                self.assertEqual(2, error.exception.code)
                native.assert_not_called()

    def test_live_requires_explicit_simulator_worker(self):
        with mock.patch.object(runner, "qualify_suite") as native:
            with self.assertRaises(SystemExit) as error:
                runner.main(["--live", "--worker", "worker.exe"])
            self.assertEqual(2, error.exception.code)
            native.assert_not_called()

    def test_native_modes_are_mutually_exclusive(self):
        with self.assertRaises(SystemExit) as error:
            runner.main(["--engineering-only", "--live"])
        self.assertEqual(2, error.exception.code)

    def test_engineering_cli_index_has_separate_verification_and_hashes(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            host, worker, _ = write_binaries(root)
            output = root / "report"
            result = {"status": "passed", "engineeringVerified": True, "runtimeVerified": False}
            with mock.patch.object(runner, "qualify_suite", return_value=result) as qualify:
                self.assertEqual(0, runner.main(["--engineering-only", "--host", str(host), "--worker", str(worker),
                    "--fixture", "runtime-boolean", "--report-directory", str(output)]))
            index = json.loads((output / "index.json").read_text())
            self.assertEqual("engineering", index["mode"])
            self.assertTrue(index["engineeringVerified"])
            self.assertFalse(index["runtimeVerified"])
            self.assertEqual("runtime-boolean/engineering-result.json", index["results"][0]["path"])
            self.assertEqual(runner.binary_manifest(host, worker), index["binaries"])
            self.assertTrue(qualify.call_args.kwargs["engineering_only"])
            self.assertEqual(worker.resolve(), qualify.call_args.kwargs["worker"])


if __name__ == "__main__":
    unittest.main()
