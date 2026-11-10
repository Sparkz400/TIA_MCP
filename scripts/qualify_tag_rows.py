"""Qualify the row form of tag_table_import_xml and its advisory hardware cross-check natively in one owned scratch
CPU 1511-1 PN with one DI 16x24VDC HF. Rows create a table, then add to and update it while a tag no row names stays; the
cross-check passes on configured input addresses and reports, without refusing, an unconfigured output address, an input
address past the module and a data type wider than its address. Owned scratch only."""
from __future__ import annotations

import argparse
from pathlib import Path

from library_native_harness import Harness, require
from probe_safety_kinds import change

MODULE = "OrderNumber:6ES7 521-1BH00-0AB0/V1.0"
TABLE = "RowsIo"
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN with one central DI 16x24VDC HF at PLC-root scope; no software units, distributed I/O, "
    "F-I/O or drives, so only central input ranges back the cross-check's passed case.",
    "The unavailable status of the cross-check (unreadable hardware configuration) is covered by managed checks only.",
    "Comments are written in the scratch project's reference language only.",
    "Engineering only; nothing is downloaded and no signal is observed.",
]


def tags(h, plc):
    tables = [t for t in h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["tagTables"] if t["name"] == TABLE]
    require(len(tables) == 1, "Expected one %s table, found %d" % (TABLE, len(tables)))
    return {t["name"]: (t["dataType"], t["logicalAddress"]) for t in tables[0]["tags"]}


