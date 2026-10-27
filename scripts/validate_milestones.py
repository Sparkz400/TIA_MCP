"""Integration validation on a NEW worker-owned TIA project and owned PLCSIM instance only.

Every change is prepared, applied with compile and save, read through change_status, and checked against an
independent readback of every block, PLC type and tag table export and every network node attribute: only the
change's own objects may differ. The saved changed project is then reopened in a fresh owned Portal and read back
again. A restored change is recovered from its own checkpoint first, in the applying Portal that change_restore is
bound to; then the saved changed project and the recovered project are each reopened in a fresh owned Portal.
"""
import argparse
import base64
import hashlib
import json
import pathlib
import re
import types
import uuid
import xml.etree.ElementTree as ET

from smoke_mcp import Client, ROOT
from validate_simulation import artifact_path, redact
from block_test_runtime import save_generated_scratch_project, verify_credential_provisioning, verify_memory_card
from qualify_cpu_memory_bytes import reopen as reopen_with_lock_retry


def export_digest(xml):
    """SHA-256 of a native XML export without its DocumentInfo, which carries the export time."""
    return hashlib.sha256(re.sub(r"<DocumentInfo>.*?</DocumentInfo>", "", xml, flags=re.S).encode("utf-8")).hexdigest()


def changed_keys(before, after):
    return sorted(key for key in before.keys() | after.keys() if key not in before or key not in after or before[key] != after[key])


