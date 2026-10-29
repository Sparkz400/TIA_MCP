"""Qualify real MCP host failure boundaries with an isolated worker test double.

Default mode never loads Siemens APIs. --live additionally reads an existing journal,
retrieves its hash-verified checkpoint into new owned TIA sessions, and verifies that
observer detach/host termination preserves the owner. It never terminates TIA.
"""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
import pathlib
import queue
import subprocess
import threading
import time
import uuid
import xml.etree.ElementTree as ET

from smoke_mcp import Client, ROOT

STABLE_BUNDLE = ROOT / "artifacts/releases/tia-mcp-v21_20261008_064849_e96b6805"
WORKER_PROJECT = ROOT / "scripts/native/ReliabilityWorker/ReliabilityWorker.csproj"
WORKER = WORKER_PROJECT.parent / "bin/Release/net10.0-windows/ReliabilityWorker.exe"
MODES = ("early-exit", "eof", "partial-frame", "wrong-id", "malformed", "timeout", "kill-worker", "kill-host", "cancel", "operation-error", "graceful-eof")


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def read_events(path):
    """Only complete durable lines count; a concurrent partial append is not evidence."""
    if not path.exists():
        return []
    text = path.read_text(encoding="utf-8")
    return [json.loads(line) for line in text[:text.rfind("\n") + 1].splitlines() if line]


def wait_for(predicate, description, timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(0.05)
    raise TimeoutError(description)


def decoded(raw):
    if "structuredContent" in raw:
        return raw["structuredContent"]
    return json.loads("\n".join(item["text"] for item in raw.get("content", []) if item.get("type") == "text"))


def require_error(raw, code):
    value = decoded(raw)
    require(raw.get("isError") and value.get("error", {}).get("code") == code, f"Expected {code}: {raw}")
    return value


def verify_no_replay(events, token, mutations=1, starts=1):
    require(all(event.get("token") == token for event in events), "Foreign worker evidence")
    require(sum(event["kind"] == "started" for event in events) == starts, "Unexpected worker restart")
    requests = [event for event in events if event["kind"] == "request"]
    # Admitted writes reach the worker as mutation_dispatch; older Hosts sent change_apply directly.
    writes = [event for event in requests if event.get("operation") in ("change_apply", "mutation_dispatch")]
    require(len(writes) == mutations, "A mutation was missing or replayed")
    require(len({event["id"] for event in requests}) == len(requests), "Duplicate request ID")


class ReliabilityClient(Client):
    """Keep asynchronous request IDs explicit so the harness can interrupt its own call."""
    def __init__(self, host, worker, artifacts, timeout=5, config=None, record=None):
        self.timeout, self.sequence = timeout, 0
        self.tool_calls = [] if record is None else record
        self.lines, self.errors = queue.Queue(), []
        environment = os.environ.copy()
        if config:
            environment["TIA_MCP_RELIABILITY_CONFIG"] = str(config)
        else:
            environment.pop("TIA_MCP_RELIABILITY_CONFIG", None)
        self.process = subprocess.Popen(
            [str(host), "--worker", str(worker), "--timeout-seconds", str(timeout), "--artifacts", str(artifacts)],
            cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", bufsize=1, env=environment,
        )
        threading.Thread(target=self._read_output, daemon=True).start()
        threading.Thread(target=self._read_errors, daemon=True).start()

    def begin(self, method, params=None):
        self.sequence += 1
        self.process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.sequence, "method": method, "params": params or {}}) + "\n")
        self.process.stdin.flush()
        return self.sequence

    def receive(self, request_id):
        deadline = time.monotonic() + self.timeout + 15
        while True:
            line = self.lines.get(timeout=max(0.01, deadline - time.monotonic()))
            if line is None:
                raise RuntimeError("Owned MCP host exited: " + "\n".join(self.errors[-10:]))
            response = json.loads(line)
            if response.get("id") != request_id:
                continue
            if "error" in response:
                raise RuntimeError(str(response["error"]))
            return response["result"]

    def request(self, method, params=None):
        result = self.receive(self.begin(method, params))
        if method == "tools/call":
            self.tool_calls.append({"name": params["name"], "isError": bool(result.get("isError"))})
        return result

    def initialize(self):
        self.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "reliability-qualification", "version": "1"}})
        self.notify("notifications/initialized")

    def raw_tool(self, name, arguments=None):
        return self.request("tools/call", {"name": name, "arguments": arguments or {}})

    def kill_owned_host(self):
        # Popen holds the exact created-process handle. No name-based or tree kill.
        self.process.kill()
        self.process.wait(timeout=10)

    def close(self):
        if self.process.poll() is None:
            try:
                self.process.stdin.close()
                self.process.wait(timeout=15)
            except (BrokenPipeError, subprocess.TimeoutExpired):
                self.kill_owned_host()
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
            if stream and not stream.closed:
                stream.close()


