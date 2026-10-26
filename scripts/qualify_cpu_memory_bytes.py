"""Qualify CPU system and clock memory bytes (DMC's remaining prerequisite) on every CPU of an owned scratch project."""
from __future__ import annotations

import argparse
from pathlib import Path
import re

from library_native_harness import Harness, require

NAMES = ("SystemMemoryByte", "SystemMemoryByteAddress", "ClockMemoryByte", "ClockMemoryByteAddress")
KIND = "hardware_cpu_memory_bytes"
# Added next to the scratch CPU 1511-1 PN; newest catalog firmware of each exact type name.
EXTRA_CPUS = ("CPU 1516-3 PN/DP", "CPU 1214C DC/DC/DC")
ENABLED = {"SystemMemoryByte": True, "SystemMemoryByteAddress": 20, "ClockMemoryByte": True, "ClockMemoryByteAddress": 21}
BLOCK = "TiaMcpMemoryBytes"


def version_key(entry):
    return tuple(int(x) for x in re.findall(r"\d+", entry.get("version") or "")), entry["typeIdentifier"]


def memory(h, cpu):
    items = h.tool("engineering_read", {"selectors": {"scope": "hardware", "hardwarePath": cpu, "maxItems": 1000}})["items"]
    require(len(items) == 1, "Expected one hardware item for " + cpu)
    attributes = {a["name"]: a for a in items[0]["attributes"]}
    return {name: attributes[name].get("value") for name in NAMES if name in attributes}, \
        [a for a in items[0]["attributes"] if "memory" in a["name"].lower()]


def tags(h, plc):
    """(table, name, address) of every PLC tag, from the unchanged native tag-table exports."""
    rows = []
    for table in h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["tagTables"]:
        xml = h.tool("tag_table_read", {"plcPath": plc, "tagTablePath": table["path"]})["xml"]
        for tag in re.findall(r"<SW\.Tags\.PlcTag\b.*?</SW\.Tags\.PlcTag>", xml, re.S):
            name = re.search(r"<Name>([^<]*)</Name>", tag).group(1)
            address = re.search(r"<LogicalAddress>([^<]*)</LogicalAddress>", tag)
            rows.append([table["path"], name, address.group(1) if address else None])
    return sorted(rows)


def apply(h, label, kind, payload, scope=None):
    prepared = h.stage("prepare " + label, lambda: h.tool("change_prepare", {"kind": kind, "payload": payload, **(scope or {})}))
    applied = h.apply_prepared(kind, prepared)
    if kind == KIND:
        require("configured_hardware_preservation_verified" in applied["stages"], "Hardware preservation was not verified")
        require([r for r in applied["readbacks"] if r["role"] == "configured-hardware-preservation"], "No preservation readback artifact")
    return prepared, applied


