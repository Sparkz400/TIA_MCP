"""Qualify the exact local/SSH stdio MCP configuration; default checks never attach or edit."""
from __future__ import annotations

import argparse
import base64
from collections import deque
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time
from run_block_tests import replace_owned_pending


class ProtocolFailure(RuntimeError):
    pass


class ToolBlocked(RuntimeError):
    def __init__(self, name, response):
        self.response = response
        super().__init__(f"{name}: {json.dumps(response, ensure_ascii=False)[:4000]}")


def load_configuration(path, server_name):
    raw = Path(path).read_bytes()
    document = json.loads(raw.decode("utf-8-sig"))
    server = document.get("mcpServers", {}).get(server_name)
    if not isinstance(server, dict):
        raise ValueError(f"Configuration has no mcpServers entry named {server_name!r}.")
    command, arguments = server.get("command"), server.get("args", [])
    if not isinstance(command, str) or not command or "\0" in command:
        raise ValueError("MCP command must be a nonempty string without NUL.")
    if not isinstance(arguments, list) or any(not isinstance(arg, str) or "\0" in arg for arg in arguments):
        raise ValueError("MCP args must be an array of strings without NUL.")
    environment = server.get("env", {})
    if not isinstance(environment, dict) or any(not isinstance(k, str) or not k or "=" in k or "\0" in k or not isinstance(v, str) or "\0" in v for k, v in environment.items()):
        raise ValueError("MCP env must contain string variable names and values.")
    if server.get("url") or server.get("type", "stdio") != "stdio":
        raise ValueError("This runner qualifies stdio configurations only.")
    cwd = server.get("cwd")
    if cwd is not None and (not isinstance(cwd, str) or not Path(cwd).is_absolute() or not Path(cwd).is_dir()):
        raise ValueError("MCP cwd must be an existing absolute client-side directory.")
    name = command.replace("\\", "/").rsplit("/", 1)[-1].lower()
    transport = "ssh" if name in {"ssh", "ssh.exe"} else "local-stdio"
    if transport == "ssh":
        # Inspect only OpenSSH options, never the opaque remote PowerShell payload.
        options = arguments[:arguments.index("--")] if "--" in arguments else arguments
        settings = {}
        index = 0
        while index < len(options):
            argument = options[index]
            if argument == "-o":
                index += 1
                if index >= len(options):
                    raise ValueError("SSH -o is missing its value.")
                option = options[index]
            elif argument.startswith("-o"):
                option = argument[2:]
            else:
                index += 1
                continue
            key, separator, value = option.partition("=")
            if not separator:
                raise ValueError("Use explicit key=value SSH options.")
            key = key.lower()
            if key in settings and settings[key].lower() != value.lower():
                raise ValueError("Conflicting SSH options are not accepted.")
            settings[key] = value
            index += 1
        if "-T" not in options or any(arg in {"-t", "-tt"} for arg in options):
            raise ValueError("SSH qualification requires -T and no pseudo-terminal.")
        for key in ("batchmode", "stricthostkeychecking"):
            if settings.get(key, "").lower() != "yes":
                raise ValueError(f"SSH qualification requires {key}=yes explicitly.")
        if not settings.get("connecttimeout", "").isdigit() or not 1 <= int(settings["connecttimeout"]) <= 120:
            raise ValueError("SSH qualification requires an explicit 1..120 second ConnectTimeout.")
    return {"command": [command, *arguments], "environment": environment, "cwd": cwd,
            "transport": transport, "configurationSha256": hashlib.sha256(raw).hexdigest()}


