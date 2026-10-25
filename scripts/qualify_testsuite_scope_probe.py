"""Sequential actual-Host Test Suite scope evidence; prepare-only never starts processes.

This is a read-only investigation of the retained ts02 fixture, not Test Suite
execution or profile qualification. Recovery gates and store windows are shared
with the translation coordinator; no arbitrary Boolean marker grants admission.
"""
import argparse
import ctypes
from datetime import datetime, timedelta, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import threading
import time

import psutil
from block_test_process import OwnedTiaProcessWatcher
from qualify_worker_startup_exact import identity
from smoke_mcp import Client
from retained_native_lifecycle import wait_retained_terminal


REPO = Path(__file__).resolve().parents[1]
BUILD = REPO / "artifacts/deployment-build/b274e08b806a44f4928e429f190b153d"
HOST = BUILD / "published-host/TiaMcp.Host.exe"
WORKER = BUILD / "src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe"
HOST_SHA = "371ab78953be0026afca604ad7f56fb940f3a869853c9235a2694f438b21820f"
WORKER_SHA = "d98271b29d7d28ca4ff5534557c0714b6ebab7e7542b2670cfbd2d30ac0a7f5a"
PACKAGE = REPO / "artifacts/startup-unblock-20261015/testsuite/manifest.json"
PACKAGE_SHA = "daee99fb731f218db1cac84887a752a1d654da2d575973cb5a6467c0d355de8d"
PROJECT = Path("D:/TIAW/ts02/s/TiaMcpScratch_20261015_075139_5b4719a4/"
               "TiaMcpScratch_20261015_075139_5b4719a4/TiaMcpScratch_20261015_075139_5b4719a4.ap21")
HELPER_SHA = "fff9ca863df86d066faa1f4e69702bee3374db3384f8f4c787b836e84144f4b8"
SCOPE_ORACLE = REPO / "scripts/native/TestSuiteSystemScopeEvidence/verify_scope.py"
SCOPE_ORACLE_SHA = "d90d0c7b2a837118082289b1a697e52a9e074f63af1547af5f38e2ef20c0d361"
CONTROLLER_MANIFEST = REPO / "artifacts/startup-unblock-20261015/testsuite/coordinator-source-manifest-04.json"
CONTROLLER_SOURCES = tuple(REPO / "scripts" / name for name in (
    "qualify_testsuite_scope_probe.py", "qualify_translation_category_probe.py", "smoke_mcp.py",
    "block_test_process.py", "qualify_worker_startup_exact.py", "verify_watch_expansion.py", "verify_watch_workflow.py",
    "verify_translation_populated_category_probe.py", "retained_native_lifecycle.py"))
ALLOWED_TOOLS = frozenset({"tia_start", "tia_session_status", "project_open", "tia_detach"})


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def ref(path):
    path = Path(path).resolve()
    return {"path": str(path), "sha256": sha(path), "bytes": path.stat().st_size}


def save(path, data):
    path = Path(path)
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(data, stream, indent=2, ensure_ascii=False)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    return ref(path)


def same_path(left, right):
    return Path(left).resolve() == Path(right).resolve()


def check_ref(item):
    require(isinstance(item, dict) and ref(item["path"]) == item,
            "Missing or changed exact artifact: " + str(item))


def checked_recovery_gate(args):
    _, gate = shared_api()
    result = gate(args.recovery_gate.resolve(), args.host.resolve(), args.worker.resolve())
    require(result.get("kind") == "completed-independent-checkpoint-retrieval-gate" and
            result.get("completed") is True and result.get("nativeInvoked") is False,
            "Recovery checker did not produce a completed independent file-only receipt.")
    require(any(case.get("rawEvidenceReverification", {}).get("recovery", {}).get("restoredProjectVerified") is True
                for case in result.get("cases", [])), "Recovery foundation has no verified restored project.")
    return result


def shared_api():
    # Imported lazily so --help and source inspection have no OS/native effects.
    from qualify_translation_category_probe import StoreReadWindow, verify_recovery_gate
    return StoreReadWindow, verify_recovery_gate


def binary_package(directory):
    return [ref(p) for p in sorted(Path(directory).iterdir())
            if p.is_file() and p.suffix.lower() in {".exe", ".dll", ".json", ".config"}]


def reviewed_controller_sources(path):
    packet = load(path)
    require(packet.get("kind") == "reviewed-testsuite-controller-sources" and packet.get("nativeInvoked") is False,
            "Reviewed Test Suite controller source manifest required.")
    rows = packet["files"]
    require(len(rows) == len(CONTROLLER_SOURCES) and {Path(row["path"]).resolve() for row in rows} == {p.resolve() for p in CONTROLLER_SOURCES},
            "Exact complete reviewed controller/dependency set required.")
    for row in rows: check_ref(row)
    return rows


