"""Qualify library consumption in a software unit with a namespace preset, the one-instance-per-device rule and the unit-aware cleanup refusal."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import shutil
import subprocess

from library_native_harness import Harness, require

ROOT = Path(__file__).resolve().parents[1]
FB, UDT, UNIT = "TiaMcpLibraryGate", "TiaMcpLibraryState", "TiaMcp_LibUnit"


def body(h):
    documents = h.directory / "documents"
    shutil.copytree(ROOT / "fixtures/libraries/type-lifecycle", documents)
    change = (ROOT / "fixtures/libraries/release-changes/TiaMcpLibraryGate.implementation.scl").read_text()

    # Authoring: generation 1 (FB 1.0.0) and generation 2 (FB 1.1.0).
    plc = h.owned_scratch("authoring")
    library = h.project_library()
    udt = h.release(library, h.create_type(library, documents, UDT, "types", plc), "1.0.0")
    fb = h.release(library, h.create_type(library, documents, FB, "blocks", plc), "1.0.0")
    global_library = h.apply("library_create", {"schemaVersion": 1, "name": "UnitQualification"})["journal"]["result"]["library"]["libraryId"]
    h.apply("library_publish", h.selected(library, [udt], destinationLibraryId=global_library))
    h.apply("library_publish", h.selected(library, [fb], destinationLibraryId=global_library))
    generation_one = h.archive(global_library)
    second = h.release(library, h.edit_source(library, plc, fb, FB, change), "1.1.0")
    h.apply("library_set_default", h.selected(library, [second]))
    h.apply("library_publish", h.selected(library, [second], destinationLibraryId=global_library))
    h.apply("library_set_default", h.selected(global_library, [second]))
    generation_two = h.archive(global_library)
    h.tool("library_close", {"libraryId": global_library})
    h.stage("close authoring session", lambda: h.tool("tia_detach"))

    # Consumer: seed one software unit with a namespace preset through the registered native fixture tool.
    plc = h.owned_scratch("consumer")
    status = h.tool("tia_session_status")
    authorization = h.directory / "unit-authorization.json"
    authorization.write_text(json.dumps({"processId": status["processId"], "projectPath": status["projects"][0]["path"],
                                         "ownsPortalProcess": True, "creationTool": "tia_create_scratch"}), encoding="utf-8")

    def seed():
        completed = subprocess.run([str(h.args.unit_seeder), str(authorization)], capture_output=True, text=True, encoding="utf-8", timeout=300)
        result = json.loads(completed.stdout.strip().splitlines()[-1])
        require(completed.returncode == 0 and result.get("completed") is True and result.get("namespacePreset") == UNIT, "Unit seeding failed: %s" % result)
        return result
    h.stage("seed software unit " + UNIT, seed)
    scopes = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["softwareScopes"]
    units = [s for s in scopes if s.get("name") == UNIT]
    require(len(units) == 1 and units[0].get("namespacePreset") == UNIT, "The seeded unit is not visible with its namespace preset: %s" % scopes)
    unit = units[0]["path"]
    h.report["unitScope"] = units[0]

    source = h.open_archive(generation_one)
    in_unit = {"plcPath": plc, "scopePath": unit}
    for version in (udt, fb):
        h.apply("library_instantiate", h.selected(source, [version], target=in_unit))

    def placement():
        software = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})
        rows = [x for key in ("blocks", "types") for x in software[key] if x["path"].rsplit("/", 1)[-1] in (FB, UDT)]
        in_scope = [x["path"] for x in rows if x.get("scopePath") == unit]
        require(len(in_scope) == 2 and len(rows) == 2, "Expected the FB and UDT exactly once, in the unit: %s" % rows)
        return {"unit": in_scope, "unitNamespaced": all("/namespaces/" + UNIT + "/" in p for p in in_scope)}
    h.report["placement"] = h.stage("FB and UDT exist once, in the unit", placement)
    consumer = h.project_library()
    h.stage("FB 1.0.0 used once (in the unit)", lambda: h.verify_use(consumer, fb, 1))
    # TIA allows one instance of a library type per device; a second scope is refused before any write.
    h.refuse("refuse the FB in the PLC root while the unit holds it", "library_instantiate",
             h.selected(source, [fb], target={"plcPath": plc}), "LibraryConflict", "per device")
    h.refuse("refuse a second UDT copy in the unit", "library_instantiate", h.selected(source, [udt], target=in_unit), "LibraryConflict")
    h.tool("library_close", {"libraryId": source})

    # Unit uses have no OB-rooted reference here, so a PLC-wide native update is refused before any write.
    source = h.open_archive(generation_two)
    h.refuse("refuse an update while unit uses have no OB-rooted reference", "library_update_project",
             h.selected(source, [second], updateScope="entireSelectedPlc", target={"plcPath": plc}), "LibraryUpdateCleanupRisk")
    def unchanged():
        require([x["version"] for x in h.versions(consumer, FB)] == ["1.0.0"], "The refused update copied a version into the project")
        return h.verify_use(consumer, fb, 1)
    h.stage("the unit stays at 1.0.0 and no newer version was copied", unchanged)
    h.tool("library_close", {"libraryId": source})
    project_path = h.tool("tia_session_status")["projects"][0]["path"]
    h.stage("close consumer session", lambda: h.tool("tia_detach"))

    h.owned_portal("reopen")
    h.stage("reopen consumer", lambda: h.tool("project_open", {"path": project_path}))
    h.stage("reopened: unit use intact", lambda: h.verify_use(h.project_library(), fb, 1) and placement())


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--unit-seeder", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "un_", "V21 library consumption in a software unit with a namespace preset beside the PLC root", [
        "One software unit without unit relations; unit-to-root calls are not exercised, so unit uses are conservatively unreachable.",
        "The unit is created by scripts/native/LibraryUnitSeeder (a registered fixture tool), not by an MCP route.",
        "No runtime execution."], [args.unit_seeder]).run(body)
