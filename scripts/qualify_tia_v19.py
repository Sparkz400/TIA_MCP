"""Qualify the read-only TIA Portal V19 adapter (X12, tia.multiversion) and V19-to-V21 project_upgrade (X01) natively.

1. Seed: V19ProjectSeeder creates an owned V19 fixture in its own V19 instance (CPU 1511-1 PN; tag table with tags and a
   constant; PLC data type; FC, FB, global DB, instance DB and OB; a software unit with its own type, FB, DB and tag
   table), compiles it without errors, saves, archives it as .zap19 and closes it.
2. V19 Host (--tia-version 19): it advertises exactly the base tools and the twelve V19 read tools and no prompt, a V21
   tool is an unknown tool, and tia_capabilities names the V19 API. In an owned V19 instance, project_open refuses an
   .ap21 path and a missing .ap19, opens the fixture, refuses a second open, and project_inventory, software_inspect,
   block_read, type_read and tag_table_read return every seeded object at the V21 worker's canonical paths, with V19
   SimaticML; the export formats are refused. readOnly opens a secondary project. A Portal the seeder holds the fixture
   open in is discovered, attached, read and left running after tia_detach. Each owned V19 Portal exits after detach.
3. V21 Host: project_upgrade upgrades the fixture's project folder and its archive into V21 on owned copies with an
   unchanged source; each upgraded project reopens with the same inventory below the project segment, and every block,
   PLC data type and tag table equals its V19 export in the compared content (block attributes and interface members;
   type members; tags and constants). Because the upgrade does not compile and V21 refuses to export inconsistent blocks,
   each upgraded copy is compiled with a checkpointed plc_compile change first, which must leave every block consistent.
   While the seeder holds the fixture open in V19, its upgrade is refused with SourceLocked.

The fixture stays in the run (it is synthetic and names no local folder). Its archive and every project file are hashed
before and after; each V19 open (adapter primary and read-only, the held Portal) may change only TIA's search index
(IM/SearchIndex), which its background indexer rewrites, and the .ap19 file must stay byte-identical. While the seeder
holds the fixture open, V21's project_upgrade refuses it with SourceLocked.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET

from block_test_process import OwnedTiaProcessWatcher
from compilation_evidence import require_compile_only
from library_native_harness import Harness, require
from run_block_tests import wait_owned_tia_exit

V19_TOOLS = {"tia_discover", "tia_capabilities", "tia_attach", "tia_session_status", "tia_start", "tia_detach", "project_open",
             "project_inventory", "software_inspect", "block_read", "type_read", "tag_table_read"}
BASE_TOOLS = {"feature_catalog", "host_build_identity", "artifact_read", "artifact_upload_abort", "artifact_upload_begin",
              "artifact_upload_chunk", "artifact_upload_commit", "artifact_upload_release", "artifact_upload_status"}
PROJECT = "TiaMcpV19Fixture"
# What the seeder authors, by scope; generated system blocks and types are not part of it.
SEEDED = {
    "plc": {"blocks": {"Main", "TiaMcp_Data", "TiaMcp_IsEnabled", "TiaMcp_MotorCtrl", "TiaMcp_MotorCtrl_DB"},
            "types": {"TiaMcp_Motor"}, "tagTables": {"TiaMcp_Tags"}},
    "softwareUnit": {"blocks": {"TiaMcp_Belt", "TiaMcp_Belt_DB"}, "types": {"TiaMcp_BeltState"}, "tagTables": {"TiaMcp_ConveyorTags"}},
}
LIMITATIONS = [
    "One synthetic V19 fixture (CPU 1511-1 PN, SCL blocks generated from source, one software unit); LAD/FBD/GRAPH, HMI, drives, safety, technology objects, libraries and know-how protection are not part of it.",
    "The V19 adapter is read-only: discover, attach, start, status, detach, project_open, project_inventory, software_inspect and native-XML block_read, type_read and tag_table_read. No change, compile, save or archive route exists for V19.",
    "Upgrade content comparison covers block attributes (name, number, language, memory layout) and interface members (name, data type, start value), PLC data type members, and tags (name, data type, address) and constants (name, data type, value). Network code is not compared.",
    "Only TIA V19 Update 5 (Openness 19.0.0.0) on this workstation; V15.1, V17, V18 and V20 sources and adapters are not exercised.",
    "project_upgrade does not compile; the qualifier compiles each owned upgraded copy (plc_compile) before comparing, so the comparison is of the compiled V21 state.",
]


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def fingerprint(folder):
    """Every file below a project folder: relative name, size and SHA-256."""
    return {p.relative_to(folder).as_posix(): sha256(p) for p in sorted(Path(folder).rglob("*")) if p.is_file()}


# TIA's background search indexer rewrites these files whenever a Portal opens the project (primary or secondary);
# they are not project content. Every other source file must stay byte-identical.
SEARCH_INDEX = "IM/SearchIndex/"


class SourceWatch:
    """The fixture's project folder as of the last check; each check records what an open changed."""

    def __init__(self, h, folder):
        self.h, self.folder, self.state = h, folder, fingerprint(folder)
        self.h.report["sourceChanges"] = {}

    def check(self, phase):
        after = fingerprint(self.folder)
        changed = sorted(name for name in set(self.state) | set(after) if self.state.get(name) != after.get(name))
        outside = [name for name in changed if not name.startswith(SEARCH_INDEX)]
        require(not outside, "%s changed project files outside TIA's search index: %s" % (phase, outside))
        self.h.report["sourceChanges"][phase] = {"searchIndexFiles": len(changed)}
        self.state = after
        return {"searchIndexFiles": len(changed)}


