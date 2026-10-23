"""Positive configured-I/O explorer qualification on a fresh, owned offline scratch.

Runs an immutable packaged host and worker. All fixture changes use prepared
changes; unknown outcomes are recorded without retry or recovery mutation.
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import queue
import time
import uuid
import xml.etree.ElementTree as ET

from smoke_mcp import Client, ROOT
from qualify_project_index import initialize, require, validate_owned_status
from qualify_project_explorer import exact_reference, transfer


CPU = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"
MODULE = "OrderNumber:6ES7 551-1AB01-0AB0/V2.0"
MODULE_NAME = "ExplorerConfiguredIoFixture"


def validate_io_graph(graph, tag_path, hardware_path, address, native_range):
    """Independent oracle uses the authored tag path and inspected address range."""
    matches = [edge for edge in graph.get("edges", []) if edge.get("kind") == "configuredIo"
               and (edge.get("source") or {}).get("objectPath") == tag_path
               and (edge.get("target") or {}).get("objectPath") == hardware_path]
    require(len(matches) == 1, "Expected exactly one tag-to-selected-hardware configuredIo edge")
    edge = matches[0]
    evidence = edge.get("evidence", {})
    native = evidence.get("native", {})
    require(evidence.get("provider") == "nativeConfiguredAddress", "Wrong evidence provider")
    require(evidence.get("origin") == "nativeConfirmed" and evidence.get("resolution") == "resolved"
            and edge.get("unresolved") is None, "Configured relationship is not natively resolved")
    require(native.get("plcOwnershipVerified") is True and native.get("ownershipResolution") == "verified",
            "Overlap alone cannot establish the selected PLC's ownership")
    require(evidence.get("sourcePath") == hardware_path and native.get("hardwarePath") == hardware_path,
            "Evidence must point to the exact independently inspected hardware object")
    require(native.get("queryAddress") == address and native.get("ioType") == "Input", "Tag address or I/O direction differs")
    require(native.get("startAddress") == native_range["startAddress"] and native.get("length") == native_range["length"]
            and native.get("lengthUnit") == "bits", "Graph must preserve the native configured address range and units")
    require(native_range["length"] >= 8 and address == "%IB" + str(native_range["startAddress"]), "Expected authored one-byte input binding")
    require(evidence.get("capturedAtUtc") and evidence.get("contentRevision") == {
        "status": "unavailable", "scope": "configuredAddressRange"}, "Evidence must keep its separate freshness boundary")
    require(not any(edge.get("kind") == "configuredIo" and (edge.get("source") or {}).get("objectPath") == hardware_path
                    and (edge.get("target") or {}).get("objectPath") == tag_path for edge in graph.get("edges", [])),
            "Reverse lookup must not reverse the underlying semantic edge direction")
    return edge


def run(args):
    prior = None
    source_report = args.verify_report or args.readback_report
    if source_report:
        prior = json.loads(source_report.read_text(encoding="utf-8"))
    if args.verify_report:
        require(prior.get("completed") is True and prior.get("knownPositiveBindingVerified") is True
                and not prior.get("unknownOutcome") and prior.get("authoredBinding"),
                "Read-only verification requires a completed positive owned-fixture report")
    elif args.readback_report:
        failures = [step for step in prior["steps"] if not step.get("passed")]
        applied = [step for step in prior["steps"] if step["tool"] == "change_apply" and step.get("passed")]
        require(prior.get("status") == "blocked" and not prior.get("unknownOutcome") and len(applied) >= 1
                and all(step["result"]["state"] == "applied" for step in applied),
                "Continuation requires known-success prepared changes and no unknown native outcome")
        require(not failures or len(failures) == 1 and failures[0]["tool"] == "project_model_relationships"
                and "InvalidRequest" in failures[0].get("failure", ""),
                "Continuation is only allowed after a fixture assertion or rejected read-only relationship request")
        require(any(step["tool"] == "tia_start" and step.get("result", {}).get("ownsPortalProcess") is True for step in prior["steps"])
                and any(step["tool"] == "tia_create_scratch" and step.get("result", {}).get("completed") is True for step in prior["steps"]),
                "Continuation requires this runner's own recorded fresh scratch provenance")
    release = pathlib.Path(args.release_file.read_text(encoding="utf-8-sig").strip()).resolve()
    host = release / "host/TiaMcp.Host.exe"
    worker = release / "workers/openness/TiaMcp.Openness.V21.exe"
    require(host.is_file() and worker.is_file(), "Immutable release host/worker are missing")
    args.report = args.report.resolve()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    run_root = args.report.parent / ("ex_io_" + uuid.uuid4().hex[:8])
    run_root.mkdir(exist_ok=False)
    report = {"schemaVersion": 1, "completed": False, "status": "running", "steps": [],
              "observedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "release": str(release), "artifactRoot": str(run_root),
              "qualificationScope": "One fresh owned offline central rack and exact input-byte tag; configured range/controller ownership only. No channels, terminals, cables, physical topology, online or runtime claim.",
              "fixture": {"cpuTypeIdentifier": CPU, "moduleTypeIdentifier": MODULE, "moduleName": MODULE_NAME},
              "binaries": {"hostAssembly": {"path": str(host.with_suffix('.dll')), "sha256": hashlib.sha256(host.with_suffix('.dll').read_bytes()).hexdigest()},
                           "worker": {"path": str(worker), "sha256": hashlib.sha256(worker.read_bytes()).hexdigest()}},
              "fixtureSourceSha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest()}
    if prior:
        if not args.verify_report:
            require(prior["release"] == str(release), "Fixture continuation must use the same immutable release")
        report["readbackProvenance"] = {"report": str(source_report.resolve()), "projectPath": prior["projectPath"],
                                       "readOnly": bool(args.verify_report),
                                       "reason": "Reopen this runner's saved exact scratch. Completed module insertion is not repeated; final verification permits no fixture changes."}
    client = None
    started = False
    session_uncertain = False
    active_change = None

    def save():
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    def call(name, arguments=None, label=None):
        nonlocal session_uncertain
        step = {"name": label or name, "tool": name, "arguments": arguments or {}, "passed": False}
        report["steps"].append(step)
        started_at = time.perf_counter()
        save()
        try:
            value = client.tool(name, arguments or {})
            step.update(passed=True, result=value)
            return value
        except Exception as error:
            step["failure"] = type(error).__name__ + ": " + str(error)
            session_uncertain = isinstance(error, (queue.Empty, TimeoutError)) or any(
                token in str(error).lower() for token in ("timeout", "timed out", "workerfault", "bridgefault", "host exited"))
            if session_uncertain:
                report["unknownOutcome"] = {"tool": name, "arguments": arguments or {}, "retried": False,
                                            "reason": "Native outcome is unknown. No operation is replayed or automatically recovered."}
            raise
        finally:
            step["milliseconds"] = (time.perf_counter() - started_at) * 1000
            save()
            print(("PASS " if step["passed"] else "FAIL ") + step["name"], flush=True)

    def apply(kind, payload):
        nonlocal active_change
        require(not args.verify_report, "Read-only final verification cannot prepare or apply a fixture change")
        validate_owned_status(call("tia_session_status"), report["processId"], pathlib.Path(report["projectPath"]), require_owner=True)
        prepared = call("change_prepare", {"kind": kind, "payload": payload, "plcPath": report["plcPath"]})
        active_change = {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}
        report["activePreparedChange"] = active_change
        save()
        result = call("change_apply", {**active_change, "idempotencyKey": uuid.uuid4().hex,
                                      "checkpointSave": True, "compile": True, "save": True})
        require(result.get("state") == "applied" and result.get("compile", {}).get("errorCount") == 0,
                "Prepared fixture operation did not apply and compile cleanly")
        active_change = None
        report.pop("activePreparedChange", None)
        return result

    def model(action, **arguments):
        return call("project_model_" + action, {"request": {"action": action, **arguments}})

    try:
        client = Client(host, args.timeout, ["--worker", str(worker), "--artifacts", str(run_root)])
        initialize(client)
        session = call("tia_start")
        report["processId"] = session["processId"]
        started = True
        validate_owned_status(session, report["processId"], require_owner=True)
        if prior:
            call("project_open", {"path": prior["projectPath"]})
            report["projectPath"] = prior["projectPath"]
            validate_owned_status(call("tia_session_status"), report["processId"], pathlib.Path(report["projectPath"]), require_owner=True)
        else:
            scratch = call("tia_create_scratch", {"cpuTypeIdentifier": CPU})
            require(scratch.get("completed") is True and scratch.get("compile", {}).get("errorCount") == 0, "Fresh scratch creation/compile failed")
            report["projectPath"] = scratch["project"]["path"]
        inventory = call("project_inventory", {"maxItems": 2000})
        require(len(inventory["plcs"]) == 1, "Owned fixture requires exactly one PLC")
        report["plcPath"] = inventory["plcs"][0]["path"]
        if prior:
            require(report["plcPath"] == prior["plcPath"], "Saved fixture PLC identity changed")
        rails = [item for item in inventory["hardware"] if item["positionNumber"] == 0 and item["path"].count("/items/") == 1]
        require(len(rails) == 1, "Owned fixture requires exactly one central rack")
        catalog = call("hardware_catalog_search", {"filter": "TM PosInput 2", "maxItems": 500})
        require(any(entry.get("typeIdentifier") == MODULE for entry in catalog["entries"]), "Exact known local module is unavailable")
        # hardware_add creates a new Device; hardware_plug is the native rack insertion operation.
        if not prior:
            apply("hardware_plug", {"hardwarePath": rails[0]["path"], "typeIdentifier": MODULE, "name": MODULE_NAME, "positionNumber": 2})
        hardware = call("hardware_inspect", {"maxItems": 2000})
        # This native module contains a same-named submodule. The plug preview
        # identifies the top-level slot; display name alone is not a selector.
        module = [item for item in hardware["items"] if item["name"] == MODULE_NAME
                  and item["positionNumber"] == 2 and item["path"].count("/items/") == 1]
        require(len(module) == 1, "Prepared module insertion lacks its unique native top-level slot object")
        module_path = module[0]["path"]
        ranges = [(item, address) for item in hardware["items"] if item["path"] == module_path or item["path"].startswith(module_path + "/")
                  for address in item.get("addresses", []) if address.get("ioType") == "Input" and address.get("length", 0) >= 8]
        require(len(ranges) == 1, "Expected one exact input address range on the local module")
        owner, native_range = ranges[0]
        report["inspectedHardware"] = {"modulePath": module_path, "rangeOwnerPath": owner["path"], "range": native_range}
        logical_address = "%IB" + str(native_range["startAddress"])
        software = call("software_inspect", {"plcPath": report["plcPath"], "maxItems": 2000})
        tags = [(table, tag) for table in software["tagTables"] for tag in table.get("tags", []) if tag["name"] == "inputByte"]
        if not tags:
            require(not args.verify_report, "Saved exact verification tag is missing; refusing replacement")
            tables = [table for table in software["tagTables"] if table.get("scopePath") == report["plcPath"]]
            require(len(tables) == 1 and not tables[0].get("tags"),
                    "Fixture tag creation requires the exact empty standard scratch tag table")
            empty = tables[0]
            exported = call("tag_table_read", {"tagTablePath": empty["path"], "plcPath": report["plcPath"]})
            document = ET.fromstring(exported["xml"])
            native_table = document.find("SW.Tags.PlcTagTable")
            require(native_table is not None, "Native empty tag export lacks SW.Tags.PlcTagTable")
            objects = native_table.find("ObjectList")
            if objects is None:
                objects = ET.SubElement(native_table, "ObjectList")
            ids = [int(item.attrib["ID"], 16) for item in document.iter() if item.attrib.get("ID")]
            native_tag = ET.SubElement(objects, "SW.Tags.PlcTag", {"ID": format(max(ids, default=0) + 1, "X"), "CompositionName": "Tags"})
            attributes = ET.SubElement(native_tag, "AttributeList")
            for name, value in (("Name", "inputByte"), ("DataTypeName", "Byte"),
                                ("LogicalAddress", "%IB" + str(native_range["startAddress"] + 1))):
                ET.SubElement(attributes, name).text = value
            apply("tag_table_import_xml", {"tagTablePath": empty["path"], "xml": ET.tostring(document, encoding="unicode"), "expectedTableNames": [empty["name"]]})
            software = call("software_inspect", {"plcPath": report["plcPath"], "maxItems": 2000})
            tags = [(table, tag) for table in software["tagTables"] for tag in table.get("tags", []) if tag["name"] == "inputByte"]
        require(len(tags) == 1, "Fixture must have one known inputByte tag")
        table, tag = tags[0]
        require(tag["dataType"] == "Byte", "Known scratch tag must be one byte wide")
        report["authoredBinding"] = {"tagPath": tag["path"], "tablePath": table["path"], "beforeAddress": tag["logicalAddress"],
                                     "logicalAddress": logical_address, "expectedHardwarePath": owner["path"]}
        if prior and prior.get("authoredBinding"):
            require(prior["authoredBinding"]["tagPath"] == tag["path"]
                    and prior["authoredBinding"]["expectedHardwarePath"] == owner["path"]
                    and prior["authoredBinding"]["logicalAddress"] == logical_address, "Saved exact binding differs from independent readback")
        if tag["logicalAddress"] != logical_address:
            apply("tag_set", {"tagTablePath": table["path"], "memberName": tag["name"], "attributes": {"LogicalAddress": logical_address}})
        readback = call("software_inspect", {"plcPath": report["plcPath"], "maxItems": 2000})
        observed = [entry for table in readback["tagTables"] for entry in table.get("tags", []) if entry["path"] == tag["path"]]
        require(len(observed) == 1 and observed[0]["logicalAddress"] == logical_address, "Exact native tag readback differs from requested address")
        snapshot = model("capture", lifetimeSeconds=900)
        report["nativeCapture"] = snapshot["usage"]
        nodes, cursor = [], None
        while True:
            page = model("search", snapshotId=snapshot["snapshotId"], maxItems=200, cursor=cursor)
            nodes.extend(page["nodes"])
            cursor = page.get("nextCursor")
            if cursor is None:
                require(len(nodes) == page["totalMatches"], "Snapshot paging omissions")
                break
            require(len(nodes) < 2000, "Tiny fixture unexpectedly exceeded bounded paging")
        tag_node = next(node for node in nodes if node.get("objectPath") == tag["path"])
        hardware_node = next(node for node in nodes if node.get("objectPath") == owner["path"])
        require(tag_node["kind"] == "tag" and hardware_node["kind"] == "hardware", "Model kinds differ from independently inspected native objects")
        tag_ref, hardware_ref = exact_reference(snapshot, tag_node), exact_reference(snapshot, hardware_node)
        report["references"] = {"tag": tag_ref, "hardware": hardware_ref}
        tag_graph = model("relationships", reference=tag_ref, kinds=["configuredIo"], direction="out", depth=1, maxNodes=10, maxEdges=20)
        report["tagEdge"] = validate_io_graph(tag_graph, tag["path"], owner["path"], logical_address, native_range)
        hardware_graph = model("relationships", reference=hardware_ref, kinds=["configuredIo"], direction="in", depth=1, maxNodes=10, maxEdges=20)
        report["hardwareEdge"] = validate_io_graph(hardware_graph, tag["path"], owner["path"], logical_address, native_range)
        require(hardware_graph["reference"] == hardware_ref and tag_graph["reference"] == tag_ref, "Graph lost exact selected references")
        report["stableEdgeIdAcrossSelections"] = report["tagEdge"]["edgeId"] == report["hardwareEdge"]["edgeId"]
        if args.verify_report:
            require(report["stableEdgeIdAcrossSelections"], "Final package must retain one stable edge identity across selections")
        exported = model("export", snapshotId=snapshot["snapshotId"])
        destination = args.report.with_suffix(".snapshot.json")
        portable = json.loads(transfer(client, exported["artifact"], destination))
        retained = [edge for edge in portable["edges"] if edge.get("kind") == "configuredIo"
                    and (edge.get("source") or {}).get("objectPath") == tag["path"]
                    and (edge.get("target") or {}).get("objectPath") == owner["path"]]
        require(retained, "Portable export omitted observed I/O evidence")
        report["retainedBindingEdgeCount"] = len(retained)
        if args.verify_report:
            require(len(retained) == 1, "Final export must deduplicate a binding observed from both selections")
        report["portableSnapshot"] = str(destination)
        report["knownPositiveBindingVerified"] = True
        report["completed"] = True
        report["status"] = "passed"
    except Exception as error:
        report["failure"] = type(error).__name__ + ": " + str(error)
        report["status"] = "unknownOutcome" if session_uncertain else "blocked"
        raise
    finally:
        if client is not None:
            if started and not session_uncertain:
                try:
                    call("tia_detach")
                except Exception as error:
                    report["detachFailure"] = str(error)
            client.close()
            report["hostExitCode"] = client.process.returncode
        save()
        print("Report: " + str(args.report), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release-file", type=pathlib.Path, default=ROOT / "artifacts/project-explorer-release.txt")
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/project-explorer-native-io.json")
    parser.add_argument("--timeout", type=int, default=300)
    previous = parser.add_mutually_exclusive_group()
    previous.add_argument("--readback-report", type=pathlib.Path, help="Continue this qualifier's saved owned scratch after a fixture assertion or known rejected read-only request, never an unknown outcome; never repeats module insertion.")
    previous.add_argument("--verify-report", type=pathlib.Path, help="Strictly read-only reopen of a completed positive fixture report against the selected immutable release.")
    run(parser.parse_args())


if __name__ == "__main__":
    main()
