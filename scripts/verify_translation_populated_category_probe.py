"""Independent, file-only verifier for the bounded Main OB1 category contrast.

No Siemens calls, imports or native profile qualification. Empty Main controls
remain inconclusive. Explicit exception checks remain active under Python -O.
"""
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
from zipfile import ZipFile

S = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
R = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}"
P = "{http://schemas.openxmlformats.org/package/2006/relationships}"
MODE = "translations-populated-category-read"
HARDWARE, COMMENTS, ALARMS = "HwcnCommentTextCategoryData", "BlockCommentCategoryData", "AlarmClassDisplayNameCategory"
LABELS = {HARDWARE: "<HwcnCommentTextCategoryData>", COMMENTS: "<BlockCommentCategoryData>", ALARMS: "<AlarmClassDisplayNameCategory>"}
MODES = ["plc-all-before", "main-all-before", "main-comments", "main-hardware", "main-alarms", "main-all-after", "plc-all-after"]
PARTS = {"xl/workbook.xml", "_rels/.rels", "xl/styles.xml", "xl/_rels/workbook.xml.rels", "docProps/custom.xml",
         "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml", "xl/sharedStrings.xml", "[Content_Types].xml"}
MAX_COMPRESSED_BYTES = 8 * 1024 * 1024
MAX_EXPANDED_BYTES = 32 * 1024 * 1024
MAX_XML_BYTES = 8 * 1024 * 1024
MAX_XML_DEPTH = 64
MAX_XML_NODES = 250000


def require(condition, message):
    if not condition: raise ValueError(message)


def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def artifact(path): return {"path": str(path), "sha256": digest(path), "bytes": path.stat().st_size}