def validate_package(args):
    controller_sources = reviewed_controller_sources(args.controller_manifest)
    require(same_path(args.host, HOST) and sha(args.host) == HOST_SHA,
            "This packet admits only the exact b274 Host path and bytes.")
    require(same_path(args.worker, WORKER) and sha(args.worker) == WORKER_SHA,
            "This packet admits only the exact b274 worker path and bytes.")
    require(same_path(args.package, PACKAGE) and sha(args.package) == PACKAGE_SHA,
            "The frozen Test Suite package manifest changed.")
    package = load(args.package)
    require(same_path(package["projectPath"], PROJECT) and PROJECT.is_file(),
            "Only the existing retained ts02 project is admitted.")
    for item in package["admissionArtifacts"] + package["supportingFiles"]:
        check_ref(item)
    require(package["binary"]["sha256"] == HELPER_SHA and sha(SCOPE_ORACLE) == SCOPE_ORACLE_SHA,
            "Frozen collector or independent scope oracle changed.")
    ownership = load(package["ownershipEvidence"]["path"])
    require(ownership.get("completed") is True and same_path(ownership["project"]["path"], PROJECT),
            "The retained completed creation receipt does not own the exact ts02 project.")
    root = args.root.resolve()
    require(root.is_relative_to(Path("D:/TIAW").resolve()) and not root.exists() and
            not root.is_relative_to(Path("D:/TIAW/ts02").resolve()),
            "A fresh run directory under D:/TIAW outside retained ts02 is required.")
    StoreReadWindow, gate = shared_api()
    require(callable(StoreReadWindow) and callable(gate), "Shared store/recovery gate API is missing.")
    package["controllerSourceFiles"] = controller_sources
    package["controllerManifest"] = ref(args.controller_manifest)
    return package


def preparation(args):
    """Collect an executable plan and file-only checks, even if admission is blocked."""
    result = {"kind": "testsuite-scope-coordinator-preflight", "nativeInvoked": False,
              "sourceReady": False, "nativeAdmissionReady": False, "failingProfilesRemainDisabled": True,
              "releaseAccepted": False, "automaticReplay": False, "issues": [],
              "runRoot": str(args.root.resolve()), "projectPath": str(PROJECT),
              "storeRoots": [str(p) for p in store_roots(args.root)],
              "plannedSessions": ["fresh-owned-first-capture-close", "fresh-owned-reopen-capture-close"],
              "allowedMcpTools": sorted(ALLOWED_TOOLS),
              "controllerSource": ref(__file__),
              "scope": "All three retained Test Suite definitions and selected typed system scope only."}
    try:
        package = validate_package(args)
        result.update(sourceReady=True, package=ref(args.package), host=ref(args.host),
                      worker=ref(args.worker), helper=package["binary"], scopeOracle=ref(SCOPE_ORACLE))
    except Exception as error:
        result["issues"].append({"stage": "frozenPackage", "error": str(error)})
    try:
        result["recoveryGate"] = checked_recovery_gate(args)
    except Exception as error:
        result["issues"].append({"stage": "actualHostCheckpointRetrievalGate", "error": str(error)})
    result["nativeAdmissionReady"] = not result["issues"]
    result["dispatchAuthorizedByPreparation"] = False
    return result


def store_roots(root):
    return sorted({(REPO / "artifacts").resolve(), Path("D:/TIAW/ts02").resolve(),
                   Path(root).resolve()}, key=lambda p: str(p).casefold())


def filetime_iso(ticks):
    require(type(ticks) is int and ticks > 0, "Exact positive process FILETIME is required.")
    seconds, fraction = divmod(ticks, 10_000_000)
    value = datetime(1601, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=seconds)
    return value.strftime("%Y-%m-%dT%H:%M:%S") + f".{fraction:07d}Z"


def process_birth(watcher):
    """Read exact 100ns birth from the already retained QUERY handle, never a new PID."""
    from ctypes import wintypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    kernel.GetProcessTimes.restype = wintypes.BOOL
    values = [wintypes.FILETIME() for _ in range(4)]
    if not kernel.GetProcessTimes(watcher._handle, *[ctypes.byref(v) for v in values]):
        raise OSError(ctypes.get_last_error(), "GetProcessTimes on retained handle failed")
    return filetime_iso((values[0].dwHighDateTime << 32) | values[0].dwLowDateTime)


def bind_handle_identity(watcher, info):
    from ctypes import wintypes
    birth = process_birth(watcher)
    require(abs(datetime.fromisoformat(birth.replace("Z", "+00:00")).timestamp() - info["created"]) <= .01,
            "Retained handle birth differs from observed process identity.")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    kernel.QueryFullProcessImageNameW.restype = wintypes.BOOL
    size = wintypes.DWORD(32768)
    buffer = ctypes.create_unicode_buffer(size.value)
    require(kernel.QueryFullProcessImageNameW(watcher._handle, 0, buffer, ctypes.byref(size)),
            "Retained handle image lookup failed.")
    require(same_path(buffer.value, info["exe"]), "Retained handle image differs from observed process identity.")
    watcher.evidence.update(birthAndImageVerified=True, nativeProcessStartTimeUtc=birth,
                            imagePath=buffer.value, sameOsHandleRetained=True)
    return birth


