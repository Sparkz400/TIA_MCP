"""Independent file-only verification of the admitted 14-case category probe.

Reads every worksheet using ZIP/ElementTree, verifies native artifact hashes and
request identity, and emits explicit unqualified read profiles for a second,
managed-reader check. No native process is opened or attached.
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
S = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
REL = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}"
HARDWARE = "HwcnCommentTextCategoryData"
COMMENTS = "BlockCommentCategoryData"
ALARMS = "AlarmClassDisplayNameCategory"
OBSERVED_LABELS = {HARDWARE: "<HwcnCommentTextCategoryData>", COMMENTS: "<BlockCommentCategoryData>", ALARMS: "<AlarmClassDisplayNameCategory>"}
EXPECTED_MODES = ["plc-all", "plc-hardware", "plc-comments", "plc-alarms", "plc-empty-categories", "plc-null-categories",
                  "block-all", "block-hardware", "block-comments", "owner-all", "owner-hardware", "owner-comments", "empty-objects", "null-objects"]


def digest(data): return hashlib.sha256(data).hexdigest()


def artifact(path): return {"path": str(path), "sha256": digest(path.read_bytes()), "bytes": path.stat().st_size}


def dump(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2, ensure_ascii=False)
        stream.write("\n")


def validate_request_matrix(native, admission, progress):
    """Validate the retained request scope before interpreting any workbook rows.

    These receipts identify named selections, not live object identity or freshness.
    The all-category baseline must actually request the complete captured inventory.
    """
    assert native["translations"] == progress and native["completed"] is True
    assert native["readOnly"] is True and native["saved"] is False and native["onlineConnected"] is False
    assert native["beforeModified"] is False and native["afterModified"] is False
    assert native["processId"] == admission["processId"] == 46936 and native["projectPath"] == admission["projectPath"]
    assert native["plcName"] == admission["plcName"] and admission["mode"] == native["mode"] == "translations-category-read"
    assert progress["completed"] is True and progress["readOnly"] is True and progress["automaticReplay"] is False
    assert progress["nativeImportExecuted"] is False and progress["nativeImportAllowed"] is False
    assert progress["sourceCulture"] == "de-DE" and progress["targetCulture"] == "en-GB"
    inventory = [c["identifier"] for c in progress["categories"]]
    assert 1 <= len(inventory) <= 64 and len(set(inventory)) == len(inventory)
    assert all(isinstance(c, str) and c.strip() for c in inventory) and set(OBSERVED_LABELS).issubset(inventory)
    exports = progress["exports"]
    assert [e["mode"] for e in exports] == EXPECTED_MODES
    owners = {
        "plc": ("plc:" + native["plcName"], "Siemens.Engineering.SW.PlcSoftware"),
        "block": ("block:TiaMcp_IsEnabled", "Siemens.Engineering.SW.Blocks.FC"),
        "owner": ("deviceItem:" + native["hardwareOwner"]["ownerName"], "Siemens.Engineering.HW.DeviceItemImpl")}
    for request in exports:
        mode = request["mode"]
        assert request["afterModified"] is False and request["automaticReplay"] is False
        assert request["sourceCulture"] == "de-DE" and request["targetCulture"] == "en-GB"
        start, finish = (datetime.fromisoformat(request[k]) for k in ("startedAtUtc", "finishedAtUtc"))
        assert start.tzinfo is not None and finish.tzinfo is not None and start <= finish
        assert datetime.fromisoformat(native["startedAtUtc"]) <= start <= finish <= datetime.fromisoformat(native["finishedAtUtc"])
        if mode.endswith("-categories"):
            expected = None if "-null-" in mode else []
        elif mode.endswith("-hardware"): expected = [HARDWARE]
        elif mode.endswith("-comments"): expected = [COMMENTS]
        elif mode.endswith("-alarms"): expected = [ALARMS]
        else: expected = inventory
        assert request["categoryIds"] == expected
        assert request["categoryArgument"] == ("null" if expected is None else "empty" if not expected else "explicit")
        if mode in ("empty-objects", "null-objects"):
            null = mode == "null-objects"
            assert request["objectArgument"] == request["objectSelection"] == ("null" if null else "empty")
            assert request["objectCount"] == (None if null else 0) and request["objectRuntimeTypes"] == (None if null else [])
        else:
            selection, runtime_type = owners[mode.split("-", 1)[0]]
            assert request["objectArgument"] == "explicit" and request["objectCount"] == 1
            assert request["objectSelection"] == selection and request["objectRuntimeTypes"] == [runtime_type]
        expected_failure = mode in ("plc-empty-categories", "plc-null-categories", "empty-objects", "null-objects")
        assert request["state"] == ("failed" if expected_failure else "returned")
    assert all(datetime.fromisoformat(a["finishedAtUtc"]) <= datetime.fromisoformat(b["startedAtUtc"]) for a, b in zip(exports, exports[1:]))


def decode(path):
    with ZipFile(path) as archive:
        assert archive.testzip() is None and len(archive.namelist()) == len(set(archive.namelist())) == 9
        parts = {name: ET.fromstring(archive.read(name)) for name in archive.namelist()}
        strings = []
        for item in parts["xl/sharedStrings.xml"].findall(S + "si"):
            assert len(item.findall(S + "t")) == 1 and len(list(item)) == 1
            strings.append(item.find(S + "t").text or "")
        links = {r.attrib["Id"]: r.attrib["Target"].lstrip("/") for r in parts["xl/_rels/workbook.xml.rels"]}
        records, sheets, profile = {}, [], []
        for sheet in parts["xl/workbook.xml"].find(S + "sheets"):
            part = links[sheet.attrib[REL + "id"]]
            data = parts[part].find(S + "sheetData")
            assert data is not None
            for index, row in enumerate(data):
                cells, number = {}, row.attrib["r"]
                for cell in row:
                    assert cell.attrib["t"] == "s" and len(list(cell)) == 1 and cell[0].tag == S + "v"
                    cells[cell.attrib["r"]] = strings[int(cell[0].text)]
                if index == 0:
                    assert number == "1" and len(cells) == 22
                    assert [cells[chr(65+i) + "1"] for i in range(22)] == ["Category", "ViewPath", "Internal ID", "Substitutions", "de-DE*", "en-GB"] + [f'ref="{i}"' for i in range(16)]
                    continue
                assert set(cells) == {c + number for c in "ABCDEF"}
                key, category = cells["C" + number], cells["A" + number]
                assert key and key not in records and cells["D" + number] == ""
                records[key] = {"nativeKey": key, "category": category, "viewPath": cells["B" + number], "substitutions": cells["D" + number],
                                "source": cells["E" + number], "target": cells["F" + number]}
                profile.append({"nativeKey": key, "category": category, "keyCell": {"part": part, "cell": "C" + number},
                                "sourceCell": {"part": part, "cell": "E" + number}, "targets": [{"culture": "en-GB", "location": {"part": part, "cell": "F" + number},
                                "allowClear": False, "allowCreateMissing": False, "maxUtf16Length": None}], "tokenGrammar": "unknown"})
            sheets.append({"name": sheet.attrib["name"], "part": part, "dataRows": len(data)-1})
        assert [s["name"] for s in sheets] == ["User Texts", "System Texts"] and sheets[1]["dataRows"] == 0
        return records, sheets, profile


def main(source, output):
    if output.exists(): raise ValueError("A fresh output directory is required.")
    native = json.loads((source / "result.json").read_text(encoding="utf-8-sig"))
    admission = json.loads((source / "admission.json").read_text(encoding="utf-8-sig"))
    progress = json.loads((source / "category-probe-progress.json").read_text(encoding="utf-8-sig"))
    validate_request_matrix(native, admission, progress)
    assert digest((source / "ExpansionEvidence.exe").read_bytes()) == admission["binarySha256"]
    exports = progress["exports"]
    output.mkdir(parents=True)
    decoded, summaries, cases = {}, [], []
    receipt_hash = digest((source / "result.json").read_bytes())
    for request in exports:
        mode = request["mode"]
        summary = {k: request.get(k) for k in ["mode", "categoryArgument", "categoryIds", "objectSelection", "objectArgument", "objectCount", "objectRuntimeTypes", "state"]}
        path = source / (mode + ".xlsx")
        if request["state"] == "failed":
            assert mode in ["plc-empty-categories", "plc-null-categories", "empty-objects", "null-objects"] and not path.exists() and "partialArtifact" not in request
            summary.update({"failureType": request["failureType"], "message": request["message"], "artifactCreated": False})
        else:
            assert request["state"] == "returned" and artifact(path)["sha256"] == request["artifact"]["sha256"] and path.stat().st_size == request["artifact"]["bytes"]
            records, sheets, rows = decode(path)
            decoded[mode] = records
            profile_path = output / (mode + ".profile.json")
            dump(profile_path, {"id": "native-category-01:" + mode, "kind": "unqualified-explicit", "sourceCulture": "de-DE", "rows": rows, "allowSharedStringToInline": False})
            cases.append({"mode": mode, "workbookPath": str(path), "workbookSha256": artifact(path)["sha256"], "profilePath": str(profile_path),
                          "identity": {"object": {"projectPath": native["projectPath"], "plcPath": None, "scopePath": None, "objectPath": native["projectPath"], "kind": "project"},
                                       "snapshot": {"observationId": "native-category-01:" + mode, "sourceSha256": receipt_hash, "tiaVersion": 21}, "contentSha256": None}, "rows": list(records.values())})
            summary.update({"artifact": artifact(path), "rows": len(records), "sheets": sheets, "categoryCounts": dict(Counter(r["category"] for r in records.values()))})
        summaries.append(summary)
    assert len(decoded) == 10 and sum(len(r) for r in decoded.values()) == 28
    for name in ["plc-hardware", "plc-comments", "plc-alarms", "owner-all", "owner-hardware", "owner-comments"]: assert decoded[name] == decoded["plc-all"]
    for name in ["block-all", "block-hardware", "block-comments"]: assert decoded[name] == {}
    sentinel = [r for r in decoded["plc-all"].values() if "\\MilestoneWatch\\" in r["viewPath"]]
    assert len(sentinel) == 1 and sentinel[0]["source"] == "Freigabestatus {0}" and sentinel[0]["target"] == "Enable status {0}" and sentinel[0]["category"] == "<BlockCommentCategoryData>"
    comparisons = []
    for mode, base, identifier, expected_count, outside_count in [("plc-hardware", "plc-all", HARDWARE, 1, 3), ("plc-comments", "plc-all", COMMENTS, 3, 1),
        ("plc-alarms", "plc-all", ALARMS, 0, 4), ("block-hardware", "block-all", HARDWARE, 0, 0), ("block-comments", "block-all", COMMENTS, 0, 0),
        ("owner-hardware", "owner-all", HARDWARE, 1, 3), ("owner-comments", "owner-all", COMMENTS, 3, 1)]:
        request = next(e for e in exports if e["mode"] == mode); control = next(e for e in exports if e["mode"] == base)
        assert request["categoryIds"] == [identifier] and request["objectSelection"] == control["objectSelection"] and request["objectRuntimeTypes"] == control["objectRuntimeTypes"]
        expected = {k:v for k,v in decoded[base].items() if v["category"] == OBSERVED_LABELS[identifier]}
        outside = [k for k,v in decoded[mode].items() if v["category"] != OBSERVED_LABELS[identifier]]
        assert len(expected) == expected_count and len(outside) == outside_count
        comparisons.append({"baselineMode": base, "selectedMode": mode, "requestedCategoryIds": [identifier], "expectedRows": expected_count,
                            "actualRows": len(decoded[mode]), "outOfCategoryRows": outside_count, "missingRows": len(set(expected)-set(decoded[mode])),
                            "sameNativeObjectSelectionInRetainedRequest": True, "restrictiveFilteringDemonstrated": False, "matchesIntersection": expected == decoded[mode]})
    prior_profile = ROOT / "fixtures/translations/native-tia21-read-20261015/capture-03/legacy.profile.json"
    prior_categories = {r["category"] for r in json.loads(prior_profile.read_text(encoding="utf-8-sig"))["rows"]}
    assert set(OBSERVED_LABELS.values()).issubset(prior_categories)
    bindings = [{"identifier": identifier, "exportLabel": label} for identifier, label in OBSERVED_LABELS.items()]
    dump(output / "managed-cases.json", {"cases": cases, "comparisons": comparisons, "categoryBindings": bindings})
    result = {"kind": "independent-native-category-probe-verification", "allChecksPassed": True, "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
              "verifierInvokedNative": False, "nativeProbePreviouslyExecuted": True, "nativeImportExecuted": False, "nativeImportAllowed": False,
              "sourceReceipt": artifact(source / "result.json"), "admission": artifact(source / "admission.json"), "binary": artifact(source / "ExpansionEvidence.exe"),
              "frozenSources": [artifact(source / n) for n in ["source-Program.cs", "source-TranslationCategoryProbe.cs", "source-ExpansionEvidence.csproj"]],
              "processId": native["processId"], "projectPath": native["projectPath"], "hardware": native["hardwareOwner"], "sequentialNativeCallsVerified": True,
              "completeCategoryInventoryRequestsVerified": True, "requestObjectTypesCountsAndNamesVerified": True,
              "liveObjectIdentityVerified": False, "liveProjectFreshnessVerified": False,
              "beforeModified": False, "afterModified": False, "projectFileHashAvailable": False,
              "successfulExports": 10, "refusedRequests": 4, "rowsIndependentlyDecoded": 28, "results": summaries, "comparisons": comparisons,
              "watchCommentSentinel": sentinel[0], "thirdLanguageExported": False,
              "categoryLabelEvidence": artifact(prior_profile),
              "conclusions": ["PLC and DeviceItem category selections returned the same complete four-row object scope, including disjoint-category rows.",
                              "An empty selected block produces three empty exports; no positive category filtering can be inferred.",
                              "Null and empty category and object arguments are all explicitly refused on this fixture.",
                              "Native imports remain disabled: category restriction is not established, workbook write/empty-cell semantics and native readback/reopen/recovery are unqualified."]}
    dump(output / "verification.json", result)
    print("Verified 14 sequential native requests: 10 exports / 28 decoded rows, 4 refusals; category leakage persists for PLC and DeviceItem.")


if __name__ == "__main__":
    if len(sys.argv) != 3: raise SystemExit("Usage: verify_translation_category_probe.py <native-capture-directory> <new-output-directory>")
    main(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())
