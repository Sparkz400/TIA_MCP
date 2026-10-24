"""Explicit sequential force-fixture phases. Native dispatch requires --execute-native.

The author-ui phase stays alive with its original process and store handles while
root operates the exact owned window. request-capture is a separate file-only
command. No online/force action or programmatic row authoring exists here.
"""
import argparse
import copy
import ctypes
from datetime import datetime, timezone
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import threading
import time
import uuid

import psutil
from block_test_process import OwnedTiaProcessWatcher, _WindowsProcessApi
from smoke_mcp import Client
from qualify_worker_startup_exact import identity
from qualify_translation_category_probe import (StoreExclusiveWindow, WindowsFiles, safe_path,
    verify_recovery_gate, require, resource_key, overlaps)

REPO = Path(__file__).resolve().parents[1]
LANE = REPO / "artifacts/startup-unblock-20261015/force"
# These maintained helpers match the retained utility functions. Their source
# paths are included in fresh controller manifests; old admission cannot admit
# this changed controller. Importing them never constructs a native coordinator.
import qualify_testsuite_scope_probe as _scope
import qualify_vci_preservation_probe as _vci
for _name in ("HOST_SHA", "WORKER_SHA", "bind_handle_identity", "graceful_close", "validate_status", "exact_exit",
              "binary_package", "check_ref", "load", "ref", "sha", "save"):
    globals()[_name] = getattr(_scope, _name)
for _name in ("freeze_binding", "fixture_snapshot", "fixture_vci", "inspection_payload", "scan_overlays"):
    globals()[_name] = getattr(_vci, _name)
_build = REPO / "artifacts/deployment-build/b274e08b806a44f4928e429f190b153d"
HOST = _build / "published-host/TiaMcp.Host.exe"
WORKER = _build / "src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe"
FORCE_MANIFEST = LANE / "manifest.json"
FORCE_MANIFEST_SHA = "e6f7cfd75d2779ed1af33381155f374c965374a109db05c1bac51deca5590bf6"
VCI_DIR = REPO / "artifacts/startup-unblock-20261015/vci/frozen-fixture-02"
VCI_MANIFEST_SHA = "d6c7c9d8bcacc969a54b4109c86751b4c91a377edc419933aa9f636176ebf615"
VCI_BINARY_SHA = "c27ba88143558c3d6c863a95f8d49014ed50124f96e15cc16c06a1cafd1985b9"
SOURCE_WORKSPACE = REPO / "artifacts/ex_io_5f3948cf"
SOURCE_PROJECT = SOURCE_WORKSPACE / "scratch/TiaMcpScratch_20261008_123008_1d9bea18/TiaMcpScratch_20261008_123008_1d9bea18/TiaMcpScratch_20261008_123008_1d9bea18.ap21"
OWNERSHIP = LANE / "explorer-owned-creation-receipt.json"
METADATA_ORACLE = LANE / "candidate-metadata-oracle.json"
FORCE_ORACLE_FILE = REPO / "scripts/native/ForceFixtureEvidence/verify_force_capture.py"
CONTROLLER_MANIFEST = LANE / "coordinator-source-manifest-04.json"
CONTROLLER_SOURCES = tuple(REPO / "scripts" / name for name in (
    "qualify_force_capture.py", "qualify_translation_category_probe.py", "retained_native_lifecycle.py",
    "block_test_process.py", "qualify_worker_startup_exact.py", "smoke_mcp.py", "verify_watch_expansion.py",
    "verify_watch_workflow.py", "verify_translation_populated_category_probe.py")) + (
    Path(_scope.__file__), Path(_vci.__file__))
PHASES = ("checkpoint", "metadata", "review-oracle", "author-ui", "request-capture", "request-cleanup", "capture", "verify", "restore")
OFFLINE_PHASES = {"review-oracle", "request-capture", "request-cleanup", "verify"}
PREVIOUS = {"metadata": "checkpoint", "review-oracle": "metadata", "author-ui": "review-oracle",
            "capture": "author-ui", "verify": "capture", "restore": "verify"}


def utc():
    return datetime.now(timezone.utc).isoformat()


def oracle_module():
    spec = importlib.util.spec_from_file_location("force_retained_byte_oracle", FORCE_ORACLE_FILE)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def workspace_manifest(root):
    """Hash every file, including native databases and sidecars; exclude nothing."""
    root = safe_path(root)
    require(root.is_dir(), "Exact workspace must exist")
    rows = []
    for path in sorted(root.rglob("*")):
        safe_path(path)
        if path.is_file():
            before = path.stat()
            digest = sha(path)
            after = path.stat()
            require((before.st_size, before.st_mtime_ns) == (after.st_size, after.st_mtime_ns),
                    "Workspace file changed during full manifest: " + str(path))
            rows.append({"path": path.relative_to(root).as_posix(), "bytes": after.st_size, "sha256": digest})
        require(len(rows) <= 100000, "Full workspace manifest file bound exceeded")
    require(rows, "Empty workspace manifest is not evidence")
    rescanned = sorted(p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file())
    require(rescanned == sorted(row["path"] for row in rows), "Workspace file inventory changed during full manifest")
    return {"root": str(root), "files": rows, "fileCount": len(rows), "exclusions": [],
            "complete": True, "capturedAtUtc": utc(), "nativeInvoked": False}


def manifest_delta(before, after):
    require(before["complete"] is True and after["complete"] is True and not before["exclusions"] and not after["exclusions"],
            "Complete unfiltered manifests required")
    old, new = ({r["path"]: r for r in m["files"]} for m in (before, after))
    return {"added": sorted(new.keys() - old.keys()), "removed": sorted(old.keys() - new.keys()),
            "changed": [k for k in sorted(old.keys() & new.keys()) if old[k] != new[k]],
            "beforeFileCount": len(old), "afterFileCount": len(new),
            "unchangedBytes": old == new, "allChangedNativeDatabaseBytesRetainedAsHashes": True,
            "binaryManifestDeltaIsNotAnUnchangedPeerClaim": True}


def check_manifest(value):
    require(value.get("complete") is True and value.get("exclusions") == [] and value["fileCount"] == len(value["files"]),
            "Complete frozen phase manifest required")
    root = safe_path(value["root"])
    for row in value["files"]:
        path = safe_path(root / row["path"])
        require(path.is_relative_to(root), "Phase manifest path escapes its root")
        check_ref({"path": str(path), "sha256": row["sha256"], "bytes": row["bytes"]})


def unchanged_workspace_payloads(before, after):
    require([r["root"] for r in before] == [r["root"] for r in after], "VCI workspace roots changed")
    deltas = [manifest_delta(a, b) for a, b in zip(before, after)]
    require(all(delta["unchangedBytes"] is True for delta in deltas), "Existing VCI workspace package files changed")
    return deltas


def validate_reviewed_oracle(value, metadata, project=SOURCE_PROJECT):
    require(value.get("profile") == "owned-peripheral-direct-bool-force-v1" and value.get("reviewStatus") == "reviewed" and
            safe_path(value["projectPath"]) == safe_path(project), "Concrete reviewed direct-peripheral oracle required")
    require(value.get("plcName") == "ScratchPLC" and value.get("forceTableName") == metadata["force"]["name"], "Wrong PLC/table")
    languages = metadata["peers"]["languages"]["active"]
    require(1 <= len(languages) <= 3 and len(set(languages)) == len(languages) and
            sorted(value["activeLanguages"]) == sorted(languages), "Review must cover every actual active culture")
    rows = value["rows"]
    require(len(rows) == 3 and [r["kind"] for r in rows] == ["signal", "comment", "signal"], "Exact signal/comment/signal order required")
    for row in rows:
        require(set(row["comments"]) == set(languages) and all(isinstance(t, str) for t in row["comments"].values()), "Exact multilingual comments required")
    signals = value["signals"]
    require(len(signals) == 2 and {s["forceOrdinal"] for s in signals} == {0, 2}, "Exactly two distinct reviewed peripheral bindings required")
    for ordinal, io, force_value in ((0, "Input", "FALSE"), (2, "Output", "TRUE")):
        row = rows[ordinal]
        require(row.get("forceIntention") is False and row.get("displayFormat") == "Bool" and row.get("forceValue") == force_value and
                row.get("name") == "" and isinstance(row.get("monitorTrigger"), str) and row["monitorTrigger"], "Unselected populated Bool direct address required")
        binding = next(s for s in signals if s["forceOrdinal"] == ordinal)
        require(binding["ioType"] == io and binding["forceName"] == row["name"] and binding["forceAddress"] == row["address"], "Oracle I/O binding differs from row")
        byte, bit = binding["byteOffset"], binding["bitOffset"]
        require(type(byte) is int and type(bit) is int and byte >= 0 and 0 <= bit <= 7 and
                row["address"] == f"%{'I' if io == 'Input' else 'Q'}{byte}.{bit}:P", "Exact reviewed direct peripheral address spelling required")
        hardware = [h for h in metadata["peers"]["hardware"] if h["path"] == binding["hardwarePath"]]
        require(len(hardware) == 1, "Reviewed hardware owner missing/ambiguous")
        ranges = [r for r in hardware[0]["addresses"] if r["ioType"] == io and
                  r["startAddress"] == binding["rangeStartAddress"] and r["lengthBits"] == binding["rangeLengthBits"]]
        require(len(ranges) == 1 and ranges[0]["ownerCount"] > 0 and ranges[0]["allOwnersMatchPlc"] is True,
                "Native configured range lacks exact PLC ownership")
        require(0 <= (byte - ranges[0]["startAddress"]) * 8 + bit < ranges[0]["lengthBits"], "Peripheral address lies outside observed range")
    return {"reviewed": True, "activeLanguages": languages, "noTagsCreated": True, "forceIntentionRequiredFalse": True}