def validate_status(status, pid, project_path=None):
    require(type(pid) is int and pid > 0 and status.get("attached") is True and status.get("ownsPortalProcess") is True and
            status.get("processId") == pid, "The exact owned TIA session identity changed.")
    projects = status.get("projects")
    require(isinstance(projects, list), "Session omitted its project inventory.")
    if project_path is None:
        require(projects == [], "The newly owned native process is not empty.")
    else:
        require(len(projects) == 1 and same_path(projects[0]["path"], project_path) and
                projects[0]["name"] == Path(project_path).stem,
                "Session does not contain only the exact owned ts02 project.")


def exact_exit(entry, expected_code=0):
    info, proof = entry["identity"], entry["proof"]
    require(proof.get("processId") == info["pid"] and proof.get("handleCaptured") is True and
            proof.get("processExitVerified") is True and proof.get("handleClosed") is True and
            proof.get("completed") is True and proof.get("exitCode") == expected_code and
            proof.get("deadlineOrCancellationObserved", False) is False and
            proof.get("birthAndImageVerified") is True and proof.get("sameOsHandleRetained") is True and
            same_path(proof["imagePath"], info["exe"]) and
            abs(datetime.fromisoformat(proof["nativeProcessStartTimeUtc"].replace("Z", "+00:00")).timestamp() - info["created"]) <= .01,
            "Exact retained process-handle exit evidence is incomplete or nonzero.")


def validate_lifecycle(session, host_path, worker_path, helper_path):
    closed, host = session["cleanup"], session["host"]
    require(closed["errors"] == [], "Cleanup retained a failure.")
    require(same_path(host["exe"], host_path), "Wrong Host executable identity.")
    exact_exit(closed["hostExit"])
    require(closed["hostExit"]["identity"] == host, "Host handle proof changed identity.")
    receipt = closed["hostClose"]
    require(receipt.get("hostExitCode") == 0 and receipt.get("forcedHostStop") is False and
            receipt.get("stderrEofObserved") is True and receipt.get("closeTimedOut", False) is False,
            "Host did not close cleanly with complete stderr.")
    exits = closed["descendantExits"]
    require(len(exits) == 2, "Exactly one worker and one owned native process are required.")
    workers = [e for e in exits if same_path(e["identity"]["exe"], worker_path)]
    require(len(workers) == 1, "The exact admitted worker lifetime is absent or ambiguous.")
    worker = workers[0]["identity"]
    require(worker["parentPid"] == host["pid"] and worker["created"] >= host["created"],
            "Worker is not a child of this Host lifetime.")
    natives = [e for e in exits if e is not workers[0]]
    native = natives[0]["identity"]
    require(native["name"] == "Siemens.Automation.Portal.exe" and native["pid"] == session["native"]["pid"] and
            native == session["native"] and native["parentPid"] == worker["pid"] and
            native["created"] >= worker["created"], "Native process identity or ownership changed.")
    for entry in exits:
        exact_exit(entry)
    require(natives[0]["proof"]["nativeProcessStartTimeUtc"] == session["nativeProcessStartTimeUtc"],
            "The native helper admission and retained handle birth differ.")
    worker_receipts = receipt.get("workerExits")
    require(isinstance(worker_receipts, list) and len(worker_receipts) == 1,
            "Exactly one complete Host worker-close receipt is required.")
    reported = worker_receipts[0]
    require(reported.get("processId") == worker["pid"] and reported.get("processExitVerified") is True and
            reported.get("exitCode") == 0 and reported.get("forcedTermination") is False,
            "Host worker-close receipt differs from the retained worker handle proof.")
    stamp = datetime.fromisoformat(reported["startedAtUtc"].replace("Z", "+00:00")).timestamp()
    require(abs(stamp - worker["created"]) <= .01, "Worker receipt has a different process birth.")
    helper = session["helper"]
    exact_exit(helper["exit"])
    require(helper["returncode"] == 0 and helper["identity"] == helper["exit"]["identity"] and
            same_path(helper["identity"]["exe"], helper_path) and helper["identity"]["parentPid"] == session["coordinatorPid"] and
            native["created"] <= helper["identity"]["created"] <= helper["closedAt"] and
            helper["closedAt"] <= closed["requestedAt"] <= closed["closedAt"],
            "Collector did not exit before owning-session cleanup.")
    return {"completed": True, "normalLifecyclePassed": True, "forcedTermination": False,
            "hostProcessId": host["pid"], "workerProcessId": worker["pid"],
            "nativeProcessId": native["pid"], "helperProcessId": helper["identity"]["pid"],
            "retainedHandlesVerified": 4, "projectLockReleaseVerified": False}


def validate_store_window(directory, resource_ref, helper):
    admission = load(resource_ref["path"])
    intent_path, close_path = directory / "store-read-window-intent.json", directory / "store-read-window-close.json"
    intent, closed = load(intent_path), load(close_path)
    require(intent == admission and intent.get("readOnly") is True and intent.get("mode") == "readOnlyProbe" and
            intent.get("sameHandlesHeldThroughDispatch") is True and intent.get("journalsModified") is False,
            "Retained resource admission differs from the held read-only store window.")
    require(closed.get("allClosed") is True and closed.get("bodyFailure") is None and
            closed.get("journalsModified") is False and len(closed.get("locks", [])) == 3 and
            all(item.get("closed") is True for item in closed["locks"]) and
            [item["path"] for item in closed["locks"]] == list(reversed(intent["lockFiles"])),
            "Exact retained store handles were not released in reverse acquisition order.")
    acquired = datetime.fromisoformat(intent["acquiredAtUtc"].replace("Z", "+00:00")).timestamp()
    released = datetime.fromisoformat(closed["releasedAtUtc"].replace("Z", "+00:00")).timestamp()
    require(acquired <= helper["requestedAt"] <= helper["closedAt"] <= released,
            "The complete helper process lifetime was not enclosed by the store-lock window.")
    return {"completed": True, "intent": ref(intent_path), "close": ref(close_path),
            "journalsModified": False, "nativeDispatchWithinHeldWindow": True}


