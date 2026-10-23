"""Finish read-only persistence/recovery and optional runtime gates of a saved run.

Requires proven native apply/preservation and checkpoint recovery from the first
report. Never reapplies an uncertain typed change. Both report hashes and exact
binary manifests are retained; an input mismatch rejects the continuation.
"""
import argparse
import json
import pathlib

from block_test_executor import execute_suite
from block_test_runtime import McpRuntimeBackend, NativeAudit, canonical, digest, utc_now
from run_conformance import native_document
from engineering_preservation import PAYLOAD_HASH_POLICY, native_payload_hash as native_hash, require
from run_block_tests import binary_manifest
from smoke_mcp import Client, ROOT


class Audit(NativeAudit):
    ALLOWED_TOOLS = NativeAudit.ALLOWED_TOOLS | {"engineering_read", "project_open"}


def assert_snapshot(expected, actual):
    require(set(expected) == set(actual), "Export coverage differs.")
    changed = [name for name in expected if expected[name]["payloadSha256"] != actual[name]["payloadSha256"]]
    require(not changed, "Complete native payload changed: " + ", ".join(changed))


def snapshot(call, names, directory):
    directory.mkdir()
    results = {}
    for ordinal, name in enumerate(names):
        result = call("block_read", {"blockName": name})
        (directory / (str(ordinal).zfill(3) + ".xml")).write_bytes(result["xml"].encode("utf-8"))
        results[name] = {"xml": result["xml"], "payloadSha256": native_hash(result["xml"])}
    return results


def recorded_snapshot(directory):
    results = {}
    for path in directory.glob("*.xml"):
        xml = path.read_text(encoding="utf-8-sig")
        root = next(e for e in native_document(xml) if e.tag.startswith("SW.Blocks."))
        name = root.find("AttributeList/Name").text
        if name in results:
            raise ValueError("Duplicate original snapshot name; exact scope cannot be recovered.")
        results[name] = {"xml": xml, "payloadSha256": native_hash(xml)}
    if not results:
        raise ValueError("Missing complete original snapshots.")
    return results


