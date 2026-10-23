"""Read-only structural oracle for captured native watch/force exports.

This reads supplied result/XML artifacts only, never the source project or TIA.
It checks ordered typed readback against independently parsed XML and preserves
absent versus explicit-empty fields. It does not infer active online actions.
"""
from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
from xml.parsers import expat

PREFIX = "SW.WatchAndForceTables."
RUNTIME_PREFIX = "Siemens.Engineering." + PREFIX
ROW_TYPES = {
    "PlcWatchTableEntry": ("watch", ["Name", "Address", "DisplayFormat", "ModifyIntention", "ModifyTrigger", "ModifyValue", "MonitorTrigger"]),
    "PlcForceTableEntry": ("force", ["Name", "Address", "DisplayFormat", "ForceIntention", "ForceValue", "MonitorTrigger"]),
    "PlcTableCommentEntry": ("comment", []),
}
LIMIT = 16 * 1024 * 1024


class EvidenceMismatch(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise EvidenceMismatch(message)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def read_bounded(path):
    require(path.stat().st_size <= LIMIT, f"Artifact exceeds {LIMIT} bytes: {path.name}")
    return path.read_bytes()


def unique_attributes(element):
    if element is None:
        return {}
    require(len({child.tag for child in element}) == len(element), "Duplicate XML property")
    require(all(len(child) == 0 for child in element), "Unexpected nested XML property")
    return {child.tag: child.text or "" for child in element}


def parse_xml(data):
    try:
        data.decode("utf-8-sig")
    except UnicodeDecodeError as error:
        raise EvidenceMismatch("Only the observed UTF-8 export profile is supported") from error
    require(b"<!DOCTYPE" not in data.upper() and b"<!ENTITY" not in data.upper(), "DTD/entity declarations are not accepted")
    try:
        root = ET.fromstring(data)
    except ET.ParseError as error:
        raise EvidenceMismatch("Malformed captured XML") from error
    require(root.tag == "Document", "Unknown export document root/profile")
    require(len(list(root.iter())) <= 100000, "XML node bound exceeded")
    ids = [element.attrib["ID"] for element in root.iter() if "ID" in element.attrib]
    require(len(ids) == len(set(ids)), "Duplicate native XML object ID")
    return root


def without_created_text(data):
    """Remove only bytes inside Document/DocumentInfo/Created; keep every other byte."""
    root = parse_xml(data)
    created = root.findall("DocumentInfo/Created")
    require(len(created) == 1 and len(created[0]) == 0 and not created[0].attrib and bool(created[0].text), "One plain DocumentInfo/Created timestamp is required")
    try:
        datetime.fromisoformat(created[0].text.replace("Z", "+00:00"))
    except ValueError as error:
        raise EvidenceMismatch("Invalid export timestamp") from error
    parser = expat.ParserCreate()
    stack, spans = [], []
    start = None

    def opened(name, attributes):
        nonlocal start
        stack.append(name)
        if stack == ["Document", "DocumentInfo", "Created"]:
            start = data.index(b">", parser.CurrentByteIndex) + 1

    def closed(name):
        if stack == ["Document", "DocumentInfo", "Created"]:
            spans.append((start, parser.CurrentByteIndex))
        stack.pop()

    parser.StartElementHandler, parser.EndElementHandler = opened, closed
    parser.Parse(data, True)
    require(len(spans) == 1, "Ambiguous timestamp byte span")
    first, last = spans[0]
    return data[:first] + b"__CAPTURE_TIME__" + data[last:]


def readback_attributes(capture):
    attributes = capture.get("attributes")
    require(isinstance(attributes, list), "Missing native dynamic attribute capture")
    require(len({item.get("name") for item in attributes}) == len(attributes), "Duplicate native dynamic attribute")
    require(all("value" in item and "readFailure" not in item for item in attributes), "Native attribute read failed")
    return {item["name"]: item["value"] for item in attributes}


def comments(row):
    result = []
    texts = row.findall("ObjectList/MultilingualText")
    require(len(texts) == 1 and texts[0].get("CompositionName") == "Comment", "Each observed row profile requires one Comment composition")
    items = texts[0].findall("ObjectList/MultilingualTextItem")
    require(len(items) <= 128, "Comment culture bound exceeded")
    for item in items:
        require(item.get("CompositionName") == "Items", "Unexpected multilingual item composition")
        attributes = unique_attributes(item.find("AttributeList"))
        require(set(attributes) == {"Culture", "Text"} and bool(attributes["Culture"]), "Comment must retain explicit culture and text, including empty text")
        result.append({"culture": attributes["Culture"], "text": attributes["Text"]})
    require(len({item["culture"] for item in result}) == len(result), "Duplicate comment culture")
    return result


def inspect_table(capture, data):
    root = parse_xml(data)
    expected_table = PREFIX + ("PlcWatchTable" if capture["kind"] == "watch" else "PlcForceTable")
    tables = [element for element in root if element.tag.startswith(PREFIX)]
    require(len(tables) == 1 and tables[0].tag == expected_table, "Native table kind differs from XML")
    table = tables[0]
    table_attributes = unique_attributes(table.find("AttributeList"))
    require(table_attributes.get("Name") == capture["name"], "Native table name differs from XML")
    dynamic = readback_attributes(capture)
    require(dynamic.get("Name") == capture["name"], "Native table name differs between typed/dynamic capture")
    require(dynamic.get("IsConsistent") == str(capture["isConsistent"]), "Native table consistency differs between captured views")
    rows = list(table.find("ObjectList"))
    require(len(rows) == len(capture["entries"]) <= 200, "Ordered row counts differ or exceed capture bound")
    observed = []
    for index, (row, native) in enumerate(zip(rows, capture["entries"])):
        runtime = native.get("runtimeType", "")
        require(runtime.startswith(RUNTIME_PREFIX), f"Unknown runtime row type at {index}")
        suffix = runtime.removeprefix(RUNTIME_PREFIX)
        require(suffix in ROW_TYPES and row.tag == PREFIX + suffix, f"Ordered runtime/XML row kind mismatch at {index}")
        kind, fields = ROW_TYPES[suffix]
        require(kind in (capture["kind"], "comment"), "Row belongs to another table family")
        require(row.get("CompositionName") == "Entries", "Row is not in the native Entries composition")
        properties = unique_attributes(row.find("AttributeList"))
        attrs = readback_attributes(native)
        field_observations = []
        for field in fields:
            require(field in native and isinstance(native[field], str), f"Typed native property missing at row {index}: {field}")
            require(attrs.get(field) == native[field], f"Typed/dynamic native property mismatch at row {index}: {field}")
            present = field in properties
            if present:
                require(properties[field] == native[field], f"Native/XML property mismatch at row {index}: {field}")
            # Absence is retained. In particular intention flags cannot be inferred from stored values.
            field_observations.append({"field": field, "nativeValue": native[field], "xmlState": "absent" if not present else "explicitEmpty" if properties[field] == "" else "value",
                                       "xmlValue": properties.get(field), "exactlyCompared": present})
        comment_items = comments(row)
        native_comments = native.get("Comment", attrs.get("Comment"))
        if native_comments is not None:
            require(native_comments == comment_items, f"Native/XML multilingual comments differ at row {index}")
        name, address = native.get("Name"), native.get("Address")
        selector = "comment" if kind == "comment" else "nameAndAddress" if name and address else "nameOnly" if name else "addressOnly" if address else "blank"
        observed.append({"index": index, "xmlId": row.attrib.get("ID"), "kind": kind, "runtimeType": runtime,
                         "selectorProfile": selector, "properties": field_observations,
                         "comments": comment_items, "commentTextReadbackCompared": native_comments is not None,
                         "unknownXmlProperties": {key: value for key, value in properties.items() if key not in fields}})
    engineering = root.findall("Engineering")
    require(len(engineering) == 1, "One Engineering profile is required")
    export_setting = root.findtext("DocumentInfo/ExportSetting")
    require(export_setting == capture["exportOptions"], "Native requested and XML recorded export options differ")
    return {"kind": capture["kind"], "group": capture["group"], "name": capture["name"], "rowCount": len(rows),
            "profile": {"engineering": engineering[0].attrib, "tableXmlType": table.tag, "exportSetting": export_setting,
                        "products": [{"kind": item.tag, **unique_attributes(item)} for item in root.findall("DocumentInfo/InstalledProducts/*")]},
            "tableNativeAttributesNotSerialized": {key: value for key, value in dynamic.items() if key not in table_attributes},
            "rows": observed}


def table_key(item):
    return item["kind"], item["group"], item["name"]


def load_capture(path):
    data = read_bounded(path)
    value = json.loads(data.decode("utf-8-sig"))
    require(value.get("completed") is True and value.get("mode") == "watch-read", "Capture did not complete a watch read")
    require(value.get("readOnly") is True and value.get("saved") is False and value.get("onlineConnected") is False, "Capture does not record read-only offline/no-save behavior")
    watch = value["watch"]
    exports = watch["exports"]
    require(len(exports) <= 8 and len({table_key(item) for item in exports}) == len(exports), "Duplicate or excessive exported tables")
    require(len({table_key(item) for item in watch["inventory"]}) == len(watch["inventory"]), "Ambiguous native table inventory")
    require(len(set(watch["requested"])) == len(watch["requested"]), "Duplicate requested table names")
    inventory = {table_key(item): item for item in watch["inventory"]}
    missing = [name for name in watch["requested"] if not any(item["name"] == name for item in exports)]
    require(watch["matched"] == len(exports) and watch["allRequestedFound"] == (not missing), "Captured request coverage contradicts exported identities")
    artifacts = {}
    parsed = []
    for exported in exports:
        key = table_key(exported)
        require(exported["kind"] in ("watch", "force") and key in inventory and exported["name"] in watch["requested"], "Unrequested or uninventoried export")
        require(inventory[key]["rows"] == len(exported["entries"]), "Native inventory and typed row counts differ")
        artifact = exported["artifact"]
        artifact_path = Path(artifact["path"])
        require(artifact_path.is_absolute() and artifact_path.resolve().parent == path.resolve().parent and artifact_path.suffix.lower() == ".xml", "Export artifact must remain beside its supplied capture")
        raw = read_bounded(artifact_path)
        require(sha256(raw) == artifact["sha256"] and len(raw) == artifact["bytes"], "Export artifact hash/length differs from native capture")
        report = inspect_table(exported, raw)
        report["artifact"] = {"file": artifact_path.name, "sha256": sha256(raw), "bytes": len(raw)}
        parsed.append(report)
        artifacts[key] = raw
    return {"capture": value, "tables": parsed, "artifacts": artifacts, "missingRequested": missing,
            "source": {"path": str(path.resolve()), "sha256": sha256(data), "bytes": len(data)}}


def verify(current_path, previous_path):
    current, previous = load_capture(current_path), load_capture(previous_path)
    for field in ("projectPath", "plcName", "processId", "api"):
        require(current["capture"][field] == previous["capture"][field], "Sequential captures differ in exact target/process/API identity: " + field)
    require(not current["missingRequested"], "Current capture did not export every exact requested table name")
    preservation = []
    for key, older in previous["artifacts"].items():
        require(key in current["artifacts"], "Earlier exported table is missing from the later capture")
        newer = current["artifacts"][key]
        a, b = without_created_text(older), without_created_text(newer)
        require(a == b, "Sequential export differs outside DocumentInfo/Created text: " + key[2])
        preservation.append({"kind": key[0], "group": key[1], "name": key[2], "equalExceptCreatedText": True,
                             "comparisonSha256": sha256(a), "previousSha256": sha256(older), "currentSha256": sha256(newer)})
    rows = [row for table in current["tables"] for row in table["rows"]]
    fields = [field for row in rows for field in row["properties"]]
    all_comments = [comment for row in rows for comment in row["comments"]]
    force_rows = [row for row in rows if row["kind"] == "force"]
    native = current["capture"]
    unknowns = []
    if any(not row["commentTextReadbackCompared"] for row in rows):
        unknowns.append("Comment row positions/kinds and exported multilingual text are checked; some or all rows lack independent native Comment text readback.")
    if any(item["xmlState"] == "absent" for item in fields):
        unknowns.append("Absent XML fields remain absent observations; no general omission/default rule is qualified, including selector, intention and format properties.")
    if force_rows and all(row["selectorProfile"] == "blank" for row in force_rows):
        unknowns.append("All captured force rows have blank selectors; a stored ForceValue alone does not qualify populated force-table behavior.")
    if any(key not in previous["artifacts"] for key in current["artifacts"]):
        unknowns.append("Newly exported tables lack prior export baselines; repeated-read preservation is limited to shared exports.")
    unknowns.extend([
        "ModifyIntention/ForceIntention and stored values are offline configuration, not proof of active modification/forcing or actual PLC values.",
        "No authored semantic expected-signal oracle or supplied print is consumed; cross-view agreement does not establish intended signal meanings/values.",
        "Project modification flags and project-file hash availability are reported without promotion; selected export stability does not prove full-project preservation.",
        "Writable grammar, import/change/readback, fresh reopen, checkpoint restore/recovery, lifecycle/access and native online/force behavior remain unqualified.",
    ])
    return {
        "schemaVersion": 1, "status": "passed-with-explicit-gaps", "capturedAtUtc": datetime.now(timezone.utc).isoformat(),
        "verifier": {"path": str(Path(__file__).resolve()), "sha256": sha256(Path(__file__).read_bytes())},
        "nativeOperationsPerformedByVerifier": False, "sourceEvidenceKind": "supplied-native-read-export-captures",
        "nativeLifecycleQualified": False, "releaseAccepted": False,
        "sources": {"current": current["source"], "previous": previous["source"]},
        "target": {key: native[key] for key in ("projectPath", "plcName", "processId", "api")},
        "requestCoverage": {"currentMissing": current["missingRequested"], "previousMissing": previous["missingRequested"]},
        "summary": {"tables": len(current["tables"]), "orderedRows": len(rows), "rowKinds": dict(Counter(row["kind"] for row in rows)),
                    "selectorProfiles": dict(Counter(row["selectorProfile"] for row in rows)),
                    "displayFormats": dict(Counter(item["nativeValue"] for item in fields if item["field"] == "DisplayFormat")),
                    "exactNativeXmlFieldComparisons": sum(item["exactlyCompared"] for item in fields),
                    "absentXmlFields": dict(Counter(item["field"] for item in fields if item["xmlState"] == "absent")),
                    "explicitEmptyXmlFields": dict(Counter(item["field"] for item in fields if item["xmlState"] == "explicitEmpty")),
                    "commentCultures": dict(Counter(item["culture"] for item in all_comments)), "commentItems": len(all_comments),
                    "nonemptyCommentItems": sum(item["text"] != "" for item in all_comments),
                    "commentTextReadbackComparedRows": sum(row["commentTextReadbackCompared"] for row in rows),
                    "storedIntentions": {name: dict(Counter(item["nativeValue"] for item in fields if item["field"] == name)) for name in ("ModifyIntention", "ForceIntention")},
                    "storedValues": {name: dict(Counter(item["nativeValue"] for item in fields if item["field"] == name)) for name in ("ModifyValue", "ForceValue")},
                    "forceRowsWithBoundSelector": sum(row["selectorProfile"] != "blank" for row in force_rows)},
        "tables": current["tables"],
        "readPreservation": {"comparisonRule": "Only the byte span inside Document/DocumentInfo/Created is replaced; every other byte must match.",
                             "sharedExports": preservation, "newExportsWithoutBaseline": [list(key) for key in current["artifacts"] if key not in previous["artifacts"]],
                             "selectedSharedExportBodiesPreserved": True, "fullProjectPreservationProved": False,
                             "beforeModified": native.get("beforeModified"), "afterModified": native.get("afterModified"),
                             "projectFileBefore": native.get("projectFileBefore"), "projectFileAfter": native.get("projectFileAfter")},
        "unknowns": unknowns,
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--current", type=Path, required=True)
    parser.add_argument("--previous", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    require(not args.output.exists(), "Refusing to overwrite an evidence receipt")
    report = verify(args.current, args.previous)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(json.dumps({"status": report["status"], "summary": report["summary"], "fullProjectPreservationProved": False}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (EvidenceMismatch, OSError, KeyError, TypeError, json.JSONDecodeError) as error:
        print("Watch evidence verification failed: " + str(error), file=sys.stderr)
        sys.exit(1)
