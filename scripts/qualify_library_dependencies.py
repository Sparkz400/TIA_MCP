"""Qualify nested FB/UDT dependency closure, exact reuse, collisions, defaults and removed-reference cleanup refusal."""
from __future__ import annotations

import argparse
from pathlib import Path
import shutil

from library_native_harness import Harness, identity, require

ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / "fixtures/libraries/dependencies"
INNER, OUTER, CHILD, PARENT = "TiaMcpDepInner", "TiaMcpDepOuter", "TiaMcpDepChild", "TiaMcpDepParent"
# Authored from the fixture documents: who requires whom, independent of the worker's closure code.
EXPECTED_EDGES = {"1.0.0": {PARENT: {CHILD, OUTER}, CHILD: {OUTER}, OUTER: {INNER}, INNER: set()},
                  "1.1.0": {PARENT: {OUTER}, OUTER: {INNER}, INNER: set()}}


def closure_edges(rows):
    names = {row["versionGuid"]: row["typeName"] for row in rows}
    require(all(g in names for row in rows for g in row["requiredVersionGuids"]), "Closure references a version outside itself")
    return {row["typeName"]: {names[g] for g in row["requiredVersionGuids"]} for row in rows}


def body(h):
    documents = h.directory / "documents"
    documents.mkdir()
    for path in FIXTURES.glob("*.*"):
        shutil.copy2(path, documents)
    collision = h.directory / "collision"
    shutil.copytree(FIXTURES / "collision", collision)

    def dependencies(library, version, number):
        result = h.query(library, query="dependencies", **identity(version))
        require(result["completeness"]["scopeComplete"] and result.get("nextOffset") is None, "Dependency closure incomplete")
        edges = closure_edges(result["items"])
        require(edges == EXPECTED_EDGES[number], "Closure differs from the authored graph: %s" % edges)
        return result

    # Authoring: Inner <- Outer (member and array) <- Child <- Parent (multi-instance and input).
    plc = h.owned_scratch("authoring")
    library = h.project_library()
    released = {}
    for name, composition in ((INNER, "types"), (OUTER, "types"), (CHILD, "blocks"), (PARENT, "blocks")):
        released[name] = h.release(library, h.create_type(library, documents, name, composition, plc), "1.0.0")
    h.stage("closure of Parent 1.0.0", lambda: dependencies(library, released[PARENT], "1.0.0"))
    global_library = h.apply("library_create", {"schemaVersion": 1, "name": "DependencyQualification"})["journal"]["result"]["library"]["libraryId"]
    h.apply("library_publish", h.selected(library, [released[PARENT]], destinationLibraryId=global_library))

    def published_closure():
        for name, version in released.items():
            rows = [x for x in h.versions(global_library, name) if identity(x) == identity(version)]
            require(len(rows) == 1 and rows[0]["state"] == "Committed", "Publishing Parent did not carry exact dependency " + name)
        return {"types": sorted(released)}
    h.stage("publishing the root carries its exact closure", published_closure)
    generation_one = h.archive(global_library)

    # Parent 1.1.0 no longer uses Child: a reference removed by an update.
    in_work = h.edit_source(library, plc, released[PARENT], PARENT, (FIXTURES / "changes" / (PARENT + ".scl")).read_text())
    parent_two = h.release(library, in_work, "1.1.0")
    h.stage("closure of Parent 1.1.0", lambda: dependencies(library, parent_two, "1.1.0"))
    h.apply("library_set_default", h.selected(library, [parent_two]))
    h.apply("library_publish", h.selected(library, [parent_two], destinationLibraryId=global_library))
    h.apply("library_set_default", h.selected(global_library, [parent_two]))
    generation_two = h.archive(global_library)
    h.report["nativeVersions"] = {**released, PARENT + "@1.1.0": parent_two}
    h.tool("library_close", {"libraryId": global_library})
    h.stage("close authoring session", lambda: h.tool("tia_detach"))

    # Consumer: exact reuse, multiple instances, no-op update, downgrade and cleanup refusals.
    consumer = h.owned_scratch("consumer")
    target = {"plcPath": consumer}
    source = h.open_archive(generation_one)
    h.apply("library_instantiate", h.selected(source, [released[INNER]], target=target))
    prepared = h.prepare("library_instantiate", h.selected(source, [released[PARENT]], target=target))

    def reuse_preview():
        preview = prepared["preview"]
        reused = preview["reusedDependencies"]
        require([(x["typeName"], x["versionGuid"]) for x in reused] == [(INNER, released[INNER]["versionGuid"])],
                "Preview does not reuse exactly the installed Inner version")
        require(sorted(preview["createdNames"]) == sorted([CHILD, OUTER, PARENT]), "Preview creates unexpected objects")
        return {"reusedDependencies": reused, "createdNames": preview["createdNames"]}
    h.stage("preview reuses the installed exact dependency", reuse_preview)
    h.apply_prepared("library_instantiate", prepared)
    consumer_library = h.project_library()
    for name, version in released.items():
        h.stage("consumer exact link " + name, lambda v=version: h.verify_use(consumer_library, v, 1))
    h.apply("source_upsert", {"scopePath": consumer, "language": "SCL", "source": (documents / "ConsumerReferences.scl").read_text(),
            "expectedBlockNames": [PARENT + "_DB", PARENT + "_DB2", "Main"]})
    h.stage("two instance DBs keep one exact Parent link", lambda: h.verify_use(consumer_library, released[PARENT], 1))
    no_op = h.apply("library_update_project", h.selected(source, [released[PARENT]], updateScope="entireSelectedPlc", target=target))
    require(no_op["journal"]["result"].get("noOp") is True, "Exact-version update was not a no-op")
    h.tool("library_close", {"libraryId": source})

    source = h.open_archive(generation_two)
    h.refuse("refuse non-default older version from a newer source", "library_update_project",
             h.selected(source, [released[PARENT]], updateScope="entireSelectedPlc", target=target), "VersionSelectionUnsupported")
    h.refuse("refuse update whose removed reference strands Child", "library_update_project",
             h.selected(source, [parent_two], updateScope="entireSelectedPlc", target=target), "LibraryUpdateCleanupRisk")
    h.apply("library_copy_elements", h.selected(source, [parent_two], destinationLibraryId=consumer_library))

    def newer_project_default():
        rows = h.versions(consumer_library, PARENT)
        require([x["version"] for x in rows if x.get("isDefault")] == ["1.1.0"], "Project default is not the copied newer release")
        return h.verify_use(consumer_library, released[PARENT], 1)
    h.stage("copied newer default leaves uses at 1.0.0", newer_project_default)
    h.tool("library_close", {"libraryId": source})
    source = h.open_archive(generation_one)
    h.refuse("refuse downgrade below the newer project default", "library_update_project",
             h.selected(source, [released[PARENT]], updateScope="entireSelectedPlc", target=target), "VersionSelectionUnsupported", "newer")
    h.tool("library_close", {"libraryId": source})
    consumer_path = h.tool("tia_session_status")["projects"][0]["path"]
    h.stage("close consumer session", lambda: h.tool("tia_detach"))

    # Collision: a same-name type with a different GUID, then a same-name unlinked UDT.
    other = h.owned_scratch("collision")
    h.create_type(h.project_library(), collision, INNER, "types", other)
    source = h.open_archive(generation_one)
    h.refuse("refuse same-name different-GUID dependency", "library_instantiate",
             h.selected(source, [released[PARENT]], target={"plcPath": other}), "LibraryConflict", INNER)
    h.apply("type_source_upsert", {"scopePath": other, "source": (collision / (OUTER + ".udt")).read_text(), "expectedTypeNames": [OUTER]})
    h.refuse("refuse same-name unlinked dependency", "library_instantiate",
             h.selected(source, [released[PARENT]], target={"plcPath": other}), "LibraryConflict", OUTER)
    h.tool("library_close", {"libraryId": source})
    h.stage("close collision session", lambda: h.tool("tia_detach"))

    # Fresh reopen of the saved consumer.
    h.owned_portal("reopen")
    h.stage("reopen consumer", lambda: h.tool("project_open", {"path": consumer_path}))
    reopened = h.project_library()
    for name, version in released.items():
        h.stage("reopened exact link " + name, lambda v=version: h.verify_use(reopened, v, 1))
    h.stage("reopened newer project default", lambda: require([x["version"] for x in h.versions(reopened, PARENT) if x.get("isDefault")] == ["1.1.0"],
                                                                 "Reopened project default differs") or {"default": "1.1.0"})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    inputs = sorted(p for p in FIXTURES.rglob("*") if p.is_file())
    Harness(args, "dq_", "V21 nested FB/UDT dependency closure, exact reuse, collisions, defaults and cleanup refusal", [
        "One PLC with standard groups; multiple PLCs and software units are not exercised.",
        "Divergent content under an identical version GUID is not constructible through the public API; reuse requires an all-identical native comparison.",
        "No runtime execution, protected/F types or crash injection."], inputs).run(body)