def terminate_owned_test_worker(pid, expected_executable):
    """Bind the PID to a handle and verify its image before terminating this test double."""
    if os.name != "nt":
        raise RuntimeError("Worker termination qualification currently requires Windows")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x1000 | 0x100000 | 0x0001, False, pid)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        buffer = ctypes.create_unicode_buffer(32768)
        length = wintypes.DWORD(len(buffer))
        if not kernel.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(length)):
            raise ctypes.WinError(ctypes.get_last_error())
        require(pathlib.Path(buffer.value).resolve() == expected_executable.resolve(), "Refusing to terminate a process other than the exact test worker")
        if not kernel.TerminateProcess(handle, 73):
            raise ctypes.WinError(ctypes.get_last_error())
        require(kernel.WaitForSingleObject(handle, 5000) == 0, "Test worker did not exit")
    finally:
        kernel.CloseHandle(handle)


def qualify_case(mode, host, worker, run_root):
    case_dir = run_root / mode
    case_dir.mkdir()
    config = case_dir / "config.json"
    token = uuid.uuid4().hex
    config.write_text(json.dumps({"token": token, "mode": mode}), encoding="utf-8")
    event_path = case_dir / "events.jsonl"
    report = {"mode": mode, "scope": "actual MCP host with worker test double", "passed": False, "eventsPath": str(event_path)}
    client = ReliabilityClient(host, worker, case_dir / "artifacts", config=config)
    report["hostPid"] = client.process.pid
    try:
        client.initialize()
        if mode == "early-exit":
            report["error"] = require_error(client.raw_tool("tia_capabilities"), "WorkerExited")
            # Nothing was sent, so the bridge is not faulted: the next call launches a fresh worker, which exits again.
            require_error(client.raw_tool("tia_capabilities"), "WorkerExited")
        elif mode == "operation-error":
            require_error(client.raw_tool("block_read", {"blockName": "fixture"}), "ProtectedBlock")
            require(client.tool("tia_capabilities")["testDouble"], "Session did not survive operation rejection")
        elif mode == "graceful-eof":
            require(client.tool("tia_capabilities")["testDouble"], "Wrong worker")
            client.close()
            wait_for(lambda: any(e["kind"] == "host-eof" for e in read_events(event_path)), "Worker did not observe graceful host EOF")
        else:
            arguments = {"changeId": token, "previewHash": "a" * 64, "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True}
            request_id = client.begin("tools/call", {"name": "change_apply", "arguments": arguments})
            committed = wait_for(lambda: next((e for e in read_events(event_path) if e["kind"] == "committed"), None), "Fake mutation did not commit")
            if mode == "kill-worker":
                terminate_owned_test_worker(committed["pid"], worker)
            elif mode == "kill-host":
                client.kill_owned_host()
            elif mode == "cancel":
                client.notify("notifications/cancelled", {"requestId": request_id, "reason": "Intentional qualification cancellation"})
            expected = {"eof": "WorkerDisconnected", "partial-frame": "WorkerDisconnected", "wrong-id": "ProtocolMismatch", "malformed": "InvalidResponse", "timeout": "TimeoutOutcomeUnknown", "kill-worker": "WorkerDisconnected", "cancel": "CancelledOutcomeUnknown"}
            if mode != "kill-host":
                if mode == "cancel":
                    # MCP may suppress the cancelled response. A subsequent tool must
                    # still reveal the terminal bridge without dispatching a new write.
                    terminal = client.raw_tool("tia_capabilities")
                    require_error(terminal, "BridgeFaulted")
                    report["error"] = {"code": "cancelled_request", "subsequentError": decoded(terminal)}
                else:
                    report["error"] = require_error(client.receive(request_id), expected[mode])
                    require_error(client.raw_tool("tia_capabilities"), "BridgeFaulted")
                require_error(client.raw_tool("change_apply", arguments), "BridgeFaulted")
            report["durableOutcome"] = json.loads((case_dir / "fake-journal.json").read_text())
            require(report["durableOutcome"]["mutationCount"] == 1, "Wrong mutation count")
        client.close()
        if mode != "kill-worker":
            wait_for(lambda: any(e["kind"] == "stopped" for e in read_events(event_path)), "Worker did not release the connection")
        events = read_events(event_path)
        count = 0 if mode in {"early-exit", "operation-error", "graceful-eof"} else 1
        verify_no_replay(events, token, mutations=count)
        report["requestCount"] = sum(e["kind"] == "request" for e in events)
        if count:
            # Explicitly new host and worker; inspect disk, never replay change_apply.
            config.write_text(json.dumps({"token": token, "mode": "inspect"}), encoding="utf-8")
            fresh = ReliabilityClient(host, worker, case_dir / "artifacts", config=config)
            try:
                fresh.initialize()
                status = fresh.tool("change_status", {"changeId": token})
                require(status["loadedFromDisk"] and status["journal"] == report["durableOutcome"], "Fresh host lost the durable outcome")
                report["freshHostJournal"] = status
            finally:
                fresh.close()
            wait_for(lambda: sum(e["kind"] == "stopped" for e in read_events(event_path)) >= (1 if mode == "kill-worker" else 2), "Fresh worker did not exit")
            verify_no_replay(read_events(event_path), token, mutations=1, starts=2)
        report["passed"] = True
    except Exception as error:
        report["failure"] = str(error)
    finally:
        client.close()
        report["hostExitCode"] = client.process.returncode
        report["stderrTail"] = client.errors[-20:]
        (case_dir / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report


def validated_checkpoint(journal_path):
    path = journal_path.resolve(strict=True)
    require(path.is_relative_to((ROOT / "artifacts").resolve()), "Checkpoint must come from this workspace's recorded artifacts")
    require(path.name == "journal.json" and path.parent.name.startswith("change-"), "Expected a change journal")
    journal = json.loads(path.read_text(encoding="utf-8-sig"))
    require(pathlib.Path(journal["projectPath"]).stem.startswith("TiaMcpScratch_"), "Native qualification accepts recorded scratch projects only")
    change_id = journal["changeId"]
    require(len(change_id) == 32 and all(c in "0123456789abcdef" for c in change_id), "Invalid change identity")
    require(path.parent.name == "change-" + change_id and path.parent.parent.name == "changes", "Journal path does not match its identity")
    archive = pathlib.Path(journal["recoveryArchive"]).resolve(strict=True)
    require(archive.is_relative_to((ROOT / "artifacts").resolve()) and archive.suffix.lower() == ".zap21", "Expected a recorded local checkpoint archive")
    require(hashlib.sha256(archive.read_bytes()).hexdigest() == journal["recoveryArchiveSha256"], "Checkpoint archive hash mismatch")
    return journal, archive, path.parent.parent.parent


def semantic_xml(xml):
    document = ET.fromstring(xml)
    for child in list(document):
        if child.tag.rsplit("}", 1)[-1] == "DocumentInfo":
            document.remove(child)
    return hashlib.sha256(ET.tostring(document, encoding="utf-8")).hexdigest()


def qualify_native(host, worker, run_root, journal_path, block_name):
    """No injected native mutation failures: recovery evidence and observer lifecycle only."""
    journal, archive, journal_artifacts = validated_checkpoint(journal_path)
    result = {"scope": "native offline owned checkpoint copies", "passed": False, "steps": [], "archiveSha256": journal["recoveryArchiveSha256"], "toolCalls": []}
    owner = observer = None
    def step(name, value):
        result["steps"].append({"name": name, "result": value})
        (run_root / "native-result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        print("PASS native " + name, flush=True)
        return value
    try:
        reader = ReliabilityClient(host, worker, journal_artifacts, timeout=300, record=result["toolCalls"])
        try:
            reader.initialize()
            status = step("fresh host persisted journal", reader.tool("change_status", {"changeId": journal["changeId"]}))
            require(status["journal"]["state"] == journal["state"], "Persisted state mismatch")
            require(status["journal"]["recoveryArchiveSha256"] == journal["recoveryArchiveSha256"], "Persisted checkpoint mismatch")
        finally:
            reader.close()
        # Keep recovery roots short: Siemens enforces a project-directory length limit.
        native_artifacts = ROOT / "artifacts" / ("rl_" + uuid.uuid4().hex[:8])
        native_artifacts.mkdir()
        result["nativeArtifacts"] = str(native_artifacts)
        owner = ReliabilityClient(host, worker, native_artifacts, timeout=300, record=result["toolCalls"])
        owner.initialize()
        start = step("start owned instance", owner.tool("tia_start"))
        pid = start["processId"]
        require_error(owner.raw_tool("project_retrieve", {"archivePath": str(archive), "expectedSha256": "0" * 64}), "CheckpointInvalid")
        step("reject wrong archive hash", {"code": "CheckpointInvalid"})
        retrieved = step("retrieve checkpoint", owner.tool("project_retrieve", {"archivePath": str(archive), "expectedSha256": journal["recoveryArchiveSha256"]}))
        project = pathlib.Path(retrieved["project"]["path"]).resolve()
        require(project.is_relative_to(native_artifacts) and project != pathlib.Path(journal["projectPath"]).resolve(), "Recovery did not create an isolated copy")
        before = owner.tool("block_read", {"blockName": block_name})
        before_hash = semantic_xml(before["xml"])
        observer = ReliabilityClient(host, worker, native_artifacts / "observer", timeout=300, record=result["toolCalls"])
        observer.initialize()
        step("observer attach", observer.tool("tia_attach", {"processId": pid}))
        require(semantic_xml(observer.tool("block_read", {"blockName": block_name})["xml"]) == before_hash, "Observer read differs")
        detached = step("observer detach", observer.tool("tia_detach"))
        require(detached["disposedOwnedPortalConnection"] is False, "Observer claimed owner disposal")
        require(owner.tool("tia_session_status")["processId"] == pid, "Owner was lost after observer detach")
        observer.tool("tia_attach", {"processId": pid})
        observer.kill_owned_host()
        observer.close()
        observer = None
        require(owner.tool("tia_session_status")["processId"] == pid, "Owner was lost after observer host exit")
        require(semantic_xml(owner.tool("block_read", {"blockName": block_name})["xml"]) == before_hash, "Project changed after observer exit")
        step("owner preserved after observer host termination", {"processId": pid, "blockSemanticSha256": before_hash})
        step("owner detach", owner.tool("tia_detach"))
        owner.close()
        owner = ReliabilityClient(host, worker, native_artifacts, timeout=300, record=result["toolCalls"])
        owner.initialize()
        owner.tool("tia_start")
        reopened = step("fresh host checkpoint retrieval", owner.tool("project_retrieve", {"archivePath": str(archive), "expectedSha256": journal["recoveryArchiveSha256"]}))
        require(pathlib.Path(reopened["project"]["path"]).resolve() != project, "Second recovery reused the prior directory")
        require(semantic_xml(owner.tool("block_read", {"blockName": block_name})["xml"]) == before_hash, "Recovered source differs across host restarts")
        require(hashlib.sha256(archive.read_bytes()).hexdigest() == journal["recoveryArchiveSha256"], "Source checkpoint was modified")
        step("recovery source preserved", {"blockSemanticSha256": before_hash, "archiveSha256": journal["recoveryArchiveSha256"]})
        result["passed"] = True
    except Exception as error:
        result["failure"] = str(error)
    finally:
        for role, client in (("observer", observer), ("owner", owner)):
            if client is not None:
                try:
                    client.tool("tia_detach")
                except Exception as error:
                    result.setdefault("cleanupErrors", []).append({"role": role, "error": str(error)})
                    result["passed"] = False
                finally:
                    client.close()
        (run_root / "native-result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=pathlib.Path, default=STABLE_BUNDLE / "host/TiaMcp.Host.exe")
    parser.add_argument("--fake-worker", type=pathlib.Path, default=WORKER)
    parser.add_argument("--build-worker", action="store_true", help="Build only this dependency-free helper; never the solution")
    parser.add_argument("--modes", nargs="+", choices=MODES, default=list(MODES))
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/reliability-qualification.json")
    parser.add_argument("--live", action="store_true", help="Explicitly start only owned native TIA instances and retrieve checkpoint copies")
    parser.add_argument("--checkpoint-journal", type=pathlib.Path)
    parser.add_argument("--native-worker", type=pathlib.Path, default=STABLE_BUNDLE / "workers/openness/TiaMcp.Openness.V21.exe")
    parser.add_argument("--block-name", default="TiaMcp_IsEnabled")
    args = parser.parse_args()
    if args.live and not args.checkpoint_journal:
        parser.error("--live requires --checkpoint-journal from a recorded isolated fixture")
    if len(args.modes) != len(set(args.modes)):
        parser.error("--modes must not contain duplicates")
    if args.live:
        validated_checkpoint(args.checkpoint_journal)
        require(args.native_worker.is_file(), "Native worker executable missing")
    if args.build_worker:
        environment = os.environ.copy()
        environment["DOTNET_CLI_HOME"] = str(ROOT / ".dotnet")
        environment["MSBuildEnableWorkloadResolver"] = "false"
        subprocess.run(["dotnet", "build", str(WORKER_PROJECT), "-c", "Release", "--ignore-failed-sources", "-m:1", "-nr:false"], cwd=ROOT, env=environment, check=True)
    require(args.host.is_file() and args.fake_worker.is_file(), "Host or fake worker executable missing; use --build-worker")
    run_root = ROOT / "artifacts" / ("reliability-" + uuid.uuid4().hex[:12])
    run_root.mkdir()
    report = {"schemaVersion": 1, "completed": False, "runDirectory": str(run_root), "hostPath": str(args.host.resolve()),
              "hostSha256": hashlib.sha256(args.host.read_bytes()).hexdigest(),
              "hostAssemblySha256": hashlib.sha256(args.host.with_suffix(".dll").read_bytes()).hexdigest(),
              "fakeWorkerPath": str(args.fake_worker.resolve()),
              "nativeQualification": {"status": "not_run", "reason": "Requires --live and a recorded checkpoint; fake tests do not certify Siemens behavior."}, "cases": []}
    def save():
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    for mode in args.modes:
        case = qualify_case(mode, args.host.resolve(), args.fake_worker.resolve(), run_root)
        report["cases"].append(case)
        save()
        print(("PASS " if case["passed"] else "FAIL ") + mode, flush=True)
    if args.live:
        report["nativeQualification"] = qualify_native(args.host.resolve(), args.native_worker.resolve(), run_root, args.checkpoint_journal, args.block_name)
        report["toolCalls"] = report["nativeQualification"]["toolCalls"]
    report["completed"] = all(case["passed"] for case in report["cases"]) and (not args.live or report["nativeQualification"]["passed"])
    report["passedCases"] = sum(case["passed"] for case in report["cases"])
    save()
    print("Report: " + str(args.report), flush=True)
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
