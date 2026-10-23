"""Sequential actual-MCP coordinator; --execute is required to start anything.

Read-only Main OB1 category capture after the independently completed recovery
matrix. Store windows are also reusable by isolated qualification coordinators.
No code in this module dispatches native work at import time.
"""
import argparse
import copy
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import threading
import time

import psutil
import native_dispatch_guard
from smoke_mcp import Client
import verify_watch_expansion as watch_oracle
from verify_watch_workflow import canonical_sha, load
from verify_translation_populated_category_probe import decode, populated_control

REPO = Path(__file__).resolve().parents[1]
LANE = REPO / "artifacts/startup-unblock-20261015/translations"
BUILD = REPO / "artifacts/deployment-build/b274e08b806a44f4928e429f190b153d"
RECOVERY_MODES = {"checkpoint.beforeArchive", "checkpoint.afterArchive", "host.watch_checkpoint", "retrieve.before", "retrieve.after", "host.watch_restore_native"}
NATIVE_MODE = "translations-populated-category-read"
OPERATION_TRANSITIONS = {
    "prepared": {"acquired", "stopped"}, "acquired": {"checkpointIntent", "stopped", "outcomeUnknown"},
    "checkpointIntent": {"ready", "outcomeUnknown", "reconciled"}, "ready": {"dispatchIntent", "reconciled", "recoveryIntent", "outcomeUnknown"},
    "dispatchIntent": {"dispatchIntent", "succeeded", "recoveryRequired", "outcomeUnknown", "reconciled"},
    "outcomeUnknown": {"reconciled", "recoveryIntent", "outcomeUnknown"}, "recoveryRequired": {"reconciled", "recoveryIntent"},
    "succeeded": {"recoveryIntent"}, "recoveryIntent": {"reconciled", "outcomeUnknown", "recoveryIntent"}}


def require(condition, message):
    if not condition: raise ValueError(message)


def utc(): return datetime.now(timezone.utc).isoformat()
def sha(path): return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def ref(path):
    path = Path(path).resolve()
    return {"path": str(path), "sha256": sha(path), "bytes": path.stat().st_size}


def save(path, value):
    with Path(path).open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2, ensure_ascii=False, allow_nan=False)
        stream.write("\n"); stream.flush(); os.fsync(stream.fileno())
    return value


def safe_path(path):
    candidate = Path(path).absolute()
    require(candidate.is_absolute() and not str(candidate).startswith("\\\\"), "Exact local absolute path required")
    for parent in [candidate, *candidate.parents]:
        require(not parent.is_symlink() and not (hasattr(parent, "is_junction") and parent.is_junction()), "Filesystem link/junction aliases are refused: " + str(parent))
    resolved = candidate.resolve()
    if resolved.is_file(): require(resolved.stat().st_nlink == 1, "Multiply linked resource refused")
    return resolved


def resource_key(kind, path):
    require(kind in {"project", "workspace", "library"}, "Unknown resource kind")
    return kind + ":" + str(safe_path(path)).rstrip("\\/").upper()


def overlaps(a, b):
    left, right = a.split(":", 1)[1].rstrip("\\/"), b.split(":", 1)[1].rstrip("\\/")
    return left == right or left.startswith(right + os.sep) or right.startswith(left + os.sep)


def verify_recovery_gate(path, host, worker):
    """Re-run the independent file-only oracle; claimed booleans alone never admit."""
    gate_path = Path(path).resolve(); gate = load(gate_path)
    cases = gate.get("cases")
    require(isinstance(cases, list) and len(cases) == len(RECOVERY_MODES), "Recovery gate requires exactly six checkpoint/retrieval interruption cases")
    seen, checked = set(), []
    host, worker = Path(host).resolve(), Path(worker).resolve()
    for case in cases:
        root = safe_path(case["root"])
        packet_path = Path(case["verificationPath"]).resolve()
        require(packet_path.is_file() and sha(packet_path) == case["verificationSha256"], "Independent recovery receipt hash differs")
        recorded = load(packet_path); admission = load(root / "admission.json")
        mode = admission["mode"]
        require(mode in RECOVERY_MODES and mode not in seen, "Missing/duplicate/foreign recovery case")
        seen.add(mode)
        require(admission["actualMcpHost"] is True and admission["nativeSequential"] is True and admission["automaticReplay"] is False,
                "Recovery case was not actual sequential MCP")
        require(Path(admission["host"]).resolve() == host and admission["hostSha256"] == sha(host) and
                Path(admission["worker"]).resolve() == worker and admission["workerSha256"] == sha(worker), "Recovery gate used different Host/worker bytes")
        require(recorded.get("kind") == "independent-watch-expansion-verification" and Path(recorded["root"]).resolve() == root and
                recorded.get("nativeInvoked") is False and recorded.get("completed") is True and recorded.get("interruptionMatrixCasePassed") is True and
                recorded.get("hostRestartVerified") is True and not recorded.get("issues"), "Recorded recovery verification is incomplete")
        require(recorded.get("stages") and all(stage.get("passed") is True for stage in recorded["stages"]) and not recorded.get("coverageGaps"), "Recorded recovery stages or coverage are incomplete")
        rerun = watch_oracle.verify(root)
        require(rerun.get("completed") is True and rerun.get("interruptionMatrixCasePassed") is True and rerun.get("hostRestartVerified") is True and not rerun.get("issues"),
                "Retained raw recovery evidence no longer verifies: " + mode + ": " + json.dumps(rerun.get("issues")))
        require(all(stage.get("passed") is True for stage in rerun["stages"]), "Recovery verifier has an unsuccessful stage")
        lifecycle = (rerun.get("interruption") or {}).get("actualHostLifecycle")
        require(isinstance(lifecycle, dict) and lifecycle.get("hostCount") == 2 and lifecycle.get("closedBeforeRestart") is True and
                lifecycle.get("exactWorkerLifetimes") == 2 and not rerun.get("coverageGaps"), "Exact actual Host restart/cleanup evidence is absent")
        if mode in {"retrieve.after", "host.watch_restore_native"}:
            require((rerun.get("recovery") or {}).get("restoredProjectVerified") is True, "Completed retrieval lacks fresh restored-project verification")
        checked.append({"mode": mode, "root": str(root), "verification": ref(packet_path), "admission": ref(root / "admission.json"),
                        "rawEvidenceReverification": rerun, "hostRestartVerified": True})
    require(seen == RECOVERY_MODES, "Recovery matrix coverage is incomplete")
    return {"kind": "completed-independent-checkpoint-retrieval-gate", "completed": True, "checkedAtUtc": utc(), "gate": ref(gate_path),
            "host": ref(host), "worker": ref(worker), "verifierSources": [ref(Path(watch_oracle.__file__)), ref(REPO / "scripts/verify_watch_workflow.py")],
            "cases": checked, "nativeInvoked": False, "automaticReplay": False}


