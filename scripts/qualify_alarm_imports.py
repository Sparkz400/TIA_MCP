"""Qualify the D11 PLC alarm import kinds natively in one owned scratch CPU 1511-1 PN.

Phase A adds an FB with a Program_Alarm and its instance DB, then applies plc_alarm_class_import (an edited display name in
the project's alarm-class DAT), plc_alarm_instance_text_import (an edited instance alarm text), plc_supervision_import (the
PLC's supervision workbook, unchanged), plc_supervision_settings_import (an edited ProDiag category name) and
plc_alarm_text_list_import (a new user text list from an authored workbook with None, then one entry text edited in TIA's
own export of it with Override) with checkpoint, compile and save. The first supervision import initializes ProDiag's global settings, which makes their export available. Each import file is a native export edited
only in the named cell or element. Each preview is asserted against the request, and a complete state (every block, type and
tag-table export, every member and group path, and plc_alarm_inspect's alarm classes, instance texts, supervisions and text
lists and the user text-list export) must differ from the previous one in exactly the intended keys. A fresh Portal then reopens the saved project, which
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
from xml.sax.saxutils import escape
import zipfile

from library_native_harness import Harness, require
from qualification_owned_scratch import complete_inventory, snapshot
from qualify_access_changes import Run as StateRun
from qualify_plc_object_mutations import coverage_gaps, diff, view
from typed_graphical_transport import artifact_bytes

KINDS = ("plc_alarm_class_import", "plc_alarm_instance_text_import", "plc_supervision_import", "plc_supervision_settings_import", "plc_alarm_text_list_import")
TEXT_LIST = "TiaMcpStates"
ENTRIES = (("0", "0", "Stopped"), ("1", "1", "Running"), ("2", "5", "Fault"))
SPREADSHEET = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
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
    "AC-identity": "refused: typed refusals of changed hashes, relative or wrongly typed files, import options a provider lacks or does not know, missing text-list names and None for an existing text list, each leaving the complete state unchanged.",
    "AC-preview": "Every preview is asserted against the request before apply; a wrong previewHash refuses at apply.",
    "AC-preserve": "applied: every program export and member and plc_alarm_inspect's alarm classes, instance texts, supervisions, text lists and user text-list export differ in exactly the intended keys.",
    "AC-native": "applied + persisted: checkpoint, compile and save stages, change_status, a fresh native export showing the edit and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the pre-change state.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json, the edited import files and limitations.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 with the default alarm classes and one Program_Alarm instance; HMI consumers of the classes are not exercised.",
    "plc_supervision_import re-imports the PLC's own supervision workbook unchanged: a scratch project has no ProDiag supervisions to edit.",
    "TIA initializes ProDiag's global settings on the first supervision import (recorded under sideEffects); before that their export fails with "
    "'Global settings are not initialized'. plc_supervision_settings_import edits one category display name.",
    "A fresh CPU has no user text list to export, so the first text-list workbook is authored in the layout TIA V21's importer reads (sheets TextList with "
    "Name/ListRange/Comment [culture] and TextListEntry with Parent/From/To/Text [culture], custom properties FileVersion 1 and FileContent 'Alarm text lists'); "
    "the Override imports edit one entry text in TIA's own export. One Decimal list with three entries; Binary and Bit lists, list comments and HMI consumers are not exercised.",
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


def text_list_workbook(cultures):
    """One user text list in the layout TIA V21's text-list importer reads; every cell is an inline string, as in edit_xlsx."""
    def sheet(rows):
        cells = lambda r, row: "".join('<c r="%s%d" t="inlineStr"><is><t>%s</t></is></c>' % (chr(65 + i), r, escape(v)) for i, v in enumerate(row))
        body = "".join('<row r="%d">%s</row>' % (r, cells(r, row)) for r, row in enumerate(rows, 1))
        return '<?xml version="1.0" encoding="utf-8"?><worksheet xmlns="%s"><sheetData>%s</sheetData></worksheet>' % (SPREADSHEET, body)
    lists = [["Name", "ListRange", *("Comment [%s]" % c for c in cultures)], [TEXT_LIST, "Decimal", *("TIA MCP qualification" for _ in cultures)]]
    entries = [["Parent", "From", "To", *("Text [%s]" % c for c in cultures)], *([TEXT_LIST, low, high, *(text for _ in cultures)] for low, high, text in ENTRIES)]
    rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
    prop = '<property fmtid="{D5CDD505-2E9C-101B-9397-08002B2CF9AE}" pid="%d" name="%s"><vt:lpwstr>%s</vt:lpwstr></property>'
    parts = {
        "[Content_Types].xml": '<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
            '<Default Extension="xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml" />'
            '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />'
            '<Override PartName="/docProps/custom.xml" ContentType="application/vnd.openxmlformats-officedocument.custom-properties+xml" />'
            '<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml" />'
            '<Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml" /></Types>',
        "_rels/.rels": '<?xml version="1.0" encoding="utf-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Type="%s/officeDocument" Target="/xl/workbook.xml" Id="R1" /><Relationship Type="%s/custom-properties" Target="/docProps/custom.xml" Id="R2" /></Relationships>' % (rel, rel),
        "docProps/custom.xml": '<?xml version="1.0" encoding="utf-8"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/custom-properties" '
            'xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">%s%s</Properties>' % (prop % (2, "FileVersion", "1"), prop % (3, "FileContent", "Alarm text lists")),
        "xl/workbook.xml": '<?xml version="1.0" encoding="utf-8"?><workbook xmlns="%s"><sheets><sheet name="TextList" sheetId="1" r:id="R1" xmlns:r="%s" />'
            '<sheet name="TextListEntry" sheetId="2" r:id="R2" xmlns:r="%s" /></sheets></workbook>' % (SPREADSHEET, rel, rel),
        "xl/_rels/workbook.xml.rels": '<?xml version="1.0" encoding="utf-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Type="%s/worksheet" Target="/xl/worksheets/sheet1.xml" Id="R1" /><Relationship Type="%s/worksheet" Target="/xl/worksheets/sheet2.xml" Id="R2" /></Relationships>' % (rel, rel),
        "xl/worksheets/sheet1.xml": sheet(lists),
        "xl/worksheets/sheet2.xml": sheet(entries),
    }
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as package:
        for name, xml in parts.items():
            package.writestr(name, xml.encode("utf-8"))
    return out.getvalue()


