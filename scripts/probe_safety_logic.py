"""Probe the behavior of changed F-logic on an F-CPU in PLCSIM Advanced V8 (safety.logic_changes).

Fixture: an owned scratch with a CPU 1511F-1 PN added with configureScratchSecurity and failsafeRuntimeAccess (its generated
scratch user holds Full access including fail-safe; scratch only) and probe_safety_kinds' ET 200SP station with F-DI
8x24VDC HF; project simulation enabled after both PLCs exist (its apply must compile every device); standard tags SafeOut
(%M100.0) and SafeAlive (%M100.1) in the F-PLC's default tag table. Channel 0 of the F-DI is read by its absolute input
address: a generic tag on an F-I/O address becomes a safety tag, which tag_create refuses (SafetyBoundary, run sl3).

The changed F-logic replaces the empty network of the generated main safety block Main_Safety_RTG1 through
change.safety_import_xml with two F-FBD networks, SafeOut := %I<start>.0 and SafeAlive := TRUE, applied with checkpoint, device
compile (the safety program included) and save. The F-PLC is then downloaded to an owned PLCSIM instance
(simulation_download with the F-CPU's generated credential), provisioned, powered on and put to RUN. SafeAlive TRUE shows the
changed F-logic runs; sim_area_write then drives the F-DI channel-0 input bit TRUE, FALSE and TRUE, and SafeOut must follow,
with the F-I/O DB's members read at each step.

Whatever V8 or V21 refuses becomes the recorded boundary: an unknown PLCSIM CPU type, a download configuration the worker does
not answer (UnsupportedDownloadConfiguration names its type), a CPU that does not reach RUN, or an F-input that stays at its
fail-safe value while the F-I/O DB reports QBAD. The safety program has no password; if one were set, safety_import_xml
refuses with SafetyLoginRequired (the worker never accepts a safety password) and the run stops there. No safety acceptance.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import re
import time
import uuid
import xml.etree.ElementTree as ElementTree

from block_test_runtime import save_generated_scratch_project, verify_credential_provisioning, verify_download, verify_owned_instance
from library_native_harness import Harness, require
from probe_safety_kinds import FDI, STATION, catalog, change, topology
from qualify_drives import outcome

MAIN = "Main_Safety_RTG1"
TAGS = {"SafeOut": "%M100.0", "SafeAlive": "%M100.1"}
CPU_TYPES = ("CPU1511F", "CPU1500_Unspecified")
FLG = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"
LIMITATIONS = [
    "One CPU 1511F-1 PN with one ET 200SP F-DI 8x24VDC HF and one owned PLCSIM Advanced V8 instance on Softbus; F-outputs, other "
    "F-I/O, F-runtime groups and F-blocks other than the generated main safety block are not exercised.",
    "Behavior is observed through standard bit memory the F-program writes and the F-DI's process-image input written by "
    "sim_area_write; PROFIsafe itself, passivation and reintegration timing are not qualified.",
    "Nothing validates, accepts or approves the safety program; no safety login, password, activation test or physical download.",
]


def network(uid, source, target):
    """One F-FBD network: target := source, where source is the literal TRUE, an input bit offset (int, absolute %I) or a tag name."""
    if source == "TRUE":
        access = '<Access Scope="LiteralConstant" UId="%d"><Constant><ConstantType>Bool</ConstantType><ConstantValue>TRUE</ConstantValue></Constant></Access>' % uid
    elif type(source) is int:
        # V21 accepts an absolute operand only under Scope="Address" (sl4: "The element Address has an invalid scope GlobalVariable").
        access = '<Access Scope="Address" UId="%d"><Address Area="Input" Type="Bool" BitOffset="%d" /></Access>' % (uid, source)
    else:
        access = '<Access Scope="GlobalVariable" UId="%d"><Symbol><Component Name="%s" /></Symbol></Access>' % (uid, source)
    return ('<NetworkSource><FlgNet xmlns="%s"><Parts>%s<Access Scope="GlobalVariable" UId="%d"><Symbol><Component Name="%s" /></Symbol></Access>'
            '<Part Name="Coil" UId="%d" /></Parts><Wires><Wire UId="%d"><IdentCon UId="%d" /><NameCon UId="%d" Name="in" /></Wire>'
            '<Wire UId="%d"><IdentCon UId="%d" /><NameCon UId="%d" Name="operand" /></Wire></Wires></FlgNet></NetworkSource>'
            % (FLG, access, uid + 1, target, uid + 2, uid + 3, uid, uid + 2, uid + 4, uid + 1, uid + 2))


def changed_main(xml, source, target, alive):
    """The generated main safety block with its one empty F-FBD network replaced by target := source and a second network
    alive := TRUE, in a second compile unit with fresh IDs."""
    units = re.findall(r'<SW\.Blocks\.CompileUnit ID="[^"]+" CompositionName="CompileUnits">.*?</SW\.Blocks\.CompileUnit>', xml, re.S)
    require(len(units) == 1 and units[0].count("<NetworkSource />") == 1 and "<ProgrammingLanguage>F_FBD</ProgrammingLanguage>" in units[0],
            "The main safety block must hold exactly one empty F-FBD network")
    ids = [int(i, 16) for i in re.findall(r' ID="([0-9A-Fa-f]+)"', xml)]
    second = units[0].replace("<NetworkSource />", network(21, "TRUE", alive))
    for old, new in zip(re.findall(r' ID="([0-9A-Fa-f]+)"', units[0]), range(max(ids) + 1, max(ids) + 100)):
        second = second.replace(' ID="%s"' % old, ' ID="%X"' % new, 1)
    first = units[0].replace("<NetworkSource />", network(21, source, target))
    result = xml.replace(units[0], first + second)
    ElementTree.fromstring(result.encode("utf-8"))  # well-formed
    return result


def input_start(rows, module):
    """The input start byte of the F-DI module's process image, from hardware_inspect rows of its subtree."""
    starts = {a["startAddress"] for r in rows if r["path"] == module or r["path"].startswith(module + "/") for a in r.get("addresses") or [] if a.get("ioType") == "Input"}
    require(len(starts) == 1, "Expected one input start address under %s: %s" % (module, sorted(starts)))
    return starts.pop()


