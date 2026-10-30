"""Qualify the to_connect motion roles and module/telegram targets natively in one owned scratch CPU 1511-1 PN.

The fixture plugs a TM PosInput 2 (channel 0 in MotionControl mode) and an AQ 2xU/I ST, and adds a SINAMICS G120C PN,
which is put on one subnet and IO system with the CPU. Each connection is prepared and applied (checkpoint, PLC compile,
save) and read back through technology_read: a TO_SpeedAxis actor on the drive's telegram item when the drive exposes one,
otherwise on an analog output channel; a TO_ExternalEncoder encoder on the TM PosInput item, connected again through the
two-module and the address overloads, to a DB member and on its channel; a measuring input under a virtual
TO_PositioningAxis by channel index and on the channel; the axis's torque interface on the telegram. A real axis or encoder
is created without compile, since it compiles only once connected. What V21 refuses, at preparation or apply, is recorded under boundaries with its exact error; a failed apply
is restored from its checkpoint. Role/target pairs without a V21 Connect overload refuse typed and change nothing. The last
connection is restored from its checkpoint, and a fresh Portal reopen must show every connection read back before it.
"""
from __future__ import annotations

import argparse
from pathlib import Path

from library_native_harness import Harness, require
from qualify_drives import DEVICE, add_drive, outcome
from qualify_hardware_changes import devices
from qualify_technology_changes import portable

TM = "OrderNumber:6ES7 551-1AB01-0AB0/V2.0"  # TM PosInput 2 V2.0, the module of fixtures/encoder-hardware
CHANNEL = {"channelType": "Technology", "channelIoType": "Complex", "channelNumber": 0}
SPEED, AXIS, EXTERNAL, PROBE = ("TO_SpeedAxis", "10.0"), ("TO_PositioningAxis", "10.0"), ("TO_ExternalEncoder", "10.0"), ("TO_MeasuringInput", "10.0")
SUBNET, IO_SYSTEM, DRIVE_ADDRESS = "MotionNet", "MotionIo", "192.168.0.20"
TELEGRAM_DB = '''DATA_BLOCK "%s"
{ S7_Optimized_Access := 'FALSE' }
VERSION : 0.1
NON_RETAIN
   VAR
      Tel : "%s";
   END_VAR

BEGIN

END_DATA_BLOCK

'''
AQ = "6ES7 532-5NB00-0AB0"  # AQ 2xU/I ST for an analog drive connection
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 with one TM PosInput 2 V2.0, one AQ 2xU/I ST and one SINAMICS G120C PN (not commissioned in Startdrive); TO_SpeedAxis, "
    "TO_PositioningAxis (virtual), TO_ExternalEncoder and TO_MeasuringInput 10.0. Other CPUs, TO versions, drives, telegrams and modules are not exercised.",
    "Outcomes are what V21 does on this fixture: a role/target pair recorded under boundaries is refused by Siemens here, not proven impossible elsewhere.",
    "The drive itself is never compiled (no compiler service); PLC compiles validate the connections, not motion behavior.",
    "Engineering only; nothing is downloaded and no axis moves.",
]


