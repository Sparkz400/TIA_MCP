"""Qualify real native interruption of admitted mutations and their trusted reconciliation.

Each scenario runs in its own owned scratch project and artifact root:
start an owned TIA, create a scratch project, dispatch one admitted mutation and
interrupt it at a journal-observed boundary by terminating the exact worker and its
owned TIA (native interruption) or the exact Host (transport loss). A fresh Host and
fresh owned TIA then call engineering_admission_reconcile once. The run checks that
nothing was replayed (journal and dispatch count unchanged) and that the admission is
either released with one disposition per resource or held with reasons.
"""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
import msvcrt
import os
import pathlib
import subprocess
import time
import uuid

from qualify_reliability import ReliabilityClient, decoded, require, terminate_owned_test_worker
from smoke_mcp import ROOT

TIA_EXE = pathlib.Path(r"D:\Arbeit\Siemens\Portal V21\Bin\Siemens.Automation.Portal.exe")
PROBE = "FUNCTION \"LaneEProbe\" : Int\n{ S7_Optimized_Access := 'TRUE' }\nVERSION : 0.1\nBEGIN\n    #LaneEProbe := 7;\nEND_FUNCTION\n"
# name: (operation, interruption, journal trigger, expected outcome or None when evidence decides)
SCENARIOS = {
    "apply-kill-applying": ("change_apply", "worker", lambda j: j.get("state") == "applying", "released"),
    "apply-kill-checkpointing": ("change_apply", "worker", lambda j: j.get("state") == "checkpointing", None),
    "apply-host-loss": ("change_apply", "host", lambda j: j.get("state") == "applying", None),
    "restore-kill-preserving": ("change_restore", "worker", lambda j: j.get("state") == "restoring", None),
    "restore-kill-retrieving": ("change_restore", "worker", lambda j: "previous_project_closed" in (j.get("stages") or []), None),
    "retrieve-kill": ("project_retrieve", "worker", None, None),
    "scratch-kill": ("scratch_create", "worker", None, None),
    # A master copy from the scratch project into a new global library checkpoints both the project and the library.
    "library-apply-kill": ("change_apply", "worker", lambda j: j.get("state") == "applying", None),
    "library-restore-kill": ("change_restore", "worker", lambda j: any(r.get("state") == "dispatching"
                             for a in (j.get("recoveryAttempts") or []) for r in a.get("resources", [])), None),
}


def alive(pid):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.restype = wintypes.HANDLE
    handle = kernel.OpenProcess(0x00100000, False, pid)  # SYNCHRONIZE
    if not handle:
        return False
    try:
        return kernel.WaitForSingleObject(handle, 0) == 0x102  # WAIT_TIMEOUT
    finally:
        kernel.CloseHandle(handle)


def children(pid):
    script = f"Get-CimInstance Win32_Process -Filter 'ParentProcessId={int(pid)}' | Select-Object ProcessId,ExecutablePath | ConvertTo-Json -Compress"
    output = subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True, check=True).stdout.strip()
    rows = json.loads(output) if output else []
    return [rows] if isinstance(rows, dict) else rows


def wait_until(predicate, description, timeout, interval=0.02):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(interval)
    raise TimeoutError(description)


def read_json(path):
    """Read with full sharing: a plain open blocks the worker's File.Replace of its journal (seen natively)."""
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.restype = wintypes.HANDLE
    handle = kernel.CreateFileW(str(path), 0x80000000, 0x7, None, 3, 0, None)  # GENERIC_READ, share read/write/delete, OPEN_EXISTING
    if handle in (None, wintypes.HANDLE(-1).value):
        return None
    try:
        with os.fdopen(msvcrt.open_osfhandle(handle, os.O_RDONLY), "rb") as stream:
            handle = None
            return json.loads(stream.read().decode("utf-8-sig"))
    except (OSError, ValueError):
        return None
    finally:
        if handle is not None:
            kernel.CloseHandle(handle)


def admissions(root, operation):
    """Latest revision of every admission for one operation, read straight from the durable store."""
    found = []
    for folder in sorted((root / "engineering-admissions").glob("[0-9a-f]" * 32)):
        latest = read_json(sorted(folder.glob("*.json"))[-1])
        if latest and latest["scope"]["operation"] == operation:
            found.append(latest)
    return found