def scan_store(root, keys):
    """Read only while caller holds this root's exact store.lock handle."""
    observations = []
    for folder_name, width in (("engineering-operations", 6), ("engineering-admissions", 8)):
        folder = root / folder_name
        if not folder.exists(): continue
        folders = sorted(p for p in folder.iterdir() if p.is_dir())
        require(len(folders) <= 10000, "Store folder bound exceeded")
        for directory in folders:
            safe_path(directory)
            if re.fullmatch(r"\.preparing-[0-9a-fA-F]{32}", directory.name): continue
            require(re.fullmatch(r"[0-9a-fA-F]{32}", directory.name), "Unknown store directory")
            files = sorted(directory.glob("*.json"))
            require(1 <= len(files) <= 4096, "Missing/excessive journal revisions")
            previous, resources, previous_plan = None, None, None
            for index, file in enumerate(files):
                require(file.name == f"{index:0{width}d}.json" and file.stat().st_size <= 8 * 1024 * 1024, "Noncontiguous/oversized journal")
                record = load(file)
                require(record["id"] == directory.name and record["revision"] == index and
                        record.get("previousSha256") == (None if previous is None else canonical_sha(previous)), "Journal hash chain differs")
                if folder_name == "engineering-operations":
                    require(record["state"] == "prepared" if previous is None else record["state"] in OPERATION_TRANSITIONS.get(previous["state"], set()), "Invalid operation state transition")
                    require(canonical_sha(record["plan"]) == record["planSha256"], "Operation plan digest differs")
                    if previous is not None and previous["state"] == record["state"] == "dispatchIntent":
                        before, after = previous.get("evidence", []), record.get("evidence", [])
                        require(len(after) == len(before) + 1 and after[:-1] == before and
                                after[-1].get("gate", "").startswith("dispatch.") and
                                record.get("checkpoints") == previous.get("checkpoints") and
                                record.get("recoveryDestinations") == previous.get("recoveryDestinations"),
                                "Pending dispatch may only append one retained result; replay is prohibited")
                    current_resources = record["plan"]["resources"]
                    current_plan = record["planSha256"]
                else:
                    require(record["state"] == "dispatchIntent" if previous is None else
                            record["state"] in ({"returned", "outcomeUnknown", "reconciled"} if previous["state"] == "dispatchIntent" else {"reconciled"} if previous["state"] == "outcomeUnknown" else set()), "Invalid mutation admission state transition")
                    current_resources = [{"resourceKey": resource_key(r["kind"], r["path"]), "kind": r["kind"]} for r in record["scope"]["resources"]]
                    require(sorted(r["resourceKey"] for r in current_resources) == record["resourceKeys"], "Mutation resource keys differ")
                    current_plan = canonical_sha(record["scope"])
                require(previous_plan in (None, current_plan), "Journal immutable scope changed")
                require(isinstance(current_resources, list) and 1 <= len(current_resources) <= 64, "Invalid journal resource scope")
                for item in current_resources:
                    key = item["resourceKey"]; kind = item["kind"]
                    require(key.startswith(kind + ":") and resource_key(kind, key[len(kind) + 1:]) == key, "Noncanonical journal resource key")
                previous, previous_plan, resources = record, current_plan, current_resources
            state = previous["state"]
            if folder_name == "engineering-operations":
                require(state in {"prepared", "acquired", "checkpointIntent", "ready", "dispatchIntent", "succeeded", "recoveryRequired", "outcomeUnknown", "recoveryIntent", "reconciled", "stopped"}, "Unknown operation state")
                pending = state not in {"prepared", "succeeded", "reconciled", "stopped"}
                owned = [r["resourceKey"] for r in resources]
                for destination in previous.get("recoveryDestinations", []):
                    owner = next(r for r in resources if r["resourceKey"] == destination["resourceKey"])
                    owned.append(resource_key(owner["kind"], destination["recoveryLocation"]))
            else:
                require(state in {"dispatchIntent", "outcomeUnknown", "returned", "reconciled"}, "Unknown mutation admission state")
                pending = state in {"dispatchIntent", "outcomeUnknown"}; owned = previous["resourceKeys"]
            require(not pending or not any(overlaps(a, b) for a in owned for b in keys), "Unresolved overlapping project/workspace reservation: " + str(directory))
            observations.append({"store": folder_name, "id": directory.name, "state": state, "pending": pending, "latest": ref(files[-1]), "resourceKeys": owned, "hashChainVerified": True})
    return observations