class McpClient:
    """One bounded serial client. Requests are never retried or replayed."""
    def __init__(self, configuration, timeout):
        self.timeout = timeout
        self.sequence = 0
        self.terminal = False
        self.output = queue.Queue(maxsize=64)
        self.errors = deque(maxlen=40)
        self.process = subprocess.Popen(configuration["command"], shell=False,
            cwd=configuration["cwd"], env={**os.environ, **configuration["environment"]},
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.readers = [threading.Thread(target=self._read_stdout, daemon=True),
                        threading.Thread(target=self._read_stderr, daemon=True)]
        for reader in self.readers:
            reader.start()

    def _put(self, message):
        try:
            self.output.put(message, timeout=0.1)
        except queue.Full:
            self.terminal = True

    def _read_stdout(self):
        try:
            while True:
                line = self.process.stdout.readline(8 * 1024 * 1024 + 1)
                if not line:
                    self._put(None)
                    return
                if len(line) > 8 * 1024 * 1024:
                    self._put(ProtocolFailure("MCP message exceeds the 8 MiB qualification bound."))
                    return
                self._put(line)
        except Exception as error:
            self._put(error)

    def _read_stderr(self):
        try:
            while True:
                line = self.process.stderr.readline(8193)
                if not line:
                    return
                self.errors.append(line.decode("utf-8", errors="replace").rstrip()[:8192])
        except (OSError, ValueError):
            pass

    def _send(self, message):
        if self.terminal:
            raise ProtocolFailure("MCP transport is terminal; no request was replayed.")
        try:
            self.process.stdin.write((json.dumps(message) + "\n").encode("utf-8"))
            self.process.stdin.flush()
        except (BrokenPipeError, OSError) as error:
            self.terminal = True
            raise ProtocolFailure("MCP input closed before the request completed.") from error

    def notify(self, method):
        self._send({"jsonrpc": "2.0", "method": method})

    def request(self, method, params=None):
        self.sequence += 1
        self._send({"jsonrpc": "2.0", "id": self.sequence, "method": method, "params": params or {}})
        deadline = time.monotonic() + self.timeout
        try:
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise queue.Empty()
                line = self.output.get(timeout=remaining)
                if line is None:
                    raise ProtocolFailure("MCP process closed stdout before responding.")
                if isinstance(line, Exception):
                    raise ProtocolFailure(str(line))
                try:
                    response = json.loads(line.decode("utf-8"))
                except (UnicodeDecodeError, json.JSONDecodeError) as error:
                    raise ProtocolFailure("MCP stdout contained non-protocol data.") from error
                if not isinstance(response, dict) or response.get("jsonrpc") != "2.0":
                    raise ProtocolFailure("Invalid JSON-RPC response envelope.")
                if "id" not in response:
                    if "method" not in response:
                        raise ProtocolFailure("Invalid JSON-RPC notification.")
                    continue
                if response["id"] != self.sequence:
                    raise ProtocolFailure("Unexpected JSON-RPC response ID; no response was reused.")
                if "error" in response:
                    raise ProtocolFailure("JSON-RPC request error: " + json.dumps(response["error"])[:2000])
                if "result" not in response:
                    raise ProtocolFailure("JSON-RPC response omitted result.")
                return response["result"]
        except queue.Empty as error:
            self.terminal = True
            raise ProtocolFailure(f"MCP request exceeded {self.timeout:g} seconds; no automatic retry.") from error
        except ProtocolFailure:
            self.terminal = True
            raise

    def tool(self, name, arguments=None):
        result = self.request("tools/call", {"name": name, "arguments": arguments or {}})
        if not isinstance(result, dict):
            raise ProtocolFailure("MCP tool result must be an object.")
        if result.get("isError"):
            raise ToolBlocked(name, result.get("structuredContent", result))
        if "structuredContent" in result:
            value = result["structuredContent"]
        else:
            try:
                value = json.loads("\n".join(item["text"] for item in result.get("content", []) if item.get("type") == "text"))
            except (KeyError, TypeError, json.JSONDecodeError) as error:
                raise ProtocolFailure("MCP tool has no usable JSON result.") from error
        if not isinstance(value, dict):
            raise ProtocolFailure("Expected a structured object from " + name)
        return value

    def close(self):
        forced = False
        try:
            self.process.stdin.close()
        except (BrokenPipeError, OSError):
            pass
        try:
            self.process.wait(timeout=min(75, max(1, self.timeout)))
        except subprocess.TimeoutExpired:
            forced = True
            # Only our direct launcher/SSH process is owned. Never kill a process
            # tree or a Siemens process; any unconfirmed remote cleanup is reported.
            self.process.kill()
            self.process.wait(timeout=5)
        for reader in self.readers:
            reader.join(timeout=0.5)
        for stream in (self.process.stdout, self.process.stderr):
            stream.close()
        return {"exitCode": self.process.returncode, "forcedLauncherStop": forced,
                "remoteProcessExitVerified": False, "stderrTail": list(self.errors)}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def initialize(client):
    result = client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
        "clientInfo": {"name": "tia-deployment-qualification", "version": "1.0.0"}})
    require(isinstance(result, dict) and isinstance(result.get("serverInfo"), dict)
        and isinstance(result.get("protocolVersion"), str), "MCP initialize response is incomplete.")
    client.notify("notifications/initialized")
    return result