def observe_metadata(directory, project):
    directory = Path(directory)
    result, admission = load(directory / "result.json"), load(directory / "admission.json")
    require(not (directory / "failure.json").exists() and result.get("completed") is True and result.get("metadataOnly") is True and
            result.get("nativeProcessExited") is True and safe_path(result["projectPath"]) == safe_path(project), "Incomplete exact metadata capture")
    for key in ("nativeAuthoringInvoked", "nativeSaveInvoked", "onlineOperationInvoked", "forceOperationInvoked", "automaticReplay", "projectModified"):
        require(result.get(key) is False, "Unexpected metadata helper effect: " + key)
    independent = oracle_module()
    before, old_xml = independent.snapshot(directory, result["before"])
    after, new_xml = independent.snapshot(directory, result["after"])
    require(before["peers"] == after["peers"] and before["force"]["oracleRows"] == after["force"]["oracleRows"] and
            independent.mask_timestamp(old_xml) == independent.mask_timestamp(new_xml), "Metadata read changed peers or force bytes")
    require(admission["binarySha256"] == sha(directory / "ForceFixtureEvidence.exe"), "Metadata binary changed")
    return {"result": ref(directory / "result.json"), "projectPath": str(safe_path(project)), "processId": result["processId"],
            "nativeProcessStartTimeUtc": result["nativeProcessStartTimeUtc"], "snapshot": after,
            "forceTimestampMaskedSha256": independent.sha(independent.mask_timestamp(new_xml)),
            "completeReadOnlyMetadata": True}


def validate_restoration(before, restored):
    require(safe_path(before["projectPath"]) != safe_path(restored["projectPath"]) and
            (before["processId"], before["nativeProcessStartTimeUtc"]) != (restored["processId"], restored["nativeProcessStartTimeUtc"]),
            "Restoration requires a new destination and fresh native process")
    require(before["snapshot"]["peers"] == restored["snapshot"]["peers"] and
            before["snapshot"]["force"]["oracleRows"] == restored["snapshot"]["force"]["oracleRows"] and
            before["forceTimestampMaskedSha256"] == restored["forceTimestampMaskedSha256"], "Checkpoint restoration differs from exact native baseline")
    return {"restoredCheckpointCopyVerified": True, "freshRestoredReadbackVerified": True,
            "originalProjectOverwritten": False, "forceEditingQualified": False, "onlineForcingQualified": False,
            "restorationScope": "Exact native force table plus all collector-exported peers, hardware, languages and access; full byte manifests retained separately."}


def validate_ui_save_evidence(live, proof):
    require(proof.get("savedViaUi") is True and proof.get("allForceSelectionsUnchecked") is True and
            safe_path(proof["projectPath"]) == safe_path(SOURCE_PROJECT) and proof["processId"] == live["native"]["pid"] and
            proof["nativeProcessStartTimeUtc"] == live["nativeProcessStartTimeUtc"] and proof["oracleSha256"] == live["oracleSha256"],
            "UI save evidence is not bound to the exact current owned window and reviewed oracle")
    require(isinstance(proof.get("computerUseEvidence"), list) and proof["computerUseEvidence"], "Retained actual computer-use evidence required")
    for item in proof["computerUseEvidence"]: check_ref(item)


def checkpoint_matches_force_metadata(checkpoint_directory, metadata_directory):
    """Cross-check independently exported native bytes before UI against archive baseline."""
    baseline = load(checkpoint_directory / "after.json")
    force = load(metadata_directory / "after.json")
    independent = oracle_module()
    expected = {}
    for collection in ("blocks", "tags", "types", "watchForceTables"):
        for item in baseline[collection]:
            descriptor = item["artifact"]; check_ref(descriptor)
            key = item["kind"] + ":" + item["group"] + "/" + item["name"]
            require(key not in expected, "Duplicate checkpoint native export identity")
            expected[key] = independent.sha(independent.mask_timestamp(Path(descriptor["path"]).read_bytes()))
    actual = {item["identity"]: independent.sha(independent.mask_timestamp(independent.artifact(metadata_directory, item["artifact"])))
              for item in force["peerArtifacts"]}
    require(len(actual) == len(force["peerArtifacts"]), "Duplicate force metadata peer identity")
    actual["force:root/" + force["force"]["name"]] = independent.sha(independent.mask_timestamp(independent.artifact(metadata_directory, force["force"]["artifact"])))
    require(actual == expected, "Fresh force metadata differs from complete native checkpoint export baseline")
    observed_languages = copy.deepcopy(force["peers"]["languages"])
    observed_languages["active"] = sorted(observed_languages["active"])
    require(observed_languages == baseline["languages"], "Checkpoint/fresh metadata cultures differ")
    return {"completeExportInventoryMatched": True, "exportCount": len(actual), "timestampOnlyMasked": True,
            "checkpointBaseline": ref(checkpoint_directory / "after.json"), "metadata": ref(metadata_directory / "after.json")}


def verify_fixture_peers(baseline_directory, actual_directory, force_name, allow_target_change=False):
    baseline = fixture_snapshot(baseline_directory, "after")
    actual = fixture_snapshot(actual_directory)
    if allow_target_change:
        for packet in (baseline, actual):
            targets = [row for row in packet["watchForceTables"] if (row["kind"], row["group"], row["name"]) == ("force", "root", force_name)]
            require(len(targets) == 1, "Exact sole reviewed force table missing from full peer inventory")
            packet["watchForceTables"].remove(targets[0])
    require(baseline == actual, "Full native peer inventory changed outside the reviewed force table")
    require(fixture_vci(baseline_directory, "after") == fixture_vci(actual_directory), "VCI workspace/mapping/settings changed")
    return {"completed": True, "fullNativePeersUnchanged": True, "vciMappingsAndSettingsUnchanged": True,
            "exactForceTargetExcluded": allow_target_change, "baseline": ref(Path(baseline_directory) / "after.json"),
            "actual": ref(Path(actual_directory) / "before.json")}


class RetainedCleanupApi(_WindowsProcessApi):
    def open_process(self, access, inherit, process_id):
        # Captured at startup; never reacquire TERMINATE rights by a later PID.
        return super().open_process(access | 0x0001, inherit, process_id)

    def terminate(self, handle):
        from ctypes import wintypes
        self.kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
        self.kernel.TerminateProcess.restype = wintypes.BOOL
        require(bool(self.kernel.TerminateProcess(handle, 97)), "Explicit exact retained-handle termination failed")


