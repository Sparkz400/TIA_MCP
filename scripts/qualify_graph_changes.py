"""Qualify the X06 GRAPH workflow natively in one owned scratch CPU 1511-1 PN: graph_export, graph_edit and the graph_import
change kind.

Phase A imports an authored GRAPH FB (the retained native seed) through block_import_xml, exports it with graph_export,
edits the retained document with graph_edit (a step rename) and applies it with graph_import with checkpoint, PLC compile and
save. graph_export must return the exact block software_inspect lists and leave the project unchanged; graph_edit's result
must equal its source with only the edited text replaced; each graph_import preview is asserted against the request, and a complete state (every block,
type and tag-table export, every member and group path) must differ from the previous one in exactly the GRAPH block,
whose native export then carries the edit. A fresh Portal then reopens the saved project, which must show the identical
state. Phase B exports, edits (another step rename) and imports once more from that state and restores the checkpoint in the
applying Portal; the recovered copy must equal the pre-change state. Typed refusals must change nothing.
"""
from __future__ import annotations

import argparse
import base64
import json
from pathlib import Path
import re

from library_native_harness import Harness, require
from qualification_owned_scratch import complete_inventory, snapshot
from qualify_access_changes import Run as StateRun
from qualify_plc_object_mutations import coverage_gaps, diff, view

ROOT = Path(__file__).resolve().parents[1]
SEED = ROOT / "tests" / "TiaMcp.Graph.Tests" / "fixtures" / "native-v21" / "populated" / "seed.xml"
BLOCK = "GraphQualification"
KINDS = ("graph_import",)
CRITERIA = {
    "AC-identity": "graph_export returns the exact block software_inspect lists and refuses non-GRAPH and missing blocks; graph_edit refuses a duplicate name; graph_import refuses documents outside expectedBlockNames and ambiguous inputs. Refusals change nothing.",
    "AC-completeness": "graph_export retains the complete native export (its SHA-256 equals the block_read artifact); graph_edit reports its comparison record.",
    "AC-preview": "graph_edit's result differs from its source only in the edited bytes; every graph_import preview is asserted against the request; a wrong previewHash refuses at apply.",
    "AC-preserve": "graph_export and graph_edit leave the project unchanged; graph_import changes exactly the GRAPH block in a complete program snapshot.",
    "AC-native": "graph_import applied + persisted: checkpoint, PLC compile and save stages, change_status, the edit in a fresh native export and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "graph_import restored: change_restore in the applying Portal into a new directory equals the pre-change state.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json, retained GRAPH records and limitations.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 with one GRAPH FB (the authored native seed: one step, one transition); branches, jumps, actions, "
    "interlocks and supervisions are not edited.",
    "graph_edit: renameStep only; the other typed operations are covered by the offline GRAPH tests, not natively here.",
    "Engineering only; no GRAPH sequence is executed, and behavioral equivalence is not certified.",
]