class Run:
    def __init__(self, h):
        self.h, self.plc, self.base = h, None, None
        h.report.update(boundaries={}, connections={}, attempts={}, refusals={})

    def to(self, *names):
        return self.plc + "/technology/" + "/".join(names)

    def items(self, prefix):
        """hardware_inspect of every device item below prefix, one call per item: a drive's attributes exceed one call's budget."""
        rows = {}
        for key in devices(self.h.tool):
            path = key[4:]
            if "/items/" in path and path.startswith(prefix + "/"):
                found = self.h.tool("hardware_inspect", {"hardwarePath": path, "maxItems": 2000})["items"]
                require(len(found) == 1, "hardware_inspect of %s returned %d rows" % (path, len(found)))
                rows[path] = found[0]
        return rows

    def change(self, label, kind, payload, compile=True, plc=True):
        """Prepare and apply one change; V21's refusal at either step is returned with its exact error, a failed apply is restored."""
        h = self.h
        def run():
            error, prepared = outcome(h, "change_prepare", {"kind": kind, "payload": payload, **({"plcPath": self.plc} if plc else {})})
            if error:
                return {"outcome": "refusedAtPrepare", "error": error}
            error, applied = outcome(h, "change_apply", {**h.apply_args(prepared), "compile": compile, "save": compile})
            if error:
                if h.unresolved_admissions():
                    h.reconcile_admission("release the failed " + label)
                restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
                return {"outcome": "failedAtApply", "error": {k: error.get(k) for k in ("code", "message")}, "restored": restored["state"]}
            status = h.tool("change_status", {"changeId": prepared["changeId"]})
            require(status["state"] == "applied" and status["stages"] == applied["stages"], "change_status differs from the apply result")
            return {"outcome": "applied", "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "stages": applied["stages"],
                    "notCompiled": [row["targetPath"] for row in (applied.get("compile") or {}).get("notCompiled") or []]}
        result = h.stage(label, run)
        h.report["attempts"][label] = result
        if result["outcome"] != "applied":
            h.report["boundaries"][label] = {"kind": kind, "payload": payload, **result}
        return result

    def slot(self, path, role):
        rows = self.h.tool("technology_read", {"plcPath": self.plc, "technologyPath": path, "maxItems": 2000})["hardwareConnections"]
        matches = [row for row in rows if row["role"] == role]
        require(len(matches) == 1, "Expected one %s row on %s: %s" % (role, path, rows))
        return portable(matches[0])

    def connect(self, label, path, role, target):
        """to_connect; an applied connection must read back connected through technology_read."""
        result = self.change(label, "to_connect", {"technologyPath": path, "role": role, **target})
        if result["outcome"] == "applied":
            row = self.h.stage("read back " + label, lambda: self.slot(path, role))
            require(row.get("isConnected") is True, "%s does not read back connected: %s" % (label, row))
            self.h.report["connections"][label] = {"technologyPath": path, "role": role, "target": target, "row": row}
        return result

    def create(self, name, family, parameters=None):
        result = self.change("create %s %s %s" % (family[0], family[1], name), "to_create",
                             {"name": name, "typeIdentifier": family[0], "version": family[1], **({"parameters": parameters} if parameters else {})},
                             compile=parameters is not None)  # An unconnected real axis or encoder does not compile.
        require(result["outcome"] == "applied", "The fixture object %s was not created: %s" % (name, result))

    def unless_connected(self, path):
        """An object left unconnected would fail every later PLC compile, so it is deleted."""
        if not any(row["technologyPath"] == path for row in self.h.report["connections"].values()):
            result = self.change("delete the unconnected " + path.rsplit("/", 1)[1], "to_delete", {"technologyPath": path})
            require(result["outcome"] == "applied", "The unconnected %s was not deleted: %s" % (path, result))

    def refuse(self, label, path, role, target, code):
        def check():
            before = self.slot(path, role)
            error = self.h.call_error("change_prepare", {"kind": "to_connect", "payload": {"technologyPath": path, "role": role, **target}, "plcPath": self.plc})
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            require(self.slot(path, role) == before, "The refusal changed the %s slot" % role)
            return error
        self.h.report["refusals"][label] = self.h.stage("refuse " + label, check)


def fixture(run):
    """TM PosInput 2 in MotionControl mode; the G120C PN and the CPU on one subnet and IO system."""
    h = run.h
    station = run.base + "ScratchStation"
    h.apply("hardware_plug", {"hardwarePath": station + "/items/Rail_0", "typeIdentifier": TM, "name": "MoEnc", "positionNumber": 2})
    module = station + "/items/MoEnc"
    owners = [p for p, row in run.items(module).items() if any((c["type"], c["ioType"], c["number"]) == tuple(CHANNEL.values()) for c in row["channels"])]
    require(len(owners) == 1, "Expected one technology channel owner under MoEnc: %s" % owners)
    run.encoder = owners[0]
    channel = next(c for c in run.items(module)[run.encoder]["channels"] if (c["type"], c["ioType"], c["number"]) == tuple(CHANNEL.values()))
    attribute = next(a for a in channel["attributes"] if a["name"] == "OperatingMode")
    mode = next(v["value"] for v in attribute["nativeEnumValues"] if v["name"] == "MotionControl")
    h.apply("hardware_channel_attributes", {"hardwarePath": run.encoder, **CHANNEL, "attributes": {"OperatingMode": mode}})

    add_drive(h)
    drive = run.base + DEVICE
    rows = {**run.items(station), **run.items(drive)}
    controllers = [p for p, row in rows.items() if p.startswith(station + "/") and (row.get("network") or {}).get("ioControllers")]
    connectors = [p for p, row in rows.items() if p.startswith(drive + "/") and (row.get("network") or {}).get("ioConnectors")]
    require(len(controllers) == 1 and len(connectors) == 1, "Expected one CPU IO controller and one drive IO connector: %s %s" % (controllers, connectors))
    node = lambda path: rows[path]["network"]["nodes"][0]["name"]
    run.change("put the CPU on " + SUBNET, "network_configure", {"hardwarePath": controllers[0], "nodeName": node(controllers[0]), "subnetName": SUBNET}, plc=False)
    run.change("put the drive on " + SUBNET, "network_configure", {"hardwarePath": connectors[0], "nodeName": node(connectors[0]), "subnetName": SUBNET,
                                                                  "attributes": {"Address": DRIVE_ADDRESS}}, plc=False)
    assigned = run.change("assign the drive to " + IO_SYSTEM, "network_io_assign", {"hardwarePath": connectors[0], "connectorIndex": 0,
                          "controllerHardwarePath": controllers[0], "controllerIndex": 0, "createIoSystemName": IO_SYSTEM})
    drive_rows = run.items(drive)
    h.report["driveItems"] = {p: {k: row.get(k) for k in ("name", "typeIdentifier", "addresses")} for p, row in drive_rows.items()}
    telegrams = [p for p, row in drive_rows.items() if "elegram" in (row.get("name") or "") and row.get("addresses")]
    run.telegram = telegrams[0] if assigned["outcome"] == "applied" and telegrams else None
    if run.telegram is None:
        h.report["boundaries"]["drive telegram target"] = {"ioAssignment": assigned["outcome"], "telegramItems": telegrams}
    else:
        run.addresses = {row["ioType"]: row["startAddress"] for row in drive_rows[run.telegram]["addresses"]}
    tm = run.items(module)
    run.tm_addresses = {row["ioType"]: row["startAddress"] for path in sorted(tm) + [run.encoder] for row in tm[path]["addresses"]}  # the channel owner wins
    analog(run, station)


