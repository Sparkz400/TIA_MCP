"""Explicit failed-controller recovery through duplicated ORIGINAL process handles.

--prepare observes only the named controller and retained receipt files. --execute
requires that reviewed admission's hash plus --release-controller. Native/worker/
Host PIDs are never opened or terminated. Only the exact held Python controller
can be terminated, after every admitted original process object proves terminal.
"""
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import time

import psutil


def require(value, message):
    if not value: raise RuntimeError(message)


def utc(): return datetime.now(timezone.utc).isoformat()
def sha(path): return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def ref(path): return {"path": str(Path(path).resolve()), "sha256": sha(path)}
def canonical(path): return str(Path(path).resolve()).casefold()


def emit(path, value):
    with Path(path).open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2); stream.write("\n"); stream.flush(); os.fsync(stream.fileno())


def observe(pid):
    p = psutil.Process(pid)
    return {"pid": p.pid, "parentPid": p.ppid(), "created": p.create_time(),
        "name": p.name(), "exe": p.exe(), "command": p.cmdline()}


def checked(packet):
    require(sha(packet["path"]) == packet["sha256"], "Bound artifact changed")
    return json.loads(Path(packet["path"]).read_text(encoding="utf-8-sig"))


def receipt_inventory(receipts, controller, blocked):
    require(len(receipts) == 3, "Exactly three retained start receipts required")
    rows = [checked(r) for r in receipts]
    require(sorted(r["role"] for r in rows) == ["host", "native", "worker"], "Exactly one Host, worker and native required")
    require(len({(r["identity"]["pid"], r["identity"]["created"]) for r in rows}) == 3, "Repeated process identity")
    by_role = {r["role"]: r for r in rows}; host, worker, native = (by_role[k]["identity"] for k in ("host", "worker", "native"))
    require(host["parentPid"] == controller["pid"] and controller["created"] <= host["created"], "Controller does not own this Host birth")
    require(worker["parentPid"] == host["pid"] and native["parentPid"] == worker["pid"] and
        host in by_role["worker"]["ancestry"] and worker in by_role["native"]["ancestry"] and host in by_role["native"]["ancestry"], "Retained exact ancestry differs")
    require(native["name"] == "Siemens.Automation.Portal.exe" and worker["name"] == "TiaMcp.Openness.V21.exe", "Unexpected native/worker image")
    require(blocked.get("workerStartupCaptured") is True and blocked.get("nativeStartupInventoryComplete") is True and
        {(r["role"], json.dumps(r["identity"], sort_keys=True)) for r in blocked["retained"]} ==
        {(r["role"], json.dumps(r["identity"], sort_keys=True)) for r in rows}, "Blocked inventory does not match all retained starts")
    for row in rows:
        h, info = row["handle"], row["identity"]
        require(h.get("handleCaptured") is True and h.get("handleClosed") is False and h.get("birthAndImageVerified") is True and
            h.get("sameOsHandleRetained") is True and h["processId"] == info["pid"] and
            canonical(h["imagePath"]) == canonical(info["exe"]) and type(h["originalFileTimeTicks"]) is int and
            abs(h["originalFileTimeTicks"] / 10000000 - 11644473600 - info["created"]) <= .000002,
            "Unqualified original handle receipt")
    return rows


