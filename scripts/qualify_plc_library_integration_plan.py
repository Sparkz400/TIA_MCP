"""Native plan/guard qualification callable from an explicitly owned fixture runner.

This module never starts TIA, installs components, closes projects or terminates
processes. Pass the initialized MCP Client belonging to an owned scratch fixture.
It deliberately attempts change_apply to prove the plan-only guard, so only an
explicit run_native=True with ownership evidence is accepted. It has not been run
against TIA as part of the offline planner delivery.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import uuid

from engineering_preservation import native_payload_hash


def require(value, message):
    if not value:
        raise AssertionError(message)


def qualify_plan(client, payload: dict, artifacts: Path, *, run_native: bool = False):
    require(run_native is True, "Explicit run_native=True is required")
    status = client.tool("tia_session_status")
    projects = status.get("projects", [])
    require(status.get("ownsPortalProcess") is True and len(projects) == 1,
            "Qualification needs one project in an owned Portal instance")
    project = projects[0]
    require(project.get("name", "").startswith("TiaMcpScratch_")
            and project.get("name") == payload["target"]["projectName"],
            "Exact selected project must be the explicitly owned scratch fixture")
    output = artifacts.resolve() / ("integration_plan_" + uuid.uuid4().hex[:8])
    output.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "profile": "PLC integration native preparation and apply-refusal only",
              "completed": False, "installationVerified": False, "runtimeVerified": False,
              "payload": payload, "sessionBefore": status, "steps": []}

    def persist():
        (output / "qualification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")

    def call(name, arguments=None):
        row = {"tool": name, "arguments": arguments, "state": "dispatching"}
        report["steps"].append(row)
        persist()
        result = client.tool(name, arguments)
        row.update(state="completed", result=result)
        persist()
        return result

    def export_caller():
        if payload["integration"]["caller"].get("mode") == "createStandaloneFc":
            inventory = call("software_inspect", {"plcPath": payload["target"]["plcPath"], "maxItems": 2000})
            require(inventory.get("completeness", {}).get("scopeComplete") is True, "Standalone caller absence requires complete inventory")
            name = payload["integration"]["caller"]["name"]
            require(not [row for key in ("blocks", "types") for row in inventory[key] if row["name"].lower() == name.lower()],
                    "Standalone caller name must remain absent")
            return {"exists": False, "name": name, "payloadSha256": None}
        result = call("block_read", {"blockPath": payload["integration"]["caller"]["blockPath"]})
        artifact = result["artifact"]
        raw = Path(artifact["path"]).read_bytes()
        require(hashlib.sha256(raw).hexdigest() == artifact["sha256"], "Native caller artifact hash mismatch")
        return {"artifact": artifact, "payloadSha256": native_payload_hash(raw.decode("utf-8-sig"))}

    persist()
    try:
        before_inventory = call("software_inspect", {"plcPath": payload["target"]["plcPath"], "maxItems": 2000})
        require(before_inventory.get("completeness", {}).get("scopeComplete") is True, "Native inventory must be explicitly complete")
        before = export_caller()
        prepared = call("change_prepare", {"kind": "library_integrate", "payload": payload})
        plan = prepared["preview"]["integrationPlan"]
        require(plan.get("applySupported") is False and plan.get("nativeMutationDispatched") is False,
                "Preparation must describe a nonexecuting plan")
        require(len(plan["steps"]) == 2 and plan["steps"][1]["kind"] == "graphical_typed_edit",
                "Missing concrete component installation steps")
        for version in plan["versions"]:
            raw = Path(version["artifact"]["path"]).read_bytes()
            require(hashlib.sha256(raw).hexdigest() == version["contentSha256"], "Pinned native version bytes differ")
        report["applyGuardDispatch"] = {"changeId": prepared["changeId"], "state": "dispatching"}
        persist()
        rejected = client.request("tools/call", {"name": "change_apply", "arguments": {
            "changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
            "idempotencyKey": "integration-plan-" + uuid.uuid4().hex,
            "checkpointSave": True, "compile": True, "save": True}})
        report["applyGuardDispatch"].update(state="response_received", result=rejected)
        persist()
        require(rejected.get("isError") is True and "PlcIntegrationNotQualified" in json.dumps(rejected),
                "Plan-only native apply was not refused; retain journals and stop without retry")
        recorded = call("change_status", {"changeId": prepared["changeId"]})
        journal = recorded["journal"]
        require(recorded["state"] == "prepared" and not journal.get("checkpoints")
                and not journal.get("idempotencyKeyHash"), "Refusal created a claim/checkpoint or changed state")
        after = export_caller()
        after_inventory = call("software_inspect", {"plcPath": payload["target"]["plcPath"], "maxItems": 2000})
        require(after_inventory.get("completeness", {}).get("scopeComplete") is True, "Native post-check inventory must be complete")
        for key in ("blocks", "types", "tagTables"):
            require(key in before_inventory and before_inventory[key] == after_inventory[key],
                    "Planning/refusal changed the native " + key + " inventory")
        require(before["payloadSha256"] == after["payloadSha256"], "Planning/refusal changed native caller payload")
        report.update(completed=True, callerBefore=before, callerAfter=after,
                      plan=plan, journal=journal,
                      qualificationScope="Exact recipe preparation, recorded version export hashes, guarded application refusal, block/type/tag inventory and caller-payload preservation. No install/compile/reopen/recovery/runtime proof.")
        return report
    except Exception as error:
        report["failure"] = repr(error)
        raise
    finally:
        persist()


if __name__ == "__main__":
    raise SystemExit("Import qualify_plan(client, payload, artifacts, run_native=True) from an owned-fixture runner. This module cannot attach to or mutate an arbitrary user project.")