def original_snapshots(report):
    # Raw MCP strings are authoritative. Older Windows runs used write_text on
    # already-CRLF XML, doubling CR characters in exported comment text on disk.
    calls = report["calls"]
    change_id = report["prepared"]["changeId"]
    prepared = next(i for i, c in enumerate(calls) if c["tool"] == "change_prepare" and c.get("result", {}).get("changeId") == change_id)
    applied = next(i for i, c in enumerate(calls) if c["tool"] == "change_apply" and c["arguments"].get("changeId") == change_id)
    before_calls = []
    index = prepared - 1
    while index >= 0 and calls[index]["tool"] == "block_read":
        before_calls.insert(0, calls[index]); index -= 1
    after_calls = []
    index = applied + 1
    while index < len(calls) and calls[index]["tool"] == "block_read":
        after_calls.append(calls[index]); index += 1
    def convert(values):
        result = {c["result"]["block"]["name"]: {"xml": c["result"]["xml"], "payloadSha256": native_hash(c["result"]["xml"])} for c in values}
        if len(result) != len(values) or not result:
            raise ValueError("Original audited snapshot coverage is ambiguous or missing.")
        return result
    return convert(before_calls), convert(after_calls)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=pathlib.Path, required=True)
    parser.add_argument("--sim-worker", type=pathlib.Path)
    parser.add_argument("--live", action="store_true")
    parser.add_argument("--allow-host-change", action="store_true", help="Record a newer MCP host; worker and simulator hashes must remain exact.")
    args = parser.parse_args()
    original = json.loads(args.report.read_text(encoding="utf-8"))
    if not all(original.get(gate) for gate in ("engineeringVerified", "preservationVerified", "failureRecoveryOriginalVerified")):
        raise ValueError("Continuation requires native apply, complete preservation and restored original proof.")
    if args.live and not args.sim_worker:
        parser.error("--live requires --sim-worker")
    binaries = binary_manifest(args.host, args.worker, args.sim_worker if args.live else None)
    compare_keys = set(binaries) - ({"host"} if args.allow_host_change else set())
    if set(binaries) != set(original["binaries"]) or any(binaries[k] != original["binaries"][k] for k in compare_keys):
        raise ValueError("Continuation must use the exact originally qualified binary manifest.")
    args.output.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "fixtureId": original["fixtureId"], "startedAtUtc": utc_now(),
              "sourceReport": str(args.report.resolve()), "sourceReportSha256": digest(original), "binaries": binaries,
              "payloadHashPolicy": PAYLOAD_HASH_POLICY,
              "hostChanged": binaries["host"] != original["binaries"]["host"],
              "engineeringVerified": True, "preservationVerified": True, "persistenceVerified": False,
              "recoveryVerified": False, "runtimeVerified": False, "calls": [], "status": "running"}
    def persist():
        (args.output / "result.json").write_text(canonical(report), encoding="utf-8")
    client_args = ["--worker", str(args.worker.resolve()), "--artifacts", original["nativeArtifacts"]]
    if args.sim_worker:
        client_args += ["--sim-worker", str(args.sim_worker.resolve())]
    client = Client(args.host.resolve(), 300, client_args)
    call = Audit(client, report["calls"], persist)
    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "typed-qualification-continuation", "version": "1.0.0"}})
        client.notify("notifications/initialized")
        require(call("tia_start")["ownsPortalProcess"] is True, "Resume requires an owned portal process.")
        call("project_open", {"path": original["scratch"]["project"]["path"]})
        before, expected = original_snapshots(original)
        actual = snapshot(call, expected.keys(), args.output / "reopened")
        assert_snapshot(expected, actual)
        report["persistenceVerified"] = True
        block_path = original["prepared"]["preview"]["typed"]["blockPath"]
        graph = call("engineering_read", {"selectors": {"scope": "graphical", "blockPath": block_path, "artifactDirectory": original["nativeArtifacts"]}})
        require(graph["readOnly"] is True and graph["graph"]["networks"], "Resume requires populated read-only graph evidence.")
        require(all(n["language"] == original["language"] for n in graph["graph"]["networks"]), "Resumed graph language differs.")
        report["graph"] = graph
        if args.live:
            program = json.loads((args.report.parent / "program.json").read_text(encoding="utf-8"))
            suite = json.loads((args.report.parent / "suite.json").read_text(encoding="utf-8"))
            credential = original["scratch"]["scratchSecurity"]["cpus"][0]["credentialMetadataPath"]
            revision = {"sourceReportSha256": digest(original), "nativeProgram": program["revision"], "exports": {name: value["payloadSha256"] for name, value in actual.items()}}
            def backend(_case):
                return McpRuntimeBackend(call, program, revision, credential, provision_runtime_credential=True, trust_owned_empty_cpu_certificate=True)
            report["runtime"] = execute_suite(suite, program, backend, revision, persist=lambda value: (report.update({"runtime": value}), persist()))
            report["runtimeVerified"] = report["runtime"].get("status") == "passed"
        else:
            report["runtime"] = {"status": "not_run"}
        call("tia_detach")
        require(call("tia_start")["ownsPortalProcess"] is True, "Resume requires an owned portal process.")
        call("project_open", {"path": original["failureRecovery"]["recoveredProject"]["path"]})
        expected = before
        assert_snapshot(expected, snapshot(call, expected.keys(), args.output / "recovered-reopened"))
        report["recoveryVerified"] = True
        report["status"] = "passed" if not args.live or report["runtimeVerified"] else "blocked"
    except Exception as error:
        report["status"] = "blocked"
        report["failure"] = {"type": type(error).__name__, "message": str(error)}
    finally:
        try:
            report["detach"] = call("tia_detach")
        except Exception as error:
            report["cleanupFailure"] = str(error)
        client.close()
        report["completedAtUtc"] = utc_now()
        persist()
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