def analog(run, station):
    """An AQ 2xU/I ST and its first analog output channel, the actor target of an analog drive connection."""
    run.analog = None
    entries = [e for e in run.h.tool("hardware_catalog_search", {"filter": "AQ 2xU/I ST", "maxItems": 200})["entries"] if AQ in e["typeIdentifier"]]
    if not entries:
        run.h.report["boundaries"]["analog output module"] = "No %s in the installed catalog" % AQ
        return
    module = station + "/items/MoAq"
    entry = sorted(entries, key=lambda e: e["typeIdentifier"])[-1]
    plugged = run.change("plug the AQ 2xU/I ST MoAq", "hardware_plug", {"hardwarePath": station + "/items/Rail_0", "typeIdentifier": entry["typeIdentifier"],
                                                                         "name": "MoAq", "positionNumber": 3}, plc=False)
    if plugged["outcome"] != "applied":
        return
    outputs = [(p, c) for p, row in run.items(module).items() for c in row["channels"] if (c["type"], c["ioType"], c["number"]) == ("Analog", "Output", 0)]
    if len(outputs) != 1:
        run.h.report["boundaries"]["analog output channel"] = "Expected one analog output channel 0 under MoAq: %s" % [p for p, _ in outputs]
        return
    run.analog = {"hardwarePath": outputs[0][0], "channelType": "Analog", "channelIoType": "Output", "channelNumber": 0}


def actor(run):
    """One TO_SpeedAxis actor on the telegram item, then on the same item through the two-module, address and DB-member overloads;
    without a telegram item, on the analog output channel."""
    if run.telegram is None and run.analog is None:
        return
    run.create("MoSpeed", SPEED)
    path = run.to("MoSpeed")
    target = run.telegram or run.analog["hardwarePath"]
    run.refuse("actor with connectOption on a single module", path, "actor", {"hardwarePath": target, "connectOption": "AllowAllModules"}, "UnsupportedTarget")
    run.refuse("actor with a measuring-input channel index", path, "actor", {"hardwarePath": target, "channelIndex": 0}, "UnsupportedTarget")
    run.refuse("actor with one address", path, "actor", {"inputAddress": 0}, "InvalidRequest")
    if run.telegram is None:
        run.connect("connect MoSpeed actor to the MoAq analog output channel", path, "actor", run.analog)
        run.unless_connected(path)
        return
    run.connect("connect MoSpeed actor to the drive telegram", path, "actor", {"hardwarePath": run.telegram})
    run.connect("connect MoSpeed actor to the telegram as input and output module", path, "actor",
                {"inputHardwarePath": run.telegram, "outputHardwarePath": run.telegram, "connectOption": "Default"})
    if "Input" in run.addresses and "Output" in run.addresses:
        run.connect("connect MoSpeed actor to the telegram addresses", path, "actor",
                    {"inputAddress": run.addresses["Input"], "outputAddress": run.addresses["Output"], "connectOption": "Default"})
    db = run.change("create the telegram DB MoTelDb", "source_upsert", {"language": "SCL", "source": TELEGRAM_DB % ("MoTelDb", "PD_TEL1"), "expectedBlockNames": ["MoTelDb"]})
    if db["outcome"] == "applied":
        run.connect("connect MoSpeed actor to the DB member MoTelDb.Tel", path, "actor", {"dbMemberPath": "MoTelDb.Tel"})
    run.unless_connected(path)


