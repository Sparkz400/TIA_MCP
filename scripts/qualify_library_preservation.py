"""Qualify independent preservation, persistence and recovery for every library change kind in one owned project.

Phase A applies each kind in an owned scratch CPU 1511-1 PN with a second PLC and an owned global library. Around every
change it takes a complete independent snapshot: every block, PLC data type and tag-table XML of both PLCs, every
project and global library type, version, folder and master copy, and the native document export of every committed
project-library version. The snapshot must differ from the previous one in exactly the intended keys. On its first use
each kind is refused once for a stale or unknown identity and once for a wrong previewHash, without any change. A fresh
Portal then reopens the saved project and global library, which must show the identical state. Phase B applies one
more change per kind from that state and restores its checkpoint into new locations; the recovered state must equal
the pre-change state. Phase C persists a failed change's partial global-library state through library_save and
restores the failed change's checkpoint.
"""
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
from pathlib import Path
import re
import shutil

from engineering_preservation import native_payload_hash
from library_native_harness import Harness, identity, require
from qualification_owned_scratch import complete_inventory, snapshot
from qualify_plc_object_mutations import diff, view

ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / "fixtures/libraries/dependencies"
INNER, OUTER, CHILD, PARENT, EXTRA = "TiaMcpDepInner", "TiaMcpDepOuter", "TiaMcpDepChild", "TiaMcpDepParent", "TiaMcpLibExtra"
KINDS = ("library_create", "library_upgrade", "library_copy_elements", "library_instantiate", "library_update_project",
         "library_create_type", "library_create_master_copy", "library_edit_version", "library_release_version",
         "library_discard_version", "library_set_default", "library_type_policy", "library_organize", "library_delete",
         "library_publish", "library_save", "library_archive", "library_cleanup", "library_harmonize")
# The remaining library change kind; scripts/test_qualify_library_preservation.py keeps KINDS plus these equal to the catalog.
EXCLUDED = {"library_integrate": "Qualified by its own combined-apply run (local.library_integrate.combined_apply.20261028) with the "
            "same independent export/inventory diff, fresh reopen and checkpoint restore; without the opt-in Host flag it is plan-only."}
NEEDS = ("applied", "refused", "previewRefused", "persisted", "restored")
# library_create and library_upgrade write only new outputs: recovery is a checkpoint-free refusal with originals untouched.
OUTPUT_ONLY = ("library_create", "library_upgrade")
MASTER = "/masterCopies/"
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 project with a second CPU at PLC-root scope, the dependency fixture "
    "(nested UDT member and array, FB with a multi-instance FB) and one owned user global library; software units, "
    "namespaces, protected and HMI types are qualified by their own runs.",
    "Implementation-only FB releases; interface changes and runtime behavior are qualified by local.library_releases.20261026.",
    "harmonizePaths is exercised only where library folder and PLC group already agree: V21 has no route that moves a "
    "library type into a folder or a PLC block into a group.",
    "library_cleanup runs PreserveDefaultVersionOfUnusedTypes on selected types; DeleteUnusedTypes and folder selections are not exercised.",
    "library_upgrade uses DMC Open Library V19 PLC (MIT); V20 and older inputs are covered by their own runs.",
    "Not covered here: " + ", ".join(sorted(EXCLUDED)) + ".",
]

PARENT_SUM = "#Sum := #Child.Sum;"
SPARE = "TiaMcpLibSpare"
CYCLE = "TiaMcpLibCycle"  # PLC 2's own SCL program-cycle OB: a new CPU's Main is LAD and SCL may not replace it.


def instance_db(name, fb):
    return 'DATA_BLOCK "%s"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\n"%s"\nBEGIN\nEND_DATA_BLOCK\n\n' % (name, fb)


def consumer_two(child=False, spare=False):
    """PLC 2 references for the update: Parent's instance DB, optionally Child and Outer directly, optionally the spare type."""
    temp = ("      Data : \"%s\";\n" % OUTER if child else "") + ("      Spare : \"%s\";\n" % SPARE if spare else "")
    calls = '    "%s_DB"(Data := #Data);\n    "%s_DB"(Data := #Data);\n' % (PARENT, CHILD) if child else '    "%s_DB"();\n' % PARENT
    calls += "    #Spare.Flag := TRUE;\n" if spare else ""
    return (instance_db(PARENT + "_DB", PARENT) + (instance_db(CHILD + "_DB", CHILD) if child else "")
            + 'ORGANIZATION_BLOCK "%s"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\n%sBEGIN\n%sEND_ORGANIZATION_BLOCK\n'
            % (CYCLE, "   VAR_TEMP\n%s   END_VAR\n" % temp if temp else "", calls))


def udt(name, members="Value : Int;"):
    return 'TYPE "%s"\nVERSION : 0.1\nSTRUCT\n    %s\nEND_STRUCT;\nEND_TYPE\n' % (name, members)


def libint_lines(text):
    """A .libint without the byte hashes of its documents: they cover each export's <Created> timestamp, and the
    documents themselves are compared by native payload. LibraryMetaFileHash and the version GUID stay."""
    return [line for line in text.splitlines() if not re.match(r"\s+Hash: ", line)]


def parent_source(increment):
    text = (FIXTURES / (PARENT + ".scl")).read_text(encoding="utf-8")
    require(text.count(PARENT_SUM) == 1, "Parent fixture statement changed")
    return text.replace(PARENT_SUM, "#Sum := #Child.Sum + %d;" % increment)


def unmatched(patterns, keys):
    """Exact pattern sets: every key matches some pattern and every pattern matches some key."""
    return {"keys": sorted(k for k in keys if not any(fnmatch.fnmatchcase(k, p) for p in patterns)),
            "patterns": sorted(p for p in patterns if not any(fnmatch.fnmatchcase(k, p) for k in keys))}


