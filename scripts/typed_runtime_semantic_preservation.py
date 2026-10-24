"""Semantic runtime preservation for typed live reports (integrator rule of 24 October 2026).

1. The generated card is verified at download and again after provisioning restored it,
   before power-on, with the same program revision.
2. The complete PLC XML readback before cleanup equals the qualified revision.
3. Project file changes come only from the receipted generated-state save (plc_compile)
   and credential provisioning; any other mutating call or later change fails.
4. Post-RUN card files are runtime state: inventoried, never compared.

Applies to retained reports without rerunning them; byte-level observations stay in the report.
"""

# Calls that cannot change project files; simulation_download writes only the card.
READ_OR_SIMULATOR = frozenset({"simulation_discovery_baseline", "sim_create", "simulation_download", "sim_state",
    "sim_tags", "sim_read", "sim_write", "sim_release", "tia_session_status", "software_inspect", "block_read",
    "block_interface", "type_read", "tag_table_read", "artifact_read", "tia_detach"})


def _observation(preservation, phase, target):
    rows = [row for row in preservation.get("observations", []) if row.get("phase") == phase and row.get("target") == target]
    return rows[0] if len(rows) == 1 else None


def evaluate(report):
    failures = []
    def require(condition, message):
        if not condition:
            failures.append(message)
    preservation, runtime = report.get("runtimePreservation") or {}, report.get("runtime") or {}
    cases = runtime.get("cases") or []
    require(runtime.get("status") == "passed" and runtime.get("caseCoverage", {}).get("fullSuitePassed") is True, "runtime suite did not pass")
    require(report.get("ownedCleanupVerified") is True and not report.get("cleanupFailure") and preservation.get("ownedTiaCleanupVerified") is True,
            "owned cleanup unverified")
    require(bool(cases), "no runtime case")
    # 1. Card at download and after provisioning restore, before power-on.
    require(bool(preservation.get("generatedCardVerification")), "card not verified at download")
    receipts = []
    for case in cases:
        isolation = case.get("isolation") or {}
        card, restored = isolation.get("memoryCard") or {}, isolation.get("restoredMemoryCard") or {}
        provisioning, save = isolation.get("credentialProvisioning") or {}, isolation.get("generatedProjectSave") or {}
        require(card.get("verifiedBeforePowerOn") is True and restored.get("verifiedBeforePowerOn") is True, "card not re-verified after provisioning")
        require(card.get("programRevision") and card.get("programRevision") == restored.get("programRevision") and card.get("files") == restored.get("files"),
                "restored card differs from the downloaded card")
        require(provisioning.get("originalProgramCardRestored") is True and provisioning.get("operatingState") == "Off", "provisioning did not restore the card")
        require(save.get("applied", {}).get("state") == "applied" and save.get("prepared", {}).get("preview", {}).get("kind") == "plc_compile",
                "generated-state save lacks its plc_compile receipt")
        receipts.append(save.get("prepared", {}).get("changeId"))
    # 2. Complete PLC XML before cleanup.
    readback = _observation(preservation, "beforeOwnedRuntimeCleanup", "completePlcXml")
    require(readback is not None and readback.get("status") == "passed", "PLC XML readback before cleanup did not pass")
    # 3. Only receipted project mutations inside the runtime window.
    active = preservation.get("projectActiveBeforeRuntime") or {}
    start = active.get("observedAtUtc")
    require(bool(start) and active.get("complete") is True, "no complete active project baseline")
    window = [call for call in report.get("calls", []) if start and call.get("startedAtUtc", "") >= start]
    prepared = [call for call in window if call.get("tool") == "change_prepare"]
    applied = [call for call in window if call.get("tool") == "change_apply"]
    require(len(prepared) == len(receipts) and all(call["arguments"].get("kind") == "plc_compile" for call in prepared), "unexpected change_prepare in runtime window")
    require(sorted(call["arguments"].get("changeId") for call in applied) == sorted(receipts) and all(call.get("status") == "passed" for call in applied),
            "change_apply in runtime window is not the receipted generated-state save")
    require(len([call for call in window if call.get("tool") == "simulation_provision_scratch_credential" and call.get("status") == "passed"]) == len(cases),
            "provisioning call missing")
    other = sorted({call.get("tool") for call in window} - READ_OR_SIMULATOR - {"change_prepare", "change_apply", "simulation_provision_scratch_credential"})
    require(not other, "other mutating calls in runtime window: " + ", ".join(other))
    during = _observation(preservation, "beforeOwnedRuntimeCleanup", "activeProjectAndAllCompanions") or {}
    after = (_observation(preservation, "afterVerifiedOwnedTiaCleanup", "projectAndAllCompanions") or {})
    opened = (preservation.get("closedToActiveTransition") or {}).get("addedInfoFiles", [])
    require(during.get("inventory", {}).get("complete") is True and after.get("inventory", {}).get("complete") is True, "incomplete project inventories")
    attributable = set(during.get("difference", {}).get("changed", [])) | set(during.get("difference", {}).get("added", []))
    late = after.get("difference", {})
    unexplained = sorted((set(late.get("changed", [])) | set(late.get("added", []))) - attributable - set(opened))
    require(not late.get("removed") and not unexplained, "project changes outside the receipted mutations: " + ", ".join(unexplained + late.get("removed", [])))
    # 4. Post-RUN card files: inventory only.
    cards = [{"phase": row["phase"], "files": row.get("inventory", {}).get("files", {})}
             for row in preservation.get("observations", []) if row.get("target") == "memoryCard"]
    return {"rule": "typed-runtime-semantic-preservation.v1", "verified": not failures, "failures": failures,
            "receiptedProjectChanges": sorted(attributable), "postRunCardInventories": cards}
