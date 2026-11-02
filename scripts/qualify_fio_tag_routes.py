"""Qualify the F-I/O tag-address refusals natively (1 November audit fixes) on an owned scratch F-CPU: tag_create, tag_set,
tag_table_import_xml, vci_synchronize WorkspaceToProject, vci_conflict_resolve take=remote, library_instantiate of a tag-table
master copy and software_unit_create_from of a unit master copy.

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
- vci_conflict_resolve take=remote of FioTags, a reviewed two-sided conflict: FioTags' package is committed and pushed to the
  Host's --git-remote (a local bare repository), another engineer moves FioProbe onto %I<start>.0 in a clone and pushes,
  FioProbe moves to %M42.0 in the project, and after vci_fetch, three vci_package_capture records, a vci_package_compare
  divergentChange row and the checkout of the fetched package into the workspace, the resolution must refuse.
- library_instantiate of a master copy of McTags into the F-PLC; into the standard PLC, after its own McProbe and McTags
  were deleted, it applies and McTags reads back with McProbe. (Renaming the table instead left McProbe in the PLC, and
  TIA then created the master copy's tag as McProbe(1), run fr_eeb8be6f; qualify_master_copy_tag_collision.py covers it.)
- software_unit_create_from of a master copy of the standard PLC's tag-bearing unit FioUnit into the F-PLC; into the standard PLC,
  after FioUnit was deleted there, it applies.
Last, a probe records whether a tag table mapped in each other VCI format still synchronizes project-ward after a
bit-memory edit (the F-I/O rule reads the package as SimaticML).
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET
from types import SimpleNamespace

from library_native_harness import Harness, require
from probe_safety_kinds import FDI, STATION, catalog, change, topology
from probe_safety_logic import input_start
from probe_wave_kinds import attempt, error_of, probe
from qualify_access_changes import files_under, upload
from qualify_typed_resolution import tag_fixture
from qualify_vci_mappings import commit, download, plain_git, prepare_files, push, sha
from typed_graphical_transport import artifact_bytes

FIO_TABLE, PROBE_TAG, MC_TABLE, MC_TAG, SD_TABLE, UNIT = "FioTags", "FioProbe", "McTags", "McProbe", "SdTags", "FioUnit"
WORKSPACE = "FioVci"
LIMITATIONS = [
    "One owned scratch with a standard CPU 1511-1 PN V4.1 and a CPU 1511F-1 PN whose ET 200SP station carries two F-DI 8x24VDC HF; "
    "addresses in the first F-DI's input range only. F-output, ET 200MP and other F-module ranges are not exercised.",
    "vci_conflict_resolve runs take=remote and take=merged against a local bare Git remote. A separate end-of-run probe records standard SCL/XML absolute F-input reads; it is not safety-program qualification.",
    "software_unit_create_from copies a unit with one memory tag within the project library; F-output ranges and external vendor unit archives are not exercised.",
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
    if vci:
        conflict_route(h, plc, table, fio, f, vci)
    library_routes(h, plc, standard, s)
    unit_routes(h, plc, standard, s)
    if vci:
        for fmt in (x for x in vci["formats"] if x != "SimaticML"):
            probe(h, "project-ward synchronization of a %s tag-table package" % fmt, lambda fmt=fmt: other_format(h, standard, empty, vci, fmt))
    absolute_block_read(h, plc, standard, fio, f, s)


def absolute_block_read(h, plc, standard, fio, scope, standard_scope):
    """Native source generation can create an implicit safety tag; refuse it and the equivalent XML before any write."""
    source = 'FUNCTION "FioReadProbe" : Bool\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\nBEGIN\n#FioReadProbe := %s;\nEND_FUNCTION\n' % fio
    payload = {"language": "SCL", "source": source, "expectedBlockNames": ["FioReadProbe"]}
    refuse(h, "source generation of an implicit F-input tag", "source_upsert", payload, scope, address=fio)
    change(h, "source_upsert", payload, standard_scope)
    exported = h.tool("block_read", {"plcPath": standard, "blockName": "FioReadProbe"})
    xml = artifact_bytes(h.tool, exported["artifact"]).decode("utf-8-sig")
    payload = {"xml": absolute_xml(xml, fio), "expectedBlockNames": ["FioReadProbe"]}
    change(h, "block_import_xml", payload, standard_scope)
    refuse(h, "XML block import of an absolute F-input", "block_import_xml", payload, scope, address=fio)


def absolute_xml(xml, fio):
    """Keep native namespace spelling: TIA can silently drop a reserialized StructuredText body."""
    byte, bit = map(int, fio.removeprefix("%I").split("."))
    pattern = r'<Access\b(?=[^>]*Scope="(?:GlobalVariable|Address)")([^>]*)>.*?</Access>'
    def replace(match):
        attributes = re.sub(r'Scope="[^"]+"', 'Scope="Address"', match.group(1))
        return '<Access%s><Address Area="Input" Type="Bool" BitOffset="%d" /></Access>' % (attributes, byte * 8 + bit)
    updated, count = re.subn(pattern, replace, xml, flags=re.S)
    require(count == 1, "Expected one input read in the native standard source export")
    ET.fromstring(updated)
    return updated


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


def conflict_route(h, plc, table, fio, f, vci):
    """vci_conflict_resolve take=remote of a reviewed two-sided conflict whose remote side moves FioProbe onto the F-DI input."""
    root, workspace = Path(vci["root"]), vci["workspace"]
    git = {"remote": str(h.directory / "git-remote.git"), "clone": str(h.directory / "git-other-engineer"), "pushes": []}
    run = SimpleNamespace(h=h, git=git, git_exe=h.args.git_executable)
    h.report["git"] = git
    def mapping():
        rows = [m for w in h.tool("vci_workspace_inspect")["workspaces"] if w["path"] == workspace for m in w["mappedObjects"]
                if (m.get("object") or [{}])[-1].get("Name") == FIO_TABLE]
        require(len(rows) == 1 and isinstance(rows[0].get("contentSha256"), str), "Expected one mapping of %s with a content hash: %s" % (FIO_TABLE, rows))
        return rows[0]
    files = {n: (root / n).read_bytes() for n in files_under(root) if n.split("/")[-1].startswith(FIO_TABLE + ".")}
    base_content = mapping()["contentSha256"]
    base = commit(run, "Baseline FioTags", prepare_files(run, "Baseline FioTags", files, branch="main"))
    push(run, "baseline", base, None)
    plain_git(run, h.directory, "clone", "--branch", "main", git["remote"], git["clone"])
    h.stage("the other engineer moves %s onto %s" % (PROBE_TAG, fio), lambda: edit_package(git["clone"], FIO_TABLE, "%M41.0", fio))
    plain_git(run, git["clone"], "commit", "-am", "Other engineer: FioProbe on the F-DI input")
    plain_git(run, git["clone"], "push", "origin", "main")
    head = plain_git(run, git["clone"], "rev-parse", "HEAD")
    change(h, "tag_set", {"tagTablePath": table, "memberName": PROBE_TAG, "attributes": {"LogicalAddress": "%M42.0"}}, f)
    native = mapping()["contentSha256"]
    fetched = h.stage("vci_fetch the other engineer's commit", lambda: h.tool("vci_fetch", {"branchName": "main", "parentRevisionRef": base["recordRef"], "expectedRemoteCommit": head}))
    require(fetched["commit"] == head, "The fetch did not reach the remote commit")
    remote = {name: download(h, "vci_file_read", fetched["recordRef"], relativePath=name) for name in files}
    def capture(label, source, content):
        snapshot = {"observationId": "qualify-fio-routes:" + label, "sourceSha256": content, "tiaVersion": 21}
        row = mapping()
        evidence = {"schemaVersion": "vci-mapping-evidence.v1", "workspaceId": workspace, "snapshot": snapshot, "enumerationComplete": True, "packages": [{
            "packageId": FIO_TABLE, "nativeStatus": row["compareState"],
            "object": {"object": {"projectPath": h.tool("tia_session_status")["projects"][0]["path"], "plcPath": plc, "scopePath": None, "objectPath": table, "kind": "tagTable"},
                       "snapshot": snapshot, "contentSha256": content},
            "profile": {"profileId": "vci-tag-table", "family": "nativeDocument", "objectKind": "tagTable", "nativeFormat": "SimaticML",
                        "evidenceSha256": sha(json.dumps(row, sort_keys=True).encode()), "evidenceOrigin": "nativeExport", "complete": True,
                        "expectedFiles": [{"relativePath": name, "role": "document"} for name in sorted(files)]}}]}
        reference = upload(h, "mapping.json", json.dumps(evidence).encode("utf-8"))
        return h.stage("vci_package_capture " + label, lambda: h.tool("vci_package_capture", {"sourceRecordRef": source, "mappingUploadRef": reference}))
    sides = [capture("baseline", base["recordRef"], base_content), capture("native", base["recordRef"], native), capture("remote", fetched["recordRef"], base_content)]
    def compared():
        result = h.tool("vci_package_compare", {"baselineRef": sides[0]["recordRef"], "projectRef": sides[1]["recordRef"], "workspaceRef": sides[2]["recordRef"],
                        "expectedHashes": {"baseline": sides[0]["manifestSha256"], "project": sides[1]["manifestSha256"], "workspace": sides[2]["manifestSha256"]}})
        rows = json.loads(download(h, "vci_artifact_read", result["recordRef"], name="comparison.json"))["rows"]
        require(result["conflictCount"] == 1 and [r["state"] for r in rows] == ["divergentChange"], "The comparison does not report the two-sided conflict: %s" % rows)
        return result
    comparison = h.stage("vci_package_compare reports a divergentChange", compared)
    def checkout():
        for name, data in remote.items():
            (root / name).write_bytes(data)
        return sorted(remote)
    h.report["vciConflictCheckout"] = h.stage("check the fetched package out into the workspace", checkout)
    refuse(h, "a reviewed remote side with %s on the F-DI input" % PROBE_TAG, "vci_conflict_resolve",
           {"workspacePath": workspace, "objectPath": table, "comparisonRef": comparison["recordRef"], "packageId": FIO_TABLE, "take": "remote"}, f, address=fio)
    merged_address = fio[:-1] + "1"
    require(sum(data.count(fio.encode()) for data in remote.values()) == 1, "Expected one F-I/O operand in the remote package")
    merged_files = {name: data.replace(fio.encode(), merged_address.encode()) for name, data in remote.items()}
    merged = prepare_files(run, "Reviewed merged F-I/O tag", merged_files, parent=fetched["recordRef"])
    refuse(h, "a reviewed merged side with %s on the F-DI input" % PROBE_TAG, "vci_conflict_resolve",
           {"workspacePath": workspace, "objectPath": table, "comparisonRef": comparison["recordRef"], "packageId": FIO_TABLE,
            "take": "merged", "mergedRecordRef": merged["recordRef"]}, f, address=merged_address)


def unit_routes(h, plc, standard, s):
    """A unit's tag-bearing master copy is refused on an F-I/O owner and applied on the standard PLC."""
    unit = standard + "/units/" + UNIT
    change(h, "software_unit_create", {"name": UNIT}, s)
    change(h, "tag_table_create", {"scopePath": unit, "tagTableName": "UnitTags"}, s)
    unit_table = unit + "/tags/UnitTags"
    change(h, "tag_create", {"tagTablePath": unit_table, "name": "UnitMarker", "dataTypeName": "Bool", "logicalAddress": "%M61.0"}, s)
    library = h.project_library()
    change(h, "library_create_master_copy", {"schemaVersion": 1, "destinationLibraryId": library, "target": {"plcPath": standard},
                                             "object": {"kind": "softwareUnit", "objectPath": unit}}, {})
    masters = [x for x in h.query(library)["items"] if x.get("kind") == "masterCopy" and x.get("name") == UNIT]
    require(len(masters) == 1, "Expected one master copy %s: %s" % (UNIT, masters))
    payload = {"libraryId": library, "masterCopyPath": masters[0]["masterCopyPath"], "name": UNIT}
    refuse(h, "a unit master copy into the F-PLC", "software_unit_create_from", payload, {"plcPath": plc}, module=FDI[:-1])
    change(h, "tag_delete", {"tagTablePath": unit_table, "memberName": "UnitMarker", "dependencyPolicy": "rejectReferenced"}, s)
    change(h, "tag_table_delete", {"tagTablePath": unit_table}, s)
    change(h, "software_unit_delete", {"unitPath": unit, "dependencyPolicy": "rejectReferenced"}, s)
    change(h, "software_unit_create_from", payload, s)
    def created():
        scopes = [x["path"] for x in h.tool("software_inspect", {"plcPath": standard, "maxItems": 2000}).get("softwareScopes", [])]
        require(unit in scopes, "%s is not a scope of the standard PLC: %s" % (unit, scopes))
        restored = tables(h).get(unit_table)
        require(restored == [["UnitMarker", "Bool", "%M61.0", False]], "The copied unit's tag differs: %s" % restored)
        return {"scopes": scopes, "restoredTags": restored}
    h.stage("the created %s is a scope of the standard PLC" % UNIT, created)


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
    parser.add_argument("--git-executable", type=Path, required=True, help="Installed Git for the Host's --git-executable and the other engineer's clone")
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    harness = Harness(args, "fr_", "F-I/O tag-address refusals of tag_create, tag_set, tag_table_import_xml, vci_synchronize, vci_conflict_resolve, "
                      "library_instantiate and software_unit_create_from on an owned scratch F-CPU", LIMITATIONS, [])
    remote = harness.directory / "git-remote.git"
    # The Host's one configured remote: a new local bare repository owned by this run.
    subprocess.run([str(args.git_executable), "init", "--bare", "--initial-branch=main", str(remote)], check=True, capture_output=True, timeout=60,
                   env={**os.environ, "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull})
    args.host_args = ["--git-executable", str(args.git_executable.resolve()), "--git-remote", str(remote)]
    harness.run(body)