class Processes:
    """Retain original birth/image-checked handles, including startup ancestry."""
    def __init__(self, anchor, folder):
        self.anchor, self.folder = anchor, folder
        self.entries, self.errors, self.forced = {}, [], False
        self.lock, self.stop = threading.Lock(), threading.Event()
        self.capture_incomplete = False
        self.thread = None
        try:
            self.retain(psutil.Process(anchor["pid"]), "anchor", [])
            self.thread = threading.Thread(target=self.monitor, daemon=True)
            self.thread.start()
        except BaseException as error:
            # Return this partial tracker so the caller keeps every handle that
            # was acquired before a capture or receipt failure.
            self.capture_incomplete = True
            self.errors.append("Startup capture failed: " + type(error).__name__ + ": " + str(error))

    def retain(self, process, role, ancestry):
        info = identity(process); key = info["pid"], info["created"]
        with self.lock:
            if key in self.entries: return self.entries[key]
            watcher = OwnedTiaProcessWatcher(info["pid"], api=RetainedCleanupApi(), query_exit_code=True)
            watcher.evidence["access"] = "SYNCHRONIZE|QUERY_LIMITED_INFORMATION|PROCESS_TERMINATE"
            watcher.evidence["terminationRequiresExplicitBoundCommand"] = True
            record = {"identity": info, "role": role, "ancestry": ancestry, "handle": watcher}
            self.entries[key] = record
            try: bind_handle_identity(watcher, info)
            except BaseException as error:
                # Keep even an unqualified original handle until terminal proof.
                # Dependent dispatch rejects the error; cleanup never reopens PID.
                self.errors.append("Original handle identity failed: " + str(error))
                self.capture_incomplete = True
            save(self.folder / f"process-{info['pid']}-{int(info['created'] * 1000000)}-start.json",
                 {k: v for k, v in record.items() if k != "handle"} | {"handleCapture": watcher.evidence})
            return record

    def all_terminal(self):
        return all(entry["handle"].api.wait(entry["handle"]._handle, 0) == 0 for entry in list(self.entries.values()))

    def capture_complete(self, expected_native, expected_worker=None):
        entries = list(self.entries.values())
        return (not self.capture_incomplete and
            sum(e["role"] == "anchor" for e in entries) == 1 and
            sum(e["role"] == "native" for e in entries) == expected_native and
            (expected_worker is None or sum(e["role"] == "worker" for e in entries) == expected_worker) and
            all(e["handle"].evidence.get("birthAndImageVerified") is True for e in entries))

    def await_terminal(self, reason, expected_native=None, expected_worker=None, missing_capture=None,
                       timeout_seconds=45, clock=time.monotonic):
        """Keep original handles and caller's store window until root cleanup/exit."""
        live_path = self.folder / "cleanup-live.json"
        seen = set(); announced = False; start = None; deadline_recorded = False
        while True:
            try:
                if start is None: start = now = clock()
                if expected_native is not None:
                    if self.capture_incomplete: missing_capture()
                    try: self.collect()
                    except BaseException:
                        self.capture_incomplete = True
                        missing_capture()
                    if not self.capture_complete(expected_native, expected_worker):
                        missing_capture()
                if self.all_terminal():
                    # Exits not yet seen happened after the previous poll.
                    for entry in list(self.entries.values()):
                        entry.setdefault("terminalDeadlineSeconds", terminal_deadline_seconds(entry, timeout_seconds))
                        entry.setdefault("observedExitSeconds", round(now - start, 1))
                    return
                now = clock()
                for entry in list(self.entries.values()):
                    entry.setdefault("terminalDeadlineSeconds", terminal_deadline_seconds(entry, timeout_seconds))
                    if "observedExitSeconds" not in entry and entry["handle"].api.wait(entry["handle"]._handle, 0) == 0:
                        entry["observedExitSeconds"] = round(now - start, 1)
                late = [e for e in self.entries.values() if "observedExitSeconds" not in e and now - start >= e["terminalDeadlineSeconds"]]
                if late and not deadline_recorded:
                    self.errors.append("Exact terminal deadline exceeded; late exit is cleanup only: " +
                                       ", ".join(sorted({e["identity"]["name"] for e in late})))
                    deadline_recorded = True
                if not announced:
                    live = {"reason": reason, "anchor": self.anchor, "coordinator": identity(psutil.Process(os.getpid())),
                        "retained": [{k: v for k, v in e.items() if k != "handle"} for e in self.entries.values()],
                        "atUtc": utc(), "sameOriginalHandlesStillHeld": True, "automaticTermination": False,
                        "commandDirectory": str(self.folder), "commandPattern": "process-cleanup-command-*.json"}
                    announced = True
                    save(live_path, live)
                    print(json.dumps({"cleanupRequired": str(live_path), "originalHandlesAndStoreWindowRetained": True}), flush=True)
                for command_path in sorted(self.folder.glob("process-cleanup-command-*.json")):
                    if command_path in seen: continue
                    seen.add(command_path)
                    try:
                        command = load(command_path)
                        require(command.get("command") == "terminate-exact-owned-handles" and command.get("liveSha256") == sha(live_path) and
                            command.get("anchor") == self.anchor and isinstance(command.get("reason"), str) and command["reason"].strip(),
                            "Explicit cleanup request is not bound to this original owned lifetime")
                        for entry in sorted(list(self.entries.values()), key=lambda e: {"native": 0, "worker": 1, "anchor": 2}[e["role"]]):
                            watcher = entry["handle"]
                            if watcher.api.wait(watcher._handle, 0) == 0: continue
                            save(self.folder / f"explicit-terminate-{entry['identity']['pid']}-{uuid.uuid4().hex}-intent.json",
                                 {"identity": entry["identity"], "command": ref(command_path), "sameOriginalHandle": True, "atUtc": utc()})
                            watcher.api.terminate(watcher._handle)
                            entry["explicitCoordinatorTermination"] = True; self.forced = True
                    except Exception as error:
                        save(command_path.with_name("cleanup-refusal-" + uuid.uuid4().hex + ".json"),
                             {"command": str(command_path), "error": str(error), "originalHandlesStillHeld": True, "atUtc": utc()})
                time.sleep(.2)
            except BaseException as error:
                # Cancellation must not escape to an enclosing StoreExclusiveWindow
                # or Host.close while any original captured process can still run.
                self.errors.append("Terminal hold interrupted: " + type(error).__name__ + ": " + str(error))
                try:
                    save(self.folder / ("cleanup-hold-interrupted-" + uuid.uuid4().hex + ".json"),
                         {"exception": type(error).__name__, "originalHandlesAndStoreWindowRetained": True,
                          "qualificationFailed": True, "automaticReplay": False, "atUtc": utc()})
                except BaseException:
                    pass

    def collect(self):
        # An owned worker remains an ancestry root after its Host exits.
        for owner in [e for e in list(self.entries.values()) if e["role"] in {"anchor", "worker"}]:
            try:
                if owner["handle"].api.wait(owner["handle"]._handle, 0) == 0: continue
                parent = psutil.Process(owner["identity"]["pid"])
                require(identity(parent) == owner["identity"], "Owned discovery identity changed")
                children = parent.children(recursive=True)
            except psutil.NoSuchProcess:
                if owner["handle"].api.wait(owner["handle"]._handle, 0) == 0: continue
                self.capture_incomplete = True
                raise
            for process in children:
                try:
                    name = process.name()
                    if name not in {"TiaMcp.Openness.V21.exe", "Siemens.Automation.Portal.exe"}: continue
                    info = identity(process)
                    existing = self.entries.get((info["pid"], info["created"]))
                    if existing is not None:
                        require(existing["identity"] == info and existing["handle"].evidence.get("birthAndImageVerified") is True,
                                "Previously captured descendant identity changed")
                        # The original ancestry was captured while its owners
                        # were alive. Never reconstruct it after their exit.
                        continue
                    chain = []
                    for parent in process.parents():
                        entry = identity(parent); chain.append(entry)
                        if entry == owner["identity"]: break
                    require(chain and chain[-1] == owner["identity"], "Owned native startup ancestry unavailable")
                    if owner["role"] == "worker": chain += owner["ancestry"]
                    self.retain(process, "native" if name == "Siemens.Automation.Portal.exe" else "worker", chain)
                except BaseException as error:
                    self.capture_incomplete = True
                    self.errors.append("Uncaptured descendant: " + type(error).__name__ + ": " + str(error))
                    raise

    def monitor(self):
        while not self.stop.wait(.05):
            try: self.collect()
            except BaseException as error:
                self.capture_incomplete = True
                self.errors.append("Startup monitor: " + type(error).__name__ + ": " + str(error)); return

    def native(self, pid):
        self.collect()
        natives = [e for e in self.entries.values() if e["role"] == "native"]
        matches = [e for e in natives if e["identity"]["pid"] == pid]
        require(len(natives) == len(matches) == 1 and not self.capture_incomplete,
                "Original sole owned native handle/ancestry not captured")
        require(not self.errors and matches[0]["handle"].evidence.get("birthAndImageVerified") is True, "Native startup handle identity unqualified")
        return matches[0]

    def finish(self, explicit_native_dispose=False):
        self.stop.set()
        if self.thread: self.thread.join(2)
        errors, exits = list(self.errors), []
        if self.thread and self.thread.is_alive(): errors.append("Ancestry monitor did not stop")
        for entry in list(self.entries.values()):
            try: proof = entry["handle"].wait_for_exit(timeout_ms=45000)
            except BaseException as error:
                proof = getattr(error, "evidence", entry["handle"].evidence); errors.append(type(error).__name__ + ": " + str(error))
            record = {k: v for k, v in entry.items() if k != "handle"} | {"proof": proof}
            exits.append(record)
            try:
                # Force helper deliberately disposes its owned SDK process; retain its
                # actual terminal code without relabeling this as normal detach.
                exact_exit(record, proof.get("exitCode") if entry.get("explicitCoordinatorTermination") or
                           entry["role"] == "native" and explicit_native_dispose else 0)
            except Exception as error: errors.append(str(error))
        value = {"completed": not errors, "exits": exits, "errors": errors, "atUtc": utc(),
                 "explicitNativeProcessDisposeInvoked": explicit_native_dispose,
                 "explicitNativeTermination": explicit_native_dispose or self.forced, "coordinatorForcedTermination": self.forced,
                 "normalLifecyclePassed": not errors and not explicit_native_dispose and not self.forced, "projectLockReleaseVerified": False}
        save(self.folder / "process-exits.json", value)
        return value


# Measured on this PC on 24 October 2026: in 16 headless runs the FileStorage server
# exited 300.0-301.7 s after the terminal wait began (a fixed idle shutdown). It gets
# that measured time plus a 60 s margin; every other owned process keeps the caller's deadline.
FILE_STORAGE_SERVER = "Siemens.Automation.ObjectFrame.FileStorage.Server.exe"
FILE_STORAGE_TERMINAL_SECONDS = 360


def terminal_deadline_seconds(entry, default_seconds):
    return max(default_seconds, FILE_STORAGE_TERMINAL_SECONDS) if entry["identity"]["name"] == FILE_STORAGE_SERVER else default_seconds


