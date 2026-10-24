"""Integration validation on a NEW worker-owned TIA project and owned PLCSIM instance only."""
import argparse
import base64
import hashlib
import json
import pathlib
import uuid
import xml.etree.ElementTree as ET

from smoke_mcp import Client, ROOT
from validate_simulation import artifact_path, redact


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/milestones.json")
    parser.add_argument("--skip-simulation", action="store_true")
    parser.add_argument("--cpu-type-identifier", help="Exact identifier validated by the installed TIA catalog before scratch CPU creation.")
    parser.add_argument("--project", type=pathlib.Path, help="Reopen an existing isolated TiaMcpScratch project under this repo's artifacts.")
    parser.add_argument("--credential-metadata", type=pathlib.Path, help="Generated scratch CPU credential metadata, required for simulation when reopening a saved project.")
    parser.add_argument("--credential-journal", type=pathlib.Path, action="append", default=[], help="Repeat for each prior recovery journal when reopening a recovered project.")
    args = parser.parse_args()
    client = Client(ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe", 300)
    report = {"steps": [], "failures": [], "limitations": [], "completed": False, "simulationVerified": False}
    sim_id = None
    credential_metadata = str(artifact_path(args.credential_metadata, ".json")) if args.credential_metadata else None
    lineage_journals = [str(artifact_path(path, ".json")) for path in args.credential_journal]

    def persist():
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    def call(name, arguments=None, label=None, allow_codes=()):
        raw = client.request("tools/call", {"name": name, "arguments": arguments or {}})
        result = raw.get("structuredContent") or json.loads(raw["content"][0]["text"])
        if raw.get("isError"):
            if result.get("error", {}).get("code") in allow_codes:
                report["limitations"].append({"stage": label or name, "result": result})
                persist()
                print("LIMIT " + (label or name) + ": " + result["error"]["code"], flush=True)
                return result
            raise RuntimeError(f"{name}: {result}")
        report["steps"].append({"name": label or name, "result": redact(result)})
        if name in ("tia_create_scratch", "project_open") and result.get("project"):
            report["currentProject"] = result["project"]["path"]
        if name == "change_restore" and result.get("recoveredProject"):
            report["currentProject"] = result["recoveredProject"]["path"]
            if result["journalPath"] not in lineage_journals:
                lineage_journals.append(result["journalPath"])
        persist()
        print("PASS " + (label or name), flush=True)
        return result

    def expected_error(name, arguments, code):
        result = client.request("tools/call", {"name": name, "arguments": arguments})
        assert result.get("isError"), result
        structured = result.get("structuredContent") or json.loads(result["content"][0]["text"])
        assert structured["error"]["code"] == code, structured
        report["steps"].append({"name": "rejected " + code, "result": structured})
        persist()
        print("PASS rejected " + code, flush=True)

    def apply(kind, payload, label):
        prepared = call("change_prepare", {"kind": kind, "payload": payload}, "prepare " + label)
        arguments = {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                     "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True}
        try:
            applied = call("change_apply", arguments, "apply " + label)
        except Exception:
            status = call("change_status", {"changeId": prepared["changeId"]})
            if status.get("recoveryArchive"):
                call("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}, "restore failed " + label)
            raise
        assert applied["state"] == "applied", applied
        assert applied["compile"]["errorCount"] == 0, applied
        return prepared, applied, arguments

    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "tia-milestones", "version": "0.2.0"}})
        client.notify("notifications/initialized")
        tools = client.request("tools/list")
        report["toolNames"] = [tool["name"] for tool in tools["tools"]]
        call("tia_start")
        if args.project:
            path = args.project.resolve()
            assert path.is_relative_to((ROOT / "artifacts").resolve()) and path.name.startswith("TiaMcpScratch_"), "Only isolated repository scratch projects may be used."
            call("project_open", {"path": str(path)})
        else:
            cpu_type = args.cpu_type_identifier or ("OrderNumber:6ES7 511-1AL03-0AB0/V4.0" if not args.skip_simulation else None)
            scratch = call("tia_create_scratch", {"cpuTypeIdentifier": cpu_type} if cpu_type else {})
            assert scratch["completed"] and scratch["compile"]["errorCount"] == 0
            report["scratchProject"] = scratch["project"]["path"]
            credential_metadata = scratch["scratchSecurity"]["cpus"][0].get("credentialMetadataPath")
        inventory = call("project_inventory", {"maxItems": 2000})
        call("software_inspect", {"maxItems": 2000})
        call("hardware_inspect", {"maxItems": 2000})
        call("technology_read")
        call("safety_inspect", allow_codes=("UnsupportedCapability",))
        interface = call("block_interface", {"blockName": "TiaMcp_EnableGate"})
        assert "Enable" in json.dumps(interface) and "Active" in json.dumps(interface)
        graph = call("call_graph", {"maxItems": 2000})
        assert any("TiaMcp_IsEnabled" in edge["calleePath"] for edge in graph["edges"]), graph
        call("symbol_trace", {"symbol": "TiaMcp_IsEnabled", "maxItems": 2000})
        impact = call("change_impact", {"symbol": "TiaMcp_IsEnabled", "maxItems": 2000})
        assert "TiaMcp_EnableGate" in json.dumps(impact), impact
        type_source = 'TYPE "TiaMcp_Status"\nVERSION : 0.1\nSTRUCT\n    Enabled : Bool;\n    CountValue : DInt;\nEND_STRUCT;\nEND_TYPE\n'
        apply("type_source_upsert", {"source": type_source, "expectedTypeNames": ["TiaMcp_Status"]}, "UDT fixture")
        type_read = call("type_read", {"typeName": "TiaMcp_Status"})
        assert "CountValue" in json.dumps(type_read) and "Enabled" in json.dumps(type_read)
        for language in ("SCL", "STL", "LAD", "FBD"):
            try:
                fixture = call("fixture_sources", {"language": language})
                prepared, applied, arguments = apply(fixture["kind"], fixture["payload"], language + " fixture")
                replay = call("change_apply", arguments, language + " idempotent replay")
                assert replay["afterFingerprint"] == applied["afterFingerprint"]
                exported = call("block_read", {"blockName": "TiaMcp_Fixture_" + language})
                assert exported["block"]["programmingLanguage"] == language, exported["block"]
                artifact = call("artifact_read", {"path": exported["artifact"]["path"]})
                if artifact["complete"]:
                    assert hashlib.sha256(base64.b64decode(artifact["data"])).hexdigest() == artifact["sha256"]
                if language in ("LAD", "FBD"):
                    document = ET.fromstring(exported["xml"])
                    location = next((unit.attrib["ID"], access.attrib["UId"])
                        for unit in document.iter("SW.Blocks.CompileUnit")
                        for access in unit.iter() if access.tag.rsplit("}", 1)[-1] == "Access"
                        if any(child.tag.rsplit("}", 1)[-1] == "Component" and child.get("Name") == "Enable" for child in access.iter()))
                    edited, _, _ = apply("graphical_operand_set", {"blockName": "TiaMcp_Fixture_" + language,
                        "networkId": location[0], "accessUid": location[1], "components": ["EnableAlternate"]}, language + " structured operand edit")
                    changed_xml = call("block_read", {"blockName": "TiaMcp_Fixture_" + language})
                    changed = ET.fromstring(changed_xml["xml"])
                    assert any(access.tag.rsplit("}", 1)[-1] == "Access" and
                        any(child.get("Name") == "EnableAlternate" for child in access.iter()) for access in changed.iter())
                    call("change_restore", {"changeId": edited["changeId"], "previewHash": edited["previewHash"]}, language + " operand edit restore")
            except Exception as error:
                report["failures"].append({"stage": language + " fixture", "error": str(error)})
                persist()
                print("FAIL " + language + ": " + str(error)[:1500], flush=True)
        # Mutate, verify and restore a standard block using a supported archive operation.
        prepared, applied, arguments = apply("block_rename", {"blockName": "TiaMcp_IsEnabled", "newName": "TiaMcp_IsEnabled_Renamed"}, "block rename")
        call("block_read", {"blockName": "TiaMcp_IsEnabled_Renamed"})
        restored = call("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        assert restored["state"] == "restored"
        call("block_read", {"blockName": "TiaMcp_IsEnabled"}, "block restored from archive")
        # A persisted source without a call can be deleted without invalidating the PLC.
        if not any(f["stage"] == "SCL fixture" for f in report["failures"]):
            apply("block_delete", {"blockName": "TiaMcp_Fixture_SCL"}, "unused fixture delete")
            expected_error("block_read", {"blockName": "TiaMcp_Fixture_SCL"}, "BlockNotFound")
        network = call("engineering_read", {"selectors": {"scope": "network"}})
        node = next((item for item in network["items"] if item.get("nodes")), None)
        if node:
            selected = node["nodes"][0]
            payload = {"hardwarePath": node["path"], "nodeName": selected["name"], "attributes": {"Address": "192.168.254.10", "SubnetMask": "255.255.255.0"}}
            apply("network_configure", payload, "offline network address")
        if not args.skip_simulation:
            apply("project_simulation_enable", {"enabled": True}, "compile support for simulation")
            source = '''DATA_BLOCK "TiaMcp_TestIO"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
VAR
    Enable : Bool;
    Active : Bool;
    AnalogIn : Int;
    AnalogOut : Int;
END_VAR
BEGIN
END_DATA_BLOCK

ORGANIZATION_BLOCK "Main"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
BEGIN
    "TiaMcp_EnableGate_DB"(Enable := "TiaMcp_TestIO".Enable, Active => "TiaMcp_TestIO".Active);
    "TiaMcp_TestIO".AnalogOut := "TiaMcp_TestIO".AnalogIn * 2;
END_ORGANIZATION_BLOCK
'''
            apply("source_upsert", {"language": "SCL", "source": source, "expectedBlockNames": ["TiaMcp_TestIO", "Main"]}, "simulation stimulus program")
            call("sim_status")
            created = call("sim_create")
            sim_id = created["instance"]["id"]
            report["simulationInstance"] = created["instance"]
            try:
                if not credential_metadata:
                    raise ValueError("Supply --credential-metadata for simulation on a reopened project; no generated credential will be guessed.")
                call("simulation_download", {"downloadTicket": created["downloadTicket"], "credentialMetadataPath": credential_metadata,
                    "credentialLineageJournalPaths": lineage_journals})
                call("sim_state", {"instanceId": sim_id, "action": "powerOn"})
                call("sim_state", {"instanceId": sim_id, "action": "run"})
                tags = call("sim_tags", {"instanceId": sim_id, "maxItems": 2000})
                report["simulationSymbols"] = tags
                signals = [{"symbol": "TiaMcp_TestIO.Active", "type": "bool"}, {"symbol": "TiaMcp_TestIO.AnalogOut", "type": "int16"}]
                call("sim_read", {"instanceId": sim_id, "signals": signals})
                simulation = call("sim_test", {"instanceId": sim_id, "signals": signals, "intervalMs": 100, "steps": [
                    {"delayMs": 500, "writes": [{"symbol": "TiaMcp_TestIO.Enable", "type": "bool", "value": True}, {"symbol": "TiaMcp_TestIO.AnalogIn", "type": "int16", "value": 7}],
                     "assertions": [{"symbol": "TiaMcp_TestIO.Active", "type": "bool", "operator": "eq", "expected": True}, {"symbol": "TiaMcp_TestIO.AnalogOut", "type": "int16", "operator": "eq", "expected": 14}]},
                    {"delayMs": 500, "writes": [{"symbol": "TiaMcp_TestIO.Enable", "type": "bool", "value": False}, {"symbol": "TiaMcp_TestIO.AnalogIn", "type": "int16", "value": -3}],
                     "assertions": [{"symbol": "TiaMcp_TestIO.Active", "type": "bool", "operator": "eq", "expected": False}, {"symbol": "TiaMcp_TestIO.AnalogOut", "type": "int16", "operator": "eq", "expected": -6}]}
                ]})
                report["simulationBehavior"] = simulation
                report["simulationVerified"] = True
            except Exception as error:
                report["limitations"].append({"stage": "simulation", "error": str(error)})
                print("LIMIT simulation: " + str(error)[:1500], flush=True)
            finally:
                call("sim_release", {"instanceId": sim_id, "deleteOwned": True})
                sim_id = None
        report["engineeringCompleted"] = not report["failures"]
        report["completed"] = report["engineeringCompleted"] and (args.skip_simulation or report["simulationVerified"])
    except Exception as error:
        report["failures"].append({"stage": "integration", "error": str(error)})
        print("FAIL integration: " + str(error)[:3000], flush=True)
    finally:
        if sim_id is not None:
            try: call("sim_release", {"instanceId": sim_id, "deleteOwned": True})
            except Exception as error: report["limitations"].append({"stage": "sim cleanup", "error": str(error)})
        try: call("tia_detach")
        except Exception as error: report["limitations"].append({"stage": "detach", "error": str(error)})
        client.close()
        report["hostExitCode"] = client.process.returncode
        report["stderrTail"] = client.errors[-20:]
        report["toolCalls"] = client.tool_calls
        persist()
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
