"""Read-only final-package verification of an existing, evidenced HMI fixture."""
import argparse
import datetime
import hashlib
import json
import pathlib
import queue
import runpy
import time
import uuid
import xml.etree.ElementTree as ET

from smoke_mcp import Client, ROOT
from qualify_project_index import initialize, require, validate_owned_status
from qualify_project_explorer import exact_reference, transfer


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def validate_screen_details(details, reference, expected_items):
    provider = details.get("details", {})
    require(details.get("status") in ("available", "partial") and details.get("reference") == reference,
            "HMI details must preserve the exact selected reference")
    require(provider.get("provider") == "hmi.v21" and provider.get("facet") == "hmiDetails"
            and provider.get("reference") == reference and provider.get("status") in ("available", "partial"),
            "Native HMI provider is not available for this exact screen")
    require(provider.get("contentRevision", {}).get("status") == "unavailable",
            "Individual screen export must not certify complete HMI content")
    require(provider.get("runtimeVerified") is False and provider.get("engineeringVerified") is False,
            "Inspection cannot qualify HMI engineering/runtime behavior")
    observed = provider["observation"]
    require(observed.get("scriptsExecuted") is False and observed.get("runtimeVerified") is False,
            "Read-only inspection must not execute project scripts")
    owners = [item for item in observed["items"] if item["kind"] == "hmiScreen"]
    require(len(owners) <= 1 and all(item["identity"]["objectPath"] == reference["objectPath"] for item in owners),
            "Included screen owner must preserve the exact selected object")
    items = [item for item in observed["items"] if item["kind"] != "hmiScreen"]
    require(len(items) == len(expected_items) == 2, "Known native screen must expose its two named payload objects")
    expected = {(item["kind"], item["name"], item["nativeType"]): item["properties"] for item in expected_items}
    actual = {(item["kind"], item["name"], item["nativeType"]): item["properties"] for item in items}
    require(expected == actual, "Native screen names, kinds or scalar geometry differ from the original fixture")
    require(all(item["identity"]["screenPath"] == reference["objectPath"] for item in items), "Export-local objects belong to a different screen")
    return provider, observed