class HostSession:
    ALLOWED = {"tia_start", "tia_session_status", "project_open", "project_retrieve", "hardware_inspect", "software_inspect", "tia_detach"}
    def __init__(self, owner, folder):
        self.owner, self.folder = owner, folder
        self.client = self.processes = None
        self.pid = None; self.sequence = 0; self.failed = False
        self.native_start_attempted = False

    def call(self, name, arguments=None):
        require(name in self.ALLOWED, "Host tool outside force capture scope")
        self.sequence += 1
        prefix = self.folder / f"call-{self.sequence:02}-{name}"
        save(str(prefix) + "-intent.json", {"tool": name, "arguments": arguments or {}, "atUtc": utc(), "automaticReplay": False})
        try:
            response = self.client.request("tools/call", {"name": name, "arguments": arguments or {}})
            save(str(prefix) + "-return.json", response)
            require(response.get("isError") is not True, "Host tool failed: " + name + ": " + json.dumps(response))
            value = response.get("structuredContent")
            if value is None: value = json.loads("\n".join(c["text"] for c in response["content"] if c.get("type") == "text"))
            self.processes.collect()
            return value
        except BaseException:
            self.failed = True; raise

    def start(self, project=None):
        self.owner.frozen()
        self.owner.pre_host_admission(project or SOURCE_PROJECT, self.folder / "before-host")
        # Assignment precedes construction: reader-thread startup may fail after
        # Popen already owns a Host, and that original object must remain held.
        self.client = Client.__new__(Client); client_error = None
        try: self.client.__init__(HOST, self.owner.args.timeout, ["--worker", str(WORKER), "--artifacts", str(self.owner.root)])
        except BaseException as error: client_error = error; self.failed = True
        if getattr(self.client, "process", None) is None:
            self.owner.hold_uncaptured_startup(None, self.folder, reason="Host constructor outcome unknown without returned Popen")
        self.processes = Processes(identity(psutil.Process(self.client.process.pid)), self.folder)
        require(not self.processes.errors, "Host startup handle identity unqualified")
        if client_error is not None: raise client_error
        save(self.folder / "initialize.json", self.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
            "clientInfo": {"name": "owned-force-fixture-coordinator", "version": "1"}}))
        self.client.notify("notifications/initialized")
        self.native_start_attempted = True
        started = self.call("tia_start"); self.pid = started.get("processId")
        validate_status(started, self.pid)
        self.native = self.processes.native(self.pid)
        require(self.startup_capture_complete(), "Exact Host/worker/native startup inventory is incomplete")
        self.status(None)
        if project is not None:
            opened = self.call("project_open", {"path": str(project)})
            require(opened.get("opened") is True and opened.get("saved") is False and safe_path(opened["project"]["path"]) == safe_path(project), "Exact project open failed")
            self.status(project)

    def status(self, project):
        value = self.call("tia_session_status")
        validate_status(value, self.pid, project)
        return value

    def startup_capture_complete(self):
        if self.processes is None or not self.processes.capture_complete(int(self.native_start_attempted), 1): return False
        return self.pid is None or any(e["role"] == "native" and e["identity"]["pid"] == self.pid
            for e in self.processes.entries.values())

    def close(self):
        errors = []; receipt = None
        if self.client is not None:
            if self.processes is not None:
                try: self.processes.collect()
                except BaseException as error:
                    self.processes.capture_incomplete = True
                    self.processes.errors.append("Pre-close capture failed: " + type(error).__name__)
            if not self.startup_capture_complete():
                self.owner.hold_uncaptured_startup(getattr(self.client, "process", None), self.folder, self.processes,
                    "Owned Host startup capture incomplete before detach or EOF")
        try:
            if self.client is not None:
                if self.pid is not None and not self.failed:
                    try:
                        detached = self.call("tia_detach")
                        require(detached.get("disposedOwnedPortalConnection") is True and detached.get("attached") is False, "Owned detach failed")
                    except BaseException as error: errors.append(type(error).__name__ + ": " + str(error))
                try:
                    receipt = graceful_close(self.client)
                except BaseException as error:
                    errors.append("Host close failed: " + type(error).__name__ + ": " + str(error))
                finally:
                    save(self.folder / "host-close.json", {"receipt": receipt, "stderr": self.client.errors, "atUtc": utc()})
        finally:
            if self.processes is not None:
                self.processes.await_terminal("Owning Host close did not release all exact owned processes",
                    expected_native=int(self.native_start_attempted), expected_worker=1,
                    missing_capture=lambda: self.owner.hold_uncaptured_startup(self.client.process, self.folder,
                        self.processes, "Owned Host startup capture incomplete during terminal hold"))
        proof = self.processes.finish() if self.processes else {"completed": False, "exits": []}
        workers = [e for e in proof["exits"] if e["role"] == "worker"]
        natives = [e for e in proof["exits"] if e["role"] == "native"]
        require(not errors and proof["completed"] and not proof.get("coordinatorForcedTermination") and receipt and receipt.get("hostExitCode") == 0 and
                receipt.get("forcedHostStop") is False and receipt.get("stderrEofObserved") is True and
                receipt.get("closeTimedOut", False) is False and
                len(workers) == len(natives) == 1 and natives[0]["identity"]["pid"] == self.pid,
                "Exact Host/native cleanup incomplete")
        require(safe_path(self.processes.anchor["exe"]) == safe_path(HOST) and
                safe_path(workers[0]["identity"]["exe"]) == safe_path(WORKER) and
                workers[0]["identity"]["parentPid"] == self.processes.anchor["pid"] and
                any(parent["pid"] == workers[0]["identity"]["pid"] and parent["created"] == workers[0]["identity"]["created"]
                    for parent in natives[0]["ancestry"]), "Exact Host/worker/native startup ancestry differs")
        reported = receipt.get("workerExits", [])
        require(len(reported) == 1 and reported[0].get("processId") == workers[0]["identity"]["pid"] and
                reported[0].get("processExitVerified") is True and reported[0].get("exitCode") == 0 and
                reported[0].get("forcedTermination") is False and
                abs(datetime.fromisoformat(reported[0]["startedAtUtc"].replace("Z", "+00:00")).timestamp() - workers[0]["identity"]["created"]) <= .01,
                "Exact worker-close receipt absent or mismatched")
        return ref(self.folder / "process-exits.json")


def completed_force_reservation(intent, outcome):
    if not outcome.exists(): return False
    value = load(outcome)
    require(value.get("intentSha256") == sha(intent), "Reservation outcome binds another intent")
    for name in ("restorationResult", "populatedVerification", "authoringResult", "freshCaptureResult"):
        check_ref(value[name])
    final = load(value["restorationResult"]["path"])
    verified = load(value["populatedVerification"]["path"])
    authored = load(value["authoringResult"]["path"])
    captured = load(value["freshCaptureResult"]["path"])
    require(value.get("completed") is True and all(packet.get("completed") is True for packet in (final, verified, authored, captured)) and
            all(packet.get("planSha256") == load(intent)["planSha256"] for packet in (final, verified, authored, captured)) and
            all(final.get(key) is True for key in ("restorationVerified", "freshOriginalReadbackVerified", "fullPeerRestorationVerified")) and
            verified.get("populatedForceCaptureVerified") is True and authored.get("uiReservation") == ref(intent),
            "Reservation cannot complete before full independent populated/restored/original verification")
    for packet in (final, verified, authored, captured):
        for item in packet["evidence"]: check_ref(item)
        check_ref(packet["phaseManifest"])
        check_manifest(load(packet["phaseManifest"]["path"]))
    for name in ("finalStoreIntent", "finalStoreClose"): check_ref(value[name])
    close = load(value["finalStoreClose"]["path"])
    require(close.get("allClosed") is True, "Final reservation store handles have not all closed")
    independently_recheck_force_chain(intent, value)
    return True


def independently_recheck_force_chain(intent, outcome):
    root = safe_path(load(intent)["runRoot"])
    expected = {"restorationResult": root / "restore/result.json", "populatedVerification": root / "verify/result.json",
                "authoringResult": root / "author-ui/result.json", "freshCaptureResult": root / "capture/result.json"}
    for key, path in expected.items(): require(outcome[key] == ref(path), "Reservation chain points outside its exact phase")
    independent = oracle_module()
    authored = independent.observe(root / "author-ui/native")
    fresh = independent.observe(root / "capture/native")
    original = independent.observe(root / "restore/original-final/native")
    require(independent.compare(authored, fresh)["freshReopenVerified"] is True and
            independent.compare(fresh, original)["freshReopenVerified"] is True, "Raw populated capture chain failed reverification")
    baseline = observe_metadata(root / "metadata/native", SOURCE_PROJECT)
    restored_path = load(root / "restore/result.json")["restoredProjectPath"]
    validate_restoration(baseline, observe_metadata(root / "restore/native", restored_path))
    for phase, changed in (("metadata", False), ("author-ui", True), ("capture", True), ("restore", False), ("restore/original-final", True)):
        verify_fixture_peers(root / "checkpoint/native", root / phase / "fullpeer/native", load(METADATA_ORACLE)["forceTableName"], changed)


def scan_force_reservations(roots, keys, allowed_owned=None):
    observations = []
    for root in roots:
        folder = root / "isolated-force-reservations"
        if not folder.exists(): continue
        for intent in sorted(folder.glob("*-intent.json")):
            safe_path(intent)
            value = load(intent)
            require(value.get("kind") == "isolated-force-ui-reservation" and value.get("productionMutationAdmission") is False,
                    "Unknown isolated force reservation")
            require(value["resourceKeys"] == [resource_key(r["kind"], r["path"]) for r in value["resources"]] and
                    len(set(value["resourceKeys"])) == len(value["resourceKeys"]), "Force reservation resource bindings differ")
            outcome = intent.with_name(intent.name.replace("-intent.json", "-outcome.json"))
            completed = completed_force_reservation(intent, outcome)
            allowed = allowed_owned is not None and ref(intent) == allowed_owned
            require(completed or allowed or not any(overlaps(a, b) for a in value["resourceKeys"] for b in keys), "Unresolved force UI reservation: " + str(intent))
            observations.append({"intent": ref(intent), "completed": completed, "sameOwnedWorkflowContinuing": allowed and not completed})
    return observations


