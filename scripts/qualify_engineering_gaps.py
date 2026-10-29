"""Qualify the engineering gaps of 29 October 2026 in one owned scratch project: typed CPU parameters
(change.hardware_cpu_parameters), the compile-only kinds (change.device_compile, change.project_compile,
change.hmi_compile) and the tag-table lifecycle (change.tag_table_create, change.tag_table_rename, change.tag_table_delete).

Every change runs through change_prepare/change_apply with checkpoint, compile and save; typed refusals come first and
change no block, type or tag table. A fresh Portal reopens the saved project and finds the same tables, program and CPU
parameters. One change of each family is restored from its checkpoint into a new directory.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import time

from library_native_harness import Harness, require

# Reviewed parameter -> candidate new values on the scratch CPU 1511-1 PN, tried in order; the names are
# HardwareEngineeringRules.CpuParameterAttributes. V21 rejects StartupActionAfterPowerOn = 2 in its setter (29 October 2026),
# so coded values are found by trial and every refused value is recorded.
PARAMETERS = {"CycleMaximumCycleTime": [200], "CycleMinimumCycleTime": [5], "CycleCommunicationLoad": [30],
              "StartupActionAfterPowerOn": [1, 4, 0, 2], "StartupComparisonPresetToActualModule": [1, 3, 0, 4], "StartupConfigurationTimeout": [30000],
              # TIA switches daylight saving off with a zone that has none (29 October 2026), so DST changes first.
              "TimeOfDayActivateDaylightSavingTime": [False], "TimeOfDayLocalTimeZone": [23, 21, 1]}
# Applied one at a time after the reviewed set; V21 may reject them at compile (they need NTP servers or access-level passwords).
OBSERVED = {"CycleEnableMinimumCycleTime": False, "TimeSynchronizationNtpV2": 1, "PlcAccessControlConfiguration": "EnabledWithAccessControlViaAccessLevel"}
TABLE, RENAMED, RESTORED_TABLE = "TiaMcpGapTable", "TiaMcpGapRenamed", "TiaMcpGapRestore"
POLICY = {"dependencyPolicy": "rejectReferenced"}
# A Classic and a Unified panel, each created through hardware_add without a start screen.
HMI_PANELS = ("TP700 Comfort", "MTP700 Unified Basic")
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 in TIA Portal V21; other CPU families, firmware, F-CPUs and software units are not qualified.",
    "CPU parameters: the reviewed subset only, each set to one new value and read back; the runtime effect needs a download and is not observed here.",
    "Compilation is checked by zero errors and complete diagnostics; it does not establish machine behavior.",
    "Tag tables: PLC-root scope only; software-unit scopes and user groups are not exercised.",
]


def cpu_values(h, path):
    items = h.tool("engineering_read", {"selectors": {"scope": "hardware", "hardwarePath": path, "maxItems": 1000}})["items"]
    require(len(items) == 1, "Expected one hardware item for " + path)
    attributes = {a["name"]: a.get("value") for a in items[0]["attributes"]}
    return {name: attributes.get(name) for name in (*PARAMETERS, *OBSERVED)}


def tables(h, plc):
    return {t["name"]: t["path"] for t in h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["tagTables"]}


def table_members(h, plc, name):
    xml = h.tool("tag_table_read", {"plcPath": plc, "tagTablePath": tables(h, plc)[name]}).get("xml", "")
    return xml.count("<SW.Tags.PlcTag ") + xml.count("<SW.Tags.PlcTag>") + xml.count("<SW.Tags.PlcUserConstant")


def reopen(h, path):
    """TIA keeps a closed instance's project lock for about two minutes; only that refusal is retried."""
    for attempt in range(13):
        result = h.client.request("tools/call", {"name": "project_open", "arguments": {"path": path}})
        message = ((result.get("structuredContent") or {}).get("error") or {}).get("message") or ""
        if not result.get("isError") or "already been opened" not in message or attempt == 12:
            require(not result.get("isError"), "project_open failed: " + message)
            return {"attempts": attempt + 1}
        time.sleep(15)


def compiled(applied, targets):
    compilation = applied.get("compile") or applied.get("compilation") or {}
    # Project compilation names each receipt by device and keeps its inventory path in targetPath.
    require(compilation.get("errorCount") == 0 and [t.get("targetPath") or t["target"] for t in compilation.get("targets", [])] == targets,
            "Unexpected compilation evidence: %s" % compilation)
    require("compile_only_requested" in applied["stages"] and "mutation_applied" not in applied["stages"], "A compile-only change ran a mutation: %s" % applied["stages"])
    return compilation


