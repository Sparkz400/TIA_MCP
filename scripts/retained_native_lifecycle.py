"""Reusable exact owned process tracking. No terminate, replay, or PID cleanup.

Start tracking before initialize/tia_start. A deadline or cancellation reports
unknown and keeps original handles until terminal proof; disappearance is never
an exit proof. A missing startup handle cannot be reconciled automatically.
"""
import copy
import ctypes
from contextlib import nullcontext
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import threading
import time

import psutil
from block_test_process import OwnedTiaProcessWatcher
from qualify_worker_startup_exact import identity


def require(condition, message):
    if not condition: raise RuntimeError(message)


def utc(): return datetime.now(timezone.utc).isoformat()


def emit(path, value):
    with Path(path).open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2); stream.write("\n"); stream.flush(); os.fsync(stream.fileno())


def bind_original_handle(watcher, info):
    """Read birth/image using the already captured handle, never a PID reopen."""
    from ctypes import wintypes
    kernel = watcher.api.kernel
    kernel.GetProcessTimes.argtypes = [wintypes.HANDLE, *([ctypes.POINTER(wintypes.FILETIME)] * 4)]
    kernel.GetProcessTimes.restype = wintypes.BOOL
    times = [wintypes.FILETIME() for _ in range(4)]
    require(kernel.GetProcessTimes(watcher._handle, *(ctypes.byref(t) for t in times)), "Original process birth unavailable")
    ticks = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
    seconds, remainder = divmod(ticks, 10000000)
    birth = datetime(1601, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=seconds)
    exact_birth = birth.strftime("%Y-%m-%dT%H:%M:%S") + f".{remainder:07d}Z"
    require(abs(ticks / 10000000 - 11644473600 - info["created"]) <= .000002, "Original process birth differs from observed identity")
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    kernel.QueryFullProcessImageNameW.restype = wintypes.BOOL
    size = wintypes.DWORD(32768); image = ctypes.create_unicode_buffer(size.value)
    require(kernel.QueryFullProcessImageNameW(watcher._handle, 0, image, ctypes.byref(size)), "Original process image unavailable")
    require(Path(image.value).resolve() == Path(info["exe"]).resolve(), "Original process image differs")
    watcher.evidence.update(birthAndImageVerified=True, processStartedAtUtc=exact_birth,
        imagePath=image.value, sameOsHandleRetained=True, originalFileTimeTicks=ticks,
        basis="Original handle captured from exact owned startup ancestry before native reply; never reopened for cleanup")
    return exact_birth


def wait_retained_terminal(watcher, *, timeout_seconds=45, report=None, clock=time.monotonic,
                           mutex=None, publish=None):
    """Never close a live handle, including on timeout, cancellation, or wait error."""
    require(not watcher.evidence.get("handleClosed"), "Cannot wait a closed original handle")
    deadline = clock() + timeout_seconds; unknown = []
    def failure(message):
        if message not in unknown:
            unknown.append(message)
            packet = {"processId": watcher.evidence["processId"], "reason": message,
                "originalHandleRetained": True, "automaticTermination": False,
                "dependentDispatchProhibited": True, "atUtc": utc()}
            try:
                if report: report(packet)
                print("BLOCKED_EXACT_PROCESS_CLEANUP " + json.dumps(packet), flush=True)
            except BaseException: pass
    while True:
        try:
            # Coordinate handle access with discovery without holding the tree
            # lock over the whole (possibly unbounded) terminal wait.
            with mutex if mutex is not None else nullcontext():
                result = watcher.api.wait(watcher._handle, 200)
            if result == 0: break
            if result != 258: failure("Original handle wait failed: " + str(result))
            if clock() >= deadline: failure("Exact terminal deadline exceeded")
        except BaseException as error:
            failure("Wait interrupted: " + type(error).__name__)
    # Publication and CloseHandle are one critical section. Keep this outside
    # the retrying wait loop so a finalization error can never re-wait a closed
    # handle. The per-row finalizer serializes multiple close_record callers.
    with mutex if mutex is not None else nullcontext():
        proof = copy.deepcopy(watcher.evidence)
        try:
            while True:
                try:
                    code = watcher.api.exit_code(watcher._handle)
                    break
                except (KeyboardInterrupt, SystemExit) as error:
                    # This object is signaled, but its exit code is still
                    # required before CloseHandle. Cancellation is sticky and
                    # cannot abandon that last read of the original object.
                    failure("Terminal query interrupted: " + type(error).__name__)
            require(type(code) is int, "Original handle terminal code unavailable")
            proof.update(processExitVerified=True, exitCode=code, terminalAtUtc=utc())
            proof["handleClosed"] = watcher.close() is True
        except BaseException as error:
            proof.update(terminalEvidenceError=type(error).__name__ + ": " + str(error))
        finally:
            proof.update(sameOsHandleRetained=True, forcedTermination=False,
                deadlineOrCancellationObserved=bool(unknown), unknownReasons=list(unknown))
            proof["completed"] = (proof.get("processExitVerified") is True and
                proof.get("handleClosed") is True and "terminalEvidenceError" not in proof)
            if publish is not None: publish(proof)
        return proof