def graceful_close(client):
    """Bounded owned Host EOF; never kill a Host/worker/Siemens process on timeout."""
    try:
        client.process.stdin.close()
    except (BrokenPipeError, OSError):
        pass
    timed_out = False
    try:
        client.process.wait(timeout=255)  # two workers × BridgeClient.WorkerExitBudgetSeconds, plus margin
    except subprocess.TimeoutExpired:
        timed_out = True
    client.output_reader.join(timeout=1)
    client.error_reader.join(timeout=1)
    receipts = []
    for line in list(client.errors):
        try:
            value = json.loads(line)
        except (ValueError, TypeError):
            continue
        if isinstance(value, dict) and isinstance(value.get("bridgeWorkerExit"), dict):
            receipts.append(value["bridgeWorkerExit"])
    for reader, stream in ((client.output_reader, client.process.stdout), (client.error_reader, client.process.stderr)):
        if not reader.is_alive():
            stream.close()
    return {"hostExitCode": client.process.returncode, "forcedHostStop": False,
            "closeTimedOut": timed_out, "workerExits": receipts,
            "stderrEofObserved": not client.error_reader.is_alive()}


class Session:
    def __init__(self, run, ordinal):
        self.run, self.ordinal = run, ordinal
        self.directory = run.root / f"session-{ordinal:02}"
        self.directory.mkdir()
        self.data = {"ordinal": ordinal, "calls": [], "automaticReplay": False, "coordinatorPid": os.getpid()}
        self.client = self.host_watcher = None
        self.tracked, self.tracking_errors = {}, []
        self.ancestry = {}
        self.stop, self.mutex = threading.Event(), threading.Lock()
        self.thread = None
        self.start_attempted = False
        self.helper_alive = False
        self.capture_incomplete = False

    def retain(self, proc, ancestry):
        try:
            info = identity(proc)
            key = info["pid"], info["created"]
            require(ancestry and ancestry[-1] == self.data["host"], "Exact owned startup ancestry unavailable.")
            with self.mutex:
                if key not in self.tracked:
                    watcher = OwnedTiaProcessWatcher(info["pid"], query_exit_code=True)
                    self.tracked[key] = info, watcher
                    self.ancestry[key] = ancestry
                    bind_handle_identity(watcher, info)
                    watcher.evidence["basis"] = "Handle retained while this exact PID and birth was an observed descendant of the owned Host; never reopened for exit proof."
                    save(self.directory / f"process-observed-{info['pid']}-{int(info['created'] * 1000000)}.json",
                         {"identity": info, "ancestry": ancestry, "handleCapture": watcher.evidence})
            return self.tracked[key]
        except BaseException as error:
            # Includes failure before OpenProcess returned a watcher. Existing
            # original handles remain retained and the missing capture is sticky.
            self.capture_incomplete = True
            self.tracking_errors.append("Original handle capture failed: " + type(error).__name__)
            raise

    def scan(self):
        owners = [(self.data["host"], self.host_watcher)] if self.host_watcher is not None else []
        owners += [(info, watcher) for info, watcher in list(self.tracked.values()) if info["name"] == "TiaMcp.Openness.V21.exe"]
        for info, watcher in owners:
            try:
                if watcher.api.wait(watcher._handle, 0) == 0: continue
                parent = psutil.Process(info["pid"])
                require(identity(parent) == info, "Owned discovery identity changed.")
                children = parent.children(recursive=True)
            except psutil.NoSuchProcess:
                if watcher.api.wait(watcher._handle, 0) == 0: continue
                self.capture_incomplete = True
                raise
            for proc in children:
                try:
                    if proc.name() in {"TiaMcp.Openness.V21.exe", "Siemens.Automation.Portal.exe"}:
                        child_info = identity(proc)
                        child_key = child_info["pid"], child_info["created"]
                        existing = self.tracked.get(child_key)
                        if existing is not None:
                            require(existing[0] == child_info and existing[1].evidence.get("birthAndImageVerified") is True and
                                    child_key in self.ancestry, "Previously captured descendant identity changed.")
                            # Keep the original ancestry; its Host can already
                            # be terminal while this owned worker/native lives.
                            continue
                        chain = []
                        for parent in proc.parents():
                            parent_info = identity(parent); chain.append(parent_info)
                            if parent_info == info: break
                        require(chain and chain[-1] == info, "Exact discovery ancestry changed.")
                        if info != self.data["host"]: chain += self.ancestry[(info["pid"], info["created"])]
                        self.retain(proc, chain)
                except BaseException:
                    self.capture_incomplete = True
                    raise

    def monitor(self):
        while not self.stop.wait(.1):
            try:
                self.scan()
            except BaseException as error:
                self.capture_incomplete = True
                self.tracking_errors.append(str(error))
                return

    def capture_complete(self):
        rows = list(self.tracked.values())
        basic = (not self.capture_incomplete and self.host_watcher is not None and
            self.host_watcher.evidence.get("birthAndImageVerified") is True and
            sum(info["name"] == "TiaMcp.Openness.V21.exe" for info, _ in rows) == 1 and
            sum(info["name"] == "Siemens.Automation.Portal.exe" for info, _ in rows) == int(self.start_attempted) and
            all(watcher.evidence.get("birthAndImageVerified") is True for _, watcher in rows))
        if not basic: return False
        host = self.data["host"]
        worker = next(info for info, _ in rows if info["name"] == "TiaMcp.Openness.V21.exe")
        if not (same_path(host["exe"], self.run.args.host) and same_path(worker["exe"], self.run.args.worker) and
                worker["parentPid"] == host["pid"] and worker["created"] >= host["created"] and
                self.ancestry.get((worker["pid"], worker["created"])) == [host]): return False
        for native, _ in rows:
            if native["name"] != "Siemens.Automation.Portal.exe": continue
            if not (native["parentPid"] == worker["pid"] and native["created"] >= worker["created"] and
                    self.ancestry.get((native["pid"], native["created"])) == [worker, host]): return False
        return True

    def select_native(self, pid):
        try:
            self.scan()
            require(self.capture_complete(), "Exact Host/worker/native startup inventory is incomplete.")
            natives = [(info, watcher) for info, watcher in self.tracked.values() if info["name"] == "Siemens.Automation.Portal.exe"]
            require(len(natives) == 1 and natives[0][0]["pid"] == pid,
                    "Returned native PID is not the sole originally ancestry-captured native.")
            return natives[0]
        except BaseException:
            self.capture_incomplete = True
            raise

    def hold_uncaptured_startup(self):
        announced = False
        while True:
            try:
                if not announced:
                    announced = True
                    save(self.directory / "uncaptured-startup.json", {"nativeStartAttempted": self.start_attempted,
                        "originalPopenHandleRetained": getattr(self.client, "process", None) is not None,
                        "constructorOutcomeUnknown": getattr(self.client, "process", None) is None,
                        "capturedOriginalHandlesRetained": True,
                        "retained": [info for info, _ in self.tracked.values()], "errors": self.tracking_errors,
                        "parentTerminalDoesNotProveChildCleanup": True, "externalExactReconciliationRequired": True,
                        "hostDetachAndCloseProhibited": True, "automaticTermination": False, "automaticReplay": False})
                    print("BLOCKED_TESTSUITE_STARTUP_CAPTURE " + str(self.directory / "uncaptured-startup.json"), flush=True)
                time.sleep(.2)
            except BaseException:
                # No automatic release/reconciliation path exists for an unseen
                # native child, including when its parent is already terminal.
                continue

    def require_captured_startup(self):
        try: self.scan()
        except BaseException as error:
            self.capture_incomplete = True
            self.tracking_errors.append("Cleanup capture failed: " + type(error).__name__)
        if not self.capture_complete(): self.hold_uncaptured_startup()

    def await_all_terminal(self, cleanup, timeout_seconds=45, clock=time.monotonic):
        deadline = None; deadline_recorded = False
        while True:
            try:
                if deadline is None: deadline = clock() + timeout_seconds
                self.require_captured_startup()
                watchers = [self.host_watcher, *(watcher for _, watcher in self.tracked.values())]
                if all(w.api.wait(w._handle, 0) == 0 for w in watchers): return
                if clock() >= deadline and not deadline_recorded:
                    cleanup["errors"].append("Exact terminal deadline exceeded; late exit is cleanup only")
                    deadline_recorded = True
                    save(self.directory / "terminal-hold-unknown.json", {"originalHandlesRetained": True,
                        "qualificationFailed": True, "automaticTermination": False, "automaticReplay": False})
                time.sleep(.2)
            except BaseException as error:
                cleanup["errors"].append("Terminal hold interrupted: " + type(error).__name__)

    def call(self, name, arguments=None):
        require(name in ALLOWED_TOOLS, "This read-only coordinator forbids MCP tool: " + name)
        row = {"name": name, "arguments": arguments or {}, "requestedAt": time.time()}
        self.data["calls"].append(row)
        save(self.directory / f"call-{len(self.data['calls']):02}-{name}-intent.json", row)
        response = self.client.request("tools/call", {"name": name, "arguments": arguments or {}})
        row["response"] = response
        row["returnedAt"] = time.time()
        save(self.directory / f"call-{len(self.data['calls']):02}-{name}-return.json", row)
        require(response.get("isError") is not True, name + " returned an error: " + json.dumps(response))
        packet = response.get("structuredContent")
        if packet is None:
            packet = json.loads("\n".join(c["text"] for c in response.get("content", []) if c.get("type") == "text"))
        return packet

    def start(self):
        self.run.check_frozen()
        self.client = Client.__new__(Client); client_error = None
        try: self.client.__init__(self.run.args.host.resolve(), self.run.args.timeout,
                                 ["--worker", str(self.run.args.worker.resolve()), "--artifacts", str(self.run.root)])
        except BaseException as error: client_error = error
        if getattr(self.client, "process", None) is None: self.hold_uncaptured_startup()
        self.data["host"] = identity(psutil.Process(self.client.process.pid))
        self.host_watcher = OwnedTiaProcessWatcher(self.client.process.pid, query_exit_code=True)
        try: bind_handle_identity(self.host_watcher, self.data["host"])
        except BaseException:
            self.capture_incomplete = True
            raise
        save(self.directory / "host-start.json", self.data["host"])
        self.thread = threading.Thread(target=self.monitor, daemon=True)
        self.thread.start()
        if client_error is not None: raise client_error
        self.data["initialize"] = self.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                      "clientInfo": {"name": "testsuite-read-only-scope-evidence", "version": "1"}})
        self.client.notify("notifications/initialized")
        self.start_attempted = True
        started = self.call("tia_start")
        validate_status(started, started.get("processId"))
        native, watcher = self.select_native(started["processId"])
        self.data["native"] = native
        self.data["nativeProcessStartTimeUtc"] = watcher.evidence["nativeProcessStartTimeUtc"]
        require(not self.tracking_errors, "Process monitoring failed: " + str(self.tracking_errors))
        validate_status(self.call("tia_session_status"), native["pid"])
        opened = self.call("project_open", {"path": str(PROJECT)})
        require(opened.get("opened") is True and opened.get("saved") is False and
                opened.get("processId") == native["pid"] and same_path(opened["project"]["path"], PROJECT),
                "project_open did not read the exact owned project without saving.")
        status = self.call("tia_session_status")
        validate_status(status, native["pid"], PROJECT)
        self.open_ref = save(self.directory / "open-observation.json", {"host": self.data["host"],
            "native": native, "nativeProcessStartTimeUtc": self.data["nativeProcessStartTimeUtc"],
            "projectOpen": opened, "sessionStatus": status, "actualMcpHost": True,
            "nativeSavedStateCheckedByCollectorBeforeExport": True, "observedAt": time.time()})

    def capture(self):
        StoreReadWindow, _ = shared_api()
        # Host calls happen outside this shared operation-store lock window.
        with StoreReadWindow(store_roots(self.run.root), PROJECT, self.directory / "store-read-window") as held:
            self.run.check_frozen()
            native = self.data["native"]
            require(identity(psutil.Process(native["pid"])) == native, "Exact native process changed before helper dispatch.")
            resource_ref = save(self.directory / "resource-admission.json", held.evidence)
            package = self.run.package
            admission = {"mode": "inspect-system-scope", "ownedScratch": True, "allProjectObjectsOwned": True,
                "sequentialNativeAdmission": True, "checkpointRetrievalGatePassed": True, "systemCaseName": "SingleStep",
                "artifactRoot": str(Path("D:/TIAW").resolve()), "outputDirectory": str(self.directory / "capture"),
                "processId": native["pid"], "nativeProcessStartTimeUtc": self.data["nativeProcessStartTimeUtc"],
                "projectPath": str(PROJECT), "binarySha256": package["binary"]["sha256"],
                "apiSha256": package["apiSha256"], "ownershipEvidence": package["ownershipEvidence"],
                "openObservationEvidence": self.open_ref, "resourceAdmissionEvidence": resource_ref,
                "checkpointRetrievalEvidence": self.run.gate_ref, "reviewedSource": package["reviewedSource"],
                "sourceFiles": package["sourceFiles"]}
            admission_ref = save(self.directory / "helper-admission.json", admission)
            self.run_helper(admission_ref)
            result = load(self.directory / "capture/result.json")
            require(result.get("completed") is True and result.get("beforeModified") is False and
                    result.get("afterModified") is False and not (self.directory / "capture/failure.json").exists(),
                    "Collector failed or the exact saved project changed.")
        validate_status(self.call("tia_session_status"), native["pid"], PROJECT)
        self.data["resourceWindowVerification"] = validate_store_window(self.directory, resource_ref, self.data["helper"])

    def run_helper(self, admission):
        info = {"admission": admission, "binary": self.run.package["binary"], "requestedAt": time.time()}
        self.data["helper"] = info
        watcher = None; process = None
        try:
            with (self.directory / "helper-stdout.log").open("xb") as out, (self.directory / "helper-stderr.log").open("xb") as err:
                process = subprocess.Popen([info["binary"]["path"], admission["path"]], cwd=REPO,
                                           stdout=out, stderr=err, creationflags=subprocess.CREATE_NO_WINDOW)
                self.helper_alive = True
                try:
                    info["identity"] = identity(psutil.Process(process.pid))
                    watcher = OwnedTiaProcessWatcher(process.pid, query_exit_code=True)
                    bind_handle_identity(watcher, info["identity"])
                    save(self.directory / "helper-start.json", info)
                    info["returncode"] = process.wait(timeout=self.run.args.timeout)
                except BaseException as error:
                    info["returncode"] = None
                    info["failure"] = type(error).__name__ + ": read-only collector outcome unknown; no retry/termination"
                finally:
                    # The Popen object retains the original CreateProcess handle
                    # even if startup identity capture failed. This frozen helper
                    # attaches read-only and never creates a native Portal.
                    if process.returncode is None:
                        try:
                            save(self.directory / "helper-outcome-unknown.json", {"pid": process.pid,
                                "originalCreateProcessHandleRetained": True, "storeLocksRetained": True,
                                "hostCloseProhibited": True, "automaticTermination": False, "automaticReplay": False})
                        except BaseException: pass
                    while process.returncode is None:
                        try: process.wait(timeout=1)
                        except BaseException as interrupted:
                            info["failure"] = info.get("failure") or type(interrupted).__name__ + ": terminal hold interrupted"
                    if watcher is not None:
                        proof = wait_retained_terminal(watcher, timeout_seconds=0)
                        info["exit"] = {"identity": info["identity"], "proof": proof}
                    else:
                        info["exit"] = {"proof": {"completed": False, "reason": "Startup identity was not captured"}}
                    info["terminalReturncode"] = process.returncode
                    self.helper_alive = False
                info["closedAt"] = time.time()
            require(info.get("returncode") == 0 and info.get("failure") is None, "Collector did not return successfully.")
            exact_exit(info["exit"])
        finally:
            require(process is None or process.returncode is not None, "Original helper must be terminal before releasing its store window")
            save(self.directory / "helper-close.json", info)

    def close(self):
        require(not self.helper_alive, "Host close prohibited while the exact attached helper may run")
        cleanup = {"requestedAt": time.time(), "errors": [], "descendantExits": [], "forcedTermination": False}
        self.data["cleanup"] = cleanup
        if self.client is None:
            cleanup["errors"].append("Client construction did not return an owned Host.")
        else:
            # Do not stop discovery, detach, EOF, or close an original handle
            # until the expected startup inventory has been captured exactly.
            self.require_captured_startup()
            if self.start_attempted:
                try:
                    detached = self.call("tia_detach")
                    cleanup["detach"] = detached
                    require(detached.get("attached") is False and detached.get("ownsPortalProcess") is False and
                            detached.get("disposedOwnedPortalConnection") is True,
                            "Detach did not dispose this owned connection.")
                except BaseException as error:
                    cleanup["errors"].append("detach: " + str(error))
            try:
                cleanup["hostClose"] = graceful_close(self.client)
            except BaseException as error:
                cleanup["errors"].append("host close: " + str(error))
            cleanup["hostLogs"] = list(self.client.errors)
            self.await_all_terminal(cleanup)
        self.stop.set()
        if self.thread is not None:
            try: self.thread.join(timeout=2)
            except BaseException as error: cleanup["errors"].append("monitor join interrupted: " + type(error).__name__)
            if self.thread.is_alive():
                cleanup["errors"].append("Process monitor did not stop.")
        cleanup["errors"].extend(self.tracking_errors)
        for info, watcher in list(self.tracked.values()):
            try:
                proof = wait_retained_terminal(watcher, timeout_seconds=45)
            except BaseException as error:
                proof = getattr(error, "evidence", {"error": str(error)})
                cleanup["errors"].append("owned descendant exit: " + str(error))
            cleanup["descendantExits"].append({"identity": info, "proof": proof})
        if self.host_watcher is not None:
            try:
                proof = wait_retained_terminal(self.host_watcher, timeout_seconds=1)
            except BaseException as error:
                proof = getattr(error, "evidence", {"error": str(error)})
                cleanup["errors"].append("Host handle exit: " + str(error))
            cleanup["hostExit"] = {"identity": self.data["host"], "proof": proof}
        cleanup["closedAt"] = time.time()
        save(self.directory / "lifecycle.json", self.data)
        return validate_lifecycle(self.data, self.run.args.host, self.run.args.worker, self.run.package["binary"]["path"])


