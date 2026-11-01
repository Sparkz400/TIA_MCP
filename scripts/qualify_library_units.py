"""Qualify library consumption in a software unit with a namespace preset, the one-instance-per-device rule, the unit-aware
cleanup refusal and an explicit namespace on a library instance: on an FB root, on a UDT dependency that the namespaced FB then
reuses, on a master copy, kept by a later reachable library update of the namespaced FB, and on an FB root whose UDT is created
alongside it (the UDT keeps its type's empty namespace)."""
from __future__ import annotations

import argparse
from pathlib import Path
import shutil

from library_native_harness import Harness, identity, require

ROOT = Path(__file__).resolve().parents[1]
FB, UDT, UNIT = "TiaMcpLibraryGate", "TiaMcpLibraryState", "TiaMcp_LibUnit"
# An FB that depends on its UDT, and a plain FC for a master copy.
DEPENDENT_FB, DEPENDENCY = "TiaMcpComponentGate", "TiaMcpComponentState"
MASTER_FC = "TiaMcpUnitMasterFc"
# A copy of the dependent FB and its UDT under other names, so the target holds neither when the FB is instantiated.
CREATED_FB, CREATED_UDT = "TiaMcpUnitCreatedGate", "TiaMcpUnitCreatedState"
MASTER_SOURCE = 'FUNCTION "' + MASTER_FC + """" : Void
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
BEGIN
END_FUNCTION
"""
# An OB in the unit's namespace that reaches every library use of the PLC, so a PLC-wide update can prove each one
# reachable: the namespaced FB, the dependent FB (and through it its UDT), the un-namespaced UDT of the first
# instantiation through a DB member, and the master-copy FC. V21 resolves unqualified names in a NAMESPACE SCL source
# against the root namespace (31 October, artifacts/lu1 and lu2), so every namespaced type and block is qualified;
# only the un-namespaced UDT keeps its simple name.
CALLER_SOURCE = "NAMESPACE " + UNIT + """
DATA_BLOCK "TiaMcpUnitNamespaced_Gate"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
NON_RETAIN
"%s"
BEGIN
END_DATA_BLOCK

DATA_BLOCK "TiaMcpUnitNamespaced_Component"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
NON_RETAIN
"%s"
BEGIN
END_DATA_BLOCK

DATA_BLOCK "TiaMcpUnitNamespaced_State"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
NON_RETAIN
VAR
    State : "%s";
END_VAR
BEGIN
END_DATA_BLOCK

ORGANIZATION_BLOCK "TiaMcpUnitNamespaced_Cycle"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
VAR_TEMP
    Active : Bool;
    ComponentActive : Bool;
END_VAR
BEGIN
    "%s.TiaMcpUnitNamespaced_Gate"(Enable := TRUE, Active => #Active);
    "%s.TiaMcpUnitNamespaced_Component"(Enable := #Active, Active => #ComponentActive);
    "%s.TiaMcpUnitNamespaced_State".State.Active := #ComponentActive;
    "%s"();
END_ORGANIZATION_BLOCK
END_NAMESPACE
""" % (UNIT + "." + FB, UNIT + "." + DEPENDENT_FB, UDT, UNIT, UNIT, UNIT, UNIT + "." + MASTER_FC)
CALLERS = ["TiaMcpUnitNamespaced_Gate", "TiaMcpUnitNamespaced_Component", "TiaMcpUnitNamespaced_State", "TiaMcpUnitNamespaced_Cycle"]


def namespaced(software, unit, names):
    """Path of each named block/type in the unit; every one must sit in the unit's namespace."""
    rows = [x for key in ("blocks", "types") for x in software[key] if x["path"].rsplit("/", 1)[-1] in names]
    paths = {x["path"].rsplit("/", 1)[-1]: x["path"] for x in rows if x.get("scopePath") == unit}
    require(len(rows) == len(names) and len(paths) == len(names), "Expected each object once, in the unit: %s" % rows)
    require(all("/namespaces/" + UNIT + "/" in path for path in paths.values()), "Not every object is in namespace %s: %s" % (UNIT, paths))
    return paths


