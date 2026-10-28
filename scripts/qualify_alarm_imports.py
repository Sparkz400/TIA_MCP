"""Qualify the D11 PLC alarm import kinds natively in one owned scratch CPU 1511-1 PN.

Phase A adds an FB with a Program_Alarm and its instance DB, then applies plc_alarm_class_import (an edited display name in
the project's alarm-class DAT), plc_alarm_instance_text_import (an edited instance alarm text), plc_supervision_import (the
PLC's supervision workbook, unchanged) and plc_supervision_settings_import (an edited ProDiag category name) with checkpoint,
compile and save. The first supervision import initializes ProDiag's global settings, which makes their export available. Each import file is a native export edited
only in the named cell or element. Each preview is asserted against the request, and a complete state (every block, type and
tag-table export, every member and group path, and plc_alarm_inspect's alarm classes, instance texts, supervisions and text
lists) must differ from the previous one in exactly the intended keys. A fresh Portal then reopens the saved project, which
must show the identical state. Phase B applies one more import per kind from that state and restores its checkpoint in the
applying Portal; the recovered copy must equal the pre-change state. Typed refusals must change nothing.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import zipfile

from library_native_harness import Harness, require
from qualification_owned_scratch import complete_inventory, snapshot
from qualify_access_changes import Run as StateRun
from qualify_plc_object_mutations import coverage_gaps, diff, view
from typed_graphical_transport import artifact_bytes

KINDS = ("plc_alarm_class_import", "plc_alarm_instance_text_import", "plc_supervision_import", "plc_supervision_settings_import")
FB = '''FUNCTION_BLOCK "AlFb"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      Trig : Bool;
   END_VAR
   VAR
      Alarm : Program_Alarm;
   END_VAR

BEGIN
\t#Alarm(SIG := #Trig);
END_FUNCTION_BLOCK

'''
CRITERIA = {
    "AC-identity": "refused: typed refusals of changed hashes, relative or wrongly typed files, import options a provider lacks or does not know and missing text-list names, each leaving the complete state unchanged.",
    "AC-preview": "Every preview is asserted against the request before apply; a wrong previewHash refuses at apply.",
    "AC-preserve": "applied: every program export and member and plc_alarm_inspect's alarm classes, instance texts, supervisions and text lists differ in exactly the intended keys.",
    "AC-native": "applied + persisted: checkpoint, compile and save stages, change_status, a fresh native export showing the edit and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the pre-change state.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json, the edited import files and limitations.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 with the default alarm classes and one Program_Alarm instance; HMI consumers of the classes are not exercised.",
    "plc_supervision_import re-imports the PLC's own supervision workbook unchanged: a scratch project has no ProDiag supervisions to edit.",
    "TIA initializes ProDiag's global settings on the first supervision import (recorded under sideEffects); before that their export fails with "
    "'Global settings are not initialized'. plc_supervision_settings_import edits one category display name.",
    "plc_alarm_text_list_import is not run: a scratch project has no user text list to export, so it has no native file to start from.",
    "Engineering only; nothing is downloaded and no alarm is raised.",
]


def creation_free(text):
    return re.sub(r'\sCreationDate="[^"]*"', "", text)


def dat_text(raw):
    """The XML inside an alarm-class DAT package, without its CreationDate stamp."""
    with zipfile.ZipFile(io.BytesIO(raw)) as package:
        names = package.namelist()
        require(len(names) == 1, "Expected one entry in the alarm-class DAT: %s" % names)
        return creation_free(package.read(names[0]).decode("utf-8-sig"))


def edit_dat(raw, old, new):
    with zipfile.ZipFile(io.BytesIO(raw)) as package:
        name = package.namelist()[0]
        text = package.read(name).decode("utf-8-sig")
    require(text.count(old) == 1, "Expected exactly one %r in the alarm-class DAT" % old)
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as package:
        package.writestr(name, ("﻿" + text.replace(old, new)).encode("utf-8"))
    return out.getvalue()


def column_number(letters):
    number = 0
    for letter in letters:
        number = number * 26 + ord(letter) - 64
    return number


def edit_xlsx(raw, sheet_name, cell, text):
    """Set one cell of one worksheet to an inline string; every other part stays byte-identical."""
    package = zipfile.ZipFile(io.BytesIO(raw))
    workbook = package.read("xl/workbook.xml").decode("utf-8-sig")
    rid = re.search(r'name="%s"[^>]*r:id="([^"]+)"' % re.escape(sheet_name), workbook).group(1)
    rels = package.read("xl/_rels/workbook.xml.rels").decode("utf-8-sig")
    target = re.search(r'Id="%s"[^>]*Target="([^"]+)"|Target="([^"]+)"[^>]*Id="%s"' % (rid, rid), rels)
    part = "xl/" + (target.group(1) or target.group(2)).lstrip("/").replace("xl/", "", 1)
    xml = package.read(part).decode("utf-8-sig")
    prefix = re.match(r"(?s).*?<(\w+:)?worksheet", xml).group(1) or ""
    column, row = re.match(r"([A-Z]+)(\d+)$", cell).groups()
    value = '<{0}c r="{1}" t="inlineStr"><{0}is><{0}t>{2}</{0}t></{0}is></{0}c>'.format(prefix, cell, text)
    row_match = re.search(r'<{0}row r="{1}"[^>]*>(.*?)</{0}row>'.format(prefix, row), xml, re.S)
    require(row_match, "Row %s is missing in %s" % (row, sheet_name))
    cells = row_match.group(1)
    existing = re.search(r'<{0}c r="{1}"(?:[^>]*/>|[^>]*>.*?</{0}c>)'.format(prefix, cell), cells, re.S)
    if existing:
        cells = cells[:existing.start()] + value + cells[existing.end():]
    else:
        later = [m for m in re.finditer(r'<{0}c r="([A-Z]+)\d+"'.format(prefix), cells) if column_number(m.group(1)) > column_number(column)]
        cells = cells[:later[0].start()] + value + cells[later[0].start():] if later else cells + value
    xml = xml[:row_match.start(1)] + cells + xml[row_match.end(1):]
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as copy:
        for info in package.infolist():
            copy.writestr(info, xml.encode("utf-8") if info.filename == part else package.read(info.filename))
    return out.getvalue()


def text_column(sheet):
    """The en-US alarm-text column of the instance-text worksheet, and the first data row."""
    header = sheet["rows"][0]["cells"]
    columns = [column for column, title in header.items() if "[en-US]" in title and "alarm text" in title.lower()]
    require(len(columns) == 1 and len(sheet["rows"]) > 1, "No single en-US alarm-text column with data rows: %s" % header)
    return columns[0], sheet["rows"][1]["row"]


class Run(StateRun):
    plc_compiled = ("plc_alarm_instance_text_import", "plc_supervision_import", "source_upsert", "instance_db_create")

    def __init__(self, h):
        super().__init__(h)
        for key in ("certificateIdentity", "notQualified"):
            h.report.pop(key)
        h.report.update(criteria=CRITERIA, excluded={})
        self.exports = {}

    def args(self, kind, payload, **top):
        return {"kind": kind, "payload": payload, "plcPath": self.plc, **top}

    def domains(self):
        """plc_alarm_inspect's alarm configuration; the export files are kept for the next import."""
        result = self.h.tool("plc_alarm_inspect", {"plcPath": self.plc, "maxItems": 2000})
        scope = result["scopes"][0]
        state = {"alarm:textLists": scope["textLists"]}
        for key, part in (("instanceTexts", scope["alarmInstanceTextExport"]), ("supervisions", scope["supervisionExport"])):
            state["alarm:" + key] = {"status": part["status"], "sheets": [{"name": s["name"], "rows": s["rows"]} for s in part.get("sheets", [])]}
            if part.get("artifact"):
                self.exports[key] = (part["artifact"], artifact_bytes(self.h.tool, part["artifact"]))
        classes = result["alarmClassExport"]
        raw = artifact_bytes(self.h.tool, classes["artifact"])
        self.exports["classes"] = (classes["artifact"], raw)
        state["alarm:classes"] = dat_text(raw)
        settings = result["supervisionSettingsExport"]
        state["alarm:supervisionSettings"] = {k: settings.get(k) for k in ("status", "nativeState")}
        if settings.get("artifact"):
            raw = artifact_bytes(self.h.tool, settings["artifact"])
            self.exports["settings"] = (settings["artifact"], raw)
            state["alarm:supervisionSettings"]["text"] = dat_text(raw)
        return state

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

    def file(self, name, raw):
        path = self.h.directory / "imports" / name
        path.parent.mkdir(exist_ok=True)
        path.write_bytes(raw)
        return {"artifactPath": str(path.resolve()), "expectedSha256": hashlib.sha256(raw).hexdigest()}

    def apply(self, label, kind, payload, preview=None, **expected):
        prepared = self.prepare(label, kind, payload, preview or {"effects": [{"action": kind}]})
        before = self.current["view"]
        def generated(category, key):
            """Compiling a Program_Alarm FB copies TIA's protected system objects into the system groups."""
            row = (self.current["view"] if category == "added" else before).get(key) or {}
            return category in ("added", "removed") and key.split(":", 1)[0] in ("block", "type") and ("/systemGroups/" in key or row.get("isKnowHowProtected") is True)
        self.finish(label, kind, prepared, tolerated=expected.pop("tolerated", generated), **expected)
        return prepared


