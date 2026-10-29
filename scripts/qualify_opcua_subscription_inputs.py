"""Actual Host subscription input guard, healthy encrypted session, and unchanged shared-node behavior."""
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time
import uuid

from smoke_mcp import Client, ROOT


def check_unknown_subscription_inputs(client, request, fixture, store, check, responses):
    """Shared by focused, aggregate and relocated-package qualification."""
    baseline = fixture("audit")
    def files():
        return sorted(str(p.relative_to(store)) for p in store.rglob("*") if p.is_file())
    paths = files()
    variants = []
    for key in ("reconnect", "transfer", "write", "resume", "maximumNotification", "discardOldets"):
        value = copy.deepcopy(request); value[key] = True; variants.append(("request." + key, value))
    for key in ("namespaceIndex", "identifierTypo", "reconnect"):
        value = copy.deepcopy(request); value["nodes"][0][key] = True; variants.append(("node." + key, value))
    for key in ("acknowledge", "minimumSeverty"):
        value = copy.deepcopy(request); value["events"] = dict(notifier=value["nodes"][0], **{key: True}); variants.append(("events." + key, value))
    value = copy.deepcopy(request); value["events"] = dict(notifier={**value["nodes"][0], "namespaceIndex": 1}); variants.append(("events.notifier.namespaceIndex", value))
    for label, value in variants:
        arguments = dict(captureId=uuid.uuid4().hex, request=value)
        response = client.request("tools/call", dict(name="runtime_ua_subscription_start", arguments=arguments))
        responses.append(dict(tool="subscription_start", arguments=arguments, response=response))
        check(response.get("isError") and "structuredContent" not in response and
              response.get("content") == [dict(type="text", text="An error occurred invoking 'runtime_ua_subscription_start'.")],
              "MCP deserialization refuses " + label + " before subscription facade")
        check(fixture("audit") == baseline, "No remote service for " + label)
        check(paths == files(), "No retained intent for " + label)
    return baseline


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=Path)
    parser.add_argument("--server", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    host, server_dll, out = args.host.resolve(strict=True), args.server.resolve(strict=True), args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    report = dict(status="failed", checks=[], responses=[], nativeStarted=False, physicalQualified=False)
    def check(condition, name):
        if not condition:
            raise AssertionError(name)
        report["checks"].append(name)
    def hashes():
        return {str(p): hashlib.sha256(p.read_bytes()).hexdigest()
                for folder in (host.parent, server_dll.parent) for p in sorted(folder.iterdir())
                if p.is_file() and p.suffix in {".dll", ".exe", ".json"}}
    report["inputsBefore"] = hashes()
    source_names = ["src/TiaMcp.OpcUa.Runtime/OpcUaSubscriptionContracts.cs", "src/TiaMcp.Host/OpcUaSubscriptionTools.cs",
                    "scripts/qualify_opcua_subscription_inputs.py", "tests/TiaMcp.OpcUa.Runtime.Tests/SubscriptionInputChecks.cs"]
    report["sourceHashes"] = {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in source_names}
    client = None; server = None; server_lines = queue.Queue(); server_errors = []
    def fixture(operation):
        server.stdin.write(json.dumps(dict(operation=operation)) + "\n"); server.stdin.flush()
        return json.loads(server_lines.get(timeout=20))
    def call(name, arguments):
        response = client.request("tools/call", dict(name="runtime_ua_" + name, arguments=arguments))
        report["responses"].append(dict(tool=name, arguments=arguments, response=response))
        return response
    try:
        server = subprocess.Popen(["dotnet", str(server_dll), "--serve", "--output", str(out / "server")],
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        def read_lines():
            for line in server.stdout:
                server_lines.put(line)
        def read_errors():
            for line in server.stderr:
                server_errors.append(line.rstrip())
        threading.Thread(target=read_lines, daemon=True).start(); threading.Thread(target=read_errors, daemon=True).start()
        check(json.loads(server_lines.get(timeout=30)).get("ready"), "Independent encrypted fixture ready")
        ready = json.loads((out / "server/ready.json").read_text(encoding="utf-8"))
        check(ready["processId"] == server.pid, "Fixture matches original owned process")
        client = Client(host, 20, ["--no-native-workers", "--artifacts", str(out / "store")])
        client.request("initialize", dict(protocolVersion="2025-11-25", capabilities={}, clientInfo=dict(name="ua-input-guard", version="1")))
        client.notify("notifications/initialized")
        report["tools"] = [tool for tool in client.request("tools/list")["tools"] if tool["name"].startswith("runtime_ua_subscription_")]
        descriptor = next(tool["inputSchema"] for tool in report["tools"] if tool["name"] == "runtime_ua_subscription_start")
        request_schema = descriptor["properties"]["request"]
        node_schema = request_schema["properties"]["nodes"]
        check(request_schema.get("additionalProperties") is False and request_schema["required"] == ["attemptId", "nodes"], "Advertised request remains strict with required fields")
        check(node_schema["type"] == "array" and node_schema["items"]["type"] == "object" and
              node_schema["items"]["required"] == ["namespaceUri", "identifierType", "identifier"] and
              node_schema["items"]["properties"] == {key: dict(type="string") for key in ("namespaceUri", "identifierType", "identifier")},
              "Converter preserves advertised array and exact node field types")
        defaults = dict(publishingIntervalMilliseconds=100, samplingIntervalMilliseconds=100, queueSize=16,
                        discardOldest=True, keepAliveCount=10, lifetimeCount=30, durationSeconds=10,
                        maximumNotifications=10000, maximumBytes=67108864, maximumRepublish=16)
        check({key: request_schema["properties"][key]["default"] for key in defaults} == defaults, "All ten advertised optional defaults unchanged")
        identity = call("client_identity", {})["structuredContent"]
        for binary in identity["runtimeBinaries"]:
            check(hashlib.sha256((host.parent / (binary["name"] + ".dll")).read_bytes()).hexdigest() == binary["sha256"], "Loaded binary " + binary["name"])
        attempt = uuid.uuid4().hex
        status = call("connect", dict(attemptId=attempt, selection=ready["selection"]))["structuredContent"]
        check(status["state"] == "connected" and status["peerVerified"], "Known healthy pinned encrypted session")
        node = dict(namespaceUri=ready["namespaceUri"], identifierType="string", identifier="bool")
        request = dict(attemptId=attempt, nodes=[node], durationSeconds=2, maximumNotifications=1)
        baseline = check_unknown_subscription_inputs(client, request, fixture, out / "store", check, report["responses"])
        response = call("subscription_start", dict(captureId=uuid.uuid4().hex, request=dict(request, queueSize=0)))
        check(response.get("isError") and response["structuredContent"]["error"]["code"] == "UaSubscriptionInvalid", "Known invalid bound still reaches typed runtime validation")
        check(fixture("audit") == baseline, "Bound refusal emits no remote service")
        # An unchanged shared node still follows the established manual-read profile.
        response = call("read", dict(attemptId=attempt, nodes=[dict(node, legacyExtra=True)]))
        check(not response.get("isError") and response["structuredContent"]["items"][0]["node"] == node, "Existing manual-read node deserialization unchanged")
        capture = uuid.uuid4().hex
        check(not call("subscription_start", dict(captureId=capture, request=request)).get("isError"), "Same known request accepted without extra options")
        deadline = time.monotonic() + 20
        while True:
            response = call("subscription_status", dict(captureId=capture))["structuredContent"]
            if response["state"] != "running":
                break
            check(time.monotonic() < deadline, "Valid request completes within bound")
            time.sleep(0.1)
        check(response["recordedProgress"]["notifications"] == 1 and response["recordedProgress"]["remoteCleanupConfirmed"], "Valid request acquires exactly one value and confirms deletion")
        page = call("subscription_page", dict(snapshotRef=response["snapshotRef"]))["structuredContent"]
        created = next(row["event"]["created"] for row in page["rows"] if row["event"]["kind"] == "created")
        check(created["items"][0]["node"] == node, "Exact nested node survives request and retained readback")
        # Inspect, rather than change, the SDK's established treatment of unbound outer tool arguments.
        outer = call("subscription_status", dict(captureId=capture, reconnect=True))
        report["outerToolArgumentObservation"] = dict(scope="Existing MCP binder, outside request record", accepted=not outer.get("isError"))
        check(call("disconnect", dict(attemptId=attempt))["structuredContent"]["state"] == "closed", "Original session closes")
        check(fixture("audit")["sessions"] == 0, "Independent server confirms session cleanup")
        report["status"] = "passed"
    except Exception as error:
        report["error"] = repr(error)
        raise
    finally:
        if client is not None:
            report["hostCleanup"] = client.close()
        if server is not None:
            if server.poll() is None:
                server.stdin.write('{"operation":"stop"}\n'); server.stdin.flush(); server.stdin.close()
                server.wait(timeout=30)
            report["serverCleanup"] = dict(exitCode=server.returncode, forced=False)
        report["inputsAfter"] = hashes()
        check(report["inputsBefore"] == report["inputsAfter"], "Exact binary inputs unchanged during qualification")
        with (out / "result.json").open("x", encoding="utf-8") as stream:
            json.dump(report, stream, indent=2); stream.write("\n")
        (out / "server-stderr.txt").write_text("\n".join(server_errors), encoding="utf-8")
    print(json.dumps(dict(status=report["status"], checks=len(report["checks"]))))


if __name__ == "__main__":
    main()