def created_alongside(software, unit):
    """Paths of the namespaced FB and of the UDT created alongside it: the FB in the unit's namespace, the UDT in none."""
    rows = [x for key in ("blocks", "types") for x in software[key] if x["path"].rsplit("/", 1)[-1] in (CREATED_FB, CREATED_UDT)]
    paths = {x["path"].rsplit("/", 1)[-1]: x["path"] for x in rows if x.get("scopePath") == unit}
    require(len(rows) == 2 and len(paths) == 2, "Expected the FB and the UDT created alongside exactly once, in the unit: %s" % rows)
    require("/namespaces/" + UNIT + "/" in paths[CREATED_FB] and "/namespaces/" not in paths[CREATED_UDT], "Namespaces differ: %s" % paths)
    return paths


def alongside_documents(folder):
    """The component FB and UDT documents, byte for byte except for the two names."""
    folder.mkdir()
    for old, new, extension in ((DEPENDENT_FB, CREATED_FB, ".scl"), (DEPENDENCY, CREATED_UDT, ".udt")):
        data = (ROOT / "fixtures/libraries/component-qualification" / (old + extension)).read_bytes()
        (folder / (new + extension)).write_bytes(data.replace(DEPENDENT_FB.encode(), CREATED_FB.encode()).replace(DEPENDENCY.encode(), CREATED_UDT.encode()))
    return folder


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
    # A dependent FB with its UDT, and a master copy of a plain FC, in the same first generation.
    dependent_documents = h.directory / "dependent-documents"; dependent_documents.mkdir()
    for name in (DEPENDENT_FB + ".scl", DEPENDENCY + ".udt"):
        shutil.copyfile(ROOT / "fixtures/libraries/component-qualification" / name, dependent_documents / name)
    dependency = h.release(library, h.create_type(library, dependent_documents, DEPENDENCY, "types", plc), "1.0.0")
    dependent = h.release(library, h.create_type(library, dependent_documents, DEPENDENT_FB, "blocks", plc), "1.0.0")
    h.apply("library_publish", h.selected(library, [dependency], destinationLibraryId=global_library))
    h.apply("library_publish", h.selected(library, [dependent], destinationLibraryId=global_library))
    h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": MASTER_SOURCE, "expectedBlockNames": [MASTER_FC]})
    software = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})
    h.apply("library_create_master_copy", {"schemaVersion": 1, "destinationLibraryId": global_library, "target": {"plcPath": plc},
            "object": {"kind": "block", "objectPath": next(x["path"] for x in software["blocks"] if x["path"].rsplit("/", 1)[-1] == MASTER_FC)}})
    # A second dependent FB with its UDT, for the UDT created alongside the namespaced FB.
    alongside_folder = alongside_documents(h.directory / "alongside-documents")
    created_dependency = h.release(library, h.create_type(library, alongside_folder, CREATED_UDT, "types", plc), "1.0.0")
    created_root = h.release(library, h.create_type(library, alongside_folder, CREATED_FB, "blocks", plc), "1.0.0")
    h.apply("library_publish", h.selected(library, [created_dependency], destinationLibraryId=global_library))
    h.apply("library_publish", h.selected(library, [created_root], destinationLibraryId=global_library))
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

    # A namespace on a dependency: the UDT is instantiated as its own root in the namespace, then the namespaced FB reuses it.
    with_dependency = h.prepare("library_instantiate", h.selected(source, [dependency], target=in_unit, namespace=UNIT))
    h.apply_prepared("library_instantiate", with_dependency)
    dependent_prepared = h.prepare("library_instantiate", h.selected(source, [dependent], target=in_unit, namespace=UNIT))
    h.stage("the dependent FB reuses the namespaced UDT", lambda: require(
        [x.get("typeName") for x in dependent_prepared["preview"].get("reusedDependencies", [])] == [DEPENDENCY]
        and dependent_prepared["preview"].get("createdNames") == [DEPENDENT_FB],
        "Preview does not reuse the namespaced UDT: %s" % dependent_prepared["preview"].get("reusedDependencies")) or True)
    h.apply_prepared("library_instantiate", dependent_prepared)
    h.report["dependencyPlacement"] = h.stage("FB and its UDT both in namespace %s" % UNIT,
        lambda: namespaced(h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000}), unit, [DEPENDENT_FB, DEPENDENCY]))
    consumer = h.project_library()
    h.stage("dependent FB and its UDT used once each", lambda: h.verify_use(consumer, dependent, 1) and h.verify_use(consumer, dependency, 1))

    # A namespace on a master copy: the created block carries it.
    masters = [x for x in h.query(source)["items"] if x.get("kind") == "masterCopy"]
    require(len(masters) == 1, "Expected exactly the one master copy: %s" % masters)
    master = h.prepare("library_instantiate", {"schemaVersion": 1, "source": h.source_ref(source), "destinationComposition": "blocks",
                       "selection": [{"masterCopyPath": masters[0]["masterCopyPath"]}], "target": in_unit, "namespace": UNIT})
    h.stage("preview declares the namespace for the master copy", lambda: require(
        (master["preview"].get("instanceNamespace") or {}).get("masterCopies") == [MASTER_FC], "Preview lacks the master copy: %s" % master["preview"].get("instanceNamespace")) or True)
    h.apply_prepared("library_instantiate", master)
    h.report["masterPlacement"] = h.stage("the master-copy FC is in namespace %s" % UNIT,
        lambda: namespaced(h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000}), unit, [MASTER_FC]))

    def others_unmoved():
        observed = namespaced(h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000}), unit, [DEPENDENT_FB, DEPENDENCY, MASTER_FC])
        require(observed == {**h.report["dependencyPlacement"], **h.report["masterPlacement"]}, "The dependent FB, its UDT or the master copy moved: %s" % observed)
        return observed
    h.tool("library_close", {"libraryId": source})

    # Unit uses have no OB-rooted reference here, so a PLC-wide native update is refused before any write.
    source = h.open_archive(generation_two)
    h.refuse("refuse an update while unit uses have no OB-rooted reference", "library_update_project",
             h.selected(source, [second], updateScope="entireSelectedPlc", target={"plcPath": plc}), "LibraryUpdateCleanupRisk")
    def unchanged():
        require([x["version"] for x in h.versions(consumer, FB)] == ["1.0.0"], "The refused update copied a version into the project")
        return h.verify_use(consumer, fb, 1)
    h.stage("the unit stays at 1.0.0 and no newer version was copied", unchanged)

    # An OB in the namespace calls the namespaced FB, so the PLC-wide update proves the use reachable and keeps the namespace.
    def namespaced_callers():
        # V21 acceptance of a NAMESPACE-wrapped SCL source in a unit (and its references to the un-namespaced UDT) is unproven.
        try:
            return h.apply("source_upsert", {"scopePath": unit, "language": "SCL", "source": CALLER_SOURCE, "expectedBlockNames": CALLERS})["state"]
        except Exception as error:
            raise RuntimeError("V21 refused the NAMESPACE %s SCL caller source in the unit: %s" % (UNIT, error)) from error
    h.stage("import the OB-rooted callers inside NAMESPACE " + UNIT, namespaced_callers)
    update = h.prepare("library_update_project", h.selected(source, [second], updateScope="entireSelectedPlc", target={"plcPath": plc}))
    guard = update["preview"]["updateCleanupGuard"]
    h.report["cleanupGuard"] = guard
    h.stage("guard proved the namespaced FB use through the unit's OB", lambda: require(
        h.report["placement"][FB] in guard["reachablePaths"] and any(root.endswith("/TiaMcpUnitNamespaced_Cycle") for root in guard["obRoots"]),
        "The guard did not prove reachability: %s" % guard) or guard["reachablePaths"])
    h.apply_prepared("library_update_project", update)
    h.stage("FB at exactly 1.1.0", lambda: h.verify_use(consumer, second, 1))
    h.stage("the updated FB keeps namespace %s" % UNIT, lambda: require(placement() == h.report["placement"], "The update moved the FB or UDT") or True)
    h.stage("the dependent FB, its UDT and the master-copy FC keep namespace %s" % UNIT, others_unmoved)

    # A dependency created alongside a namespaced root: the target holds no UDT of the second pair, so the FB's
    # instantiation creates it. The namespace goes on the selected FB only; the UDT keeps its type's empty namespace, the
    # root namespace against which V21 resolves unqualified names. This follows the PLC-wide update, whose cleanup guard
    # would otherwise need an OB that reaches the new FB.
    alongside = h.prepare("library_instantiate", h.selected(source, [created_root], target=in_unit, namespace=UNIT))
    h.stage("the namespaced FB creates its UDT alongside", lambda: require(
        alongside["preview"].get("reusedDependencies") == [] and alongside["preview"].get("createdNames") == [CREATED_FB, CREATED_UDT]
        and (alongside["preview"].get("instanceNamespace") or {}).get("objects") == [{"typeName": CREATED_FB, "typeNamespace": ""}],
        "Preview does not create the UDT alongside the namespaced FB: %s" % alongside["preview"]) or alongside["preview"]["createdNames"])
    applied_alongside = h.apply_prepared("library_instantiate", alongside)
    h.stage("the FB and the UDT created alongside compile without errors", lambda: require(
        applied_alongside["journal"]["compile"]["errorCount"] == 0, "Compile differs: %s" % applied_alongside["journal"]["compile"]) or 0)
    h.report["alongsidePlacement"] = h.stage("FB in namespace %s and the UDT created alongside without, both in the unit" % UNIT,
        lambda: created_alongside(h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000}), unit))
    h.stage("the FB and the UDT created alongside used once each",
            lambda: h.verify_use(consumer, created_root, 1) and h.verify_use(consumer, created_dependency, 1))
    h.tool("library_close", {"libraryId": source})
    project_path = h.tool("tia_session_status")["projects"][0]["path"]
    h.stage("close consumer session", lambda: h.tool("tia_detach"))

    # The namespaced instantiation's checkpoint restores the project as it was before the FB.
    h.owned_portal("restore")
    def restore(change):
        restored = h.tool("change_restore", {"changeId": change["changeId"], "previewHash": change["previewHash"]})
        rows = restored["resources"]
        require(restored["state"] == "restored_to_new_locations" and [r["kind"] for r in rows] == ["project"] and rows[0]["state"] == "completed",
                "Checkpoint recovery differs: %s" % restored)
        return restored
    h.stage("restore the namespaced instantiation's checkpoint", lambda: restore(prepared))
    def restored_state():
        software = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})
        names = sorted(x["path"].rsplit("/", 1)[-1] for key in ("blocks", "types") for x in software[key] if x["path"].rsplit("/", 1)[-1] in (FB, UDT))
        require(names == [UDT], "Restored project differs: %s" % names)
        return h.verify_use(h.project_library(), udt, 1)
    h.stage("restored: the UDT only, no FB", restored_state)
    h.stage("close restore session", lambda: h.tool("tia_detach"))

    h.owned_portal("reopen")
    h.stage("reopen consumer", lambda: h.reopen_project(project_path))
    h.stage("reopened: namespaced FB at 1.1.0 and UDT intact", lambda: h.verify_use(h.project_library(), second, 1) and placement())
    h.stage("reopened: the dependent FB, its UDT and the master-copy FC in namespace %s" % UNIT, others_unmoved)
    def reopened_alongside():
        require(created_alongside(h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000}), unit) == h.report["alongsidePlacement"],
                "The FB or the UDT created alongside moved")
        library = h.project_library()
        return h.verify_use(library, created_root, 1) and h.verify_use(library, created_dependency, 1)
    h.stage("reopened: the FB in namespace %s and the UDT created alongside without" % UNIT, reopened_alongside)
    h.stage("close reopen session", lambda: h.tool("tia_detach"))

    # The created-alongside instantiation's checkpoint restores the updated project without that FB and UDT.
    h.owned_portal("restore alongside")
    h.stage("restore the created-alongside instantiation's checkpoint", lambda: restore(alongside))
    def restored_before_alongside():
        software = h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})
        names = [x["path"].rsplit("/", 1)[-1] for key in ("blocks", "types") for x in software[key]]
        require(CREATED_FB not in names and CREATED_UDT not in names, "The restored project still holds the FB or UDT created alongside: %s" % names)
        require(placement() == h.report["placement"], "The restored FB or UDT moved")
        others_unmoved()
        return h.verify_use(h.project_library(), second, 1)
    h.stage("restored: no FB or UDT created alongside, the namespaced objects in place and the FB at 1.1.0", restored_before_alongside)


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
        "The explicit namespace is exercised on three FB roots, a UDT root that the second FB then reuses, and one master-copy FC; the UDT created alongside the third FB keeps its type's empty namespace.",
        "No runtime execution."], [args.unit_seeder]).run(body)