def local(tag):
    return tag.rsplit("}", 1)[-1]


def children(element, name):
    return [child for child in element if local(child.tag) == name]


def descendants(element, name):
    return [node for node in element.iter() if local(node.tag) == name]


def attributes(element):
    """An object's AttributeList as {name: text}; the SimaticML schema keeps them as child elements."""
    lists = children(element, "AttributeList")
    return {local(a.tag): (a.text or "").strip() for a in lists[0]} if lists else {}


def members(section):
    rows = []
    for member in children(section, "Member"):
        start = children(member, "StartValue")
        rows.append({"name": member.get("Name"), "datatype": member.get("Datatype"), "startValue": (start[0].text or "").strip() if start else None,
                     "members": members(member)})
    return rows


def interface(element):
    return {section.get("Name"): members(section) for section in descendants(element, "Section")}


def content(xml_text, kind):
    """The compared content of one native export, independent of the SimaticML engineering version."""
    root = ET.fromstring(xml_text.encode("utf-8"))
    engineering = [e.get("version") for e in root.iter() if local(e.tag) == "Engineering"]
    if kind == "block":
        block = next(e for e in root if local(e.tag).startswith("SW.Blocks."))
        attrs = attributes(block)
        return engineering, {"kind": local(block.tag), **{key: attrs.get(key) for key in ("Name", "Number", "ProgrammingLanguage", "MemoryLayout")},
                             "interface": interface(block)}
    if kind == "type":
        node = next(e for e in root if local(e.tag).startswith("SW.Types."))
        return engineering, {"kind": local(node.tag), "name": attributes(node).get("Name"), "interface": interface(node)}
    table = next(e for e in root if local(e.tag) == "SW.Tags.PlcTagTable")
    tags = sorted((attributes(t).get("Name"), attributes(t).get("DataTypeName"), attributes(t).get("LogicalAddress")) for t in descendants(table, "SW.Tags.PlcTag"))
    constants = sorted((attributes(c).get("Name"), attributes(c).get("DataTypeName"), attributes(c).get("Value")) for c in descendants(table, "SW.Tags.PlcUserConstant"))
    return engineering, {"name": attributes(table).get("Name"), "tags": [list(t) for t in tags], "constants": [list(c) for c in constants]}


def below_project(path):
    """A canonical path without its /projects/<name> prefix, so a V19 path and its upgraded V21 path compare equal."""
    parts = path.split("/")
    require(parts[1] == "projects", "Not a project path: " + path)
    return "/" + "/".join(parts[3:])