def classify(observation):
    """The behavior outcome of one PLCSIM observation: steps of {written, safeOut, qbad}, plus alive."""
    steps = observation["steps"]
    if observation.get("alive") is not True:
        return "changedLogicNotObserved"
    if all(step["safeOut"] == step["written"] for step in steps):
        return "behaviorQualified"
    if any(step.get("qbad") is True for step in steps):
        return "fIoPassivated"
    return "inputInjectionIneffective"


class Lab:
    def __init__(self, h, plc):
        self.h, self.plc, self.instance, self.symbols = h, plc, None, {}

    def read(self, *names):
        rows = self.h.tool("sim_read", {"instanceId": self.instance["id"], "signals": [{"symbol": self.symbols[n], "type": "bool"} for n in names]})["values"]
        return {n: (r["value"] if r.get("quality") == "good" else {"quality": r.get("quality")}) for n, r in zip(names, rows)}


def online_route(h, lab, ticket):
    """The online route (change.simulation_download_online) to the same owned instance, as the bounded follow-up when the
    memory card was refused. It answers only its reviewed download configurations: V21's SafetyProgram configuration ('Load
    safety program to device') is a safety confirmation and refuses as UnsupportedDownloadConfiguration; nothing is answered
    beyond that. Returns the prepare or apply refusal, or the applied report."""
    payload = {"downloadTicket": ticket["downloadTicket"], "credentialMetadataPath": ticket["credentialMetadataPath"],
               "softwareScope": "all", "dataBlockValues": "reinitialize", "allowStop": True}
    error, prepared = outcome(h, "change_prepare", {"kind": "simulation_download_online", "plcPath": lab.plc, "payload": payload})
    if error:
        return {"stage": "prepare", "error": error}
    error, applied = outcome(h, "change_apply", {**h.apply_args(prepared), "compile": False, "save": False})
    readbacks = (applied or {}).get("readbacks") or ((error or {}).get("details") or {}).get("readbacks") or []
    report = next((r["report"] for r in readbacks if r.get("role") == "owned-simulation-download"), {})
    return {"stage": "apply", "error": error, "downloadConfigurations": report.get("downloadConfigurations"),
            "onlineChallenges": report.get("onlineChallenges"), "failure": report.get("failure"), "completed": report.get("completed")}


def boundary(h, stage, error):
    h.report["boundary"] = {"stage": stage, "error": error}
    h.report["outcome"] = "boundary"