class WindowsFiles:
    def __init__(self):
        require(os.name == "nt", "Native coordination is Windows-only")
        import ctypes
        from ctypes import wintypes
        self.c = ctypes; self.k = ctypes.WinDLL("kernel32", use_last_error=True)
        self.k.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        self.k.CreateFileW.restype = wintypes.HANDLE
        self.k.CloseHandle.argtypes = [wintypes.HANDLE]; self.k.CloseHandle.restype = wintypes.BOOL

    def lock(self, path):
        handle = self.k.CreateFileW(str(path), 0xC0000000, 0, None, 4, 0x80, None)
        if handle == self.c.c_void_p(-1).value: raise OSError(self.c.get_last_error(), "Shared store lock unavailable: " + str(path))
        return handle

    def close(self, handle): return bool(self.k.CloseHandle(handle))


class StoreExclusiveWindow:
    """Held store locks plus honest coordinator envelope; no native dispatch/replay.

    Mutation callers remain responsible for their own durable checkpoints,
    mutation intents and outcome/reconciliation. This is not production adapter
    qualification, nor permission to relabel a mutation as read-only.
    """
    def __init__(self, roots, project_path, receipt_prefix, *, mode, resources, effects, _api=None, _guard_root=None, _guard_owner=None, isolation=None):
        require(mode in {"readOnlyProbe", "isolatedQualificationMutation"} and isinstance(effects, str) and effects.strip(), "Explicit coordinator mode/effects required")
        self.roots = sorted((safe_path(r) for r in roots), key=lambda p: str(p).upper())
        require(self.roots and len(self.roots) <= 16 and len(set(str(p).upper() for p in self.roots)) == len(self.roots), "Unique bounded canonical store roots required")
        require(isinstance(resources, list) and all(isinstance(r.get("effect"), str) and r["effect"].strip() for r in resources), "Every resource needs its explicit effect")
        self.keys = [resource_key(r["kind"], r["path"]) for r in resources]
        require(1 <= len(self.keys) <= 64 and len(set(self.keys)) == len(self.keys) and resource_key("project", project_path) in self.keys, "Exact project and bounded unique resources required")
        self.prefix, self.api, self.handles = Path(receipt_prefix), _api, []
        self.guard_root = safe_path(native_dispatch_guard.DEFAULT_ROOT if _guard_root is None else _guard_root)
        self.guard_owner, self.entered = _guard_owner, False
        self.isolation = isolation
        if isolation is not None:
            from native_dispatch_isolation import RebootIsolation
            require(isinstance(isolation, RebootIsolation) and mode == "isolatedQualificationMutation"
                    and _guard_owner is None
                    and any(r.get("kind") == "workspace" and safe_path(r["path"]) == isolation.workspace for r in resources),
                    "Boot isolation requires an explicit fresh fixture workspace and original guard owner")
            isolation.validate_workspace()
        self.guard_dependents, self.guard_borrowed = 0, False
        self.evidence = {"kind": "held-coordinator-store-window", "mode": mode, "readOnly": mode == "readOnlyProbe", "effects": effects,
                         "projectPath": str(safe_path(project_path)), "resourceKeys": self.keys, "resources": copy.deepcopy(resources),
                         "roots": [str(p) for p in self.roots], "automaticReplay": False, "nativeDispatchedByWindow": False, "nativeQualified": False}

    def __enter__(self):
        self.api = self.api or WindowsFiles()
        try:
            if self.guard_owner is None:
                self.guard_root.mkdir(parents=True, exist_ok=True)
                safe_path(self.guard_root)
                guard_lock = self.guard_root / "dispatch.lock"; safe_path(guard_lock)
                self.handles.append((guard_lock, self.api.lock(guard_lock)))
                self.evidence["nativeDispatchGuard"] = {
                    "root": str(self.guard_root),
                    "observations": native_dispatch_guard.scan(self.guard_root, isolation=self.isolation),
                    "sameRegistryLockHeldThroughDispatch": True}
            else:
                require(isinstance(self.guard_owner, StoreExclusiveWindow) and self.guard_owner.entered
                        and self.guard_owner.guard_owner is None and self.guard_owner.handles
                        and self.guard_owner.guard_root == self.guard_root,
                        "Batched store windows require their still-held original native guard owner")
                self.evidence["nativeDispatchGuard"] = copy.deepcopy(self.guard_owner.evidence["nativeDispatchGuard"])
                self.evidence["nativeDispatchGuard"]["sharedOriginalOwnerIntent"] = ref(str(self.guard_owner.prefix) + "-intent.json")
                self.guard_owner.guard_dependents += 1
                self.guard_borrowed = True
            for root in self.roots:
                require(root.is_dir(), "Declared resource store root is absent")
                directory = root / "engineering-operations"; directory.mkdir(exist_ok=True)
                safe_path(directory)
                path = directory / "store.lock"; safe_path(path)
                self.handles.append((path, self.api.lock(path)))
            self.evidence["stores"] = [{"root": str(root), "observations": scan_store(root, self.keys)} for root in self.roots]
            self.evidence.update(acquiredAtUtc=utc(), coordinatorPid=os.getpid(), sameHandlesHeldThroughDispatch=True,
                                 lockFiles=[str(p) for p, _ in self.handles], journalsModified=False)
            save(str(self.prefix) + "-intent.json", self.evidence)
            self.entered = True
            return self
        except BaseException as error:
            self._release(error); raise

    def _release(self, error=None):
        require(self.guard_dependents == 0, "Native guard owner cannot release while a batched window is held")
        self.entered = False
        closed = []
        for path, handle in reversed(self.handles): closed.append({"path": str(path), "closed": self.api.close(handle)})
        self.handles = []
        result = {"kind": "held-coordinator-store-window-close", "releasedAtUtc": utc(), "locks": closed,
                  "allClosed": all(item["closed"] for item in closed), "journalsModified": False, "automaticReplay": False,
                  "bodyFailure": None if error is None else type(error).__name__ + ": " + str(error)}
        save(str(self.prefix) + "-close.json", result)
        require(result["allClosed"], "A retained store-lock handle did not close")
        if self.guard_borrowed:
            self.guard_owner.guard_dependents -= 1
            self.guard_borrowed = False

    def __exit__(self, kind, error, trace): self._release(error)


