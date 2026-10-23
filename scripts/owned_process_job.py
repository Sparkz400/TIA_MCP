"""Windows process containment assigned atomically by CreateProcessW.

No kill-on-close, breakaway, PID reopening, termination or automatic replay.
Accounting covers ordinary CreateProcess descendants, not WMI/broker launches
or explicit alternate parents. Native callers must retain the main editor's
birth/image-checked handle and reject observed out-of-job descendants.
"""
import ctypes as c
from ctypes import wintypes as w
import os
import subprocess
import time
import uuid


class StartupInfo(c.Structure):
    _fields_ = [
        ("cb", w.DWORD), ("lpReserved", w.LPWSTR), ("lpDesktop", w.LPWSTR), ("lpTitle", w.LPWSTR),
        ("dwX", w.DWORD), ("dwY", w.DWORD), ("dwXSize", w.DWORD), ("dwYSize", w.DWORD),
        ("dwXCountChars", w.DWORD), ("dwYCountChars", w.DWORD), ("dwFillAttribute", w.DWORD),
        ("dwFlags", w.DWORD), ("wShowWindow", w.WORD), ("cbReserved2", w.WORD),
        ("lpReserved2", c.c_void_p), ("hStdInput", w.HANDLE), ("hStdOutput", w.HANDLE), ("hStdError", w.HANDLE),
    ]


class StartupInfoEx(c.Structure):
    _fields_ = [("StartupInfo", StartupInfo), ("lpAttributeList", c.c_void_p)]


class ProcessInfo(c.Structure):
    _fields_ = [("hProcess", w.HANDLE), ("hThread", w.HANDLE), ("dwProcessId", w.DWORD), ("dwThreadId", w.DWORD)]


class Accounting(c.Structure):
    _fields_ = [(name, c.c_int64) for name in ("user", "kernel", "periodUser", "periodKernel")] + [
        ("faults", w.DWORD), ("total", w.DWORD), ("active", w.DWORD), ("terminated", w.DWORD)]


class BasicLimits(c.Structure):
    _fields_ = [("processTime", c.c_int64), ("jobTime", c.c_int64), ("flags", w.DWORD),
                ("minimumWorkingSet", c.c_size_t), ("maximumWorkingSet", c.c_size_t),
                ("activeLimit", w.DWORD), ("affinity", c.c_size_t),
                ("priority", w.DWORD), ("scheduling", w.DWORD)]


