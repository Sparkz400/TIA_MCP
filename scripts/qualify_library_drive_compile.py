"""Qualify a library transaction's device compile in a project that holds a drive without a compiler service.

One owned scratch CPU 1511-1 PN gets a SINAMICS G120C PN (added without compile or save). library_create_master_copy of
the scratch's TiaMcp_IsEnabled block into the project library then compiles every device: the PLC station compiles and
the drive is listed under notCompiled. The plan, the applied change and the original lock manifest bytes are validated as
library observations: the drive covers its required target only as notCompiled, is reported as compilerNotCompiled and is
bound by the prepared device path/name scope; it is never counted as compiled.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from library_native_harness import Harness, require
from qualify_drives import DEVICE, add_drive
from validate_library_observations import validate_bytes

LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 with one SINAMICS G120C PN that is not on the CPU's IO system; one library_create_master_copy into the project library.",
    "Other library transactions compile through the same device compile and observation checks; they are not run here with a drive.",
]


def body(h):
    plc = h.owned_scratch("library drive compile")
    project = h.tool("tia_session_status")["projects"][0]["name"]
    drive = "/projects/%s/devices/%s" % (project, DEVICE)
    add_drive(h)
    library = h.project_library()
    block = next(b["path"] for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"] if b["name"] == "TiaMcp_IsEnabled")
    prepared = h.prepare("library_create_master_copy", {"schemaVersion": 1, "destinationLibraryId": library, "target": {"plcPath": plc},
                                                        "object": {"kind": "block", "objectPath": block}})
    def plan():
        result = validate_bytes(json.dumps(prepared["preview"]).encode(), "operation-plan")
        require(drive in prepared["preview"]["compileTargetPaths"], "The prepared compile scope lacks the drive: %s" % prepared["preview"]["compileTargetPaths"])
        return result
    h.report["plan"] = h.stage("validate the plan with the drive in its compile scope", plan)
    applied = h.apply_prepared("library_create_master_copy", prepared)
    def change():
        compiled = applied["journal"]["compile"]
        require([row["targetPath"] for row in compiled["notCompiled"]] == [drive] and drive not in [row["targetPath"] for row in compiled["targets"]],
                "Expected only the drive under notCompiled: %s" % compiled["notCompiled"])
        result = validate_bytes(json.dumps(applied).encode(), "compiler-change")
        require(result["compilerReportsSuccess"] is True and result["compilerNotCompiled"] == [drive] and result["compilerScopeBinding"] == "prepared-device-paths",
                "The observation does not report the drive as not compiled: %s" % result)
        return result
    h.report["change"] = h.stage("validate the applied change: the drive is reported as not compiled", change)
    def lock():
        manifest = applied["journal"]["lockManifest"]
        raw = Path(manifest["path"]).read_bytes()
        require(hashlib.sha256(raw).hexdigest() == manifest["sha256"], "The lock manifest changed")
        result = validate_bytes(raw, "compiler-lock", manifest["sha256"])
        require(result["compilerReportsSuccess"] is True and result["compilerNotCompiled"] == [drive], "The lock does not report the drive as not compiled: %s" % result)
        return result
    h.report["lock"] = h.stage("validate the original lock manifest bytes", lock)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "ld_", "Library transaction device compile in one owned scratch CPU 1511-1 PN with a SINAMICS G120C PN", LIMITATIONS, []).run(body)
