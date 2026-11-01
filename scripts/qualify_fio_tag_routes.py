"""Qualify the F-I/O tag-address refusals natively (1 November audit fix) on an owned scratch F-CPU: tag_create, tag_set,
tag_table_import_xml, vci_synchronize WorkspaceToProject and library_instantiate of a tag-table master copy.

Fixture: the scratch's standard CPU 1511-1 PN V4.1, which owns no F-I/O, and probe_safety_kinds' CPU 1511F-1 PN, added with
configureScratchSecurity and failsafeRuntimeAccess (its generated scratch user holds Full access including fail-safe, which
its compile requires; scratch only), with an ET 200SP station and two F-DI 8x24VDC HF on its IO system. The first F-DI's
input start byte comes from hardware_inspect (probe_safety_logic.input_start).

Every refusal must come from change_prepare as SafetyBoundary naming the F-DI, and no tag table or tag of either PLC
(software_inspect) may change. The same routes with a bit-memory address apply with checkpoint, compile and save, and read back:
- tag_create of FioIn at %I<start>.0 in the F-PLC's table FioTags, and tag_set of FioProbe (%M40.0) to that address.
- tag_table_import_xml of a new table FioImport with one tag there; McTags with McProbe at %M60.0 imports into the standard PLC.
- vci_synchronize WorkspaceToProject of FioTags, mapped by export in SimaticML to a workspace outside the project and the
  artifact store, after its package was edited from %M40.0 to %I<start>.0; edited to %M41.0, it applies.
- library_instantiate of a master copy of McTags into the F-PLC; into the standard PLC, after its own McProbe and McTags
  were deleted, it applies and McTags reads back with McProbe. (Renaming the table instead left McProbe in the PLC, and
  TIA then created the master copy's tag as McProbe(1), run fr_eeb8be6f.)
Last, a probe records whether a tag table mapped in each other VCI format still synchronizes project-ward after a
bit-memory edit (the F-I/O rule reads the package as SimaticML).
"""
from __future__ import annotations

import argparse
from pathlib import Path

from library_native_harness import Harness, require
from probe_safety_kinds import FDI, STATION, catalog, change, topology
from probe_safety_logic import input_start
from probe_wave_kinds import attempt, error_of, probe
from qualify_access_changes import files_under
from qualify_typed_resolution import tag_fixture
from typed_graphical_transport import artifact_bytes

FIO_TABLE, PROBE_TAG, MC_TABLE, MC_TAG, SD_TABLE = "FioTags", "FioProbe", "McTags", "McProbe", "SdTags"
WORKSPACE = "FioVci"
LIMITATIONS = [
    "One owned scratch with a standard CPU 1511-1 PN V4.1 and a CPU 1511F-1 PN whose ET 200SP station carries two F-DI 8x24VDC HF; "
    "addresses in the first F-DI's input range only. F-output, ET 200MP and other F-module ranges are not exercised.",
    "vci_conflict_resolve's project-ward route and block or SCL imports with absolute operands are not exercised.",
    "The F-CPU's generated scratch user holds Full access including fail-safe (owned scratch only); no safety login, password or download.",
]


def tables(h):
    """Every tag table of every PLC with its tags as (name, type, address, isSafety)."""
    plcs = [p["path"] for p in h.tool("project_inventory", {"maxItems": 2000})["plcs"]]
    return {t["path"]: sorted([g["name"], g.get("dataType"), g.get("logicalAddress"), g.get("isSafety")] for g in t.get("tags", []))
            for plc in plcs for t in h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["tagTables"]}


def table_path(h, plc, name):
    found = [t["path"] for t in h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["tagTables"] if t["name"] == name]
    require(len(found) == 1, "Expected one tag table %s on %s: %s" % (name, plc, found))
    return found[0]


def refuse(h, label, kind, payload, scope, module=FDI, address=None):
    """change_prepare must refuse with SafetyBoundary naming the F-I/O module, and no tag table or tag of any PLC may change."""
    def check():
        before = tables(h)
        result = h.client.request("tools/call", {"name": "change_prepare", "arguments": {"kind": kind, "payload": payload, **scope}})
        require(result.get("isError"), "Preparation was not refused: %s" % (result.get("structuredContent") or {}).get("preview"))
        error = error_of(result)
        details = error.get("details") or {}
        require(error.get("code") == "SafetyBoundary" and module in str(details.get("fIoPath")) and (address is None or details.get("logicalAddress") == address),
                "Expected SafetyBoundary naming %s %s, observed %s" % (module, address or "", error))
        require(tables(h) == before, "The refused preparation changed a tag table or tag")
        return error
    return h.stage("refuse %s: %s" % (kind, label), check)


