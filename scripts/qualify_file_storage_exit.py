"""Qualify natural exit of every owned TIA process within its terminal deadline.

Runs the worker's headless PLCSIM discovery probe without starting a CPU: one owned
Portal, one new scratch project and its FileStorage server. Original handles are
retained in an owned job; nothing is terminated. The FileStorage server gets its
measured deadline (qualify_force_capture.FILE_STORAGE_TERMINAL_SECONDS); every other
process keeps 45 s.
"""
import argparse
import json
import sys
import time
from pathlib import Path

import psutil
import native_dispatch_guard as guard
from owned_process_job import OwnedProcessJob
from prepare_hmi_fixture_ui import FixtureProcesses, hold_missing
from qualify_force_capture import FILE_STORAGE_SERVER
from qualify_translation_category_probe import StoreExclusiveWindow
from qualify_worker_startup_exact import identity

ROOT = Path(__file__).resolve().parents[1]
SOURCES = [Path(__file__)] + [ROOT / "scripts" / n for n in ("native_dispatch_guard.py", "owned_process_job.py", "prepare_hmi_fixture_ui.py",
                                                          "qualify_worker_startup_exact.py", "qualify_translation_category_probe.py", "qualify_force_capture.py")]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="New directory under this repository's artifacts.")
    args = parser.parse_args()
    root, worker = args.output.resolve(), args.worker.resolve()
    guard.require(root.is_relative_to((ROOT / "artifacts").resolve()) and not root.exists(), "A new artifacts directory is required")
    root.mkdir(parents=True)
    output = root / "native"
    project = output / "project/PlcsimProbe/PlcsimProbe.ap21"
    pins = [guard.reference(p) for p in SOURCES]
    binaries = [guard.reference(p) for p in sorted(worker.parent.iterdir()) if p.is_file()]
    result = {"completed": False, "automaticReplay": False}
    resources = [dict(kind="project", path=str(project), effect="Create and close one new scratch discovery project"),
                 dict(kind="workspace", path=str(output), effect="Read-only PLCSIM discovery without starting a CPU")]
    with StoreExclusiveWindow([root], project, root / "window", mode="isolatedQualificationMutation", resources=resources,
                              effects="One owned headless Portal and project, with exact retained process lifetime"):
        request = guard.write(root / "request.json", dict(worker=guard.reference(worker), resources=resources,
            runner=guard.reference(__file__), sourcePins=pins, workerFiles=binaries, mode="--plcsim-discovery-probe"))
        process = tracker = ticket = None
        with (root / "stdout.log").open("xb") as stdout, (root / "stderr.log").open("xb") as stderr:
            try:
                process = OwnedProcessJob()
                ticket = guard.begin(guard.DEFAULT_ROOT, job_name=process.name, source=request["path"])
                process.start([str(worker), "--plcsim-discovery-probe", str(output)], stdout=stdout, stderr=stderr)
                tracker = FixtureProcesses(identity(psutil.Process(process.pid)), root, output, job=process)
                seen = False
                while process.poll() is None:
                    if not seen and (output / "start-return.json").exists():
                        try: started = guard.read(output / "start-return.json")
                        except json.JSONDecodeError: time.sleep(.1); continue
                        tracker.native(started["processId"])
                        seen = True
                    time.sleep(.1)
            finally:
                if process is not None and process.process_handle is not None:
                    if tracker is None: hold_missing(root, "Probe started but original process capture failed")
                    tracker.await_terminal("Natural exit within each process's terminal deadline; no automatic termination", timeout_seconds=45)
                    native_result = guard.read(output / "result.json")
                    proof = tracker.finish(explicit_native_dispose=native_result.get("explicitOwnedSdkProcessDisposeCompleted") is True)
                    result["processProof"] = proof
                    result["workerExitCode"] = process.wait()
                    guard.complete(ticket, proof["jobProof"]["path"])
                    process.close()
                    storage = [e for e in proof["exits"] if e["identity"]["name"] == FILE_STORAGE_SERVER]
                    result["fileStorageExits"] = [{k: e.get(k) for k in ("observedExitSeconds", "terminalDeadlineSeconds")} for e in storage]
                    result["exitSeconds"] = {e["identity"]["name"] + ":" + str(e["identity"]["pid"]): e.get("observedExitSeconds") for e in proof["exits"]}
                    result["completed"] = (native_result.get("completed") is True and result["workerExitCode"] == 0 and proof["completed"] and
                                           proof["normalLifecyclePassed"] and len(storage) >= 1)
    result["sourcePinsUnchanged"] = [guard.reference(p) for p in SOURCES] == pins
    result["workerFilesUnchanged"] = [guard.reference(p) for p in sorted(worker.parent.iterdir()) if p.is_file()] == binaries
    result["completed"] = result["completed"] and result["sourcePinsUnchanged"] and result["workerFilesUnchanged"]
    guard.write(root / "result.json", result)
    print(json.dumps({"completed": result["completed"], "fileStorageExits": result.get("fileStorageExits"), "report": str(root / "result.json")}), flush=True)
    return 0 if result["completed"] else 1


if __name__ == "__main__":
    sys.exit(main())