class WindowsApi:
    """Only open_controller invokes OpenProcess; all other process objects are duplicated."""
    def __init__(self):
        require(os.name == "nt" and ctypes.sizeof(ctypes.c_void_p) == 8, "64-bit Windows required")
        self.k = ctypes.WinDLL("kernel32", use_last_error=True); self.nt = ctypes.WinDLL("ntdll")
        signatures = {
            "OpenProcess": ([wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
            "GetCurrentProcess": ([], wintypes.HANDLE),
            "DuplicateHandle": ([wintypes.HANDLE, wintypes.HANDLE, wintypes.HANDLE, ctypes.POINTER(wintypes.HANDLE), wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.BOOL),
            "GetProcessId": ([wintypes.HANDLE], wintypes.DWORD),
            "GetProcessTimes": ([wintypes.HANDLE, *([ctypes.POINTER(wintypes.FILETIME)] * 4)], wintypes.BOOL),
            "QueryFullProcessImageNameW": ([wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)], wintypes.BOOL),
            "WaitForSingleObject": ([wintypes.HANDLE, wintypes.DWORD], wintypes.DWORD),
            "GetExitCodeProcess": ([wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)], wintypes.BOOL),
            "TerminateProcess": ([wintypes.HANDLE, wintypes.UINT], wintypes.BOOL),
            "CloseHandle": ([wintypes.HANDLE], wintypes.BOOL)}
        for name, (args, restype) in signatures.items():
            method = getattr(self.k, name); method.argtypes = args; method.restype = restype
        self.nt.NtQuerySystemInformation.argtypes = [wintypes.ULONG, ctypes.c_void_p, wintypes.ULONG, ctypes.POINTER(wintypes.ULONG)]
        self.nt.NtQuerySystemInformation.restype = ctypes.c_long

    def open_controller(self, pid):
        handle = self.k.OpenProcess(0x00100000 | 0x1000 | 0x0040 | 0x0001, False, pid)
        require(handle, "Exact controller handle unavailable"); return handle

    def handles(self, controller_pid):
        class Entry(ctypes.Structure):
            _fields_ = [("Object", ctypes.c_void_p), ("UniqueProcessId", ctypes.c_size_t), ("HandleValue", ctypes.c_size_t),
                ("GrantedAccess", wintypes.ULONG), ("CreatorBackTraceIndex", wintypes.USHORT), ("ObjectTypeIndex", wintypes.USHORT),
                ("HandleAttributes", wintypes.ULONG), ("Reserved", wintypes.ULONG)]
        require(ctypes.sizeof(Entry) == 40, "Unexpected SystemExtendedHandleInformation layout")
        size = 1 << 20
        for _ in range(12):
            require(size <= 256 * 1024 * 1024, "System handle inventory exceeded bound")
            buffer = ctypes.create_string_buffer(size); needed = wintypes.ULONG()
            status = self.nt.NtQuerySystemInformation(64, buffer, size, ctypes.byref(needed)) & 0xFFFFFFFF
            if status == 0xC0000004:
                size = max(size * 2, needed.value + 65536); continue
            require(status == 0, "NtQuerySystemInformation64 failed: " + hex(status))
            count = ctypes.c_size_t.from_buffer(buffer).value
            require(16 + count * ctypes.sizeof(Entry) <= size, "Truncated system handle inventory")
            result = []
            for i in range(count):
                entry = Entry.from_buffer(buffer, 16 + i * ctypes.sizeof(Entry))
                if entry.UniqueProcessId == controller_pid and (entry.GrantedAccess & 0x101000) == 0x101000:
                    result.append({"sourceHandle": entry.HandleValue, "grantedAccess": entry.GrantedAccess,
                        "objectTypeIndex": entry.ObjectTypeIndex})
            require(len(result) <= 4096, "Controller handle inventory exceeded bound")
            return result
        raise RuntimeError("System handle inventory did not stabilize")

    def duplicate(self, controller, source):
        duplicate = wintypes.HANDLE()
        # Zero options: NEVER DUPLICATE_CLOSE_SOURCE. Restricted duplicate has no termination access.
        if not self.k.DuplicateHandle(controller, source, self.k.GetCurrentProcess(), ctypes.byref(duplicate), 0x101000, False, 0): return None
        return duplicate.value

    def process_info(self, handle):
        pid = self.k.GetProcessId(handle)
        if not pid: return None
        times = [wintypes.FILETIME() for _ in range(4)]
        require(self.k.GetProcessTimes(handle, *(ctypes.byref(t) for t in times)), "Duplicated process birth unavailable")
        ticks = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
        length = wintypes.DWORD(32768); image = ctypes.create_unicode_buffer(length.value)
        require(self.k.QueryFullProcessImageNameW(handle, 0, image, ctypes.byref(length)), "Duplicated process image unavailable")
        return {"pid": pid, "originalFileTimeTicks": ticks, "imagePath": image.value}

    def wait(self, handle, milliseconds=0): return self.k.WaitForSingleObject(handle, milliseconds)
    def exit_code(self, handle):
        code = wintypes.DWORD()
        require(self.k.GetExitCodeProcess(handle, ctypes.byref(code)), "GetExitCodeProcess failed"); return code.value
    def terminate_controller(self, handle):
        require(self.k.TerminateProcess(handle, 125), "Exact failed-controller termination failed")
    def close(self, handle): return bool(self.k.CloseHandle(handle))


def validate_controller(api, handle, expected, observer):
    require(observer(expected["pid"]) == expected, "Controller identity or full command changed")
    captured = api.process_info(handle)
    require(captured and captured["pid"] == expected["pid"] and canonical(captured["imagePath"]) == canonical(expected["exe"]) and
        abs(captured["originalFileTimeTicks"] / 10000000 - 11644473600 - expected["created"]) <= .000002,
        "Original controller handle differs from admission")
    require(api.wait(handle) == 258, "Controller must still be the held live owner")
    return captured


def reconcile(admission, admission_ref, output, *, api, observer=observe, release_controller=False, reason=""):
    require(release_controller and isinstance(reason, str) and reason.strip(), "Explicit failed-controller release and reason required")
    require(admission.get("kind") == "reviewed-retained-tree-reconciliation" and admission.get("automaticReplay") is False, "Wrong reconciliation admission")
    require(sha(__file__) == admission["utility"]["sha256"], "Reviewed reconciliation utility changed")
    controller = admission["controller"]
    require(controller["pid"] != os.getpid() and controller["name"].lower() in {"python.exe", "pythonw.exe"} and controller["command"], "Only exact held Python controller may be released")
    rows = receipt_inventory(admission["startReceipts"], controller, checked(admission["blockedReceipt"]))
    output = Path(output); output.mkdir(exist_ok=False)
    emit(output / "intent.json", {"admission": admission_ref, "controller": controller, "reason": reason,
        "explicitControllerTerminationRequested": True, "nativeTerminationRequested": False, "normalLifecyclePassed": False,
        "automaticReplay": False, "atUtc": utc()})
    held = []; control = None; released = False; result = None
    try:
        control = api.open_controller(controller["pid"])
        controller_handle = validate_controller(api, control, controller, observer)
        selected = {}; inspected = 0
        expected = {r["identity"]["pid"]: r for r in rows}
        for source in api.handles(controller["pid"]):
            duplicate = api.duplicate(control, source["sourceHandle"])
            if duplicate is None: continue
            held.append(duplicate); inspected += 1
            info = api.process_info(duplicate)
            if info is None or info["pid"] not in expected:
                require(api.close(duplicate), "Candidate duplicate CloseHandle failed"); held.remove(duplicate); continue
            row = expected[info["pid"]]; capture = row["handle"]
            require(info["originalFileTimeTicks"] == capture["originalFileTimeTicks"] and canonical(info["imagePath"]) == canonical(capture["imagePath"]),
                "Controller-held PID has a different original birth/image")
            if info["pid"] in selected:
                require(api.close(duplicate), "Redundant duplicate CloseHandle failed"); held.remove(duplicate); continue
            selected[info["pid"]] = {"handle": duplicate, "source": source, "processObject": info, "receipt": row}
        require(set(selected) == set(expected), "An admitted original process handle is absent; native PID reopening prohibited")
        proofs = []
        for pid, match in selected.items():
            require(api.wait(match["handle"]) == 0, "Original admitted process is not signaled; controller release prohibited")
            proofs.append({"identity": match["receipt"]["identity"], "role": match["receipt"]["role"], "sourceControllerHandle": match["source"],
                "originalProcessObject": match["processObject"], "sameKernelProcessObjectDuplicatedFromHeldController": True,
                "exactOriginalWatcherHandleNumberKnown": False, "processExitVerified": True, "exitCode": api.exit_code(match["handle"]),
                "nativePidReopened": False, "nativeTerminationInvoked": False, "verifiedAtUtc": utc()})
        emit(output / "terminal-proof.json", {"completed": True, "qualificationPassed": False, "normalLifecyclePassed": False,
            "controller": controller, "controllerHandle": controller_handle, "exits": proofs, "inspectedControllerCandidates": inspected,
            "basis": "Duplicated existing controller-owned references to the exact receipt-bound kernel process objects; original watcher handle numbers were not logged", "atUtc": utc()})
        validate_controller(api, control, controller, observer)
        require(all(api.wait(m["handle"]) == 0 for m in selected.values()), "Retained terminal proof changed")
        emit(output / "release-controller-intent.json", {"controller": controller, "controllerHandle": controller_handle,
            "terminalProof": ref(output / "terminal-proof.json"), "admission": admission_ref, "exitCode": 125,
            "explicitControllerTermination": True, "nativeTerminationInvoked": False, "normalLifecyclePassed": False,
            "qualificationPassed": False, "reason": reason, "atUtc": utc()})
        api.terminate_controller(control); released = True
        while True:
            try:
                state = api.wait(control, 200)
                if state == 0: break
                require(state == 258, "Exact controller terminal wait failed")
            except BaseException:
                # Termination has been requested; retain the exact handle until
                # terminal rather than letting cancellation erase release proof.
                continue
        result = {"reconciliationCompleted": True, "qualificationPassed": False, "normalLifecyclePassed": False,
            "coordinatorInterventionRequired": True, "explicitControllerTermination": True,
            "controllerExitCode": api.exit_code(control), "controller": controller, "exits": proofs,
            "nativePidReopened": False, "nativeTerminationInvoked": False, "automaticReplay": False,
            "originalControllerReferencesReleasedByOwnerExit": True, "terminalProof": ref(output / "terminal-proof.json"), "atUtc": utc()}
    except BaseException as error:
        try: emit(output / "failure.json", {"reconciliationCompleted": False, "qualificationPassed": False,
            "controllerTerminationRequested": released, "failure": type(error).__name__ + ": " + str(error), "atUtc": utc()})
        except BaseException: pass
        raise
    finally:
        closes = [{"helperHandle": int(h), "closed": api.close(h)} for h in held]
        if control is not None: closes.append({"controllerHandle": int(control), "closed": api.close(control)})
        emit(output / "duplicate-close.json", {"allClosed": all(r["closed"] for r in closes), "closes": closes,
            "originalSourceHandlesClosedByDuplicateCall": False, "atUtc": utc()})
    require(all(r["closed"] for r in closes), "Reconciliation helper handle release incomplete")
    result["helperHandleClose"] = ref(output / "duplicate-close.json")
    emit(output / "result.json", result)
    return result


def main():
    p = argparse.ArgumentParser(description=__doc__)
    mode = p.add_mutually_exclusive_group(required=True); mode.add_argument("--prepare", action="store_true"); mode.add_argument("--execute", action="store_true")
    p.add_argument("--controller-pid", type=int); p.add_argument("--start-receipt", type=Path, action="append", default=[])
    p.add_argument("--blocked-receipt", type=Path); p.add_argument("--admission", type=Path)
    p.add_argument("--expected-admission-sha256"); p.add_argument("--output", type=Path, required=True)
    p.add_argument("--release-controller", action="store_true"); p.add_argument("--reason", default="")
    args = p.parse_args()
    if args.prepare:
        require(args.controller_pid and args.blocked_receipt, "Exact controller PID and blocked receipt required")
        controller = observe(args.controller_pid); receipts = [ref(path) for path in args.start_receipt]; blocked = ref(args.blocked_receipt)
        receipt_inventory(receipts, controller, checked(blocked))
        packet = {"kind": "reviewed-retained-tree-reconciliation", "controller": controller, "startReceipts": receipts,
            "blockedReceipt": blocked, "utility": ref(__file__), "automaticReplay": False,
            "allowedAction": "prove-three-retained-process-objects-terminal-then-release-exact-failed-controller", "atUtc": utc()}
        emit(args.output, packet); print(json.dumps({"preparedOnly": True, "admission": ref(args.output)}, indent=2)); return 0
    require(args.admission and args.expected_admission_sha256 and sha(args.admission) == args.expected_admission_sha256, "Reviewed admission hash required")
    result = reconcile(checked(ref(args.admission)), ref(args.admission), args.output, api=WindowsApi(),
        release_controller=args.release_controller, reason=args.reason)
    print(json.dumps(result, indent=2)); return 0


if __name__ == "__main__": raise SystemExit(main())
