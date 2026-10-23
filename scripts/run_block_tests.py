"""Prepare block tests, qualify engineering with --engineering-only, or execute on owned PLCSIM with --live."""
import argparse
import copy
import datetime
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import time
import uuid

from block_test_runtime import McpRuntimeBackend, NativeAudit, NativeToolError, RuntimeCleanupUncertain, canonical, digest, redact, utc_now
from block_test_process import OwnedTiaProcessWatcher
from block_test_stop import StopRequest, remaining_selection
from smoke_mcp import Client, ROOT
from run_conformance import native_document, structural_xml


class EngineeringAudit(NativeAudit):
    """Engineering-only calls; reopening is bound to this run's created scratch."""
    ALLOWED_TOOLS = frozenset({"tia_start", "tia_create_scratch", "tia_detach", "change_prepare", "change_apply",
                              "change_status", "block_interface", "block_read", "type_read", "project_open"})
    OPTIONAL_TOOLS = frozenset()

    def __init__(self, client, records, persist):
        super().__init__(client, records, persist)
        self.owned_project_path = None

    def __call__(self, name, arguments=None):
        if name == "project_open" and (not self.owned_project_path or
                arguments != {"path": self.owned_project_path}):
            raise ValueError("Only the exact scratch project created by this run may be reopened.")
        result = super().__call__(name, arguments)
        if name == "tia_create_scratch" and result.get("completed") is True:
            path = result.get("project", {}).get("path")
            if not isinstance(path, str) or not pathlib.Path(path).is_absolute() or pathlib.Path(path).suffix.lower() != ".ap21":
                raise ValueError("Scratch creation did not identify an absolute .ap21 project path.")
            self.owned_project_path = path
        return result


def file_record(path):
    with path.open("rb") as stream:
        return {"path": str(path), "bytes": path.stat().st_size,
                "sha256": hashlib.file_digest(stream, "sha256").hexdigest()}


def binary_manifest(host, worker, sim_worker=None):
    """Hash selected local binaries and adjacent runtime dependencies, before startup."""
    if worker is None:
        raise ValueError("An explicit --worker is required for native qualification.")
    result = {}
    for role, selected in (("host", host), ("worker", worker), ("simWorker", sim_worker)):
        if selected is None:
            continue
        executable = pathlib.Path(selected).resolve(strict=True)
        if not executable.is_file():
            raise ValueError("Selected binary is not a file: " + str(executable))
        files = {executable, *(path.resolve() for path in executable.parent.iterdir()
                  if path.is_file() and path.suffix.lower() in {".exe", ".dll", ".config", ".json"})}
        result[role] = {"path": str(executable), "files": [file_record(path) for path in sorted(files)]}
    return result


def export_snapshot(call, suite, program, output, phase):
    """Archive verified raw exports and compare payloads, excluding export provenance."""
    destination = output / phase
    destination.mkdir()
    results, manifest = {}, {}
    selectors = [("block", name) for name in dict.fromkeys([*suite["names"], *program["expectedBlockNames"]])]
    selectors += [("type", name) for name in suite.get("typeNames", [])]
    for kind, name in selectors:
        result = call("block_read" if kind == "block" else "type_read", {kind + "Name": name})
        key = kind + ":" + name
        results[key] = result
        save_json(output / (phase + ".json"), results)
        if result.get(kind, {}).get("name") != name:
            raise RuntimeError("Native export returned a different object: " + key)
        if result.get(kind, {}).get("isConsistent") is not True:
            raise RuntimeError("Native " + kind + " is not compile-consistent: " + name)
        artifact = result.get("artifact", {})
        path = pathlib.Path(artifact["path"])
        data = path.read_bytes()
        signature = hashlib.sha256(data).hexdigest()
        if signature != artifact.get("sha256") or len(data) != artifact.get("bytes"):
            raise RuntimeError("Native export differs from its file manifest: " + key)
        # Archival filenames use an ordinal rather than custom suite object names.
        target = destination / (str(len(manifest) + 1).zfill(3) + ".xml")
        target.write_bytes(data)
        payload_hash = digest(structural_xml(native_document(data.decode("utf-8-sig"))))
        manifest[key] = {"path": str(target.relative_to(output)), "sha256": signature,
                         "bytes": len(data), "payloadSha256": payload_hash}
        save_json(output / (phase + "-manifest.json"), manifest)
    return results, manifest


def detach_owned(call):
    result = call("tia_detach")
    if result.get("disposedOwnedPortalConnection") is not True or result.get("attached") is not False:
        raise RuntimeError("Detach did not confirm disposal of this run's owned TIA connection.")
    return result


def same_user_project_lock(error, project_path, *, user=None, computer=None):
    """Recognize only the observed explicit Siemens rejection for this project."""
    if not isinstance(error, NativeToolError) or error.tool != "project_open":
        return False
    detail = error.result.get("error", {})
    if (detail.get("code") != "EngineeringError"
            or detail.get("details", {}).get("exceptionType") != "Siemens.Engineering.EngineeringTargetInvocationException"):
        return False
    user = user if user is not None else os.environ.get("USERNAME")
    computer = computer if computer is not None else os.environ.get("COMPUTERNAME")
    if not user or not computer:
        return False
    expected = ("Error when calling method 'Open' of type 'Siemens.Engineering.ProjectComposition'.\n\n"
        + "Unable to open the project under path '" + project_path + "'.\n\n"
        + "An error occurred while opening the project\n"
        + "Auf das Projekt/die Bibliothek " + project_path + " kann nicht zugegriffen werden. "
        + "Es/sie wurde bereits vom Benutzer " + user + " auf dem Computer " + computer + " geöffnet. "
        + "Hinweis: Wenn die Anwendung nicht korrekt geschlossen wurde, können die geöffneten Projekte und Bibliotheken "
        + "erst nach einer Verzögerung von 2 Minuten wieder geöffnet werden.")
    message = detail.get("message")
    return (isinstance(message, str) and message.replace("\r\n", "\n").replace("\u00c3\u00b6", "ö").strip().casefold() == expected.casefold())