def compare(observed, expected):
    """Mismatches between an observed diff and expected added/removed/changed patterns; empty when exact. Changed keys
    matching an optional "may" pattern are allowed without being required (the targeted type's own derived rows)."""
    result = {}
    for part in ("added", "removed", "changed"):
        keys = observed[part]
        if part == "changed":
            required = expected.get(part, ())
            keys = [k for k in keys if any(fnmatch.fnmatchcase(k, p) for p in required)
                    or not any(fnmatch.fnmatchcase(k, p) for p in expected.get("may", ()))]
        miss = unmatched(expected.get(part, ()), keys)
        if miss["keys"] or miss["patterns"]:
            result[part] = miss
    return result


def own(name, prefix="P|"):
    """A type's own rows. Its native status follows instance/default relations (observed: Consistent, then
    NonDefaultVersionInstantiation while a PLC uses a non-default version), and releasing a higher version makes it
    the default, so these rows may change with any change of that type."""
    return [prefix + "type:" + name, prefix + "version:%s@*" % name]


def library_row(prefix, row):
    """A logical library key that survives archive, retrieval and working copies, and the row it compares."""
    kind = row.get("kind")
    if row.get("unavailable"):  # Elements of absent products (DMC's SiVArc master copies) stay explicit rows.
        return prefix + kind + ":" + (row.get("path") or row.get("masterCopyPath")), {"unavailable": True, "name": row.get("name")}
    if kind == "type":
        return prefix + "type:" + row["name"], {k: row.get(k) for k in ("path", "typeGuid", "engineeringType", "doNotUse", "setForUpdate", "status")}
    if kind == "version":
        return prefix + "version:%s@%s" % (row["typeName"], row["version"]), {k: v for k, v in row.items() if k != "kind"}
    if kind in ("typeFolder", "masterCopyFolder"):
        return prefix + kind + ":" + row["path"], True
    if kind == "masterCopy":
        return prefix + "masterCopy:" + row["masterCopyPath"], {k: row.get(k) for k in ("name", "author", "creationDate", "contents")}
    raise AssertionError("Unknown library row kind %s" % kind)


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class Run:
    """Current native state, the handles it lives in, and per-kind criterion coverage."""

    def __init__(self, h, documents):
        self.h, self.documents, self.count = h, documents, 0
        self.plcs, self.pl, self.g, self.g_path, self.current = [], None, None, None, None
        self.coverage, self.mismatches = {}, []
        h.report.update(coverage=self.coverage, mismatches=self.mismatches, excluded=EXCLUDED)

    def mark(self, kind, need, label):
        self.coverage.setdefault(kind, {}).setdefault(need, []).append(label)

    def mismatch(self, label, detail):
        self.mismatches.append({"step": label, "detail": detail})
        self.h.persist()
        print("MISMATCH %s: %s" % (label, json.dumps(detail)[:2000]), flush=True)

    # Independent observation: software_inspect/block_read/type_read/tag_table_read and library_query only.
    def plc_call(self, plc):
        return lambda name, args=None: self.h.tool(name, {**(args or {}), "plcPath": plc} if name == "software_inspect" else args)

    def library_view(self, prefix, library, export):
        result = self.h.query(library)
        items = list(result["items"])
        while result.get("nextOffset") is not None:  # Later pages are pinned to the first page's revision.
            result = self.h.query(library, offset=result["nextOffset"], revision=result["revision"])
            items += result["items"]
        require(result["completeness"]["scopeComplete"], "Library metadata incomplete")
        rows, formats = {}, {}
        for row in items:
            key, value = library_row(prefix, row)
            require(key not in rows, "Duplicate library identity " + key)
            rows[key] = value
            if row.get("kind") == "type":
                formats[row["typeGuid"]] = row.get("supportedExportFormats") or []
        for key, value in rows.items() if export else ():
            if "|version:" in key and value["state"] == "Committed":
                value["exportSha256"] = self.export_hash(library, value, formats[value["typeGuid"]])
        return rows

    def export_hash(self, library, version, formats):
        chosen = next((x for x in formats if x.lower() in ("simaticml", "xml", ".xml")), formats[0] if formats else None)
        require(chosen, "No native export format for " + version["typeName"])
        result = self.h.query(library, query="export", format=chosen, **identity(version))
        require(result["files"] and not result["filesTruncated"], "Native version export is empty or truncated")
        parts = []
        for artifact in result["files"]:
            data = Path(artifact["path"]).read_bytes()
            require(hashlib.sha256(data).hexdigest() == artifact["sha256"], "Native export hash differs")
            name = Path(artifact["path"]).name
            if name.lower().endswith(".xml"):
                digest = native_payload_hash(data.decode("utf-8-sig"))
            elif name.lower().endswith(".libint"):
                digest = hashlib.sha256("\n".join(libint_lines(data.decode("utf-8-sig"))).encode()).hexdigest()
            else:
                digest = hashlib.sha256(data).hexdigest()
            parts.append(name + ":" + digest)
        return hashlib.sha256("\n".join(sorted(parts)).encode()).hexdigest()

    def snap(self, label):
        self.count += 1
        base = self.h.directory / "snapshots" / ("%03d-%s" % (self.count, re.sub(r"[^A-Za-z0-9]+", "-", label)[:40]))
        state = {}

        def take():
            for index, plc in enumerate(self.plcs):
                taken = snapshot(self.plc_call(plc), base / ("plc%d" % index))
                state.update(view(taken["inventory"], taken["objects"]))
            state.update(self.library_view("P|", self.pl, True))
            if self.g:
                state.update(self.library_view("G|", self.g, False))
            (base / "view.json").write_text(json.dumps(state, indent=1, sort_keys=True, default=str), encoding="utf-8")
            return {"keys": len(state), "view": str(base / "view.json")}
        self.h.stage("snapshot " + label, take)
        return state

    def state(self):
        """Cheap refusal check: PLC inventories and library rows without XML or document exports."""
        result = {}
        for plc in self.plcs:
            result.update(view(complete_inventory(self.plc_call(plc))))
        result.update(self.library_view("P|", self.pl, False))
        if self.g:
            result.update(self.library_view("G|", self.g, False))
        return result

    def expect(self, label, before, expected):
        observed = diff(before, self.current)

        def check():
            problems = compare(observed, expected)
            if problems:
                self.mismatch(label, {"observed": observed, "problems": problems})
            return {"observed": observed, "exact": not problems}
        return self.h.stage("verify " + label, check)

    def refuse(self, label, kind, payload, code, top=None):
        def check():
            before = self.state()
            error = self.h.call_error("change_prepare", {"kind": kind, "payload": payload, **(top or {})})
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            require(self.state() == before, "Refused preparation changed native state")
            return error
        self.h.stage("refuse %s: %s" % (kind, label), check)

    def refuse_identity(self, kind, payload, top):
        if "source" in payload:
            bad, code = {**payload, "source": {**payload["source"], "revision": "stale-" + payload["source"]["revision"][:32]}}, "PreviewStale"
        elif "destinationLibraryId" in payload:
            bad, code = {**payload, "destinationLibraryId": "lib-not-registered"}, "LibraryIdentityMismatch"
        elif kind == "library_upgrade":
            bad, code = {**payload, "expectedSha256": "0" * 64}, "PackageChanged"
        else:
            bad, code = {**payload, "name": "bad|name"}, "InvalidRequest"
        self.refuse("stale or unknown identity", kind, bad, code, top)
        self.mark(kind, "refused", code)

    def refuse_preview(self, kind, prepared):
        def check():
            before = self.state()
            error = self.h.call_error("change_apply", {**self.h.apply_args(prepared), "previewHash": "0" * 64})
            require(error.get("code") == "PreviewMismatch", "Expected PreviewMismatch, observed %s" % error.get("code"))
            status = self.h.tool("change_status", {"changeId": prepared["changeId"]})
            require(status["state"] == "prepared" and self.state() == before, "A refused apply changed the change or native state")
            return error
        self.h.stage("refuse apply %s: wrong previewHash" % kind, check)
        self.mark(kind, "previewRefused", "PreviewMismatch")

    def prepare(self, label, kind, payload, top=None):
        if "refused" not in self.coverage.get(kind, {}):
            self.refuse_identity(kind, payload, top)
        prepared = self.h.stage("prepare " + label, lambda: self.h.tool("change_prepare", {"kind": kind, "payload": payload, **(top or {})}))
        shown = prepared["preview"]
        require(prepared["state"] == "prepared" and shown["kind"] == kind and shown["payload"] == json.loads(json.dumps(payload))
                and shown["selection"] == payload.get("selection", []), "Preview of %s does not restate the exact request" % label)
        if "previewRefused" not in self.coverage.get(kind, {}):
            self.refuse_preview(kind, prepared)
        return prepared

    def change(self, label, kind, payload, expected=None, then=None, restore=False, top=None):
        """Prepare, apply with checkpoint/compile/save, and require an exact independent snapshot diff."""
        prepared = self.prepare(label, kind, payload, top)
        before = self.current
        applied = self.h.apply_prepared(kind, prepared)
        if then:
            then(applied)
        self.current = self.snap(label)
        self.expect(label, before, expected or {})
        self.mark(kind, "applied", label)
        if restore:
            self.restore(label, kind, prepared, applied, before)
        return applied

    def helper(self, label, kind, payload, plc):
        """A non-library prerequisite (engineering change kind); only the following snapshot is recorded."""
        prepared = self.h.stage("prepare " + label, lambda: self.h.tool("change_prepare", {"kind": kind, "payload": payload, "plcPath": plc}))
        self.h.apply_prepared(kind, prepared)
        self.current = self.snap(label)

    def reopen_global(self, label):
        path = self.g_path
        opened = self.h.stage("reopen global library " + label, lambda: self.h.tool("library_open", {"request": {
            "path": path, "expectedSha256": sha256(path), "writableWorkingCopy": True}}))
        self.g, self.g_path = opened["libraryId"], opened["path"]

    def restore(self, label, kind, prepared, applied, before):
        h = self.h
        checkpoints = sorted({c["kind"] for c in applied["journal"]["checkpoints"]})
        if kind in OUTPUT_ONLY:
            def refused():
                error = h.call_error("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
                require(not checkpoints and error.get("code") == "CheckpointInvalid", "Output-only change restore differs: %s" % error)
                return error
            h.stage("restore %s: no checkpoint, new output retained" % label, refused)
        else:
            require(checkpoints, "Applied change %s has no checkpoint" % label)
            if "project" in checkpoints:  # Project retrieval needs an empty owned Portal.
                h.stage("detach before restoring " + label, lambda: h.tool("tia_detach"))
                h.owned_portal("restore " + label)
            elif self.g:  # The retrieved copy carries the same library name; close the saved original first.
                h.tool("library_close", {"libraryId": self.g})
            self.g = None
            restored = h.stage("restore " + label, lambda: h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
            rows = restored["resources"]
            require(restored["state"] == "restored_to_new_locations" and sorted(r["kind"] for r in rows) == checkpoints
                    and all(r["state"] == "completed" for r in rows), "Checkpoint recovery of %s differs: %s" % (label, rows))
            for row in rows:
                if row["kind"] == "project":
                    self.pl = h.project_library()
                else:
                    self.g, self.g_path = row["library"]["libraryId"], row["library"]["path"]
            if self.g is None:
                self.reopen_global("after restoring " + label)
        recovered = self.snap("restored " + label)
        h.stage("restored %s equals the pre-change state" % label, lambda: (recovered == before or self.mismatch(
            "restore " + label, diff(before, recovered))) and len(before))
        self.current = recovered
        self.mark(kind, "restored", label)


def create_type(run, name, composition, plc, restore=False):
    h = run.h
    key = ("type:%s/types/%s" if composition == "types" else "block:%s/blocks/%s") % (plc, name)
    run.change("create type " + name, "library_create_type", {"schemaVersion": 1, "destinationLibraryId": run.pl, "documentDirectory": str(run.documents),
               "documentName": name, "targetEnvironment": "selectedPlc", "destinationComposition": composition, "target": {"plcPath": plc}},
               {"added": [key, "P|type:" + name, "P|version:%s@*" % name]}, restore=restore)
    return None if restore else in_work(run, name)


def in_work(run, name):
    rows = [x for x in run.h.versions(run.pl, name) if x["state"] == "InWork"]
    require(len(rows) == 1, "Expected one InWork version of " + name)
    return rows[0]


def version(run, library, name, number):
    rows = [x for x in run.h.versions(library, name) if x["version"] == number]
    require(len(rows) == 1, "Expected %s %s" % (name, number))
    return rows[0]


def release(run, work, number, changed=(), restore=False):
    """Observed on V21: a released block instance exports without its HeaderAuthor/Family/Name/Version, while a PLC
    data type instance exports unchanged; callers name those PLC keys. Releasing a higher version made it the default."""
    name = work["typeName"]
    run.change("release %s %s" % (name, number), "library_release_version", run.h.selected(run.pl, [work], versionNumber=number,
               author="TIA MCP qualification", comment="Preservation release " + number),
               {"removed": ["P|version:%s@%s" % (name, work["version"])], "added": ["P|version:%s@%s" % (name, number)],
                "changed": list(changed), "may": own(name)}, restore=restore)
    if restore:
        return None
    released = version(run, run.pl, name, number)
    run.h.stage("released %s %s is committed" % (name, number), lambda: require(
        released["state"] == "Committed", "Release readback differs: %s" % released) or {"isDefault": released["isDefault"]})
    return released


def edit_documents(run, previous, plc, source, composition, extension, changed):
    """The next InWork version from authored documents (the harness tries the accepted meta-file layouts)."""
    before, name = run.current, previous["typeName"]
    created = run.h.edit_source(run.pl, plc, previous, name, source, composition, extension)
    run.current = run.snap("document edit " + name)
    run.expect("document edit " + name, before, {"added": ["P|version:%s@*" % name], "changed": list(changed), "may": own(name)})
    run.mark("library_edit_version", "applied", "documents " + previous["typeName"])
    return created


def set_default(run, target, current, restore=False):
    """Make an older committed version the default again: exactly the two default rows change."""
    name = target["typeName"]
    run.change("default %s %s" % (name, target["version"]), "library_set_default", run.h.selected(run.pl, [target]),
               {"changed": ["P|version:%s@%s" % (name, target["version"]), "P|version:%s@%s" % (name, current)], "may": own(name)}, restore=restore)
    if not restore:
        run.h.stage("%s %s is the default" % (name, target["version"]), lambda: require(
            version(run, run.pl, name, target["version"])["isDefault"] is True, "Default readback differs") or target["version"])


def phase_a(run, upgrade_input):
    h = run.h
    p1 = h.owned_scratch("authoring")
    run.plcs, run.pl = [p1], h.project_library()
    blk = lambda plc, name: "block:%s/blocks/%s" % (plc, name)
    typ = lambda plc, name: "type:%s/types/%s" % (plc, name)
    blocks = {b["name"]: b["path"] for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"] if b["plcPath"] == p1}
    run.current = run.snap("baseline")

    released = {}
    for name, composition in ((INNER, "types"), (OUTER, "types"), (CHILD, "blocks"), (PARENT, "blocks")):
        released[name] = release(run, create_type(run, name, composition, p1), "1.0.0", [blk(p1, name)] if composition == "blocks" else [])
        if name == INNER:
            again = run.snap("unchanged repeat")
            h.stage("two snapshots without a change are equal", lambda: (again == run.current or run.mismatch("repeat snapshot", diff(run.current, again))) and len(again))
    run.helper("consumer references in PLC 1", "source_upsert", {"scopePath": p1, "language": "SCL", "expectedBlockNames": [PARENT + "_DB", PARENT + "_DB2", "Main"],
               "source": (FIXTURES / "ConsumerReferences.scl").read_text(encoding="utf-8")}, p1)

    def created(applied):
        result = applied["journal"]["result"]["library"]
        run.g, run.g_path = result["libraryId"], result["path"]
    run.change("create global library", "library_create", {"schemaVersion": 1, "name": "PreservationQualification"},
               {"added": ["G|typeFolder:/types", "G|masterCopyFolder:/masterCopies"]}, then=created)
    closure = ["G|type:" + n for n in released] + ["G|version:%s@1.0.0" % n for n in released]
    run.change("publish Parent 1.0.0", "library_publish", h.selected(run.pl, [released[PARENT]], destinationLibraryId=run.g), {"added": closure})

    cpu = h.scratch["selectedCpu"]["typeIdentifier"]
    prepared = h.prepare("hardware_add", {"typeIdentifier": cpu, "deviceName": "ScratchStation2", "itemName": "ScratchPLC2", "configureScratchSecurity": True})
    h.apply_prepared("hardware_add", prepared)
    p2 = next(p["path"] for p in h.tool("project_inventory", {"maxItems": 1000})["plcs"] if p["path"] != p1)
    run.plcs = [p1, p2]
    run.current = run.snap("second PLC")
    run.change("instantiate Parent 1.0.0 into PLC 2", "library_instantiate", h.selected(run.g, [released[PARENT]], target={"plcPath": p2}),
               {"added": [blk(p2, PARENT), blk(p2, CHILD), typ(p2, OUTER), typ(p2, INNER)]})
    # An unrelated type, unreferenced in PLC 2 until the last reference step before the update.
    (run.documents / (SPARE + ".udt")).write_text(udt(SPARE, "Flag : Bool;"), encoding="utf-8")
    spare = release(run, create_type(run, SPARE, "types", p1), "1.0.0")
    run.change("publish unrelated spare type", "library_publish", h.selected(run.pl, [spare], destinationLibraryId=run.g),
               {"added": ["G|type:" + SPARE, "G|version:%s@1.0.0" % SPARE]})
    run.change("instantiate spare into PLC 2", "library_instantiate", h.selected(run.g, [spare], target={"plcPath": p2}), {"added": [typ(p2, SPARE)]})

    # Edit through the PLC 1 test instance, then discard; refuse discarding a committed version.
    run.change("edit Parent test instance", "library_edit_version", h.selected(run.pl, [released[PARENT]], testInstanceName=PARENT, target={"plcPath": p1}),
               {"added": ["P|version:%s@*" % PARENT], "changed": [blk(p1, PARENT)], "may": own(PARENT)})
    run.refuse("a committed version", "library_discard_version", h.selected(run.pl, [released[PARENT]]), "InvalidChangeState")
    edited = in_work(run, PARENT)
    run.change("discard Parent edit", "library_discard_version", h.selected(run.pl, [edited]),
               {"removed": ["P|version:%s@%s" % (PARENT, edited["version"])], "changed": [blk(p1, PARENT)], "may": own(PARENT)})

    # Implementation-only releases 1.1.0 and 1.2.0; the PLC 1 test instance follows each new version, and each release
    # becomes the default, so the explicit default change goes back to 1.0.0.
    parents = {}
    for increment, number in ((1, "1.1.0"), (2, "1.2.0")):
        work = edit_documents(run, parents.get("1.1.0", released[PARENT]), p1, parent_source(increment), "blocks", ".scl", [blk(p1, PARENT)])
        parents[number] = release(run, work, number, [blk(p1, PARENT)])
        if number == "1.1.0":
            set_default(run, released[PARENT], "1.1.0")
        elif not version(run, run.pl, PARENT, number)["isDefault"]:  # Publication needs the default.
            set_default(run, parents[number], "1.0.0")
    run.refuse("the default version", "library_delete", h.selected(run.pl, [parents["1.2.0"]]), "DependencyConflict")
    run.change("delete unused Parent 1.1.0", "library_delete", h.selected(run.pl, [parents["1.1.0"]]),
               {"removed": ["P|version:%s@1.1.0" % PARENT], "may": own(PARENT)})
    run.change("publish Parent 1.2.0", "library_publish", h.selected(run.pl, [parents["1.2.0"]], destinationLibraryId=run.g),
               {"added": ["G|version:%s@1.2.0" % PARENT], "changed": ["G|version:%s@1.0.0" % PARENT], "may": own(PARENT, "G|")})
    # A native project update can delete library objects that no OB reaches, so preparation must refuse until every
    # library-linked object in PLC 2 has an OB-rooted reference that does not pass through the updated Parent.
    update = lambda: h.selected(run.g, [version(run, run.g, PARENT, "1.2.0")], updateScope="entireSelectedPlc", target={"plcPath": p2})
    run.refuse("no OB reaches any library object in PLC 2", "library_update_project", update(), "LibraryUpdateCleanupRisk")
    for label, child, spare in (("Parent only", False, False), ("Parent, Child and Outer", True, False), ("every library object", True, True)):
        run.helper("PLC 2 references " + label, "source_upsert", {"scopePath": p2, "language": "SCL", "source": consumer_two(child, spare),
                   "expectedBlockNames": [PARENT + "_DB"] + ([CHILD + "_DB"] if child else []) + [CYCLE]}, p2)
        if not spare:
            run.refuse(("Child, Outer and Inner reachable only through the updated Parent" if not child else "the unrelated spare type unreferenced"),
                       "library_update_project", update(), "LibraryUpdateCleanupRisk")
    run.change("update PLC 2 to Parent 1.2.0", "library_update_project", update(), {"changed": [blk(p2, PARENT)], "may": own(PARENT)})
    run.refuse("types with only used versions", "library_cleanup", {"schemaVersion": 1, "source": h.source_ref(run.pl), "types": [released[INNER]["typeGuid"]],
               "cleanUpMode": "PreserveDefaultVersionOfUnusedTypes"}, "NoEffect")
    run.change("clean up unused Parent versions", "library_cleanup", {"schemaVersion": 1, "source": h.source_ref(run.pl), "types": [released[PARENT]["typeGuid"]],
               "cleanUpMode": "PreserveDefaultVersionOfUnusedTypes"}, {"removed": ["P|version:%s@1.0.0" % PARENT], "may": own(PARENT)})

    # Type policy, including malformed and read-only flags refused before mutation.
    inner_rows = ["P|type:" + INNER, "P|version:%s@1.0.0" % INNER]
    for label, flags, code in (("no policy flag", {}, "InvalidRequest"), ("non-boolean doNotUse", {"doNotUse": "true"}, "InvalidRequest"),
                               ("mixed malformed setForUpdate", {"doNotUse": True, "setForUpdate": 1}, "InvalidRequest"),
                               ("read-only setForUpdate on a PLC data type", {"doNotUse": False, "setForUpdate": True}, "AttributeNotWritable")):
        run.refuse(label, "library_type_policy", h.selected(run.pl, [released[INNER]], **flags), code)
    for flag in (True, False):
        run.change("Inner doNotUse %s" % flag, "library_type_policy", h.selected(run.pl, [released[INNER]], doNotUse=flag), {"changed": inner_rows})

    # Folders.
    run.change("create folder", "library_organize", h.selected(run.pl, [], folderKind="types", action="create", name="Preserve"),
               {"added": ["P|typeFolder:/types/Preserve"]})
    run.refuse("a duplicate folder", "library_organize", h.selected(run.pl, [], folderKind="types", action="create", name="Preserve"), "LibraryConflict")
    run.refuse("renaming the root folder", "library_organize", h.selected(run.pl, [], folderKind="types", action="rename", folderPath="/types", name="Root"), "TargetIncompatible")
    run.change("rename folder", "library_organize", h.selected(run.pl, [], folderKind="types", action="rename", folderPath="/types/Preserve", name="Preserved"),
               {"added": ["P|typeFolder:/types/Preserved"], "removed": ["P|typeFolder:/types/Preserve"]})
    run.change("delete folder", "library_organize", h.selected(run.pl, [], folderKind="types", action="delete", folderPath="/types/Preserved"),
               {"removed": ["P|typeFolder:/types/Preserved"]})

    # Master copies into the global library, then one copied back into the project library.
    for name in ("TiaMcp_IsEnabled", "TiaMcp_EnableGate"):
        payload = {"schemaVersion": 1, "destinationLibraryId": run.g, "target": {"plcPath": p1}, "object": {"kind": "block", "objectPath": blocks[name]}}
        run.change("master copy " + name, "library_create_master_copy", payload, {"added": ["G|masterCopy:" + MASTER + name]})
    run.refuse("a duplicate master copy", "library_create_master_copy", payload, "LibraryConflict")
    run.change("copy master TiaMcp_IsEnabled into the project library", "library_copy_elements",
               {"schemaVersion": 1, "source": h.source_ref(run.g), "selection": [{"masterCopyPath": MASTER + "TiaMcp_IsEnabled"}], "destinationLibraryId": run.pl},
               {"added": ["P|masterCopy:" + MASTER + "TiaMcp_IsEnabled"]})

    # Renamed instances in PLC 2; harmonize restores Parent's library name (Child stays renamed for phase B). Exports that
    # name a renamed FB change with it: an instance DB's InstanceOfName, the calling OB's CallInfo and Parent's Child member.
    for name in (PARENT, CHILD):
        run.helper("rename %s in PLC 2" % name, "block_rename", {"blockName": name, "newName": name + "Local"}, p2)
    harmonize = {"schemaVersion": 1, "source": h.source_ref(run.pl), "target": {"plcPath": p2}, "types": [released[PARENT]["typeGuid"]]}
    run.refuse("no harmonize flag", "library_harmonize", harmonize, "InvalidRequest")
    run.change("harmonize Parent in PLC 2", "library_harmonize", {**harmonize, "source": h.source_ref(run.pl), "harmonizeNames": True, "harmonizePaths": True},
               {"added": [blk(p2, PARENT)], "removed": [blk(p2, PARENT + "Local")], "changed": [blk(p2, PARENT + "_DB"), blk(p2, CYCLE)]})

    # Parent 1.3.0 is the global default while PLC 2 stays at 1.2.0, for the restored update in phase B.
    parents["1.3.0"] = release(run, edit_documents(run, parents["1.2.0"], p1, parent_source(3), "blocks", ".scl", [blk(p1, PARENT)]), "1.3.0", [blk(p1, PARENT)])
    run.change("publish Parent 1.3.0", "library_publish", h.selected(run.pl, [parents["1.3.0"]], destinationLibraryId=run.g),
               {"added": ["G|version:%s@1.3.0" % PARENT], "changed": ["G|version:%s@1.2.0" % PARENT], "may": own(PARENT, "G|")})

    archive = {}
    def archived(applied):
        archive.update(applied["journal"]["result"]["archive"])
        require(sha256(archive["path"]) == archive["sha256"], "Native archive hash differs")
    run.change("archive global library", "library_archive", {"schemaVersion": 1, "source": h.source_ref(run.g)}, then=archived)
    archive["rows"] = run.library_view("G|", run.g, False)
    run.change("save clean global library", "library_save", {"schemaVersion": 1, "source": h.source_ref(run.g)})

    upgraded = {"input": sha256(upgrade_input)}
    def upgrade_result(applied):
        library = applied["journal"]["result"]["library"]
        upgraded.update(libraryId=library["libraryId"], path=library["path"])
        rows = run.library_view("U|", library["libraryId"], False)
        versions = [v for k, v in rows.items() if "|version:" in k]
        require(versions and all(v["state"] == "Committed" for v in versions), "Upgraded library is empty or has uncommitted versions")
        upgraded["rows"] = rows
    run.change("upgrade DMC V19 PLC library", "library_upgrade", {"schemaVersion": 1, "path": str(upgrade_input), "expectedSha256": upgraded["input"]},
               then=upgrade_result)
    h.stage("upgrade source bytes unchanged", lambda: require(sha256(upgrade_input) == upgraded["input"], "Upgrade input changed") or upgraded["input"])
    h.tool("library_close", {"libraryId": upgraded["libraryId"]})
    return {"p1": p1, "p2": p2, "blocks": blocks, "released": released, "parents": parents, "archive": archive, "upgraded": upgraded}


def reopen(run, a):
    h = run.h
    final, project = run.current, h.tool("tia_session_status")["projects"][0]["path"]
    h.stage("close authoring session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen the saved project", lambda: h.reopen_project(project))
    run.pl = h.project_library()

    def persisted_outputs():  # Before the global library reopens: its archive holds a library of the same name.
        upgraded = a["upgraded"]
        opened = h.tool("library_open", {"request": {"path": upgraded["path"], "expectedSha256": sha256(upgraded["path"])}})
        rows = run.library_view("U|", opened["libraryId"], False)
        h.tool("library_close", {"libraryId": opened["libraryId"]})
        require(rows == upgraded["rows"], "Reopened upgraded library differs")
        source = h.open_archive(a["archive"])
        rows = run.library_view("G|", source, False)
        h.tool("library_close", {"libraryId": source})
        require(rows == a["archive"]["rows"], "Archived global library differs from its archived state")
        return {"upgradedRows": len(rows), "archiveRows": len(a["archive"]["rows"])}
    h.stage("reopened upgrade output and archive equal their recorded state", persisted_outputs)
    run.reopen_global("after reopen")
    run.current = run.snap("reopened")
    h.stage("reopened state equals every verified change", lambda: (run.current == final or run.mismatch("reopen", diff(final, run.current))) and len(final))
    for kind in KINDS:
        if "applied" in run.coverage.get(kind, {}):
            run.mark(kind, "persisted", "fresh Portal reopen")


def phase_b(run, a):
    """From the reopened state, one more change per kind followed by its checkpoint restore."""
    h, p1, p2 = run.h, a["p1"], a["p2"]
    blk = lambda plc, name: "block:%s/blocks/%s" % (plc, name)
    inner = a["released"][INNER]
    run.change("Inner doNotUse True", "library_type_policy", h.selected(run.pl, [inner], doNotUse=True),
               {"changed": ["P|type:" + INNER, "P|version:%s@1.0.0" % INNER]}, restore=True)
    run.change("create folder Restore", "library_organize", h.selected(run.pl, [], folderKind="types", action="create", name="Restore"),
               {"added": ["P|typeFolder:/types/Restore"]}, restore=True)
    run.change("master copy TiaMcp_EnableGate_DB", "library_create_master_copy", {"schemaVersion": 1, "destinationLibraryId": run.g, "target": {"plcPath": p1},
               "object": {"kind": "block", "objectPath": a["blocks"]["TiaMcp_EnableGate_DB"]}}, {"added": ["G|masterCopy:" + MASTER + "TiaMcp_EnableGate_DB"]}, restore=True)
    run.change("copy master TiaMcp_EnableGate into the project library", "library_copy_elements",
               {"schemaVersion": 1, "source": h.source_ref(run.g), "selection": [{"masterCopyPath": MASTER + "TiaMcp_EnableGate"}], "destinationLibraryId": run.pl},
               {"added": ["P|masterCopy:" + MASTER + "TiaMcp_EnableGate"]}, restore=True)
    run.change("instantiate master TiaMcp_IsEnabled into PLC 2", "library_instantiate",
               {**h.selected(run.g, [], target={"plcPath": p2}), "selection": [{"masterCopyPath": MASTER + "TiaMcp_IsEnabled"}], "destinationComposition": "blocks"},
               {"added": [blk(p2, "TiaMcp_IsEnabled")]}, restore=True)
    run.change("update PLC 2 to Parent 1.3.0", "library_update_project",
               h.selected(run.g, [version(run, run.g, PARENT, "1.3.0")], updateScope="entireSelectedPlc", target={"plcPath": p2}),
               {"changed": [blk(p2, PARENT)], "may": own(PARENT)}, restore=True)
    run.change("harmonize Child in PLC 2", "library_harmonize", {"schemaVersion": 1, "source": h.source_ref(run.pl), "target": {"plcPath": p2},
               "types": [a["released"][CHILD]["typeGuid"]], "harmonizeNames": True, "harmonizePaths": True},
               {"added": [blk(p2, CHILD)], "removed": [blk(p2, CHILD + "Local")], "changed": [blk(p2, PARENT), blk(p2, PARENT + "_DB"), blk(p2, CHILD + "_DB"), blk(p2, CYCLE)]},
               restore=True)

    # A fresh UDT type carries the authoring and version kinds.
    (run.documents / (EXTRA + ".udt")).write_text(udt(EXTRA), encoding="utf-8")
    create_type(run, EXTRA, "types", p1, restore=True)
    work = create_type(run, EXTRA, "types", p1)
    release(run, work, "1.0.0", restore=True)
    first = release(run, work, "1.0.0")
    typ = "type:%s/types/%s" % (p1, EXTRA)
    for restore in (True, False):
        run.change("edit %s test instance" % EXTRA, "library_edit_version", h.selected(run.pl, [first], testInstanceName=EXTRA, target={"plcPath": p1}),
                   {"added": ["P|version:%s@*" % EXTRA], "may": own(EXTRA)}, restore=restore)
    edited = in_work(run, EXTRA)
    for restore in (True, False):
        run.change("discard %s edit" % EXTRA, "library_discard_version", h.selected(run.pl, [edited]),
                   {"removed": ["P|version:%s@%s" % (EXTRA, edited["version"])], "may": own(EXTRA)}, restore=restore)
    second = release(run, edit_documents(run, first, p1, udt(EXTRA, "Value : Int;\n    Extra : Bool;"), "types", ".udt", [typ]), "1.1.0")
    set_default(run, first, "1.1.0", restore=True)
    older = "P|version:%s@1.0.0" % EXTRA
    run.change("delete unused %s 1.0.0" % EXTRA, "library_delete", h.selected(run.pl, [first]), {"removed": [older], "may": own(EXTRA)}, restore=True)
    run.change("clean up unused %s versions" % EXTRA, "library_cleanup", {"schemaVersion": 1, "source": h.source_ref(run.pl), "types": [first["typeGuid"]],
               "cleanUpMode": "PreserveDefaultVersionOfUnusedTypes"}, {"removed": [older], "may": own(EXTRA)}, restore=True)
    run.change("publish %s" % EXTRA, "library_publish", h.selected(run.pl, [second], destinationLibraryId=run.g),
               {"added": ["G|type:" + EXTRA, "G|version:%s@1.0.0" % EXTRA, "G|version:%s@1.1.0" % EXTRA]}, restore=True)
    run.change("archive global library", "library_archive", {"schemaVersion": 1, "source": h.source_ref(run.g)}, restore=True)
    run.change("save clean global library", "library_save", {"schemaVersion": 1, "source": h.source_ref(run.g)}, restore=True)

    def close_created(applied):
        h.tool("library_close", {"libraryId": applied["journal"]["result"]["library"]["libraryId"]})
    run.change("create second global library", "library_create", {"schemaVersion": 1, "name": "PreservationRestore"}, then=close_created, restore=True)
    upgraded = a["upgraded"]
    run.change("upgrade DMC V19 PLC library again", "library_upgrade", {"schemaVersion": 1, "path": upgraded["source"], "expectedSha256": upgraded["input"]},
               then=close_created, restore=True)


def phase_c(run, a):
    """library_save of a global library left dirty by a failed same-session change, then restore of that save."""
    h, p1 = run.h, a["p1"]
    # A third CPU without its confidential-data password makes every later project compile fail.
    prepared = h.prepare("hardware_add", {"typeIdentifier": h.scratch["selectedCpu"]["typeIdentifier"], "deviceName": "ScratchStation3", "itemName": "ScratchPLC3"})
    h.stage("apply hardware_add without compile", lambda: h.tool("change_apply", {**h.apply_args(prepared), "compile": False}))
    clean = run.library_view("G|", run.g, False)
    failing = h.prepare("library_create_master_copy", {"schemaVersion": 1, "destinationLibraryId": run.g, "target": {"plcPath": p1},
                        "object": {"kind": "block", "objectPath": a["blocks"]["TiaMcp_EnableGate_DB"]}})

    def fail():
        result = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(failing)})
        error = (result.get("structuredContent") or {}).get("error") or {}
        require(result.get("isError") is True and error.get("code") == "CompileFailed", "Expected CompileFailed, observed %s" % error)
        status = h.tool("change_status", {"changeId": failing["changeId"]})
        require(status["state"] == "recovery_required", "Failed change state is %s" % status["state"])
        return {"error": error, "state": status["state"]}
    h.stage("master copy whose project compile fails leaves the global library dirty", fail)
    if h.unresolved_admissions():
        h.reconcile_admission("reconcile the failed change's admission")
    run.refuse("an unrelated recoveryChangeId", "library_save", {"schemaVersion": 1, "source": h.source_ref(run.g), "recoveryChangeId": "lib-not-a-failed-change"},
               "RecoveryRequired")
    saved = h.apply("library_save", {"schemaVersion": 1, "source": h.source_ref(run.g), "recoveryChangeId": failing["changeId"]})
    dirty = run.library_view("G|", run.g, False)
    h.stage("saved global library holds the failed change's partial state", lambda: require(
        diff(clean, dirty) == {"added": ["G|masterCopy:" + MASTER + "TiaMcp_EnableGate_DB"], "removed": [], "changed": []},
        "Persisted partial state differs: %s" % diff(clean, dirty)) or diff(clean, dirty))
    h.tool("library_close", {"libraryId": run.g})
    restored = h.stage("restore the save", lambda: h.tool("change_restore", {"changeId": saved["changeId"], "previewHash": saved["previewHash"]}))
    rows = restored["resources"]
    require([r["kind"] for r in rows] == ["library"] and rows[0]["state"] == "completed", "Expected one recovered library")
    recovered = run.library_view("G|", rows[0]["library"]["libraryId"], False)
    h.stage("recovered global library equals its state before the failed change", lambda: require(
        recovered == clean, "Recovered library differs: %s" % diff(clean, recovered)) or len(clean))
    run.mark("library_save", "restored", "failed-change partial state")


def body(h, upgrade_input):
    documents = h.directory / "documents"
    documents.mkdir()
    for path in FIXTURES.glob("*.*"):
        shutil.copy2(path, documents)
    run = Run(h, documents)
    a = phase_a(run, upgrade_input)
    a["upgraded"]["source"] = str(upgrade_input)
    reopen(run, a)
    phase_b(run, a)
    phase_c(run, a)
    gaps = {kind: [n for n in NEEDS if n not in run.coverage.get(kind, {})] for kind in KINDS}
    h.stage("every kind meets every criterion", lambda: require(not any(gaps.values()), "Criterion gaps: %s" % gaps) or sorted(KINDS))
    h.stage("every snapshot diff was exact", lambda: require(not run.mismatches, "%d snapshot mismatches" % len(run.mismatches)) or 0)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--upgrade-input", type=Path, required=True, help="DMC 'Open Library V19 PLC.zal19' extracted from Siemens-Open-Library-V19.zip")
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    upgrade_input = args.upgrade_input.resolve()
    inputs = sorted(p for p in FIXTURES.glob("*.*")) + [upgrade_input]
    Harness(args, "lp_", "V21 library change kinds with independent preservation snapshots, fresh reopen and per-kind checkpoint recovery",
            LIMITATIONS, inputs).run(lambda h: body(h, upgrade_input))