def classes(run, old, new, restore=False):
    payload = run.file("classes-%s.dat" % new, edit_dat(run.exports["classes"][1], "<DisplayName>%s</DisplayName>" % old, "<DisplayName>%s</DisplayName>" % new))
    run.apply("rename alarm class display name %s to %s" % (old, new), "plc_alarm_class_import", payload, changed=["alarm:classes"],
              check=lambda applied, state: require("<DisplayName>%s</DisplayName>" % new in state["alarm:classes"], "The fresh export lacks the new display name"),
              restore=restore)


def instance_text(run, text, restore=False):
    sheet = run.current["view"]["alarm:instanceTexts"]["sheets"][0]
    column, row = text_column(sheet)
    payload = run.file("instance-%s.xlsx" % text, edit_xlsx(run.exports["instanceTexts"][1], sheet["name"], "%s%d" % (column, row), text))
    instance = run.plc + "/blocks/AlFbDb"
    def shown(applied, state):
        rows = state["alarm:instanceTexts"]["sheets"][0]["rows"]
        require(next(r for r in rows if r["row"] == row)["cells"].get(column) == text, "The fresh export does not show %r" % text)
    run.apply("set the instance alarm text to %s" % text, "plc_alarm_instance_text_import", payload, changed=["alarm:instanceTexts"],
              tolerated=lambda category, key: category == "changed" and key == "block:" + instance, check=shown, restore=restore)


