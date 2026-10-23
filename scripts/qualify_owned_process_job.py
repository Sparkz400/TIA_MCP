"""Bounded actual Windows process tests; no TIA, device, or target termination."""
import argparse
import ctypes as c
from ctypes import wintypes as w
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import psutil

from native_dispatch_guard import write
from owned_process_job import OwnedProcessJob


def child(mode, job_name):
    kernel = c.WinDLL("kernel32", use_last_error=True)
    kernel.OpenJobObjectW.argtypes = [w.DWORD, w.BOOL, w.LPCWSTR]; kernel.OpenJobObjectW.restype = w.HANDLE
    kernel.GetCurrentProcess.restype = w.HANDLE
    kernel.IsProcessInJob.argtypes = [w.HANDLE, w.HANDLE, c.POINTER(w.BOOL)]
    kernel.CloseHandle.argtypes = [w.HANDLE]
    handle = kernel.OpenJobObjectW(4, False, job_name)
    if not handle:
        raise c.WinError(c.get_last_error())
    member = w.BOOL()
    if not kernel.IsProcessInJob(kernel.GetCurrentProcess(), handle, c.byref(member)) or not member.value:
        raise AssertionError("Test interpreter escaped the original job")
    kernel.CloseHandle(handle)
    print(json.dumps({"mode": mode, "pid": os.getpid(), "created": psutil.Process().create_time(), "parentPid": os.getppid(),
                      "jobName": job_name, "originalJobMember": True}), flush=True)
    common = [sys.executable, __file__, "--job-name", job_name, "--child"]
    if mode == "brief":
        return 0
    if mode == "linger":
        time.sleep(3)
        return 0
    if mode == "bursts":
        for _ in range(24):
            subprocess.run([*common, "brief"], check=True, stdout=sys.stdout, stderr=sys.stderr,
                           creationflags=subprocess.CREATE_NO_WINDOW)
        return 0
    if mode == "orphan":
        subprocess.Popen([*common, "linger"], stdout=sys.stdout, stderr=sys.stderr,
                         creationflags=subprocess.CREATE_NO_WINDOW)
        return 0
    if mode == "breakaway":
        try:
            process = subprocess.Popen([*common, "brief"], stdout=sys.stdout, stderr=sys.stderr,
                creationflags=subprocess.CREATE_NO_WINDOW | subprocess.CREATE_BREAKAWAY_FROM_JOB)
            process.wait()
            return 92
        except OSError as error:
            print(json.dumps({"breakawayRefused": True, "winerror": error.winerror}), flush=True)
            return 0
    raise ValueError(mode)


def qualify(root):
    root = Path(root).resolve(); root.mkdir(parents=True, exist_ok=False)
    checks, cases = [], []

    def check(value, name):
        if not value:
            raise AssertionError(name)
        checks.append(name)

    for mode, total in (("bursts", 25), ("orphan", 2), ("breakaway", 1)):
        directory = root / mode; directory.mkdir()
        job = OwnedProcessJob()
        with (directory / "stdout.log").open("xb") as stdout, (directory / "stderr.log").open("xb") as stderr:
            job.start([sys.executable, __file__, "--job-name", job.name, "--child", mode], stdout=stdout, stderr=stderr)
            check(job.contains(job.process_handle), mode + ": original helper is a job member")
            check(not job.contains(job.k.GetCurrentProcess()), mode + ": unrelated coordinator remains outside job")
            check(job.wait(30) == 0, mode + ": original helper signaled with exit zero")
            after_helper = job.snapshot()
            if mode == "orphan":
                check(after_helper["activeProcesses"] >= 1, "Parent exit does not release its live child")
                try:
                    job.close()
                    raise AssertionError("Live job unexpectedly closed")
                except ValueError:
                    check(job.job is not None and job.process_handle is not None, "Refused close retains original handles")
            deadline = time.monotonic() + 15
            while job.snapshot()["activeProcesses"] != 0:
                if time.monotonic() >= deadline:
                    raise TimeoutError("Bounded test child did not drain; job remains held")
                time.sleep(.02)
            proof = job.terminal_proof()
            write(directory / "observed-terminal.json", proof)
            records = [json.loads(line) for line in (directory / "stdout.log").read_text().splitlines()]
            members = [row for row in records if row.get("originalJobMember")]
            check(len(members) == total and len({(row["pid"], row["created"]) for row in members}) == total,
                  mode + ": every expected interpreter independently observed original-job membership")
            check(proof["totalProcesses"] >= total,
                  mode + ": raw job accounting covers expected interpreters (not a distinct-process enumeration)")
            check(proof["limitTerminatedProcesses"] == 0, mode + ": no limit termination")
            receipt = write(directory / "terminal.json", proof)
            cases.append({"mode": mode, "proof": receipt, "afterHelper": after_helper,
                          "membershipObservations": members})
            job.close()
    job = OwnedProcessJob()
    with (root / "failed-launch.log").open("xb") as output:
        try:
            job.start([str(root / "absent-owned-test.exe")], stdout=output, stderr=output)
            raise AssertionError("Missing executable launched")
        except OSError:
            check(job.process_handle is None and job.snapshot()["totalProcesses"] == 0,
                  "Atomic launch failure creates no helper or descendant")
        try:
            job.start([sys.executable, "--version"], stdout=output, stderr=output)
            raise AssertionError("A second launch was permitted")
        except ValueError:
            check(job.snapshot()["totalProcesses"] == 0, "Failed atomic launch cannot be automatically retried")
    job.close()
    report = {"completed": True, "nativeInvoked": False, "automaticTermination": False,
              "checks": checks, "checkCount": len(checks), "cases": cases,
              "scope": "Actual Windows Python process trees; Siemens job compatibility remains unverified"}
    write(root / "verification.json", report)
    print(json.dumps(report))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--child", choices=["brief", "linger", "bursts", "orphan", "breakaway"])
    parser.add_argument("--job-name")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.child:
        raise SystemExit(child(args.child, args.job_name))
    if args.output is None:
        parser.error("--output is required")
    qualify(args.output)