def reviewed_controller_sources(manifest):
    value = load(manifest)
    require(value.get("kind") == "reviewed-force-controller-sources" and value.get("nativeInvoked") is False,
            "Reviewed force controller source manifest required before initial dispatch")
    rows = value["files"]
    require(len(rows) == len(CONTROLLER_SOURCES) and {safe_path(row["path"]) for row in rows} == {safe_path(path) for path in CONTROLLER_SOURCES},
            "Reviewed source manifest must bind exactly every controller/dependency source")
    for row in rows: check_ref(row)
    return rows


def bind_package(controller_manifest=CONTROLLER_MANIFEST):
    source_rows = reviewed_controller_sources(controller_manifest)
    require(sha(HOST) == HOST_SHA and sha(WORKER) == WORKER_SHA, "Exact b274 binaries changed")
    require(sha(FORCE_MANIFEST) == FORCE_MANIFEST_SHA, "Frozen force package manifest changed")
    force = load(FORCE_MANIFEST)
    for item in force["admissionArtifacts"] + force["supportingFiles"]: check_ref(item)
    require(sha(VCI_DIR / "manifest.json") == VCI_MANIFEST_SHA and sha(VCI_DIR / "VciFixtureEvidence.exe") == VCI_BINARY_SHA,
            "Frozen checkpoint helper changed")
    fixture = freeze_binding(VCI_DIR / "VciFixtureEvidence.exe", VCI_DIR / "manifest.json", ["Program.cs", "VciFixtureEvidence.csproj"])
    ownership = load(OWNERSHIP)
    require(ownership.get("completed") is True and ownership["compile"]["errorCount"] == 0 and
            safe_path(ownership["project"]["path"]) == safe_path(SOURCE_PROJECT) and SOURCE_PROJECT.is_file(), "Exact Explorer ownership unavailable")
    files = force["admissionArtifacts"] + force["supportingFiles"] + fixture["files"]
    files += binary_package(HOST.parent) + binary_package(WORKER.parent) + binary_package(Path(force["binary"]["path"]).parent)
    files += [ref(FORCE_MANIFEST), ref(VCI_DIR / "manifest.json")]
    files += source_rows + [ref(controller_manifest)]
    return {"force": force, "fixture": fixture, "files": files, "reviewedControllerManifest": ref(controller_manifest)}