class Scenario:
    def __init__(self, name, host, worker, base):
        self.name, self.host, self.worker = name, host, worker
        self.root = base / name
        self.root.mkdir(parents=True)
        self.report = {"scenario": name, "artifactRoot": str(self.root), "steps": [], "passed": False, "toolCalls": []}

    def client(self):
        return ReliabilityClient(self.host, self.worker, self.root, timeout=900, record=self.report["toolCalls"])

    def step(self, name, value):
        self.report["steps"].append({"name": name, "result": value})
        print(f"  {self.name}: {name}", flush=True)
        return value

    def start(self, client, label="tia_start"):
        """Start the owned TIA and resolve this Host's exact worker before any timed interruption."""
        tia = self.step(label, client.tool("tia_start"))["processId"]
        workers = [int(c["ProcessId"]) for c in children(client.process.pid)
                   if c.get("ExecutablePath") and pathlib.Path(c["ExecutablePath"]).resolve() == self.worker]
        require(len(workers) == 1, "Expected exactly one native worker under the Host")
        self.worker_pid = workers[0]
        return tia

    def interrupt(self, client, tia_pid, mode):
        """Terminate exactly this Host's worker (and its owned TIA), or exactly this Host."""
        workers = [self.worker_pid]
        if mode == "worker":
            terminate_owned_test_worker(workers[0], self.worker)
            tia_exited_alone = not alive(tia_pid)
            if not tia_exited_alone:
                terminate_owned_test_worker(tia_pid, TIA_EXE)
            return {"terminated": "worker", "workerPid": workers[0], "tiaPid": tia_pid, "tiaExitedWithWorker": tia_exited_alone}
        client.kill_owned_host()
        worker_exited = True
        try:
            wait_until(lambda: not alive(workers[0]), "Worker did not exit after Host loss", 600, 0.5)
        except TimeoutError:
            worker_exited = False
            terminate_owned_test_worker(workers[0], self.worker)
        tia_exited = True
        try:
            wait_until(lambda: not alive(tia_pid), "Owned TIA did not exit after its worker", 120, 0.5)
        except TimeoutError:
            tia_exited = False
            terminate_owned_test_worker(tia_pid, TIA_EXE)
        return {"terminated": "host", "workerPid": workers[0], "workerExitedAfterHostLoss": worker_exited,
                "tiaPid": tia_pid, "tiaExitedWithWorker": tia_exited}

    def run(self, operation, mode, trigger, expected):
        if self.name.startswith("library-"):
            return self.run_library(operation, mode, trigger, expected)
        client = self.client()
        try:
            client.initialize()
            tia = self.start(client)
            if operation == "scratch_create":
                request = client.begin("tools/call", {"name": "tia_create_scratch", "arguments": {}})
                wait_until(lambda: any((self.root / "scratch").glob("*/*/*.ap21")), "Scratch project file never appeared", 600)
                return self.finish(client, request, tia, operation, mode, None, expected)
            scratch = self.step("tia_create_scratch", client.tool("tia_create_scratch"))
            project = scratch["artifactDirectory"]
            if operation == "project_retrieve":
                # The archive is the project's own checkpoint from a completed apply.
                applied = self.apply(client)
                archive = pathlib.Path(applied["recoveryArchive"])
                client.tool("tia_detach")
                client.close()
                client = self.client()
                client.initialize()
                tia = self.start(client, "tia_start (empty owned instance)")
                request = client.begin("tools/call", {"name": "project_retrieve", "arguments": {
                    "archivePath": str(archive), "expectedSha256": applied["recoveryArchiveSha256"]}})
                wait_until(lambda: any(p.is_dir() and any(p.iterdir()) for p in (self.root / "recovery").glob("r_*")), "Retrieval never wrote", 600)
                return self.finish(client, request, tia, operation, mode, None, expected)
            prepared = self.prepare(client)
            journal = pathlib.Path(prepared["journalPath"])
            if operation == "change_apply":
                request = client.begin("tools/call", {"name": "change_apply", "arguments": self.apply_arguments(prepared)})
            else:
                applied = self.step("change_apply", client.tool("change_apply", self.apply_arguments(prepared)))
                require(applied["state"] == "applied", "Fixture apply did not complete")
                request = client.begin("tools/call", {"name": "change_restore", "arguments": {
                    "changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}})
            wait_until(lambda: trigger(read_json(journal) or {}), "Journal never reached the interruption boundary", 900)
            return self.finish(client, request, tia, operation, mode, journal, expected, project)
        finally:
            client.close()

    def run_library(self, operation, mode, trigger, expected):
        client = self.client()
        try:
            client.initialize()
            tia = self.start(client)
            self.step("tia_create_scratch", client.tool("tia_create_scratch"))
            inventory = client.tool("project_inventory", {"maxItems": 1000})
            block = next(b for b in inventory["blocks"] if b["name"] == "TiaMcp_IsEnabled")
            created = self.step("library_create", client.tool("change_apply", self.apply_arguments(
                client.tool("change_prepare", {"kind": "library_create", "payload": {"schemaVersion": 1, "name": "LaneELib"}}))))
            require(created["state"] == "applied", "Fixture library was not created")
            prepared = self.step("change_prepare library_create_master_copy", client.tool("change_prepare", {"kind": "library_create_master_copy", "payload": {
                "schemaVersion": 1, "destinationLibraryId": created["journal"]["result"]["library"]["libraryId"],
                "target": {"plcPath": inventory["plcs"][0]["path"]}, "object": {"kind": "block", "objectPath": block["path"]}}}))
            journal = pathlib.Path(prepared["journalPath"])
            if operation == "change_apply":
                request = client.begin("tools/call", {"name": "change_apply", "arguments": self.apply_arguments(prepared)})
            else:
                applied = self.step("change_apply library_create_master_copy", client.tool("change_apply", self.apply_arguments(prepared)))
                require(applied["state"] == "applied" and len(applied["journal"]["checkpoints"]) == 2, "Expected a project and a library checkpoint")
                client.tool("tia_detach")
                client.close()
                client = self.client()
                client.initialize()
                tia = self.start(client, "tia_start (empty owned instance)")
                request = client.begin("tools/call", {"name": "change_restore", "arguments": {
                    "changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}})
            wait_until(lambda: trigger(read_json(journal) or {}), "Journal never reached the interruption boundary", 900)
            return self.finish(client, request, tia, operation, mode, journal, expected)
        finally:
            client.close()

    def prepare(self, client):
        return self.step("change_prepare", client.tool("change_prepare", {"kind": "source_upsert", "payload": {
            "language": "SCL", "source": PROBE, "expectedBlockNames": ["LaneEProbe"]}}))

    def apply_arguments(self, prepared):
        return {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "idempotencyKey": uuid.uuid4().hex,
                "checkpointSave": True, "compile": True, "save": True}

    def apply(self, client):
        prepared = self.prepare(client)
        applied = self.step("change_apply", client.tool("change_apply", self.apply_arguments(prepared)))
        require(applied["state"] == "applied", "Fixture apply did not complete")
        return applied

    def finish(self, client, request, tia, operation, mode, journal, expected, project=None):
        before = journal.read_bytes() if journal else None
        self.step("interrupt", {"journalAtInterrupt": read_json(journal) if journal else None, **self.interrupt(client, tia, mode)})
        if mode == "worker":
            response = client.receive(request)
            self.step("interrupted response", {"isError": response.get("isError"), "error": decoded(response).get("error", {}).get("code")})
            require(response.get("isError"), "An interrupted mutation must not report success")
        client.close()
        pending = [a for a in admissions(self.root, operation) if a["state"] in ("dispatchIntent", "outcomeUnknown")]
        require(len(pending) == 1, f"Expected one unresolved {operation} admission, found {len(pending)}")
        admission = pending[0]
        self.step("unresolved admission", {"admissionId": admission["id"], "state": admission["state"], "resources": admission["scope"]["resources"]})
        journal_after_interrupt = journal.read_bytes() if journal else None
        fresh = self.client()
        try:
            fresh.initialize()
            self.step("fresh tia_start", fresh.tool("tia_start"))
            result = self.step("engineering_admission_reconcile", fresh.tool("engineering_admission_reconcile", {"admissionId": admission["id"]}))
            status = self.step("engineering_operation_status", fresh.tool("engineering_operation_status", {"operationId": admission["id"]}))
            fresh.tool("tia_detach")
        finally:
            fresh.close()
        if journal:
            require(journal.read_bytes() == journal_after_interrupt, "Reconciliation changed the change journal (replay)")
        require(result["automaticReplay"] is False, "Reconciliation must not replay")
        evidence = read_json(self.root / result["evidence"]["relativePath"])
        rows = evidence["verification"]["resources"]
        require(len(rows) == len(admission["scope"]["resources"]), "One fresh observation per admitted resource")
        if result["released"]:
            require(status["state"] == "reconciled" and len(status["reconciliation"]) == len(rows), "Release reconciles every resource")
            require(all(r["disposition"] in ("unchanged", "verifiedApplied") for r in rows), "Released only on proven resources")
        else:
            require(status["state"] == admission["state"] and result["reasons"], "A held admission keeps its state and says why")
        if expected:
            require(("released" if result["released"] else "held") == expected, f"Expected {expected}")
        self.report.update({"outcome": "released" if result["released"] else "held", "admissionId": admission["id"],
                            "dispositions": [{"kind": r["kind"], "effect": r["effect"], "disposition": r["disposition"],
                                              "reasons": r["reasons"], "evidence": r.get("evidence")} for r in rows],
                            "reasons": result.get("reasons", []), "journalUnchangedByReconcile": journal is not None or None,
                            "journalChangedAfterInterrupt": None if journal is None else before != journal_after_interrupt})
        self.report["passed"] = True
        return self.report


def reconcile_existing(host, worker, artifacts, admission_id):
    """Reconcile an admission another run left held, e.g. a download whose worker crashed after completion."""
    report = {"scenario": "existing-" + admission_id, "artifactRoot": str(artifacts), "steps": [], "passed": False, "toolCalls": []}
    client = ReliabilityClient(host, worker, artifacts, timeout=900, record=report["toolCalls"])
    try:
        client.initialize()
        report["steps"].append({"name": "tia_start", "result": client.tool("tia_start")})
        result = client.tool("engineering_admission_reconcile", {"admissionId": admission_id})
        report["steps"].append({"name": "engineering_admission_reconcile", "result": result})
        evidence = read_json(artifacts / result["evidence"]["relativePath"])
        report["dispositions"] = [{"kind": r["kind"], "effect": r["effect"], "disposition": r["disposition"], "reasons": r["reasons"],
                                   "evidence": r.get("evidence")} for r in evidence["verification"]["resources"]]
        report["outcome"] = "released" if result["released"] else "held"
        report["reasons"] = result.get("reasons", [])
        client.tool("tia_detach")
        report["passed"] = True
    except Exception as error:
        report["failure"] = str(error)
    finally:
        client.close()
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=pathlib.Path, default=ROOT / "src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe")
    parser.add_argument("--base", type=pathlib.Path, required=True, help="Short new artifact base; TIA limits project path length.")
    parser.add_argument("--scenarios", nargs="*", choices=list(SCENARIOS), default=[])
    parser.add_argument("--existing", nargs=2, action="append", default=[], metavar=("ARTIFACTS", "ADMISSION_ID"))
    parser.add_argument("--report", type=pathlib.Path, required=True)
    args = parser.parse_args()
    host, worker = args.host.resolve(strict=True), args.worker.resolve(strict=True)
    args.base.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "completed": False, "base": str(args.base), "hostSha256": hashlib.sha256(host.read_bytes()).hexdigest(),
              "workerSha256": hashlib.sha256(worker.read_bytes()).hexdigest(), "cases": []}
    def save():
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    for name in args.scenarios:
        print("RUN " + name, flush=True)
        scenario = Scenario(name, host, worker, args.base)
        try:
            scenario.run(*SCENARIOS[name])
        except Exception as error:
            scenario.report["failure"] = str(error)
        report["cases"].append(scenario.report)
        save()
        print(("PASS " if scenario.report["passed"] else "FAIL ") + name + " " + str(scenario.report.get("outcome") or scenario.report.get("failure", ""))[:500], flush=True)
    for artifacts, admission_id in args.existing:
        case = reconcile_existing(host, worker, pathlib.Path(artifacts).resolve(strict=True), admission_id)
        report["cases"].append(case)
        save()
        print(("PASS " if case["passed"] else "FAIL ") + case["scenario"] + " " + str(case.get("outcome") or case.get("failure", ""))[:500], flush=True)
    report["toolCalls"] = [call for case in report["cases"] for call in case["toolCalls"]]
    report["completed"] = all(case["passed"] for case in report["cases"])
    save()
    print("Report: " + str(args.report), flush=True)
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
