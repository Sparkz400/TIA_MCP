"""Qualify generated fixtures. Default is a pure dry run; --live owns a new scratch project."""
import argparse
import copy
import datetime
import hashlib
import json
import pathlib
import uuid
import xml.etree.ElementTree as ET

from generate_conformance import ROOT, DEFAULT_OUTPUT, canonical, generate, validate_manifest
from smoke_mcp import Client


class NativeFailure(RuntimeError):
    def __init__(self, tool, result):
        self.tool = tool
        self.result = result
        super().__init__(f"{tool}: {json.dumps(result, ensure_ascii=False)}")


def pointer(value, path):
    if not path:
        return value
    if not path.startswith("/"):
        raise ValueError("Expected an absolute JSON pointer.")
    for part in path[1:].split("/"):
        part = part.replace("~1", "/").replace("~0", "~")
        value = value[int(part)] if isinstance(value, list) else value[part]
    return value


def local_name(element):
    return element.tag.rsplit("}", 1)[-1]


def safe_xml(text):
    if "<!DOCTYPE" in text.upper() or "<!ENTITY" in text.upper():
        raise ValueError("DTD/entity declarations are not allowed in fixture XML.")
    return ET.fromstring(text)


def access_location(xml, symbol):
    matches = []
    for unit in safe_xml(xml).iter("SW.Blocks.CompileUnit"):
        for access in unit.iter():
            if local_name(access) == "Access" and any(local_name(child) == "Component" and child.get("Name") == symbol for child in access.iter()):
                matches.append({"networkId": unit.attrib["ID"], "accessUid": access.attrib["UId"]})
    if len(matches) != 1:
        raise ValueError(f"Expected exactly one network access for {symbol}; found {len(matches)}.")
    return matches[0]


def resolve(value, results, run_id):
    if isinstance(value, dict):
        if set(value) == {"$ref"}:
            name, separator, path = value["$ref"].partition("/")
            return copy.deepcopy(pointer(results[name], "/" + path if separator else ""))
        if set(value) == {"$runKey"}:
            return hashlib.sha256((run_id + ":" + value["$runKey"]).encode()).hexdigest()
        if set(value) == {"$xmlAccess"}:
            selector = value["$xmlAccess"]
            return access_location(results[selector["stepId"]]["xml"], selector["symbol"])[selector["attribute"]]
        return {key: resolve(child, results, run_id) for key, child in value.items()}
    if isinstance(value, list):
        return [resolve(child, results, run_id) for child in value]
    return value


def structural_xml(element):
    # Only discard serializer indentation between elements. Text/comment values,
    # including significant leading/trailing spaces, are part of the payload.
    text = element.text or ""
    if len(element) and not text.strip():
        text = ""
    tail = element.tail or ""
    if not tail.strip():
        tail = ""
    return (element.tag, tuple(sorted(element.attrib.items())), text, tail,
            tuple(structural_xml(child) for child in element))


def native_document(xml):
    """Retain the whole native payload; only export provenance is excluded."""
    document = safe_xml(xml)
    for child in list(document):
        if local_name(child) == "DocumentInfo":
            document.remove(child)
    return document


def graphical_sections(xml):
    return [element for element in safe_xml(xml).iter() if local_name(element) in {"Interface", "NetworkSource"}]