class Coordinator:
    def __init__(self, args, prepared):
        self.args, self.root = args, safe_path(args.root)
        if args.phase == "checkpoint":
            self.root.mkdir(parents=True, exist_ok=False)
            self.package = prepared["package"]
            save(self.root / "plan.json", prepared)
            save(self.root / "recovery-gate-verified.json", prepared["recoveryGate"])
            source = self.root / "controller-source"; source.mkdir()
            for item in self.package["files"]:
                if item["path"].endswith(".py"):
                    target = source / Path(item["path"]).name
                    if not target.exists(): shutil.copyfile(item["path"], target)
        else:
            self.package = load(self.root / "plan.json")["package"]
        self.plan_sha = sha(self.root / "plan.json")
        self.stores = sorted({safe_path(REPO / "artifacts"), safe_path(SOURCE_WORKSPACE), self.root}, key=lambda p: str(p).upper())
        self.folder = self.root / args.phase if args.phase != "request-cleanup" else self.root / "cleanup-requests" / uuid.uuid4().hex
        self.folder.mkdir(parents=args.phase == "request-cleanup", exist_ok=False)
        self.evidence = []
        self.result = {"phase": args.phase, "completed": False, "planSha256": self.plan_sha, "automaticReplay": False,
                       "nativeInvoked": args.phase not in OFFLINE_PHASES, "forceEditingQualified": False,
                       "onlineForcingQualified": False, "failingProfilesRemainDisabled": True, "releaseAccepted": False}

    def write(self, name, value):
        item = save(self.folder / (name + ".json"), value); self.evidence.append(item); return item

    def frozen(self):
        for item in self.package["files"]: check_ref(item)
        require(sha(self.root / "plan.json") == self.plan_sha, "Immutable phase plan changed")
        plan = load(self.root / "plan.json")
        require(plan["recoveryGateInput"] == ref(self.args.recovery_gate) and
                plan["recoveryGate"] == load(self.root / "recovery-gate-verified.json"), "Frozen recovery foundation changed")

    def previous(self, phase):
        result = load(self.root / phase / "result.json")
        require(result.get("completed") is True and result.get("planSha256") == self.plan_sha, "Prior phase incomplete: " + phase)
        for item in result["evidence"]: check_ref(item)
        check_ref(result["phaseManifest"])
        check_manifest(load(result["phaseManifest"]["path"]))
        return result

    def window(self, project, output, prefix, mutation=False):
        resources = [{"kind": "project", "path": str(project), "effect": "manualUiForceFixtureSave" if mutation else "readOnlyNativeProbe"},
                     {"kind": "workspace", "path": str(self.root if mutation else output), "effect": "newEvidenceFilesAndRecoveryDestination"}]
        return StoreExclusiveWindow(self.stores, project, prefix,
            mode="isolatedQualificationMutation" if mutation else "readOnlyProbe", resources=resources,
            effects="Root enters reviewed offline force rows and saves in exact owned UI; no forcing/online action" if mutation else
                    "Read-only exact owned project capture or already-saved archive; no project save/import")

    def check_windows(self, held):
        allowed = None
        if self.args.phase in {"capture", "restore"} or self.args.phase == "author-ui" and self.result.get("uiReservation"):
            allowed = self.result["uiReservation"] if self.args.phase == "author-ui" else self.previous("author-ui")["uiReservation"]
            check_ref(allowed)
            intent = load(allowed["path"])
            require(intent.get("planSha256") == self.plan_sha and safe_path(intent["runRoot"]) == self.root,
                    "Continuing reservation belongs to another workflow")
        self.write("other-reservations-" + uuid.uuid4().hex[:8], {"vci": scan_overlays(self.stores, held.keys),
            "force": scan_force_reservations(self.stores, held.keys, allowed), "sameStoreHandlesHeld": True})

    def pre_host_admission(self, project, prefix):
        # Host store APIs acquire their own locks. Check both custom overlays
        # while ours are held, close this window, then dispatch sequentially.
        with self.window(project, self.root, prefix) as held:
            self.check_windows(held)
        self.evidence += [ref(str(prefix) + "-intent.json"), ref(str(prefix) + "-close.json")]

    def hold_uncaptured_startup(self, process, folder, tracker=None, reason="Startup tracker unavailable"):
        """No parent exit can establish cleanup of an uncaptured native child."""
        announced = False
        while True:
            try:
                if not announced:
                    announced = True
                    save(folder / "uncaptured-startup.json", {"pid": process.pid if process is not None else None, "automaticTermination": False,
                        "nativeQualificationRefused": True, "originalPopenHandleRetained": process is not None,
                        "constructorOutcomeUnknown": process is None,
                        "reason": reason, "capturedOriginalHandlesRetained": tracker is not None,
                        "retained": [{k: v for k, v in e.items() if k != "handle"} for e in tracker.entries.values()] if tracker else [],
                        "parentTerminalDoesNotProveChildCleanup": True, "externalExactReconciliationRequired": True, "atUtc": utc()})
                    print(json.dumps({"uncapturedStartup": str(folder / "uncaptured-startup.json"), "storeWindowRetained": True}), flush=True)
                time.sleep(.2)
            except BaseException:
                # No automatic release path exists. Root must reconcile the
                # external exact process inventory before ending this controller.
                continue

    def run_process(self, binary, arguments, folder, own_portal=False, author=False, oracle_sha=None):
        folder.mkdir(exist_ok=False)
        tracker = None; result = {"binary": ref(binary), "arguments": arguments, "requestedAtUtc": utc()}
        with (folder / "stdout.raw").open("xb") as out, (folder / "stderr.raw").open("xb") as err:
            process = subprocess.Popen([str(binary), *arguments], cwd=REPO, stdin=subprocess.DEVNULL,
                stdout=out, stderr=err, creationflags=subprocess.CREATE_NO_WINDOW)
            try:
                tracker = Processes(identity(psutil.Process(process.pid)), folder)
                require(not tracker.errors, "Helper startup handle identity unqualified")
                result["helper"] = tracker.anchor
                deadline = time.monotonic() + (self.args.ui_timeout_minutes * 60 + self.args.timeout if author else self.args.timeout)
                ready_emitted = False
                while process.poll() is None and time.monotonic() < deadline:
                    try: tracker.collect()
                    except psutil.NoSuchProcess:
                        if process.poll() is not None: break
                        raise
                    ready_path = self.folder / "native/ui-ready.json"
                    if author and ready_path.exists() and not ready_emitted:
                        ready = load(ready_path)
                        native = tracker.native(ready["processId"])
                        require(safe_path(ready["projectPath"]) == safe_path(SOURCE_PROJECT), "UI selected a foreign project")
                        live = {"coordinator": identity(psutil.Process(os.getpid())), "helper": tracker.anchor,
                            "native": native["identity"], "nativeProcessStartTimeUtc": native["handle"].evidence["nativeProcessStartTimeUtc"],
                            "uiReady": ref(ready_path), "oracleSha256": oracle_sha, "planSha256": self.plan_sha,
                            "storeHandlesStillHeld": True, "projectPath": str(SOURCE_PROJECT), "atUtc": utc()}
                        self.write("author-live", live)
                        print(json.dumps({"uiReady": str(self.folder / "author-live.json"), "nativeProcessId": ready["processId"]}), flush=True)
                        ready_emitted = True
                    time.sleep(.1)
                result["returncode"] = process.poll()
                result["deadlineExceeded"] = result["returncode"] is None
                if result["deadlineExceeded"]:
                    save(folder / "outcome-unknown.json", {"reason": "Helper deadline exceeded", "anchor": tracker.anchor,
                        "originalHandlesAndStoreWindowRetained": True, "automaticReplay": False, "atUtc": utc()})
            finally:
                if tracker is not None:
                    # Count and qualify the expected child before any terminal
                    # handle can be closed, even if the helper already exited.
                    if not tracker.capture_complete(int(own_portal)):
                        self.hold_uncaptured_startup(process, folder, tracker, "Expected helper/native startup capture incomplete")
                    if own_portal and (self.folder / "native/launch-return.json").exists():
                        try:
                            launch = load(self.folder / "native/launch-return.json")
                            native = next(e for e in tracker.entries.values() if e["role"] == "native")
                            require(launch["processId"] == native["identity"]["pid"] and
                                launch["nativeProcessStartTimeUtc"] == native["handle"].evidence["nativeProcessStartTimeUtc"],
                                "Launch receipt identifies an uncaptured native lifetime")
                        except BaseException:
                            self.hold_uncaptured_startup(process, folder, tracker, "Native launch receipt capture mismatch")
                    tracker.await_terminal("Helper timeout or lingering owned native process; no automatic replay/termination",
                        expected_native=int(own_portal), missing_capture=lambda: self.hold_uncaptured_startup(process, folder,
                            tracker, "Expected helper/native capture incomplete during terminal hold"))
                    process.poll()
                    result["terminalReturncode"] = process.returncode
                else:
                    self.hold_uncaptured_startup(process, folder)
                result["processProof"] = tracker.finish(explicit_native_dispose=own_portal) if tracker else {"completed": False}
                result["finishedAtUtc"] = utc()
                save(folder / "result.json", result)
        require(result["returncode"] == 0 and not result.get("deadlineExceeded") and result["processProof"]["completed"] and
                not result["processProof"]["coordinatorForcedTermination"], "Helper or exact cleanup failed; retained intent is not replayable")
        natives = [e for e in result["processProof"]["exits"] if e["role"] == "native"]
        require(len(natives) == (1 if own_portal else 0), "Unexpected owned native process inventory")
        if own_portal:
            launch = load(self.folder / "native/launch-return.json")
            native = natives[0]
            require(launch["processId"] == native["identity"]["pid"] and
                    launch["nativeProcessStartTimeUtc"] == native["proof"]["nativeProcessStartTimeUtc"], "Native launch return differs from original handle birth")
        self.evidence.append(ref(folder / "result.json"))
        return result

    def checkpoint(self):
        self.write("source-workspace-before", workspace_manifest(SOURCE_WORKSPACE))
        self.write("project-before", workspace_manifest(SOURCE_PROJECT.parent))
        host = HostSession(self, self.folder)
        try:
            host.start(SOURCE_PROJECT)
            owned = self.write("current-owned-session", host.status(SOURCE_PROJECT))
            self.write("hardware-before", inspection_payload(host.call("hardware_inspect", {"maxItems": 10000})))
            output = self.folder / "native"
            with self.window(SOURCE_PROJECT, output, self.folder / "store-window") as held:
                self.check_windows(held)
                binding = self.package["fixture"]
                admission = {"mode": "checkpoint", "binarySha256": binding["binary"]["sha256"], "artifactRoot": "D:\\",
                    "outputDirectory": str(output), "projectPath": str(SOURCE_PROJECT), "plcName": "ScratchPLC", "processId": host.pid,
                    "processStartedAtUtc": host.native["handle"].evidence["nativeProcessStartTimeUtc"], "ownedScratch": True,
                    "sequentialNativeAdmission": True, "checkpointRetrievalFoundationPassed": True, "automaticReplay": False,
                    "hostBinary": ref(HOST), "workerBinary": ref(WORKER), "sourceFiles": binding["sources"],
                    "scratchReceipt": ref(OWNERSHIP), "sessionOwnershipReceipt": owned,
                    "recoveryFoundationEvidence": ref(self.root / "recovery-gate-verified.json"),
                    "storeWindowIntent": ref(self.folder / "store-window-intent.json")}
                request = self.write("admission", admission)
                self.run_process(binding["binary"]["path"], ["checkpoint", request["path"]], self.folder / "helper")
                captured = load(output / "result.json")
                require(captured.get("completed") is True and captured.get("nativeInventoryUnchanged") is True and
                        captured.get("saveInvoked") is False and captured["checkpoint"]["role"] == "current-saved" and
                        captured.get("readOnly") is True and captured.get("saved") is False and
                        captured.get("languageOrCommentSetterInvoked") is False and captured.get("watchForceMutationInvoked") is False and
                        fixture_snapshot(output, "before") == fixture_snapshot(output, "after") and
                        fixture_vci(output, "before") == fixture_vci(output, "after"), "Current saved checkpoint changed native inventory/VCI settings")
                self.result["checkpoint"] = captured["checkpoint"]
                self.evidence.append(ref(output / "result.json"))
                self.evidence.append({k: captured["checkpoint"][k] for k in ("path", "sha256", "bytes")})
            host.status(SOURCE_PROJECT)
            hardware_after = inspection_payload(host.call("hardware_inspect", {"maxItems": 10000}))
            self.write("hardware-after", hardware_after)
            require(hardware_after == load(self.folder / "hardware-before.json"), "Checkpoint changed configured hardware")
        finally:
            self.evidence.append(host.close())
        self.write("source-workspace-after", workspace_manifest(SOURCE_WORKSPACE))
        self.write("project-after", workspace_manifest(SOURCE_PROJECT.parent))
        self.write("source-workspace-delta", manifest_delta(load(self.folder / "source-workspace-before.json"), load(self.folder / "source-workspace-after.json")))
        self.write("project-delta", manifest_delta(load(self.folder / "project-before.json"), load(self.folder / "project-after.json")))

    def fullpeer_inventory(self, project, allow_target_change):
        """A fresh actual Host owns the project; attached helper reads only."""
        folder = self.folder / "fullpeer"; folder.mkdir(exist_ok=False)
        host = HostSession(self, folder)
        output = folder / "native"
        try:
            host.start(project)
            owned = save(folder / "current-owned-session.json", host.status(project))
            hardware = inspection_payload(host.call("hardware_inspect", {"maxItems": 10000}))
            save(folder / "hardware.json", hardware)
            require(hardware == load(self.root / "checkpoint/hardware-before.json"), "Fresh full peer hardware changed")
            with self.window(project, output, folder / "store-window") as held:
                self.check_windows(held)
                binding = self.package["fixture"]
                admission = {"mode": "inventory", "binarySha256": binding["binary"]["sha256"], "artifactRoot": "D:\\",
                    "outputDirectory": str(output), "projectPath": str(project), "plcName": "ScratchPLC", "processId": host.pid,
                    "processStartedAtUtc": host.native["handle"].evidence["nativeProcessStartTimeUtc"], "ownedScratch": True,
                    "sequentialNativeAdmission": True, "checkpointRetrievalFoundationPassed": True, "automaticReplay": False,
                    "hostBinary": ref(HOST), "workerBinary": ref(WORKER), "sourceFiles": binding["sources"],
                    "scratchReceipt": ref(OWNERSHIP), "sessionOwnershipReceipt": owned,
                    "recoveryFoundationEvidence": ref(self.root / "recovery-gate-verified.json"),
                    "storeWindowIntent": ref(folder / "store-window-intent.json")}
                request = save(folder / "admission.json", admission)
                self.run_process(binding["binary"]["path"], ["inventory", request["path"]], folder / "helper")
                captured = load(output / "result.json")
                require(captured.get("completed") is True and captured.get("readOnly") is True and captured.get("saved") is False and
                        captured.get("watchForceMutationInvoked") is False and captured.get("nativeImportEnabled") is False and
                        not (output / "failure.json").exists(), "Full peer inventory was incomplete or mutable")
                checked = verify_fixture_peers(self.root / "checkpoint/native", output,
                    load(METADATA_ORACLE)["forceTableName"], allow_target_change)
                self.evidence += [save(folder / "independent-peers.json", checked), ref(output / "result.json"), owned,
                                  ref(folder / "admission.json"), ref(folder / "hardware.json")]
            self.evidence += [ref(folder / "store-window-intent.json"), ref(folder / "store-window-close.json")]
            host.status(project)
        finally:
            self.evidence.append(host.close())
        return ref(folder / "independent-peers.json")

    def force_capture(self, mode, project, ownership, oracle_path):
        self.frozen()
        checkpoint = self.previous("checkpoint")["checkpoint"]
        output = self.folder / "native"
        mutation = mode == "author-ui"
        # The complete source workspace includes its operation-store lock file.
        # Capture it only while that FileShare.None handle is not held; native
        # project subdirectories are still captured inside the admitted window.
        self.write("source-workspace-before", workspace_manifest(SOURCE_WORKSPACE))
        self.write("vci-workspaces-before", self.vci_workspace_manifests())
        with self.window(project, output, self.folder / "store-window", mutation) as held:
            self.check_windows(held)
            self.write("project-workspace-before", workspace_manifest(Path(project).parent))
            resource = self.write("resource-admission", held.evidence)
            force = self.package["force"]
            admission = {"mode": mode, "ownedScratch": True, "allProjectObjectsOwned": True, "sequentialNativeAdmission": True,
                "checkpointRetrievalGatePassed": True, "projectPath": str(project), "projectFileSha256": sha(project), "artifactRoot": "D:\\",
                "outputDirectory": str(output), "binarySha256": force["binary"]["sha256"], "apiSha256": force["apiSha256"],
                "sourceFiles": force["sourceFiles"], "ownershipEvidence": ref(ownership),
                "reviewedCheckpoint": {k: checkpoint[k] for k in ("path", "sha256", "bytes")}, "oracle": ref(oracle_path),
                "resourceAdmissionEvidence": resource, "checkpointRetrievalEvidence": ref(self.root / "recovery-gate-verified.json"),
                "uiTimeoutMinutes": self.args.ui_timeout_minutes}
            request = self.write("admission", admission)
            reservation = None
            if mutation:
                reservation_dir = (REPO / "artifacts/isolated-force-reservations")
                reservation_dir.mkdir(exist_ok=True)
                reservation = reservation_dir / (uuid.uuid4().hex + "-intent.json")
                save(reservation, {"kind": "isolated-force-ui-reservation", "productionMutationAdmission": False,
                    "resources": held.evidence["resources"], "resourceKeys": held.keys, "admission": request,
                    "automaticReplay": False, "manualUiSaveAdmitted": True, "atUtc": utc(),
                    "planSha256": self.plan_sha, "runRoot": str(self.root),
                    "retainedUntilFreshCaptureAndRestoredAndOriginalReadback": True})
                self.result["uiReservation"] = ref(reservation)
            self.run_process(force["binary"]["path"], [request["path"]], self.folder / "helper", own_portal=True,
                             author=mutation, oracle_sha=admission["oracle"]["sha256"])
            require(load(output / "result.json").get("completed") is True, "Force helper incomplete")
            self.evidence.append(ref(output / "result.json"))
            self.write("project-workspace-after", workspace_manifest(Path(project).parent))
            self.write("project-workspace-delta", manifest_delta(load(self.folder / "project-workspace-before.json"), load(self.folder / "project-workspace-after.json")))
            if mode == "metadata": self.result["metadata"] = observe_metadata(output, project)
            else:
                observed = oracle_module().observe(output)
                self.write("independent-capture", observed)
                if mutation:
                    baseline = self.previous("metadata")["metadata"]["snapshot"]
                    initial, _ = oracle_module().snapshot(output, load(output / "result.json")["before"])
                    require(initial["peers"] == baseline["peers"] and
                            initial["force"]["oracleRows"] == baseline["force"]["oracleRows"] and
                            initial["force"]["semanticSha256"] == baseline["force"]["semanticSha256"],
                            "Authoring started from a different native baseline than reviewed metadata")
        self.write("source-workspace-after", workspace_manifest(SOURCE_WORKSPACE))
        vci_after = self.vci_workspace_manifests()
        self.write("vci-workspaces-after", vci_after)
        old_vci = load(self.folder / "vci-workspaces-before.json")
        deltas = unchanged_workspace_payloads(old_vci, vci_after)
        self.write("vci-workspaces-delta", deltas)
        self.write("source-workspace-delta", manifest_delta(load(self.folder / "source-workspace-before.json"), load(self.folder / "source-workspace-after.json")))
        self.evidence += [ref(self.folder / "store-window-intent.json"), ref(self.folder / "store-window-close.json")]
        self.result["fullPeerVerification"] = self.fullpeer_inventory(project, allow_target_change=mode != "metadata")
        self.write("source-workspace-after-fullpeer", workspace_manifest(SOURCE_WORKSPACE))

    def vci_workspace_manifests(self):
        state = fixture_vci(self.root / "checkpoint/native", "after")
        roots = set()
        for workspace in state["workspaces"]:
            path = safe_path(workspace["root"])
            require(path.is_relative_to(safe_path(SOURCE_WORKSPACE)) or path.is_relative_to(self.root),
                    "VCI workspace root is outside this exact owned force fixture; new admission required")
            roots.add(path)
        return [workspace_manifest(path) for path in sorted(roots, key=lambda p: str(p).upper())]

    def review_oracle(self):
        previous = self.previous("metadata")
        require(self.args.oracle is not None, "--oracle requires the independently reviewed concrete input")
        value = load(self.args.oracle)
        self.result["review"] = validate_reviewed_oracle(value, previous["metadata"]["snapshot"])
        shutil.copyfile(self.args.oracle, self.folder / "reviewed-oracle.json")
        self.evidence.append(ref(self.folder / "reviewed-oracle.json"))
        self.result["oracle"] = ref(self.folder / "reviewed-oracle.json")
        self.result["metadataEvidence"] = ref(self.root / "metadata/result.json")

    def request_capture(self):
        require(self.args.ui_save_evidence is not None, "--ui-save-evidence is required after actual UI Save")
        live = load(self.root / "author-ui/author-live.json")
        for role in ("coordinator", "helper", "native"):
            current = identity(psutil.Process(live[role]["pid"]))
            require(current == live[role], "Exact live UI process identity changed: " + role)
        require(live["planSha256"] == self.plan_sha and live["storeHandlesStillHeld"] is True, "Wrong live coordinator plan")
        proof = load(self.args.ui_save_evidence)
        validate_ui_save_evidence(live, proof)
        self.evidence.append(ref(self.args.ui_save_evidence))
        check_ref(live["uiReady"])
        command = self.root / "author-ui/native/coordinator-command.json"
        require(not command.exists(), "No command replay")
        temporary = self.root / ("capture-command-" + uuid.uuid4().hex + ".tmp")
        save(temporary, {"command": "capture-close", "oracleSha256": live["oracleSha256"]})
        temporary.rename(command)  # Windows rename refuses an existing destination.
        self.evidence.append(ref(command))
        self.result.update(nativeInvoked=False, uiSaveRequiresNativeReadbackVerification=True)

    def request_cleanup(self):
        require(self.args.cleanup_live is not None and self.args.cleanup_reason and self.args.cleanup_reason.strip(),
                "Exact --cleanup-live and explicit --cleanup-reason required")
        path = safe_path(self.args.cleanup_live)
        require(path.is_relative_to(self.root) and path.name == "cleanup-live.json", "Cleanup target must be this run's retained live receipt")
        live = load(path)
        require(live.get("sameOriginalHandlesStillHeld") is True and live.get("automaticTermination") is False and
                identity(psutil.Process(live["coordinator"]["pid"])) == live["coordinator"], "Original cleanup coordinator is no longer live")
        require(safe_path(live["commandDirectory"]) == path.parent and live["commandPattern"] == "process-cleanup-command-*.json",
                "Wrong cleanup command scope")
        command = path.parent / ("process-cleanup-command-" + uuid.uuid4().hex + ".json")
        temporary = self.folder / "command.tmp"
        save(temporary, {"command": "terminate-exact-owned-handles", "liveSha256": sha(path), "anchor": live["anchor"],
                         "reason": self.args.cleanup_reason, "requestedAtUtc": utc(), "automaticReplay": False})
        temporary.rename(command)
        self.evidence += [ref(path), ref(command)]
        self.result.update(nativeInvoked=False, cleanupRequested=True, cleanupCompleted=False,
                           qualificationAfterExplicitTerminationAllowed=False)

    def verify(self):
        self.previous("author-ui"); self.previous("capture")
        oracle = oracle_module()
        verified = oracle.compare(oracle.observe(self.root / "author-ui/native"), oracle.observe(self.root / "capture/native"))
        require(verified["freshReopenVerified"] is True, "Genuine populated capture did not survive fresh reopen")
        self.write("independent-populated-capture", verified)
        self.result["populatedForceCaptureVerified"] = True

    def restore(self):
        checkpoint = self.previous("checkpoint")["checkpoint"]
        baseline = self.previous("metadata")["metadata"]
        source_before = workspace_manifest(SOURCE_WORKSPACE)
        self.write("original-before-restoration", source_before)
        host = HostSession(self, self.folder)
        try:
            host.start()
            retrieved = host.call("project_retrieve", {"archivePath": checkpoint["path"], "expectedSha256": checkpoint["sha256"]})
            restored = safe_path(retrieved["project"]["path"])
            require(retrieved.get("retrieved") is True and retrieved["archive"]["sha256"] == checkpoint["sha256"] and
                    restored != safe_path(SOURCE_PROJECT) and restored.is_relative_to(self.root) and restored.name == SOURCE_PROJECT.name,
                    "Retrieval did not produce the exact checkpoint in a new owned destination")
            retrieval_ref = self.write("actual-host-retrieval", retrieved)
            host.status(restored)
            self.write("hardware-retrieved", inspection_payload(host.call("hardware_inspect", {"maxItems": 10000})))
        finally:
            self.evidence.append(host.close())
        require(load(self.root / "checkpoint/hardware-before.json") == load(self.folder / "hardware-retrieved.json"), "Restored Host hardware differs")
        # Derived ownership is explicitly bound to the actual retrieve result,
        # never represented as a fresh tia_create_scratch or compile receipt.
        ownership = self.write("restored-ownership", {"completed": True, "project": {"path": str(restored)},
            "provenance": "Actual owned Host retrieval to a new destination", "retrieval": retrieval_ref,
            "originalOwnership": ref(OWNERSHIP), "checkpoint": checkpoint, "originalCreationClaim": False})
        metadata_oracle = copy.deepcopy(load(METADATA_ORACLE)); metadata_oracle["projectPath"] = str(restored)
        oracle_ref = self.write("restored-metadata-oracle", metadata_oracle)
        # The owning retrieve Host/native pair has fully exited. This helper
        # creates a different native process and reopens the recovered project.
        self.force_capture("metadata", restored, Path(ownership["path"]), Path(oracle_ref["path"]))
        self.result["restoration"] = validate_restoration(baseline, self.result["metadata"])
        original_after = workspace_manifest(SOURCE_WORKSPACE)
        self.write("original-after-restoration", original_after)
        delta = manifest_delta(source_before, original_after)
        require(delta["unchangedBytes"] is True, "Original authored workspace changed during separate recovery")
        self.write("original-restoration-delta", delta)
        restored_metadata = self.result["metadata"]
        parent_folder = self.folder
        self.folder = parent_folder / "original-final"
        self.folder.mkdir()
        try:
            self.force_capture("capture", SOURCE_PROJECT, OWNERSHIP, Path(self.previous("review-oracle")["oracle"]["path"]))
            independent = oracle_module()
            fresh_original = independent.compare(independent.observe(self.root / "capture/native"), independent.observe(self.folder / "native"))
            require(fresh_original["freshReopenVerified"] is True, "Authored original changed during checkpoint recovery")
            self.write("fresh-original-verification", fresh_original)
        finally:
            self.folder = parent_folder
        self.result["metadata"] = restored_metadata
        self.result.update(restorationVerified=True, restoredProjectPath=str(restored), originalProjectOverwritten=False,
                           populatedCaptureVerifiedSeparately=True, freshOriginalReadbackVerified=True,
                           fullPeerRestorationVerified=True)

    def execute(self):
        try:
            self.frozen()
            if self.args.phase in PREVIOUS: self.previous(PREVIOUS[self.args.phase])
            self.write("phase-intent", {"phase": self.args.phase, "planSha256": self.plan_sha, "atUtc": utc(), "automaticReplay": False})
            if self.args.phase == "checkpoint": self.checkpoint()
            elif self.args.phase == "metadata":
                self.force_capture("metadata", SOURCE_PROJECT, OWNERSHIP, METADATA_ORACLE)
                self.write("checkpoint-baseline-match", checkpoint_matches_force_metadata(self.root / "checkpoint/native", self.folder / "native"))
            elif self.args.phase == "review-oracle": self.review_oracle()
            elif self.args.phase == "author-ui":
                oracle = self.previous("review-oracle")["oracle"]
                validate_reviewed_oracle(load(oracle["path"]), self.previous("metadata")["metadata"]["snapshot"])
                self.force_capture("author-ui", SOURCE_PROJECT, OWNERSHIP, Path(oracle["path"]))
            elif self.args.phase == "request-capture": self.request_capture()
            elif self.args.phase == "request-cleanup": self.request_cleanup()
            elif self.args.phase == "capture": self.force_capture("capture", SOURCE_PROJECT, OWNERSHIP, Path(self.previous("review-oracle")["oracle"]["path"]))
            elif self.args.phase == "verify": self.verify()
            elif self.args.phase == "restore": self.restore()
            self.result["completed"] = True
        except BaseException as error:
            self.result["failure"] = {"type": type(error).__name__, "message": str(error)}
        finally:
            self.result["phaseManifest"] = self.write("phase-file-manifest", workspace_manifest(self.folder))
            self.result["evidence"] = self.evidence
            save(self.folder / "result.json", self.result)
        if self.args.phase == "restore" and self.result["completed"]:
            intent = Path(self.previous("author-ui")["uiReservation"]["path"])
            outcome = intent.with_name(intent.name.replace("-intent.json", "-outcome.json"))
            value = {"completed": True, "intentSha256": sha(intent), "restorationResult": ref(self.folder / "result.json"),
                "populatedVerification": ref(self.root / "verify/result.json"), "authoringResult": ref(self.root / "author-ui/result.json"),
                "freshCaptureResult": ref(self.root / "capture/result.json"), "atUtc": utc(), "automaticReplay": False}
            prefix = self.root / "reservation-finalization"
            with StoreExclusiveWindow(self.stores, SOURCE_PROJECT, prefix, mode="readOnlyProbe",
                resources=[{"kind": "project", "path": str(SOURCE_PROJECT), "effect": "finalIndependentReadbackVerification"},
                           {"kind": "workspace", "path": str(self.root), "effect": "finalIndependentEvidenceVerification"}],
                effects="File-only final populated/restored/original independent verification; no native calls") as held:
                scan_overlays(self.stores, held.keys)
                scan_force_reservations(self.stores, held.keys, ref(intent))
                independently_recheck_force_chain(intent, value)
            value["finalStoreIntent"] = ref(str(prefix) + "-intent.json")
            value["finalStoreClose"] = ref(str(prefix) + "-close.json")
            require(load(value["finalStoreClose"]["path"]).get("allClosed") is True, "Final reservation store release incomplete")
            save(outcome, value)
            completed_force_reservation(intent, outcome)
        return self.result


