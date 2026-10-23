"""Read-only lifetime evidence for an exact owned TIA process.

Capture the handle immediately after a successful owned tia_start. Waiting on
that retained handle avoids reopening a possibly reused PID during cleanup.
Process exit does not prove that Siemens has released every project lock.
"""
import copy
import datetime
import math
import os
import time


SYNCHRONIZE = 0x00100000
QUERY_LIMITED_INFORMATION = 0x1000
WAIT_OBJECT_0 = 0
WAIT_TIMEOUT = 258
WAIT_FAILED = 0xFFFFFFFF


class ProcessExitNotVerified(RuntimeError):
    def __init__(self, message, evidence):
        super().__init__(message)
        self.evidence = copy.deepcopy(evidence)


class _WindowsProcessApi:
    def __init__(self):
        if os.name != "nt":
            raise OSError("Owned TIA process monitoring requires Windows.")
        import ctypes
        from ctypes import wintypes
        self.ctypes = ctypes
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.kernel.WaitForSingleObject.restype = wintypes.DWORD
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel.CloseHandle.restype = wintypes.BOOL
        self.kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, self.ctypes.POINTER(wintypes.DWORD)]
        self.kernel.GetExitCodeProcess.restype = wintypes.BOOL

    def open_process(self, access, inherit, process_id):
        return self.kernel.OpenProcess(access, inherit, process_id)

    def wait(self, handle, timeout_ms):
        return self.kernel.WaitForSingleObject(handle, timeout_ms)

    def close(self, handle):
        return bool(self.kernel.CloseHandle(handle))

    def exit_code(self, handle):
        value = self.ctypes.wintypes.DWORD()
        if not self.kernel.GetExitCodeProcess(handle, self.ctypes.byref(value)):
            raise OSError(self.last_error(), "GetExitCodeProcess failed")
        return value.value

    def last_error(self):
        return self.ctypes.get_last_error()


class OwnedTiaProcessWatcher:
    """One captured SYNCHRONIZE handle; no terminate, attach or UI operation."""
    def __init__(self, process_id, *, api=None, clock=time.monotonic, wait_chunk_ms=1000, query_exit_code=False):
        if type(process_id) is not int or not 0 < process_id <= 0xFFFFFFFF:
            raise ValueError("A positive exact owned TIA process ID is required.")
        if type(wait_chunk_ms) is not int or not 1 <= wait_chunk_ms <= 1000:
            raise ValueError("Process wait chunks must be between 1 and 1000 ms.")
        self.api = api if api is not None else _WindowsProcessApi()
        self.query_exit_code = query_exit_code
        self.clock, self.wait_chunk_ms = clock, wait_chunk_ms
        self._wait_started = self._close_attempted = self._close_verified = False
        self.evidence = {"processId": process_id, "access": "SYNCHRONIZE", "inheritable": False,
            "basis": "Handle captured immediately from the exact owned tia_start process ID; never reopened by PID.",
            "handleCaptured": False, "processExitVerified": False, "handleClosed": False,
            "projectLockReleaseVerified": False}
        access = SYNCHRONIZE | (QUERY_LIMITED_INFORMATION if query_exit_code else 0)
        if query_exit_code: self.evidence["access"] = "SYNCHRONIZE|QUERY_LIMITED_INFORMATION"
        self._handle = self.api.open_process(access, False, process_id)
        if not self._handle:
            self.evidence.update(failure="OpenProcess", win32Error=self.api.last_error())
            raise ProcessExitNotVerified("Could not capture the exact owned TIA process handle.", self.evidence)
        self.evidence.update(handleCaptured=True, capturedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())

    def close(self):
        """Attempt CloseHandle once, including callers that never begin a wait."""
        if self._close_attempted:
            return self._close_verified
        self._close_attempted = True
        self._close_verified = self.api.close(self._handle) is True
        self.evidence["handleClosed"] = self._close_verified
        if not self._close_verified:
            self.evidence["closeWin32Error"] = self.api.last_error()
        return self._close_verified

    def wait_for_exit(self, timeout_ms=120000):
        """Accept only WAIT_OBJECT_0, close the captured handle after every wait."""
        if self._wait_started or self._close_attempted:
            raise ProcessExitNotVerified("The captured process handle cannot be waited again after use or disposal.", self.evidence)
        self._wait_started = True
        started = self.clock()
        self.evidence.update(timeoutMs=timeout_ms, waitChunkMs=self.wait_chunk_ms, waitCount=0,
                             completed=False, waitResult=None)
        failure = None
        try:
            if type(timeout_ms) is not int or not 1 <= timeout_ms <= 300000:
                raise ValueError("Process-exit timeout must be between 1 and 300000 ms.")
            deadline = started + timeout_ms / 1000
            while True:
                remaining_ms = math.ceil(max(0, deadline - self.clock()) * 1000)
                if remaining_ms <= 0:
                    failure = "Process exit was not observed within the bounded wait."
                    self.evidence["failure"] = "timeout"
                    break
                result = self.api.wait(self._handle, min(self.wait_chunk_ms, remaining_ms))
                self.evidence["waitCount"] += 1
                self.evidence["waitResult"] = result
                if result == WAIT_OBJECT_0:
                    self.evidence["processExitVerified"] = True
                    if self.query_exit_code: self.evidence["exitCode"] = self.api.exit_code(self._handle)
                    break
                if result != WAIT_TIMEOUT:
                    self.evidence["failure"] = "WaitForSingleObject" if result == WAIT_FAILED else "unexpectedWaitResult"
                    if result == WAIT_FAILED:
                        self.evidence["win32Error"] = self.api.last_error()
                    failure = "The exact owned TIA process-exit wait failed."
                    break
        except Exception as error:
            failure = "The exact owned TIA process-exit wait raised " + type(error).__name__ + "."
            self.evidence.update(failure="waitException", exceptionType=type(error).__name__)
        finally:
            self.evidence["elapsedMs"] = max(0, round((self.clock() - started) * 1000))
            try:
                if not self.close():
                    failure = failure or "Closing the captured process handle failed."
            except Exception as error:
                failure = failure or "Closing the captured process handle raised " + type(error).__name__ + "."
                self.evidence.update(handleClosed=False, closeExceptionType=type(error).__name__)
        self.evidence["completed"] = self.evidence["processExitVerified"] is True and self.evidence["handleClosed"] is True and failure is None
        if not self.evidence["completed"]:
            raise ProcessExitNotVerified(failure or "Process exit could not be verified.", self.evidence)
        return copy.deepcopy(self.evidence)
