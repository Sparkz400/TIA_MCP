"""Qualify #213 natively: drive_inventory enumerates two SINAMICS G120C PN drives on the scratch CPU's IO system with
telegram addresses from Startdrive, joins PLC tags imported at exactly those addresses, and reports an unmapped telegram
range and the drives without technology objects. Owned scratch only."""
from __future__ import annotations

import argparse
from pathlib import Path

from library_native_harness import Harness, require
from probe_safety_kinds import change

LIMITATIONS = [
    "Two SINAMICS G120C PN drives on the owned scratch CPU 1511-1 PN's IO system; no technology object is connected, so the "
    "technology join is covered by managed tests only.",
    "Offline engineering addresses only; no online values, download or commissioning.",
]


def interface(h, device):
    rows = sorted((r for r in h.tool("hardware_inspect", {"maxItems": 2000})["items"]
                   if r["path"].startswith(device + "/") and r.get("network", {}).get("nodes")), key=lambda r: r["path"])
    require(rows, "No network interface under " + device)
    return rows[0]


def body(h):
    plc = h.owned_scratch("drive inventory")
    devices = "/projects/%s/devices/" % h.tool("tia_session_status")["projects"][0]["name"]
    entries = h.tool("hardware_catalog_search", {"filter": "G120C PN", "maxItems": 200})["entries"]
    rows = sorted((e for e in entries if "G120C PN" in (e.get("typeName") or "") and e.get("typeIdentifier", "").startswith("OrderNumber:")),
                  key=lambda e: [int(x) for x in e["typeIdentifier"].rsplit("/", 1)[1].split(".") if x.isdigit()])
    require(rows, "No SINAMICS G120C PN in the installed catalog")
    drive_type = h.report["driveType"] = rows[-1]["typeIdentifier"]
    controller = interface(h, devices + "ScratchStation")
    change(h, "network_configure", {"hardwarePath": controller["path"], "nodeName": controller["network"]["nodes"][0]["name"], "subnetName": "DriveNet",
                                    "attributes": {"Address": "192.0.2.10", "SubnetMask": "255.255.255.0"}}, {})
    controller = interface(h, devices + "ScratchStation")
    for n in (1, 2):
        change(h, "hardware_add", {"typeIdentifier": drive_type, "deviceName": "Drive%d" % n, "itemName": "Drive%dItem" % n}, {}, compile=False)
        port = interface(h, devices + "Drive%d" % n)
        change(h, "network_configure", {"hardwarePath": port["path"], "nodeName": port["network"]["nodes"][0]["name"], "subnetName": "DriveNet",
                                        "attributes": {"Address": "192.0.2.%d" % (20 + n), "SubnetMask": "255.255.255.0"}}, {}, compile=False)
        port = interface(h, devices + "Drive%d" % n)
        require(port["network"].get("ioConnectors"), "The drive interface exposes no IO connector: %s" % port["network"])
        change(h, "network_io_assign", {"hardwarePath": port["path"], "connectorIndex": 0, "controllerHardwarePath": controller["path"], "controllerIndex": 0,
                                        **({"createIoSystemName": "DriveIoSystem"} if n == 1 else {})}, {"plcPath": plc})

    first = h.stage("drive inventory before tags", lambda: h.tool("drive_inventory"))
    require(first["coverage"]["drives"]["status"] == "complete", "Drive coverage is %s" % first["coverage"]["drives"])
    require(len(first["drives"]) == 2 and first["noDrivesConfirmed"] is False, "Expected two drives: %s" % [d["hardwarePath"] for d in first["drives"]])
    ranges = [(d["name"], t["number"], a) for d in first["drives"] for o in d["driveObjects"] for t in o["telegrams"] for a in t["addresses"]]
    require(ranges and all(isinstance(a["startAddress"], int) and a["lengthBits"] for _, _, a in ranges), "Telegram addresses are incomplete: %s" % ranges)

    # One tag per telegram word at the native start address, except the second drive's output, which stays unmapped.
    tags, unmapped = [], None
    for name, number, a in ranges:
        if name.startswith("Drive2") and a["direction"].startswith("Out"):
            unmapped = a
            continue
        area = "I" if a["direction"].startswith("In") else "Q"
        tags.append(("%s_%s" % (name, "ZSW1" if area == "I" else "STW1"), "%%%sW%d" % (area, a["startAddress"])))
    xml = ('<?xml version="1.0" encoding="utf-8"?><Document><Engineering version="V21"/><SW.Tags.PlcTagTable ID="0"><AttributeList><Name>Drives</Name>'
           '</AttributeList><ObjectList>' + "".join('<SW.Tags.PlcTag ID="%X" CompositionName="Tags"><AttributeList><Name>%s</Name><DataTypeName>Word</DataTypeName>'
           '<LogicalAddress>%s</LogicalAddress></AttributeList></SW.Tags.PlcTag>' % (i + 1, n, a) for i, (n, a) in enumerate(tags)) +
           '</ObjectList></SW.Tags.PlcTagTable></Document>')
    change(h, "tag_table_import_xml", {"xml": xml, "expectedTableNames": ["Drives"]}, {"plcPath": plc})

    def joined():
        result = h.tool("drive_inventory")
        found = {t["name"]: t["logicalAddress"] for d in result["drives"] for o in d["driveObjects"] for tg in o["telegrams"] for a in tg["addresses"] for t in a["tags"]}
        require(found == dict(tags), "Joined tags %s differ from imported %s" % (found, dict(tags)))
        require(len(result["reconciliation"]["telegramRangesWithoutTags"]) == 1 and "Drive2" in result["reconciliation"]["telegramRangesWithoutTags"][0],
                "Unmapped ranges: %s" % result["reconciliation"]["telegramRangesWithoutTags"])
        require(len(result["reconciliation"]["drivesWithoutTechnologyObject"]) == 2, "Drives without TO: %s" % result["reconciliation"]["drivesWithoutTechnologyObject"])
        require(result["writeReady"] is True, "Blockers: %s" % result["blockers"])
        return {"tags": found, "unmapped": result["reconciliation"]["telegramRangesWithoutTags"], "unmappedRange": unmapped,
                "telegrams": [(d["name"], t["number"], t["inputBytes"], t["outputBytes"]) for d in result["drives"] for o in d["driveObjects"] for t in o["telegrams"]]}
    h.stage("drive inventory joins every imported tag by address", joined)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "di_", "#213 drive inventory with two G120C PN drives in one owned scratch", LIMITATIONS, []).run(body)
