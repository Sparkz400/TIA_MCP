"""Qualify #214 natively: servo_call_plan plans one wrapper call, multi-instance and fault Bool per G120C PN drive from
drive_inventory, its source_upsert applies and compiles, each drive's control tag then has exactly the caller as writer,
and a second writer of a control tag blocks a new plan. Owned scratch only."""
from __future__ import annotations

import argparse
import uuid
from pathlib import Path

from library_native_harness import Harness, require
from probe_safety_kinds import change

LIMITATIONS = [
    "Two SINAMICS G120C PN drives with telegram 1 on the owned scratch CPU 1511-1 PN; a synthetic SCL wrapper FB stands in for a vendor servo block.",
    "Engineering evidence only: no safety program, no machine behavior, no download.",
]
WRAPPER = ('FUNCTION_BLOCK "ServoWrap"\nVERSION : 0.1\n   VAR_INPUT\n      zsw : Word;\n      reset : Bool;\n      ss1 : Bool;\n   END_VAR\n'
           '   VAR_OUTPUT\n      stw : Word;\n      fault : Bool;\n   END_VAR\n\nBEGIN\n   #fault := #zsw.%X3;\n   IF #reset OR #ss1 THEN #stw := 16#0400; ELSE #stw := 16#047F; END_IF;\nEND_FUNCTION_BLOCK\n')


def interface(h, device):
    rows = sorted((r for r in h.tool("hardware_inspect", {"maxItems": 2000})["items"]
                   if r["path"].startswith(device + "/") and r.get("network", {}).get("nodes")), key=lambda r: r["path"])
    require(rows, "No network interface under " + device)
    return rows[0]


def table(name, tags):
    return ('<?xml version="1.0" encoding="utf-8"?><Document><Engineering version="V21"/><SW.Tags.PlcTagTable ID="0"><AttributeList><Name>%s</Name>'
            '</AttributeList><ObjectList>' % name + "".join('<SW.Tags.PlcTag ID="%X" CompositionName="Tags"><AttributeList><Name>%s</Name><DataTypeName>%s</DataTypeName>'
            '<LogicalAddress>%s</LogicalAddress></AttributeList></SW.Tags.PlcTag>' % (i + 1, n, t, a) for i, (n, t, a) in enumerate(tags)) +
            '</ObjectList></SW.Tags.PlcTagTable></Document>')