class Fixture:
    """Seeder subprocesses: create, and hold (keeps the fixture open in a separate V19 Portal until released)."""

    def __init__(self, h):
        self.h = h

    def create(self):
        directory = self.h.directory / "fixture"
        with tempfile.TemporaryFile("w+", encoding="utf-8") as output:
            completed = subprocess.run([str(self.h.args.seeder), "create", str(directory)], stdout=output, stderr=subprocess.STDOUT, timeout=1800)
            output.seek(0)
            result = json.loads(output.read().strip().splitlines()[-1])
        require(completed.returncode == 0 and result.get("completed") is True, "Seeding failed: %s" % result)
        require(result["compile"]["errorCount"] == 0, "The fixture does not compile")
        project = Path(result["project"]["path"])
        require(project.suffix == ".ap19" and project.stem == PROJECT and directory in project.parents, "Unexpected fixture project: %s" % project)
        require(sha256(result["archive"]["path"]) == result["archive"]["sha256"], "The archive hash differs from the seeder's")
        scopes = {scope["kind"]: scope for scope in result["inventory"]["scopes"]}
        for kind, expected in SEEDED.items():
            require({b["name"] for b in scopes[kind]["blocks"]} == expected["blocks"], "Seeded %s blocks differ: %s" % (kind, scopes[kind]["blocks"]))
            # TIA adds each scope's default tag table, named in the Portal's UI language.
            require(set(scopes[kind]["types"]) == expected["types"] and expected["tagTables"] <= {t["name"] for t in scopes[kind]["tagTables"]},
                    "Seeded %s types or tag tables differ" % kind)
        return result

    def hold(self, project):
        self.release = self.h.directory / ("release-%d" % time.time_ns())
        self.output = tempfile.TemporaryFile("w+", encoding="utf-8")
        self.process = subprocess.Popen([str(self.h.args.seeder), "hold", str(project), str(self.release)], stdout=self.output, stderr=subprocess.STDOUT)
        for _ in range(240):
            self.output.seek(0)
            lines = [json.loads(line) for line in self.output.read().splitlines() if line.strip()]
            if lines:
                require(lines[0].get("holding") is True, "The seeder did not hold the fixture: %s" % lines[0])
                return lines[0]
            require(self.process.poll() is None, "The seeder exited before holding the fixture")
            time.sleep(1)
        raise AssertionError("The seeder did not open the fixture within four minutes")

    def unhold(self):
        self.release.write_text("release", encoding="utf-8")
        code = self.process.wait(timeout=300)
        self.output.seek(0)
        lines = [json.loads(line) for line in self.output.read().splitlines() if line.strip()]
        self.output.close()
        require(code == 0 and lines[-1] == {"holding": False, "released": True, "saved": False}, "The holding seeder did not end cleanly: %s %s" % (code, lines))
        return lines[-1]


def connect(h, version):
    """A fresh Host for one TIA version; the previous Host closes first and its receipt stays in the report."""
    h.args.worker = h.args.v19_worker if version == 19 else h.args.v21_worker
    h.args.host_args = ["--tia-version", "19"] if version == 19 else []
    h.connect()


def owned(h, label):
    """tia_start with the exact owned process captured; returns the watcher that must see it exit after detach."""
    h.stage("start owned TIA " + label, lambda: h.tool("tia_start"))
    status = h.tool("tia_session_status")
    require(status.get("ownsPortalProcess") is True and not status.get("projects"), "Session must be owned and empty")
    return OwnedTiaProcessWatcher(status["processId"])


def release(h, watcher, label):
    h.stage("detach " + label, lambda: h.tool("tia_detach"))
    record = {}
    h.stage("the owned TIA of %s exits" % label, lambda: wait_owned_tia_exit(watcher, True, record) and record["tiaProcessExit"])


def inventory_view(result):
    require(result["completeness"]["truncated"] is False and not result.get("warnings"), "Inventory is truncated or has warnings: %s" % result.get("warnings"))
    return {"hardware": sorted([below_project(r["path"]), r.get("typeIdentifier")] for r in result["hardware"]),
            "plcs": sorted(below_project(r["path"]) for r in result["plcs"]),
            "blocks": sorted([below_project(r["path"]), r["name"], r["number"], r["kind"], r["programmingLanguage"]] for r in result["blocks"]),
            "consistency": {below_project(r["path"]): r["isConsistent"] for r in result["blocks"]}}


