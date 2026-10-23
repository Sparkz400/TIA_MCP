"""Independent, file-only oracle for the owned first watch workflow.

Never attaches to TIA. Retains failed stages as failures, including disappeared
blank peer rows; normalizes only known container indentation and export time.
No production parser or semantic-hash implementation is imported.
"""
import argparse
import copy
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET
from xml.parsers import expat

PREFIX = "SW.WatchAndForceTables."
SYMBOL = '"TiaMcp_EnableGate_DB".Active'
COMMENTS = {"de-DE": "Freigabestatus {0}", "en-GB": "Enable status {0}", "nl-NL": "Schakelstatus {0}"}
TABLES = {PREFIX + name for name in ("PlcWatchTable", "PlcForceTable")}
ROWS = {PREFIX + name for name in ("PlcWatchTableEntry", "PlcForceTableEntry", "PlcTableCommentEntry")}
NATIVE_CHILDREN = {
    "Document": {"DocumentInfo"} | TABLES, "DocumentInfo": {"InstalledProducts"},
    "InstalledProducts": {"Product", "OptionPackage"},
    **{name: {"AttributeList", "ObjectList"} for name in TABLES | ROWS | {"MultilingualText", "MultilingualTextItem"}},
}
NATIVE_OBJECTS = {**{name: ROWS for name in TABLES}, **{name: {"MultilingualText"} for name in ROWS}, "MultilingualText": {"MultilingualTextItem"}}


class VerificationError(ValueError): pass


def need(condition, message):
    if not condition: raise VerificationError(message)


def sha(data): return hashlib.sha256(data).hexdigest()


