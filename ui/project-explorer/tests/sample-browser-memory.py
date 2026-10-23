"""Sample this benchmark's Chromium process tree using Windows public process APIs.

Working-set sums can double-count shared pages. These are observed samples, not a
certified continuous peak. No other processes are inspected for memory contents.
"""
import ctypes
from ctypes import wintypes
import json
import os
import sys
import threading
import time


def main():
    if os.name != "nt":
        print(json.dumps({"unavailable": "Windows process-tree sampler is not available on this OS."}), flush=True)
        return
    root_pid = int(sys.argv[1])
    interval = float(sys.argv[2]) if len(sys.argv) > 2 else 0.05
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    pointer = ctypes.c_size_t

    class ProcessEntry(ctypes.Structure):
        _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
                    ("th32ProcessID", wintypes.DWORD), ("th32DefaultHeapID", pointer),
                    ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
                    ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", wintypes.LONG),
                    ("dwFlags", wintypes.DWORD), ("szExeFile", wintypes.WCHAR * 260)]

    class MemoryCounters(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                    ("PeakWorkingSetSize", pointer), ("WorkingSetSize", pointer),
                    ("QuotaPeakPagedPoolUsage", pointer), ("QuotaPagedPoolUsage", pointer),
                    ("QuotaPeakNonPagedPoolUsage", pointer), ("QuotaNonPagedPoolUsage", pointer),
                    ("PagefileUsage", pointer), ("PeakPagefileUsage", pointer), ("PrivateUsage", pointer)]

    kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)]
    kernel.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)]
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(MemoryCounters), wintypes.DWORD]
    stopped = threading.Event()
    threading.Thread(target=lambda: (sys.stdin.readline(), stopped.set()), daemon=True).start()
    while not stopped.is_set():
        started = time.time()
        snapshot = kernel.CreateToolhelp32Snapshot(2, 0)
        if snapshot == ctypes.c_void_p(-1).value:
            print(json.dumps({"unavailable": "Process enumeration failed", "error": ctypes.get_last_error()}), flush=True)
            return
        entries = {}
        entry = ProcessEntry()
        entry.dwSize = ctypes.sizeof(entry)
        try:
            success = kernel.Process32FirstW(snapshot, ctypes.byref(entry))
            while success:
                entries[entry.th32ProcessID] = entry.th32ParentProcessID
                success = kernel.Process32NextW(snapshot, ctypes.byref(entry))
        finally:
            kernel.CloseHandle(snapshot)
        pids = {root_pid}
        previous = -1
        while previous != len(pids):
            previous = len(pids)
            pids.update(pid for pid, parent in entries.items() if parent in pids)
        working = private = errors = observed = 0
        for pid in pids:
            handle = kernel.OpenProcess(0x0400 | 0x0010, False, pid)
            if not handle:
                errors += 1
                continue
            counters = MemoryCounters()
            counters.cb = ctypes.sizeof(counters)
            try:
                if psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
                    working += counters.WorkingSetSize
                    private += counters.PrivateUsage
                    observed += 1
                else:
                    errors += 1
            finally:
                kernel.CloseHandle(handle)
        print(json.dumps({"timestampMs": int(started * 1000), "workingSetBytes": working,
                          "privateCommitBytes": private, "processCount": observed,
                          "unavailableProcessCount": errors, "samplingCostMs": (time.time() - started) * 1000}), flush=True)
        stopped.wait(interval)


if __name__ == "__main__":
    main()
