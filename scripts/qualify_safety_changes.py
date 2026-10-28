"""Qualify the D14 safety rows natively in one owned scratch with an F-CPU 1511F-1 PN.

The F-CPU is added with configureScratchSecurity and failsafeRuntimeAccess: its generated scratch user holds "Full access
including fail-safe", which an F-CPU compile requires (granted in this owned scratch only). probe_safety_kinds.topology adds an
ET 200SP station with two F-DI modules on the F-CPU's IO system. Then:

- safety_inspect and safety_validation_inspect: exact F-PLC identity, runtime group, signatures and both F-DI F-parameters;
  maxItems 1 reports truncation; a standard PLC is refused with UnsupportedCapability.
- safety_printout: a PDF whose bytes match the reported size and SHA-256; an absent printer and a bad layout are refused.
- safety_fparameter_set: Failsafe_FDestinationAddress of one F-DI moved to a free address; typed refusals for a
  non-allowlisted attribute, a wrong expected value, a missing acknowledgement and a duplicate address.
- safety_import_xml: Main_Safety_RTG1 re-imported with a block comment; refusals for a missing acknowledgement, a standard
  block and a scope mismatch.

Each change runs with checkpoint, compile and save and an exact state diff over hardware, device inventory and every PLC's
program; F-generated blocks and the F-station's signature attributes that the safety compile rewrites are recorded as side
effects. A fresh Portal reopen must show the identical state; from there each kind is applied again and restored from its
checkpoint into a new directory equal to the reopened state. No safety login, password, acceptance or download.
"""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path

from library_native_harness import Harness, require
from probe_safety_kinds import DEST, FDI, FDI2, MONITORING, STATION, REMOTE, catalog, change, free_destination, topology, write
from qualify_hardware_changes import Run as HardwareRun, reopen
from qualify_plc_object_mutations import coverage_gaps

KINDS = ("safety_fparameter_set", "safety_import_xml")
MAIN = "Main_Safety_RTG1"
CRITERIA = {
    "AC-identity": "Reads name the exact F-PLC and refuse a standard PLC; changes refuse a non-allowlisted attribute, a wrong expected value, a duplicate F-destination, missing acknowledgements, a standard block and a scope mismatch, each leaving the project unchanged.",
    "AC-completeness": "safety_inspect and safety_validation_inspect report explicit gaps, list every F-DI and report truncation at maxItems 1.",
    "AC-preview": "Every preview is asserted against the request before apply; a wrong previewHash refuses at apply.",
    "AC-preserve": "applied: the complete state differs only in the edited F-DI or block plus recorded F-compile side effects.",
    "AC-native": "applied + persisted: checkpoint, compile and save stages, change_status and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the reopened state, per kind.",
    "AC-evidence": "This report: binaries, tool calls, step results, the printout hash, snapshots with state.json and limitations.",
}
LIMITATIONS = [
    "One F-CPU 1511F-1 PN with an ET 200SP station and two F-DI 8x24VDC HF in an owned scratch; the F-CPU's generated scratch user holds "
    "Full access including fail-safe (failsafeRuntimeAccess, scratch only).",
    "safety_fparameter_set changes Failsafe_FDestinationAddress only: Failsafe_FMonitoringtime of this F-DI is not declared writable "
    "(AttributeNotWritable), as its F-monitoring time is not manually assigned.",
    "safety_import_xml re-imports the generated main safety block with a changed block comment; no F-logic behavior, F-I/O "
    "commissioning, acceptance, login, password or download is exercised.",
    "A printout or signature read does not validate, accept or approve a safety program.",
]


class Run(HardwareRun):
    plc_compiled = ("safety_import_xml",)  # safety_fparameter_set compiles all devices.

    def args(self, kind, payload, **top):
        return {"kind": kind, "payload": payload, "plcPath": self.plc, **top}


def side_effects(run):
    """The safety compile rewrites generated F-blocks and the F-station's signature attributes; anything else stays exact."""
    prefix = "block:" + run.plc + "/blocks/"
    stations = tuple("hw:" + run.path(name) for name in (STATION, REMOTE))
    def tolerated(category, key):
        if category != "changed":
            return False
        before = run.current_before.get(key) or {}
        return (key.startswith(prefix) and (before.get("isKnowHowProtected") or "/groups/" in key)) or key.startswith(stations)
    return tolerated


