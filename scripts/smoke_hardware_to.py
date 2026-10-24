"""Validate hardware/TO engineering on a new worker-owned offline scratch project.

All changes use the real MCP preview/archive/apply workflow. No discovery attach,
physical connection, download or runtime write is performed.
"""
import argparse
import base64
import json
import pathlib
import uuid

from smoke_mcp import Client, ROOT
from qualify_hardware_families import FAMILIES, qualify_hardware_families
from qualify_technology_families import load_profiles, qualify_technology_families


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/hardware-to-smoke.json")
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=pathlib.Path, help="Use the worker built for the selected host configuration.")
    parser.add_argument("--skip-hardware", action="store_true")
    parser.add_argument("--skip-technology", action="store_true")
    parser.add_argument("--io-family", action="append", choices=tuple(FAMILIES), help="Repeat to select central I/O families; defaults to DI, DO, AI and AO.")
    parser.add_argument("--pid-version", default="3.0")
    parser.add_argument("--cpu-type-identifier", default="OrderNumber:6ES7 511-1AL03-0AB0/V4.1", help="Exact installed CPU type for a new owned scratch.")
    parser.add_argument("--technology-profile", type=pathlib.Path, action="append", default=[], help="Opt-in axis/encoder JSON fixture with explicit CPU, type, version and parameter dependencies.")
    parser.add_argument("--project", type=pathlib.Path, help="Reopen only an isolated TiaMcpScratch .ap21 under repository artifacts.")
    parser.add_argument("--artifacts", type=pathlib.Path, help="Short artifact directory; keeps scratch paths within the native limit.")
    args = parser.parse_args()
    technology_profiles = load_profiles(args.technology_profile)
    client = Client(args.host.resolve(), 300, [part for option, path in (("--worker", args.worker), ("--artifacts", args.artifacts)) if path
                                               for part in (option, str(path.resolve()))])
    report = {"steps": [], "failures": [], "limitations": [], "completed": False,
              "scope": "New worker-owned offline scratch project only; no physical PLC operations"}
    started = False

    def persist():
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    def call(name, arguments=None, label=None, allow_error=False):
        raw = client.request("tools/call", {"name": name, "arguments": arguments or {}})
        value = raw.get("structuredContent") or json.loads(raw["content"][0]["text"])
        failed = bool(raw.get("isError"))
        report["steps"].append({"name": label or name, "arguments": arguments or {}, "isError": failed, "result": value})
        persist()
        print(("ERROR " if failed else "PASS ") + (label or name), flush=True)
        if failed and not allow_error:
            raise RuntimeError(f"{label or name}: {json.dumps(value, ensure_ascii=False)}")
        return value

    def apply(kind, payload, label, prepared=None):
        prepared = prepared or call("change_prepare", {"kind": kind, "payload": payload}, "prepare " + label)
        try:
            outcome = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True}, "apply " + label)
            assert outcome["state"] == "applied" and outcome["compile"]["errorCount"] == 0, outcome
            return outcome
        except Exception as primary_error:
            recovery = {"label": label, "primaryFailure": str(primary_error), "status": "blocked"}
            report.setdefault("failureRecovery", []).append(recovery)
            try:
                status = call("change_status", {"changeId": prepared["changeId"]}, "status failed " + label)
                recovery["changeStatus"] = status
                if status.get("recoveryArchive"):
                    restored = call("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}, "restore failed " + label)
                    report["currentProject"] = restored["recoveredProject"]["path"]
                    recovery["result"] = restored
                    recovery["status"] = "passed"
            except Exception as recovery_error:
                recovery["error"] = str(recovery_error)
            persist()
            raise

    def xml_from_export(exported):
        # artifact_read verifies that the path belongs to this MCP artifact root.
        artifact = call("artifact_read", {"path": exported["artifact"]["path"]})
        assert artifact["complete"]
        return base64.b64decode(artifact["data"]).decode("utf-8-sig")

    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "tia-hardware-to-smoke", "version": "0.2.0"}})
        client.notify("notifications/initialized")
        start = call("tia_start")
        started = True
        report["ownedProcessId"] = start["processId"]
        if args.project:
            project_path = args.project.resolve()
            assert project_path.is_relative_to((ROOT / "artifacts").resolve()) and project_path.name.startswith("TiaMcpScratch_") and project_path.suffix == ".ap21"
            opened = call("project_open", {"path": str(project_path)})
            report["currentProject"] = opened["project"]["path"]
        else:
            scratch = call("tia_create_scratch", {"cpuTypeIdentifier": args.cpu_type_identifier})
            assert scratch["completed"] and scratch["compile"]["errorCount"] == 0
            report["currentProject"] = scratch["project"]["path"]
        if not args.skip_hardware:
            capability = call("feature_catalog", {"featureId": "change.hardware_address_set"}, allow_error=True)
            if capability.get("error", {}).get("code") not in (None, "FeatureNotFound"):
                raise RuntimeError("Hardware feature discovery failed: " + str(capability))
            address_enabled = any(item.get("implementation", {}).get("status") in ("implemented", "partial") for item in capability.get("features", []))
            report["hardwareFamilies"] = []
            qualify_hardware_families(call, apply, args.io_family or list(FAMILIES), address_enabled, report["hardwareFamilies"])
            report["hardwareCompleted"] = all(item["status"] == "passed" for item in report["hardwareFamilies"])
            persist()

        if not args.skip_technology:
            technology = call("engineering_read", {"selectors": {"scope": "technology", "maxItems": 2000}})
            example = technology["creationMetadata"]["documentedExample"]
            existing = [obj for obj in technology["technologyObjects"] if obj["name"] == "TiaMcp_PID_Fixture"]
            if existing:
                assert len(existing) == 1 and existing[0]["typeIdentifier"] == example["typeIdentifier"] and existing[0]["version"] == args.pid_version
                report["technologyReusedFromPriorScratchRun"] = True
            else:
                apply("to_create", {"name": "TiaMcp_PID_Fixture", "typeIdentifier": example["typeIdentifier"], "version": args.pid_version}, "documented PID technology object")
            data = call("engineering_read", {"selectors": {"scope": "technology", "name": "TiaMcp_PID_Fixture", "maxItems": 2000}}, "read PID technology parameters")
            obj = data["technologyObjects"][0]
            report["observedTechnologyType"] = {"typeIdentifier": obj["typeIdentifier"], "version": obj["version"]}
            read_only = next((parameter for parameter in obj["parameters"] if parameter["name"] == "Retain.CtrlParams.Gain"), None)
            if read_only and read_only.get("writable") is False:
                rejected = call("change_prepare", {"kind": "to_parameters", "payload": {"name": obj["name"], "parameters": {read_only["name"]: 1.125}}}, "reject native read-only PID parameter before apply", allow_error=True)
                assert rejected.get("error", {}).get("code") == "ReadOnlyParameter", rejected
                report["readOnlyParameterRejectedDuringPrepare"] = True
            elif read_only:
                report["limitations"].append("Native metadata did not identify Retain.CtrlParams.Gain as read-only; a prior native write rejected it. No further write was attempted.")
            gain = next((parameter for parameter in obj["parameters"] if parameter["name"] == "Config.InputUpperLimit" and parameter["type"] in ("System.Single", "System.Double") and isinstance(parameter.get("value"), (int, float))), None)
            if gain:
                value = float(gain["value"]) + 0.125
                apply("to_parameters", {"name": obj["name"], "parameters": {gain["name"]: value}}, "offline PID input limit parameter")
                changed = call("engineering_read", {"selectors": {"scope": "technology", "name": obj["name"], "maxItems": 2000}}, "verify PID input limit parameter")
                actual = next(parameter for parameter in changed["technologyObjects"][0]["parameters"] if parameter["name"] == gain["name"])
                assert abs(actual["value"] - value) < 1e-6, actual
                report["technologyParameterChanged"] = True
            else:
                report["limitations"].append("PID object did not expose the documented writable Config.InputUpperLimit scalar in the native parameter collection; no guessed parameter was written.")
            call("technology_read", {"technologyName": obj["name"], "maxItems": 2000, "exportXml": True}, "native PID XML export")
            apply("to_delete", {"name": obj["name"]}, "PID technology object deletion")
            technology = call("engineering_read", {"selectors": {"scope": "technology", "maxItems": 2000}}, "verify PID deletion")
            assert not any(item["name"] == obj["name"] for item in technology["technologyObjects"])
            report["technologyCompleted"] = True
            # Fresh creation selects this exact CPU identifier. Existing scratch
            # projects require their native inventory to establish the same ID.
            cpu_type = args.cpu_type_identifier if not args.project else None
            if args.project:
                inventory = call("project_inventory", {"maxItems": 2000})
                observed = {item.get("typeIdentifier") for item in inventory["hardware"]}
                if args.cpu_type_identifier in observed:
                    cpu_type = args.cpu_type_identifier
            report["technologyFamilies"] = []
            qualify_technology_families(call, apply, technology_profiles, cpu_type, report["technologyFamilies"])
            persist()

        source = 'TYPE "TiaMcp_XmlTypeFixture"\nVERSION : 0.1\nSTRUCT\n    Enabled : Bool;\n    CountValue : DInt;\nEND_STRUCT;\nEND_TYPE\n'
        apply("type_source_upsert", {"source": source, "expectedTypeNames": ["TiaMcp_XmlTypeFixture"]}, "UDT for XML roundtrip")
        exported = call("type_read", {"typeName": "TiaMcp_XmlTypeFixture"})
        apply("plc_type_import_xml", {"xml": xml_from_export(exported), "expectedTypeNames": ["TiaMcp_XmlTypeFixture"]}, "UDT XML roundtrip")
        call("type_read", {"typeName": "TiaMcp_XmlTypeFixture"}, "verify UDT XML roundtrip")
        report["typeRoundtripCompleted"] = True
        software = call("software_inspect", {"maxItems": 2000})
        tables = software.get("tagTables", [])
        if tables:
            table_name = tables[0]["name"]
            exported = call("tag_table_read", {"tagTableName": table_name})
            apply("tag_table_import_xml", {"xml": xml_from_export(exported), "expectedTableNames": [table_name]}, "native tag table XML roundtrip")
            call("tag_table_read", {"tagTableName": table_name}, "verify native tag table XML roundtrip")
            report["tagTableRoundtripCompleted"] = True
        else:
            report["limitations"].append("No tag table was exposed in the scratch PLC; tag-table XML roundtrip was not run.")
        report["completed"] = True
    except Exception as error:
        report["failures"].append(str(error))
        print("FAIL " + str(error)[:3000], flush=True)
        raise
    finally:
        if started:
            try:
                call("tia_detach")
            except Exception as error:
                report["detachFailure"] = str(error)
        client.close()
        report["hostExitCode"] = client.process.returncode
        report["toolCalls"] = client.tool_calls
        report["hostLogs"] = client.errors
        persist()
        print("Report: " + str(args.report), flush=True)


if __name__ == "__main__":
    main()