def load(path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            need(key not in result, "Duplicate JSON property: " + key)
            result[key] = value
        return result
    return json.loads(Path(path).read_text(encoding="utf-8-sig"), object_pairs_hook=unique)


def ref(path):
    path = Path(path)
    return {"path": str(path), "sha256": sha(path.read_bytes()), "bytes": path.stat().st_size}


def dotnet_canonical(value):
    """Independent equivalent for this ASCII-key, integer-only journal contract."""
    if isinstance(value, str):
        text = json.dumps(value, ensure_ascii=True).replace('\\"', '\\u0022')
        for char in "<>&'+`": text = text.replace(char, "\\u%04X" % ord(char))
        return re.sub(r"\\u([0-9a-f]{4})", lambda m: "\\u" + m[1].upper(), text)
    if isinstance(value, dict): return "{" + ",".join(dotnet_canonical(k) + ":" + dotnet_canonical(value[k]) for k in sorted(value, key=lambda s: s.encode("utf-16-be"))) + "}"
    if isinstance(value, list): return "[" + ",".join(dotnet_canonical(v) for v in value) + "]"
    need(value is None or isinstance(value, (int, bool)), "Unsupported numeric journal representation.")
    return json.dumps(value, separators=(",", ":"))


def canonical_sha(value): return sha(dotnet_canonical(value).encode("utf-8"))


def xml(data):
    need(0 < len(data) <= 16 * 1024 * 1024, "XML size bound.")
    need(b"<!DOCTYPE" not in data.upper() and b"<!ENTITY" not in data.upper() and b"\x00" not in data, "DTD/entities and non-UTF8 workflow XML are refused.")
    root = ET.fromstring(data, parser=ET.XMLParser(target=ET.TreeBuilder(insert_comments=True, insert_pis=True)))
    need(root.tag == "Document", "Native Document root required.")
    need(len(root.findall("Engineering")) == 1 and root.find("Engineering").attrib.get("version") == "V21", "Exact V21 engineering version required.")
    return root


def external_xml_nodes(data):
    """ElementTree drops prolog/epilog nodes; preserve those unknown nodes separately."""
    depth, seen_root, nodes = 0, False, []
    parser = expat.ParserCreate()
    def opened(name, attrs):
        nonlocal depth, seen_root
        depth += 1; seen_root = True
    def closed(name):
        nonlocal depth
        depth -= 1
    def comment(text):
        if depth == 0: nodes.append(("epilog" if seen_root else "prolog", "comment", text))
    def instruction(target, text):
        if depth == 0: nodes.append(("epilog" if seen_root else "prolog", "pi", target, text))
    parser.StartElementHandler, parser.EndElementHandler = opened, closed
    parser.CommentHandler, parser.ProcessingInstructionHandler = comment, instruction
    parser.Parse(data, True)
    return tuple(nodes)



def xml_key(root, ignore_created=True):
    """Keep all unknown/leaf text, attributes, comments, PIs and child ordering."""
    def visit(node, preserve=False, is_created=False, native=False, parent=None):
        setting = node.attrib.get("{http://www.w3.org/XML/1998/namespace}space")
        if setting is not None: preserve = setting == "preserve"
        tag = node.tag if isinstance(node.tag, str) else "#comment" if node.tag is ET.Comment else "#processing-instruction"
        direct = [node.text or ""] + [c.tail or "" for c in node]
        structural = native and not preserve and all(not text.strip() for text in direct)
        text = "" if ignore_created and is_created else "" if structural else node.text or ""
        allowed = (NATIVE_OBJECTS.get(parent, set()) if tag == "ObjectList" else NATIVE_CHILDREN.get(tag, set())) if native else set()
        children = tuple((visit(child, preserve, native and tag == "DocumentInfo" and child.tag == "Created", child.tag in allowed, tag), "" if structural else child.tail or "") for child in node)
        return tag, tuple(sorted(node.attrib.items())), text, children
    return visit(root, native=root.tag == "Document")


def table(root):
    tables = [e for e in root if isinstance(e.tag, str) and e.tag.startswith(PREFIX)]
    need(len(tables) == 1 and tables[0].tag == PREFIX + "PlcWatchTable", "Exactly one native watch table required.")
    return tables[0]


def target_oracle(data, literal):
    root = xml(data); watch = table(root)
    need(watch.findtext("AttributeList/Name") == "MilestoneWatch", "Authored table name differs.")
    rows = list(watch.find("ObjectList"))
    need(len(rows) == 1 and rows[0].tag == PREFIX + "PlcWatchTableEntry", "Exactly one authored watch row required.")
    attrs = rows[0].find("AttributeList")
    need(attrs is not None and attrs.findtext("Name") == SYMBOL and attrs.findtext("DisplayFormat") == "Bool" and attrs.findtext("ModifyValue") == literal, "Authored symbol/Bool/literal differs.")
    need(attrs.findtext("ModifyIntention", "false").lower() == "false", "Offline review must not arm a modify action.")
    comments = {}
    for item in rows[0].findall("ObjectList/MultilingualText[@CompositionName='Comment']/ObjectList/MultilingualTextItem/AttributeList"):
        culture = item.findtext("Culture"); need(culture not in comments, "Duplicate comment culture.")
        comments[culture] = item.findtext("Text", "")
    need(comments == COMMENTS, "Exact three authored comments differ or a language is omitted.")
    return root


def only_literal_change(before, candidate):
    old, new = target_oracle(before, "FALSE"), target_oracle(candidate, "TRUE")
    table(new).find("ObjectList/" + PREFIX + "PlcWatchTableEntry/AttributeList/ModifyValue").text = "FALSE"
    need(xml_key(old, False) == xml_key(new, False) and external_xml_nodes(before) == external_xml_nodes(candidate), "Reviewed input changes payload beyond the one approved ModifyValue field.")


def typed_oracle(snapshot, literal):
    target = snapshot["target"]
    need(target["name"] == "MilestoneWatch" and target["kind"] == "watch" and target["isConsistent"] is True, "Typed native target/name/consistency differs.")
    need(len(target["rows"]) == 1, "Typed native row count differs.")
    row = target["rows"][0]
    need(row["kind"] == "PlcWatchTableEntry" and row["name"] == SYMBOL and row["displayFormat"] == "Bool" and row["modifyValue"] == literal and row["modifyIntention"] is False,
         "Independent typed native row values differ.")
    need(snapshot["observation"]["offline"] is True, "Native offline observation required.")


def artifact(root, item):
    path = Path(item["path"]) if "path" in item else root / item["relativePath"]
    if "relativePath" in item: need(path.resolve().is_relative_to(root.resolve()), "Artifact path escapes run root.")
    need(path.is_file() and sha(path.read_bytes()) == item["sha256"], "Artifact missing or hash changed: " + str(path))
    if "bytes" in item: need(path.stat().st_size == item["bytes"], "Artifact length changed.")
    return path


def snapshot_check(run_root, snapshot, literal, candidate=None):
    typed_oracle(snapshot, literal)
    path = artifact(run_root, snapshot["target"]["artifact"]); tree = target_oracle(path.read_bytes(), literal)
    if candidate is not None: need(xml_key(tree) == xml_key(xml(candidate)) and external_xml_nodes(path.read_bytes()) == external_xml_nodes(candidate), "Native XML readback differs from complete reviewed document.")
    for entry in snapshot["tables"]: artifact(run_root, entry["artifact"])
    receipt = artifact(run_root, snapshot["receipt"])
    retained = load(receipt)
    expected = {k:v for k,v in snapshot.items() if k not in ["receipt", "saved", "automaticReplay"]}
    need(retained == expected, "Snapshot receipt differs from returned native evidence.")
    return path


def peer_check(root, before, after):
    need(before["peers"] == after["peers"], "Complete peer JSON changed: " + ", ".join(sorted(k for k in set(before["peers"]) | set(after["peers"]) if before["peers"].get(k) != after["peers"].get(k))))
    need(before["peerSha256"] == after["peerSha256"], "Peer digest changed.")
    def docs(snapshot):
        values = {}
        for entry in snapshot["tables"]:
            if entry["name"] == "MilestoneWatch" and entry["kind"] == "watch": continue
            key = entry["kind"], entry["group"], entry["name"]
            need(key not in values, "Duplicate peer table identity.")
            data = artifact(root, entry["artifact"]).read_bytes()
            values[key] = xml_key(xml(data)), external_xml_nodes(data)
        folder = Path(snapshot["target"]["artifact"]["path"]).parent
        for path in folder.glob("*.xml"):
            if path.name.startswith(("block-", "type-", "tags-")):
                data = path.read_bytes(); tree = xml(data)
                objects = [e for e in tree if isinstance(e.tag, str) and e.tag not in ["Engineering", "DocumentInfo"]]
                need(len(objects) == 1, "Ambiguous native peer export.")
                key = path.name.split("-")[0], objects[0].tag, objects[0].findtext("AttributeList/Name")
                need(key not in values, "Ambiguous peer names require a scoped oracle.")
                values[key] = xml_key(tree), external_xml_nodes(data)
        return values
    need(docs(before) == docs(after), "Independent complete peer XML changed.")


def without_created_bytes(data):
    """Mask just the native timestamp's byte span; retain every other byte."""
    tree = xml(data); created = tree.findall("DocumentInfo/Created")
    need(len(created) == 1 and not created[0].attrib and len(created[0]) == 0 and bool(created[0].text), "One plain native Created timestamp required.")
    datetime.fromisoformat(created[0].text.replace("Z", "+00:00"))
    parser = expat.ParserCreate(); stack, spans = [], []; start = None
    def opened(name, attributes):
        nonlocal start
        stack.append(name)
        if stack == ["Document", "DocumentInfo", "Created"]: start = data.index(b">", parser.CurrentByteIndex) + 1
    def closed(name):
        if stack == ["Document", "DocumentInfo", "Created"]: spans.append((start, parser.CurrentByteIndex))
        stack.pop()
    parser.StartElementHandler, parser.EndElementHandler = opened, closed; parser.Parse(data, True)
    need(len(spans) == 1, "Native timestamp byte span is ambiguous.")
    first, last = spans[0]
    return data[:first] + b"__CAPTURE_TIME__" + data[last:]


def strict_native_exports(root, before, after, literal):
    """For this fixed fixture, reject even whitespace/serialization drift."""
    old = artifact(root, before["target"]["artifact"]).read_bytes()
    new = artifact(root, after["target"]["artifact"]).read_bytes()
    target_oracle(old, "FALSE"); target_oracle(new, literal)
    if literal == "TRUE":
        match = b"<ModifyValue>TRUE</ModifyValue>"
        need(new.count(match) == 1, "One exact approved native literal required.")
        new = new.replace(match, b"<ModifyValue>FALSE</ModifyValue>")
    need(without_created_bytes(old) == without_created_bytes(new), "Native target bytes changed beyond Created and approved literal.")
    def peer_bytes(snapshot):
        values = {}
        for entry in snapshot["tables"]:
            if entry["name"] == "MilestoneWatch" and entry["kind"] == "watch": continue
            key = entry["kind"], entry["group"], entry["name"]
            need(key not in values, "Duplicate native peer identity.")
            values[key] = without_created_bytes(artifact(root, entry["artifact"]).read_bytes())
        folder = artifact(root, snapshot["target"]["artifact"]).parent
        for path in folder.glob("*.xml"):
            if path.name.startswith(("block-", "type-", "tags-")):
                data = path.read_bytes(); tree = xml(data)
                objects = [e for e in tree if isinstance(e.tag, str) and e.tag not in ["Engineering", "DocumentInfo"]]
                need(len(objects) == 1, "Ambiguous native peer export.")
                key = path.name.split("-")[0], objects[0].tag, objects[0].findtext("AttributeList/Name")
                need(key not in values, "Ambiguous native peer identity.")
                values[key] = without_created_bytes(data)
        return values
    old_peers, new_peers = peer_bytes(before), peer_bytes(after)
    need(old_peers == new_peers, "Native peer bytes changed beyond Created.")
    return {"targetBytesEqualExceptCreatedAndApprovedLiteral": True, "peerExportsEqualExceptCreated": len(old_peers), "whitespaceNormalized": False}


def replay_refusal_check(root, operation):
    refusal = load(root / "replay-refusal.json")
    need(refusal["operationId"] == operation["id"] and refusal["code"] == "RecoveryRequired" and refusal["nativeDispatchInvoked"] is False,
         "Explicit uncertain-operation replay refusal is missing or unbound.")
    return ref(root / "replay-refusal.json")


def reviewed_input_digest(approval):
    digests = [approval[key] for key in ["reviewedInputSha256", "inputSha256"] if key in approval]
    need(digests, "Reviewed input digest is missing.")
    need(all(isinstance(digest, str) and re.fullmatch(r"[0-9a-f]{64}", digest) for digest in digests), "Reviewed input digest is malformed.")
    need(all(digest == digests[0] for digest in digests), "Reviewed input digest aliases conflict.")
    return digests[0]


def journal_check(root, operation_id):
    paths = sorted((root / "engineering-operations" / operation_id).glob("[0-9]*.json"))
    need(paths, "No durable journal records.")
    records = [load(path) for path in paths]
    for index, record in enumerate(records):
        need(record["id"] == operation_id and record["revision"] == index and paths[index].stem == f"{index:06}", "Journal revision/id gap.")
        need(record["planSha256"] == canonical_sha(record["plan"]), "Plan digest mismatch.")
        need(record["previousSha256"] == (canonical_sha(records[index-1]) if index else None), "Journal hash chain changed.")
        need(record["plan"] == records[0]["plan"], "Journal immutable plan changed.")
        for item in record["plan"]["inputs"] + record["checkpoints"] + [e["artifact"] for e in record["evidence"]]: artifact(root, item)
    dispatches = 0
    for index, record in enumerate(records):
        if record["state"] != "dispatchIntent": continue
        previous = records[index - 1] if index else None
        if previous is None or previous["state"] != "dispatchIntent":
            dispatches += 1
        else:
            # A returned result adds evidence while ownership remains pending fresh reopen.
            # A second intent or replacement result is never an evidence-only revision.
            old, new = previous["evidence"], record["evidence"]
            need(len(new) == len(old) + 1 and new[:-1] == old and new[-1]["gate"].startswith("dispatch.")
                 and len({e["gate"] for e in new}) == len(new)
                 and record["checkpoints"] == previous["checkpoints"],
                 "Pending dispatch revision must append one immutable result; replay detected.")
    need(dispatches == 1, "Exactly one durable native dispatch is required; replay detected or no dispatch.")
    return records


def verify(run_root):
    root = Path(run_root).resolve(); stages, issues = [], []
    result = {"kind": "independent-watch-workflow-verification", "root": str(root), "nativeInvoked": False,
              "verifiedAtUtc": datetime.now(timezone.utc).isoformat(), "stages": stages, "issues": issues}
    def check(name, action):
        try:
            detail = action(); stages.append({"name": name, "passed": True, "detail": detail}); return detail
        except (VerificationError, OSError, ValueError, KeyError, IndexError, TypeError, ET.ParseError) as error:
            issues.append({"stage": name, "error": str(error)}); stages.append({"name": name, "passed": False}); return None
    admission = check("admission", lambda: load(root / "admission.json"))
    review_outer = check("reviewPresent", lambda: load(root / "review.json"))
    operation = check("operationIdentity", lambda: load(root / "operation.json"))
    if not all(v is not None for v in [admission, review_outer, operation]): result["completed"] = False; return result
    plan_record = review_outer["operation"]; review = review_outer["review"]; before = review["before"]
    def sources():
        worker = Path(admission["worker"]); need(sha(worker.read_bytes()) == admission["workerSha256"], "Admitted worker changed.")
        manifest = worker.parent.parent / "manifest.json"; values = load(manifest)
        for item in values: artifact(root, item)
        for suffix in ["WatchTableDocument.cs", "WatchTableDispatcher.cs", "OpennessSession.WatchTables.cs", "EngineeringOperationStore.cs"]:
            need(any(Path(v["path"]).name == suffix for v in values), "Frozen source missing: " + suffix)
        need(admission["nativeSequential"] is True and before["observation"]["adapterSha256"] == admission["workerSha256"], "Adapter/admission identity differs.")
        return {"manifest": ref(manifest), "filesVerified": len(values), "worker": ref(worker)}
    check("frozenSourcesAndBinary", sources)
    records = check("durableJournalAndNoReplay", lambda: journal_check(root, operation["id"]))
    if records is not None:
        result["journalStates"] = [r["state"] for r in records]
        stages[-1]["detail"] = {"records": len(records), "finalState": records[-1]["state"], "dispatchCount": 1}
    def approved():
        approval = load(root / "independent-review.json")
        need(approval["approved"] is True and approval["noOnlineAction"] is True and approval["operationId"] == operation["id"] == plan_record["id"], "Exact reviewed operation approval missing.")
        need(approval["reviewedPlanSha256"] == operation["digest"] == plan_record["planSha256"] == (root / "approved-digest").read_text().strip(), "Approved plan digest differs.")
        need(records is not None and records[0] == plan_record, "Durable journal is not the exact approved prepared record.")
        artifact(root, review_outer["reviewArtifact"])
        need(load(artifact(root, plan_record["plan"]["inputs"][0])) == review, "Retained review differs from plan.")
        candidate = artifact(root, plan_record["plan"]["inputs"][1])
        need(sha(candidate.read_bytes()) == reviewed_input_digest(approval), "Reviewed input digest differs.")
        need(review["edit"] == {"ordinal": 0, "field": "ModifyValue", "before": "FALSE", "after": "TRUE"}, "Only authored FALSE-to-TRUE review is accepted.")
        old = snapshot_check(root, before, "FALSE"); only_literal_change(old.read_bytes(), candidate.read_bytes())
        return {"candidate": ref(candidate), "baseline": ref(old), "review": ref(root / "independent-review.json")}
    approved_info = check("reviewAndExactSingleFieldDiff", approved)
    if approved_info is None: result["completed"] = False; return result
    candidate = Path(approved_info["candidate"]["path"]).read_bytes(); baseline_bytes = Path(approved_info["baseline"]["path"]).read_bytes()
    interrupted = admission["mode"] == "interrupt"
    if not interrupted:
        def applied():
            packet = load(root / "applied.json"); snap = packet["applied"]
            snapshot_check(root, snap, "TRUE", candidate); peer_check(root, before, snap)
            strict_native_exports(root, before, snap, "TRUE")
            need(snap["saved"] is True and snap["automaticReplay"] is False, "Native saved/no-replay receipt missing.")
            artifact(root, packet["evidence"])
            return {"processId": snap["processId"], "saved": True, "peerJsonEqual": True, "targetXmlEqual": True}
        check("appliedTypedXmlReadbackAndPeers", applied)
        def fresh():
            packet = load(root / "verified.json"); snap = packet["reopened"]
            snapshot_check(root, snap, "TRUE", candidate); peer_check(root, before, snap)
            strict_native_exports(root, before, snap, "TRUE")
            need(snap["processId"] != before["processId"] and snap["observation"]["sessionEpoch"] != before["observation"]["sessionEpoch"] and snap["projectModified"] is False, "Fresh saved native instance required.")
            need(snap["project"]["path"] == operation["projectPath"], "Fresh reopen is a different project.")
            return {"processId": snap["processId"], "projectPath": snap["project"]["path"]}
        check("freshReopen", fresh)
        if not (root / "verified.json").exists():
            for file in sorted((root / "watch-observations").glob("*/snapshot.json")):
                snap = load(file)
                observed_rows = snap["target"].get("rows", [])
                if snap["processId"] != before["processId"] and observed_rows and observed_rows[0].get("modifyValue") == "TRUE":
                    check("failedFreshObservation:" + file.parent.name, lambda snap=snap: peer_check(root, before, snap))
                    differences = []
                    for old in before["tables"]:
                        new = next((t for t in snap["tables"] if all(t[k] == old[k] for k in ["kind", "group", "name"])), None)
                        if new is not None and len(old.get("rows", [])) != len(new.get("rows", [])):
                            differences.append({"table": old["name"], "beforeTypedRows": len(old.get("rows", [])), "afterTypedRows": len(new.get("rows", [])), "beforeArtifact": old["artifact"], "afterArtifact": new["artifact"]})
                    result["failedFreshPeerDifferences"] = differences
    else:
        def interruption():
            failure = load(root / "failure.json"); need(failure["automaticReplay"] is False, "Interruption replay must be disabled.")
            need(records is not None and "outcomeUnknown" in [r["state"] for r in records], "Interrupted native effect must be durably unknown.")
            need(not (root / "applied.json").exists(), "Interrupted case unexpectedly has a normal apply receipt.")
            saved = load(root / "watch-observations/interrupted-after-native-import.json")
            need(saved["saved"] is True and saved["automaticReplay"] is False and saved["projectPath"] == operation["projectPath"], "Saved interruption receipt missing.")
            return {"nativeWorkerLossRetained": True, "automaticReplay": False, "explicitReplayRefusal": replay_refusal_check(root, operation)}
        check("interruptedOutcomeUnknownWithoutReplay", interruption)
    def recovery():
        need(records is not None, "Durable records missing."); final = records[-1]
        need(final["state"] == "reconciled", "Journal is not reconciled: " + final["state"])
        proofs = {e["gate"]: load(artifact(root, e["artifact"])) for e in final["evidence"]}
        recovered = proofs["recovery.retrieve"]; restored = proofs["recovery.restored"]; original = proofs["recovery.original"]
        need(len(final["checkpoints"]) == 1 and len(final["recoveryDestinations"]) == 1 and len(final["recoveryResolutions"]) == 1, "One exact archive and recovery destination required.")
        archive = artifact(root, final["checkpoints"][0]); need(recovered["checkpoint"] == final["checkpoints"][0], "Restore archive is not the bound checkpoint.")
        for snap in [recovered["restored"], restored]:
            snapshot_check(root, snap, "FALSE", baseline_bytes); peer_check(root, before, snap); strict_native_exports(root, before, snap, "FALSE")
        snapshot_check(root, original, "TRUE", candidate); peer_check(root, before, original)
        strict_native_exports(root, before, original, "TRUE")
        pids = [before["processId"], recovered["recovered"]["processId"], restored["processId"], original["processId"]]
        epochs = [before["observation"]["sessionEpoch"], recovered["recovered"]["sessionEpoch"], restored["observation"]["sessionEpoch"], original["observation"]["sessionEpoch"]]
        if not interrupted:
            fresh = load(root / "verified.json")["reopened"]
            pids.insert(1, fresh["processId"]); epochs.insert(1, fresh["observation"]["sessionEpoch"])
        need(len(set(pids)) == len(pids) and len(set(epochs)) == len(epochs), "Recovery phases require distinct fresh processes and sessions.")
        need(restored["projectModified"] is False and original["projectModified"] is False, "Recovery reconciliation must read saved projects.")
        recovered_path = recovered["recovered"]["project"]["path"]
        need(recovered_path != operation["projectPath"] and restored["project"]["path"] == recovered_path and original["project"]["path"] == operation["projectPath"], "Restored/original identity differs.")
        need(final["reconciliation"][0]["disposition"] == "verifiedApplied" and final["recoveryResolutions"][0]["disposition"] == "verifiedRestored", "Recovery disposition not independently verified.")
        ending = load(root / ("interruption-recovery-result.json" if interrupted else "result.json"))
        need(ending["completed"] is True and ending["id"] == operation["id"] and ending["recoveryPath"] == recovered_path, "Native harness did not complete the bound recovery.")
        need((ending["automaticReplay"] is False and ending["original"] == operation["projectPath"]) if interrupted else
             (ending["nativeOnline"] is False and ending["projectPath"] == operation["projectPath"]), "Final offline/no-replay result is unbound.")
        return {"archive": ref(archive), "freshProcessIds": pids, "recoveryPath": recovered_path, "finalJournalState": final["state"], "automaticReplay": False, "nativeBytesEqualExceptCreatedAndApprovedLiteral": True}
    check("checkpointFreshRestoreOriginalAndJournalReconciliation", recovery)
    if (root / "failure.json").exists():
        result["retainedFailure"] = {"artifact": ref(root / "failure.json"), "diagnostic": load(root / "failure.json")}
        if not interrupted: issues.append({"stage": "nativeRun", "error": "Native run retained failure.json; it is not a successful workflow."})
    result["completed"] = not issues
    result["interpretation"] = "Verified retained native lifecycle evidence." if not issues else "Failed or incomplete workflow; all observed mismatches remain failures."
    return result


def main():
    parser = argparse.ArgumentParser(); parser.add_argument("run_root"); parser.add_argument("--output", required=True)
    args = parser.parse_args(); output = Path(args.output)
    need(not output.exists(), "Use a fresh verification output path.")
    result = verify(args.run_root); output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8", newline="\n") as stream: json.dump(result, stream, indent=2, ensure_ascii=False); stream.write("\n")
    print(json.dumps({"completed": result["completed"], "passedStages": sum(s["passed"] for s in result["stages"]), "issues": result["issues"], "output": str(output)}, ensure_ascii=True))
    return 0 if result["completed"] else 1


if __name__ == "__main__": raise SystemExit(main())