def text_columns(sheet):
    return sorted(c for c, title in sheet["rows"][0]["cells"].items() if title.startswith("Text ["))


def entry_cell(sheets, text):
    """The cell of the first-culture Text column whose value is text, in the TextListEntry worksheet of a text-list export."""
    sheet = next(s for s in sheets if s["name"] == "TextListEntry")
    column = text_columns(sheet)[0]
    rows = [r["row"] for r in sheet["rows"][1:] if r["cells"].get(column) == text]
    require(len(rows) == 1, "Expected one %r entry in the text-list export: %s" % (text, sheet["rows"]))
    return "%s%d" % (column, rows[0])


def entry_texts(state):
    sheets = [s for s in state["alarm:userTextLists"].get("sheets") or [] if s["name"] == "TextListEntry"]
    return sorted(r["cells"].get(c) for s in sheets for c in text_columns(s)[:1] for r in s["rows"][1:])


def text_column(sheet):
    """The en-US alarm-text column of the instance-text worksheet, and the first data row."""
    header = sheet["rows"][0]["cells"]
    columns = [column for column, title in header.items() if "[en-US]" in title and "alarm text" in title.lower()]
    require(len(columns) == 1 and len(sheet["rows"]) > 1, "No single en-US alarm-text column with data rows: %s" % header)
    return columns[0], sheet["rows"][1]["row"]


