"""Qualify the PLCSIM gaps of 29 October 2026 on one owned PLCSIM Advanced V8 instance: the online download of a changed
program (change.simulation_download_online), virtual time (sim_time, sim_test virtualTime), interrupt and fault injection
(sim_event: OB40, OB82, OB83, OB86), memory reset (sim_state memoryReset) and storage archive/retrieve (sim_storage).

The fixture is an owned scratch CPU 1511-1 PN with simulation support, a central DI module whose channel 0 rising-edge
hardware interrupt is enabled and linked to OB40 (change.hardware_channel_attributes), a PLC tag on that input, an ET 200SP
station on the CPU's IO system, a global DB GapDb with one counter per interrupt OB, an IEC timer and the CPU clock, the
interrupt OBs (imported with their explicit numbers and event classes) and Main. The program is downloaded to a fresh
owned instance, provisioned and started like the D09 runner. Every injected event must raise its OB's counter; a rising
edge written to the input is recorded. After the instance is released, channel 1's interrupt is enabled and disabled
again, and the disabling change is restored from its checkpoint.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import time
import uuid
from xml.sax.saxutils import escape

from block_test_runtime import save_generated_scratch_project, verify_credential_provisioning, verify_download, verify_memory_card, verify_owned_instance
from library_native_harness import Harness, require

DB = "GapDb"
DB_SOURCE = '''DATA_BLOCK "GapDb"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
NON_RETAIN
   VAR
      Start : Bool;
      TimerDone : Bool;
      ProgramVersion : Int;
      Ob40 : DInt;
      Ob82 : DInt;
      Ob83 : DInt;
      Ob86 : DInt;
      Year : UInt;
      Clock : DTL;
      Delay : TON_TIME;%s
   END_VAR

BEGIN

END_DATA_BLOCK

'''
MAIN_SOURCE = '''ORGANIZATION_BLOCK "Main"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_TEMP
      Status : Int;
   END_VAR

BEGIN
    "GapDb".Delay(IN := "GapDb".Start, PT := T#5S, Q => "GapDb".TimerDone);
    #Status := RD_SYS_T(OUT => "GapDb".Clock);
    "GapDb".Year := "GapDb".Clock.YEAR;
    "GapDb".ProgramVersion := %d;
END_ORGANIZATION_BLOCK

'''
# number -> block name, Openness SecondaryType, GapDb counter, informative S7-1500 start information.
OBS = {
    40: ("GapHardwareInterrupt", "HardwareInterrupt", "Ob40", [("LADDR", "HW_IO"), ("USI", "Word"), ("IChannel", "USInt"), ("EventType", "Byte")]),
    82: ("GapDiagnosticError", "DiagnosticErrorInterrupt", "Ob82", [("IO_State", "Word"), ("LADDR", "HW_ANY"), ("Channel", "UInt"), ("MultiError", "Bool")]),
    83: ("GapPullOrPlug", "PullOrPlugOfModules", "Ob83", [("LADDR", "HW_IO"), ("Event_Class", "Byte"), ("Fault_ID", "Byte")]),
    86: ("GapRackFailure", "RackOrStationFailure", "Ob86", [("LADDR", "HW_IO"), ("Event_Class", "Byte"), ("Fault_ID", "Byte")]),
}
DI_ARTICLE, DI_FILTER = "6ES7 521-1BH00", "DI 16x24VDC HF"
# The DI 16x24VDC HF V1.0 channel attributes of its rising-edge hardware interrupt (V21 hardware parameters manual).
INTERRUPT_ACTIVE, INTERRUPT_OB = "HardwareInterruptRisingEdgeActive", "HardwareInterruptRisingEdgeInterrupt"
INPUT_TAG = "GapIn0"
LIMITATIONS = [
    "One owned PLCSIM Advanced V8 CPU 1511 instance on Softbus of an owned scratch CPU 1511-1 PN V4.1, provisioned with first-use TLS trust under the owned-empty-CPU policy like the D09 runner.",
    "Events are injected on the fixture's central DI module and ET 200SP station; other modules, channels, event types and OBs are not exercised.",
    "The hardware interrupt is configured on DI 16x24VDC HF V1.0 channel 0 (rising edge, OB40 linked by name); falling edges, priorities and other modules are not exercised.",
    "Virtual time is checked with one IEC TON in OB1 and the CPU clock read with RD_SYS_T; scan-exact timing is not claimed.",
]


def ob_xml(number):
    """A Openness OB document with its explicit number and event class and an SCL body that counts its calls."""
    name, secondary, counter, inputs = OBS[number]
    members = "".join('<Member Name="%s" Datatype="%s" Accessibility="Public" Informative="true" />' % pair for pair in inputs)
    def symbol(uid):
        return ('<Access Scope="GlobalVariable" UId="%d"><Symbol UId="%d"><Component Name="%s" UId="%d"><BooleanAttribute Name="HasQuotes" UId="%d">true</BooleanAttribute></Component>'
                '<Token Text="." UId="%d" /><Component Name="%s" UId="%d" /></Symbol></Access>') % (uid, uid + 1, DB, uid + 2, uid + 3, uid + 4, counter, uid + 5)
    body = (symbol(21) + '<Blank UId="27" /><Token Text=":=" UId="28" /><Blank UId="29" />' + symbol(30) + '<Blank UId="36" /><Token Text="+" UId="37" /><Blank UId="38" />'
            '<Access Scope="LiteralConstant" UId="39"><Constant UId="40"><ConstantValue UId="41">1</ConstantValue></Constant></Access><Token Text=";" UId="42" />')
    return ('<?xml version="1.0" encoding="utf-8"?><Document><Engineering version="V21" /><SW.Blocks.OB ID="0"><AttributeList>'
            '<AutoNumber>false</AutoNumber><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">'
            '<Section Name="Input">' + members + '</Section><Section Name="Temp" /><Section Name="Constant" /></Sections></Interface>'
            '<MemoryLayout>Optimized</MemoryLayout><Name>' + escape(name) + '</Name><Namespace /><Number>%d</Number><ProgrammingLanguage>SCL</ProgrammingLanguage>'
            '<SecondaryType>%s</SecondaryType></AttributeList><ObjectList><SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits"><AttributeList><NetworkSource>'
            '<StructuredText xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4">' % (number, secondary) + body +
            '</StructuredText></NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.OB></Document>')


def latest(entries, article):
    rows = sorted([e for e in entries if article in (e.get("articleNumber") or "")], key=lambda e: e["typeIdentifier"], reverse=True)
    require(rows, "No installed catalog entry for " + article)
    return rows[0]


def plant(h, plc, obs):
    """GapDb, Main and the interrupt OBs whose fixture exists, each with checkpoint, compile and save."""
    h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": DB_SOURCE % "", "expectedBlockNames": [DB]})
    for number in obs:
        h.apply("block_import_xml", {"scopePath": plc, "xml": ob_xml(number), "expectedBlockNames": [OBS[number][0]]})
    h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": MAIN_SOURCE % 1, "expectedBlockNames": ["Main"]})


def rack(h, device):
    rows = [r["path"] for r in h.tool("project_inventory", {"maxItems": 1000})["hardware"] if r["path"].startswith(device + "/items/") and r["path"].count("/items/") == 1
            and r.get("positionNumber") == 0]
    require(len(rows) == 1, "Expected one rack of the scratch station: %s" % rows)
    return rows[0]


def hardware_ids(h, plc):
    """System constant name -> hardware identifier, from the PLC's system constants."""
    tables = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["tagTables"]
    # V21 reports each system constant's hardware identifier as a decimal string.
    return {c["name"]: int(c["value"]) for table in tables for c in table.get("constants", []) if c.get("kind") == "system" and str(c.get("value")).isdigit()}


