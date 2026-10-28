"""Native probe of the D14 kinds safety_fparameter_set and safety_printout on an owned scratch F-topology.

Adds a CPU 1511F-1 PN and an ET 200SP station (IM 155-6 PN ST, server module, F-DI 8x24VDC HF) on its PROFINET IO
system through the qualified remote-I/O change sequence, then records each probe's exact outcome and continues. A probe
that fails after its apply was dispatched restores that change's checkpoint. The F-CPU's generated scratch user receives "Full access including fail-safe" (failsafeRuntimeAccess), which its
compile requires. Never logs in to a safety program, passes a password or downloads. Observations of this scratch only, not feature qualification.
"""
from __future__ import annotations

import argparse
import hashlib
from itertools import chain
from pathlib import Path

from library_native_harness import Harness, require
from probe_wave_kinds import attempt, error_of, probe
from qualify_cpu_memory_bytes import version_key
from qualify_remote_io import SERVER_MODULE_NAMES, server_relocation, verified_manifest

KIND = "safety_fparameter_set"
STATION, REMOTE, HEAD, FDI, FDI2 = "TiaMcpFStation", "TiaMcpRemoteIo", "TiaMcpRemoteHead", "TiaMcpFdi1", "TiaMcpFdi2"
DEST, MONITORING = "Failsafe_FDestinationAddress", "Failsafe_FMonitoringtime"


def free_destination(current, modules):
    """Nearest F-destination address no listed F-I/O uses, below the current one first, so it stays near TIA's own assignments."""
    used = {row.get(DEST) for row in modules.values()}
    return next(v for v in chain(range(current - 1, 0, -1), range(current + 1, 65535)) if v not in used)


def change(h, kind, payload, scope, compile=True):
    """A prepared change that must apply; as in qualify_remote_io, only hardware_add and network_configure defer compile/save."""
    prepared = h.stage("prepare " + kind, lambda: h.tool("change_prepare", {"kind": kind, "payload": payload, **scope}))
    if compile:
        return prepared, h.apply_prepared(kind, prepared)
    applied = h.stage("apply " + kind + " without compile", lambda: h.tool("change_apply", {**h.apply_args(prepared), "compile": False, "save": False}))
    require(applied["state"] == "applied", "Prerequisite change did not reach applied")
    return prepared, applied


def catalog(h, search, name, article):
    """Installed catalog entries of one type name and article family, newest version first."""
    entries = [e for e in h.tool("hardware_catalog_search", {"filter": search, "maxItems": 500})["entries"]
               if name in (e.get("typeName") or "") and article in (e.get("articleNumber") or "")]
    require(entries, "Installed catalog has no %s entry with article %s" % (name, article))
    return sorted(entries, key=version_key, reverse=True)


def fio(h, plc):
    """safety_validation_inspect of the F-PLC, and the Failsafe_* values of every listed F-I/O by hardware path."""
    inspected = h.tool("safety_validation_inspect", {"plcPath": plc, "maxItems": 2000})
    return inspected, {m["path"]: {a["name"]: a.get("value") for a in m["fParameters"]} for m in inspected["fIoModules"]}


def write(path, name, expected, value):
    return {"safetyAcknowledged": True, "parameters": [{"hardwarePath": path, "name": name, "expected": expected, "value": value}]}


def refused(h, plc, payload, code):
    """change_prepare must refuse with code; an unexpectedly prepared change is recorded and never applied."""
    result = h.client.request("tools/call", {"name": "change_prepare", "arguments": {"kind": KIND, "payload": payload, "plcPath": plc}})
    require(result.get("isError"), "Preparation was not refused: %s" % (result.get("structuredContent") or {}).get("preview"))
    error = error_of(result)
    require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
    return error


def round_trip(h, plc, path, name, choose):
    """Change one F-parameter of one F-I/O away from its current value and back, with the signatures around both."""
    _, before = fio(h, plc)
    current = before[path][name]
    require(type(current) is int, "%s is not an integer: %r" % (name, current))
    value = choose(current, before)
    changed = attempt(h, KIND, write(path, name, current, value), {"plcPath": plc})
    require(changed["prepared"], "Change was refused: %s" % changed.get("error"))
    back = attempt(h, KIND, write(path, name, value, current), {"plcPath": plc})
    require(back["prepared"], "Change back was refused: %s" % back.get("error"))
    _, after = fio(h, plc)
    require(after[path][name] == current, name + " was not restored")
    signatures = [{k: v for k, v in rows[path].items() if "Signature" in k} for rows in (before, after)]
    return {"before": current, "value": value, "change": changed, "changeBack": back,
            "signaturesBefore": signatures[0], "signaturesAfter": signatures[1], "signaturesRestored": signatures[0] == signatures[1]}


