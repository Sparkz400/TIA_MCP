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

from qualify_hardware_families import FAMILIES, candidates
from qualify_reliability import ReliabilityClient, decoded, require, terminate_owned_test_worker
from smoke_mcp import ROOT

TIA_EXE = pathlib.Path(r"D:\Arbeit\Siemens\Portal V21\Bin\Siemens.Automation.Portal.exe")
def probe(name="LaneEProbe"):
    return f"FUNCTION \"{name}\" : Int\n{{ S7_Optimized_Access := 'TRUE' }}\nVERSION : 0.1\nBEGIN\n    #{name} := 7;\nEND_FUNCTION\n"
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
    # MCP cancellation while the native apply runs: the Host gives up, the worker finishes its call.
    "apply-cancel": ("change_apply", "cancel", lambda j: j.get("state") == "applying", None),
    # Boundaries that must refuse before any admission: a stale preview, a concurrent change from a second Host
    # attached to the same TIA, and an apply after the owned TIA disappeared.
    "stale-concurrent": ("change_apply", "none", None, None),
    "tia-unavailable": ("change_apply", "none", None, None),
    "library-open-kill": ("library_open", "worker", None, None),
    # A completed two-checkpoint recovery, then both recovered copies reopened in a fresh Host.
    "library-restore-complete": ("change_restore", "none", None, None),
}

# Generic change_apply matrix: one fixture per reconciliation class, killed at each durable journal boundary.
# Reconciliation depends on the kind only through its fingerprint route, so each class has one representative.
# Kinds whose outcome fingerprint needs prepared in-memory targets (AdmissionReconcileRules.cs keeps the same set).
TARGET_KINDS = {"hmi_engineering", "hardware_attributes", "network_configure", "tag_set", "constant_set",
                "block_attributes_set", "project_attributes_set", "connection_set"}
HARDWARE_RULE_KINDS = {"hardware_plug", "hardware_move", "hardware_channel_attributes", "hardware_address_set", "network_io_assign", "hardware_cpu_memory_bytes"}
PROBE, HW_NAME, TO_NAME = "LaneEProbe", "TiaMcp_DI_Fixture", "TiaMcp_PID_Fixture"


def reconciliation_class(kind):
    """The reconciler route a change kind takes; scripts/test_qualify_interruption.py keeps every catalog kind mapped."""
    if kind.startswith("library_"):
        return "library"
    if kind in TARGET_KINDS or kind.startswith("to_"):
        return "target"
    return "hardware" if kind in HARDWARE_RULE_KINDS else "project"


def stages(journal):
    return journal.get("stages") or []


# point: (journal trigger, interruption mode)
POINTS = {
    "ckpt": (lambda j: j.get("state") == "checkpointing", "worker"),
    "mut": (lambda j: j.get("state") == "applying" and "mutation_applied" not in stages(j), "worker"),
    "comp": (lambda j: j.get("state") == "applying" and "mutation_applied" in stages(j) and "changed_project_saved" not in stages(j), "worker"),
    "save": (lambda j: j.get("state") == "applying" and "changed_project_saved" in stages(j), "worker"),
    "host": (lambda j: j.get("state") == "applying", "host"),
}


def expected_outcome(kind, point):
    """Exact reconciliation and the effect an independent fresh read must find in the original project."""
    if point == "ckpt":
        return {"released": False, "effectPersisted": False}  # No complete hash-recorded checkpoint.
    if point in ("mut", "comp"):
        return {"released": True, "project": "unchanged", "effectPersisted": False}  # Never saved after the checkpoint.
    if point == "save":
        return {"released": False, "effectPersisted": True}  # Saved, but no applied outcome was journaled.
    # Transport loss: the worker finishes and journals the applied outcome; target kinds cannot recompute it.
    if reconciliation_class(kind) == "target":
        return {"released": False, "effectPersisted": True}
    return {"released": True, "project": "verifiedApplied", "effectPersisted": True}


def prepare_source(scenario, client):
    return scenario.prepare(client)


