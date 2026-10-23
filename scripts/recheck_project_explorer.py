"""Read-only release recheck of a completed, independently authored explorer fixture.

Starts a new owned TIA instance and opens the exact saved fixture copy. Never
compiles, saves, mutates, attaches to another process or replays an operation.
"""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import statistics
import time
import uuid

from smoke_mcp import Client, ROOT
from qualify_project_index import initialize, require, validate_owned_status, generated_functions
from qualify_project_explorer import exact_reference, transfer, validate_fixture_relationships, validate_typed_preview


def run(args):
    source_bytes = args.fixture_report.read_bytes()
    source = json.loads(source_bytes)
    require(source.get("completed") is True, "Only a completed fixture report may authorize this read-only recheck")
    project = Path(source["projectPath"]).resolve()
    require(project.is_relative_to((ROOT / "artifacts").resolve()) and project.is_file()
            and project.suffix == ".ap21" and project.stem.startswith("TiaMcpScratch_"), "Expected exact owned scratch fixture path")
    require(not args.report.exists(), "Do not overwrite prior qualification evidence")
    report = {"schemaVersion": 1, "completed": False, "readOnlyRecheck": True,
              "observedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "fixtureReport": str(args.fixture_report.resolve()), "fixtureReportSha256": hashlib.sha256(source_bytes).hexdigest(),
              "projectPath": str(project), "projectFileSha256Before": hashlib.sha256(project.read_bytes()).hexdigest(),
              "projectFileHashScope": "Only the .ap21 file; never a complete project revision",
              "hostAssemblySha256": hashlib.sha256(args.host.with_suffix(".dll").read_bytes()).hexdigest(),
              "workerSha256": hashlib.sha256(args.worker.read_bytes()).hexdigest(), "steps": []}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    artifact_root = ROOT / "artifacts" / ("exr_" + uuid.uuid4().hex[:8])
    client = Client(args.host.resolve(), args.timeout, ["--worker", str(args.worker.resolve()), "--artifacts", str(artifact_root)])

    def save():
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    def call(name, arguments=None, label=None):
        start = time.perf_counter()
        step = {"name": label or name, "passed": False}
        report["steps"].append(step)
        try:
            result = client.tool(name, arguments or {})
            step.update(passed=True, result=result)
            return result
        except Exception as error:
            step["failure"] = str(error)
            raise
        finally:
            step["milliseconds"] = (time.perf_counter() - start) * 1000
            save()
            print(("PASS " if step["passed"] else "FAIL ") + step["name"], flush=True)

    def model(action, **request):
        return call("project_model_" + action, {"request": request}, action)

    def rejected(request, codes):
        response = client.request("tools/call", {"name": "project_model", "arguments": {"request": request}})
        body = response.get("structuredContent") or json.loads(response["content"][0]["text"])
        require(response.get("isError") and body["error"]["code"] in codes, "Old selection or exhausted scope must fail closed")
        return body

    try:
        initialize(client)
        started = call("tia_start")
        validate_owned_status(started, started["processId"], require_owner=True)
        call("project_open", {"path": str(project)})
        validate_owned_status(call("tia_session_status"), started["processId"], project, require_owner=True)
        inventory = call("project_inventory", {"maxItems": 2000})
        plc = next(p["path"] for p in inventory["plcs"] if p["name"] == "ScratchPLC")
        header = model("capture", lifetimeSeconds=900)
        report["nativeCapture"] = header["usage"]
        nodes, cursor = [], None
        while True:
            page = model("search", snapshotId=header["snapshotId"], maxItems=200, cursor=cursor)
            nodes.extend(page["nodes"])
            cursor = page.get("nextCursor")
            if cursor is None:
                require(len(nodes) == page["totalMatches"], "Search omitted captured nodes")
                break
        require(len({n["nodeId"] for n in nodes}) == len(nodes), "Duplicate native page identities")
        report["nodeCount"] = len(nodes)
        count = source.get("generatedFunctionCount", 0)
        if count:
            require({n["name"] for n in nodes if n["name"].startswith("TiaMcp_Index_Explorer_")} == set(generated_functions(count, "TiaMcp_Index_Explorer")), "Saved independent function set changed")
            report["generatedFunctionCount"] = count
        groups = {n["name"]: n for n in nodes if n["name"] in ("ExplorerEmpty", "ExplorerNested", "EmptyLeaf")}
        require(len(groups) == 3 and groups["EmptyLeaf"]["parentId"] == groups["ExplorerNested"]["nodeId"]
                and all(n["origin"] == "native" for n in groups.values()), "Native empty/nested fixture hierarchy changed")
        block = next(n for n in nodes if n.get("objectPath") == plc + "/blocks/TiaMcp_EnableGate")
        reference = exact_reference(header, block)
        model("details", reference=reference)
        direct = call("block_read", {"blockPath": block["objectPath"]})
        require(block["objectPath"] in json.dumps(direct), "Independent inspection lost the exact selected object")
        graph = model("relationships", reference=reference, depth=1, maxNodes=100, maxEdges=200)
        validate_fixture_relationships(graph, plc)
        for direction in ("in", "out"):
            filtered = model("relationships", reference=reference, kinds=["calls"], direction=direction)
            require(filtered["edges"], "Directional fixture calls disappeared")
            endpoint = "target" if direction == "in" else "source"
            require(all(e[endpoint]["objectPath"] == reference["objectPath"] for e in filtered["edges"]), "Directional call filter is reversed")
        hardware = next(n for n in nodes if n["kind"] == "hardware" and n.get("objectPath") == plc.rsplit("/plc/", 1)[0])
        hardware_reference = exact_reference(header, hardware)
        hardware_details = model("details", reference=hardware_reference)
        inspected = call("hardware_inspect", {"hardwarePath": hardware["objectPath"], "maxItems": 2000})
        require(hardware_details["status"] == "available", "Exact hardware details unavailable")
        direct_item = next(i for i in inspected["items"] if i["path"] == hardware["objectPath"])
        detail_item = next(i for i in hardware_details["details"]["items"] if i["path"] == hardware["objectPath"])
        require(direct_item["positionNumber"] == detail_item["positionNumber"] and direct_item["addresses"] == detail_item["addresses"]
                and hardware["metadata"]["topologyVerified"] is False, "Native hardware evidence or topology boundary changed")
        report["hardwareQualification"] = {"reference": hardware_reference, "independentInspectionVerified": True, "physicalTopologyVerified": False}
        for preview in source.get("typedPreviewQualification", []):
            language = preview["language"]
            node = next(n for n in nodes if n.get("objectPath") == plc + "/blocks/ExplorerPreview_" + language)
            exact = exact_reference(header, node)
            details = model("details", reference=exact)
            provider = validate_typed_preview(details, exact, language)
            transfer(client, provider["sourceArtifact"], args.report.with_suffix("." + language.lower() + ".xml"))
        verified = model("verify", snapshotId=header["snapshotId"], reference=reference)
        require(verified["valid"] and verified["referenceVerified"] and verified["contentVerified"] is False, "Metadata/content separation or exact verification changed")
        timings = []
        for _ in range(25):
            start = time.perf_counter()
            client.tool("project_model_search", {"request": {"snapshotId": header["snapshotId"], "text": "TiaMcp", "maxItems": 200}})
            timings.append((time.perf_counter() - start) * 1000)
        report["cachedQueryMilliseconds"] = {"count": 25, "median": statistics.median(timings), "p95": sorted(timings)[23], "includesMcpTransport": True}
        for format in ("json", "html"):
            exported = model("export", snapshotId=header["snapshotId"], format=format)
            destination = args.report.with_suffix(".snapshot.json" if format == "json" else ".html")
            transfer(client, exported["artifact"], destination)
            report.setdefault("exports", {})[format] = str(destination)
        old = source.get("selectedReference")
        if old:
            report["previousWorkerSelectionRejected"] = rejected({"action": "details", "reference": old}, {"SessionMismatch", "SnapshotNotFound", "StaleSelection"})
        partial = model("capture", maxVisited=12)
        require(partial["usage"]["visited"] <= 12 and partial["usage"]["truncated"], "Native traversal exceeded its visit budget")
        model("release", snapshotId=partial["snapshotId"])
        for maximum_depth in (1, 2):
            shallow = model("capture", maxDepth=maximum_depth)
            page = model("search", snapshotId=shallow["snapshotId"], maxItems=200)
            require(page.get("nextCursor") is None, "Known shallow fixture unexpectedly exceeded a full page")
            by_id = {node["nodeId"]: node for node in page["nodes"]}
            for node in by_id.values():
                depth, parent = 0, node.get("parentId")
                while parent is not None:
                    require(parent in by_id, "Shallow native capture omitted a parent")
                    depth += 1
                    parent = by_id[parent].get("parentId")
                    require(depth <= maximum_depth, "Native capture exceeded actual parent depth")
            require(shallow["usage"]["truncated"], "Depth-limited capture must report partial coverage")
            model("release", snapshotId=shallow["snapshotId"])
        if len(inventory["plcs"]) > 1:
            later = inventory["plcs"][-1]["path"]
            report["scopedBudgetExhaustion"] = rejected({"action": "capture", "plcPath": later, "maxVisited": 2}, {"TraversalIncomplete"})
        call("tia_detach")
        report["disconnectSelectionRejected"] = rejected({"action": "details", "reference": reference}, {"SessionMismatch", "SnapshotInvalidated", "SnapshotNotFound", "StaleSelection"})
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        print("FAIL " + str(error), flush=True)
    finally:
        try:
            client.tool("tia_detach")
        except Exception as error:
            report["cleanupNote"] = str(error)
        client.close()
        report["finishedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        save()
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--fixture-report", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    raise SystemExit(run(parser.parse_args()))
