"""Qualify real SSH MCP reconnect/recovery on a newly created remote scratch only.

The config must use strict noninteractive OpenSSH. No credential material is read
or recorded. Optional --interrupt kills only this runner's local SSH process after
an independent SSH connection observes the pending write's durable checkpoint.
Writes are never retried. Unknown outcomes are reconciled from the saved journal.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path, PureWindowsPath
import threading
import time
import uuid
import xml.etree.ElementTree as ET

from qualify_deployment import McpClient, ProtocolFailure, ToolBlocked, initialize, load_configuration, require

ROOT = Path(__file__).resolve().parents[1]


def remote_path(path, root):
    value, base = PureWindowsPath(path), PureWindowsPath(root)
    require(value.is_absolute() and base.is_absolute() and len(base.parts) >= 2, "Explicit absolute remote artifact paths are required")
    require(".." not in value.parts and ".." not in base.parts and value != base and value.is_relative_to(base), "Remote artifact escaped the configured root")
    return value


def owned_status(status, pid, project=None, owner=False):
    require(status.get("attached") is True and status.get("processId") == pid and isinstance(pid, int) and pid > 0, "Session is not this runner's exact owned TIA instance")
    require(not owner or status.get("ownsPortalProcess") is True, "New primary session must own the instance")
    projects = status.get("projects", [])
    if project is None:
        require(projects == [], "New owned session is not empty")
    else:
        require(len(projects) == 1 and PureWindowsPath(projects[0]["path"]) == PureWindowsPath(project), "Remote project identity changed")


def semantic_xml(data):
    root = ET.fromstring(data)
    for child in list(root):
        if child.tag.rsplit("}", 1)[-1] == "DocumentInfo":
            root.remove(child)
    return hashlib.sha256(ET.tostring(root, encoding="utf-8")).hexdigest()


def fetch(client, path, remote_root, expected_hash=None, maximum=32 * 1024 * 1024):
    remote_path(path, remote_root)
    offset, contents, signature, length = 0, bytearray(), None, None
    while True:
        piece = client.tool("artifact_read", {"path": path, "offset": offset, "count": 65536})
        require(piece.get("encoding") == "base64" and piece.get("offset") == offset, "Artifact offset/encoding mismatch")
        data = base64.b64decode(piece.get("data", ""), validate=True)
        require(len(data) <= 65536 and piece.get("count") == len(data) and piece.get("nextOffset") == offset + len(data), "Artifact transfer did not preserve byte boundaries")
        if signature is None:
            signature, length = piece.get("sha256"), piece.get("bytes")
            require(isinstance(length, int) and 0 <= length <= maximum, "Artifact exceeds its qualification bound")
        require(piece.get("sha256") == signature and piece.get("bytes") == length, "Artifact changed between read chunks")
        contents.extend(data)
        offset += len(data)
        if piece.get("complete") is True:
            require(offset == length and hashlib.sha256(contents).hexdigest() == signature, "Artifact hash/length mismatch")
            require(expected_hash is None or signature == expected_hash, "Artifact differs from immutable checkpoint/export hash")
            return bytes(contents), {"remotePath": path, "bytes": length, "sha256": signature}
        require(data and offset < length, "Artifact transfer made no progress")


def journal_identity(journal, change_id, project, key=None):
    require(journal.get("changeId") == change_id and PureWindowsPath(journal.get("projectPath", "")) == PureWindowsPath(project), "Journal belongs to another change or project")
    if key is not None:
        require(journal.get("idempotencyKey") == key, "Journal idempotency identity differs from dispatched request")


def checkpoint(journal, remote_root):
    path = journal.get("recoveryArchive")
    signature = journal.get("recoveryArchiveSha256")
    require(isinstance(path, str) and remote_path(path, remote_root).suffix.lower() == ".zap21", "No valid persisted checkpoint archive")
    require(isinstance(signature, str) and len(signature) == 64 and all(c in "0123456789abcdefABCDEF" for c in signature), "No valid persisted checkpoint hash")
    require("recovery_archive_created" in journal.get("stages", []), "Journal has not durably recorded checkpoint creation")
    return path, signature.lower()


def transient_journal_read(error):
    # Only an explicitly unavailable path permits another bounded read. A path
    # denial, access failure or another engineering error is never ignored.
    return isinstance(error, ToolBlocked) and error.response.get("error", {}).get("code") == "ArtifactUnavailable"


def verify_launcher_shutdown(result, interrupted=False):
    require(result.get("forcedLauncherStop") is False, "SSH launcher did not finish within the shutdown bound")
    require(interrupted or result.get("exitCode") == 0, "Uninterrupted SSH launcher exited unsuccessfully")


def fault_source(token, count):
    require(1 <= count <= 100 and len(token) == 8 and all(c in "0123456789abcdef" for c in token), "Invalid bounded fault fixture")
    names = [f"TiaMcp_SshFault_{token}_{index:03}" for index in range(count)]
    source = "\n\n".join(f'FUNCTION "{name}" : Bool\nVERSION : 0.1\nVAR_INPUT\n Enable : Bool;\nEND_VAR\nBEGIN\n #{name} := #Enable;\nEND_FUNCTION' for name in names) + "\n"
    return names, source


def verify_fixture_absent(inventory, names, project):
    completeness = inventory.get("completeness", {})
    require(completeness.get("scopeComplete") is True and completeness.get("truncated") is False,
            "Incomplete block scope cannot prove fixture absence")
    require("program blocks" in completeness.get("scope", "").lower() and inventory.get("warnings") == [],
            "Inventory does not provide warning-free exposed program-block coverage")
    require(PureWindowsPath(inventory.get("project", {}).get("path", "")) == PureWindowsPath(project),
            "Absence evidence belongs to another recovered project")
    blocks = inventory.get("blocks")
    require(isinstance(blocks, list) and all(isinstance(block, dict) and isinstance(block.get("name"), str) for block in blocks),
            "Inventory omitted the block collection")
    observed = {block["name"] for block in blocks}
    require("TiaMcp_IsEnabled" in observed, "Recovered baseline block is missing from the scoped inventory")
    require(not observed.intersection(names), "Checkpoint incorrectly includes post-checkpoint fault fixture")
    return {"scopeComplete": True, "truncated": False, "wholeProjectComplete": completeness.get("complete"),
            "scope": completeness["scope"], "generatedNamesAbsent": len(names)}


def run(args):
    configuration = load_configuration(args.config, args.server)
    require(configuration["transport"] == "ssh", "This qualification requires an actual strict SSH configuration")
    remote_path(str(PureWindowsPath(args.remote_artifact_root) / "ownership-check"), args.remote_artifact_root)
    local = args.output.resolve() / ("ssh_recovery_" + uuid.uuid4().hex[:8])
    local.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "completed": False, "configurationSha256": configuration["configurationSha256"], "transport": "ssh",
              "serverName": args.server, "remoteArtifactRoot": args.remote_artifact_root, "localEvidenceDirectory": str(local), "steps": [],
              "mutationDispatches": [], "interruption": {"requested": args.interrupt, "status": "not_requested" if not args.interrupt else "not_exercised"},
              "limitations": ["No simulator or physical PLC calls", "No arbitrary remote process termination", "A durable journal is the last recorded observation, not proof of unrecorded vendor internals"]}
    clients = []
    primary = observer = None
    active_thread = active_client = None
    pid = None
    project = None

    def save():
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    def note(label, value):
        report["steps"].append({"name": label, "passed": True, "result": value})
        save()
        print("PASS " + label, flush=True)
        return value

    def connect(label):
        client = McpClient(configuration, args.timeout)
        clients.append({"client": client, "label": label, "closed": False, "attached": False})
        initialize(client)
        note(label + " initialize", {"launcherProcessId": client.process.pid, "transport": "ssh"})
        return client

    def state(client):
        return next(row for row in clients if row["client"] is client)

    def call(client, name, arguments=None, label=None):
        record = {"name": label or name, "tool": name, "arguments": arguments or {}, "passed": False}
        report["steps"].append(record)
        save()
        started = time.monotonic()
        try:
            value = client.tool(name, arguments)
            if name in {"tia_start", "tia_attach"}:
                state(client)["attached"] = True
            elif name == "tia_detach":
                state(client)["attached"] = False
            record["result"], record["passed"] = value, True
            return value
        except Exception as error:
            record["failure"] = str(error)
            raise
        finally:
            record["elapsedSeconds"] = round(time.monotonic() - started, 6)
            save()
            print(("PASS " if record["passed"] else "FAIL ") + record["name"], flush=True)

    def disconnect(client, label):
        if state(client)["attached"] and not client.terminal:
            call(client, "tia_detach", label=label + " detach")
        interrupted = client.terminal
        value = client.close()
        state(client)["closed"] = True
        note(label + " SSH closed", value)
        verify_launcher_shutdown(value, interrupted)

    def archive_copy(client, path, signature, filename):
        data, evidence = fetch(client, path, args.remote_artifact_root, signature)
        target = local / filename
        with target.open("xb") as output:
            output.write(data)
        return note("immutable backup " + filename, {**evidence, "localPath": str(target)})

    def export(client, name, filename):
        exported = call(client, "block_read", {"blockName": name}, "export " + name)
        data, evidence = fetch(client, exported["artifact"]["path"], args.remote_artifact_root, exported["artifact"]["sha256"])
        target = local / filename
        with target.open("xb") as output:
            output.write(data)
        return note("transfer " + filename, {**evidence, "localPath": str(target), "semanticSha256": semantic_xml(data)})

    def assert_primary(client, selected_project, selected_pid, owned=True):
        status = call(client, "tia_session_status", label="verify exact remote fixture ownership")
        owned_status(status, selected_pid, selected_project, owner=owned)
        remote_path(selected_project, args.remote_artifact_root)
        require(PureWindowsPath(selected_project).stem.startswith("TiaMcpScratch_"), "Mutation target is not a scratch fixture")

    def prepare(client, kind, payload, selected_project, selected_pid):
        assert_primary(client, selected_project, selected_pid)
        prepared = call(client, "change_prepare", {"kind": kind, "payload": payload, "projectName": PureWindowsPath(selected_project).stem})
        require(PureWindowsPath(prepared["preview"]["project"]["path"]) == PureWindowsPath(selected_project), "Preview targets another project")
        remote_path(prepared["journalPath"], args.remote_artifact_root)
        return prepared

    def apply_arguments(prepared):
        return {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "idempotencyKey": uuid.uuid4().hex,
                "checkpointSave": True, "compile": True, "save": True}

    def dispatch_record(arguments):
        require(not any(item["changeId"] == arguments["changeId"] for item in report["mutationDispatches"]), "Runner refuses to replay a mutation")
        report["mutationDispatches"].append({"changeId": arguments["changeId"], "idempotencyKey": arguments["idempotencyKey"], "dispatchCount": 1})
        save()

    def new_owned(client, label):
        started = call(client, "tia_start", label=label)
        owned_status(started, started["processId"], owner=True)
        return started["processId"]

    def retrieve(client, archive, signature, previous_project, label):
        retrieved = call(client, "project_retrieve", {"archivePath": archive, "expectedSha256": signature}, label)
        value = retrieved["project"]["path"]
        require(retrieved.get("retrieved") is True and PureWindowsPath(value) != PureWindowsPath(previous_project), "Recovery reused original project path")
        remote_path(value, args.remote_artifact_root)
        return value

    try:
        primary = connect("initial")
        pid = new_owned(primary, "start new remote owned instance")
        scratch = call(primary, "tia_create_scratch", {"cpuTypeIdentifier": args.cpu}, "create isolated remote scratch")
        require(scratch.get("completed") is True and scratch.get("compile", {}).get("errorCount") == 0, "Remote scratch did not compile")
        project = scratch["project"]["path"]
        assert_primary(primary, project, pid)
        report["initialProjectPath"], report["initialOwnedPid"] = project, pid
        before = export(primary, "TiaMcp_IsEnabled", "before.xml")
        renamed = "TiaMcp_Remote_" + uuid.uuid4().hex[:8]
        prepared = prepare(primary, "block_rename", {"blockName": "TiaMcp_IsEnabled", "newName": renamed}, project, pid)
        arguments = apply_arguments(prepared)
        dispatch_record(arguments)
        applied = call(primary, "change_apply", arguments, "apply remote rename exactly once")
        require(applied["state"] == "applied" and applied["compile"]["errorCount"] == 0, "Remote mutation did not apply/compile")
        archive, signature = checkpoint(applied, args.remote_artifact_root)
        first_backup = archive_copy(primary, archive, signature, "before-normal-change.zap21")
        after = export(primary, renamed, "after.xml")
        disconnect(primary, "initial connection")

        primary = connect("reconnect")
        status = call(primary, "change_status", {"changeId": prepared["changeId"]}, "fresh SSH reads persisted normal journal")
        require(status.get("recoveredFromDisk") is True and status.get("automaticRetryAllowed") is False, "Fresh SSH did not load a durable non-retryable journal")
        journal_identity(status["journal"], prepared["changeId"], project, arguments["idempotencyKey"])
        require(status["journal"]["state"] == "applied", "Persisted normal outcome differs")
        pid = new_owned(primary, "new owned instance after SSH reconnect")
        call(primary, "project_open", {"path": project}, "reopen only saved owned scratch")
        assert_primary(primary, project, pid)
        reopened = export(primary, renamed, "reopened.xml")
        require(reopened["semanticSha256"] == after["semanticSha256"], "Saved changed block differs after reconnect")
        _, original_meta = fetch(primary, archive, args.remote_artifact_root, signature)
        note("original checkpoint preserved across reconnect", original_meta)
        disconnect(primary, "reconnected saved fixture")

        primary = connect("normal recovery")
        pid = new_owned(primary, "new owned recovery instance")
        recovered_project = retrieve(primary, archive, signature, project, "retrieve normal checkpoint in fresh SSH host")
        assert_primary(primary, recovered_project, pid)
        recovered = export(primary, "TiaMcp_IsEnabled", "recovered.xml")
        require(recovered["semanticSha256"] == before["semanticSha256"], "Checkpoint recovery did not restore baseline XML")
        report["normalRecoveryPassed"] = True

        if args.interrupt:
            # Artifact reads are host-local and require no Siemens attachment.
            # This keeps remote/service-style session qualification independent
            # from attachment support for already-running TIA instances.
            observer = connect("independent artifact reader")
            names, source = fault_source(uuid.uuid4().hex[:8], args.fault_blocks)
            (local / "fault-fixture.scl").write_text(source, encoding="utf-8")
            fault = prepare(primary, "source_upsert", {"language": "SCL", "source": source, "expectedBlockNames": names}, recovered_project, pid)
            fault_args = apply_arguments(fault)
            dispatch_record(fault_args)
            pending = {"requestStartedAt": time.monotonic()}
            finished = threading.Event()
            def apply_pending():
                try:
                    pending["response"] = primary.tool("change_apply", fault_args)
                    pending["responseReceivedAt"] = time.monotonic()
                except Exception as error:
                    pending["errorType"], pending["error"] = type(error).__name__, str(error)
                    pending["errorReceivedAt"] = time.monotonic()
                finally:
                    finished.set()
            thread = threading.Thread(target=apply_pending, daemon=True)
            active_thread, active_client = thread, primary
            thread.start()
            observed = None
            deadline = time.monotonic() + min(args.timeout, 120)
            polls, transient_reads = 0, 0
            while not finished.is_set() and time.monotonic() < deadline:
                try:
                    data, meta = fetch(observer, fault["journalPath"], args.remote_artifact_root, maximum=4 * 1024 * 1024)
                    polls += 1
                    candidate = json.loads(data.decode("utf-8-sig"))
                    journal_identity(candidate, fault["changeId"], recovered_project)
                    if candidate.get("state") in {"applying", "applied", "recovery_required"}:
                        journal_identity(candidate, fault["changeId"], recovered_project, fault_args["idempotencyKey"])
                        checkpoint(candidate, args.remote_artifact_root)
                        if not finished.is_set():
                            observed = candidate
                            (local / "fault-journal-before-interruption.json").write_bytes(data)
                            pending["sshTerminationAt"] = time.monotonic()
                            # Exact Popen process handle: only the local SSH client.
                            primary.process.kill()
                            primary.process.wait(timeout=10)
                            primary.terminal = True
                            break
                except ValueError as error:
                    if "changed between read chunks" not in str(error):
                        raise
                    transient_reads += 1  # Retrying an artifact read never replays a write.
                except ToolBlocked as error:
                    if not transient_journal_read(error):
                        raise
                    transient_reads += 1
                time.sleep(0.02)
            thread.join(timeout=15)
            require(finished.is_set(), "Interrupted request did not return; native outcome remains unknown")
            active_thread = active_client = None
            if observed is None or "response" in pending:
                report["interruption"] = {"requested": True, "status": "not_exercised", "reason": "No pending response at a durable checkpoint, or apply acknowledgement won the race", "request": pending,
                                          "journalPolls": polls, "transientReadRaces": transient_reads}
                raise ValueError("Actual interrupted-write qualification was not observed; no mutation will be retried")
            require(pending.get("errorType") == "ProtocolFailure", "Connection interruption did not produce a transport failure")
            report["interruption"] = {"requested": True, "status": "transport_interrupted_outcome_initially_unknown", "observedJournalState": observed["state"],
                                      "changeId": fault["changeId"], "request": pending, "journalPolls": polls, "transientReadRaces": transient_reads,
                                      "remoteProcessExitVerified": False, "mutationReplayCount": 0,
                                      "interruptionPoint": "checkpoint_recorded_before_recorded_terminal_outcome" if observed["state"] == "applying" else "terminal_journal_before_client_acknowledgement",
                                      "timingLimit": "The journal observation precedes SSH termination; exact native execution state at termination is not instrumented."}
            state(primary)["attached"] = False  # Broken SSH cannot confirm its remote attachment exit.
            disconnect(primary, "interrupted SSH launcher")
            reconciler = connect("fault reconciliation")
            final_journal = None
            deadline = time.monotonic() + min(args.timeout, 300)
            while time.monotonic() < deadline:
                saved = call(reconciler, "change_status", {"changeId": fault["changeId"]}, "read interrupted change journal without replay")
                require(saved.get("recoveredFromDisk") is True and saved.get("automaticRetryAllowed") is False, "Reconciliation did not use durable journal-only status")
                candidate = saved["journal"]
                journal_identity(candidate, fault["changeId"], recovered_project, fault_args["idempotencyKey"])
                if candidate["state"] in {"applied", "recovery_required"}:
                    final_journal = candidate
                    break
                time.sleep(1)
            require(final_journal is not None, "Journal did not reach a terminal state; mutation outcome remains unknown and no replay is permitted")
            fault_archive, fault_hash = checkpoint(final_journal, args.remote_artifact_root)
            require((fault_archive, fault_hash) == checkpoint(observed, args.remote_artifact_root), "Recovery checkpoint identity changed after interruption")
            archive_copy(reconciler, fault_archive, fault_hash, "before-interrupted-change.zap21")
            (local / "fault-journal-reconciled.json").write_text(json.dumps(final_journal, indent=2), encoding="utf-8")
            report["interruption"]["reconciledJournalState"] = final_journal["state"]
            report["interruption"]["status"] = "reconciled_without_replay"
            new_pid = new_owned(reconciler, "new owned instance for interrupted checkpoint recovery")
            final_project = retrieve(reconciler, fault_archive, fault_hash, recovered_project, "recover interrupted change checkpoint explicitly")
            assert_primary(reconciler, final_project, new_pid)
            final_block = export(reconciler, "TiaMcp_IsEnabled", "recovered-after-interruption.xml")
            require(final_block["semanticSha256"] == recovered["semanticSha256"], "Interrupted checkpoint did not preserve baseline source")
            inventory = call(reconciler, "project_inventory", {"maxItems": 2000}, "verify interrupted source fixture absent from checkpoint recovery")
            note("fixture absence established only in exposed program-block scope", verify_fixture_absent(inventory, names, final_project))
            _, preserved = fetch(reconciler, fault_archive, args.remote_artifact_root, fault_hash)
            note("interrupted checkpoint remains immutable", preserved)
            disconnect(observer, "independent artifact reader")
            disconnect(reconciler, "interrupted checkpoint recovery")
            report["interruption"]["checkpointRecoveryPassed"] = True
        else:
            disconnect(primary, "normal checkpoint recovery")
        require(hashlib.sha256((local / "before-normal-change.zap21").read_bytes()).hexdigest() == first_backup["sha256"], "Local immutable normal checkpoint changed")
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        if isinstance(error, ToolBlocked):
            report["blockedResponse"] = error.response
        print("FAIL " + str(error), flush=True)
    finally:
        for row in reversed(clients):
            if row["closed"]:
                continue
            client = row["client"]
            try:
                pending_on_client = active_thread is not None and active_thread.is_alive() and active_client is client
                if pending_on_client:
                    # Never send a concurrent detach through a client whose mutation
                    # is unresolved. Close only this SSH stream, retain the journal,
                    # and report uncertainty rather than consume/replay its response.
                    report.setdefault("cleanupErrors", []).append({"session": row["label"], "error": "Mutation request remains pending; only the owned SSH stream will close. Native outcome is unknown."})
                    report["completed"] = False
                elif row["attached"] and not client.terminal:
                    call(client, "tia_detach", label="cleanup " + row["label"])
            except Exception as error:
                report.setdefault("cleanupErrors", []).append({"session": row["label"], "error": str(error)})
                report["completed"] = False
            finally:
                try:
                    interrupted = client.terminal
                    closed = client.close()
                    note("close " + row["label"], closed)
                    row["closed"] = True
                    verify_launcher_shutdown(closed, interrupted)
                except Exception as error:
                    report.setdefault("cleanupErrors", []).append({"session": row["label"], "error": str(error)})
                    report["completed"] = False
        report["passedSteps"] = sum(step["passed"] for step in report["steps"])
        save()
        print("Report: " + str(args.report), flush=True)
    return 0 if report["completed"] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--server", required=True)
    parser.add_argument("--remote-artifact-root", required=True)
    parser.add_argument("--report", type=Path, default=ROOT / "artifacts/ssh-recovery.json")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts")
    parser.add_argument("--timeout", type=float, default=600)
    parser.add_argument("--interrupt", action="store_true", help="Interrupt this runner's SSH connection only after durable checkpoint evidence")
    parser.add_argument("--fault-blocks", type=int, default=100)
    parser.add_argument("--cpu", default="OrderNumber:6ES7 511-1AL03-0AB0/V4.0")
    args = parser.parse_args()
    require(args.report.resolve() != args.config.resolve(), "Report must not overwrite MCP configuration")
    require(10 <= args.timeout <= 900, "timeout must be 10..900 seconds")
    if args.interrupt:
        fault_source("00000000", args.fault_blocks)
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