class Lab:
    def __init__(self, h):
        self.h, self.symbols = h, {}

    def resolve(self, names):
        tags = self.h.tool("sim_tags", {"instanceId": self.instance["id"], "filter": DB, "maxItems": 2000})["tags"]
        for name in names:
            found = [t["symbol"] for t in tags if t["symbol"].replace('"', "") == DB + "." + name]
            require(len(found) == 1, "PLCSIM symbol %s.%s is missing or ambiguous" % (DB, name))
            self.symbols[name] = found[0]
        return self.symbols

    def refresh(self):
        """PLCSIM answers NotUpToDate (-47) after a program change until its tag list is updated (observed 29 October 2026)."""
        return self.resolve(list(self.symbols))

    def signal(self, name, kind):
        return {"symbol": self.symbols[name], "type": kind}

    def read(self, **signals):
        rows = self.h.tool("sim_read", {"instanceId": self.instance["id"], "signals": [self.signal(n, k) for n, k in signals.items()]})["values"]
        require(all(r["quality"] == "good" for r in rows), "Bad PLCSIM read: %s" % rows)
        return {n: r["value"] for n, r in zip(signals, rows)}

    def write(self, name, kind, value):
        self.h.tool("sim_write", {"instanceId": self.instance["id"], "writes": [{**self.signal(name, kind), "value": value}]})

    def state(self, action, timeout=60000):
        result = self.h.tool("sim_state", {"instanceId": self.instance["id"], "action": action, "timeoutMs": timeout})
        return result["instance"]["operatingState"]

    def counters(self):
        return self.read(Ob40="int32", Ob82="int32", Ob83="int32", Ob86="int32")