class RetainedNativeTree:
    """Track owned Host/worker/native descendants before any native reply."""
    def __init__(self, process, host_executable, worker_executable, directory, *,
                 watcher_factory=OwnedTiaProcessWatcher, binder=bind_original_handle, start_thread=True):
        self.process = process; self.host_executable = Path(host_executable).resolve()
        self.worker_executable = Path(worker_executable).resolve(); self.directory = Path(directory)
        self.watcher_factory, self.binder = watcher_factory, binder
        self.entries, self.errors = {}, []; self.pending_start = None; self.unknown = False
        # This adapter owns an engineering Host. Its worker startup is expected
        # from construction, before initialize/discovery can time out. Absence
        # is never a positive proof that the Host did not launch that worker.
        self.expected_worker_count = 1; self.accepted_native_keys = set(); self.inventory_wait_reasons = []
        self.mutex = threading.RLock(); self.finalizers = {}; self.stop = threading.Event(); self.thread = None
        self.finalization_errors = []; self.cleanup_reasons = []
        self.directory.mkdir(exist_ok=False)
        self.anchor = self.retain(psutil.Process(process.pid), "host", [])
        require(Path(self.anchor["identity"]["exe"]).resolve() == self.host_executable, "Wrong owned Host image")
        if start_thread:
            self.thread = threading.Thread(target=self.monitor, daemon=True); self.thread.start()

    def retain(self, process, role, ancestry):
        with self.mutex:
            info = identity(process); key = info["pid"], info["created"]
            if key in self.entries: return self.entries[key]
            watcher = self.watcher_factory(info["pid"], query_exit_code=True)
            row = {"identity": info, "role": role, "ancestry": ancestry, "watcher": watcher, "proof": None}
            self.entries[key] = row  # retain even if identity validation fails
            self.finalizers[key] = threading.Lock()
            try:
                self.binder(watcher, info)
                require(identity(process) == info, "Process changed during original handle capture")
                original_value = getattr(watcher._handle, "value", watcher._handle)
                watcher.evidence.update(ownerProcessId=os.getpid(),
                    ownerHandleValue=original_value if type(original_value) is int else None)
                if role == "worker": require(Path(info["exe"]).resolve() == self.worker_executable, "Unexpected worker image")
                if role == "native": require(info["name"] == "Siemens.Automation.Portal.exe", "Unexpected native image")
                emit(self.directory / f"process-{info['pid']}-{watcher.evidence['originalFileTimeTicks']}-start.json",
                    {k: copy.deepcopy(v) for k, v in row.items() if k != "watcher"} | {"handle": copy.deepcopy(watcher.evidence)})
            except BaseException as error:
                self.errors.append(type(error).__name__ + ": " + str(error)); self.unknown = True
                raise
            return row

    def snapshot(self):
        with self.mutex: return list(self.entries.values())

    def collect(self):
        # Serialize the *entire* pass, including ancestry and capture. A second
        # collector must re-evaluate the inventory, not resume a stale snapshot.
        with self.mutex:
            if not self.stop.is_set(): self._collect_locked()

    def _terminal_locked(self, row):
        proof = row["proof"]
        if proof is not None:
            require(proof.get("processExitVerified") is True and type(proof.get("exitCode")) is int,
                "Retained terminal proof is incomplete")
            return True
        require(not row["watcher"].evidence.get("handleClosed"), "Original handle closed without terminal proof")
        result = row["watcher"].api.wait(row["watcher"]._handle, 0)
        require(result in (0, 258), "Original handle discovery wait failed: " + str(result))
        return result == 0

    def _collect_locked(self):
        owners = [r for r in self.snapshot() if r["role"] in {"host", "worker"} and r["proof"] is None]
        for owner in owners:
            try:
                if self._terminal_locked(owner): continue
                current = psutil.Process(owner["identity"]["pid"])
                require(identity(current) == owner["identity"], "Owned discovery identity changed")
                # The admitted ownership chain is strictly Host -> worker ->
                # native. Descendants of TIA are vendor helpers, not candidate
                # workers/native starts. Do not recursively race their teardown.
                children = current.children(recursive=False)
            except psutil.NoSuchProcess:
                # Recheck the retained handle; process-table disappearance alone
                # cannot establish exit and cannot cause handle disposal.
                if self._terminal_locked(owner): continue
                self.errors.append("Uncaptured owner disappearance while original handle remained live"); self.unknown = True
                raise
            except BaseException as error:
                self.errors.append("Owned discovery failure: " + type(error).__name__ + ": " + str(error)); self.unknown = True
                raise
            for child in children:
                try:
                    reused = any(r["identity"]["pid"] == child.pid for r in self.entries.values())
                    # Filter new, observed non-targets before requesting birth
                    # or ancestry: short-lived diagnostic helpers are not native
                    # startup candidates. An unavailable name is NOT an exclusion
                    # and remains fail-closed, as does a known candidate vanishing.
                    name = None
                    if not reused:
                        name = child.name()
                        if name not in {"TiaMcp.Openness.V21.exe", "Siemens.Automation.Portal.exe"}: continue
                    # psutil Process instances cache creation time. Match that
                    # exact birth before asking for parents that may have exited.
                    # Never reconstruct an already captured ancestry chain.
                    key = child.pid, child.create_time()
                    captured = self.entries.get(key)
                    if captured is not None:
                        require(captured["watcher"].evidence.get("birthAndImageVerified") is True,
                            "Previously captured child has no exact original handle binding")
                        try: current_identity = identity(child)
                        except psutil.NoSuchProcess:
                            # The cached full birth must already match, and only
                            # the retained original handle can establish exit.
                            require(self._terminal_locked(captured),
                                "Captured child vanished while original handle remained live")
                            continue
                        require(current_identity == captured["identity"], "Previously captured exact child identity changed")
                        continue
                    require(not reused, "Previously captured child PID has a different birth; no adoption")
                    chain = []
                    for parent in child.parents():
                        entry = identity(parent); chain.append(entry)
                        if entry == owner["identity"]: break
                    require(chain and chain[-1] == owner["identity"], "Exact owned startup ancestry unavailable")
                    # Preserve the already verified ancestry above this owner
                    # before publishing the immutable start receipt.
                    chain.extend(a for a in owner["ancestry"] if a not in chain)
                    row = self.retain(child, "native" if name == "Siemens.Automation.Portal.exe" else "worker", chain)
                    require((row["identity"]["pid"], row["identity"]["created"]) == key and row["identity"]["name"] == name,
                        "Descendant changed during discovery; no adoption")
                    if row["role"] == "worker" and row not in owners: owners.append(row)
                except BaseException as error:
                    self.errors.append("Uncaptured descendant: " + type(error).__name__ + ": " + str(error)); self.unknown = True
                    raise

    def monitor(self):
        while not self.stop.wait(.05):
            try: self.collect()
            except BaseException as error:
                with self.mutex:
                    self.errors.append("Startup monitor: " + type(error).__name__ + ": " + str(error)); self.unknown = True
                return

    def one(self, role, pid=None):
        self.collect()
        rows = [r for r in self.snapshot() if r["role"] == role and r["proof"] is None and (pid is None or r["identity"]["pid"] == pid)]
        require(not self.errors and len(rows) == 1, "Exact sole retained " + role + " unavailable")
        return rows[0]

    def begin_start(self):
        with self.mutex: self._begin_start_locked()

    def _begin_start_locked(self):
        require(not self.stop.is_set() and self.pending_start is None and not self.errors and not self.unknown and
            not self.inventory_wait_reasons and not self.finalization_errors,
            "Earlier native startup remains unknown")
        self.collect()
        # tia_start may be the first call that launches the lazy worker. The
        # expected worker remains mandatory at reply acceptance and every
        # cleanup boundary; absence here is not a cleanup/no-dispatch proof.
        natives = [r for r in self.snapshot() if r["role"] == "native"]
        require(all(r["proof"] is not None and r["proof"].get("completed") is True and
            r["proof"].get("exitCode") == 0 and not r["proof"].get("deadlineOrCancellationObserved") for r in natives) and
            {(r["identity"]["pid"], r["identity"]["created"]) for r in natives} == self.accepted_native_keys,
            "Earlier or unexpected native startup has no complete exact lifetime")
        self.pending_start = {(r["identity"]["pid"], r["identity"]["created"]) for r in self.snapshot() if r["role"] == "native"}

    def accept_start(self, pid):
        with self.mutex:
            require(self.pending_start is not None, "No native start was admitted")
            self.collect()
            require(not self.unknown and not self.errors and self.worker_captured(), "Exact startup capture is incomplete")
            fresh = [r for r in self.snapshot() if r["role"] == "native" and (r["identity"]["pid"], r["identity"]["created"]) not in self.pending_start]
            require(len(fresh) == 1 and fresh[0]["identity"]["pid"] == pid and fresh[0]["proof"] is None,
                "Exactly one fresh native identity is required for this startup")
            row = fresh[0]; worker = next(r for r in self.snapshot() if r["role"] == "worker")
            require(row["identity"]["parentPid"] == worker["identity"]["pid"] and worker["identity"] in row["ancestry"],
                "Fresh native identity does not have the exact captured worker ancestry")
            self.accepted_native_keys.add((row["identity"]["pid"], row["identity"]["created"]))
            self.pending_start = None
            return row

    def worker_captured(self):
        if not getattr(self, "anchor", None): return False
        rows = [r for r in self.snapshot() if r["role"] == "worker"]
        return len(rows) == self.expected_worker_count and all(
            r["watcher"].evidence.get("birthAndImageVerified") is True and
            Path(r["identity"]["exe"]).resolve() == self.worker_executable and
            r["identity"]["parentPid"] == self.anchor["identity"]["pid"] and
            self.anchor["identity"] in r["ancestry"] for r in rows)

    def pending_captured(self):
        if self.pending_start is None: return True
        fresh = [r for r in self.snapshot() if r["role"] == "native" and (r["identity"]["pid"], r["identity"]["created"]) not in self.pending_start]
        return len(fresh) == 1

    def native_inventory_complete(self):
        natives = [r for r in self.snapshot() if r["role"] == "native"]
        workers = [r for r in self.snapshot() if r["role"] == "worker"]
        if natives and (len(workers) != 1 or any(r["watcher"].evidence.get("birthAndImageVerified") is not True or
                r["identity"]["parentPid"] != workers[0]["identity"]["pid"] or workers[0]["identity"] not in r["ancestry"] for r in natives)):
            return False
        captured = {(r["identity"]["pid"], r["identity"]["created"]) for r in natives}
        if self.pending_start is None: return captured == self.accepted_native_keys
        return self.pending_start == self.accepted_native_keys and self.pending_captured()

    def await_capture_complete(self):
        """Hold before detach/EOF/finalization if an expected startup is uncaptured.

        No process-table absence grants a no-dispatch exemption. A failed capture
        is sticky and requires explicit external reconciliation; this function
        never invents a replacement handle or an automatic reconciliation.
        """
        while True:
            try:
                with self.mutex:
                    self.collect()
                    if not self.unknown and self.worker_captured() and self.native_inventory_complete(): return
                reason = "Expected startup inventory incomplete; exact handles and dispatch boundary retained"
                if reason not in self.inventory_wait_reasons:
                    self.inventory_wait_reasons.append(reason)
                    packet = {"reason": reason, "expectedWorkerCount": self.expected_worker_count,
                        "workerStartupCaptured": self.worker_captured(), "nativeStartupInventoryComplete": self.native_inventory_complete(),
                        "captureErrors": list(self.errors), "hostEofProhibited": True, "nativeDetachProhibited": True,
                        "originalHandlesRetained": True, "automaticTermination": False, "requiresExternalReconciliation": True,
                        "atUtc": utc()}
                    emit(self.directory / "startup-inventory-blocked.json", packet)
                    print("BLOCKED_STARTUP_INVENTORY " + json.dumps(packet), flush=True)
                time.sleep(.2)
            except BaseException as error:
                reason = "Startup inventory hold interrupted: " + type(error).__name__
                if reason not in self.inventory_wait_reasons:
                    self.inventory_wait_reasons.append(reason)
                    try: print(reason + "; original handles retained, no detach/EOF", flush=True)
                    except BaseException: pass

    def all_terminal(self):
        with self.mutex: return all(self._terminal_locked(r) for r in self.snapshot())

    def close_record(self, row, timeout_seconds=45):
        key = row["identity"]["pid"], row["identity"]["created"]
        with self.mutex:
            require(self.entries.get(key) is row, "Cannot finalize an unretained process")
            finalizer = self.finalizers[key]
        # Only one waiter can consume this original handle. Discovery continues
        # between its wait chunks; close + proof publication share its mutex.
        with finalizer:
            return self._close_record_once(row, timeout_seconds)

    def _close_record_once(self, row, timeout_seconds):
        self.await_capture_complete()
        if row["proof"] is None:
            reports = []
            def report(packet):
                packet.update(identity=row["identity"], role=row["role"], handleEvidence=copy.deepcopy(row["watcher"].evidence))
                reports.append(copy.deepcopy(packet))
                emit(self.directory / f"process-{row['identity']['pid']}-{row['watcher'].evidence['originalFileTimeTicks']}-blocked-{len(reports):03}.json", packet)
            wait_retained_terminal(row["watcher"], timeout_seconds=timeout_seconds, report=report,
                mutex=self.mutex, publish=lambda proof: row.update(proof=proof))
            try:
                emit(self.directory / f"process-{row['identity']['pid']}-{row['watcher'].evidence['originalFileTimeTicks']}-exit.json",
                    {"identity": row["identity"], "role": row["role"], "proof": row["proof"], "blockedReports": reports})
            except BaseException as error:
                # Terminal handles are already safe; an evidence-write failure
                # must fail the verdict without stranding remaining live handles.
                with self.mutex:
                    message = "Terminal receipt failed: " + type(error).__name__ + ": " + str(error)
                    self.finalization_errors.append(message)
                    row["proof"].update(completed=False, terminalReceiptError=message)
        return copy.deepcopy(row["proof"])

    def close_all(self, *, timeout_seconds=75):
        deadline = time.monotonic() + timeout_seconds; reasons = self.cleanup_reasons; report_number = 0
        while True:
            try:
                with self.mutex:
                    self.collect()
                    if not self.unknown and self.worker_captured() and self.native_inventory_complete() and self.all_terminal():
                        self.stop.set()
                        break
                reason = None
                if self.unknown: reason = "Uncaptured or unqualified startup handle; explicit external reconciliation required"
                elif time.monotonic() >= deadline: reason = "Owned process tree terminal deadline exceeded"
                if reason and reason not in reasons:
                    reasons.append(reason); report_number += 1
                    packet = {"reason": reason, "errors": self.errors, "automaticTermination": False,
                        "originalHandlesRetained": True, "startupReplyPending": self.pending_start is not None,
                        "expectedWorkerCount": self.expected_worker_count, "workerStartupCaptured": self.worker_captured(),
                        "nativeStartupInventoryComplete": self.native_inventory_complete(),
                        "retained": [{"identity": r["identity"], "role": r["role"]} for r in self.snapshot()], "atUtc": utc()}
                    emit(self.directory / f"blocked-{report_number:03}.json", packet)
                    print("BLOCKED_NATIVE_TREE_CLEANUP " + json.dumps(packet), flush=True)
                time.sleep(.2)
            except BaseException as error:
                message = "Cleanup interrupted: " + type(error).__name__
                if message not in reasons:
                    reasons.append(message)
                    try: print(message + "; exact handles retained, no dependent dispatch", flush=True)
                    except BaseException: pass
        self.stop.set()
        while self.thread:
            try:
                self.thread.join(2)
                if not self.thread.is_alive(): break
                message = "Startup monitor failed to stop; original handles retained"
            except BaseException as error:
                message = "Monitor join interrupted: " + type(error).__name__
            if message not in reasons: reasons.append(message)
        exits = []
        for row in self.snapshot():
            while True:
                try:
                    proof = self.close_record(row)
                    break
                except BaseException as error:
                    message = "Handle finalization interrupted: " + type(error).__name__
                    if message not in reasons: reasons.append(message)
            exits.append({"identity": row["identity"], "role": row["role"], "ancestry": row["ancestry"], "proof": proof})
        return {"completed": all(e["proof"].get("completed") and e["proof"].get("exitCode") == 0 and not e["proof"].get("deadlineOrCancellationObserved") for e in exits) and not reasons and not self.errors and not self.finalization_errors and not self.inventory_wait_reasons and self.pending_start is None and self.worker_captured() and self.native_inventory_complete(),
            "exits": exits, "errors": self.errors, "unknownReasons": reasons,
            "startupOutcomeUnknown": self.pending_start is not None, "forcedTermination": False,
            "startupInventoryUnknownReasons": list(self.inventory_wait_reasons),
            "finalizationErrors": list(self.finalization_errors),
            "expectedWorkerCount": self.expected_worker_count, "workerStartupCaptured": self.worker_captured(),
            "nativeStartupInventoryComplete": self.native_inventory_complete()}


def close_host_without_kill(client, tree):
    """EOF once, retained tree terminal proof, then EOF/worker readback. No kill."""
    interrupted = None
    tree.await_capture_complete()
    try: client.process.stdin.close()
    except (BrokenPipeError, OSError): pass
    except BaseException as error: interrupted = type(error).__name__
    proof = tree.close_all()
    client.process.poll()
    for reader in (client.output_reader, client.error_reader): reader.join(timeout=1)
    receipts = []
    for line in list(client.errors):
        try: item = json.loads(line)
        except (ValueError, TypeError): continue
        if isinstance(item, dict) and isinstance(item.get("bridgeWorkerExit"), dict): receipts.append(item["bridgeWorkerExit"])
    for reader, stream in ((client.output_reader, client.process.stdout), (client.error_reader, client.process.stderr)):
        if not reader.is_alive(): stream.close()
    return {"hostExitCode": client.process.returncode, "forcedHostStop": False, "workerExits": receipts,
        "stderrEofObserved": not client.error_reader.is_alive(), "closeInterrupted": interrupted, "tree": proof}