def read_catalog(client, *, include_features=False):
    features = []
    offset = 0
    expected_total = None
    expected_version = None
    for _ in range(100):
        result = client.tool("feature_catalog", {"offset": offset, "maxItems": 50})
        require(result.get("targetAvailabilityProbed") is False, "Catalog must not certify an arbitrary target.")
        rows = result.get("features")
        paging = result.get("pagination", {})
        require(isinstance(rows, list) and len(rows) <= 50, "Catalog page violated its bound.")
        require(type(paging.get("total")) is int and 0 <= paging["total"] <= 5000, "Invalid catalog total.")
        if expected_total is None:
            expected_total = paging["total"]
            expected_version = result.get("catalogVersion")
        require(paging["total"] == expected_total and result.get("catalogVersion") == expected_version, "Catalog changed during paging.")
        features.extend(rows)
        if paging.get("complete") is True:
            ids = [row.get("featureId") for row in features]
            require(len(features) == expected_total and all(isinstance(value, str) for value in ids)
                and len(set(ids)) == len(ids), "Catalog is incomplete or contains duplicate feature IDs.")
            summary = {"catalogVersion": expected_version, "featureCount": len(ids), "featureIds": ids}
            if include_features: summary["features"] = features
            return summary
        next_offset = paging.get("nextOffset")
        require(rows and type(next_offset) is int and next_offset == offset + len(rows), "Catalog pagination did not advance exactly once.")
        offset = next_offset
    raise ValueError("Catalog pagination exceeded 100 pages.")


def read_artifact(client, artifact):
    require(isinstance(artifact, dict) and isinstance(artifact.get("path"), str), "Export omitted its artifact path.")
    offset, contents, expected_hash, expected_length = 0, bytearray(), None, None
    for _ in range(256):
        piece = client.tool("artifact_read", {"path": artifact["path"], "offset": offset, "count": 65536})
        require(piece.get("offset") == offset and piece.get("encoding") == "base64", "Artifact transfer offset/encoding mismatch.")
        data = base64.b64decode(piece.get("data", ""), validate=True)
        require(len(data) <= 65536 and piece.get("count") == len(data) and piece.get("nextOffset") == offset + len(data), "Artifact transfer did not preserve byte boundaries.")
        if expected_hash is None:
            expected_hash, expected_length = piece.get("sha256"), piece.get("bytes")
            require(isinstance(expected_length, int) and 0 <= expected_length <= 16 * 1024 * 1024, "Export exceeded qualification's 16 MiB limit.")
        require(piece.get("sha256") == expected_hash and piece.get("bytes") == expected_length, "Artifact changed during transfer.")
        contents.extend(data)
        offset += len(data)
        if piece.get("complete") is True:
            require(offset == expected_length and hashlib.sha256(contents).hexdigest() == expected_hash, "Transferred artifact hash/length mismatch.")
            if artifact.get("sha256"):
                require(expected_hash == artifact["sha256"], "Transfer differs from the original export hash.")
            return {"bytes": offset, "sha256": expected_hash, "pathOnEngineeringHost": artifact["path"]}
        require(data and offset < expected_length, "Artifact transfer stalled.")
    raise ValueError("Artifact transfer exceeded 256 chunks.")


