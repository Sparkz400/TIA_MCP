"""Qualify library consumption and update scope across two PLCs of one owned scratch project, including the forced
per-PLC version assignment (a downgrade below the project default) and its checkpoint restore."""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import shutil

from engineering_preservation import native_payload_hash
from library_native_harness import Harness, identity, require

ROOT = Path(__file__).resolve().parents[1]
FB, UDT = "TiaMcpLibraryGate", "TiaMcpLibraryState"


def body(h):
    documents = h.directory / "documents"
    shutil.copytree(ROOT / "fixtures/libraries/type-lifecycle", documents)
    change = (ROOT / "fixtures/libraries/release-changes/TiaMcpLibraryGate.implementation.scl").read_text()

    # Authoring: generation 1 (FB 1.0.0) and generation 2 (FB 1.1.0) archives.
    plc = h.owned_scratch("authoring")
    library = h.project_library()
    udt = h.release(library, h.create_type(library, documents, UDT, "types", plc), "1.0.0")
    fb = h.release(library, h.create_type(library, documents, FB, "blocks", plc), "1.0.0")
    global_library = h.apply("library_create", {"schemaVersion": 1, "name": "ScopeQualification"})["journal"]["result"]["library"]["libraryId"]
    h.apply("library_publish", h.selected(library, [udt], destinationLibraryId=global_library))
    h.apply("library_publish", h.selected(library, [fb], destinationLibraryId=global_library))
    generation_one = h.archive(global_library)
    second = h.release(library, h.edit_source(library, plc, fb, FB, change), "1.1.0")
    h.apply("library_set_default", h.selected(library, [second]))
    h.apply("library_publish", h.selected(library, [second], destinationLibraryId=global_library))
    h.apply("library_set_default", h.selected(global_library, [second]))
    generation_two = h.archive(global_library)
    # Pinned: both releases, default 1.0.0 below the highest release.
    h.apply("library_set_default", h.selected(global_library, [fb]))
    pinned = h.archive(global_library)
    h.tool("library_close", {"libraryId": global_library})
    h.stage("close authoring session", lambda: h.tool("tia_detach"))

    # Consumer with a second PLC of the same CPU type.
    h.owned_portal("consumer")
    created = h.stage("create owned scratch consumer", lambda: h.tool("tia_create_scratch"))
    require(created.get("completed") is True and created["compile"]["errorCount"] == 0, "Scratch compile failed")
    first_plc = h.tool("project_inventory", {"maxItems": 1000})["plcs"][0]["path"]
    h.apply("hardware_add", {"typeIdentifier": created["selectedCpu"]["typeIdentifier"], "deviceName": "ScratchStation2", "itemName": "ScratchPLC2",
            "configureScratchSecurity": True})  # A new S7-1500 needs its confidential-data password before it compiles.
    plcs = [p["path"] for p in h.tool("project_inventory", {"maxItems": 1000})["plcs"]]
    require(len(plcs) == 2 and first_plc in plcs, "Expected the scratch PLC and one added PLC")
    second_plc = next(p for p in plcs if p != first_plc)

    source = h.open_archive(generation_one)
    for target in (first_plc, second_plc):
        for version in (udt, fb):
            h.apply("library_instantiate", h.selected(source, [version], target={"plcPath": target}))
    h.tool("library_close", {"libraryId": source})
    h.apply("source_upsert", {"scopePath": first_plc, "language": "SCL", "source": (documents / "ConsumerReferences.scl").read_text(),
            "expectedBlockNames": [FB + "_DB", UDT + "_DB", "Main"]})
    h.apply("source_upsert", {"scopePath": second_plc, "language": "SCL",
            "source": (ROOT / "fixtures/libraries/scopes/SecondPlcReferences.scl").read_text(), "expectedBlockNames": [FB + "_DB", UDT + "_DB", "TiaMcpScopeCycle"]})
    consumer = h.project_library()

    def uses_by_plc(version, expected):
        rows = h.verify_use(consumer, version, sum(expected.values()))["items"]
        # softwarePath is the owning device item; the PLC path extends it with /plc/<name>.
        observed = {p: sum(1 for r in rows if p == r["softwarePath"] or p.startswith(r["softwarePath"] + "/plc/")) for p in (first_plc, second_plc)}
        require(observed == expected, "Uses per PLC differ for %s %s: %s" % (version["typeName"], version["version"], observed))
        return observed
    h.stage("FB 1.0.0 used once in each PLC", lambda: uses_by_plc(fb, {first_plc: 1, second_plc: 1}))
    h.stage("UDT 1.0.0 used once in each PLC", lambda: uses_by_plc(udt, {first_plc: 1, second_plc: 1}))

    def first_plc_snapshot():
        rows = []
        for block in sorted((b for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"] if b["plcPath"] == first_plc), key=lambda b: b["path"]):
            exported = h.tool("block_read", {"blockPath": block["path"]})
            raw = Path(exported["artifact"]["path"]).read_bytes()
            require(hashlib.sha256(raw).hexdigest() == exported["artifact"]["sha256"], "Native export hash differs")
            rows.append((block["path"], native_payload_hash(raw.decode("utf-8-sig"))))
        return rows
    before = h.stage("capture the first PLC before updating the second", first_plc_snapshot)

    # Update scope is the selected PLC only.
    source = h.open_archive(generation_two)
    h.apply("library_update_project", h.selected(source, [second], updateScope="entireSelectedPlc", target={"plcPath": second_plc}))
    h.stage("only the second PLC moved to 1.1.0", lambda: uses_by_plc(second, {first_plc: 0, second_plc: 1}))
    h.stage("the first PLC kept 1.0.0", lambda: uses_by_plc(fb, {first_plc: 1, second_plc: 0}))
    after = h.stage("capture the first PLC after updating the second", first_plc_snapshot)
    require(before == after, "Updating the second PLC changed native content of the first PLC")
    h.report["firstPlcPreserved"] = True
    h.apply("library_update_project", h.selected(source, [second], updateScope="entireSelectedPlc", target={"plcPath": first_plc}))
    h.stage("both PLCs at 1.1.0", lambda: uses_by_plc(second, {first_plc: 1, second_plc: 1}))
    h.tool("library_close", {"libraryId": source})

    def project_default(version):
        rows = h.versions(consumer, FB)
        require([x["versionGuid"] for x in rows if x.get("isDefault")] == [version["versionGuid"]], "Project default differs: %s" % rows)
        return sorted((x["version"], x.get("isDefault")) for x in rows)
    h.stage("project default FB 1.1.0", lambda: project_default(second))

    # Forced per-PLC version assignment: the pinned source default 1.0.0 is below its own 1.1.0 release and the project default.
    source = h.open_archive(pinned)
    def assign(version=fb, scope="entireSelectedPlc", **extra):
        return h.selected(source, [version], updateScope=scope, target={"plcPath": second_plc}, **extra)
    h.refuse("refuse the lower source default without forceDefault", "library_update_project", assign(), "VersionSelectionUnsupported", "forceDefault")
    h.refuse("refuse a non-boolean forceDefault", "library_update_project", assign(forceDefault="true"), "InvalidRequest", "boolean")
    h.refuse("refuse a non-default version even when forced", "library_update_project", assign(second, forceDefault=True), "VersionSelectionUnsupported", "library_set_default")
    for scope in ("selectedInstances", "selectedFolder", "softwareUnit"):
        h.refuse("refuse updateScope " + scope, "library_update_project", assign(scope=scope, forceDefault=True), "NativeScopeTooBroad", "no instance")
    before = h.stage("capture the first PLC before the forced update", first_plc_snapshot)
    prepared = h.prepare("library_update_project", assign(forceDefault=True))

    def forced_preview():
        preview, impact = prepared["preview"], prepared["preview"]["updateImpact"]
        rows = [t for t in impact["types"] if t["requested"]["typeGuid"] == fb["typeGuid"]]
        require(impact["forceDefault"] is True and len(rows) == 1 and rows[0]["forceDefault"] is True and rows[0]["defaultEffect"] == "forcedDowngrade"
                and rows[0]["projectDefaultBefore"]["versionGuid"] == second["versionGuid"] and rows[0]["requested"]["versionGuid"] == fb["versionGuid"],
                "Preview does not declare the forced downgrade of the project default: %s" % rows)
        moved = [(u["beforeVersionGuid"], u["requestedVersionGuid"]) for u in impact["affectedUses"] if u["action"] == "update"]
        require(moved == [(second["versionGuid"], fb["versionGuid"])] and impact["noOp"] is False, "Preview moves other uses: %s" % moved)
        require([e["forceDefault"] for e in preview["effects"] if e["action"] == "updateAllSelectedTypeUses"] == [True], "Effect lacks forceDefault")
        peers = preview["updateBaseline"]["peerPlcUses"]
        require({p["plcPath"] for p in peers} == {first_plc} and {p["versionGuid"] for p in peers} == {second["versionGuid"], udt["versionGuid"]},
                "Peer PLC baseline differs: %s" % peers)
        return {"type": rows[0], "peerPlcUses": peers}
    h.stage("preview declares the forced downgrade and the peer PLC baseline", forced_preview)
    applied = h.apply_prepared("library_update_project", prepared)
    readbacks = applied["journal"]["updateReadbacks"]
    h.stage("every readback phase verified", lambda: require([r["phase"] for r in readbacks if r["verified"]] == ["checkpoint", "native_action", "compile", "save"],
                                                             "Readbacks: %s" % [(r["phase"], r["verified"]) for r in readbacks]) or len(readbacks))
    h.stage("only the second PLC moved to 1.0.0", lambda: uses_by_plc(fb, {first_plc: 0, second_plc: 1}))
    h.stage("the first PLC kept 1.1.0", lambda: uses_by_plc(second, {first_plc: 1, second_plc: 0}))
    h.stage("the UDT stayed at 1.0.0 in both PLCs", lambda: uses_by_plc(udt, {first_plc: 1, second_plc: 1}))
    h.stage("project default is the assigned FB 1.0.0", lambda: project_default(fb))
    after = h.stage("capture the first PLC after the forced update", first_plc_snapshot)
    require(before == after, "The forced update of the second PLC changed native content of the first PLC")
    h.report["firstPlcPreservedForced"] = True
    h.tool("library_close", {"libraryId": source})
    project_path = h.tool("tia_session_status")["projects"][0]["path"]
    h.stage("close consumer session", lambda: h.tool("tia_detach"))

    # The forced update's checkpoint restores the pre-update project to a new location.
    h.owned_portal("restore")
    def restore():
        restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        rows = restored["resources"]
        require(restored["state"] == "restored_to_new_locations" and [r["kind"] for r in rows] == ["project"] and rows[0]["state"] == "completed",
                "Checkpoint recovery differs: %s" % restored)
        return restored
    h.stage("restore the forced update's checkpoint", restore)
    consumer = h.project_library()
    h.stage("restored: both PLCs at 1.1.0, project default 1.1.0", lambda: {"fb": uses_by_plc(second, {first_plc: 1, second_plc: 1}),
                                                                          "default": project_default(second)})
    h.stage("close restore session", lambda: h.tool("tia_detach"))

    h.owned_portal("reopen")
    h.stage("reopen consumer", lambda: h.reopen_project(project_path))
    consumer = h.project_library()
    h.stage("reopened: second PLC at FB 1.0.0, first at 1.1.0, UDT unchanged, default 1.0.0", lambda: {
        "assigned": uses_by_plc(fb, {first_plc: 0, second_plc: 1}), "kept": uses_by_plc(second, {first_plc: 1, second_plc: 0}),
        "udt": uses_by_plc(udt, {first_plc: 1, second_plc: 1}), "default": project_default(fb)})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "sq_", "V21 library consumption, explicit update scope and forced per-PLC version assignment across two PLCs", [
        "Software units and namespaces are not exercised here (qualify_library_units.py covers them).",
        "Both PLCs use the scratch CPU type; mixed CPU families are not exercised.",
        "The forced assignment is a downgrade of a root FB whose UDT dependency is unchanged; forced dependency defaults are refused, not exercised.",
        "No runtime execution."], [p for p in (ROOT / "fixtures/libraries/scopes").iterdir()]).run(body)