def body(h):
    plc = h.owned_scratch("plcsim gaps")
    cpu = plc.rsplit("/plc/", 1)[0]
    device = cpu.split("/items/", 1)[0]
    lab = Lab(h)
    cpus = h.scratch["scratchSecurity"]["cpus"]
    require(len(cpus) == 1 and cpus[0]["runtimeUser"].get("supported") is True, "Exactly one scratch CPU with a generated runtime user is required")
    secret, login = cpus[0]["credentialMetadataPath"], cpus[0]["runtimeUser"]["credentialMetadataPath"]
    h.apply("project_simulation_enable", {"enabled": True})

    # Hardware fixture: a central DI module and an ET 200SP station; the DI channel 0 hardware interrupt follows the OBs.
    di = latest(h.tool("hardware_catalog_search", {"filter": DI_FILTER, "maxItems": 200})["entries"], DI_ARTICLE)
    h.apply("hardware_plug", {"hardwarePath": rack(h, device), "typeIdentifier": di["typeIdentifier"], "name": "GapDI", "positionNumber": 2})
    module = next(r["path"] for r in h.tool("project_inventory", {"maxItems": 1000})["hardware"] if r["path"].endswith("/items/GapDI"))
    obs, boundaries = [40, 82, 83], {}
    station = remote_station(h, plc, cpu)
    if station:
        obs.append(86)
    else:
        boundaries["OB86"] = h.report.get("remoteStation")
    h.report["boundaries"] = boundaries
    plant(h, plc, sorted(obs))
    lab.interrupt = configure_interrupt(h, plc, module)
    ids = hardware_ids(h, plc)
    h.report["hardwareIds"] = ids

    h.stage("discovery baseline", lambda: h.tool("simulation_discovery_baseline", {})["baseline"])
    created = h.stage("sim_create", lambda: h.tool("sim_create", {"cpuType": "CPU1511", "creationId": uuid.uuid4().hex}))
    # The ticket expires 15 minutes after sim_create; online steps start only with two minutes to spare.
    lab.deadline = time.monotonic() + 13 * 60
    lab.instance = verify_owned_instance(created["instance"])
    try:
        ticket = created["downloadTicket"]
        card = {"downloadTicket": ticket, "credentialMetadataPath": secret}
        download = h.stage("simulation_download", lambda: h.tool("simulation_download", card))
        verify_download(download, lab.instance)
        h.report["memoryCard"] = verify_memory_card(download)
        h.report["generatedProjectSave"] = save_generated_scratch_project(h.tool, download)
        provisioned = h.stage("simulation_provision_scratch_credential", lambda: h.tool("simulation_provision_scratch_credential", {**card, "trustOwnedEmptyCpuCertificate": True}))
        h.report["credentialProvisioning"] = verify_credential_provisioning(provisioned, download, trust_owned_empty_cpu_certificate=True)
        h.stage("power on", lambda: lab.state("powerOn"))
        require(h.stage("run", lambda: lab.state("run", 30000)) == "Run", "The instance did not enter RUN")
        h.stage("resolve GapDb in PLCSIM", lambda: lab.resolve(["Start", "TimerDone", "ProgramVersion", "Ob40", "Ob82", "Ob83", "Ob86", "Year"]))
        require(h.stage("program version 1 runs", lambda: lab.read(ProgramVersion="int16"))["ProgramVersion"] == 1, "Version 1 is not running")

        online_downloads(h, lab, plc, ticket, login, secret)
        virtual_time(h, lab)
        events(h, lab, ids, module, station, obs)
        memory_reset(h, lab)
        storage(h, lab)
    finally:
        released = h.stage("sim_release", lambda: h.tool("sim_release", {"instanceId": lab.instance["id"], "deleteOwned": True}))
        require(released.get("unregistered") is True, "The owned instance was not released")
    restore_interrupt(h, lab.interrupt)


def interrupt_channel(h, module, number):
    """The digital input channel of the DI module's subtree, with its interrupt attribute metadata from hardware_inspect."""
    items = {}
    for row in h.tool("project_inventory", {"maxItems": 1000})["hardware"]:
        if row["path"] == module or row["path"].startswith(module + "/"):
            for item in h.tool("hardware_inspect", {"hardwarePath": row["path"], "maxItems": 2000})["items"]:
                items[item["path"]] = item
    rows = [(item, channel) for item in items.values() for channel in item.get("channels") or [] if channel["number"] == number and channel["ioType"] == "Input"]
    require(len(rows) == 1, "Expected one input channel %d under GapDI: %s" % (number, [(i["path"], c.get("type")) for i, c in rows]))
    item, channel = rows[0]
    attributes = {a["name"]: a for a in channel.get("attributes", [])}
    selector = {"hardwarePath": item["path"], "channelType": channel["type"], "channelIoType": "Input", "channelNumber": number}
    return item, selector, {name: attributes.get(name) for name in (INTERRUPT_ACTIVE, INTERRUPT_OB)}


def interrupt_payload(selector, attributes, ob=OBS[40][0]):
    native = (attributes.get(INTERRUPT_ACTIVE) or {}).get("nativeValueType")
    # The manual lists a Boolean for this module; V21 also declares an enum of the same name for other modules.
    return {**selector, "attributes": {INTERRUPT_ACTIVE: "Active" if native == "Siemens.Engineering.HW." + INTERRUPT_ACTIVE else True, INTERRUPT_OB: ob}}


