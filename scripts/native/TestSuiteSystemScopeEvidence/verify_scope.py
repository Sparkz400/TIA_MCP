"""Independent, read-only oracle for the narrowly retained SingleStep collector profile.

This program imports no Siemens assemblies and makes no native calls. A collector
exit code, successful SaveToFile, or repeated empty folder never implies preservation.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re

SOURCE_SHA = "7c139e29b965e7769e32b8cf14be55b2f0ef6f4a83febd1ca78cb10e0c77bcee"
EXPECTED = {
    "style": ("TiaMcp_CasingAvailability", "0561b772ad361a76e922351639e7a319db2c4102f9c5dc790a8c8f88768ad0b1"),
    "application": ("TiaMcp_ScratchSingleStep", "e5024900e6eddda8bffa40fda3ea1c005ad1f156d4ace9c985b5dac05697ca8d"),
    "system": ("SingleStep", "fba99160b6e881c3561c2175f46395aad51613873eb6e0cfa2ff93a6a0210276"),
}
PROPERTIES = {"AUTHOR", "VERSION", "COMMENT", "SERVER_ADDRESS", "FOLDER_PATH", "SELECTED_SERVER_INTERFACE"}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def scope(data: bytes) -> dict:
    require(0 < len(data) <= 16 * 1024 * 1024, "TST byte bound")
    lines = data.decode("utf-8-sig", errors="strict").splitlines()
    require(sum(line.strip().startswith('TEST_CASE "') for line in lines) == 1, "One test case required")
    require(sum(line.strip() == "END_TEST_CASE" for line in lines) == 1, "One test case end required")
    require(sum(line.strip() == "PROPERTY" for line in lines) == 1, "One property section required")
    require(sum(line.strip() == "END_PROPERTY" for line in lines) == 1, "One property section end required")
    start, end = next(i for i, v in enumerate(lines) if v.strip() == "PROPERTY"), next(i for i, v in enumerate(lines) if v.strip() == "END_PROPERTY")
    require(start < end, "Property order")
    parsed = {}
    for line in lines[start + 1:end]:
        if not line.strip() or line.lstrip().startswith("//"):
            continue
        match = re.fullmatch(r'\s*([A-Z_]+)\s*:\s*"([^"\r\n]*)"\s*(?://.*)?', line)
        require(match is not None, "Unsupported property syntax")
        key, value = match.groups()
        require(key in PROPERTIES and key not in parsed, "Unknown or duplicate property")
        parsed[key] = value
    require(set(parsed) == PROPERTIES, "Complete retained property profile required")
    require(parsed["SELECTED_SERVER_INTERFACE"] == "Standard_SIMATIC", "Unreviewed interface profile")
    return {"serverAddress": parsed["SERVER_ADDRESS"], "folderPath": parsed["FOLDER_PATH"], "serverInterface": "StandardSIMATIC"}


def artifact(root: Path, item: dict) -> bytes:
    require(set(item) == {"path", "sha256", "bytes"}, "Artifact descriptor fields")
    relative = Path(item["path"])
    require(not relative.is_absolute() and ".." not in relative.parts, "Artifact leaves evidence directory")
    path = (root / relative).resolve()
    require(path.is_relative_to(root.resolve()), "Artifact leaves evidence directory")
    data = path.read_bytes()
    require(len(data) == item["bytes"] and sha(data) == item["sha256"], "Artifact bytes changed")
    return data


def typed(data: bytes) -> dict:
    item = json.loads(data)
    require(item["name"] == "SingleStep", "Readback belongs to another definition")
    require(isinstance(item["folderIsNull"], bool) and item["folderIsNull"] == (item["folderPath"] is None), "Null folder evidence disagrees")
    require(isinstance(item["serverAddress"], str) and isinstance(item["serverInterface"], str), "Incomplete typed scope")
    require(item["folderPath"] is None or isinstance(item["folderPath"], str), "Folder type")
    return {k: item[k] for k in ("serverAddress", "folderPath", "serverInterface")}


def observe(directory: Path) -> dict:
    raw_result = (directory / "result.json").read_bytes()
    result = json.loads(raw_result)
    require(not (directory / "failure.json").exists(), "Collector reported failure")
    require(result["mode"] == "inspect-system-scope" and result["completed"] is True, "Incomplete or wrong collector mode")
    for flag in ("automaticReplay", "nativeMutationInvoked", "applicationExecutionInvoked", "systemExecutionInvoked", "styleExecutionInvoked", "endpointConnectionInvoked", "nativeQualified", "testsPassed", "effectiveStyleScopeVerified", "beforeModified", "afterModified"):
        require(result[flag] is False, "Unexpected or missing false flag: " + flag)
    admission = json.loads((directory / "admission.json").read_bytes())
    require(admission["processId"] == result["processId"] and admission["nativeProcessStartTimeUtc"] == result["nativeProcessStartTimeUtc"], "Process identity mismatch")
    require(admission["projectPath"].casefold() == result["projectPath"].casefold(), "Project identity mismatch")
    require(admission["openObservationEvidence"]["sha256"] == result["projectOpenEvidenceSha256"], "Open evidence mismatch")
    for key in ("ownershipEvidence", "openObservationEvidence", "resourceAdmissionEvidence", "checkpointRetrievalEvidence"):
        require(sha((directory / (key + ".json")).read_bytes()) == admission[key]["sha256"], "Retained external evidence hash mismatch")
    reviewed = artifact(directory, result["reviewedSource"])
    require(sha(reviewed) == SOURCE_SHA and admission["reviewedSource"]["sha256"] == SOURCE_SHA, "Reviewed input changed")
    require(sha((directory / "TestSuiteSystemScopeEvidence.exe").read_bytes()) == admission["binarySha256"], "Collector binary changed")
    require(len(admission["sourceFiles"]) == 2, "Exact collector source pair required")
    for item in admission["sourceFiles"]:
        # Windows source paths are retained even when the offline verifier runs on another platform.
        filename = item["path"].replace("\\", "/").rsplit("/", 1)[-1]
        require(sha((directory / ("source-" + filename)).read_bytes()) == item["sha256"], "Collector source changed")
    before = typed(artifact(directory, result["typedBefore"]))
    middle = typed(artifact(directory, result["typedAfterFirstExport"]))
    after = typed(artifact(directory, result["typedAfter"]))
    definitions = []
    for stage in ("definitionsBefore", "definitionsAfter"):
        records = result[stage]
        require(len(records) == 3 and {r["family"] for r in records} == set(EXPECTED), "Exact three-family inventory required")
        parsed = {}
        for record in records:
            family = record["family"]
            require(record["name"] == EXPECTED[family][0], "Definition identity mismatch")
            data = artifact(directory, record["artifact"])
            parsed[family] = {"sha256": sha(data), "scope": scope(data) if family == "system" else None}
        definitions.append(parsed)
    expected = scope(reviewed)
    exported = definitions[0]["system"]["scope"]
    peer_unchanged = definitions[0] == definitions[1]
    native_unchanged = before == middle == after
    identity_matches = all(definitions[0][family]["sha256"] == expected_pair[1] for family, expected_pair in EXPECTED.items())
    differences = [{"stage": stage, "property": key, "expected": value, "observed": observed[key]}
                   for stage, observed in (("typedBefore", before), ("export", exported), ("typedAfterFirstExport", middle), ("typedAfter", after))
                   for key, value in expected.items() if value != observed[key]]
    getter_export_agree = before == exported == after
    # Retain null vs empty as a mismatch; neither preserves the reviewed nonempty folder.
    folder_diagnosis = "matchesReviewedFolder" if before["folderPath"] == exported["folderPath"] == expected["folderPath"] else (
        "currentNativeFolderAlreadyMissingBeforeThisExport" if before["folderPath"] in (None, "") else
        "nonemptyNativeFolderDiffersFromExport" if before["folderPath"] != exported["folderPath"] else "differentNonemptyFolder")
    return {"directory": str(directory.resolve()), "resultSha256": sha(raw_result), "projectPath": result["projectPath"],
            "processId": result["processId"], "nativeProcessStartTimeUtc": result["nativeProcessStartTimeUtc"],
            "projectOpenEvidenceSha256": result["projectOpenEvidenceSha256"], "reviewedScope": expected,
            "typedScope": before, "exportScope": exported, "definitionExports": definitions[0],
            "testSuiteDefinitionPeersUnchanged": peer_unchanged, "typedReadbackUnchanged": native_unchanged,
            "retainedDefinitionIdentityMatches": identity_matches, "typedScopeAgreesWithExport": getter_export_agree,
            "reviewedScopePreserved": not differences, "differences": differences, "folderDiagnosis": folder_diagnosis,
            "originalImportVersusExportCauseEstablished": False,
            "collectionConsistencyPassed": peer_unchanged and native_unchanged and identity_matches}


def compare(first: dict, reopened: dict | None = None) -> dict:
    reopen_verified = False
    if reopened is not None:
        require(first["projectPath"].casefold() == reopened["projectPath"].casefold(), "Fresh reopen belongs to another project")
        require((first["processId"], first["nativeProcessStartTimeUtc"]) != (reopened["processId"], reopened["nativeProcessStartTimeUtc"]), "Fresh native process proof required")
        require(first["projectOpenEvidenceSha256"] != reopened["projectOpenEvidenceSha256"], "Independent open observation required")
        reopen_verified = (first["collectionConsistencyPassed"] and reopened["collectionConsistencyPassed"] and
                           first["definitionExports"] == reopened["definitionExports"] and first["typedScope"] == reopened["typedScope"])
    return {"nativeInvokedByVerifier": False, "first": first, "reopened": reopened, "freshReopenVerified": reopen_verified,
            "scopePreservationPassed": bool(reopen_verified and first["reviewedScopePreserved"] and reopened["reviewedScopePreserved"]),
            "projectWidePeerPreservationVerified": False, "restorationVerified": False, "originalImportVersusExportCauseEstablished": False,
            "nativeExecutionAuthorized": False, "failingProfilesRemainDisabled": True, "releaseAccepted": False}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("first", type=Path)
    parser.add_argument("--reopened", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = compare(observe(args.first), observe(args.reopened) if args.reopened else None)
    with args.output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(report, stream, indent=2, ensure_ascii=False)
        stream.write("\n")
    print(json.dumps({"report": str(args.output), "scopePreservationPassed": report["scopePreservationPassed"], "freshReopenVerified": report["freshReopenVerified"], "failingProfilesRemainDisabled": True}))


if __name__ == "__main__":
    main()