def simulation_compiled(compile, devices):
    """project_simulation_enable's compile receipt must cover every device of the project, with no error: the setting changes
    how every PLC compiles (sl1 refused AmbiguousTarget and sl2 FingerprintUnavailable while it compiled one PLC)."""
    compiled = {target.get("targetPath") for target in compile.get("targets") or []}
    require(compile.get("errorCount") == 0 and compile.get("targetIdentityPolicy") == "device-path-v1" and set(devices) <= compiled,
            "project_simulation_enable did not compile every device without error: %s" % {k: compile.get(k) for k in ("errorCount", "requiredTargets")})
    return sorted(compiled)


def body(h):
    standard = h.owned_scratch("safety logic")
    fcpu = h.report["fCpu"] = catalog(h, "1511F-1 PN", "1511F-1 PN", "6ES7 ")[0]
    _, added = change(h, "hardware_add", {"typeIdentifier": fcpu["typeIdentifier"], "deviceName": STATION, "itemName": "TiaMcpFPlc",
                                          "configureScratchSecurity": True, "failsafeRuntimeAccess": True}, {})
    credentials = [c for c in added["scratchSecurity"]["cpus"] if c.get("credentialMetadataPath")]
    require(len(credentials) == 1, "Expected the F-CPU's one generated scratch credential: %s" % [c.get("cpuName") for c in added["scratchSecurity"]["cpus"]])
    plc = next(p["path"] for p in h.tool("project_inventory", {"maxItems": 2000})["plcs"] if STATION in p["path"])
    modules = topology(h, {"plcPath": plc})
    rows = [row for item in h.tool("project_inventory", {"maxItems": 2000})["hardware"] if item["path"].startswith(modules[FDI])
            for row in h.tool("hardware_inspect", {"hardwarePath": item["path"], "maxItems": 2000})["items"]]
    start = h.report["fdiInputStart"] = input_start(rows, modules[FDI])
    # Enabled with both PLCs in the project and no plcPath: its apply must compile every device.
    _, enabled = change(h, "project_simulation_enable", {"enabled": True}, {})
    h.report["simulationCompile"] = simulation_compiled(enabled["compile"], [p.split("/items/", 1)[0] for p in (standard, plc)])
    safety = h.stage("safety_inspect of the F-PLC", lambda: h.tool("safety_inspect", {"plcPath": plc, "maxItems": 2000}))
    h.report["safetyProgram"] = {k: safety.get(k) for k in ("isPasswordSet", "isLoggedOn")}
    tables = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["tagTables"]
    default = [t for t in tables if any(c.get("kind") == "system" for c in t.get("constants", []))]
    require(len(default) == 1, "Expected one default tag table: %s" % [t["name"] for t in tables])
    for name, address in TAGS.items():
        h.apply("tag_create", {"tagTablePath": default[0]["path"], "name": name, "dataTypeName": "Bool", "logicalAddress": address})

    xml = h.tool("block_read", {"plcPath": plc, "blockName": MAIN})["xml"]
    payload = {"xml": changed_main(xml, start * 8, "SafeOut", "SafeAlive"), "expectedBlockNames": [MAIN], "safetyAcknowledged": True}
    def imported():
        error, prepared = outcome(h, "change_prepare", {"kind": "safety_import_xml", "payload": payload, "plcPath": plc})
        if error:
            return {"outcome": "refusedAtPrepare", "error": error}
        error, applied = outcome(h, "change_apply", h.apply_args(prepared))
        if error:
            if h.unresolved_admissions():
                h.reconcile_admission("release the failed F-logic import")
            status = h.tool("change_status", {"changeId": prepared["changeId"]})
            restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
            return {"outcome": "failedAtApply", "error": {k: error.get(k) for k in ("code", "message")}, "compile": status.get("compile"),
                    "readbacks": status.get("readbacks"), "restored": restored["state"]}
        return {"outcome": "applied", "stages": applied["stages"], "compile": applied.get("compile"), "safetyAfter": applied.get("safetyAfter")}
    result = h.report["import"] = h.stage("safety_import_xml of the changed F-logic", imported)
    if result["outcome"] != "applied":
        return boundary(h, "safety_import_xml", result)
    simulate(h, Lab(h, plc), credentials[0], start)