def edit_package(root, name, old, new):
    """Edit the one package file of a mapping that holds old, as a version-control checkout would; returns its name."""
    hits = [n for n in files_under(root) if n.split("/")[-1].startswith(name + ".") and (Path(root) / n).read_bytes().count(old.encode()) == 1]
    require(len(hits) == 1, "Expected one package file of %s holding %s once: %s" % (name, old, files_under(root)))
    path = Path(root) / hits[0]
    path.write_bytes(path.read_bytes().replace(old.encode(), new.encode()))
    return hits[0]


def has_tag(h, plc, table, tag):
    """The table holds exactly the given tag (name, type, address, isSafety False)."""
    rows = tables(h).get(table_path(h, plc, table))
    require(rows == [tag], "%s holds %s, expected %s" % (table, rows, [tag]))
    return rows


def body(h):
    standard = h.owned_scratch("fio routes")
    fcpu = h.report["fCpu"] = catalog(h, "1511F-1 PN", "1511F-1 PN", "6ES7 ")[0]
    change(h, "hardware_add", {"typeIdentifier": fcpu["typeIdentifier"], "deviceName": STATION, "itemName": "TiaMcpFPlc",
                               "configureScratchSecurity": True, "failsafeRuntimeAccess": True}, {})
    plc = h.report["fPlc"] = next(p["path"] for p in h.tool("project_inventory", {"maxItems": 2000})["plcs"] if STATION in p["path"])
    modules = h.report["fModules"] = topology(h, {"plcPath": plc})
    rows = [row for item in h.tool("project_inventory", {"maxItems": 2000})["hardware"] if item["path"].startswith(modules[FDI])
            for row in h.tool("hardware_inspect", {"hardwarePath": item["path"], "maxItems": 2000})["items"]]
    fio = "%%I%d.0" % input_start(rows, modules[FDI])
    h.report["fioAddress"] = fio
    f, s = {"plcPath": plc}, {"plcPath": standard}

    # tag_create and tag_set
    change(h, "tag_table_create", {"tagTableName": FIO_TABLE}, f)
    table = table_path(h, plc, FIO_TABLE)
    empty = h.stage("export the empty table " + FIO_TABLE, lambda: artifact_bytes(h.tool, h.tool("tag_table_read", {"tagTablePath": table})["artifact"]).decode("utf-8-sig"))
    change(h, "tag_create", {"tagTablePath": table, "name": PROBE_TAG, "dataTypeName": "Bool", "logicalAddress": "%M40.0"}, f)
    refuse(h, "a new tag on the F-DI input", "tag_create", {"tagTablePath": table, "name": "FioIn", "dataTypeName": "Bool", "logicalAddress": fio}, f, address=fio)
    refuse(h, PROBE_TAG + " moved onto the F-DI input", "tag_set", {"tagTablePath": table, "memberName": PROBE_TAG, "attributes": {"LogicalAddress": fio}}, f, address=fio)

    # tag_table_import_xml
    refuse(h, "a new table with a tag on the F-DI input", "tag_table_import_xml",
           {"xml": tag_fixture(empty, "FioImport", "FioImportIn", fio), "expectedTableNames": ["FioImport"]}, f, address=fio)
    change(h, "tag_table_import_xml", {"xml": tag_fixture(empty, MC_TABLE, MC_TAG, "%M60.0"), "expectedTableNames": [MC_TABLE]}, s)
    h.stage("the standard PLC's imported " + MC_TABLE, lambda: has_tag(h, standard, MC_TABLE, [MC_TAG, "Bool", "%M60.0", False]))

    vci = vci_routes(h, plc, table, fio, f)
    library_routes(h, plc, standard, s)
    if vci:
        for fmt in (x for x in vci["formats"] if x != "SimaticML"):
            probe(h, "project-ward synchronization of a %s tag-table package" % fmt, lambda fmt=fmt: other_format(h, standard, empty, vci, fmt))