def restore(h, prepared, label, check):
    def run():
        recovered = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        require(recovered["state"] == "restored" and "checkpoint_retrieved_to_new_directory" in recovered["stages"], "Checkpoint recovery was not verified: %s" % recovered["stages"])
        check()
        return {"recoveredProject": recovered["recoveredProject"], "stages": recovered["stages"]}
    return h.stage("restore " + label + " from its checkpoint", run)


def body(h):
    plc = h.owned_scratch("engineering gaps")
    project_path = h.tool("tia_session_status")["projects"][0]["path"]
    cpu = plc.rsplit("/plc/", 1)[0]
    device = cpu.split("/items/", 1)[0]
    default = next(name for name in tables(h, plc) if name not in (TABLE, RENAMED))
    h.report["fixture"] = {"plcPath": plc, "cpuPath": cpu, "devicePath": device, "defaultTagTable": default}

    # Tag-table lifecycle.
    h.refuse("refuse creating the default table's name", "tag_table_create", {"tagTableName": default.upper()}, "ObjectExists")
    h.refuse("refuse renaming a missing table", "tag_table_rename", {"tagTableName": "TiaMcpGapMissing", "newName": "X"}, "ObjectNotFound")
    h.refuse("refuse deleting the default tag table", "tag_table_delete", {"tagTableName": default}, "DefaultTagTable")
    created = h.apply("tag_table_create", {"tagTableName": TABLE})
    h.stage("read back the created empty table", lambda: require(TABLE in tables(h, plc) and table_members(h, plc, TABLE) == 0, "Created table missing or not empty")
            or {"readbacks": [r["role"] for r in created["readbacks"]]})
    h.refuse("refuse a case-only duplicate name", "tag_table_create", {"tagTableName": TABLE.lower()}, "ObjectExists")
    h.refuse("refuse renaming onto the default table", "tag_table_rename", {"tagTableName": TABLE, "newName": default}, "ObjectExists")
    h.apply("tag_table_rename", {"tagTableName": TABLE, "newName": RENAMED})
    h.stage("read back the rename", lambda: require(RENAMED in tables(h, plc) and TABLE not in tables(h, plc), "Rename not observed") or sorted(tables(h, plc)))
    h.apply("tag_create", {"tagTableName": RENAMED, "name": "TiaMcpGapBit", "dataTypeName": "Bool", "logicalAddress": "%M100.0"})
    h.refuse("refuse deleting a non-empty table", "tag_table_delete", {"tagTableName": RENAMED}, "TagTableNotEmpty")
    h.apply("tag_delete", {"tagTableName": RENAMED, "memberName": "TiaMcpGapBit", **POLICY})
    h.apply("tag_table_delete", {"tagTableName": RENAMED})
    h.stage("read back the delete", lambda: require(RENAMED not in tables(h, plc), "Deleted table still present") or sorted(tables(h, plc)))

    # Compile-only kinds.
    h.refuse("refuse project_compile with a payload", "project_compile", {"x": 1}, "InvalidRequest")
    h.refuse("refuse device_compile of a missing device", "device_compile", {"devicePath": device + "Missing"}, "ObjectNotFound")
    inventory = h.inventory()
    def no_flag():
        prepared = h.prepare("device_compile", {"devicePath": device})
        error = h.call_error("change_apply", {**h.apply_args(prepared), "compile": False, "save": False})
        require(error.get("code") == "InvalidRequest", "compile=false was not refused: %s" % error)
        return error
    h.stage("refuse device_compile without compile=true", no_flag)
    device_prepared = h.prepare("device_compile", {"devicePath": device})
    require(device_prepared["preview"]["effects"][0]["targets"] == [device], "Unexpected device compile preview: %s" % device_prepared["preview"]["effects"])
    h.report["deviceCompile"] = compiled(h.apply_prepared("device_compile", device_prepared), [device])
    project_prepared = h.prepare("project_compile", {})
    targets = project_prepared["preview"]["effects"][0]["targets"]
    require(targets == [device], "project_compile targets differ from the project's devices: %s" % targets)
    h.report["projectCompile"] = compiled(h.apply_prepared("project_compile", project_prepared), targets)
    require(h.inventory() == inventory, "A compile-only change altered blocks, types or tag tables")

    # Typed CPU parameters.
    original = h.stage("discover CPU parameters", lambda: cpu_values(h, cpu))
    require(all(original[name] is not None for name in original), "The CPU does not expose every reviewed parameter: %s" % original)
    require(all(original[name] != value for name, value in OBSERVED.items()), "A planned value equals the scratch default: %s" % original)
    h.refuse("refuse disabling access control", "hardware_cpu_parameters", {"hardwarePath": cpu, "attributes": {"PlcAccessControlConfiguration": "Disabled"}}, "ProtectionDowngradeRefused")
    h.refuse("refuse an attribute outside the subset", "hardware_cpu_parameters", {"hardwarePath": cpu, "attributes": {"Comment": "x"}}, "UnsupportedAttribute")
    first = next(iter(PARAMETERS))
    h.refuse("refuse an unchanged parameter", "hardware_cpu_parameters", {"hardwarePath": cpu, "attributes": {first: original[first]}}, "InvalidRequest")
    h.refuse("refuse a null parameter", "hardware_cpu_parameters", {"hardwarePath": cpu, "attributes": {first: None}}, "InvalidRequest")
    h.refuse("refuse a parameter on the device's rack", "hardware_cpu_parameters", {"hardwarePath": rack(h, device), "attributes": {first: PARAMETERS[first][0]}}, "UnsupportedCapability")
    h.report["parameterValues"] = {name: set_first(h, cpu, name, [v for v in candidates if v != original[name]]) for name, candidates in PARAMETERS.items()}
    for name, value in OBSERVED.items():
        h.report.setdefault("observedParameters", {})[name] = observe(h, cpu, name, value)
    configured = cpu_values(h, cpu)

    # Persistence: a fresh Portal reopens the saved project.
    software = h.inventory()
    h.stage("close changed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen saved project", lambda: reopen(h, project_path))
    def reopened():
        require(cpu_values(h, cpu) == configured, "Reopened CPU parameters differ")
        require(h.inventory() == software, "Reopened blocks, types or tag tables differ")
        return configured
    h.stage("reopened CPU parameters, blocks and tag tables", reopened)

    # Checkpoint recovery: a created table, then a parameter change, each restored into a new directory.
    prepared = h.prepare("tag_table_create", {"tagTableName": RESTORED_TABLE})
    h.apply_prepared("tag_table_create", prepared)
    restore(h, prepared, "tag_table_create", lambda: require(RESTORED_TABLE not in tables(h, plc), "The restored project still has the created table"))
    name, value = next(iter(original.items()))
    prepared = h.prepare("hardware_cpu_parameters", {"hardwarePath": cpu, "attributes": {name: original[name]}})
    h.apply_prepared("hardware_cpu_parameters", prepared)
    restore(h, prepared, "hardware_cpu_parameters", lambda: require(cpu_values(h, cpu) == configured, "The restored CPU parameters differ from the checkpoint"))
    # One combined change, so TIA's dependent settings (daylight saving follows the zone) are part of the reviewed request.
    current = cpu_values(h, cpu)
    h.apply("hardware_cpu_parameters", {"hardwarePath": cpu, "attributes": {n: original[n] for n in reversed((*PARAMETERS, *OBSERVED)) if current[n] != original[n]}})
    h.stage("original CPU parameters restored", lambda: require(cpu_values(h, cpu) == original, "Original CPU parameters not restored") or original)

    # Last: every later device compile would include these panels, which TIA cannot compile without a start screen.
    for panel in HMI_PANELS:
        hmi = add_hmi(h, panel)
        if hmi:
            h.report.setdefault("hmiCompile", {})[panel] = hmi_compile(h, hmi)