class Run(StateRun):
    plc_compiled = ("plc_alarm_instance_text_import", "plc_supervision_import", "plc_alarm_text_list_import", "source_upsert", "instance_db_create")

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
        self.cultures = result["activeCultures"]
        state = {"alarm:textLists": scope["textLists"]}
        lists = scope["userTextListExport"]
        # Without a user text list V21 refuses the export; only its status is state then.
        state["alarm:userTextLists"] = {"status": lists["status"], "sheets": [{"name": s["name"], "rows": s["rows"]} for s in lists.get("sheets", [])]}
        if lists.get("artifact"):
            self.exports["userTextLists"] = (lists["artifact"], artifact_bytes(self.h.tool, lists["artifact"]))
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


def text_list_create(run):
    payload = {**run.file("text-list.xlsx", text_list_workbook(run.cultures)), "importOption": "None", "expectedTextListNames": [TEXT_LIST]}
    def created(applied, state):
        row = next((i for i in state["alarm:textLists"]["items"] if i["name"] == TEXT_LIST), None)
        require(row and row["textListKind"] == "user" and row["listRange"] == "Decimal", "The new user text list is missing: %s" % state["alarm:textLists"])
        require(state["alarm:userTextLists"]["status"] == "observed" and {"Stopped", "Running", "Fault"} <= set(entry_texts(state)),
                "The fresh text-list export lacks the imported entries: %s" % state["alarm:userTextLists"])
    run.apply("create user text list %s" % TEXT_LIST, "plc_alarm_text_list_import", payload,
              {"effects": [{"action": "plc_alarm_text_list_import", "importOption": "None", "createTextLists": [TEXT_LIST], "replaceTextLists": []}]},
              changed=["alarm:textLists", "alarm:userTextLists"], check=created)


def text_list_override(run, old, new, restore=False):
    raw = run.exports["userTextLists"][1]
    payload = {**run.file("text-list-%s.xlsx" % new.replace(" ", "-"), edit_xlsx(raw, "TextListEntry", entry_cell(run.current["view"]["alarm:userTextLists"]["sheets"], old), new)),
               "importOption": "Override", "expectedTextListNames": [TEXT_LIST]}
    def shown(applied, state):
        texts = entry_texts(state)
        require(new in texts and old not in texts and {"Stopped", "Running"} <= set(texts), "The fresh text-list export does not show %r: %s" % (new, texts))
    run.apply("replace text-list entry %s with %s" % (old, new), "plc_alarm_text_list_import", payload,
              {"effects": [{"action": "plc_alarm_text_list_import", "importOption": "Override", "createTextLists": [], "replaceTextLists": [TEXT_LIST]}]},
              changed=["alarm:userTextLists"], tolerated=lambda category, key: category == "changed" and key == "alarm:textLists", check=shown, restore=restore)


def text_lists(run):
    lists = run.file("text-list-refused.xlsx", text_list_workbook(run.cultures))
    run.refuse("plc_alarm_text_list_import", "an unknown import option", {**lists, "importOption": "Merge", "expectedTextListNames": [TEXT_LIST]}, "InvalidRequest")
    run.refuse("plc_alarm_text_list_import", "a changed hash", {**lists, "expectedSha256": "0" * 64, "importOption": "None", "expectedTextListNames": [TEXT_LIST]}, "ArtifactChanged")
    run.refuse("plc_alarm_text_list_import", "a DAT instead of a workbook", {**run.file("lists.dat", run.exports["classes"][1]), "importOption": "None", "expectedTextListNames": [TEXT_LIST]}, "InvalidArtifact")
    text_list_create(run)
    run.refuse("plc_alarm_text_list_import", "None for an existing text list", {**lists, "importOption": "None", "expectedTextListNames": [TEXT_LIST]}, "ObjectExists")
    text_list_override(run, "Fault", "Fault active")


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
    text_lists(run)


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
    text_list_override(run, "Fault active", "Fault again", restore=True)


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