class Run(StateRun):
    plc_compiled = ("block_import_xml", "graph_import")

    def __init__(self, h):
        super().__init__(h)
        for key in ("certificateIdentity", "notQualified"):
            h.report.pop(key)
        h.report.update(criteria=CRITERIA, excluded={}, graph={})

    def args(self, kind, payload, **top):
        return {"kind": kind, "payload": payload, "plcPath": self.plc, **top}

    def domains(self):
        return {}

    def state(self):
        return view(complete_inventory(self.h.tool))

    def snap(self, label):
        self.count += 1
        destination = self.h.directory / "snapshots" / ("%03d-%s" % (self.count, re.sub(r"[^A-Za-z0-9]+", "-", label)[:40]))
        taken = {}
        def take():
            taken.update(snapshot(self.h.tool, destination))
            return {"objects": len(taken["objects"])}
        self.h.stage("snapshot " + label, take)
        state = view(taken["inventory"], taken["objects"])
        (destination / "state.json").write_text(json.dumps(state, indent=1), encoding="utf-8")
        return {"view": state, "files": {key: row.get("file") for key, row in taken["objects"].items()}}

    def block(self):
        return self.plc + "/blocks/" + BLOCK

    def document(self, record):
        """The retained original.xml of a GRAPH record, read through graph_artifact_read."""
        chunks, offset = [], 0
        while True:
            piece = self.h.tool("graph_artifact_read", {"recordRef": record, "name": "original.xml", "offset": offset})
            chunks.append(base64.b64decode(piece["data"]))
            offset = piece["nextOffset"]
            if piece["complete"]:
                return b"".join(chunks)

    def export(self, label):
        """graph_export of the GRAPH block: the exact block, the retained bytes of its native export, no project change."""
        h = self
        def check():
            before = h.state()
            result = h.h.tool("graph_export", {"blockPath": h.block()})
            row = next(r for r in complete_inventory(h.h.tool)["blocks"] if r["path"] == h.block())
            require(result["block"]["path"] == h.block() and result["block"]["number"] == row["number"] and result["block"]["programmingLanguage"] == "GRAPH",
                    "graph_export returned another block: %s" % result["block"])
            require(result["document"]["sourceSha256"] == result["artifact"]["sha256"] and result["document"]["hasErrors"] is False,
                    "The retained document is not the exported artifact: %s" % result["document"])
            require(h.state() == before, "graph_export changed the project")
            return result
        result = self.h.stage("graph_export " + label, check)
        self.mark("graph_export", "applied", label)
        return result["document"]["recordRef"]

    def edit(self, label, source, operations, old, new):
        """graph_edit: a new retained document whose bytes differ from the source only in the edited text."""
        def check():
            before = self.state()
            result = self.h.tool("graph_edit", {"sourceRef": source, "operations": operations})
            original, edited = self.document(source), self.document(result["document"]["recordRef"])
            # A step rename also renames the step's G7_StepPlus member in the block interface, so every occurrence changes.
            require(old.encode("utf-8") in original and edited == original.replace(old.encode("utf-8"), new.encode("utf-8")),
                    "graph_edit changed more than %r -> %r" % (old, new))
            require(self.state() == before, "graph_edit changed the project")
            return {"document": result["document"], "comparison": result["comparison"], "bytes": [len(original), len(edited)]}
        result = self.h.stage("graph_edit " + label, check)
        self.h.report["graph"][label] = result
        self.mark("graph_edit", "applied", label)
        return result["document"]["recordRef"]

    def prepare(self, label, kind, payload, preview=None, shown=None, exact=None):
        if kind != "graph_import":
            return super().prepare(label, kind, payload, preview, shown, exact)
        # The Host replaces graphRef's document by its validated XML and source hash before the worker sees the payload.
        prepared = self.h.stage("prepare " + label, lambda: self.h.tool("change_prepare", self.args(kind, payload)))
        actual = prepared["preview"]
        echoed = actual["payload"]
        require(prepared["state"] == "prepared" and actual["changeId"] == prepared["changeId"] and actual["kind"] == kind and actual["project"]["path"] == self.project
                and echoed.get("graphRef") == payload["graphRef"] and echoed.get("expectedBlockNames") == payload["expectedBlockNames"]
                and len(echoed.get("sourceSha256") or "") == 64 and "<SW.Blocks.FB" in echoed.get("xml", ""),
                "Preview of %s differs: %s" % (label, {k: actual.get(k) for k in ("kind", "project")}))
        return prepared

    def apply(self, label, kind, payload, preview=None, **expected):
        prepared = self.prepare(label, kind, payload, preview or {})
        self.finish(label, kind, prepared, **expected)
        return prepared


def generated(before, current):
    """Importing a GRAPH FB copies TIA's protected system objects for it."""
    def check(category, key):
        row = (current if category == "added" else before).get(key) or {}
        return category in ("added", "removed") and key.split(":", 1)[0] in ("block", "type") and ("/systemGroups/" in key or row.get("isKnowHowProtected") is True)
    return check


