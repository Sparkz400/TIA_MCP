"""Qualify sim_read_signals, sim_write_signals, sim_area_read, sim_area_write and sim_observe on one owned PLCSIM Advanced V8
instance on Softbus.

The fixture is an owned scratch CPU 1511-1 PN with simulation support: PLC tags on known input, output and marker addresses,
a global DB SigDb with one member per primitive type, and a Main that copies IB20 to QB20, IW22 + 1 to QW22 and MW30 to MW32
and executes STP two seconds after SigDb.Arm is set. The program is downloaded to a fresh owned instance, provisioned and run.

Independent oracles: every batch write is read back with single sim_read calls and every batch read equals them; area
writes are read back symbolically (tags at the written addresses and the program's copies), symbolic writes are read back
by address; observed operating-state changes match the polled state (sim_status) and the log of a separate PowerShell
process that drives the instance through its own PLCSIM API connection. Another such process registers and holds a
foreign instance for the ownership refusals and unregisters it at the end, also after a failure.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import time
import uuid

from block_test_runtime import save_generated_scratch_project, verify_credential_provisioning, verify_download, verify_owned_instance
from library_native_harness import Harness, require

API_DIR = r"C:\Program Files (x86)\Common Files\Siemens\PLCSIMADV\API\8.0"
DB = "SigDb"
# member -> (S7 type, signal type, first value, second value)
MEMBERS = {
    "VBool": ("Bool", "bool", True, False), "VSInt": ("SInt", "int8", -128, 127), "VUSInt": ("USInt", "uint8", 255, 1),
    "VInt": ("Int", "int16", -32768, 32767), "VUInt": ("UInt", "uint16", 65535, 2), "VDInt": ("DInt", "int32", -2147483648, 2147483647),
    "VUDInt": ("UDInt", "uint32", 4294967295, 3), "VLInt": ("LInt", "int64", -9223372036854775808, 9223372036854775807),
    "VULInt": ("ULInt", "uint64", 18446744073709551615, 4), "VReal": ("Real", "float", 1.5, -0.25), "VLReal": ("LReal", "double", -2.25e300, 3.0),
}
TAGS = {"SigInByte": ("USInt", "%IB20", "uint8"), "SigInWord": ("Int", "%IW22", "int16"), "SigOutByte": ("USInt", "%QB20", "uint8"),
        "SigOutWord": ("Int", "%QW22", "int16"), "SigMarker": ("Int", "%MW30", "int16"), "SigMarkerCopy": ("Int", "%MW32", "int16"),
        "SigBit": ("Bool", "%M34.5", "bool")}
EXTRA = {"Text": "string", "Arm": "bool", "Scans": "int32"}
DB_SOURCE = '''DATA_BLOCK "SigDb"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
NON_RETAIN
   VAR
%s      Text : String;
      Arm : Bool;
      Scans : DInt;
      Halt : TON_TIME;
   END_VAR

BEGIN

END_DATA_BLOCK

''' % "".join("      %s : %s;\n" % (name, row[0]) for name, row in MEMBERS.items())
MAIN_SOURCE = '''ORGANIZATION_BLOCK "Main"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1

BEGIN
    "SigDb".Scans := "SigDb".Scans + 1;
    "SigOutByte" := "SigInByte";
    "SigOutWord" := "SigInWord" + 1;
    "SigMarkerCopy" := "SigMarker";
    "SigDb".Halt(IN := "SigDb".Arm, PT := T#2S);
    IF "SigDb".Halt.Q THEN
        STP();
    END_IF;
END_ORGANIZATION_BLOCK

'''
# A separate PLCSIM API client: holds a foreign instance it registered until a line or EOF on stdin, then unregisters it;
# or drives Run/Stop on an instance with delays and logs each result.
ACTOR = r'''param([string]$ApiDir, [string]$Mode, [string]$Name, [string]$Storage = '', [string]$Steps = '')
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $ApiDir 'Siemens.Simatic.Simulation.Runtime.Api.x64.dll'))
$manager = [Siemens.Simatic.Simulation.Runtime.SimulationRuntimeManager]
function Emit($value) { [Console]::Out.WriteLine((ConvertTo-Json $value -Compress -Depth 5)); [Console]::Out.Flush() }
function Now { (Get-Date).ToUniversalTime().ToString('o') }
try {
    if ($Mode -eq 'hold') {
        $instance = $manager::RegisterInstance([Siemens.Simatic.Simulation.Runtime.ECPUType]::CPU1511, $Name)
        try {
            $instance.StoragePath = $Storage
            Emit @{ id = $instance.ID; name = $instance.Name; state = "$($instance.OperatingState)" }
            [void][Console]::In.ReadLine()
        } finally { $instance.UnregisterInstance(); $instance.Dispose() }
        Emit @{ unregistered = $Name; atUtc = (Now) }
    } elseif ($Mode -eq 'unregister') {
        $instance = $manager::CreateInterface($Name)
        $instance.UnregisterInstance(); $instance.Dispose()
        Emit @{ unregistered = $Name; atUtc = (Now) }
    } else {
        $instance = $manager::CreateInterface($Name)
        Emit @{ ready = (Now); state = "$($instance.OperatingState)" }
        $log = New-Object System.Collections.ArrayList
        foreach ($step in $Steps.Split(',')) {
            $action, $delay = $step.Split(':')
            Start-Sleep -Milliseconds ([int]$delay)
            $started = Now
            if ($action -eq 'Run') { $instance.Run(30000) } else { $instance.Stop(30000) }
            [void]$log.Add(@{ action = $action; startedAtUtc = $started; completedAtUtc = (Now); state = "$($instance.OperatingState)" })
        }
        $instance.Dispose()
        Emit @{ steps = $log }
    }
} finally { $manager::Shutdown() }
'''
CRITERIA = {
    "sim_read_signals": {"AC-identity": "Unselected instances refuse as SimulationNotSelected; string types as UnsupportedSignalType before any runtime call.",
                         "AC-runtime": "One ReadSignals call returns every primitive type equal to independent single sim_read reads; a missing symbol is a bad item with its runtime code.",
                         "AC-evidence": "This report."},
    "sim_write_signals": {"AC-identity": "A foreign (unowned) instance refuses as OwnedInstanceRequired; invalid batches refuse before any write.",
                          "AC-runtime": "One WriteSignals call writes every primitive type including its range limits, read back by single sim_read calls.",
                          "AC-evidence": "This report."},
    "sim_area_read": {"AC-identity": "Ranges beyond the area refuse as InvalidRequest.", "AC-runtime": "Input, output and marker bytes and bits equal symbolic reads of tags at those addresses.",
                      "AC-evidence": "This report."},
    "sim_area_write": {"AC-identity": "Outputs and foreign instances refuse (InvalidRequest, OwnedInstanceRequired).",
                       "AC-runtime": "Input and marker bytes and bits read back symbolically and through the program's copies; the scan overwrites a program-written marker.",
                       "AC-evidence": "This report."},
    "sim_observe": {"AC-identity": "Unselected instances refuse; bounds refuse before subscribing.",
                    "AC-completeness": "maxEvents ends the window early and marks it truncated; LEDs are reported only as changes.",
                    "AC-simulator-lifecycle": "An STP-caused RUN to STOP and an external client's STOP/RUN sequence are observed in order and match the polled state and the client's log.",
                    "AC-evidence": "This report."},
}
LIMITATIONS = [
    "One owned PLCSIM Advanced V8 CPU 1511 instance on Softbus of an owned scratch CPU 1511-1 PN V4.1, provisioned with first-use TLS trust under the owned-empty-CPU policy like the D09 runner.",
    "Process-image addresses IB20..23, QB20..23 and MB30..34 have no configured I/O module; inputs of configured modules are not exercised.",
    "Operating-state changes inside an observation window come from the program (STP) and a separate PLCSIM API client; the PLCSIM control panel and TIA Portal are not exercised.",
]


class Lab:
    def __init__(self, h):
        self.h, self.symbols, self.instance = h, {}, None

    def resolve(self):
        tags = self.h.tool("sim_tags", {"instanceId": self.instance["id"], "maxItems": 2000})["tags"]
        rows = {}
        for name in list(MEMBERS) + list(EXTRA):
            found = [t for t in tags if t["symbol"].replace('"', "") == DB + "." + name]
            require(len(found) == 1, "PLCSIM symbol %s.%s is missing or ambiguous" % (DB, name))
            self.symbols[name], rows[name] = found[0]["symbol"], found[0]
        for name in TAGS:
            found = [t for t in tags if t["symbol"].replace('"', "") == name]
            require(len(found) == 1, "PLCSIM tag %s is missing or ambiguous" % name)
            self.symbols[name], rows[name] = found[0]["symbol"], found[0]
        return {name: {k: row[k] for k in ("symbol", "dataType", "area", "offset", "bit")} for name, row in rows.items()}

    def signal(self, name, kind=None):
        return {"symbol": self.symbols[name], "type": kind or (MEMBERS[name][1] if name in MEMBERS else TAGS[name][2] if name in TAGS else EXTRA[name])}

    def single(self, *names):
        """The oracle: one sim_read call per symbol."""
        result = {}
        for name in names:
            row = self.h.tool("sim_read", {"instanceId": self.instance["id"], "signals": [self.signal(name)]})["values"][0]
            require(row["quality"] == "good", "Bad sim_read of %s: %s" % (name, row))
            result[name] = row["value"]
        return result

    def batch(self, *names):
        rows = self.h.tool("sim_read_signals", {"instanceId": self.instance["id"], "signals": [self.signal(n) for n in names]})["values"]
        require(all(r["quality"] == "good" for r in rows), "Bad batch read: %s" % rows)
        return {n: r["value"] for n, r in zip(names, rows)}

    def area(self, area, offset, length=None, bit=None):
        arguments = {"instanceId": self.instance["id"], "area": area, "offset": offset}
        if bit is None:
            arguments["length"] = length or 1
        else:
            arguments["bit"] = bit
        result = self.h.tool("sim_area_read", arguments)
        return result["value"] if bit is not None else result["bytes"]

    def write_area(self, area, offset, data=None, bit=None, value=None):
        arguments = {"instanceId": self.instance["id"], "area": area, "offset": offset}
        arguments.update({"bytes": data} if bit is None else {"bit": bit, "value": value})
        return self.h.tool("sim_area_write", arguments)

    def polled(self):
        rows = [s for s in self.h.tool("sim_status", {})["selections"] if s["id"] == self.instance["id"]]
        require(len(rows) == 1, "The owned instance is not listed once by sim_status")
        return rows[0]["operatingState"]


def actor(h, mode, name, storage="", steps=""):
    path = h.directory / "actor.ps1"
    if not path.exists():
        path.write_text(ACTOR, encoding="utf-8")
    command = ["powershell.exe", "-NoProfile", "-NonInteractive", "-File", str(path), "-ApiDir", API_DIR, "-Mode", mode, "-Name", name]
    if storage:
        command += ["-Storage", storage]
    if steps:
        command += ["-Steps", steps]
    return subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")


def actor_result(process, timeout=120):
    """Ends a holding client (a line on stdin) and returns its JSON lines; a client that fails or hangs is killed."""
    try:
        out, err = process.communicate("\n", timeout=timeout)
    except BaseException:
        process.kill()
        process.communicate()
        raise
    require(process.returncode == 0, "PowerShell API client failed (%s): %s" % (process.returncode, err.strip()[-2000:]))
    lines = [json.loads(line) for line in out.splitlines() if line.strip().startswith("{")]
    require(lines, "PowerShell API client wrote no result: %s" % err.strip()[-2000:])
    return lines


def refuse(h, label, tool, arguments, codes):
    def check():
        error = h.call_error(tool, arguments)
        require(error.get("code") in codes, "Expected %s, observed %s: %s" % (codes, error.get("code"), error.get("message")))
        return error["code"]
    return h.stage("refuse %s: %s" % (tool, label), check)


def body(h):
    plc = h.owned_scratch("plcsim signals")
    h.report["criteria"] = CRITERIA
    lab = Lab(h)
    cpus = h.scratch["scratchSecurity"]["cpus"]
    require(len(cpus) == 1, "Exactly one generated scratch credential is required")
    h.apply("project_simulation_enable", {"enabled": True})
    tables = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["tagTables"]
    default = [t for t in tables if any(c.get("kind") == "system" for c in t.get("constants", []))]
    require(len(default) == 1, "Expected one default tag table: %s" % [t["name"] for t in tables])
    for name, (kind, address, _) in TAGS.items():
        h.apply("tag_create", {"tagTablePath": default[0]["path"], "name": name, "dataTypeName": kind, "logicalAddress": address})
    h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": DB_SOURCE, "expectedBlockNames": [DB]})
    h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": MAIN_SOURCE, "expectedBlockNames": ["Main"]})

    creation = uuid.uuid4().hex
    created = h.stage("sim_create", lambda: h.tool("sim_create", {"cpuType": "CPU1511", "creationId": creation}))
    lab.instance = verify_owned_instance(created["instance"])
    released, foreign, failed = False, {}, True
    try:
        ticket = {"downloadTicket": created["downloadTicket"], "credentialMetadataPath": cpus[0]["credentialMetadataPath"]}
        download = h.stage("simulation_download", lambda: h.tool("simulation_download", ticket))
        verify_download(download, lab.instance)
        h.report["generatedProjectSave"] = save_generated_scratch_project(h.tool, download)
        provisioned = h.stage("simulation_provision_scratch_credential",
                              lambda: h.tool("simulation_provision_scratch_credential", {**ticket, "trustOwnedEmptyCpuCertificate": True}))
        h.report["credentialProvisioning"] = verify_credential_provisioning(provisioned, download, trust_owned_empty_cpu_certificate=True)
        h.stage("power on", lambda: h.tool("sim_state", {"instanceId": lab.instance["id"], "action": "powerOn", "timeoutMs": 60000}))
        running = h.stage("run", lambda: h.tool("sim_state", {"instanceId": lab.instance["id"], "action": "run", "timeoutMs": 30000}))
        require(running["instance"]["operatingState"] == "Run", "The instance did not enter RUN")
        h.report["tags"] = h.stage("resolve the fixture in the PLCSIM tag list", lab.resolve)

        signals(h, lab)
        areas(h, lab)
        observe(h, lab)
        foreign_instance(h, lab, foreign)

        off = h.stage("stop and power off", lambda: [h.tool("sim_state", {"instanceId": lab.instance["id"], "action": a, "timeoutMs": 60000})["instance"]["operatingState"]
                                                       for a in ("stop", "powerOff")])
        require(off == ["Stop", "Off"], "The instance did not stop and switch off: %s" % off)
        result = h.stage("sim_release", lambda: h.tool("sim_release", {"instanceId": lab.instance["id"], "deleteOwned": True}))
        require(result.get("unregistered") is True, "The owned instance was not released")
        released = True
        for tool, arguments in (("sim_read_signals", {"signals": [{"symbol": "x", "type": "bool"}]}), ("sim_write_signals", {"writes": [{"symbol": "x", "type": "bool", "value": True}]}),
                                ("sim_area_read", {"area": "input", "offset": 0}), ("sim_area_write", {"area": "input", "offset": 0, "bytes": [0]}), ("sim_observe", {})):
            refuse(h, "a released instance", tool, {"instanceId": lab.instance["id"], **arguments}, ["SimulationNotSelected"])
        failed = False
    finally:
        # Every cleanup runs even when an earlier one fails; a cleanup failure fails an otherwise passing run.
        cleanups = []
        if not released:
            cleanups.append(("sim_release", lambda: h.tool("sim_release", {"instanceId": lab.instance["id"], "deleteOwned": True})))
        if foreign.get("selected"):
            cleanups.append(("sim_release the foreign selection", lambda: h.tool("sim_release", {"instanceId": foreign["id"]})))
        if foreign.get("name") and not foreign.get("unregistered"):
            cleanups.append(("unregister the foreign instance", lambda: unregister_foreign(h, foreign)))
        errors = []
        for label, operation in cleanups:
            try:
                h.stage(label, operation)
            except Exception as error:
                errors.append("%s: %s" % (label, error))
        if errors:
            h.report["cleanupFailures"] = errors
            if not failed:
                raise AssertionError("Cleanup failed: %s" % errors)


def signals(h, lab):
    names = list(MEMBERS)
    def batch_write(index):
        def run():
            values = {n: MEMBERS[n][2 + index] for n in names}
            result = h.tool("sim_write_signals", {"instanceId": lab.instance["id"], "writes": [{**lab.signal(n), "value": values[n]} for n in names]})
            require([w["result"] for w in result["writes"]] == ["OK"] * len(names), "Batch write results: %s" % result["writes"])
            oracle = lab.single(*names)
            require(oracle == values, "Single reads after the batch write differ: %s != %s" % (oracle, values))
            read = lab.batch(*names)
            require(read == oracle, "Batch read %s differs from single reads %s" % (read, oracle))
            return {"written": values, "singleReads": oracle, "batchRead": read}
        return run
    h.report["batchFirst"] = h.stage("sim_write_signals every primitive type, read back by sim_read and sim_read_signals", batch_write(0))
    def single_write():
        values = {n: MEMBERS[n][2] for n in names}
        h.tool("sim_write", {"instanceId": lab.instance["id"], "writes": [{**lab.signal(n), "value": values[n]} for n in names]})
        read = lab.batch(*names)
        require(read == values, "sim_read_signals after sim_write: %s != %s" % (read, values))
        return read
    h.report["batchAfterSingleWrite"] = h.stage("sim_read_signals after independent sim_write", single_write)
    h.report["batchSecond"] = h.stage("sim_write_signals the second value set", batch_write(1))
    def bad_item():
        # Observed on 30 October 2026: PLCSIM rejects the whole batch; the unknown item reports DoesNotExist, the others
        # SignalConfigurationError, and no item carries a value.
        rows = h.tool("sim_read_signals", {"instanceId": lab.instance["id"], "signals": [lab.signal("VBool"), {"symbol": '"SigDb".Missing', "type": "int16"}, lab.signal("Scans", "int32")]})["values"]
        require(rows[1]["quality"] == "bad" and rows[1].get("errorCode") == "DoesNotExist" and all(r["quality"] == "bad" and r.get("errorCode") and "value" not in r for r in rows),
                "Bad-item batch: %s" % rows)
        good = lab.batch("VBool", "Scans")
        return {"rejected": rows, "withoutTheUnknownSymbol": good}
    h.report["batchBadItem"] = h.stage("sim_read_signals: an unknown symbol rejects the batch; without it the batch reads", bad_item)
    before = lab.single(*names)
    instance = lab.instance["id"]
    refuse(h, "a string type", "sim_read_signals", {"instanceId": instance, "signals": [lab.signal("Text", "string")]}, ["UnsupportedSignalType"])
    refuse(h, "a wstring write", "sim_write_signals", {"instanceId": instance, "writes": [{**lab.signal("Text", "wstring"), "value": "x"}]}, ["UnsupportedSignalType"])
    refuse(h, "duplicate symbols", "sim_write_signals", {"instanceId": instance, "writes": [{**lab.signal("VInt"), "value": 1}, {**lab.signal("VInt"), "value": 2}]}, ["InvalidRequest"])
    refuse(h, "65 signals", "sim_read_signals", {"instanceId": instance, "signals": [lab.signal("VBool")] * 65}, ["InvalidRequest"])
    refuse(h, "a uint8 out of range", "sim_write_signals", {"instanceId": instance, "writes": [{**lab.signal("VInt"), "value": 5}, {**lab.signal("VUSInt"), "value": 256}]}, ["InvalidRequest"])
    refuse(h, "a 300-character symbol", "sim_read_signals", {"instanceId": instance, "signals": [{"symbol": "x" * 300, "type": "bool"}]}, ["InvalidRequest"])
    refuse(h, "an unselected instance", "sim_read_signals", {"instanceId": 987654, "signals": [lab.signal("VBool")]}, ["SimulationNotSelected"])
    after = h.stage("refused batches wrote nothing", lambda: lab.single(*names))
    require(after == before, "A refused batch changed values: %s -> %s" % (before, after))
    def partial():
        error = h.call_error("sim_write_signals", {"instanceId": instance, "writes": [{**lab.signal("VInt"), "value": 1234}, {"symbol": '"SigDb".Missing', "type": "int16", "value": 1}]})
        require(error.get("code") == "PartialSimulationWrite" and error["details"]["retrySafe"] is False, "Partial batch write: %s" % error)
        return {"items": error["details"]["writes"], "batchError": error["details"].get("batchError"), "validItemReadBack": lab.single("VInt")["VInt"]}
    h.report["batchPartialWrite"] = h.stage("sim_write_signals reports a failed item as PartialSimulationWrite", partial)


def areas(h, lab):
    def scan():
        time.sleep(0.3)
    sizes = h.stage("area sizes", lambda: {a: h.tool("sim_area_read", {"instanceId": lab.instance["id"], "area": a, "offset": 0})["areaSize"] for a in ("input", "output", "marker")})
    h.report["areaSizes"] = sizes
    def input_bytes():
        lab.write_area("input", 20, [0xA5])
        lab.write_area("input", 22, [0x12, 0x34])
        scan()
        symbolic = lab.single("SigInByte", "SigInWord", "SigOutByte", "SigOutWord")
        require(symbolic == {"SigInByte": 0xA5, "SigInWord": 0x1234, "SigOutByte": 0xA5, "SigOutWord": 0x1235}, "Symbolic reads after the input write: %s" % symbolic)
        inputs, outputs = lab.area("input", 20, 4), lab.area("output", 20, 4)
        require(inputs[0] == 0xA5 and inputs[2:] == [0x12, 0x34] and outputs[0] == 0xA5 and outputs[2:] == [0x12, 0x35], "Area reads: I %s Q %s" % (inputs, outputs))
        return {"symbolic": symbolic, "input20": inputs, "output20": outputs}
    h.report["areaInput"] = h.stage("input bytes written by address read back symbolically and through the program's outputs", input_bytes)
    def markers():
        h.tool("sim_write", {"instanceId": lab.instance["id"], "writes": [{**lab.signal("SigMarker"), "value": -2}]})
        scan()
        by_address = {"MW30": lab.area("marker", 30, 2), "MW32": lab.area("marker", 32, 2)}
        require(by_address == {"MW30": [0xFF, 0xFE], "MW32": [0xFF, 0xFE]}, "Marker bytes after the symbolic write: %s" % by_address)
        lab.write_area("marker", 30, [0x04, 0xD2])
        scan()
        symbolic = lab.single("SigMarker", "SigMarkerCopy")
        require(symbolic == {"SigMarker": 1234, "SigMarkerCopy": 1234}, "Symbolic markers after the area write: %s" % symbolic)
        # MW32 is written by the program every scan: an assignment there lasts at most until the next scan.
        lab.write_area("marker", 32, [0x00, 0x01])
        scan()
        overwritten = lab.single("SigMarkerCopy")["SigMarkerCopy"]
        require(overwritten == 1234, "The scan did not overwrite the program-written marker: %s" % overwritten)
        return {"symbolicToArea": by_address, "areaToSymbolic": symbolic, "programWrittenMarkerAfterScan": overwritten}
    h.report["areaMarkers"] = h.stage("marker words by address against symbolic reads and writes", markers)
    def bits():
        lab.write_area("marker", 34, bit=5, value=True)
        on = {"symbolic": lab.single("SigBit")["SigBit"], "bit": lab.area("marker", 34, bit=5), "byte": lab.area("marker", 34)[0]}
        lab.write_area("marker", 34, bit=5, value=False)
        off = {"symbolic": lab.single("SigBit")["SigBit"], "bit": lab.area("marker", 34, bit=5), "byte": lab.area("marker", 34)[0]}
        require(on["symbolic"] is True and on["bit"] is True and on["byte"] & 0x20 and off["symbolic"] is False and off["bit"] is False and not off["byte"] & 0x20,
                "Marker bit 34.5: on %s off %s" % (on, off))
        lab.write_area("input", 20, bit=0, value=False)
        scan()
        cleared = lab.single("SigInByte", "SigOutByte")
        require(cleared == {"SigInByte": 0xA4, "SigOutByte": 0xA4}, "Input bit 20.0 cleared: %s" % cleared)
        return {"on": on, "off": off, "inputBitCleared": cleared}
    h.report["areaBits"] = h.stage("bits by address against symbolic reads", bits)
    instance = lab.instance["id"]
    refuse(h, "the output area", "sim_area_write", {"instanceId": instance, "area": "output", "offset": 20, "bytes": [1]}, ["InvalidRequest"])
    refuse(h, "a range beyond the marker area", "sim_area_read", {"instanceId": instance, "area": "marker", "offset": sizes["marker"] - 1, "length": 2}, ["InvalidRequest"])
    refuse(h, "a write beyond the input area", "sim_area_write", {"instanceId": instance, "area": "input", "offset": sizes["input"], "bytes": [1]}, ["InvalidRequest"])
    refuse(h, "length 1025", "sim_area_read", {"instanceId": instance, "area": "input", "offset": 0, "length": 1025}, ["InvalidRequest"])
    refuse(h, "a byte value 256", "sim_area_write", {"instanceId": instance, "area": "marker", "offset": 30, "bytes": [256]}, ["InvalidRequest"])
    refuse(h, "bit 8", "sim_area_write", {"instanceId": instance, "area": "marker", "offset": 34, "bit": 8, "value": True}, ["InvalidRequest"])
    refuse(h, "a timer area", "sim_area_read", {"instanceId": instance, "area": "timer", "offset": 0}, ["InvalidRequest"])
    unchanged = h.stage("refused area writes wrote nothing", lambda: lab.single("SigMarker", "SigInByte"))
    require(unchanged == {"SigMarker": 1234, "SigInByte": 0xA4}, "A refused area write changed values: %s" % unchanged)


def states(result):
    return [e["current"] for e in result["events"] if e["kind"] == "operatingState"]


def observe(h, lab):
    instance = lab.instance["id"]
    def snapshot():
        result = h.tool("sim_observe", {"instanceId": instance})
        polled = lab.polled()
        require(result["before"]["operatingState"] == result["after"]["operatingState"] == polled == "Run" and result["events"] == [] and result["truncated"] is False,
                "Snapshot: %s, polled %s" % ({k: result[k] for k in ("before", "after", "events")}, polled))
        return {"before": result["before"], "after": result["after"], "display": result["display"], "polled": polled}
    h.report["observeSnapshot"] = h.stage("sim_observe snapshot in RUN", snapshot)
    def program_stop():
        h.tool("sim_write_signals", {"instanceId": instance, "writes": [{**lab.signal("Arm"), "value": True}]})
        result = h.tool("sim_observe", {"instanceId": instance, "windowMs": 8000})
        polled = lab.polled()
        observed = states(result)
        require(observed and observed[-1] == "Stop" and result["after"]["operatingState"] == polled == "Stop" and result["before"]["operatingState"] == "Run",
                "STP window: states %s, after %s, polled %s" % (observed, result["after"], polled))
        first = next(e for e in result["events"] if e["kind"] == "operatingState")
        require(first["previous"] == "Run", "The first observed transition does not leave RUN: %s" % first)
        return {"events": result["events"], "ledModes": result["ledModes"], "after": result["after"], "polled": polled, "elapsedMs": result["elapsedMs"]}
    h.report["observeProgramStop"] = h.stage("sim_observe sees the program's STP take the CPU from RUN to STOP", program_stop)
    def external(steps, window, expected, maximum=64):
        def run():
            process = actor(h, "sequence", lab.instance["name"], steps=steps)
            try:
                line = process.stdout.readline()
                ready = json.loads(line) if line.strip() else {}
                require("ready" in ready, "The PowerShell API client did not start: %s %s" % (line, process.stderr.read() if process.poll() is not None else ""))
                result = h.tool("sim_observe", {"instanceId": instance, "windowMs": window, "maxEvents": maximum})
            except BaseException:
                # Never leave a client that would still change the instance's state later.
                process.kill()
                process.communicate()
                raise
            log = actor_result(process)[-1]["steps"]
            polled = lab.polled()
            observed = states(result)
            return {"actor": {"ready": ready, "steps": log}, "events": result["events"], "states": observed, "ledModes": result["ledModes"],
                    "after": result["after"], "polled": polled, "elapsedMs": result["elapsedMs"], "truncated": result["truncated"]}
        outcome = h.stage("sim_observe an external API client's %s" % steps, run)
        expected(outcome)
        return outcome
    def sequence(outcome):
        observed = [s for s in outcome["states"] if s in ("Run", "Stop")]
        require(observed == ["Run", "Stop", "Run"] and [s["state"] for s in outcome["actor"]["steps"]] == ["Run", "Stop", "Run"]
                and outcome["after"]["operatingState"] == outcome["polled"] == "Run" and outcome["truncated"] is False,
                "External sequence: observed %s, actor %s, after %s, polled %s" % (outcome["states"], outcome["actor"]["steps"], outcome["after"], outcome["polled"]))
    h.report["observeExternal"] = external("Run:1500,Stop:2000,Run:1500", 15000, sequence)
    def early(outcome):
        require(outcome["truncated"] is True and len(outcome["events"]) == 1 and outcome["elapsedMs"] < 15000, "maxEvents 1: %s" % outcome)
    h.report["observeMaxEvents"] = external("Stop:1500", 15000, early, maximum=1)
    def settled():
        for _ in range(50):
            if lab.polled() == "Stop":
                break
            time.sleep(0.1)
        running = h.tool("sim_state", {"instanceId": instance, "action": "run", "timeoutMs": 30000})["instance"]["operatingState"]
        require(running == "Run", "The instance did not return to RUN")
        return running
    h.stage("return to RUN after the external STOP", settled)
    refuse(h, "windowMs 60001", "sim_observe", {"instanceId": instance, "windowMs": 60001}, ["InvalidRequest"])
    refuse(h, "maxEvents 0", "sim_observe", {"instanceId": instance, "maxEvents": 0}, ["InvalidRequest"])
    refuse(h, "an unselected instance", "sim_observe", {"instanceId": 987654}, ["SimulationNotSelected"])


def foreign_instance(h, lab, foreign):
    """An instance registered by another API client: selectable, readable and observable, never writable."""
    name = "TiaMcpForeign_" + uuid.uuid4().hex[:12]
    storage = h.directory / "foreign"
    storage.mkdir()
    # The client holds its registration until a line or EOF on stdin; body's cleanup ends it (or unregisters by name).
    foreign.update(name=name, holder=actor(h, "hold", name, storage=str(storage)))
    def register():
        line = foreign["holder"].stdout.readline()
        require(line.strip().startswith("{"), "The holding API client did not register: %s" % line)
        return json.loads(line)
    registered = h.stage("register a foreign instance through a separate API client", register)
    foreign.update(id=registered["id"], registered=registered)
    selected = h.stage("sim_select the foreign instance", lambda: h.tool("sim_select", {"instanceId": foreign["id"], "expectedName": name})["instance"])
    foreign["selected"] = True
    require(selected["owned"] is False, "A foreign instance was selected as owned")
    refuse(h, "a foreign instance", "sim_write_signals", {"instanceId": foreign["id"], "writes": [{"symbol": '"SigDb".VBool', "type": "bool", "value": True}]}, ["OwnedInstanceRequired"])
    refuse(h, "a foreign instance", "sim_area_write", {"instanceId": foreign["id"], "area": "marker", "offset": 0, "bytes": [1]}, ["OwnedInstanceRequired"])
    refuse(h, "a foreign instance", "sim_write", {"instanceId": foreign["id"], "writes": [{"symbol": '"SigDb".VBool', "type": "bool", "value": True}]}, ["OwnedInstanceRequired"])
    refuse(h, "a foreign instance", "sim_state", {"instanceId": foreign["id"], "action": "powerOff", "timeoutMs": 30000}, ["OwnedInstanceRequired"])
    refuse(h, "a foreign instance", "sim_test", {"instanceId": foreign["id"], "signals": [{"symbol": '"SigDb".VBool', "type": "bool"}],
                                                "steps": [{"writes": [{"symbol": '"SigDb".VBool', "type": "bool", "value": True}]}]}, ["OwnedInstanceRequired"])
    def readable():
        observed = h.tool("sim_observe", {"instanceId": foreign["id"], "windowMs": 200})
        require(observed["after"]["operatingState"] == "Off", "The foreign instance is not Off: %s" % observed["after"])
        read = h.client.request("tools/call", {"name": "sim_area_read", "arguments": {"instanceId": foreign["id"], "area": "marker", "offset": 0}})
        return {"observe": {k: observed[k] for k in ("before", "after", "events", "display")}, "areaReadWhileOff": read.get("structuredContent")}
    foreign["readOnly"] = h.stage("observe and read the foreign instance", readable)
    h.stage("sim_release the foreign selection", lambda: h.tool("sim_release", {"instanceId": foreign["id"]}))
    foreign["selected"] = False
    h.stage("unregister the foreign instance through its holding client", lambda: unregister_foreign(h, foreign))
    h.report["foreignInstance"] = {k: v for k, v in foreign.items() if k != "holder"}


def unregister_foreign(h, foreign):
    """End the holding client, which unregisters the instance; if it failed, unregister by name. Then no instance of that name may remain."""
    result = None
    try:
        result = actor_result(foreign["holder"])[-1]
    except Exception as error:
        foreign["holderFailure"] = str(error)
    if any(i["name"] == foreign["name"] for i in h.tool("sim_status", {})["instances"]):
        result = actor_result(actor(h, "unregister", foreign["name"]))[-1]
    require(not any(i["name"] == foreign["name"] for i in h.tool("sim_status", {})["instances"]), "The foreign instance is still registered")
    foreign["unregistered"] = result or True
    return result


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
    Harness(args, "ps_", "PLCSIM batch signals, I/Q/M areas and operating-state observation on one owned PLCSIM Advanced V8 instance", LIMITATIONS, []).run(body)