def body(h):
    plc = h.owned_scratch("cpu memory bytes")
    scratch_cpu = h.tool("tia_session_status")
    for name in EXTRA_CPUS:
        entries = [e for e in h.tool("hardware_catalog_search", {"filter": name.replace("CPU ", ""), "maxItems": 200})["entries"] if e.get("typeName") == name]
        require(entries, "No catalog entry for " + name)
        selected = max(entries, key=version_key)
        h.report.setdefault("addedCpus", []).append(selected)
        station = "Station" + re.sub(r"\W", "", name)[3:]
        apply(h, "add " + name, "hardware_add", {"typeIdentifier": selected["typeIdentifier"], "deviceName": station, "itemName": "PLC" + station[7:],
              "configureScratchSecurity": True})  # A new CPU needs its confidential-data password before it compiles.
    plcs = [p["path"] for p in h.tool("project_inventory", {"maxItems": 1000})["plcs"]]
    require(len(plcs) == 1 + len(EXTRA_CPUS) and plc in plcs, "Expected the scratch PLC and every added CPU")
    cpus = {p: p.rsplit("/plc/", 1)[0] for p in plcs}  # A PLC path extends its CPU device item with /plc/<name>.
    project_path = scratch_cpu["projects"][0]["path"]

    original, observed = {}, {}
    for p, cpu in cpus.items():
        def discover(cpu=cpu):
            values, metadata = memory(h, cpu)
            require(set(values) == set(NAMES), "CPU lacks the reviewed attributes; observed memory attributes: %s" % metadata)
            return {"values": values, "metadata": metadata}
        original[cpu] = h.stage("discover memory bytes on " + cpu, discover)["values"]
        observed[p] = tags(h, p)
    h.report["original"] = original

    first = next(iter(cpus.values()))
    hardware = [x["path"] for x in h.tool("project_inventory", {"maxItems": 1000})["hardware"]]
    non_cpu = next(x for x in hardware if x.startswith(first + "/") and set(memory(h, x)[0]) != set(NAMES))  # e.g. its PROFINET interface
    h.refuse("refuse a non-CPU item", KIND, {"hardwarePath": non_cpu, "attributes": {"SystemMemoryByte": True}}, "UnsupportedCapability")
    h.refuse("refuse the same enabled byte", KIND, {"hardwarePath": first, "attributes": {**ENABLED, "ClockMemoryByteAddress": 20}}, "AddressConflict")
    h.refuse("refuse a byte above 65535", KIND, {"hardwarePath": first, "attributes": {"ClockMemoryByteAddress": 65536}}, "InvalidRequest")
    h.refuse("refuse a string flag", KIND, {"hardwarePath": first, "attributes": {"ClockMemoryByte": "true"}}, "InvalidRequest")
    h.refuse("refuse an unchanged request", KIND, {"hardwarePath": first, "attributes": {"SystemMemoryByte": original[first]["SystemMemoryByte"]}}, "InvalidRequest")
    h.refuse("refuse an attribute outside the subset", KIND, {"hardwarePath": first, "attributes": {"Comment": "x"}}, "UnsupportedAttribute")
    require(all(original[c] != ENABLED for c in cpus.values()), "The scratch default already equals the requested setting")

    effects = {}
    for p, cpu in cpus.items():
        apply(h, "enable memory bytes on " + cpu, KIND, {"hardwarePath": cpu, "attributes": ENABLED})
        h.stage("read back memory bytes on " + cpu, lambda cpu=cpu: require(memory(h, cpu)[0] == ENABLED, "Native readback differs") or ENABLED)
        after = tags(h, p)
        effects[p] = {"added": [r for r in after if r not in observed[p]], "removed": [r for r in observed[p] if r not in after]}
        observed[p] = after
    h.report["tagEffects"] = effects

    # A block that reads one system memory bit and one clock memory bit compiles in every PLC.
    for p in plcs:
        system = [r for r in observed[p] if (r[2] or "").startswith("%M20.")]
        clock = [r for r in observed[p] if (r[2] or "").startswith("%M21.")]
        operands = ('"%s" OR "%s"' % (system[0][1], clock[0][1])) if system and clock else "%M20.0 OR %M21.5"
        h.report.setdefault("blockOperands", {})[p] = operands
        source = 'FUNCTION "%s" : Bool\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\nBEGIN\n\t#%s := %s;\nEND_FUNCTION\n' % (BLOCK, BLOCK, operands)
        apply(h, "compile a memory byte consumer in " + p, "source_upsert", {"scopePath": p, "language": "SCL", "source": source, "expectedBlockNames": [BLOCK]})

    configured = {c: memory(h, c)[0] for c in cpus.values()}
    software = h.inventory()
    h.stage("close changed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen saved project", lambda: h.tool("project_open", {"path": project_path}))

    def reopened():
        require({c: memory(h, c)[0] for c in cpus.values()} == configured, "Reopened memory bytes differ")
        require(h.inventory() == software, "Reopened blocks, types or tag tables differ")
        require({p: tags(h, p) for p in plcs} == observed, "Reopened tags differ")
        return configured
    h.stage("reopened memory bytes, blocks and tags", reopened)

    # Archive recovery: move both bytes to DMC's documented defaults, then restore the checkpoint.
    moved = {"SystemMemoryByteAddress": 1, "ClockMemoryByteAddress": 0}
    prepared, _ = apply(h, "move memory bytes on " + first, KIND, {"hardwarePath": first, "attributes": moved})
    h.stage("read back moved bytes", lambda: require(memory(h, first)[0] == {**ENABLED, **moved}, "Moved bytes differ") or moved)
    recovered = h.stage("restore the move from its checkpoint", lambda: h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
    require(recovered["state"] == "restored" and "configured_hardware_recovery_verified" in recovered["stages"], "Archive recovery was not verified")
    h.stage("recovered memory bytes, blocks and tags", reopened)

    # A byte Siemens refuses for this CPU stays recoverable through the same checkpoint.
    prepared = h.prepare(KIND, {"hardwarePath": first, "attributes": {"ClockMemoryByteAddress": 65535}})
    outcome = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
    content = outcome.get("structuredContent") or {}
    h.report["nativeBound"] = {"isError": outcome.get("isError"), "error": content.get("error"), "state": content.get("state")}
    if outcome.get("isError"):
        recovered = h.stage("restore the refused byte", lambda: h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
        require(recovered["state"] == "restored" and "configured_hardware_recovery_verified" in recovered["stages"], "Refused-byte recovery was not verified")
    else:
        h.report["nativeBound"]["accepted"] = memory(h, first)[0]
        apply(h, "move the clock byte back on " + first, KIND, {"hardwarePath": first, "attributes": {"ClockMemoryByteAddress": 21}})
    h.stage("memory bytes after the native bound", reopened)

    # Restore the original settings; the consumer block first stops reading the bytes.
    for p in plcs:
        apply(h, "release the consumer in " + p, "source_upsert", {"scopePath": p, "language": "SCL", "expectedBlockNames": [BLOCK],
              "source": 'FUNCTION "%s" : Bool\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\nBEGIN\n\t#%s := FALSE;\nEND_FUNCTION\n' % (BLOCK, BLOCK)})
    for cpu in cpus.values():
        apply(h, "restore original memory bytes on " + cpu, KIND, {"hardwarePath": cpu, "attributes": original[cpu]})
        h.stage("read back original memory bytes on " + cpu, lambda cpu=cpu: require(memory(h, cpu)[0] == original[cpu], "Original settings not restored") or original[cpu])
    h.report["finalTags"] = {p: tags(h, p) for p in plcs}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "cm_", "CPU system and clock memory bytes on every CPU of an owned scratch project", [
        "Qualified on the scratch CPU 1511-1 PN plus one CPU 1516-3 PN/DP and one CPU 1214C DC/DC/DC; other CPU families and firmware are not qualified.",
        "The DMC Siemens Open Library archive is not on this machine, so DMC FB compilation itself is not claimed; an authored block reading the bytes compiles instead.",
        "Runtime behavior of the memory bits is not observed."], []).run(body)
