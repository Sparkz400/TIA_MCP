"""Explorer desktop roundtrip through a real MCP Apps product (Explorer/reviews); needs an operator at the desktop.

prepare: an owned TIA creates a scratch (Main, TiaMcp_EnableGate, its instance DB, TiaMcp_IsEnabled) and closes. The run
root then holds workspace/.vscode/mcp.json, which registers this script's `serve` as the stdio server, and the operator steps.
serve: the product starts it. It starts the frozen Host and relays every stdio line unchanged, appending each one to
transcript.jsonl; it never edits, drops or replays a message.
verify: checks the transcript (MCP Apps negotiation and resource read, a verified selection followed by a
project_model_details call with the identical six-field reference, and that reference refused after tia_detach), reads
the selected block with block_read in a fresh owned TIA, checks the artifact fallback with a client that does not
negotiate MCP Apps, and asks the operator what only the screen shows. Nothing in the project is changed.

Operator steps (also written to <root>/operator-instructions.txt):
1. Open the folder <root>/workspace in VS Code (or another MCP Apps product) and start the server "tia-mcp-desk".
   Open the product's developer tools (VS Code: Help > Toggle Developer Tools) on the Network tab.
2. In agent chat: "Call tia_start, then project_open with path <project>, then project_model_capture with
   lifetimeSeconds 900."
3. In the Explorer view, search TiaMcp_EnableGate, select the function block and press "Send selection". Take a screenshot.
4. The agent must answer by calling project_model_details with the sent reference. If it calls anything else first,
   ask: "Call project_model_details with exactly the reference I sent."
5. Ask: "Call tia_detach." Then press "Send selection" again in the view. The view must show the stale or disconnected
   state. Take a screenshot.
6. Save both screenshots under <root>/screenshots, stop the server and close the product.
7. Run: python <script> verify --root <root> --host <host> --worker <worker>, and answer its questions.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import threading
import time

URI, MIME = "ui://tia-project-explorer/view.html", "text/html;profile=mcp-app"
FIELDS = {"schemaVersion", "snapshotId", "sessionGeneration", "projectPath", "nodeId", "objectPath"}
# E1 accepts the same codes for a reference reused after a disconnect.
STALE = {"SessionMismatch", "StaleSelection", "SnapshotInvalidated", "SnapshotNotFound"}
BLOCK_FIELDS = ("path", "name", "number", "kind", "programmingLanguage")
QUESTIONS = (("rendered", "Did the Explorer render inside the product's chat after project_model_capture?"),
             ("noNetwork", "Did the developer tools show no network request from the Explorer view?"),
             ("staleShown", "After tia_detach, did the view show the stale or disconnected state for the reused selection?"))
INSTRUCTIONS = __doc__[__doc__.index("Operator steps"):]


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def now():
    return datetime.now(timezone.utc).isoformat()


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def body(result):
    """structuredContent, or the JSON of the text content."""
    if "structuredContent" in (result or {}):
        return result["structuredContent"]
    text = "\n".join(c.get("text", "") for c in (result or {}).get("content", []) if c.get("type") == "text")
    try:
        return json.loads(text)
    except ValueError:
        return {"text": text}


def exchanges(records):
    """Client requests in order, each with its session, response and position; server-to-client requests are skipped."""
    rows, pending = [], {}
    for index, record in enumerate(records):
        message = record.get("message")
        if not isinstance(message, dict):
            continue
        key = (record["session"], message.get("id"))
        if record["dir"] == "c2s" and "method" in message and "id" in message:
            pending[key] = row = {"session": record["session"], "index": index, "method": message["method"], "params": message.get("params") or {}}
            rows.append(row)
        elif record["dir"] == "s2c" and "method" not in message and key in pending:
            pending.pop(key).update(result=message.get("result"), error=message.get("error"))
    return rows


def model_call(row, action):
    """The project_model request of a tools/call for one action, by alias or by request.action."""
    if row["method"] != "tools/call":
        return None
    name, request = row["params"].get("name"), (row["params"].get("arguments") or {}).get("request") or {}
    if name == "project_model_" + action or name == "project_model" and request.get("action") == action:
        return request
    return None


def failed(row):
    return row.get("error") is not None or (row.get("result") or {}).get("isError") is True


def review(records):
    """Every transcript gate of the desktop roundtrip; raises on the first unmet gate."""
    rows = exchanges(records)
    starts = [r for r in rows if r["method"] == "initialize"]
    negotiated = [r for r in starts if MIME in ((r["params"].get("capabilities") or {}).get("extensions") or {}).get("io.modelcontextprotocol/ui", {}).get("mimeTypes", [])]
    require(negotiated, "No session negotiated MCP Apps (io.modelcontextprotocol/ui with %s)" % MIME)
    session = negotiated[-1]["session"]
    rows = [r for r in rows if r["session"] == session]
    listed = [t for r in rows if r["method"] == "tools/list" and not failed(r) for t in r["result"]["tools"] if t["name"] == "project_model_capture"]
    require(listed and all(((t.get("_meta") or {}).get("ui") or {}).get("resourceUri") == URI for t in listed), "tools/list did not offer the Explorer resource")
    reads = [r for r in rows if r["method"] == "resources/read" and r["params"].get("uri") == URI and not failed(r)]
    require(reads and reads[0]["result"]["contents"][0]["mimeType"] == MIME, "The product never read the Explorer resource")
    captures = [r for r in rows if model_call(r, "capture") is not None and not failed(r)]
    require(captures, "No successful project_model capture")
    verified = [r for r in rows if (model_call(r, "verify") or {}).get("reference") and not failed(r) and body(r["result"]).get("valid") is True
                and body(r["result"]).get("referenceVerified") is True]
    require(verified, "The view never verified a selection (Send selection)")
    sent = verified[0]
    reference = model_call(sent, "verify")["reference"]
    require(set(reference) == FIELDS and "/blocks/" in reference["objectPath"], "The sent reference is not an exact six-field block reference: %s" % reference)
    require(reference["snapshotId"] in {body(r["result"]).get("snapshotId") for r in captures}, "The sent reference names no captured snapshot")
    details = [r for r in rows if r["index"] > sent["index"] and (model_call(r, "details") or {}).get("reference") == reference]
    require(details and not failed(details[0]), "No successful project_model_details with the identical reference after the selection was sent")
    detach = [r for r in rows if r["index"] > details[0]["index"] and r["method"] == "tools/call" and r["params"].get("name") == "tia_detach" and not failed(r)]
    require(detach, "No successful tia_detach after the details call")
    reused = [r for r in rows if r["index"] > detach[0]["index"] and any((model_call(r, a) or {}).get("reference") == reference for a in ("verify", "details"))]
    require(reused and failed(reused[0]), "The reference was not reused after tia_detach, or it was accepted")
    code = (body(reused[0]["result"]).get("error") or {}).get("code") if reused[0].get("result") else None
    require(code in STALE, "The reused reference was refused with %s, not one of %s" % (code, sorted(STALE)))
    return {"session": session, "clientInfo": negotiated[-1]["params"].get("clientInfo"), "reference": reference,
            "verification": body(sent["result"]), "details": body(details[0]["result"]), "staleCode": code,
            "order": {"capture": captures[0]["index"], "sent": sent["index"], "details": details[0]["index"], "detach": detach[0]["index"], "reused": reused[0]["index"]}}


def same_block(details, direct):
    """The detail provider's block identity equals block_read's on every field both report."""
    shown = (details.get("details") or {}).get("block") or {}
    keys = [k for k in BLOCK_FIELDS if k in shown and k in direct]
    require("path" in keys and all(shown[k] == direct[k] for k in keys), "Details and block_read differ: %s / %s" % (shown, direct))
    return {k: direct[k] for k in keys}


def offline_html(text):
    """An exported viewer loads nothing from the network."""
    remote = re.findall(r"""(?:src|href|action)\s*=\s*["']?\s*(?:https?:)?//""", text, re.I) + re.findall(r"@import\s+url\(\s*['\"]?https?:", text, re.I)
    require(not remote, "The exported viewer references remote resources: %s" % remote[:5])


