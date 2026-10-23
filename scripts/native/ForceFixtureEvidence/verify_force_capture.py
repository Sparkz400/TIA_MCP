"""Independent retained-byte verifier; never imports Siemens code or operates a PLC."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

PREFIX = "SW.WatchAndForceTables."


def require(ok, message):
    if not ok:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def artifact(directory, record):
    relative = Path(record["path"])
    require(not relative.is_absolute() and ".." not in relative.parts, "Artifact path escape")
    path = (directory / relative).resolve()
    require(path.is_relative_to(directory.resolve()), "Artifact path escape")
    data = path.read_bytes()
    require(sha(data) == record["sha256"] and len(data) == record["bytes"], "Artifact bytes changed")
    return data


def mask_timestamp(data):
    root = ET.fromstring(data)
    require(root.tag == "Document" and len(root.findall("DocumentInfo/Created")) == 1, "Single native export timestamp required")
    pattern = rb"(<DocumentInfo>\s*<Created>)[^<]*(</Created>)"
    require(len(re.findall(pattern, data)) == 1, "Only DocumentInfo/Created can be masked")
    return re.sub(pattern, rb"\g<1>__NATIVE_EXPORT_TIME__\g<2>", data, count=1)


def parse_force(data):
    root = ET.fromstring(data)
    tables = root.findall(PREFIX + "PlcForceTable")
    require(len(tables) == 1, "Exactly one genuine force-table root required")
    table = tables[0]
    rows = []
    for entry in table.findall("ObjectList/*"):
        if entry.attrib.get("CompositionName") != "Entries":
            continue
        require(entry.tag in {PREFIX + "PlcForceTableEntry", PREFIX + "PlcTableCommentEntry"}, "Unknown row type")
        comments = {}
        for item in entry.findall("ObjectList/MultilingualText[@CompositionName='Comment']/ObjectList/MultilingualTextItem"):
            culture = item.findtext("AttributeList/Culture")
            require(culture and culture not in comments, "Duplicate/missing culture")
            comments[culture] = item.findtext("AttributeList/Text") or ""
        row = {"kind": "signal" if entry.tag.endswith(".PlcForceTableEntry") else "comment", "comments": comments}
        if row["kind"] == "signal":
            for key, field in (("name", "Name"), ("address", "Address"), ("displayFormat", "DisplayFormat"), ("forceValue", "ForceValue"), ("monitorTrigger", "MonitorTrigger")):
                row[key] = entry.findtext("AttributeList/" + field) or ""
            intention = entry.findtext("AttributeList/ForceIntention")
            require(intention in (None, "", "false", "False"), "Serialized force selection is true or unknown")
            row["forceIntentionSerialized"] = intention
        rows.append(row)
    return table.findtext("AttributeList/Name"), rows


def check_rows(oracle, typed_rows, force_xml):
    name, xml_rows = parse_force(force_xml)
    require(name == oracle["forceTableName"], "Force table identity differs")
    expected = oracle["rows"]
    require(len(expected) == len(typed_rows) == len(xml_rows) == 3, "Exact three-row profile required")
    require([r["kind"] for r in expected] == ["signal", "comment", "signal"], "Reviewed row order differs")
    serialized_intentions = []
    for index, (desired, native, xml) in enumerate(zip(expected, typed_rows, xml_rows)):
        require(native == desired, f"Typed row {index} differs from independent oracle")
        if desired["kind"] == "signal":
            require(desired["forceIntention"] is False, "ForceIntention=true is forbidden")
            require(desired["displayFormat"] == "Bool" and (desired["name"].strip() or desired["address"].strip()), "Blank/Undef rows are not populated signals")
            serialized_intentions.append(xml.pop("forceIntentionSerialized"))
            # Missing native XML defaults remain explicitly reported. Typed false is mandatory;
            # omission alone is not treated as proof that forcing is inactive.
            xml["forceIntention"] = False
        require(xml == desired, f"Native XML row {index} differs from reviewed typed fields")
        require(set(desired["comments"]) == set(oracle["activeLanguages"]), "All reviewed cultures required")
    require(expected[0]["forceValue"] == "FALSE" and expected[2]["forceValue"] == "TRUE", "Expected stored FALSE/TRUE values differ")
    return serialized_intentions


def snapshot(directory, descriptor):
    data = artifact(directory, descriptor)
    value = json.loads(data)
    require(value["projectModified"] is False, "Capture modified project")
    for peer in value["peerArtifacts"]:
        raw = artifact(directory, peer["artifact"])
        require(sha(mask_timestamp(raw)) == value["peers"][peer["identity"]], "Peer fingerprint disagrees with native bytes")
    force_data = artifact(directory, value["force"]["artifact"])
    require(sha(mask_timestamp(force_data)) == value["force"]["semanticSha256"], "Force fingerprint disagrees with native bytes")
    return value, force_data


def observe(directory):
    require(not (directory / "failure.json").exists(), "Collector failure remains unqualified; inspect retained after.json separately")
    result = json.loads((directory / "result.json").read_bytes())
    require(result["completed"] is True and result["nativeProcessExited"] is True and result.get("metadataOnly") is False, "Complete populated capture and native exit required")
    for flag in ("nativeAuthoringInvoked", "nativeSaveInvoked", "onlineOperationInvoked", "forceOperationInvoked", "automaticReplay", "nativeQualified", "restorationVerified", "projectModified"):
        require(result[flag] is False, "Unexpected helper effect/claim: " + flag)
    admission = json.loads((directory / "admission.json").read_bytes())
    oracle = json.loads((directory / "reviewed-oracle.json").read_bytes())
    require(result["reviewedOracleSha256"] == admission["oracle"]["sha256"], "Oracle identity mismatch")
    # The original independently reviewed input remains the admission source; the collector
    # also serializes its parsed JSON for readback. Compare its complete semantics.
    original = (directory / "reviewed-oracle-original.json").read_bytes()
    require(sha(original) == admission["oracle"]["sha256"] and json.loads(original) == oracle, "Reviewed oracle bytes changed")
    require(sha((directory / "ForceFixtureEvidence.exe").read_bytes()) == admission["binarySha256"], "Frozen collector binary changed")
    require(sha((directory / "reviewed-before.zap21").read_bytes()) == admission["reviewedCheckpoint"]["sha256"], "Reviewed checkpoint changed")
    for source in admission["sourceFiles"]:
        name = source["path"].replace("\\", "/").rsplit("/", 1)[-1]
        require(sha((directory / ("source-" + name)).read_bytes()) == source["sha256"], "Frozen collector source changed")
    before, _ = snapshot(directory, result["before"])
    after, force_data = snapshot(directory, result["after"])
    require(before["peers"] == after["peers"] and result["peerFingerprintsUnchanged"] is True, "Unchanged peers failed")
    require(after["force"]["isConsistent"] is True and result["populatedSignalRows"] == 2 and result["oracleMatched"] is True, "Populated/consistent/oracle native gate failed")
    serialized = check_rows(oracle, after["force"]["oracleRows"], force_data)
    return {"directory": str(directory.resolve()), "projectPath": result["projectPath"], "processId": result["processId"],
            "nativeProcessStartTimeUtc": result["nativeProcessStartTimeUtc"], "mode": result["mode"],
            "oracleSha256": result["reviewedOracleSha256"], "resultSha256": sha((directory / "result.json").read_bytes()),
            "forceSha256": sha(force_data), "forceTimestampMaskedSha256": sha(mask_timestamp(force_data)),
            "peers": after["peers"], "typedRows": after["force"]["oracleRows"], "serializedForceIntentions": serialized,
            "nativeProcessExited": True, "populatedSignalRowsVerified": 2, "scope": after["preservationLimit"]}


def compare(first, reopened=None):
    fresh = False
    if reopened is not None:
        require(first["projectPath"].casefold() == reopened["projectPath"].casefold(), "Reopen belongs to another project")
        require((first["processId"], first["nativeProcessStartTimeUtc"]) != (reopened["processId"], reopened["nativeProcessStartTimeUtc"]), "Distinct native process required")
        require(first["oracleSha256"] == reopened["oracleSha256"], "Independent oracle changed across reopen")
        require(first["forceTimestampMaskedSha256"] == reopened["forceTimestampMaskedSha256"] and first["typedRows"] == reopened["typedRows"], "Force bytes/typed rows changed across reopen")
        require(first["peers"] == reopened["peers"], "Peers changed across reopen")
        fresh = True
    return {"nativeInvokedByVerifier": False, "first": first, "reopened": reopened, "freshReopenVerified": fresh,
            "populatedForceCaptureVerified": True, "forceEditingQualified": False, "onlineForcingQualified": False,
            "restorationVerified": False, "releaseAccepted": False, "failingProfilesRemainDisabled": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("first", type=Path)
    parser.add_argument("--reopened", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = compare(observe(args.first), observe(args.reopened) if args.reopened else None)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2, ensure_ascii=False)
        stream.write("\n")
    print(json.dumps({"populatedForceCaptureVerified": True, "freshReopenVerified": result["freshReopenVerified"], "forceEditingQualified": False}))


if __name__ == "__main__":
    main()
