"""Exact helper CreateProcess lifetime; no PID reopen, termination or native API."""
import copy
import ctypes
from datetime import datetime, timedelta, timezone
import os
from pathlib import Path
import subprocess


def require(value, message):
    if not value: raise RuntimeError(message)


class CreatedProcess:
    def __init__(self, process, executable):
        from ctypes import wintypes
        self.process, self.handle = process, process._handle
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel.GetProcessTimes.argtypes = [wintypes.HANDLE, *([ctypes.POINTER(wintypes.FILETIME)] * 4)]
        self.kernel.GetProcessTimes.restype = wintypes.BOOL
        self.kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        self.kernel.QueryFullProcessImageNameW.restype = wintypes.BOOL
        self.kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        self.kernel.GetExitCodeProcess.restype = wintypes.BOOL
        times = [wintypes.FILETIME() for _ in range(4)]
        require(self.kernel.GetProcessTimes(self.handle, *(ctypes.byref(t) for t in times)), "Original helper birth unavailable")
        ticks = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
        seconds, remainder = divmod(ticks, 10000000)
        birth = datetime(1601, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=seconds)
        length = wintypes.DWORD(32768); buffer = ctypes.create_unicode_buffer(length.value)
        require(self.kernel.QueryFullProcessImageNameW(self.handle, 0, buffer, ctypes.byref(length)), "Original helper image unavailable")
        require(Path(buffer.value).resolve() == Path(executable).resolve(), "Created helper image differs")
        self.proof = {"identity": {"pid": process.pid, "parentPid": os.getpid(), "exe": str(Path(executable).resolve()),
                "processStartedAtUtc": birth.strftime("%Y-%m-%dT%H:%M:%S") + f".{remainder:07d}Z"},
            "sameCreateProcessHandle": True, "birthAndImageVerified": True, "processExitVerified": False, "handleClosed": False, "forcedTermination": False}

    def finish(self):
        from ctypes import wintypes
        code = self.process.wait(timeout=0); actual = wintypes.DWORD()
        require(self.kernel.GetExitCodeProcess(self.handle, ctypes.byref(actual)) and actual.value == code, "Original helper terminal code differs")
        self.proof.update(processExitVerified=True, exitCode=code)
        self.handle.Close(); self.proof["handleClosed"] = self.handle.closed is True
        self.proof["completed"] = self.proof["processExitVerified"] and self.proof["handleClosed"]
        return copy.deepcopy(self.proof)


def await_created_terminal(process, lifetime, timeout, report):
    """Caller holds resource locks. Cancellation/deadline cannot unwind them while helper lives."""
    unknown = []
    try:
        process.wait(timeout=timeout)
    except BaseException as error:
        unknown.append(type(error).__name__ + ": " + str(error))
        try: report({"outcomeUnknown": True, "originalCreateProcessHandleRetained": True, "storeLocksRetained": True,
                     "hostCloseProhibited": True, "automaticTermination": False, "failure": unknown[-1]})
        except BaseException: pass
        while process.returncode is None:
            try: process.wait(timeout=1)
            except subprocess.TimeoutExpired: continue
            except BaseException as repeated:
                message = type(repeated).__name__ + ": " + str(repeated)
                if message not in unknown:
                    unknown.append(message)
                    try: report({"outcomeUnknown": True, "storeLocksRetained": True, "originalCreateProcessHandleRetained": True, "failure": message})
                    except BaseException: pass
    if lifetime is None:
        # The original Popen handle proved terminal, but identity capture failed.
        if hasattr(process, "_handle"): process._handle.Close()
        return {"completed": False, "processExitVerified": True, "exitCode": process.returncode,
                "sameCreateProcessHandle": True, "identityCaptureFailed": True, "forcedTermination": False, "unknownReasons": unknown}
    proof = lifetime.finish(); proof["deadlineOrCancellationObserved"] = bool(unknown); proof["unknownReasons"] = unknown
    proof["completed"] = proof["completed"] and not unknown
    return proof