def dump(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write("\n")


def timestamp(value):
    parsed = datetime.fromisoformat(value)
    require(parsed.tzinfo is not None, "Naive request timestamp")
    return parsed


def validate_requests(native, admission, progress):
    require(native["translations"] == progress, "Result/progress receipt mismatch")
    require(native["completed"] is True and progress["completed"] is True, "Incomplete capture")
    require(native["mode"] == admission["mode"] == MODE, "Admitted mode mismatch")
    require(type(native["processId"]) is int and native["processId"] > 0 and native["processId"] == admission["processId"], "Admitted process mismatch")
    require(native["projectPath"] == admission["projectPath"] and native["plcName"] == admission["plcName"] == progress["plcName"], "Admitted owner mismatch")
    require(native["hardwareOwner"]["ownerName"] == progress["hardwareOwnerName"], "Hardware owner mismatch")
    require(native["readOnly"] is True and native["saved"] is False and native["onlineConnected"] is False and
            native["beforeModified"] is False and native["afterModified"] is False, "Capture changed project or went online")
    require(progress["kind"] == "translation-populated-category-probe" and progress["readOnly"] is True and progress["automaticReplay"] is False and
            progress["nativeImportExecuted"] is False and progress["nativeImportAllowed"] is False and progress["nativeQualified"] is False, "Unsafe qualification or mutation flag")
    require(progress["sourceCulture"] == "de-DE" and progress["targetCulture"] == "en-GB" and progress["maximumExportCalls"] == 7, "Language/call bound mismatch")
    require(isinstance(progress["projectName"], str) and progress["projectName"] and native["projectPath"].replace("\\", "/").endswith("/" + progress["projectName"] + ".ap21"), "Project name/path mismatch")
    block = progress["selectedBlock"]
    require(block["name"] == "Main" and block["number"] == 1 and block["runtimeType"] == "Siemens.Engineering.SW.Blocks.OB" and
            block["selectionPath"] == "plc:" + native["plcName"] + "/BlockGroup/Blocks/Main" and
            block["parentRuntimeType"] == "Siemens.Engineering.SW.Blocks.PlcBlockSystemGroup" and isinstance(block["parentName"], str) and block["parentName"], "Exact root Main OB1 was not resolved")
    inventory = [c["identifier"] for c in progress["categories"]]
    require(1 <= len(inventory) <= 64 and len(set(inventory)) == len(inventory) and all(isinstance(c, str) and c for c in inventory) and set(LABELS) <= set(inventory), "Category inventory mismatch")
    populated = progress["populatedMainControlPassed"]
    require(type(populated) is bool, "Missing populated gate decision")
    expected_modes = MODES if populated else MODES[:2]
    exports = progress["exports"]
    require([e["mode"] for e in exports] == expected_modes, "Probe order/count or empty-control stop mismatch")
    if not populated:
        require(progress["outcome"] == "inconclusive-main-control-not-populated-or-not-preserved" and progress["restrictedExportsSkipped"] is True, "Empty control became a pass")
    else:
        require(progress["outcome"] in ("exports-completed-awaiting-independent-comparison", "baseline-drift-refused"), "Unknown populated outcome")
    before, after = timestamp(native["startedAtUtc"]), timestamp(native["finishedAtUtc"])
    last = before
    for request in exports:
        mode = request["mode"]
        expected = {"main-comments": [COMMENTS], "main-hardware": [HARDWARE], "main-alarms": [ALARMS]}.get(mode, inventory)
        selection, kind = ("plc:" + native["plcName"], "Siemens.Engineering.SW.PlcSoftware") if mode.startswith("plc-") else ("block:Main:OB:1", block["runtimeType"])
        require(request["categoryArgument"] == "explicit" and request["categoryIds"] == expected, "Category request mismatch")
        require(request["objectArgument"] == "explicit" and request["objectCount"] == 1 and request["objectSelection"] == selection and request["objectRuntimeTypes"] == [kind], "Native object selection mismatch")
        require(request["sourceCulture"] == "de-DE" and request["targetCulture"] == "en-GB" and request["automaticReplay"] is False and
                request["beforeModified"] is False and request["afterModified"] is False and request["state"] == "returned", "Request failed, replayed or changed project")
        start, finish = timestamp(request["startedAtUtc"]), timestamp(request["finishedAtUtc"])
        require(before <= last <= start <= finish <= after, "Requests overlap or leave capture interval")
        last = finish
    return populated


class BoundedXmlTree(ET.TreeBuilder):
    """Reject DTDs and oversized trees while parsing, independent of XML encoding."""

    def __init__(self):
        super().__init__()
        self.depth = 0
        self.nodes = 0

    def start(self, tag, attrs):
        self.depth += 1
        self.nodes += 1
        require(self.depth <= MAX_XML_DEPTH, "XML depth budget exceeded")
        require(self.nodes <= MAX_XML_NODES, "XML node budget exceeded")
        return super().start(tag, attrs)

    def end(self, tag):
        element = super().end(tag)
        self.depth -= 1
        return element

    def doctype(self, name, pubid, system):
        raise ValueError("XML DTDs and entities are not accepted")


def decode(path):
    require(path.stat().st_size <= MAX_COMPRESSED_BYTES, "Oversized workbook")
    with ZipFile(path) as archive:
        entries = archive.infolist()
        require(len(entries) == len(PARTS) and {e.filename for e in entries} == PARTS, "Unrecognized ZIP layout")
        # Inspect the directory before opening any compressed stream. testzip()
        # would expand the entire archive before applying this bound.
        require(all(e.file_size >= 0 for e in entries) and sum(e.file_size for e in entries) <= MAX_EXPANDED_BYTES,
                "Expanded workbook budget exceeded")
        require(all(e.file_size <= MAX_XML_BYTES for e in entries), "XML part byte budget exceeded")
        parts = {}
        expanded = 0
        for entry in entries:
            # ZipExtFile verifies CRC at EOF. Read at most the limit plus one,
            # then check observed bytes as well as the directory declaration.
            with archive.open(entry) as stream:
                raw = stream.read(MAX_XML_BYTES + 1)
            expanded += len(raw)
            require(len(raw) <= MAX_XML_BYTES, "XML part byte budget exceeded")
            require(len(raw) == entry.file_size and expanded <= MAX_EXPANDED_BYTES, "Expanded workbook size mismatch")
            parts[entry.filename] = ET.fromstring(raw, parser=ET.XMLParser(target=BoundedXmlTree()))
    shared = parts["xl/sharedStrings.xml"]
    require(shared.tag == S + "sst", "Unknown string table")
    strings = []
    for item in shared:
        require(item.tag == S + "si" and len(item) == 1 and item[0].tag == S + "t" and len(item[0]) == 0 and
                not (item.text or "").strip() and not (item[0].tail or "").strip(), "Unsupported rich or mixed shared string")
        strings.append(item[0].text or "")
    links = {}
    for rel in parts["xl/_rels/workbook.xml.rels"]:
        require(rel.tag == P + "Relationship" and rel.attrib["Id"] not in links, "Unknown/duplicate relationship")
        links[rel.attrib["Id"]] = rel
    workbook = parts["xl/workbook.xml"]
    require(workbook.tag == S + "workbook" and len(workbook.findall(S + "sheets")) == 1, "Unknown workbook")
    sheets = list(workbook.find(S + "sheets"))
    require(len(sheets) == 2, "Both captured worksheets are required")
    rows, profiles, sheet_summaries = {}, [], []
    for sheet_index, sheet in enumerate(sheets):
        name, part = ["User Texts", "System Texts"][sheet_index], f"xl/worksheets/sheet{sheet_index + 1}.xml"
        require(sheet.tag == S + "sheet" and sheet.attrib["name"] == name and sheet.attrib.get("state", "visible") == "visible", "Hidden/renamed/reordered sheet")
        rel = links[sheet.attrib[R + "id"]]
        require(rel.attrib["Target"] == "/" + part and "TargetMode" not in rel.attrib and rel.attrib["Type"] == R[1:-1] + "/worksheet", "Wrong sheet relationship")
        root = parts[part]
        require(root.tag == S + "worksheet" and len(root.findall(S + "sheetData")) == 1, "Missing/duplicate sheet data")
        data = list(root.find(S + "sheetData"))
        require(1 <= len(data) <= 10001, "Missing header or excessive rows")
        for index, row in enumerate(data):
            number = str(index + 1)
            require(row.tag == S + "row" and row.attrib["r"] == number, "Missing/duplicate/reordered row")
            cells = {}
            for cell in row:
                require(cell.tag == S + "c" and cell.attrib["t"] == "s" and len(cell) == 1 and cell[0].tag == S + "v" and
                        cell.attrib["r"] not in cells and (cell[0].text or "").isdigit(), "Unsupported/duplicate cell")
                position = int(cell[0].text)
                require(0 <= position < len(strings), "Invalid shared string index")
                cells[cell.attrib["r"]] = strings[position]
            width = 22 if index == 0 else 6
            require(set(cells) == {chr(65 + i) + number for i in range(width)}, "Incomplete row")
            if index == 0:
                require([cells[chr(65 + i) + "1"] for i in range(22)] == ["Category", "ViewPath", "Internal ID", "Substitutions", "de-DE*", "en-GB"] + [f'ref="{i}"' for i in range(16)], "Header/language mismatch")
                continue
            key = cells["C" + number]
            require(key and key not in rows, "Missing/duplicate native key")
            rows[key] = {"nativeKey": key, "category": cells["A" + number], "viewPath": cells["B" + number], "substitutions": cells["D" + number],
                         "source": cells["E" + number], "target": cells["F" + number], "sheet": name, "part": part}
            profiles.append({"nativeKey": key, "category": cells["A" + number], "keyCell": {"part": part, "cell": "C" + number},
                             "sourceCell": {"part": part, "cell": "E" + number}, "targets": [{"culture": "en-GB", "location": {"part": part, "cell": "F" + number},
                             "allowClear": False, "allowCreateMissing": False, "maxUtf16Length": None}], "tokenGrammar": "unknown"})
        sheet_summaries.append({"name": name, "part": part, "rows": len(data) - 1})
    return rows, profiles, sheet_summaries


def populated_control(plc, main, project_name, plc_name):
    prefix = project_name + "\\" + plc_name + "\\Programmbausteine\\Main\\"
    sentinels = {"Remanence": "=True, if remanent data are available", "Initial_Call": "Initial call of this OB"}
    return bool(main) and all(k in plc and r == plc[k] and r["viewPath"].startswith(prefix) for k, r in main.items()) and all(
        any(r["viewPath"] == prefix + tail and r["category"] == LABELS[COMMENTS] and r["source"] == source for r in main.values()) for tail, source in sentinels.items())


def compare(baseline, selected, identifier):
    expected = {k: r for k, r in baseline.items() if r["category"] == LABELS[identifier]}
    return {"expectedRows": len(expected), "actualRows": len(selected), "outOfCategoryRows": sum(r["category"] != LABELS[identifier] for r in selected.values()),
            "missingKeys": sorted(set(expected) - set(selected)), "unexpectedKeys": sorted(set(selected) - set(expected)),
            "changedKeys": sorted(k for k in set(expected) & set(selected) if expected[k] != selected[k]),
            "matchesIntersection": expected == selected,
            "restrictiveFilteringDemonstrated": bool(expected) and len(expected) < len(baseline) and expected == selected,
            "nonemptyBaselineExcludedByDisjointCategory": bool(baseline) and not expected and not selected,
            "nativeQualified": False, "nativeImportAllowed": False}


def main(source, output):
    require(not output.exists(), "Fresh verification directory required")
    native = json.loads((source / "result.json").read_text(encoding="utf-8-sig"))
    admission = json.loads((source / "admission.json").read_text(encoding="utf-8-sig"))
    progress = json.loads((source / "populated-category-probe-progress.json").read_text(encoding="utf-8-sig"))
    populated = validate_requests(native, admission, progress)
    require(digest(source / "ExpansionEvidence.exe") == admission["binarySha256"], "Admitted binary bytes differ")
    api = {Path(item["path"]).name: item["sha256"] for item in native["api"]}
    require(len(native["api"]) == 3 and api == admission["apiSha256"] and set(api) == {"Siemens.Engineering.Base.dll", "Siemens.Engineering.Step7.dll", "Siemens.Engineering.TestSuite.dll"}, "Recorded API identities differ from admission")
    required_sources = {"Program.cs", "TranslationPopulatedCategoryProbe.cs", "TranslationCategoryWorkbook.cs", "ExpansionEvidence.csproj", "LibraryJournalStore.cs"}
    require(required_sources <= set(admission["sourceSha256"]), "Missing reviewed source hashes")
    frozen_sources = []
    for name, expected in admission["sourceSha256"].items():
        require(Path(name).name == name and digest(source / ("source-" + name)) == expected, "Admitted source bytes differ")
        frozen_sources.append(artifact(source / ("source-" + name)))
    decoded, summaries, profiles = {}, [], {}
    for request in progress["exports"]:
        mode = request["mode"]
        path = source / (mode + ".xlsx")
        require(request["artifact"]["path"] == str(path) and artifact(path) == request["artifact"], "Export path/size/hash differs")
        records, profile, sheets = decode(path)
        require(request["decodedRows"] == len(records), "Helper and independent row counts differ")
        decoded[mode], profiles[mode] = records, profile
        summaries.append({"mode": mode, "artifact": artifact(path), "rows": len(records), "sheets": sheets, "categories": dict(Counter(r["category"] for r in records.values()))})
    actual_gate = populated_control(decoded["plc-all-before"], decoded["main-all-before"], progress["projectName"], progress["plcName"])
    require(actual_gate == populated, "Helper and independent populated gate differ")
    comparisons = []
    if populated:
        main_unchanged = decoded["main-all-before"] == decoded["main-all-after"]
        plc_unchanged = decoded["plc-all-before"] == decoded["plc-all-after"]
        require(main_unchanged == progress["mainBaselineUnchanged"] and plc_unchanged == progress["plcBaselineUnchanged"], "Helper and independent baseline comparison differ")
        require((main_unchanged and plc_unchanged) == (progress["outcome"] == "exports-completed-awaiting-independent-comparison"), "Baseline drift was not refused")
        for mode, identifier in [("main-comments", COMMENTS), ("main-hardware", HARDWARE), ("main-alarms", ALARMS)]:
            comparisons.append({"baselineMode": "main-all-before", "selectedMode": mode, "requestedCategoryIds": [identifier],
                                "sameNativeSelectionInRetainedRequest": True, **compare(decoded["main-all-before"], decoded[mode], identifier)})
    else:
        main_unchanged, plc_unchanged = None, None
        require(not any((source / (mode + ".xlsx")).exists() for mode in MODES[2:]), "Restricted/after artifacts exist despite refused gate")
    output.mkdir(parents=True)
    cases = []
    for mode, rows in decoded.items():
        profile_path = output / (mode + ".profile.json")
        dump(profile_path, {"id": source.name + ":" + mode, "kind": "unqualified-explicit", "sourceCulture": "de-DE", "rows": profiles[mode], "allowSharedStringToInline": False})
        cases.append({"mode": mode, "workbookPath": str(source / (mode + ".xlsx")), "workbookSha256": digest(source / (mode + ".xlsx")), "profilePath": str(profile_path),
                      "identity": {"object": {"projectPath": native["projectPath"], "plcPath": None, "scopePath": None, "objectPath": native["projectPath"], "kind": "project"},
                                   "snapshot": {"observationId": source.name + ":" + mode, "sourceSha256": digest(source / "result.json"), "tiaVersion": 21}, "contentSha256": None}, "rows": list(rows.values())})
    dump(output / "managed-cases.json", {"cases": cases, "comparisons": comparisons, "categoryBindings": [{"identifier": k, "exportLabel": v} for k, v in LABELS.items()]})
    outcome = "inconclusive-main-control" if not populated else "baseline-drift-refused" if not (main_unchanged and plc_unchanged) else "category-intersection-observed" if all(c["matchesIntersection"] for c in comparisons) else "category-mismatch-observed"
    dump(output / "verification.json", {"kind": "independent-populated-category-probe-verification", "allEvidenceChecksPassed": True, "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
         "verifierInvokedNative": False, "nativeImportAllowed": False, "nativeQualified": False, "outcome": outcome, "populatedControlPassed": populated,
         "sourceReceipt": artifact(source / "result.json"), "admission": artifact(source / "admission.json"), "binary": artifact(source / "ExpansionEvidence.exe"), "frozenSources": frozen_sources,
         "processId": native["processId"], "projectPath": native["projectPath"], "selectedBlock": progress["selectedBlock"], "mainBaselineUnchanged": main_unchanged,
         "plcBaselineUnchanged": plc_unchanged, "sameObjectSelectionVerifiedFromRetainedRequestOnly": True, "liveObjectIdentityVerified": False, "liveProjectFreshnessVerified": False,
         "exports": summaries, "comparisons": comparisons})
    print(f"Verified {len(decoded)} retained sequential exports: {outcome}; native imports remain disabled.")


if __name__ == "__main__":
    if len(sys.argv) != 3: raise SystemExit("Usage: verify_translation_populated_category_probe.py <capture-directory> <fresh-output-directory>")
    main(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())
