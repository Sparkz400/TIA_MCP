"""Qualify the X01 project-level kinds natively in one owned scratch CPU 1511-1 PN: project languages, project_copy and
project_open with readOnly. project_attributes_set must refuse every allowlisted attribute, which V21 declares read-only.

Phase A applies project_languages_set with checkpoint, compile and save. Each preview is asserted
against the request and project_properties, and a complete state (every block, type and tag-table export, every member and
group path, and the project_properties metadata, languages, text categories and used products) must differ from the previous
one in exactly the intended keys. A fresh Portal then reopens the saved project, which must show the identical state. Phase B
applies one more change per kind and restores its checkpoint; the recovered copy must equal the pre-change state. Phase C
copies the saved project with project_copy, which must leave the source unchanged, and opens the copy read-only in a fresh
Portal: it must be secondary, equal to the source and refuse every change with ReadOnlyProject. Typed refusals must change
nothing.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re

from library_native_harness import Harness, require
from qualification_owned_scratch import complete_inventory, snapshot
from qualify_access_changes import Run as StateRun
from qualify_plc_object_mutations import coverage_gaps, diff, matches, view

KINDS = ("project_languages_set",)
# V21 native attribute info declares the whole project_attributes_set allowlist read-only.
READ_ONLY = ("Author", "Copyright", "Family", "Version")
# Point-in-time values that a save or a recovered copy changes by themselves.
VOLATILE = ("lastModified", "lastModifiedBy", "sizeKb", "isModified")
CRITERIA = {
    "AC-identity": "refused: typed refusals of read-only and non-allowlisted attributes, unknown or unsupported cultures, removal of the reference or editing language, mutations of a read-only project and a missing copy source, each leaving the complete state unchanged.",
    "AC-preview": "Every preview is asserted against the request and project_properties before apply; a wrong previewHash refuses at apply.",
    "AC-preserve": "applied: every program export and member, and the project metadata, languages, text categories and used products differ in exactly the intended keys; project_copy leaves the source unchanged and its copy equals it.",
    "AC-native": "applied + persisted: checkpoint, compile and save stages, change_status, project_properties readback and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the pre-change state.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json and limitations.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 project; multiuser, protected (UMAC) and upgraded projects are not exercised.",
    "project_attributes_set cannot apply on V21: native attribute info declares Author, Copyright, Family and Version read-only, so each is refused "
    "with AttributeNotWritable (asserted here); the multilingual Comment is refused by design.",
    "project_languages_set adds and removes one supported language and switches the editing language; texts in a removed language are not seeded, "
    "so their loss is not observed.",
    "project_copy copies a saved project; the UnsavedProject refusal is not exercised, because no read-only route leaves the source modified.",
    "project_open readOnly: TIA opens the project as secondary; whether Siemens itself write-protects it is not established, the refusal is the worker's guard.",
]


def project(properties):
    """Comparable project metadata from project_properties, without point-in-time values and the project path."""
    require(properties["completeness"]["complete"] is True, "project_properties is incomplete: %s" % properties["completeness"])
    return {"project:properties": {k: v for k, v in properties["properties"].items() if k not in VOLATILE},
            "project:languages": properties["languageSettings"], "project:attributeAccess": properties["attributeAccess"],
            "project:textCategories": properties["textCategories"], "project:usedProducts": properties["usedProducts"]}


class Run(StateRun):
    """The access runner's apply/refuse/restore gates over the PLC program and project_properties."""

    def __init__(self, h):
        super().__init__(h)
        for key in ("certificateIdentity", "notQualified"):
            h.report.pop(key)
        h.report.update(criteria=CRITERIA, excluded={})

    def args(self, kind, payload, **top):
        return {"kind": kind, "payload": payload, **top}

    def domains(self):
        return project(self.h.tool("project_properties", {"maxItems": 200}))

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
        return {"view": state}

    def apply(self, label, kind, payload, preview, **expected):
        prepared = self.prepare(label, kind, payload, preview)
        self.finish(label, kind, prepared, **expected)
        return prepared


def languages(run, label, payload, after, restore=False):
    current = run.current["view"]["project:languages"]
    run.apply(label, "project_languages_set", payload,
              {"effects": [{"languageSettings": {"before": {k: current[k] for k in ("active", "reference", "editing")}, "after": after}}]},
              changed=["project:languages"], rows={"project:languages": after}, restore=restore)


def pick_language(run):
    """A supported language that is not active, for the add/remove cycle."""
    settings = run.current["view"]["project:languages"]
    for culture in ("de-DE", "fr-FR", "it-IT", "es-ES", "zh-CN"):
        if culture in settings["supported"] and culture not in settings["active"]:
            return culture
    raise AssertionError("No inactive supported language: %s" % settings)


