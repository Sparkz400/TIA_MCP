"""Exercise the actual stdio MCP server. Default is discovery; --scratch explicitly creates only a new test project."""
import argparse
import json
import pathlib
import queue
import subprocess
import threading
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]


class HostExited(RuntimeError):
    """The Host ended or closed its input before answering. The message starts "MCP host exited" and carries its last stderr lines."""


OUTPUT_SCHEMA_META = "tiaMcp/outputSchema"


def all_toolsets(command):
    """The Host advertises only base and core by default; harnesses exercise every tool, so they ask for all of them
    unless the command already names its own toolsets. A launcher script takes -Toolsets, the Host executable --toolsets."""
    if any(str(part).lower() in ("--toolsets", "-toolsets") for part in command):
        return list(command)
    launcher = any(str(part).lower().endswith("start-tiamcp.ps1") for part in command)
    return [*command, *(("-Toolsets", "all") if launcher else ("--toolsets", "all"))]


class Client:
    def __init__(self, executable, timeout, extra_arguments=None, command=None):
        self.timeout = timeout
        self.sequence = 0
        self.lines = queue.Queue()
        self.errors = []
        self.tool_calls = []  # Curated evidence records which tools answered without error.
        self.output_schemas = None  # Advertised outputSchema per tool, loaded before the first tools/call.
        self.output_schema_uris = {}  # Tools whose schema is a resource: _meta["tiaMcp/outputSchema"], {kind} filled per call.
        self.exited = False  # Set once the Host is gone, so later requests fail at once instead of waiting out the timeout.
        self.process = subprocess.Popen(
            command if command is not None else all_toolsets([str(executable), "--timeout-seconds", str(timeout), *(extra_arguments or [])]),
            cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace", bufsize=1,
        )
        self.output_reader = threading.Thread(target=self._read_output, daemon=True)
        self.error_reader = threading.Thread(target=self._read_errors, daemon=True)
        self.output_reader.start()
        self.error_reader.start()

    def _read_output(self):
        for line in self.process.stdout:
            self.lines.put(line)
        self.lines.put(None)

    def _read_errors(self):
        for line in self.process.stderr:
            self.errors.append(line.rstrip())

    def _host_exited(self, reason):
        # Let the stderr reader drain, so the startup failure (bad option, missing worker) is part of the message.
        self.exited = True
        # Harnesses that build a client with Client.__new__ set only the attributes they use.
        reader = getattr(self, "error_reader", None)
        if reader is not None:
            reader.join(timeout=2)
        poll = getattr(self.process, "poll", None)
        code = poll() if poll is not None else None
        status = f"exit code {code}" if code is not None else "process still running"
        return HostExited(f"MCP host exited {reason} ({status}): " + "\n".join(getattr(self, "errors", [])[-10:]))

    def _send(self, message):
        # Only a Host already seen gone refuses here; a dead pipe fails on the write and a closed stdout on the read.
        if getattr(self, "exited", False):
            raise self._host_exited("before the request was sent")
        try:
            self.process.stdin.write(json.dumps(message) + "\n")
            self.process.stdin.flush()
        except (BrokenPipeError, OSError, ValueError) as error:
            raise self._host_exited(f"while it was being written to ({type(error).__name__})") from error

    def notify(self, method, params=None):
        self._send({"jsonrpc": "2.0", "method": method, "params": params or {}})

    def request(self, method, params=None):
        # The change tools answer with a summary by default; qualification runners read full previews and journals.
        # Here, because many runners send tools/call themselves instead of through tool().
        if method == "tools/call" and params.get("name") in ("change_prepare", "change_apply", "change_status"):
            params = {**params, "arguments": {"includeDetails": True, **(params.get("arguments") or {})}}
        if method == "tools/call" and self.output_schemas is None:
            listed = self.request("tools/list")["tools"]
            self.output_schemas = {tool["name"]: tool["outputSchema"] for tool in listed if "outputSchema" in tool}
            self.output_schema_uris = {tool["name"]: tool["_meta"][OUTPUT_SCHEMA_META] for tool in listed
                                       if OUTPUT_SCHEMA_META in (tool.get("_meta") or {})}
        self.sequence += 1
        self._send({"jsonrpc": "2.0", "id": self.sequence, "method": method, "params": params or {}})
        deadline = time.monotonic() + self.timeout + 15
        while True:
            try:
                line = self.lines.get(timeout=max(0.01, deadline - time.monotonic()))
            except queue.Empty:
                if getattr(self.process, "poll", lambda: None)() is not None:
                    raise self._host_exited(f"while {method} was pending") from None
                raise TimeoutError(f"MCP host sent no response to {method} within {self.timeout + 15} s.") from None
            if line is None:
                raise self._host_exited(f"before answering {method}")
            response = json.loads(line)
            if response.get("id") != self.sequence:
                continue
            if "error" in response:
                raise RuntimeError(response["error"])
            if method == "tools/call":
                self.tool_calls.append({"name": params["name"], "isError": bool(response["result"].get("isError"))})
                self.check_output(params["name"], response["result"], params.get("arguments") or {})
            return response["result"]

    def check_output(self, name, result, arguments=None):
        """Every structured result, success or refusal, must match the tool's advertised outputSchema, inline or as the
        tia://output-schemas resource its _meta names (review_* tools by record kind)."""
        uri = self.output_schema_uris.get(name)
        if uri is not None:
            uri = uri.replace("{kind}", str((arguments or {}).get("kind")))
            if uri not in self.output_schemas:
                try:
                    self.output_schemas[uri] = json.loads(self.request("resources/read", {"uri": uri})["contents"][0]["text"])
                except RuntimeError:
                    self.output_schemas[uri] = None  # A record kind without a declared result contract.
            name = uri
        schema = (self.output_schemas or {}).get(name)
        if schema is not None and "structuredContent" in result:
            if not hasattr(schema, "validate"):
                from jsonschema import Draft202012Validator  # Only Hosts that advertise schemas need it.
                schema = self.output_schemas[name] = Draft202012Validator(schema)
            schema.validate(result["structuredContent"])

    def tool(self, name, arguments=None):
        result = self.request("tools/call", {"name": name, "arguments": arguments or {}})
        if result.get("isError"):
            raise RuntimeError(f"{name}: {result}")
        if "structuredContent" in result:
            return result["structuredContent"]
        content = [item["text"] for item in result.get("content", []) if item.get("type") == "text"]
        return json.loads("\n".join(content))

    def close(self):
        forced = False
        try:
            self.process.stdin.close()
        except (BrokenPipeError, OSError):
            pass
        try:
            # The host can wait up to 120 seconds (BridgeClient.WorkerExitBudgetSeconds) for each of two native workers.
            self.process.wait(timeout=255)
        except subprocess.TimeoutExpired:
            forced = True
            self.process.kill()  # This owns only the MCP host, never a Siemens process.
            self.process.wait(timeout=5)
        self.output_reader.join(timeout=1)
        self.error_reader.join(timeout=1)
        for reader, stream in ((self.output_reader, self.process.stdout), (self.error_reader, self.process.stderr)):
            if not reader.is_alive():
                stream.close()
        receipts = []
        for line in self.errors:
            try:
                value = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(value, dict) and isinstance(value.get("bridgeWorkerExit"), dict):
                receipts.append(value["bridgeWorkerExit"])
        return {"hostExitCode": self.process.returncode, "forcedHostStop": forced,
                "workerExits": receipts, "stderrEofObserved": not self.error_reader.is_alive(),
                "toolCalls": self.tool_calls}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scratch", action="store_true", help="Explicitly start TIA and create a separate new scratch project.")
    parser.add_argument("--attach", type=int, help="Read-only attachment to this existing V21 process after other checks.")
    parser.add_argument("--export-languages", action="store_true", help="With --attach, export one eligible block per standard language to local artifacts.")
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--report", type=pathlib.Path)
    parser.add_argument("--host", type=pathlib.Path, help="Host executable to test, including a packaged host.")
    parser.add_argument("--worker", type=pathlib.Path, help="Explicit engineering worker executable.")
    parser.add_argument("--sim-worker", type=pathlib.Path, help="Explicit simulator worker executable.")
    parser.add_argument("--artifacts", type=pathlib.Path, help="Artifact directory on the engineering machine.")
    args = parser.parse_args()
    executable = args.host or ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe"
    extra_arguments = []
    for option, path in (("--worker", args.worker), ("--sim-worker", args.sim_worker), ("--artifacts", args.artifacts)):
        if path:
            extra_arguments.extend((option, str(path.resolve())))
    client = Client(executable, args.timeout, extra_arguments)
    report = {"steps": [], "completed": False}

    def step(name, value):
        report["steps"].append({"name": name, "result": value})
        print(f"PASS {name}", flush=True)
        return value

    try:
        initialization = client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "tia-mcp-smoke", "version": "0.1.0"}})
        step("initialize", initialization)
        client.notify("notifications/initialized")
        listed = client.request("tools/list")
        names = [tool["name"] for tool in listed["tools"]]
        assert {"tia_discover", "tia_capabilities", "tia_create_scratch", "xref_query", "block_read"}.issubset(names), names
        step("tools/list", names)
        step("capabilities", client.tool("tia_capabilities"))
        step("discovery", client.tool("tia_discover"))
        if args.scratch:
            step("start isolated TIA", client.tool("tia_start"))
            scratch = step("create scratch", client.tool("tia_create_scratch"))
            assert scratch.get("completed") is True, scratch
            assert scratch.get("compile", {}).get("errorCount") == 0, scratch
            inventory = step("scratch inventory", client.tool("project_inventory"))
            step("scratch block export", client.tool("block_read", {"blockName": "TiaMcp_EnableGate"}))
            xrefs = step("scratch references", client.tool("xref_query", {"blockName": "TiaMcp_IsEnabled"}))
            assert "TiaMcp_EnableGate" in json.dumps(xrefs), "Expected FC-to-FB reference is missing"
            step("detach scratch", client.tool("tia_detach"))
        if args.attach:
            step("attach existing TIA", client.tool("tia_attach", {"processId": args.attach}))
            inventory = step("existing project inventory", client.tool("project_inventory", {"maxItems": 2000 if args.export_languages else 100}))
            if args.export_languages:
                samples = {}
                for block in inventory.get("blocks", []):
                    language = block.get("programmingLanguage")
                    if language in {"LAD", "FBD", "STL", "SCL"} and block.get("isConsistent") and not block.get("isKnowHowProtected"):
                        samples.setdefault(language, block)
                for language, block in samples.items():
                    step(f"existing {language} XML export", client.tool("block_read", {"blockPath": block["path"]}))
                step("language export coverage", {"exported": sorted(samples), "notObserved": sorted({"LAD", "FBD", "STL", "SCL"} - samples.keys()), "inventoryCompleteness": inventory.get("completeness")})
            detached = step("detach existing TIA", client.tool("tia_detach"))
            assert detached.get("disposedOwnedPortalConnection") is False, detached
            discovered = step("existing TIA remains open", client.tool("tia_discover"))
            assert any(p["processId"] == args.attach for p in discovered["processes"]), discovered
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        client.close()
        report["hostExitCode"] = client.process.returncode
        report["hostLogs"] = client.errors
        report["toolCalls"] = client.tool_calls
        destination = args.report or ROOT / "artifacts" / ("smoke-" + time.strftime("%Y%m%d-%H%M%S") + ".json")
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
        print(f"Report: {destination}", flush=True)


if __name__ == "__main__":
    main()
