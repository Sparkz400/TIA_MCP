"""Apply DMC's memory-byte and tag-table prerequisites, then compile its digital-input FB and dependency closure."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
from urllib.parse import quote

from library_native_harness import Harness, require
from qualify_library_vendor import all_items
from qualify_cpu_memory_bytes import ENABLED, memory

TABLE = "Open Library"


def body(h):
    # The input is the upgraded, re-archived DMC V19 PLC library retained by the vendor run.
    vendor = json.loads(Path(h.args.vendor_report).read_text(encoding="utf-8"))
    require(vendor.get("completed") is True, "The vendor report must be a completed run")
    archive = [s for s in vendor["steps"] if s["name"] == "apply library_archive"][0]["result"]["journal"]["result"]["archive"]
    source_archive = h.directory / "inputs" / Path(archive["path"]).name
    source_archive.parent.mkdir()
    shutil.copy2(archive["path"], source_archive)
    require(hashlib.sha256(source_archive.read_bytes()).hexdigest() == archive["sha256"], "The retained DMC V21 archive changed")
    h.report["input"] = {"vendorReport": str(h.args.vendor_report), "archive": archive}

    plc = h.owned_scratch("dmc prerequisites")
    cpu = plc.rsplit("/plc/", 1)[0]
    h.apply("hardware_cpu_memory_bytes", {"hardwarePath": cpu, "attributes": ENABLED})
    h.stage("DMC system and clock memory bytes read back", lambda:
            require(memory(h, cpu)[0] == ENABLED, "The CPU memory bytes differ from the prerequisite") or ENABLED)
    library = h.stage("open upgraded DMC library", lambda: h.tool("library_open", {"request": {
        "path": str(source_archive), "expectedSha256": archive["sha256"]}}))["libraryId"]
    masters = [x for x in all_items(h, library)["items"] if x.get("kind") == "masterCopy" and x.get("name") == TABLE]
    require(len(masters) == 1 and not masters[0].get("unavailable"), "Expected one readable 'Open Library' master copy")
    payload = {"schemaVersion": 1, "source": h.source_ref(library), "selection": [{"masterCopyPath": masters[0]["masterCopyPath"]}],
               "destinationComposition": "tagTables", "target": {"plcPath": plc}}
    h.apply("library_instantiate", payload)

    def table_present():
        tables = [path for path, _ in h.inventory()[plc]["tagTables"]]
        require(sum(path.endswith(("/" + TABLE, "/" + quote(TABLE))) for path in tables) == 1, "The tag table was not created exactly once: %s" % tables)
        return {"tagTables": tables}
    h.stage("DMC tag table exists in PLC tags", table_present)
    h.refuse("refuse a second copy of the same tag table", "library_instantiate",
             {**payload, "source": h.source_ref(library)}, "LibraryConflict", "tag table")

    versions = [x for x in all_items(h, library)["items"] if x.get("kind") == "version"
                and x.get("isDefault") and x.get("typeName") == "fbIO_DigitalInput"]
    require(len(versions) == 1, "Expected one default DMC fbIO_DigitalInput version")
    h.report["compiledVendorFb"] = versions[0]
    h.apply("library_instantiate", h.selected(library, versions, target={"plcPath": plc}))
    h.stage("DMC FB exact library link in consumer", lambda: h.verify_use(h.project_library(), versions[0], 1))
    h.report["vendorFbExport"] = h.stage("read the compiled DMC FB", lambda:
        h.tool("block_read", {"plcPath": plc, "blockName": "fbIO_DigitalInput"}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--vendor-report", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "pq_", "DMC prerequisites and fbIO_DigitalInput dependency compilation from the upgraded V21 library", [
        "One owned CPU1511 V4.1; compilation and exact library identity of fbIO_DigitalInput only, not runtime behavior or all DMC blocks.",
        "Uses the vendor run's retained upgraded archive; the upgrade itself is the vendor evidence."], [args.vendor_report]).run(body)
