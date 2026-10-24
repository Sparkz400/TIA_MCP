"""Separate runtime acceptance for one passed component installation.

Opens the saved installed consumer project in a fresh owned session, proves it
equals the installation's fresh-reopen snapshot, adds the OB1 call to the new FC
as its own reviewed change (only OB1 may change), downloads to a fresh owned
PLCSIM instance and drives the mapped tags. No retry, replay or recovery.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import traceback
import uuid

from block_test_runtime import (save_generated_scratch_project, utc_now, verify_credential_provisioning, verify_download,
                                verify_memory_card, verify_owned_instance)
from qualification_owned_scratch import assert_snapshot, require, snapshot
from run_block_tests import save_json
from smoke_mcp import Client
from run_conformance import safe_xml


def installed_inputs(report_path):
    """The installation report is read-only input; every acceptance flag is required."""
    path = Path(report_path).resolve()
    raw = path.read_bytes()
    report = json.loads(raw.decode("utf-8"))
    require(report.get("status") == "passed" and all(report.get(flag) is True for flag in (
        "preservationVerified", "freshReopenVerified", "firstCheckpointRecoveryVerified")),
        "Runtime acceptance requires a passed installation with preservation, reopen and recovery.")
    require(report.get("runtimeVerified") is False and report.get("productionApplySupported") is False,
            "Installation report claims runtime or production scope.")
    cpus = report["consumerScratch"]["scratchSecurity"]["cpus"]
    require(len(cpus) == 1 and cpus[0].get("credentialMetadataPath"), "Exactly one generated scratch credential is required.")
    reopened = json.loads((path.parent / "reopened-install" / "manifest.json").read_text(encoding="utf-8"))
    return {"report": report, "sha256": hashlib.sha256(raw).hexdigest(), "path": str(path),
            "project": report["consumerScratch"]["project"]["path"], "credential": cpus[0]["credentialMetadataPath"],
            "reopened": reopened}


OB_ORACLE_FAILURE = "OB1 must contain exactly one call to the installed FC."


def continued_inputs(previous_path, inputs):
    """Continue only from a retained post-OB snapshot: after the corrected OB-oracle failure before any
    simulator call, or after a verified OB call whose owned simulator stopped at the missing CPU password."""
    path = Path(previous_path).resolve()
    raw = path.read_bytes(); previous = json.loads(raw.decode("utf-8"))
    sim_calls = [call for call in previous.get("calls", []) if call["tool"].startswith("sim") or call["tool"] == "simulation_download"]
    oracle = previous.get("failure") == OB_ORACLE_FAILURE and previous.get("obCallVerified") is False and not sim_calls
    password = (previous.get("obCallVerified") is True and "SimulationConfigurationPasswordRequired" in (previous.get("failure") or "")
                and previous.get("cleanup", {}).get("release", {}).get("unregistered") is True)
    require(previous.get("kind") == "plc_component_runtime" and previous.get("status") == "failed" and (oracle or password)
            and previous.get("installedProjectVerified") is True and previous.get("runtimeVerified") is False
            and previous["installationReport"]["sha256"] == inputs["sha256"] and previous["obCall"]["state"] == "applied",
            "Continuation requires an accepted earlier runtime failure with its retained post-OB snapshot.")
    expected = json.loads((path.parent / "ob-call" / "manifest.json").read_text(encoding="utf-8"))
    return {"path": str(path), "sha256": hashlib.sha256(raw).hexdigest(), "expected": expected}


def ob_call_source(fixture):
    return "\n".join(['ORGANIZATION_BLOCK "Main"', "{ S7_Optimized_Access := 'TRUE' }", "VERSION : 0.1",
                      "BEGIN", '    "' + fixture["caller"] + '"();', "END_ORGANIZATION_BLOCK", ""])


def verify_ob_call(xml, fixture):
    document = safe_xml(xml)
    local = lambda node: node.tag.rsplit("}", 1)[-1]
    blocks = [node for node in document if node.tag.startswith("SW.Blocks.")]
    require(len(blocks) == 1 and blocks[0].tag == "SW.Blocks.OB" and blocks[0].findtext("AttributeList/Number") == "1",
            "OB call target is not exactly OB1.")
    calls = [node for node in document.iter() if local(node) == "CallInfo"]
    # SCL names the callee in Instance/Component; graphical CallInfo carries Name.
    names = [[c.get("Name") for c in call.iter() if local(c) == "Component"] or [call.get("Name")] for call in calls]
    require(len(calls) == 1 and names[0] == [fixture["caller"]] and calls[0].get("BlockType") == "FC", OB_ORACLE_FAILURE)


def scenario(fixture):
    enable, active = (next(s for s in fixture["signals"] if s["direction"] == d) for d in ("Input", "Output"))
    step = lambda value: {"delayMs": 500, "writes": [{"symbol": enable["name"], "type": "bool", "value": value}],
                          "assertions": [{"symbol": active["name"], "type": "bool", "operator": "eq", "expected": value}]}
    # FB: State.Active := Enable; Active := State.Active. Each edge is observed.
    return [step(True), step(False), step(True), step(False)], [{"symbol": s["name"], "type": "bool"} for s in (enable, active)]


class ComponentRuntime:
    def __init__(self, args):
        require(args.run_native, "Explicit --run-native is required.")
        self.args, self.inputs = args, installed_inputs(args.install_report)
        self.fixture = self.inputs["report"]["fixture"]
        self.output = args.output.resolve(); self.output.mkdir(parents=True, exist_ok=False)
        self.report = {"schemaVersion": 1, "kind": "plc_component_runtime", "status": "running", "startedAtUtc": utc_now(),
            "installationReport": {"path": self.inputs["path"], "sha256": self.inputs["sha256"]}, "project": self.inputs["project"],
            "calls": [], "installedProjectVerified": False, "obCallVerified": False, "runtimeVerified": False,
            "productionApplySupported": False, "automaticReplayAllowed": False}
        self.client = self.instance = None
        self.continued = continued_inputs(args.continue_from, self.inputs) if args.continue_from else None
        if self.continued: self.report["continuedFrom"] = {"path": self.continued["path"], "sha256": self.continued["sha256"]}
        self.persist()

    def persist(self):
        save_json(self.output / "result.json", self.report)

    def call(self, tool, arguments=None):
        row = {"tool": tool, "arguments": arguments or {}, "utc": utc_now()}
        self.report["calls"].append(row); self.persist()
        try:
            row["result"] = self.client.tool(tool, arguments or {}); row["status"] = "passed"
            return row["result"]
        except Exception as error:
            row.update(status="failed", error=str(error)); raise
        finally:
            self.persist()

    def apply(self, kind, payload):
        prepared = self.call("change_prepare", {"kind": kind, "payload": payload})
        result = self.call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
            "idempotencyKey": "component-runtime-" + uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
        require(result.get("state") == "applied" and result["compile"]["errorCount"] == 0, "Change did not apply and compile; no retry.")
        return result

    def run(self):
        try:
            self.client = Client(self.args.host, self.args.timeout, ["--worker", str(self.args.worker), "--sim-worker", str(self.args.sim_worker),
                                                                   "--artifacts", self.inputs["report"]["nativeArtifactRoot"]])
            self.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "component-runtime", "version": "1"}})
            self.client.notify("notifications/initialized")
            started = self.call("tia_start")
            require(started.get("ownsPortalProcess") is True and started.get("projects") == [], "A fresh owned empty Portal is required.")
            opened = self.call("project_open", {"path": self.inputs["project"]})
            require(opened.get("opened") is True and opened.get("project", {}).get("path") == self.inputs["project"], "Installed project did not open exactly.")
            if self.continued:
                # The saved project already holds the earlier run's reviewed OB call; prove exact identity.
                after = snapshot(self.call, self.output / "continued")
                assert_snapshot(self.continued["expected"], after)
            else:
                before = snapshot(self.call, self.output / "installed")
                assert_snapshot(self.inputs["reopened"], before)
                self.report["installedProjectVerified"] = True; self.persist()
                # PLCSIM download requires simulation-capable compilation; its own reviewed setting change.
                self.report["simulationEnable"] = self.apply("project_simulation_enable", {"enabled": True})
                before = snapshot(self.call, self.output / "simulation-enabled")
                self.report["simulationEnableKeptInstalledXml"] = all(row["payloadSha256"] == self.inputs["reopened"]["objects"][key]["payloadSha256"]
                    for key, row in before["objects"].items() if key in self.inputs["reopened"]["objects"])
                # Separate operation: the installation never called its FC from an OB.
                self.report["obCall"] = self.apply("source_upsert", {"language": "SCL", "source": ob_call_source(self.fixture), "expectedBlockNames": ["Main"]})
                after = snapshot(self.call, self.output / "ob-call")
            mains = [row for row in after["inventory"]["blocks"] if row["name"] == "Main"]
            require(len(mains) == 1, "Exact OB1 Main is ambiguous.")
            if not self.continued: assert_snapshot(before, after, except_paths=[mains[0]["path"]])
            verify_ob_call(Path(after["objects"]["block:" + mains[0]["path"]]["file"]).read_text(encoding="utf-8-sig"), self.fixture)
            self.report["obCallVerified"] = True; self.persist()
            # Same owned-instance, card and credential provisioning sequence as the frozen runtime corpus.
            self.report["discoveryBaseline"] = self.call("simulation_discovery_baseline", {})["baseline"]
            created = self.call("sim_create", {"cpuType": "CPU1511", "creationId": uuid.uuid4().hex})
            instance = verify_owned_instance(created["instance"])
            require(instance.get("operatingState") == "Off", "A fresh owned OFF instance is required.")
            self.instance = instance["id"]
            ticket = {"downloadTicket": created["downloadTicket"], "credentialMetadataPath": self.inputs["credential"]}
            download = self.call("simulation_download", ticket)
            verify_download(download, instance)
            self.report["memoryCard"] = verify_memory_card(download)
            self.report["generatedProjectSave"] = save_generated_scratch_project(self.call, download)
            provisioned = self.call("simulation_provision_scratch_credential", {**ticket, "trustOwnedEmptyCpuCertificate": True})
            self.report["credentialProvisioning"] = verify_credential_provisioning(provisioned, download, trust_owned_empty_cpu_certificate=True)
            self.report["restoredMemoryCard"] = verify_memory_card(download)
            self.call("sim_state", {"instanceId": self.instance, "action": "powerOn", "timeoutMs": 60000})
            running = self.call("sim_state", {"instanceId": self.instance, "action": "run", "timeoutMs": 30000})
            require(running.get("instance", {}).get("operatingState") == "Run", "Simulator did not enter RUN.")
            steps, signals = scenario(self.fixture)
            behavior = self.call("sim_test", {"instanceId": self.instance, "signals": signals, "intervalMs": 100, "steps": steps})
            require(behavior.get("passed") is True, "Component runtime scenario failed.")
            self.report["runtimeVerified"] = True
            self.report["status"] = "passed"
        except Exception as error:
            self.report.update(status="failed", failure=str(error), traceback=traceback.format_exc())
            raise
        finally:
            cleanup = self.report.setdefault("cleanup", {})
            if self.instance is not None:
                try: cleanup["release"] = self.call("sim_release", {"instanceId": self.instance, "deleteOwned": True})
                except Exception as error: cleanup["releaseError"] = str(error); self.report["status"] = "failed"
            if self.client is not None:
                try: cleanup["detach"] = self.call("tia_detach")
                except Exception as error: cleanup["detachError"] = str(error); self.report["status"] = "failed"
                self.client.close(); cleanup["hostExitCode"] = self.client.process.returncode
            self.report["completedAtUtc"] = utc_now(); self.persist()
            print("Report: " + str(self.output / "result.json"), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--sim-worker", type=Path, required=True)
    parser.add_argument("--install-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--continue-from", type=Path, help="Earlier runtime report that failed only at the corrected OB-call oracle.")
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    require(5 <= args.timeout <= 900, "Timeout must be 5..900 seconds.")
    ComponentRuntime(args).run()


if __name__ == "__main__":
    main()
