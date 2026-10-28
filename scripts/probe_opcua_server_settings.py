"""Native capture of the CPU OPC UA server attribute names on one owned scratch CPU 1511-1 PN.

Read-only after scratch creation: opcua_engineering_inspect plus hardware_inspect of the CPU and each
of its device items. Rows whose native attribute name looks like an OPC UA server setting are kept
with access mode, .NET type and default value. Observations of this scratch only, not qualification.
"""
from __future__ import annotations

import argparse
import re
from pathlib import Path

from library_native_harness import Harness

CANDIDATE = re.compile(r"opc|guest|anonym|accept|trust|licen|ua(?=[A-Z])", re.IGNORECASE)


def strings(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from strings(item)
    elif isinstance(value, list):
        for item in value:
            yield from strings(item)


def body(h):
    plc = h.owned_scratch("opcua server settings")
    cpu = plc.rsplit("/plc/", 1)[0]
    h.report.update(plc=plc, cpu=cpu, scratchCpu=h.scratch_cpu)
    h.report["opcuaInspect"] = h.stage("opcua_engineering_inspect", lambda: h.tool("opcua_engineering_inspect", {"plcPath": plc, "maxItems": 2000}))
    listing = h.stage("hardware_inspect all", lambda: h.tool("hardware_inspect", {"maxItems": 2000}))
    paths = sorted({row["path"] for row in listing.get("items", [])} | {s for s in strings(h.report["opcuaInspect"]) if s.startswith(cpu)})
    paths = [p for p in paths if p == cpu or p.startswith(cpu + "/")]
    candidates, names = [], {}
    for path in paths:
        item = h.stage("hardware_inspect " + path, lambda: h.tool("hardware_inspect", {"hardwarePath": path, "maxItems": 2000}))
        for row in item.get("items", []):
            names[row["path"]] = sorted(a["name"] for a in row.get("attributes", []))
            for attribute in row.get("attributes", []):
                if CANDIDATE.search(attribute["name"]):
                    candidates.append({"hardwarePath": row["path"], "itemName": row.get("name"), **{k: attribute.get(k) for k in
                                       ("name", "accessMode", "type", "value", "valueStatus")}})
        h.persist()
    h.report.update(itemPaths=paths, attributeNames=names, candidates=candidates)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "us_", "Native capture of CPU OPC UA server attribute names on an owned scratch CPU 1511-1 PN",
            ["Read-only capture after scratch creation; names and defaults of one scratch, not qualification."], []).run(body)