def open_qualified_project(call, project_path, evidence, persist, *, budget_ms=150000, interval_ms=5000,
                           max_attempts=31, clock=time.monotonic, sleep=time.sleep, user=None, computer=None):
    """Bounded read-only retries only after an exact known project-lock rejection.

    The budget bounds admission of retries; each native call retains the MCP
    timeout. NativeAudit persists every rejected call without replacing it.
    """
    if (type(budget_ms) is not int or not 1 <= budget_ms <= 300000 or type(interval_ms) is not int
            or not 1 <= interval_ms <= 10000 or type(max_attempts) is not int or not 1 <= max_attempts <= 64):
        raise ValueError("Invalid bounded project-open retry policy.")
    policy = {"projectPath": project_path, "budgetMs": budget_ms, "intervalMs": interval_ms,
              "maxAttempts": max_attempts, "attempts": [], "scope": "Exact explicit same-user/same-host Siemens project-lock rejection only.",
              "budgetBasis": "Monotonic retry admission window; native call timeout applies separately."}
    evidence["projectOpenRetry"] = policy
    deadline, last_error = clock() + budget_ms / 1000, None
    for ordinal in range(1, max_attempts + 1):
        if last_error is not None and clock() >= deadline:
            policy["exhausted"] = True
            persist()
            raise last_error
        attempt = {"ordinal": ordinal, "startedAtUtc": utc_now(), "status": "started"}
        policy["attempts"].append(attempt)
        persist()
        try:
            opened = call("project_open", {"path": project_path})
            if (opened.get("opened") is not True or opened.get("saved") is not False
                    or opened.get("project", {}).get("path") != project_path):
                raise RuntimeError("Reopen did not confirm the exact saved scratch project without saving changes.")
            attempt["status"] = "passed"
            persist()
            return opened
        except Exception as error:
            attempt["status"] = "rejected"
            attempt["knownSameUserProjectLock"] = same_user_project_lock(error, project_path, user=user, computer=computer)
            persist()
            if not attempt["knownSameUserProjectLock"]:
                raise
            last_error = error
            remaining = deadline - clock()
            if ordinal == max_attempts or remaining <= 0:
                policy["exhausted"] = True
                persist()
                raise
            attempt["retryDelayMs"] = min(interval_ms, max(0, int(remaining * 1000)))
            persist()
            sleep(attempt["retryDelayMs"] / 1000)
    raise last_error


def wait_owned_tia_exit(watcher, attempted, record, timeout_ms=120000):
    if not attempted:
        return {"required": False, "completed": True}
    if watcher is None:
        record["tiaProcessExit"] = {"completed": False, "processExitVerified": False, "failure": "Owned TIA start had no captured process handle."}
        raise RuntimeError("Owned TIA process exit cannot be verified without its captured handle.")
    try:
        record["tiaProcessExit"] = watcher.wait_for_exit(timeout_ms)
        if (record["tiaProcessExit"].get("completed") is not True
                or record["tiaProcessExit"].get("processExitVerified") is not True
                or record["tiaProcessExit"].get("handleClosed") is not True):
            raise RuntimeError("Owned TIA process wait did not prove exit and handle cleanup.")
        return record["tiaProcessExit"]
    except Exception as error:
        if hasattr(error, "evidence"):
            record["tiaProcessExit"] = copy.deepcopy(error.evidence)
        else:
            record.setdefault("tiaProcessExit", {"completed": False, "processExitVerified": False, "exceptionType": type(error).__name__})
        raise
    finally:
        # The production watcher closes in its wait's finally; this idempotent
        # call also covers an injected wait that raises before disposing.
        if watcher.close() is not True:
            raise RuntimeError("The owned TIA process handle could not be closed.")


class CaseSessionAudit(NativeAudit):
    """Read the exact qualified scratch project in a new owned session."""
    ALLOWED_TOOLS = (NativeAudit.ALLOWED_TOOLS - {"tia_create_scratch", "change_prepare", "change_apply", "change_status"}) | {"project_open"}

    def __init__(self, client, records, persist, project_path):
        super().__init__(client, records, persist)
        self.project_path = project_path

    def __call__(self, name, arguments=None):
        if name == "project_open" and arguments != {"path": self.project_path}:
            raise ValueError("A case may reopen only its exact already qualified scratch project.")
        return super().__call__(name, arguments)