class Run:
    def __init__(self, args, preparation_result):
        require(preparation_result["nativeAdmissionReady"] is True, "Preflight gate did not pass.")
        self.args, self.root = args, args.root.resolve()
        self.package = validate_package(args)
        self.root.mkdir(parents=True, exist_ok=False)
        self.frozen_files = binary_package(args.host.parent) + binary_package(args.worker.parent)
        self.frozen_files += binary_package(Path(self.package["binary"]["path"]).parent)
        self.frozen_files += self.package["admissionArtifacts"] + self.package["supportingFiles"] + [ref(args.package)]
        self.frozen_files += self.package["controllerSourceFiles"] + [self.package["controllerManifest"]]
        self.frozen_files.append(ref(args.recovery_gate))
        source_dir = self.root / "controller-source"
        source_dir.mkdir()
        sources = []
        for name in ("qualify_testsuite_scope_probe.py", "qualify_translation_category_probe.py", "smoke_mcp.py",
                     "block_test_process.py", "qualify_worker_startup_exact.py", "verify_watch_expansion.py", "verify_watch_workflow.py",
                     "retained_native_lifecycle.py"):
            original = REPO / "scripts" / name
            target = source_dir / name
            shutil.copyfile(original, target)
            self.frozen_files.append(ref(original))
            sources.append({"original": ref(original), "snapshot": ref(target)})
        shutil.copyfile(SCOPE_ORACLE, source_dir / "verify_scope.py")
        self.scope_oracle_path = source_dir / "verify_scope.py"
        save(self.root / "controller-source-manifest.json", sources + [{"original": ref(SCOPE_ORACLE), "snapshot": ref(self.scope_oracle_path)}])
        save(self.root / "frozen-package-files.json", self.frozen_files)
        save(self.root / "preflight.json", preparation_result)
        shutil.copyfile(args.recovery_gate, self.root / "recovery-gate-input.json")
        self.gate_ref = save(self.root / "recovery-gate-verification.json", preparation_result["recoveryGate"])
        self.report = {"kind": "testsuite-read-only-system-scope-investigation", "completed": False,
                       "actualMcpHost": True, "nativeSequential": True, "automaticReplay": False,
                       "nativeMutationInvoked": False, "nativeTestExecutionInvoked": False,
                       "endpointConnectionInvoked": False, "failingProfilesRemainDisabled": True,
                       "releaseAccepted": False, "restorationVerified": False, "sessions": []}

    def check_frozen(self):
        for item in self.frozen_files:
            check_ref(item)

    def execute(self):
        for ordinal in (1, 2):
            session = Session(self, ordinal)
            try:
                session.start()
                session.capture()
            except BaseException as error:
                session.data["failure"] = str(error)
                raise
            finally:
                try:
                    session.data["lifecycleVerification"] = session.close()
                finally:
                    self.report["sessions"].append(session.data)
            # A failed capture or incomplete close never admits a second native run.
            self.check_frozen()
        first, second = self.report["sessions"]
        require(first["cleanup"]["closedAt"] <= second["host"]["created"] and
                first["host"]["pid"] != second["host"]["pid"], "Fresh Host started before exact prior cleanup.")
        spec = importlib.util.spec_from_file_location("retained_testsuite_scope_oracle", self.scope_oracle_path)
        scope = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(scope)
        verdict = scope.compare(scope.observe(self.root / "session-01/capture"), scope.observe(self.root / "session-02/capture"))
        self.report["independentVerification"] = save(self.root / "independent-scope-verification.json", verdict)
        require(verdict["freshReopenVerified"] is True, "Independent read-only capture/fresh-reopen consistency failed.")
        self.report.update(completed=True, collectionConsistencyPassed=True, freshReopenVerified=True,
                           scopePreservationPassed=verdict["scopePreservationPassed"],
                           folderDiagnosis=verdict["first"]["folderDiagnosis"],
                           originalImportVersusExportCauseEstablished=False,
                           projectWidePeerPreservationVerified=False,
                           acceptance="Read-only current-state investigation completed; no Test Suite profile promotion.")
        return self.report