def simulate(h, lab, credential, start):
    attempts = h.report["simCreate"] = []
    created = None
    for cpu_type in CPU_TYPES:
        error, created = outcome(h, "sim_create", {"cpuType": cpu_type, "creationId": uuid.uuid4().hex})
        attempts.append({"cpuType": cpu_type, "error": error, "instance": None if error else created["instance"]})
        if not error:
            break
    if attempts[-1]["error"]:
        return boundary(h, "sim_create", attempts)
    lab.instance = verify_owned_instance(created["instance"])
    try:
        ticket = {"downloadTicket": created["downloadTicket"], "plcPath": lab.plc, "credentialMetadataPath": credential["credentialMetadataPath"]}
        error, download = outcome(h, "simulation_download", ticket)
        if error:
            boundary(h, "simulation_download", error)
            h.report["onlineRoute"] = h.stage("simulation_download_online after the refused memory card", lambda: online_route(h, lab, ticket))
            return
        verify_download(download, lab.instance)
        h.report["download"] = {k: download.get(k) for k in ("configurations", "compilation", "download", "programRevision")}
        h.report["generatedProjectSave"] = save_generated_scratch_project(h.tool, download)
        provisioned = h.stage("simulation_provision_scratch_credential", lambda: h.tool("simulation_provision_scratch_credential",
                              {**{k: ticket[k] for k in ("downloadTicket", "credentialMetadataPath")}, "plcPath": lab.plc, "trustOwnedEmptyCpuCertificate": True}))
        h.report["credentialProvisioning"] = verify_credential_provisioning(provisioned, download, trust_owned_empty_cpu_certificate=True)
        for action, timeout in (("powerOn", 60000), ("run", 30000)):
            error, state = outcome(h, "sim_state", {"instanceId": lab.instance["id"], "action": action, "timeoutMs": timeout})
            if error or action == "run" and state["instance"]["operatingState"] != "Run":
                observed = h.tool("sim_observe", {"instanceId": lab.instance["id"]})
                return boundary(h, "sim_state " + action, {"error": error, "state": state if not error else None, "observe": observed})
        tags = h.tool("sim_tags", {"instanceId": lab.instance["id"], "maxItems": 2000})["tags"]
        for name in TAGS:
            found = [t for t in tags if t["symbol"].replace('"', "") == name]
            require(len(found) == 1, "PLCSIM tag %s is missing or ambiguous" % name)
            lab.symbols[name] = found[0]["symbol"]
        db = h.tool("safety_validation_inspect", {"plcPath": lab.plc, "maxItems": 2000})
        names = [r.get("value") for m in db["fIoModules"] if "/" + FDI in m["path"] for r in m["fParameters"] if r["name"] == "Failsafe_FIODBName"]
        members = [t for t in tags if names and t["symbol"].replace('"', "").startswith(names[0] + ".") and t.get("dataType") == "Bool"][:32]
        lab.symbols.update({t["symbol"]: t["symbol"] for t in members})
        observation = {"alive": lab.read("SafeAlive")["SafeAlive"], "fIoDb": names[:1], "steps": []}
        for value in (True, False, True):
            h.tool("sim_area_write", {"instanceId": lab.instance["id"], "area": "input", "offset": start, "bit": 0, "value": value})
            time.sleep(1.0)
            read = lab.read("SafeOut", *(t["symbol"] for t in members))
            read["inputImage"] = h.tool("sim_area_read", {"instanceId": lab.instance["id"], "area": "input", "offset": start, "bit": 0})["value"]
            qbad = [v for k, v in read.items() if k.replace('"', "").endswith(".QBAD")]
            observation["steps"].append({"written": value, "safeOut": read["SafeOut"], "inputImage": read["inputImage"], "qbad": qbad[0] if qbad else None, "fIoDb": read})
        h.report["observation"] = observation
        h.report["outcome"] = classify(observation)
        for action in ("stop", "powerOff"):
            h.tool("sim_state", {"instanceId": lab.instance["id"], "action": action, "timeoutMs": 60000})
    finally:
        h.stage("sim_release", lambda: h.tool("sim_release", {"instanceId": lab.instance["id"], "deleteOwned": True}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--sim-worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "sl_", "safety.logic_changes: changed F-logic of an F-CPU 1511F-1 PN in PLCSIM Advanced V8", LIMITATIONS, []).run(body)