class CaseRuntimeBackend(McpRuntimeBackend):
    """Lazy fresh host/TIA lifetime per case, including all partial-start cleanup."""
    def __init__(self, program, revision, credential_metadata, *, suite, output, audit, persist,
                 binaries, timeout, client_factory=Client, process_watcher_factory=OwnedTiaProcessWatcher,
                 process_exit_timeout_ms=120000, project_open_options=None, artifacts_directory=None, **runtime_options):
        super().__init__(self._call, program, revision, credential_metadata, **runtime_options)
        self.suite, self.output, self.audit, self.persist = suite, output, audit, persist
        self.binaries, self.timeout, self.client_factory = binaries, timeout, client_factory
        self.client, self.session_call, self.attached = None, None, False
        self.process_watcher_factory, self.process_exit_timeout_ms = process_watcher_factory, process_exit_timeout_ms
        self.process_watcher, self.tia_start_attempted = None, False
        self.project_open_options = project_open_options or {}
        self.artifacts_directory = artifacts_directory
        self.project_path = revision["project"]["path"]
        if not pathlib.Path(self.project_path).is_absolute() or pathlib.Path(self.project_path).suffix.lower() != ".ap21":
            raise ValueError("A case requires its qualified absolute scratch project path.")
        self.session = {"isolation": "fresh_host_and_owned_tia_per_case", "projectPath": self.project_path,
                        "binariesSha256": digest(binaries), "started": False, "exportsVerified": False,
                        "evidenceVersion": 2, "processExitVerificationRequired": True}
        audit["session"] = self.session

    def _call(self, name, arguments=None):
        if self.session_call is None:
            raise RuntimeError("The case's owned engineering session has not started.")
        return self.session_call(name, arguments)

    def _verify_binaries(self):
        current = binary_manifest(pathlib.Path(self.binaries["host"]["path"]),
            pathlib.Path(self.binaries["worker"]["path"]), pathlib.Path(self.binaries["simWorker"]["path"]))
        if current != self.binaries or digest(current) != self.revision["binariesSha256"]:
            raise RuntimeError("Case binaries differ from the qualified program revision.")

    def start(self):
        if self.session["started"]:
            raise RuntimeError("A case session cannot be retried or reused.")
        self.session["started"] = True
        self.persist()
        self._verify_binaries()
        self.session["binaryPreflightVerified"] = True
        args = ["--worker", self.binaries["worker"]["path"], "--sim-worker", self.binaries["simWorker"]["path"]]
        if self.artifacts_directory is not None:
            args += ["--artifacts", str(pathlib.Path(self.artifacts_directory).resolve())]
        self.client = self.client_factory(pathlib.Path(self.binaries["host"]["path"]), self.timeout, args)
        self.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
            "clientInfo": {"name": "tia-block-test-case", "version": "1.0.0"}})
        self.client.notify("notifications/initialized")
        available = {item["name"] for item in self.client.request("tools/list")["tools"]}
        required = CaseSessionAudit.ALLOWED_TOOLS | ({"simulation_provision_scratch_credential"} if self.provision_runtime_credential else set())
        if required - available:
            raise RuntimeError("Required case MCP tools unavailable: " + ", ".join(sorted(required - available)))
        self.session_call = CaseSessionAudit(self.client, self.audit["calls"], self.persist, self.project_path)
        self.attached = True  # A failed start can still leave an owned partial attachment.
        self.tia_start_attempted = True
        started = self._call("tia_start")
        if started.get("attached") is not True or started.get("ownsPortalProcess") is not True or type(started.get("processId")) is not int:
            raise RuntimeError("Case startup did not identify a fresh owned TIA process.")
        self.session["tiaProcessId"] = started["processId"]
        self.process_watcher = self.process_watcher_factory(started["processId"])
        self.session["tiaProcessCapture"] = copy.deepcopy(self.process_watcher.evidence)
        open_qualified_project(self.session_call, self.project_path, self.session, self.persist, **self.project_open_options)
        self.output.mkdir()
        _, manifest = export_snapshot(self.session_call, self.suite, self.program, self.output, "exports")
        expected = self.revision["reopenedExports"]
        self.session["exports"] = manifest
        self.session["payloadComparison"] = {key: key in manifest and expected[key]["payloadSha256"] == manifest[key]["payloadSha256"] for key in expected}
        self.session["exportsVerified"] = set(manifest) == set(expected) and all(self.session["payloadComparison"].values())
        self.persist()
        if not self.session["exportsVerified"]:
            raise RuntimeError("Case native exports differ from the qualified saved program revision.")
        evidence = super().start()
        evidence["engineeringSession"] = copy.deepcopy(self.session)
        return evidence

    def close(self):
        cleanup, errors = {"completed": False}, []
        try:
            cleanup.update(super().close())
        except Exception as error:
            if isinstance(getattr(error, "cleanup", None), dict):
                cleanup.update(copy.deepcopy(error.cleanup))
            action = "runtime_admission" if cleanup.get("runtimeStateUncertain") is True and not cleanup.get("releaseFailure") else "sim_release"
            errors.append({"action": action, "type": type(error).__name__, "message": str(error)})
        if self.attached:
            try:
                cleanup["tiaDetach"] = detach_owned(self.session_call)
                self.attached = False
            except Exception as error:
                errors.append({"action": "tia_detach", "type": type(error).__name__, "message": str(error)})
        if self.client is not None:
            try:
                cleanup["launcherShutdown"] = self.client.close()
                cleanup["hostExitCode"] = self.client.process.returncode
                if type(cleanup["hostExitCode"]) is not int or cleanup["hostExitCode"] != 0:
                    raise RuntimeError("Case host exited unsuccessfully.")
                shutdown = cleanup["launcherShutdown"]
                if isinstance(shutdown, dict) and (shutdown.get("forcedHostStop") or any(
                        item.get("processExitVerified") is not True or item.get("exitCode") != 0
                        for item in shutdown.get("workerExits", []))):
                    raise RuntimeError("Case native worker shutdown was not verified cleanly.")
            except Exception as error:
                errors.append({"action": "host_close", "type": type(error).__name__, "message": str(error)})
        try:
            wait_owned_tia_exit(self.process_watcher, self.tia_start_attempted, cleanup, self.process_exit_timeout_ms)
        except Exception as error:
            errors.append({"action": "tia_process_exit", "type": type(error).__name__, "message": str(error)})
        try:
            self._verify_binaries()
            cleanup["binaryHashesVerified"] = True
        except Exception as error:
            errors.append({"action": "binary_verification", "type": type(error).__name__, "message": str(error)})
        cleanup["completed"] = not errors
        cleanup["errors"] = errors
        self.session["cleanup"] = copy.deepcopy(cleanup)
        self.persist()
        if errors:
            if cleanup.get("runtimeStateUncertain") is True:
                raise RuntimeCleanupUncertain("Case session stopped_due_to_uncertain_runtime after all independent cleanup attempts.", cleanup)
            raise RuntimeError("Case session cleanup incomplete: " + ", ".join(row["action"] for row in errors))
        return cleanup


