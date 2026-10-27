"""Actual stdio OPC UA artifact workflow; deliberately nonexistent native workers, no TIA."""
import argparse
import base64
import hashlib
import json
import pathlib
import time

from jsonschema import Draft202012Validator

from smoke_mcp import Client, ROOT


TOOLS = {
    "opcua_inspect", "opcua_compare", "opcua_dependencies", "opcua_dependency_compare",
    "opcua_report_page", "opcua_reopen", "opcua_list", "opcua_artifact_read",
}
ALLOWED = TOOLS | {
    "artifact_upload_begin", "artifact_upload_chunk", "artifact_upload_commit", "artifact_upload_release",
}


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    output = args.output.resolve()
    require(not output.exists(), "Use a new isolated output directory")
    output.mkdir(parents=True)
    missing = output / "intentionally-absent-native-worker.exe"
    artifacts = output / "host-artifacts"
    report = {"schemaVersion": 1, "completed": False, "nativeExecuted": False,
              "targetProbed": False, "checks": [], "hostExits": [], "uploadRetries": []}
    host = args.host.resolve()
    report["hostSha256"] = digest(host.read_bytes())
    report["harnessSha256"] = digest(pathlib.Path(__file__).read_bytes())
    report["assemblySha256"] = {name: digest((host.parent / name).read_bytes()) for name in (
        "TiaMcp.Host.dll", "TiaMcp.Core.dll", "TiaMcp.OpcUa.dll")}
    fixture = ROOT / "fixtures/opcua"
    schema = json.loads((ROOT / "contracts/opc-ua-workflow.v1.schema.json").read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    report["contractSha256"] = digest((ROOT / "contracts/opc-ua-workflow.v1.schema.json").read_bytes())
    client = None

    def check(name, condition):
        require(condition, name)
        report["checks"].append(name)

    def start():
        nonlocal client
        require(not missing.exists(), "Native worker must be absent")
        client = Client(host, 60, ["--worker", str(missing), "--sim-worker", str(missing),
                                 "--artifacts", str(artifacts)])
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                      "clientInfo": {"name": "opcua-offline-contract", "version": "1"}})
        client.notify("notifications/initialized")
        return client

    def call(name, arguments=None):
        require(name in ALLOWED, "Harness attempted a non-offline call")
        value = client.tool(name, arguments or {})
        if name in TOOLS:
            validator.validate(value)
        return value

    def reject(name, arguments, code):
        require(name in ALLOWED, "Harness attempted a non-offline call")
        result = client.request("tools/call", {"name": name, "arguments": arguments})
        if name in TOOLS and "structuredContent" in result:
            validator.validate(result["structuredContent"])
        check(name + " rejects " + code, result.get("isError") and
              result.get("structuredContent", {}).get("error", {}).get("code") == code)

    def upload(name, data):
        status = call("artifact_upload_begin", {"fileName": name, "expectedBytes": len(data), "sha256": digest(data)})
        size = status["maxChunkBytes"]
        for offset in range(0, len(data), size):
            arguments = {"uploadId": status["uploadId"], "offset": offset,
                         "data": base64.b64encode(data[offset:offset + size]).decode("ascii")}
            # Windows background scanners can transiently hold upload.json during its
            # atomic replacement. Exact-byte chunk retry is part of the upload contract.
            for attempt in range(3):
                result = client.request("tools/call", {"name": "artifact_upload_chunk", "arguments": arguments})
                if not result.get("isError"):
                    break
                error = result.get("structuredContent", {}).get("error", {})
                require(error.get("code") == "PackageInvalid" and attempt < 2, "Upload failed: " + str(error))
                report["uploadRetries"].append({"uploadId": status["uploadId"], "offset": offset, "error": error})
                time.sleep(0.05 * (attempt + 1))
        return call("artifact_upload_commit", {"uploadId": status["uploadId"]})["artifactRef"]

    def capture(name):
        return call("opcua_inspect", {"nodeSetUploadRef": upload(pathlib.Path(name).name, (fixture / name).read_bytes())})

    def download(reference):
        data = bytearray()
        while True:
            page = call("opcua_artifact_read", {"artifactRef": reference, "offset": len(data)})
            require(page["count"] <= 65536 and page["offset"] == len(data), "Unbounded or inconsistent download")
            data.extend(base64.b64decode(page["data"], validate=True))
            if page["complete"]:
                require(digest(data) == page["sha256"], "Download digest mismatch")
                return bytes(data)

    def artifact(record, name):
        return record["recordRef"] + ":" + name

    def record_path(record, name):
        # Only this harness's own isolated artifacts are changed for integrity negatives.
        return artifacts / "opcua-workflow" / record["recordRef"].split(":")[1] / name

    def stop():
        exit_receipt = client.close()
        report["hostExits"].append(exit_receipt)
        require(exit_receipt["hostExitCode"] == 0 and not exit_receipt["forcedHostStop"], "Host exit was not clean")
        require(all(receipt.get("started") is not True for receipt in exit_receipt["workerExits"]), "Native worker started")

    try:
        client = start()
        tools = {t["name"]: t for t in client.request("tools/list")["tools"]}
        check("all eight production OPC UA tools registered", TOOLS <= tools.keys())
        check("all OPC UA tools are closed-world with no path/output/overwrite input", all(
            tools[name]["annotations"]["openWorldHint"] is False and
            not {"path", "outputPath", "overwrite"} & tools[name]["inputSchema"].get("properties", {}).keys()
            for name in TOOLS))
        check("offline OPC UA tool set unchanged beside worker-backed engineering tools", {name for name in tools if name.startswith("opcua_") and not name.startswith("opcua_engineering_")} == TOOLS)

        original = (fixture / "machine-v1.xml").read_bytes()
        original_upload = upload("machine-v1.xml", original)
        before = call("opcua_inspect", {"nodeSetUploadRef": original_upload})
        after = capture("machine-v2.xml")
        check("exact original content hash and offline flags", before["summary"]["sourceSha256"] == digest(original)
              and not before["nativeQualified"] and not before["nativeFreshnessVerified"])
        check("exact original download", download(artifact(before, "original.xml")) == original)
        call("artifact_upload_release", {"uploadId": original_upload[7:]})
        check("independent retained copy survives upload release", download(artifact(before, "original.xml")) == original)
        first_page = call("opcua_report_page", {"recordRef": before["recordRef"], "section": "nodes", "pageSize": 1})
        next_page = call("opcua_report_page", {"recordRef": before["recordRef"], "section": "nodes", "pageSize": 1,
                                               "cursor": first_page["nextCursor"]})
        check("actual MCP cursor roundtrip", next_page["offset"] == 1 and len(next_page["rows"]) == 1)
        reject("opcua_report_page", {"recordRef": after["recordRef"], "section": "nodes", "cursor": first_page["nextCursor"]}, "OpcUaStaleCursor")
        reject("opcua_report_page", {"recordRef": before["recordRef"], "section": "nodes", "pageSize": 501}, "OpcUaPageLimit")
        reject("opcua_report_page", {"recordRef": before["recordRef"], "section": "../original.xml"}, "OpcUaInvalidSection")

        manifest = (fixture / "consumers-v1.json").read_bytes()
        comparison = call("opcua_compare", {"beforeRef": before["recordRef"], "afterRef": after["recordRef"],
                                             "consumerManifestUploadRef": upload("consumers.json", manifest)})
        impacts = call("opcua_report_page", {"recordRef": comparison["recordRef"], "section": "clientImpacts"})
        check("three independent breaking changes and affected consumers", comparison["summary"]["breakingChangeCount"] == 3 and
              len(impacts["rows"]) == 3 and all(row["status"] == "affected" for row in impacts["rows"]))
        check("exact supplied manifest retained with unknown consumers", download(artifact(comparison, "consumers.json")) == manifest
              and comparison["summary"]["unknownConsumers"] and not comparison["summary"]["compatibilityCertified"])

        annotated = original.replace(b"<DisplayName>Speed</DisplayName>", b"<DisplayName>Speed<!-- retained annotation --></DisplayName>") + b"<?vendor retained?>"
        require(annotated != original, "Annotation adversary must change the source")
        annotated_record = call("opcua_inspect", {"nodeSetUploadRef": upload("annotations.xml", annotated)})
        check("annotations retain exact original uploaded bytes", download(artifact(annotated_record, "original.xml")) == annotated)
        annotation_comparison = call("opcua_compare", {"beforeRef": before["recordRef"], "afterRef": annotated_record["recordRef"],
            "consumerManifestUploadRef": upload("annotation-consumers.json", manifest)})
        annotation_changes = call("opcua_report_page", {"recordRef": annotation_comparison["recordRef"], "section": "changes"})["rows"]
        check("node and outside-root annotations are visible through MCP", {row["field"] for row in annotation_changes} == {"annotationsSha256", "envelopeOrExtensions"}
            and all(row["severity"] == "reviewRequired" for row in annotation_changes))
        annotation_impacts = call("opcua_report_page", {"recordRef": annotation_comparison["recordRef"], "section": "clientImpacts"})["rows"]
        check("annotation consumer impact survives report paging", any(row["status"] == "affected" and
            any(change["field"] == "annotationsSha256" for change in row["directChanges"]) for row in annotation_impacts))
        moved_annotation = annotated.replace(b"Speed<!-- retained annotation -->", b"Spe<!-- retained annotation -->ed")
        require(moved_annotation != annotated, "Annotation offset adversary must move within unchanged text")
        moved_record = call("opcua_inspect", {"nodeSetUploadRef": upload("moved-annotation.xml", moved_annotation)})
        moved_comparison = call("opcua_compare", {"beforeRef": annotated_record["recordRef"], "afterRef": moved_record["recordRef"]})
        moved_changes = call("opcua_report_page", {"recordRef": moved_comparison["recordRef"], "section": "changes"})["rows"]
        check("annotation text offsets remain significant without false payload changes", len(moved_changes) == 1
            and moved_changes[0]["field"] == "annotationsSha256" and moved_changes[0]["severity"] == "reviewRequired")

        device = capture("dependencies/device.xml")
        types1 = capture("dependencies/types-v1.xml")
        types2 = capture("dependencies/types-v2.xml")
        first_set = call("opcua_dependencies", {"modelRefs": [device["recordRef"], types1["recordRef"]]})
        next_set = call("opcua_dependencies", {"modelRefs": [device["recordRef"], types2["recordRef"]]})
        set_hash = digest(("opcua-model-set.v1\n" + "\n".join(sorted(digest((fixture / name).read_bytes())
                          for name in ["dependencies/device.xml", "dependencies/types-v1.xml"])) + "\n").encode())
        check("explicit model set digest independently computed", first_set["summary"]["modelSetSha256"] == set_hash)
        check("selected edges resolve without completeness or version certification", first_set["summary"]["selectedDependenciesResolved"]
              and not first_set["summary"]["dependencyClosureComplete"] and not first_set["summary"]["requiredModelVersionsEvaluated"])
        set_comparison = call("opcua_dependency_compare", {"beforeSetRef": first_set["recordRef"], "afterSetRef": next_set["recordRef"],
                               "consumerManifestUploadRef": upload("consumers-v2.json", (fixture / "dependencies/consumers-v2.json").read_bytes())})
        set_impacts = call("opcua_report_page", {"recordRef": set_comparison["recordRef"], "section": "clientImpacts"})["rows"]
        sensor = next(row for row in set_impacts if row["consumerId"] == "SensorClient")
        check("v2 consumer transitive impact has actual dependency path", sensor["status"] == "potentiallyAffected" and
              any(len(row["path"]) == 2 for row in sensor["indirectChanges"]))
        check("nested before dependency section is pageable", call("opcua_report_page", {
            "recordRef": set_comparison["recordRef"], "section": "before.dependencies"})["total"] == 2)
        missing_set = call("opcua_dependencies", {"modelRefs": [device["recordRef"]]})
        diagnostics = call("opcua_report_page", {"recordRef": missing_set["recordRef"], "section": "diagnostics"})["rows"]
        limitations = call("opcua_report_page", {"recordRef": missing_set["recordRef"], "section": "limitations"})["rows"]
        check("missing and unsupported dependencies remain explicit", not missing_set["summary"]["selectedDependenciesResolved"] and
              any(row["code"] == "DependencyNodeNotProvided" for row in diagnostics) and any("remain opaque" in row for row in limitations))
        reject("opcua_dependencies", {"modelRefs": [device["recordRef"], device["recordRef"]]}, "OpcUaInvalidSources")
        reject("opcua_dependencies", {"modelRefs": []}, "OpcUaInvalidSources")
        reject("opcua_dependency_compare", {"beforeSetRef": before["recordRef"], "afterSetRef": first_set["recordRef"]}, "OpcUaInvalidReference")

        for path in ["../machine.xml", "C:\\Windows\\win.ini", "file:///tmp/a.xml"]:
            reject("opcua_reopen", {"recordRef": path}, "OpcUaInvalidReference")
        reject("opcua_inspect", {"nodeSetUploadRef": "../machine.xml"}, "InvalidRequest")
        reject("opcua_artifact_read", {"artifactRef": before["recordRef"] + ":../../receipt.json"}, "OpcUaInvalidReference")
        reject("opcua_artifact_read", {"artifactRef": artifact(before, "report.json"), "count": 65537}, "OpcUaPageLimit")
        reject("opcua_artifact_read", {"artifactRef": artifact(before, "report.json"), "offset": 10**12}, "OpcUaInvalidCursor")
        reject("opcua_inspect", {"nodeSetUploadRef": upload("malformed.xml", b"<unclosed")}, "OpcUaInvalidXml")
        reject("opcua_inspect", {"nodeSetUploadRef": upload("doctype.xml", b"<!DOCTYPE x SYSTEM 'http://127.0.0.1/never'><x/>")}, "OpcUaInvalidXml")
        reject("opcua_inspect", {"nodeSetUploadRef": upload("oversize.xml", b"x" * (16 * 1024 * 1024 + 1))}, "OpcUaCapacity")
        reject("opcua_compare", {"beforeRef": before["recordRef"], "afterRef": after["recordRef"],
                                 "consumerManifestUploadRef": upload("oversize.json", b" " * (1024 * 1024 + 1))}, "OpcUaCapacity")
        reject("opcua_compare", {"beforeRef": before["recordRef"], "afterRef": after["recordRef"],
                                 "consumerManifestUploadRef": upload("malformed.json", b"{")}, "OpcUaWorkflowFailed")
        stale = json.loads(manifest); stale["modelSha256"] = "0" * 64
        reject("opcua_compare", {"beforeRef": before["recordRef"], "afterRef": after["recordRef"],
                                 "consumerManifestUploadRef": upload("stale.json", json.dumps(stale).encode())}, "OpcUaStaleConsumers")
        stale_set = json.loads((fixture / "dependencies/consumers-v2.json").read_bytes()); stale_set["modelSetSha256"] = "0" * 64
        reject("opcua_dependency_compare", {"beforeSetRef": first_set["recordRef"], "afterSetRef": next_set["recordRef"],
                                            "consumerManifestUploadRef": upload("stale-v2.json", json.dumps(stale_set).encode())}, "OpcUaStaleConsumers")

        committed = upload("immutable.xml", original)
        reject("artifact_upload_chunk", {"uploadId": committed[7:], "offset": 0,
                                         "data": base64.b64encode(b"overwrite").decode()}, "UploadNotWritable")
        pending = call("artifact_upload_begin", {"fileName": "pending.xml", "expectedBytes": len(original), "sha256": digest(original)})
        reject("opcua_inspect", {"nodeSetUploadRef": "upload:" + pending["uploadId"]}, "UploadIncomplete")
        retained = {name: record_path(before, name).read_bytes() for name in ["original.xml", "report.json", "receipt.json"]}
        again = call("opcua_inspect", {"nodeSetUploadRef": committed})
        check("repeated inspection creates new record without overwriting previous publication", again["recordRef"] != before["recordRef"] and
              all(record_path(before, name).read_bytes() == data for name, data in retained.items()))

        large_xml = ("<UANodeSet xmlns='http://opcfoundation.org/UA/2011/03/UANodeSet.xsd'><NamespaceUris><Uri>urn:test</Uri>"
                     "</NamespaceUris><UAObject NodeId='ns=1;s=A' BrowseName='1:A'><References>" +
                     "<Reference ReferenceType='i=35'>ns=1;s=B</Reference>" * 4000 + "</References></UAObject></UANodeSet>").encode()
        large = call("opcua_inspect", {"nodeSetUploadRef": upload("large-row.xml", large_xml)})
        page = call("opcua_report_page", {"recordRef": large["recordRef"], "section": "nodes"})
        full = json.loads(download(artifact(large, "report.json")))
        check("oversize row is explicitly omitted while full report preserves all data", page["rows"][0]["omitted"] and
              len(json.dumps(page).encode()) < 128 * 1024 and len(full["nodes"][0]["references"]) == 4000)

        stop(); client = start()
        reopened = call("opcua_reopen", {"recordRef": set_comparison["recordRef"]})
        check("real Host restart retains exact dependency comparison", reopened == set_comparison)
        check("real Host restart retains exact original bytes", download(artifact(before, "original.xml")) == original)
        check("real Host restart retains annotation comparison", call("opcua_reopen", {"recordRef": annotation_comparison["recordRef"]}) == annotation_comparison)
        discovered = call("opcua_list", {"count": 100})
        check("restart discovery includes all complete records", any(row["recordRef"] == set_comparison["recordRef"] for row in discovered["rows"]))

        # Tamper only harness-owned isolated records, retaining each failure independently.
        stale_manifest = capture("machine-v1.xml")
        with record_path(stale_manifest, "intent.json").open("ab") as stream:
            stream.write(b" ")
        reject("opcua_reopen", {"recordRef": stale_manifest["recordRef"]}, "OpcUaIntegrityFailure")
        tampered = capture("machine-v1.xml")
        child = call("opcua_compare", {"beforeRef": tampered["recordRef"], "afterRef": after["recordRef"]})
        record_path(tampered, "original.xml").write_bytes(b"changed retained source")
        reject("opcua_reopen", {"recordRef": child["recordRef"]}, "OpcUaIntegrityFailure")
        reject("opcua_artifact_read", {"artifactRef": artifact(child, "report.json")}, "OpcUaIntegrityFailure")
        incomplete = capture("machine-v1.xml")
        record_path(incomplete, "receipt.json").rename(record_path(incomplete, "receipt.json.pending"))
        reject("opcua_reopen", {"recordRef": incomplete["recordRef"]}, "OpcUaPublicationIncomplete")
        listed = call("opcua_list", {"count": 100})["rows"]
        check("incomplete and integrity-failed records remain discoverable", {"incomplete", "integrity-failed"} <= {row["state"] for row in listed})
        native_store = artifacts / "engineering-operations"
        check("offline workflow creates no native operation records", not native_store.exists() or not any(native_store.iterdir()))
        check("worker path remains nonexistent", not missing.exists())
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
    finally:
        if client is not None:
            try:
                stop()
            except Exception as error:
                report["completed"] = False
                report["shutdownFailure"] = str(error)
        report["checkCount"] = len(report["checks"])
        (output / "verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(("PASS" if report["completed"] else "FAIL") + " OPC UA actual offline MCP: " +
          str(report["checkCount"]) + " checks; " + str(output / "verification.json"))
    if not report["completed"]:
        print(report.get("failure", report.get("shutdownFailure", "Unknown failure")))
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