def seeded_objects(h, plc_path):
    """Every seeded block, type and tag table by canonical path, from project_inventory and software_inspect."""
    inventory = h.tool("project_inventory", {"maxItems": 2000})
    software = h.tool("software_inspect", {"maxItems": 2000, **({"plcPath": plc_path} if plc_path else {})})
    require(software["completeness"]["truncated"] is False and not software["warnings"], "software_inspect is incomplete")
    scopes = {scope["kind"]: scope for scope in software["softwareScopes"]}
    require(set(scopes) == {"plc", "softwareUnit"} and scopes["softwareUnit"]["name"] == "TiaMcp_Conveyor", "Unexpected scopes: %s" % list(scopes))
    require(sorted(b["path"] for b in software["blocks"]) == sorted(b["path"] for b in inventory["blocks"]), "software_inspect and project_inventory list different blocks")
    objects = {"block": {}, "type": {}, "tagTable": {}}
    for kind, expected in SEEDED.items():
        scope = scopes[kind]["path"]
        blocks = {b["name"]: b["path"] for b in software["blocks"] if b["scopePath"] == scope and "/systemGroups/" not in b["path"]}
        types = {t["name"]: t["path"] for t in software["types"] if t["scopePath"] == scope and "/systemGroups/" not in t["path"]}
        tables = {t["name"]: t["path"] for t in software["tagTables"] if t["scopePath"] == scope}
        require(expected["blocks"] <= set(blocks) and set(types) == expected["types"] and expected["tagTables"] <= set(tables),
                "%s objects differ: blocks %s types %s tables %s" % (kind, sorted(blocks), sorted(types), sorted(tables)))
        objects["block"].update({blocks[n]: n for n in expected["blocks"]})
        objects["type"].update({types[n]: n for n in expected["types"]})
        objects["tagTable"].update({tables[n]: n for n in expected["tagTables"]})
    return inventory, software, objects


READERS = {"block": ("block_read", "blockPath"), "type": ("type_read", "typePath"), "tagTable": ("tag_table_read", "tagTablePath")}


def read_all(h, objects, version):
    """Export every seeded object and return its compared content by path below the project."""
    contents = {}
    for kind, rows in objects.items():
        tool, key = READERS[kind]
        for path in sorted(rows):
            result = h.tool(tool, {key: path})
            require(result["identity"]["objectPath"] == path if "identity" in result else result["block"]["path"] == path, "%s answered another object" % tool)
            require(result["artifact"]["sha256"] == sha256(result["artifact"]["path"]), "The export differs from its reported hash")
            engineering, compared = content(Path(result["artifact"]["path"]).read_text(encoding="utf-8-sig"), kind)
            require(engineering and set(engineering) == {"V%d" % version}, "%s exported SimaticML %s, not V%d" % (path, engineering, version))
            contents[below_project(path)] = {"kind": kind, "content": compared, "sha256": result["artifact"]["sha256"]}
    return contents


def body(h):
    fixture = Fixture(h)
    seeded = h.stage("seed the V19 fixture", fixture.create)
    h.report["fixture"] = {key: seeded[key] for key in ("cpu", "compile", "inventory", "apiAssemblyVersion")}
    project, archive = Path(seeded["project"]["path"]), Path(seeded["archive"]["path"])
    sources = {"projectFolder": fingerprint(project.parent), "archive": sha256(archive)}
    h.report["sourceFiles"] = len(sources["projectFolder"])
    watch = SourceWatch(h, project.parent)
    v19 = adapter(h, fixture, project, watch)
    upgrades(h, fixture, project, archive, v19, watch)
    def sources_unchanged():
        after = fingerprint(project.parent)
        changed = sorted(name for name in set(sources["projectFolder"]) | set(after) if sources["projectFolder"].get(name) != after.get(name))
        outside = [name for name in changed if not name.startswith(SEARCH_INDEX)]
        require(not outside and sha256(archive) == sources["archive"], "The fixture's source files changed: %s" % outside)
        require(after[project.name] == sources["projectFolder"][project.name], "The .ap19 project file changed")
        return {"files": len(after), "searchIndexFilesChanged": len(changed), "archive": sources["archive"]}
    h.stage("the fixture's archive and every project file outside TIA's search index are unchanged", sources_unchanged)