def phase_a(run):
    access = run.current["view"]["project:attributeAccess"]
    require(access == {name: "Read" for name in READ_ONLY}, "Expected the V21 read-only project attributes: %s" % access)
    for name in READ_ONLY:
        run.refuse("project_attributes_set", name + ", which V21 declares read-only", {"attributes": {name: "TiaMcp"}}, "AttributeNotWritable")
    run.refuse("project_attributes_set", "the multilingual Comment", {"attributes": {"Comment": "x"}}, "UnsupportedAttribute")
    settings = run.current["view"]["project:languages"]
    reference, culture = settings["reference"], pick_language(run)
    stale = run.prepare("add %s (made stale below)" % culture, "project_languages_set", {"addActive": [culture]})
    run.refuse_apply("project_languages_set", "a wrong previewHash", {**stale, "previewHash": "0" * 64}, "PreviewMismatch")
    run.refuse("project_languages_set", "an unknown culture", {"addActive": ["xx-INVALID"]}, "InvalidRequest")
    run.refuse("project_languages_set", "removing the reference language", {"removeActive": [reference]}, "LanguageInUse")
    run.refuse("project_languages_set", "an editing language that is not active", {"editingLanguage": culture}, "InvalidRequest")
    run.refuse("project_languages_set", "an empty request", {}, "InvalidRequest")
    active = sorted(settings["active"] + [culture])
    languages(run, "add %s as editing language" % culture, {"addActive": [culture], "editingLanguage": culture},
              {"active": active, "reference": reference, "editing": culture})
    run.refuse_apply("project_languages_set", "a preview made stale by a later change", stale, "StaleRevision")
    languages(run, "edit in %s again" % settings["editing"], {"editingLanguage": settings["editing"]},
              {"active": active, "reference": reference, "editing": settings["editing"]})
    run.h.report["fixture"] = {"language": culture, "reference": reference}


def reopen(run, path, label):
    h = run.h
    h.stage("close the session " + label, lambda: h.tool("tia_detach"))
    h.owned_portal(label)
    return h.stage("open " + label, lambda: h.reopen_project(path))


def persisted(run):
    final = run.current
    reopen(run, run.project, "the saved project")
    run.current = run.b0 = run.snap("reopened")
    run.h.stage("reopened state equals every verified change",
                lambda: require(run.b0["view"] == final["view"], "Reopened state differs: %s" % diff(final["view"], run.b0["view"])) or len(final["view"]))
    for kind, rows in list(run.coverage.items()):
        if rows.get("applied"):
            run.mark(kind, "persisted", "fresh Portal reopen")


def phase_b(run):
    culture = run.h.report["fixture"]["language"]
    settings = run.current["view"]["project:languages"]
    languages(run, "remove %s" % culture, {"removeActive": [culture]},
              {"active": sorted(c for c in settings["active"] if c != culture), "reference": settings["reference"], "editing": settings["editing"]}, restore=True)


def phase_c(run):
    """project_copy of the recovered project, then the copy opened read-only in a fresh Portal."""
    h, source = run.h, run.current
    error = h.stage("refuse project_copy of a missing project", lambda: h.call_error("project_copy", {"projectName": "TiaMcpMissing"}))
    require(error.get("code") == "ProjectNotFound", "Expected ProjectNotFound: %s" % error)
    copied = h.stage("project_copy", lambda: h.tool("project_copy"))
    copy = copied["project"]["path"]
    require(copied.get("copyClosed") is True and Path(copy).resolve() != Path(run.project).resolve() and Path(copy).is_file(), "The copy is not a closed project file in a new directory: %s" % copied)
    after = run.snap("source after project_copy")
    h.stage("project_copy left the source unchanged", lambda: require(after["view"] == source["view"], "Source changed: %s" % diff(source["view"], after["view"])) or len(source["view"]))
    h.stage("close the session before the read-only open", lambda: h.tool("tia_detach"))
    h.owned_portal("read-only copy")
    opened = h.stage("open the copy read-only", lambda: h.tool("project_open", {"path": copy, "readOnly": True}))
    status = h.tool("tia_session_status")
    require([(p["path"], p.get("readOnly")) for p in status["projects"]] == [(copy, True)], "The copy is not the only, read-only project: %s" % status["projects"])
    view_copy = run.snap("read-only copy")
    def equal():
        mine, theirs = dict(view_copy["view"]), dict(source["view"])
        require(mine.pop("project:properties").pop("isPrimary") is False, "The read-only copy reports itself as primary")
        theirs["project:properties"] = {k: v for k, v in theirs["project:properties"].items() if k != "isPrimary"}
        require(mine == theirs, "The copy differs from its source: %s" % diff(theirs, mine))
        return len(mine)
    h.stage("the read-only copy equals its source", equal)
    for kind, payload in (("project_languages_set", {"editingLanguage": run.current["view"]["project:languages"]["reference"]}), ("tag_create", {"tagTableName": "Default tag table", "name": "RoTag", "dataTypeName": "Bool", "logicalAddress": "%M90.0"})):
        run.refuse(kind, "a read-only project", payload, "ReadOnlyProject")
    h.report["copy"] = {"opened": opened, "result": copied}


def body(h):
    run = Run(h)
    h.owned_scratch("project settings")
    run.project = h.tool("tia_session_status")["projects"][0]["path"]
    run.current = run.snap("baseline")
    phase_a(run)
    persisted(run)
    phase_b(run)
    phase_c(run)
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
    Harness(args, "ps_", "X01 project settings, copy and read-only open in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
