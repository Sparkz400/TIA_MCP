"""Bounded actual MCP startup probe with retained exact native-process handles."""
import argparse
import hashlib
import json
import threading
import time
from pathlib import Path

import psutil
from block_test_process import OwnedTiaProcessWatcher
from smoke_mcp import Client


def identity(process):
    return {"pid": process.pid, "parentPid": process.ppid(), "created": process.create_time(),
            "name": process.name(), "exe": process.exe(), "command": process.cmdline()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=120)
    args = parser.parse_args()
    args.root.mkdir(parents=True, exist_ok=False)
    report = {"completed": False, "scope": "empty start/status/detach only", "automaticReplay": False,
              "steps": [], "processes": [], "nativeExits": [], "binaries": {}}
    for key, path in (("host", args.host), ("worker", args.worker)):
        report["binaries"][key] = {"path": str(path.resolve()), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
    def save():
        (args.root / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    client = Client(args.host.resolve(), args.timeout, ["--worker", str(args.worker.resolve()), "--artifacts", str(args.root.resolve() / "host")])
    report["host"] = identity(psutil.Process(client.process.pid))
    watchers, seen = {}, set()
    stop = threading.Event()
    def monitor():
        while not stop.wait(0.25):
            try:
                parent = psutil.Process(client.process.pid)
                if parent.create_time() != report["host"]["created"]:
                    return
                descendants = parent.children(recursive=True)
                for proc in descendants:
                    key = (proc.pid, proc.create_time())
                    if key in seen:
                        continue
                    entry = identity(proc)
                    seen.add(key)
                    report["processes"].append(entry)
                    if entry["name"] == "Siemens.Automation.Portal.exe" and any(
                            p["pid"] == entry["parentPid"] and Path(p["exe"]).resolve() == args.worker.resolve()
                            for p in report["processes"]):
                        watchers[proc.pid] = OwnedTiaProcessWatcher(proc.pid, query_exit_code=True)
                        watchers[proc.pid].evidence["basis"] = "Retained handle captured from the exact admitted worker's native child PID and creation time; never reopened for exit proof."
                    (args.root / "processes.json").write_text(json.dumps(report["processes"], indent=2), encoding="utf-8")
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
    monitor_thread = threading.Thread(target=monitor, daemon=True)
    monitor_thread.start()
    save()
    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "exact-startup", "version": "1"}})
        client.notify("notifications/initialized")
        for name in ["tia_discover", "tia_start", "tia_session_status", "tia_detach", "tia_discover"]:
            row = {"name": name, "started": time.time()}
            report["steps"].append(row)
            save()
            row["result"] = client.tool(name)
            row["elapsedSeconds"] = time.time() - row["started"]
            save()
            print("PASS", name, round(row["elapsedSeconds"], 3), flush=True)
            if name in ("tia_start", "tia_session_status"):
                stop.set()
                monitor_thread.join(2)
                assert not monitor_thread.is_alive(), "Process monitor did not stop before handle verification"
                assert row["result"]["ownsPortalProcess"] is True and row["result"]["projects"] == []
                pid = row["result"]["processId"]
                if name == "tia_start":
                    report["ownedProcessId"] = pid
                else:
                    assert pid == report["ownedProcessId"], "Status changed the exact owned native process"
                if pid not in watchers:
                    watchers[pid] = OwnedTiaProcessWatcher(pid, query_exit_code=True)
            if name == "tia_detach":
                for watcher in watchers.values():
                    report["nativeExits"].append(watcher.wait_for_exit(timeout_ms=30000))
        before = report["steps"][0]["result"]["processes"]
        after = report["steps"][-1]["result"]["processes"]
        assert before == after, "Discovery changed after empty owned lifecycle"
        report["preexistingPreserved"] = True
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        print("FAIL", error, flush=True)
    finally:
        report["close"] = client.close()
        stop.set()
        monitor_thread.join(2)
        for pid, watcher in watchers.items():
            if any(e.get("processId") == pid for e in report["nativeExits"]):
                continue
            try:
                report["nativeExits"].append(watcher.wait_for_exit(timeout_ms=30000))
            except Exception as error:
                report["nativeExits"].append(getattr(error, "evidence", {"processId": pid, "error": str(error)}))
        report["hostLogs"] = client.errors
        report["completed"] = bool(report["completed"] and watchers and all(e.get("completed") for e in report["nativeExits"]))
        worker_ids = {p["pid"] for p in report["processes"] if Path(p["exe"]).resolve() == args.worker.resolve()}
        worker_exits = report["close"]["workerExits"]
        report["completed"] &= report["close"]["hostExitCode"] == 0 and not report["close"]["forcedHostStop"] and len(worker_ids) == len(worker_exits) == 1 and all(e["processId"] in worker_ids and e["processExitVerified"] and e["exitCode"] == 0 for e in worker_exits)
        save()
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
