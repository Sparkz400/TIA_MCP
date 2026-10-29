"""Qualify library consumption in a software unit with a namespace preset, the one-instance-per-device rule, the unit-aware
cleanup refusal and an explicit namespace on a library instance."""
from __future__ import annotations

import argparse
from pathlib import Path
import shutil

from library_native_harness import Harness, identity, require

ROOT = Path(__file__).resolve().parents[1]
FB, UDT, UNIT = "TiaMcpLibraryGate", "TiaMcpLibraryState", "TiaMcp_LibUnit"


def body(h):
    documents = h.directory / "documents"
    shutil.copytree(ROOT / "fixtures/libraries/type-lifecycle", documents)
    change = (ROOT / "fixtures/libraries/release-changes/TiaMcpLibraryGate.implementation.scl").read_text(encoding="utf-8")

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
    seeded = h.seed("seed software unit " + UNIT)
    require(seeded.get("namespacePreset") == UNIT, "Unit seeding failed: %s" % seeded)
    scopes = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["softwareScopes"]
    units = [s for s in scopes if s.get("name") == UNIT]
    require(len(units) == 1 and units[0].get("namespacePreset") == UNIT, "The seeded unit is not visible with its namespace preset: %s" % scopes)
    unit = units[0]["path"]
    h.report["unitScope"] = units[0]

    source = h.open_archive(generation_one)
    in_unit = {"plcPath": plc, "scopePath": unit}
    # TIA does not apply the unit's namespace preset to library instances: the UDT keeps its type's empty namespace.
    h.apply("library_instantiate", h.selected(source, [udt], target=in_unit))
    # An explicit namespace is reviewed in the preview and set on the created FB.
    h.refuse("refuse an explicit namespace in the PLC root", "library_instantiate",
             h.selected(source, [fb], target={"plcPath": plc}, namespace=UNIT), "TargetIncompatible", "software-unit")
    h.refuse("refuse a malformed namespace", "library_instantiate",
             h.selected(source, [fb], target=in_unit, namespace="1 bad"), "InvalidRequest", "identifiers")
    prepared = h.prepare("library_instantiate", h.selected(source, [fb], target=in_unit, namespace=UNIT))

    def namespace_preview():
        row = prepared["preview"].get("instanceNamespace") or {}
        require(row.get("namespace") == UNIT and row.get("unitNamespacePreset") == UNIT and row.get("objects") == [{"typeName": FB, "typeNamespace": ""}],
                "Preview does not declare the explicit namespace: %s" % row)
        return row
    h.stage("preview declares namespace %s for the FB" % UNIT, namespace_preview)
    applied = h.apply_prepared("library_instantiate", prepared)

    def created():
        rows = applied["journal"]["result"]["objects"]
        require(len(rows) == 1 and rows[0]["object"].get("Namespace") == UNIT and rows[0]["linkVerified"] is True
                and identity(rows[0]["version"]) == identity(fb), "Created FB differs: %s" % rows)
        return rows[0]
    h.stage("the created FB carries the namespace and its exact link", created)

    def placement():
        software = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})
        rows = [x for key in ("blocks", "types") for x in software[key] if x["path"].rsplit("/", 1)[-1] in (FB, UDT)]
        paths = {x["path"].rsplit("/", 1)[-1]: x["path"] for x in rows if x.get("scopePath") == unit}
        require(len(rows) == 2 and len(paths) == 2, "Expected the FB and UDT exactly once, in the unit: %s" % rows)
        require("/namespaces/" + UNIT + "/" in paths[FB] and "/namespaces/" not in paths[UDT], "Instance namespaces differ: %s" % paths)
        return paths
    h.report["placement"] = h.stage("FB in namespace %s and UDT without, both in the unit" % UNIT, placement)
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

    # The namespaced instantiation's checkpoint restores the project as it was before the FB.
    h.owned_portal("restore")
    def restore():
        restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        rows = restored["resources"]
        require(restored["state"] == "restored_to_new_locations" and [r["kind"] for r in rows] == ["project"] and rows[0]["state"] == "completed",
                "Checkpoint recovery differs: %s" % restored)
        return restored
    h.stage("restore the namespaced instantiation's checkpoint", restore)
    def restored_state():
        software = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})
        names = sorted(x["path"].rsplit("/", 1)[-1] for key in ("blocks", "types") for x in software[key] if x["path"].rsplit("/", 1)[-1] in (FB, UDT))
        require(names == [UDT], "Restored project differs: %s" % names)
        return h.verify_use(h.project_library(), udt, 1)
    h.stage("restored: the UDT only, no FB", restored_state)
    h.stage("close restore session", lambda: h.tool("tia_detach"))

    h.owned_portal("reopen")
    h.stage("reopen consumer", lambda: h.reopen_project(project_path))
    h.stage("reopened: namespaced FB and UDT intact", lambda: h.verify_use(h.project_library(), fb, 1) and placement())


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
        "The explicit namespace is exercised on one FB root whose UDT dependency is reused; created dependencies keep their type namespace, unexercised.",
        "No runtime execution."], [args.unit_seeder]).run(body)