def prepare_hardware(scenario, client):
    rails = [i for i in client.tool("project_inventory", {"maxItems": 2000})["hardware"]
             if i["positionNumber"] == 0 and i["path"].count("/items/") == 1]
    require(len(rails) == 1, "Owned fixture requires one central rail")
    catalog = client.tool("hardware_catalog_search", {"filter": FAMILIES["DI"]["filter"], "maxItems": 500})
    for entry in candidates(catalog, "DI"):
        prepared = decoded(client.raw_tool("change_prepare", {"kind": "hardware_plug", "payload": {
            "hardwarePath": rails[0]["path"], "typeIdentifier": entry["typeIdentifier"], "name": HW_NAME, "positionNumber": 2}}))
        if prepared.get("changeId"):
            return scenario.step("change_prepare hardware_plug", prepared)
        require(prepared.get("error", {}).get("code") == "IncompatibleHardware", f"Unexpected hardware preparation failure: {prepared}")
    raise RuntimeError("No compatible central DI module; no type identifier is guessed")


def prepare_technology(scenario, client):
    example = client.tool("engineering_read", {"selectors": {"scope": "technology", "maxItems": 2000}})["creationMetadata"]["documentedExample"]
    return scenario.step("change_prepare to_create", client.tool("change_prepare", {"kind": "to_create", "payload": {
        "name": TO_NAME, "typeIdentifier": example["typeIdentifier"], "version": example["version"]}}))


def source_present(client):
    return PROBE in {b["name"] for b in client.tool("project_inventory", {"maxItems": 1000})["blocks"]}


def hardware_present(client):
    return HW_NAME in {i["name"] for i in client.tool("project_inventory", {"maxItems": 2000})["hardware"]}


def technology_present(client):
    read = client.tool("engineering_read", {"selectors": {"scope": "technology", "maxItems": 2000}})
    return TO_NAME in {o["name"] for o in read["technologyObjects"]}


# Short scenario prefix (TIA limits project paths): kind, fixture, independent effect oracle.
KINDS = {"su": ("source_upsert", prepare_source, source_present),
         "hw": ("hardware_plug", prepare_hardware, hardware_present),
         "to": ("to_create", prepare_technology, technology_present)}