def configure_interrupt(h, plc, module):
    """Enable channel 0's rising-edge hardware interrupt and link OB40, then tag the input; V21's refusal fails the run with its exact text."""
    item, selector, attributes = h.stage("inspect the DI channel 0 interrupt attributes", lambda: interrupt_channel(h, module, 0))
    h.report["interruptChannel"] = {"selector": selector, "attributes": attributes, "addresses": item.get("addresses")}
    payload = interrupt_payload(selector, attributes)
    kind = "hardware_channel_attributes"
    prepared = h.prepare(kind, payload)
    changes = {row["name"]: row for row in prepared["preview"]["attributeChanges"]}
    require(set(changes) == {INTERRUPT_ACTIVE, INTERRUPT_OB} and changes[INTERRUPT_OB]["after"] == OBS[40][0], "Interrupt preview: %s" % changes)
    h.refuse("refuse an OB link to a diagnostic error OB", kind, interrupt_payload(selector, attributes, OBS[82][0]), "InvalidRequest", "not a hardware interrupt OB")
    h.refuse("refuse an OB link to a missing block", kind, interrupt_payload(selector, attributes, "GapMissingOb"), "BlockNotFound")
    h.refuse("refuse a non-string OB link", kind, {**payload, "attributes": {INTERRUPT_OB: 40}}, "InvalidRequest")
    h.refuse("refuse enabling the interrupt without its OB", kind, {**payload, "attributes": {INTERRUPT_ACTIVE: payload["attributes"][INTERRUPT_ACTIVE]}}, "InvalidRequest", "requires")
    applied = h.apply_prepared(kind, prepared)
    require("configured_hardware_preservation_verified" in applied["stages"], "Interrupt preservation stage missing: %s" % applied["stages"])
    # The worker verified the OB link by name; hardware_inspect's rendering of the linked object is recorded, the flag asserted.
    after = h.stage("read back the DI channel 0 interrupt", lambda: interrupt_channel(h, module, 0)[2])
    require((after[INTERRUPT_ACTIVE] or {}).get("value") in (True, "Active"), "Interrupt flag readback: %s" % after)
    inputs = [a for a in item.get("addresses") or [] if a.get("ioType") == "Input"]
    require(len(inputs) == 1, "Expected one input address range on the channel item: %s" % item.get("addresses"))
    tables = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["tagTables"]
    default = [t for t in tables if any(c.get("kind") == "system" for c in t.get("constants", []))]
    require(len(default) == 1, "Expected one default tag table: %s" % [t["name"] for t in tables])
    address = "%%I%d.0" % inputs[0]["startAddress"]
    h.apply("tag_create", {"tagTablePath": default[0]["path"], "name": INPUT_TAG, "dataTypeName": "Bool", "logicalAddress": address})
    h.report["hardwareInterrupt"] = {"selector": selector, "preview": prepared["preview"]["attributeChanges"], "stages": applied["stages"], "readback": after, "inputTag": {INPUT_TAG: address}}
    return {"module": module, "plc": plc}


def restore_interrupt(h, interrupt):
    """Channel 1: enable and link, then disable (V21 then undeclares the OB link); the disabling change is restored from its
    checkpoint, and the restored hardware manifest must equal the enabled one."""
    def run():
        kind = "hardware_channel_attributes"
        _, selector, before = interrupt_channel(h, interrupt["module"], 1)
        payload = interrupt_payload(selector, before)
        h.apply(kind, payload)
        enabled = interrupt_channel(h, interrupt["module"], 1)[2]
        require(enabled[INTERRUPT_OB] is not None, "Enabling did not declare the OB link: %s" % enabled)
        flag = payload["attributes"][INTERRUPT_ACTIVE]
        h.refuse("refuse disabling the interrupt with an OB link", kind, {**selector, "attributes": {INTERRUPT_ACTIVE: False if flag is True else "Deactivated", INTERRUPT_OB: OBS[40][0]}}, "InvalidRequest", "omit")
        prepared = h.prepare(kind, {**selector, "attributes": {INTERRUPT_ACTIVE: False if flag is True else "Deactivated"}})
        disabling = h.apply_prepared(kind, prepared)
        require("configured_hardware_preservation_verified" in disabling["stages"], "Disable preservation stage missing: %s" % disabling["stages"])
        disabled = interrupt_channel(h, interrupt["module"], 1)[2]
        require(disabled[INTERRUPT_OB] is None and disabled[INTERRUPT_ACTIVE]["value"] in (False, "Deactivated"), "Disabling left the interrupt: %s" % disabled)
        restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        require(restored["state"] == "restored" and "configured_hardware_recovery_verified" in restored["stages"], "Interrupt restore: %s" % restored["stages"])
        back = interrupt_channel(h, interrupt["module"], 1)[2]
        require(back == enabled, "The restored channel 1 interrupt differs from its prechange state: %s -> %s" % (enabled, back))
        return {"selector": selector, "before": before, "enabled": enabled, "disabled": disabled, "stages": restored["stages"], "restored": back}
    h.report["hardwareInterruptRestore"] = h.stage("enable, disable and restore channel 1's hardware interrupt", run)


