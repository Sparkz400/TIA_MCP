"""Qualify library maintenance lifecycle gates: discard, type policy, folders, publication, master copies, deletion and recovery."""
from __future__ import annotations

import argparse
from pathlib import Path
import shutil

from library_native_harness import Harness, identity, require

ROOT = Path(__file__).resolve().parents[1]
UDT, FB = "TiaMcpLibraryState", "TiaMcpLibraryGate"


def body(h):
    documents = h.directory / "documents"
    shutil.copytree(ROOT / "fixtures/libraries/type-lifecycle", documents)
    plc = h.owned_scratch("authoring")
    blocks = {b["name"]: b["path"] for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"] if b["plcPath"] == plc}
    library = h.project_library()
    udt = h.release(library, h.create_type(library, documents, UDT, "types", plc), "1.0.0")
    fb = h.release(library, h.create_type(library, documents, FB, "blocks", plc), "1.0.0")

    def numbers(name):
        return sorted(x["version"] for x in h.versions(library, name))

    # Edit then discard restores the released link.
    h.apply("library_edit_version", h.selected(library, [fb], testInstanceName=FB, target={"plcPath": plc}))
    in_work = [x for x in h.versions(library, FB) if x["state"] == "InWork"]
    require(len(in_work) == 1, "Edit did not create one InWork version")
    h.refuse("refuse discarding a committed version", "library_discard_version", h.selected(library, [fb]), "InvalidChangeState")
    discarded = h.apply("library_discard_version", h.selected(library, [in_work[0]]))
    require(discarded["journal"]["result"]["discardedVersionGuid"] == in_work[0]["versionGuid"], "Discard result differs")
    h.stage("discard leaves only 1.0.0 linked", lambda: require(numbers(FB) == ["1.0.0"], "Discard left versions") or h.verify_use(library, fb, 1))

    # Type policy, including malformed flags rejected before mutation.
    for label, flags in (("no policy flag", {}), ("non-boolean doNotUse", {"doNotUse": "true"}),
                         ("mixed malformed setForUpdate", {"doNotUse": True, "setForUpdate": 1})):
        h.refuse("refuse " + label, "library_type_policy", h.selected(library, [udt], **flags), "InvalidRequest")
    h.apply("library_type_policy", h.selected(library, [udt], doNotUse=True))
    global_library = h.apply("library_create", {"schemaVersion": 1, "name": "LifecycleQualification"})["journal"]["result"]["library"]["libraryId"]
    h.refuse("refuse publishing a DoNotUse type", "library_publish", h.selected(library, [udt], destinationLibraryId=global_library),
             "DependencyConflict")
    # PLC data types expose SetForUpdate read-only; it is refused before mutation, not half-applied.
    h.refuse("refuse read-only setForUpdate on a PLC data type", "library_type_policy",
             h.selected(library, [udt], doNotUse=False, setForUpdate=True), "AttributeNotWritable", "SetForUpdate")
    h.apply("library_type_policy", h.selected(library, [udt], doNotUse=False))

    def policy():
        row = [x for x in h.versions(library, UDT) if identity(x) == identity(udt)][0]
        require(row["doNotUse"] is False, "Type policy readback differs")
        return row
    h.stage("type policy readback", policy)

    # Folders.
    def folders():
        return sorted(x["path"] for x in h.query(library)["items"] if x.get("kind") == "typeFolder")
    h.apply("library_organize", h.selected(library, [], folderKind="types", action="create", name="Archive"))
    h.refuse("refuse duplicate folder", "library_organize", h.selected(library, [], folderKind="types", action="create", name="Archive"),
             "LibraryConflict")
    h.refuse("refuse renaming the root folder", "library_organize", h.selected(library, [], folderKind="types", action="rename",
             folderPath="/types", name="Root"), "TargetIncompatible")
    created = [p for p in folders() if p.endswith("/Archive")]
    require(len(created) == 1, "Created folder is not listed")
    h.apply("library_organize", h.selected(library, [], folderKind="types", action="rename", folderPath=created[0], name="Archived"))
    renamed = [p for p in folders() if p.endswith("/Archived")]
    require(len(renamed) == 1 and not any(p.endswith("/Archive") for p in folders()), "Rename readback differs")
    h.apply("library_organize", h.selected(library, [], folderKind="types", action="delete", folderPath=renamed[0]))
    h.stage("folder lifecycle readback", lambda: require(not any("Archiv" in p for p in folders()), "Deleted folder remains") or folders())

    # Publication into an existing global library, master copies and their name conflict.
    h.apply("library_publish", h.selected(library, [fb], destinationLibraryId=global_library))
    h.apply("library_publish", h.selected(library, [udt], destinationLibraryId=global_library))
    h.apply("library_publish", h.selected(library, [fb], destinationLibraryId=global_library))
    for version in (fb, udt):
        h.stage("published exact " + version["typeName"], lambda v=version: require(
            [identity(x) for x in h.versions(global_library, v["typeName"])] == [identity(v)], "Publication readback differs") or identity(v))
    master_payload = {"schemaVersion": 1, "destinationLibraryId": global_library, "target": {"plcPath": plc},
                      "object": {"objectPath": blocks["TiaMcp_IsEnabled"]}}
    h.apply("library_create_master_copy", master_payload)
    h.refuse("refuse duplicate master copy", "library_create_master_copy", master_payload, "LibraryConflict")
    masters = [x for x in h.query(global_library)["items"] if x.get("kind") == "masterCopy"]
    require([x["name"] for x in masters] == ["TiaMcp_IsEnabled"], "Master copy readback differs")

    # Deletion constraints, then a permitted deletion of an unused non-default version.
    h.refuse("refuse deleting the default used version", "library_delete", h.selected(library, [fb]), "DependencyConflict")
    source_text = (documents / (FB + ".scl")).read_text()
    unused = h.release(library, h.edit_source(library, plc, fb, FB, source_text), "1.1.0")
    second = h.release(library, h.edit_source(library, plc, unused, FB, source_text), "1.2.0")
    h.apply("library_set_default", h.selected(library, [second]))
    h.stage("1.1.0 is neither default nor used", lambda: h.verify_use(library, unused, 0))
    h.apply("library_delete", h.selected(library, [unused]))
    h.stage("deleted unused version is absent", lambda: require(numbers(FB) == ["1.0.0", "1.2.0"], "Version deletion readback differs") or numbers(FB))
    h.apply("library_publish", h.selected(library, [second], destinationLibraryId=global_library))
    h.apply("library_set_default", h.selected(global_library, [second]))
    h.refuse("refuse deleting a non-default global-library version", "library_delete", h.selected(global_library, [fb]),
             "DependencyUnresolved")
    deletion = h.apply("library_delete", {"schemaVersion": 1, "source": h.source_ref(global_library),
                       "selection": [{"masterCopyPath": masters[0]["masterCopyPath"]}]})
    require(not [x for x in h.query(global_library)["items"] if x.get("kind") == "masterCopy"], "Master copy deletion readback differs")
    archive = h.archive(global_library)
    h.tool("library_close", {"libraryId": global_library})
    h.stage("close authoring session", lambda: h.tool("tia_detach"))

    # Fresh consumer uses the final generation.
    consumer = h.owned_scratch("consumer")
    source = h.open_archive(archive)
    h.apply("library_instantiate", h.selected(source, [second], target={"plcPath": consumer}))
    h.stage("consumer uses exact 1.2.0", lambda: h.verify_use(h.project_library(), second, 1))

    def numbering():
        rows = [b for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"] if b["plcPath"] == consumer]
        gate = next(b for b in rows if b["name"] == FB)
        same_kind = [b for b in rows if b["kind"] == gate["kind"]]
        require(len(same_kind) > 1 and len({b["number"] for b in same_kind}) == len(same_kind),
                "Instantiation beside an existing %s left a duplicate block number" % gate["kind"])
        return {b["name"]: b["number"] for b in same_kind}
    h.stage("instantiated FB number does not collide", numbering)
    h.tool("library_close", {"libraryId": source})
    h.stage("close consumer session", lambda: h.tool("tia_detach"))

    # Recovery of a library-only change: the checkpoint before deleting the master copy still holds it.
    h.owned_portal("recovery")
    restored = h.stage("restore master-copy deletion checkpoint", lambda: h.tool("change_restore", {
        "changeId": deletion["changeId"], "previewHash": deletion["previewHash"]}))
    rows = restored["resources"]
    require([r["kind"] for r in rows] == ["library"] and rows[0]["state"] == "completed", "Expected one recovered library")
    recovered = rows[0]["library"]["libraryId"]
    h.stage("recovered library holds the deleted master copy and exact types", lambda: require(
        [x["name"] for x in h.query(recovered)["items"] if x.get("kind") == "masterCopy"] == ["TiaMcp_IsEnabled"]
        and [identity(x) for x in h.versions(recovered, FB) if x["version"] == "1.2.0"] == [identity(second)],
        "Recovered library differs from its checkpoint") or {"libraryId": recovered})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "lc_", "V21 library maintenance lifecycle: discard, policy, folders, publication, master copies, deletion, consumer and recovery", [
        "Releases 1.1.0 and 1.2.0 are created from the original FB document; they are lifecycle generations, not behavior changes.",
        "Folder moves are not offered; folders are created, renamed and deleted empty.",
        "Version deletion is qualified only in project libraries; global-library external consumers cannot be enumerated."], []).run(body)