def axis(run):
    """A virtual positioning axis: its torque interface on the telegram, and a measuring input on the TM PosInput."""
    run.create("MoAxis", AXIS, {"VirtualAxis.Mode": 1})
    if run.telegram is not None:
        run.connect("connect MoAxis torque to the drive telegram", run.to("MoAxis"), "torque", {"hardwarePath": run.telegram})
    probe = run.change("create measuring input MoProbe under MoAxis", "to_child_create",
                       {"ownerPath": run.to("MoAxis"), "composition": "MeasuringInputs", "name": "MoProbe", "typeIdentifier": PROBE[0], "version": PROBE[1]}, compile=False)
    if probe["outcome"] != "applied":
        return
    path = run.to("MoAxis", "children", "MeasuringInputs", "MoProbe")
    run.refuse("measuring input on a tag", path, "measuringInput", {"tagName": "MoMissing"}, "UnsupportedTarget")
    run.refuse("measuring input on an output address", path, "measuringInput", {"outputAddress": 0}, "InvalidRequest")
    run.connect("connect MoProbe to MoEnc by channel index 0", path, "measuringInput", {"hardwarePath": run.encoder, "channelIndex": 0})
    run.connect("connect MoProbe to the MoEnc channel", path, "measuringInput", {"hardwarePath": run.encoder, **CHANNEL})
    run.unless_connected(path)


def encoder(run):
    """A TO_ExternalEncoder on the TM PosInput item (module target), connected again through the two-module and address
    overloads and to a DB member, then on its channel; the channel change is restored."""
    run.create("MoEncoder", EXTERNAL)
    path = run.to("MoEncoder")
    run.refuse("encoder with a channel index", path, "encoder", {"hardwarePath": run.encoder, "channelIndex": 0}, "UnsupportedTarget")
    run.refuse("encoder with only an input address", path, "encoder", {"inputAddress": 0}, "InvalidRequest")
    run.connect("connect MoEncoder to the MoEnc module", path, "encoder", {"hardwarePath": run.encoder})
    run.connect("connect MoEncoder to MoEnc as input and output module", path, "encoder",
                {"inputHardwarePath": run.encoder, "outputHardwarePath": run.encoder, "connectOption": "Default"})
    if "Input" in run.tm_addresses and "Output" in run.tm_addresses:
        run.connect("connect MoEncoder to the MoEnc addresses", path, "encoder",
                    {"inputAddress": run.tm_addresses["Input"], "outputAddress": run.tm_addresses["Output"], "connectOption": "Default"})
    db = run.change("create the encoder telegram DB MoEncDb", "source_upsert", {"language": "SCL", "source": TELEGRAM_DB % ("MoEncDb", "PD_TEL83"), "expectedBlockNames": ["MoEncDb"]})
    if db["outcome"] == "applied":
        run.connect("connect MoEncoder to the DB member MoEncDb.Tel", path, "encoder", {"dbMemberPath": "MoEncDb.Tel"})
    before = run.h.stage("read MoEncoder before the last connection", lambda: run.slot(path, "encoder"))
    last = run.connect("connect MoEncoder to the MoEnc channel", path, "encoder", {"hardwarePath": run.encoder, **CHANNEL})
    if last["outcome"] == "applied":
        def restore():
            restored = run.h.tool("change_restore", {"changeId": last["changeId"], "previewHash": last["previewHash"]})
            require(restored["state"] == "restored" and "checkpoint_retrieved_to_new_directory" in restored["stages"], "Restore failed: %s" % restored["stages"])
            require(run.slot(path, "encoder") == before, "The restored encoder slot differs from its checkpoint")
            return {"stages": restored["stages"], "project": restored["recoveredProject"]["path"]}
        run.h.report["restore"] = run.h.stage("restore the last connection from its checkpoint", restore)
        del run.h.report["connections"]["connect MoEncoder to the MoEnc channel"]


def reopen(run):
    """A fresh owned Portal reopens the saved project; every connected slot reads back as before."""
    h = run.h
    final = {label: run.slot(row["technologyPath"], row["role"]) for label, row in h.report["connections"].items()}
    project = h.tool("tia_session_status")["projects"][0]["path"]
    h.stage("close the changed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen the saved project", lambda: h.reopen_project(project))
    def same():
        for label, row in h.report["connections"].items():
            require(run.slot(row["technologyPath"], row["role"]) == final[label], "Reopened %s differs" % label)
        return sorted(final)
    h.stage("reopened connections equal the saved ones", same)


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("motion connections")
    run.base = "/projects/%s/devices/" % h.tool("tia_session_status")["projects"][0]["name"]
    fixture(run)
    # The first motion object adds MC_Servo and MC_Interpolator, which stay inconsistent without a compile, and the change
    # fingerprint refuses inconsistent blocks (native, 30 October 2026): the compiled virtual axis comes before any object
    # created without compile.
    axis(run)
    actor(run)
    encoder(run)
    reopen(run)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "mc_", "D13 to_connect motion roles and targets in one owned scratch CPU 1511-1 PN with a TM PosInput 2 and a SINAMICS G120C PN",
            LIMITATIONS, []).run(body)
