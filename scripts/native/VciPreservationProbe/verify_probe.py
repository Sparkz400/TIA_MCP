"""Independent retained-byte audit; does not load Siemens assemblies or perform native calls."""
import argparse
import hashlib
import itertools
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def read_json(path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, "Duplicate JSON key: " + key)
            result[key] = value
        return result
    return json.loads(Path(path).read_text(encoding="utf-8-sig"), object_pairs_hook=unique)


def artifact(row, owner=None):
    lexical = Path(row["path"]).absolute()
    for part in [lexical, *lexical.parents]:
        require(not part.is_symlink() and not getattr(part, "is_junction", lambda: False)(), "Link in artifact path")
    path = lexical.resolve()
    if owner is not None:
        require(path.is_relative_to(Path(owner).resolve()), "Artifact escaped capture directory")
    for part in [path, *path.parents]:
        require(not part.is_symlink() and not getattr(part, "is_junction", lambda: False)(), "Link in artifact path")
    data = path.read_bytes()
    require(len(data) <= 16 * 1024 * 1024, "Artifact exceeds package bound")
    require(hashlib.sha256(data).hexdigest() == row["sha256"], "Artifact hash mismatch")
    if "bytes" in row:
        require(len(data) == row["bytes"], "Artifact length mismatch")
    return data


def inventory(row, owner):
    folder = Path(row["directory"]).resolve()
    require(folder.is_relative_to(Path(owner).resolve()), "Package directory escaped capture")
    declared = {}
    for file in row["files"]:
        require(file["relativePath"] not in declared, "Duplicate package file")
        require(Path(file["path"]).resolve() == folder / file["relativePath"], "Relative inventory path mismatch")
        declared[file["relativePath"]] = artifact(file, folder)
    actual = {p.relative_to(folder).as_posix() for p in folder.rglob("*") if p.is_file()}
    require(actual == set(declared), "Complete package inventory mismatch")
    require(sum(map(len, declared.values())) <= 16 * 1024 * 1024, "Package exceeds bound")
    return declared


def native_block(data):
    # Explicit entity/doctype refusal, including UTF-16, before stdlib parsing.
    require(b"<!DOCTYPE" not in data.upper().replace(b"\x00", b"") and
            b"<!ENTITY" not in data.upper().replace(b"\x00", b""), "DTD/entity input refused")
    root = ET.fromstring(data)
    require(root.tag == "Document", "Native Document root required")
    blocks = [node for node in root if node.tag.startswith("SW.Blocks.")]
    require(len(blocks) == 1, "Exactly one native block required")
    node = blocks[0]
    require(len(node.findall("AttributeList")) == 1, "Ambiguous block attributes")
    attributes = node.find("AttributeList")
    identity = {}
    for field in ("Name", "Number"):
        matches = attributes.findall(field)
        require(len(matches) == 1 and len(matches[0]) == 0, "Ambiguous block identity")
        identity[field.lower()] = matches[0].text or ""
    identity["kind"] = node.tag.removeprefix("SW.Blocks.")
    identity["number"] = int(identity["number"])
    return node, identity


def comments(node):
    require(len(node.findall("ObjectList")) == 1, "Ambiguous native object list")
    compositions = [n for n in node.find("ObjectList") if n.tag == "MultilingualText" and n.get("CompositionName") == "Comment"]
    require(len(compositions) == 1 and len(compositions[0].findall("ObjectList")) == 1, "Ambiguous comment composition")
    result = {}
    for item in compositions[0].find("ObjectList"):
        require(item.tag == "MultilingualTextItem" and len(item.findall("AttributeList")) == 1, "Unknown comment representation")
        attributes = item.find("AttributeList")
        values = {}
        for key in ("Culture", "Text"):
            matches = attributes.findall(key)
            require(len(matches) == 1 and len(matches[0]) == 0, "Unknown/rich comment representation")
            values[key] = matches[0].text or ""
        require(values["Culture"] not in result, "Duplicate comment culture")
        result[values["Culture"]] = values["Text"]
    return result


def tree(node):
    # Keep every block attribute, ID, text, and child order. No content normalization.
    return (node.tag, tuple(sorted(node.attrib.items())), node.text,
            tuple((tree(child), child.tail) for child in node))


def capture_binding(folder, mode):
    """Bind retained records; process creation time/lifecycle still needs separate proof."""
    folder = Path(folder)
    receipt = read_json(folder / "result.json")
    admission = read_json(folder / "admission.json")
    require(receipt.get("mode") == admission.get("mode") == mode and
            receipt.get("completed") is True and receipt.get("saved") is (mode == "export"),
            "Successful saved export receipt required" if mode == "export" else "Read-only verification snapshot required")
    require(receipt.get("nativeImportQualified") is False and receipt.get("wholeProjectPreservationQualified") is False,
            "Collector cannot promote native import")
    require(receipt.get("automaticReplay") is False, "Wrong admission/replay state")
    require(type(receipt.get("processId")) is int and receipt["processId"] > 0 and
            receipt["processId"] == admission.get("processId"), "Receipt/admission process binding mismatch")
    require(receipt.get("checkpoint") == admission.get("checkpoint"), "Receipt/admission checkpoint binding mismatch")
    artifact(receipt["checkpoint"])
    require(receipt.get("oracle") == admission.get("oracle"), "Receipt/admission oracle binding mismatch")
    oracle = read_json(folder / "oracle.json")
    require(oracle == json.loads(artifact(receipt["oracle"])), "Oracle retained copy differs")
    project, plc = receipt.get("projectPath"), receipt.get("plcName")
    require(isinstance(project, str) and Path(project).is_absolute() and isinstance(plc, str) and bool(plc) and
            oracle.get("projectPath") == project == admission.get("projectPath") and
            oracle.get("plcName") == plc == admission.get("plcName"), "Exact project/PLC binding mismatch")
    return receipt, oracle