def assert_expectation(result, assertion, results):
    value = pointer(result, assertion["path"])
    expected = assertion["value"]
    operation = assertion["op"]
    if operation == "equals":
        # Python bool is an int subclass; JSON true must not satisfy errorCount=1.
        passed = value == expected and not (isinstance(value, bool) != isinstance(expected, bool))
    elif operation == "nonempty":
        passed = value is not None and value != "" and value != [] and value != {}
    elif operation == "contains":
        passed = expected in value
    elif operation == "member":
        candidates = [member for section in value for member in section.get("members", [])]
        matching = []
        for index, name in enumerate(expected["path"]):
            matching = [member for member in candidates if member.get("name") == name]
            if index < len(expected["path"]) - 1:
                candidates = [child for member in matching for child in member.get("members", [])]
        normalize = lambda text: "".join(text.split()).casefold()
        passed = len(matching) == 1 and normalize(matching[0].get("dataType", "")) == normalize(expected["dataType"])
    elif operation == "callEdge":
        def terminal(path):
            return path.replace("\\", "/").rstrip("/").split("/")[-1]
        passed = any(terminal(edge.get("callerPath", "")) == expected["caller"] and
                     terminal(edge.get("calleePath", "")) == expected["callee"] for edge in value)
    elif operation == "symbolAccess":
        passed = bool(access_location(value, expected))
    elif operation == "operandPreserved":
        before = native_document(results[expected["baselineStep"]]["xml"])
        after = native_document(value)
        changed = 0
        for section in before.iter():
            if local_name(section) == "NetworkSource":
                for element in section.iter():
                    if local_name(element) == "Component" and element.get("Name") == expected["from"]:
                        element.set("Name", expected["to"])
                        changed += 1
        passed = changed == 1 and structural_xml(before) == structural_xml(after)
    else:
        raise ValueError("Unknown expectation: " + operation)
    if not passed:
        raise AssertionError("Expectation failed: " + canonical(assertion).strip())


def native_call(client, tool, arguments=None):
    response = client.request("tools/call", {"name": tool, "arguments": arguments or {}})
    result = response.get("structuredContent")
    if result is None:
        result = json.loads("\n".join(item["text"] for item in response.get("content", []) if item.get("type") == "text"))
    if response.get("isError"):
        raise NativeFailure(tool, result)
    return result


def load_manifests(directory, selected=None):
    directory = pathlib.Path(directory).resolve()
    index = json.loads((directory / "index.json").read_text(encoding="utf-8"))
    if index.get("schemaVersion") != 1 or not isinstance(index.get("fixtures"), list):
        raise ValueError("Invalid generated index.")
    output = []
    observed = set()
    for entry in index["fixtures"]:
        identifier = entry["fixtureId"]
        if identifier in observed:
            raise ValueError("Duplicate fixture in index.")
        observed.add(identifier)
        candidate = directory / entry["path"]
        path = candidate.resolve()
        if path.parent != directory or candidate.is_symlink():
            raise ValueError("Manifest must be an immediate file in generated directory.")
        data = path.read_bytes()
        if hashlib.sha256(data).hexdigest() != entry["sha256"]:
            raise ValueError("Manifest hash mismatch: " + identifier)
        manifest = validate_manifest(json.loads(data))
        if manifest["fixtureId"] != identifier:
            raise ValueError("Manifest/index fixture mismatch.")
        for source in entry.get("sources", []):
            source_candidate = directory / source["path"]
            source_path = source_candidate.resolve()
            if source_path.parent != directory or source_candidate.is_symlink() or hashlib.sha256(source_path.read_bytes()).hexdigest() != source["sha256"]:
                raise ValueError("Source integrity check failed: " + identifier)
        if not selected or identifier in selected:
            output.append((manifest, entry["sha256"]))
    if selected and set(selected) - observed:
        raise ValueError("Requested fixture missing from index.")
    return output


