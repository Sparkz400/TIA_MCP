"""Exact lifetime adapter for component and encoder qualification.

Every Host reopen captures Host/worker/native handles before native startup reply.
Unknown startup/cleanup retains those handles; it never authorizes continuation.
"""
import copy
from datetime import datetime
from pathlib import Path
import traceback
import time

from block_test_runtime import utc_now
from qualification_owned_scratch import OwnedScratch, QualificationAudit, require
from retained_native_lifecycle import RetainedNativeTree, close_host_without_kill


def hold_unknown_startup(reason):
    """No trustworthy CreateProcess handle exists; never infer absence from a failed constructor."""
    while True:
        try:
            print("BLOCKED_UNCAPTURED_HOST_STARTUP " + reason + "; no automatic cleanup or dependent dispatch", flush=True)
            time.sleep(30)
        except BaseException:
            continue


def worker_receipt_bound(shutdown, tree):
    workers = [row for row in tree.snapshot() if row["role"] == "worker"]
    receipts = shutdown.get("workerExits", [])
    if len(workers) != 1 or len(receipts) != 1:
        return False
    row, receipt = workers[0], receipts[0]
    try:
        born = datetime.fromisoformat(receipt["startedAtUtc"].replace("Z", "+00:00")).timestamp()
        return (receipt["processId"] == row["identity"]["pid"] and abs(born - row["identity"]["created"]) <= .00001
                and receipt.get("processExitVerified") is True and receipt.get("exitCode") == 0
                and receipt.get("forcedTermination") is False and shutdown.get("stderrEofObserved") is True)
    except (KeyError, TypeError, ValueError):
        return False


class ComponentOwnedScratch(OwnedScratch):
    def __init__(self, *args, lifecycle_directory, tree_factory=RetainedNativeTree, closer=close_host_without_kill, **kwargs):
        super().__init__(*args, **kwargs)
        self.lifecycle_directory = Path(lifecycle_directory)
        self.lifecycle_directory.mkdir(parents=True, exist_ok=False)
        self.tree_factory, self.closer = tree_factory, closer
        self.tree = self.native_row = None

    def start(self):
        require(self.client is None and not self.poisoned, "Previous exact owned lifecycle is incomplete")
        require(self.manifest_factory(self.host, self.worker) == self.binaries, "Selected binaries changed")
        self.session = {"startedAtUtc": utc_now(), "calls": [], "startAttempted": False}
        self.report.setdefault("sessions", []).append(self.session); self.persist()
        arguments = ["--worker", str(self.worker), "--artifacts", str(self.artifacts)]
        constructor_error = None
        retained_type = getattr(self.client_factory, "retained_type", None)
        if retained_type is not None:
            self.client = retained_type.__new__(retained_type)
            try: self.client_factory(self.host, self.timeout, arguments, allocated_client=self.client)
            except BaseException as error: constructor_error = error
        else:  # Injected offline fake client factories only.
            self.client = self.client_factory(self.host, self.timeout, arguments)
        if getattr(self.client, "process", None) is None:
            hold_unknown_startup("Client constructor did not retain an exact Popen process")
        self.tree = self.tree_factory.__new__(self.tree_factory)
        try:
            self.tree.__init__(self.client.process, self.host, self.worker, self.lifecycle_directory / f"host-{len(self.report['sessions']):02}")
        except BaseException:
            self.tree.unknown = True; self.poisoned = True; raise
        if constructor_error is not None:
            self.tree.unknown = True; self.poisoned = True
            self.tree.await_capture_complete()
            raise constructor_error
        self.session["hostProcessId"] = self.client.process.pid
        self.session["hostHandle"] = copy.deepcopy(self.tree.anchor["watcher"].evidence)
        self.audit = QualificationAudit(self.client, self.session["calls"], self.persist)
        self.audit.ALLOWED_TOOLS = self.audit.ALLOWED_TOOLS | self.extra_tools | {"tia_discover"}
        self.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "component-exact-lifetime", "version": "1"}})
        self.client.notify("notifications/initialized")
        self.session["beforeDiscovery"] = self.audit("tia_discover")
        worker = self.tree.one("worker")
        self.session["workerHandle"] = copy.deepcopy(worker["watcher"].evidence)
        self.tree.begin_start(); self.session["startAttempted"] = True; self.persist()
        started = self.audit("tia_start")
        require(started.get("attached") is True and started.get("ownsPortalProcess") is True and type(started.get("processId")) is int,
                "Startup did not identify an exact owned native process")
        self.native_row = self.tree.accept_start(started["processId"])
        require(self.native_row["identity"]["parentPid"] == worker["identity"]["pid"], "Native startup ancestry differs")
        self.watcher = self.native_row["watcher"]
        self.session.update(processId=started["processId"], handleCapture=copy.deepcopy(self.watcher.evidence))
        require(started.get("projects") == [], "Fresh native process already contained a project")
        self.persist(); return started

    def close(self):
        if self.client is None:
            return
        cleanup = {"completed": False, "errors": [], "automaticTermination": False}; self.session["cleanup"] = cleanup
        errors = cleanup["errors"]
        uncertain = any(c.get("status") != "passed" for c in self.session["calls"])
        if self.native_row is not None and not uncertain:
            try:
                self.tree.await_capture_complete()
                detached = self.audit("tia_detach"); cleanup["detach"] = detached
                require(detached.get("disposedOwnedPortalConnection") is True and detached.get("attached") is False, "Owned detach did not return exact disposal")
                cleanup["nativeExit"] = self.tree.close_record(self.native_row)
                proof = cleanup["nativeExit"]
                require(proof.get("completed") and proof.get("exitCode") == 0 and not proof.get("deadlineOrCancellationObserved"), "Native exit was unclean or unknown")
            except BaseException as error:
                errors.append({"action": "native_close", "message": str(error), "type": type(error).__name__})
        # On an uncertain request, EOF is the sole close action. Do not issue a
        # second native command or close a still-live handle. The tree waits hold.
        try:
            require(self.tree is not None, "Exact original process tracker is unavailable")
            shutdown = self.closer(self.client, self.tree); cleanup["launcherShutdown"] = shutdown
            cleanup["workerShutdownReceiptBoundToBirth"] = worker_receipt_bound(shutdown, self.tree)
            require(shutdown.get("tree", {}).get("completed") is True and shutdown.get("hostExitCode") == 0
                    and shutdown.get("forcedHostStop") is False and shutdown.get("closeInterrupted") is None
                    and cleanup["workerShutdownReceiptBoundToBirth"], "Exact Host/worker tree closure failed")
        except BaseException as error:
            errors.append({"action": "host_tree_close", "message": str(error), "type": type(error).__name__, "traceback": traceback.format_exc()})
            # A closer must retain on unresolved handles. If an implementation
            # error unwinds, return to the same retained tree before unwinding locks.
            if self.tree is not None:
                cleanup["terminalFallback"] = self.tree.close_all()
        cleanup["clientDiagnostics"] = self.client_diagnostics()
        if self.manifest_factory(self.host, self.worker) != self.binaries:
            errors.append({"action": "binary_hashes", "message": "Selected binaries changed"})
        cleanup["completed"] = not errors
        self.session["completedAtUtc"] = utc_now(); self.poisoned = bool(errors); self.persist()
        self.client = self.audit = self.watcher = self.current_project = self.native_row = None
        self.tree = None
        require(not errors, "Exact owned component cleanup failed: " + ", ".join(row["action"] for row in errors))