def set_first(h, cpu, name, candidates):
    """Apply the first candidate the native setter accepts; each refused value is recorded. The setter throws before any
    project change, so a refused attempt leaves the project as its checkpoint saved it."""
    def run():
        refused = []
        for value in candidates:
            prepared = h.prepare("hardware_cpu_parameters", {"hardwarePath": cpu, "attributes": {name: value}})
            outcome = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
            content = outcome.get("structuredContent") or {}
            if not outcome.get("isError"):
                require("configured_hardware_preservation_verified" in content["stages"], "Hardware preservation was not verified for " + name)
                require(cpu_values(h, cpu)[name] == value, "Native readback differs for " + name)
                return {"value": value, "refused": refused}
            error = content.get("error") or {}
            require("set_" + name in (error.get("message") or "") and "mutation_applied" not in (error.get("details") or {}).get("stages", []),
                    "Unexpected failure for %s = %s: %s" % (name, value, error.get("message")))
            refused.append({"value": value, "code": error.get("code"), "message": error.get("message")})
        raise AssertionError("The native setter refused every candidate for %s: %s" % (name, refused))
    return h.stage("set " + name, run)


def observe(h, cpu, name, value):
    """Apply one parameter whose compile outcome V21 decides; a failed compile is recorded and restored from its checkpoint."""
    def run():
        prepared = h.prepare("hardware_cpu_parameters", {"hardwarePath": cpu, "attributes": {name: value}})
        outcome = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
        content = outcome.get("structuredContent") or {}
        if not outcome.get("isError"):
            require("configured_hardware_preservation_verified" in content["stages"] and cpu_values(h, cpu)[name] == value, "Readback or preservation failed for " + name)
            return {"outcome": "applied", "value": value}
        error = content.get("error") or {}
        compilation = (error.get("details") or {}).get("compilation") or {}
        recovered = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        require(recovered["state"] == "restored", "The refused parameter was not restored")
        return {"outcome": "refused", "error": error.get("code"), "message": error.get("message"),
                "messages": [t.get("messages") for t in compilation.get("targets", [])], "restored": cpu_values(h, cpu)[name]}
    return h.stage("observe " + name, run)


