"""Durable, fail-closed native coordinator admission.

The registry is shared by this user's StoreExclusiveWindow callers, across
source checkouts. It is
not a system-wide TIA policy or a production adapter security boundary.
An unresolved ticket survives its coordinator and its store lock.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import uuid

DEFAULT_ROOT = Path(os.environ.get("LOCALAPPDATA", str(Path.home() / ".local/state"))) / "TiaMcp/native-dispatch-guard"


def require(value, message):
    if not value:
        raise ValueError(message)


def reference(path):
    path = Path(path).resolve(strict=True)
    return {"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def write(path, value):
    with Path(path).open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, allow_nan=False)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    return reference(path)


def read(path):
    path = Path(path)
    require(path.is_file() and not path.is_symlink() and path.stat().st_nlink == 1
            and path.stat().st_size <= 1024 * 1024, "Invalid native dispatch evidence: " + str(path))
    return json.loads(path.read_text(encoding="utf-8-sig"))


def scan(root, *, isolation=None):
    """Must be called while the registry dispatch.lock is held."""
    root = Path(root)
    if isolation is not None:
        from native_dispatch_isolation import RebootIsolation
        require(type(isolation) is RebootIsolation, "Exact fixture reboot isolation policy required")
    tickets = sorted(p for p in root.iterdir() if p.name != "dispatch.lock")
    require(len(tickets) <= 4096, "Native dispatch registry bound exceeded")
    observations = []
    for folder in tickets:
        require(folder.is_dir() and not folder.is_symlink()
                and not (hasattr(folder, "is_junction") and folder.is_junction())
                and re.fullmatch(r"[a-f0-9]{32}", folder.name), "Invalid native dispatch ticket")
        intent_path = folder / "intent.json"
        intent = read(intent_path)
        require(intent.get("schemaVersion") == "native-dispatch-intent.v1"
                and intent.get("id") == folder.name
                and intent.get("automaticReplay") is False, "Invalid native dispatch intent")
        require(set(p.name for p in folder.iterdir()) <= {"intent.json", "terminal.json"},
                "Unknown native dispatch ticket evidence")
        terminal_path = folder / "terminal.json"
        if not terminal_path.exists() and isolation is not None:
            # The explicit fixture-only exception retains the unresolved ticket.
            # It cannot supply old cleanup proof or admit a different ticket.
            observations.append(isolation.admit(intent_path))
            continue
        require(terminal_path.exists(), "Unresolved native dispatch quarantine: " + str(intent_path))
        require(intent.get("containmentRequiredFromCreation") is True
                and intent.get("captureIncomplete") is not True
                and intent.get("completeOwnedTreeExitVerified") is not False,
                "Legacy uncontained lifetime cannot be reconciled by a new job: " + str(intent_path))
        terminal = read(terminal_path)
        require(terminal.get("schemaVersion") == "native-dispatch-terminal.v1"
                and terminal.get("intent") == reference(intent_path)
                and terminal.get("jobEvidence") == reference(terminal.get("jobEvidence", {}).get("path", ""))
                and terminal.get("originalAttemptPromoted") is False, "Invalid native terminal receipt")
        proof = read(terminal["jobEvidence"]["path"])
        launched = (proof.get("createdAtomicallyInJob") is True
                    and proof.get("helperHandleSignaled") is True and proof.get("totalProcesses", 0) >= 1)
        never_created = (proof.get("launchNeverCreated") is True
                         and proof.get("createdAtomicallyInJob") is False
                         and proof.get("helperHandleSignaled") is False and proof.get("totalProcesses") == 0)
        require(proof.get("schemaVersion") == "owned-job-terminal.v1"
                and proof.get("jobName") == intent.get("jobName")
                and (launched or never_created) and proof.get("activeProcesses") == 0
                and proof.get("limitFlags") == 0
                and proof.get("automaticTermination") is False,
                "Native terminal receipt lacks exact retained job/helper proof")
        observations.append({"intent": reference(intent_path), "terminal": reference(terminal_path)})
    return observations


def begin(root, *, job_name, source, legacy_uncontained=False):
    """Caller holds the registry lock through terminal proof or durable failure."""
    require(isinstance(job_name, str) and job_name.startswith("Local\\TiaMcp-Owned-"),
            "Exact owned job name required")
    folder = Path(root) / uuid.uuid4().hex
    folder.mkdir()
    write(folder / "intent.json", {
        "schemaVersion": "native-dispatch-intent.v1", "id": folder.name,
        "jobName": job_name, "source": reference(source), "automaticReplay": False,
        "containmentRequiredFromCreation": not legacy_uncontained,
    })
    return folder


def complete(folder, job_evidence):
    folder = Path(folder)
    require(read(folder / "intent.json").get("containmentRequiredFromCreation") is True,
            "Legacy uncontained lifetime cannot be completed by job evidence")
    return write(folder / "terminal.json", {
        "schemaVersion": "native-dispatch-terminal.v1",
        "intent": reference(folder / "intent.json"), "jobEvidence": reference(job_evidence),
        "originalAttemptPromoted": False,
    })