class StoreReadWindow(StoreExclusiveWindow):
    def __init__(self, roots, project_path, receipt_prefix, *, _api=None, _guard_root=None):
        super().__init__(roots, project_path, receipt_prefix, mode="readOnlyProbe", resources=[{"kind": "project", "path": str(project_path), "effect": "readOnlyNativeProbe"}],
                         effects="Read-only admitted collector under its native ExclusiveAccess; no project save/import/language activation", _api=_api, _guard_root=_guard_root)


def process_identity(process):
    return {"pid": process.pid, "parentPid": process.ppid(), "created": process.create_time(), "name": process.name(), "exe": process.exe(), "command": process.cmdline()}


class RetainedProcess:
    """Birth-checked OS handle retained for all waits and any exact fallback stop."""
    def __init__(self, expected):
        require(os.name == "nt", "Retained native process handles require Windows")
        import ctypes
        from ctypes import wintypes
        self.c, self.w = ctypes, wintypes
        self.k = ctypes.WinDLL("kernel32", use_last_error=True)
        self.k.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]; self.k.OpenProcess.restype = wintypes.HANDLE
        self.k.CloseHandle.argtypes = [wintypes.HANDLE]; self.k.CloseHandle.restype = wintypes.BOOL
        self.k.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]; self.k.WaitForSingleObject.restype = wintypes.DWORD
        self.k.GetProcessTimes.argtypes = [wintypes.HANDLE, *([ctypes.POINTER(wintypes.FILETIME)] * 4)]; self.k.GetProcessTimes.restype = wintypes.BOOL
        self.k.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]; self.k.GetExitCodeProcess.restype = wintypes.BOOL
        self.k.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]; self.k.TerminateProcess.restype = wintypes.BOOL
        self.k.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        self.k.QueryFullProcessImageNameW.restype = wintypes.BOOL
        self.handle = self.k.OpenProcess(0x00100000 | 0x1000 | 0x0001, False, expected["pid"])
        require(self.handle, "Could not retain exact process handle")
        self.closed = False
        try:
            times = [wintypes.FILETIME() for _ in range(4)]
            require(self.k.GetProcessTimes(self.handle, *(ctypes.byref(t) for t in times)), "Process birth lookup failed")
            created = ((times[0].dwHighDateTime << 32) | times[0].dwLowDateTime) / 10000000 - 11644473600
            size = wintypes.DWORD(32768); buffer = ctypes.create_unicode_buffer(size.value)
            require(self.k.QueryFullProcessImageNameW(self.handle, 0, buffer, ctypes.byref(size)), "Process executable lookup failed")
            require(abs(created - expected["created"]) <= .01 and Path(buffer.value).resolve() == Path(expected["exe"]).resolve(), "PID birth/executable changed before handle capture")
            self.evidence = {"identity": copy.deepcopy(expected), "handleCapturedAtUtc": utc(), "handleBirthEpoch": created,
                             "handleImage": buffer.value, "birthAndImageVerified": True, "sameHandleRetained": True,
                             "processExitVerified": False, "forcedTermination": False, "handleClosed": False}
        except BaseException:
            self.k.CloseHandle(self.handle); self.closed = True; raise

    def wait(self, seconds):
        require(not self.closed and 0 <= seconds <= 300, "Invalid retained-handle wait")
        deadline = time.monotonic() + seconds
        while True:
            remaining = max(0, deadline - time.monotonic())
            result = self.k.WaitForSingleObject(self.handle, min(1000, int(remaining * 1000)))
            if result == 0:
                code = self.w.DWORD(); require(self.k.GetExitCodeProcess(self.handle, self.c.byref(code)), "Terminal exit-code query failed")
                self.evidence.update(processExitVerified=True, exitCode=code.value, exitedObservedAtUtc=utc()); return True
            require(result == 258, "Unexpected exact-process wait result")
            if remaining <= 0: return False

    def terminate(self, reason):
        require(not self.closed, "Retained handle already closed")
        if self.wait(0): return
        self.evidence.update(forcedTermination=True, terminationReason=reason, terminationRequestedAtUtc=utc(), terminationMethod="TerminateProcess on original retained birth-checked handle")
        require(self.k.TerminateProcess(self.handle, 97), "Exact retained-handle termination failed")

    def finish(self, seconds=30, force=False, reason="bounded owned-process cleanup"):
        try:
            if not self.wait(seconds) and force:
                self.terminate(reason); self.wait(30)
        finally:
            if not self.closed:
                self.evidence["handleClosed"] = bool(self.k.CloseHandle(self.handle)); self.closed = True
        self.evidence["completed"] = self.evidence["processExitVerified"] and self.evidence["handleClosed"]
        return copy.deepcopy(self.evidence)