def adapter(h, fixture, project, watch):
    connect(h, 19)
    def surface():
        tools = {t["name"] for t in h.client.request("tools/list", {})["tools"]}
        require(tools == V19_TOOLS | BASE_TOOLS, "Unexpected V19 surface: extra %s missing %s" % (sorted(tools - V19_TOOLS - BASE_TOOLS), sorted(V19_TOOLS | BASE_TOOLS - tools)))
        require(h.client.request("prompts/list", {})["prompts"] == [], "A V19 Host advertises prompts")
        for hidden in ("change_prepare", "project_upgrade", "tia_create_scratch", "library_list"):
            try:
                h.client.request("tools/call", {"name": hidden, "arguments": {}})
                raise AssertionError(hidden + " is callable on a V19 Host")
            except RuntimeError as error:
                require("Unknown tool" in str(error), "%s: %s" % (hidden, error))
        return sorted(tools)
    h.stage("the V19 Host advertises only the base and V19 read tools", surface)
    info = h.stage("tia_capabilities names the V19 adapter", lambda: h.tool("tia_capabilities"))
    require(info["tiaVersion"] == 19 and info["apiAssemblyVersion"].startswith("19.") and info["readOnly"] is True, "Not the V19 adapter: %s" % info)

    watcher = owned(h, "V19 read")
    refusal = lambda args, code: h.stage("project_open refuses %s" % code, lambda: (lambda e: require(e.get("code") == code, str(e)) or e["code"])(h.call_error("project_open", args)))
    refusal({"path": str(project.with_suffix(".ap21"))}, "ProjectVersionUnsupported")
    refusal({"path": str(project.with_name("Missing.ap19"))}, "ProjectNotFound")
    opened = h.stage("open the V19 fixture", lambda: h.tool("project_open", {"path": str(project)}))
    require(opened["opened"] is True and opened["readOnly"] is False and opened["saved"] is False, "Unexpected open result: %s" % opened)
    refusal({"path": str(project)}, "ProjectIsolationRequired")
    inventory, software, objects = h.stage("inventory and software scopes of the V19 fixture", lambda: seeded_objects(h, None))
    view = inventory_view(inventory)
    require(all(view["consistency"][below_project(p)] for p in objects["block"]), "A seeded block is inconsistent in V19")
    contents = h.stage("read every seeded block, type and tag table as V19 XML", lambda: read_all(h, objects, 19))
    def formats():
        path = next(iter(objects["block"]))
        codes = [h.call_error("block_read", {"blockPath": path, "format": "generatedSource"}).get("code"),
                 h.call_error("type_read", {"typePath": next(iter(objects["type"])), "format": "simaticSd"}).get("code")]
        require(codes == ["UnsupportedOnTiaVersion"] * 2, "Export formats were not refused: %s" % codes)
        return codes
    h.stage("the V19 adapter refuses the V21 export formats", formats)
    release(h, watcher, "V19 read")
    h.stage("the primary open changed no project file outside TIA's search index", lambda: watch.check("primaryOpen"))

    watcher = owned(h, "V19 read-only")
    secondary = h.stage("open the V19 fixture read-only", lambda: h.tool("project_open", {"path": str(project), "readOnly": True}))
    require(secondary["readOnly"] is True, "readOnly did not open a secondary project")
    require(inventory_view(h.tool("project_inventory", {"maxItems": 2000})) == view, "The read-only inventory differs")
    def readonly_export():
        error = h.call_error("block_read", {"blockPath": next(iter(objects["block"]))})
        require(error.get("code") == "ReadOnlyProject", "Expected TIA's read-only export refusal: %s" % error)
        return error["code"]
    h.stage("block_read on the read-only project is refused with ReadOnlyProject", readonly_export)
    release(h, watcher, "V19 read-only")
    h.stage("the read-only open changed no project file outside TIA's search index", lambda: watch.check("secondaryOpen"))

    held = h.stage("the seeder holds the fixture open in its own V19 Portal", lambda: fixture.hold(project))
    def attached():
        discovered = h.tool("tia_discover")["processes"]
        require(any(p["processId"] == held["portalProcessId"] for p in discovered), "tia_discover does not list the holding Portal: %s" % discovered)
        status = h.tool("tia_attach", {"processId": held["portalProcessId"]})
        require(status["ownsPortalProcess"] is False and [p["name"] for p in status["projects"]] == [PROJECT], "Unexpected attachment: %s" % status)
        require(inventory_view(h.tool("project_inventory", {"maxItems": 2000})) == view, "The attached inventory differs")
        h.tool("tia_detach")
        require(any(p["processId"] == held["portalProcessId"] for p in h.tool("tia_discover")["processes"]), "tia_detach closed the user-owned Portal")
        return {"discovered": len(discovered)}
    h.stage("discover, attach, read and detach the held V19 Portal", attached)
    return {"view": view, "contents": contents, "objects": objects, "fixture": fixture}


def upgrades(h, fixture, project, archive, v19, watch):
    connect(h, 21)
    results = {}
    try:
        results["lockedSource"] = h.stage("project_upgrade while V19 holds the fixture open", lambda: locked(h, project))
    finally:
        h.report["holdRelease"] = h.stage("release the held fixture", fixture.unhold)
    h.stage("the held open changed no project file outside TIA's search index", lambda: watch.check("heldOpen"))
    for label, source in (("projectFolder", project), ("archive", archive)):
        results[label] = upgrade(h, label, source, v19)
    require(results["projectFolder"]["inventory"] == results["archive"]["inventory"], "Folder and archive upgrades differ")
    h.report["upgrades"] = results


