"""Validate a saved repository scratch program on a fresh owned PLCSIM instance."""
import argparse
import copy
import json
import pathlib

from smoke_mcp import Client, ROOT
from block_test_runtime import verify_credential_provisioning, verify_download, verify_memory_card, verify_owned_instance, save_generated_scratch_project


def artifact_path(value, suffix=None):
    path = pathlib.Path(value).resolve()
    if not path.is_relative_to((ROOT / "artifacts").resolve()):
        raise ValueError("Validation inputs and reports must remain inside this repository's artifacts directory.")
    if suffix and path.suffix.lower() != suffix:
        raise ValueError("Unexpected artifact extension: " + path.name)
    return path


def redact(value):
    if isinstance(value, dict):
        return {key: "<redacted>" if key.lower() in {"downloadticket", "password", "cleartext", "plaintext", "secret"} else redact(item) for key, item in value.items()}
    if isinstance(value, list):
        return [redact(item) for item in value]
    return value


class ToolFailure(RuntimeError):
    def __init__(self, tool, error):
        self.tool = tool
        self.error = redact(error)
        super().__init__(tool + ": " + error.get("code", "UnknownError") + ": " + error.get("message", ""))


def scenario(signals):
    by_member = {signal["symbol"].rsplit(".", 1)[-1].strip('"'): signal for signal in signals}
    def write(member, value):
        return {**by_member[member], "value": value}
    def assertion(member, expected):
        return {**by_member[member], "operator": "eq", "expected": expected}
    return [
        {"delayMs": 500, "writes": [write("Enable", True), write("AnalogIn", 7)], "assertions": [assertion("Active", True), assertion("AnalogOut", 14)]},
        {"delayMs": 500, "writes": [write("Enable", False), write("AnalogIn", -3)], "assertions": [assertion("Active", False), assertion("AnalogOut", -6)]},
        {"delayMs": 500, "writes": [write("Enable", True), write("AnalogIn", 0)], "assertions": [assertion("Active", True), assertion("AnalogOut", 0)]},
        {"delayMs": 500, "writes": [write("Enable", False), write("AnalogIn", 0)], "assertions": [assertion("Active", False), assertion("AnalogOut", 0)]},
    ]


