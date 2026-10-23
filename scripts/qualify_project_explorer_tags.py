"""Large native explorer qualification using one fresh owned 2,000-tag scratch.

The only fixture mutation is one existing prepared tag_table_import_xml.
Unknown native outcomes are never retried. No other fixture is touched.
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import queue
import runpy
import statistics
import time
import uuid
import xml.etree.ElementTree as ET

from smoke_mcp import Client, ROOT
from qualify_project_index import initialize, require, validate_owned_status
from qualify_project_explorer import exact_reference, transfer, validate_fixture_relationships


def generated_tag_table(empty_xml, count=2000):
    require(count == 2000, "This independently authored scale profile requires exactly 2,000 tags")
    document = ET.fromstring(empty_xml)
    table = document.find("SW.Tags.PlcTagTable")
    require(table is not None and not list(document.iter("SW.Tags.PlcTag")), "Fixture import requires an empty native tag table")
    objects = table.find("ObjectList")
    if objects is None:
        objects = ET.SubElement(table, "ObjectList")
    ids = [int(item.attrib["ID"], 16) for item in document.iter() if item.attrib.get("ID")]
    first_id = max(ids, default=0) + 1
    for number in range(count):
        tag = ET.SubElement(objects, "SW.Tags.PlcTag", {"ID": format(first_id + number, "X"), "CompositionName": "Tags"})
        attributes = ET.SubElement(tag, "AttributeList")
        for name, value in (("Name", "ExplorerScale_" + str(number).zfill(4)), ("DataTypeName", "Byte"), ("LogicalAddress", "%MB" + str(number))):
            ET.SubElement(attributes, name).text = value
    return ET.tostring(document, encoding="unicode")


def validate_native_tags(xml_bytes, table_path, count=2000):
    native = ET.fromstring(xml_bytes)
    tags = list(native.iter("SW.Tags.PlcTag"))
    require(len(tags) == count, "Native export must contain every independently authored tag")
    result = {}
    for tag in tags:
        attributes = tag.find("AttributeList")
        name, data_type, address = (attributes.findtext(key) for key in ("Name", "DataTypeName", "LogicalAddress"))
        require(name not in result, "Duplicate native exported tag name")
        result[name] = {"objectPath": table_path + "/tags/" + name, "dataType": data_type, "logicalAddress": address}
    expected = {"ExplorerScale_" + str(number).zfill(4): {"objectPath": table_path + "/tags/ExplorerScale_" + str(number).zfill(4),
                "dataType": "Byte", "logicalAddress": "%MB" + str(number)} for number in range(count)}
    require(result == expected, "Native exported names/types/addresses differ from independently authored declarations")
    return result


def run(args):
    prior = None
    if args.verify_report:
        prior = json.loads(args.verify_report.read_text(encoding="utf-8"))
        applies = [step["result"] for step in prior["steps"] if step["tool"] == "change_apply" and step.get("passed")]
        require(not prior.get("unknownOutcome") and len(applies) == 1 and applies[0]["state"] == "applied"
                and applies[0]["compile"]["errorCount"] == 0, "Read-only continuation requires one known successful fixture import")
        require(any(step["tool"] == "tia_create_scratch" and step.get("result", {}).get("completed") is True for step in prior["steps"]),
                "Read-only continuation requires recorded fresh owned scratch provenance")
        require(any(item.get("code") == "ModelInvalidated" for item in prior.get("rejections", [])),
                "Original run must record actual prepared-change invalidation evidence")
    release = pathlib.Path(args.release_file.read_text(encoding="utf-8-sig").strip()).resolve()
    require(release.name == "tia-mcp-v21_20261008_125230_8b2f8ce9", "This qualification is pinned to the final accepted bundle")
    host, worker = release / "host/TiaMcp.Host.exe", release / "workers/openness/TiaMcp.Openness.V21.exe"
    args.report = args.report.resolve()
    output = args.report.parent
    artifact_root = output / ("ex_tags_" + uuid.uuid4().hex[:8])
    artifact_root.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "completed": False, "status": "running", "steps": [], "checks": [],
              "observedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "release": str(release),
              "artifactRoot": str(artifact_root), "generatedTagCount": 2000,
              "fixtureProfile": "One fresh standard CPU, its existing SCL program, native hardware ancestry and 2,000 distinct Byte memory tags.",
              "qualificationScope": "Bounded native metadata/content reads and offline explorer lifecycle. No runtime values, physical topology or function-count performance claim.",
              "binaries": {"hostAssembly": hashlib.sha256(host.with_suffix(".dll").read_bytes()).hexdigest(),
                           "worker": hashlib.sha256(worker.read_bytes()).hexdigest()},
              "scriptSha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest()}
    if prior:
        require(prior["release"] == str(release), "Continuation must use the same immutable release")
        report["fixtureProvenance"] = {"report": str(args.verify_report.resolve()), "sha256": hashlib.sha256(args.verify_report.read_bytes()).hexdigest(),
                                       "readOnlyContinuation": True, "originalInvalidationEvidence": prior["rejections"]}
        for key in ("fixtureXml", "fixturePreparationApplyCompileMilliseconds", "preparedChange", "selectedCpu"):
            report[key] = prior[key]
    client = None
    started = False
    uncertain = False

    def save():
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    def check(name, value):
        require(value, name)
        report["checks"].append({"name": name, "passed": True})
        save()
        print("PASS " + name, flush=True)

    def call(tool, arguments=None, label=None):
        nonlocal uncertain
        if args.verify_report:
            require(tool in {"tia_start", "project_open", "tia_session_status", "tag_table_read", "project_index", "project_model", "tia_detach"},
                    "Read-only continuation prohibits fixture mutations")
        step = {"name": label or tool, "tool": tool, "arguments": arguments or {}, "passed": False}
        report["steps"].append(step)
        begin = time.perf_counter()
        save()
        try:
            value = client.tool(tool, arguments or {})
            step.update(passed=True, result=value)
            return value
        except Exception as error:
            step["failure"] = type(error).__name__ + ": " + str(error)
            uncertain = isinstance(error, (queue.Empty, TimeoutError)) or any(word in str(error).lower() for word in ("timeout", "timed out", "host exited", "workerfault", "bridgefault"))
            if uncertain:
                report["unknownOutcome"] = {"tool": tool, "retried": False, "reason": "No native operation is replayed after an unknown outcome."}
            raise
        finally:
            step["milliseconds"] = (time.perf_counter() - begin) * 1000
            save()
            print(("PASS " if step["passed"] else "FAIL ") + step["name"], flush=True)

    def model(action, **request):
        return call("project_model", {"request": {"action": action, **request}}, "model " + action)

    def rejected(request, codes, name):
        response = client.request("tools/call", {"name": "project_model", "arguments": {"request": request}})
        value = response.get("structuredContent") or json.loads(response["content"][0]["text"])
        code = value.get("error", {}).get("code")
        report.setdefault("rejections", []).append({"name": name, "code": code, "response": value})
        check(name, response.get("isError") is True and code in codes)

    def page_all(snapshot, action="search", page_size=137, **filters):
        nodes, cursor = [], None
        while True:
            page = model(action, snapshotId=snapshot["snapshotId"], maxItems=page_size, cursor=cursor, **filters)
            require(page["rowPayloadBytes"] <= 512 * 1024, "Cached page exceeds its byte bound")
            nodes.extend(page["nodes"])
            cursor = page.get("nextCursor")
            if cursor is None:
                require(len(nodes) == page["totalMatches"], "Paged result count differs")
                break
            require(len(nodes) <= 50000, "Cursor did not terminate within the capture bound")
        require(len(nodes) == len({node["nodeId"] for node in nodes}), "Pages duplicated a node")
        return nodes

    try:
        client = Client(host, args.timeout, ["--worker", str(worker), "--artifacts", str(artifact_root)])
        initialize(client)
        session = call("tia_start")
        started = True
        pid = session["processId"]
        validate_owned_status(session, pid, require_owner=True)
        if prior:
            project = pathlib.Path(prior["projectPath"]).resolve()
            require(project.is_file() and project.is_relative_to((ROOT / "artifacts").resolve()), "Saved owned scale fixture is unavailable")
            call("project_open", {"path": str(project)})
            validate_owned_status(call("tia_session_status"), pid, project, require_owner=True)
            plc = prior["plcPath"]
            imported = next(step["arguments"]["payload"] for step in prior["steps"] if step["tool"] == "change_prepare")
            table = {"path": imported["tagTablePath"], "name": imported["expectedTableNames"][0]}
            report.update(projectPath=str(project), processId=pid, plcPath=plc)
            check("recorded native prepared change invalidated the previous exact selection", prior["rejections"][0]["code"] == "ModelInvalidated")
        else:
            scratch = call("tia_create_scratch")
            require(scratch.get("completed") is True and scratch["compile"]["errorCount"] == 0, "Fresh scratch creation/compile failed")
            project = pathlib.Path(scratch["project"]["path"])
            report.update(projectPath=str(project), processId=pid, selectedCpu=scratch["selectedCpu"])
            inventory = call("project_inventory", {"maxItems": 2000})
            require(len(inventory["plcs"]) == 1, "Owned scale fixture needs exactly one PLC")
            plc = inventory["plcs"][0]["path"]
            report["plcPath"] = plc
            software = call("software_inspect", {"plcPath": plc, "maxItems": 2000})
            tables = [table for table in software["tagTables"] if table.get("scopePath") == plc]
            require(len(tables) == 1 and not tables[0]["tags"], "Expected one empty native scratch table")
            table = tables[0]
            exported_empty = call("tag_table_read", {"plcPath": plc, "tagTablePath": table["path"]})
            empty_xml = transfer(client, exported_empty["artifact"], args.report.with_suffix(".before-tags.xml"))
            xml = generated_tag_table(empty_xml)
            fixture_path = args.report.with_suffix(".import.xml")
            fixture_path.write_text(xml, encoding="utf-8")
            report["fixtureXml"] = {"path": str(fixture_path), "bytes": len(xml.encode("utf-8")), "sha256": hashlib.sha256(xml.encode("utf-8")).hexdigest()}
            before = model("capture", lifetimeSeconds=900)
            before_blocks = page_all(before, kind="block")
            previous_ref = exact_reference(before, next(node for node in before_blocks if node["name"] == "TiaMcp_EnableGate"))
            validate_owned_status(call("tia_session_status"), pid, project, require_owner=True)
            begin = time.perf_counter()
            prepared = call("change_prepare", {"kind": "tag_table_import_xml", "plcPath": plc,
                            "payload": {"tagTablePath": table["path"], "xml": xml, "expectedTableNames": [table["name"]]}})
            report["preparedChange"] = {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}
            applied = call("change_apply", {**report["preparedChange"], "idempotencyKey": uuid.uuid4().hex,
                                          "checkpointSave": True, "compile": True, "save": True})
            report["fixturePreparationApplyCompileMilliseconds"] = (time.perf_counter() - begin) * 1000
            require(applied["state"] == "applied" and applied["compile"]["errorCount"] == 0, "2,000-tag import did not apply/compile")
            rejected({"action": "details", "reference": previous_ref}, {"ModelInvalidated", "SnapshotInvalidated", "SnapshotNotFound", "SnapshotExpired", "StaleSelection"}, "prepared fixture change invalidates earlier exact selection")
        native_export = call("tag_table_read", {"plcPath": plc, "tagTablePath": table["path"]})
        native_bytes = transfer(client, native_export["artifact"], args.report.with_suffix(".native-tags.xml"))
        expected = validate_native_tags(native_bytes, table["path"])
        report["nativeTagExport"] = {"sha256": native_export["artifact"]["sha256"], "bytes": len(native_bytes), "independentlyVerifiedTags": len(expected)}
        old_index = call("project_index", {"request": {"action": "capture", "lifetimeSeconds": 900}})
        snapshot = model("capture", lifetimeSeconds=900, maxVisited=50000, maxBytes=32 * 1024 * 1024)
        report["nativeCapture"] = snapshot["usage"]
        nodes = page_all(snapshot)
        report["nodeCount"] = len(nodes)
        actual_tags = {node["name"]: {"objectPath": node["objectPath"], "dataType": node["metadata"]["dataType"],
                        "logicalAddress": node["metadata"]["logicalAddress"]} for node in nodes if node["kind"] == "tag"}
        check("all 2,000 native tag identities/types/addresses match independent export", actual_tags == expected)
        ids = {node["nodeId"] for node in nodes}
        check("every captured node has its exact captured ancestor", all(node.get("parentId") is None or node["parentId"] in ids for node in nodes))
        check("virtual navigation never exposes engineering object paths", all(bool(node.get("objectPath")) == (node["origin"] == "native") for node in nodes))
        partition = []
        for parent in sorted({node.get("parentId") for node in nodes}, key=lambda value: value or ""):
            children = page_all(snapshot, "children", page_size=73, parentId=parent)
            require({node["nodeId"] for node in children} == {node["nodeId"] for node in nodes if node.get("parentId") == parent}, "Independent parent partition differs")
            partition.extend(children)
        check("all child pages cover the native hierarchy without omissions or duplicates", len(partition) == len(nodes) == len({node["nodeId"] for node in partition}))
        filtered = page_all(snapshot, text="ExplorerScale_", kind="tag", plcPath=plc)
        check("filtered paged search returns precisely the generated native declarations", len(filtered) == 2000 and {node["name"] for node in filtered} == set(expected))
        legacy = call("project_index", {"request": {"action": "query", "indexId": old_index["indexId"], "maxItems": 200}})
        check("legacy project_index callers survive explorer reads", legacy["valid"] and bool(legacy["rows"]))
        tag_node = next(node for node in nodes if node["name"] == "ExplorerScale_1999")
        reference = exact_reference(snapshot, tag_node)
        report["selectedReference"] = reference
        detail = model("details", reference=reference)
        check("exact final tag selection preserves independently verified metadata", detail["status"] == "available" and detail["reference"] == reference
              and detail["details"]["logicalAddress"] == "%MB1999" and detail["contentRevision"]["status"] == "unavailable")
        block = next(node for node in nodes if node["kind"] == "block" and node["name"] == "TiaMcp_EnableGate")
        block_ref = exact_reference(snapshot, block)
        block_detail = model("details", reference=block_ref)
        check("native block content still inspected in the large metadata fixture", block_detail["status"] == "available" and block_detail["contentRevision"]["status"] == "available")
        graph = model("relationships", reference=block_ref, depth=1, maxNodes=100, maxEdges=200)
        validate_fixture_relationships(graph, plc)
        check("authored calls/instance/member evidence remains correct at large metadata scale", True)
        verified = model("verify", reference=reference)
        check("exact reference metadata verification explicitly excludes content", verified["matchesCurrentMetadata"] and verified["referenceVerified"] and verified["contentVerified"] is False)
        timings, sizes = [], []
        for _ in range(25):
            begin = time.perf_counter()
            response = client.tool("project_model", {"request": {"action": "search", "snapshotId": snapshot["snapshotId"], "text": "ExplorerScale_", "maxItems": 200}})
            timings.append((time.perf_counter() - begin) * 1000)
            sizes.append(response["rowPayloadBytes"])
        report["cachedQueryMilliseconds"] = {"count": 25, "median": statistics.median(timings), "p95": sorted(timings)[23],
                                             "maximum": max(timings), "includesMcpTransport": True, "samples": timings}
        report["cachedPageBytes"] = {"maximum": max(sizes), "samples": sizes}
        report["exports"] = {}
        for format in ("json", "html"):
            exported = model("export", snapshotId=snapshot["snapshotId"], format=format)
            destination = args.report.with_suffix(".snapshot.json" if format == "json" else ".html")
            data = transfer(client, exported["artifact"], destination)
            report["exports"][format] = {"path": str(destination), "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}
            if format == "json":
                portable = json.loads(data)
        schema = runpy.run_path(str(ROOT / "tests/ProjectModelContracts.py"))
        errors = [str(error) for error in schema["Draft202012Validator"](schema["MODEL"], registry=schema["REGISTRY"], format_checker=schema["FORMATS"]).iter_errors(portable)]
        report["schemaValidation"] = {"passed": not errors, "errors": errors}
        check("large portable native snapshot passes versioned schema", not errors)
        invalid_ref = {**reference, "sessionGeneration": "stale-generation"}
        rejected({"action": "details", "reference": invalid_ref}, {"SessionMismatch", "StaleSelection", "InvalidSelection"}, "stale generation cannot substitute a same-named tag")
        partial = model("capture", maxVisited=12, lifetimeSeconds=900)
        check("native bounded capture reports explicit partial coverage", partial["usage"]["visited"] <= 12 and any(item["status"] in ("partial", "truncated") for item in partial["coverage"]))
        model("release", snapshotId=partial["snapshotId"])
        rejected({"action": "search", "snapshotId": partial["snapshotId"]}, {"SnapshotNotFound", "SnapshotReleased", "ModelNotFound"}, "released snapshot cannot be queried")
        rejected({"action": "change_apply"}, {"InvalidRequest"}, "explorer route rejects mutation")
        call("tia_detach")
        started = False
        rejected({"action": "details", "reference": reference}, {"ModelInvalidated", "SnapshotInvalidated", "SessionMismatch", "SnapshotNotFound", "StaleSelection"}, "session loss invalidates exact selection")
        report.update(completed=True, status="passed")
    except Exception as error:
        report.update(failure=type(error).__name__ + ": " + str(error), status="unknownOutcome" if uncertain else "blocked")
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
        report["finishedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        save()
        print("Report: " + str(args.report), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release-file", type=pathlib.Path, default=ROOT / "artifacts/project-explorer-release.txt")
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/project-explorer-native-large-tags.json")
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--verify-report", type=pathlib.Path, help="Continue the exact saved owned fixture read-only after its known successful import; never replays that import.")
    run(parser.parse_args())
