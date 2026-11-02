"""Apply DMC's prerequisites, then compile one or all default PLC code blocks with their dependency closures."""
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

    versions = [x for x in all_items(h, library)["items"] if x.get("kind") == "version" and x.get("isDefault")
                and ("CodeBlockLibraryTypeVersion" in x.get("engineeringType", "") if h.args.all_default_blocks
                     else x.get("typeName") == "fbIO_DigitalInput")]
    require(versions and sum(x["typeName"] == "fbIO_DigitalInput" for x in versions) == 1, "Default DMC code-block inventory is incomplete")
    h.report["compiledVendorBlocks"] = versions
    # The production intake accepts at most 100 selected components per reviewed change.
    for start in range(0, len(versions), 100):
        installed = {x["versionGuid"] for x in all_items(h, h.project_library())["items"] if x.get("kind") == "version"}
        batch = []
        for version in versions[start:start + 100]:
            if version["versionGuid"] in installed:
                h.stage("DMC prior batch created dependency: " + version["typeName"], lambda v=version: h.verify_use(h.project_library(), v, 1))
            else:
                batch.append(version)
        if batch:
            h.apply("library_instantiate", h.selected(library, batch, target={"plcPath": plc}))
    project_library = h.project_library()
    for version in versions:
        h.stage("DMC exact library link: " + version["typeName"], lambda v=version: h.verify_use(project_library, v, 1))
    h.report["vendorFbExport"] = h.stage("read the compiled DMC FB", lambda:
        h.tool("block_read", {"plcPath": plc, "blockName": "fbIO_DigitalInput"}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--vendor-report", type=Path, required=True)
    parser.add_argument("--all-default-blocks", action="store_true", help="Compile every default PLC code-block version, not only fbIO_DigitalInput")
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "pq_", "DMC prerequisites and dependency compilation from the upgraded V21 library", [
        "One owned CPU1511 V4.1; compilation and exact library identity of the selected default code-block versions, not runtime behavior, HMI content or vendor master copies.",
        "Uses the vendor run's retained upgraded archive; the upgrade itself is the vendor evidence."], [args.vendor_report]).run(body)