class DescendantTracker:
    def __init__(self, host, worker_path, root):
        self.host, self.worker_path, self.root = host, Path(worker_path).resolve(), root
        self.tracked, self.failures, self.lock = {}, [], threading.Lock()
        self.stop = threading.Event(); self.thread = threading.Thread(target=self.monitor, daemon=True); self.thread.start()

    def collect(self):
        try:
            parent = psutil.Process(self.host["pid"])
            require(abs(parent.create_time() - self.host["created"]) <= .01, "Host PID was reused")
            descendants = parent.children(recursive=True)
            workers = [p for p in descendants if Path(p.exe()).resolve() == self.worker_path]
            candidates = workers + [p for p in descendants if p.name() == "Siemens.Automation.Portal.exe" and p.ppid() in {w.pid for w in workers}]
            for process in candidates:
                observed = process_identity(process); key = observed["pid"], observed["created"]
                with self.lock:
                    if key in self.tracked: continue
                    watcher = RetainedProcess(observed); self.tracked[key] = watcher
                    save(self.root / f"process-observed-{observed['pid']}-{int(observed['created'] * 1000000)}.json", watcher.evidence)
        except (psutil.NoSuchProcess, psutil.AccessDenied): pass

    def monitor(self):
        while not self.stop.wait(.1):
            try: self.collect()
            except Exception as error:
                with self.lock: self.failures.append(type(error).__name__ + ": " + str(error))
                return

    def native(self, pid):
        self.collect()
        with self.lock:
            matches = [w for w in self.tracked.values() if w.evidence["identity"]["pid"] == pid and w.evidence["identity"]["name"] == "Siemens.Automation.Portal.exe"]
            require(len(matches) == 1 and not self.failures, "Fresh owned native child lacks exact retained lineage/handle")
            return matches[0]

    def stop_monitor(self):
        self.stop.set(); self.thread.join(3); require(not self.thread.is_alive(), "Process monitor did not stop")


def package_files(executable):
    return [ref(p) for p in sorted(Path(executable).resolve().parent.iterdir()) if p.is_file() and p.suffix.lower() in {".exe", ".dll", ".json", ".config"}]


def verify_package(files):
    require(files and all(Path(f["path"]).is_file() and sha(f["path"]) == f["sha256"] and Path(f["path"]).stat().st_size == f["bytes"] for f in files), "Frozen executable package changed")


def source_control(capture, project):
    """The old PLC export proves source sentinels, not populated block selection."""
    from verify_translation_category_probe import validate_request_matrix
    native, admission, progress = (load(capture / name) for name in ("result.json", "admission.json", "category-probe-progress.json"))
    validate_request_matrix(native, admission, progress)
    require(safe_path(native["projectPath"]) == project, "Source project does not match the genuine retained category capture")
    require(sha(capture / "ExpansionEvidence.exe") == admission["binarySha256"], "Retained source collector hash differs")
    request = next(r for r in progress["exports"] if r["mode"] == "plc-all")
    workbook = capture / "plc-all.xlsx"
    require(sha(workbook) == request["artifact"]["sha256"] and workbook.stat().st_size == request["artifact"]["bytes"], "Retained genuine PLC baseline changed")
    records, _, _ = decode(workbook)
    project_name = project.stem; plc_name = native["plcName"]
    prefix = project_name + "\\" + plc_name + "\\Programmbausteine\\Main\\"
    main = {k: v for k, v in records.items() if v["viewPath"].startswith(prefix)}
    require(populated_control(records, main, project_name, plc_name), "Retained source lacks both genuine Main interface-comment sentinels")
    return {"capture": str(capture), "receipt": ref(capture / "result.json"), "workbook": ref(workbook), "projectPath": str(project),
            "plcName": plc_name, "mainSourceRecords": list(main.values()), "exactSelectedBlockPopulationStillRequiresFreshNativeControl": True}


def preflight(args):
    require(os.name == "nt", "Native coordinator supports Windows only")
    host, worker = safe_path(args.host), safe_path(args.worker)
    root, project = safe_path(args.root), safe_path(args.project)
    require(not root.exists() and project.is_file() and project.suffix.lower() == ".ap21" and project.stem.startswith("TiaMcpScratch_"), "Fresh run root and exact existing owned scratch required")
    require(host == (BUILD / "published-host/TiaMcp.Host.exe").resolve() and worker == (BUILD / "src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe").resolve(), "This coordinator is bound to actual b274 Host/worker")
    template_path = safe_path(args.template); template = load(template_path)
    require(template["mode"] == NATIVE_MODE and template["readOnly"] is True and template["nativeImportAllowed"] is False and template["automaticReplay"] is False, "Invalid read-only collector template")
    helper = safe_path(template["binaryPath"])
    require(sha(helper) == template["binarySha256"], "Frozen collector binary hash changed")
    require(len(template["sourceFiles"]) == len(template["sourceSha256"]), "Frozen source map/count differs")
    for source in template["sourceFiles"]:
        require(sha(source) == template["sourceSha256"][Path(source).name], "Frozen collector source changed")
    api_root = Path(r"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48")
    require(all(sha(api_root / name) == value for name, value in template["apiSha256"].items()), "Installed API bytes changed")
    expected_roots = {safe_path(REPO / "artifacts"), safe_path(Path("D:/TIAW/wt02")), root}
    roots = [safe_path(p) for p in (args.resource_root or sorted(expected_roots, key=str))]
    require(len(roots) == 3 and set(roots) == expected_roots, "Exactly canonical repository, source wt02 ledger and new run roots must be admitted")
    gate = verify_recovery_gate(args.recovery_gate, host, worker)
    source = source_control(safe_path(args.source_capture), project)
    managed = safe_path(args.managed_tests); require(managed.is_file(), "Managed independent reader test binary required")
    return {"kind": "translation-category-coordinator-preflight", "completed": True, "nativeInvoked": False, "automaticReplay": False,
            "host": ref(host), "worker": ref(worker), "hostPackage": package_files(host), "workerPackage": package_files(worker), "helperPackage": package_files(helper),
            "template": ref(template_path), "helper": ref(helper), "projectBefore": ref(project), "sourceControl": source, "recoveryGate": gate,
            "resourceRoots": [str(p) for p in sorted(roots, key=lambda p: str(p).upper())], "root": str(root), "managedTests": ref(managed), "managedPackage": package_files(managed),
            "nativeImportAllowed": False, "nativeQualified": False}