def vci_routes(h, plc, table, fio, f):
    groups = [g["path"] for g in h.tool("vci_workspace_inspect")["groups"] if g.get("kind") == "system"]
    require(len(groups) == 1, "Expected one system VCI workspace group: %s" % groups)
    root = h.directory.parent / ("vci-" + h.directory.name)  # Beside the artifact store, so outside it and outside the project.
    change(h, "vci_workspace_create", {"groupPath": groups[0], "name": WORKSPACE, "rootPath": str(root), "language": "en-US"}, f)
    workspace = groups[0] + "/" + WORKSPACE
    base = {"workspacePath": workspace, "objectPath": table, "mode": "export", "directory": "", "fileName": FIO_TABLE}
    def formats():
        error = h.call_error("change_prepare", {"kind": "vci_mapping_add", "payload": {**base, "format": "TiaMcpBogus"}, **f})
        require(error.get("code") == "VciFormatUnsupported", "Expected VciFormatUnsupported: %s" % error)
        return [x.strip() for x in error["message"].rsplit(":", 1)[1].split(",")]
    found = h.report["vciTagTableFormats"] = h.stage("vci_mapping_add lists the tag table's formats", formats)
    if "SimaticML" not in found:
        h.report["vciBlocked"] = "V21 offers no SimaticML format for a tag table: %s" % found
        return None
    change(h, "vci_mapping_add", {**base, "format": "SimaticML"}, f)
    sync = {"workspacePath": workspace, "objectPath": table, "direction": "WorkspaceToProject"}
    h.report["vciEdited"] = h.stage("edit the package: %s from %%M40.0 to %s" % (PROBE_TAG, fio), lambda: edit_package(root, FIO_TABLE, "%M40.0", fio))
    refuse(h, "a package tag on the F-DI input", "vci_synchronize", sync, f, address=fio)
    h.stage("edit the package: %s to %%M41.0" % PROBE_TAG, lambda: edit_package(root, FIO_TABLE, fio, "%M41.0"))
    change(h, "vci_synchronize", sync, f)
    h.stage("%s reads back at %%M41.0" % PROBE_TAG, lambda: has_tag(h, plc, FIO_TABLE, [PROBE_TAG, "Bool", "%M41.0", False]))
    return {"workspace": workspace, "root": root, "formats": found}


def library_routes(h, plc, standard, s):
    library = h.project_library()
    source = table_path(h, standard, MC_TABLE)
    change(h, "library_create_master_copy", {"schemaVersion": 1, "destinationLibraryId": library, "target": {"plcPath": standard},
                                             "object": {"kind": "tagTable", "objectPath": source}}, {})
    masters = [x for x in h.query(library)["items"] if x.get("kind") == "masterCopy" and x.get("name") == MC_TABLE]
    require(len(masters) == 1, "Expected one master copy %s: %s" % (MC_TABLE, masters))
    def instantiate(target):
        return {"schemaVersion": 1, "source": h.source_ref(library), "selection": [{"masterCopyPath": masters[0]["masterCopyPath"]}],
                "destinationComposition": "tagTables", "target": {"plcPath": target}}
    # Either F-DI may be named: a master copy is refused by any F-I/O range of the PLC.
    refuse(h, "a tag-table master copy into the F-PLC", "library_instantiate", instantiate(plc), {}, module=FDI[:-1])
    change(h, "tag_delete", {"tagTablePath": source, "memberName": MC_TAG, "dependencyPolicy": "rejectReferenced"}, s)
    change(h, "tag_table_delete", {"tagTablePath": source}, s)
    change(h, "library_instantiate", instantiate(standard), {})
    h.stage("the instantiated %s reads back on the standard PLC" % MC_TABLE, lambda: has_tag(h, standard, MC_TABLE, [MC_TAG, "Bool", "%M60.0", False]))


def other_format(h, standard, empty, vci, fmt):
    """A standard-PLC table mapped in fmt, edited %M44.0 -> %M45.0 and synchronized project-ward: applied or the exact refusal."""
    name = SD_TABLE + str(len(h.report.get("probes", [])))
    imported = attempt(h, "tag_table_import_xml", {"xml": tag_fixture(empty, name, name + "In", "%M44.0"), "expectedTableNames": [name]}, {"plcPath": standard})
    require(imported["prepared"], "The probe table was refused: %s" % imported.get("error"))
    table = table_path(h, standard, name)
    mapped = attempt(h, "vci_mapping_add", {"workspacePath": vci["workspace"], "objectPath": table, "mode": "export", "directory": "", "fileName": name, "format": fmt}, {"plcPath": standard})
    require(mapped["prepared"], "The %s mapping was refused: %s" % (fmt, mapped.get("error")))
    edited = edit_package(vci["root"], name, "%M44.0", "%M45.0")
    synced = attempt(h, "vci_synchronize", {"workspacePath": vci["workspace"], "objectPath": table, "direction": "WorkspaceToProject"}, {"plcPath": standard})
    return {"format": fmt, "editedFile": edited, "synchronize": synced, "tags": tables(h).get(table)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "fr_", "F-I/O tag-address refusals of tag_create, tag_set, tag_table_import_xml, vci_synchronize and library_instantiate on an owned scratch F-CPU",
            LIMITATIONS, []).run(body)