def body(h):
    plc = h.owned_scratch("servo calls")
    devices = "/projects/%s/devices/" % h.tool("tia_session_status")["projects"][0]["name"]
    entries = h.tool("hardware_catalog_search", {"filter": "G120C PN", "maxItems": 200})["entries"]
    rows = sorted((e for e in entries if "G120C PN" in (e.get("typeName") or "") and e.get("typeIdentifier", "").startswith("OrderNumber:")),
                  key=lambda e: [int(x) for x in e["typeIdentifier"].rsplit("/", 1)[1].split(".") if x.isdigit()])
    controller = interface(h, devices + "ScratchStation")
    change(h, "network_configure", {"hardwarePath": controller["path"], "nodeName": controller["network"]["nodes"][0]["name"], "subnetName": "DriveNet",
                                    "attributes": {"Address": "192.0.2.10", "SubnetMask": "255.255.255.0"}}, {})
    controller = interface(h, devices + "ScratchStation")
    for n in (1, 2):
        change(h, "hardware_add", {"typeIdentifier": rows[-1]["typeIdentifier"], "deviceName": "Drive%d" % n, "itemName": "Drive%dItem" % n}, {}, compile=False)
        port = interface(h, devices + "Drive%d" % n)
        change(h, "network_configure", {"hardwarePath": port["path"], "nodeName": port["network"]["nodes"][0]["name"], "subnetName": "DriveNet",
                                        "attributes": {"Address": "192.0.2.%d" % (20 + n), "SubnetMask": "255.255.255.0"}}, {}, compile=False)
        port = interface(h, devices + "Drive%d" % n)
        change(h, "network_io_assign", {"hardwarePath": port["path"], "connectorIndex": 0, "controllerHardwarePath": controller["path"], "controllerIndex": 0,
                                        **({"createIoSystemName": "DriveIoSystem"} if n == 1 else {})}, {"plcPath": plc})
    inventory = h.stage("drive inventory", lambda: h.tool("drive_inventory"))
    drives = {d["name"]: d for d in inventory["drives"]}
    tags = []
    for name, drive in sorted(drives.items()):
        for a in (a for o in drive["driveObjects"] for t in o["telegrams"] for a in t["addresses"]):
            area = "I" if a["direction"].startswith("In") else "Q"
            tags.append(("%s_%s" % (name, "ZSW1" if area == "I" else "STW1"), "Word", "%%%sW%d" % (area, a["startAddress"])))
    change(h, "tag_table_import_xml", {"xml": table("Drives", tags), "expectedTableNames": ["Drives"]}, {"plcPath": plc})
    change(h, "tag_table_import_xml", {"xml": table("Signals", [("Reset", "Bool", "%M100.0"), ("DoorOpen", "Bool", "%M100.1"), ("SS1", "Bool", "%M100.2")]),
                                       "expectedTableNames": ["Signals"]}, {"plcPath": plc})
    change(h, "source_upsert", {"language": "SCL", "source": WRAPPER, "expectedBlockNames": ["ServoWrap"]}, {"plcPath": plc})

    request = {"plcPath": plc, "callerName": "Servos", "wrapperName": "ServoWrap",
               "parameters": {"status": "zsw", "control": "stw", "reset": "reset", "ss1": "ss1", "fault": "fault"},
               "resetExpression": '"Reset" AND NOT "DoorOpen"', "ss1Expression": '"SS1"',
               "drives": [{"hardwarePath": d["hardwarePath"], "instanceName": "Axis%d" % i} for i, (_, d) in enumerate(sorted(drives.items()), start=1)]}
    plan = h.stage("servo_call_plan is ready", lambda: h.tool("servo_call_plan", {"request": request}))
    require(plan["writeReady"] is True, "Blockers: %s" % plan["blockers"])
    prepared = h.stage("prepare the planned source_upsert", lambda: h.tool("change_prepare", plan["change"]))
    applied = h.apply_prepared("source_upsert", prepared)
    require(applied["compile"]["compilerSucceeded"] is True, "The planned caller does not compile cleanly: %s" % applied["compile"]["unresolvedSymbols"])

    def verify():
        source = h.tool("block_read", {"plcPath": plc, "blockName": "Servos", "format": "generatedSource"})
        text = Path(next(a["path"] for a in source["export"]["artifacts"] if str(a["path"]).endswith(".scl"))).read_text(encoding="utf-8-sig")
        result = {}
        for row in plan["drives"]:
            require(text.count('#%s(' % row["instanceName"]) == 1 and '%s : Bool;' % row["faultName"] in text, "Call or fault Bool missing for %s" % row["instanceName"])
            writers = [w["actor"]["name"] for w in h.tool("symbol_trace", {"plcPath": plc, "symbol": row["controlTag"]})["writers"]]
            require(writers and all(w.startswith("Servos") for w in writers), "Writers of %s: %s" % (row["controlTag"], writers))
            result[row["controlTag"]] = writers
        return result
    h.stage("each drive has one call, one instance, one fault Bool and only the caller as control writer", verify)

    old = plan["drives"][0]["controlTag"]
    change(h, "source_upsert", {"language": "SCL", "source": 'FUNCTION "OldWriter" : Void\nVERSION : 0.1\nBEGIN\n   "%s" := 16#0;\nEND_FUNCTION\n' % old,
                                "expectedBlockNames": ["OldWriter"]}, {"plcPath": plc})

    def blocked():
        again = h.tool("servo_call_plan", {"request": request})
        kinds = [(b["kind"], b["target"]) for b in again["blockers"]]
        require(again["writeReady"] is False and "change" not in again and any(k == "duplicateControlWriter" and "OldWriter" in m for (k, _), m in
                zip(kinds, [b["message"] for b in again["blockers"]])), "Expected a duplicate-writer blocker: %s" % again["blockers"])
        return again["blockers"]
    h.stage("a second writer of a control tag blocks a new plan", blocked)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "sc_", "#214 coordinated servo calls with two G120C PN drives in one owned scratch", LIMITATIONS, []).run(body)