class Coordinator:
    def __init__(self, args, prepared):
        self.args, self.prepared, self.root = args, prepared, Path(prepared["root"])
        self.root.mkdir(parents=True, exist_ok=False)
        save(self.root / "preflight.json", prepared)
        self.client = self.tracker = self.host_handle = self.native = self.helper_handle = None
        self.helper_process = None; self.helper_proof = None; self.sequence = 0
        self.detached = False; self.request_failed = False
        self.report = {"kind": "sequential-translation-category-coordinator", "completed": False, "nativeImportAllowed": False,
                       "nativeQualified": False, "automaticReplay": False, "actualMcpHost": True, "nativeSequential": True,
                       "startedAtUtc": utc(), "calls": [], "cleanup": {}}
        source_root = self.root / "coordinator-source"; source_root.mkdir()
        sources = []
        for name in ("qualify_translation_category_probe.py", "smoke_mcp.py", "verify_watch_expansion.py", "verify_watch_workflow.py",
                     "verify_translation_category_probe.py", "verify_translation_populated_category_probe.py"):
            destination = source_root / name; shutil.copyfile(REPO / "scripts" / name, destination); sources.append(ref(destination))
        save(self.root / "coordinator-source-manifest.json", sources)

    def call(self, name, arguments=None):
        self.sequence += 1
        prefix = self.root / f"call-{self.sequence:03}-{name}"
        save(str(prefix) + "-intent.json", {"tool": name, "arguments": arguments or {}, "startedAtUtc": utc(), "automaticReplay": False})
        print(name, flush=True)
        try:
            response = self.client.request("tools/call", {"name": name, "arguments": arguments or {}})
            save(str(prefix) + "-raw.json", response)
            require(not response.get("isError"), "MCP tool failed: " + name + ": " + json.dumps(response, ensure_ascii=True))
            decoded = response.get("structuredContent")
            if decoded is None: decoded = json.loads("\n".join(item["text"] for item in response.get("content", []) if item.get("type") == "text"))
            save(str(prefix) + "-decoded.json", decoded)
            self.report["calls"].append({"tool": name, "raw": ref(str(prefix) + "-raw.json"), "decoded": ref(str(prefix) + "-decoded.json")})
            self.tracker.collect()
            return decoded
        except Exception as error:
            self.request_failed = True
            save(str(prefix) + "-failure.json", {"type": type(error).__name__, "message": str(error), "automaticReplay": False})
            raise

    def status(self, opened):
        value = self.call("tia_session_status")
        require(value["ownsPortalProcess"] is True and value["processId"] == self.native.evidence["identity"]["pid"], "Owned native process changed")
        projects = value["projects"]
        require(projects == [] if not opened else len(projects) == 1 and safe_path(projects[0]["path"]) == Path(self.prepared["projectBefore"]["path"]), "Session project selection changed")
        require(not self.native.wait(0), "Owned native process exited before session status")
        return value

    def run_helper(self):
        for package in ("hostPackage", "workerPackage", "helperPackage"): verify_package(self.prepared[package])
        template = load(self.prepared["template"]["path"])
        require(sha(self.prepared["template"]["path"]) == self.prepared["template"]["sha256"], "Prepared admission template changed")
        with StoreReadWindow([Path(p) for p in self.prepared["resourceRoots"]], Path(self.prepared["projectBefore"]["path"]), self.root / "resource-window") as held:
            require(not self.native.wait(0), "Native lifetime ended before collector admission")
            admission = copy.deepcopy(template)
            admission.update(requestStatus="admitted-current-sequential-read-window", processId=self.native.evidence["identity"]["pid"],
                             processBirthEpoch=self.native.evidence["handleBirthEpoch"], projectPath=self.prepared["projectBefore"]["path"],
                             plcName=self.prepared["sourceControl"]["plcName"], outputDirectory=str(self.root / "native"), artifactRoot=str(self.root),
                             resourceAdmission=copy.deepcopy(held.evidence), recoveryGate=ref(self.root / "preflight.json"),
                             hostIdentity=self.host_handle.evidence["identity"], workerBinary=self.prepared["worker"], admittedAtUtc=utc())
            path = self.root / "native-admission.json"; save(path, admission)
            save(self.root / "helper-dispatch-intent.json", {"executable": self.prepared["helper"], "arguments": [NATIVE_MODE, str(path)],
                                                          "admission": ref(path), "startedAtUtc": utc(), "automaticReplay": False})
            with (self.root / "helper.stdout.raw").open("xb") as stdout, (self.root / "helper.stderr.raw").open("xb") as stderr:
                self.helper_process = subprocess.Popen([self.prepared["helper"]["path"], NATIVE_MODE, str(path)], cwd=REPO,
                                                        stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr, creationflags=subprocess.CREATE_NO_WINDOW)
                try:
                    observed = process_identity(psutil.Process(self.helper_process.pid))
                    require(Path(observed["exe"]).resolve() == Path(self.prepared["helper"]["path"]).resolve(), "Collector executable mismatch")
                    self.helper_handle = RetainedProcess(observed); save(self.root / "helper-process-start.json", self.helper_handle.evidence)
                    self.helper_process.wait(timeout=self.args.timeout)
                except subprocess.TimeoutExpired:
                    save(self.root / "helper-outcome-unknown.json", {"reason": "bounded collector deadline exceeded", "identity": observed, "automaticReplay": False,
                                                                   "retainedIntentRequiresReconciliation": True, "atUtc": utc()})
                    self.helper_handle.terminate("collector deadline; no replay")
                    self.helper_process.wait(timeout=30)
                    raise
                finally:
                    if self.helper_handle is not None:
                        self.helper_proof = self.helper_handle.finish(10, force=True, reason="exact collector cleanup")
                    else:
                        if self.helper_process.poll() is None: self.helper_process.kill()
                        self.helper_process.wait(timeout=30)
                        self.helper_proof = {"completed": False, "processExitVerified": True, "pid": self.helper_process.pid, "forcedTermination": True,
                                             "method": "exact Popen-owned handle fallback", "reason": "Additional birth-checked handle capture failed; no lifecycle qualification"}
                    save(self.root / "helper-process-exit.json", self.helper_proof)
            require(self.helper_proof["completed"] and not self.helper_proof["forcedTermination"] and self.helper_process.returncode == 0, "Collector did not close normally")
            packet = load(self.root / "native/result.json")
            require(packet["processId"] == admission["processId"] and safe_path(packet["projectPath"]) == Path(admission["projectPath"]) and
                    packet["beforeModified"] is False and packet["afterModified"] is False and packet["readOnly"] is True and packet["saved"] is False and
                    packet["completed"] is True and packet["onlineConnected"] is False, "Collector saved-state/identity readback differs")
            self.report["collectorOutcome"] = packet["translations"]["outcome"]
            self.report["savedNativeReadback"] = {"beforeModified": False, "afterModified": False, "saveInvoked": False, "evidence": ref(self.root / "native/result.json")}

    def execute(self):
        try:
            for package in ("hostPackage", "workerPackage", "helperPackage"): verify_package(self.prepared[package])
            require(sha(self.prepared["projectBefore"]["path"]) == self.prepared["projectBefore"]["sha256"], "Project file changed since preflight")
            self.client = Client(Path(self.prepared["host"]["path"]), self.args.timeout,
                                 ["--worker", self.prepared["worker"]["path"], "--artifacts", str(self.root)])
            observed = process_identity(psutil.Process(self.client.process.pid))
            self.host_handle = RetainedProcess(observed)
            self.tracker = DescendantTracker(observed, self.prepared["worker"]["path"], self.root)
            save(self.root / "host-process-start.json", self.host_handle.evidence)
            initialized = self.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "translation-category-native-coordinator", "version": "1"}})
            save(self.root / "initialize-raw.json", initialized); self.client.notify("notifications/initialized")
            before = self.call("tia_discover"); self.report["preexistingProcesses"] = before["processes"]
            started = self.call("tia_start")
            require(started["ownsPortalProcess"] is True and started["projects"] == [], "Fresh worker-owned empty tia_start required")
            self.native = self.tracker.native(started["processId"]); save(self.root / "owned-native-start.json", self.native.evidence)
            self.status(False)
            opened = self.call("project_open", {"path": self.prepared["projectBefore"]["path"]})
            require(opened["opened"] is True and opened["saved"] is False and safe_path(opened["project"]["path"]) == Path(self.prepared["projectBefore"]["path"]), "Exact saved source was not opened read-only")
            self.status(True)
            self.run_helper()
            self.status(True)
            self.call("tia_detach"); self.detached = True
            native_proof = self.native.finish(45, force=True, reason="owned native did not exit after detach")
            save(self.root / "owned-native-exit.json", native_proof)
            require(native_proof["completed"] and not native_proof["forcedTermination"], "Owned native required explicit termination")
            after = self.call("tia_discover")
            normalize = lambda values: sorted(values, key=lambda v: v["processId"])
            require(normalize(after["processes"]) == normalize(before["processes"]), "Preexisting TIA discovery changed")
            self.report["preexistingPreserved"] = True
        except Exception as error:
            self.report["failure"] = {"type": type(error).__name__, "message": str(error)}
            print("FAIL", str(error), flush=True)
        finally:
            try: self.cleanup()
            except Exception as error:
                self.report["failure"] = self.report.get("failure") or {"type": type(error).__name__, "message": "cleanup: " + str(error)}
                save(self.root / "cleanup-failure.json", {"message": str(error), "automaticReplay": False})
        if not self.report.get("failure") and self.report["cleanup"].get("normalLifecyclePassed"):
            try:
                after = ref(self.prepared["projectBefore"]["path"])
                require(after["sha256"] == self.prepared["projectBefore"]["sha256"], "Project file bytes changed after read-only native closure")
                self.report["projectAfter"] = after
                self.decode_after_close()
                self.report["completed"] = True
            except Exception as error: self.report["failure"] = {"type": type(error).__name__, "message": str(error)}
        self.report["finishedAtUtc"] = utc(); save(self.root / "result.json", self.report)
        return 0 if self.report["completed"] else 1

    def cleanup(self):
        cleanup = self.report["cleanup"]; failures = []; proofs = []
        if self.helper_handle is not None and not self.helper_handle.closed:
            try:
                self.helper_proof = self.helper_handle.finish(5, force=True, reason="exact collector cleanup after coordinator failure")
                save(self.root / "helper-cleanup-fallback.json", self.helper_proof)
            except Exception as error: failures.append("helper: " + str(error))
        if self.client is not None:
            if self.native is not None and not self.native.closed and not self.detached and not self.request_failed:
                try: self.call("tia_detach"); self.detached = True
                except Exception as error: failures.append("detach: " + str(error))
            try:
                cleanup["hostClose"] = self.client.close()
                save(self.root / "host-close.json", {"close": cleanup["hostClose"], "rawStderrLines": self.client.errors, "atUtc": utc()})
            except Exception as error: failures.append("Host close: " + str(error))
        if self.tracker is not None:
            try: self.tracker.stop_monitor()
            except Exception as error: failures.append("monitor: " + str(error))
            failures += self.tracker.failures
            watchers = list(self.tracker.tracked.values())
            watchers.sort(key=lambda w: 0 if w.evidence["identity"]["name"] == "Siemens.Automation.Portal.exe" else 1)
            for watcher in watchers:
                try:
                    proof = copy.deepcopy(watcher.evidence) if watcher.closed else watcher.finish(15, force=True, reason="exact owned descendant cleanup after Host closure")
                    proofs.append(proof)
                except Exception as error: failures.append("descendant: " + str(error))
        if self.host_handle is not None:
            try: cleanup["hostProof"] = self.host_handle.finish(10, force=True, reason="exact Host cleanup")
            except Exception as error: failures.append("Host proof: " + str(error))
        cleanup.update(descendantProofs=proofs, helperProof=self.helper_proof, failures=failures, finishedAtUtc=utc())
        worker_proofs = [p for p in proofs if Path(p["identity"]["exe"]).resolve() == Path(self.prepared["worker"]["path"])]
        native_proofs = [p for p in proofs if p["identity"]["name"] == "Siemens.Automation.Portal.exe"]
        all_proofs = proofs + ([cleanup["hostProof"]] if "hostProof" in cleanup else []) + ([self.helper_proof] if self.helper_proof else [])
        close = cleanup.get("hostClose", {})
        cleanup["allExactExitsVerified"] = bool(all_proofs) and all(p.get("completed") for p in all_proofs)
        receipts = close.get("workerExits", [])
        bound_receipt = len(worker_proofs) == len(receipts) == 1 and receipts[0].get("processId") == worker_proofs[0]["identity"]["pid"] and receipts[0].get("processExitVerified") is True and receipts[0].get("exitCode") == 0
        if bound_receipt:
            started = datetime.fromisoformat(receipts[0]["startedAtUtc"].replace("Z", "+00:00")).timestamp()
            bound_receipt = abs(started - worker_proofs[0]["identity"]["created"]) <= .01 and receipts[0].get("forcedTermination") is False
        cleanup["workerShutdownReceiptBoundToBirth"] = bound_receipt
        cleanup["normalLifecyclePassed"] = (not failures and cleanup["allExactExitsVerified"] and len(native_proofs) == 1 and bound_receipt and bool(self.helper_proof) and
            not any(p.get("forcedTermination") for p in all_proofs) and close.get("hostExitCode") == 0 and close.get("forcedHostStop") is False and all(p.get("exitCode") == 0 for p in all_proofs))
        save(self.root / "cleanup.json", cleanup)

    def decode_after_close(self):
        require(self.report["cleanup"]["allExactExitsVerified"], "Independent decoders run only after exact native/Host/helper exits")
        verify_package(self.prepared["managedPackage"])
        output = self.root / "independent"
        commands = [[sys.executable, str(self.root / "coordinator-source/verify_translation_populated_category_probe.py"), str(self.root / "native"), str(output)],
                    ["dotnet", self.prepared["managedTests"]["path"], "--native-category-probe", str(output / "managed-cases.json"), str(output / "managed-verification.json")]]
        for index, command in enumerate(commands, 1):
            run = subprocess.run(command, cwd=REPO, capture_output=True, timeout=90)
            (self.root / f"decoder-{index}.stdout.raw").write_bytes(run.stdout); (self.root / f"decoder-{index}.stderr.raw").write_bytes(run.stderr)
            save(self.root / f"decoder-{index}.json", {"command": command, "returncode": run.returncode, "nativeInvoked": False, "ranAfterExactProcessCleanup": True})
            require(run.returncode == 0, "Independent/managed decoder failed")
        independent, managed = load(output / "verification.json"), load(output / "managed-verification.json")
        require(independent["allEvidenceChecksPassed"] is True and independent["nativeImportAllowed"] is False and independent["nativeQualified"] is False and
                managed["allPassed"] is True and managed["nativeImportAllowed"] is False, "Independent result drifted or enabled profile")
        self.report.update(outcome=independent["outcome"], independentVerification=ref(output / "verification.json"), managedVerification=ref(output / "managed-verification.json"),
                           categoryRestrictionQualified=False, importsRemainDisabled=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, default=BUILD / "published-host/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=Path, default=BUILD / "src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe")
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--project", type=Path, default=Path(r"D:\TIAW\wt02\s\TiaMcpScratch_20261015_071342_e395a300\TiaMcpScratch_20261015_071342_e395a300\TiaMcpScratch_20261015_071342_e395a300.ap21"))
    parser.add_argument("--source-capture", type=Path, default=REPO / "artifacts/production-watch-20261015/translations/native-category-01")
    parser.add_argument("--template", type=Path, default=LANE / "native-request-template.json")
    parser.add_argument("--managed-tests", type=Path, default=LANE / "managed-build/bin/TiaMcp.Translations.Tests/debug/TiaMcp.Translations.Tests.dll")
    parser.add_argument("--resource-root", type=Path, action="append", help="Repeat exactly for canonical repository artifacts, source wt02 ledger, and the new run root; defaults to all three")
    parser.add_argument("--recovery-gate", type=Path, required=True, help="SHA-bound six-case independent checkpoint/retrieval verification manifest")
    parser.add_argument("--preflight-output", type=Path, help="Fresh receipt path; failures are retained without starting Host/native")
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--execute", action="store_true", help="Root-only explicit sequential dispatch after successful independent recovery gate")
    args = parser.parse_args()
    dispatch_phase_entered = False
    try:
        require(30 <= args.timeout <= 300, "Timeout must be bounded between 30 and 300 seconds")
        prepared = preflight(args)
        if args.preflight_output: save(args.preflight_output, prepared)
        if not args.execute:
            print(json.dumps({"completed": True, "preparedOnly": True, "nativeInvoked": False, "root": prepared["root"], "resourceRoots": prepared["resourceRoots"]}, indent=2))
            return 0
        coordinator = Coordinator(args, prepared)
        dispatch_phase_entered = True
        return coordinator.execute()
    except Exception as error:
        failure = {"completed": False, "nativeMayHaveBeenInvoked": dispatch_phase_entered, "automaticReplay": False, "failure": {"type": type(error).__name__, "message": str(error)}}
        if not dispatch_phase_entered: failure["nativeInvoked"] = False
        if args.preflight_output and not args.preflight_output.exists(): save(args.preflight_output, failure)
        print(json.dumps(failure, indent=2)); return 1


if __name__ == "__main__": raise SystemExit(main())