KIND_SCENARIOS = {f"{prefix}-{point}": (prefix, point) for prefix in KINDS for point in POINTS}


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

    def wait_exit(self, tia_pid, worker_timeout=600):
        """Let the worker and its owned TIA exit on their own; terminate only the exact processes that do not."""
        worker_exited = tia_exited = True
        try:
            wait_until(lambda: not alive(self.worker_pid), "Worker did not exit", worker_timeout, 0.5)
        except TimeoutError:
            worker_exited = False
            terminate_owned_test_worker(self.worker_pid, self.worker)
        try:
            wait_until(lambda: not alive(tia_pid), "Owned TIA did not exit after its worker", 120, 0.5)
        except TimeoutError:
            tia_exited = False
            terminate_owned_test_worker(tia_pid, TIA_EXE)
        return {"workerPid": self.worker_pid, "workerExitedAlone": worker_exited, "tiaPid": tia_pid, "tiaExitedWithWorker": tia_exited}

    def interrupt(self, client, tia_pid, mode, request=None):
        """Terminate exactly this Host's worker (and its owned TIA), or exactly this Host, or cancel the MCP request."""
        workers = [self.worker_pid]
        if mode == "cancel":
            client.notify("notifications/cancelled", {"requestId": request, "reason": "Qualification cancellation during apply"})
            following = decoded(client.raw_tool("tia_capabilities")).get("error", {}).get("code")
            require(following == "BridgeFaulted", "After cancellation the bridge must refuse further calls")
            client.close()
            return {"terminated": None, "cancelled": True, "followingCallError": following, **self.wait_exit(tia_pid)}
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

    def run_kind(self, prefix, point):
        """Interrupt one change kind at one journal boundary, reconcile once, then check the exact outcome,
        no replay and recovery from the checkpoint against independent fresh reads."""
        kind, prepare, present = KINDS[prefix]
        trigger, mode = POINTS[point]
        expected = self.report["expected"] = expected_outcome(kind, point)
        self.report.update({"kind": kind, "point": point, "reconciliationClass": reconciliation_class(kind)})
        client = self.client()
        try:
            client.initialize()
            tia = self.start(client)
            self.step("tia_create_scratch", client.tool("tia_create_scratch"))
            require(not present(client), "The fixture effect must be absent before the change")
            prepared = prepare(self, client)
            project = pathlib.Path(prepared["preview"]["project"]["path"])
            journal = pathlib.Path(prepared["journalPath"])
            request = client.begin("tools/call", {"name": "change_apply", "arguments": self.apply_arguments(prepared)})

            def reached():
                current = read_json(journal) or {}
                require(current.get("state") not in ("applied", "recovery_required"),
                        "The apply ended before the interruption boundary: " + str(current.get("state")))
                return trigger(current)
            wait_until(reached, "Journal never reached the interruption boundary", 900, 0.005)
            reconciled = self.finish(client, request, tia, "change_apply", mode, journal, "released" if expected["released"] else "held")
        finally:
            client.close()
        self.report["passed"] = False  # finish() checked reconciliation only; recovery and the oracle follow.
        rows = {r["effect"]: r for r in reconciled["dispositions"]}
        if "project" in expected:
            require(rows["checkpointMutationCompileSave"]["disposition"] == expected["project"], f"Expected project {expected['project']}")
        final = read_json(journal)
        archive, archive_hash = final.get("recoveryArchive"), final.get("recoveryArchiveSha256")
        every = admissions(self.root, "change_apply")
        unresolved = [a["id"] for a in every if a["state"] in ("dispatchIntent", "outcomeUnknown")]
        self.step("admissions after reconcile", {"unresolved": unresolved, "total": len(every)})
        require(len(every) == 1, "Reconciliation must not dispatch another apply")
        require(len(unresolved) == (0 if expected["released"] else 1), "A held admission stays unresolved; a released one is reconciled")
        # Recovery from the checkpoint: retrieval of the hash-recorded archive into a new directory. A held
        # admission keeps reserving its checkpoint, so retrieval is refused until a human decides.
        recovery = self.client()
        try:
            recovery.initialize()
            self.start(recovery, "recovery tia_start")
            if archive_hash:
                retrieved = decoded(recovery.raw_tool("project_retrieve", {"archivePath": archive, "expectedSha256": archive_hash}))
                self.step("project_retrieve checkpoint", retrieved)
                if expected["released"]:
                    require("error" not in retrieved, "A released admission must allow checkpoint recovery")
                    require(not present(recovery), "The recovered checkpoint must not contain the change")
                    recovery.tool("tia_detach")
                    recovery.close()
                    recovery = self.client()
                    recovery.initialize()
                    self.start(recovery, "original tia_start")
                else:
                    require(retrieved.get("error", {}).get("code") == "ResourceBusy", "A held admission must keep its checkpoint reserved")
            else:
                require(point == "ckpt", "Only a checkpoint-stage kill may leave no recorded archive")
            self.step("project_open original", recovery.tool("project_open", {"path": str(project)}))
            persisted = self.step("effect persisted in original", present(recovery))
            recovery.tool("tia_detach")
        finally:
            recovery.close()
        require(persisted == expected["effectPersisted"], f"Independent read found effectPersisted={persisted}")
        self.report.update({"effectPersisted": persisted, "checkpointRecovered": bool(archive_hash) and expected["released"],
                            "journalStagesAtEnd": stages(final), "journalStateAtEnd": final.get("state")})
        self.report["passed"] = True
        return self.report

    def run(self, operation, mode, trigger, expected):
        if self.name.startswith("library-"):
            return self.run_library(operation, mode, trigger, expected)
        if self.name == "stale-concurrent":
            return self.run_stale_concurrent()
        if self.name == "tia-unavailable":
            return self.run_tia_unavailable()
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
            if operation == "library_open":
                return self.run_library_open(client, tia)
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
                library_path = next(c["originalPath"] for c in applied["journal"]["checkpoints"] if c["kind"] == "library")
                client.tool("tia_detach")
                client.close()
                client = self.client()
                client.initialize()
                tia = self.start(client, "tia_start (empty owned instance)")
                if mode == "none":
                    return self.complete_library_restore(client, prepared, library_path, block["name"])
                request = client.begin("tools/call", {"name": "change_restore", "arguments": {
                    "changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}})
            wait_until(lambda: trigger(read_json(journal) or {}), "Journal never reached the interruption boundary", 900)
            return self.finish(client, request, tia, operation, mode, journal, expected)
        finally:
            client.close()

    def run_library_open(self, client, tia):
        created = self.step("library_create", client.tool("change_apply", self.apply_arguments(
            client.tool("change_prepare", {"kind": "library_create", "payload": {"schemaVersion": 1, "name": "LaneEOpen"}}))))
        library_id = created["journal"]["result"]["library"]["libraryId"]
        revision = next(x["revision"] for x in client.tool("library_list")["libraries"] if x["libraryId"] == library_id)
        archived = self.step("library_archive", client.tool("change_apply", self.apply_arguments(client.tool("change_prepare", {
            "kind": "library_archive", "payload": {"schemaVersion": 1, "source": {"libraryId": library_id, "revision": revision}}}))))
        archive = archived["journal"]["result"]["archive"]
        request = client.begin("tools/call", {"name": "library_open", "arguments": {"request": {"path": archive["path"], "expectedSha256": archive["sha256"]}}})
        wait_until(lambda: any((self.root / "libraries").glob("l_*/open-receipt.json")), "library_open never wrote its receipt", 600)
        return self.finish(client, request, tia, "library_open", "worker", None, None)

    def complete_library_restore(self, client, prepared, library_path, block_name):
        restored = self.step("change_restore (complete)", client.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
        resources = restored["resources"]
        require(restored.get("originalsPreserved") is True and len(resources) == 2 and all(r["state"] == "completed" for r in resources),
                "Both checkpoints must be recovered into new locations")
        client.tool("tia_detach")
        client.close()
        pending = [a for a in admissions(self.root, "change_restore") if a["state"] in ("dispatchIntent", "outcomeUnknown")]
        require(not pending, "A completed recovery leaves no unresolved admission")
        fresh = self.client()
        try:
            fresh.initialize()
            self.start(fresh, "fresh tia_start")
            project = next(r for r in resources if r["kind"] == "project")["project"]["path"]
            self.step("project_open recovered", fresh.tool("project_open", {"path": project}))
            names = {b["name"] for b in fresh.tool("project_inventory", {"maxItems": 1000})["blocks"]}
            require(block_name in names, "Recovered project lost its fixture block")
            destination = pathlib.Path(next(r for r in resources if r["kind"] == "library")["destination"])
            markers = list(destination.rglob("*.al21"))
            require(len(markers) == 1, "Recovered library directory must hold exactly one library")

            def masters(path):
                opened = fresh.tool("library_open", {"request": {"path": str(path), "expectedSha256": hashlib.sha256(path.read_bytes()).hexdigest()}})
                items = fresh.tool("library_query", {"request": {"libraryId": opened["libraryId"]}})["items"]
                return sorted(x["name"] for x in items if x.get("kind") == "masterCopy")
            recovered_masters, original_masters = masters(markers[0]), masters(pathlib.Path(library_path))
            self.step("library_open recovered and original", {"recovered": recovered_masters, "original": original_masters})
            require(block_name in original_masters and block_name not in recovered_masters,
                    "The recovered library must equal its pre-change checkpoint while the original keeps the applied master copy")
            fresh.tool("tia_detach")
        finally:
            fresh.close()
        self.report.update({"outcome": "recovered", "recoveredProject": project, "recoveredLibrary": str(markers[0]),
                            "recoveredMasters": recovered_masters, "originalMasters": original_masters})
        self.report["passed"] = True
        return self.report

    def run_stale_concurrent(self):
        owner = self.client()
        try:
            owner.initialize()
            tia = self.start(owner)
            self.step("tia_create_scratch", owner.tool("tia_create_scratch"))
            stale = self.prepare(owner, "LaneEStaleX")
            observer = self.client()
            try:
                observer.initialize()
                self.step("second Host tia_attach", observer.tool("tia_attach", {"processId": tia}))
                concurrent = self.step("second Host change_apply", observer.tool("change_apply", self.apply_arguments(self.prepare(observer, "LaneEConcurrentY"))))
                require(concurrent["state"] == "applied", "Concurrent change did not apply")
                observer.tool("tia_detach")
            finally:
                observer.close()
            refused = decoded(owner.raw_tool("change_apply", self.apply_arguments(stale)))
            self.step("apply after concurrent change", refused)
            later = self.prepare(owner, "LaneEStaleW")
            self.step("change_apply other preview", owner.tool("change_apply", self.apply_arguments(self.prepare(owner, "LaneEStaleV"))))
            refused_same = decoded(owner.raw_tool("change_apply", self.apply_arguments(later)))
            self.step("apply stale preview", refused_same)
            codes = [r.get("error", {}).get("code") for r in (refused, refused_same)]
            require(all(c in ("StaleRevision", "StaleMutationScope", "PreviewStale") for c in codes), f"Stale applies must be refused: {codes}")
            names = {b["name"] for b in owner.tool("project_inventory", {"maxItems": 1000})["blocks"]}
            require({"LaneEConcurrentY", "LaneEStaleV"} <= names and not {"LaneEStaleX", "LaneEStaleW"} & names, "Only the fresh changes may be present")
            journals = [read_json(pathlib.Path(p["journalPath"])) for p in (stale, later)]
            require(all(j["state"] == "prepared" and not j.get("idempotencyKey") for j in journals), "Refused previews must stay prepared and unclaimed")
            unresolved = [a for a in admissions(self.root, "change_apply") if a["state"] in ("dispatchIntent", "outcomeUnknown")]
            require(not unresolved, "Refusal before dispatch must not leave an admission")
            owner.tool("tia_detach")
        finally:
            owner.close()
        self.report.update({"outcome": "refused", "codes": codes, "blocks": sorted(names)})
        self.report["passed"] = True
        return self.report

    def run_tia_unavailable(self):
        client = self.client()
        try:
            client.initialize()
            tia = self.start(client)
            self.step("tia_create_scratch", client.tool("tia_create_scratch"))
            prepared = self.prepare(client)
            terminate_owned_test_worker(tia, TIA_EXE)
            self.step("owned TIA terminated while idle", {"tiaPid": tia})
            refused = decoded(client.raw_tool("change_apply", self.apply_arguments(prepared)))
            self.step("apply without TIA", refused)
            require("error" in refused, "An apply without its TIA must fail")
            journal = read_json(pathlib.Path(prepared["journalPath"]))
            require(journal["state"] == "prepared" and not journal.get("idempotencyKey"), "No apply may be claimed without TIA")
            unresolved = [a for a in admissions(self.root, "change_apply") if a["state"] in ("dispatchIntent", "outcomeUnknown")]
        finally:
            client.close()
        fresh = self.client()
        try:
            fresh.initialize()
            self.start(fresh, "fresh tia_start")
            fresh.tool("project_open", {"path": prepared["preview"]["project"]["path"]})
            names = {b["name"] for b in fresh.tool("project_inventory", {"maxItems": 1000})["blocks"]}
            require("LaneEProbe" not in names, "The project must be unchanged")
            fresh.tool("tia_detach")
        finally:
            fresh.close()
        self.report.update({"outcome": "refused", "code": refused["error"].get("code"), "unresolvedAdmissions": [a["id"] for a in unresolved]})
        require(not unresolved, "Refusal before dispatch must not leave an admission")
        self.report["passed"] = True
        return self.report

    def prepare(self, client, name="LaneEProbe"):
        return self.step("change_prepare " + name, client.tool("change_prepare", {"kind": "source_upsert", "payload": {
            "language": "SCL", "source": probe(name), "expectedBlockNames": [name]}}))

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
        self.step("interrupt", {"journalAtInterrupt": read_json(journal) if journal else None, **self.interrupt(client, tia, mode, request)})
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
    parser.add_argument("--scenarios", nargs="*", choices=list(SCENARIOS) + list(KIND_SCENARIOS), default=[])
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
            if name in KIND_SCENARIOS:
                scenario.run_kind(*KIND_SCENARIOS[name])
            else:
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