def parser():
    value = argparse.ArgumentParser(description=__doc__)
    value.add_argument("--root", type=Path, required=True, help="Fresh output under D:/TIAW, outside ts02.")
    value.add_argument("--recovery-gate", type=Path, required=True, help="Six hash-bound actual-Host interruption receipts; all are reverified.")
    value.add_argument("--host", type=Path, default=HOST)
    value.add_argument("--worker", type=Path, default=WORKER)
    value.add_argument("--package", type=Path, default=PACKAGE)
    value.add_argument("--controller-manifest", type=Path, default=CONTROLLER_MANIFEST)
    modes = value.add_mutually_exclusive_group(required=True)
    modes.add_argument("--prepare-only", action="store_true", help="File-only checks; never starts Host/TIA/helper or acquires stores.")
    modes.add_argument("--execute-native", action="store_true", help="Integration owner only, sequential native window after recovery gates.")
    value.add_argument("--output", type=Path, help="Required fresh preflight report path for --prepare-only.")
    value.add_argument("--timeout", type=int, default=240)
    return value


def main(argv=None):
    args = parser().parse_args(argv)
    require(30 <= args.timeout <= 600, "Timeout must be within 30..600 seconds.")
    require(args.prepare_only == (args.output is not None), "Use --output exactly with --prepare-only.")
    prepared = preparation(args)
    if args.prepare_only:
        args.output.resolve().parent.mkdir(parents=True, exist_ok=True)
        save(args.output.resolve(), prepared)
        print(json.dumps({"nativeInvoked": False, "sourceReady": prepared["sourceReady"],
                          "nativeAdmissionReady": prepared["nativeAdmissionReady"], "output": str(args.output.resolve())}))
        return 0 if prepared["nativeAdmissionReady"] else 2
    require(prepared["nativeAdmissionReady"], "Native dispatch blocked: " + json.dumps(prepared["issues"]))
    run = Run(args, prepared)
    try:
        run.execute()
    except BaseException as error:
        run.report["failure"] = str(error)
    finally:
        save(run.root / "result.json", run.report)
    print(json.dumps({"completed": run.report["completed"], "root": str(run.root),
                      "scopePreservationPassed": run.report.get("scopePreservationPassed"), "failingProfilesRemainDisabled": True}))
    return 0 if run.report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