def body(h):
    plc = h.owned_scratch("tag rows")
    scope = {"plcPath": plc}
    paths = [r["path"] for r in h.tool("hardware_inspect", {"maxItems": 2000})["items"]]
    # The rail's name follows the TIA user interface language (Rail_0, Profilschiene_0): it is the station's item beside the CPU.
    rails = [p for p in paths if "/devices/ScratchStation/items/" in p and "/" not in p.split("/devices/ScratchStation/items/")[1] and not p.endswith("/ScratchPLC")]
    require(len(rails) == 1, "Expected one rail in the scratch: %s" % paths)
    change(h, "hardware_plug", {"hardwarePath": rails[0], "typeIdentifier": MODULE, "name": "RowsDi", "positionNumber": 2}, {})
    inputs = [a for r in h.tool("hardware_inspect", {"maxItems": 2000})["items"] if "/RowsDi" in r["path"]
              for a in r.get("addresses") or [] if a["ioType"] == "Input" and (a.get("lengthBits") or a.get("length") or 0) >= 16]
    require(inputs, "The plugged DI module exposes no 16-bit input range")
    start = h.report["inputStartByte"] = inputs[0]["startAddress"]

    # Create: German mnemonic and upper-case type are normalized; both input bits lie on the module.
    create = {"tagTableName": TABLE, "tags": [
        {"name": "Rows_Start", "dataType": "BOOL", "logicalAddress": "e%d.0" % start, "comment": "-S1 start"},
        {"name": "Rows_Stop", "dataType": "Bool", "logicalAddress": "%%I%d.1" % start},
        {"name": "Rows_Level", "dataType": "word", "logicalAddress": "mw10"}]}
    prepared, _ = change(h, "tag_table_import_xml", create, scope)
    cross = prepared["preview"]["hardwareCrossCheck"]
    require(cross["status"] == "passed" and cross["ioTagCount"] == 2 and cross["findingCount"] == 0, "Cross-check of configured inputs: %s" % cross)
    require({"tagRows": {"tagTableName": TABLE, "tableAction": "create", "tagsAdded": 3, "tagsUpdated": 0}} in prepared["preview"]["effects"],
            "Create effects: %s" % prepared["preview"]["effects"])
    expected = {"Rows_Start": ("Bool", "%%I%d.0" % start), "Rows_Stop": ("Bool", "%%I%d.1" % start), "Rows_Level": ("Word", "%MW10")}
    h.stage("rows created the table", lambda: require(tags(h, plc) == expected, "Tags after create: %s" % tags(h, plc)) or expected)

    # Update and add: Rows_Level is not named and must stay; three findings are reported and the import still applies.
    update = {"tagTableName": TABLE.lower(), "tags": [
        {"name": "rows_stop", "dataType": "Bool", "logicalAddress": "%%I%d.7" % (start + 1), "comment": "-S2 stop"},
        {"name": "Rows_Lamp", "dataType": "Bool", "logicalAddress": "%Q500.0"},
        {"name": "Rows_Far", "dataType": "Bool", "logicalAddress": "%%I%d.0" % (start + 2)},
        {"name": "Rows_Wide", "dataType": "Word", "logicalAddress": "%%IB%d" % start}]}
    prepared = h.stage("prepare rows with findings", lambda: h.tool("change_prepare", {"kind": "tag_table_import_xml", "payload": update, **scope}))
    cross = prepared["preview"]["hardwareCrossCheck"]
    found = sorted((f["tag"], f["finding"]) for f in cross["findings"])
    require(cross["status"] == "findings" and cross["blocking"] is False and found ==
            [("Rows_Far", "noConfiguredAddress"), ("Rows_Lamp", "noConfiguredAddress"), ("Rows_Wide", "dataTypeWidthMismatch")], "Findings: %s" % cross)
    require(any("does not block the import" in w for w in prepared["preview"]["warnings"]), "No cross-check warning: %s" % prepared["preview"]["warnings"])
    h.report["findings"] = cross

    # Rows_Wide (Word on a byte address) is a declaration V21 may refuse; the other findings must import.
    wide = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
    outcome = wide.get("structuredContent") or {}
    h.report["widthMismatchApply"] = {"isError": wide.get("isError") is True, "state": outcome.get("state") or (outcome.get("error") or {}).get("code"),
                                     "message": (outcome.get("error") or {}).get("message")}
    if wide.get("isError") is True or outcome.get("state") != "applied":
        status = h.tool("change_status", {"changeId": prepared["changeId"]})
        h.report["widthMismatchApply"]["status"] = status.get("state")
        require(status.get("state") in ("not_applied", "rolled_back", "restored") and tags(h, plc) == expected,
                "A refused width mismatch must leave the table unchanged: %s / %s" % (status.get("state"), tags(h, plc)))
        update["tags"].pop()
        prepared, _ = change(h, "tag_table_import_xml", update, scope)
        require(prepared["preview"]["hardwareCrossCheck"]["findingCount"] == 2, "Findings without the width mismatch: %s" % prepared["preview"]["hardwareCrossCheck"])
    else:
        expected["Rows_Wide"] = ("Word", "%%IB%d" % start)
    require({"tagRows": {"tagTableName": TABLE, "tableAction": "update", "tagsAdded": len(update["tags"]) - 1, "tagsUpdated": 1}} in prepared["preview"]["effects"],
            "Update effects: %s" % prepared["preview"]["effects"])
    expected.update({"Rows_Stop": ("Bool", "%%I%d.7" % (start + 1)), "Rows_Lamp": ("Bool", "%Q500.0"), "Rows_Far": ("Bool", "%%I%d.0" % (start + 2))})
    h.stage("rows with findings imported and kept the unnamed tag", lambda: require(tags(h, plc) == expected, "Tags after update: %s" % tags(h, plc)) or expected)

    # Refusals leave the table unchanged.
    for label, payload, code in (
            ("rows together with xml", {**create, "xml": "<Document/>"}, "InvalidRequest"),
            ("a symbolic address", {"tagTableName": TABLE, "tags": [{"name": "Rows_Bad", "dataType": "Bool", "logicalAddress": "Tag_1"}]}, "InvalidRequest"),
            ("a comment in an inactive language", {"tagTableName": TABLE, "commentCulture": "ja-JP",
                                                   "tags": [{"name": "Rows_Jp", "dataType": "Bool", "logicalAddress": "%M20.0", "comment": "x"}]}, "InvalidRequest")):
        def refused(payload=payload, code=code):
            error = h.call_error("change_prepare", {"kind": "tag_table_import_xml", "payload": payload, **scope})
            require(error.get("code") == code, "Expected %s, got %s" % (code, error))
            return error
        h.stage("refuse " + label, refused)
    h.stage("refusals left the table unchanged", lambda: require(tags(h, plc) == expected, "Tags after refusals: %s" % tags(h, plc)) or expected)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "tr_", "Row form of tag_table_import_xml and its advisory hardware cross-check in one owned scratch", LIMITATIONS, []).run(body)