def printout(h, plc):
    result = h.client.request("tools/call", {"name": "safety_printout", "arguments": {"plcPath": plc, "printer": "MicrosoftPrintToPdf",
                              "documentLayout": "DocuInfo_ISO_A4_Portrait", "option": "Compact", "activationTestReports": False}})
    if result.get("isError"):
        error = error_of(result)
        require(error.get("code") == "PrinterUnavailable", "Printout failed: %s" % error)
        return {"refused": error}
    artifact = result["structuredContent"]["safetyPrintout"]
    content = Path(artifact["path"]).read_bytes()
    require(len(content) == artifact["bytes"] and hashlib.sha256(content).hexdigest() == artifact["sha256"], "Printout file differs from its reported size or SHA-256")
    return result["structuredContent"]


def body(h):
    h.owned_scratch("safety probe")
    fcpu = h.report["fCpu"] = catalog(h, "1511F-1 PN", "1511F-1 PN", "6ES7 ")[0]
    # A new CPU needs its confidential-data password before it compiles (qualify_cpu_memory_bytes.add_cpu).
    # The F-CPU compiles only when a local user holds "Full access including fail-safe"; granted in this owned scratch only.
    change(h, "hardware_add", {"typeIdentifier": fcpu["typeIdentifier"], "deviceName": STATION, "itemName": "TiaMcpFPlc",
                               "configureScratchSecurity": True, "failsafeRuntimeAccess": True}, {})
    plc = h.report["fPlc"] = next(p["path"] for p in h.tool("project_inventory", {"maxItems": 2000})["plcs"] if STATION in p["path"])
    probe(h, "safety_inspect of the F-PLC", lambda: h.tool("safety_inspect", {"plcPath": plc, "maxItems": 2000}))
    try:
        fparameters(h, plc, {"plcPath": plc})
    finally:  # The printout needs only the F-CPU, so it is probed even when the F-I/O route fails.
        probe(h, "safety_printout MicrosoftPrintToPdf Compact", lambda: printout(h, plc))
        h.report["summary"] = {p["name"]: p["outcome"] for p in h.report.get("probes", [])}