def preflight(args):
    root = safe_path(args.root)
    require(root.is_relative_to(Path("D:/TIAW").resolve()) and root != Path("D:/TIAW").resolve(), "Fresh short force root must be under D:/TIAW")
    if args.phase == "checkpoint": require(not root.exists(), "Checkpoint phase requires a fresh root; no replay")
    else: require((root / "plan.json").is_file() and (args.phase == "request-cleanup" or not (root / args.phase).exists()), "Existing plan and new phase directory required")
    package = bind_package(args.controller_manifest)
    gate = verify_recovery_gate(args.recovery_gate, HOST, WORKER)
    return {"kind": "owned-force-capture-plan", "completed": True, "nativeInvoked": False, "phase": args.phase,
            "root": str(root), "sourceProject": str(SOURCE_PROJECT), "sourceWorkspace": str(SOURCE_WORKSPACE),
            "package": package, "recoveryGate": gate, "recoveryGateInput": ref(args.recovery_gate),
            "allNativeRunsSequential": True, "forceEditingQualified": False, "onlineForcingQualified": False,
            "failingProfilesRemainDisabled": True, "automaticReplay": False,
            "helperArtifactRoot": "D:\\", "artifactRootIsOnlyLexicalContainmentNotBroadWriteAuthority": True}


def parser():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--phase", choices=PHASES, default="checkpoint")
    p.add_argument("--root", type=Path, required=True)
    p.add_argument("--recovery-gate", type=Path, required=True)
    p.add_argument("--controller-manifest", type=Path, default=CONTROLLER_MANIFEST)
    mode = p.add_mutually_exclusive_group(required=True)
    mode.add_argument("--prepare-only", action="store_true")
    mode.add_argument("--execute-native", action="store_true")
    mode.add_argument("--offline", action="store_true")
    p.add_argument("--output", type=Path)
    p.add_argument("--oracle", type=Path)
    p.add_argument("--ui-save-evidence", type=Path)
    p.add_argument("--cleanup-live", type=Path)
    p.add_argument("--cleanup-reason")
    p.add_argument("--timeout", type=int, default=240)
    p.add_argument("--ui-timeout-minutes", type=int, default=30)
    return p