def rack(h, device):
    rows = [r["path"] for r in h.tool("project_inventory", {"maxItems": 1000})["hardware"] if r["path"].startswith(device + "/items/") and r["path"].count("/items/") == 1
            and r.get("positionNumber") == 0]
    require(len(rows) == 1, "Expected one rack of the scratch station: %s" % rows)
    return rows[0]


def add_hmi(h, panel):
    """A panel for hmi_compile, added without compile: a device created through Openness has no start screen. A refused
    creation is recorded and leaves hmi_compile unqualified for that family."""
    def run():
        before = {t["hmiPath"] for t in h.tool("library_query", {"request": {"action": "hmi_targets"}})["targets"]}
        entries = [e for e in h.tool("hardware_catalog_search", {"filter": panel, "maxItems": 200})["entries"] if e.get("typeName") == panel]
        require(entries, "No catalog entry named " + panel)
        selected = max(entries, key=lambda e: (tuple(int(x) for x in (e.get("version") or "0").split(".") if x.isdigit()), e["typeIdentifier"]))
        name = "TiaMcpGapHmi" + str(len(before) + 1)
        outcome = h.client.request("tools/call", {"name": "change_prepare", "arguments": {"kind": "hardware_add", "payload": {
            "typeIdentifier": selected["typeIdentifier"], "deviceName": name, "itemName": name + "Panel"}}})
        if outcome.get("isError"):
            return {"catalog": selected, "refused": (outcome.get("structuredContent") or {}).get("error")}
        applied = h.client.request("tools/call", {"name": "change_apply", "arguments": {**h.apply_args(outcome["structuredContent"]), "compile": False, "save": False}})
        if applied.get("isError"):
            return {"catalog": selected, "applyFailed": (applied.get("structuredContent") or {}).get("error")}
        found = [t for t in h.tool("library_query", {"request": {"action": "hmi_targets"}})["targets"] if t["hmiPath"] not in before]
        require(len(found) == 1, "Expected one new HMI target: %s" % found)
        return {"catalog": selected, "target": found[0]}
    result = h.stage("add a " + panel + " for hmi_compile", run)
    h.report.setdefault("hmiFixtures", {})[panel] = result
    if "target" not in result:
        return None
    target = result["target"]
    return {**target, "devicePath": target["hmiPath"].split("/items/", 1)[0]}


def hmi_compile(h, hmi):
    """hmi_compile of the exact target: either a clean compile, or TIA's compile errors returned as CompileFailed and the
    checkpoint restored into a new directory. Both outcomes are recorded."""
    target = {"family": hmi["family"], "hmiPath": hmi["hmiPath"]}
    h.refuse("refuse hmi_compile of a missing " + hmi["family"] + " target", "hmi_compile", {"target": {**target, "hmiPath": hmi["hmiPath"] + "Missing"}}, "HmiTargetNotFound")
    prepared = h.prepare("hmi_compile", {"target": target})
    require(prepared["preview"]["effects"][0]["targets"] == [hmi["compileScope"]], "Unexpected hmi_compile preview: %s" % prepared["preview"]["effects"])
    def run():
        outcome = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
        content = outcome.get("structuredContent") or {}
        if not outcome.get("isError"):
            return {"outcome": "compiled", "compilation": compiled(content, [hmi["compileScope"]])}
        error = content.get("error") or {}
        details = error.get("details") or {}
        compilation = details.get("compile") or details.get("compilation") or {}
        errors = [m["description"] for t in compilation.get("targets", []) for m in t.get("messages", []) if m.get("state") == "Error" and m.get("description")]
        require(error.get("code") == "CompileFailed" and [t.get("target") for t in compilation.get("targets", [])] == [hmi["compileScope"]] and errors,
                "hmi_compile failed without typed compile diagnostics: %s" % error)
        recovered = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        require(recovered["state"] == "restored", "The failed HMI compile was not restored")
        return {"outcome": "compileErrorsReported", "errors": errors, "restoredStages": recovered["stages"]}
    return h.stage("hmi_compile " + hmi["family"], run)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "eg_", "Engineering gaps: CPU parameters, compile-only kinds and tag-table lifecycle in an owned scratch", LIMITATIONS, []).run(body)