def checked_values(observed, signals):
    values = observed["values"]
    expected = {(signal["symbol"], signal["type"]) for signal in signals}
    actual = {(value["symbol"], value["type"]) for value in values}
    assert len(values) == len(signals) and actual == expected, {"expectedSignals": signals, "actualValues": values}
    assert all(value["quality"] == "good" for value in values), values
    return {value["symbol"]: value["value"] for value in values}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=pathlib.Path, required=True)
    parser.add_argument("--credential-metadata", type=pathlib.Path, required=True)
    parser.add_argument("--credential-journal", type=pathlib.Path, action="append", default=[], help="Exact recovery journal; repeat for every hop from credential project to selected project.")
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/simulation-validation.json")
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=pathlib.Path, help="Explicit engineering worker build for an isolated host run.")
    parser.add_argument("--sim-worker", type=pathlib.Path, help="Explicit simulator worker build for an isolated host run.")
    parser.add_argument("--provision-runtime-credential", action="store_true", help="Explicitly checkpoint/compile/save generated scratch state, then provision this owned simulator before normal power-on.")
    parser.add_argument("--trust-owned-empty-cpu-certificate", action="store_true",
                        help="Authorize first-use TLS trust only for this owned empty CPU, pinned within this attempt; requires --provision-runtime-credential.")
    args = parser.parse_args(argv)
    if args.trust_owned_empty_cpu_certificate and not args.provision_runtime_credential:
        parser.error("--trust-owned-empty-cpu-certificate requires --provision-runtime-credential")
    project = artifact_path(args.project, ".ap21")
    metadata = artifact_path(args.credential_metadata, ".json")
    journals = [artifact_path(path, ".json") for path in args.credential_journal]
    report_path = artifact_path(args.report, ".json")
    if not project.name.startswith("TiaMcpScratch_") or not all(path.is_file() for path in [project, metadata, *journals]):
        raise ValueError("Only existing isolated scratch projects and explicit credential/journal artifacts may be used.")
    host_arguments = []
    for flag, path in (("--worker", args.worker), ("--sim-worker", args.sim_worker)):
        if path is not None:
            host_arguments.extend((flag, str(path.resolve())))
    report = {"project": str(project), "credentialMetadataPath": str(metadata), "credentialLineageJournalPaths": list(map(str, journals)),
              "provisionRuntimeCredential": args.provision_runtime_credential,
              "trustOwnedEmptyCpuCertificate": args.trust_owned_empty_cpu_certificate,
              "host": str(args.host.resolve()), "hostArguments": host_arguments,
              "steps": [], "cleanup": [], "completed": False, "simulationVerified": False}
    client = Client(args.host.resolve(), 300, host_arguments)
    instance_id = None
    attached = False

    def persist():
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(redact(report), ensure_ascii=False, indent=2), encoding="utf-8")

    def call(name, arguments=None, cleanup=False):
        raw = client.request("tools/call", {"name": name, "arguments": arguments or {}})
        result = raw.get("structuredContent") or json.loads(raw["content"][0]["text"])
        report["cleanup" if cleanup else "steps"].append({"name": name, "success": not bool(raw.get("isError")), "result": redact(copy.deepcopy(result))})
        persist()
        if raw.get("isError"):
            raise ToolFailure(name, result.get("error", {"code": "ToolError", "message": "No structured error supplied."}))
        print("PASS " + name, flush=True)
        return result

    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "tia-simulation-validation", "version": "0.2.0"}})
        client.notify("notifications/initialized")
        call("tia_start")
        attached = True
        opened = call("project_open", {"path": str(project)})
        call("sim_status")
        if args.provision_runtime_credential:
            report["discoveryBaseline"] = call("simulation_discovery_baseline")["baseline"]
        created = call("sim_create", {"cpuType": "CPU1511"})
        instance = verify_owned_instance(created["instance"])
        instance_id = instance["id"]
        if instance.get("operatingState") != "Off":
            raise RuntimeError("A fresh owned OFF simulator is required.")
        download_arguments = {"downloadTicket": created["downloadTicket"], "credentialMetadataPath": str(metadata),
                              "credentialLineageJournalPaths": list(map(str, journals))}
        download = call("simulation_download", download_arguments)
        verify_download(download, created["instance"])
        if args.provision_runtime_credential:
            report["memoryCard"] = verify_memory_card(download)
            persist()
            provision_arguments = dict(download_arguments)
            if args.trust_owned_empty_cpu_certificate:
                provision_arguments["trustOwnedEmptyCpuCertificate"] = True
            if download.get("project", {}).get("path") != str(project):
                raise RuntimeError("Generated-state save must target the exact opened scratch project.")
            report["generatedProjectSave"] = save_generated_scratch_project(call, download)
            provisioned = call("simulation_provision_scratch_credential", provision_arguments)
            report["credentialProvisioning"] = verify_credential_provisioning(provisioned, download,
                trust_owned_empty_cpu_certificate=args.trust_owned_empty_cpu_certificate)
            report["restoredMemoryCard"] = verify_memory_card(download)
            persist()
        call("sim_state", {"instanceId": instance_id, "action": "powerOn", "timeoutMs": 60000})
        running = call("sim_state", {"instanceId": instance_id, "action": "run", "timeoutMs": 30000})
        assert running["instance"]["operatingState"] == "Run", running["instance"]
        tags = call("sim_tags", {"instanceId": instance_id, "filter": "TiaMcp_TestIO", "maxItems": 2000})
        symbols = {item["symbol"] for item in tags["tags"]}
        signals = []
        for member, kind in [("Enable", "bool"), ("Active", "bool"), ("AnalogIn", "int16"), ("AnalogOut", "int16")]:
            wanted = "TiaMcp_TestIO." + member
            matching = [symbol for symbol in symbols if symbol.replace('"', '') == wanted]
            assert len(matching) == 1, {"missingOrAmbiguousSymbol": wanted, "availableSymbols": sorted(symbols)}
            signals.append({"symbol": matching[0], "type": kind})
        observed = call("sim_read", {"instanceId": instance_id, "signals": signals})
        checked_values(observed, signals)
        call("sim_write", {"instanceId": instance_id, "writes": [{**signals[0], "value": False}, {**signals[2], "value": 0}]})
        readback = call("sim_read", {"instanceId": instance_id, "signals": signals})
        values = checked_values(readback, signals)
        assert values[signals[0]["symbol"]] is False and values[signals[2]["symbol"]] == 0, values
        steps = scenario(signals)
        tested = call("sim_test", {"instanceId": instance_id, "signals": signals, "intervalMs": 100, "steps": steps})
        expected_assertions = sum(len(step["assertions"]) for step in steps)
        assert tested["passed"] and tested["completed"] and len(tested["assertions"]) == expected_assertions
        assert all(assertion["passed"] for assertion in tested["assertions"]), tested["assertions"]
        stopped = call("sim_state", {"instanceId": instance_id, "action": "stop", "timeoutMs": 30000})
        if stopped["instance"]["operatingState"] != "Stop": raise RuntimeError("sim_state stop did not reach STOP")
        off = call("sim_state", {"instanceId": instance_id, "action": "powerOff", "timeoutMs": 60000})
        if off["instance"]["operatingState"] != "Off": raise RuntimeError("sim_state powerOff did not reach OFF")
        # Independent observation through the registry, not the state call's own echo.
        row = [i for i in call("sim_status")["instances"] if i["id"] == instance_id]
        if len(row) != 1 or row[0].get("operatingState") != "Off": raise RuntimeError("sim_status does not show the instance OFF")
        report["simulationVerified"] = True
        report["simulationReportPath"] = tested["reportPath"]
        report["completed"] = True
    except ToolFailure as error:
        report["failure"] = {"stage": error.tool, "error": error.error}
        print("FAIL " + error.tool + ": " + error.error.get("code", "UnknownError"), flush=True)
    except Exception as error:
        report["failure"] = {"stage": "validation", "exceptionType": type(error).__name__, "message": str(error)}
        print("FAIL validation: " + str(error)[:1000], flush=True)
    finally:
        if instance_id is not None:
            try:
                released = call("sim_release", {"instanceId": instance_id, "deleteOwned": True}, cleanup=True)
                assert released["unregistered"] is True
            except Exception as error:
                report["cleanupFailure"] = {"stage": "sim_release", "message": str(error)}
                report["completed"] = False
        if attached:
            try:
                call("tia_detach", cleanup=True)
            except Exception as error:
                report["detachFailure"] = str(error)
                report["completed"] = False
        client.close()
        report["hostExitCode"] = client.process.returncode
        report["toolCalls"] = getattr(client, "tool_calls", [])
        persist()
        print(str(report_path), flush=True)
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