def supervisions(run, restore=False):
    payload = {**run.file("supervisions.xlsx", run.exports["supervisions"][1]), "importOption": "Override"}
    run.apply("re-import the supervision workbook", "plc_supervision_import", payload, restore=restore,
              tolerated=lambda category, key: category == "changed" and key == "alarm:supervisionSettings")


def settings(run, old, new, restore=False):
    element = '<DisplayName Culture="en-US">%s</DisplayName>'
    payload = run.file("settings-%s.dat" % new.replace(" ", "-"), edit_dat(run.exports["settings"][1], element % old, element % new))
    run.apply("rename ProDiag category %s to %s" % (old, new), "plc_supervision_settings_import", payload, changed=["alarm:supervisionSettings"],
              check=lambda applied, state: require(element % new in state["alarm:supervisionSettings"]["text"], "The fresh settings export lacks %r" % new),
              restore=restore)


def phase_a(run):
    run.apply("create AlFb with a Program_Alarm", "source_upsert", {"language": "SCL", "source": FB, "expectedBlockNames": ["AlFb"]},
              {"effects": [{"action": "create", "blockName": "AlFb"}]}, added=["block:" + run.plc + "/blocks/AlFb"])
    run.apply("create AlFb's instance DB", "instance_db_create", {"name": "AlFbDb", "instanceOfName": "AlFb"},
              {"effects": [{"action": "create", "blockType": "InstanceDB", "blockName": "AlFbDb"}]}, added=["block:" + run.plc + "/blocks/AlFbDb"], changed=["alarm:instanceTexts"])
    dat = run.file("classes-unchanged.dat", run.exports["classes"][1])
    run.refuse("plc_alarm_class_import", "a changed hash", {**dat, "expectedSha256": "0" * 64}, "ArtifactChanged")
    run.refuse("plc_alarm_class_import", "a relative path", {**dat, "artifactPath": "classes.dat"}, "InvalidArtifact")
    run.refuse("plc_alarm_class_import", "an import option the provider lacks", {**dat, "importOption": "Override"}, "InvalidRequest")
    run.refuse("plc_alarm_class_import", "a workbook instead of a DAT", run.file("classes.xlsx", run.exports["supervisions"][1]), "InvalidArtifact")
    texts = run.file("instance-unchanged.xlsx", run.exports["instanceTexts"][1])
    run.refuse("plc_alarm_instance_text_import", "an import option the provider lacks", {**texts, "importOption": "Override"}, "InvalidRequest")
    run.refuse("plc_alarm_instance_text_import", "a changed hash", {**texts, "expectedSha256": "0" * 64}, "ArtifactChanged")
    run.refuse("plc_supervision_import", "an unknown import option", {**run.file("supervisions.xlsx", run.exports["supervisions"][1]), "importOption": "Merge"}, "InvalidRequest")
    run.refuse("plc_alarm_text_list_import", "no expected text-list names", {**run.file("lists.xlsx", run.exports["supervisions"][1]), "importOption": "None", "expectedTextListNames": []}, "InvalidRequest")
    stale = run.prepare("re-import the unchanged alarm classes (made stale below)", "plc_alarm_class_import", dat)
    run.refuse_apply("plc_alarm_class_import", "a wrong previewHash", {**stale, "previewHash": "0" * 64}, "PreviewMismatch")
    classes(run, "A", "AQ")
    run.refuse_apply("plc_alarm_class_import", "a preview made stale by a later import", stale, "StaleRevision")
    instance_text(run, "TiaMcpAlarm")
    supervisions(run)
    require("settings" in run.exports, "The supervision import did not make the ProDiag settings exportable")
    run.refuse("plc_supervision_settings_import", "an import option the provider lacks", {**run.file("settings.dat", run.exports["settings"][1]), "importOption": "Override"}, "InvalidRequest")
    settings(run, "Category 4", "TiaMcp Category")


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
    classes(run, "AQ", "AR", restore=True)
    instance_text(run, "TiaMcpAgain", restore=True)
    supervisions(run, restore=True)
    settings(run, "TiaMcp Category", "TiaMcp Again", restore=True)


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("alarm imports")
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
    Harness(args, "al_", "D11 PLC alarm imports in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