def initial_result(manifest, manifest_hash, run_id, phase, live):
    return {"schemaVersion": 1, "runId": run_id, "fixtureId": manifest["fixtureId"], "status": "skipped",
            "environment": {"tiaVersion": 21, "requestedPhase": phase, "live": live, "cpuFamily": "S7-1500",
                            "observedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat()},
            "steps": [], "evidence": [{"kind": "fixture_manifest", "path": manifest["fixtureId"] + ".manifest.json", "sha256": manifest_hash}],
            "limitations": ["Runtime behavior has not been executed; successful native compilation is not behavioral qualification."]}


def archive_manifests(manifests, output):
    """Keep the exact tested specification even if the generated corpus later changes."""
    for manifest, expected_hash in manifests:
        encoded = canonical(manifest).encode("utf-8")
        if hashlib.sha256(encoded).hexdigest() != expected_hash:
            raise ValueError("Loaded manifest does not match its canonical generated hash.")
        path = pathlib.Path(output) / (manifest["fixtureId"] + ".manifest.json")
        if path.exists() and path.read_bytes() != encoded:
            raise ValueError("Refusing to overwrite a different archived fixture manifest.")
        path.write_bytes(encoded)


def run_fixture(client, manifest, report, persist):
    results = {}
    last_prepare = None
    for item in manifest["steps"]:
        arguments = resolve(item["arguments"], results, report["runId"] + ":" + manifest["fixtureId"])
        record = {"stepId": item["stepId"], "tool": item["tool"], "status": "failed", "arguments": arguments}
        report["steps"].append(record)
        persist()
        try:
            result = native_call(client, item["tool"], arguments)
            results[item["stepId"]] = result
            record["result"] = result
            if item["tool"] == "change_prepare":
                last_prepare = result
            for assertion in item["expect"].get("assertions", []):
                assert_expectation(result, assertion, results)
            record["status"] = "passed"
            report["evidence"].append({"kind": item["expect"].get("evidenceKind", "observation"),
                                       "stepId": item["stepId"], "encoding": "canonical-json-utf8",
                                       "sha256": hashlib.sha256(canonical(result).encode()).hexdigest()})
            persist()
            print("PASS " + manifest["fixtureId"] + "/" + item["stepId"], flush=True)
        except Exception as error:
            record["error"] = str(error)
            if isinstance(error, NativeFailure):
                record["nativeFailure"] = error.result
            report["status"] = "failed"
            persist()
            # Status and recovery are never retries. Preserve the primary failure even if recovery fails.
            if last_prepare and item["tool"] == "change_apply":
                recovery = {"stepId": "failure_recovery", "tool": "change_restore", "status": "blocked"}
                report["steps"].append(recovery)
                try:
                    status = native_call(client, "change_status", {"changeId": last_prepare["changeId"]})
                    recovery["changeStatus"] = status
                    if status.get("recoveryArchive"):
                        recovery["result"] = native_call(client, "change_restore", {
                            "changeId": last_prepare["changeId"], "previewHash": last_prepare["previewHash"]})
                        recovery["status"] = "passed"
                    else:
                        recovery["error"] = "No recovery archive was reported; native failure is retained."
                except Exception as recovery_error:
                    recovery["error"] = str(recovery_error)
                persist()
            return
    report["status"] = "passed"
    persist()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true", help="Start a new owned headless TIA instance and scratch project.")
    parser.add_argument("--phase", choices=("engineering", "runtime"), default="engineering")
    parser.add_argument("--generated", type=pathlib.Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--fixture", action="append")
    parser.add_argument("--report-directory", type=pathlib.Path)
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--continue-on-failure", action="store_true", help="Continue independent fixtures only after successful recovery or a read-only failure.")
    args = parser.parse_args()
    if not (args.generated / "index.json").exists():
        generate(args.generated)
    manifests = load_manifests(args.generated, args.fixture)
    run_id = "conformance_" + datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d_%H%M%S") + "_" + uuid.uuid4().hex[:8]
    output = (args.report_directory or ROOT / "artifacts/conformance/runs" / run_id).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if (output / "index.json").exists():
        raise ValueError("Report directory already contains a run; choose a new directory to preserve its evidence.")
    archive_manifests(manifests, output)
    summary = {"schemaVersion": 1, "runId": run_id, "requestedPhase": args.phase, "live": args.live,
               "results": [], "cleanup": {}, "runtimeVerified": False}
    reports = []
    for manifest, manifest_hash in manifests:
        report = initial_result(manifest, manifest_hash, run_id, args.phase, args.live)
        reports.append((manifest, report))
        summary["results"].append({"fixtureId": manifest["fixtureId"], "path": manifest["fixtureId"] + ".result.json", "status": "skipped"})

    def persist():
        for (_, report), entry in zip(reports, summary["results"]):
            entry["status"] = report["status"]
            (output / entry["path"]).write_text(canonical(report), encoding="utf-8")
        (output / "index.json").write_text(canonical(summary), encoding="utf-8")

    persist()
    if not args.live or args.phase == "runtime":
        for manifest, report in reports:
            report["status"] = "blocked" if args.live else "skipped"
            report["limitations"].append("This engineering-manifest runner does not execute runtime vectors. Use scripts/run_block_tests.py for the reusable inspected block executor and runtime corpus; PLCSIM qualification remains pending."
                                          if args.phase == "runtime" else "Dry-run manifest and source validation only; no TIA processes or projects were touched.")
            report["steps"] = [{"stepId": item["stepId"], "tool": item["tool"], "status": "skipped"} for item in manifest["steps"]]
        persist()
        print(f"Validated {len(reports)} fixture manifests; reports: {output}")
        return 2 if args.live else 0

    client = None
    failure = False
    try:
        client = Client(args.host.resolve(), args.timeout)
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                     "clientInfo": {"name": "tia-conformance", "version": "1.0.0"}})
        client.notify("notifications/initialized")
        summary["toolNames"] = [tool["name"] for tool in client.request("tools/list")["tools"]]
        required = {item["tool"] for manifest, _ in reports for item in manifest["steps"]}
        missing = required - set(summary["toolNames"])
        if missing:
            raise RuntimeError("Required MCP tools are missing: " + ", ".join(sorted(missing)))
        summary["session"] = native_call(client, "tia_start")
        # Fixed exact S7-1500 V4.0 profile: other families are declared candidates, never inferred qualifications.
        scratch = native_call(client, "tia_create_scratch", {"cpuTypeIdentifier": "OrderNumber:6ES7 511-1AL03-0AB0/V4.0"})
        summary["scratch"] = scratch
        if not scratch.get("completed") or scratch.get("compile", {}).get("errorCount") != 0:
            raise RuntimeError("Scratch creation/compilation did not complete successfully.")
        for _, report in reports:
            report["environment"]["project"] = scratch["project"]
            report["environment"]["cpuTypeIdentifier"] = "OrderNumber:6ES7 511-1AL03-0AB0/V4.0"
        persist()
        current_project = scratch["project"]
        for manifest, report in reports:
            report["environment"]["project"] = current_project
            run_fixture(client, manifest, report, persist)
            for item in report["steps"]:
                recovered_project = item.get("result", {}).get("recoveredProject")
                if recovered_project:
                    current_project = recovered_project
                    summary["currentProject"] = current_project
            if report["status"] == "failed":
                failure = True
                apply_failed = any(item["tool"] == "change_apply" and item["status"] == "failed" for item in report["steps"])
                recovered = any(item["stepId"] == "failure_recovery" and item["status"] == "passed" for item in report["steps"])
                if not args.continue_on_failure or (apply_failed and not recovered):
                    for _, unexecuted in reports:
                        if unexecuted["status"] == "skipped":
                            unexecuted["status"] = "blocked"
                            unexecuted["limitations"].append("A preceding fixture failed; execution stopped to preserve the inspected state.")
                    break
    except Exception as error:
        failure = True
        summary["failure"] = str(error)
        for _, report in reports:
            if report["status"] == "skipped":
                report["status"] = "blocked"
                report["limitations"].append("Run setup or preceding session failed: " + str(error))
    finally:
        if client:
            try:
                summary["cleanup"]["detach"] = native_call(client, "tia_detach")
            except Exception as error:
                summary["cleanup"]["error"] = str(error)
                failure = True
            try:
                client.close()
                summary["cleanup"]["hostExitCode"] = client.process.returncode
            except Exception as error:
                summary["cleanup"]["hostCloseError"] = str(error)
                failure = True
        persist()
    print(f"Conformance reports: {output}")
    return 1 if failure else 0


if __name__ == "__main__":
    raise SystemExit(main())
