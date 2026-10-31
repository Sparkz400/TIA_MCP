"""Qualify the D01 VCI mapping kinds natively in one owned scratch CPU 1511-1 PN: vci_mapping_add, vci_synchronize,
vci_mapping_remove and vci_conflict_resolve, with an external Git round trip through vci_push and vci_fetch.

Phase A creates a workspace with a root outside the project and the artifact store and an SCL FC, then maps the FC by
export, synchronizes it to the workspace after a project edit and back into the project after a workspace-file edit, and
removes the mapping, each with checkpoint, compile and save. Each preview is asserted against the request, and a complete
state (every block, type and tag-table export, every member and group path, and every workspace with its mappings) must
differ from the previous one in exactly the intended keys; each mapping's compare state and the package files are checked
after every change. Phase C maps a second FC in a second workspace, commits and pushes its package to a local bare remote
(the Host's --git-remote), and runs three rounds in which another engineer commits to the remote branch while the FC is
edited in TIA: vci_fetch, the fetched package checked out into the workspace, the two-sided conflict detected (VciConflict
both ways and a three-way vci_package_compare divergentChange), then resolved take=remote, take=native and take=merged
through vci_conflict_resolve, the result committed on top of the remote commit and pushed. TIA and the bare repository's
history are checked independently. A fresh Portal then reopens the saved project, which must show the identical state.
Phase B applies one more change per kind (one more conflict round for vci_conflict_resolve) and restores its checkpoint in
the applying Portal; the recovered copy must equal the pre-change project state (change_restore never restores workspace
files, so mapping compare states are not part of that comparison). Typed refusals must change nothing.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

from library_native_harness import Harness, require
from qualification_owned_scratch import complete_inventory, snapshot
from qualify_access_changes import Run as StateRun, files_under, upload, workspace_create
from qualify_plc_object_mutations import coverage_gaps, diff, view

KINDS = ("vci_mapping_add", "vci_synchronize", "vci_mapping_remove", "vci_conflict_resolve")
CRITERIA = {
    "AC-identity": "refused: typed refusals of unsupported formats, objects already mapped, missing packages, unknown workspaces, unmapped objects, too broad project-ward scopes, unresolved two-sided conflicts and resolutions without the reviewed remote side, each leaving the complete state unchanged.",
    "AC-preview": "Every preview is asserted against the request and vci_workspace_inspect before apply; the Host-bound resolution payload equals the reviewed comparison row and package bytes.",
    "AC-preserve": "applied: every program export and member and every workspace and mapping identity differ in exactly the intended keys; package files change only where the change writes them.",
    "AC-native": "applied + persisted: checkpoint, compile and save stages, change_status, the mapping compare state and package files after each change, no second apply of an applied resolution, and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the pre-change project state.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json, workspace file inventories, Git records and the bare remote's history, and limitations.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1, workspaces in the root workspace group and SCL FCs mapped by export; connect mode, library types, "
    "nested directories and global library policies are not exercised.",
    "Workspace files are edited by this runner between changes, including the checkout of a fetched revision; change_restore never restores them, "
    "so restore equality covers the project state only.",
    "Mapped blocks are edited in place (block_attributes_set): re-importing a mapped block from SCL source replaces the object and TIA drops its mapping.",
    "The Git remote is a local bare repository; the other engineer is plain Git in a runner-owned clone. No network remote or credentials are used.",
    "V21 has no project-neutral VCI export, so the three-way plan identifies the native side by the block's native export hash on the base package files.",
]
AUTHOR = {"name": "TIA MCP qualification", "email": "qualification@example.invalid"}


def fc(name, value):
    return 'FUNCTION "%s" : Void\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\n   VAR_TEMP\n      Value : Int;\n   END_VAR\nBEGIN\n\t#Value := %d;\nEND_FUNCTION\n\n' % (name, value)


def portable(value):
    """Mapping identities without project paths, which a recovered copy changes."""
    if isinstance(value, dict):
        return {k: portable(v) for k, v in value.items() if k not in ("projectPath", "compareState", "compareDetails", "childMappingState", "supportedFormats", "contentSha256")}
    return [portable(v) for v in value] if isinstance(value, list) else value


class Run(StateRun):
    plc_compiled = KINDS + ("vci_workspace_create", "source_upsert", "block_attributes_set")

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


def add(run, workspace, root, block, fmt, restore=False, file_name=None):
    file_name = file_name or block
    payload = {"workspacePath": workspace, "objectPath": run.plc + "/blocks/" + block, "mode": "export", "directory": "", "fileName": file_name, "format": fmt}
    before = inventory(root)
    def check(applied, state):
        compare_state(run, workspace, block, "Equal")
        files = inventory(root)
        require(set(before) < set(files) and all(files[name] == digest for name, digest in before.items()), "The export did not only add package files: %s" % sorted(set(files) ^ set(before)))
        run.h.report["files"]["add " + block] = sorted(set(files) - set(before))
    run.apply("map %s by export" % block, "vci_mapping_add", payload, {"effects": [{"action": "exportObject", "directory": "", "fileName": file_name, "format": fmt}]},
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


def edit_in_project(run, workspace, block, family):
    """An in-place header edit; a source_upsert would replace the block and TIA would drop its mapping."""
    run.apply("set %s's family to %s" % (block, family), "block_attributes_set", {"blockName": block, "attributes": {"HeaderFamily": family}},
              {"effects": [{"action": "block_attributes_set"}, {"attributes": [{"name": "HeaderFamily", "after": family}]}]}, changed=["block:%s/blocks/%s" % (run.plc, block)])
    require(run.mapping(workspace, block)["compareState"] != "Equal", "The project edit of %s is not visible to its mapping" % block)


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
    edit_in_project(run, workspace, "VciFc", "VciFam")
    synchronize(run, workspace, root, "VciFc", "ProjectToWorkspace", [])
    run.h.report["files"]["edited"] = edit_package(root, "VciFc", ":= 1;", ":= 3;")
    require(run.mapping(workspace, "VciFc")["compareState"] != "Equal", "The workspace edit is not visible to the mapping")
    synchronize(run, workspace, root, "VciFc", "WorkspaceToProject", [block])
    add(run, workspace, root, "VciKeep", fmt)
    remove(run, workspace, root, "VciFc")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def plain_git(run, directory, *arguments):
    """The other engineer: plain Git without user or system configuration, outside the Host's restricted profile."""
    environment = {**os.environ, "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull, "GIT_TERMINAL_PROMPT": "0"}
    result = subprocess.run([str(run.git_exe), "-c", "user.name=Other engineer", "-c", "user.email=other@example.invalid", "-c", "core.autocrlf=false",
                             "-c", "core.hooksPath=" + os.devnull, *arguments], cwd=str(directory), env=environment, capture_output=True, timeout=120)
    require(result.returncode == 0, "git %s failed: %s" % (" ".join(arguments), result.stderr.decode(errors="replace")))
    return result.stdout.decode("utf-8").strip()


def remote_git(run, *arguments):
    return plain_git(run, run.git["remote"], "--git-dir=" + str(run.git["remote"]), *arguments)


def download(h, tool, reference, **selector):
    data = bytearray()
    while True:
        page = h.tool(tool, {"recordRef": reference, "offset": len(data), **selector})
        data.extend(base64.b64decode(page["data"]))
        if page["complete"]:
            require(len(data) == page["bytes"] and sha(data) == page["sha256"], "Download from %s differs from its bound hash" % reference)
            return bytes(data)


def package_files(root, block):
    return {name: (Path(root) / name).read_bytes() for name in files_under(root) if name.split("/")[-1].startswith(block + ".")}


def block_key(run, block):
    return "block:%s/blocks/%s" % (run.plc, block)


def block_xml(run, block):
    return Path(run.current["files"][block_key(run, block)]).read_text(encoding="utf-8-sig")


def prepare_files(run, label, files, parent=None, branch=None):
    h = run.h
    selected = [{"relativePath": name, "uploadRef": upload(h, re.sub(r"[^A-Za-z0-9.]+", "_", name), data)} for name, data in sorted(files.items())]
    return h.stage("vci_prepare " + label, lambda: h.tool("vci_prepare", {"files": selected, "author": AUTHOR, "message": label,
                   **({"parentRevisionRef": parent} if parent else {}), **({"branchName": branch} if branch else {})}))


def commit(run, label, preview):
    return run.h.stage("vci_commit " + label, lambda: run.h.tool("vci_commit", {"previewRef": preview["recordRef"], "reviewedPlanSha256": preview["planSha256"]}))


def refuse_tool(run, label, tool, arguments, code):
    def check():
        error = run.h.call_error(tool, arguments)
        require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
        return error
    return run.h.stage("refuse %s: %s" % (tool, label), check)


def push(run, label, revision, expected):
    arguments = {"revisionRef": revision["recordRef"], "branchName": "main", **({"expectedRemoteCommit": expected} if expected else {})}
    def check():
        result = run.h.tool("vci_push", arguments)
        require(result["commit"] == revision["commit"] and result["expectedRemoteCommit"] == expected, "The push receipt differs: %s" % result)
        require(remote_git(run, "rev-parse", "refs/heads/main") == revision["commit"], "The bare remote does not hold the pushed commit")
        return result
    pushed = run.h.stage("vci_push " + label, check)
    run.git["pushes"].append(pushed["recordRef"])
    return pushed


def remote_commit(run, value):
    """Another engineer pulls the remote branch, changes the FC's value in the package document and pushes."""
    g = run.git
    plain_git(run, g["clone"], "pull", "--ff-only", "origin", "main")
    path = Path(g["clone"]) / g["document"]
    data = path.read_bytes()
    require(b":= %d;" % g["value"] in data, "The remote document lacks the value %d" % g["value"])
    path.write_bytes(data.replace(b":= %d;" % g["value"], b":= %d;" % value))
    plain_git(run, g["clone"], "commit", "-am", "Other engineer: VciGitFc value %d" % value)
    plain_git(run, g["clone"], "push", "origin", "main")
    return plain_git(run, g["clone"], "rev-parse", "HEAD")


def capture(run, label, source, content, block):
    """Supplied mapping evidence: the native side is the block's native export hash, the files are the record's package files."""
    g, h = run.git, run.h
    snapshot_id = {"observationId": "qualify-vci-git:" + label, "sourceSha256": content, "tiaVersion": 21}
    row = run.mapping(g["workspace"], block)
    evidence = {"schemaVersion": "vci-mapping-evidence.v1", "workspaceId": g["workspace"], "snapshot": snapshot_id, "enumerationComplete": True, "packages": [{
        "packageId": block, "nativeStatus": row["compareState"],
        "object": {"object": {"projectPath": run.project, "plcPath": run.plc, "scopePath": None, "objectPath": run.plc + "/blocks/" + block, "kind": "block"},
                   "snapshot": snapshot_id, "contentSha256": content},
        "profile": {"profileId": "vci-scl-fc", "family": "nativeDocument", "objectKind": "block", "nativeFormat": h.report["fixture"]["format"],
                    "evidenceSha256": sha(json.dumps(row, sort_keys=True).encode()), "evidenceOrigin": "nativeExport", "complete": True,
                    "expectedFiles": [{"relativePath": name, "role": "document"} for name in g["files"]]}}]}
    reference = upload(h, "mapping.json", json.dumps(evidence).encode("utf-8"))
    return h.stage("vci_package_capture " + label, lambda: h.tool("vci_package_capture", {"sourceRecordRef": source, "mappingUploadRef": reference}))


def compare(run, label, baseline, native, remote):
    h = run.h
    def check():
        result = h.tool("vci_package_compare", {"baselineRef": baseline["recordRef"], "projectRef": native["recordRef"], "workspaceRef": remote["recordRef"],
                        "expectedHashes": {"baseline": baseline["manifestSha256"], "project": native["manifestSha256"], "workspace": remote["manifestSha256"]}})
        rows = json.loads(download(h, "vci_artifact_read", result["recordRef"], name="comparison.json"))["rows"]
        require(result["conflictCount"] == 1 and len(rows) == 1 and rows[0]["state"] == "divergentChange" and rows[0]["projectChange"] == "modified"
                and rows[0]["workspaceChange"] == "modified", "The three-way plan does not report the two-sided conflict: %s" % rows)
        return {**result, "row": rows[0]}
    return h.stage("vci_package_compare " + label, check)


def content_hash(run, block):
    """The native-side hash of the plan: vci_workspace_inspect's export hash of the mapped object, which the worker rechecks."""
    content = (run.mapping(run.git["workspace"], block) or {}).get("contentSha256")
    require(isinstance(content, str) and re.fullmatch("[0-9a-f]{64}", content), "vci_workspace_inspect reports no content hash for %s" % block)
    return content


def resolve(run, label, take, comparison, remote_files, result_files, native_content, merged=None, changed=(), restore=False, check=None):
    g, block = run.git, "VciGitFc"
    payload = {"workspacePath": g["workspace"], "objectPath": run.plc + "/blocks/" + block, "comparisonRef": comparison["recordRef"], "packageId": block,
               "take": take, **({"mergedRecordRef": merged} if merged else {})}
    direction = "ProjectToWorkspace" if take == "native" else "WorkspaceToProject"
    def rows(files, content):
        return [{"path": name, "sha256": sha(data), "bytes": len(data), "contentBase64": base64.b64encode(data).decode("ascii") if content else None}
                for name, data in sorted(files.items())]
    row = comparison["row"]
    shown = {"workspacePath": g["workspace"], "objectPath": payload["objectPath"], "take": take, "direction": direction,
             "plan": {"comparisonRef": comparison["recordRef"], "reportSha256": comparison["reportSha256"], "workspaceId": g["workspace"], "packageId": block,
                      "state": "divergentChange", "projectObjectContentSha256": native_content,
                      "baselineContentSha256": row["baselineContentSha256"], "projectContentSha256": row["projectContentSha256"],
                      "workspaceContentSha256": row["workspaceContentSha256"], "mergedRecordRef": merged},
             "workspaceFiles": rows(remote_files, False), "resultFiles": rows(result_files, True) if take != "native" else []}
    # The worker re-exports the block at prepare; its live hash must be the reviewed native side.
    preview = {"effects": [{"action": "resolveConflict", "take": take, "direction": direction, "nativeConflict": True, "projectObjectContentSha256": native_content,
                            "resultFiles": [{k: v for k, v in r.items() if k != "contentBase64"} for r in shown["resultFiles"]]}]}
    def verify(applied, state):
        compare_state(run, g["workspace"], block, "Equal")
        files = package_files(g["root"], block)
        require(files != remote_files if take == "native" else files == result_files, "The workspace package after %s differs from the resolution" % label)
        run.h.report["files"][label] = {name: sha(data) for name, data in files.items()}
        if check:
            check(files)
    prepared = run.prepare(label, "vci_conflict_resolve", payload, preview, shown=shown)
    run.finish(label, "vci_conflict_resolve", prepared, changed=list(changed), check=verify, restore=restore)
    if not restore:
        refuse = lambda: run.h.call_error("change_apply", run.h.apply_args(prepared))
        run.h.stage("an applied resolution is never applied again: " + label,
                    lambda: require(refuse().get("code") == "InvalidChangeState", "A second apply was not refused") or "InvalidChangeState")
    return prepared


def git_setup(run):
    h, g, block = run.h, run.git, "VciGitFc"
    workspace = workspace_create(run, "TiaMcpVciGit", run.root(2))
    root = run.current["view"]["vciWorkspace:" + workspace]["rootPath"]
    run.apply("create " + block, "source_upsert", {"language": "SCL", "source": fc(block, 1), "expectedBlockNames": [block]},
              {"effects": [{"action": "create", "blockName": block}]}, added=[block_key(run, block)])
    add(run, workspace, root, block, h.report["fixture"]["format"])
    files = package_files(root, block)
    g.update(workspace=workspace, root=root, files=sorted(files), document=next(name for name, data in files.items() if b":= 1;" in data), value=1)
    base = commit(run, "Baseline VciGitFc", prepare_files(run, "Baseline VciGitFc", files, branch="main"))
    push(run, "baseline", base, None)
    refuse_tool(run, "a remote branch that already exists", "vci_push", {"revisionRef": base["recordRef"], "branchName": "main"}, "GitReferenceConflict")
    plain_git(run, Path(g["clone"]).parent, "clone", "--branch", "main", str(g["remote"]), str(g["clone"]))
    g.update(base=base["recordRef"], head=base["commit"], baseContent=content_hash(run, block), commits=[base["commit"]])


def git_round(run, take, value, family, restore=False):
    """Remote commit and native edit on the same FC, fetch, checkout, conflict detected, resolved by take; the result goes back to Git."""
    g, h, block = run.git, run.h, "VciGitFc"
    label = "%s resolution %d" % (take, len(g["rounds"]) + 1)
    first = not g["rounds"]
    remote_head = remote_commit(run, value)
    edit_in_project(run, g["workspace"], block, family)
    native_content = content_hash(run, block)
    require(family in block_xml(run, block), "The native edit %s is not in the block export" % family)
    if restore:
        run.b0 = run.current
    if first:
        refuse_tool(run, "a stale expected remote commit", "vci_fetch", {"branchName": "main", "parentRevisionRef": g["base"], "expectedRemoteCommit": g["head"]}, "PreviewStale")
    fetched = h.stage("vci_fetch " + label, lambda: h.tool("vci_fetch", {"branchName": "main", "parentRevisionRef": g["base"], "expectedRemoteCommit": remote_head}))
    require(fetched["commit"] == remote_head and fetched["previousCommit"] == g["head"], "The fetch did not fast-forward the base to the remote commit")
    remote_files = {name: download(h, "vci_file_read", fetched["recordRef"], relativePath=name) for name in g["files"]}
    require(b":= %d;" % value in remote_files[g["document"]], "The fetched document lacks the remote value")
    comparison = compare(run, label, capture(run, label + " baseline", g["base"], g["baseContent"], block),
                         capture(run, label + " native", g["base"], native_content, block), capture(run, label + " remote", fetched["recordRef"], g["baseContent"], block))
    request = {"workspacePath": g["workspace"], "objectPath": run.plc + "/blocks/" + block, "comparisonRef": comparison["recordRef"], "packageId": block, "take": "remote"}
    if first:
        run.refuse("vci_conflict_resolve", "a resolution before the remote side is checked out", request, "VciResolutionNotNeeded")
    for name, data in remote_files.items():  # The checkout a Git client would make of the fetched revision.
        (Path(g["root"]) / name).write_bytes(data)
    if first:
        sync = {"workspacePath": g["workspace"], "objectPath": run.plc + "/blocks/" + block}
        run.refuse("vci_synchronize", "an unresolved two-sided conflict, project-ward", {**sync, "direction": "WorkspaceToProject"}, "VciConflict")
        run.refuse("vci_synchronize", "an unresolved two-sided conflict, workspace-ward", {**sync, "direction": "ProjectToWorkspace"}, "VciConflict")
    entry = {"take": take, "remoteCommit": remote_head, "family": family, "remoteValue": value, "fetch": fetched["recordRef"], "comparison": comparison["recordRef"]}
    g["rounds"].append(entry)
    g["commits"].append(remote_head)
    if take == "remote":
        def won(files):
            require(family not in block_xml(run, block), "The native edit survived take=remote")
        resolve(run, label, take, comparison, remote_files, remote_files, native_content, changed=[block_key(run, block)], restore=restore, check=won)
        g.update(base=fetched["recordRef"], head=remote_head, value=value)
    elif take == "native":
        def kept(files):
            document = files[g["document"]]
            require(b":= %d;" % g["value"] in document and b":= %d;" % value not in document and family in block_xml(run, block),
                    "take=native did not keep the project's value and edit")
        resolve(run, label, take, comparison, remote_files, {}, native_content, restore=restore, check=kept)
        files = package_files(g["root"], block)
        entry["familyInDocument"] = family.encode() in files[g["document"]]
        if not restore:
            preview = prepare_files(run, "Resolve VciGitFc: take native", {n: d for n, d in files.items() if d != remote_files.get(n)}, parent=fetched["recordRef"])
            refuse_tool(run, "a stale expected remote commit", "vci_push", {"revisionRef": g["base"], "branchName": "main", "expectedRemoteCommit": g["head"]}, "GitReferenceConflict")
            resolved = commit(run, "Resolve VciGitFc: take native", preview)
            push(run, label, resolved, remote_head)
            g.update(base=resolved["recordRef"], head=resolved["commit"])
            g["commits"].append(resolved["commit"])
    else:
        document = remote_files[g["document"]]
        previous = g["rounds"][-2]["family"] if len(g["rounds"]) > 1 else None
        # A real merge keeps the remote value and the native family when the document carries the family; otherwise a new reviewed value.
        merged_family = bool(previous) and previous.encode() in document
        merged = document.replace(previous.encode(), family.encode()) if merged_family else document.replace(b":= %d;" % value, b":= %d;" % (value + 1))
        entry.update(mergedFamily=merged_family, mergedSha256=sha(merged))
        result = {**remote_files, g["document"]: merged}
        preview = prepare_files(run, "Resolve VciGitFc: merged", {g["document"]: merged}, parent=fetched["recordRef"])
        def combined(files):
            require((family in block_xml(run, block)) == merged_family, "The merged document's native edit is not what TIA imported")
        resolve(run, label, take, comparison, remote_files, result, native_content, merged=preview["recordRef"], changed=[block_key(run, block)], restore=restore, check=combined)
        resolved = commit(run, "Resolve VciGitFc: merged", preview)
        push(run, label, resolved, remote_head)
        g.update(base=resolved["recordRef"], head=resolved["commit"], value=value if merged_family else value + 1)
        g["commits"].append(resolved["commit"])
    g["baseContent"] = content_hash(run, block)


def git_history(run, label):
    """The bare remote's first-parent history, independently of the Host: newest first, every round's commit in order."""
    def check():
        rows = [line.split("\x00") for line in remote_git(run, "log", "--first-parent", "--format=%H%x00%P%x00%an%x00%s", "refs/heads/main").splitlines()]
        require([r[0] for r in rows] == list(reversed(run.git["commits"])), "The remote history differs: %s" % rows)
        history = run.h.tool("vci_history", {"revisionRef": run.git["base"], "count": 50})["history"]
        require(all(commit in history for commit in run.git["commits"]), "vci_history of the last base misses a remote commit")
        run.h.report["git"]["history"] = rows
        return {"commits": len(rows)}
    run.h.stage("Git history " + label, check)


def phase_c(run):
    git_setup(run)
    git_round(run, "remote", 5, "NativeA")
    git_round(run, "native", 7, "NativeB")
    git_round(run, "merged", 8, "NativeC")
    git_history(run, "after the three resolutions")


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
    edit_in_project(run, workspace, "VciKeep", "VciAgain")
    run.b0 = run.current
    synchronize(run, workspace, root, "VciKeep", "ProjectToWorkspace", [], restore=True)
    # vci_mapping_remove keeps the package files, so a new export needs a new file name (the old one refuses with VciPackageCollision).
    run.refuse("vci_mapping_add", "a package left by a removed mapping", {"workspacePath": workspace, "objectPath": run.plc + "/blocks/VciFc", "mode": "export",
                                                                          "directory": "", "fileName": "VciFc", "format": fmt}, "VciPackageCollision")
    add(run, workspace, root, "VciFc", fmt, restore=True, file_name="VciFcAgain")
    remove(run, workspace, root, "VciKeep", restore=True)
    git_round(run, "remote", 6, "NativeD", restore=True)
    git_history(run, "after the restored resolution")


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("vci mappings")
    run.cpu, run.language = run.plc.rsplit("/plc/", 1)[0], "en-US"
    run.project = h.tool("tia_session_status")["projects"][0]["path"]
    run.current = run.snap("baseline")
    run.group = next(row["path"] for key, row in sorted(run.current["view"].items()) if key.startswith("vciGroup:") and row["kind"] == "system")
    run.git_exe = h.args.git_executable
    run.git = {"remote": str(h.directory / "git-remote.git"), "clone": str(h.directory / "git-other-engineer"), "rounds": [], "pushes": []}
    h.report["git"] = run.git
    phase_a(run)
    phase_c(run)
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
    parser.add_argument("--git-executable", type=Path, required=True, help="Installed Git for the Host's --git-executable and the other engineer's clone")
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    harness = Harness(args, "vc_", "D01 VCI mappings and an external Git round trip in one owned scratch CPU 1511-1 PN", LIMITATIONS, [])
    remote = harness.directory / "git-remote.git"
    # The Host's one configured remote: a new local bare repository owned by this run.
    subprocess.run([str(args.git_executable), "init", "--bare", "--initial-branch=main", str(remote)], check=True, capture_output=True, timeout=60,
                   env={**os.environ, "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull})
    args.host_args = ["--git-executable", str(args.git_executable.resolve()), "--git-remote", str(remote)]
    harness.run(body)