def qualify(configuration, report_path, timeout=300, engineering=False, simulation=False, target_label=None):
    report_path = Path(report_path)
    report = {"schemaVersion": 1, "startedAtUtc": datetime.now(timezone.utc).isoformat(),
        "configurationSha256": configuration["configurationSha256"], "transport": configuration["transport"],
        "targetLabel": target_label, "engineeringRequested": engineering, "simulationRequested": simulation, "completed": False,
        "stages": [], "sessions": [], "scope": {
            "virtualMachineQualification": "unverified", "runtimeExecution": "unverified",
            "physicalPlcAccess": "not_attempted", "note": "A target label, local run, SSH connection or simulator status does not prove VM placement or PLC runtime execution."}}

    def save():
        report_path.parent.mkdir(parents=True, exist_ok=True)
        temporary = report_path.with_name(report_path.name + ".pending")
        temporary.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
        replace_owned_pending(temporary, report_path)

    def stage(name, action, dependency=True, blocked_reason="A prerequisite did not pass."):
        started = time.monotonic()
        row = {"name": name, "status": "blocked"}
        report["stages"].append(row)
        if not dependency:
            row["reason"] = blocked_reason
            save()
            return None
        try:
            value = action()
            row.update(status="passed", result=value)
            return value
        except ToolBlocked as error:
            row.update(status="blocked", reason=str(error), error=error.response)
        except Exception as error:
            row.update(status="failed", reason=f"{type(error).__name__}: {error}")
        finally:
            row["elapsedSeconds"] = round(time.monotonic() - started, 3)
            save()
            print(row["status"].upper() + " " + name, flush=True)
        return None

    client = None
    owned = False
    save()
    try:
        def launch():
            nonlocal client
            client = McpClient(configuration, timeout)
            return {"launcherProcessId": client.process.pid, "transport": configuration["transport"]}
        launched = stage("launch_configuration", launch)
        initialized = stage("initialize", lambda: initialize(client), launched is not None)
        def list_tools():
            result = client.request("tools/list")
            require(isinstance(result, dict) and isinstance(result.get("tools"), list), "tools/list response is incomplete.")
            names = [tool.get("name") for tool in result["tools"]]
            required = {"feature_catalog", "tia_capabilities", "tia_discover", "tia_session_status"}
            if simulation:
                required.add("sim_status")
            if engineering:
                required |= {"tia_start", "tia_create_scratch", "tia_detach", "project_inventory", "block_read", "artifact_read"}
            require(all(isinstance(name, str) for name in names) and len(names) == len(set(names)), "Tool list contains invalid/duplicate names.")
            require(required <= set(names), "Required tools missing: " + ", ".join(sorted(required - set(names))))
            return {"count": len(names), "names": sorted(names)}
        listed = stage("tools", list_tools, initialized is not None)
        catalog = stage("feature_catalog", lambda: read_catalog(client), listed is not None)
        if catalog and listed:
            stage("tool_catalog_consistency", lambda: require(set(listed["names"]) <= set(catalog["featureIds"]), "Tool list and feature catalog disagree.") or {"consistent": True})
        def bridge_environment():
            result = client.tool("tia_capabilities")
            require(result.get("tiaVersion") == 21 and result.get("architecture") == "x64", "Expected the installed V21 x64 engineering bridge.")
            require(isinstance(result.get("apiAssemblyVersion"), str) and isinstance(result.get("apiDirectory"), str), "Native API evidence is missing.")
            return result
        bridge = stage("engineering_environment", bridge_environment, listed is not None)
        def discover():
            result = client.tool("tia_discover")
            require(isinstance(result.get("processes"), list), "Discovery response omitted processes.")
            return result
        discovery = stage("engineering_discovery", discover, bridge is not None)
        def detached():
            result = client.tool("tia_session_status")
            require(result.get("attached") is False and result.get("ownsPortalProcess") is False
                and result.get("projects") == [], "Fresh MCP worker unexpectedly has an attached project.")
            return result
        session = stage("detached_session", detached, bridge is not None)
        def simulator_status():
            result = client.tool("sim_status")
            if result.get("available") is False:
                raise ToolBlocked("sim_status", {"error": {"code": "SimulatorUnavailable", "message": "Simulator runtime manager reports unavailable."}, "result": result})
            require(isinstance(result.get("instances"), list) and result.get("available") is True, "Simulator runtime manager is unavailable or status is malformed.")
            return result
        if simulation:
            stage("simulator_discovery", simulator_status, listed is not None and not client.terminal)
        else:
            report["stages"].append({"name": "simulator_discovery", "status": "unverified", "reason": "Requires explicit --simulation; offline qualification does not initialize PLCSIM."})
        if engineering:
            def start_owned():
                nonlocal owned
                result = client.tool("tia_start")
                # Remember ownership before other assertions so a partial result
                # cannot suppress the eventual detach of our own instance.
                owned = result.get("ownsPortalProcess") is True
                require(owned and result.get("attached") is True and result.get("projects") == [], "tia_start did not yield an empty owned instance.")
                return result
            started = stage("owned_scratch_start", start_owned, session is not None and not client.terminal)
            def create():
                result = client.tool("tia_create_scratch")
                require(result.get("completed") is True and result.get("compile", {}).get("errorCount") == 0, "Scratch creation or compile did not complete successfully.")
                return result
            scratch = stage("owned_scratch_create_compile", create, started is not None)
            def inspect_scratch():
                result = client.tool("project_inventory", {"maxItems": 200})
                require(isinstance(result.get("blocks"), list) and isinstance(result.get("plcs"), list)
                    and any(block.get("name") == "TiaMcp_EnableGate" for block in result["blocks"]), "Scratch inventory did not contain its generated block.")
                return result
            stage("owned_scratch_inventory", inspect_scratch, scratch is not None)
            exported = stage("owned_scratch_export", lambda: client.tool("block_read", {"blockName": "TiaMcp_EnableGate"}), scratch is not None)
            stage("owned_scratch_artifact_transfer", lambda: read_artifact(client, exported.get("artifact")), exported is not None)
        else:
            report["stages"].append({"name": "owned_scratch_engineering", "status": "unverified", "reason": "Requires explicit --engineering; no project was attached, started or changed."})
    finally:
        if client:
            if owned:
                def detach_owned():
                    result = client.tool("tia_detach")
                    require(result.get("attached") is False and result.get("ownsPortalProcess") is False
                        and result.get("projects") == [] and result.get("disposedOwnedPortalConnection") is True,
                        "Owned scratch attachment release was not confirmed.")
                    return result
                stage("owned_scratch_detach", detach_owned, not client.terminal,
                      "Transport is terminal; detach was not replayed. Siemens process exit is unverified.")
            closed = client.close()
            report["sessions"].append(closed)
            stage("launcher_shutdown", lambda: require(closed["exitCode"] == 0 and not closed["forcedLauncherStop"], "Launcher did not exit cleanly on stdin EOF.") or closed)
        save()

    # Reconnection creates a new host/worker; it does not resume/replay an earlier
    # operation and never attaches to a user or orphaned scratch process.
    initial_protocol_ok = all(row["status"] == "passed" for row in report["stages"] if row["name"] in {"launch_configuration", "initialize", "tools", "feature_catalog", "launcher_shutdown"})
    if initial_protocol_ok:
        reconnect = None
        try:
            def reopen():
                nonlocal reconnect
                reconnect = McpClient(configuration, timeout)
                return initialize(reconnect)
            opened = stage("reconnect_initialize", reopen)
            stage("reconnect_catalog", lambda: read_catalog(reconnect), opened is not None)
            def fresh_session():
                result = reconnect.tool("tia_session_status")
                require(result.get("attached") is False and result.get("ownsPortalProcess") is False
                    and result.get("projects") == [], "Reconnected worker unexpectedly retained an attachment.")
                return result
            stage("reconnect_detached_session", fresh_session, opened is not None and bridge is not None)
        finally:
            if reconnect:
                closed = reconnect.close()
                report["sessions"].append(closed)
                stage("reconnect_shutdown", lambda: require(closed["exitCode"] == 0 and not closed["forcedLauncherStop"], "Reconnect launcher did not exit cleanly.") or closed)
    else:
        report["stages"].append({"name": "reconnect", "status": "blocked", "reason": "Initial transport/host qualification did not pass; no retry was attempted."})
    statuses = {row["status"] for row in report["stages"] if row["status"] != "unverified"}
    report["status"] = "failed" if "failed" in statuses else "blocked" if "blocked" in statuses else "passed"
    report["completed"] = report["status"] == "passed"
    report["finishedAtUtc"] = datetime.now(timezone.utc).isoformat()
    save()
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--server", default="tia-v21")
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--timeout", type=float, default=300, help="Per-request client bound; use a value above the server's timeout for engineering.")
    parser.add_argument("--target-label", help="Descriptive label only; does not certify remote/VM placement.")
    parser.add_argument("--engineering", action="store_true", help="Explicitly create/compile/export a new owned scratch project and transfer its XML. No existing project argument is accepted.")
    parser.add_argument("--simulation", action="store_true", help="Read PLCSIM Advanced status; does not create/power-on a CPU. Optional for offline-only installs.")
    args = parser.parse_args()
    if not 1 <= args.timeout <= 1200:
        parser.error("--timeout must be between 1 and 1200 seconds.")
    if args.config.resolve() == args.report.resolve():
        parser.error("--report must not overwrite the MCP configuration.")
    execution_attempted = False
    try:
        configuration = load_configuration(args.config, args.server)
        execution_attempted = True
        report = qualify(configuration, args.report, args.timeout, args.engineering, args.simulation, args.target_label)
        print("Report: " + str(args.report.resolve()))
        return 0 if report["completed"] else 2 if report["status"] == "blocked" else 1
    except Exception as error:
        # Preserve any stages already written if an infrastructure/reporting error
        # escapes; configuration errors produce evidence before any execution.
        args.report.parent.mkdir(parents=True, exist_ok=True)
        report = {}
        if execution_attempted and args.report.exists():
            try:
                report = json.loads(args.report.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError):
                pass
        report.update(schemaVersion=1, completed=False, status="failed", failure=f"{type(error).__name__}: {error}", executionAttempted=execution_attempted)
        args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print("FAIL " + str(error))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