def load_suites(selected=None, suite_paths=None):
    spec = importlib.util.spec_from_file_location("tia_runtime_catalog", ROOT / "fixtures/runtime_catalog.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    catalog = module.specifications()
    known = {suite["fixtureId"] for suite in catalog}
    if selected and set(selected) - known:
        raise ValueError("Unknown runtime fixtures: " + ", ".join(sorted(set(selected) - known)))
    suites = [suite for suite in catalog if suite["fixtureId"] in selected] if selected else ([] if suite_paths else catalog)
    for path in suite_paths or []:
        data = json.loads(pathlib.Path(path).read_text(encoding="utf-8-sig"))
        suites.extend(data if isinstance(data, list) else [data])
    identifiers = [suite["fixtureId"] for suite in suites]
    if len(identifiers) != len(set(identifiers)) or any(not re.fullmatch(r"[a-z0-9][a-z0-9_-]{0,79}", name) for name in identifiers):
        raise ValueError("Runtime fixture IDs must be unique safe artifact names.")
    if not suites:
        raise ValueError("At least one runtime suite is required.")
    return suites


def source_record(name, source):
    return {"path": name, "sha256": hashlib.sha256(source.encode("utf-8")).hexdigest(), "bytes": len(source.encode("utf-8"))}


def replace_owned_pending(pending, target, *, budget_ms=2000, interval_ms=25, clock=time.monotonic, sleep=time.sleep):
    if type(budget_ms) is not int or not 0 <= budget_ms <= 10000 or type(interval_ms) is not int or not 1 <= interval_ms <= 1000:
        raise ValueError("Report replace retries require a bounded budget and positive interval.")
    if pending.name != target.name + ".pending" or pending.resolve().parent != target.resolve().parent:
        raise ValueError("Only this report's same-directory pending file can be replaced.")
    deadline = clock() + budget_ms / 1000
    attempts = 0
    while True:
        attempts += 1
        try:
            pending.replace(target)
            return {"attempts": attempts}
        except OSError as error:
            remaining = deadline - clock()
            if getattr(error, "winerror", None) not in (5, 32, 33) or remaining <= 0:
                raise
            sleep(min(interval_ms / 1000, remaining))
            if clock() >= deadline:
                raise


def save_json(path, value):
    pending = path.with_name(path.name + ".pending")
    # NativeAudit redacts credentials at the trust boundary. Rewriting arbitrary
    # authored parameter names here (e.g. dut.Token) would change the archived
    # specification and break its exact revision hash.
    pending.write_text(canonical(value), encoding="utf-8", newline="")
    replace_owned_pending(pending, path)


def validated_case_pairs(suite, selection):
    expected = [(case["id"], repetition) for case in suite["cases"] for repetition in range(1, case.get("repeat", 1) + 1)]
    if type(selection) not in (tuple, list) or not selection:
        raise ValueError("Case selection must be a nonempty sequence of exact case/repetition pairs.")
    selected = set()
    for pair in selection:
        if type(pair) is not tuple or len(pair) != 2 or type(pair[0]) is not str or type(pair[1]) is not int:
            raise ValueError("Case selection requires a string caseId and integer repetition.")
        if pair not in expected or pair in selected:
            raise ValueError("Case selection has a duplicate or unknown case/repetition.")
        selected.add(pair)
    return [pair for pair in expected if pair in selected]


def load_case_selection(path, suites):
    raw = path.read_bytes()
    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("Case selection JSON has duplicate object keys.")
            result[key] = value
        return result
    manifest = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique_object)
    if (not isinstance(manifest, dict) or set(manifest) != {"schemaVersion", "suites"}
            or type(manifest["schemaVersion"]) is not int or manifest["schemaVersion"] != 1
            or not isinstance(manifest["suites"], dict) or not manifest["suites"]):
        raise ValueError("Case selection requires schemaVersion 1 and a nonempty suites object.")
    known = {suite["fixtureId"]: suite for suite in suites}
    result = {}
    for fixture, request in manifest["suites"].items():
        if fixture not in known or not isinstance(request, dict) or set(request) != {"suiteSha256", "cases"}:
            raise ValueError("Case selection identifies an unknown suite or unsupported fields.")
        if request["suiteSha256"] != digest(known[fixture]) or not isinstance(request["cases"], list):
            raise ValueError("Case selection does not match the exact authored suite hash.")
        pairs = []
        for row in request["cases"]:
            if not isinstance(row, dict) or set(row) != {"caseId", "repetition"}:
                raise ValueError("Case selection requires exact caseId/repetition objects.")
            pairs.append((row["caseId"], row["repetition"]))
        result[fixture] = validated_case_pairs(known[fixture], pairs)
    return result, raw


def prepare_suite(suite, output):
    from block_test_program import inspect_interface, generate_program
    from block_test_executor import validate_suite

    interface = inspect_interface(suite["interface"], suite.get("typeDefinitions"))
    program = generate_program(interface, instances=tuple(suite["instances"]), prefix="BlockTest")
    validate_suite(suite, program)
    output.mkdir(parents=True, exist_ok=False)
    save_json(output / "suite.json", suite)
    save_json(output / "declared-interface.json", suite["interface"])
    save_json(output / "program.json", program)
    sources = [("dut.scl", suite["source"]), ("test-program.scl", program["source"])]
    if suite.get("typeSource"):
        sources.insert(0, ("types.scl", suite["typeSource"]))
    for name, source in sources:
        (output / name).write_text(source, encoding="utf-8", newline="")
    revision = {"suiteSha256": digest(suite), "programRevision": program["revision"],
                "sources": [source_record(name, source) for name, source in sources],
                "declaredInterfaceSha256": digest(suite["interface"]), "nativeInterfaceVerified": False}
    revision["revision"] = digest(revision)
    save_json(output / "revision.json", revision)
    plan = {"schemaVersion": 1, "fixtureId": suite["fixtureId"], "status": "prepared",
            "runtimeVerified": False, "nativeCompileVerified": False, "engineeringVerified": False, "revision": revision,
            "cases": [{"id": case["id"], "status": "not_run"} for case in suite["cases"]],
            "timing": program["timing"], "stateCaptureLimitations": program.get("stateCaptureLimitations", []),
            "isolation": "Live execution creates a new owned scratch project per suite and a new owned simulator per case.",
            "limitations": ["Offline preparation only. Sources and declarations have not been checked by native TIA compilation.",
                            "Runtime qualification remains pending; preparing a program does not prove its PLC behavior."],
            "engineeringPlan": ["tia_start", "tia_create_scratch", "import declared types and DUT source via change_prepare/change_apply",
                                "block_interface and type_read native inspection", "generate/import test program from native interface",
                                "compile/save via change_apply", "export all blocks and types",
                                "detach/start/reopen exact owned scratch and compare native exports"],
            "casePlan": ["sim_create owned OFF instance", "simulation_download and verify generated card hashes", "powerOn and run",
                         "verify fresh counters", "apply stimuli, acknowledge calls and evaluate assertions", "sim_release deleteOwned"],
            "physicalPlcAccess": False}
    save_json(output / "result.json", plan)
    return program, revision, plan