def require_effects(before, after, expected, optional=()):
    """A change must alter every expected object, may alter the optional dependents, and nothing else."""
    changed = set(changed_keys(before, after))
    missing, unrelated = set(expected) - changed, changed - set(expected) - set(optional)
    if missing or unrelated:
        raise AssertionError(f"Expected changes missing: {sorted(missing)}; unrelated changes: {sorted(unrelated)}")
    return sorted(changed)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/milestones.json")
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe",
                        help="Host executable to run, for example a frozen build.")
    parser.add_argument("--skip-simulation", action="store_true")
    parser.add_argument("--cpu-type-identifier", help="Exact identifier validated by the installed TIA catalog before scratch CPU creation.")
    parser.add_argument("--project", type=pathlib.Path, help="Reopen an existing isolated TiaMcpScratch project under this repo's artifacts.")
    parser.add_argument("--credential-metadata", type=pathlib.Path, help="Generated scratch CPU credential metadata, required for simulation when reopening a saved project.")
    parser.add_argument("--credential-journal", type=pathlib.Path, action="append", default=[], help="Repeat for each prior recovery journal when reopening a recovered project.")
    parser.add_argument("--worker", type=pathlib.Path, help="Explicit engineering worker executable.")
    parser.add_argument("--sim-worker", type=pathlib.Path, help="Explicit simulator worker executable.")
    parser.add_argument("--trust-owned-empty-cpu-certificate", action="store_true",
                        help="Authorize first-use TLS trust for each fresh owned empty CPU during credential provisioning.")
    parser.add_argument("--artifacts", type=pathlib.Path, help="Short artifact directory (for example D:\\wp\\m); keeps scratch and recovery paths within the native limit.")
    args = parser.parse_args()
    extra_arguments = [part for option, path in (("--worker", args.worker), ("--sim-worker", args.sim_worker), ("--artifacts", args.artifacts)) if path
                       for part in (option, str(path.resolve()))]
    client = Client(args.host.resolve(), 300, extra_arguments)
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

    def record(name, result):
        report["steps"].append({"name": name, "result": result})
        persist()
        print("PASS " + name, flush=True)
        return result

    def snapshot(label):
        """Independent readback: every block, PLC type and tag table export and every network node attribute."""
        software = client.tool("software_inspect", {"maxItems": 2000})
        assert not software["completeness"].get("truncated"), software["completeness"]
        state = {}
        for block in software["blocks"]:
            state["block " + block["name"]] = export_digest(client.tool("block_read", {"blockPath": block["path"]})["xml"])
        for item in software["types"]:
            sections = client.tool("type_read", {"typePath": item["path"]})["sections"]
            state["type " + item["name"]] = hashlib.sha256(json.dumps(sections, sort_keys=True).encode("utf-8")).hexdigest()
        for table in software["tagTables"]:
            state["tag table " + table["name"]] = export_digest(client.tool("tag_table_read", {"tagTablePath": table["path"]})["xml"])
        for item in client.tool("engineering_read", {"selectors": {"scope": "network"}})["items"]:
            for node in item.get("nodes") or []:
                for name, value in node["attributes"].items():
                    state[f"network {item['path']} {node['name']} {name}"] = value
        return record("snapshot " + label, state)

    def same(label, reference):
        state = snapshot(label)
        if state != reference:
            raise AssertionError(label + " differs from its reference in " + str(changed_keys(reference, state)))

    def reopen(path, label):
        call("tia_detach", label="close before reopen " + label)
        started = call("tia_start", label="fresh owned TIA for " + label)
        assert started.get("ownsPortalProcess") is True and not started.get("projects"), started
        opened = reopen_with_lock_retry(types.SimpleNamespace(client=client), path)
        report["currentProject"] = path
        record("reopen " + label, {"path": path, **opened})

    def apply(kind, payload, label, expected, optional=()):
        """Prepare, apply, read change_status and require that only the change's own objects differ."""
        before = snapshot("before " + label)
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
        name = f"{label} ({kind})"
        status = call("change_status", {"changeId": prepared["changeId"]}, "status " + name)
        assert status["state"] == "applied" and status["stages"] == applied["stages"], status
        assert status["afterFingerprint"] == applied["afterFingerprint"] and status["recoveryArchiveSha256"], status
        after = snapshot("after " + label)
        record("preserved unrelated objects " + name, require_effects(before, after, expected, optional))
        return {"name": name, "prepared": prepared, "applied": applied, "arguments": arguments, "before": before, "after": after}

    def settle(change, restore=False):
        """Reopen the saved change in a fresh owned Portal. change_restore is bound to the applying Portal, so a
        restore runs first; the recovered project is then reopened too and the run continues there."""
        name, prepared = change["name"], change["prepared"]
        changed_project = report["currentProject"]
        if restore:
            restored = call("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}, "restore " + name)
            assert restored["state"] == "restored" and restored["recoveredProject"], restored
            same("restored " + name, change["before"])
        reopen(changed_project, "changed " + name)
        status = call("change_status", {"changeId": prepared["changeId"]}, "status after reopen " + name)
        assert (status.get("state") or status["journal"]["state"]) == ("restored" if restore else "applied"), status
        same("reopened changed " + name, change["after"])
        if restore:
            reopen(restored["recoveredProject"]["path"], "recovered " + name)
            same("reopened recovered " + name, change["before"])

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
        udt = apply("type_source_upsert", {"source": type_source, "expectedTypeNames": ["TiaMcp_Status"]}, "UDT fixture", {"type TiaMcp_Status"})
        type_read = call("type_read", {"typeName": "TiaMcp_Status"})
        assert "CountValue" in json.dumps(type_read) and "Enabled" in json.dumps(type_read)
        settle(udt, restore=True)  # No later step uses the UDT.
        for language in ("SCL", "STL", "LAD", "FBD"):
            try:
                block = "TiaMcp_Fixture_" + language
                fixture = call("fixture_sources", {"language": language})
                change = apply(fixture["kind"], fixture["payload"], language + " fixture", {"block " + block})
                replay = call("change_apply", change["arguments"], language + " idempotent replay")
                assert replay["afterFingerprint"] == change["applied"]["afterFingerprint"]
                exported = call("block_read", {"blockName": block})
                assert exported["block"]["programmingLanguage"] == language, exported["block"]
                artifact = call("artifact_read", {"path": exported["artifact"]["path"]})
                if artifact["complete"]:
                    assert hashlib.sha256(base64.b64decode(artifact["data"])).hexdigest() == artifact["sha256"]
                # STL (source_upsert) and LAD (block_import_xml) also recover from their own checkpoint. Nothing later uses
                # the STL block; LAD is imported again for its operand edit. SCL stays for the delete below.
                settle(change, restore=language in ("STL", "LAD"))
                if language == "LAD":
                    settle(apply(fixture["kind"], fixture["payload"], "LAD fixture reimport", {"block " + block}))
                if language in ("LAD", "FBD"):
                    document = ET.fromstring(call("block_read", {"blockName": block})["xml"])
                    location = next((unit.attrib["ID"], access.attrib["UId"])
                        for unit in document.iter("SW.Blocks.CompileUnit")
                        for access in unit.iter() if access.tag.rsplit("}", 1)[-1] == "Access"
                        if any(child.tag.rsplit("}", 1)[-1] == "Component" and child.get("Name") == "Enable" for child in access.iter()))
                    edited = apply("graphical_operand_set", {"blockName": block, "networkId": location[0], "accessUid": location[1],
                        "components": ["EnableAlternate"]}, language + " structured operand edit", {"block " + block})
                    assert "native_preservation_verified" in edited["applied"]["stages"], edited["applied"]["stages"]
                    assert any(r["role"] == "structured-edit-readback" for r in edited["applied"]["readbacks"]), edited["applied"]["readbacks"]
                    changed_xml = call("block_read", {"blockName": block})
                    changed = ET.fromstring(changed_xml["xml"])
                    assert any(access.tag.rsplit("}", 1)[-1] == "Access" and
                        any(child.get("Name") == "EnableAlternate" for child in access.iter()) for access in changed.iter())
                    settle(edited, restore=True)
            except Exception as error:
                report["failures"].append({"stage": language + " fixture", "error": str(error)})
                persist()
                print("FAIL " + language + ": " + str(error)[:1500], flush=True)
        # Mutate, verify and restore a standard block using a supported archive operation. Its caller names the new block.
        renamed = apply("block_rename", {"blockName": "TiaMcp_IsEnabled", "newName": "TiaMcp_IsEnabled_Renamed"}, "block rename",
                        {"block TiaMcp_IsEnabled", "block TiaMcp_IsEnabled_Renamed"}, {"block TiaMcp_EnableGate"})
        call("block_read", {"blockName": "TiaMcp_IsEnabled_Renamed"})
        settle(renamed, restore=True)
        call("block_read", {"blockName": "TiaMcp_IsEnabled"}, "block restored from archive")
        # A persisted source without a call can be deleted without invalidating the PLC; a called block cannot.
        if not any(f["stage"] == "SCL fixture" for f in report["failures"]):
            expected_error("change_prepare", {"kind": "block_delete", "payload": {"blockName": "TiaMcp_IsEnabled"}}, "DependencyExists")
            deleted = apply("block_delete", {"blockName": "TiaMcp_Fixture_SCL"}, "unused fixture delete", {"block TiaMcp_Fixture_SCL"})
            expected_error("block_read", {"blockName": "TiaMcp_Fixture_SCL"}, "BlockNotFound")
            settle(deleted, restore=True)
        network = call("engineering_read", {"selectors": {"scope": "network"}})
        node = next((item for item in network["items"] if item.get("nodes")), None)
        if node:
            selected = node["nodes"][0]
            payload = {"hardwarePath": node["path"], "nodeName": selected["name"], "attributes": {"Address": "192.168.254.10", "SubnetMask": "255.255.255.0"}}
            settle(apply("network_configure", payload, "offline network address",
                         {f"network {node['path']} {selected['name']} Address"}), restore=True)
        if not args.skip_simulation:
            settle(apply("project_simulation_enable", {"enabled": True}, "compile support for simulation", ()))
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
            settle(apply("source_upsert", {"language": "SCL", "source": source, "expectedBlockNames": ["TiaMcp_TestIO", "Main"]},
                         "simulation stimulus program", {"block TiaMcp_TestIO", "block Main"}))
            call("sim_status")
            call("simulation_discovery_baseline")
            created = call("sim_create")
            sim_id = created["instance"]["id"]
            report["simulationInstance"] = created["instance"]
            try:
                if not credential_metadata:
                    raise ValueError("Supply --credential-metadata for simulation on a reopened project; no generated credential will be guessed.")
                download_arguments = {"downloadTicket": created["downloadTicket"], "credentialMetadataPath": credential_metadata,
                    "credentialLineageJournalPaths": lineage_journals}
                download = call("simulation_download", download_arguments)
                # The scratch CPU is password-protected: provision its runtime credential before power-on.
                report["memoryCard"] = verify_memory_card(download)
                report["generatedProjectSave"] = save_generated_scratch_project(call, download)
                provisioned = call("simulation_provision_scratch_credential",
                    dict(download_arguments, trustOwnedEmptyCpuCertificate=args.trust_owned_empty_cpu_certificate))
                report["credentialProvisioning"] = verify_credential_provisioning(provisioned, download,
                    trust_owned_empty_cpu_certificate=args.trust_owned_empty_cpu_certificate)
                report["restoredMemoryCard"] = verify_memory_card(download)
                call("sim_state", {"instanceId": sim_id, "action": "powerOn", "timeoutMs": 60000})
                call("sim_state", {"instanceId": sim_id, "action": "run", "timeoutMs": 30000})
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
                stopped = call("sim_state", {"instanceId": sim_id, "action": "stop", "timeoutMs": 30000})
                if stopped["instance"]["operatingState"] != "Stop": raise RuntimeError("sim_state stop did not reach STOP")
                off = call("sim_state", {"instanceId": sim_id, "action": "powerOff", "timeoutMs": 60000})
                if off["instance"]["operatingState"] != "Off": raise RuntimeError("sim_state powerOff did not reach OFF")
                # Independent observation through a fresh selection, not the state call's own echo.
                observed = call("sim_select", {"instanceId": sim_id, "expectedName": created["instance"]["name"]})
                if observed["instance"].get("operatingState") != "Off": raise RuntimeError("A fresh selection does not show the instance OFF")
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
