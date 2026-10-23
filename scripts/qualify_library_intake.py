"""Exercise library upload/staging through the real MCP host, without a Siemens worker.

The fixture is deliberately synthetic. Passing proves transport and intake behavior,
not native library compatibility, vendor authenticity, import or compilation.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
from pathlib import Path
import uuid
import zipfile

from smoke_mcp import Client


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def zip_bytes(entries):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in entries:
            archive.writestr(name, data)
    return stream.getvalue()


def run(host, artifacts):
    directory = artifacts.resolve() / ("intake_" + uuid.uuid4().hex[:8])
    directory.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "nativeCodeExecuted": False, "nativeQualified": False,
              "completed": False, "host": str(host.resolve()), "steps": [],
              "limitations": ["Synthetic ZIP; no native validity, vendor authenticity or engineering behavior claim."]}
    report["hostSha256"] = hashlib.sha256(host.read_bytes()).hexdigest()
    client = None

    def connect():
        # Even an accidental native call cannot start the default user's worker.
        instance = Client(host.resolve(), 30, ["--artifacts", str(directory),
                          "--worker", str(directory / "worker-intentionally-absent.exe"),
                          "--sim-worker", str(directory / "sim-worker-intentionally-absent.exe")])
        instance.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                         "clientInfo": {"name": "library-intake-qualification", "version": "1"}})
        instance.notify("notifications/initialized")
        return instance

    def record(name, result):
        report["steps"].append({"name": name, "status": "passed", "result": result})
        print("PASS " + name, flush=True)
        return result

    def failure(name, arguments, code=None):
        result = client.request("tools/call", {"name": name, "arguments": arguments})
        require(result.get("isError") is True, name + " unexpectedly succeeded")
        data = result.get("structuredContent") or json.loads("\n".join(
            block["text"] for block in result.get("content", []) if block.get("type") == "text"))
        require(isinstance(data.get("error", {}).get("code"), str), "Missing structured error")
        require(code is None or data["error"]["code"] == code, str(data))
        return data

    def begin(name, data):
        return client.tool("artifact_upload_begin", {"fileName": name, "expectedBytes": len(data),
                                                    "sha256": hashlib.sha256(data).hexdigest()})

    def chunk(identifier, offset, data):
        return client.tool("artifact_upload_chunk", {"uploadId": identifier, "offset": offset,
                                                    "data": base64.b64encode(data).decode("ascii")})

    try:
        client = connect()
        names = {tool["name"] for tool in client.request("tools/list")["tools"]}
        expected = {"artifact_upload_" + suffix for suffix in ("begin", "chunk", "status", "commit", "abort", "release")}
        expected |= {"library_package_" + suffix for suffix in ("stage", "inspect", "status", "release")}
        require(expected <= names, "Missing package tools: " + str(expected - names))
        record("tool discovery without Siemens worker", sorted(expected))

        data = zip_bytes([("Siemens-example/Library/Example.al21", "synthetic marker"),
                          ("Siemens-example/Library/System/data.bin", b"synthetic native payload"),
                          ("Siemens-example/README.txt", "Synthetic package, not from Siemens."),
                          ("Siemens-example/Examples/Demo.zap21", "synthetic archive"),
                          ("Siemens-example/Code/Example.scl", "FUNCTION Example : Bool\nEND_FUNCTION"),
                          ("Siemens-example/install.cmd", "This text must never execute")])
        started = begin("example.zip", data)
        identifier = started["uploadId"]
        split = len(data) // 2
        received = chunk(identifier, 0, data[:split])
        require(received["receivedBytes"] == split, "Chunk progress incorrect")
        client.close()
        require(client.process.returncode == 0, "First host did not exit normally")
        client = connect()
        resumed = client.tool("artifact_upload_status", {"uploadId": identifier})
        require(resumed["receivedBytes"] == split, "Host restart lost upload progress")
        repeated = chunk(identifier, 0, data[:split])
        require(repeated["receivedBytes"] == split, "Repeated chunk incorrectly advanced progress")
        record("restart and byte-identical retry", repeated)
        record("incomplete commit rejected", failure("artifact_upload_commit", {"uploadId": identifier}, "UploadIncomplete"))
        chunk(identifier, split, data[split:])
        committed = client.tool("artifact_upload_commit", {"uploadId": identifier})
        again = client.tool("artifact_upload_commit", {"uploadId": identifier})
        require(committed["artifactRef"] == again["artifactRef"], "Commit retry changed artifact")
        record("complete upload hash and repeated commit", committed)
        package = client.tool("library_package_stage", {"artifactRef": committed["artifactRef"]})
        package_id = package["packageId"]
        candidates = []
        offset = 0
        while True:
            page = client.tool("library_package_inspect", {"packageId": package_id, "count": 1, "offset": offset})
            require(page["offset"] == offset and len(page["items"]) <= 1, "Candidate paging exceeded request")
            candidates.extend(page["items"])
            if page["nextOffset"] is None:
                break
            require(page["nextOffset"] > offset, "Candidate paging made no progress")
            offset = page["nextOffset"]
        require({item["kind"] for item in candidates} == {"globalLibraryDirectory", "exampleProjectArchive", "sourceFile"}, "Candidate classification differs")
        require(all(item["completeness"] != "verified" for item in candidates), "Synthetic library falsely claimed native validity")
        record("explicit mixed-package candidates and bounded pages", candidates)
        docs = client.tool("library_package_inspect", {"packageId": package_id, "section": "documents"})
        require(any("README" in item for item in docs["items"]), "Documentation omitted")
        record("documentation retained", docs)
        record("release committed upload after independent staging", client.tool("artifact_upload_release", {"uploadId": identifier}))
        client.close()
        client = connect()
        status = client.tool("library_package_status", {"packageId": package_id})
        require(status["state"] == "ready" and status["sourceSha256"] == hashlib.sha256(data).hexdigest(), "Package lost across restart")
        record("durable package status", status)
        record("explicit package release", client.tool("library_package_release", {"packageId": package_id}))

        hostile = zip_bytes([("../escaped.txt", "must stay inside package")])
        upload = begin("hostile.zip", hostile)
        chunk(upload["uploadId"], 0, hostile)
        committed_bad = client.tool("artifact_upload_commit", {"uploadId": upload["uploadId"]})
        record("ZIP traversal rejected through MCP", failure("library_package_stage", {"artifactRef": committed_bad["artifactRef"]}, "PackageInvalid"))
        require(not (directory / "library-intake" / "escaped.txt").exists(), "ZIP traversal escaped its root")
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        if client is not None:
            client.close()
            report["hostExitCode"] = client.process.returncode
            report["hostLogs"] = client.errors
        output = directory / "qualification.json"
        output.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print("Report: " + str(output), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    arguments = parser.parse_args()
    run(arguments.host, arguments.artifacts)