def reads(run, standard):
    h, plc = run.h, run.plc
    inspected = h.stage("safety_inspect of the F-PLC", lambda: h.tool("safety_inspect", {"plcPath": plc, "maxItems": 2000}))
    require(inspected["plcPath"] == plc and inspected["isPasswordSet"] is False and inspected["isLoggedOn"] is False
            and [g["mainSafetyBlockName"] for g in inspected["runtimeGroups"]] == [MAIN]
            and {"CollectiveOfflineSignature", "SoftwareOfflineSignature"} <= {s["type"] for s in inspected["programSignatures"]}
            and inspected["completeness"]["truncated"] is False and inspected["completeness"]["gaps"], "safety_inspect differs from the F-PLC")
    cut = h.stage("safety_inspect with maxItems 1", lambda: h.tool("safety_inspect", {"plcPath": plc, "maxItems": 1}))
    require(cut["completeness"]["truncated"] is True, "maxItems 1 was not reported as truncated")
    validated = h.stage("safety_validation_inspect of the F-PLC", lambda: h.tool("safety_validation_inspect", {"plcPath": plc, "maxItems": 2000}))
    modules = {m["path"]: {a["name"]: a.get("value") for a in m["fParameters"]} for m in validated["fIoModules"]}
    owned = {name: [p for p in modules if p == path or p.startswith(path + "/")] for name, path in run.fdi.items()}
    require(sorted(owned) == [FDI, FDI2] and all(len(v) == 1 for v in owned.values())
            and modules[owned[FDI][0]][DEST] != modules[owned[FDI2][0]][DEST], "safety_validation_inspect does not list both F-DI F-I/O")
    for name, error in (("safety_inspect", "UnsupportedCapability"), ("safety_validation_inspect", "UnsupportedCapability")):
        h.stage("refuse %s of the standard PLC" % name, lambda name=name, error=error: require(
            h.call_error(name, {"plcPath": standard, "maxItems": 100}).get("code") == error, name + " did not refuse the standard PLC") or error)
    printed = h.stage("safety_printout to PDF", lambda: h.tool("safety_printout", {"plcPath": plc, "printer": "MicrosoftPrintToPdf",
                      "documentLayout": "DocuInfo_ISO_A4_Portrait", "option": "Compact", "activationTestReports": False}))
    artifact = printed["safetyPrintout"]
    content = Path(artifact["path"]).read_bytes()
    require(printed["plcPath"] == plc and content[:4] == b"%PDF" and len(content) == artifact["bytes"]
            and hashlib.sha256(content).hexdigest() == artifact["sha256"], "The printout file differs from its receipt")
    h.report["printout"] = artifact
    for label, extra, error in (("the absent XPS printer", {"printer": "MicrosoftXpsDocumentWriter"}, "PrinterUnavailable"),
                                ("a layout name with a space", {"documentLayout": "Docu Info"}, "InvalidRequest")):
        arguments = {"plcPath": plc, "printer": "MicrosoftPrintToPdf", "documentLayout": "DocuInfo_ISO_A4_Portrait", "option": "Compact", **extra}
        h.stage("refuse safety_printout with " + label, lambda arguments=arguments, error=error: require(
            h.call_error("safety_printout", arguments).get("code") == error, "safety_printout did not refuse " + label) or error)
    return {name: paths[0] for name, paths in owned.items()}, modules


def fparameter(run, di, modules, label, restore=False):
    current = run.h.tool("safety_validation_inspect", {"plcPath": run.plc, "maxItems": 2000})
    values = {m["path"]: {a["name"]: a.get("value") for a in m["fParameters"]} for m in current["fIoModules"]}
    before, value = values[di][DEST], free_destination(values[di][DEST], values)
    run.current_before = run.current["view"]
    prepared = run.prepare(label, "safety_fparameter_set", write(di, DEST, before, value))
    applied = run.finish(label, "safety_fparameter_set", prepared, changed=["hw:" + di], tolerated=side_effects(run),
                         rows={"hw:" + di: {"attributes": {DEST: value}}}, restore=restore)
    require("configured_hardware_preservation_verified" in applied["stages"], "The F-parameter write lacks configured-hardware preservation")


def import_main(run, text, label, restore=False, stale=False):
    h = run.h
    xml = h.tool("block_read", {"plcPath": run.plc, "blockName": MAIN})["xml"]
    require("<Text />" in xml, "The main safety block has no empty comment text to fill")
    edited = xml.replace("<Text />", "<Text>%s</Text>" % text, 1)
    payload = {"xml": edited, "expectedBlockNames": [MAIN], "safetyAcknowledged": True}
    key = "block:" + run.plc + "/blocks/" + MAIN
    run.current_before = run.current["view"]
    prepared = run.prepare(label, "safety_import_xml", payload)
    if stale:
        run.refuse_apply("safety_import_xml", "a wrong previewHash", {**prepared, "previewHash": "0" * 64}, "PreviewMismatch")
    run.finish(label, "safety_import_xml", prepared, changed=[key], tolerated=side_effects(run), restore=restore,
               check=lambda applied, state: require(text in h.tool("block_read", {"plcPath": run.plc, "blockName": MAIN})["xml"], "The comment is not in the export"))
    return xml


