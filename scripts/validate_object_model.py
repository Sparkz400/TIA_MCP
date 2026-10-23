"""Qualify scope-aware routing on a newly created worker-owned scratch project only."""
import argparse
import base64
import hashlib
import json
import pathlib
import subprocess
import uuid
import xml.etree.ElementTree as ET

from smoke_mcp import Client, ROOT
from qualify_unit_routes import qualify_unit_routes


def semantic_xml(xml):
    document = ET.fromstring(xml)
    for child in list(document):
        if child.tag.rsplit("}", 1)[-1] == "DocumentInfo":
            document.remove(child)
    return hashlib.sha256(ET.tostring(document, encoding="utf-8")).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/object-model-live.json")
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=pathlib.Path)
    parser.add_argument("--seeder", type=pathlib.Path, default=ROOT / "scripts/native/UnitSeeder/bin/Debug/net48/UnitSeeder.exe")
    parser.add_argument("--project", type=pathlib.Path, help="Reopen a previously task-created scratch project under repository artifacts after a failed fixture setup.")
    parser.add_argument("--seeded-source-dir", type=pathlib.Path, help="Reuse this saved fixture's prior native source exports when its units were successfully seeded already.")
    parser.add_argument("--unit-routes-only", action="store_true", help="Run the added LAD/FBD and tag-table scope routes after fresh native fixture setup, without repeating the historical routing sequence.")
    args = parser.parse_args()
    run_dir = ROOT / "artifacts" / ("unit-validation-" + uuid.uuid4().hex[:12])
    run_dir.mkdir(parents=True)
    authorization = run_dir / "authorization.json"
    seeder = args.seeder.resolve()
    client = Client(args.host.resolve(), 300, ["--worker", str(args.worker.resolve())] if args.worker else None)
    report = {"schemaVersion": 1, "steps": [], "completed": False}

    def save():
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    def record(label, result):
        report["steps"].append({"name": label, "result": result})
        save()
        print("PASS " + label, flush=True)
        return result

    def call(name, arguments=None, label=None):
        return record(label or name, client.tool(name, arguments))

    def rejected(name, arguments, code):
        raw = client.request("tools/call", {"name": name, "arguments": arguments})
        value = raw.get("structuredContent") or json.loads(raw["content"][0]["text"])
        assert raw.get("isError") and value["error"]["code"] == code, value
        return record("rejected " + code, value)

    def native(mode):
        completed = subprocess.run([str(seeder), mode, str(authorization)], cwd=ROOT, capture_output=True, text=True, encoding="utf-8", timeout=300)
        result = json.loads(completed.stdout.strip().splitlines()[-1])
        record("native fixture " + mode, result)
        assert completed.returncode == 0 and result.get("completed"), result
        return result

    def prepare(kind, payload):
        return call("change_prepare", {"kind": kind, "payload": payload}, "prepare " + kind)

    def apply(kind, payload):
        prepared = prepare(kind, payload)
        try:
            result = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                          "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True}, "apply " + kind)
            assert result["state"] == "applied" and result["compile"]["errorCount"] == 0, result
            return result
        except Exception as primary_error:
            recovery = {"kind": kind, "primaryFailure": str(primary_error), "status": "blocked"}
            report.setdefault("failureRecovery", []).append(recovery)
            try:
                status = call("change_status", {"changeId": prepared["changeId"]})
                recovery["changeStatus"] = status
                if status.get("recoveryArchive"):
                    restored = call("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
                    recovery["result"] = restored
                    recovery["status"] = "passed"
                    report["recoveredProjectPath"] = restored["recoveredProject"]["path"]
            except Exception as recovery_error:
                recovery["error"] = str(recovery_error)
            save()
            raise

    def read(path):
        return call("block_read", {"blockPath": path}, "read " + path.rsplit("/", 1)[-1])

    def export_xml(exported):
        value = call("artifact_read", {"path": exported["artifact"]["path"]})
        assert value["complete"], "Native export is truncated"
        data = base64.b64decode(value["data"], validate=True)
        assert hashlib.sha256(data).hexdigest() == exported["artifact"]["sha256"], "Native export hash mismatch"
        return data.decode("utf-8-sig")

    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "unit-qualification", "version": "1"}})
        client.notify("notifications/initialized")
        started = call("tia_start")
        assert started["ownsPortalProcess"] is True, started
        if args.project:
            path = args.project.resolve()
            assert path.is_relative_to(ROOT / "artifacts") and path.name.startswith("TiaMcpScratch_") and path.suffix == ".ap21"
            opened = call("project_open", {"path": str(path)})
            report["projectPath"] = opened["project"]["path"]
        else:
            scratch = call("tia_create_scratch")
            assert scratch["completed"] and scratch["compile"]["errorCount"] == 0, scratch
            report["projectPath"] = scratch["project"]["path"]
        authorization.write_text(json.dumps({"processId": started["processId"], "projectPath": report["projectPath"],
                                             "ownsPortalProcess": True, "creationTool": "tia_create_scratch"}), encoding="utf-8")
        if not args.seeded_source_dir:
            native("seed")
        native("add-alternate-namespace")
        source_dir = args.seeded_source_dir.resolve() if args.seeded_source_dir else run_dir
        assert source_dir.is_relative_to(ROOT / "artifacts"), "Native fixture sources must remain in repository artifacts"
        inventory = call("software_inspect", {"maxItems": 2000})
        duplicates = [block for block in inventory["blocks"] if block["name"] == "TiaMcp_IsEnabled"]
        assert len(duplicates) == 4, duplicates
        root = next(block for block in duplicates if "/units/" not in block["path"])
        a = next(block for block in duplicates if "/namespaces/TiaMcp_UnitA/" in block["path"])
        alternate = next(block for block in duplicates if "/namespaces/TiaMcp_UnitA.Alternate/" in block["path"])
        b = next(block for block in duplicates if "/units/TiaMcp_UnitB/" in block["path"])
        if args.unit_routes_only:
            report["unitRoutes"] = {}
            qualify_unit_routes(call, apply, rejected, export_xml, {
                "root": {"path": root["scopePath"], "namespace": ""},
                "A": {"path": a["scopePath"], "namespace": "TiaMcp_UnitA"},
                "B": {"path": b["scopePath"], "namespace": "TiaMcp_UnitB"}}, report["unitRoutes"])
            report["historicalRoutingSequence"] = "not repeated; see prior reports"
            report["completed"] = True
            return
        root_hash, b_hash = semantic_xml(read(root["path"])["xml"]), semantic_xml(read(b["path"])["xml"])
        alternate_hash = semantic_xml(read(alternate["path"])["xml"])
        rejected("object_resolve", {"selectors": {"kind": "block", "name": "TiaMcp_IsEnabled"}}, "AmbiguousTarget")
        rejected("block_read", {"blockName": "TiaMcp_IsEnabled"}, "AmbiguousTarget")
        rejected("change_prepare", {"kind": "block_rename", "payload": {"blockName": "TiaMcp_IsEnabled", "newName": "UnqualifiedRename"}}, "AmbiguousTarget")
        resolved = call("object_resolve", {"selectors": {"objectPath": a["path"]}})
        assert resolved["identity"]["scopePath"] == a["scopePath"] and resolved["identity"]["objectPath"] == a["path"], resolved
        rejected("block_read", {"blockName": "TiaMcp_IsEnabled", "scopePath": a["scopePath"]}, "AmbiguousTarget")
        scoped = call("object_resolve", {"selectors": {"kind": "block", "name": "TiaMcp_IsEnabled", "scopePath": a["scopePath"], "namespace": "TiaMcp_UnitA"}}, "namespace-qualified unit block resolve")
        assert scoped["identity"]["objectPath"] == a["path"], scoped
        rejected("object_resolve", {"selectors": {"objectPath": a["path"], "projectPath": str(ROOT / "artifacts/WrongCopy.ap21")}}, "ProjectMismatch")
        rejected("block_read", {"blockPath": a["path"], "scopePath": b["scopePath"]}, "BlockNotFound")
        source = (source_dir / "TiaMcp_UnitA-native.scl").read_text(encoding="utf-8-sig").replace("TiaMcp_Seed_TiaMcp_UnitA", "TiaMcp_IsEnabled").replace(":= #Enable", ":= NOT #Enable")
        rejected("change_prepare", {"kind": "source_upsert", "payload": {"blockPath": a["path"], "language": "SCL", "source": source.replace("NAMESPACE TiaMcp_UnitA", "NAMESPACE TiaMcp_UnitB"), "expectedBlockNames": ["TiaMcp_IsEnabled"]}}, "NamespaceMismatch")
        apply("source_upsert", {"scopePath": a["scopePath"], "language": "SCL", "source": source, "expectedBlockNames": ["TiaMcp_IsEnabled"]})
        changed = read(a["path"])
        wrong_namespace_xml = ET.fromstring(changed["xml"])
        wrong_namespace_xml.find("SW.Blocks.FC/AttributeList/Namespace").text = "TiaMcp_UnitB"
        rejected("change_prepare", {"kind": "block_import_xml", "payload": {"blockPath": a["path"], "xml": ET.tostring(wrong_namespace_xml, encoding="unicode"), "expectedBlockNames": ["TiaMcp_IsEnabled"]}}, "NamespaceMismatch")
        apply("block_import_xml", {"blockPath": a["path"], "xml": changed["xml"], "expectedBlockNames": ["TiaMcp_IsEnabled"]})
        assert semantic_xml(read(a["path"])["xml"]) == semantic_xml(changed["xml"]), "Unit XML round trip changed native payload"
        group_source = source.replace("TiaMcp_IsEnabled", "TiaMcp_GroupFunction")
        group_path = a["scopePath"] + "/blocks/groups/Helpers"
        apply("source_upsert", {"scopePath": a["scopePath"], "groupPath": group_path, "language": "SCL", "source": group_source, "expectedBlockNames": ["TiaMcp_GroupFunction"]})
        group_block = read(group_path + "/namespaces/TiaMcp_UnitA/TiaMcp_GroupFunction")
        assert group_block["block"]["path"] == group_path + "/namespaces/TiaMcp_UnitA/TiaMcp_GroupFunction"
        apply("block_delete", {"blockPath": group_block["block"]["path"]})
        rejected("block_read", {"blockPath": group_block["block"]["path"]}, "BlockNotFound")
        type_source = 'NAMESPACE TiaMcp_UnitA\nTYPE "TiaMcp_UnitData"\nVERSION : 0.1\n STRUCT\n Enabled : Bool;\n CountValue : Int;\n END_STRUCT;\nEND_TYPE\nEND_NAMESPACE\n'
        type_group = a["scopePath"] + "/types/groups/Data"
        apply("type_source_upsert", {"scopePath": a["scopePath"], "groupPath": type_group, "source": type_source, "expectedTypeNames": ["TiaMcp_UnitData"]})
        type_path = type_group + "/namespaces/TiaMcp_UnitA/TiaMcp_UnitData"
        unit_type = call("type_read", {"typePath": type_path})
        type_xml = pathlib.Path(unit_type["artifact"]["path"]).read_text(encoding="utf-8-sig")
        apply("plc_type_import_xml", {"typePath": type_path, "xml": type_xml, "expectedTypeNames": ["TiaMcp_UnitData"]})
        apply("block_rename", {"blockPath": a["path"], "newName": "TiaMcp_IsEnabled_AChanged"})
        a_path = a["path"].rsplit("/", 1)[0] + "/TiaMcp_IsEnabled_AChanged"
        read(a_path)
        assert semantic_xml(read(root["path"])["xml"]) == root_hash, "Root block changed during unit edits"
        assert semantic_xml(read(b["path"])["xml"]) == b_hash, "Unit B block changed during unit A edits"
        assert semantic_xml(read(alternate["path"])["xml"]) == alternate_hash, "Same-unit alternate namespace block changed"
        record("unrelated root, unit B and same-unit alternate namespace payloads preserved", {"rootSha256": root_hash, "unitBSha256": b_hash, "alternateNamespaceSha256": alternate_hash})
        report["unitRoutes"] = {}
        qualify_unit_routes(call, apply, rejected, export_xml, {
            "root": {"path": root["scopePath"], "namespace": ""},
            "A": {"path": a["scopePath"], "namespace": "TiaMcp_UnitA"},
            "B": {"path": b["scopePath"], "namespace": "TiaMcp_UnitB"}}, report["unitRoutes"])
        save()
        pending = prepare("block_rename", {"blockPath": a_path, "newName": "MustNotBeApplied"})
        native("mutate-other-unit")
        rejected("change_apply", {"changeId": pending["changeId"], "previewHash": pending["previewHash"], "idempotencyKey": uuid.uuid4().hex,
                                  "checkpointSave": True, "compile": True, "save": True}, "StaleRevision")
        read(a_path)
        report["completed"] = True
    except Exception as exc:
        report["failure"] = str(exc)
        raise
    finally:
        try:
            call("tia_detach")
        finally:
            client.close()
            report["hostExitCode"] = client.process.returncode
            report["hostLogs"] = client.errors
            save()


if __name__ == "__main__":
    main()