def snapshot(folder, name, expected_project=None, expected_plc=None):
    folder = Path(folder)
    data = read_json(folder / (name + ".json"))
    require(isinstance(data.get("projectPath"), str) and Path(data["projectPath"]).is_absolute() and
            isinstance(data.get("plcName"), str) and bool(data["plcName"]), "Snapshot project/PLC identity missing")
    require((expected_project is None or data["projectPath"] == expected_project) and
            (expected_plc is None or data["plcName"] == expected_plc), "Snapshot project/PLC binding mismatch")
    peers = []
    for block in data["blocks"]:
        xml, identity = native_block(artifact(block["xml"], folder / name))
        require(all(block[k] == value for k, value in identity.items()), "Snapshot XML identity mismatch")
        observed = comments(xml)
        expected = {c["culture"]: c["text"] for c in block["comments"]}
        require(len(expected) == len(block["comments"]) and observed == expected, "Snapshot direct/XML comments disagree")
        peers.append((block["identity"], tree(xml), expected))
    require(0 < len(peers) <= 32, "Invalid bounded peer inventory")
    require(len({json.dumps(b["identity"], sort_keys=True) for b in data["blocks"]}) == len(peers), "Duplicate native peer identity")
    workspaces = data["workspaces"]
    require(isinstance(workspaces, list) and len(workspaces) <= 64, "Invalid bounded workspace inventory")
    for workspace in workspaces:
        identity = workspace.get("identity")
        require(isinstance(identity, list) and 0 < len(identity) <= 32 and
                all(isinstance(part, dict) and part for part in identity) and
                identity[-1].get("Name") == workspace.get("name"), "Workspace native identity/name mismatch")
        require(isinstance(workspace.get("root"), str) and Path(workspace["root"]).is_absolute() and
                isinstance(workspace.get("culture"), str) and bool(workspace["culture"]), "Workspace root/culture identity missing")
        require(isinstance(workspace.get("mappings"), list) and len(workspace["mappings"]) <= 128, "Invalid bounded mapping inventory")
    require(len({json.dumps(w["identity"], sort_keys=True) for w in workspaces}) == len(workspaces), "Duplicate native workspace identity")
    return data, (peers, data["languages"], data["projectLanguageImportOptions"])