def refusals(run, di, di2, modules):
    current = modules[di]
    run.refuse("safety_fparameter_set", "a non-allowlisted Failsafe_ attribute",
               write(di, "Failsafe_FSourceAddress", current["Failsafe_FSourceAddress"], current["Failsafe_FSourceAddress"] + 1), "UnsupportedAttribute")
    run.refuse("safety_fparameter_set", "a wrong expected value", write(di, DEST, current[DEST] + 1, current[DEST] + 2), "ExpectedValueMismatch")
    run.refuse("safety_fparameter_set", "a missing safetyAcknowledged", {"parameters": write(di, DEST, current[DEST], current[DEST] + 2)["parameters"]}, "InvalidRequest")
    run.refuse("safety_fparameter_set", "the other F-DI's F-destination address", write(di, DEST, current[DEST], modules[di2][DEST]), "AddressConflict")
    run.refuse("safety_fparameter_set", "Failsafe_FMonitoringtime of this F-DI", write(di, MONITORING, current[MONITORING], current[MONITORING] + 10), "AttributeNotWritable")
    xml = run.h.tool("block_read", {"plcPath": run.plc, "blockName": MAIN})["xml"]
    run.refuse("safety_import_xml", "a missing safetyAcknowledged", {"xml": xml, "expectedBlockNames": [MAIN]}, "InvalidRequest")
    run.refuse("safety_import_xml", "another expected block name", {"xml": xml, "expectedBlockNames": ["TiaMcpOther"], "safetyAcknowledged": True}, "XmlScopeMismatch")
    standard = run.h.tool("block_read", {"plcPath": run.standard, "blockName": "TiaMcp_EnableGate"})["xml"]
    run.refuse("safety_import_xml", "the standard block TiaMcp_EnableGate", {"xml": standard, "expectedBlockNames": ["TiaMcp_EnableGate"], "safetyAcknowledged": True}, "SafetyBoundary")


def body(h):
    run = Run(h)
    h.report["criteria"] = CRITERIA
    run.standard = h.owned_scratch("safety changes")
    status = h.tool("tia_session_status")["projects"][0]
    run.project, run.base = status["path"], "/projects/%s/devices/" % status["name"]
    fcpu = h.report["fCpu"] = catalog(h, "1511F-1 PN", "1511F-1 PN", "6ES7 ")[0]
    _, added = change(h, "hardware_add", {"typeIdentifier": fcpu["typeIdentifier"], "deviceName": STATION, "itemName": "TiaMcpFPlc",
                                          "configureScratchSecurity": True, "failsafeRuntimeAccess": True}, {})
    grant = [cpu["runtimeUser"] for cpu in added["scratchSecurity"]["cpus"] if cpu["runtimeUser"].get("supported")]
    require(len(grant) == 1 and "ailsafe" in (grant[0]["functionRightIdentifier"] + grant[0]["functionRightName"]).replace("-", ""),
            "The F-CPU's scratch user did not receive the fail-safe right: %s" % grant)
    h.report["failsafeGrant"] = {k: grant[0][k] for k in ("cpuName", "username", "roleName", "functionRightIdentifier", "functionRightName", "secretReturned")}
    run.plc = next(p["path"] for p in h.tool("project_inventory", {"maxItems": 2000})["plcs"] if STATION in p["path"])
    run.fdi = topology(h, {"plcPath": run.plc})
    require(sorted(run.fdi) == [FDI, FDI2], "Both F-DI modules are required: %s" % run.fdi)
    run.current = run.b0 = run.snap("baseline")
    owned, modules = reads(run, run.standard)
    di, di2 = owned[FDI], owned[FDI2]
    refusals(run, di, di2, modules)
    fparameter(run, di, modules, "move %s's F-destination address" % FDI)
    import_main(run, "TiaMcp safety import", "re-import %s with a block comment" % MAIN, stale=True)
    reopen(run)
    fparameter(run, di, modules, "move %s's F-destination address again" % FDI, restore=True)
    import_main(run, "TiaMcp safety import again", "re-import %s with another comment" % MAIN, restore=True)
    gaps = coverage_gaps(run.coverage, KINDS)
    require(not gaps, "Kinds without applied, persisted, refused and restored evidence: %s" % gaps)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "sf_", "D14 safety rows in one owned scratch with an F-CPU 1511F-1 PN", LIMITATIONS, []).run(body)