def graph_import(run, label, record, text, restore=False):
    key = "block:" + run.block()
    before = run.current["view"]
    payload = {"graphRef": record, "expectedBlockNames": [BLOCK]}
    def exported(applied, state):
        require(text in Path(run.current["files"][key]).read_text(encoding="utf-8-sig"), "The native export of %s lacks %r" % (BLOCK, text))
    prepared = run.prepare(label, "graph_import", payload, {})
    run.finish(label, "graph_import", prepared, changed=[key], check=exported, tolerated=generated(before, run.current["view"]), restore=restore)


def phase_a(run):
    xml = SEED.read_text(encoding="utf-8-sig")
    before = run.current["view"]
    prepared = run.prepare("import the GRAPH seed", "block_import_xml", {"xml": xml, "expectedBlockNames": [BLOCK]}, {})
    run.finish("import the GRAPH seed", "block_import_xml", prepared, added=["block:" + run.block()], tolerated=generated(before, {}))
    h = run.h
    def refuse_export(label, arguments, code):
        def check():
            before = run.state()
            error = h.call_error("graph_export", arguments)
            require(error.get("code") == code and run.state() == before, "Expected %s: %s" % (code, error))
            return error
        h.stage("refuse graph_export: " + label, check)
        run.mark("graph_export", "refused", code)
    refuse_export("an SCL block", {"blockPath": run.plc + "/blocks/TiaMcp_IsEnabled"}, "UnsupportedLanguage")
    refuse_export("a missing block", {"blockPath": run.plc + "/blocks/TiaMcpMissing"}, "BlockNotFound")
    source = run.export("of the seed")
    def refuse_edit():
        before = run.state()
        error = h.call_error("graph_edit", {"sourceRef": source, "operations": [{"op": "renameStep", "number": 1, "name": "StartCommand"}]})
        require(error.get("code") == "GraphEditDuplicate" and run.state() == before, "Expected GraphEditDuplicate: %s" % error)
        return error
    h.stage("refuse graph_edit: a step name an interface member uses", refuse_edit)
    run.mark("graph_edit", "refused", "GraphEditDuplicate")
    edited = run.edit("rename step 1", source, [{"op": "renameStep", "number": 1, "name": "TiaMcpIdle"}], 'Name="Idle"', 'Name="TiaMcpIdle"')
    run.refuse("graph_import", "a block outside expectedBlockNames", {"graphRef": edited, "expectedBlockNames": ["TiaMcpOther"]}, "XmlScopeMismatch")
    run.refuse("graph_import", "both graphRef and path", {"graphRef": edited, "path": str(SEED), "expectedBlockNames": [BLOCK]}, "InvalidRequest")
    stale = run.prepare("import the renamed step (made stale below)", "graph_import", {"graphRef": edited, "expectedBlockNames": [BLOCK]}, {})
    run.refuse_apply("graph_import", "a wrong previewHash", {**stale, "previewHash": "0" * 64}, "PreviewMismatch")
    graph_import(run, "import the renamed step", edited, 'Name="TiaMcpIdle"')
    run.refuse_apply("graph_import", "a preview made stale by a later change", stale, "StaleRevision")


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
    source = run.export("of the reopened block")
    edited = run.edit("rename step 1 again", source, [{"op": "renameStep", "number": 1, "name": "TiaMcpAgain"}], 'Name="TiaMcpIdle"', 'Name="TiaMcpAgain"')
    graph_import(run, "import the step renamed again", edited, 'Name="TiaMcpAgain"', restore=True)


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("graph changes")
    run.project = h.tool("tia_session_status")["projects"][0]["path"]
    run.current = run.snap("baseline")
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
    Harness(args, "gr_", "X06 GRAPH export, edit and import in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