def fparameters(h, plc, scope):
    """qualify_remote_io's route (local.remote-io.full-route.20261024) with the F-CPU as IO controller, then the F-parameter probes."""
    head = h.report["interfaceModule"] = catalog(h, "IM 155-6 PN ST", "PN ST", "6ES7 155-6")[0]
    require(head["typeIdentifier"] == "OrderNumber:6ES7 155-6AU02-0BN0/V6.4", "The generated-server contract requires the captured IM 155-6AU02 V6.4 profile.")
    change(h, "hardware_add", {"typeIdentifier": head["typeIdentifier"], "deviceName": REMOTE, "itemName": HEAD}, scope, compile=False)
    inventory = h.tool("project_inventory", {"maxItems": 2000})["hardware"]
    rows = []
    for item in (i for i in inventory if STATION in i["path"] or REMOTE in i["path"]):
        inspected = h.tool("hardware_inspect", {"hardwarePath": item["path"], "maxItems": 2000})
        require(not inspected.get("completeness", {}).get("truncated"), "Selected hardware read was truncated.")
        rows.extend(inspected["items"])
    controller = next(r for r in rows if STATION in r["path"] and r.get("network", {}).get("ioControllers"))
    device = next(r for r in rows if REMOTE in r["path"] and r.get("network", {}).get("ioConnectors"))
    rails = [r for r in inventory if REMOTE in r["path"] and r["classification"] == "None" and r["positionNumber"] == 0 and r["path"].count("/items/") == 1]
    require(len(rails) == 1, "The remote-I/O profile must expose exactly one root rack at position 0.")
    heads = [r for r in rows if r["name"] == HEAD and r["path"].count("/items/") == 1]
    require(len(heads) == 1, "The remote head identity is ambiguous.")
    server = SERVER_MODULE_NAMES.get(device["name"])
    require(server, "No captured server-module name for the interface name " + repr(device["name"]))
    author = next(a["value"] for a in heads[0]["attributes"] if a["name"] == "Author" and "error" not in a)
    for index, target in enumerate((controller, device), start=10):
        change(h, "network_configure", {"hardwarePath": target["path"], "nodeName": target["network"]["nodes"][0]["name"], "subnetName": "TiaMcpFSubnet",
                                        "attributes": {"Address": "192.0.2." + str(index), "SubnetMask": "255.255.255.0"}}, scope, compile=False)
    prepared, assigned = change(h, "network_io_assign", {"hardwarePath": device["path"], "connectorIndex": 0, "controllerHardwarePath": controller["path"],
        "controllerIndex": 0, "createIoSystemName": "TiaMcpFIoSystem", "expectedServerModule": {"name": server, "typeIdentifier": "OrderNumber:6ES7 193-6PA00-0AA0/V1.2",
        "containerHardwarePath": rails[0]["path"], "positionNumber": 1, "attributes": {"AdditionalInformation": "", "Author": author, "Comment": "",
        "LocationIdentifier": "", "Name": server, "PlantDesignation": ""}}}, scope)
    companion = next(e["expectedNativeCompanion"] for e in prepared["preview"]["effects"] if "expectedNativeCompanion" in e)
    # The generated server occupies slot 1, where no input module plugs; qualify_remote_io moves it to slot 4 the same way.
    change(h, "hardware_move", server_relocation(verified_manifest(assigned, "configured-hardware-preservation"), companion), scope)
    for module in catalog(h, "F-DI 8x24VDC HF", "F-DI 8x24VDC HF", "6ES7 136-6"):
        plug = {"hardwarePath": rails[0]["path"], "typeIdentifier": module["typeIdentifier"], "name": FDI, "positionNumber": 1}
        proposed = h.client.request("tools/call", {"name": "change_prepare", "arguments": {"kind": "hardware_plug", "payload": plug, **scope}})
        if not proposed.get("isError"):
            h.apply_prepared("hardware_plug", proposed["structuredContent"])
            h.report["fModule"] = module
            break
        require(error_of(proposed).get("code") == "IncompatibleHardware", "Unexpected F-DI preparation failure: %s" % error_of(proposed))
    require("fModule" in h.report, "No catalog-selected F-DI module plugs into the ET 200SP station.")

    def second():
        outcome = attempt(h, "hardware_plug", {**plug, "name": FDI2, "positionNumber": 2}, scope)
        require(outcome["prepared"], "The second F-DI was refused: %s" % outcome.get("error"))
        return outcome
    probe(h, "plug a second F-DI for the duplicate-address refusal", second)

    paths = {r["name"]: r["path"] for r in h.tool("project_inventory", {"maxItems": 2000})["hardware"] if r["name"] in (FDI, FDI2)}
    inspected, modules = h.stage("safety_validation_inspect of the F-PLC", lambda: fio(h, plc))
    owned = {name: [p for p in modules if p == path or p.startswith(path + "/")] for name, path in paths.items()}
    require(len(owned.get(FDI, [])) == 1, "Expected one F-I/O item under the F-DI: %s" % owned)
    di, di2 = owned[FDI][0], (owned.get(FDI2) or [None])[0]
    h.report["fIo"] = {"di": di, "di2": di2}
    current = modules[di]

    if inspected["isPasswordSet"] and not inspected["isLoggedOn"]:
        probe(h, "refuse a password-protected F-program", lambda: refused(h, plc, write(di, DEST, current[DEST], current[DEST] + 1), "SafetyLoginRequired"))
        h.report["fParameterProbes"] = "Stopped: the scratch F-program is password-protected; nothing logs in."
    else:
        checks = [("a non-allowlisted Failsafe_ attribute", lambda: write(di, "Failsafe_FSourceAddress", current["Failsafe_FSourceAddress"], current["Failsafe_FSourceAddress"] + 1), "UnsupportedAttribute"),
                  ("a wrong expected value", lambda: write(di, DEST, current[DEST] + 1, current[DEST] + 2), "ExpectedValueMismatch"),
                  ("a missing safetyAcknowledged", lambda: {"parameters": write(di, DEST, current[DEST], current[DEST] + 2)["parameters"]}, "InvalidRequest")]
        if di2:
            checks.append(("a duplicate F-destination address", lambda: write(di, DEST, current[DEST], modules[di2][DEST]), "AddressConflict"))
        else:
            h.report.setdefault("probes", []).append({"name": "refuse a duplicate F-destination address", "outcome": "skipped", "reason": "No second F-DI F-I/O was added."})
        for label, payload, code in checks:
            probe(h, "refuse " + label, lambda payload=payload, code=code: refused(h, plc, payload(), code))
        probe(h, MONITORING + " change and change back", lambda: round_trip(h, plc, di, MONITORING, lambda value, _: value - 10 if value > 1000 else value + 10))
        probe(h, DEST + " change and change back", lambda: round_trip(h, plc, di, DEST, free_destination))
        h.stage("safety_validation_inspect after the F-parameter probes", lambda: fio(h, plc)[0])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "ps_", "Exploratory native probe of safety_fparameter_set and safety_printout on an owned scratch F-CPU with ET 200SP F-DI",
            ["Exploratory probe: observations of one scratch project, not feature qualification.",
             "No safety login, password, activation-test execution or download; a password-protected F-program stops the F-parameter probes."], []).run(body)