def main(argv=None):
    args = parser().parse_args(argv)
    require(30 <= args.timeout <= 600 and 1 <= args.ui_timeout_minutes <= 60, "Bounded native/UI deadlines required")
    require(args.prepare_only == (args.output is not None), "Use --output exactly with --prepare-only")
    require(args.prepare_only or args.offline == (args.phase in OFFLINE_PHASES), "Phase requires explicit native or file-only mode")
    prepared = preflight(args)
    if args.prepare_only:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        save(args.output, prepared)
        print(json.dumps({"nativeInvoked": False, "ready": True, "phase": args.phase, "output": str(args.output)}))
        return 0
    lock_api = lock_handle = None
    # request-capture intentionally talks only to the already live owner's files.
    # Its owner retains this lock and all original process/store handles.
    try:
        if args.phase not in {"request-capture", "request-cleanup"}:
            if args.phase == "checkpoint":
                coordinator = Coordinator(args, prepared)
            else:
                lock_api = WindowsFiles(); lock_handle = lock_api.lock(args.root / "coordinator.lock")
                coordinator = Coordinator(args, prepared)
            if lock_handle is None:
                lock_api = WindowsFiles(); lock_handle = lock_api.lock(args.root / "coordinator.lock")
        else: coordinator = Coordinator(args, prepared)
        result = coordinator.execute()
    finally:
        if lock_handle is not None: require(lock_api.close(lock_handle), "Coordinator lock handle did not close")
    print(json.dumps({"completed": result["completed"], "phase": args.phase, "failure": result.get("failure"),
                      "forceEditingQualified": False, "onlineForcingQualified": False}))
    return 0 if result["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