def online_downloads(h, lab, plc, ticket, login, secret):
    base = {"downloadTicket": ticket, "runtimeCredentialMetadataPath": login, "credentialMetadataPath": secret}
    def download(label, payload):
        require(time.monotonic() < lab.deadline, "The download ticket is about to expire before " + label)
        prepared = h.stage("prepare simulation_download_online " + label, lambda: h.tool("change_prepare", {"kind": "simulation_download_online", "plcPath": plc, "payload": {**base, **payload}}))
        row = {"kind": "simulation_download_online", "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "state": "dispatching"}
        h.report["mutations"].append(row)
        applied = h.stage("apply simulation_download_online " + label, lambda: h.tool("change_apply", {**h.apply_args(prepared), "compile": False, "save": False}))
        require(applied["state"] == "applied", "Online download did not apply")
        row["state"] = "applied"
        report = next(r["report"] for r in applied["readbacks"] if r["role"] == "owned-simulation-download")
        h.stage("refresh the PLCSIM tag list after " + label, lab.refresh)
        require(report["completed"] is True and report["operatingStateBefore"] == "Run" and report["operatingStateAfterDownload"] == "Run", "Online download report: %s"
                % {k: report.get(k) for k in ("completed", "operatingStateBefore", "operatingStateAfterDownload", "failure")})
        return prepared, applied, report

    h.refuse("refuse an online download without a ticket", "simulation_download_online", {"softwareScope": "changes", "dataBlockValues": "keepActualValues", "allowStop": False}, "PhysicalPlcRefused")
    h.refuse("refuse reinitialize without allowStop", "simulation_download_online", {**base, "softwareScope": "all", "dataBlockValues": "reinitialize", "allowStop": False}, "InvalidRequest")
    def compile_true():
        prepared = h.tool("change_prepare", {"kind": "simulation_download_online", "plcPath": plc, "payload": {**base, "softwareScope": "changes", "dataBlockValues": "keepActualValues", "allowStop": False}})
        error = h.call_error("change_apply", h.apply_args(prepared))
        require(error.get("code") == "InvalidRequest", "compile=true/save=true was not refused: %s" % error)
        return error
    h.stage("refuse an online download applied with compile and save", compile_true)
    # 1. A code change, values kept: Version follows the new program while the counters keep their actual values.
    lab.write("Ob82", "int32", 7)
    h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": MAIN_SOURCE % 2, "expectedBlockNames": ["Main"]})
    _, _, report = download("code change", {"softwareScope": "changes", "dataBlockValues": "keepActualValues", "allowStop": False})
    h.report["onlineCodeChange"] = report
    values = h.stage("version 2 runs with kept values", lambda: lab.read(ProgramVersion="int16", Ob82="int32"))
    require(values == {"ProgramVersion": 2, "Ob82": 7}, "Expected Version 2 and the kept counter 7: %s" % values)
    # 2. A DB layout change with keepActualValues: TIA either keeps the values or demands a reinitialization, which is
    # refused without allowStop. Either way a further layout change then downloads reinitialized, with a stop and restart.
    h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": DB_SOURCE % "\n      Added : Int;", "expectedBlockNames": [DB]})
    def keep_layout():
        prepared = h.tool("change_prepare", {"kind": "simulation_download_online", "plcPath": plc, "payload": {**base, "softwareScope": "changes", "dataBlockValues": "keepActualValues", "allowStop": False}})
        outcome = h.client.request("tools/call", {"name": "change_apply", "arguments": {**h.apply_args(prepared), "compile": False, "save": False}})
        content = outcome.get("structuredContent") or {}
        readbacks = content.get("readbacks") or ((content.get("error") or {}).get("details") or {}).get("readbacks") or []
        report = next((r["report"] for r in readbacks if r.get("role") == "owned-simulation-download"), {})
        lab.refresh()
        result = {"isError": outcome.get("isError"), "error": (content.get("error") or {}).get("code"), "configurations": report.get("downloadConfigurations"),
                  "operatingStateAfter": report.get("operatingStateAfter"), "values": lab.read(ProgramVersion="int16", Ob82="int32")}
        if outcome.get("isError"):
            require(result["error"] in ("SimulationDownloadReinitializationRequired", "SimulationDownloadStopRequired") and result["values"]["Ob82"] == 7,
                    "A refused keep-values download must be typed and change nothing: %s" % result)
        else:
            require(result["values"] == {"ProgramVersion": 2, "Ob82": 7}, "A kept-values layout download must keep the counter: %s" % result)
        return result
    # Each further online session needs the ticket; one that would start too late is recorded as skipped.
    if time.monotonic() >= lab.deadline:
        h.report["layoutChangeKeepValues"] = h.report["onlineLayoutChange"] = "skipped: the download ticket expires before another online session"
        return
    h.report["layoutChangeKeepValues"] = h.stage("layout change downloaded with keepActualValues", keep_layout)
    if time.monotonic() >= lab.deadline:
        h.report["onlineLayoutChange"] = "skipped: the download ticket expires before another online session"
        return
    h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": DB_SOURCE % "\n      Added : Int;\n      Again : Int;", "expectedBlockNames": [DB]})
    last, _, report = download("layout change, reinitialized", {"softwareScope": "changes", "dataBlockValues": "reinitialize", "allowStop": True})
    h.report["onlineLayoutChange"] = report
    values = h.stage("reinitialized values after the layout change", lambda: lab.read(ProgramVersion="int16", Ob82="int32"))
    require(values == {"ProgramVersion": 2, "Ob82": 0}, "Expected the reinitialized counter 0 with Version 2: %s" % values)
    # 3. Recovery: the checkpoint restores the project into a new directory; the CPU keeps the downloaded program, and the
    # recovered copy is not the project that produced the card, so it cannot download to the instance.
    def recover():
        status = h.tool("change_status", {"changeId": last["changeId"]})
        require(status["state"] == "applied" and status["recoveryArchiveSha256"], "change_status lost the checkpoint: %s" % status.get("state"))
        recovered = h.tool("change_restore", {"changeId": last["changeId"], "previewHash": last["previewHash"]})
        require(recovered["state"] == "restored" and "checkpoint_retrieved_to_new_directory" in recovered["stages"], "Checkpoint recovery was not verified: %s" % recovered["stages"])
        cpu = lab.read(ProgramVersion="int16", Ob82="int32")
        require(cpu == {"ProgramVersion": 2, "Ob82": 0}, "The CPU must keep the downloaded program: %s" % cpu)
        result = {"recoveredProject": recovered["recoveredProject"], "stages": recovered["stages"], "cpu": cpu}
        if time.monotonic() < lab.deadline:
            error = h.call_error("change_prepare", {"kind": "simulation_download_online", "plcPath": plc, "payload": {**base, "softwareScope": "changes", "dataBlockValues": "keepActualValues", "allowStop": False}})
            require(error.get("code") == "SimulationTargetMismatch", "A download from the recovered copy was not refused: %s" % error)
            result["recoveredCopyRefusal"] = error.get("code")
        return result
    h.report["recovery"] = h.stage("restore the last online download's project checkpoint", recover)
    if time.monotonic() < lab.deadline:
        # Recorded, not asserted: the recovered project predates the reinitialized layout the CPU runs.
        compare = h.stage("plc_online_compare of the recovered project", lambda: h.tool("plc_online_compare", {"downloadTicket": ticket, "plcPath": plc, "maxItems": 2000, "runtimeCredentialMetadataPath": login}))
        h.report["compareAfterRecovery"] = {"truncated": compare["compareTruncated"], "root": compare["compare"]["state"]}
    else:
        h.report["compareAfterRecovery"] = "skipped: the download ticket expires before another online session"


def virtual_time(h, lab):
    signals = [lab.signal("TimerDone", "bool")]
    steps = [{"writes": [{**lab.signal("Start", "bool"), "value": False}], "delayMs": 200},
             {"writes": [{**lab.signal("Start", "bool"), "value": True}], "delayMs": 4000, "assertions": [{**lab.signal("TimerDone", "bool"), "operator": "eq", "expected": False}]},
             {"delayMs": 1500, "assertions": [{**lab.signal("TimerDone", "bool"), "operator": "eq", "expected": True}]},
             {"writes": [{**lab.signal("Start", "bool"), "value": False}], "delayMs": 200, "assertions": [{**lab.signal("TimerDone", "bool"), "operator": "eq", "expected": False}]}]
    def scenario(virtual):
        started = time.monotonic()
        report = h.tool("sim_test", {"instanceId": lab.instance["id"], "signals": signals, "steps": steps, "intervalMs": 500, "virtualTime": virtual})
        require(report["passed"] is True, "Timer scenario failed")
        result = {"wallSeconds": round(time.monotonic() - started, 3), "elapsedMs": report["elapsedMs"], "timing": report["timing"], "reportPath": report["reportPath"]}
        if virtual:
            require(report["virtualTime"]["restoredOperatingMode"] == report["virtualTime"]["previousOperatingMode"], "The operating mode was not restored")
            result["virtualTime"] = report["virtualTime"]
        return result
    h.report["timerWallClock"] = h.stage("timer scenario on the wall clock", lambda: scenario(False))
    h.report["timerVirtual"] = h.stage("timer scenario in virtual time", lambda: scenario(True))
    h.report["timerVirtualRepeat"] = h.stage("timer scenario in virtual time, repeated", lambda: scenario(True))
    def scaled():
        h.tool("sim_time", {"instanceId": lab.instance["id"], "scaleFactor": 10})
        try:
            lab.write("Start", "bool", False)
            time.sleep(0.2)
            lab.write("Start", "bool", True)
            time.sleep(0.25)
            early = lab.read(TimerDone="bool")["TimerDone"]
            time.sleep(1.0)
            late = lab.read(TimerDone="bool")["TimerDone"]
            lab.write("Start", "bool", False)
        finally:
            after = h.tool("sim_time", {"instanceId": lab.instance["id"], "scaleFactor": 1})
        require(early is False and late is True, "At scale 10 the 5 s timer should finish within 1.25 s of wall time: %s" % [early, late])
        return {"after0.25s": early, "after1.25s": late, "restored": after["after"]}
    h.report["scaleFactor"] = h.stage("timer at scale factor 10", scaled)
    def clock():
        h.tool("sim_time", {"instanceId": lab.instance["id"], "systemTimeUtc": "2031-06-15T12:00:00Z"})
        time.sleep(0.5)
        year = lab.read(Year="uint16")["Year"]
        require(year == 2031, "RD_SYS_T reads year %s after setting 2031" % year)
        return {"year": year, "state": h.tool("sim_time", {"instanceId": lab.instance["id"]})["after"]}
    h.report["systemTime"] = h.stage("RD_SYS_T sees the set virtual system time", clock)


def events(h, lab, ids, module, station, obs):
    configured = h.stage("list configured process events", lambda: h.tool("sim_event", {"instanceId": lab.instance["id"]})["configuredProcessEvents"])
    h.report["configuredProcessEvents"] = configured
    di = ids.get("Local~GapDI")
    require(di is not None, "No system constant names the GapDI module: %s" % sorted(ids))
    def inject(label, event, counter, delta):
        def run():
            before = lab.counters()
            result = h.tool("sim_event", {"instanceId": lab.instance["id"], "event": event})
            for _ in range(50):
                after = lab.counters()
                if after[counter] >= before[counter] + delta:
                    break
                time.sleep(0.1)
            require(after[counter] == before[counter] + delta and all(after[k] == before[k] for k in after if k != counter), "%s: counters %s -> %s" % (label, before, after))
            return {"event": result["event"], "sequenceNumber": result["sequenceNumber"], "before": before, "after": after}
        return h.stage(label, run)
    results = {}
    results["OB82"] = inject("diagnostic alarm raises OB82", {"type": "diagnosticAlarm", "hardwareId": di, "moduleState": "error",
                             "channels": [{"channel": 0, "errorType": 1, "severity": "failure", "direction": "appear"}]}, "Ob82", 1)
    results["OB83"] = [inject("pull raises OB83", {"type": "pullOrPlug", "hardwareId": di, "action": "pull"}, "Ob83", 1),
                       inject("plug raises OB83", {"type": "pullOrPlug", "hardwareId": di, "action": "plug"}, "Ob83", 1)]
    process = [row for row in configured if row["channel"] == 0]
    require(len(process) == 1, "The configured channel 0 hardware interrupt is not listed once: %s" % configured)
    results["OB40"] = inject("hardware interrupt raises OB40", {"type": "hardwareInterrupt", "hardwareId": process[0]["hardwareId"], "channel": 0, "edge": "risingEdge"}, "Ob40", 1)
    def input_edge():
        """Recorded, not asserted: whether a rising edge written to the input tag itself raises OB40."""
        tag = [t["symbol"] for t in h.tool("sim_tags", {"instanceId": lab.instance["id"], "filter": INPUT_TAG, "maxItems": 50})["tags"] if t["symbol"].replace('"', "") == INPUT_TAG]
        require(len(tag) == 1, "PLCSIM symbol %s is missing or ambiguous" % INPUT_TAG)
        signal = {"symbol": tag[0], "type": "bool"}
        h.tool("sim_write", {"instanceId": lab.instance["id"], "writes": [{**signal, "value": False}]})
        time.sleep(0.5)
        before = lab.counters()
        h.tool("sim_write", {"instanceId": lab.instance["id"], "writes": [{**signal, "value": True}]})
        time.sleep(2)
        after = lab.counters()
        h.tool("sim_write", {"instanceId": lab.instance["id"], "writes": [{**signal, "value": False}]})
        return {"symbol": tag[0], "before": before, "after": after, "ob40Raised": after["Ob40"] - before["Ob40"]}
    results["OB40inputEdge"] = h.stage("record whether a rising edge written to the input raises OB40", input_edge)
    if 86 in obs:
        results["OB86"] = [inject("station failure raises OB86", {"type": "rackOrStationFault", "hardwareId": station, "action": "fault"}, "Ob86", 1),
                           inject("station return raises OB86", {"type": "rackOrStationFault", "hardwareId": station, "action": "return"}, "Ob86", 1)]
    status = h.client.request("tools/call", {"name": "sim_event", "arguments": {"instanceId": lab.instance["id"], "event": {"type": "statusEvent", "hardwareId": di, "specifier": 1}}})
    results["statusEvent"] = {"isError": status.get("isError"), "content": status.get("structuredContent")}
    def refusal():
        error = h.call_error("sim_event", {"instanceId": lab.instance["id"], "event": {"type": "hardwareInterrupt", "hardwareId": 0, "channel": 0, "edge": "risingEdge"}})
        require(error.get("code") == "InvalidRequest", "Malformed event was not refused: %s" % error)
        return error
    results["refusal"] = h.stage("refuse a malformed event before the runtime call", refusal)
    h.report["events"] = results


def memory_reset(h, lab):
    lab.write("Ob82", "int32", 5)
    def reset():
        state = lab.state("memoryReset")
        if state != "Run":
            state = lab.state("run", 30000)
        lab.refresh()
        values = lab.read(Ob82="int32", ProgramVersion="int16")
        require(state == "Run" and values == {"Ob82": 0, "ProgramVersion": 2}, "After memory reset: %s %s" % (state, values))
        return {"state": state, "values": values}
    h.report["memoryReset"] = h.stage("memory reset restarts the loaded program with start values", reset)


def storage(h, lab):
    require(lab.state("stop", 30000) == "Stop", "The instance did not stop")
    require(lab.state("powerOff") == "Off", "The instance did not switch off")
    archived = h.stage("archive the virtual memory card", lambda: h.tool("sim_storage", {"instanceId": lab.instance["id"], "action": "archive"}))
    receipt = archived["archive"]
    require(archived["ownershipMarkerUnchanged"] is True and receipt["bytes"] > 0 and Path(receipt["archivePath"]).is_file(), "Archive receipt: %s" % receipt)
    def changed():
        error = h.call_error("sim_storage", {"instanceId": lab.instance["id"], "action": "retrieve", "archivePath": receipt["archivePath"], "expectedSha256": "0" * 64})
        require(error.get("code") == "SimulationArchiveChanged", "A wrong hash was not refused: %s" % error)
        return error
    h.stage("refuse retrieve with a wrong hash", changed)
    retrieved = h.stage("retrieve the archive", lambda: h.tool("sim_storage", {"instanceId": lab.instance["id"], "action": "retrieve", "archivePath": receipt["archivePath"], "expectedSha256": receipt["sha256"]}))
    require(retrieved["ownershipMarkerUnchanged"] is True, "The ownership marker changed")
    h.stage("power on the retrieved card", lambda: lab.state("powerOn"))
    require(h.stage("run the retrieved card", lambda: lab.state("run", 30000)) == "Run", "The retrieved card did not enter RUN")
    h.stage("refresh the PLCSIM tag list after the retrieve", lab.refresh)
    values = h.stage("the retrieved card runs the online-downloaded program", lambda: lab.read(ProgramVersion="int16"))
    require(values["ProgramVersion"] == 2, "The retrieved card runs %s" % values)
    require(lab.state("stop", 30000) == "Stop", "The instance did not stop")
    require(lab.state("powerOff") == "Off", "The instance did not switch off")
    h.report["storage"] = {"archive": receipt, "retrieved": retrieved["retrieved"]}


def remote_station(h, plc, cpu):
    """An ET 200SP IM 155-6 PN ST on the CPU's IO system, for OB86; a refusal is recorded and leaves OB86 unproven."""
    from qualify_remote_io import SERVER_MODULE_NAMES
    def run():
        heads = sorted([e for e in h.tool("hardware_catalog_search", {"filter": "IM 155-6 PN ST", "maxItems": 500})["entries"]
                        if "6ES7 155-6" in (e.get("articleNumber") or "") and "PN ST" in (e.get("typeName") or "")], key=lambda e: e["typeIdentifier"], reverse=True)
        require(heads and heads[0]["typeIdentifier"] == "OrderNumber:6ES7 155-6AU02-0BN0/V6.4", "The captured IM 155-6AU02 V6.4 profile is required: %s" % heads[:1])
        prepared = h.prepare("hardware_add", {"typeIdentifier": heads[0]["typeIdentifier"], "deviceName": "GapStation", "itemName": "GapHead"})
        h.tool("change_apply", {**h.apply_args(prepared), "compile": False, "save": False})
        rows = [item for r in h.tool("project_inventory", {"maxItems": 2000})["hardware"]
                for item in h.tool("hardware_inspect", {"hardwarePath": r["path"], "maxItems": 2000})["items"]]
        controller = next(r for r in rows if r["path"].startswith(cpu) and r.get("network", {}).get("ioControllers"))
        remote = next(r for r in rows if "GapStation" in r["path"] and r.get("network", {}).get("ioConnectors"))
        rail = next(r["path"] for r in h.tool("project_inventory", {"maxItems": 2000})["hardware"] if "GapStation" in r["path"] and r.get("positionNumber") == 0 and r["path"].count("/items/") == 1)
        head = next(r for r in rows if r["name"] == "GapHead" and r["path"].count("/items/") == 1)
        author = next(a["value"] for a in head["attributes"] if a["name"] == "Author" and "error" not in a)
        server = SERVER_MODULE_NAMES[remote["name"]]
        for target, address in ((controller, None), (remote, "192.168.0.2")):
            payload = {"hardwarePath": target["path"], "nodeName": target["network"]["nodes"][0]["name"], "subnetName": "GapSubnet"}
            if address:
                payload["attributes"] = {"Address": address, "SubnetMask": "255.255.255.0"}
            prepared = h.prepare("network_configure", payload)
            h.tool("change_apply", {**h.apply_args(prepared), "compile": False, "save": False})
        h.apply("network_io_assign", {"hardwarePath": remote["path"], "connectorIndex": 0, "controllerHardwarePath": controller["path"], "controllerIndex": 0,
                                      "createIoSystemName": "GapIoSystem", "expectedServerModule": {"name": server, "typeIdentifier": "OrderNumber:6ES7 193-6PA00-0AA0/V1.2",
                                      "containerHardwarePath": rail, "positionNumber": 1, "attributes": {"AdditionalInformation": "", "Author": author, "Comment": "", "LocationIdentifier": "",
                                      "Name": server, "PlantDesignation": ""}}})
        ids = hardware_ids(h, plc)
        station = ids.get("GapHead~IODevice")
        require(station is not None, "No system constant names the ET 200SP station: %s" % sorted(ids))
        return {"station": station, "systemConstants": {k: v for k, v in ids.items() if "Gap" in k}}
    try:
        result = h.stage("ET 200SP station on the CPU's IO system", run)
        h.report["remoteStation"] = result
        return result["station"]
    except Exception as error:
        h.report["remoteStation"] = {"failure": str(error)}
        return None


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
    Harness(args, "pg_", "PLCSIM gaps on one owned PLCSIM Advanced V8 instance: online download, virtual time, events, memory reset and storage", LIMITATIONS, []).run(body)
