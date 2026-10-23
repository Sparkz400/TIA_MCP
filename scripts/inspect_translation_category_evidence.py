"""Read retained native workbooks and installed vendor metadata; never invoke TIA.

This is an independent ZIP/XML oracle for the already retained category-filter
anomaly, not the managed parser or a claim that category semantics are resolved.
"""
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
from zipfile import ZipFile

if not __debug__:
    raise RuntimeError("Evidence verification requires assertions; run Python without -O or -OO.")

ROOT = Path(__file__).resolve().parents[1]
CAPTURE = ROOT / "artifacts/expansion-foundation-20261015/native/translations-03"
HELP = Path("C:/Program Files/Siemens/Automation/Portal V21/Help/en-US/TIAPortalOpennessenUS.zip")
API = Path("C:/Program Files/Siemens/Automation/Portal V21/PublicAPI/V21/net48/Siemens.Engineering.Base.xml")
S = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def artifact(path):
    return {"path": str(path), "sha256": digest(path.read_bytes()), "bytes": path.stat().st_size}


def rows(path):
    with ZipFile(path) as archive:
        assert archive.testzip() is None
        shared = ET.fromstring(archive.read("xl/sharedStrings.xml"))
        strings = []
        for item in shared.findall(S + "si"):
            assert not item.findall(S + "r") and len(item.findall(S + "t")) == 1
            strings.append(item.find(S + "t").text or "")
        result = {}
        for name in archive.namelist():
            if not name.startswith("xl/worksheets/") or not name.endswith(".xml"):
                continue
            sheet = ET.fromstring(archive.read(name))
            for row in sheet.find(S + "sheetData"):
                cells = {}
                for cell in row:
                    assert cell.attrib["t"] == "s"
                    cells[cell.attrib["r"]] = strings[int(cell.find(S + "v").text)]
                number = row.attrib["r"]
                if number == "1":
                    assert [cells[c + number] for c in "ABCDEF"] == ["Category", "ViewPath", "Internal ID", "Substitutions", "de-DE*", "en-GB"]
                    continue
                assert set(cells) == {c + number for c in "ABCDEF"}
                key = cells["C" + number]
                assert key not in result
                result[key] = {"category": cells["A" + number], "viewPath": cells["B" + number], "substitutions": cells["D" + number],
                               "source": cells["E" + number], "target": cells["F" + number]}
        return result


def main(output):
    native = json.loads((CAPTURE / "result.json").read_text(encoding="utf-8-sig"))
    expected_id = "HwcnCommentTextCategoryData"
    assert expected_id in {c["id"] for c in native["translations"]["categories"]}
    workbooks = {}
    for mode in ("filtered-plc", "filtered-category", "filtered-block"):
        item = next(e for e in native["translations"]["exports"] if e["mode"] == mode)
        path = CAPTURE / (mode + ".xlsx")
        assert digest(path.read_bytes()) == item["artifact"]["sha256"]
        workbooks[mode] = {"artifact": artifact(path), "rows": rows(path)}
    baseline = workbooks["filtered-plc"]["rows"]
    selected = workbooks["filtered-category"]["rows"]
    allowed_label = "<HwcnCommentTextCategoryData>"
    expected = {key: row for key, row in baseline.items() if row["category"] == allowed_label}
    unexpected = {key: row for key, row in selected.items() if row["category"] != allowed_label}
    assert len(expected) == 1 and len(selected) == 5 and len(unexpected) == 4 and baseline == selected
    source = CAPTURE / "source-Program.cs"
    helper = source.read_text(encoding="utf-8-sig")
    assert 'project.TextCategories.Find("HwcnCommentTextCategoryData")' in helper
    metadata = ET.parse(API)
    export_members = [m for m in metadata.findall(".//member") if m.attrib.get("name", "").startswith("M:Siemens.Engineering.ProjectBase.ExportProjectTexts(")]
    assert len(export_members) == 2
    help_topics = []
    with ZipFile(HELP) as archive:
        for entry in archive.namelist():
            if entry.endswith(".htm"):
                content = archive.read(entry)
                if b"ExportProjectTexts" in content:
                    help_topics.append({"entry": entry, "sha256": digest(content), "bytes": len(content),
                                        "containsObjectsToExportParameter": b"objectsToExport" in content,
                                        "containsTextCategoryType": b"TextCategory" in content})
    assert help_topics and not any(t["containsObjectsToExportParameter"] or t["containsTextCategoryType"] for t in help_topics)
    result = {
        "kind": "translation-category-filter-investigation", "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "nativeInvoked": False, "nativeImportAllowed": False, "categorySemanticsResolved": False,
        "captureReceipt": artifact(CAPTURE / "result.json"), "frozenHelperSource": artifact(source),
        "frozenHelperContainsSingleCategorySelection": True, "installedMetadata": artifact(API),
        "exportMemberIds": [m.attrib["name"] for m in export_members],
        "installedHelp": artifact(HELP), "installedExportTopics": help_topics,
        "onlineSourcesReferencedByPriorInvestigation": [
            "https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/import/export-of-project-data/project-texts/export-of-project-texts",
            "https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/what-s-new-in-tia-portal-openness"
        ],
        "requestedCategoryId": expected_id, "observedCategoryLabel": allowed_label,
        "baselineRows": len(baseline), "expectedSelectedRows": len(expected), "actualSelectedRows": len(selected),
        "outOfCategoryRows": len(unexpected), "sameKeysAndTextsAsAllCategoriesForPlc": baseline == selected,
        "observedCategoryCounts": dict(Counter(row["category"] for row in selected.values())),
        "outOfCategoryKeys": sorted(unexpected),
        "artifacts": {mode: data["artifact"] for mode, data in workbooks.items()},
        "onlineSourcesFetchedByThisRun": False,
        "conclusion": "The captured single-category PLC request does not satisfy a category intersection. Installed export topics do not explain the five-argument category/object combination. Further admitted read-only probes are required; import remains disabled."
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, ensure_ascii=False)
        stream.write("\n")
    print("Independent retained-capture verification: 1 requested-category row, 4 outside-category rows; no native invocation.")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: inspect_translation_category_evidence.py <new-output-json>")
    main(Path(sys.argv[1]).resolve())
