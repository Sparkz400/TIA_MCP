"""Exercise the actual stdio MCP server. Default is discovery; --scratch explicitly creates only a new test project."""
import argparse
import json
import pathlib
import queue
import subprocess
import threading
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]


class Client:
    def __init__(self, executable, timeout, extra_arguments=None, command=None):
        self.timeout = timeout
        self.sequence = 0
        self.lines = queue.Queue()
        self.errors = []
        self.tool_calls = []  # Curated evidence records which tools answered without error.
        self.process = subprocess.Popen(
            command if command is not None else [str(executable), "--timeout-seconds", str(timeout), *(extra_arguments or [])],
            cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", bufsize=1,
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

    def notify(self, method, params=None):
        self.process.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method, "params": params or {}}) + "\n")
        self.process.stdin.flush()

    def request(self, method, params=None):
        self.sequence += 1
        self.process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.sequence, "method": method, "params": params or {}}) + "\n")
        self.process.stdin.flush()
        deadline = time.monotonic() + self.timeout + 15
        while True:
            line = self.lines.get(timeout=max(0.01, deadline - time.monotonic()))
            if line is None:
                raise RuntimeError("MCP host exited: " + "\n".join(self.errors[-10:]))
            response = json.loads(line)
            if response.get("id") != self.sequence:
                continue
            if "error" in response:
                raise RuntimeError(response["error"])
            if method == "tools/call":
                self.tool_calls.append({"name": params["name"], "isError": bool(response["result"].get("isError"))})
            return response["result"]

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