def review(export_directory, reopened=None, restored=None):
    folder = Path(export_directory)
    receipt, oracle = capture_binding(folder, "export")
    before, before_peers = snapshot(folder, "before", receipt["projectPath"], receipt["plcName"])
    after, after_peers = snapshot(folder, "after", receipt["projectPath"], receipt["plcName"])
    require(before_peers == after_peers, "Bounded block/language/settings peers changed")
    require(all(after["workspaces"].count(w) == 1 for w in before["workspaces"]), "Preexisting VCI mapping changed")
    expected = {b["name"]: b for b in oracle["blocks"]}
    require(len(expected) == len(oracle["blocks"]) == 2, "Two unique oracle blocks required")
    for name, block in expected.items():
        matches = [b for b in before["blocks"] if b["name"] == name]
        require(len(matches) == 1 and all(matches[0][k] == block[k] for k in ("name", "number", "kind")), "Oracle/native identity mismatch")
        wanted = {c["culture"]: c["text"] for c in block["comments"]}
        require(set(wanted) == {"de-DE", "en-GB"} and len(block["comments"]) == 2 and all(wanted.values()), "Exact nonempty bilingual oracle required")
        require(all(c in matches[0]["comments"] for c in block["comments"]), "Oracle comments do not match independent native readback")
    reports = []
    vci = [row for row in receipt["profiles"] if "workspace" in row]
    direct = [row for row in receipt["profiles"] if row.get("route") == "PlcBlock.ExportAsDocuments"]
    matrix = set(itertools.product(expected, ("de-DE", "en-GB"), ("SCL", "SimaticML", "SimaticSD")))
    require(len(vci) == 12 and {(r["block"], r["culture"], r["format"]) for r in vci} == matrix, "Incomplete/duplicate VCI matrix")
    require(len(direct) == 2 and {r["block"] for r in direct} == set(expected), "Incomplete/duplicate direct SD matrix")
    require(len(receipt["profiles"]) == 14, "Unexpected profile row")
    added = [w for w in after["workspaces"] if w not in before["workspaces"]]
    require(len({r["workspace"] for r in vci}) == len(vci) and
            not any(w["name"] == r["workspace"] for w in before["workspaces"] for r in vci),
            "Export workspace identity reused")
    require(len(added) == len(vci) and {w["name"] for w in added} == {r["workspace"] for r in vci},
            "Unreviewed or missing workspace effect")
    directories = [Path(row["directory"]).resolve() for row in receipt["profiles"]]
    require(all(not left.is_relative_to(right) and not right.is_relative_to(left)
                for left, right in itertools.combinations(directories, 2)), "Overlapping profile directories")
    for row in vci + direct:
        files = inventory(row, folder)
        preserved = False
        issues = []
        if "workspace" in row:
            workspace = next(w for w in added if w["name"] == row["workspace"])
            require(Path(workspace["root"]).resolve() == Path(row["directory"]).resolve() and
                    workspace["culture"] == row["culture"], "Saved workspace root/culture differs")
            mapping = row.get("mapping")
            if row["state"] != "exported":
                issues.append("nativeExportFailed")
                require(workspace["mappings"] == ([] if mapping is None else [mapping]), "Failed export mapping effect unreviewed")
            else:
                source = next(b for b in before["blocks"] if b["name"] == row["block"])
                require(mapping["identity"] == source["identity"] and mapping["format"] == row["format"] and
                        Path(mapping["directory"]).resolve() == Path(row["directory"]).resolve() and mapping["fileName"] == "fixture", "Mapped native object/path/format mismatch")
                require(row["workspaceLanguage"] == row["culture"], "Workspace culture mismatch")
                require(workspace["mappings"] == [mapping], "Saved mapping readback differs")
                require(".vci/workspace.vci.config" in files, "Missing VCI configuration")
                config = ET.fromstring(files[".vci/workspace.vci.config"])
                require(config.tag == "WorkspaceLanguage" and not list(config) and config.text == row["culture"], "Retained workspace culture differs")
        if row["format"] == "SimaticML" and row["state"] == "exported":
            require("fixture.xml" in files, "Missing XML primary")
            xml, identity = native_block(files["fixture.xml"])
            wanted = expected[row["block"]]
            require(all(identity[k] == wanted[k] for k in identity), "Exported XML identity mismatch")
            preserved = comments(xml) == {c["culture"]: c["text"] for c in wanted["comments"]}
            if not preserved:
                issues.append("commentCulturesMissingChangedOrUnreviewed")
        else:
            issues.append("commentLanguageGrammarUnqualified:" + row["format"])
        resources = [name for name in files if name.lower().endswith(".s7res")]
        if row["format"] == "SimaticSD" and not resources:
            issues.append("simaticSdResourcesAbsent")
        reports.append({"block": row["block"], "culture": row.get("culture"), "format": row["format"],
                        "route": row.get("route", "Workspace.ExportObject"), "files": sorted(files),
                        "resourceFiles": resources, "exactBlockCommentsPreserved": preserved, "issues": issues})
    reopen_matches = restored_matches = None
    for directory, baseline, phase in ((reopened, (after, after_peers), "reopen"), (restored, (before, before_peers), "restoration")):
        if directory is None:
            continue
        other, other_oracle = capture_binding(directory, "snapshot")
        require(other["plcName"] == receipt["plcName"], "Verification PLC binding differs")
        require(other["checkpoint"] == receipt["checkpoint"], "Verification checkpoint binding differs")
        if phase == "reopen":
            require(Path(other["projectPath"]).resolve() == Path(receipt["projectPath"]).resolve(), "Reopen requires the exact exported project")
        else:
            require(Path(other["projectPath"]).resolve() != Path(receipt["projectPath"]).resolve(), "Restoration requires a new project destination")
        require(other_oracle == {**oracle, "projectPath": other["projectPath"]}, "Verification oracle content differs")
        observed, peers = snapshot(directory, "before", other["projectPath"], other["plcName"])
        matches = peers == baseline[1] and observed["workspaces"] == baseline[0]["workspaces"]
        require(matches, "Fresh " + phase + " bounded snapshot differs")
        if phase == "reopen":
            reopen_matches = True
        else:
            restored_matches = True
    return {"nativeCalls": 0, "profiles": reports, "boundedPeersUnchanged": True,
            "captureBindingsVerified": True, "workspaceEffectsFullyAccounted": True,
            "allExportsReturned": all(r["state"] == "exported" for r in vci) and all(r["state"] == "returned" for r in direct),
            "reopenedSnapshotMatches": reopen_matches, "restoredSnapshotMatches": restored_matches,
            "actualLifecycleAndCleanupProofRequiredSeparately": True, "nativeImportEnabled": False,
            "fullNativeQualification": False, "scope": "retainedBytesAndBoundedSnapshotComparisonOnly"}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--export", dest="export_directory", required=True)
    parser.add_argument("--reopened")
    parser.add_argument("--restored")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    report = review(args.export_directory, args.reopened, args.restored)
    with Path(args.output).open("x", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, ensure_ascii=False)
    print(json.dumps({"profiles": len(report["profiles"]), "nativeCalls": 0, "nativeImportEnabled": False}))