def host_client(args, root):
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from smoke_mcp import Client
    client = Client(args.host.resolve(), args.timeout, ["--worker", str(args.worker.resolve()), "--artifacts", str(root / "h")])
    client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "explorer-desktop-qualification", "version": "1"}})
    client.notify("notifications/initialized")
    return client


def binaries(args):
    return {name: {"path": str(p.resolve()), "sha256": sha256(p)} for name, p in (("host", args.host.with_suffix(".dll")), ("worker", args.worker), ("runner", Path(__file__)))}


def instructions(root, project, args):
    verify = '"%s" verify --root "%s" --host "%s" --worker "%s"' % (Path(__file__).resolve(), root, args.host.resolve(), args.worker.resolve())
    text = INSTRUCTIONS.replace("<script> verify --root <root> --host <host> --worker <worker>", verify)
    text = re.sub(r"<root>/([\w.-]+)", lambda match: str(root / match.group(1)), text)
    return text.replace("<root>", str(root)).replace("<project>", project)


def prepare(args):
    root = args.root.resolve()
    require(not root.exists(), "Choose a new --root")
    root.mkdir(parents=True)
    report = {"schemaVersion": 1, "startedAtUtc": now(), **binaries(args)}
    client = host_client(args, root)
    try:
        started = client.tool("tia_start")
        require(started.get("ownsPortalProcess") is True and not started.get("projects"), "Expected a new owned, empty Portal")
        scratch = client.tool("tia_create_scratch")
        require(scratch.get("completed") is True and scratch["compile"]["errorCount"] == 0, "Scratch compile failed")
        report["project"] = scratch["project"]
    finally:
        client.tool("tia_detach")
        report["hostExits"] = [client.close()]
    project = report["project"]["path"]
    workspace = root / "workspace" / ".vscode"
    workspace.mkdir(parents=True)
    (root / "screenshots").mkdir()
    serve = [str(Path(__file__).resolve()), "serve", "--root", str(root), "--host", str(args.host.resolve()), "--worker", str(args.worker.resolve())]
    (workspace / "mcp.json").write_text(json.dumps({"servers": {"tia-mcp-desk": {"type": "stdio", "command": sys.executable, "args": serve}}}, indent=2), encoding="utf-8")
    (root / "operator-instructions.txt").write_text(instructions(root, project, args), encoding="utf-8")
    report["completedAtUtc"] = now()
    (root / "prepare.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print((root / "operator-instructions.txt").read_text(encoding="utf-8"), flush=True)


def serve(args):
    """Relay stdio between the product and the Host; the transcript only appends."""
    root = args.root.resolve()
    path, lock = root / "transcript.jsonl", threading.Lock()
    session = 1 + (sum(1 for line in path.read_text(encoding="utf-8").splitlines() if "start" in json.loads(line)) if path.exists() else 0)
    out = path.open("a", encoding="utf-8")
    def log(direction, line):
        try:
            entry = {"message": json.loads(line)}
        except ValueError:
            entry = {"raw": line.decode("utf-8", "replace")}
        with lock:
            out.write(json.dumps({"t": now(), "session": session, "dir": direction, **entry}) + "\n")
            out.flush()
    with lock:
        out.write(json.dumps({"t": now(), "session": session, "start": binaries(args)}) + "\n")
        out.flush()
    host = subprocess.Popen([str(args.host.resolve()), "--worker", str(args.worker.resolve()), "--artifacts", str(root / "h")],
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    def pump():
        for line in sys.stdin.buffer:
            log("c2s", line)
            host.stdin.write(line)
            host.stdin.flush()
        host.stdin.close()
    threading.Thread(target=pump, daemon=True).start()
    for line in host.stdout:
        log("s2c", line)
        sys.stdout.buffer.write(line)
        sys.stdout.buffer.flush()
    with lock:
        out.write(json.dumps({"t": now(), "session": session, "end": host.wait()}) + "\n")
    out.close()


def verify(args):
    root = args.root.resolve()
    report = {"schemaVersion": 1, "completed": False, "startedAtUtc": now(), "steps": [], **binaries(args),
              "prepare": json.loads((root / "prepare.json").read_text(encoding="utf-8")),
              "transcript": {"path": str(root / "transcript.jsonl"), "sha256": sha256(root / "transcript.jsonl")},
              "screenshots": [{"name": p.name, "sha256": sha256(p)} for p in sorted((root / "screenshots").iterdir())]}
    def step(name, result):
        report["steps"].append({"name": name, "result": result})
        print("PASS " + name, flush=True)
        return result
    client = None
    try:
        records = [json.loads(line) for line in (root / "transcript.jsonl").read_text(encoding="utf-8").splitlines() if line.strip()]
        found = step("desktop transcript: negotiation, exact selection roundtrip, refusal after detach", review([r for r in records if "dir" in r]))
        require(Path(found["reference"]["projectPath"]).resolve() == Path(report["prepare"]["project"]["path"]).resolve(), "The selection names another project")
        client = host_client(args, root)
        tools = client.request("tools/list")["tools"]
        require(all("resourceUri" not in ((t.get("_meta") or {}).get("ui") or {}) for t in tools)
                and URI not in [r["uri"] for r in client.request("resources/list")["resources"]], "App UI offered without negotiation")
        step("an unnegotiated client sees no app UI", len(tools))
        client.tool("tia_start")
        for attempt in range(13):  # The desk session's Openness attachment can hold TIA's project lock for up to two minutes.
            opened = client.request("tools/call", {"name": "project_open", "arguments": {"path": found["reference"]["projectPath"]}})
            if not opened.get("isError"):
                break
            require(attempt < 12 and "already been opened" in json.dumps(opened), "project_open failed: %s" % json.dumps(opened)[:500])
            time.sleep(15)
        direct = client.tool("block_read", {"blockPath": found["reference"]["objectPath"]})
        require(Path(direct["project"]["path"]).resolve() == Path(found["reference"]["projectPath"]).resolve(), "block_read answered from another project")
        step("independent block_read equals the details the product received", same_block(found["details"], direct["block"]))
        capture = client.tool("project_model_capture", {"request": {"lifetimeSeconds": 300}})
        exported = client.tool("project_model_export", {"request": {"snapshotId": capture["snapshotId"], "format": "html"}})["artifact"]
        html = Path(exported["path"])
        require(exported["networkRequired"] is False and sha256(html) == exported["sha256"], "Export artifact differs from its manifest")
        offline_html(html.read_text(encoding="utf-8"))
        step("artifact fallback: structured capture and an offline HTML viewer without MCP Apps", {"snapshotId": capture["snapshotId"], "artifact": exported})
        answers = {key: input(question + " [y/n] ").strip().lower() == "y" for key, question in QUESTIONS}
        report["operator"] = {"product": input("Product name and version (for example VS Code 1.140.0): ").strip(), "answers": answers}
        require(all(answers.values()), "The operator did not confirm: %s" % answers)
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        if client:
            try:
                client.tool("tia_detach")
            except Exception as error:
                report["detachFailure"] = str(error)
            report["hostExits"] = [client.close()]
        report["completedAtUtc"] = now()
        path = args.report or root / "verification.json"
        path.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print("Report: " + str(path), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("mode", choices=("prepare", "serve", "verify"))
    parser.add_argument("--root", type=Path, required=True, help="Run root, short (TIA refuses project paths past about 140 characters).")
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--timeout", type=int, default=600)
    args = parser.parse_args()
    {"prepare": prepare, "serve": serve, "verify": verify}[args.mode](args)
