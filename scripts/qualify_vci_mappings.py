"""Qualify the D01 VCI mapping kinds natively in one owned scratch CPU 1511-1 PN: vci_mapping_add, vci_synchronize and
vci_mapping_remove.

Phase A creates a workspace with a root outside the project and the artifact store and an SCL FC, then maps the FC by
export, synchronizes it to the workspace after a project edit and back into the project after a workspace-file edit, and
removes the mapping, each with checkpoint, compile and save. Each preview is asserted against the request, and a complete
state (every block, type and tag-table export, every member and group path, and every workspace with its mappings) must
differ from the previous one in exactly the intended keys; each mapping's compare state and the package files are checked
after every change. A fresh Portal then reopens the saved project, which must show the identical state. Phase B applies one
more change per kind and restores its checkpoint in the applying Portal; the recovered copy must equal the pre-change project
state (change_restore never restores workspace files, so mapping compare states are not part of that comparison). Typed
refusals must change nothing.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re

from library_native_harness import Harness, require
from qualification_owned_scratch import complete_inventory, snapshot
from qualify_access_changes import Run as StateRun, files_under, workspace_create
from qualify_plc_object_mutations import coverage_gaps, diff, view

KINDS = ("vci_mapping_add", "vci_synchronize", "vci_mapping_remove")
CRITERIA = {
    "AC-identity": "refused: typed refusals of unsupported formats, objects already mapped, missing packages, unknown workspaces, unmapped objects and too broad project-ward scopes, each leaving the complete state unchanged.",
    "AC-preview": "Every preview is asserted against the request and vci_workspace_inspect before apply; a wrong previewHash refuses at apply.",
    "AC-preserve": "applied: every program export and member and every workspace and mapping identity differ in exactly the intended keys; package files change only where the change writes them.",
    "AC-native": "applied + persisted: checkpoint, compile and save stages, change_status, the mapping compare state and package files after each change, and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the pre-change project state.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json, workspace file inventories and limitations.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1, one workspace in the root workspace group and SCL FCs mapped by export; connect mode, library types, "
    "nested directories and global library policies are not exercised.",
    "Workspace files are edited by this runner between changes; change_restore never restores them, so restore equality covers the project state only.",
    "Engineering only; no version-control system is used.",
]


def fc(name, value):
    return 'FUNCTION "%s" : Void\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\n   VAR_TEMP\n      Value : Int;\n   END_VAR\nBEGIN\n\t#Value := %d;\nEND_FUNCTION\n\n' % (name, value)


def portable(value):
    """Mapping identities without project paths, which a recovered copy changes."""
    if isinstance(value, dict):
        return {k: portable(v) for k, v in value.items() if k not in ("projectPath", "compareState", "compareDetails", "childMappingState", "supportedFormats")}
    return [portable(v) for v in value] if isinstance(value, list) else value


class Run(StateRun):
    plc_compiled = KINDS + ("vci_workspace_create", "source_upsert")

    def __init__(self, h):
        super().__init__(h)
        for key in ("certificateIdentity", "notQualified"):
            h.report.pop(key)
        h.report.update(criteria=CRITERIA, excluded={}, files={})

    def args(self, kind, payload, **top):
        return {"kind": kind, "payload": payload, "plcPath": self.plc, **top}

    def inspect(self):
        result = self.h.tool("vci_workspace_inspect")
        require(result["completeness"]["truncated"] is False, "vci_workspace_inspect truncated its scope")
        return result

    def domains(self):
        result = self.inspect()
        state = {"vciGroup:" + row["path"]: row for row in result["groups"]}
        state.update({"vciWorkspace:" + row["path"]: portable(row) for row in result["workspaces"]})
        return state

    def mapping(self, workspace, block):
        row = next(r for r in self.inspect()["workspaces"] if r["path"] == workspace)
        # vci_workspace_inspect identifies a mapped object by its native object chain, ending in the block.
        rows = [m for m in row["mappedObjects"] if (m.get("object") or [{}])[-1].get("Name") == block]
        return rows[0] if rows else None

    def state(self):
        return {**view(complete_inventory(self.h.tool)), **self.domains()}

    def snap(self, label):
        self.count += 1
        destination = self.h.directory / "snapshots" / ("%03d-%s" % (self.count, re.sub(r"[^A-Za-z0-9]+", "-", label)[:40]))
        taken = {}
        def take():
            taken.update(snapshot(self.h.tool, destination))
            return {"objects": len(taken["objects"])}
        self.h.stage("snapshot " + label, take)
        state = {**view(taken["inventory"], taken["objects"]), **self.domains()}
        (destination / "state.json").write_text(json.dumps(state, indent=1), encoding="utf-8")
        return {"view": state, "files": {key: row.get("file") for key, row in taken["objects"].items()}}

    def apply(self, label, kind, payload, preview=None, **expected):
        prepared = self.prepare(label, kind, payload, preview or {"effects": [{}]})
        self.finish(label, kind, prepared, **expected)
        return prepared


def inventory(root):
    root = Path(root)
    return {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in files_under(root)}


def compare_state(run, workspace, block, wanted):
    row = run.mapping(workspace, block)
    require(row is not None and row["compareState"] == wanted, "Mapping of %s is %s, expected %s" % (block, row and row["compareState"], wanted))


def add(run, workspace, root, block, fmt, restore=False):
    payload = {"workspacePath": workspace, "objectPath": run.plc + "/blocks/" + block, "mode": "export", "directory": "", "fileName": block, "format": fmt}
    before = inventory(root)
    def check(applied, state):
        compare_state(run, workspace, block, "Equal")
        files = inventory(root)
        require(set(before) < set(files) and all(files[name] == digest for name, digest in before.items()), "The export did not only add package files: %s" % sorted(set(files) ^ set(before)))
        run.h.report["files"]["add " + block] = sorted(set(files) - set(before))
    run.apply("map %s by export" % block, "vci_mapping_add", payload, {"effects": [{"action": "exportObject", "directory": "", "fileName": block, "format": fmt}]},
              changed=["vciWorkspace:" + workspace], check=check, restore=restore)


def synchronize(run, workspace, root, block, direction, changed, restore=False):
    payload = {"workspacePath": workspace, "objectPath": run.plc + "/blocks/" + block, "direction": direction}
    before = inventory(root)
    def check(applied, state):
        compare_state(run, workspace, block, "Equal")
        files = inventory(root)
        if direction == "WorkspaceToProject":
            require(files == before, "A project-ward synchronization changed workspace files")
        else:
            require(set(files) == set(before) and files != before, "A workspace-ward synchronization did not rewrite the package")
    run.apply("synchronize %s %s" % (block, direction), "vci_synchronize", payload, {"effects": [{"action": "synchronize", "direction": direction, "scope": "objectPath"}]},
              changed=changed, check=check, restore=restore)


def remove(run, workspace, root, block, restore=False):
    payload = {"workspacePath": workspace, "objectPath": run.plc + "/blocks/" + block}
    before = inventory(root)
    def check(applied, state):
        require(run.mapping(workspace, block) is None and inventory(root) == before, "The mapping remains or its package files changed")
    run.apply("remove the mapping of " + block, "vci_mapping_remove", payload, {"effects": [{"action": "removeMapping"}]},
              changed=["vciWorkspace:" + workspace], check=check, restore=restore)


def edit_package(root, block, old, new):
    """Edit the exported package text in the workspace, as a version-control checkout would."""
    for name in files_under(root):
        path = Path(root) / name
        if name.startswith(block) and path.suffix.lower() in (".scl", ".xml", ".s7dcl", ".s7res"):
            text = path.read_text(encoding="utf-8-sig")
            if old in text:
                path.write_text(text.replace(old, new), encoding="utf-8")
                return name
    raise AssertionError("No package file of %s holds %r: %s" % (block, old, files_under(root)))


def phase_a(run):
    workspace = workspace_create(run, "TiaMcpVci", run.root(1))
    root = run.current["view"]["vciWorkspace:" + workspace]["rootPath"]
    run.h.report["fixture"] = {"workspace": workspace, "root": root}
    for name in ("VciFc", "VciKeep"):
        run.apply("create " + name, "source_upsert", {"language": "SCL", "source": fc(name, 1), "expectedBlockNames": [name]},
                  {"effects": [{"action": "create", "blockName": name}]}, added=["block:%s/blocks/%s" % (run.plc, name)])
    base = {"workspacePath": workspace, "objectPath": run.plc + "/blocks/VciFc", "mode": "export", "directory": "", "fileName": "VciFc"}
    error = run.refuse("vci_mapping_add", "an unsupported format", {**base, "format": "TiaMcpBogus"}, "VciFormatUnsupported")
    formats = [f.strip() for f in error["message"].rsplit(":", 1)[1].split(",")]
    fmt = next((f for f in formats if "SCL" in f.upper() or "SIMATICSD" in f.upper().replace(" ", "")), formats[0])
    run.h.report["fixture"].update(formats=formats, format=fmt)
    run.refuse("vci_mapping_add", "an unknown workspace", {**base, "format": fmt, "workspacePath": workspace + "Missing"}, "VciWorkspaceNotFound")
    run.refuse("vci_mapping_add", "connect without package files", {**base, "format": fmt, "mode": "connect"}, "VciPackageMissing")
    run.refuse("vci_mapping_remove", "an unmapped object", {"workspacePath": workspace, "objectPath": base["objectPath"]}, "VciMappingNotFound")
    add(run, workspace, root, "VciFc", fmt)
    run.refuse("vci_mapping_add", "an object already mapped", {**base, "format": fmt, "fileName": "VciFcAgain"}, "VciMappingExists")
    run.refuse("vci_synchronize", "a project-ward synchronization of every mapping", {"workspacePath": workspace, "allMappedObjects": True, "direction": "WorkspaceToProject"}, "VciScopeTooBroad")
    block = "block:%s/blocks/VciFc" % run.plc
    run.apply("edit VciFc in the project", "source_upsert", {"language": "SCL", "source": fc("VciFc", 2), "expectedBlockNames": ["VciFc"]},
              {"effects": [{"action": "source_upsert"}]}, changed=[block])
    compare_state(run, workspace, "VciFc", "ProjectObjectChanged" if run.mapping(workspace, "VciFc")["compareState"] != "Equal" else "Equal")
    synchronize(run, workspace, root, "VciFc", "ProjectToWorkspace", [])
    run.h.report["files"]["edited"] = edit_package(root, "VciFc", ":= 2;", ":= 3;")
    synchronize(run, workspace, root, "VciFc", "WorkspaceToProject", [block])
    require(":= 3;" in Path(run.current["files"][block]).read_text(encoding="utf-8-sig") or "3" in run.current["view"][block].get("payloadSha256", ""),
            "The project block does not carry the workspace edit")
    add(run, workspace, root, "VciKeep", fmt)
    remove(run, workspace, root, "VciFc")


def reopen(run):
    h, final = run.h, run.current
    h.stage("close the changed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen the saved project", lambda: h.reopen_project(run.project))
    run.current = run.b0 = run.snap("reopened")
    h.stage("reopened state equals every verified change",
            lambda: require(run.b0["view"] == final["view"], "Reopened state differs: %s" % diff(final["view"], run.b0["view"])) or len(final["view"]))
    for kind, rows in list(run.coverage.items()):
        if rows.get("applied"):
            run.mark(kind, "persisted", "fresh Portal reopen")


def phase_b(run):
    workspace, root, fmt = run.h.report["fixture"]["workspace"], run.h.report["fixture"]["root"], run.h.report["fixture"]["format"]
    run.apply("edit VciKeep in the project", "source_upsert", {"language": "SCL", "source": fc("VciKeep", 5), "expectedBlockNames": ["VciKeep"]},
              {"effects": [{"action": "source_upsert"}]}, changed=["block:%s/blocks/VciKeep" % run.plc])
    run.b0 = run.current
    synchronize(run, workspace, root, "VciKeep", "ProjectToWorkspace", [], restore=True)
    add(run, workspace, root, "VciFc", fmt, restore=True)
    remove(run, workspace, root, "VciKeep", restore=True)


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("vci mappings")
    run.cpu, run.language = run.plc.rsplit("/plc/", 1)[0], "en-US"
    run.project = h.tool("tia_session_status")["projects"][0]["path"]
    run.current = run.snap("baseline")
    run.group = next(row["path"] for key, row in sorted(run.current["view"].items()) if key.startswith("vciGroup:") and row["kind"] == "system")
    phase_a(run)
    reopen(run)
    phase_b(run)
    gaps = coverage_gaps(run.coverage, KINDS)
    require(not gaps, "Kinds without applied, persisted, refused and restored evidence: %s" % gaps)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "vc_", "D01 VCI mappings in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