def locked(h, project):
    """project_upgrade while V19 holds the fixture open is refused with SourceLocked and leaves the source unchanged."""
    watcher = owned(h, "locked-source upgrade")
    before = fingerprint(project.parent)
    error = h.call_error("project_upgrade", {"path": str(project), "expectedSha256": sha256(project)})
    require(error.get("code") == "SourceLocked", "Expected SourceLocked while V19 holds the fixture: %s" % error)
    outcome = {"upgraded": False, "code": error["code"]}
    require(fingerprint(project.parent) == before, "The locked source changed")
    release(h, watcher, "locked-source upgrade")
    return outcome


def upgrade(h, label, source, v19):
    watcher = owned(h, "upgrade " + label)
    result = h.stage("project_upgrade the V19 " + label, lambda: h.tool("project_upgrade", {"path": str(source), "expectedSha256": sha256(source)}))
    require(result.get("upgraded") is True and result.get("sourceUnchanged") is True and (result.get("source") or {}).get("version") == "V19",
            "Upgrade of %s failed: %s" % (label, result.get("failure")))
    logs = result.get("upgradeLogs") or []
    require(logs and all(log.get("succeeded") for log in logs), "TIA's conversion log does not record success: %s" % logs)
    upgraded = Path(result["project"]["path"])
    view = inventory_view(h.tool("project_inventory", {"maxItems": 2000}))
    release(h, watcher, "upgrade " + label)
    watcher = owned(h, "reopen " + label)
    h.stage("reopen the upgraded " + label, lambda: h.reopen_project(str(upgraded)))
    def reopened_inventory():
        reopened = inventory_view(h.tool("project_inventory", {"maxItems": 2000}))
        require(reopened == view, "The reopened inventory differs from the upgraded one")
        expected = {k: v for k, v in v19["view"].items() if k != "consistency"}
        require({k: v for k, v in reopened.items() if k != "consistency"} == expected, "The V21 inventory differs from the V19 one below the project")
        return {"inconsistentBlocks": sorted(p for p, ok in reopened["consistency"].items() if not ok)}
    before_compile = h.stage("the reopened %s has the V19 inventory below the project" % label, reopened_inventory)
    # The upgrade does not compile, and V21 refuses to export inconsistent blocks: compile the owned upgraded copy first.
    def compiled():
        plc = h.tool("project_inventory", {"maxItems": 2000})["plcs"][0]["path"]
        prepared = h.tool("change_prepare", {"kind": "plc_compile", "plcPath": plc, "payload": {}})
        applied = h.apply_prepared("plc_compile", prepared)
        require_compile_only(prepared, applied, plc)
        after = inventory_view(h.tool("project_inventory", {"maxItems": 2000}))
        require(all(after["consistency"].values()), "Blocks stay inconsistent after the V21 compile: %s" % [p for p, ok in after["consistency"].items() if not ok])
        return {"changeId": applied["changeId"], "compile": applied.get("compile")}
    compile_result = h.stage("compile the upgraded %s in V21" % label, compiled)
    def compared():
        _, _, objects = seeded_objects(h, None)
        contents = read_all(h, objects, 21)
        require(set(contents) == set(v19["contents"]), "Upgraded objects differ: %s" % sorted(set(contents) ^ set(v19["contents"])))
        differing = sorted(path for path in contents if contents[path]["content"] != v19["contents"][path]["content"])
        require(not differing, "Content differs after the upgrade: %s" % {p: [v19["contents"][p]["content"], contents[p]["content"]] for p in differing})
        return {"objects": len(contents)}
    comparison = h.stage("the upgraded %s equals the V19 fixture" % label, compared)
    release(h, watcher, "reopen " + label)
    return {"project": result["project"]["name"], "logs": [{k: log.get(k) for k in ("succeeded", "errors", "warnings")} for log in logs],
            "inventory": {k: v for k, v in view.items() if k != "consistency"}, **before_compile, "compile": compile_result, **comparison}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--v19-worker", type=Path, required=True)
    parser.add_argument("--v21-worker", type=Path, required=True)
    parser.add_argument("--seeder", type=Path, required=True, help="V19ProjectSeeder.exe; its runs must lie under the repository's artifacts")
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    args.worker, args.host_args = args.v19_worker, ["--tia-version", "19"]
    Harness(args, "v19_", "X12 read-only TIA V19 adapter and X01 V19-to-V21 project_upgrade on an owned V19 fixture", LIMITATIONS,
            [args.seeder, args.v21_worker], scratch=False).run(body)