def run(args):
    evidence = json.loads(args.evidence.read_text(encoding="utf-8-sig"))
    require(evidence.get("completed") is True, "Original HMI evidence is incomplete")
    profile = next(item for item in evidence["profiles"] if item["profile"] == "classic-comfort")
    require(profile["gates"]["explorerIntegration"]["status"] == "passed", "Original live explorer provider gate did not pass")
    previous = profile["explorer"]["details"]
    selected_path = previous["reference"]["objectPath"]
    expected_items = previous["details"]["observation"]["items"]
    project = pathlib.Path(profile["retrieval"]["project"]["path"]).resolve()
    require(project.is_file() and project.suffix.lower() == ".ap21", "Saved native HMI project is unavailable")
    require(project == pathlib.Path(previous["reference"]["projectPath"]).resolve(), "Fixture project copy identity differs")
    require(project.is_relative_to((ROOT / "artifacts/recovery").resolve()), "Only this repository's recorded recovered fixture is authorized")
    release = pathlib.Path(args.release_file.read_text(encoding="utf-8-sig").strip()).resolve()
    host, worker = release / "host/TiaMcp.Host.exe", release / "workers/openness/TiaMcp.Openness.V21.exe"
    require(host.is_file() and worker.is_file(), "Immutable release host and worker must exist")
    args.report = args.report.resolve()
    output = args.report.parent
    artifact_root = output / ("ex_hmi_" + uuid.uuid4().hex[:8])
    artifact_root.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "completed": False, "status": "running", "readOnly": True, "steps": [],
              "observedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "release": str(release), "profile": profile["profile"], "selectedTarget": profile["selectedTarget"],
              "projectPath": str(project), "projectFileSha256Before": sha256(project),
              "sourceEvidence": {"path": str(args.evidence.resolve()), "sha256": sha256(args.evidence),
                                 "nativeReport": evidence["nativeReport"], "nativeReportSha256": evidence["nativeReportSha256"],
                                 "retrievedArchive": profile["retrieval"]["archive"]},
              "binaries": {"hostAssembly": {"path": str(host.with_suffix(".dll")), "sha256": sha256(host.with_suffix(".dll"))},
                           "worker": {"path": str(worker), "sha256": sha256(worker)}},
              "knownLimitations": {name: item for name, item in profile["gates"].items() if item["status"] != "passed"},
              "scope": "Read-only Classic Comfort explorer integration. Native screen rendering, family-wide acceptance, mutation, compile, save/reopen preservation and runtime behavior remain separate gates.",
              "scriptSha256": sha256(pathlib.Path(__file__))}
    client = None
    started = False
    uncertain = False

    def save():
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    def call(tool, arguments=None):
        nonlocal uncertain
        require(tool in {"tia_start", "project_open", "tia_session_status", "project_model", "tia_detach"},
                "Verifier has a strict read-only operation allowlist")
        if tool == "project_model":
            require(arguments["request"]["action"] in {"capture", "search", "children", "details", "relationships", "export"}, "Mutation is not allowed")
        step = {"name": tool, "arguments": arguments or {}, "passed": False}
        report["steps"].append(step)
        start = time.perf_counter()
        save()
        try:
            value = client.tool(tool, arguments or {})
            step.update(passed=True, result=value)
            return value
        except Exception as error:
            step["failure"] = type(error).__name__ + ": " + str(error)
            uncertain = isinstance(error, (TimeoutError, queue.Empty)) or any(word in str(error).lower() for word in ("timeout", "timed out", "host exited", "workerfault", "bridgefault"))
            if uncertain:
                report["unknownOutcome"] = {"tool": tool, "retryAllowed": False}
            raise
        finally:
            step["milliseconds"] = (time.perf_counter() - start) * 1000
            save()
            print(("PASS " if step["passed"] else "FAIL ") + tool + (" " + arguments["request"]["action"] if tool == "project_model" else ""), flush=True)

    def model(action, **request):
        return call("project_model", {"request": {"action": action, **request}})

    try:
        client = Client(host, args.timeout, ["--worker", str(worker), "--artifacts", str(artifact_root)])
        initialize(client)
        session = call("tia_start")
        started = True
        report["processId"] = session["processId"]
        validate_owned_status(session, session["processId"], require_owner=True)
        call("project_open", {"path": str(project)})
        validate_owned_status(call("tia_session_status"), session["processId"], project, require_owner=True)
        snapshot = model("capture", maxVisited=50000, maxBytes=8388608, lifetimeSeconds=900)
        require(pathlib.Path(snapshot["project"]["projectPath"]).resolve() == project, "Captured project copy differs")
        report["nativeCapture"] = snapshot["usage"]
        nodes, cursor = [], None
        while True:
            page = model("search", snapshotId=snapshot["snapshotId"], maxItems=100, cursor=cursor)
            nodes.extend(page["nodes"])
            cursor = page.get("nextCursor")
            if cursor is None:
                require(len(nodes) == page["totalMatches"], "Paged native hierarchy is incomplete")
                break
            require(len(nodes) < 1000, "Fixture hierarchy exceeded expected bounded scope")
        paths = {node["objectPath"]: node for node in nodes if node.get("objectPath")}
        ids = {node["nodeId"] for node in nodes}
        require(all(node.get("parentId") is None or node["parentId"] in ids for node in nodes), "Captured ancestry is broken")
        screens = [node for node in nodes if node["kind"] == "hmiScreen"]
        require(len(screens) == 4 and len({node["objectPath"] for node in screens}) == 4, "Two native targets must preserve their distinct same-named screens")
        require(selected_path in paths and paths[selected_path]["kind"] == "hmiScreen", "Original exact screen is missing; no name fallback allowed")
        node = paths[selected_path]
        children, cursor = [], None
        while True:
            page = model("children", snapshotId=snapshot["snapshotId"], parentId=node["parentId"], maxItems=1, cursor=cursor)
            children.extend(page["nodes"])
            cursor = page.get("nextCursor")
            if cursor is None:
                break
            require(len(children) < 10, "Screen child pagination did not terminate")
        require({child["nodeId"] for child in children} == {item["nodeId"] for item in nodes if item.get("parentId") == node["parentId"]}, "Native sibling paging differs")
        reference = exact_reference(snapshot, node)
        report["reference"] = reference
        details = model("details", reference=reference, maxItems=100)
        provider, observed = validate_screen_details(details, reference, expected_items)
        require(len(observed["artifacts"]) == 1, "Expected one retained native Classic screen export")
        artifact = observed["artifacts"][0]
        native_path = args.report.with_suffix(".screen.xml")
        native_bytes = transfer(client, artifact, native_path)
        payload_items = [item for item in observed["items"] if item["kind"] != "hmiScreen"]
        require(all(item["source"].get("exportSha256") == artifact["sha256"] for item in payload_items), "Provider item provenance differs from native bytes")
        xml = ET.fromstring(native_bytes)
        native_types = {element.tag.split("}")[-1] for element in xml.iter()}
        require({"Hmi.Screen.ScreenLayer", "Hmi.Screen.Rectangle"} <= native_types, "Original native XML lacks the independently expected layer and rectangle")
        report["screenEvidence"] = {"artifact": str(native_path), "sha256": artifact["sha256"], "objects": [
            {"kind": item["kind"], "name": item["name"], "nativeType": item["nativeType"], "properties": item["properties"]} for item in payload_items],
            "includedExactScreenOwnerCount": len(observed["items"]) - len(payload_items),
            "displayItemCount": sum(item["kind"] == "hmiScreenItem" for item in observed["items"]),
            "structuralLayerCount": sum(item["kind"] == "hmiScreenLayer" for item in observed["items"]),
            "sourceHashesVerified": True, "scriptsExecuted": False, "completeHmiContentRevision": False}
        relations = model("relationships", reference=reference, kinds=["hmiBinding", "screenNavigation"], direction="both", depth=1, maxNodes=10, maxEdges=20)
        require(relations["reference"] == reference and len(relations["edges"]) <= 20 and len(relations["nodes"]) <= 10,
                "HMI relation query lost exact reference or exceeded graph bounds")
        require(relations.get("scriptsExecuted") is False and relations.get("nativeCoverage") is not None, "Relationship coverage or no-script evidence is absent")
        report["relationships"] = {"edgeCount": len(relations["edges"]), "unresolvedObservationCount": len(relations.get("unresolvedObservations", [])),
                                   "coverage": relations["nativeCoverage"], "positiveBindingQualified": False}
        report["nodeCount"] = len(nodes)
        report["exports"] = {}
        for format in ("json", "html"):
            exported = model("export", snapshotId=snapshot["snapshotId"], format=format)
            target = args.report.with_suffix(".snapshot.json" if format == "json" else ".html")
            content = transfer(client, exported["artifact"], target)
            report["exports"][format] = {"path": str(target), "sha256": hashlib.sha256(content).hexdigest(), "bytes": len(content)}
            if format == "json":
                portable = json.loads(content)
        schema = runpy.run_path(str(ROOT / "tests/ProjectModelContracts.py"))
        validator = schema["Draft202012Validator"](schema["MODEL"], registry=schema["REGISTRY"], format_checker=schema["FORMATS"])
        errors = [str(error) for error in validator.iter_errors(portable)]
        provider_validator = schema["Draft202012Validator"](schema["PROVIDER"], registry=schema["REGISTRY"], format_checker=schema["FORMATS"])
        errors.extend(str(error) for error in provider_validator.iter_errors(provider))
        report["schemaValidation"] = {"passed": not errors, "errors": errors}
        require(not errors, "Snapshot/provider schema validation failed")
        require(next(item for item in portable["nodes"] if item.get("objectPath") == selected_path)["details"]["details"]["observation"]["items"] == observed["items"],
                "Portable export omitted or changed the native HMI observation")
        report.update(completed=True, status="passed", runtimeVerified=False)
    except Exception as error:
        report.update(failure=type(error).__name__ + ": " + str(error), status="unknownOutcome" if uncertain else "pending")
        raise
    finally:
        if client:
            if started and not uncertain:
                try:
                    call("tia_detach")
                except Exception as error:
                    report["detachFailure"] = str(error)
            client.close()
            report["hostExitCode"] = client.process.returncode
        report["projectFileSha256After"] = sha256(project)
        report["projectFileBytesUnchanged"] = report["projectFileSha256Before"] == report["projectFileSha256After"]
        save()
        print("Report: " + str(args.report), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence", type=pathlib.Path, default=ROOT / "artifacts/hmi-bridge-20261008-root-e.json")
    parser.add_argument("--release-file", type=pathlib.Path, default=ROOT / "artifacts/project-explorer-release.txt")
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/project-explorer-final-hmi.json")
    parser.add_argument("--timeout", type=int, default=300)
    run(parser.parse_args())


if __name__ == "__main__":
    main()