class OwnedProcessJob:
    def __init__(self):
        if os.name != "nt":
            raise OSError("Owned process jobs require Windows")
        self.k = c.WinDLL("kernel32", use_last_error=True)
        declarations = {
            "CreateJobObjectW": ([c.c_void_p, w.LPCWSTR], w.HANDLE),
            "CloseHandle": ([w.HANDLE], w.BOOL),
            "QueryInformationJobObject": ([w.HANDLE, c.c_int, c.c_void_p, w.DWORD, c.c_void_p], w.BOOL),
            "IsProcessInJob": ([w.HANDLE, w.HANDLE, c.POINTER(w.BOOL)], w.BOOL),
            "InitializeProcThreadAttributeList": ([c.c_void_p, w.DWORD, w.DWORD, c.POINTER(c.c_size_t)], w.BOOL),
            "UpdateProcThreadAttribute": ([c.c_void_p, w.DWORD, c.c_size_t, c.c_void_p, c.c_size_t, c.c_void_p, c.c_void_p], w.BOOL),
            "DeleteProcThreadAttributeList": ([c.c_void_p], None),
            "GetCurrentProcess": ([], w.HANDLE),
            "DuplicateHandle": ([w.HANDLE, w.HANDLE, w.HANDLE, c.POINTER(w.HANDLE), w.DWORD, w.BOOL, w.DWORD], w.BOOL),
            "CreateProcessW": ([w.LPCWSTR, w.LPWSTR, c.c_void_p, c.c_void_p, w.BOOL,
                                w.DWORD, c.c_void_p, w.LPCWSTR, c.c_void_p, c.POINTER(ProcessInfo)], w.BOOL),
            "WaitForSingleObject": ([w.HANDLE, w.DWORD], w.DWORD),
            "GetExitCodeProcess": ([w.HANDLE, c.POINTER(w.DWORD)], w.BOOL),
        }
        for name, (args, result) in declarations.items():
            fn = getattr(self.k, name); fn.argtypes = args; fn.restype = result
        self.name = "Local\\TiaMcp-Owned-" + uuid.uuid4().hex
        c.set_last_error(0)
        self.job = self.k.CreateJobObjectW(None, self.name)
        self.check(self.job, "CreateJobObjectW")
        self.process_handle = None
        self.pid = None
        self.created_atomically = False
        self.start_attempted = False
        if c.get_last_error() == 183:
            self.k.CloseHandle(self.job); self.job = None
            raise OSError("Unexpected preexisting owned job")

    @staticmethod
    def check(value, operation):
        if not value:
            raise c.WinError(c.get_last_error(), operation)
        return value

    def contains(self, handle):
        answer = w.BOOL()
        self.check(self.k.IsProcessInJob(handle, self.job, c.byref(answer)), "IsProcessInJob")
        return bool(answer.value)

    def snapshot(self):
        account, limits = Accounting(), BasicLimits()
        self.check(self.k.QueryInformationJobObject(self.job, 1, c.byref(account), c.sizeof(account), None),
                   "QueryInformationJobObject accounting")
        self.check(self.k.QueryInformationJobObject(self.job, 2, c.byref(limits), c.sizeof(limits), None),
                   "QueryInformationJobObject limits")
        if limits.flags != 0:
            raise ValueError("Owned job limits changed; breakaway/kill/limit termination not admitted")
        return {"jobName": self.name, "activeProcesses": account.active, "totalProcesses": account.total,
                "limitTerminatedProcesses": account.terminated, "limitFlags": limits.flags,
                "createdAtomicallyInJob": self.created_atomically, "automaticTermination": False,
                "helperHandleSignaled": self.process_handle is not None and self.poll() is not None}

    def start(self, args, *, stdout, stderr):
        """Create already inside the job; never fall back to uncontained launch."""
        if self.start_attempted:
            raise ValueError("An owned job can launch its helper only once")
        self.start_attempted = True
        import msvcrt
        attribute_buffer = None
        initialized = False
        handles = []
        with open(os.devnull, "rb") as stdin:
            try:
                current = self.k.GetCurrentProcess()
                for stream in (stdin, stdout, stderr):
                    duplicate = w.HANDLE()
                    self.check(self.k.DuplicateHandle(current, msvcrt.get_osfhandle(stream.fileno()),
                        current, c.byref(duplicate), 0, True, 2), "DuplicateHandle standard stream")
                    handles.append(duplicate.value)
                size = c.c_size_t()
                self.k.InitializeProcThreadAttributeList(None, 2, 0, c.byref(size))
                if not size.value:
                    raise OSError("Attribute list size unavailable")
                attribute_buffer = c.create_string_buffer(size.value)
                self.check(self.k.InitializeProcThreadAttributeList(attribute_buffer, 2, 0, c.byref(size)),
                           "InitializeProcThreadAttributeList")
                initialized = True
                jobs = (w.HANDLE * 1)(self.job)
                inherited = (w.HANDLE * len(handles))(*handles)
                for attribute, value in ((0x0002000D, jobs), (0x00020002, inherited)):
                    self.check(self.k.UpdateProcThreadAttribute(attribute_buffer, 0, attribute, value,
                        c.sizeof(value), None, None), "UpdateProcThreadAttribute")
                startup = StartupInfoEx()
                startup.StartupInfo.cb = c.sizeof(startup)
                startup.StartupInfo.dwFlags = 0x100
                startup.StartupInfo.hStdInput, startup.StartupInfo.hStdOutput, startup.StartupInfo.hStdError = handles
                startup.lpAttributeList = c.addressof(attribute_buffer)
                process = ProcessInfo()
                command = c.create_unicode_buffer(subprocess.list2cmdline([str(a) for a in args]))
                self.snapshot()
                self.check(self.k.CreateProcessW(str(args[0]), command, None, None, True,
                    0x08000000 | 0x00080000, None, None, c.byref(startup), c.byref(process)), "CreateProcessW in owned job")
                # Retain before any post-launch check or file I/O can fail.
                self.process_handle, self.pid = process.hProcess, process.dwProcessId
                self.created_atomically = True
                self.check(self.k.CloseHandle(process.hThread), "CloseHandle primary thread")
                if not self.contains(self.process_handle):
                    raise ValueError("Original helper handle is outside its creation job")
            finally:
                if initialized:
                    self.k.DeleteProcThreadAttributeList(attribute_buffer)
                for handle in handles:
                    self.k.CloseHandle(handle)
        return self

    def poll(self):
        if not self.process_handle:
            return None
        result = self.k.WaitForSingleObject(self.process_handle, 0)
        if result == 258:
            return None
        if result != 0:
            raise c.WinError(c.get_last_error(), "WaitForSingleObject original helper")
        code = w.DWORD()
        self.check(self.k.GetExitCodeProcess(self.process_handle, c.byref(code)), "GetExitCodeProcess")
        return code.value

    def wait(self, timeout=45):
        deadline = time.monotonic() + timeout
        while self.poll() is None:
            if time.monotonic() >= deadline:
                raise TimeoutError("Original helper is still live; job and process handles retained")
            time.sleep(.02)
        return self.poll()

    def terminal_proof(self):
        proof = self.snapshot()
        if not proof["helperHandleSignaled"] or proof["activeProcesses"] != 0:
            raise ValueError("Owned job/helper is not terminal")
        return {"schemaVersion": "owned-job-terminal.v1", **proof,
                "scope": "Retained helper and ordinary CreateProcess descendants in the original non-breakaway job",
                "individualEphemeralProcessHandlesClaimed": False,
                "brokeredOrAlternateParentProcessesQualified": False}

    def close(self):
        if self.process_handle:
            self.terminal_proof()
            self.check(self.k.CloseHandle(self.process_handle), "CloseHandle helper")
            self.process_handle = None
        if self.job:
            if self.snapshot()["activeProcesses"] != 0:
                raise ValueError("Live job cannot be released")
            self.check(self.k.CloseHandle(self.job), "CloseHandle job")
            self.job = None