def apply_change(call, kind, payload):
    prepared = call("change_prepare", {"kind": kind, "payload": payload})
    try:
        result = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                      "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
    except Exception:
        # Preserve the isolated failed project and partial journal; never retry a mutation.
        try:
            call("change_status", {"changeId": prepared["changeId"]})
        except Exception:
            pass
        raise
    errors = result.get("compile", {}).get("errorCount")
    if (result.get("state") != "applied" or type(errors) is not int or errors != 0 or
            "changed_project_saved" not in result.get("stages", [])):
        raise RuntimeError("Engineering change did not apply, compile and save successfully: " + kind)
    return result


def qualify_suite(suite, output, host, timeout, *, engineering_only=False, worker=None, sim_worker=None,
                  client_factory=Client, execute=None, expected_binaries=None, provision_runtime_credential=False,
                  trust_owned_empty_cpu_certificate=False, process_watcher_factory=OwnedTiaProcessWatcher,
                  process_exit_timeout_ms=120000, project_open_options=None, case_selection=None, artifacts_directory=None,
                  stop_requested=None):
    from block_test_program import inspect_interface, generate_program
    from block_test_executor import execute_suite, validate_suite

    report = {"schemaVersion": 1, "fixtureId": suite["fixtureId"], "status": "blocked", "runtimeVerified": False,
              "mode": "engineering" if engineering_only else "runtime", "engineeringVerified": False,
              "provisionRuntimeCredential": provision_runtime_credential,
              "trustOwnedEmptyCpuCertificate": trust_owned_empty_cpu_certificate,
              "processExitVerificationRequired": True, "tiaProcessLifetimes": [],
              "qualificationScope": "selected_cases" if case_selection is not None else "full_suite",
              "selectedCasesPassed": False,
              "nativeCompileVerified": False, "saveReopenVerified": False,
              "startedAtUtc": utc_now(), "engineering": [], "runtime": [], "cleanup": [], "physicalPlcAccess": False}
    client, attached = None, False
    process_watcher, process_exit_pending, process_record = None, False, None
    result_name = "engineering-result.json" if engineering_only else "live-result.json"

    def persist():
        save_json(output / result_name, report)

    def persist_execution(execution):
        report["execution"] = copy.deepcopy(execution)
        persist()

    def start_owned_tia(call, phase):
        nonlocal process_watcher, process_exit_pending, process_record, attached
        process_watcher, process_exit_pending, attached = None, True, True
        process_record = {"phase": phase, "startAttempted": True}
        report["tiaProcessLifetimes"].append(process_record)
        persist()
        result = call("tia_start")
        if result.get("attached") is not True or result.get("ownsPortalProcess") is not True or type(result.get("processId")) is not int:
            raise RuntimeError("Qualification startup did not identify an exact owned TIA process.")
        process_record["processId"] = result["processId"]
        process_watcher = process_watcher_factory(result["processId"])
        process_record["capture"] = copy.deepcopy(process_watcher.evidence)
        persist()

    def wait_qualification_exit():
        nonlocal process_exit_pending, process_watcher
        if not process_exit_pending:
            return
        try:
            wait_owned_tia_exit(process_watcher, True, process_record, process_exit_timeout_ms)
        except Exception as error:
            report["processExitFailure"] = {"type": type(error).__name__, "message": str(error)}
            report["engineeringVerified"] = False
            raise
        finally:
            process_exit_pending, process_watcher = False, None
            persist()

    persist()
    try:
        if case_selection is not None:
            if engineering_only:
                raise ValueError("Explicit case selection requires runtime qualification.")
            case_selection = validated_case_pairs(suite, case_selection)
            report["caseSelection"] = [{"caseId": case_id, "repetition": repetition} for case_id, repetition in case_selection]
        if type(trust_owned_empty_cpu_certificate) is not bool:
            raise ValueError("Owned empty-CPU TLS trust authorization must be a boolean.")
        if trust_owned_empty_cpu_certificate and not provision_runtime_credential:
            raise ValueError("Owned empty-CPU TLS trust requires explicit runtime credential provisioning.")
        if provision_runtime_credential and engineering_only:
            raise ValueError("Runtime credential provisioning requires runtime qualification.")
        if not engineering_only and sim_worker is None:
            raise ValueError("An explicit --sim-worker is required for runtime qualification.")
        report["binaries"] = binary_manifest(host, worker, None if engineering_only else sim_worker)
        if expected_binaries is not None and report["binaries"] != expected_binaries:
            raise RuntimeError("Selected binaries changed since run preflight.")
        report["binaryPreflightVerified"] = True
        extra_arguments = ["--worker", report["binaries"]["worker"]["path"]]
        if artifacts_directory is not None:
            extra_arguments += ["--artifacts", str(pathlib.Path(artifacts_directory).resolve())]
        if not engineering_only:
            extra_arguments += ["--sim-worker", report["binaries"]["simWorker"]["path"]]
        report["hostArguments"] = extra_arguments
        persist()
        client = client_factory(pathlib.Path(report["binaries"]["host"]["path"]), timeout, extra_arguments)
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                       "clientInfo": {"name": "tia-block-tests", "version": "1.0.0"}})
        client.notify("notifications/initialized")
        available = {item["name"] for item in client.request("tools/list")["tools"]}
        required = EngineeringAudit.ALLOWED_TOOLS | (frozenset() if engineering_only else NativeAudit.ALLOWED_TOOLS)
        if provision_runtime_credential:
            required |= {"simulation_provision_scratch_credential"}
        missing = required - available
        if missing:
            raise RuntimeError("Required MCP tools unavailable: " + ", ".join(sorted(missing)))
        call = EngineeringAudit(client, report["engineering"], persist)
        start_owned_tia(call, "initial_qualification")
        scratch = call("tia_create_scratch", {"cpuTypeIdentifier": "OrderNumber:6ES7 511-1AL03-0AB0/V4.0"})
        errors = scratch.get("compile", {}).get("errorCount")
        if scratch.get("completed") is not True or type(errors) is not int or errors != 0:
            raise RuntimeError("Isolated scratch creation and compile must succeed.")
        report["scratch"] = redact(scratch)
        credentials = scratch.get("scratchSecurity", {}).get("cpus", [])
        if not engineering_only and (len(credentials) != 1 or not credentials[0].get("credentialMetadataPath")):
            raise RuntimeError("Fresh scratch CPU did not return its exact generated credential metadata path.")
        if suite.get("typeSource"):
            apply_change(call, "type_source_upsert", {"source": suite["typeSource"], "expectedTypeNames": suite["typeNames"]})
        apply_change(call, "source_upsert", {"language": suite["language"], "source": suite["source"], "expectedBlockNames": suite["names"]})
        interfaces = {name: call("block_interface", {"blockName": name, "maxItems": 2000}) for name in suite["names"]}
        native = interfaces[suite["blockName"]]
        types = {name: call("type_read", {"typeName": name, "maxItems": 2000}) for name in suite.get("typeNames", [])}
        # Nested/multi-instance FB declarations come from native interfaces too.
        definitions = {**types, **{name: result for name, result in interfaces.items()
                                   if name != suite["blockName"] and "SW.Blocks.FB" in result.get("objectKinds", [])}}
        save_json(output / "native-interface.json", native)
        save_json(output / "native-block-interfaces.json", interfaces)
        save_json(output / "native-types.json", types)
        interface = inspect_interface(native, definitions)
        program = generate_program(interface, instances=tuple(suite["instances"]), prefix="BlockTest")
        validate_suite(suite, program)
        (output / "native-test-program.scl").write_text(program["source"], encoding="utf-8", newline="")
        save_json(output / "native-program.json", program)
        apply_change(call, "source_upsert", {"language": "SCL", "source": program["source"], "expectedBlockNames": program["expectedBlockNames"]})
        if not engineering_only:
            apply_change(call, "project_simulation_enable", {"enabled": True})
        report["nativeCompileVerified"] = True
        exports, before = export_snapshot(call, suite, program, output, "native-exports")
        report["exports"] = before
        persist()
        try:
            detach_owned(call)
        except Exception as error:
            report["detachFailure"] = {"type": type(error).__name__, "message": str(error)}
            raise
        finally:
            attached = False
        wait_qualification_exit()
        start_owned_tia(call, "reopened_qualification")
        open_qualified_project(call, call.owned_project_path, process_record, persist, **(project_open_options or {}))
        _, after = export_snapshot(call, suite, program, output, "reopened-exports")
        report["reopenedExports"] = after
        report["persistenceComparison"] = {key: before[key]["payloadSha256"] == after[key]["payloadSha256"] for key in before}
        persist()
        if not all(report["persistenceComparison"].values()):
            raise RuntimeError("Native exported payload changed after save/reopen.")
        report["saveReopenVerified"] = True
        report["engineeringVerified"] = True
        revision = {"suiteSha256": digest(suite), "programRevision": program["revision"], "nativeInterfaceVerified": True,
                    "nativeInterfaceSha256": digest(native), "nativeBlockInterfacesSha256": digest(interfaces),
                    "nativeTypesSha256": digest(types), "nativeBlocksSha256": digest(exports),
                    "sources": [source_record("dut.scl", suite["source"]), source_record("native-test-program.scl", program["source"])],
                    "project": scratch["project"], "exports": before, "reopenedExports": after,
                    "binariesSha256": digest(report["binaries"]), "saveReopenVerified": True}
        if suite.get("typeSource"):
            revision["sources"].insert(0, source_record("types.scl", suite["typeSource"]))
        revision["revision"] = digest(revision)
        report["revision"] = revision
        save_json(output / "native-revision.json", revision)

        def backend_factory(case):
            audit = {"caseId": case["id"], "calls": []}
            report["runtime"].append(audit)
            persist()
            return CaseRuntimeBackend(program, revision, credentials[0]["credentialMetadataPath"],
                                     suite=suite, output=output / ("case-" + str(len(report["runtime"])).zfill(3)),
                                     audit=audit, persist=persist, binaries=report["binaries"], timeout=timeout,
                                     client_factory=client_factory,
                                     process_watcher_factory=process_watcher_factory, process_exit_timeout_ms=process_exit_timeout_ms,
                                     project_open_options=project_open_options, artifacts_directory=artifacts_directory,
                                     provision_runtime_credential=provision_runtime_credential,
                                     trust_owned_empty_cpu_certificate=trust_owned_empty_cpu_certificate)

        if engineering_only:
            report["status"] = "passed"
            report["cases"] = [{"id": case["id"], "status": "not_run"} for case in suite["cases"]]
            report["limitations"] = ["Engineering qualification only; PLC runtime behavior and timing remain unverified.",
                                     "Export comparison excludes DocumentInfo provenance and serializer indentation."]
        else:
            # The qualification session must be closed before the first case owns
            # another TIA session. Each case re-verifies exports after reopening.
            try:
                detach_owned(NativeAudit(client, report["cleanup"], persist))
            except Exception as error:
                report["detachFailure"] = {"type": type(error).__name__, "message": str(error)}
                report["engineeringVerified"] = False
                raise
            finally:
                attached = False
            qualified_client, client = client, None
            try:
                report["launcherShutdown"] = qualified_client.close()
                report["hostExitCode"] = qualified_client.process.returncode
                if type(report["hostExitCode"]) is not int or report["hostExitCode"] != 0:
                    raise RuntimeError("Qualification host did not exit successfully before runtime cases.")
            except Exception as error:
                report["hostCloseFailure"] = {"type": type(error).__name__, "message": str(error)}
                report["engineeringVerified"] = False
                raise
            wait_qualification_exit()
            report["caseSessionIsolation"] = "fresh_host_and_owned_tia_per_case"
            persist()
            execute_options = {"persist": persist_execution}
            if case_selection is not None:
                execute_options["case_selection"] = case_selection
            if stop_requested is not None:
                execute_options["stop_requested"] = stop_requested
            execution = (execute or execute_suite)(suite, program, backend_factory, revision, **execute_options)
            report["execution"] = execution
            report["status"] = execution["status"]
            report["runtimeVerified"] = case_selection is None and execution["status"] == "passed" and execution.get("runtimeQualified") is True
            report["selectedCasesPassed"] = execution.get("selectedCasesPassed") is True
            report["caseCoverage"] = copy.deepcopy(execution.get("caseCoverage", {}))
            if execution.get("admission") == "stopped_by_request":
                report["admission"] = report["statusReason"] = "stopped_by_request"
                report["stopRequested"] = True
            report["caseCleanupFailed"] = any(case.get("cleanup", {}).get("error") for case in execution.get("cases", []))
            if execution.get("admission") == "stopped_due_to_uncertain_runtime":
                report["admission"] = report["statusReason"] = "stopped_due_to_uncertain_runtime"
                report["runtimeStateUncertain"] = True
    except Exception as error:
        # Native/setup failure is unqualified (blocked), never a false behavioral pass.
        report["failure"] = {"type": type(error).__name__, "message": str(error)}
        report["status"] = "failed" if any(report.get(key) for key in ("detachFailure", "hostCloseFailure", "processExitFailure")) else "blocked"
        report["runtimeVerified"] = False
    finally:
        if client:
            if attached:
                try:
                    cleanup_call = NativeAudit(client, report["cleanup"], persist)
                    detach_owned(cleanup_call)
                except Exception as error:
                    report["detachFailure"] = {"type": type(error).__name__, "message": str(error)}
                    report["status"], report["runtimeVerified"], report["engineeringVerified"] = "failed", False, False
            try:
                report["launcherShutdown"] = client.close()
                report["hostExitCode"] = client.process.returncode
                if report["hostExitCode"] != 0:
                    report["hostCloseFailure"] = {"type": "HostExitFailure", "message": "MCP host exited unsuccessfully."}
                    report["status"], report["runtimeVerified"], report["engineeringVerified"] = "failed", False, False
            except Exception as error:
                report["hostCloseFailure"] = {"type": type(error).__name__, "message": str(error)}
                report["status"], report["runtimeVerified"], report["engineeringVerified"] = "failed", False, False
        try:
            wait_qualification_exit()
        except Exception:
            report["status"], report["runtimeVerified"], report["engineeringVerified"] = "failed", False, False
        if report.get("binaries"):
            try:
                if binary_manifest(host, worker, None if engineering_only else sim_worker) != report["binaries"]:
                    raise RuntimeError("Selected binaries changed during qualification.")
                report["binaryHashesVerified"] = report.get("binaryPreflightVerified") is True
            except Exception as error:
                report["binaryFailure"] = {"type": type(error).__name__, "message": str(error)}
                report["status"], report["runtimeVerified"], report["engineeringVerified"] = "failed", False, False
        report["completedAtUtc"] = utc_now()
        persist()
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--live", action="store_true", help="Qualify engineering and execute on isolated owned PLCSIM instances.")
    mode.add_argument("--engineering-only", "--engineering", action="store_true", help="Import, compile, export and verify save/reopen in owned TIA projects without PLCSIM.")
    parser.add_argument("--fixture", action="append", help="Fixture ID; repeat to select multiple suites.")
    parser.add_argument("--suite", type=pathlib.Path, action="append", help="Custom suite JSON (one suite or array). Defaults to these instead of the catalog; combine with --fixture to include catalog suites.")
    parser.add_argument("--case-selection", type=pathlib.Path, help="With --live, exact suite hash and case/repetition manifest; selected passes never qualify a full suite.")
    parser.add_argument("--stop-file", type=pathlib.Path,
                        help="Stop admission after the current case and verified cleanup when this file exists. Default: <report-directory>/stop-request.json. No native call is cancelled.")
    parser.add_argument("--report-directory", type=pathlib.Path)
    parser.add_argument("--artifacts", type=pathlib.Path, default=ROOT / "artifacts",
                        help="Native host artifact root, independent of frozen binary location; choose a short path for TIA's 143-character project limit.")
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=pathlib.Path, help="Explicit engineering worker executable; required for either native mode.")
    parser.add_argument("--sim-worker", type=pathlib.Path, help="Explicit simulator worker executable; required only for --live.")
    parser.add_argument("--provision-runtime-credential", action="store_true", help="With --live, provision each owned instance with its generated scratch credential before normal power-on.")
    parser.add_argument("--trust-owned-empty-cpu-certificate", action="store_true",
                        help="With provisioning, authorize first-use TLS trust for each owned empty CPU, pinned within each attempt.")
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--continue-on-failure", action="store_true", help="After successful cleanup, continue with a fresh scratch suite following a failure.")
    args = parser.parse_args(argv)
    if not 5 <= args.timeout <= 900:
        parser.error("--timeout must be between 5 and 900 seconds (the host's supported range)")
    native = args.live or args.engineering_only
    if args.stop_file and not native:
        parser.error("--stop-file requires --live or --engineering-only")
    if args.case_selection and not args.live:
        parser.error("--case-selection requires --live")
    if args.trust_owned_empty_cpu_certificate and not args.provision_runtime_credential:
        parser.error("--trust-owned-empty-cpu-certificate requires --provision-runtime-credential")
    if args.provision_runtime_credential and not args.live:
        parser.error("--provision-runtime-credential requires --live")
    if native and not args.worker:
        parser.error("--worker is required for native qualification; worker build selection must be explicit")
    if args.live and not args.sim_worker:
        parser.error("--sim-worker is required for --live")
    suites = load_suites(args.fixture, args.suite)
    selection, selection_raw = None, None
    if args.case_selection:
        selection, selection_raw = load_case_selection(args.case_selection, suites)
        if args.fixture and set(args.fixture) != set(selection):
            parser.error("--fixture must exactly match the suites named by --case-selection")
        suites = [suite for suite in suites if suite["fixtureId"] in selection]
    run_id = "block_tests_" + datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d_%H%M%S") + "_" + uuid.uuid4().hex[:8]
    output = (args.report_directory or ROOT / "artifacts/block-tests/runs" / run_id).resolve()
    if output.exists() and any(output.iterdir()):
        raise ValueError("Report directory must be empty; prior program and failure evidence is never overwritten.")
    output.mkdir(parents=True, exist_ok=True)
    stop = StopRequest(args.stop_file or output / "stop-request.json")
    index = {"schemaVersion": 1, "runId": run_id, "createdAtUtc": utc_now(), "live": args.live,
             "provisionRuntimeCredential": args.provision_runtime_credential,
             "trustOwnedEmptyCpuCertificate": args.trust_owned_empty_cpu_certificate,
             "mode": "runtime" if args.live else "engineering" if args.engineering_only else "offline",
             "qualificationScope": "selected_cases" if selection is not None else "full_suite", "selectedCasesPassed": False,
             "runtimeVerified": False, "engineeringVerified": False, "status": "prepared", "results": []}
    if native:
        index["stopRequest"] = stop.evidence()
    if selection is not None:
        selection_path = output / "case-selection.json"
        with selection_path.open("xb") as stream:
            stream.write(selection_raw)
        index["caseSelectionManifest"] = {**file_record(selection_path), "sourcePath": str(args.case_selection.resolve())}
    for suite in suites:
        destination = output / suite["fixtureId"]
        _, _, report = prepare_suite(suite, destination)
        index["results"].append({"fixtureId": suite["fixtureId"], "path": suite["fixtureId"] + "/result.json", "status": report["status"]})
    save_json(output / "index.json", index)
    if native:
        try:
            index["binaries"] = binary_manifest(args.host, args.worker, args.sim_worker if args.live else None)
        except Exception as error:
            index["status"] = "blocked"
            index["failure"] = {"type": type(error).__name__, "message": str(error)}
            save_json(output / "index.json", index)
            print(f"Native qualification blocked: {error}\nReport: {output}")
            return 2
        save_json(output / "index.json", index)
        for suite, entry in zip(suites, index["results"]):
            if stop():
                index["admission"] = index["statusReason"] = "stopped_by_request"
                index["stopRequest"] = stop.evidence()
                break
            print(f"Qualifying {suite['fixtureId']} ({index['mode']})", flush=True)
            report = qualify_suite(suite, output / suite["fixtureId"], args.host.resolve(), args.timeout,
                                   engineering_only=args.engineering_only, worker=args.worker, sim_worker=args.sim_worker,
                                   expected_binaries=index["binaries"], provision_runtime_credential=args.provision_runtime_credential,
                                   trust_owned_empty_cpu_certificate=args.trust_owned_empty_cpu_certificate,
                                   case_selection=selection[suite["fixtureId"]] if selection is not None else None,
                                   artifacts_directory=args.artifacts, stop_requested=stop)
            result_name = "engineering-result.json" if args.engineering_only else "live-result.json"
            entry["path"], entry["status"] = suite["fixtureId"] + "/" + result_name, report["status"]
            entry["engineeringVerified"] = report["engineeringVerified"]
            entry["runtimeVerified"] = report["runtimeVerified"]
            entry["selectedCasesPassed"] = report.get("selectedCasesPassed") is True
            entry["qualificationScope"] = report.get("qualificationScope", "full_suite")
            entry["caseCoverage"] = report.get("caseCoverage", {})
            if report.get("runtimeStateUncertain") is True:
                entry["runtimeStateUncertain"] = True
                entry["admission"] = index["admission"] = index["statusReason"] = "stopped_due_to_uncertain_runtime"
            elif report.get("admission") == "stopped_by_request":
                entry["admission"] = index["admission"] = index["statusReason"] = "stopped_by_request"
            index["stopRequest"] = stop.evidence()
            save_json(output / "index.json", index)
            print(f"{suite['fixtureId']}: {report['status']}" + (" " + report["failure"]["message"] if report.get("failure") else ""), flush=True)
            if (report.get("runtimeStateUncertain") is True
                    or report.get("admission") == "stopped_by_request"
                    or report.get("admission") == "stopped_due_to_uncertain_runtime"
                    or (report["status"] != "passed" and (not args.continue_on_failure or report.get("detachFailure")
                        or report.get("hostCloseFailure") or report.get("processExitFailure")
                        or report.get("caseCleanupFailed") or report.get("binaryFailure")))):
                break
        index["engineeringVerified"] = all(entry.get("engineeringVerified") is True for entry in index["results"])
        index["runtimeVerified"] = args.live and selection is None and all(entry.get("runtimeVerified") is True for entry in index["results"])
        index["selectedCasesPassed"] = selection is not None and all(entry.get("selectedCasesPassed") is True for entry in index["results"])
        index["status"] = "passed" if all(entry["status"] == "passed" for entry in index["results"]) else "failed" if any(entry["status"] == "failed" for entry in index["results"]) else "blocked"
        index["completedAtUtc"] = utc_now()
        index["stopRequest"] = stop.evidence()
        if args.live:
            remaining = remaining_selection(suites, index["results"], digest, selection)
            if remaining is not None:
                remaining_path = output / "remaining-cases.json"
                save_json(remaining_path, remaining)
                index["remainingCaseSelection"] = file_record(remaining_path)
                index["resumePolicy"] = "Separate explicit subset run only after unresolved outcomes are reconciled; no automatic replay and no merged full-corpus claim."
        save_json(output / "index.json", index)
    print(f"{'Runtime qualification' if args.live else 'Engineering qualification' if args.engineering_only else 'Prepared block tests'}: {output}")
    print(f"{len(suites)} suites; engineeringVerified={index['engineeringVerified']}; runtimeVerified={index['runtimeVerified']}")
    return 0 if not native or index["status"] == "passed" else 1 if index["status"] == "failed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
