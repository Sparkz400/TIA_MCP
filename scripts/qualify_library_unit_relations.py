"""Qualify library types used across related software units: a provider unit holds the instances, a related consumer unit calls them, and a PLC-wide update follows the cross-unit references."""
from __future__ import annotations

import argparse
from pathlib import Path
import shutil

from library_native_harness import Harness, require

ROOT = Path(__file__).resolve().parents[1]
FB, UDT = "TiaMcpLibraryGate", "TiaMcpLibraryState"
PROVIDER, CONSUMER = "TiaMcp_LibProvider", "TiaMcp_LibConsumer"
CALLERS = ["TiaMcpUnitConsumer_Gate", "TiaMcpUnitConsumer_State", "TiaMcpUnitConsumer_Cycle"]
SOURCE = ROOT / "fixtures/libraries/unit-relations/UnitConsumer.scl"


def unit_scopes(scopes):
    """The two seeded units by name, and the consumer's single software-unit relation to the provider."""
    units = {s["name"]: s for s in scopes if s.get("name") in (PROVIDER, CONSUMER)}
    require(len(units) == 2, "Both seeded units must be visible: %s" % scopes)
    relations = units[CONSUMER].get("relations") or []
    require([(r.get("relationType"), r.get("relatedObject")) for r in relations] == [("SoftwareUnit", PROVIDER)],
            "The consumer unit must have exactly one software-unit relation to the provider: %s" % relations)
    require(not units[PROVIDER].get("relations"), "The provider unit must have no relations: %s" % units[PROVIDER].get("relations"))
    return units


def body(h):
    documents = h.directory / "documents"
    shutil.copytree(ROOT / "fixtures/libraries/type-lifecycle", documents)
    change = (ROOT / "fixtures/libraries/release-changes/TiaMcpLibraryGate.implementation.scl").read_text()

    # Authoring: generation 1 (UDT and FB 1.0.0) and generation 2 (FB 1.1.0).
    plc = h.owned_scratch("authoring")
    library = h.project_library()
    udt = h.release(library, h.create_type(library, documents, UDT, "types", plc), "1.0.0")
    fb = h.release(library, h.create_type(library, documents, FB, "blocks", plc), "1.0.0")
    global_library = h.apply("library_create", {"schemaVersion": 1, "name": "UnitRelationQualification"})["journal"]["result"]["library"]["libraryId"]
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

    # Consumer: a provider unit and a consumer unit related to it.
    plc = h.owned_scratch("consumer")
    h.report["relationSeed"] = h.seed("seed related software units", "related")
    units = h.stage("units and their relation are visible", lambda: unit_scopes(h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["softwareScopes"]))
    provider, consumer = units[PROVIDER]["path"], units[CONSUMER]["path"]
    h.report["unitScopes"] = units

    source = h.open_archive(generation_one)
    for version in (udt, fb):
        h.apply("library_instantiate", h.selected(source, [version], target={"plcPath": plc, "scopePath": provider}))
    # The one-instance-per-device rule holds across related units: neither the root nor its dependency is copied again.
    h.refuse("refuse the FB in the related consumer unit", "library_instantiate",
             h.selected(source, [fb], target={"plcPath": plc, "scopePath": consumer}), "LibraryConflict", "per device")
    h.refuse("refuse the UDT in the related consumer unit", "library_instantiate",
             h.selected(source, [udt], target={"plcPath": plc, "scopePath": consumer}), "LibraryConflict", "per device")
    h.tool("library_close", {"libraryId": source})

    # A related unit reaches only published objects of the provider unit.
    h.report["publishSeed"] = h.seed("publish the provider's library instances", "publish", PROVIDER, FB, UDT)
    h.apply("source_upsert", {"scopePath": consumer, "language": "SCL", "source": SOURCE.read_text(), "expectedBlockNames": CALLERS})

    def placement():
        software = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})
        rows = {x["name"]: x for key in ("blocks", "types") for x in software[key] if x["name"] in [FB, UDT] + CALLERS}
        require(sorted(rows) == sorted([FB, UDT] + CALLERS), "Expected each library instance and caller exactly once: %s" % sorted(rows))
        require(all(rows[n]["scopePath"] == provider for n in (FB, UDT)), "Library instances left the provider unit")
        require(all(rows[n]["scopePath"] == consumer for n in CALLERS), "Callers left the consumer unit")
        return {n: rows[n]["path"] for n in sorted(rows)}
    h.report["placement"] = h.stage("instances in the provider, callers in the consumer", placement)
    consumer_library = h.project_library()
    h.stage("FB 1.0.0 used once (in the provider)", lambda: h.verify_use(consumer_library, fb, 1))

    # The consumer's cycle OB calls the provider's FB through the relation, so the cleanup guard can prove reachability.
    source = h.open_archive(generation_two)
    applied = h.apply("library_update_project", h.selected(source, [second], updateScope="entireSelectedPlc", target={"plcPath": plc}))
    guard = applied["preview"]["updateCleanupGuard"]
    h.report["cleanupGuard"] = guard
    h.stage("guard proved the provider uses through the consumer OB", lambda: require(
        h.report["placement"][FB] in guard["reachablePaths"] and h.report["placement"]["TiaMcpUnitConsumer_Cycle"] in guard["obRoots"],
        "The guard did not prove reachability from the consumer unit's OB: %s" % guard) or guard["reachablePaths"])
    h.stage("FB at exactly 1.1.0 in the provider", lambda: h.verify_use(consumer_library, second, 1))
    h.stage("UDT stays at 1.0.0", lambda: h.verify_use(consumer_library, udt, 1))
    h.stage("placement unchanged after the update", lambda: require(placement() == h.report["placement"], "Paths changed in the update"))
    h.tool("library_close", {"libraryId": source})
    project_path = h.tool("tia_session_status")["projects"][0]["path"]
    h.stage("close consumer session", lambda: h.tool("tia_detach"))

    h.owned_portal("reopen")
    h.stage("reopen consumer", lambda: h.reopen_project(project_path))
    h.stage("reopened: relation intact", lambda: unit_scopes(h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["softwareScopes"]))
    h.stage("reopened: FB 1.1.0 and placement intact", lambda: h.verify_use(h.project_library(), second, 1) and placement())


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
    Harness(args, "ur_", "V21 library types used across two related software units", [
        "Units, the relation and the published flag are set by scripts/native/LibraryUnitSeeder (registered fixture tool), not by MCP routes.",
        "One relation of type SoftwareUnit; NonUnitDB and TODB relations are not exercised.",
        "No runtime execution."], [args.unit_seeder, SOURCE]).run(body)
